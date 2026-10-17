using System.Text;
using Dapper;
using Blackglass.Accounts.Domain.Ids;
using Blackglass.Accounts.Domain.Settlement;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Catalogue;
using Blackglass.Accounts.Persistence.Hosts;
using Blackglass.Accounts.Persistence.Settlement;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>The operator recovery verbs at the store (S9-B8, S9-B9; revision 3 R8 targeted recovery and T0-3): revalidate,
/// import-result, both participant-void modes, declare-lost, attempt show and the anomaly review.</summary>
[Trait("Requires", "Postgres")]
public class RecoveryTests : IAsyncLifetime
{
    TestDatabase db = null!;
    MissionKit kit = null!;
    SettlementStore store = null!;
    RecoveryStore recovery = null!;

    public async Task InitializeAsync()
    {
        db = await MigratedAsync();
        kit = await MissionKit.CreateAsync(db);
        store = ManifestKit.Store(db);
        recovery = new RecoveryStore(db.AsApp(), new CatalogueHistoryStore(db.AsApp()), SettlementOptions.Default);
    }

    public async Task DisposeAsync()
    {
        try { Assert.Empty(await InvariantFailuresAsync(db)); }
        finally { await db.DisposeAsync(); }
    }

    Task<T> Scalar<T>(string sql, object? args = null) => ScalarAsSuperuserAsync<T>(db, sql, args);

    Task<ResultOutcome> Relay(ResultManifest m) => ManifestKit.SubmitAsync(store, db, ManifestKit.Body(m));

    async Task<long> LatestSubmissionAsync(ManifestKit.Running r) =>
        await Scalar<long>("select max(submission_id) from result_submissions where attempt_id = @Attempt", new { r.Attempt });

    Task<string> StateAsync(ManifestKit.Running r) => Scalar<string>("select state from attempt_participants where attempt_id = @Attempt", new { r.Attempt });

    async Task RevokeAsync(bool reenable = true)
    {
        var admin = new HostAdminStore(db.AsApp());
        await admin.DisableAsync("h1", Op);
        if (!reenable) return;
        await admin.RotateSecretAsync("h1", Op);
        await admin.SwitchAsync("h1", HostSwitch.Enable, Op);
    }

    // ---- revalidate ----

