using System.Text.Json;
using Dapper;
using Npgsql;
using Blackglass.Accounts.Domain.Attempts;
using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.Domain.Hosts;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence;
using Blackglass.Accounts.Persistence.Accounts;
using Blackglass.Accounts.Persistence.Attempts;
using Blackglass.Accounts.Persistence.Energy;
using Blackglass.Accounts.Persistence.Hosts;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>
/// Per-participant release before start (Stage 8 Task 6; spec §7.2, §7.6; owner rulings B6, B11 revised, plan revision 2 R6,
/// R7): the player cancel in every pre-start attempt state, the assignment, launch and join deadlines, and the exit and
/// failure reports, each releasing every named participant still admitted exactly once (participant, hold, energy and ledger
/// independently per participant), cancelling the attempt only when no participant remains, never after a start. Exit and
/// failure reports are stored as evidence (idempotent; a conflicting one refused). The deadline worker cools down an item
/// that keeps failing so the rest of its batch proceeds.
/// </summary>
[Trait("Requires", "Postgres")]
public class ReleaseTests : IAsyncLifetime
{
    TestDatabase db = null!;
    AccountAdminStore accounts = null!;
    AdmissionStore admissions = null!;
    AttemptStore attempts = null!;
    HostStore hosts = null!;
    ProcessStore processes = null!;

    public async Task InitializeAsync()
    {
        db = await MigratedAsync();
        accounts = new AccountAdminStore(db.AsApp());
        var dev = new DevHosts(db.AsApp());
        await dev.RegisterAsync("dev-fixture-1", 10, Op);
        await dev.HeartbeatAsync("dev-fixture-1", Op);
        admissions = new AdmissionStore(db.AsApp(), new ServiceHealth(devHostsEnabled: true, leaseSeconds: 600), new CryptoAttemptSecrets(), GameCatalogue.Embedded, 60);
        attempts = new AttemptStore(db.AsApp());
        hosts = new HostStore(db.AsApp(), leaseSeconds: 30);
        processes = new ProcessStore(db.AsApp());
    }

    public async Task DisposeAsync()
    {
        try { Assert.Empty(await InvariantFailuresAsync(db)); }
        finally { await db.DisposeAsync(); }
    }

    // ---- helpers ----

    async Task<(long Account, Guid Attempt)> AdmittedAsync()
    {
        var account = (await accounts.CreateAccountAsync(null, null, Op)).AccountId;
        var operative = await ScalarAsSuperuserAsync<long>(db, "select min(operative_id) from operatives where account_id = @account", new { account });
        var admission = await admissions.AdmitAsync(account, Guid.CreateVersion7(), new DeployRequest("procedural-standard", [operative]));
        return (account, Guid.Parse(JsonDocument.Parse(admission.Body).RootElement.GetProperty("attemptId").GetString()!));
    }

    /// <summary>Adds a second participant to an attempt as co-op admission would (direct SQL, as the superuser): its own
    /// snapshot, a hold of 10 and its own reservation from its own wallet.</summary>
    async Task<long> AddParticipantAsync(Guid attempt)
    {
        var account = (await accounts.CreateAccountAsync(null, null, Op)).AccountId;
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync("""
            insert into attempt_participants (attempt_id, account_id, template_id, template_version, loadout_snapshot, inventory_version, energy_cost)
            select @attempt, @account, template_id, template_version, '{"operatives":[]}', 1, 10 from mission_attempts where attempt_id = @attempt;
            insert into energy_holds (attempt_id, account_id, amount) values (@attempt, @account, 10);
            update energy_wallets set balance = balance - 10 where account_id = @account;
            insert into energy_ledger (account_id, attempt_id, kind, delta, balance_after, idempotency_key)
            select @account, @attempt, 'reserve', -10, balance, 'reserve:' || @attempt::text from energy_wallets where account_id = @account;
            """, new { attempt, account }, tx);
        await tx.CommitAsync();
        return account;
    }

    Task StartAsync(Guid attempt, long account) => AttemptFixtures.StartAsync(db, attempt, account);

    async Task MoveAsync(Guid attempt, params string[] states)
    {
        foreach (var s in states) await AttemptFixtures.MoveAsync(db, attempt, s);
    }

