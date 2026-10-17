using Dapper;
using Npgsql;
using Blackglass.Accounts.Domain.Attempts;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence;
using Blackglass.Accounts.Persistence.Attempts;
using Blackglass.Accounts.Persistence.Energy;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>
/// The backend-committed start (Stage 8 Task 9; spec §7.5; owner rulings B8, B9): per participant, only after a ticket that
/// participant's session redeemed and before the join deadline (database clock), the participant joins, its hold is charged
/// exactly once (a positive hold with one zero-delta <c>charge:&lt;attempt&gt;</c> entry, a zero hold with none) and the attempt
/// runs; a repeat answers the stored start, even after the deadline; a cancel, the join deadline and the start race to exactly
/// one outcome; an injected failure at any write leaves nothing.
/// </summary>
[Trait("Requires", "Postgres")]
public class StartTests : IAsyncLifetime
{
    TestDatabase db = null!;
    MissionKit kit = null!;
    StartStore starts = null!;
    AttemptStore attempts = null!;

    public async Task InitializeAsync()
    {
        db = await MigratedAsync();
        kit = await MissionKit.CreateAsync(db);
        starts = new StartStore(db.AsApp());
        attempts = new AttemptStore(db.AsApp());
    }

    public async Task DisposeAsync()
    {
        try { Assert.Empty(await InvariantFailuresAsync(db)); }
        finally { await db.DisposeAsync(); }
    }

    Task<StartResult> StartAsync(MissionKit.Mission m, long? account = null) => starts.StartAsync(m.Attempt, account ?? m.Account, m.Session, default);

    async Task<(string Attempt, string Participant, string Hold, long Charges, int Balance)> SnapshotAsync(MissionKit.Mission m, long? account = null)
    {
        var a = account ?? m.Account;
        await using var su = await db.AsSuperuser().OpenConnectionAsync();
        return await su.QuerySingleAsync<(string, string, string, long, int)>("""
            select (select state from mission_attempts where attempt_id = @attempt),
                   (select state from attempt_participants where attempt_id = @attempt and account_id = @a),
                   (select state from energy_holds where attempt_id = @attempt and account_id = @a),
                   (select count(*) from energy_ledger where attempt_id = @attempt and account_id = @a and kind = 'charge'),
                   (select balance from energy_wallets where account_id = @a)
            """, new { attempt = m.Attempt, a });
    }

    async Task ExpireJoinDeadlineAsync(Guid attempt)
    {
        await using var su = await db.AsSuperuser().OpenConnectionAsync();
        await using var tx = await su.BeginTransactionAsync();
        await su.ExecuteAsync("set local session_replication_role = replica", transaction: tx);
        await su.ExecuteAsync("update mission_attempts set join_deadline = clock_timestamp() - interval '1 second' where attempt_id = @attempt", new { attempt }, tx);
        await tx.CommitAsync();
    }

    // ---- the start ----

    [Fact]
    public async Task A_start_charges_once_and_moves_the_attempt_to_running()
    {
        var m = await kit.ReadyAsync();
        await kit.RedeemAsync(m);
        var started = Assert.IsType<StartResult.Started>(await StartAsync(m));
        Assert.False(started.Replayed);
        Assert.Equal(("running", "joined", "charged", 1L, 40), await SnapshotAsync(m));
        Assert.Equal((0, 40, "charge:" + m.Attempt.ToString("D")), await (await db.AsSuperuser().OpenConnectionAsync()).QuerySingleAsync<(int, int, string)>(
            "select delta, balance_after, idempotency_key from energy_ledger where kind = 'charge' and attempt_id = @a", new { a = m.Attempt }));
        Assert.Equal(started.StartedAt, await ScalarAsSuperuserAsync<DateTime>(db, "select started_at from attempt_participants where attempt_id = @a", new { a = m.Attempt }));
        Assert.Equal(started.StartedAt, await ScalarAsSuperuserAsync<DateTime>(db, "select started_at from mission_attempts where attempt_id = @a", new { a = m.Attempt }));
    }

    [Fact]
    public async Task A_repeated_start_charges_once_and_returns_the_stored_startedAt()
    {
        var m = await kit.ReadyAsync();
        await kit.RedeemAsync(m);
        var first = Assert.IsType<StartResult.Started>(await StartAsync(m));
        var gate = new TaskCompletionSource();
        var repeats = Enumerable.Range(0, 20).Select(_ => Task.Run(async () => { await gate.Task; return await StartAsync(m); })).ToArray();
        gate.SetResult();
        foreach (var r in await Task.WhenAll(repeats)) Assert.Equal(first with { Replayed = true }, r);
        Assert.Equal(("running", "joined", "charged", 1L, 40), await SnapshotAsync(m));
    }

