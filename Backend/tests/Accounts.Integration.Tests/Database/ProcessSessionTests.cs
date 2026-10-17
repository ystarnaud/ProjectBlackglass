using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Blackglass.Accounts.Domain.Attempts;
using Blackglass.Accounts.Domain.Auth;
using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.Domain.Hosts;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Accounts;
using Blackglass.Accounts.Persistence.Attempts;
using Blackglass.Accounts.Persistence.Catalogue;
using Blackglass.Accounts.Persistence.Hosts;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>
/// The process session (Stage 8 Task 7; spec §7.4, §8.4, §16.3; owner rulings B4, B10, plan revision 2 R1, R3, R4): register
/// (exactly the claimed, still allowed build; the seed only here, never the salt), ready (objectives and a pool inside the
/// catalogue's table with its canonical hash, or the attempt is cancelled), the process heartbeat (the lease, joined ⇄ left for
/// started participants only) and the process routes' authentication (only the attempt's own unended credential).
/// </summary>
[Trait("Requires", "Postgres")]
public class ProcessSessionTests : IAsyncLifetime
{
    TestDatabase db = null!;
    HostStore hosts = null!;
    ProcessStore processes = null!;
    AttemptStore attempts = null!;
    CatalogueHistoryStore catalogues = null!;
    static readonly string Catalogue = GameCatalogue.Embedded.Version;
    static readonly BuildIdentity Build = new("r1", Catalogue);
    static readonly LootTable Table = GameCatalogue.Embedded.Document.LootTable;

    public async Task InitializeAsync()
    {
        db = await MigratedAsync();
        hosts = new HostStore(db.AsApp(), leaseSeconds: 30);
        processes = new ProcessStore(db.AsApp());
        catalogues = new CatalogueHistoryStore(db.AsApp());
        attempts = new AttemptStore(db.AsApp(), processLeaseSeconds: 30);
        var admin = new HostAdminStore(db.AsApp());
        await admin.AllowBuildAsync(Build, [new TemplateRef("procedural-standard", 1)], Op);
        await admin.RegisterAsync("h1", 4, 7000, Op);
        await hosts.HeartbeatAsync("h1", await AttemptFixtures.HostSecretHashAsync(db, "h1"), new HeartbeatRequest(Build, true, [], 0), default);
    }

    public async Task DisposeAsync()
    {
        try { Assert.Empty(await InvariantFailuresAsync(db)); }
        finally { await db.DisposeAsync(); }
    }

    async Task<long> NewAccountAsync()
    {
        await using var conn = await db.AsApp().OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<long>("insert into accounts (status) values ('active') returning account_id");
    }

    sealed record Claimed(Guid Attempt, long Account, OpaqueSecret Credential, Guid Key);

    async Task<Claimed> ClaimedAsync()
    {
        var account = await NewAccountAsync();
        var attempt = await AttemptFixtures.AdmitAsync(db, account);
        var key = Guid.NewGuid();
        var c = Assert.IsType<ClaimResult.Claimed>(await hosts.ClaimAsync("h1", await AttemptFixtures.HostSecretHashAsync(db, "h1"), key, 120, default));
        Assert.Equal(attempt, c.AttemptId);
        return new Claimed(attempt, account, c.Credential, key);
    }

    async Task<(Claimed Claim, Guid Session)> RegisteredAsync()
    {
        var claim = await ClaimedAsync();
        var r = Assert.IsType<RegisterResult.Registered>(await processes.RegisterAsync(claim.Attempt, claim.Credential.Hash(), new RegisterRequest(Build), default));
        return (claim, r.ProcessSessionId);
    }

    static readonly PoolLine[] Pool = [new(0, 0, "item.medkit", 2), new(0, 1, "item.light-vest", 1), new(2, 0, "item.combat-blade", 1)];
    static readonly Objective[] Objectives = [new("extract", true), new("intel.cache-1", false)];

    static ReadyReport Ready(Guid session, PoolLine[]? pool = null, Objective[]? objectives = null, string? lootHash = null, string layout = "cf2e14dece9cc2a6")
    {
        pool ??= Pool;
        return new ReadyReport(session, layout, Table.Version, lootHash ?? LootHash.Compute(Table.Version, pool), objectives ?? Objectives, pool);
    }

