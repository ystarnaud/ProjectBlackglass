using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Blackglass.Accounts.Api.Attempts;
using Blackglass.Accounts.Domain.Attempts;
using Blackglass.Accounts.Domain.Ids;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Accounts;
using Blackglass.Accounts.Persistence.Attempts;
using Blackglass.Accounts.Persistence.Energy;
using static Blackglass.Accounts.IntegrationTests.Database.PlayerApiTests;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>
/// POST /v1/attempts through serve (Task 6): the body cap (413) and content type (415), the Idempotency-Key header (400
/// InvalidIdempotencyKey) and the strict body (400) before any connection; then the store's refusals as ProblemDetails
/// {code[, index]} (409, 422, 503 ServiceUnavailable with Retry-After) and the 201 body exactly as stored in deploy_requests.
/// The account comes only from the session; another account's operative is answered exactly like an unknown one. Every
/// admitting test has a fresh development host fixture and runs serve with BLACKGLASS_DEV_HOSTS=enabled; one test proves
/// that with fixtures disabled admission is 503.
/// </summary>
[Trait("Requires", "Postgres")]
public class AttemptApiTests : IAsyncLifetime
{
    static readonly Dictionary<string, string?> Admitting = new()
    {
        ["BLACKGLASS_DEV_AUTH"] = "enabled", [AttemptSettings.DevHostsVar] = "enabled", [AttemptSettings.HostLeaseSecondsVar] = "600",
    };

    TestDatabase db = null!;
    AccountAdminStore admin = null!;
    readonly List<ApiServer> servers = [];

    public async Task InitializeAsync()
    {
        db = await MigratedAsync();
        admin = new AccountAdminStore(db.AsApp());
        var hosts = new DevHosts(db.AsApp());
        await hosts.RegisterAsync("dev-fixture-1", 10, Op);
        await hosts.HeartbeatAsync("dev-fixture-1", Op);
    }

    public async Task DisposeAsync()
    {
        foreach (var server in servers) await server.DisposeAsync();
        try { Assert.Empty(await InvariantFailuresAsync(db)); }
        finally { await db.DisposeAsync(); }
    }

    // ---- helpers -----------------------------------------------------------------------------------------------------------

    async Task<ApiServer> StartAsync(Dictionary<string, string?>? extra = null, Action<Microsoft.AspNetCore.Builder.WebApplicationBuilder>? configure = null,
        bool admitting = true)
    {
        var settings = admitting ? new Dictionary<string, string?>(Admitting) : new Dictionary<string, string?> { ["BLACKGLASS_DEV_AUTH"] = "enabled" };
        foreach (var (k, v) in extra ?? new()) settings[k] = v;
        var api = await ApiServer.StartAsync(db, settings, configure);
        servers.Add(api);
        return api;
    }

    sealed record Player(long Account, string Token, long[] Operatives);

    async Task<Player> PlayerAsync(ApiServer api)
    {
        var issued = await admin.CreateAccountAsync(null, null, Op);
        var login = await api.Http.PostAsJsonAsync("/v1/auth/dev/login", new { accountKey = issued.AccountKey.Text });
        var token = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.GetProperty("accessToken").GetString()!;
        await using var su = await db.AsSuperuser().OpenConnectionAsync();
        var operatives = (await su.QueryAsync<long>("select operative_id from operatives where account_id = @a order by operative_id", new { a = issued.AccountId })).ToArray();
        return new Player(issued.AccountId, token, operatives);
    }

    static string Body(params long[] operatives) => BodyOf("procedural-standard", operatives);

    static string BodyOf(string template, params long[] operatives) =>
        $$"""{"missionTemplate":"{{template}}","operativeIds":[{{string.Join(",", operatives.Select(o => "\"" + Base58Id.Format(o) + "\""))}}]}""";

