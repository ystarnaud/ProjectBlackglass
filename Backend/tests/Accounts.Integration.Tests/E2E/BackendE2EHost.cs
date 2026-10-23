using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Npgsql;
using Blackglass.Accounts.Api;
using Blackglass.Accounts.Domain.Attempts;
using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.Domain.Hosts;
using Blackglass.Accounts.Domain.Ids;
using Blackglass.Accounts.Domain.Settlement;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Accounts;
using Blackglass.Accounts.Persistence.Hosts;
using Blackglass.Accounts.Persistence.Migrations;

namespace Blackglass.Accounts.IntegrationTests.E2E;

/// <summary>
/// Stage 13 CP4 (owner authorization 2026-10-23): the real API, in this process on a loopback port, against one disposable
/// database on the shared test cluster (created and removed only through <see cref="Cp4Database"/>), with the Unity Editor's
/// -attempt server (Assets/_Project/Tests/PlayMode/BackendE2EPlayTests.cs) as the mission process. This test plays the operator
/// (seeding through the real admin stores), the player (dev login, deploy, ticket, receipt over HTTP), the host agent (heartbeat,
/// claim, launching, the relay of the published result to the existing authenticated result route, exited) and the network
/// between the mission server and the API (<see cref="LostResponseProxy"/>). Scenario A loses the first start answer after the
/// backend committed it; scenario B stops this API after the start and restarts it after the process published its result.
/// Runs only with BLACKGLASS_E2E=1, BLACKGLASS_E2E_DIR (a new private directory) and BLACKGLASS_CP4_SYSTEM_ID (the cluster's
/// expected system identifier, from the read-only baseline step); see Backend/README.md, "Stage 13 CP4".
/// </summary>
[Trait("Requires", "Postgres")]
[Trait("Category", "BackendE2E")]
public sealed class BackendE2EHost
{
    const string HostId = "cp4-host";
    const string Template = "procedural-standard";
    static readonly TimeSpan UnityBoot = TimeSpan.FromMinutes(20);
    static readonly TimeSpan UnityRun = TimeSpan.FromMinutes(15);
    static readonly BuildIdentity EditorBuild = new("editor", GameCatalogue.Embedded.Version);
    static readonly OperatorContext Op = OperatorContext.Create("stage13-cp4", "Stage 13 CP4 end-to-end (disposable database)");

    E2EExchange x = null!;
    readonly List<string> transcript = [];
    readonly List<string> secrets = [];

    void Say(string line)
    {
        lock (transcript) transcript.Add($"{DateTime.UtcNow:HH:mm:ss.fff} {line}");
    }

