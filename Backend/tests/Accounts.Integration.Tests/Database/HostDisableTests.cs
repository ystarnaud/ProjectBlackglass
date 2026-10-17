using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using Blackglass.Accounts.Api;
using Blackglass.Accounts.Api.Admin;
using Blackglass.Accounts.Domain.Attempts;
using Blackglass.Accounts.Domain.Auth;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Attempts;
using Blackglass.Accounts.Persistence.Hosts;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>
/// Drain versus disable (owner decision of 2026-10-17, plan revision 2 R8). Drain stops new claims only: the agent and its
/// processes keep working. Disable is emergency isolation: in one audited transaction the host's secret is replaced by an
/// unknown one and every process credential of its attempts is revoked for good; disabled-host processes can no longer
/// register, report ready, heartbeat, redeem tickets or start participants, and no usable ticket is minted for its attempts.
/// Nothing is cancelled, released, voided or refunded, and no slot is freed. Re-enabling needs a rotated secret and never
/// restores a revoked credential.
/// </summary>
[Trait("Requires", "Postgres")]
public class HostDisableTests : IAsyncLifetime
{
    TestDatabase db = null!;
    MissionKit kit = null!;
    HostAdminStore admin = null!;
    StartStore starts = null!;

    public async Task InitializeAsync()
    {
        db = await MigratedAsync();
        kit = await MissionKit.CreateAsync(db);
        admin = new HostAdminStore(db.AsApp());
        starts = new StartStore(db.AsApp());
    }

    public async Task DisposeAsync()
    {
        try { Assert.Empty(await InvariantFailuresAsync(db)); }
        finally { await db.DisposeAsync(); }
    }

    Task<string> StateAsync(Guid attempt) => ScalarAsSuperuserAsync<string>(db, """
        select a.state || ':' || p.state || ':' || h.state || ':' || (select count(*) from energy_ledger l where l.attempt_id = a.attempt_id and l.kind = 'charge')
               || ':' || (select count(*) from energy_ledger l where l.attempt_id = a.attempt_id and l.kind = 'release') || ':' || coalesce(a.slot_released_at::text, 'live')
        from mission_attempts a join attempt_participants p using (attempt_id) join energy_holds h using (attempt_id, account_id) where a.attempt_id = @attempt
        """, new { attempt });