    static HttpRequestMessage Deploy(string? token, string json, string? key, string mediaType = "application/json")
    {
        var request = As(token, HttpMethod.Post, "/v1/attempts", json, mediaType);
        if (key is not null) request.Headers.TryAddWithoutValidation(AttemptEndpoints.IdempotencyKeyHeader, key);
        return request;
    }

    static string NewKey() => Guid.CreateVersion7().ToString("D");

    static async Task<(HttpStatusCode Status, string Body, HttpResponseMessage Response)> SendAsync(ApiServer api, HttpRequestMessage request)
    {
        var response = await api.Http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(), response);
    }

    static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    static void AssertProblem((HttpStatusCode Status, string Body, HttpResponseMessage Response) response, HttpStatusCode status, string code, int? index = null)
    {
        Assert.True(status == response.Status, $"{(int)response.Status} {response.Body}");
        var problem = Json(response.Body);
        Assert.Equal(code, problem.GetProperty("code").GetString());
        if (index is { } at) Assert.Equal(at, problem.GetProperty("index").GetInt32());
        else Assert.False(problem.TryGetProperty("index", out _));
    }

    Task<T> ScalarAsync<T>(string sql, object? args = null) => ScalarAsSuperuserAsync<T>(db, sql, args);

    /// <summary>Everything an admission could write, database-wide, with the wallet and inventories xmins (see AdmissionStoreTests).</summary>
    Task<string> StateAsync() => ScalarAsync<string>(
        """
        select concat_ws(' | ',
          (select count(*) from mission_attempts), (select count(*) from attempt_participants), (select count(*) from energy_holds),
          (select count(*) from attempt_insurance), (select count(*) from deploy_requests), (select count(*) from energy_ledger),
          (select string_agg(account_id || ':' || xmin::text || ':' || balance || ':' || policy_version, ',' order by account_id) from energy_wallets),
          (select string_agg(account_id || ':' || xmin::text || ':' || version, ',' order by account_id) from inventories))
        """);

    // ---- admission -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Deploy_admits_with_201_and_the_stored_body()
    {
        var api = await StartAsync();
        var p = await PlayerAsync(api);
        var key = NewKey();

        var (status, body, response) = await SendAsync(api, Deploy(p.Token, Body(p.Operatives[2], p.Operatives[0]), key));

        Assert.True(HttpStatusCode.Created == status, $"{(int)status} {body}");
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        // The first answer is the stored text itself (Checkpoint 1 F3), so a replay is byte-identical.
        Assert.Equal(await ScalarAsync<string>("select response::text from deploy_requests where account_id = @a", new { a = p.Account }), body);
        var json = Json(body);
        Assert.Equal(["attemptId", "energy", "insurance", "state"], json.EnumerateObject().Select(m => m.Name).Order(StringComparer.Ordinal).ToArray());
        var attempt = Guid.Parse(json.GetProperty("attemptId").GetString()!);
        Assert.Equal(attempt.ToString("D"), json.GetProperty("attemptId").GetString()); // canonical lower case
        Assert.Equal(7, attempt.Version); // UUIDv7
        Assert.Equal("admitted", json.GetProperty("state").GetString());
        Assert.Equal((10, 40), (json.GetProperty("energy").GetProperty("cost").GetInt32(), json.GetProperty("energy").GetProperty("balanceAfter").GetInt32()));
        Assert.False(json.GetProperty("insurance").GetProperty("covered").GetBoolean());
        await using (var su = await db.AsSuperuser().OpenConnectionAsync())
            Assert.Equal((p.Account, "procedural-standard", 1), await su.QuerySingleAsync<(long, string, int)>(
                "select created_by, template_id, template_version from mission_attempts where attempt_id = @attempt", new { attempt }));
        // The assignment deadline is the attempt's creation clock plus the provisional 60 s.
        Assert.True(await ScalarAsync<bool>(
            "select assign_deadline between created_at + interval '59 seconds' and created_at + interval '61 seconds' from mission_attempts where attempt_id = @attempt", new { attempt }));

        var (again, againBody, _) = await SendAsync(api, Deploy(p.Token, Body(p.Operatives[2], p.Operatives[0]), key));
        Assert.Equal((HttpStatusCode.Created, body), (again, againBody));
        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from mission_attempts"));
    }

    [Fact]
    public async Task Twenty_identical_requests_give_one_attempt_and_identical_bodies()
    {
        const int Callers = 20;
        var api = await StartAsync(new() { [AttemptSettings.DeployRateVar] = "100" });
        var p = await PlayerAsync(api);
        var key = NewKey();

        var responses = await Task.WhenAll(Enumerable.Range(0, Callers).Select(_ => Task.Run(() => SendAsync(api, Deploy(p.Token, Body(p.Operatives[0]), key)))));

        Assert.All(responses, r => Assert.True(HttpStatusCode.Created == r.Status, $"{(int)r.Status} {r.Body}"));
        Assert.Single(responses.Select(r => r.Body).Distinct(StringComparer.Ordinal)); // byte-identical
        Assert.Equal((1L, 1L, 1L), (await ScalarAsync<long>("select count(*) from mission_attempts"), await ScalarAsync<long>("select count(*) from deploy_requests"),
            await ScalarAsync<long>("select count(*) from energy_ledger where kind = 'reserve'")));
        Assert.Equal(40, await ScalarAsync<int>("select balance from energy_wallets where account_id = @a", new { a = p.Account }));
    }

    [Fact]
    public async Task No_healthy_host_is_503_with_energy_untouched()
    {
        // Fixtures disabled (the default): the fresh dev_fixture host is not capacity, so every deploy is refused before any lock.
        var api = await StartAsync(admitting: false);
        var p = await PlayerAsync(api);
        // A later policy version makes any materialization rewrite the wallet, so an unchanged wallet row means none ran.
        await new EnergyAdminStore(db.AsApp(), 1000).PublishPolicyAsync(100, 50, TimeSpan.FromMinutes(10), 1, 100, Op);
        var before = await StateAsync();

        var refused = await SendAsync(api, Deploy(p.Token, Body(p.Operatives[0]), NewKey()));
        AssertProblem(refused, HttpStatusCode.ServiceUnavailable, "ServiceUnavailable");
        Assert.Equal("5", refused.Response.Headers.GetValues("Retry-After").Single());
        Assert.Equal(before, await StateAsync());

        // While the health query itself waits (on a lock held over mission_hosts), the admission's backend holds no inventories
        // or wallet lock and has no transaction id: the check runs before the transaction begins.
        await using (var holder = await db.AsSuperuser().OpenConnectionAsync())
        {
            await using var held = await holder.BeginTransactionAsync();
            await holder.ExecuteAsync("lock table mission_hosts in access exclusive mode", transaction: held);
            var waiting = Task.Run(() => SendAsync(api, Deploy(p.Token, Body(p.Operatives[0]), NewKey())));
            await WaitUntilABackendWaitsOnALockAsync(db);
            var pid = await holder.ExecuteScalarAsync<int>(
                "select pid from pg_stat_activity where datname = current_database() and wait_event_type = 'Lock' and usename = 'accounts_app'", transaction: held);
            Assert.True(await holder.ExecuteScalarAsync<bool>("select backend_xid is null from pg_stat_activity where pid = @pid", new { pid }, held));
            Assert.Equal(0L, await holder.ExecuteScalarAsync<long>(
                """
                select count(*) from pg_locks l left join pg_class c on c.oid = l.relation
                where l.pid = @pid and (l.locktype = 'transactionid' or c.relname in ('inventories', 'inventories_pkey', 'energy_wallets', 'energy_wallets_pkey', 'accounts'))
                """, new { pid }, held));
            await held.RollbackAsync();
            AssertProblem(await waiting, HttpStatusCode.ServiceUnavailable, "ServiceUnavailable");
        }
        Assert.Equal(before, await StateAsync());
    }

    [Fact]
    public async Task A_foreign_or_unknown_operative_is_the_same_422()
    {
        var api = await StartAsync(new() { [AttemptSettings.DeployRateVar] = "100" });
        var p = await PlayerAsync(api);
        var other = await PlayerAsync(api);
        var unknown = other.Operatives.Max() + 1_000;
        var before = await StateAsync();

        var foreign = await SendAsync(api, Deploy(p.Token, Body(other.Operatives[0]), NewKey()));
        var missing = await SendAsync(api, Deploy(p.Token, Body(unknown), NewKey()));
        AssertProblem(foreign, HttpStatusCode.UnprocessableEntity, "UnknownOperative", 0);
        Assert.Equal(foreign.Body, missing.Body); // indistinguishable, byte for byte
        Assert.Equal(foreign.Response.Content.Headers.ContentType, missing.Response.Content.Headers.ContentType);

        // The index is the request's position.
        AssertProblem(await SendAsync(api, Deploy(p.Token, Body(p.Operatives[0], other.Operatives[1]), NewKey())), HttpStatusCode.UnprocessableEntity, "UnknownOperative", 1);
        AssertProblem(await SendAsync(api, Deploy(p.Token, Body(p.Operatives[0], unknown), NewKey())), HttpStatusCode.UnprocessableEntity, "UnknownOperative", 1);
        Assert.Equal(before, await StateAsync());
    }

    [Fact]
    public async Task Operative_count_outside_the_template_is_422()
    {
        await new MissionTemplates(db.AsApp()).PublishAsync("pair", 10, 2, 2, Op);
        var api = await StartAsync();
        var p = await PlayerAsync(api);
        var before = await StateAsync();

        AssertProblem(await SendAsync(api, Deploy(p.Token, BodyOf("pair", p.Operatives[0]), NewKey())), HttpStatusCode.UnprocessableEntity, "OperativeCount");
        AssertProblem(await SendAsync(api, Deploy(p.Token, BodyOf("pair", p.Operatives), NewKey())), HttpStatusCode.UnprocessableEntity, "OperativeCount");
        Assert.Equal(before, await StateAsync());

        Assert.Equal(HttpStatusCode.Created, (await SendAsync(api, Deploy(p.Token, BodyOf("pair", p.Operatives[0], p.Operatives[1]), NewKey()))).Status);
    }

    [Fact]
    public async Task Unknown_templates_retired_operatives_and_unknown_items_are_422_with_their_index()
    {
        var api = await StartAsync();
        var p = await PlayerAsync(api);
        var (o1, o2, o3) = (p.Operatives[0], p.Operatives[1], p.Operatives[2]);
        await ExecuteAsSuperuserAsync(db, "update operatives set definition_id = 'operative.retired' where operative_id = @o2", new { o2 });
        await ExecuteAsSuperuserAsync(db, "update item_instances set definition_id = 'item.retired' where operative_id = @o1 and equipped_slot = 'weapon'", new { o1 });
        var before = await StateAsync();

        AssertProblem(await SendAsync(api, Deploy(p.Token, BodyOf("no-such-template", o3), NewKey())), HttpStatusCode.UnprocessableEntity, "UnknownTemplate");
        AssertProblem(await SendAsync(api, Deploy(p.Token, Body(o3, o2), NewKey())), HttpStatusCode.UnprocessableEntity, "RetiredOperative", 1);
        AssertProblem(await SendAsync(api, Deploy(p.Token, Body(o3, o1), NewKey())), HttpStatusCode.UnprocessableEntity, "UnknownItem", 1);
        Assert.Equal(before, await StateAsync());
    }

    [Fact]
    public async Task Conflicts_are_409_with_their_codes_and_nothing_written()
    {
        await new MissionTemplates(db.AsApp()).PublishAsync("expensive", 51, 1, 3, Op);
        var api = await StartAsync();
        var p = await PlayerAsync(api);
        var before = await StateAsync();
        AssertProblem(await SendAsync(api, Deploy(p.Token, BodyOf("expensive", p.Operatives[0]), NewKey())), HttpStatusCode.Conflict, "InsufficientEnergy");
        Assert.Equal(before, await StateAsync());

        var key = NewKey();
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(api, Deploy(p.Token, Body(p.Operatives[0]), key))).Status);
        before = await StateAsync();
        AssertProblem(await SendAsync(api, Deploy(p.Token, Body(p.Operatives[1]), key)), HttpStatusCode.Conflict, "IdempotencyKeyReused");
        AssertProblem(await SendAsync(api, Deploy(p.Token, Body(p.Operatives[1]), NewKey())), HttpStatusCode.Conflict, "AttemptActive");
        Assert.Equal(before, await StateAsync());
    }

    [Fact]
    public async Task Malformed_requests_are_refused_before_any_connection()
    {
        // Admission's store is replaced by one whose database is unreachable: a request that reached it would be 503, so every
        // 4xx below is decided before admission opens a connection (only authentication, which reads the session, touches the
        // database first).
        await using var unreachable = Npgsql.NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1");
        var api = await StartAsync(new() { [AttemptSettings.DeployRateVar] = "1000", [AttemptSettings.DeployMaxBodyBytesVar] = "1024" },
            b => b.Services.AddSingleton(new AdmissionStore(unreachable, new ServiceHealth(true, 600), new CryptoAttemptSecrets(),
                Blackglass.Accounts.Domain.Catalogue.GameCatalogue.Embedded, 60)));
        var p = await PlayerAsync(api);
        var good = Body(p.Operatives[0]);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await SendAsync(api, Deploy(p.Token, good, NewKey(), "text/plain"))).Status);
        var oversized = "{\"missionTemplate\":\"procedural-standard\",\"operativeIds\":[\"2\"]" + new string(' ', 1100) + "}";
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await SendAsync(api, Deploy(p.Token, oversized, NewKey()))).Status);

        foreach (var key in new[] { null, "", "not-a-uuid", NewKey().ToUpperInvariant(), "{" + NewKey() + "}", NewKey().Replace("-", ""), NewKey()[..35], NewKey() + "0", NewKey() + "," + NewKey(), Guid.NewGuid().ToString("D").Replace("-", "_") })
            AssertProblem(await SendAsync(api, Deploy(p.Token, good, key)), HttpStatusCode.BadRequest, "InvalidIdempotencyKey");
        var twice = Deploy(p.Token, good, NewKey());
        twice.Headers.TryAddWithoutValidation(AttemptEndpoints.IdempotencyKeyHeader, NewKey()); // two header values
        AssertProblem(await SendAsync(api, twice), HttpStatusCode.BadRequest, "InvalidIdempotencyKey");

        var o = Base58Id.Format(p.Operatives[0]);
        foreach (var (json, code) in new[]
                 {
                     ("", "InvalidBody"), ("[]", "InvalidBody"), ("{}", "InvalidBody"), ("{\"missionTemplate\":\"procedural-standard\"}", "InvalidBody"),
                     ($"{{\"missionTemplate\":\"procedural-standard\",\"operativeIds\":[\"{o}\"],\"accountId\":\"2\"}}", "InvalidBody"),
                     ($"{{\"missionTemplate\":\"Procedural\",\"operativeIds\":[\"{o}\"]}}", "InvalidBody"),
                     ("{\"missionTemplate\":\"procedural-standard\",\"operativeIds\":[]}", "InvalidBody"),
                     ($"{{\"missionTemplate\":\"procedural-standard\",\"operativeIds\":[{p.Operatives[0]}]}}", "InvalidBody"), // a number, not Base58
                     ($"{{\"missionTemplate\":\"procedural-standard\",\"operativeIds\":[\"{o}\",\"{o}\"]}}", "DuplicateOperative"),
                     ("{\"missionTemplate\":\"procedural-standard\",\"operativeIds\":[\"0OIl\"]}", "InvalidOperativeId"),
                 })
            AssertProblem(await SendAsync(api, Deploy(p.Token, json, NewKey())), HttpStatusCode.BadRequest, code);

        // No session: the uniform 401, before anything else.
        var anonymous = await SendAsync(api, Deploy(null, good, NewKey()));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.Status);
        Assert.Equal("", anonymous.Body);
        // The substitution is real: a well-formed request reaches the unreachable store.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await SendAsync(api, Deploy(p.Token, good, NewKey()))).Status);
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from mission_attempts"));
    }

    [Fact]
    public async Task Deploys_are_rate_limited_per_account()
    {
        var api = await StartAsync(); // the provisional default: 6 a minute per account
        var chatty = await PlayerAsync(api);
        var quiet = await PlayerAsync(api);
        for (var i = 0; i < 6; i++)
            AssertProblem(await SendAsync(api, Deploy(chatty.Token, "{}", NewKey())), HttpStatusCode.BadRequest, "InvalidBody"); // refusals count too
        var limited = await SendAsync(api, Deploy(chatty.Token, Body(chatty.Operatives[0]), NewKey()));
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.Status);
        Assert.Equal("60", limited.Response.Headers.GetValues("Retry-After").Single());
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from mission_attempts"));

        Assert.Equal(HttpStatusCode.Created, (await SendAsync(api, Deploy(quiet.Token, Body(quiet.Operatives[0]), NewKey()))).Status); // its own window
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(api, As(chatty.Token, HttpMethod.Get, "/v1/inventory"))).Status); // other routes keep theirs
    }

    [Fact]
    public async Task A_lock_timeout_is_503_and_writes_nothing()
    {
        var api = await StartAsync(new() { ["BLACKGLASS_DB_LOCK_TIMEOUT_MS"] = "300" });
        var p = await PlayerAsync(api);
        var key = NewKey();
        var before = await StateAsync();
        await using (var holder = await db.AsSuperuser().OpenConnectionAsync())
        {
            await using var held = await holder.BeginTransactionAsync();
            await holder.ExecuteAsync("select 1 from inventories where account_id = @a for update", new { a = p.Account }, held);
            var busy = await SendAsync(api, Deploy(p.Token, Body(p.Operatives[0]), key));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, busy.Status);
            Assert.Equal("1", busy.Response.Headers.GetValues("Retry-After").Single());
            Assert.Equal("", busy.Body);
            await held.RollbackAsync();
        }
        Assert.Equal(before, await StateAsync());
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(api, Deploy(p.Token, Body(p.Operatives[0]), key))).Status); // no retry happened on its own
    }

    [Fact]
    public async Task Seed_and_salt_are_stored_and_absent_from_every_response_and_log()
    {
        var secrets = new FixedSecrets(1_987_654_321, Enumerable.Range(0, 32).Select(i => (byte)(0xA0 ^ (i * 7))).ToArray());
        var logs = new CapturingLoggerProvider();
        var api = await StartAsync(new() { [AttemptSettings.DeployRateVar] = "100" }, b =>
        {
            b.Logging.ClearProviders();
            b.Logging.SetMinimumLevel(LogLevel.Trace);
            b.Logging.AddProvider(logs);
            b.Services.AddSingleton<IAttemptSecrets>(secrets);
        });
        var p = await PlayerAsync(api);
        var other = await PlayerAsync(api);
        var bodies = new List<string>();
        async Task SendAndKeepAsync(HttpRequestMessage request) => bodies.Add((await SendAsync(api, request)).Body);

        // Admitted, replayed, refused (409, 422, 400) and failed (500, from an injected fault after the secrets were drawn).
        var key = NewKey();
        await SendAndKeepAsync(Deploy(p.Token, Body(p.Operatives[0]), key));
        await SendAndKeepAsync(Deploy(p.Token, Body(p.Operatives[0]), key));
        await SendAndKeepAsync(Deploy(p.Token, Body(p.Operatives[1]), NewKey()));
        await SendAndKeepAsync(Deploy(p.Token, Body(other.Operatives[0]), NewKey()));
        await SendAndKeepAsync(Deploy(p.Token, "{}", NewKey()));
        await ExecuteAsSuperuserAsync(db,
            "create function bg_test_fail() returns trigger language plpgsql as $$ begin raise exception 'injected failure' using errcode = 'P0001'; end $$; " +
            "create trigger bg_test_fail before insert on deploy_requests for each row execute function bg_test_fail();");
        var failed = await SendAsync(api, Deploy(other.Token, Body(other.Operatives[0]), NewKey()));
        Assert.Equal(HttpStatusCode.InternalServerError, failed.Status);
        bodies.Add(failed.Body);
        await ExecuteAsSuperuserAsync(db, "drop trigger bg_test_fail on deploy_requests; drop function bg_test_fail();");

        // Stored with the attempt.
        Assert.Equal(secrets.Seed, await ScalarAsync<int>("select seed from mission_attempts"));
        Assert.Equal(secrets.Salt, await ScalarAsync<byte[]>("select retention_salt from mission_attempts"));
        // Absent from every body (no member named or valued like them) and from every log line, in every spelling.
        Assert.Contains("attemptId", bodies[0]);
        foreach (var body in bodies)
        {
            Assert.DoesNotContain("seed", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("salt", body, StringComparison.OrdinalIgnoreCase);
        }
        var values = new[]
        {
            secrets.Seed.ToString(System.Globalization.CultureInfo.InvariantCulture), Convert.ToHexStringLower(secrets.Salt), Convert.ToHexString(secrets.Salt),
            Convert.ToBase64String(secrets.Salt).TrimEnd('='), Convert.ToHexStringLower(secrets.Salt[..8]),
        };
        var text = string.Join('\n', bodies) + '\n' + logs.Text;
        Assert.Contains("/v1/attempts", logs.Text); // the capture saw the requests
        foreach (var value in values) Assert.DoesNotContain(value, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_admitted_account_is_InventoryLocked()
    {
        var api = await StartAsync();
        var p = await PlayerAsync(api);
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(api, Deploy(p.Token, Body(p.Operatives[0]), NewKey()))).Status);
        var vest = await ScalarAsync<long>("select item_id from item_instances where account_id = @a and definition_id = 'item.light-vest' and operative_id is null", new { a = p.Account });

        var edit = await SendAsync(api, As(p.Token, HttpMethod.Post, "/v1/inventory/operations",
            $$"""{"expectedVersion":1,"operations":[{"op":"equipFromStash","operativeId":"{{Base58Id.Format(p.Operatives[1])}}","itemId":"{{Base58Id.Format(vest)}}"}]}"""));
        AssertProblem(edit, HttpStatusCode.Conflict, "InventoryLocked");
        await ExecuteAsSuperuserAsync(db, "update operatives set experience = 100 where operative_id = @o", new { o = p.Operatives[1] });
        AssertProblem(await SendAsync(api, Pick(p.Token, Base58Id.Format(p.Operatives[1]), """{"choiceId":"combat","expectedVersion":1}""")), HttpStatusCode.Conflict, "InventoryLocked");
        var inventory = await SendAsync(api, As(p.Token, HttpMethod.Get, "/v1/inventory"));
        Assert.Equal(HttpStatusCode.OK, inventory.Status);
        Assert.True(Json(inventory.Body).GetProperty("locked").GetBoolean());
        Assert.Equal(1L, await ScalarAsync<long>("select version from inventories where account_id = @a", new { a = p.Account }));
    }

    /// <summary>Known, distinctive secrets, so the test can look for them; never used outside tests.</summary>
    sealed class FixedSecrets(int seed, byte[] salt) : IAttemptSecrets
    {
        public int Seed { get; } = seed;
        public byte[] Salt { get; } = salt;
        public int NextSeed() => Seed;
        public byte[] NextRetentionSalt() => (byte[])Salt.Clone();
    }
}
