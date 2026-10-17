using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using Blackglass.Accounts.Domain.Attempts;
using Blackglass.Accounts.Domain.Auth;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Attempts;
using Blackglass.Accounts.Persistence.Hosts;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>
/// The Stage 8 lifecycle end to end (Task 10; owner requirements 5 and 12): driven entirely over HTTP from deploy to a running
/// attempt by test stand-ins for the agent and the mission process, with every log line and every client and agent body free
/// of the seed and the salt; and backend unavailability (a database outage longer than both leases, an API restart, a lapsed
/// host lease) that never voids, cancels, releases or refunds a started participant, while deadlines stay database time.
/// </summary>
[Trait("Requires", "Postgres")]
public class LifecycleTests : IAsyncLifetime
{
    TestDatabase db = null!;
    MissionKit kit = null!;

    public async Task InitializeAsync()
    {
        db = await MigratedAsync();
        kit = await MissionKit.CreateAsync(db);
    }

    public async Task DisposeAsync()
    {
        try { Assert.Empty(await InvariantFailuresAsync(db)); }
        finally { await db.DisposeAsync(); }
    }

    static async Task<(HttpStatusCode Status, string Body)> SendAsync(ApiServer api, HttpMethod method, string path, string? token, string? body = null, string? key = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        using var response = await api.Http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    Task<string> StateAsync(Guid attempt) => ScalarAsSuperuserAsync<string>(db, """
        select a.state || ':' || p.state || ':' || h.state || ':' || (select count(*) from energy_ledger l where l.attempt_id = a.attempt_id and l.kind = 'release')
               || ':' || (select count(*) from energy_ledger l where l.attempt_id = a.attempt_id and l.kind = 'charge')
        from mission_attempts a join attempt_participants p using (attempt_id) join energy_holds h using (attempt_id, account_id)
        where a.attempt_id = @attempt
        """, new { attempt });

    [Fact]
    public async Task The_whole_lifecycle_to_running_is_driven_over_HTTP_and_no_log_or_client_body_holds_the_seed_or_salt()
    {
        var hostSecret = (await new HostAdminStore(db.AsApp()).RotateSecretAsync("h1", Op)).Secret.Text;
        var logs = new CapturingLoggerProvider();
        await using var api = await ApiServer.StartAsync(db, new() { ["BLACKGLASS_DEV_AUTH"] = "enabled", ["BLACKGLASS_CLAIM_WAIT_SECONDS"] = "0" },
            b => { b.Logging.ClearProviders(); b.Logging.SetMinimumLevel(LogLevel.Trace); b.Logging.AddProvider(logs); });
        var bodies = new List<string>(); // every client and agent response: none may carry the seed or the salt

        // The player.
        var issued = await kit.Accounts.CreateAccountAsync(null, null, Op);
        var login = await api.Http.PostAsJsonAsync("/v1/auth/dev/login", new { accountKey = issued.AccountKey.Text });
        var token = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.GetProperty("accessToken").GetString()!;
        var operative = await ScalarAsSuperuserAsync<long>(db, "select min(operative_id) from operatives where account_id = @a", new { a = issued.AccountId });

        // The agent heartbeats, the player deploys, the agent claims and launches.
        var beat = JsonSerializer.Serialize(new { buildRevision = "r1", catalogueVersion = MissionKit.Build.CatalogueVersion, inventory = new { complete = true, units = Array.Empty<object>() }, outboxPending = 0 });
        var hb = await SendAsync(api, HttpMethod.Post, "/agent/v1/heartbeat", hostSecret, beat);
        Assert.Equal(HttpStatusCode.OK, hb.Status);
        bodies.Add(hb.Body);
        var deploy = await SendAsync(api, HttpMethod.Post, "/v1/attempts", token,
            JsonSerializer.Serialize(new { missionTemplate = "procedural-standard", operativeIds = new[] { Blackglass.Accounts.Domain.Ids.Base58Id.Format(operative) } }), Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Created, deploy.Status);
        bodies.Add(deploy.Body);
        var attempt = JsonDocument.Parse(deploy.Body).RootElement.GetProperty("attemptId").GetString()!;
        var claim = await SendAsync(api, HttpMethod.Post, "/agent/v1/claim", hostSecret, "{}", key: Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, claim.Status);
        bodies.Add(claim.Body);
        var claimed = JsonDocument.Parse(claim.Body).RootElement;
        Assert.Equal(attempt, claimed.GetProperty("attemptId").GetString());
        var credential = claimed.GetProperty("processCredential").GetString()!;
        var launching = await SendAsync(api, HttpMethod.Post, $"/agent/v1/attempts/{attempt}/launching", hostSecret, $$"""{"credentialGeneration":{{claimed.GetProperty("credentialGeneration").GetInt32()}}}""");
        Assert.Equal(HttpStatusCode.OK, launching.Status);
        bodies.Add(launching.Body);

        // The process registers (the one response with the seed), reports ready, redeems the player's ticket and starts it.
        var register = await SendAsync(api, HttpMethod.Post, $"/server/v1/attempts/{attempt}/sessions", credential,
            $$"""{"buildRevision":"r1","catalogueVersion":"{{MissionKit.Build.CatalogueVersion}}"}""");
        Assert.Equal(HttpStatusCode.OK, register.Status);
        var session = JsonDocument.Parse(register.Body).RootElement.GetProperty("processSessionId").GetString()!;
        PoolLine[] pool = [new(0, 0, "item.medkit", 2), new(1, 0, "item.light-vest", 1)];
        var ready = await SendAsync(api, HttpMethod.Post, $"/server/v1/attempts/{attempt}/ready", credential, JsonSerializer.Serialize(new
        {
            processSessionId = session, layoutHash = "cf2e14dece9cc2a6", lootTableVersion = MissionKit.Table.Version, lootHash = LootHash.Compute(MissionKit.Table.Version, pool),
            objectives = new[] { new { objectiveId = "extract", required = true } },
            lootPool = pool.Select(l => new { containerIndex = l.ContainerIndex, line = l.Line, definitionId = l.DefinitionId, quantity = l.Quantity }),
        }));
        Assert.Equal(HttpStatusCode.OK, ready.Status);
        bodies.Add(ready.Body);
        var view = await SendAsync(api, HttpMethod.Get, $"/v1/attempts/{attempt}", token);
        bodies.Add(view.Body);
        Assert.Equal(7000, JsonDocument.Parse(view.Body).RootElement.GetProperty("join").GetProperty("port").GetInt32());
        var mint = await SendAsync(api, HttpMethod.Post, $"/v1/attempts/{attempt}/tickets", token, "{}");
        Assert.Equal(HttpStatusCode.Created, mint.Status);
        bodies.Add(mint.Body);
        var redeem = await SendAsync(api, HttpMethod.Post, $"/server/v1/attempts/{attempt}/tickets/redeem", credential,
            $$"""{"processSessionId":"{{session}}","ticket":"{{JsonDocument.Parse(mint.Body).RootElement.GetProperty("ticket").GetString()}}"}""");
        Assert.Equal(HttpStatusCode.OK, redeem.Status);
        bodies.Add(redeem.Body);
        var account = Blackglass.Accounts.Domain.Ids.Base58Id.Format(issued.AccountId);
        var start = await SendAsync(api, HttpMethod.Post, $"/server/v1/attempts/{attempt}/participants/{account}/start", credential, $$"""{"processSessionId":"{{session}}"}""");
        Assert.Equal(HttpStatusCode.OK, start.Status);
        bodies.Add(start.Body);
        var again = await SendAsync(api, HttpMethod.Post, $"/server/v1/attempts/{attempt}/participants/{account}/start", credential, $$"""{"processSessionId":"{{session}}"}""");
        Assert.Equal(start, again); // a lost acknowledgement: the same answer
        var processBeat = await SendAsync(api, HttpMethod.Post, $"/server/v1/attempts/{attempt}/heartbeat", credential,
            $$"""{"processSessionId":"{{session}}","phase":"running","participants":[{"accountId":"{{account}}","connected":true}]}""");
        Assert.Equal((HttpStatusCode.OK, "{}"), processBeat);

        // Running, charged once, the inventory locked.
        var running = await SendAsync(api, HttpMethod.Get, $"/v1/attempts/{attempt}", token);
        bodies.Add(running.Body);
        Assert.Equal(("running", "joined", "charged"), (JsonDocument.Parse(running.Body).RootElement.GetProperty("state").GetString(),
            JsonDocument.Parse(running.Body).RootElement.GetProperty("participantState").GetString(),
            JsonDocument.Parse(running.Body).RootElement.GetProperty("energy").GetProperty("state").GetString()));
        Assert.Equal("running:joined:charged:0:1", await StateAsync(Guid.Parse(attempt)));
        var inventory = await SendAsync(api, HttpMethod.Get, "/v1/inventory", token);
        Assert.True(JsonDocument.Parse(inventory.Body).RootElement.GetProperty("locked").GetBoolean());

        // Secrecy (owner requirement 5): the seed reached the registered process only; the salt reached nothing.
        var (seed, salt) = await (await db.AsSuperuser().OpenConnectionAsync()).QuerySingleAsync<(int, byte[])>(
            "select seed, retention_salt from attempt_secrets where attempt_id = @a::uuid", new { a = attempt });
        Assert.Equal(seed, JsonDocument.Parse(register.Body).RootElement.GetProperty("seed").GetInt32());
        var spellings = new[] { seed.ToString(System.Globalization.CultureInfo.InvariantCulture), Convert.ToHexString(salt), Convert.ToHexStringLower(salt), Convert.ToBase64String(salt) };
        foreach (var text in bodies.Append(logs.Text))
            foreach (var spelling in spellings)
                Assert.DoesNotContain(spelling, text);
        foreach (var spelling in spellings.Skip(1)) Assert.DoesNotContain(spelling, register.Body);
        // Nor any credential: the logs show at most an 8-hex hash prefix.
        foreach (var secret in new[] { hostSecret, credential, token, JsonDocument.Parse(mint.Body).RootElement.GetProperty("ticket").GetString()! })
            Assert.DoesNotContain(secret, logs.Text);
    }

    async Task<MissionKit.Mission> RunningAsync()
    {
        var m = await kit.ReadyAsync();
        await kit.RedeemAsync(m);
        Assert.IsType<StartResult.Started>(await new StartStore(db.AsApp()).StartAsync(m.Attempt, m.Account, m.Session, default));
        Assert.Equal(new ProcessHeartbeatResult.Ok(), await kit.Processes.HeartbeatAsync(m.Attempt, new ProcessHeartbeatRequest(m.Session, "running", []), default));
        return m;
    }

    [Fact]
    public async Task A_database_outage_during_running_never_voids()
    {
        var m = await RunningAsync();
        var attempts = new AttemptStore(db.AsApp(), processLeaseSeconds: 5);
        // Both leases lapse while the database is out (backdated before the outage: time is never mocked in the API).
        await ExecuteAsSuperuserAsync(db, """
            update mission_attempts set last_process_heartbeat_at = clock_timestamp() - interval '1 hour' where attempt_id = @a;
            update mission_hosts set last_heartbeat_at = clock_timestamp() - interval '1 hour';
            """, new { a = m.Attempt });
        await using (var api = await ApiServer.StartAsync(db))
        {
            await ApiServer.SetDatabaseReachableAsync(db, false);
            try
            {
                await Assert.ThrowsAnyAsync<NpgsqlException>(() => attempts.SweepAsync(10));
                await Assert.ThrowsAnyAsync<NpgsqlException>(() => kit.Hosts.ExitedAsync("h1", m.Attempt, new ExitedRequest(null, "SIGKILL", []), default));
                var during = await SendAsync(api, HttpMethod.Post, $"/server/v1/attempts/{m.Attempt}/heartbeat", m.Credential.Text,
                    $$"""{"processSessionId":"{{m.Session}}","phase":"running","participants":[]}""");
                Assert.Equal(HttpStatusCode.ServiceUnavailable, during.Status); // unavailable, never a 401 that would stop the process
            }
            finally { await ApiServer.SetDatabaseReachableAsync(db, true); }
        }
        NpgsqlConnection.ClearAllPools();
        // Back: the lease makes it unresponsive at most; a heartbeat restores it; nothing was cancelled, released or refunded.
        Assert.Equal(1, (await attempts.SweepAsync(10)).Unresponsive);
        Assert.Equal("unresponsive:joined:charged:0:1", await StateAsync(m.Attempt));
        Assert.Equal(new ProcessHeartbeatResult.Ok(), await kit.Processes.HeartbeatAsync(m.Attempt, new ProcessHeartbeatRequest(m.Session, "running", []), default));
        Assert.Equal("running:joined:charged:0:1", await StateAsync(m.Attempt));
        Assert.Null(await ScalarAsSuperuserAsync<DateTime?>(db, "select slot_released_at from mission_attempts where attempt_id = @a", new { a = m.Attempt }));
    }

    [Fact]
    public async Task An_API_restart_during_running_changes_nothing()
    {
        var m = await RunningAsync();
        var before = await StateAsync(m.Attempt);
        for (var i = 0; i < 2; i++)
        {
            await using var api = await ApiServer.StartAsync(db, new() { ["BLACKGLASS_ATTEMPT_SWEEP_SECONDS"] = "1" });
            await Task.Delay(1500); // at least one sweep runs in this process
        }
        Assert.Equal(before, await StateAsync(m.Attempt));
        Assert.Equal(new ProcessHeartbeatResult.Ok(), await kit.Processes.HeartbeatAsync(m.Attempt, new ProcessHeartbeatRequest(m.Session, "running", []), default));
    }

    [Fact]
    public async Task A_lapsed_host_lease_never_cancels_a_running_attempt_or_frees_its_slot()
    {
        var m = await RunningAsync();
        await ExecuteAsSuperuserAsync(db, "update mission_hosts set last_heartbeat_at = clock_timestamp() - interval '1 hour'");
        var report = await new AttemptStore(db.AsApp()).SweepAsync(10);
        Assert.Equal((0, 0), (report.Released, report.Unresponsive));
        Assert.Equal("running:joined:charged:0:1", await StateAsync(m.Attempt));
        Assert.Null(await ScalarAsSuperuserAsync<DateTime?>(db, "select slot_released_at from mission_attempts where attempt_id = @a", new { a = m.Attempt }));
    }

    [Fact]
    public async Task A_backend_restart_before_start_keeps_the_deadlines_authoritative()
    {
        var m = await kit.ReadyAsync();
        await kit.RedeemAsync(m);
        const string Deadlines = "select row(assign_deadline, launch_deadline, join_deadline)::text from mission_attempts where attempt_id = @a";
        var before = await ScalarAsSuperuserAsync<string>(db, Deadlines, new { a = m.Attempt });
        await using (var api = await ApiServer.StartAsync(db)) { }
        await using (var api = await ApiServer.StartAsync(db)) { }
        Assert.Equal(before, await ScalarAsSuperuserAsync<string>(db, Deadlines, new { a = m.Attempt }));
        await using (var su = await db.AsSuperuser().OpenConnectionAsync())
        await using (var tx = await su.BeginTransactionAsync())
        {
            await su.ExecuteAsync("set local session_replication_role = replica", transaction: tx);
            await su.ExecuteAsync("update mission_attempts set join_deadline = clock_timestamp() - interval '1 second' where attempt_id = @a", new { a = m.Attempt }, tx);
            await tx.CommitAsync();
        }
        Assert.IsType<StartResult.DeadlinePassed>(await new StartStore(db.AsApp()).StartAsync(m.Attempt, m.Account, m.Session, default));
        Assert.Equal(1, (await new AttemptStore(db.AsApp()).SweepAsync(10)).Released);
        Assert.Equal("cancelled:released:released:1:0", await StateAsync(m.Attempt));
    }
}