    [Fact]
    public async Task Twenty_concurrent_first_starts_charge_once()
    {
        var m = await kit.ReadyAsync();
        await kit.RedeemAsync(m);
        var gate = new TaskCompletionSource();
        var racers = Enumerable.Range(0, 20).Select(_ => Task.Run(async () => { await gate.Task; return await StartAsync(m); })).ToArray();
        gate.SetResult();
        var results = (await Task.WhenAll(racers)).Cast<StartResult.Started>().ToList();
        Assert.Single(results, r => !r.Replayed);
        Assert.Single(results.Select(r => r.StartedAt).Distinct());
        Assert.Equal(("running", "joined", "charged", 1L, 40), await SnapshotAsync(m));
    }

    [Fact]
    public async Task A_zero_cost_start_charges_with_no_ledger_entry()
    {
        // A fixture attempt (zero-amount hold) on h1's build, through the real claim, register, ready, ticket and start.
        await using (var su = await db.AsSuperuser().OpenConnectionAsync())
            await su.ExecuteAsync("insert into allowed_builds (build_revision, catalogue_version) values ('r1', 'sha256:' || repeat('ab', 32)); insert into allowed_build_templates (build_revision, catalogue_version, template_id, template_version) values ('r1', 'sha256:' || repeat('ab', 32), 'procedural-standard', 1)");
        var zeroBuild = new Blackglass.Accounts.Domain.Hosts.BuildIdentity("r1", "sha256:" + new string('a', 0) + string.Concat(Enumerable.Repeat("ab", 32)));
        await kit.Hosts.HeartbeatAsync("h1", await AttemptFixtures.HostSecretHashAsync(db, "h1"), new HeartbeatRequest(zeroBuild, true, [], 0), default);
        var account = (await kit.Accounts.CreateAccountAsync(null, null, Op)).AccountId;
        var attempt = await AttemptFixtures.AdmitAsync(db, account);
        var claim = Assert.IsType<Blackglass.Accounts.Persistence.Hosts.ClaimResult.Claimed>(await kit.Hosts.ClaimAsync("h1", await AttemptFixtures.HostSecretHashAsync(db, "h1"), Guid.NewGuid(), 120, default));
        var session = Assert.IsType<RegisterResult.Registered>(await kit.Processes.RegisterAsync(attempt, claim.Credential.Hash(), new RegisterRequest(zeroBuild), default)).ProcessSessionId;
        PoolLine[] pool = [new(0, 0, "item.medkit", 1)];
        Assert.IsType<ReadyResult.Ready>(await kit.Processes.ReadyAsync(attempt, new ReadyReport(session, "cf2e14dece9cc2a6", MissionKit.Table.Version,
            LootHash.Compute(MissionKit.Table.Version, pool), [new Objective("extract", true)], pool), MissionKit.Table, zeroBuild.CatalogueVersion, 120, default));
        var m = new MissionKit.Mission(account, attempt, session, claim.Credential);
        await kit.RedeemAsync(m);
        Assert.IsType<StartResult.Started>(await StartAsync(m));
        Assert.Equal(("running", "joined", "charged", 0L, 50), await SnapshotAsync(m));
    }

    [Fact]
    public async Task A_start_without_a_ticket_redeemed_by_its_session_is_NotRedeemed()
    {
        var m = await kit.ReadyAsync();
        Assert.IsType<StartResult.NotRedeemed>(await StartAsync(m));
        await kit.Tickets.MintAsync(m.Account, m.Attempt, 120, default); // minted, never redeemed
        Assert.IsType<StartResult.NotRedeemed>(await StartAsync(m));
        Assert.IsType<StartResult.WrongSession>(await starts.StartAsync(m.Attempt, m.Account, Guid.CreateVersion7(), default));
        Assert.IsType<StartResult.NotFound>(await starts.StartAsync(m.Attempt, (await kit.AdmitAsync()).Account, m.Session, default));
        Assert.Equal(("ready", "admitted", "reserved", 0L, 40), await SnapshotAsync(m));
    }

    [Fact]
    public async Task A_start_after_the_join_deadline_is_DeadlinePassed_and_the_worker_then_releases()
    {
        var m = await kit.ReadyAsync();
        await kit.RedeemAsync(m);
        await ExpireJoinDeadlineAsync(m.Attempt);
        Assert.IsType<StartResult.DeadlinePassed>(await StartAsync(m));
        Assert.Equal(("ready", "admitted", "reserved", 0L, 40), await SnapshotAsync(m));
        Assert.Equal(1, (await attempts.SweepAsync(10)).Released);
        Assert.Equal(("cancelled", "released", "released", 0L, 50), await SnapshotAsync(m));
        Assert.IsType<StartResult.Cancelled>(await StartAsync(m));
    }