    async Task<(Claimed Claim, Guid Session)> ReadyAsync()
    {
        var (claim, session) = await RegisteredAsync();
        Assert.IsType<ReadyResult.Ready>(await processes.ReadyAsync(claim.Attempt, Ready(session), catalogues, 120, default));
        return (claim, session);
    }

    Task<string> StateAsync(Guid attempt) => ScalarAsSuperuserAsync<string>(db, "select state || ':' || coalesce(cancel_reason, '-') from mission_attempts where attempt_id = @attempt", new { attempt });

    // ---- register ----

    [Fact]
    public async Task Register_returns_the_session_seed_and_snapshots_never_the_salt()
    {
        var claim = await ClaimedAsync();
        Assert.Equal(new LaunchingResult.Accepted("launching"), await hosts.LaunchingAsync("h1", await AttemptFixtures.HostSecretHashAsync(db, "h1"), claim.Attempt, 1, default));
        var r = Assert.IsType<RegisterResult.Registered>(await processes.RegisterAsync(claim.Attempt, claim.Credential.Hash(), new RegisterRequest(Build), default));
        await using var su = await db.AsSuperuser().OpenConnectionAsync();
        var (seed, salt) = await su.QuerySingleAsync<(int, byte[])>("select seed, retention_salt from attempt_secrets where attempt_id = @a", new { a = claim.Attempt });
        Assert.Equal((seed, "procedural-standard", 1, Catalogue), (r.Seed, r.TemplateId, r.TemplateVersion, r.CatalogueVersion));
        Assert.Equal(claim.Account, Assert.Single(r.Participants).AccountId);
        Assert.Equal(7, r.ProcessSessionId.Version);
        Assert.Equal(r.ProcessSessionId, await su.ExecuteScalarAsync<Guid>("select process_session_id from mission_attempts where attempt_id = @a", new { a = claim.Attempt }));
        var text = JsonSerializer.Serialize(r);
        Assert.DoesNotContain(Convert.ToHexString(salt), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToBase64String(salt), text);
    }

