using Dapper;
using Blackglass.Accounts.Domain.Attempts;
using Blackglass.Accounts.Domain.Settlement;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Attempts;
using Blackglass.Accounts.Persistence.Catalogue;
using Blackglass.Accounts.Persistence.Hosts;
using Blackglass.Accounts.Persistence.Settlement;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>The monitoring sweeps (S9-B18): alerts repeat once per period and never change a state, hold, inventory or lock; the
/// auto-drain counts distinct attempts with exit-evidence voids, only drains, and never disables, revokes, voids or refunds.</summary>
[Trait("Requires", "Postgres")]
public class MonitoringTests : IAsyncLifetime
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

    MonitoringStore Monitor(MonitoringOptions? options = null) => new(db.AsApp(), options ?? MonitoringOptions.Default);

    Task<T> Scalar<T>(string sql, object? args = null) => ScalarAsSuperuserAsync<T>(db, sql, args);

    /// <summary>Everything a sweep must never change: participants, holds, inventories, items, stacks, wallets, hosts' switches.</summary>
    Task<string> DigestAsync() => Scalar<string>("""
        select md5(
          (select coalesce(string_agg(attempt_id::text || account_id || state || coalesce(quarantine_reason, ''), ',' order by attempt_id, account_id), '') from attempt_participants) ||
          (select coalesce(string_agg(attempt_id::text || account_id || state, ',' order by attempt_id, account_id), '') from energy_holds) ||
          (select coalesce(string_agg(account_id::text || version, ',' order by account_id), '') from inventories) ||
          (select coalesce(string_agg(item_id::text || coalesce(operative_id::text, '') || coalesce(equipped_slot, ''), ',' order by item_id), '') from item_instances) ||
          (select coalesce(string_agg(account_id::text || coalesce(operative_id::text, '') || definition_id || quantity, ',' order by 1), '') from item_stacks) ||
          (select coalesce(string_agg(account_id::text || balance, ',' order by account_id), '') from energy_wallets) ||
          (select coalesce(string_agg(host_id || enabled || secret_revoked, ',' order by host_id), '') from mission_hosts) ||
          (select count(*)::text from participant_settlements))
        """);

    [Fact]
    public async Task A_quarantine_escalates_once_per_period_and_never_changes_anything()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        Assert.IsType<ResultOutcome.Quarantined>(await ManifestKit.SubmitAsync(ManifestKit.Store(db), db, ManifestKit.Body(ManifestKit.Manifest(r) with { Integrity = "ledgerMismatch" })));
        var digest = await DigestAsync();
        for (var period = 1; period <= 3; period++)
        {
            await ExecuteAsSuperuserAsync(db, "update attempt_participants set escalate_at = clock_timestamp() - interval '1 second' where attempt_id = @Attempt", new { r.Attempt });
            Assert.Equal(1, (await Monitor().SweepAsync(default)).Escalations);
            Assert.Equal(0, (await Monitor().SweepAsync(default)).Escalations);
            Assert.True(await Scalar<bool>("select escalate_at > clock_timestamp() + interval '23 hours' from attempt_participants where attempt_id = @Attempt", new { r.Attempt }));
            Assert.Equal((long)period, await Scalar<long>("select count(*) from anomaly_events where kind = 'quarantine_escalation' and attempt_id = @Attempt", new { r.Attempt }));
        }
        Assert.Equal(digest, await DigestAsync());
    }

    [Fact]
    public async Task Unresponsive_and_revoked_attempts_alert_once_per_period_and_stay_as_they_are()
    {
        var silent = await ManifestKit.StartAsync(kit, db);
        await ExecuteAsSuperuserAsync(db, "update mission_attempts set state = 'unresponsive', last_process_heartbeat_at = clock_timestamp() - interval '11 minutes' where attempt_id = @Attempt", new { silent.Attempt });
        var digest = await DigestAsync();
        Assert.Equal(1, (await Monitor().SweepAsync(default)).Unresponsive);
        Assert.Equal(0, (await Monitor().SweepAsync(default)).Unresponsive);
        await ExecuteAsSuperuserAsync(db, "update mission_attempts set last_process_heartbeat_at = clock_timestamp() - interval '21 minutes' where attempt_id = @Attempt", new { silent.Attempt });
        Assert.Equal(1, (await Monitor().SweepAsync(default)).Unresponsive);
        Assert.Equal("unresponsive", await Scalar<string>("select state from mission_attempts where attempt_id = @Attempt", new { silent.Attempt }));
        Assert.Equal(digest, await DigestAsync());

        var revoked = await ManifestKit.StartAsync(kit, db);
        await new HostAdminStore(db.AsApp()).DisableAsync("h1", Op);
        var afterDisable = await DigestAsync();
        Assert.Equal(2, (await Monitor().SweepAsync(default)).Revoked); // both attempts on the disabled host are revoked and unsettled
        Assert.Equal(0, (await Monitor().SweepAsync(default)).Revoked);
        Assert.Equal(afterDisable, await DigestAsync());
        Assert.Equal("joined", await Scalar<string>("select state from attempt_participants where attempt_id = @Attempt", new { revoked.Attempt }));
    }

    async Task VoidByEvidenceAsync(ManifestKit.Running r) =>
        Assert.Equal(new ExitReportResult.Applied("voided"), await kit.Hosts.ExitedAsync("h1", await AttemptFixtures.HostSecretHashAsync(db, "h1"), r.Attempt,
            new ExitedRequest(1, null, [new ExitResult(r.Account, false)], true), default));

    [Fact]
    public async Task Three_distinct_voided_attempts_drain_the_host_which_keeps_running_its_attempts()
    {
        var running = await ManifestKit.StartAsync(kit, db);
        var voided = new List<ManifestKit.Running>();
        for (var i = 0; i < 3; i++) voided.Add(await ManifestKit.StartAsync(kit, db));
        for (var i = 0; i < 2; i++) await VoidByEvidenceAsync(voided[i]);
        Assert.Empty((await Monitor().SweepAsync(default)).Drained);
        Assert.Empty((await Monitor(MonitoringOptions.Default with { AutoDrainAttempts = 0 }).SweepAsync(default)).Drained);
        await VoidByEvidenceAsync(voided[2]);
        Assert.Empty((await Monitor(MonitoringOptions.Default with { AutoDrainAttempts = 0 }).SweepAsync(default)).Drained); // 0 never drains
        var before = await DigestAsync();
        Assert.Equal(["h1"], (await Monitor().SweepAsync(default)).Drained);
        Assert.Equal(before, await DigestAsync()); // still enabled, unrevoked; nothing voided or refunded
        Assert.True(await Scalar<bool>("select drained and enabled and not secret_revoked from mission_hosts where host_id = 'h1'"));
        Assert.True(await Scalar<bool>("select exists (select 1 from anomaly_events where kind = 'host_auto_drained' and host_id = 'h1')"));
        // Drain semantics: the running attempt's process and the host's agent go on.
        Assert.IsType<ProcessHeartbeatResult.Ok>(await kit.Processes.HeartbeatAsync(running.Attempt, new ProcessHeartbeatRequest(running.Mission.Session, "play", []), default));
        Assert.IsType<ResultOutcome.Settled>(await ManifestKit.SubmitAsync(ManifestKit.Store(db), db, ManifestKit.Body(ManifestKit.Manifest(running))));
        Assert.Empty((await Monitor().SweepAsync(default)).Drained);
        // The operator's re-enable sticks: voids before it are not counted again.
        await new HostAdminStore(db.AsApp()).SwitchAsync("h1", HostSwitch.Enable, Op);
        Assert.Empty((await Monitor().SweepAsync(default)).Drained);
        Assert.False(await Scalar<bool>("select drained from mission_hosts where host_id = 'h1'"));
    }

    /// <summary>A started attempt whose agent reported the process gone with the participant's result present (complete scan):
    /// the participant is result_pending and the attempt ending (S9-B5), waiting for the relay.</summary>
    async Task<ManifestKit.Running> EndingAsync()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        Assert.IsType<ExitReportResult.Applied>(await kit.Hosts.ExitedAsync("h1", await AttemptFixtures.HostSecretHashAsync(db, "h1"), r.Attempt,
            new ExitedRequest(0, null, [new ExitResult(r.Account, true)], true), default));
        Assert.Equal("ending", await Scalar<string>("select state from mission_attempts where attempt_id = @Attempt", new { r.Attempt }));
        return r;
    }

    Task BackdateEndingAsync(ManifestKit.Running r, int minutes) => ExecuteAsSuperuserAsync(db, $"""
        begin; set local session_replication_role = replica;
        update mission_attempts set ending_at = clock_timestamp() - interval '{minutes} minutes' where attempt_id = @Attempt;
        commit
        """, new { r.Attempt });

    [Fact]
    public async Task Ending_at_is_stamped_when_the_attempt_enters_ending_and_the_app_cannot_change_it()
    {
        var running = await ManifestKit.StartAsync(kit, db);
        Assert.Null(await Scalar<DateTime?>("select ending_at from mission_attempts where attempt_id = @Attempt", new { running.Attempt }));
        var r = await EndingAsync();
        var stamped = await Scalar<DateTime?>("select ending_at from mission_attempts where attempt_id = @Attempt", new { r.Attempt });
        Assert.NotNull(stamped);
        await using (var app = await db.AsApp().OpenConnectionAsync())
        {
            try { await app.ExecuteAsync("update mission_attempts set ending_at = clock_timestamp() - interval '1 day' where attempt_id = @Attempt", new { r.Attempt }); }
            catch (Npgsql.PostgresException e) when (e.SqlState == "42501") { } // either refused or kept: never rewritten
        }
        Assert.Equal(stamped, await Scalar<DateTime?>("select ending_at from mission_attempts where attempt_id = @Attempt", new { r.Attempt }));
    }

    [Fact]
    public async Task A_stalled_ending_attempt_alerts_after_ten_minutes_then_hourly_and_never_changes_anything()
    {
        var r = await EndingAsync();
        var digest = await DigestAsync();
        await BackdateEndingAsync(r, 9);
        Assert.Equal(0, (await Monitor().SweepAsync(default)).Stalled);
        await BackdateEndingAsync(r, 11);
        Assert.Equal(1, (await Monitor().SweepAsync(default)).Stalled);
        Assert.Equal(0, (await Monitor().SweepAsync(default)).Stalled); // deduplicated within the interval
        await BackdateEndingAsync(r, 69);
        Assert.Equal(0, (await Monitor().SweepAsync(default)).Stalled);
        await BackdateEndingAsync(r, 71);
        Assert.Equal(1, (await Monitor().SweepAsync(default)).Stalled); // the hourly repeat
        Assert.Equal(0, (await Monitor().SweepAsync(default)).Stalled);
        var (host, elapsed, unsettled) = await RowAsSuperuserAsync<(string, long, int)>(db, """
            select host_id, (detail ->> 'elapsedSeconds')::bigint, (detail ->> 'unsettled')::int from anomaly_events
            where kind = 'ending_attempt_stalled' and attempt_id = @Attempt order by id desc limit 1
            """, new { r.Attempt });
        Assert.Equal(("h1", 1), (host, unsettled));
        Assert.InRange(elapsed, 71 * 60, 72 * 60);
        Assert.Equal(2L, await Scalar<long>("select count(*) from anomaly_events where kind = 'ending_attempt_stalled' and attempt_id = @Attempt", new { r.Attempt }));
        Assert.Equal(digest, await DigestAsync()); // no state, hold, inventory, wallet or host changed
        Assert.Equal(("ending", "result_pending"), await RowAsSuperuserAsync<(string, string)>(db,
            "select a.state, p.state from mission_attempts a join attempt_participants p using (attempt_id) where a.attempt_id = @Attempt", new { r.Attempt }));
        // Recovery: the relay arrives, the attempt leaves ending, and the alerts stop.
        Assert.IsType<ResultOutcome.Settled>(await ManifestKit.SubmitAsync(ManifestKit.Store(db), db, ManifestKit.Body(ManifestKit.Manifest(r))));
        Assert.Equal(0, (await Monitor().SweepAsync(default)).Stalled);
        Assert.Equal(2L, await Scalar<long>("select count(*) from anomaly_events where kind = 'ending_attempt_stalled' and attempt_id = @Attempt", new { r.Attempt }));
    }

    [Fact]
    public async Task Concurrent_sweeps_raise_one_stalled_ending_alert_per_interval()
    {
        var r = await EndingAsync();
        await BackdateEndingAsync(r, 11);
        var sweeps = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => Monitor().SweepAsync(default))));
        Assert.Equal(1, sweeps.Sum(s => s.Stalled));
        Assert.Equal(1L, await Scalar<long>("select count(*) from anomaly_events where kind = 'ending_attempt_stalled' and attempt_id = @Attempt", new { r.Attempt }));
    }

    [Fact]
    public async Task An_attempt_unresponsive_before_its_first_heartbeat_alerts_from_its_start()
    {
        var silent = await ManifestKit.StartAsync(kit, db);
        await ExecuteAsSuperuserAsync(db, """
            begin; set local session_replication_role = replica;
            update mission_attempts set state = 'unresponsive', last_process_heartbeat_at = null, started_at = clock_timestamp() - interval '11 minutes' where attempt_id = @Attempt;
            commit
            """, new { silent.Attempt });
        Assert.Equal(1, (await Monitor().SweepAsync(default)).Unresponsive);
        Assert.Equal(0, (await Monitor().SweepAsync(default)).Unresponsive);
    }

    [Fact]
    public async Task Voided_participants_of_one_attempt_count_once()
    {
        // Co-op shaped (direct SQL: admission refuses co-op): two started participants of one attempt, both voided by evidence.
        var r = await ManifestKit.StartAsync(kit, db);
        var account = (await kit.Accounts.CreateAccountAsync(null, null, Op)).AccountId;
        var operative = await Scalar<long>("select min(operative_id) from operatives where account_id = @account", new { account });
        var version = await Scalar<long>("select version from inventories where account_id = @account", new { account });
        var snapshot = new LoadoutSnapshot(account, version, MissionKit.Catalogue.Version,
            [new SnapshotOperative(0, operative, "operative.x", 0, 0, [], [], [], new SnapshotEquipment(null, null, null))]).ToJson();
        await ExecuteAsSuperuserAsync(db, """
            insert into attempt_participants (attempt_id, account_id, template_id, template_version, loadout_snapshot, inventory_version, energy_cost)
            values (@Attempt, @account, 'procedural-standard', 1, @snapshot::jsonb, @version, 0);
            insert into energy_holds (attempt_id, account_id, amount) values (@Attempt, @account, 0);
            update attempt_participants set state = 'joined', started_at = clock_timestamp() where attempt_id = @Attempt and account_id = @account;
            update energy_holds set state = 'charged' where attempt_id = @Attempt and account_id = @account;
            """, new { r.Attempt, account, snapshot, version });
        Assert.Equal(new ExitReportResult.Applied("voided"), await kit.Hosts.ExitedAsync("h1", await AttemptFixtures.HostSecretHashAsync(db, "h1"), r.Attempt,
            new ExitedRequest(1, null, [new ExitResult(r.Account, false), new ExitResult(account, false)], true), default));
        Assert.Equal(2L, await Scalar<long>("select count(*) from participant_settlements where void_basis = 'exit_evidence'"));
        Assert.Empty((await Monitor(MonitoringOptions.Default with { AutoDrainAttempts = 2 }).SweepAsync(default)).Drained);
    }

    [Fact]
    public async Task Operator_voids_are_not_counted()
    {
        var recovery = new RecoveryStore(db.AsApp(), new CatalogueHistoryStore(db.AsApp()), SettlementOptions.Default);
        for (var i = 0; i < 3; i++)
        {
            var r = await ManifestKit.StartAsync(kit, db);
            Assert.IsType<ResultOutcome.Quarantined>(await ManifestKit.SubmitAsync(ManifestKit.Store(db), db, ManifestKit.Body(ManifestKit.Manifest(r) with { Integrity = "ledgerMismatch" })));
            await recovery.RevalidateAsync(r.Attempt, r.Account, await Scalar<long>("select max(submission_id) from result_submissions where attempt_id = @Attempt", new { r.Attempt }), false, Op, default);
            await recovery.VoidParticipantAsync(r.Attempt, r.Account, new VoidChecks(true, true, false, false), "outbox empty", Op, default);
        }
        Assert.Empty((await Monitor().SweepAsync(default)).Drained);
        Assert.False(await Scalar<bool>("select drained from mission_hosts where host_id = 'h1'"));
    }

    [Fact]
    public async Task Two_concurrent_sweeps_raise_one_alert_per_period()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        Assert.IsType<ResultOutcome.Quarantined>(await ManifestKit.SubmitAsync(ManifestKit.Store(db), db, ManifestKit.Body(ManifestKit.Manifest(r) with { Integrity = "ledgerMismatch" })));
        await ExecuteAsSuperuserAsync(db, """
            update attempt_participants set escalate_at = clock_timestamp() - interval '1 second' where attempt_id = @Attempt;
            update mission_attempts set state = 'unresponsive', last_process_heartbeat_at = clock_timestamp() - interval '11 minutes' where attempt_id = @Attempt;
            """, new { r.Attempt });
        await Task.WhenAll(Monitor().SweepAsync(default), Monitor().SweepAsync(default));
        Assert.Equal(1L, await Scalar<long>("select count(*) from anomaly_events where kind = 'quarantine_escalation'"));
        Assert.Equal(1L, await Scalar<long>("select count(*) from anomaly_events where kind = 'unresponsive_attempt'"));
    }
}