    [Fact]
    public async Task Revalidate_records_a_failure_without_change_and_settles_a_stored_submission_that_passes()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        Assert.IsType<ResultOutcome.Quarantined>(await Relay(ManifestKit.Manifest(r) with { BuildRevision = "r9" }));
        var bad = await LatestSubmissionAsync(r);
        var failed = await recovery.RevalidateAsync(r.Attempt, r.Account, bad, false, Op, default);
        Assert.Equal("failed", failed.Result);
        Assert.Equal(["V4.build"], failed.Codes);
        Assert.Equal("quarantined", await StateAsync(r));
        // A valid manifest arrives while quarantined: stored, not settled (S9-B21); the operator revalidates it.
        Assert.IsType<ResultOutcome.Quarantined>(await Relay(ManifestKit.Manifest(r)));
        var good = await LatestSubmissionAsync(r);
        Assert.Equal("settled", (await recovery.RevalidateAsync(r.Attempt, r.Account, good, false, Op, default)).Result);
        Assert.Equal("settled", await StateAsync(r));
        Assert.Equal(good, await Scalar<long>("select submission_id from participant_settlements where attempt_id = @Attempt", new { r.Attempt }));
        Assert.Equal(new[] { "failed", "settled" }, (await (await db.AsSuperuser().OpenConnectionAsync()).QueryAsync<string>(
            "select result ->> 'result' from operator_actions where action = 'revalidate' and attempt_id = @Attempt order by id", new { r.Attempt })).ToArray());
        var e = await Assert.ThrowsAsync<RecoveryException>(() => recovery.RevalidateAsync(r.Attempt, r.Account, good, false, Op, default));
        Assert.Equal(RecoveryError.NotAllowed, e.Error);
    }

    [Fact]
    public async Task Revalidate_refuses_another_participants_submission()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        var other = await ManifestKit.StartAsync(kit, db);
        Assert.IsType<ResultOutcome.Quarantined>(await Relay(ManifestKit.Manifest(other) with { Integrity = "ledgerMismatch" }));
        Assert.IsType<ResultOutcome.Quarantined>(await Relay(ManifestKit.Manifest(r) with { Integrity = "ledgerMismatch" }));
        var otherSubmission = await LatestSubmissionAsync(other);
        var e = await Assert.ThrowsAsync<RecoveryException>(() => recovery.RevalidateAsync(r.Attempt, r.Account, otherSubmission, false, Op, default));
        Assert.Equal(RecoveryError.NotAllowed, e.Error);
    }

    // ---- import-result ----

    [Fact]
    public async Task A_revoked_attempts_result_is_imported_with_host_trust_reviewed_then_settled_by_revalidation()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        await RevokeAsync();
        var file = ManifestKit.Manifest(r).ToCanonicalBytes();
        var missing = await Assert.ThrowsAsync<RecoveryException>(() => recovery.ImportResultAsync(r.Attempt, r.Account, file, "outbox copy", false, Op, default));
        Assert.Equal(RecoveryError.MissingCheck, missing.Error);
        var imported = await recovery.ImportResultAsync(r.Attempt, r.Account, file, "outbox copy", true, Op, default);
        Assert.True(imported.Inserted);
        Assert.Equal(("quarantined", "host_revoked"), await (await db.AsSuperuser().OpenConnectionAsync()).QuerySingleAsync<(string, string)>(
            "select state, quarantine_reason from attempt_participants where attempt_id = @Attempt", new { r.Attempt }));
        Assert.Equal(("operator_import", true), await (await db.AsSuperuser().OpenConnectionAsync()).QuerySingleAsync<(string, bool)>(
            "select source, attempt_revoked from result_submissions where submission_id = @SubmissionId", imported));
        Assert.False((await recovery.ImportResultAsync(r.Attempt, r.Account, file, "outbox copy", true, Op, default)).Inserted);
        Assert.Equal(1L, await Scalar<long>("select count(*) from result_submissions where attempt_id = @Attempt", new { r.Attempt }));
        Assert.Equal(RecoveryError.MissingCheck, (await Assert.ThrowsAsync<RecoveryException>(() => recovery.RevalidateAsync(r.Attempt, r.Account, imported.SubmissionId, false, Op, default))).Error);
        Assert.Equal("settled", (await recovery.RevalidateAsync(r.Attempt, r.Account, imported.SubmissionId, true, Op, default)).Result);
    }

    [Fact]
    public async Task An_import_works_past_the_cap_and_only_stores_evidence_for_a_settled_participant()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        var capped = ManifestKit.Store(db, SettlementOptions.Default with { SubmissionsPerParticipant = 1 });
        Assert.IsType<ResultOutcome.Quarantined>(await ManifestKit.SubmitAsync(capped, db, ManifestKit.Body(ManifestKit.Manifest(r) with { Integrity = "ledgerMismatch" })));
        Assert.Equal(new ResultOutcome.Refused("SubmissionLimit"), await ManifestKit.SubmitAsync(capped, db, ManifestKit.Body(ManifestKit.Manifest(r))));
        var imported = await recovery.ImportResultAsync(r.Attempt, r.Account, ManifestKit.Manifest(r).ToCanonicalBytes(), "beyond the cap", false, Op, default);
        Assert.True(imported.Inserted);
        Assert.Equal("quarantined", imported.Disposition);

        var settled = await ManifestKit.StartAsync(kit, db);
        Assert.IsType<ResultOutcome.Settled>(await Relay(ManifestKit.Manifest(settled)));
        var receipt = await Scalar<string>("select receipt from participant_settlements where attempt_id = @Attempt", new { settled.Attempt });
        var evidence = await recovery.ImportResultAsync(settled.Attempt, settled.Account, ManifestKit.Manifest(settled, "defeat").ToCanonicalBytes(), "investigation", false, Op, default);
        Assert.Equal("conflict", evidence.Disposition);
        Assert.True(await Scalar<bool>("select exists (select 1 from anomaly_events where kind = 'settlement_conflict' and attempt_id = @Attempt)", new { settled.Attempt }));
        Assert.Equal(receipt, await Scalar<string>("select receipt from participant_settlements where attempt_id = @Attempt", new { settled.Attempt }));
    }

    [Fact]
    public async Task Attempt_show_marks_evidence_and_matching_hashes_and_prints_no_secret()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        var m = ManifestKit.Manifest(r);
        await store.RecordedAsync(r.Attempt, r.Account, r.Mission.Credential.Hash(), ResultManifest.HashOf(m.ToCanonicalBytes()), default);
        await RevokeAsync();
        var imported = await recovery.ImportResultAsync(r.Attempt, r.Account, m.ToCanonicalBytes(), "outbox copy", true, Op, default);
        var lines = await recovery.ShowAsync(r.Attempt, null, false, default);
        var row = Assert.Single(lines, l => l.StartsWith($"submission.{imported.SubmissionId}=", StringComparison.Ordinal));
        Assert.Contains("evidence=post-revocation", row);
        Assert.Contains("matchesRecorded=true", row);
        Assert.DoesNotContain(lines, l => l.Contains("rawBase64", StringComparison.Ordinal));
        var salt = await Scalar<byte[]>("select retention_salt from attempt_secrets where attempt_id = @Attempt", new { r.Attempt });
        var seed = await Scalar<int>("select seed from attempt_secrets where attempt_id = @Attempt", new { r.Attempt });
        var all = string.Join('\n', lines);
        Assert.DoesNotContain(Convert.ToHexStringLower(salt), all, StringComparison.Ordinal);
        Assert.DoesNotContain($"seed={seed}", all, StringComparison.Ordinal);
        Assert.DoesNotContain(r.Mission.Credential.Text, all, StringComparison.Ordinal);
        var withRaw = await recovery.ShowAsync(r.Attempt, imported.SubmissionId, true, default);
        Assert.Contains($"submission.{imported.SubmissionId}.rawBase64={Convert.ToBase64String(m.ToCanonicalBytes())}", withRaw);
    }

    // ---- participant void ----

    [Fact]
    public async Task A_quarantine_mode_void_needs_its_checks_and_a_failed_revalidation()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        var all = new VoidChecks(true, true, false, false);
        Assert.Equal(RecoveryError.NotAllowed, (await Assert.ThrowsAsync<RecoveryException>(() => recovery.VoidParticipantAsync(r.Attempt, r.Account, all, "outbox empty", Op, default))).Error); // not quarantined
        Assert.IsType<ResultOutcome.Quarantined>(await Relay(ManifestKit.Manifest(r) with { BuildRevision = "r9" }));
        Assert.Equal(RecoveryError.MissingCheck, (await Assert.ThrowsAsync<RecoveryException>(() =>
            recovery.VoidParticipantAsync(r.Attempt, r.Account, all with { OutboxChecked = false }, "outbox empty", Op, default))).Error);
        Assert.Equal(RecoveryError.MissingCheck, (await Assert.ThrowsAsync<RecoveryException>(() =>
            recovery.VoidParticipantAsync(r.Attempt, r.Account, all with { Revalidated = false }, "outbox empty", Op, default))).Error);
        Assert.Equal(RecoveryError.NotAllowed, (await Assert.ThrowsAsync<RecoveryException>(() =>
            recovery.VoidParticipantAsync(r.Attempt, r.Account, all, "outbox empty", Op, default))).Error); // no revalidation recorded yet
        await recovery.RevalidateAsync(r.Attempt, r.Account, await LatestSubmissionAsync(r), false, Op, default);
        Assert.Equal(new VoidResult("operator_quarantine", 10), await recovery.VoidParticipantAsync(r.Attempt, r.Account, all, "outbox empty", Op, default));
        Assert.Equal("settled", await StateAsync(r));
        Assert.Equal("quarantine", await Scalar<string>("select checks ->> 'mode' from operator_actions where action = 'participant_void' and attempt_id = @Attempt", new { r.Attempt }));
        Assert.Equal(RecoveryError.NotAllowed, (await Assert.ThrowsAsync<RecoveryException>(() => recovery.VoidParticipantAsync(r.Attempt, r.Account, all, "again", Op, default))).Error);
    }

    [Fact]
    public async Task A_revoked_attempts_participant_is_voided_in_revoked_mode_without_declaring_the_host_lost()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        await RevokeAsync(reenable: false);
        var checks = new VoidChecks(true, false, true, true);
        Assert.Equal(RecoveryError.MissingCheck, (await Assert.ThrowsAsync<RecoveryException>(() =>
            recovery.VoidParticipantAsync(r.Attempt, r.Account, checks with { HostTrustReviewed = false }, "outbox lost", Op, default))).Error);
        Assert.Equal(RecoveryError.MissingCheck, (await Assert.ThrowsAsync<RecoveryException>(() =>
            recovery.VoidParticipantAsync(r.Attempt, r.Account, checks with { Unrecoverable = false }, "outbox lost", Op, default))).Error);
        Assert.Equal(new VoidResult("operator_revoked_unrecoverable", 10), await recovery.VoidParticipantAsync(r.Attempt, r.Account, checks, "outbox lost", Op, default));
        Assert.Null(await Scalar<DateTime?>("select declared_lost_at from mission_hosts where host_id = 'h1'"));
        // The host is recovered and re-enabled, never declared lost.
        var admin = new HostAdminStore(db.AsApp());
        await admin.RotateSecretAsync("h1", Op);
        await admin.SwitchAsync("h1", HostSwitch.Enable, Op);
        Assert.True(await Scalar<bool>("select enabled from mission_hosts where host_id = 'h1'"));
    }

    [Fact]
    public async Task A_revoked_attempts_participant_with_a_submission_needs_a_failed_revalidation_first()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        Assert.IsType<ResultOutcome.Quarantined>(await Relay(ManifestKit.Manifest(r) with { BuildRevision = "r9" }));
        await RevokeAsync(reenable: false);
        var checks = new VoidChecks(true, true, true, true);
        Assert.Equal(RecoveryError.NotAllowed, (await Assert.ThrowsAsync<RecoveryException>(() => recovery.VoidParticipantAsync(r.Attempt, r.Account, checks, "outbox lost", Op, default))).Error);
        Assert.Equal("failed", (await recovery.RevalidateAsync(r.Attempt, r.Account, await LatestSubmissionAsync(r), true, Op, default)).Result);
        Assert.Equal("operator_revoked_unrecoverable", (await recovery.VoidParticipantAsync(r.Attempt, r.Account, checks, "outbox lost", Op, default)).Basis);
    }

    // ---- declare-lost ----

    [Fact]
    public async Task Declare_lost_needs_a_disabled_silent_host_then_voids_its_started_participants_once()
    {
        var running = await ManifestKit.StartAsync(kit, db);
        var quarantined = await ManifestKit.StartAsync(kit, db);
        Assert.IsType<ResultOutcome.Quarantined>(await Relay(ManifestKit.Manifest(quarantined) with { Integrity = "ledgerMismatch" }));
        Assert.Equal(RecoveryError.NotAllowed, (await Assert.ThrowsAsync<RecoveryException>(() => recovery.DeclareLostAsync("h1", true, "machine gone", Op, default))).Error); // enabled
        await RevokeAsync(reenable: false);
        Assert.Equal(RecoveryError.NotAllowed, (await Assert.ThrowsAsync<RecoveryException>(() => recovery.DeclareLostAsync("h1", true, "machine gone", Op, default))).Error); // fresh heartbeat
        await ExecuteAsSuperuserAsync(db, "update mission_hosts set last_heartbeat_at = clock_timestamp() - interval '11 minutes' where host_id = 'h1'");
        Assert.Equal(RecoveryError.MissingCheck, (await Assert.ThrowsAsync<RecoveryException>(() => recovery.DeclareLostAsync("h1", false, "machine gone", Op, default))).Error);
        var lost = await recovery.DeclareLostAsync("h1", true, "machine gone", Op, default);
        Assert.True(lost.DeclaredNow);
        Assert.Equal([(running.Attempt, running.Account)], lost.Voided);
        Assert.Equal("quarantined", await StateAsync(quarantined));
        Assert.Equal("host_declared_lost", await Scalar<string>("select void_basis from participant_settlements where attempt_id = @Attempt", new { running.Attempt }));
        var again = await recovery.DeclareLostAsync("h1", true, "machine gone", Op, default);
        Assert.False(again.DeclaredNow);
        Assert.Empty(again.Voided);
        var admin = new HostAdminStore(db.AsApp());
        await Assert.ThrowsAnyAsync<Exception>(() => admin.RotateSecretAsync("h1", Op));
        await Assert.ThrowsAnyAsync<Exception>(() => admin.SwitchAsync("h1", HostSwitch.Enable, Op));
    }

    // ---- anomalies ----

    [Fact]
    public async Task An_anomaly_is_listed_and_reviewed_once_with_an_audit_row()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        Assert.IsType<ResultOutcome.Quarantined>(await Relay(ManifestKit.Manifest(r) with { Integrity = "ledgerMismatch" }));
        var anomalies = new AnomalyStore(db.AsApp());
        var open = await anomalies.ListAsync(true, default);
        var q = Assert.Single(open, a => a.Kind == "result_quarantined");
        await anomalies.ReviewAsync(q.Id, "inspected the outbox", Op, default);
        Assert.DoesNotContain(await anomalies.ListAsync(true, default), a => a.Id == q.Id);
        Assert.Equal(RecoveryError.NotAllowed, (await Assert.ThrowsAsync<RecoveryException>(() => anomalies.ReviewAsync(q.Id, "again", Op, default))).Error);
        Assert.Equal(q.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            await Scalar<string>("select result ->> 'anomalyId' from operator_actions where action = 'anomaly_review'"));
        _ = Encoding.UTF8;
        _ = Base58Id.Alphabet;
    }
}
