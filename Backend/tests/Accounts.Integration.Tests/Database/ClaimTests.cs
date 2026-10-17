using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;
using Blackglass.Accounts.Domain.Attempts;
using Blackglass.Accounts.Domain.Auth;
using Blackglass.Accounts.Domain.Hosts;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence;
using Blackglass.Accounts.Persistence.Attempts;
using Blackglass.Accounts.Persistence.Hosts;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>
/// The claim and the launching report (Stage 8 Task 5; spec §7.4; owner rulings B4-B6, plan revision 2 R1, R7). A claim
/// assigns the oldest compatible admitted attempt to the calling host, once, under the host row lock, with the lowest free
/// port and a fresh process credential; a retry of its key while the attempt is still assigned is a recovery (same attempt,
/// a new credential, the next generation), refused once launching began; launching names the generation it launches with.
/// </summary>
[Trait("Requires", "Postgres")]
public class ClaimTests : IAsyncLifetime
{
    TestDatabase db = null!;
    static readonly string Catalogue = Blackglass.Accounts.Domain.Catalogue.GameCatalogue.Embedded.Version;
    static readonly BuildIdentity Build = new("r1", Catalogue);
    NpgsqlDataSource wide = null!;
    HostStore hosts = null!;

    public async Task InitializeAsync()
    {
        db = await MigratedAsync();
        var port = int.Parse(db.EnvFor("app")[DbSettings.PortVar]!, System.Globalization.CultureInfo.InvariantCulture);
        wide = new DbSettings(db.Host, port, db.Name, "accounts_app", db.Password("app")).CreateDataSource("blackglass-tests", maxPoolSize: 24);
        hosts = new HostStore(wide, leaseSeconds: 30);
        var admin = new HostAdminStore(db.AsApp());
        await admin.AllowBuildAsync(Build, [new TemplateRef("procedural-standard", 1)], Op);
        foreach (var (host, portBase) in new[] { ("h1", 7000), ("h2", 8000) })
        {
            await admin.RegisterAsync(host, 3, portBase, Op);
            await BeatAsync(host);
        }
    }

    public async Task DisposeAsync()
    {
        try { Assert.Empty(await InvariantFailuresAsync(db)); }
        finally
        {
            await wide.DisposeAsync();
            await db.DisposeAsync();
        }
    }

    async Task BeatAsync(string host, BuildIdentity? build = null) =>
        await hosts.HeartbeatAsync(host, await AttemptFixtures.HostSecretHashAsync(db, host), new HeartbeatRequest(build ?? Build, true, [], 0), default);

    async Task<long> NewAccountAsync()
    {
        await using var conn = await db.AsApp().OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<long>("insert into accounts (status) values ('active') returning account_id");
    }

    async Task<Guid> AdmitAsync() => await AttemptFixtures.AdmitAsync(db, await NewAccountAsync());

    async Task<ClaimResult> ClaimAsync(string host, Guid? key = null) => await hosts.ClaimAsync(host, await AttemptFixtures.HostSecretHashAsync(db, host), key ?? Guid.NewGuid(), launchDeadlineSeconds: 120, default);

    async Task<(string State, string? Host, int? Port, int? Generation)> RowAsync(Guid attempt)
    {
        await using var su = await db.AsSuperuser().OpenConnectionAsync();
        return await su.QuerySingleAsync<(string, string?, int?, int?)>("select state, host_id, mission_port, credential_generation from mission_attempts where attempt_id = @attempt", new { attempt });
    }

    // ---- assignment ----