    [Fact]
    public async Task A_lost_start_acknowledgement_retried_after_the_join_deadline_returns_the_stored_result()
    {
        var m = await kit.ReadyAsync();
        await kit.RedeemAsync(m);
        var first = Assert.IsType<StartResult.Started>(await StartAsync(m));
        await ExpireJoinDeadlineAsync(m.Attempt);
        Assert.Equal(first with { Replayed = true }, await StartAsync(m)); // B8: the committed start stands
        Assert.Equal(new SweepReport(0, 0, 0, 0, []), await attempts.SweepAsync(10), SweepComparer.Instance);
        Assert.Equal(("running", "joined", "charged", 1L, 40), await SnapshotAsync(m));
    }

    [Fact]
    public async Task A_start_after_cancel_is_Cancelled()
    {
        var m = await kit.ReadyAsync();
        await kit.RedeemAsync(m);
        await attempts.CancelAsync(m.Account, m.Attempt);
        Assert.IsType<StartResult.Cancelled>(await StartAsync(m));
        Assert.Equal(("cancelled", "released", "released", 0L, 50), await SnapshotAsync(m));
    }

    // ---- races ----

    [Fact]
    public async Task A_start_and_a_cancel_race_and_exactly_one_wins()
    {
        for (var round = 0; round < 10; round++)
        {
            var m = await kit.ReadyAsync();
            await kit.RedeemAsync(m);
            var gate = new TaskCompletionSource();
            var start = Task.Run(async () => { await gate.Task; return await StartAsync(m); });
            var cancel = Task.Run(async () =>
            {
                await gate.Task;
                try { return (ReleaseOutcome?)await attempts.CancelAsync(m.Account, m.Attempt); }
                catch (AttemptException e) when (e.Error == AttemptError.InvalidState) { return null; }
            });
            gate.SetResult();
            var (s, c) = (await start, await cancel);
            var snapshot = await SnapshotAsync(m);
            if (s is StartResult.Started)
            {
                Assert.Null(c);
                Assert.Equal(("running", "joined", "charged", 1L, 40), snapshot);
            }
            else
            {
                Assert.IsType<StartResult.Cancelled>(s);
                Assert.NotNull(c);
                Assert.Equal(("cancelled", "released", "released", 0L, 50), snapshot);
            }
            await kit.BeatAsync();
        }
    }

    [Fact]
    public async Task A_start_and_the_join_deadline_race_and_exactly_one_wins()
    {
        for (var round = 0; round < 10; round++)
        {
            var m = await kit.ReadyAsync();
            await kit.RedeemAsync(m);
            // The deadline passes for the worker's selection only: the start re-checks it under its locks, by the database clock.
            var gate = new TaskCompletionSource();
            var start = Task.Run(async () => { await gate.Task; return await StartAsync(m); });
            var sweep = Task.Run(async () => { await gate.Task; await ExpireJoinDeadlineAsync(m.Attempt); return await attempts.SweepAsync(10); });
            gate.SetResult();
            var (s, w) = (await start, await sweep);
            Assert.Empty(w.Failures);
            if (s is StartResult.Started) Assert.Equal(("running", "joined", "charged", 1L, 40), await SnapshotAsync(m));
            else
            {
                // The start came after the deadline (refused) or after the worker's release (cancelled); either way never charged.
                Assert.True(s is StartResult.DeadlinePassed or StartResult.Cancelled, s.GetType().Name);
                await attempts.SweepAsync(10); // a sweep that met the start's lock skipped the attempt (SKIP LOCKED); the next one releases
                Assert.Equal(("cancelled", "released", "released", 0L, 50), await SnapshotAsync(m));
            }
            await kit.BeatAsync();
        }
    }

