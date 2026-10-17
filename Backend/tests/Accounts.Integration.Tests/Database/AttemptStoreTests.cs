using System.Text.Json;
using Dapper;
using Npgsql;
using Blackglass.Accounts.Domain.Attempts;
using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence;
using Blackglass.Accounts.Persistence.Accounts;
using Blackglass.Accounts.Persistence.Attempts;
using Blackglass.Accounts.Persistence.Energy;
using Blackglass.Accounts.Persistence.Player;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>
/// The player cancel before start and the exactly-once release of reserved energy (Task 7; owner rulings S7-D6, S7-D7)
/// against a real PostgreSQL 17, through <see cref="AttemptStore"/>. Attempts are admitted by the real
/// <see cref="AdmissionStore"/> (a reserve of 10 from a full wallet of 50), so every release has a real hold and ledger to
/// undo. The release lock order is the attempt, its participants, the hold, the inventories row, then (positive amounts
/// only) the wallet and the shared energy policy lock; races are built with the held-lock pattern on the inventories row,
/// which every release takes after the attempt row. The 0000-0007 invariants hold after every test.
/// </summary>
[Trait("Requires", "Postgres")]
public class AttemptStoreTests : IAsyncLifetime
{
    const string Template = "procedural-standard";
    TestDatabase db = null!;
    AccountAdminStore accounts = null!;
    EnergyAdminStore energy = null!;
    AdmissionStore admissions = null!;
    AttemptStore attempts = null!;

    public async Task InitializeAsync()
    {
        db = await MigratedAsync();
        accounts = new AccountAdminStore(db.AsApp());
        energy = new EnergyAdminStore(db.AsApp(), 1000);
        var hosts = new DevHosts(db.AsApp());
        await hosts.RegisterAsync("dev-fixture-1", 10, Op);
        await hosts.HeartbeatAsync("dev-fixture-1", Op);
        admissions = new AdmissionStore(db.AsApp(), new ServiceHealth(devHostsEnabled: true, leaseSeconds: 600), new CryptoAttemptSecrets(), GameCatalogue.Embedded, 60);
        attempts = new AttemptStore(db.AsApp());
    }

    public async Task DisposeAsync()
    {
        try { Assert.Empty(await InvariantFailuresAsync(db)); }
        finally { await db.DisposeAsync(); }
    }

    // ---- helpers -----------------------------------------------------------------------------------------------------------

    async Task<(long Account, long[] Operatives)> PlayerAsync()
    {
        var account = (await accounts.CreateAccountAsync(null, null, Op)).AccountId;
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        return (account, (await conn.QueryAsync<long>("select operative_id from operatives where account_id = @account order by operative_id", new { account })).ToArray());
    }

    async Task<Guid> AdmitAsync(long account, long operative, string template = Template, Guid? key = null) =>
        AttemptOf(await admissions.AdmitAsync(account, key ?? Guid.CreateVersion7(), new DeployRequest(template, [operative])));

    static Guid AttemptOf(Admission admission) => Guid.Parse(JsonDocument.Parse(admission.Body).RootElement.GetProperty("attemptId").GetString()!);

    Task<T> ScalarAsync<T>(string sql, object? args = null) => ScalarAsSuperuserAsync<T>(db, sql, args);