    [Fact]
    public async Task A_claim_assigns_the_oldest_compatible_attempt_with_the_lowest_free_port()
    {
        var first = await AdmitAsync();
        var second = await AdmitAsync();
        var claimed = Assert.IsType<ClaimResult.Claimed>(await ClaimAsync("h1"));
        Assert.Equal((first, 7000, 1, false), (claimed.AttemptId, claimed.MissionPort, claimed.CredentialGeneration, claimed.Recovered));
        Assert.Equal(OpaqueSecret.ProcessCredentialPrefix, claimed.Credential.Prefix);
        Assert.Equal(claimed.Credential.Hash(), await ScalarAsSuperuserAsync<byte[]>(db, "select process_credential_hash from mission_attempts where attempt_id = @first", new { first }));
        var now = await ScalarAsSuperuserAsync<DateTime>(db, "select clock_timestamp()");
        Assert.InRange(claimed.LaunchDeadline, now.AddSeconds(110), now.AddSeconds(121));
        Assert.Equal(("assigned", "h1", 7000, 1), await RowAsync(first));
        var next = Assert.IsType<ClaimResult.Claimed>(await ClaimAsync("h1"));
        Assert.Equal((second, 7001), (next.AttemptId, next.MissionPort));
        Assert.Equal(0L, await ScalarAsSuperuserAsync<long>(db, "select count(*) from operator_actions where action like 'claim%'"));
    }

    [Fact]
    public async Task A_claim_skips_incompatible_templates_catalogues_and_disabled_builds()
    {
        var admin = new HostAdminStore(db.AsApp());
        // An attempt of another template version and one admitted under another catalogue: never this host's.
        await ExecuteAsSuperuserAsync(db, "insert into mission_templates (template_id, version, energy_cost, min_operatives, max_operatives) values ('procedural-standard', 2, 0, 1, 3)");
        var v2 = await AdmitAsync();
        await ExecuteAsSuperuserAsync(db, "begin; set local session_replication_role = replica; update mission_attempts set template_version = 2 where attempt_id = @v2; update attempt_participants set template_version = 2 where attempt_id = @v2; commit", new { v2 });
        var otherCatalogue = await AdmitAsync();
        await ExecuteAsSuperuserAsync(db, "begin; set local session_replication_role = replica; update mission_attempts set catalogue_version = 'sha256:' || repeat('cd', 32) where attempt_id = @otherCatalogue; commit", new { otherCatalogue });
        Assert.IsType<ClaimResult.NoCandidate>(await ClaimAsync("h1"));
        // The build listing v2: claimable.
        await admin.AllowBuildAsync(Build, [new TemplateRef("procedural-standard", 2)], Op);
        Assert.Equal(v2, Assert.IsType<ClaimResult.Claimed>(await ClaimAsync("h1")).AttemptId);
        // A disabled build claims nothing, and its host is unavailable until it reports an allowed build.
        var compatible = await AdmitAsync();
        await admin.DisableBuildAsync(Build, Op);
        Assert.IsType<ClaimResult.HostUnavailable>(await ClaimAsync("h1"));
        await admin.AllowBuildAsync(Build, [new TemplateRef("procedural-standard", 1)], Op);
        Assert.Equal(compatible, Assert.IsType<ClaimResult.Claimed>(await ClaimAsync("h1")).AttemptId);
        Assert.Equal(("admitted", null, null, null), await RowAsync(otherCatalogue));
    }

    [Fact]
    public async Task A_host_at_capacity_gets_no_candidate_until_a_slot_is_confirmed_free()
    {
        var claimed = new List<Guid>();
        for (var i = 0; i < 4; i++) await AdmitAsync();
        for (var i = 0; i < 3; i++) claimed.Add(Assert.IsType<ClaimResult.Claimed>(await ClaimAsync("h1")).AttemptId);
        Assert.IsType<ClaimResult.NoCandidate>(await ClaimAsync("h1"));
        // Cancelled is not free (B6): the slot stays until the agent confirms the unit gone.
        await AttemptFixtures.MoveAsync(db, claimed[1], "cancelled", "h1");
        Assert.IsType<ClaimResult.NoCandidate>(await ClaimAsync("h1"));
        await hosts.HeartbeatAsync("h1", await AttemptFixtures.HostSecretHashAsync(db, "h1"), new HeartbeatRequest(Build, true, [new UnitReport(claimed[0], "active"), new UnitReport(claimed[2], "active")], 0), default);
        var reused = Assert.IsType<ClaimResult.Claimed>(await ClaimAsync("h1"));
        Assert.Equal(7001, reused.MissionPort); // the freed port, the lowest free one
    }