    [Fact]
    public async Task A_start_drains_with_a_same_account_grant_and_a_queued_publication_without_deadlock()
    {
        var port = int.Parse(db.EnvFor("app")[DbSettings.PortVar]!, System.Globalization.CultureInfo.InvariantCulture);
        await using var wide = new DbSettings(db.Host, port, db.Name, "accounts_app", db.Password("app")).CreateDataSource("blackglass-tests", maxPoolSize: 12);
        var racingStarts = new StartStore(wide);
        var energy = new EnergyAdminStore(wide, 1000);
        for (var round = 0; round < 8; round++)
        {
            var m = await kit.ReadyAsync();
            await kit.RedeemAsync(m);
            var gate = new TaskCompletionSource();
            var work = new List<Task>
            {
                Task.Run(async () => { await gate.Task; Assert.IsType<StartResult.Started>(await racingStarts.StartAsync(m.Attempt, m.Account, m.Session, default)); }),
                Task.Run(async () => { await gate.Task; await energy.GrantAsync(m.Account, 5, Guid.CreateVersion7(), Op); }),
                Task.Run(async () => { await gate.Task; await energy.PublishPolicyAsync(50 + round, 50, TimeSpan.FromMinutes(10), 1, 100, Op); }),
                Task.Run(async () => { await gate.Task; await energy.GrantAsync(m.Account, 3, Guid.CreateVersion7(), Op); }),
            };
            gate.SetResult();
            await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(60)); // no 40P01, no timeout, no hang
            var (attempt, participant, hold, charges, _) = await SnapshotAsync(m);
            Assert.Equal(("running", "joined", "charged", 1L), (attempt, participant, hold, charges));
        }
    }

    // ---- failure injection ----

    [Theory]
    [InlineData("attempt_participants")]
    [InlineData("energy_holds")]
    [InlineData("energy_ledger")]
    [InlineData("mission_attempts")]
    public async Task A_start_commit_failure_injected_at_each_write_leaves_nothing(string table)
    {
        var m = await kit.ReadyAsync();
        await kit.RedeemAsync(m);
        var op = table == "energy_ledger" ? "insert" : "update";
        await ExecuteAsSuperuserAsync(db,
            "create function bg_test_fail() returns trigger language plpgsql as $$ begin raise exception 'injected' using errcode = 'P0001'; end $$; " +
            $"create trigger bg_test_fail before {op} on {table} for each row execute function bg_test_fail();");
        try
        {
            var e = await Assert.ThrowsAsync<PostgresException>(() => StartAsync(m));
            Assert.Equal("P0001", e.SqlState);
        }
        finally { await ExecuteAsSuperuserAsync(db, $"drop trigger bg_test_fail on {table}; drop function bg_test_fail();"); }
        Assert.Equal(("ready", "admitted", "reserved", 0L, 40), await SnapshotAsync(m));
        Assert.IsType<StartResult.Started>(await StartAsync(m)); // and the next try commits once
        Assert.Equal(("running", "joined", "charged", 1L, 40), await SnapshotAsync(m));
    }

    [Fact]
    public async Task Per_participant_charges_are_independent()
    {
        // Co-op shaped (direct SQL): a second participant with its own reservation joins the same ready attempt; each start
        // charges only its own hold, with its own entry, and the attempt starts once.
        var m = await kit.ReadyAsync();
        var second = (await kit.Accounts.CreateAccountAsync(null, null, Op)).AccountId;
        await using (var su = await db.AsSuperuser().OpenConnectionAsync())
        await using (var tx = await su.BeginTransactionAsync())
        {
            await su.ExecuteAsync("""
                insert into attempt_participants (attempt_id, account_id, template_id, template_version, loadout_snapshot, inventory_version, energy_cost)
                select @attempt, @second, template_id, template_version, '{"operatives":[]}', 1, 10 from mission_attempts where attempt_id = @attempt;
                insert into energy_holds (attempt_id, account_id, amount) values (@attempt, @second, 10);
                update energy_wallets set balance = balance - 10 where account_id = @second;
                insert into energy_ledger (account_id, attempt_id, kind, delta, balance_after, idempotency_key)
                select @second, @attempt, 'reserve', -10, balance, 'reserve:' || @attempt::text from energy_wallets where account_id = @second;
                """, new { attempt = m.Attempt, second }, tx);
            await tx.CommitAsync();
        }
        await kit.RedeemAsync(m);
        await kit.RedeemAsync(m, second);
        var first = Assert.IsType<StartResult.Started>(await StartAsync(m));
        Assert.Equal(("running", "admitted", "reserved", 0L, 40), await SnapshotAsync(m, second));
        var other = Assert.IsType<StartResult.Started>(await StartAsync(m, second));
        Assert.True(other.StartedAt >= first.StartedAt);
        Assert.Equal(("running", "joined", "charged", 1L, 40), await SnapshotAsync(m));
        Assert.Equal(("running", "joined", "charged", 1L, 40), await SnapshotAsync(m, second));
        Assert.Equal(first.StartedAt, await ScalarAsSuperuserAsync<DateTime>(db, "select started_at from mission_attempts where attempt_id = @a", new { a = m.Attempt }));
    }
}