    /// <summary>The read-only step before and after CP4: the cluster's identity and its database and role rows, written to
    /// BLACKGLASS_CP4_BASELINE_OUT (a new file). No write of any kind; the identity is not judged here, only recorded.</summary>
    [E2EFact]
    public async Task Cp4_read_only_cluster_baseline()
    {
        var output = Environment.GetEnvironmentVariable("BLACKGLASS_CP4_BASELINE_OUT");
        Assert.False(string.IsNullOrEmpty(output) || File.Exists(output), "BLACKGLASS_CP4_BASELINE_OUT must name a new file");
        await using var admin = new NpgsqlCp4ClusterAdmin(Cp4Cluster.FromSecrets());
        var identity = await admin.IdentityAsync();
        var baseline = await admin.BaselineAsync();
        await File.WriteAllTextAsync(output!, JsonSerializer.Serialize(new
        {
            identity,
            cp4Databases = baseline.Databases.Where(d => d.Name.Contains("_cp4_", StringComparison.Ordinal)).ToArray(),
            baseline,
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    }

    [E2EFact]
    public async Task Cp4_a_lost_start_answer_and_an_api_outage_each_settle_exactly_once()
    {
        x = E2EExchange.Create(Environment.GetEnvironmentVariable("BLACKGLASS_E2E_DIR"));
        var expected = Environment.GetEnvironmentVariable("BLACKGLASS_CP4_SYSTEM_ID") ?? "";
        var cluster = Cp4Cluster.FromSecrets();
        secrets.AddRange([cluster.SuperuserPassword, cluster.MigratorPassword, cluster.AppPassword]);
        await using var admin = new NpgsqlCp4ClusterAdmin(cluster);

        var before = await admin.BaselineAsync();
        x.Evidence("cluster-baseline-before.json", before);
        // The live refusal probe: a wrong expected identity is refused before any write, and the cluster is unchanged.
        await Assert.ThrowsAsync<Cp4Refusal>(() => Cp4Database.CreateAsync(admin, "0", x.Record, () => DateTime.UtcNow));
        Assert.Empty(Cp4Database.Differences(before, await admin.BaselineAsync()));
        Say("refusal probe: a wrong system identifier was refused before any write; cluster unchanged");

        var record = await Cp4Database.CreateAsync(admin, expected, x.Record, () => DateTime.UtcNow);
        Say($"owned database {record.Name} (oid {record.Oid}) created");
        x.Evidence("owned-database.json", record);
        Exception? failure = null;
        try { await RunAsync(cluster, record); }
        catch (Exception e)
        {
            failure = e;
            Say($"FAILED: {e.GetType().Name}: {e.Message}");
            throw;
        }
        finally
        {
            var dropped = await Cp4Database.DropAsync(admin, expected, x.Record);
            var after = await admin.BaselineAsync();
            var differences = Cp4Database.Differences(before, after);
            Say($"owned database dropped: {dropped}; cluster differences from the baseline: {differences.Count}");
            x.Evidence("cluster-baseline-after.json", after);
            x.Evidence("cluster-differences.json", differences);
            x.WriteBytes("evidence/transcript.log", Encoding.UTF8.GetBytes(string.Join("\n", transcript) + "\n"));
            var leaks = x.EvidenceTexts().Where(e => secrets.Any(s => s.Length > 0 && e.Text.Contains(s, StringComparison.Ordinal))).Select(e => e.File).ToList();
            if (failure is null)
            {
                Assert.True(dropped, "the owned database was present until the end");
                Assert.Empty(differences);
                Assert.Empty(leaks);
            }
        }
    }

    sealed class Api(Cp4Cluster cluster, string database, E2EExchange x, int port)
    {
        WebApplication? app;
        public string BaseUrl { get; } = $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}";

        public async Task StartAsync()
        {
            x.WriteBytes("secrets/db-app", Encoding.UTF8.GetBytes(cluster.AppPassword));
            var env = new Dictionary<string, string?>
            {
                ["BLACKGLASS_DB_HOST"] = cluster.Host,
                ["BLACKGLASS_DB_PORT"] = cluster.Port.ToString(CultureInfo.InvariantCulture),
                ["BLACKGLASS_DB_NAME"] = database,
                ["BLACKGLASS_DB_USER"] = "accounts_app",
                ["BLACKGLASS_DB_PASSWORD_FILE"] = x.PathOf("secrets/db-app"),
                ["ASPNETCORE_URLS"] = BaseUrl,
                ["BLACKGLASS_DEV_AUTH"] = "enabled",
                ["BLACKGLASS_CLAIM_WAIT_SECONDS"] = "0",
            };
            var error = new StringWriter();
            var start = await ServeCommand.StartAsync(env, error, default);
            app = start.App ?? throw new InvalidOperationException($"serve failed ({start.ExitCode}): {error}");
        }

        public async Task StopAsync()
        {
            if (app is null) return;
            await app.StopAsync();
            await app.DisposeAsync();
            app = null;
            File.Delete(x.PathOf("secrets/db-app"));
        }
    }

    sealed record Snapshot(int Balance, List<string> Ledger, Dictionary<string, int> Stacks, Dictionary<string, List<long>> Instances, Dictionary<long, int> Xp);

    NpgsqlDataSource su = null!;
    Api api = null!;
    LostResponseProxy proxy = null!;
    HttpClient http = null!;
    string hostSecret = "";
    volatile string? activeAttempt;

    async Task RunAsync(Cp4Cluster cluster, Cp4DatabaseRecord record)
    {
        await using var migrator = cluster.DataSource(record.Name, "accounts_migrator", cluster.MigratorPassword);
        await using var app = cluster.DataSource(record.Name, "accounts_app", cluster.AppPassword);
        await using var superuser = cluster.DataSource(record.Name, "postgres", cluster.SuperuserPassword);
        su = superuser;
        await new Migrator(migrator, MigrationCatalog.Embedded).MigrateAsync();
        Say($"migrated to schema {MigrationCatalog.Embedded.LatestVersion}");

        var hosts = new HostAdminStore(app);
        await hosts.AllowBuildAsync(EditorBuild, [new TemplateRef(Template, 1)], Op);
        var host = await hosts.RegisterAsync(HostId, 2, 7800, Op);
        hostSecret = host.Secret.Text;
        secrets.Add(hostSecret);
        Say($"seeded: build 'editor' allowlisted for {Template}:1 and catalogue {EditorBuild.CatalogueVersion} (this database only); host {HostId}");

        api = new Api(cluster, record.Name, x, FreePort());
        x.CreateDirectory("secrets");
        await api.StartAsync();
        proxy = new LostResponseProxy(new Uri(api.BaseUrl).Port);
        http = new HttpClient { BaseAddress = new Uri(api.BaseUrl), Timeout = TimeSpan.FromSeconds(30) };
        using var stopBeats = new CancellationTokenSource();
        var beating = BeatAsync(stopBeats.Token);
        Say($"API on {api.BaseUrl} (loopback, ephemeral); relay for the mission server on {proxy.BaseUrl}");
        x.WriteJson("host.json", new { baseUrl = proxy.BaseUrl });
        try
        {
            var a = await ScenarioAsync("A", dropStart: true, outage: false, new AccountAdminStore(app, GameCatalogue.Embedded));
            x.Evidence("scenario-A.json", a);
            var b = await ScenarioAsync("B", dropStart: false, outage: true, new AccountAdminStore(app, GameCatalogue.Embedded));
            x.Evidence("scenario-B.json", b);
            x.Evidence("relay-log.json", proxy.Log);
        }
        finally
        {
            await stopBeats.CancelAsync();
            try { await beating; } catch (OperationCanceledException) { }
            http.Dispose();
            await proxy.DisposeAsync();
            await api.StopAsync();
            Say("API stopped; relay closed");
        }
    }

    /// <summary>The agent's heartbeat every 5 s: build 'editor', a complete inventory listing the running unit. Failures (the
    /// outage) are only counted.</summary>
    async Task BeatAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await HeartbeatAsync(); } catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { }
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }

    async Task<int> HeartbeatAsync()
    {
        var units = activeAttempt is { } a ? $"[{{\"attemptId\":\"{a}\",\"activeState\":\"active\"}}]" : "[]";
        var (status, _) = await PostAsync("/agent/v1/heartbeat",
            $"{{\"buildRevision\":\"{EditorBuild.Revision}\",\"catalogueVersion\":\"{EditorBuild.CatalogueVersion}\",\"inventory\":{{\"complete\":true,\"units\":{units}}},\"outboxPending\":0}}",
            hostSecret);
        return status;
    }

    async Task<(int Status, byte[] Body)> PostAsync(string path, string json, string? bearer, Guid? idempotencyKey = null) =>
        await SendAsync(HttpMethod.Post, path, Encoding.UTF8.GetBytes(json), bearer, idempotencyKey);

    async Task<(int Status, byte[] Body)> SendAsync(HttpMethod method, string path, byte[]? body, string? bearer, Guid? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        if (bearer is not null) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
        if (idempotencyKey is { } key) request.Headers.Add("Idempotency-Key", key.ToString("D"));
        using var response = await http.SendAsync(request);
        return ((int)response.StatusCode, await response.Content.ReadAsByteArrayAsync());
    }

    static JsonElement Parse(byte[] body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    static string Text(byte[] body) => Encoding.UTF8.GetString(body);

    async Task<T> ScalarAsync<T>(string sql, object? args = null)
    {
        await using var conn = await su.OpenConnectionAsync();
        return (await conn.ExecuteScalarAsync<T>(sql, args))!;
    }

    async Task<List<T>> RowsAsync<T>(string sql, object? args = null)
    {
        await using var conn = await su.OpenConnectionAsync();
        return (await conn.QueryAsync<T>(sql, args)).ToList();
    }

    async Task<string> AwaitStateAsync(string sql, object args, Func<string, bool> done, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var state = await ScalarAsync<string?>(sql, args) ?? "";
            if (done(state)) return state;
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"{what}: still '{state}' after {timeout}.");
            await Task.Delay(250);
        }
    }

    async Task<Snapshot> SnapshotAsync(long account) => new(
        await ScalarAsync<int>("select balance from energy_wallets where account_id = @account", new { account }),
        await RowsAsync<string>("select kind || ' ' || delta || ' ' || balance_after || ' ' || idempotency_key from energy_ledger where account_id = @account order by entry_id", new { account }),
        (await RowsAsync<(string Key, int Quantity)>(
            "select coalesce(operative_id::text, 'stash') || ':' || definition_id, quantity from item_stacks where account_id = @account", new { account }))
            .ToDictionary(r => r.Key, r => r.Quantity),
        (await RowsAsync<(string Key, long Item)>(
            "select coalesce(operative_id::text, 'stash') || ':' || definition_id, item_id from item_instances where account_id = @account order by item_id", new { account }))
            .GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.Select(r => r.Item).ToList()),
        (await RowsAsync<(long Operative, int Xp)>("select operative_id, experience from operatives where account_id = @account", new { account }))
            .ToDictionary(r => r.Operative, r => r.Xp));

    async Task<Dictionary<string, object?>> ScenarioAsync(string s, bool dropStart, bool outage, AccountAdminStore accounts)
    {
        var evidence = new Dictionary<string, object?> { ["scenario"] = s };
        x.CreateDirectory($"{s}/credentials");
        x.CreateDirectory($"{s}/outbox");
        await x.WaitForJsonAsync($"{s}/unity-waiting.json", UnityBoot);
        Say($"[{s}] Unity is waiting");

        // The player: a new test account (its starter operatives and wallet provisioned in the same transaction), dev login, deploy.
        var issued = await accounts.CreateAccountAsync(null, null, Op);
        secrets.Add(issued.AccountKey.Text);
        var account = issued.AccountId;
        var accountId = Base58Id.Format(account);
        var operatives = await RowsAsync<long>("select operative_id from operatives where account_id = @account order by operative_id limit 3", new { account });
        var before = await SnapshotAsync(account);
        var (status, body) = await PostAsync("/v1/auth/dev/login", JsonSerializer.Serialize(new { accountKey = issued.AccountKey.Text }), null);
        Assert.Equal(200, status);
        var token = Parse(body).GetProperty("accessToken").GetString()!;
        secrets.Add(token);
        (status, body) = await PostAsync("/v1/attempts",
            JsonSerializer.Serialize(new { missionTemplate = Template, operativeIds = operatives.Select(Base58Id.Format).ToArray() }), token, Guid.NewGuid());
        Assert.True(status == 201, $"deploy answered {status}: {Text(body)}");
        var attempt = Parse(body).GetProperty("attemptId").GetString()!;
        var attemptGuid = Guid.Parse(attempt);
        Say($"[{s}] account {accountId} deployed {operatives.Count} operative(s): attempt {attempt}");
        evidence["account"] = accountId;
        evidence["attempt"] = attempt;
        evidence["operatives"] = operatives.Select(Base58Id.Format).ToArray();
        var seedAndSalt = await RowsAsync<(long Seed, byte[] Salt)>("select seed, retention_salt from attempt_secrets where attempt_id = @attemptGuid", new { attemptGuid });
        secrets.Add(seedAndSalt.Single().Seed.ToString(CultureInfo.InvariantCulture));
        secrets.Add(Convert.ToHexStringLower(seedAndSalt.Single().Salt));

        // The agent: heartbeat, claim, launching; the credential handed over as LoadCredential would.
        Assert.Equal(200, await HeartbeatAsync());
        (status, body) = await PostAsync("/agent/v1/claim", "{}", hostSecret, Guid.NewGuid());
        Assert.True(status == 200, $"claim answered {status}: {Text(body)}");
        var claim = Parse(body);
        Assert.Equal(attempt, claim.GetProperty("attemptId").GetString());
        var credential = claim.GetProperty("processCredential").GetString()!;
        secrets.Add(credential);
        var generation = claim.GetProperty("credentialGeneration").GetInt32();
        (status, body) = await PostAsync($"/agent/v1/attempts/{attempt}/launching", $"{{\"credentialGeneration\":{generation}}}", hostSecret);
        Assert.True(status == 200, $"launching answered {status}: {Text(body)}");
        x.WriteBytes($"{s}/credentials/process", Encoding.ASCII.GetBytes(credential));
        activeAttempt = attempt;
        if (dropStart)
            proxy.DropNextAnswer($"/participants/{accountId}/start", () => x.WriteJson($"{s}/start-dropped.json", new { utc = DateTime.UtcNow }));
        x.WriteJson($"{s}/start.json", new { attemptId = attempt, accountId });
        Say($"[{s}] claimed (generation {generation}) and launching; credential handed over{(dropStart ? "; the relay will lose the first start answer" : "")}");

        await AwaitStateAsync("select state from mission_attempts where attempt_id = @attemptGuid", new { attemptGuid }, st => st is "ready" or "running", TimeSpan.FromMinutes(5), "ready");
        File.Delete(x.PathOf($"{s}/credentials/process")); // read once by the process at its start
        Say($"[{s}] the process registered and reported ready; the credential file is deleted");

        (status, body) = await PostAsync($"/v1/attempts/{attempt}/tickets", "{}", token);
        Assert.True(status == 201, $"ticket answered {status}: {Text(body)}");
        var ticket = Parse(body).GetProperty("ticket").GetString()!;
        secrets.Add(ticket);
        x.WriteBytes($"{s}/ticket", Encoding.ASCII.GetBytes(ticket));
        Say($"[{s}] the player minted a join ticket (player route); handed to the server's AdmitTicket");

        await AwaitStateAsync("select state from attempt_participants where attempt_id = @attemptGuid", new { attemptGuid }, st => st != "admitted", TimeSpan.FromMinutes(3), "the start");
        var started = await RowsAsync<(string State, DateTime StartedAt)>("select state, started_at from attempt_participants where attempt_id = @attemptGuid", new { attemptGuid });
        Assert.Equal("joined", started.Single().State);
        var startedAt = started.Single().StartedAt;
        Say($"[{s}] the backend committed the start at {startedAt:O}");

        if (outage)
        {
            await api.StopAsync();
            x.WriteJson($"{s}/api-stopped.json", new { utc = DateTime.UtcNow });
            Say($"[{s}] the CP4 API is stopped (PostgreSQL untouched)");
        }

        var unity = await x.WaitForJsonAsync($"{s}/unity-result.json", UnityRun);
        activeAttempt = null;
        evidence["unity"] = unity;
        Say($"[{s}] the process exited {unity.GetProperty("exitCode").GetInt32()} ({unity.GetProperty("exitReason").GetString()})");
        Assert.Empty(Cp4EvidenceChecks.UnityReport(unity, outage)); // exit 0, held unchanged and paused, notice sent after the durable write, loot collected

        var ready = x.PathOf($"{s}/outbox/{accountId}.manifest.ready");
        Assert.False(File.Exists(x.PathOf($"{s}/outbox/{accountId}.manifest.written")), "the published result replaced the written one");
        var bytes = await File.ReadAllBytesAsync(ready);
        var parsed = Assert.IsType<ParseResult.Parsed>(ResultManifest.Parse(bytes));
        Assert.Equal(unity.GetProperty("manifestHash").GetString(), parsed.ManifestHash);
        Assert.Equal(attemptGuid, parsed.Manifest.AttemptId);
        evidence["manifestHash"] = parsed.ManifestHash;
        evidence["readyFileSha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes));
        evidence["readyFileBytes"] = bytes.Length;
        evidence["manifestOutcome"] = parsed.Manifest.Outcome;
        Say($"[{s}] published result {parsed.ManifestHash} ({bytes.Length} bytes, outcome {parsed.Manifest.Outcome})");

        if (outage)
        {
            // While the API is down: the result is on disk, nothing settled, voided or refunded.
            evidence["duringOutage"] = new
            {
                attemptState = await ScalarAsync<string>("select state from mission_attempts where attempt_id = @attemptGuid", new { attemptGuid }),
                participantState = await ScalarAsync<string>("select state from attempt_participants where attempt_id = @attemptGuid", new { attemptGuid }),
                settlements = await ScalarAsync<long>("select count(*) from participant_settlements where attempt_id = @attemptGuid", new { attemptGuid }),
                refunds = await ScalarAsync<long>("select count(*) from energy_ledger where attempt_id = @attemptGuid and kind = 'void_refund'", new { attemptGuid }),
            };
            Assert.Equal(0L, await ScalarAsync<long>("select count(*) from participant_settlements where attempt_id = @attemptGuid", new { attemptGuid }));
            Assert.Equal(0L, await ScalarAsync<long>("select count(*) from energy_ledger where attempt_id = @attemptGuid and kind = 'void_refund'", new { attemptGuid }));
            await api.StartAsync();
            Assert.Equal(200, await HeartbeatAsync());
            Say($"[{s}] during the outage: no settlement, no void, no refund; the CP4 API restarted on the same loopback port");
        }

        // The agent's relay of the published file, verbatim, to the existing authenticated result route; then a duplicate relay.
        var relays = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            (status, body) = await SendAsync(HttpMethod.Post, "/agent/v1/results", bytes, hostSecret);
            relays.Add($"{status} {Text(body)}");
        }
        evidence["relays"] = relays;
        Assert.Equal("200 {\"disposition\":\"settled\",\"outcome\":\"success\"}", relays[0]);
        Assert.Equal("200 {\"disposition\":\"duplicate\",\"outcome\":\"success\"}", relays[1]);
        (status, body) = await PostAsync($"/agent/v1/attempts/{attempt}/exited",
            $"{{\"exitCode\":{unity.GetProperty("exitCode").GetInt32()},\"signal\":null,\"results\":[{{\"accountId\":\"{accountId}\",\"present\":true}}],\"outbox\":{{\"complete\":true}}}}", hostSecret);
        evidence["exited"] = $"{status} {Text(body)}";
        Assert.Equal(200, status);
        await HeartbeatAsync();
        Say($"[{s}] relayed: {relays[0]} then {relays[1]}; exited: {status} {Text(body)}");

        // The player's receipt, twice: the same stored bytes.
        (status, body) = await SendAsync(HttpMethod.Get, $"/v1/attempts/{attempt}/receipt", null, token);
        Assert.True(status == 200, $"receipt answered {status}: {Text(body)}");
        var receiptText = Text(body);
        (status, body) = await SendAsync(HttpMethod.Get, $"/v1/attempts/{attempt}/receipt", null, token);
        Assert.Equal(receiptText, Text(body));
        var stored = await ScalarAsync<string>("select receipt from participant_settlements where attempt_id = @attemptGuid", new { attemptGuid });
        Assert.Equal(stored, receiptText);
        evidence["receipt"] = Parse(Encoding.UTF8.GetBytes(receiptText));
        Say($"[{s}] receipt fetched twice (identical, equal to the stored bytes)");

        // The database's account of it.
        var after = await SnapshotAsync(account);
        var settlement = await RowsAsync<(string Outcome, string? VoidBasis, string? RecordedHash, string? ManifestHash)>(
            "select outcome, void_basis, recorded_hash, manifest_hash from participant_settlements where attempt_id = @attemptGuid", new { attemptGuid });
        var submissions = await RowsAsync<string>(
            "select disposition || ' x' || receive_count from result_submissions where attempt_id = @attemptGuid order by submission_id", new { attemptGuid });
        var participant = await RowsAsync<(string State, DateTime StartedAt)>("select state, started_at from attempt_participants where attempt_id = @attemptGuid", new { attemptGuid });
        var attemptState = await ScalarAsync<string>("select state from mission_attempts where attempt_id = @attemptGuid", new { attemptGuid });
        var charges = await RowsAsync<string>("select idempotency_key from energy_ledger where attempt_id = @attemptGuid and kind = 'charge'", new { attemptGuid });
        var refunds = await ScalarAsync<long>("select count(*) from energy_ledger where attempt_id = @attemptGuid and kind in ('void_refund', 'release')", new { attemptGuid });
        var cost = await ScalarAsync<int>("select energy_cost from attempt_participants where attempt_id = @attemptGuid", new { attemptGuid });
        var exits = await RowsAsync<string>("select coalesce(exit_code::text, '-') || ' ' || outbox_complete from attempt_exit_reports where attempt_id = @attemptGuid", new { attemptGuid });
        evidence["database"] = new
        {
            attemptState, participant = participant.Select(p => new { p.State, p.StartedAt }), settlement = settlement.Select(r => new { r.Outcome, r.VoidBasis, r.RecordedHash, r.ManifestHash }),
            submissions, charges, refundsOrReleases = refunds, energyCost = cost, exits, ledgerBefore = before.Ledger, ledgerAfter = after.Ledger,
            balanceBefore = before.Balance, balanceAfter = after.Balance, stacksBefore = before.Stacks, stacksAfter = after.Stacks,
            instancesBefore = before.Instances, instancesAfter = after.Instances, xpBefore = before.Xp, xpAfter = after.Xp,
        };

        Assert.Equal("settled", attemptState);
        Assert.Equal(("success", (string?)null, parsed.ManifestHash), (settlement.Single().Outcome, settlement.Single().VoidBasis, settlement.Single().ManifestHash));
        Assert.Equal(outage ? "absent" : "matched", settlement.Single().RecordedHash); // B: the recorded notice never reached the backend
        Assert.Equal(startedAt, participant.Single().StartedAt); // started once; a retried start replays, never restarts
        Assert.Equal([$"charge:{attempt}"], charges);
        Assert.Equal(0L, refunds);
        // The wallet moved by exactly its new ledger entries: one reserve of the cost, the zero-delta charge (and settle), and any
        // regeneration that accrued during the run (1 per 10 minutes); never a release or a refund.
        var newEntries = after.Ledger.Skip(before.Ledger.Count).Select(e => e.Split(' ')).ToList();
        evidence["newLedgerEntries"] = newEntries.Select(e => string.Join(' ', e)).ToArray();
        Assert.Equal(["reserve", "charge"], newEntries.Where(e => e[0] is not ("regen" or "settle")).Select(e => e[0]).ToArray());
        Assert.Equal(-cost, newEntries.Where(e => e[0] != "regen").Sum(e => int.Parse(e[1], CultureInfo.InvariantCulture)));
        Assert.Equal(before.Balance + newEntries.Sum(e => int.Parse(e[1], CultureInfo.InvariantCulture)), after.Balance);
        Assert.Equal(["settled x2"], submissions); // one submission, the duplicate relay counted on it
        Assert.Empty(Cp4EvidenceChecks.Receipt(Parse(Encoding.UTF8.GetBytes(receiptText)))); // something granted, XP awarded, energy settled
        AssertRewards(Parse(Encoding.UTF8.GetBytes(receiptText)), before, after);

        // The relay's view: the ticket redeemed once, the start lost then answered (A) or answered (B), the notice answered (A) or
        // every attempt refused by the stopped API (B).
        evidence["processRequests"] = proxy.Log.Where(e => e.Path.Contains(attempt, StringComparison.Ordinal)).ToList();
        Assert.Empty(Cp4EvidenceChecks.RelayLog(proxy.Log, attempt, accountId, dropStart, outage));
        Say($"[{s}] verified: settled once ({settlement.Single().Outcome}, recorded {settlement.Single().RecordedHash}), one charge, no refund, started once, rewards match the receipt");
        return evidence;
    }

    /// <summary>The inventory and XP changes are exactly the receipt's: each granted item's ids appear, each granted or consumed
    /// quantity moves the operative's holding by that much, and each operative's experience rises by its XP line.</summary>
    static void AssertRewards(JsonElement receipt, Snapshot before, Snapshot after)
    {
        var delta = new Dictionary<string, int>();
        void Add(string key, int q) => delta[key] = delta.GetValueOrDefault(key) + q;
        foreach (var g in receipt.GetProperty("granted").EnumerateArray())
        {
            var key = $"{Id(g.GetProperty("operativeId").GetString()!)}:{g.GetProperty("definitionId").GetString()}";
            Add(key, g.GetProperty("quantity").GetInt32());
            foreach (var id in g.GetProperty("itemIds").EnumerateArray().Select(e => Id(e.GetString()!)))
            {
                Assert.Contains(id, after.Instances.GetValueOrDefault(key) ?? []);
                Assert.DoesNotContain(id, before.Instances.Values.SelectMany(v => v));
            }
        }
        foreach (var c in receipt.GetProperty("consumed").EnumerateArray())
            Add($"{Id(c.GetProperty("operativeId").GetString()!)}:{c.GetProperty("definitionId").GetString()}", -c.GetProperty("quantity").GetInt32());
        int Held(Snapshot s, string key) => s.Stacks.GetValueOrDefault(key) + (s.Instances.GetValueOrDefault(key)?.Count ?? 0);
        foreach (var key in before.Stacks.Keys.Concat(before.Instances.Keys).Concat(after.Stacks.Keys).Concat(after.Instances.Keys).Distinct())
            Assert.True(Held(after, key) - Held(before, key) == delta.GetValueOrDefault(key), $"{key}: held {Held(before, key)} -> {Held(after, key)}, receipt says {delta.GetValueOrDefault(key)}");
        var xp = receipt.GetProperty("xp").EnumerateArray().ToDictionary(e => Id(e.GetProperty("operativeId").GetString()!), e => e.GetProperty("amount").GetInt32());
        foreach (var (operative, was) in before.Xp)
            Assert.Equal(was + xp.GetValueOrDefault(operative), after.Xp[operative]);
    }

    static long Id(string text) => Base58Id.TryParse(text, out var id) ? id : throw new FormatException("not a Base58 id");

    static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}
