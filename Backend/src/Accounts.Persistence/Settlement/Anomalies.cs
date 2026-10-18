using System.Text.Json;
using Dapper;
using Npgsql;

namespace Blackglass.Accounts.Persistence.Settlement;

/// <summary>
/// Stage 9's durable alerts (S9-B18): one <c>anomaly_events</c> row per finding, deduplicated while unreviewed by (kind,
/// dedupe_key). The detail never holds a payload, seed, salt, secret or credential: only ids, hashes, lengths and codes. A row
/// naming an account takes that accounts row FOR KEY SHARE through its foreign key, so every caller has already locked it
/// (plan §E, rev 3) before any attempt, participant, inventory or wallet lock.
/// </summary>
public static class Anomalies
{
    public const string ResultQuarantined = "result_quarantined";
    public const string ResultMalformed = "result_malformed";
    public const string ResultRefused = "result_refused";
    public const string ResultRefusedRevoked = "result_refused_revoked";
    public const string SettlementConflict = "settlement_conflict";
    public const string LateResultAfterVoid = "late_result_after_void";
    public const string ResultWithoutRecordedHash = "result_without_recorded_hash";
    public const string RecordedHashMismatch = "recorded_hash_mismatch";
    public const string InfraVoid = "infra_void";
    public const string ExitEvidenceInconclusive = "exit_evidence_inconclusive";
    public const string ExitEvidenceContradiction = "exit_evidence_contradiction";
    public const string ExitEvidencePostRevocation = "exit_evidence_post_revocation";
    public const string QuarantineEscalation = "quarantine_escalation";
    public const string UnresponsiveAttempt = "unresponsive_attempt";
    public const string RevokedAttemptUnsettled = "revoked_attempt_unsettled";
    public const string EndingAttemptStalled = "ending_attempt_stalled";
    public const string CatalogueUnavailable = "catalogue_unavailable";
    public const string SubmissionLimit = "submission_limit";
    public const string HostAutoDrained = "host_auto_drained";
    public const string SettlementFailure = "settlement_failure";

    /// <summary>Raises <paramref name="kind"/> once per <paramref name="dedupeKey"/> while unreviewed; returns whether this call inserted it.</summary>
    public static async Task<bool> RaiseAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string kind, string dedupeKey, object detail, CancellationToken ct,
        long? accountId = null, string? hostId = null, string? buildRevision = null, Guid? attemptId = null) =>
        await conn.ExecuteAsync(new CommandDefinition(
            """
            insert into anomaly_events (kind, account_id, host_id, build_revision, attempt_id, dedupe_key, detail)
            values (@kind, @accountId, @hostId, @buildRevision, @attemptId, @dedupeKey, @detail::jsonb)
            on conflict (kind, dedupe_key) where reviewed_at is null do nothing
            """, new { kind, accountId, hostId, buildRevision, attemptId, dedupeKey, detail = JsonSerializer.Serialize(detail) }, tx, cancellationToken: ct)) == 1;
}
