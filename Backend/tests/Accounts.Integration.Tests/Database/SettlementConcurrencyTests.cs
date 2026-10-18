using Dapper;
using Blackglass.Accounts.Domain.Attempts;
using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.Domain.Inventory;
using Blackglass.Accounts.Domain.Settlement;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Attempts;
using Blackglass.Accounts.Persistence.Catalogue;
using Blackglass.Accounts.Persistence.Energy;
using Blackglass.Accounts.Persistence.Hosts;
using Blackglass.Accounts.Persistence.Player;
using Blackglass.Accounts.Persistence.Settlement;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>Concurrency (plan Task 8, the owner's five required race and crash tests, and revision 3's lock-order races): exactly
/// one outcome per participant under every race, no settlement after a disable commits, and no deadlock against suspension,
/// credential rotation, energy grants or policy publication.</summary>
[Trait("Requires", "Postgres")]
public class SettlementConcurrencyTests : IAsyncLifetime
{
    TestDatabase db = null!;
    MissionKit kit = null!;
    HostAdminStore hosts = null!;
    const int Rounds = 50;

    public async Task InitializeAsync()
    {
        db = await MigratedAsync();
        kit = await MissionKit.CreateAsync(db, capacity: 64);
        hosts = new HostAdminStore(db.AsApp());
    }

    public async Task DisposeAsync()
    {
        try { Assert.Empty(await InvariantFailuresAsync(db)); }
        finally { await db.DisposeAsync(); }
    }

    Task<T> Scalar<T>(string sql, object? args = null) => ScalarAsSuperuserAsync<T>(db, sql, args);

    /// <summary>Frees the attempt's slot as the agent's exit report would (the host has 64): a settled attempt directly, an
    /// unsettled one by an incomplete exit report (which voids nothing).</summary>
    async Task FreeSlotAsync(ManifestKit.Running r)
    {
        if (await Scalar<string>("select state from mission_attempts where attempt_id = @Attempt", new { r.Attempt }) == "settled")
            await ExecuteAsSuperuserAsync(db, "update mission_attempts set slot_released_at = clock_timestamp() where attempt_id = @Attempt and slot_released_at is null", new { r.Attempt });
        else
            await kit.Hosts.ExitedAsync("h1", await AttemptFixtures.HostSecretHashAsync(db, "h1"), r.Attempt, new ExitedRequest(0, null, [new ExitResult(r.Account, true)], false), default);
    }

    async Task<ExitReportResult> VoidReportAsync(ManifestKit.Running r) =>
        await kit.Hosts.ExitedAsync("h1", await AttemptFixtures.HostSecretHashAsync(db, "h1"), r.Attempt, new ExitedRequest(1, null, [new ExitResult(r.Account, false)], true), default);

    // ---- the owner's required races ----

    [Fact]
    public async Task Disable_racing_settlement_never_settles_after_the_disable()
    {
        var random = new Random(17);
        for (var round = 0; round < Rounds; round++)
        {
            var r = await ManifestKit.StartAsync(kit, db);
            var body = ManifestKit.Body(ManifestKit.Manifest(r));
            var secret = await AttemptFixtures.HostSecretHashAsync(db, "h1");
            var store = ManifestKit.Store(db);
            var delay = random.Next(0, 25);
            var relay = Task.Run(() => store.SubmitAsync("h1", secret, body, default));
            var disable = Task.Run(async () => { await Task.Delay(delay); await hosts.DisableAsync("h1", Op); });
            await Task.WhenAll(relay, disable);
            var settled = await Scalar<bool>("select exists (select 1 from participant_settlements where attempt_id = @Attempt)", new { r.Attempt });
            if (settled)
            {
                Assert.IsType<ResultOutcome.Settled>((await relay));
                Assert.False(await Scalar<bool>("""
                    select exists (select 1 from participant_settlements s join mission_attempts a using (attempt_id)
                                   where s.attempt_id = @Attempt and a.credential_revoked_at is not null and a.credential_revoked_at < s.settled_at)
                    """, new { r.Attempt }));
            }
            else
            {
                Assert.True((await relay) is ResultOutcome.Unauthenticated or ResultOutcome.Refused { Code: "AttemptRevoked" }, $"round {round}: {(await relay)}");
                Assert.Equal(0L, await Scalar<long>("select count(*) from result_submissions where attempt_id = @Attempt", new { r.Attempt }));
            }
            await hosts.RotateSecretAsync("h1", Op);
            await hosts.SwitchAsync("h1", HostSwitch.Enable, Op);
            var again = await ManifestKit.SubmitAsync(store, db, body);
            // A replay never crosses the revocation: an attempt revoked live refuses it; one that settled before the disable was never
            // revoked (disable revokes live attempts only), so its settlement's answer stands.
            Assert.Equal(settled ? new ResultOutcome.Duplicate("success") : new ResultOutcome.Refused("AttemptRevoked"), again);
            await FreeSlotAsync(r);
        }
    }