    [Fact]
    public async Task Register_from_assigned_moves_to_launching_and_a_replay_returns_the_same_session()
    {
        var claim = await ClaimedAsync();
        var first = Assert.IsType<RegisterResult.Registered>(await processes.RegisterAsync(claim.Attempt, claim.Credential.Hash(), new RegisterRequest(Build), default));
        Assert.Equal("launching:-", await StateAsync(claim.Attempt));
        var again = Assert.IsType<RegisterResult.Registered>(await processes.RegisterAsync(claim.Attempt, claim.Credential.Hash(), new RegisterRequest(Build), default));
        Assert.Equal(first.ProcessSessionId, again.ProcessSessionId);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(again));
        // The agent's late launching report, with the current generation, is a lost acknowledgement.
        Assert.Equal(new LaunchingResult.Accepted("launching"), await hosts.LaunchingAsync("h1", await AttemptFixtures.HostSecretHashAsync(db, "h1"), claim.Attempt, 1, default));
    }

    [Theory]
    [InlineData("other-revision")]
    [InlineData("other-catalogue")]
    [InlineData("build-disabled")]
    public async Task Register_with_another_build_or_a_disabled_build_is_BuildNotAllowed_and_writes_nothing(string fault)
    {
        var claim = await ClaimedAsync();
        var build = fault switch
        {
            "other-revision" => new BuildIdentity("r2", Catalogue),
            "other-catalogue" => new BuildIdentity("r1", "sha256:" + new string('c', 64)),
            _ => Build,
        };
        if (fault == "build-disabled") await new HostAdminStore(db.AsApp()).DisableBuildAsync(Build, Op);
        Assert.IsType<RegisterResult.BuildNotAllowed>(await processes.RegisterAsync(claim.Attempt, claim.Credential.Hash(), new RegisterRequest(build), default));
        Assert.Equal("assigned:-", await StateAsync(claim.Attempt));
        Assert.Null(await ScalarAsSuperuserAsync<Guid?>(db, "select process_session_id from mission_attempts where attempt_id = @a", new { a = claim.Attempt }));
    }

    [Fact]
    public async Task Claim_recovery_racing_register_leaves_exactly_one_consistent_outcome()
    {
        for (var round = 0; round < 12; round++)
        {
            var claim = await ClaimedAsync();
            var start = new TaskCompletionSource();
            var recovery = Task.Run(async () => { await start.Task; return await hosts.ClaimAsync("h1", await AttemptFixtures.HostSecretHashAsync(db, "h1"), claim.Key, 120, default); });
            var register = Task.Run(async () => { await start.Task; return await processes.RegisterAsync(claim.Attempt, claim.Credential.Hash(), new RegisterRequest(Build), default); });
            start.SetResult();
            var (r, g) = (await recovery, await register);
            if (r is ClaimResult.Claimed c)
            {
                // The recovery committed first (still assigned): the old credential can't register (checked inside register's own
                // transaction, Final M1), nothing was written, and the new credential registers.
                Assert.Equal(2, c.CredentialGeneration);
                Assert.IsType<RegisterResult.Unauthenticated>(g);
                Assert.Equal("assigned:-", await StateAsync(claim.Attempt));
                Assert.IsType<RegisterResult.Registered>(await processes.RegisterAsync(claim.Attempt, c.Credential.Hash(), new RegisterRequest(Build), default));
            }
            else
            {
                Assert.IsType<ClaimResult.InvalidState>(r); // register committed first: launching, the running credential kept
                Assert.IsType<RegisterResult.Registered>(g);
                Assert.Equal(claim.Attempt, (await processes.AuthenticateAsync(claim.Credential.Hash(), default))?.AttemptId);
            }
            Assert.Equal("launching:-", await StateAsync(claim.Attempt));
            await AttemptFixtures.MoveAsync(db, claim.Attempt, "cancelled", "h1");
            await hosts.HeartbeatAsync("h1", await AttemptFixtures.HostSecretHashAsync(db, "h1"), new HeartbeatRequest(Build, true, [], 0), default); // confirm the slot free
        }
    }

    [Fact]
    public async Task A_superseded_credential_cannot_register_even_after_it_authenticated()
    {
        var claim = await ClaimedAsync();
        var recovered = Assert.IsType<ClaimResult.Claimed>(await hosts.ClaimAsync("h1", await AttemptFixtures.HostSecretHashAsync(db, "h1"), claim.Key, 120, default));
        Assert.IsType<RegisterResult.Unauthenticated>(await processes.RegisterAsync(claim.Attempt, claim.Credential.Hash(), new RegisterRequest(Build), default));
        Assert.Equal("assigned:-", await StateAsync(claim.Attempt));
        Assert.IsType<RegisterResult.Registered>(await processes.RegisterAsync(claim.Attempt, recovered.Credential.Hash(), new RegisterRequest(Build), default));
    }

    // ---- ready ----

    [Fact]
    public async Task Ready_stores_objectives_pool_and_hash_once_and_sets_the_join_deadline()
    {
        var (claim, session) = await RegisteredAsync();
        var now = await ScalarAsSuperuserAsync<DateTime>(db, "select clock_timestamp()");
        var ready = Assert.IsType<ReadyResult.Ready>(await processes.ReadyAsync(claim.Attempt, Ready(session), catalogues, 120, default));
        Assert.InRange(ready.JoinDeadline, now.AddSeconds(110), now.AddSeconds(125));
        await using var su = await db.AsSuperuser().OpenConnectionAsync();
        Assert.Equal(("ready", "cf2e14dece9cc2a6", Table.Version, LootHash.Compute(Table.Version, Pool)), await su.QuerySingleAsync<(string, string, int, string)>(
            "select state, layout_hash, loot_table_version, loot_hash from mission_attempts where attempt_id = @a", new { a = claim.Attempt }));
        Assert.Equal(["extract:True:0", "intel.cache-1:False:1"], (await su.QueryAsync<string>(
            "select objective_id || ':' || required || ':' || ordinal from attempt_objectives where attempt_id = @a order by ordinal", new { a = claim.Attempt })).ToList()
            .Select(s => s.Replace("true", "True").Replace("false", "False")));
        Assert.Equal(3L, await su.ExecuteScalarAsync<long>("select count(*) from attempt_loot_pool where attempt_id = @a", new { a = claim.Attempt }));
        // A repeat with the identical body is a lost acknowledgement; any difference is refused and changes nothing.
        Assert.Equal(ready, await processes.ReadyAsync(claim.Attempt, Ready(session), catalogues, 120, default));
        Assert.IsType<ReadyResult.InvalidState>(await processes.ReadyAsync(claim.Attempt, Ready(session, layout: "0000000000000000"), catalogues, 120, default));
        Assert.IsType<ReadyResult.InvalidState>(await processes.ReadyAsync(claim.Attempt, Ready(session, objectives: [new("extract", true)]), catalogues, 120, default));
        Assert.Equal(3L, await su.ExecuteScalarAsync<long>("select count(*) from attempt_loot_pool where attempt_id = @a", new { a = claim.Attempt }));
    }

    public static TheoryData<string> InvalidPools => new() { "unknown-definition", "quantity", "container", "lines", "table-version", "wrong-hash" };

    [Theory]
    [MemberData(nameof(InvalidPools))]
    public async Task An_invalid_pool_cancels_and_releases(string fault)
    {
        var (claim, session) = await RegisteredAsync();
        var report = fault switch
        {
            "unknown-definition" => Ready(session, [new(0, 0, "item.rocket", 1)]),
            "quantity" => Ready(session, [new(0, 0, "item.medkit", 5)]),
            "container" => Ready(session, [new(3, 0, "item.medkit", 1)]),
            "lines" => Ready(session, [new(0, 1, "item.medkit", 1)]),
            "table-version" => Ready(session) with { LootTableVersion = Table.Version + 1, LootHash = LootHash.Compute(Table.Version + 1, Pool) },
            _ => Ready(session, lootHash: "sha256:" + new string('0', 64)),
        };
        var refused = Assert.IsType<ReadyResult.InvalidLootPool>(await processes.ReadyAsync(claim.Attempt, report, catalogues, 120, default));
        Assert.NotEmpty(refused.Errors);
        Assert.Equal("cancelled:invalid_pool", await StateAsync(claim.Attempt));
        Assert.Equal("released:invalid_pool", await ScalarAsSuperuserAsync<string>(db,
            "select state || ':' || release_reason from attempt_participants where attempt_id = @a", new { a = claim.Attempt }));
        Assert.Equal(0L, await ScalarAsSuperuserAsync<long>(db, "select count(*) from attempt_loot_pool"));
    }

    [Fact]
    public async Task Ready_after_the_launch_deadline_is_refused_and_the_worker_then_cancels()
    {
        var (claim, session) = await RegisteredAsync();
        await using (var su = await db.AsSuperuser().OpenConnectionAsync())
        await using (var tx = await su.BeginTransactionAsync())
        {
            await su.ExecuteAsync("set local session_replication_role = replica", transaction: tx);
            await su.ExecuteAsync("update mission_attempts set launch_deadline = clock_timestamp() - interval '1 second' where attempt_id = @a", new { a = claim.Attempt }, tx);
            await tx.CommitAsync();
        }
        Assert.IsType<ReadyResult.InvalidState>(await processes.ReadyAsync(claim.Attempt, Ready(session), catalogues, 120, default)); // Final M2
        Assert.Equal("launching:-", await StateAsync(claim.Attempt));
        Assert.Equal(1, (await attempts.SweepAsync(10)).Released);
        Assert.Equal("cancelled:launch_timeout", await StateAsync(claim.Attempt));
    }

    [Fact]
    public async Task Ready_under_a_genuinely_unavailable_catalogue_is_refused_without_cancelling()
    {
        // Final M4, rewritten for S9-B20.3 (ruling): ready judges the pool under the attempt's admission catalogue from the history,
        // so CatalogueMismatch remains only for a version the history lacks (here a schema-8 style attempt, its version unregistered).
        var (claim, session) = await RegisteredAsync();
        var missing = "sha256:" + new string('c', 64);
        await ExecuteAsSuperuserAsync(db, """
            insert into allowed_builds (build_revision, catalogue_version) values ('r1', @missing);
            insert into allowed_build_templates (build_revision, catalogue_version, template_id, template_version) values ('r1', @missing, 'procedural-standard', 1);
            begin; set local session_replication_role = replica; update mission_attempts set catalogue_version = @missing where attempt_id = @attempt; commit;
            """, new { missing, attempt = claim.Attempt });
        var refused = await processes.ReadyAsync(claim.Attempt, Ready(session, [new(0, 0, "item.rocket", 1)]), catalogues, 120, default);
        Assert.IsType<ReadyResult.CatalogueMismatch>(refused);
        Assert.Equal("launching:-", await StateAsync(claim.Attempt));
    }

    [Fact]
    public async Task A_failure_report_after_the_attempt_ended_is_refused_and_stores_nothing()
    {
        var (claim, session) = await ReadyAsync();
        await attempts.CancelAsync(claim.Account, claim.Attempt);
        Assert.Equal(new ExitReportResult.Ended(), await processes.FailedAsync(claim.Attempt, new FailedRequest(session, "late"), default)); // Final M1
        Assert.Equal(0L, await ScalarAsSuperuserAsync<long>(db, "select count(*) from attempt_exit_reports"));
    }

    [Fact]
    public async Task Invalid_objectives_write_nothing()
    {
        var (claim, session) = await RegisteredAsync();
        foreach (var objectives in new[] { Array.Empty<Objective>(), [new Objective("extract", false)], [new Objective("Extract!", true)] })
            Assert.IsType<ReadyResult.InvalidObjectives>(await processes.ReadyAsync(claim.Attempt, Ready(session, objectives: objectives), catalogues, 120, default));
        Assert.Equal("launching:-", await StateAsync(claim.Attempt));
    }

    [Fact]
    public async Task Every_process_call_with_another_session_is_WrongSession()
    {
        var (claim, _) = await ReadyAsync();
        var other = Guid.CreateVersion7();
        Assert.IsType<ReadyResult.WrongSession>(await processes.ReadyAsync(claim.Attempt, Ready(other), catalogues, 120, default));
        Assert.IsType<ReadyResult.WrongSession>(await processes.ReadyAsync(claim.Attempt, Ready(other, lootHash: "sha256:" + new string('0', 64)), catalogues, 120, default)); // never cancels
        Assert.IsType<ProcessHeartbeatResult.WrongSession>(await processes.HeartbeatAsync(claim.Attempt, new ProcessHeartbeatRequest(other, "running", []), default));
        Assert.Equal(new ExitReportResult.WrongSession(), await processes.FailedAsync(claim.Attempt, new FailedRequest(other, "x"), default));
        Assert.Equal("ready:-", await StateAsync(claim.Attempt));
    }

    // ---- heartbeat and lease ----

    [Fact]
    public async Task A_lapsed_process_lease_makes_running_unresponsive_and_a_heartbeat_restores_it()
    {
        var (claim, session) = await ReadyAsync();
        await AttemptFixtures.StartAsync(db, claim.Attempt, claim.Account);
        Assert.Equal(new ProcessHeartbeatResult.Ok(), await processes.HeartbeatAsync(claim.Attempt, new ProcessHeartbeatRequest(session, "running", []), default));
        Assert.Equal(0, (await attempts.SweepAsync(10)).Unresponsive);
        await ExecuteAsSuperuserAsync(db, "update mission_attempts set last_process_heartbeat_at = clock_timestamp() - interval '31 seconds' where attempt_id = @a", new { a = claim.Attempt });
        Assert.Equal(1, (await attempts.SweepAsync(10)).Unresponsive);
        Assert.Equal("unresponsive:-", await StateAsync(claim.Attempt));
        Assert.Equal(0, (await attempts.SweepAsync(10)).Unresponsive);
        Assert.Equal(new ProcessHeartbeatResult.Ok(), await processes.HeartbeatAsync(claim.Attempt, new ProcessHeartbeatRequest(session, "running", []), default));
        Assert.Equal("running:-", await StateAsync(claim.Attempt));
        Assert.Equal("joined:charged", await ScalarAsSuperuserAsync<string>(db,
            "select p.state || ':' || h.state from attempt_participants p join energy_holds h using (attempt_id, account_id) where p.attempt_id = @a", new { a = claim.Attempt }));
    }

    [Fact]
    public async Task Heartbeat_moves_started_participants_between_joined_and_left_and_never_marks_an_unstarted_one_joined()
    {
        var (claim, session) = await ReadyAsync();
        Task<string> ParticipantAsync() => ScalarAsSuperuserAsync<string>(db, "select state from attempt_participants where attempt_id = @a", new { a = claim.Attempt });
        Assert.Equal(new ProcessHeartbeatResult.Ok(), await processes.HeartbeatAsync(claim.Attempt, new ProcessHeartbeatRequest(session, "lobby", [new(claim.Account, true)]), default));
        Assert.Equal("admitted", await ParticipantAsync()); // B10: only the start commit makes a participant joined
        await AttemptFixtures.StartAsync(db, claim.Attempt, claim.Account);
        await processes.HeartbeatAsync(claim.Attempt, new ProcessHeartbeatRequest(session, "running", [new(claim.Account, false)]), default);
        Assert.Equal("left", await ParticipantAsync());
        await processes.HeartbeatAsync(claim.Attempt, new ProcessHeartbeatRequest(session, "running", [new(claim.Account, false)]), default);
        Assert.Equal("left", await ParticipantAsync());
        await processes.HeartbeatAsync(claim.Attempt, new ProcessHeartbeatRequest(session, "running", [new(claim.Account, true)]), default);
        Assert.Equal("joined", await ParticipantAsync());
    }

    [Fact]
    public async Task Heartbeat_with_a_foreign_participant_is_refused_and_writes_nothing()
    {
        var (claim, session) = await ReadyAsync();
        var stranger = await NewAccountAsync();
        Assert.IsType<ProcessHeartbeatResult.UnknownParticipant>(await processes.HeartbeatAsync(claim.Attempt,
            new ProcessHeartbeatRequest(session, "running", [new(claim.Account, true), new(stranger, true)]), default));
        Assert.Null(await ScalarAsSuperuserAsync<DateTime?>(db, "select last_process_heartbeat_at from mission_attempts where attempt_id = @a", new { a = claim.Attempt }));
    }

    // ---- HTTP: the process scheme and the routes ----

    [Fact]
    public async Task Process_routes_open_only_for_their_own_attempts_unended_credential()
    {
        var (claim, session) = await RegisteredAsync();
        var other = await ClaimedAsync();
        var hostSecret = (await new HostAdminStore(db.AsApp()).RotateSecretAsync("h1", Op)).Secret;
        await using var api = await ApiServer.StartAsync(db, new() { ["BLACKGLASS_DEV_AUTH"] = "enabled" });
        var issued = await new AccountAdminStore(db.AsApp()).CreateAccountAsync(null, null, Op);
        var login = await api.Http.PostAsJsonAsync("/v1/auth/dev/login", new { accountKey = issued.AccountKey.Text });
        var sessionToken = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.GetProperty("accessToken").GetString()!;

        async Task<HttpStatusCode> PostAsync(string path, string token, string body)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await api.Http.SendAsync(request);
            return response.StatusCode;
        }
        var heartbeat = $$"""{"processSessionId":"{{session}}","phase":"running","participants":[]}""";
        var agentBeat = $$"""{"buildRevision":"r1","catalogueVersion":"{{Catalogue}}","inventory":{"complete":false,"units":[]},"outboxPending":0}""";
        var route = $"/server/v1/attempts/{claim.Attempt}/heartbeat";
        Assert.Equal(HttpStatusCode.OK, await PostAsync(route, claim.Credential.Text, heartbeat));
        // Each credential opens only its own routes: a valid session token, host secret and another attempt's credential are 401.
        Assert.Equal(HttpStatusCode.Unauthorized, await PostAsync(route, sessionToken, heartbeat));
        Assert.Equal(HttpStatusCode.Unauthorized, await PostAsync(route, hostSecret.Text, heartbeat));
        Assert.Equal(HttpStatusCode.Unauthorized, await PostAsync(route, other.Credential.Text, heartbeat));
        Assert.Equal(HttpStatusCode.Unauthorized, await PostAsync($"/server/v1/attempts/{claim.Attempt.ToString().ToUpperInvariant()}/heartbeat", claim.Credential.Text, heartbeat));
        Assert.Equal(HttpStatusCode.Unauthorized, await PostAsync("/agent/v1/heartbeat", claim.Credential.Text, agentBeat));
        Assert.Equal(HttpStatusCode.Unauthorized, await PostAsync("/agent/v1/heartbeat", sessionToken, agentBeat));
        Assert.Equal(HttpStatusCode.OK, await PostAsync("/agent/v1/heartbeat", hostSecret.Text, agentBeat));
        using (var client = new HttpRequestMessage(HttpMethod.Get, $"/v1/attempts/{claim.Attempt}"))
        {
            client.Headers.Authorization = new AuthenticationHeaderValue("Bearer", claim.Credential.Text);
            Assert.Equal(HttpStatusCode.Unauthorized, (await api.Http.SendAsync(client)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Conflict, await PostAsync(route, claim.Credential.Text, heartbeat.Replace(session.ToString(), Guid.CreateVersion7().ToString())));

        // R4: once the attempt is cancelled, every process route is the uniform 401, the heartbeat included.
        await AttemptFixtures.MoveAsync(db, claim.Attempt, "cancelled", "h1");
        Assert.Equal(HttpStatusCode.Unauthorized, await PostAsync(route, claim.Credential.Text, heartbeat));
        Assert.Equal(HttpStatusCode.Unauthorized, await PostAsync($"/server/v1/attempts/{claim.Attempt}/sessions", claim.Credential.Text, $$"""{"buildRevision":"r1","catalogueVersion":"{{Catalogue}}"}"""));
        Assert.Equal(HttpStatusCode.Unauthorized, await PostAsync($"/server/v1/attempts/{claim.Attempt}/failed", claim.Credential.Text, $$"""{"processSessionId":"{{session}}","reason":"x"}"""));
    }

    [Fact]
    public async Task Register_and_ready_over_http()
    {
        var claim = await ClaimedAsync();
        await using var api = await ApiServer.StartAsync(db);
        async Task<(HttpStatusCode Status, string Body)> PostAsync(string path, string body)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", claim.Credential.Text);
            using var response = await api.Http.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }
        var (status, body) = await PostAsync($"/server/v1/attempts/{claim.Attempt}/sessions", $$"""{"buildRevision":"r1","catalogueVersion":"{{Catalogue}}"}""");
        Assert.Equal(HttpStatusCode.OK, status);
        var registered = JsonDocument.Parse(body).RootElement;
        Assert.Equal(["processSessionId", "seed", "missionTemplate", "catalogueVersion", "participants", "deadlines"], registered.EnumerateObject().Select(p => p.Name));
        Assert.Equal(Blackglass.Accounts.Domain.Ids.Base58Id.Format(claim.Account), registered.GetProperty("participants")[0].GetProperty("accountId").GetString());
        Assert.Equal(JsonValueKind.Object, registered.GetProperty("participants")[0].GetProperty("loadout").ValueKind);
        var session = registered.GetProperty("processSessionId").GetString()!;
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync($"/server/v1/attempts/{claim.Attempt}/sessions", """{"buildRevision":"r2","catalogueVersion":"sha256:abababababababababababababababababababababababababababababababab"}""")).Status);

        var ready = JsonSerializer.Serialize(new
        {
            processSessionId = session, layoutHash = "cf2e14dece9cc2a6", lootTableVersion = Table.Version, lootHash = LootHash.Compute(Table.Version, Pool),
            objectives = Objectives.Select(o => new { objectiveId = o.ObjectiveId, required = o.Required }),
            lootPool = Pool.Select(l => new { containerIndex = l.ContainerIndex, line = l.Line, definitionId = l.DefinitionId, quantity = l.Quantity }),
        });
        // Since S9-B20.3 the pool is judged under the attempt's admission catalogue from the history (here the API's own); the
        // genuinely-unavailable case is Ready_under_a_genuinely_unavailable_catalogue_is_refused_without_cancelling.
        var readied = await PostAsync($"/server/v1/attempts/{claim.Attempt}/ready", ready);
        Assert.Equal(HttpStatusCode.OK, readied.Status);
        Assert.Contains("\"joinDeadline\"", readied.Body);
        var hb = await PostAsync($"/server/v1/attempts/{claim.Attempt}/heartbeat", $$"""{"processSessionId":"{{session}}","phase":"lobby","participants":[]}""");
        Assert.Equal((HttpStatusCode.OK, "{}"), hb);
    }
}
