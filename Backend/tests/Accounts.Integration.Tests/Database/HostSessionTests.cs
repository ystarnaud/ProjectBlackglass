using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dapper;
using Blackglass.Accounts.Domain.Auth;
using Blackglass.Accounts.Domain.Hosts;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence;
using Blackglass.Accounts.Persistence.Accounts;
using Blackglass.Accounts.Persistence.Attempts;
using Blackglass.Accounts.Persistence.Hosts;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>
/// Host and process authentication and the host heartbeat (Stage 8 Task 4; owner rulings B3, B6, plan revision 2 R2): the
/// <c>Host</c> scheme accepts only an enabled agent host's current <c>hs1_</c> secret on the agent routes and nothing else
/// does; a process credential authenticates only for its own, unended attempt; the heartbeat stamps the database time and the
/// reported build, frees a slot only on a complete unit inventory that no longer lists an ended attempt, and lists the ended
/// attempts whose units remain as <c>stop</c>.
/// </summary>
[Trait("Requires", "Postgres")]
public class HostSessionTests : IAsyncLifetime
{
    TestDatabase db = null!;
    ApiServer api = null!;
    OpaqueSecret secret = null!;
    static readonly string Catalogue = Blackglass.Accounts.Domain.Catalogue.GameCatalogue.Embedded.Version;
    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync()
    {
        db = await MigratedAsync();
        secret = (await new HostAdminStore(db.AsApp()).RegisterAsync("h1", 4, 7000, Op)).Secret;
        api = await ApiServer.StartAsync(db, new() { ["BLACKGLASS_DEV_AUTH"] = "enabled" });
    }

    public async Task DisposeAsync()
    {
        try
        {
            await api.DisposeAsync();
            Assert.Empty(await InvariantFailuresAsync(db));
        }
        finally { await db.DisposeAsync(); }
    }

    static string HeartbeatBody(bool complete = true, params (Guid Attempt, string State)[] units) => JsonSerializer.Serialize(new
    {
        buildRevision = "r1", catalogueVersion = Catalogue,
        inventory = new { complete, units = units.Select(u => new { attemptId = u.Attempt.ToString("D"), activeState = u.State }) },
        outboxPending = 0,
    });