    Task<string> AttemptStateAsync(Guid attempt) => ScalarAsSuperuserAsync<string>(db,
        "select state || ':' || coalesce(cancel_reason, '-') from mission_attempts where attempt_id = @attempt", new { attempt });

    Task<string> ParticipantAsync(Guid attempt, long account) => ScalarAsSuperuserAsync<string>(db,
        "select p.state || ':' || coalesce(p.release_reason, '-') || ':' || h.state from attempt_participants p join energy_holds h using (attempt_id, account_id) where p.attempt_id = @attempt and p.account_id = @account",
        new { attempt, account });

    Task<long> EntriesAsync(Guid attempt, long account, string kind) => ScalarAsSuperuserAsync<long>(db,
        "select count(*) from energy_ledger where attempt_id = @attempt and account_id = @account and kind = @kind", new { attempt, account, kind });

    Task<int> BalanceAsync(long account) => ScalarAsSuperuserAsync<int>(db, "select balance from energy_wallets where account_id = @account", new { account });

    async Task ExpireAsync(Guid attempt, string column)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync("set local session_replication_role = replica", transaction: tx);
        await conn.ExecuteAsync($"update mission_attempts set {column} = clock_timestamp() - interval '1 second' where attempt_id = @attempt", new { attempt }, tx);
        await tx.CommitAsync();
    }

    // ---- player cancel ----

    [Theory]
    [InlineData("admitted")]
    [InlineData("assigned")]
    [InlineData("launching")]
    [InlineData("ready")]
    public async Task Cancel_before_start_releases_that_participant_once_in_every_pre_start_state(string state)
    {
        var (account, attempt) = await AdmittedAsync();
        var states = new[] { "assigned", "launching", "ready" };
        await MoveAsync(attempt, states.Take(Array.IndexOf(states, state) + 1).ToArray());
        Assert.Equal(40, await BalanceAsync(account));
        var outcome = await attempts.CancelAsync(account, attempt);
        Assert.Equal(new ReleaseOutcome("released", 10, 50, CancelReasons.PlayerCancel, Replayed: false), outcome);
        Assert.Equal("cancelled:player_cancel", await AttemptStateAsync(attempt));
        Assert.Equal("released:player_cancel:released", await ParticipantAsync(attempt, account));
        Assert.Equal(1L, await EntriesAsync(attempt, account, "release"));
        Assert.Equal(outcome with { Replayed = true }, await attempts.CancelAsync(account, attempt));
        Assert.Equal(1L, await EntriesAsync(attempt, account, "release"));
        // B6: the slot is not freed by the cancel; the agent confirms the unit gone.
        Assert.Null(await ScalarAsSuperuserAsync<DateTime?>(db, "select slot_released_at from mission_attempts where attempt_id = @attempt", new { attempt }));
    }

    [Fact]
    public async Task Cancel_after_this_participants_start_is_refused_and_writes_nothing()
    {
        var (account, attempt) = await AdmittedAsync();
        await MoveAsync(attempt, "assigned", "launching", "ready");
        await StartAsync(attempt, account);
        var before = await ScalarAsSuperuserAsync<string>(db, "select string_agg(xmin::text, ',') from (select xmin from mission_attempts union all select xmin from attempt_participants union all select xmin from energy_holds) x");
        Assert.Equal(AttemptError.InvalidState, (await Assert.ThrowsAsync<AttemptException>(() => attempts.CancelAsync(account, attempt))).Error);
        Assert.Equal(before, await ScalarAsSuperuserAsync<string>(db, "select string_agg(xmin::text, ',') from (select xmin from mission_attempts union all select xmin from attempt_participants union all select xmin from energy_holds) x"));
        Assert.Equal(0L, await EntriesAsync(attempt, account, "release"));
    }

    [Fact]
    public async Task The_attempt_is_cancelled_only_when_no_participant_remains()
    {
        var (first, attempt) = await AdmittedAsync();
        var second = await AddParticipantAsync(attempt);
        await MoveAsync(attempt, "assigned");
        await attempts.CancelAsync(first, attempt);
        Assert.Equal(("assigned:-", "released:player_cancel:released", "admitted:-:reserved"),
            (await AttemptStateAsync(attempt), await ParticipantAsync(attempt, first), await ParticipantAsync(attempt, second)));
        await attempts.CancelAsync(second, attempt);
        Assert.Equal("cancelled:player_cancel", await AttemptStateAsync(attempt));
        Assert.Equal((50, 50), (await BalanceAsync(first), await BalanceAsync(second)));
        Assert.Equal((1L, 1L), (await EntriesAsync(attempt, first, "release"), await EntriesAsync(attempt, second, "release")));
    }

    [Fact]
    public async Task Two_participants_release_independently()
    {
        // Co-op shaped (direct SQL): one participant starts, the other never does; the join deadline releases only the second,
        // the attempt keeps running, and each hold and ledger moves on its own.
        var (starter, attempt) = await AdmittedAsync();
        var idle = await AddParticipantAsync(attempt);
        await MoveAsync(attempt, "assigned", "launching", "ready");
        await StartAsync(attempt, starter);
        await ExpireAsync(attempt, "join_deadline");
        var report = await attempts.SweepAsync(10);
        Assert.Equal(1, report.Released);
        Assert.Equal("running:-", await AttemptStateAsync(attempt));
        Assert.Equal(("joined:-:charged", "released:join_timeout:released"), (await ParticipantAsync(attempt, starter), await ParticipantAsync(attempt, idle)));
        Assert.Equal((1L, 0L, 0L, 1L), (await EntriesAsync(attempt, starter, "charge"), await EntriesAsync(attempt, starter, "release"),
            await EntriesAsync(attempt, idle, "charge"), await EntriesAsync(attempt, idle, "release")));
        Assert.Equal((40, 50), (await BalanceAsync(starter), await BalanceAsync(idle)));
        Assert.Equal(new SweepReport(0, 0, 0, 0, []), await attempts.SweepAsync(10), SweepComparer.Instance); // nothing left to release
    }

    // ---- deadlines ----

    [Fact]
    public async Task Launch_deadline_cancels_assigned_and_launching_attempts_and_keeps_the_slot_until_exited()
    {
        var (a1, assigned) = await AdmittedAsync();
        var (a2, launching) = await AdmittedAsync();
        await MoveAsync(assigned, "assigned");
        await MoveAsync(launching, "assigned", "launching");
        await ExpireAsync(assigned, "launch_deadline");
        await ExpireAsync(launching, "launch_deadline");
        Assert.Equal(2, (await attempts.SweepAsync(10)).Released);
        Assert.Equal(("cancelled:launch_timeout", "cancelled:launch_timeout"), (await AttemptStateAsync(assigned), await AttemptStateAsync(launching)));
        Assert.Equal(("released:launch_timeout:released", "released:launch_timeout:released"), (await ParticipantAsync(assigned, a1), await ParticipantAsync(launching, a2)));
        Assert.Equal(2L, await ScalarAsSuperuserAsync<long>(db, "select count(*) from mission_attempts where slot_released_at is null and host_id is not null"));
    }

    [Fact]
    public async Task Join_deadline_releases_unstarted_participants_and_cancels_only_if_none_started()
    {
        var (account, attempt) = await AdmittedAsync();
        await MoveAsync(attempt, "assigned", "launching", "ready");
        await ExpireAsync(attempt, "join_deadline");
        Assert.Equal(1, (await attempts.SweepAsync(10)).Released);
        Assert.Equal(("cancelled:join_timeout", "released:join_timeout:released"), (await AttemptStateAsync(attempt), await ParticipantAsync(attempt, account)));
    }

    [Fact]
    public async Task The_worker_never_touches_a_running_or_launching_attempt_before_its_deadline_or_after_start()
    {
        var (account, running) = await AdmittedAsync();
        await MoveAsync(running, "assigned", "launching", "ready");
        await StartAsync(running, account);
        await ExpireAsync(running, "launch_deadline"); // a running attempt past its launch deadline: never the launch release
        await ExpireAsync(running, "join_deadline");   // nor the join release, with no unstarted participant left
        var (_, fresh) = await AdmittedAsync();
        await MoveAsync(fresh, "assigned", "launching");
        var before = await ScalarAsSuperuserAsync<string>(db, "select string_agg(attempt_id || state, ',' order by attempt_id) from mission_attempts");
        Assert.Equal(new SweepReport(0, 0, 0, 0, []), await attempts.SweepAsync(10), SweepComparer.Instance);
        Assert.Equal(before, await ScalarAsSuperuserAsync<string>(db, "select string_agg(attempt_id || state, ',' order by attempt_id) from mission_attempts"));
    }

    [Theory]
    [InlineData("assign")]
    [InlineData("launch")]
    [InlineData("join")]
    public async Task A_cancel_and_every_deadline_race_and_release_once(string deadline)
    {
        for (var round = 0; round < 8; round++)
        {
            var (account, attempt) = await AdmittedAsync();
            if (deadline == "launch") await MoveAsync(attempt, "assigned");
            if (deadline == "join") await MoveAsync(attempt, "assigned", "launching", "ready");
            await ExpireAsync(attempt, deadline + "_deadline");
            var start = new TaskCompletionSource();
            var cancel = Task.Run(async () => { await start.Task; return await attempts.CancelAsync(account, attempt); });
            var sweep = Task.Run(async () => { await start.Task; return await attempts.SweepAsync(10); });
            start.SetResult();
            var outcome = await cancel;
            Assert.Empty((await sweep).Failures);
            Assert.Equal(1L, await EntriesAsync(attempt, account, "release"));
            Assert.Equal(50, await BalanceAsync(account));
            Assert.StartsWith("cancelled:", await AttemptStateAsync(attempt));
            Assert.Equal(outcome.CancelReason, (await AttemptStateAsync(attempt)).Split(':')[1]);
        }
    }

    [Fact]
    public async Task A_persistently_failing_item_cools_down_and_the_rest_of_the_batch_proceeds()
    {
        var (_, stuck) = await AdmittedAsync();
        var others = new List<Guid>();
        for (var i = 0; i < 3; i++) others.Add((await AdmittedAsync()).Attempt);
        foreach (var a in others.Prepend(stuck)) await AttemptFixtures.ExpireDeadlineAsync(db, a);
        await ExecuteAsSuperuserAsync(db, $$"""
            create function bg_test_fail() returns trigger language plpgsql as $$ begin raise exception 'injected' using errcode = 'P0001'; end $$;
            create trigger bg_test_fail before update on attempt_participants for each row when (old.attempt_id = '{{stuck}}') execute function bg_test_fail();
            """);
        var cooldown = new SweepCooldown();
        var first = await attempts.SweepAsync(1, cooldown); // a batch of one: the stuck item is the oldest
        Assert.Single(first.Failures);
        Assert.Contains("P0001", first.Failures[0]);
        // The next two sweeps skip it (then four, eight, ... after further failures) and release the rest one by one: the stuck
        // item never holds the head of the batch for long.
        Assert.Equal(1, (await attempts.SweepAsync(1, cooldown)).Released);
        Assert.Equal(1, (await attempts.SweepAsync(1, cooldown)).Released);
        var third = await attempts.SweepAsync(1, cooldown); // its cool-down is over: tried again, fails again, cools down for four
        Assert.Equal((0, 1), (third.Released, third.Failures.Count));
        Assert.Equal(1, (await attempts.SweepAsync(1, cooldown)).Released);
        Assert.All(others, a => Assert.StartsWith("cancelled:no_capacity", AttemptStateAsync(a).Result));
        await ExecuteAsSuperuserAsync(db, "drop trigger bg_test_fail on attempt_participants; drop function bg_test_fail();");
        SweepReport last = new(0, 0, 0, 0, []);
        for (var i = 0; i < 6 && last.Released == 0; i++) last = await attempts.SweepAsync(1, cooldown);
        Assert.Equal(1, last.Released);
        Assert.Equal("cancelled:no_capacity", await AttemptStateAsync(stuck));
        Assert.Equal(0, cooldown.Count); // success clears it
    }

    // ---- exit and failure reports (B11 revised, R6) ----

    static ExitedRequest Exit(int? code, string? signal, params (long Account, bool Present)[] results) =>
        new(code, signal, results.Select(r => new ExitResult(r.Account, r.Present)).ToList());

    [Fact]
    public async Task Exited_before_start_cancels_releases_frees_the_slot_and_stores_the_evidence()
    {
        var (account, attempt) = await AdmittedAsync();
        await MoveAsync(attempt, "assigned", "launching");
        Assert.Equal(new ExitReportResult.Applied("cancelled"), await hosts.ExitedAsync(AttemptFixtures.FixtureHost, attempt, Exit(1, null, (account, false)), default));
        Assert.Equal(("cancelled:process_exited", "released:process_exited:released"), (await AttemptStateAsync(attempt), await ParticipantAsync(attempt, account)));
        Assert.NotNull(await ScalarAsSuperuserAsync<DateTime?>(db, "select slot_released_at from mission_attempts where attempt_id = @attempt", new { attempt }));
        Assert.Equal("agent:fixture-host:launching:False:1::cancelled|" + account + ":False", await EvidenceAsync(attempt));
        Assert.Equal(50, await BalanceAsync(account));
    }

    [Fact]
    public async Task Exited_after_start_applies_none_and_stores_the_evidence()
    {
        var (account, attempt) = await AdmittedAsync();
        await MoveAsync(attempt, "assigned", "launching", "ready");
        await StartAsync(attempt, account);
        Assert.Equal(new ExitReportResult.Applied("none"), await hosts.ExitedAsync(AttemptFixtures.FixtureHost, attempt, Exit(null, "SIGKILL", (account, true)), default));
        Assert.Equal(("running:-", "joined:-:charged"), (await AttemptStateAsync(attempt), await ParticipantAsync(attempt, account)));
        Assert.Equal(0L, await EntriesAsync(attempt, account, "release"));
        Assert.NotNull(await ScalarAsSuperuserAsync<DateTime?>(db, "select slot_released_at from mission_attempts where attempt_id = @attempt", new { attempt })); // the unit is gone
        Assert.Equal("agent:fixture-host:running:True::SIGKILL:none|" + account + ":True", await EvidenceAsync(attempt));
    }

    Task<string> EvidenceAsync(Guid attempt) => ScalarAsSuperuserAsync<string>(db, """
        select r.source || ':' || coalesce(r.host_id, '') || ':' || r.attempt_state || ':' || case when r.any_started then 'True' else 'False' end || ':'
               || coalesce(r.exit_code::text, '') || ':' || coalesce(r.signal, '') || ':' || r.outcome || '|'
               || coalesce((select string_agg(x.account_id || ':' || case when x.present then 'True' else 'False' end, ',' order by x.account_id)
                            from attempt_exit_results x where x.attempt_id = r.attempt_id), '')
        from attempt_exit_reports r where r.attempt_id = @attempt and r.source = 'agent'
        """, new { attempt });

    [Fact]
    public async Task An_identical_exit_report_repeats_and_a_conflicting_one_is_refused_and_keeps_the_first()
    {
        var (account, attempt) = await AdmittedAsync();
        await MoveAsync(attempt, "assigned", "launching", "ready");
        await StartAsync(attempt, account);
        var report = Exit(137, null, (account, false));
        Assert.Equal(new ExitReportResult.Applied("none"), await hosts.ExitedAsync(AttemptFixtures.FixtureHost, attempt, report, default));
        var evidence = await EvidenceAsync(attempt);
        Assert.Equal(new ExitReportResult.Applied("none"), await hosts.ExitedAsync(AttemptFixtures.FixtureHost, attempt, report, default));
        foreach (var conflicting in new[] { Exit(0, null, (account, false)), Exit(137, "SIGKILL", (account, false)), Exit(137, null, (account, true)), Exit(137, null) })
            Assert.Equal(new ExitReportResult.Conflicting(), await hosts.ExitedAsync(AttemptFixtures.FixtureHost, attempt, conflicting, default));
        Assert.Equal(evidence, await EvidenceAsync(attempt));
        // The process's failure report is evidence too, under its own source; identical repeats and conflicts likewise.
        var session = await ScalarAsSuperuserAsync<Guid>(db, "select process_session_id from mission_attempts where attempt_id = @attempt", new { attempt });
        Assert.Equal(new ExitReportResult.Applied("none"), await processes.FailedAsync(attempt, new FailedRequest(session, "crashed"), default));
        Assert.Equal(new ExitReportResult.Applied("none"), await processes.FailedAsync(attempt, new FailedRequest(session, "crashed"), default));
        Assert.Equal(new ExitReportResult.Conflicting(), await processes.FailedAsync(attempt, new FailedRequest(session, "oom"), default));
        Assert.Equal(new ExitReportResult.WrongSession(), await processes.FailedAsync(attempt, new FailedRequest(Guid.CreateVersion7(), "crashed"), default));
        Assert.Equal("crashed", await ScalarAsSuperuserAsync<string>(db, "select reason from attempt_exit_reports where attempt_id = @attempt and source = 'process'", new { attempt }));
        Assert.Equal(("running:-", "joined:-:charged"), (await AttemptStateAsync(attempt), await ParticipantAsync(attempt, account)));
    }

    [Fact]
    public async Task An_exit_report_naming_a_foreign_participant_or_another_hosts_attempt_writes_nothing()
    {
        var (account, attempt) = await AdmittedAsync();
        var (stranger, _) = await AdmittedAsync();
        await MoveAsync(attempt, "assigned", "launching");
        Assert.Equal(new ExitReportResult.UnknownParticipant(), await hosts.ExitedAsync(AttemptFixtures.FixtureHost, attempt, Exit(1, null, (account, false), (stranger, true)), default));
        Assert.Equal(new ExitReportResult.NotFound(), await hosts.ExitedAsync("other-host", attempt, Exit(1, null), default));
        Assert.Equal(new ExitReportResult.NotFound(), await hosts.ExitedAsync(AttemptFixtures.FixtureHost, Guid.CreateVersion7(), Exit(1, null), default));
        Assert.Equal("launching:-", await AttemptStateAsync(attempt));
        Assert.Equal(0L, await ScalarAsSuperuserAsync<long>(db, "select count(*) from attempt_exit_reports"));
    }

    [Fact]
    public async Task Failed_before_start_cancels_and_releases()
    {
        var (account, attempt) = await AdmittedAsync();
        await MoveAsync(attempt, "assigned", "launching", "ready");
        var session = await ScalarAsSuperuserAsync<Guid>(db, "select process_session_id from mission_attempts where attempt_id = @attempt", new { attempt });
        Assert.Equal(new ExitReportResult.Applied("cancelled"), await processes.FailedAsync(attempt, new FailedRequest(session, "generation-failed"), default));
        Assert.Equal(("cancelled:process_failed", "released:process_failed:released"), (await AttemptStateAsync(attempt), await ParticipantAsync(attempt, account)));
        Assert.Null(await ScalarAsSuperuserAsync<DateTime?>(db, "select slot_released_at from mission_attempts where attempt_id = @attempt", new { attempt })); // a failure is not an exit
        Assert.Equal(new ExitReportResult.Applied("cancelled"), await processes.FailedAsync(attempt, new FailedRequest(session, "generation-failed"), default));
    }

    [Fact]
    public async Task Exit_reports_race_cancels_and_deadlines_without_deadlock()
    {
        for (var round = 0; round < 8; round++)
        {
            var (account, attempt) = await AdmittedAsync();
            await MoveAsync(attempt, "assigned", "launching");
            await ExpireAsync(attempt, "launch_deadline");
            var start = new TaskCompletionSource();
            var exit = Task.Run(async () => { await start.Task; return await hosts.ExitedAsync(AttemptFixtures.FixtureHost, attempt, Exit(0, null), default); });
            var cancel = Task.Run(async () => { await start.Task; return await attempts.CancelAsync(account, attempt); });
            var sweep = Task.Run(async () => { await start.Task; return await attempts.SweepAsync(10); });
            start.SetResult();
            await Task.WhenAll(exit, cancel, sweep);
            Assert.Empty((await sweep).Failures);
            Assert.Equal(1L, await EntriesAsync(attempt, account, "release"));
            Assert.Equal(50, await BalanceAsync(account));
        }
    }
}