    async Task<T> RowAsync<T>(string sql, object? args = null)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        return await conn.QuerySingleAsync<T>(sql, args);
    }

    /// <summary>
    /// Everything a release could write, database-wide: every attempt, participant and hold with its state and xmin, the
    /// ledger, and every wallet and inventories row with its xmin. Equal snapshots mean nothing was written (a row lock
    /// leaves xmin alone).
    /// </summary>
    Task<string> StateAsync() => ScalarAsync<string>(
        """
        select concat_ws(' | ',
          (select string_agg(attempt_id || ':' || state || ':' || coalesce(cancel_reason, '-') || ':' || xmin::text, ',' order by attempt_id) from mission_attempts),
          (select string_agg(attempt_id || ':' || state || ':' || xmin::text, ',' order by attempt_id) from attempt_participants),
          (select string_agg(attempt_id || ':' || state || ':' || xmin::text, ',' order by attempt_id) from energy_holds),
          (select count(*) from energy_ledger), (select count(*) from attempt_insurance), (select count(*) from deploy_requests),
          (select string_agg(account_id || ':' || xmin::text || ':' || balance || ':' || policy_version, ',' order by account_id) from energy_wallets),
          (select string_agg(account_id || ':' || xmin::text || ':' || version, ',' order by account_id) from inventories))
        """);

    sealed record Entry(string Kind, int Delta, int BalanceAfter);

    async Task<List<Entry>> EntriesAsync(long account)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        return (await conn.QueryAsync<Entry>(
            "select kind as Kind, delta as Delta, balance_after as BalanceAfter from energy_ledger where account_id = @account order by entry_id", new { account })).ToList();
    }

    Task<int> BalanceAsync(long account) => ScalarAsync<int>("select balance from energy_wallets where account_id = @account", new { account });

    Task<long> ActiveParticipationsAsync(long account) => ScalarAsync<long>(
        "select count(*) from attempt_participants where account_id = @account and state = 'admitted'", new { account });

    Task<long> ReleaseEntriesAsync(Guid attempt) => ScalarAsync<long>("select count(*) from energy_ledger where kind = 'release' and attempt_id = @attempt", new { attempt });

    static async Task<AttemptException> RefusedAsync(Task task) => await Assert.ThrowsAsync<AttemptException>(() => task);

    // ---- cancel --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cancel_before_start_releases_once_and_restores_the_balance()
    {
        var (account, operatives) = await PlayerAsync();
        var attempt = await AdmitAsync(account, operatives[0]);
        Assert.Equal(40, await BalanceAsync(account));

        var outcome = await attempts.CancelAsync(account, attempt);

        Assert.Equal(new ReleaseOutcome("released", 10, 50, CancelReasons.PlayerCancel, Replayed: false), outcome);
        Assert.Equal([new Entry("open", 50, 50), new Entry("reserve", -10, 40), new Entry("release", 10, 50)], await EntriesAsync(account));
        Assert.Equal(50, await BalanceAsync(account));
        Assert.Equal(("cancelled", "player_cancel", true), await RowAsync<(string, string, bool)>(
            "select state, cancel_reason, ended_at >= created_at from mission_attempts where attempt_id = @attempt", new { attempt }));
        Assert.Equal("released", await ScalarAsync<string>("select state from attempt_participants where attempt_id = @attempt", new { attempt }));
        Assert.Equal("released", await ScalarAsync<string>("select state from energy_holds where attempt_id = @attempt", new { attempt }));
        Assert.Equal(("release:" + attempt.ToString("D"), attempt), await RowAsync<(string, Guid)>(
            "select idempotency_key, attempt_id from energy_ledger where kind = 'release' and account_id = @account", new { account }));
        Assert.Equal(0L, await ActiveParticipationsAsync(account)); // the inventory lock is released with it

        // A repeat returns the stored outcome and writes nothing.
        var before = await StateAsync();
        for (var i = 0; i < 2; i++)
            Assert.Equal(outcome with { Replayed = true }, await attempts.CancelAsync(account, attempt));
        Assert.Equal(before, await StateAsync());
        Assert.Equal(1L, await ReleaseEntriesAsync(attempt));
    }

    [Fact]
    public async Task A_release_above_the_cap_is_kept()
    {
        // Released energy is given back whole (E6): 40 after the reserve, 55 after a grant, 65 after the release, over the cap of 50.
        var (account, operatives) = await PlayerAsync();
        var attempt = await AdmitAsync(account, operatives[0]);
        Assert.Equal(55, (await energy.GrantAsync(account, 15, Guid.CreateVersion7(), Op)).BalanceAfter);

        var outcome = await attempts.CancelAsync(account, attempt);

        Assert.Equal((10, 65), (outcome.Released, outcome.BalanceAfter));
        Assert.Equal(65, await BalanceAsync(account));
        Assert.Equal(new Entry("release", 10, 65), (await EntriesAsync(account))[^1]);
    }

    [Fact]
    public async Task Two_concurrent_cancels_release_once()
    {
        // Both cancels queue: the first holds the attempt row and waits on the inventories row (held here), the second waits on
        // the attempt row. The first releases; the second then sees the stored outcome.
        var (account, operatives) = await PlayerAsync();
        var attempt = await AdmitAsync(account, operatives[0]);

        Task<ReleaseOutcome> first, second;
        await using (var holder = await db.AsSuperuser().OpenConnectionAsync())
        {
            await using var held = await holder.BeginTransactionAsync();
            await holder.ExecuteAsync("select 1 from inventories where account_id = @account for update", new { account }, held);
            first = Task.Run(() => attempts.CancelAsync(account, attempt));
            await AttemptFixtures.WaitForLockWaitersAsync(db, 1);
            second = Task.Run(() => attempts.CancelAsync(account, attempt));
            await AttemptFixtures.WaitForLockWaitersAsync(db, 2);
            Assert.False(first.IsCompleted || second.IsCompleted);
            await held.CommitAsync();
        }
        var outcomes = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal([false, true], outcomes.Select(o => o.Replayed).Order().ToArray());
        Assert.Single(outcomes.Select(o => o with { Replayed = false }).Distinct());
        Assert.Equal(1L, await ReleaseEntriesAsync(attempt));
        Assert.Equal(50, await BalanceAsync(account)); // restored exactly once
    }

    [Fact]
    public async Task A_cancel_and_the_deadline_worker_release_once()
    {
        // The worker selected the attempt (DueAsync) just before the player cancelled; both then race for the release, in both
        // orders. The winner's reason is stored; the loser sees the stored outcome; the energy comes back once.
        foreach (var cancelFirst in new[] { true, false })
        {
            var (account, operatives) = await PlayerAsync();
            var attempt = await AdmitAsync(account, operatives[0]);
            await AttemptFixtures.ExpireDeadlineAsync(db, attempt);
            var due = Assert.Single(await attempts.DueAsync(10));
            Assert.Equal(new DueAttempt(attempt, account), due);

            Task<ReleaseOutcome> cancel;
            Task<ExpireResult> expire;
            await using (var holder = await db.AsSuperuser().OpenConnectionAsync())
            {
                await using var held = await holder.BeginTransactionAsync();
                await holder.ExecuteAsync("select 1 from inventories where account_id = @account for update", new { account }, held);
                if (cancelFirst)
                {
                    cancel = Task.Run(() => attempts.CancelAsync(account, attempt));
                    await AttemptFixtures.WaitForLockWaitersAsync(db, 1);
                    expire = Task.Run(() => attempts.ExpireAsync(due));
                }
                else
                {
                    expire = Task.Run(() => attempts.ExpireAsync(due));
                    await AttemptFixtures.WaitForLockWaitersAsync(db, 1);
                    cancel = Task.Run(() => attempts.CancelAsync(account, attempt));
                }
                await AttemptFixtures.WaitForLockWaitersAsync(db, 2);
                await held.CommitAsync();
            }
            await Task.WhenAll(cancel, expire).WaitAsync(TimeSpan.FromSeconds(30));

            var winner = cancelFirst ? CancelReasons.PlayerCancel : CancelReasons.NoCapacity;
            Assert.Equal(new ReleaseOutcome("released", 10, 50, winner, Replayed: !cancelFirst), await cancel);
            Assert.Equal(cancelFirst ? ExpireResult.AlreadyEnded : ExpireResult.Released, await expire);
            Assert.Equal(winner, await ScalarAsync<string>("select cancel_reason from mission_attempts where attempt_id = @attempt", new { attempt }));
            Assert.Equal(1L, await ReleaseEntriesAsync(attempt));
            Assert.Equal(50, await BalanceAsync(account));
        }
    }

    [Fact]
    public async Task Cancel_refuses_unknown_foreign_and_started_attempts_writing_nothing()
    {
        var (account, operatives) = await PlayerAsync();
        var (other, otherOperatives) = await PlayerAsync();
        var mine = await AdmitAsync(account, operatives[0]);
        var theirs = await AdmitAsync(other, otherOperatives[0]);
        var before = await StateAsync();

        // Another account's attempt is the same NotFound as an unknown one, and is never locked: a held lock on its row does not
        // delay the refusal (a 300 ms lock timeout would fail it otherwise).
        var bounded = new AttemptStore(db.AsAppWith(new DbTimeouts(LockTimeoutMs: 300, StatementTimeoutMs: 10_000)));
        await using (var holder = await db.AsSuperuser().OpenConnectionAsync())
        {
            await using var held = await holder.BeginTransactionAsync();
            await holder.ExecuteAsync("select 1 from mission_attempts where attempt_id = @theirs for update", new { theirs }, held);
            Assert.Equal(AttemptError.NotFound, (await RefusedAsync(bounded.CancelAsync(account, theirs))).Error);
            await held.RollbackAsync();
        }
        Assert.Equal(AttemptError.NotFound, (await RefusedAsync(attempts.CancelAsync(account, Guid.CreateVersion7()))).Error);
        Assert.Null(await attempts.GetAsync(account, theirs));
        Assert.Null(await attempts.GetAsync(account, Guid.CreateVersion7()));
        Assert.Equal(before, await StateAsync());

        // After start (any state but admitted) a cancel is InvalidState and never cancels: the attempt is assigned here by SQL.
        await ExecuteAsSuperuserAsync(db, "update mission_attempts set state = 'assigned' where attempt_id = @mine", new { mine });
        before = await StateAsync();
        Assert.Equal(AttemptError.InvalidState, (await RefusedAsync(attempts.CancelAsync(account, mine))).Error);
        Assert.Equal(before, await StateAsync());
        Assert.Equal(1L, await ActiveParticipationsAsync(account));
    }

    [Fact]
    public async Task Get_attempt_reads_the_participation_in_one_snapshot()
    {
        var (account, operatives) = await PlayerAsync();
        var now = await ScalarAsync<DateTime>("select clock_timestamp()");
        await SeedCoverageAsync(account, now.AddHours(-1), now.AddDays(1));
        var attempt = await AdmitAsync(account, operatives[0]);
        var deadline = await ScalarAsync<DateTime>("select assign_deadline from mission_attempts where attempt_id = @attempt", new { attempt });

        Assert.Equal(new AttemptView(attempt, "admitted", "admitted", deadline, 10, "reserved", "standard", 1), await attempts.GetAsync(account, attempt));
        await attempts.CancelAsync(account, attempt);
        Assert.Equal(new AttemptView(attempt, "cancelled", "released", deadline, 10, "released", "standard", 1), await attempts.GetAsync(account, attempt));
        Assert.Equal(DateTimeKind.Utc, (await attempts.GetAsync(account, attempt))!.AssignDeadline.Kind);
    }

    // ---- atomicity and what survives -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("energy_wallets", "update", null)]
    [InlineData("energy_ledger", "insert", "new.kind = 'release'")]
    [InlineData("mission_attempts", "update", null)]
    [InlineData("attempt_participants", "update", null)]
    [InlineData("energy_holds", "update", null)]
    public async Task A_failure_during_release_rolls_back(string table, string operation, string? when)
    {
        var (account, operatives) = await PlayerAsync();
        var attempt = await AdmitAsync(account, operatives[0]);
        await ExecuteAsSuperuserAsync(db,
            "create function bg_test_fail() returns trigger language plpgsql as $$ begin raise exception 'injected failure' using errcode = 'P0001'; end $$; " +
            "create trigger bg_test_fail before " + operation + " on " + table + " for each row " + (when is null ? "" : "when (" + when + ") ") + "execute function bg_test_fail();");
        var before = await StateAsync();

        var failure = await Assert.ThrowsAsync<PostgresException>(() => attempts.CancelAsync(account, attempt));
        Assert.Equal("P0001", failure.SqlState);
        Assert.Equal(before, await StateAsync()); // no partial release: attempt, participant, hold, wallet and ledger all unchanged
        Assert.Equal(1L, await ActiveParticipationsAsync(account)); // still locked, still reserved

        // Nothing retried on its own; the player's retry after the fault is gone releases exactly once.
        await ExecuteAsSuperuserAsync(db, "drop trigger bg_test_fail on " + table + "; drop function bg_test_fail();");
        Assert.Equal(new ReleaseOutcome("released", 10, 50, CancelReasons.PlayerCancel, Replayed: false), await attempts.CancelAsync(account, attempt));
        Assert.Equal(1L, await ReleaseEntriesAsync(attempt));
        Assert.Equal(50, await BalanceAsync(account));
    }

    [Fact]
    public async Task A_release_keeps_the_admission_and_insurance_records()
    {
        var (account, operatives) = await PlayerAsync();
        var now = await ScalarAsync<DateTime>("select clock_timestamp()");
        await SeedCoverageAsync(account, now.AddHours(-1), now.AddDays(1));
        var key = Guid.CreateVersion7();
        var request = new DeployRequest(Template, [operatives[0]]);
        var admitted = await admissions.AdmitAsync(account, key, request);
        var attempt = AttemptOf(admitted);
        const string AttemptFixed = "select row(attempt_id, created_by, template_id, template_version, seed, retention_salt, created_at, assign_deadline)::text from mission_attempts where attempt_id = @attempt";
        const string ParticipantFixed = "select row(attempt_id, account_id, template_id, template_version, loadout_snapshot, inventory_version, energy_cost)::text from attempt_participants where attempt_id = @attempt";
        const string Insurance = "select xmin::text || ' ' || row_to_json(i)::text from attempt_insurance i where attempt_id = @attempt";
        const string Deploy = "select xmin::text || ' ' || row_to_json(d)::text from deploy_requests d where attempt_id = @attempt";
        const string Reserve = "select xmin::text || ' ' || row_to_json(l)::text from energy_ledger l where kind = 'reserve' and attempt_id = @attempt";
        var kept = new[] { AttemptFixed, ParticipantFixed, Insurance, Deploy, Reserve };
        var before = new List<string>();
        foreach (var sql in kept) before.Add(await ScalarAsync<string>(sql, new { attempt }));

        await attempts.CancelAsync(account, attempt);

        // The admission records all remain, unchanged: only the three states moved (and the attempt's reason and end).
        for (var i = 0; i < kept.Length; i++) Assert.Equal(before[i], await ScalarAsync<string>(kept[i], new { attempt }));
        Assert.Equal((1L, 1L, 1L, 1L, 1L), (await ScalarAsync<long>("select count(*) from mission_attempts"), await ScalarAsync<long>("select count(*) from attempt_participants"),
            await ScalarAsync<long>("select count(*) from energy_holds"), await ScalarAsync<long>("select count(*) from attempt_insurance"), await ScalarAsync<long>("select count(*) from deploy_requests")));

        // A replay of the original deploy key returns the original 201 body: it is the recorded response; nothing is reserved
        // again and the account does not become active again.
        var replay = await admissions.AdmitAsync(account, key, request);
        Assert.True(replay.Replayed);
        Assert.Equal(admitted.Body, replay.Body);
        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from energy_ledger where kind = 'reserve'"));
        Assert.Equal(50, await BalanceAsync(account));
        Assert.Equal(0L, await ActiveParticipationsAsync(account));
    }

    [Fact]
    public async Task A_zero_amount_release_writes_no_ledger_entry()
    {
        await new MissionTemplates(db.AsApp()).PublishAsync("free", 0, 1, 3, Op);
        var (account, operatives) = await PlayerAsync();
        var attempt = await AdmitAsync(account, operatives[0], "free");
        var wallet = await ScalarAsync<string>("select xmin::text || ':' || balance from energy_wallets where account_id = @account", new { account });
        var entries = await EntriesAsync(account);

        // With the wallet row and the exclusive publication lock both held elsewhere, the release still goes through: a
        // 0-amount hold takes no wallet lock and no policy lock (a 300 ms lock timeout would fail it otherwise).
        var bounded = new AttemptStore(db.AsAppWith(new DbTimeouts(LockTimeoutMs: 300, StatementTimeoutMs: 10_000)));
        ReleaseOutcome outcome;
        await using (var holder = await db.AsSuperuser().OpenConnectionAsync())
        {
            await using var held = await holder.BeginTransactionAsync();
            await holder.ExecuteAsync("select 1 from energy_wallets where account_id = @account for update", new { account }, held);
            await holder.ExecuteAsync("select pg_advisory_xact_lock(@key)", new { key = EnergyPolicies.PublicationLockKey }, held);
            outcome = await bounded.CancelAsync(account, attempt).WaitAsync(TimeSpan.FromSeconds(10));
            await held.RollbackAsync();
        }

        Assert.Equal(new ReleaseOutcome("released", 0, null, CancelReasons.PlayerCancel, Replayed: false), outcome);
        Assert.Equal(entries, await EntriesAsync(account));
        Assert.Equal(wallet, await ScalarAsync<string>("select xmin::text || ':' || balance from energy_wallets where account_id = @account", new { account }));
        Assert.Equal(("released", "released"), await RowAsync<(string, string)>(
            "select p.state, h.state from attempt_participants p join energy_holds h using (attempt_id, account_id) where p.attempt_id = @attempt", new { attempt }));
        Assert.Equal(outcome with { Replayed = true }, await attempts.CancelAsync(account, attempt));

        // An account without a wallet releases a zero-cost attempt the same way.
        long walletless;
        await using (var conn = await db.AsApp().OpenConnectionAsync())
        {
            await using var tx = await conn.BeginTransactionAsync();
            walletless = await conn.ExecuteScalarAsync<long>("insert into accounts (status) values ('active') returning account_id", transaction: tx);
            await StarterProvisioner.ProvisionAsync(conn, tx, walletless, GameCatalogue.Embedded, default);
            await tx.CommitAsync();
        }
        var free = await AdmitAsync(walletless, await ScalarAsync<long>("select min(operative_id) from operatives where account_id = @walletless", new { walletless }), "free");
        Assert.Equal(new ReleaseOutcome("released", 0, null, CancelReasons.PlayerCancel, Replayed: false), await attempts.CancelAsync(walletless, free));
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from energy_ledger where account_id = @walletless", new { walletless }));
    }

    /// <summary>A coverage written directly with the activation that set it (see AdmissionStoreTests).</summary>
    async Task SeedCoverageAsync(long account, DateTime startsAt, DateTime expiresAt)
    {
        var activation = Guid.CreateVersion7();
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync(
            """
            insert into insurance_activations (activation_id, account_id, policy_id, policy_version, duration, previous_expires_at, resulting_expires_at, source, idempotency_key)
            values (@activation, @account, 'standard', 1, @duration, null, @expiresAt, 'dev_grant', gen_random_uuid())
            """, new { activation, account, duration = expiresAt - startsAt, expiresAt }, tx);
        await conn.ExecuteAsync(
            "insert into insurance_coverages (account_id, policy_id, policy_version, activation_id, starts_at, expires_at) values (@account, 'standard', 1, @activation, @startsAt, @expiresAt)",
            new { account, activation, startsAt, expiresAt }, tx);
        await tx.CommitAsync();
    }
}