    async Task<(HttpStatusCode Status, string Body)> PostAsync(string path, string? token, string body, string contentType = "application/json")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, contentType) };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await api.Http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    Task<(HttpStatusCode Status, string Body)> HeartbeatAsync(string body, string? token = null) => PostAsync("/agent/v1/heartbeat", token ?? secret.Text, body);

    // ---- authentication ----

    [Fact]
    public async Task Only_a_host_secret_authenticates_an_agent_route()
    {
        Assert.Equal(HttpStatusCode.OK, (await HeartbeatAsync(HeartbeatBody())).Status);
        foreach (var token in new[] { OpaqueSecret.New(OpaqueSecret.AccessTokenPrefix).Text, OpaqueSecret.New(OpaqueSecret.ProcessCredentialPrefix).Text,
                                      OpaqueSecret.New(OpaqueSecret.HostSecretPrefix).Text, secret.Text + "x", "hs1_short" })
        {
            var (status, body) = await HeartbeatAsync(HeartbeatBody(), token);
            Assert.Equal((HttpStatusCode.Unauthorized, ""), (status, body));
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync("/agent/v1/heartbeat", null, HeartbeatBody())).Status);
        Assert.Equal(401, await api.SendRawAsync($"POST /agent/v1/heartbeat HTTP/1.1\nAuthorization: Bearer {secret.Text}\nAuthorization: Bearer {secret.Text}\nContent-Type: application/json\n", HeartbeatBody()));
        Assert.Equal(401, await api.SendRawAsync($"POST /agent/v1/heartbeat HTTP/1.1\nAuthorization: Basic {secret.Text}\nContent-Type: application/json\n", HeartbeatBody()));
        // The host secret opens no client route either.
        using var client = new HttpRequestMessage(HttpMethod.Get, $"/v1/attempts/{Guid.CreateVersion7()}");
        client.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret.Text);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Http.SendAsync(client)).StatusCode);
    }

    [Fact]
    public async Task A_disabled_host_and_a_rotated_secret_are_401()
    {
        var hosts = new HostAdminStore(db.AsApp());
        var rotated = (await hosts.RotateSecretAsync("h1", Op)).Secret;
        Assert.Equal(HttpStatusCode.Unauthorized, (await HeartbeatAsync(HeartbeatBody())).Status);
        Assert.Equal(HttpStatusCode.OK, (await HeartbeatAsync(HeartbeatBody(), rotated.Text)).Status);
        // A disable revokes the secret for good: re-enabling needs a rotation, and the old secret never works again (R8).
        await hosts.SwitchAsync("h1", HostSwitch.Disable, Op);
        Assert.Equal(HttpStatusCode.Unauthorized, (await HeartbeatAsync(HeartbeatBody(), rotated.Text)).Status);
        rotated = (await hosts.RotateSecretAsync("h1", Op)).Secret;
        await hosts.SwitchAsync("h1", HostSwitch.Enable, Op);
        Assert.Equal(HttpStatusCode.OK, (await HeartbeatAsync(HeartbeatBody(), rotated.Text)).Status);
        // A drained host still authenticates and heartbeats (its running attempts continue); it is told it is drained.
        await hosts.SwitchAsync("h1", HostSwitch.Drain, Op);
        var (status, body) = await HeartbeatAsync(HeartbeatBody(), rotated.Text);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(JsonDocument.Parse(body).RootElement.GetProperty("drained").GetBoolean());
    }

    [Fact]
    public async Task A_process_credential_authenticates_only_its_own_unended_attempt()
    {
        var processes = new ProcessStore(db.AsApp());
        var account = await NewAccountAsync();
        var attempt = await AttemptFixtures.AdmitAsync(db, account);
        await AttemptFixtures.MoveAsync(db, attempt, "assigned", "h1");
        var credential = OpaqueSecret.New(OpaqueSecret.ProcessCredentialPrefix);
        await ExecuteAsSuperuserAsync(db, "update mission_attempts set process_credential_hash = @hash, credential_generation = 2 where attempt_id = @attempt",
            new { hash = credential.Hash(), attempt });
        Assert.Equal(attempt, (await processes.AuthenticateAsync(credential.Hash(), default))?.AttemptId);
        Assert.Null(await processes.AuthenticateAsync(OpaqueSecret.New(OpaqueSecret.ProcessCredentialPrefix).Hash(), default));
        await AttemptFixtures.MoveAsync(db, attempt, "cancelled", "h1");
        Assert.Null(await processes.AuthenticateAsync(credential.Hash(), default)); // ended: refused, whatever route
    }

    // ---- heartbeat ----

    [Fact]
    public async Task Heartbeat_stamps_the_database_time_and_reported_build()
    {
        var before = await ScalarAsSuperuserAsync<DateTime>(db, "select clock_timestamp()");
        var (status, body) = await HeartbeatAsync(HeartbeatBody());
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("""{"drained":false,"stop":[]}""", body);
        await using var su = await db.AsSuperuser().OpenConnectionAsync();
        var row = await su.QuerySingleAsync<(DateTime At, string Build, string Catalogue)>("select last_heartbeat_at, build_revision, catalogue_version from mission_hosts where host_id = 'h1'");
        Assert.True(row.At >= before && row.At <= await su.ExecuteScalarAsync<DateTime>("select clock_timestamp()"));
        Assert.Equal(("r1", Catalogue), (row.Build, row.Catalogue));
        Assert.Equal(1L, await su.ExecuteScalarAsync<long>("select count(*) from operator_actions")); // heartbeats aren't audited
    }

    [Fact]
    public async Task Heartbeat_bodies_are_strict()
    {
        foreach (var bad in new[] { "{}", "[]", HeartbeatBody().Replace("\"r1\"", "\"-r1\""), HeartbeatBody().Replace("\"outboxPending\":0", "\"outboxPending\":0,\"hostId\":\"h2\"") })
            Assert.Equal(HttpStatusCode.BadRequest, (await HeartbeatAsync(bad)).Status);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await PostAsync("/agent/v1/heartbeat", secret.Text, HeartbeatBody(), "text/plain")).Status);
        Assert.Null(await ScalarAsSuperuserAsync<DateTime?>(db, "select last_heartbeat_at from mission_hosts where host_id = 'h1'"));
    }

    async Task<long> NewAccountAsync()
    {
        await using var conn = await db.AsApp().OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<long>("insert into accounts (status) values ('active') returning account_id");
    }

    async Task<Guid> AttemptOnHostAsync(params string[] states)
    {
        var attempt = await AttemptFixtures.AdmitAsync(db, await NewAccountAsync());
        foreach (var s in states) await AttemptFixtures.MoveAsync(db, attempt, s, "h1");
        return attempt;
    }

    Task<DateTime?> ReleasedAtAsync(Guid attempt) => ScalarAsSuperuserAsync<DateTime?>(db, "select slot_released_at from mission_attempts where attempt_id = @attempt", new { attempt });

    [Fact]
    public async Task Heartbeat_frees_the_slots_of_ended_attempts_no_longer_listed_and_asks_to_stop_the_rest()
    {
        var listed = await AttemptOnHostAsync("assigned", "cancelled");
        var gone = await AttemptOnHostAsync("assigned", "cancelled");
        var running = await AttemptOnHostAsync("assigned", "launching", "ready", "running");
        var (status, body) = await HeartbeatAsync(HeartbeatBody(true, (listed, "active")));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal($$"""{"drained":false,"stop":["{{listed}}"]}""", body);
        Assert.NotNull(await ReleasedAtAsync(gone));     // ended and absent from a complete inventory: confirmed gone
        Assert.Null(await ReleasedAtAsync(listed));      // its unit is still there: told to stop, slot kept
        Assert.Null(await ReleasedAtAsync(running));     // R2: a running attempt's slot is freed only by an exit report
        var releasedAt = await ReleasedAtAsync(gone);
        Assert.Equal(HttpStatusCode.OK, (await HeartbeatAsync(HeartbeatBody())).Status);
        Assert.Equal(releasedAt, await ReleasedAtAsync(gone)); // written once
        Assert.NotNull(await ReleasedAtAsync(listed));
        Assert.Null(await ReleasedAtAsync(running));
    }

    [Fact]
    public async Task An_incomplete_inventory_frees_no_slot()
    {
        var gone = await AttemptOnHostAsync("assigned", "cancelled");
        var listed = await AttemptOnHostAsync("assigned", "cancelled");
        var (status, body) = await HeartbeatAsync(HeartbeatBody(false, (listed, "deactivating")));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal($$"""{"drained":false,"stop":["{{listed}}"]}""", body); // stop still names what it did list
        Assert.Null(await ReleasedAtAsync(gone));
        Assert.Null(await ReleasedAtAsync(listed));
    }

    [Fact]
    public async Task A_heartbeat_ignores_units_of_other_hosts_and_unknown_attempts()
    {
        await ExecuteAsSuperuserAsync(db, "insert into mission_hosts (host_id, kind, secret_hash, capacity, enabled, port_base) values ('h2', 'agent', decode(repeat('07', 32), 'hex'), 2, true, 8000)");
        var theirs = await AttemptFixtures.AdmitAsync(db, await NewAccountAsync());
        await AttemptFixtures.MoveAsync(db, theirs, "assigned", "h2");
        await AttemptFixtures.MoveAsync(db, theirs, "cancelled", "h2");
        var (status, body) = await HeartbeatAsync(HeartbeatBody(true, (theirs, "active"), (Guid.CreateVersion7(), "active")));
        Assert.Equal((HttpStatusCode.OK, """{"drained":false,"stop":[]}"""), (status, body));
        Assert.Null(await ReleasedAtAsync(theirs)); // never another host's slot
    }

    [Fact]
    public async Task A_lapsed_host_keeps_its_slots_and_gets_no_capacity()
    {
        await new HostAdminStore(db.AsApp()).AllowBuildAsync(new BuildIdentity("r1", Catalogue), [new TemplateRef("procedural-standard", 1)], Op);
        Assert.Equal(HttpStatusCode.OK, (await HeartbeatAsync(HeartbeatBody())).Status);
        var cancelled = await AttemptOnHostAsync("assigned", "cancelled");
        await using (var conn = await db.AsApp().OpenConnectionAsync())
            Assert.True(await ServiceHealth.CheckAsync(conn, null, false, 30, "procedural-standard", 1, Catalogue, default));
        await ExecuteAsSuperuserAsync(db, "update mission_hosts set last_heartbeat_at = clock_timestamp() - interval '1 hour'");
        await using (var conn = await db.AsApp().OpenConnectionAsync())
            Assert.False(await ServiceHealth.CheckAsync(conn, null, false, 30, "procedural-standard", 1, Catalogue, default));
        Assert.Null(await ReleasedAtAsync(cancelled)); // a lapsed lease frees nothing
    }

    [Fact]
    public async Task A_failing_heartbeat_is_a_database_failure_and_writes_nothing()
    {
        await ExecuteAsSuperuserAsync(db,
            "create function bg_test_fail() returns trigger language plpgsql as $$ begin raise exception 'injected' using errcode = 'P0001'; end $$; " +
            "create trigger bg_test_fail before update on mission_hosts for each row execute function bg_test_fail();");
        Assert.Equal(HttpStatusCode.InternalServerError, (await HeartbeatAsync(HeartbeatBody())).Status);
        Assert.Null(await ScalarAsSuperuserAsync<DateTime?>(db, "select last_heartbeat_at from mission_hosts where host_id = 'h1'"));
        await ExecuteAsSuperuserAsync(db, "drop trigger bg_test_fail on mission_hosts; drop function bg_test_fail();");
    }

    // ---- B2: error detail and parameters never reach a log ----

    [Fact]
    public void The_production_data_source_neither_logs_parameters_nor_requests_error_detail()
    {
        var settings = new DbSettings(db.Host, 5432, db.Name, "accounts_app", "unused-password-xyz");
        using var ds = settings.CreateDataSource("probe");
        var cs = new Npgsql.NpgsqlConnectionStringBuilder(ds.ConnectionString);
        Assert.False(cs.IncludeErrorDetail);
        Assert.DoesNotContain("unused-password-xyz", settings.ToString());
        // No source file turns on parameter logging, wires Npgsql into a logger, or asks for error detail (Checkpoint 1 M8).
        var root = Path.Combine(AppContext.BaseDirectory, "source");
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (var forbidden in new[] { "EnableParameterLogging", "UseLoggerFactory", "IncludeErrorDetail = true", "NpgsqlLoggingConfiguration" })
                Assert.False(text.Contains(forbidden, StringComparison.Ordinal), $"{Path.GetRelativePath(root, file)} contains {forbidden}");
        }
    }

    [Fact]
    public void The_accounts_stack_postgres_runs_with_terse_error_verbosity()
    {
        var lines = ComposeText.Lines("compose.yaml");
        var command = Assert.Single(ComposeText.Block(lines, "services", "postgres"), l => l.TrimStart().StartsWith("command:", StringComparison.Ordinal));
        Assert.Contains("\"-c\", \"log_error_verbosity=terse\"", command);
        Assert.Contains("\"-c\", \"cluster_name=blackglass-accounts\"", command);
        // The test cluster keeps the default verbosity (B2: no change to it).
        var test = ComposeText.Lines("compose.test.yaml");
        Assert.DoesNotContain(test, l => l.Contains("log_error_verbosity", StringComparison.Ordinal));
    }
}