    static async Task<(HttpStatusCode Status, string Body, string? Challenge)> PostAsync(ApiServer api, string path, string token, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await api.Http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(), response.Headers.WwwAuthenticate.ToString());
    }

    static string Beat => $$"""{"buildRevision":"r1","catalogueVersion":"{{MissionKit.Build.CatalogueVersion}}","inventory":{"complete":false,"units":[]},"outboxPending":0}""";

    static string ProcessBeat(Guid session) => $$"""{"processSessionId":"{{session}}","phase":"running","participants":[]}""";

    async Task<MissionKit.Mission> RunningAsync()
    {
        var m = await kit.ReadyAsync();
        await kit.RedeemAsync(m);
        Assert.IsType<StartResult.Started>(await starts.StartAsync(m.Attempt, m.Account, m.Session, default));
        return m;
    }

    // ---- drain ----

    [Fact]
    public async Task Drain_blocks_claims_but_preserves_existing_agent_and_process_operations()
    {
        var secret = (await admin.RotateSecretAsync("h1", Op)).Secret.Text;
        var ready = await kit.ReadyAsync();
        await kit.AdmitAsync(); // a waiting attempt the drained host must not take
        await admin.SwitchAsync("h1", HostSwitch.Drain, Op);
        Assert.IsType<ClaimResult.HostUnavailable>(await kit.Hosts.ClaimAsync("h1", Guid.NewGuid(), 120, default));
        await using var api = await ApiServer.StartAsync(db);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(api, "/agent/v1/heartbeat", secret, Beat)).Status);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(api, $"/server/v1/attempts/{ready.Attempt}/heartbeat", ready.Credential.Text, ProcessBeat(ready.Session))).Status);
        await kit.RedeemAsync(ready);
        Assert.IsType<StartResult.Started>(await starts.StartAsync(ready.Attempt, ready.Account, ready.Session, default));
        Assert.Equal(new ExitReportResult.Applied("none"), await kit.Hosts.ExitedAsync("h1", ready.Attempt, new ExitedRequest(0, null, []), default));
    }

    // ---- disable ----

    [Fact]
    public async Task Disable_revokes_the_host_secret_and_every_process_credential_of_its_attempts_and_preserves_all_records()
    {
        var secret = (await admin.RotateSecretAsync("h1", Op)).Secret.Text;
        var running = await RunningAsync();
        var ready = await kit.ReadyAsync();
        const string Records = """
            select string_agg(x, ' | ' order by x) from (
              select 'a:' || attempt_id || ':' || state || ':' || coalesce(host_id, '') || ':' || coalesce(mission_port, 0) as x from mission_attempts
              union all select 'p:' || attempt_id || ':' || account_id || ':' || state from attempt_participants
              union all select 'h:' || attempt_id || ':' || account_id || ':' || state || ':' || amount from energy_holds
              union all select 'l:' || entry_id || ':' || kind || ':' || delta || ':' || balance_after from energy_ledger
              union all select 'i:' || attempt_id || ':' || account_id || ':' || policy_version from attempt_insurance
              union all select 'w:' || account_id || ':' || balance from energy_wallets
              union all select 'v:' || account_id || ':' || version from inventories) r
            """;
        var before = await ScalarAsSuperuserAsync<string>(db, Records);
        var disabled = await admin.DisableAsync("h1", Op);
        Assert.Equal((false, 2), (disabled.Host.Enabled, disabled.RevokedCredentials));
        Assert.Equal(before, await ScalarAsSuperuserAsync<string>(db, Records)); // nothing cancelled, released, refunded or moved
        Assert.Equal("running:joined:charged:1:0:live", await StateAsync(running.Attempt));
        Assert.Equal(1L, await ScalarAsSuperuserAsync<long>(db, "select count(*) from attempt_participants where state = 'joined'")); // the inventory stays locked

        await using var api = await ApiServer.StartAsync(db);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(api, "/agent/v1/heartbeat", secret, Beat)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(api, $"/server/v1/attempts/{running.Attempt}/heartbeat", running.Credential.Text, ProcessBeat(running.Session))).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(api, $"/server/v1/attempts/{ready.Attempt}/heartbeat", ready.Credential.Text, ProcessBeat(ready.Session))).Status);
        Assert.Null(await kit.Hosts.AuthenticateAsync(OpaqueSecretHash(secret), default));
        Assert.Null(await kit.Processes.AuthenticateAsync(running.Credential.Hash(), default));
        Assert.Equal("disable", await ScalarAsSuperuserAsync<string>(db, "select result ->> 'operation' from operator_actions where action = 'host_disable'"));
    }

    static byte[] OpaqueSecretHash(string text) => OpaqueSecret.TryParse(text, OpaqueSecret.HostSecretPrefix, out var s) ? s!.Hash() : [];

    [Fact]
    public async Task Disabled_host_processes_can_no_longer_act_even_past_authentication()
    {
        // The stores re-check under the attempt lock, so a request that authenticated before the disable committed is refused
        // once it commits (no unauthorized write after the disable).
        var ready = await kit.ReadyAsync();
        var minted = Assert.IsType<MintResult.Minted>(await kit.Tickets.MintAsync(ready.Account, ready.Attempt, 120, default));
        var registered = await kit.ReadyAsync();
        var claim = (ClaimResult.Claimed)await AdmitAndClaimAsync();
        await admin.DisableAsync("h1", Op);
        Assert.IsType<RedeemResult.Unknown>(await kit.Tickets.RedeemAsync(ready.Attempt, ready.Session, minted.Ticket.Text, default));
        Assert.IsType<MintResult.InvalidState>(await kit.Tickets.MintAsync(ready.Account, ready.Attempt, 120, default));
        Assert.IsType<StartResult.Revoked>(await starts.StartAsync(ready.Attempt, ready.Account, ready.Session, default));
        Assert.IsType<ProcessHeartbeatResult.Revoked>(await kit.Processes.HeartbeatAsync(ready.Attempt, new ProcessHeartbeatRequest(ready.Session, "running", [new(ready.Account, true)]), default));
        Assert.IsType<RegisterResult.Unauthenticated>(await kit.Processes.RegisterAsync(claim.AttemptId, claim.Credential.Hash(), new RegisterRequest(MissionKit.Build), default));
        Assert.Equal(new ExitReportResult.Ended(), await kit.Processes.FailedAsync(registered.Attempt, new FailedRequest(registered.Session, "late"), default));
        Assert.Equal("ready:admitted:reserved:0:0:live", await StateAsync(ready.Attempt));
        Assert.Equal("ready:admitted:reserved:0:0:live", await StateAsync(registered.Attempt));
        Assert.Equal(0L, await ScalarAsSuperuserAsync<long>(db, "select count(*) from attempt_exit_reports"));
        Assert.Null(await ScalarAsSuperuserAsync<DateTime?>(db, "select redeemed_at from join_tickets"));
    }

    async Task<ClaimResult> AdmitAndClaimAsync()
    {
        await kit.AdmitAsync();
        await kit.BeatAsync();
        return await kit.Hosts.ClaimAsync("h1", Guid.NewGuid(), 120, default);
    }

    [Fact]
    public async Task Re_enabling_requires_rotation_and_never_restores_revoked_credentials()
    {
        var old = (await admin.RotateSecretAsync("h1", Op)).Secret.Text;
        var running = await RunningAsync();
        var assignedKey = Guid.NewGuid();
        await kit.AdmitAsync();
        await kit.BeatAsync();
        var assigned = Assert.IsType<ClaimResult.Claimed>(await kit.Hosts.ClaimAsync("h1", assignedKey, 120, default));
        await admin.DisableAsync("h1", Op);
        Assert.Equal(MissionAdminError.SecretRevoked, (await Assert.ThrowsAsync<MissionAdminException>(() => admin.SwitchAsync("h1", HostSwitch.Enable, Op))).Error);
        var fresh = (await admin.RotateSecretAsync("h1", Op)).Secret.Text;
        Assert.False(await ScalarAsSuperuserAsync<bool>(db, "select enabled from mission_hosts where host_id = 'h1'")); // rotation does not enable
        await admin.SwitchAsync("h1", HostSwitch.Enable, Op);

        Assert.Null(await kit.Hosts.AuthenticateAsync(OpaqueSecretHash(old), default));
        Assert.NotNull(await kit.Hosts.AuthenticateAsync(OpaqueSecretHash(fresh), default));
        Assert.Null(await kit.Processes.AuthenticateAsync(running.Credential.Hash(), default)); // never restored
        Assert.Null(await kit.Processes.AuthenticateAsync(assigned.Credential.Hash(), default));
        // A recovery of the revoked, still-assigned attempt issues nothing; launching it is refused.
        Assert.IsType<ClaimResult.InvalidState>(await kit.Hosts.ClaimAsync("h1", assignedKey, 120, default));
        Assert.IsType<LaunchingResult.InvalidState>(await kit.Hosts.LaunchingAsync("h1", assigned.AttemptId, 1, default));
        // The reauthorized host returns to service: a new attempt, a new credential that works.
        await kit.BeatAsync();
        var next = await kit.ReadyAsync();
        Assert.Equal(next.Attempt, (await kit.Processes.AuthenticateAsync(next.Credential.Hash(), default))?.AttemptId);
        await kit.RedeemAsync(next);
        Assert.IsType<StartResult.Started>(await starts.StartAsync(next.Attempt, next.Account, next.Session, default));
        // The old running attempt is untouched: still running, charged once, its slot held.
        Assert.Equal("running:joined:charged:1:0:live", await StateAsync(running.Attempt));
    }

    [Fact]
    public async Task Unknown_foreign_disabled_and_rotated_host_credentials_are_indistinguishable()
    {
        var old = (await admin.RotateSecretAsync("h1", Op)).Secret.Text;
        await admin.DisableAsync("h1", Op);
        await using var api = await ApiServer.StartAsync(db);
        var answers = new List<(HttpStatusCode, string, string?)>();
        foreach (var token in new[] { old, OpaqueSecret.New(OpaqueSecret.HostSecretPrefix).Text, OpaqueSecret.New(OpaqueSecret.ProcessCredentialPrefix).Text })
            answers.Add(await PostAsync(api, "/agent/v1/heartbeat", token, Beat));
        Assert.All(answers, a => Assert.Equal((HttpStatusCode.Unauthorized, "", "Bearer"), a));
    }

    [Fact]
    public async Task A_failed_disable_leaves_no_partially_revoked_state()
    {
        var running = await RunningAsync();
        const string Host = "select row(enabled, secret_hash, secret_revoked)::text from mission_hosts where host_id = 'h1'";
        var before = await ScalarAsSuperuserAsync<string>(db, Host);
        await ExecuteAsSuperuserAsync(db,
            "create function bg_test_fail() returns trigger language plpgsql as $$ begin raise exception 'injected' using errcode = 'P0001'; end $$; " +
            "create trigger bg_test_fail before update on mission_attempts for each row when (new.credential_revoked_at is not null) execute function bg_test_fail();");
        try { Assert.Equal("P0001", (await Assert.ThrowsAsync<PostgresException>(() => admin.DisableAsync("h1", Op))).SqlState); }
        finally { await ExecuteAsSuperuserAsync(db, "drop trigger bg_test_fail on mission_attempts; drop function bg_test_fail();"); }
        Assert.Equal(before, await ScalarAsSuperuserAsync<string>(db, Host));
        Assert.Equal(0L, await ScalarAsSuperuserAsync<long>(db, "select count(*) from mission_attempts where credential_revoked_at is not null"));
        Assert.Equal(0L, await ScalarAsSuperuserAsync<long>(db, "select count(*) from operator_actions where action = 'host_disable'"));
        Assert.Equal(running.Attempt, (await kit.Processes.AuthenticateAsync(running.Credential.Hash(), default))?.AttemptId);
    }

    [Fact]
    public async Task Disable_racing_claim_register_and_start_yields_serializable_outcomes()
    {
        for (var round = 0; round < 4; round++) // live slots accumulate (nothing is cancelled): 3 per round of 16
        {
            // An attempt the previous round's racing claim never took is withdrawn by its player, so this round's claims are exact.
            await using (var su = await db.AsSuperuser().OpenConnectionAsync())
                foreach (var (attempt, account) in await su.QueryAsync<(Guid, long)>("select attempt_id, created_by from mission_attempts where state = 'admitted'"))
                    await new AttemptStore(db.AsApp()).CancelAsync(account, attempt);
            var secret = (await admin.RotateSecretAsync("h1", Op)).Secret;
            await admin.SwitchAsync("h1", HostSwitch.Enable, Op);
            await kit.BeatAsync();
            var toStart = await kit.ReadyAsync();
            await kit.RedeemAsync(toStart);
            await kit.AdmitAsync();
            await kit.BeatAsync();
            var toRegister = Assert.IsType<ClaimResult.Claimed>(await kit.Hosts.ClaimAsync("h1", Guid.NewGuid(), 120, default));
            await kit.AdmitAsync(); // for the racing claim

            var gate = new TaskCompletionSource();
            var disable = Task.Run(async () => { await gate.Task; return await admin.DisableAsync("h1", Op); });
            var start = Task.Run(async () => { await gate.Task; return await starts.StartAsync(toStart.Attempt, toStart.Account, toStart.Session, default); });
            var register = Task.Run(async () => { await gate.Task; return await kit.Processes.RegisterAsync(toRegister.AttemptId, toRegister.Credential.Hash(), new RegisterRequest(MissionKit.Build), default); });
            var claim = Task.Run(async () => { await gate.Task; return await kit.Hosts.ClaimAsync("h1", Guid.NewGuid(), 120, default); });
            gate.SetResult();
            await Task.WhenAll(disable, start, register, claim);
            var (s, r, c) = (await start, await register, await claim);

            // Each racer either committed before the disable (and its credential is then revoked by it) or was refused.
            Assert.True(s is StartResult.Started or StartResult.Revoked, s.GetType().Name);
            Assert.True(r is RegisterResult.Registered or RegisterResult.Unauthenticated, r.GetType().Name);
            Assert.True(c is ClaimResult.Claimed or ClaimResult.Unauthenticated or ClaimResult.HostUnavailable, c.GetType().Name);
            Assert.True((await StateAsync(toStart.Attempt)) is "running:joined:charged:1:0:live" or "ready:admitted:reserved:0:0:live");
            // After the disable committed: every credential of the host is revoked, whoever won, and nothing more is allowed.
            Assert.Equal(0L, await ScalarAsSuperuserAsync<long>(db,
                "select count(*) from mission_attempts where host_id = 'h1' and state not in ('cancelled', 'settled') and credential_revoked_at is null"));
            Assert.IsType<StartResult.Revoked>(await starts.StartAsync(toStart.Attempt, toStart.Account, toStart.Session, default));
            Assert.Null(await kit.Hosts.AuthenticateAsync(secret.Hash(), default));
            Assert.Equal(0L, await ScalarAsSuperuserAsync<long>(db,
                "select count(*) from (select attempt_id from energy_ledger where kind = 'charge' group by attempt_id, account_id having count(*) > 1) d"));
        }
    }

    [Fact]
    public async Task The_disable_verb_is_confirmed_audited_and_prints_no_secret()
    {
        var secret = (await admin.RotateSecretAsync("h1", Op)).Secret;
        var running = await RunningAsync();
        var (output, error) = (new StringWriter(), new StringWriter());
        var code = await AdminCommand.RunAsync(["host", "disable", "--host-id", "h1", "--operator", "ops", "--reason", "suspected compromise", "--yes"],
            db.EnvFor("app"), output, error, default, loadCatalogue: () => throw new InvalidOperationException("no catalogue"));
        Assert.Equal(ExitCodes.Ok, code);
        Assert.Contains("revokedCredentials=1", output.ToString());
        var enable = new StringWriter();
        Assert.Equal(ExitCodes.Conflict, await AdminCommand.RunAsync(["host", "enable", "--host-id", "h1", "--operator", "ops", "--reason", "back", "--yes"],
            db.EnvFor("app"), TextWriter.Null, enable, default, loadCatalogue: () => throw new InvalidOperationException("no catalogue")));
        Assert.Contains("rotate-secret", enable.ToString());
        var audit = await ScalarAsSuperuserAsync<string>(db, "select string_agg(result::text, ' ') from operator_actions");
        foreach (var text in new[] { output.ToString(), error.ToString(), enable.ToString(), audit })
        {
            Assert.DoesNotContain(secret.Text, text);
            Assert.DoesNotContain(running.Credential.Text, text);
            Assert.DoesNotContain(Convert.ToHexString(secret.Hash()), text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Convert.ToHexString(running.Credential.Hash()), text, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains("suspected compromise", await ScalarAsSuperuserAsync<string>(db, "select reason from operator_actions where action = 'host_disable'"));
    }
}