    [Fact]
    public async Task Ten_claimers_and_three_attempts_assign_each_once()
    {
        for (var round = 0; round < 5; round++)
        {
            var attempts = new[] { await AdmitAsync(), await AdmitAsync(), await AdmitAsync() };
            var start = new TaskCompletionSource();
            var claims = Enumerable.Range(0, 10).Select(i => Task.Run(async () =>
            {
                await start.Task;
                return await ClaimAsync(i % 2 == 0 ? "h1" : "h2");
            })).ToArray();
            start.SetResult();
            var results = await Task.WhenAll(claims);
            var won = results.OfType<ClaimResult.Claimed>().ToList();
            Assert.Equal(attempts.Order(), won.Select(w => w.AttemptId).Order());
            Assert.All(results, r => Assert.True(r is ClaimResult.Claimed or ClaimResult.NoCandidate, r.GetType().Name));
            await using var su = await db.AsSuperuser().OpenConnectionAsync();
            Assert.Equal(0L, await su.ExecuteScalarAsync<long>(
                "select count(*) from (select host_id from mission_attempts where host_id is not null and slot_released_at is null group by host_id having count(*) > 3) over_capacity"));
            Assert.Equal(0L, await su.ExecuteScalarAsync<long>(
                "select count(*) from (select host_id, mission_port from mission_attempts where slot_released_at is null and host_id is not null group by 1, 2 having count(*) > 1) clashes"));
            // Free every slot for the next round (cancel and confirm), as an agent would.
            foreach (var w in won) await AttemptFixtures.MoveAsync(db, w.AttemptId, "cancelled", w.HostId);
            await su.ExecuteAsync("update mission_attempts set slot_released_at = clock_timestamp() where slot_released_at is null and host_id is not null");
        }
    }

    [Fact]
    public async Task Two_hosts_race_for_one_attempt_and_one_wins()
    {
        for (var round = 0; round < 10; round++)
        {
            var attempt = await AdmitAsync();
            var start = new TaskCompletionSource();
            var racers = new[] { "h1", "h2" }.Select(h => Task.Run(async () => { await start.Task; return await ClaimAsync(h); })).ToArray();
            start.SetResult();
            var results = await Task.WhenAll(racers);
            var winner = Assert.Single(results.OfType<ClaimResult.Claimed>());
            Assert.Equal(attempt, winner.AttemptId);
            Assert.Single(results.OfType<ClaimResult.NoCandidate>());
            await AttemptFixtures.MoveAsync(db, attempt, "cancelled", winner.HostId);
            await ExecuteAsSuperuserAsync(db, "update mission_attempts set slot_released_at = clock_timestamp() where attempt_id = @attempt", new { attempt });
        }
    }

    [Fact]
    public async Task A_claim_never_takes_an_attempt_past_its_assign_deadline()
    {
        var expired = await AdmitAsync();
        await AttemptFixtures.ExpireDeadlineAsync(db, expired);
        Assert.IsType<ClaimResult.NoCandidate>(await ClaimAsync("h1"));
        Assert.Equal(("admitted", null, null, null), await RowAsync(expired));
    }

    [Fact]
    public async Task A_drained_disabled_lapsed_or_buildless_host_takes_nothing()
    {
        await AdmitAsync();
        var admin = new HostAdminStore(db.AsApp());
        await admin.SwitchAsync("h1", HostSwitch.Drain, Op);
        Assert.IsType<ClaimResult.HostUnavailable>(await ClaimAsync("h1"));
        await admin.SwitchAsync("h1", HostSwitch.Enable, Op);
        await ExecuteAsSuperuserAsync(db, "update mission_hosts set last_heartbeat_at = clock_timestamp() - interval '1 hour' where host_id = 'h1'");
        Assert.IsType<ClaimResult.HostUnavailable>(await ClaimAsync("h1"));
        await BeatAsync("h1", new BuildIdentity("unlisted", Catalogue));
        Assert.IsType<ClaimResult.HostUnavailable>(await ClaimAsync("h1"));
        await admin.SwitchAsync("h1", HostSwitch.Disable, Op);
        Assert.IsType<ClaimResult.Unauthenticated>(await ClaimAsync("h1"));
        Assert.Equal(0L, await ScalarAsSuperuserAsync<long>(db, "select count(*) from mission_attempts where host_id is not null"));
    }