    [Fact]
    public async Task A_disable_waiting_on_a_paused_settlement_follows_it_and_the_settlement_stays_final()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        const long Key = 910_001;
        await FaultTriggers.InstallPauseAsync(db, "participant_settlements", "insert", Key);
        Task<ResultOutcome> relay;
        Task disable;
        await using (await FaultTriggers.HoldAsync(db, Key))
        {
            relay = ManifestKit.SubmitAsync(ManifestKit.Store(db), db, ManifestKit.Body(ManifestKit.Manifest(r)));
            await FaultTriggers.WaitForPausedAsync(db, Key);
            disable = hosts.DisableAsync("h1", Op); // waits on the attempt row the paused settlement holds
            await AttemptFixtures.WaitForLockWaitersAsync(db, 2);
        }
        Assert.IsType<ResultOutcome.Settled>(await relay);
        await disable;
        await FaultTriggers.RemoveAsync(db, "participant_settlements");
        Assert.True(await Scalar<bool>("select s.settled_at <= coalesce(a.credential_revoked_at, 'infinity') from participant_settlements s join mission_attempts a using (attempt_id) where s.attempt_id = @Attempt", new { r.Attempt }));
    }

    [Fact]
    public async Task A_result_racing_the_infrastructure_void_ends_in_exactly_one_outcome()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var r = await ManifestKit.StartAsync(kit, db);
            var store = ManifestKit.Store(db);
            var relay = Task.Run(() => ManifestKit.SubmitAsync(store, db, ManifestKit.Body(ManifestKit.Manifest(r))));
            var exited = Task.Run(() => VoidReportAsync(r));
            await Task.WhenAll(relay, exited);
            var outcome = await Scalar<string>("select outcome from participant_settlements where attempt_id = @Attempt", new { r.Attempt });
            if (outcome == "infra_void")
            {
                Assert.Equal(new ExitReportResult.Applied("voided"), (await exited));
                Assert.Equal(new ResultOutcome.Refused("AlreadySettledAsVoid"), (await relay));
            }
            else
            {
                Assert.Equal("success", outcome);
                Assert.IsType<ResultOutcome.Settled>((await relay));
                Assert.Equal(new ExitReportResult.Applied("none"), (await exited)); // the submission (or the settlement) blocks the void
            }
            Assert.Equal(1L, await Scalar<long>("select count(*) from participant_settlements where attempt_id = @Attempt", new { r.Attempt }));
            await FreeSlotAsync(r);
        }
    }

    [Fact]
    public async Task A_catalogue_upgrade_during_missions_never_judges_a_result_under_the_new_catalogue()
    {
        var running = new List<ManifestKit.Running>();
        for (var i = 0; i < 6; i++) running.Add(await ManifestKit.StartAsync(kit, db, pool: [new(0, 0, "item.medkit", 2), new(0, 1, "item.servo-boots", 1)]));
        var upgrade = Task.Run(() => new CatalogueHistoryStore(db.AsApp()).RegisterAsync(CatalogueHistoryTests.Retiring(), null, default));
        var relays = running.Select(r => Task.Run(() => ManifestKit.SubmitAsync(ManifestKit.Store(db), db, ManifestKit.Body(ManifestKit.Manifest(r))))).ToList();
        await upgrade;
        var settledAll = await Task.WhenAll(relays);
        Assert.All(settledAll, x => Assert.IsType<ResultOutcome.Settled>(x));
        Assert.Equal(6L, await Scalar<long>("select count(*) from participant_settlements where catalogue_version = @v", new { v = GameCatalogue.Embedded.Version }));
    }

    [Fact]
    public async Task Repeated_identical_relays_in_parallel_settle_once()
    {
        var r = await ManifestKit.StartAsync(kit, db, pool: [new(0, 0, "item.medkit", 2), new(0, 1, "item.light-vest", 1)]);
        var body = ManifestKit.Manifest(r).ToCanonicalBytes();
        var relays = Enumerable.Range(0, 20).Select(_ => Task.Run(() => ManifestKit.SubmitAsync(ManifestKit.Store(db), db, ResultBody.Read(body)))).ToList();
        var results = await Task.WhenAll(relays);
        Assert.Single(results, x => x is ResultOutcome.Settled);
        Assert.Equal(19, results.Count(x => x is ResultOutcome.Duplicate));
        Assert.Equal((1L, 1L, 1L), await RowAsSuperuserAsync<(long, long, long)>(db, """
            select (select count(*) from participant_settlements where attempt_id = @Attempt),
                   (select count(*) from energy_ledger where kind = 'settle' and attempt_id = @Attempt),
                   (select count(*) from item_instances where origin = 'settlement')
            """, new { r.Attempt }));
        // A lost response: the caller never saw the answer and retries: the stored outcome and the same receipt.
        var receipt = await Scalar<string>("select receipt from participant_settlements where attempt_id = @Attempt", new { r.Attempt });
        Assert.Equal(new ResultOutcome.Duplicate("success"), await ManifestKit.SubmitAsync(ManifestKit.Store(db), db, ResultBody.Read(body)));
        Assert.Equal(receipt, await Scalar<string>("select receipt from participant_settlements where attempt_id = @Attempt", new { r.Attempt }));
    }

    // ---- further races ----

    [Fact]
    public async Task Two_different_manifests_in_parallel_settle_one_and_conflict_the_other()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        var a = Task.Run(() => ManifestKit.SubmitAsync(ManifestKit.Store(db), db, ManifestKit.Body(ManifestKit.Manifest(r, "success"))));
        var b = Task.Run(() => ManifestKit.SubmitAsync(ManifestKit.Store(db), db, ManifestKit.Body(ManifestKit.Manifest(r, "defeat"))));
        var both = await Task.WhenAll(a, b);
        Assert.Single(both, x => x is ResultOutcome.Settled);
        Assert.Single(both, x => x is ResultOutcome.Refused { Code: "Conflict" });
    }

    [Fact]
    public async Task A_relay_racing_an_operator_revalidation_settles_once()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        var store = ManifestKit.Store(db);
        Assert.IsType<ResultOutcome.Quarantined>(await ManifestKit.SubmitAsync(store, db, ManifestKit.Body(ManifestKit.Manifest(r) with { Integrity = "ledgerMismatch" })));
        var valid = ManifestKit.Body(ManifestKit.Manifest(r));
        Assert.IsType<ResultOutcome.Quarantined>(await ManifestKit.SubmitAsync(store, db, valid));
        var submission = await Scalar<long>("select max(submission_id) from result_submissions where attempt_id = @Attempt", new { r.Attempt });
        var recovery = new RecoveryStore(db.AsApp(), new CatalogueHistoryStore(db.AsApp()), SettlementOptions.Default);
        var revalidate = Task.Run(() => recovery.RevalidateAsync(r.Attempt, r.Account, submission, false, Op, default));
        var relay = Task.Run(() => ManifestKit.SubmitAsync(ManifestKit.Store(db), db, valid));
        await Task.WhenAll(revalidate, relay);
        Assert.Equal("settled", (await revalidate).Result);
        Assert.Equal(1L, await Scalar<long>("select count(*) from participant_settlements where attempt_id = @Attempt", new { r.Attempt }));
    }

    // ---- deterministic orderings (the owner's Stage 9 merge corrections, item 5) ----

    /// <summary>The persistent effects of one participant's settlement: operative XP, minted items, held stacks, settle entries
    /// and settlements. Two participants settled with the same manifest shape from the same starter have equal effects.</summary>
    Task<(long Xp, long Items, long Stacks, long SettleEntries, long Settlements)> EffectsAsync(ManifestKit.Running r) =>
        RowAsSuperuserAsync<(long, long, long, long, long)>(db, """
            select (select coalesce(sum(experience), 0) from operatives where account_id = @Account),
                   (select count(*) from item_instances where account_id = @Account and origin <> 'starter'),
                   (select coalesce(sum(quantity), 0) from item_stacks where account_id = @Account),
                   (select count(*) from energy_ledger where account_id = @Account and kind = 'settle'),
                   (select count(*) from participant_settlements where account_id = @Account)
            """, new { r.Account });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Two_relays_and_a_revalidation_in_a_forced_order_commit_exactly_one_settlement(bool revalidationFirst)
    {
        const long Key = 910_101;
        var control = await ManifestKit.StartAsync(kit, db);
        Assert.IsType<ResultOutcome.Settled>(await ManifestKit.SubmitAsync(ManifestKit.Store(db), db, ManifestKit.Body(ManifestKit.Manifest(control))));
        var r = await ManifestKit.StartAsync(kit, db);
        var store = ManifestKit.Store(db);
        var recovery = new RecoveryStore(db.AsApp(), new CatalogueHistoryStore(db.AsApp()), SettlementOptions.Default);
        var valid = ManifestKit.Manifest(r);
        var submission = 0L;
        if (revalidationFirst)
        {
            // Quarantined (sticky) with the valid manifest stored: the operator revalidates it.
            Assert.IsType<ResultOutcome.Quarantined>(await ManifestKit.SubmitAsync(store, db, ManifestKit.Body(valid with { Integrity = "ledgerMismatch" })));
            Assert.IsType<ResultOutcome.Quarantined>(await ManifestKit.SubmitAsync(store, db, ManifestKit.Body(valid)));
            submission = await Scalar<long>("select max(submission_id) from result_submissions where attempt_id = @Attempt", new { r.Attempt });
        }
        await FaultTriggers.InstallPauseAsync(db, "participant_settlements", "insert", Key);
        Task<RevalidateResult> revalidate;
        Task<ResultOutcome> same, different;
        await using (await FaultTriggers.HoldAsync(db, Key))
        {
            // The first writer holds the participant at its settlement insert; the other two queue behind its locks.
            if (revalidationFirst)
            {
                revalidate = Task.Run(() => recovery.RevalidateAsync(r.Attempt, r.Account, submission, false, Op, default));
                await FaultTriggers.WaitForPausedAsync(db, Key);
                same = Task.Run(() => ManifestKit.SubmitAsync(store, db, ManifestKit.Reencoded(valid)));
                different = Task.Run(() => ManifestKit.SubmitAsync(store, db, ManifestKit.Body(ManifestKit.Manifest(r, "defeat"))));
            }
            else
            {
                same = Task.Run(() => ManifestKit.SubmitAsync(store, db, ManifestKit.Body(valid)));
                await FaultTriggers.WaitForPausedAsync(db, Key);
                different = Task.Run(() => ManifestKit.SubmitAsync(store, db, ManifestKit.Body(ManifestKit.Manifest(r, "defeat"))));
                revalidate = Task.Run(() => recovery.RevalidateAsync(r.Attempt, r.Account, 1, false, Op, default));
            }
            await AttemptFixtures.WaitForLockWaitersAsync(db, 3);
        }
        if (revalidationFirst)
        {
            Assert.Equal("settled", (await revalidate).Result);
            Assert.Equal(new ResultOutcome.Duplicate("success"), await same);
        }
        else
        {
            Assert.IsType<ResultOutcome.Settled>(await same);
            Assert.Equal(RecoveryError.NotAllowed, (await Assert.ThrowsAsync<RecoveryException>(() => revalidate)).Error); // settled: nothing to revalidate
        }
        Assert.Equal(new ResultOutcome.Refused("Conflict"), await different);
        await FaultTriggers.RemoveAsync(db, "participant_settlements");
        Assert.Equal(await EffectsAsync(control), await EffectsAsync(r)); // one settlement's items, XP and energy, applied once
        var receipt = await Scalar<string>("select receipt from participant_settlements where attempt_id = @Attempt", new { r.Attempt });
        Assert.Equal(new ResultOutcome.Duplicate("success"), await ManifestKit.SubmitAsync(store, db, ManifestKit.Body(valid)));
        Assert.Equal(receipt, await Scalar<string>("select receipt from participant_settlements where attempt_id = @Attempt", new { r.Attempt }));
    }

    /// <summary>
    /// Lock order (the owner's item 5): each path that writes for a participant is paused while it holds every lock it takes,
    /// and a conflicting path then runs against the same account, host and attempt; both must finish (no deadlock, no lock
    /// timeout). Paths: a result relay (settle), an exit-evidence void (exited), an operator protective void (void), host
    /// disable and account suspension. The lock modes, and why no cycle can form, are in the stage plan's As built (§E).
    /// </summary>
    [Theory]
    [InlineData("settle", "disable")]
    [InlineData("disable", "settle")]
    [InlineData("exited", "disable")]
    [InlineData("disable", "exited")]
    [InlineData("void", "disable")]
    [InlineData("disable", "void")]
    [InlineData("settle", "exited")]
    [InlineData("exited", "settle")]
    [InlineData("settle", "suspend")]
    [InlineData("suspend", "settle")]
    [InlineData("exited", "suspend")]
    [InlineData("suspend", "exited")]
    [InlineData("void", "suspend")]
    [InlineData("suspend", "void")]
    public async Task A_path_holding_its_locks_and_a_conflicting_path_both_finish(string holder, string contender)
    {
        const long Key = 910_201;
        var r = await ManifestKit.StartAsync(kit, db);
        var recovery = new RecoveryStore(db.AsApp(), new CatalogueHistoryStore(db.AsApp()), SettlementOptions.Default);
        if (holder == "void" || contender == "void")
        {
            Assert.IsType<ResultOutcome.Quarantined>(await ManifestKit.SubmitAsync(ManifestKit.Store(db), db, ManifestKit.Body(ManifestKit.Manifest(r) with { BuildRevision = "r9" })));
            var bad = await Scalar<long>("select max(submission_id) from result_submissions where attempt_id = @Attempt", new { r.Attempt });
            Assert.Equal("failed", (await recovery.RevalidateAsync(r.Attempt, r.Account, bad, false, Op, default)).Result);
        }
        var accounts = new Blackglass.Accounts.Persistence.Accounts.AccountAdminStore(db.AsApp());
        Task Run(string path) => path switch
        {
            "settle" => ManifestKit.SubmitAsync(ManifestKit.Store(db), db, ManifestKit.Body(ManifestKit.Manifest(r))),
            "exited" => VoidReportAsync(r),
            "void" => recovery.VoidParticipantAsync(r.Attempt, r.Account, new VoidChecks(true, true, false, false), "outbox empty", Op, default),
            "disable" => hosts.DisableAsync("h1", Op),
            "suspend" => accounts.SuspendAsync(r.Account, Op),
            _ => throw new ArgumentOutOfRangeException(nameof(path)),
        };
        var table = holder is "disable" or "suspend" ? "operator_actions" : "participant_settlements"; // each path's last write
        await FaultTriggers.InstallPauseAsync(db, table, "insert", Key);
        Task held, other;
        await using (await FaultTriggers.HoldAsync(db, Key))
        {
            held = Task.Run(() => Run(holder));
            await FaultTriggers.WaitForPausedAsync(db, Key);
            other = Task.Run(() => Run(contender));
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!other.IsCompleted && await Scalar<long>("select count(*) from pg_stat_activity where datname = current_database() and wait_event_type = 'Lock'") < 2)
            {
                Assert.True(DateTime.UtcNow < deadline, "the contender neither finished nor waited");
                await Task.Delay(20);
            }
        }
        foreach (var t in new[] { held, other })
        {
            try { await t.WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (RecoveryException) { } // a refusal (settled, revoked) is an answer, not a lock failure; any database error fails the test
        }
        await FaultTriggers.RemoveAsync(db, table);
    }

    [Fact]
    public async Task An_inventory_edit_racing_a_settlement_is_locked_until_it_commits()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        var version = await Scalar<long>("select version from inventories where account_id = @Account", new { r.Account });
        var op = r.Snapshot.Operatives[0].OperativeId;
        var edits = new InventoryEditStore(db.AsApp(), new ParticipationLock(), GameCatalogue.Embedded);
        var relay = Task.Run(() => ManifestKit.SubmitAsync(ManifestKit.Store(db), db, ManifestKit.Body(ManifestKit.Manifest(r, collectAll: false))));
        var edit = Task.Run(async () =>
        {
            try { await edits.ApplyAsync(r.Account, version, [new MoveStack(op, "item.medkit", 1, Destination.Stash)], default); return "applied"; }
            catch (PlayerException e) { return e.Error.ToString(); }
        });
        await Task.WhenAll(relay, edit);
        Assert.IsType<ResultOutcome.Settled>((await relay));
        Assert.Contains((await edit), new[] { "InventoryLocked", "VersionMismatch" });
        var after = await Scalar<long>("select version from inventories where account_id = @Account", new { r.Account });
        await edits.ApplyAsync(r.Account, after, [new MoveStack(op, "item.medkit", 1, Destination.Stash)], default);
    }

    [Fact]
    public async Task Settlement_racing_an_energy_grant_and_a_policy_publication_does_not_deadlock()
    {
        var energy = new EnergyAdminStore(db.AsApp(), 1000);
        for (var round = 0; round < 10; round++)
        {
            var r = await ManifestKit.StartAsync(kit, db);
            await Task.WhenAll(
                Task.Run(() => ManifestKit.SubmitAsync(ManifestKit.Store(db), db, ManifestKit.Body(ManifestKit.Manifest(r)))),
                Task.Run(() => energy.GrantAsync(r.Account, 1, Guid.NewGuid(), Op)),
                Task.Run(() => energy.PublishPolicyAsync(60, 50, TimeSpan.FromMinutes(10), 1, 100, Op)));
            Assert.Equal("settled", await Scalar<string>("select state from attempt_participants where attempt_id = @Attempt", new { r.Attempt }));
            await FreeSlotAsync(r);
        }
    }

    [Fact]
    public async Task Every_account_referencing_path_racing_suspension_and_credential_rotation_does_not_deadlock()
    {
        // Thirteen concurrent transactions per round: a pool of their own (the shared test pool holds five connections).
        await using var wide = Npgsql.NpgsqlDataSource.Create(new Npgsql.NpgsqlConnectionStringBuilder
        {
            Host = db.Host, Port = int.Parse(db.EnvFor("app")["BLACKGLASS_DB_PORT"]!, System.Globalization.CultureInfo.InvariantCulture),
            Database = db.Name, Username = "accounts_app", Password = db.Password("app"), MaxPoolSize = 20, Timeout = 30,
        });
        var accounts = new Blackglass.Accounts.Persistence.Accounts.AccountAdminStore(wide);
        var settlements = new SettlementStore(wide, new CatalogueHistoryStore(wide), SettlementOptions.Default);
        var exits = new HostStore(wide, 30);
        var monitoring = new MonitoringStore(wide, MonitoringOptions.Default with { AutoDrainAttempts = 0 }); // the voids here would drain h1 by round 3
        for (var round = 0; round < 6; round++)
        {
            var settle = await ManifestKit.StartAsync(kit, db);
            var quarantine = await ManifestKit.StartAsync(kit, db);
            var mismatch = await ManifestKit.StartAsync(kit, db);
            var voided = await ManifestKit.StartAsync(kit, db);
            Assert.IsType<RecordedOutcome.Accepted>(await settlements.RecordedAsync(mismatch.Attempt, mismatch.Account, mismatch.Mission.Credential.Hash(),
                ResultManifest.HashOf(ManifestKit.Manifest(mismatch).ToCanonicalBytes()), default));
            var secret = await AttemptFixtures.HostSecretHashAsync(db, "h1");
            var work = new List<Task>
            {
                Task.Run(() => settlements.SubmitAsync("h1", secret, ManifestKit.Body(ManifestKit.Manifest(settle)), default)),
                Task.Run(() => settlements.SubmitAsync("h1", secret, ManifestKit.Body(ManifestKit.Manifest(quarantine) with { Integrity = "ledgerMismatch" }), default)),
                Task.Run(() => settlements.RecordedAsync(mismatch.Attempt, mismatch.Account, mismatch.Mission.Credential.Hash(), "sha256:" + new string('e', 64), default)),
                Task.Run(() => exits.ExitedAsync("h1", secret, voided.Attempt, new ExitedRequest(1, null, [new ExitResult(voided.Account, false)], true), default)),
                Task.Run(() => monitoring.SweepAsync(default)),
            };
            foreach (var r in new[] { settle, quarantine, mismatch, voided })
            {
                work.Add(Task.Run(() => accounts.SuspendAsync(r.Account, Op)));
                work.Add(Task.Run(() => accounts.RotateCredentialAsync(r.Account, Op)));
            }
            await Task.WhenAll(work);
            Assert.Equal(("settled", "quarantined", "quarantined", "settled"), await RowAsSuperuserAsync<(string, string, string, string)>(db, """
                select (select state from attempt_participants where attempt_id = @a1), (select state from attempt_participants where attempt_id = @a2),
                       (select state from attempt_participants where attempt_id = @a3), (select state from attempt_participants where attempt_id = @a4)
                """, new { a1 = settle.Attempt, a2 = quarantine.Attempt, a3 = mismatch.Attempt, a4 = voided.Attempt }));
            foreach (var r in new[] { settle, quarantine, mismatch, voided }) await FreeSlotAsync(r);
        }
    }
}
