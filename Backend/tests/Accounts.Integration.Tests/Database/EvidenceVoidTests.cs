using Dapper;
using Blackglass.Accounts.Domain.Attempts;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Attempts;
using Blackglass.Accounts.Persistence.Hosts;
using Blackglass.Accounts.Persistence.Settlement;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>The positive-evidence void (S9-B7; revision 3: the supplementary outbox observation) and the late result after a void:
/// only an exit status plus a complete outbox scan listing a started, unsettled participant absent, with no recorded hash or
/// submission, on an unrevoked attempt, voids; nothing less does.</summary>
[Trait("Requires", "Postgres")]
public class EvidenceVoidTests : IAsyncLifetime
{
    TestDatabase db = null!;
    MissionKit kit = null!;
    SettlementStore store = null!;

    public async Task InitializeAsync()
    {
        db = await MigratedAsync();
        kit = await MissionKit.CreateAsync(db);
        store = ManifestKit.Store(db);
    }

    public async Task DisposeAsync()
    {
        try { Assert.Empty(await InvariantFailuresAsync(db)); }
        finally { await db.DisposeAsync(); }
    }

    async Task<ExitReportResult> Exited(ManifestKit.Running r, int? code, string? signal, bool? complete, params (long Account, bool Present)[] results) =>
        await kit.Hosts.ExitedAsync("h1", await AttemptFixtures.HostSecretHashAsync(db, "h1"), r.Attempt,
            new ExitedRequest(code, signal, [.. results.Select(x => new ExitResult(x.Account, x.Present))], complete ?? false), default);

    Task<T> Scalar<T>(string sql, object? args = null) => ScalarAsSuperuserAsync<T>(db, sql, args);

    async Task<string> ParticipantState(ManifestKit.Running r, long? account = null) =>
        await Scalar<string>("select state from attempt_participants where attempt_id = @Attempt and account_id = @a", new { r.Attempt, a = account ?? r.Account });

    async Task<bool> AnomalyAsync(string kind, Guid attempt) =>
        await Scalar<bool>("select exists (select 1 from anomaly_events where kind = @kind and attempt_id = @attempt)", new { kind, attempt });

    /// <summary>A second, started participant in <paramref name="r"/>'s attempt (co-op shaped, by direct SQL: admission refuses
    /// co-op until it is authorized): a zero-cost hold, charged, and a snapshot of its first operative with an empty bag.</summary>
    async Task<long> AddStartedParticipantAsync(ManifestKit.Running r)
    {
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
        return account;
    }