    [Fact]
    public async Task A_claim_waits_and_returns_an_attempt_admitted_during_the_wait_and_otherwise_nothing()
    {
        var clock = Stopwatch.StartNew();
        Assert.IsType<ClaimResult.NoCandidate>(await hosts.ClaimWaitingAsync("h1", await AttemptFixtures.HostSecretHashAsync(db, "h1"), Guid.NewGuid(), 120, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100), default));
        Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(0.9), TimeSpan.FromSeconds(4));
        var waiting = hosts.ClaimWaitingAsync("h1", await AttemptFixtures.HostSecretHashAsync(db, "h1"), Guid.NewGuid(), 120, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(100), default);
        await Task.Delay(300);
        var attempt = await AdmitAsync();
        Assert.Equal(attempt, Assert.IsType<ClaimResult.Claimed>(await waiting).AttemptId);
        // A cancelled request ends the wait.
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var h2Secret = await AttemptFixtures.HostSecretHashAsync(db, "h2");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hosts.ClaimWaitingAsync("h2", h2Secret, Guid.NewGuid(), 120, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(100), cancel.Token));
    }

    [Fact]
    public async Task A_claim_holds_no_connection_while_waiting()
    {
        var port = int.Parse(db.EnvFor("app")[DbSettings.PortVar]!, System.Globalization.CultureInfo.InvariantCulture);
        await using var narrow = new DbSettings(db.Host, port, db.Name, "accounts_app", db.Password("app")).CreateDataSource("blackglass-tests", maxPoolSize: 3);
        var store = new HostStore(narrow, leaseSeconds: 30);
        var (h1, h2) = (await AttemptFixtures.HostSecretHashAsync(db, "h1"), await AttemptFixtures.HostSecretHashAsync(db, "h2"));
        var clock = Stopwatch.StartNew();
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(i =>
            store.ClaimWaitingAsync(i % 2 == 0 ? "h1" : "h2", i % 2 == 0 ? h1 : h2, Guid.NewGuid(), 120, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(200), default)));
        Assert.All(results, r => Assert.IsType<ClaimResult.NoCandidate>(r));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"waiting claims took {clock.Elapsed}: each must hold a connection only per try");
    }

    [Fact]
    public async Task Claim_and_launching_over_http()
    {
        var secret = (await new HostAdminStore(db.AsApp()).RotateSecretAsync("h1", Op)).Secret;
        await using var api = await ApiServer.StartAsync(db, new() { ["BLACKGLASS_CLAIM_WAIT_SECONDS"] = "0" });
        async Task<(HttpStatusCode Status, string Body, string? CacheControl)> PostAsync(string path, string body, string? key)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret.Text);
            if (key is not null) request.Headers.Add("Idempotency-Key", key);
            using var response = await api.Http.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsStringAsync(), response.Headers.CacheControl?.ToString());
        }
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync("/agent/v1/claim", "{}", Guid.NewGuid().ToString())).Status);
        foreach (var badKey in new[] { null, "not-a-uuid", Guid.NewGuid().ToString().ToUpperInvariant() })
            Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("/agent/v1/claim", "{}", badKey)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("/agent/v1/claim", """{"hostId":"h2"}""", Guid.NewGuid().ToString())).Status);

        var attempt = await AdmitAsync();
        var key = Guid.NewGuid().ToString();
        var (status, body, cache) = await PostAsync("/agent/v1/claim", "{}", key);
        Assert.Equal((HttpStatusCode.OK, "no-store"), (status, cache));
        var claim = JsonDocument.Parse(body).RootElement;
        Assert.Equal(["attemptId", "missionPort", "processCredential", "credentialGeneration", "launchDeadline", "recovered"], claim.EnumerateObject().Select(p => p.Name));
        Assert.Equal((attempt.ToString(), 7000, 1, false), (claim.GetProperty("attemptId").GetString(), claim.GetProperty("missionPort").GetInt32(),
            claim.GetProperty("credentialGeneration").GetInt32(), claim.GetProperty("recovered").GetBoolean()));
        Assert.True(OpaqueSecret.TryParse(claim.GetProperty("processCredential").GetString(), OpaqueSecret.ProcessCredentialPrefix, out _));
        var recovered = JsonDocument.Parse((await PostAsync("/agent/v1/claim", "{}", key)).Body).RootElement;
        Assert.Equal((2, true), (recovered.GetProperty("credentialGeneration").GetInt32(), recovered.GetProperty("recovered").GetBoolean()));

        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync($"/agent/v1/attempts/{attempt}/launching", """{"credentialGeneration":1}""", null)).Status);
        var launching = await PostAsync($"/agent/v1/attempts/{attempt}/launching", """{"credentialGeneration":2}""", null);
        Assert.Equal((HttpStatusCode.OK, """{"state":"launching"}"""), (launching.Status, launching.Body));
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync("/agent/v1/claim", "{}", key)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync($"/agent/v1/attempts/{attempt.ToString().ToUpperInvariant()}/launching", """{"credentialGeneration":2}""", null)).Status);
    }

    // ---- recovery (R1) and launching ----

    [Fact]
    public async Task A_claim_retry_while_assigned_recovers_with_a_new_credential_and_generation()
    {
        var attempt = await AdmitAsync();
        var key = Guid.NewGuid();
        var first = Assert.IsType<ClaimResult.Claimed>(await ClaimAsync("h1", key));
        var recovered = Assert.IsType<ClaimResult.Claimed>(await ClaimAsync("h1", key));
        Assert.Equal((attempt, first.MissionPort, first.LaunchDeadline, 2, true), (recovered.AttemptId, recovered.MissionPort, recovered.LaunchDeadline, recovered.CredentialGeneration, recovered.Recovered));
        Assert.NotEqual(first.Credential.Text, recovered.Credential.Text);
        var processes = new ProcessStore(db.AsApp());
        Assert.Null(await processes.AuthenticateAsync(first.Credential.Hash(), default));
        Assert.Equal(attempt, (await processes.AuthenticateAsync(recovered.Credential.Hash(), default))?.AttemptId);
        // The same key from another host is never this host's attempt: an ordinary claim with no candidate.
        Assert.IsType<ClaimResult.NoCandidate>(await ClaimAsync("h2", key));
    }

    [Fact]
    public async Task A_claim_retry_after_launching_is_refused_and_keeps_the_credential()
    {
        var attempt = await AdmitAsync();
        var key = Guid.NewGuid();
        var claimed = Assert.IsType<ClaimResult.Claimed>(await ClaimAsync("h1", key));
        Assert.Equal(new LaunchingResult.Accepted("launching"), await hosts.LaunchingAsync("h1", await AttemptFixtures.HostSecretHashAsync(db, "h1"), attempt, 1, default));
        Assert.IsType<ClaimResult.InvalidState>(await ClaimAsync("h1", key));
        Assert.Equal(attempt, (await new ProcessStore(db.AsApp()).AuthenticateAsync(claimed.Credential.Hash(), default))?.AttemptId);
        Assert.Equal(("launching", "h1", 7000, 1), await RowAsync(attempt));
    }

    [Fact]
    public async Task Launching_with_a_superseded_generation_is_refused_and_writes_nothing()
    {
        var attempt = await AdmitAsync();
        var key = Guid.NewGuid();
        await ClaimAsync("h1", key);
        await ClaimAsync("h1", key); // recovery: generation 2
        Assert.Equal(new LaunchingResult.CredentialSuperseded(), await hosts.LaunchingAsync("h1", await AttemptFixtures.HostSecretHashAsync(db, "h1"), attempt, 1, default));
        Assert.Equal(("assigned", "h1", 7000, 2), await RowAsync(attempt));
        Assert.Equal(new LaunchingResult.Accepted("launching"), await hosts.LaunchingAsync("h1", await AttemptFixtures.HostSecretHashAsync(db, "h1"), attempt, 2, default));
        Assert.Equal(new LaunchingResult.Accepted("launching"), await hosts.LaunchingAsync("h1", await AttemptFixtures.HostSecretHashAsync(db, "h1"), attempt, 2, default)); // a lost acknowledgement
        Assert.Equal(new LaunchingResult.CredentialSuperseded(), await hosts.LaunchingAsync("h1", await AttemptFixtures.HostSecretHashAsync(db, "h1"), attempt, 1, default));
    }

    [Fact]
    public async Task Launching_is_refused_after_cancel_and_for_another_hosts_attempt()
    {
        var attempt = await AdmitAsync();
        await ClaimAsync("h1");
        Assert.Equal(new LaunchingResult.NotFound(), await hosts.LaunchingAsync("h2", await AttemptFixtures.HostSecretHashAsync(db, "h2"), attempt, 1, default));
        Assert.Equal(new LaunchingResult.NotFound(), await hosts.LaunchingAsync("h1", await AttemptFixtures.HostSecretHashAsync(db, "h1"), Guid.CreateVersion7(), 1, default));
        await AttemptFixtures.MoveAsync(db, attempt, "cancelled", "h1");
        Assert.Equal(new LaunchingResult.InvalidState(), await hosts.LaunchingAsync("h1", await AttemptFixtures.HostSecretHashAsync(db, "h1"), attempt, 1, default));
    }

    [Fact]
    public async Task Claim_recovery_racing_launching_leaves_exactly_one_consistent_outcome()
    {
        for (var round = 0; round < 15; round++)
        {
            var attempt = await AdmitAsync();
            var key = Guid.NewGuid();
            var host = round % 2 == 0 ? "h1" : "h2";
            Assert.IsType<ClaimResult.Claimed>(await ClaimAsync(host, key));
            var start = new TaskCompletionSource();
            var recovery = Task.Run(async () => { await start.Task; return await ClaimAsync(host, key); });
            var launching = Task.Run(async () => { await start.Task; return await hosts.LaunchingAsync(host, await AttemptFixtures.HostSecretHashAsync(db, host), attempt, 1, default); });
            start.SetResult();
            var (r, l) = (await recovery, await launching);
            var row = await RowAsync(attempt);
            if (r is ClaimResult.Claimed c)
            {
                // The recovery committed first: generation 2, and launching with generation 1 was refused.
                Assert.Equal((2, new LaunchingResult.CredentialSuperseded(), "assigned"), (c.CredentialGeneration, l, row.State));
            }
            else
            {
                // Launching committed first: the recovery was refused and generation 1 stays in force.
                Assert.IsType<ClaimResult.InvalidState>(r);
                Assert.Equal((new LaunchingResult.Accepted("launching"), "launching", 1), (l, row.State, row.Generation));
            }
            await AttemptFixtures.MoveAsync(db, attempt, "cancelled", host);
            await ExecuteAsSuperuserAsync(db, "update mission_attempts set slot_released_at = clock_timestamp() where attempt_id = @attempt", new { attempt });
        }
    }

    [Fact]
    public async Task Claims_heartbeats_and_slot_releases_race_without_deadlock()
    {
        // R7: claims and heartbeats lock host then attempts; a slot release elsewhere locks the attempt only. Many of each at once:
        // no 40P01, no lock timeout, every attempt assigned at most once.
        for (var i = 0; i < 6; i++) await AdmitAsync();
        var cancelled = new List<Guid>();
        for (var i = 0; i < 2; i++)
        {
            var c = Assert.IsType<ClaimResult.Claimed>(await ClaimAsync("h1")).AttemptId;
            await AttemptFixtures.MoveAsync(db, c, "cancelled", "h1");
            cancelled.Add(c);
        }
        var start = new TaskCompletionSource();
        var work = new List<Task>();
        for (var i = 0; i < 6; i++) work.Add(Task.Run(async () => { await start.Task; await ClaimAsync(i % 2 == 0 ? "h1" : "h2"); }));
        for (var i = 0; i < 6; i++) work.Add(Task.Run(async () => { await start.Task; await BeatAsync(i % 2 == 0 ? "h1" : "h2"); }));
        foreach (var c in cancelled)
            work.Add(Task.Run(async () =>
            {
                await start.Task;
                await using var conn = await wide.OpenConnectionAsync();
                await conn.ExecuteAsync("update mission_attempts set slot_released_at = clock_timestamp() where attempt_id = @c and slot_released_at is null", new { c });
            }));
        start.SetResult();
        await Task.WhenAll(work);
        Assert.Equal(0L, await ScalarAsSuperuserAsync<long>(db,
            "select count(*) from (select host_id, mission_port from mission_attempts where slot_released_at is null and host_id is not null group by 1, 2 having count(*) > 1) clashes"));
    }
}