    [Fact]
    public async Task A_complete_scan_with_an_exit_status_listing_the_participant_absent_voids_it()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        var inventory = await ManifestKit.InventoryAsync(db, r.Account);
        var version = await Scalar<long>("select version from inventories where account_id = @Account", new { r.Account });
        Assert.Equal(new ExitReportResult.Applied("voided"), await Exited(r, 139, null, true, (r.Account, false)));
        Assert.Equal("settled", await ParticipantState(r));
        Assert.Equal("refunded", await Scalar<string>("select state from energy_holds where attempt_id = @Attempt", new { r.Attempt }));
        Assert.Equal((1L, 10), await RowAsSuperuserAsync<(long, int)>(db, 
            "select count(*), coalesce(sum(delta), 0)::int from energy_ledger where kind = 'void_refund' and attempt_id = @Attempt", new { r.Attempt }));
        Assert.Equal(50, await Scalar<int>("select balance from energy_wallets where account_id = @Account", new { r.Account }));
        Assert.Equal(inventory, await ManifestKit.InventoryAsync(db, r.Account)); // nothing consumed, granted or re-equipped
        Assert.Equal(version + 1, await Scalar<long>("select version from inventories where account_id = @Account", new { r.Account }));
        Assert.Equal(("infra_void", "exit_evidence"), await RowAsSuperuserAsync<(string, string)>(db, 
            "select outcome, void_basis from participant_settlements where attempt_id = @Attempt", new { r.Attempt }));
        var receipt = await Scalar<string>("select receipt from participant_settlements where attempt_id = @Attempt", new { r.Attempt });
        Assert.Contains("\"outcome\":\"infra_void\"", receipt);
        Assert.Contains("\"energy\":{\"cost\":10,\"refunded\":10,\"state\":\"refunded\"}", receipt);
        Assert.True(await AnomalyAsync("infra_void", r.Attempt));
        Assert.Equal("voided", await Scalar<string>("select outcome from attempt_exit_reports where attempt_id = @Attempt", new { r.Attempt }));
        Assert.Equal("settled", await Scalar<string>("select state from mission_attempts where attempt_id = @Attempt", new { r.Attempt }));
        // A late result after the void: AlreadySettledAsVoid, stored, alerted, the void receipt unchanged.
        Assert.Equal(new ResultOutcome.Refused("AlreadySettledAsVoid"), await ManifestKit.SubmitAsync(store, db, ManifestKit.Body(ManifestKit.Manifest(r))));
        Assert.Equal("late_after_void", await Scalar<string>("select disposition from result_submissions where attempt_id = @Attempt", new { r.Attempt }));
        Assert.True(await AnomalyAsync("late_result_after_void", r.Attempt));
        Assert.Equal(receipt, await Scalar<string>("select receipt from participant_settlements where attempt_id = @Attempt", new { r.Attempt }));
    }

    [Theory]
    [InlineData(1, null, null)]     // outbox absent
    [InlineData(1, null, false)]    // an incomplete scan
    [InlineData(null, null, true)]  // no exit status
    public async Task Anything_less_than_proof_voids_nothing_and_alerts(int? code, string? signal, bool? complete)
    {
        var r = await ManifestKit.StartAsync(kit, db);
        Assert.Equal(new ExitReportResult.Applied("none"), await Exited(r, code, signal, complete, (r.Account, false)));
        Assert.Equal("joined", await ParticipantState(r));
        Assert.True(await AnomalyAsync("exit_evidence_inconclusive", r.Attempt));
        Assert.True(await Scalar<bool>("select slot_released_at is not null from mission_attempts where attempt_id = @Attempt", new { r.Attempt }));
        Assert.Equal(0L, await Scalar<long>("select count(*) from participant_settlements"));
    }

    [Fact]
    public async Task A_participant_missing_from_an_incomplete_list_is_not_proven_absent_and_a_complete_list_must_name_it()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        Assert.IsType<ExitReportResult.IncompleteResults>(await Exited(r, 1, null, true));
        Assert.Equal(0L, await Scalar<long>("select count(*) from attempt_exit_reports"));
        Assert.Equal(new ExitReportResult.Applied("none"), await Exited(r, 1, null, false));
        Assert.Equal("joined", await ParticipantState(r));
        Assert.True(await AnomalyAsync("exit_evidence_inconclusive", r.Attempt));
    }

    [Fact]
    public async Task A_recorded_hash_or_a_stored_submission_against_an_absence_is_a_contradiction_not_a_void()
    {
        var recorded = await ManifestKit.StartAsync(kit, db);
        await store.RecordedAsync(recorded.Attempt, recorded.Account, recorded.Mission.Credential.Hash(),
            Blackglass.Accounts.Domain.Settlement.ResultManifest.HashOf(ManifestKit.Manifest(recorded).ToCanonicalBytes()), default);
        Assert.Equal(new ExitReportResult.Applied("none"), await Exited(recorded, 1, null, true, (recorded.Account, false)));
        Assert.Equal("result_pending", await ParticipantState(recorded));
        Assert.True(await AnomalyAsync("exit_evidence_contradiction", recorded.Attempt));

        var submitted = await ManifestKit.StartAsync(kit, db);
        Assert.IsType<ResultOutcome.Quarantined>(await ManifestKit.SubmitAsync(store, db, ManifestKit.Body(ManifestKit.Manifest(submitted) with { Integrity = "ledgerMismatch" })));
        Assert.Equal(new ExitReportResult.Applied("none"), await Exited(submitted, 1, null, true, (submitted.Account, false)));
        Assert.Equal("quarantined", await ParticipantState(submitted));
        Assert.True(await AnomalyAsync("exit_evidence_contradiction", submitted.Attempt));
    }

    [Fact]
    public async Task Present_means_result_pending_and_mixed_presence_voids_only_the_absent_participant()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        var other = await AddStartedParticipantAsync(r);
        Assert.Equal(new ExitReportResult.Applied("voided"), await Exited(r, 0, null, true, (r.Account, true), (other, false)));
        Assert.Equal("result_pending", await ParticipantState(r));
        Assert.Equal("settled", await ParticipantState(r, other));
        Assert.Equal("ending", await Scalar<string>("select state from mission_attempts where attempt_id = @Attempt", new { r.Attempt }));
        // Crash before the hash notification: no recorded call, the relay settles with 'absent', and nothing voids it.
        Assert.IsType<ResultOutcome.Settled>(await ManifestKit.SubmitAsync(store, db, ManifestKit.Body(ManifestKit.Manifest(r))));
        Assert.Equal("absent", await Scalar<string>("select recorded_hash from participant_settlements where attempt_id = @Attempt and account_id = @Account", new { r.Attempt, r.Account }));
        Assert.Equal("settled", await Scalar<string>("select state from mission_attempts where attempt_id = @Attempt", new { r.Attempt }));
    }

    [Fact]
    public async Task A_repeat_is_idempotent_and_a_different_report_conflicts_and_voids_nothing()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        Assert.Equal(new ExitReportResult.Applied("none"), await Exited(r, 0, null, true, (r.Account, true)));
        Assert.Equal(new ExitReportResult.Applied("none"), await Exited(r, 0, null, true, (r.Account, true)));
        Assert.IsType<ExitReportResult.Conflicting>(await Exited(r, 0, null, true, (r.Account, false)));
        Assert.Equal("result_pending", await ParticipantState(r));
    }

    [Fact]
    public async Task An_incomplete_scan_can_be_completed_later_by_one_immutable_observation()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        Assert.Equal(new ExitReportResult.Applied("none"), await Exited(r, 137, null, false, (r.Account, false)));
        Assert.Equal("joined", await ParticipantState(r));
        Assert.Equal(new ExitReportResult.Applied("voided"), await Exited(r, 137, null, true, (r.Account, false)));
        Assert.Equal("settled", await ParticipantState(r));
        Assert.Equal(("none", false), await RowAsSuperuserAsync<(string, bool)>(db, 
            "select outcome, outbox_complete from attempt_exit_reports where attempt_id = @Attempt", new { r.Attempt })); // the first report is unchanged
        Assert.Equal("voided", await Scalar<string>("select outcome from attempt_outbox_observations where attempt_id = @Attempt", new { r.Attempt }));
        Assert.Equal(new ExitReportResult.Applied("voided"), await Exited(r, 137, null, true, (r.Account, false)));
        Assert.IsType<ExitReportResult.Conflicting>(await Exited(r, 137, null, true, (r.Account, true)));
        Assert.IsType<ExitReportResult.Conflicting>(await Exited(r, 1, null, true, (r.Account, false)));
    }

    [Fact]
    public async Task An_observation_contradicting_the_first_reports_presence_voids_nothing()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        Assert.Equal(new ExitReportResult.Applied("none"), await Exited(r, 0, null, false, (r.Account, true)));
        Assert.Equal("result_pending", await ParticipantState(r));
        Assert.Equal(new ExitReportResult.Applied("none"), await Exited(r, 0, null, true, (r.Account, false)));
        Assert.Equal("result_pending", await ParticipantState(r));
        Assert.True(await AnomalyAsync("exit_evidence_contradiction", r.Attempt));
    }

    [Fact]
    public async Task A_revoked_attempts_report_is_stored_frees_the_slot_and_voids_nothing()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        var admin = new HostAdminStore(db.AsApp());
        await admin.DisableAsync("h1", Op);
        await admin.RotateSecretAsync("h1", Op);
        await admin.SwitchAsync("h1", HostSwitch.Enable, Op);
        Assert.Equal(new ExitReportResult.Applied("none"), await Exited(r, 1, null, true, (r.Account, false)));
        Assert.True(await Scalar<bool>("select attempt_revoked from attempt_exit_reports where attempt_id = @Attempt", new { r.Attempt }));
        Assert.True(await Scalar<bool>("select slot_released_at is not null from mission_attempts where attempt_id = @Attempt", new { r.Attempt }));
        Assert.Equal("joined", await ParticipantState(r));
        Assert.True(await AnomalyAsync("exit_evidence_post_revocation", r.Attempt));
        // A later observation of the revoked attempt voids nothing either.
        Assert.Equal(0L, await Scalar<long>("select count(*) from participant_settlements"));
    }

    [Fact]
    public async Task An_unresponsive_attempt_without_evidence_is_never_voided_by_the_worker()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        await ExecuteAsSuperuserAsync(db, "update mission_attempts set last_process_heartbeat_at = clock_timestamp() - interval '1 hour' where attempt_id = @Attempt", new { r.Attempt });
        var attempts = new AttemptStore(db.AsApp(), processLeaseSeconds: 30);
        await attempts.SweepAsync(10);
        await attempts.SweepAsync(10);
        Assert.Equal("unresponsive", await Scalar<string>("select state from mission_attempts where attempt_id = @Attempt", new { r.Attempt }));
        Assert.Equal("joined", await ParticipantState(r));
        Assert.Equal("charged", await Scalar<string>("select state from energy_holds where attempt_id = @Attempt", new { r.Attempt }));
        Assert.Equal(0L, await Scalar<long>("select count(*) from participant_settlements"));
    }
}
