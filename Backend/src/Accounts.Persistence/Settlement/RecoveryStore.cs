using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;
using Blackglass.Accounts.Domain.Ids;
using Blackglass.Accounts.Domain.Settlement;
using Blackglass.Accounts.Persistence.Accounts;
using Blackglass.Accounts.Persistence.Catalogue;

namespace Blackglass.Accounts.Persistence.Settlement;

public enum RecoveryError { NotFound, NotAllowed, MissingCheck }

/// <summary>A refused recovery verb: nothing changed. The message names the rule, never a payload, secret or salt.</summary>
public sealed class RecoveryException(RecoveryError error, string message) : Exception(message)
{
    public RecoveryError Error { get; } = error;
}

public sealed record RevalidateResult(string Result, IReadOnlyList<string> Codes, string? ManifestHash);

public sealed record ImportResult(long SubmissionId, bool Inserted, string Disposition);

/// <summary>What the operator attests before a participant void (S9-B9, revision 3).</summary>
public sealed record VoidChecks(bool OutboxChecked, bool Revalidated, bool HostTrustReviewed, bool Unrecoverable);

public sealed record VoidResult(string Basis, int Refunded);

public sealed record DeclareLostResult(bool DeclaredNow, IReadOnlyList<(Guid Attempt, long Account)> Voided);

/// <summary>
/// The confirmed, audited operator recovery verbs (S9-B8, S9-B9; revision 3 R8 targeted recovery, T0-3): re-validation of a
/// stored submission, result import, the participant void (quarantine mode, and revoked mode with basis
/// operator_revoked_unrecoverable), the host-loss declaration and the read-only attempt view. Every change writes its
/// operator_actions row in the same transaction, takes accounts FOR KEY SHARE before the attempt and participant (§E), and is
/// never retried. No HTTP route reaches any of them.
/// </summary>
public sealed class RecoveryStore(NpgsqlDataSource db, CatalogueHistoryStore catalogues, SettlementOptions options)
{
    /// <summary>How long a disabled host's heartbeat must have been silent before it may be declared lost (provisional, §19).</summary>
    public const int DeclareLostStaleMinutes = 10;

    readonly SettlementStore settlement = new(db, catalogues, options);

    async Task<(NpgsqlConnection Conn, NpgsqlTransaction Tx, SettlementStore.AttemptRow Attempt, SettlementStore.ParticipantRow Participant)> LockAsync(
        Guid attemptId, long accountId, CancellationToken ct)
    {
        var conn = await DbFailure.OpenAsync(db, ct);
        try
        {
            var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            if (!await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                    "select exists (select 1 from attempt_participants where attempt_id = @attemptId and account_id = @accountId)", new { attemptId, accountId }, tx, cancellationToken: ct)))
                throw new RecoveryException(RecoveryError.NotFound, "No such participant.");
            await SettlementStore.LockAccountAsync(conn, tx, accountId, ct);
            var attempt = await SettlementStore.LockAttemptAsync(conn, tx, attemptId, ct);
            var participant = await SettlementStore.LockParticipantAsync(conn, tx, attemptId, accountId, ct);
            return (conn, tx, attempt, participant);
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    static Task AuditAsync(NpgsqlConnection conn, NpgsqlTransaction tx, OperatorContext op, string action, Guid? attemptId, long? accountId, string? hostId,
        object checks, object result, CancellationToken ct) =>
        conn.ExecuteAsync(new CommandDefinition(
            """
            insert into operator_actions (correlation_id, operator, action, attempt_id, account_id, host_id, reason, checks, result)
            values (@correlationId, @operator, @action, @attemptId, @accountId, @hostId, @reason, @checks::jsonb, @result::jsonb)
            """, new { correlationId = op.CorrelationId, @operator = op.Operator, action, attemptId, accountId, hostId, reason = op.Reason,
                checks = JsonSerializer.Serialize(checks), result = JsonSerializer.Serialize(result) }, tx, cancellationToken: ct));

    /// <summary>
    /// Re-runs validation (V4-V16) on the stored canonical bytes of <paramref name="submissionId"/>, a submission of this
    /// participant, under the attempt's admission catalogue; the host is not re-authenticated (the operator path). A pass settles
    /// through the route's own transaction code; a failure records "failed" with its codes and changes nothing else. A revoked
    /// attempt, or a host_revoked quarantine, needs <paramref name="hostTrustReviewed"/>. A settled participant is refused.
    /// </summary>
    public async Task<RevalidateResult> RevalidateAsync(Guid attemptId, long accountId, long submissionId, bool hostTrustReviewed, OperatorContext op, CancellationToken ct)
    {
        var (conn, tx, attempt, participant) = await LockAsync(attemptId, accountId, ct);
        await using var _ = conn;
        await using var __ = tx;
        if (participant.State is "settled" or "admitted" or "released")
            throw new RecoveryException(RecoveryError.NotAllowed, $"The participant is {participant.State}: only a started, unsettled participant is revalidated.");
        var reason = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "select quarantine_reason from attempt_participants where attempt_id = @attemptId and account_id = @accountId", new { attemptId, accountId }, tx, cancellationToken: ct));
        if ((attempt.Revoked || reason == "host_revoked") && !hostTrustReviewed)
            throw new RecoveryException(RecoveryError.MissingCheck, "This attempt's credentials were revoked: confirm the host's trust was reviewed (--host-trust-reviewed).");
        var row = await conn.QuerySingleOrDefaultAsync<(string? ManifestHash, byte[]? Canonical)?>(new CommandDefinition(
            """
            select s.manifest_hash, x.canonical_bytes from result_submissions s join result_submission_payloads x using (submission_id)
            where s.submission_id = @submissionId and s.attempt_id = @attemptId and s.account_id = @accountId
            """, new { submissionId, attemptId, accountId }, tx, cancellationToken: ct))
            ?? throw new RecoveryException(RecoveryError.NotAllowed, "The submission is not this participant's.");

        IReadOnlyList<string> codes;
        ParseResult.Parsed? parsed = null;
        SettlementContext? context = null;
        if (row.Canonical is null) codes = ["malformed"];
        else if ((parsed = ResultManifest.Parse(row.Canonical) as ParseResult.Parsed) is null) codes = ["malformed"];
        else if (await catalogues.LookupLockedAsync(conn, tx, attempt.CatalogueVersion, ct) is not CatalogueLookup.Found { Catalogue: var admission }) codes = ["catalogue_unavailable"];
        else
        {
            var link = new SettlementStore.Link(attemptId, accountId);
            context = await settlement.ContextAsync(conn, tx, link, attempt, participant, admission, ct);
            codes = ManifestValidator.Validate(parsed.Manifest, parsed.ManifestHash, context);
            if (codes.Count == 0)
            {
                var hold = await conn.QuerySingleAsync<(int Amount, string State)>(new CommandDefinition(
                    "select amount, state from energy_holds where attempt_id = @attemptId and account_id = @accountId for update", new { attemptId, accountId }, tx, cancellationToken: ct));
                var version = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                    "select version from inventories where account_id = @accountId for update", new { accountId }, tx, cancellationToken: ct));
                if (version != participant.InventoryVersion) codes = ["V16.inventory_base"];
                else
                {
                    await settlement.ApplySettlementAsync(conn, tx, link, attempt, participant, context, parsed.Manifest, parsed.ManifestHash, submissionId, hold.Amount, ct);
                    await AuditAsync(conn, tx, op, "revalidate", attemptId, accountId, null, new { submissionId, hostTrustReviewed },
                        new { result = "settled", submissionId, manifestHash = parsed.ManifestHash }, ct);
                    await tx.CommitAsync(ct);
                    return new RevalidateResult("settled", [], parsed.ManifestHash);
                }
            }
        }
        await AuditAsync(conn, tx, op, "revalidate", attemptId, accountId, null, new { submissionId, hostTrustReviewed },
            new { result = "failed", submissionId, codes, manifestHash = row.ManifestHash }, ct);
        await tx.CommitAsync(ct);
        return new RevalidateResult("failed", codes, row.ManifestHash);
    }

    /// <summary>
    /// Stores an operator-retrieved outbox file as evidence (source operator_import; T0-3): for any started participant, never
    /// counted against the submission cap. A revoked attempt needs <paramref name="hostTrustReviewed"/>. An unsettled participant
    /// is quarantined (host_revoked for a revoked attempt, else operator_import) and never settled by the import itself; for a
    /// settled one the file is only stored (same_as or conflict, with an anomaly). The same file twice adds no row.
    /// </summary>
    public async Task<ImportResult> ImportResultAsync(Guid attemptId, long accountId, byte[] raw, string evidence, bool hostTrustReviewed, OperatorContext op, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (raw.Length > ResultManifest.MaxBytes) throw new RecoveryException(RecoveryError.NotAllowed, "The file is larger than a manifest may be (64 KiB).");
        var (conn, tx, attempt, participant) = await LockAsync(attemptId, accountId, ct);
        await using var _ = conn;
        await using var __ = tx;
        if (attempt.Revoked && !hostTrustReviewed)
            throw new RecoveryException(RecoveryError.MissingCheck, "This attempt's credentials were revoked: confirm the host's trust was reviewed (--host-trust-reviewed).");
        if (participant.State is "admitted" or "released")
            throw new RecoveryException(RecoveryError.NotAllowed, $"The participant is {participant.State}: only a started participant has a result to import.");
        var body = ResultBody.Read(raw);
        var parsed = body.Parsed as ParseResult.Parsed;
        if (parsed is not null && (parsed.Manifest.AttemptId != attemptId || parsed.Manifest.AccountId != accountId))
            throw new RecoveryException(RecoveryError.NotAllowed, "The file names another attempt or participant.");
        var rawHash = SHA256.HashData(raw);
        var existing = await conn.QuerySingleOrDefaultAsync<(long Id, string Disposition)?>(new CommandDefinition(
            "select submission_id, disposition from result_submissions where attempt_id = @attemptId and account_id = @accountId and raw_hash = @rawHash",
            new { attemptId, accountId, rawHash }, tx, cancellationToken: ct));
        if (existing is { } e)
        {
            await AuditAsync(conn, tx, op, "result_import", attemptId, accountId, null, new { hostTrustReviewed, evidence },
                new { result = "already_stored", submissionId = e.Id, rawHash = Convert.ToHexStringLower(rawHash) }, ct);
            await tx.CommitAsync(ct);
            return new ImportResult(e.Id, false, e.Disposition);
        }
        var link = new SettlementStore.Link(attemptId, accountId);
        if (parsed is null)
        {
            var code = ((ParseResult.Malformed)body.Parsed).Code;
            await SettlementStore.InsertSubmissionAsync(conn, tx, "operator_import", null, link, attemptId, accountId, attempt.Revoked, body, rawHash, null, "malformed", null, [code], ct);
            if (participant.State is "joined" or "left" or "result_pending") await settlement.QuarantineAsync(conn, tx, link, attempt.Revoked ? "host_revoked" : "operator_import", ct);
            await Anomalies.RaiseAsync(conn, tx, Anomalies.ResultMalformed, $"import:{attemptId}:{accountId}:{Convert.ToHexStringLower(rawHash)}", new { source = "operator_import", code }, ct,
                accountId: accountId, attemptId: attemptId);
        }
        else
            await settlement.DecideLinkedAsync(conn, tx, "operator_import", null, link, attempt, participant, body, rawHash, parsed, false, ct, importReason: op.Reason);
        var stored = await conn.QuerySingleAsync<(long Id, string Disposition)>(new CommandDefinition(
            "select submission_id, disposition from result_submissions where attempt_id = @attemptId and account_id = @accountId and raw_hash = @rawHash",
            new { attemptId, accountId, rawHash }, tx, cancellationToken: ct));
        await AuditAsync(conn, tx, op, "result_import", attemptId, accountId, null, new { hostTrustReviewed, evidence },
            new { result = "stored", submissionId = stored.Id, disposition = stored.Disposition, rawHash = Convert.ToHexStringLower(rawHash), manifestHash = parsed?.ManifestHash }, ct);
        await tx.CommitAsync(ct);
        return new ImportResult(stored.Id, true, stored.Disposition);
    }

    /// <summary>
    /// The operator-authorized protective void (S9-B9; revision 3). Always: <c>--outbox-checked</c> and evidence; a settled
    /// participant is refused (nothing overwritten); with a stored submission, <c>--revalidated</c> and a revalidate action
    /// with result "failed" recorded after the latest submission. Quarantine mode (attempt not revoked): the participant must
    /// be quarantined; basis operator_quarantine. Revoked mode (attempt revoked): the participant started and is unsettled, and
    /// <c>--host-trust-reviewed</c> and <c>--unrecoverable</c> are attested; basis operator_revoked_unrecoverable; the host
    /// need not be declared lost.
    /// </summary>
    public async Task<VoidResult> VoidParticipantAsync(Guid attemptId, long accountId, VoidChecks checks, string evidence, OperatorContext op, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(checks);
        if (!checks.OutboxChecked) throw new RecoveryException(RecoveryError.MissingCheck, "Check the host's outbox and relay state first, then attest it (--outbox-checked).");
        if (string.IsNullOrWhiteSpace(evidence)) throw new RecoveryException(RecoveryError.MissingCheck, "Record the findings (--evidence).");
        var (conn, tx, attempt, participant) = await LockAsync(attemptId, accountId, ct);
        await using var _ = conn;
        await using var __ = tx;
        if (participant.State is "settled") throw new RecoveryException(RecoveryError.NotAllowed, "The participant is already settled: a void never overwrites a settlement.");
        if (participant.State is "admitted" or "released") throw new RecoveryException(RecoveryError.NotAllowed, $"The participant is {participant.State}: it never started.");
        string basis;
        if (attempt.Revoked)
        {
            if (!checks.HostTrustReviewed) throw new RecoveryException(RecoveryError.MissingCheck, "Review the host's trust and recovery status first (--host-trust-reviewed).");
            if (!checks.Unrecoverable) throw new RecoveryException(RecoveryError.MissingCheck, "Attest that the result is verified unrecoverable or can't be accepted safely (--unrecoverable).");
            basis = "operator_revoked_unrecoverable";
        }
        else
        {
            if (participant.State != "quarantined") throw new RecoveryException(RecoveryError.NotAllowed, "Only a quarantined participant of an unrevoked attempt is voided by an operator.");
            basis = "operator_quarantine";
        }
        var submissions = (await conn.QueryAsync<(long Id, string? Hash, DateTime Received)>(new CommandDefinition(
            "select submission_id, manifest_hash, first_received_at from result_submissions where attempt_id = @attemptId and account_id = @accountId order by submission_id",
            new { attemptId, accountId }, tx, cancellationToken: ct))).ToList();
        if (submissions.Count > 0)
        {
            if (!checks.Revalidated) throw new RecoveryException(RecoveryError.MissingCheck, "A submission is stored: revalidate it first and attest it (--revalidated).");
            var latest = submissions.Max(s => s.Received);
            if (!await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                    """
                    select exists (select 1 from operator_actions where action = 'revalidate' and attempt_id = @attemptId and account_id = @accountId
                                   and result ->> 'result' = 'failed' and created_at >= @latest)
                    """, new { attemptId, accountId, latest }, tx, cancellationToken: ct)))
                throw new RecoveryException(RecoveryError.NotAllowed, "No failed revalidation is recorded after the latest submission.");
        }
        var refunded = await VoidStore.VoidParticipantLockedAsync(conn, tx, attemptId, accountId, attempt.State, basis, ct);
        await AuditAsync(conn, tx, op, "participant_void", attemptId, accountId, null,
            new { mode = attempt.Revoked ? "revoked" : "quarantine", checks.OutboxChecked, checks.Revalidated, checks.HostTrustReviewed, checks.Unrecoverable, evidence,
                submissions = submissions.Select(s => new { submissionId = s.Id, manifestHash = s.Hash }) },
            new { basis, refunded }, ct);
        await Anomalies.RaiseAsync(conn, tx, Anomalies.InfraVoid, $"{attemptId}:{accountId}", new { basis, correlationId = op.CorrelationId }, ct, accountId: accountId, attemptId: attemptId);
        await tx.CommitAsync(ct);
        return new VoidResult(basis, refunded);
    }

    /// <summary>
    /// The host-loss declaration (S9-B8), for confirmed permanent host or outbox loss: the host must be disabled with its secret
    /// revoked, silent for <see cref="DeclareLostStaleMinutes"/> minutes, with <c>--outbox-unrecoverable</c> and evidence. The
    /// stamp (permanent) and its audit commit alone (the host lock is never held with an attempt lock, R7); then each started,
    /// unsettled, unquarantined participant of the host's attempts is voided in its own transaction (basis host_declared_lost,
    /// one audit row each under the same correlation id). A re-run resumes. Quarantined participants keep their own recovery.
    /// </summary>
    public async Task<DeclareLostResult> DeclareLostAsync(string hostId, bool outboxUnrecoverable, string evidence, OperatorContext op, CancellationToken ct)
    {
        if (!outboxUnrecoverable) throw new RecoveryException(RecoveryError.MissingCheck, "Attest that the host's outbox is unrecoverable (--outbox-unrecoverable).");
        if (string.IsNullOrWhiteSpace(evidence)) throw new RecoveryException(RecoveryError.MissingCheck, "Record the evidence (--evidence).");
        bool declaredNow;
        await using (var conn = await DbFailure.OpenAsync(db, ct))
        await using (var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct))
        {
            var host = await conn.QuerySingleOrDefaultAsync<(bool Enabled, bool Revoked, bool Fresh, DateTime? Lost)?>(new CommandDefinition(
                """
                select enabled, secret_revoked, coalesce(last_heartbeat_at > clock_timestamp() - make_interval(mins => @stale), false), declared_lost_at
                from mission_hosts where host_id = @hostId and kind = 'agent' for update
                """, new { hostId, stale = DeclareLostStaleMinutes }, tx, cancellationToken: ct))
                ?? throw new RecoveryException(RecoveryError.NotFound, "No such agent host.");
            if (host.Lost is null)
            {
                if (host.Enabled || !host.Revoked) throw new RecoveryException(RecoveryError.NotAllowed, "Only a disabled host with a revoked secret is declared lost (host disable first).");
                if (host.Fresh) throw new RecoveryException(RecoveryError.NotAllowed, $"The host heartbeated within {DeclareLostStaleMinutes} minutes: host loss is never inferred from a timeout, and not declared while it reports.");
                await conn.ExecuteAsync(new CommandDefinition("update mission_hosts set declared_lost_at = clock_timestamp() where host_id = @hostId", new { hostId }, tx, cancellationToken: ct));
                await AuditAsync(conn, tx, op, "host_declared_lost", null, null, hostId, new { outboxUnrecoverable, evidence }, new { result = "declared" }, ct);
                declaredNow = true;
            }
            else declaredNow = false;
            await tx.CommitAsync(ct);
        }

        var voided = new List<(Guid, long)>();
        List<(Guid Attempt, long Account)> candidates;
        await using (var conn = await DbFailure.OpenAsync(db, ct))
            candidates = (await conn.QueryAsync<(Guid, long)>(new CommandDefinition(
                """
                select p.attempt_id, p.account_id from attempt_participants p join mission_attempts a using (attempt_id)
                where a.host_id = @hostId and p.started_at is not null and p.state in ('joined', 'left', 'result_pending')
                order by p.attempt_id, p.account_id
                """, new { hostId }, cancellationToken: ct))).ToList();
        foreach (var (attemptId, accountId) in candidates)
        {
            var (conn, tx, attempt, participant) = await LockAsync(attemptId, accountId, ct);
            await using var _ = conn;
            await using var __ = tx;
            if (participant.State is not ("joined" or "left" or "result_pending")) continue; // settled or quarantined meanwhile
            var refunded = await VoidStore.VoidParticipantLockedAsync(conn, tx, attemptId, accountId, attempt.State, "host_declared_lost", ct);
            await AuditAsync(conn, tx, op, "participant_void", attemptId, accountId, hostId, new { mode = "host_declared_lost", outboxUnrecoverable, evidence },
                new { basis = "host_declared_lost", refunded }, ct);
            await Anomalies.RaiseAsync(conn, tx, Anomalies.InfraVoid, $"{attemptId}:{accountId}", new { basis = "host_declared_lost", hostId, correlationId = op.CorrelationId }, ct,
                accountId: accountId, hostId: hostId, attemptId: attemptId);
            await tx.CommitAsync(ct);
            voided.Add((attemptId, accountId));
        }
        return new DeclareLostResult(declaredNow, voided);
    }

    /// <summary>
    /// The operator's read-only view of an attempt (S9-B9 `attempt show`): states, submissions (ids, sources, dispositions,
    /// codes, hashes, pre- or post-revocation, whether a manifest matches the recorded hash), exit evidence, settlements and
    /// actions, as "key=value" lines. Never a seed, salt, credential, ticket or receipt; a raw payload only for the named
    /// submission with <paramref name="raw"/> (base64).
    /// </summary>
    public async Task<IReadOnlyList<string>> ShowAsync(Guid attemptId, long? submissionId, bool raw, CancellationToken ct)
    {
        await using var conn = await DbFailure.OpenAsync(db, ct);
        var lines = new List<string>();
        var a = await conn.QuerySingleOrDefaultAsync<(string State, string? Host, string Catalogue, DateTime? Revoked, DateTime? Started)?>(new CommandDefinition(
            "select state, host_id, catalogue_version, credential_revoked_at, started_at from mission_attempts where attempt_id = @attemptId", new { attemptId }, cancellationToken: ct))
            ?? throw new RecoveryException(RecoveryError.NotFound, "No such attempt.");
        lines.Add($"attempt={attemptId}");
        lines.Add($"attempt.state={a.State}");
        lines.Add($"attempt.host={a.Host ?? "-"}");
        lines.Add($"attempt.catalogueVersion={a.Catalogue}");
        lines.Add($"attempt.revokedAt={Stamp(a.Revoked)}");
        foreach (var p in await conn.QueryAsync<(long Account, string State, string? Reason, string? Recorded, DateTime? Escalate)>(new CommandDefinition(
                     "select account_id, state, quarantine_reason, recorded_manifest_hash, escalate_at from attempt_participants where attempt_id = @attemptId order by account_id",
                     new { attemptId }, cancellationToken: ct)))
        {
            var acc = Base58Id.Format(p.Account);
            lines.Add($"participant.{acc}.state={p.State}");
            lines.Add($"participant.{acc}.quarantineReason={p.Reason ?? "-"}");
            lines.Add($"participant.{acc}.recordedHash={p.Recorded ?? "-"}");
            lines.Add($"participant.{acc}.escalateAt={Stamp(p.Escalate)}");
            foreach (var s in await conn.QueryAsync<(long Id, string Source, string Disposition, string[] Codes, byte[]? RawHash, string? Hash, bool Revoked, int Count)>(new CommandDefinition(
                         """
                         select submission_id, source, disposition, codes, raw_hash, manifest_hash, attempt_revoked, receive_count from result_submissions
                         where attempt_id = @attemptId and account_id = @Account order by submission_id
                         """, new { attemptId, p.Account }, cancellationToken: ct)))
                lines.Add($"submission.{s.Id}={acc} source={s.Source} disposition={s.Disposition} codes={string.Join(',', s.Codes)} rawHash={(s.RawHash is null ? "-" : Convert.ToHexStringLower(s.RawHash))} " +
                          $"manifestHash={s.Hash ?? "-"} evidence={(s.Revoked ? "post-revocation" : "pre-revocation")} matchesRecorded={(s.Hash is not null && s.Hash == p.Recorded ? "true" : "false")} received={s.Count}");
            var settled = await conn.QuerySingleOrDefaultAsync<(string Outcome, string? Basis, string? Recorded, long? Submission)?>(new CommandDefinition(
                "select outcome, void_basis, recorded_hash, submission_id from participant_settlements where attempt_id = @attemptId and account_id = @Account",
                new { attemptId, p.Account }, cancellationToken: ct));
            if (settled is { } st)
                lines.Add($"settlement.{acc}=outcome={st.Outcome} voidBasis={st.Basis ?? "-"} recordedHash={st.Recorded ?? "-"} submission={st.Submission?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}");
        }
        foreach (var r in await conn.QueryAsync<(string Source, int? Code, string? Signal, string Outcome, bool? Complete, bool? Revoked)>(new CommandDefinition(
                     "select source, exit_code, signal, outcome, outbox_complete, attempt_revoked from attempt_exit_reports where attempt_id = @attemptId order by source",
                     new { attemptId }, cancellationToken: ct)))
            lines.Add($"exitReport.{r.Source}=exitCode={r.Code?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"} signal={r.Signal ?? "-"} outcome={r.Outcome} " +
                      $"outboxComplete={(r.Complete is { } c ? (c ? "true" : "false") : "-")} evidence={(r.Revoked == true ? "post-revocation" : "pre-revocation")}");
        foreach (var o in await conn.QueryAsync<string>(new CommandDefinition(
                     "select outcome from attempt_outbox_observations where attempt_id = @attemptId", new { attemptId }, cancellationToken: ct)))
            lines.Add($"outboxObservation=outcome={o}");
        foreach (var x in await conn.QueryAsync<(string Action, long? Account, DateTime At, string Result)>(new CommandDefinition(
                     "select action, account_id, created_at, result::text from operator_actions where attempt_id = @attemptId order by id", new { attemptId }, cancellationToken: ct)))
            lines.Add($"action={x.Action} account={(x.Account is { } acc ? Base58Id.Format(acc) : "-")} at={Stamp(x.At)} result={x.Result}");
        if (submissionId is { } id && raw)
        {
            var bytes = await conn.QuerySingleOrDefaultAsync<byte[]?>(new CommandDefinition(
                "select x.raw_bytes from result_submission_payloads x join result_submissions s using (submission_id) where s.submission_id = @id and s.attempt_id = @attemptId",
                new { id, attemptId }, cancellationToken: ct));
            lines.Add($"submission.{id}.rawBase64={(bytes is null ? "-" : Convert.ToBase64String(bytes))}");
        }
        return lines;
    }

    static string Stamp(DateTime? value) => value is { } v ? v.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture) : "-";
}

/// <summary>Operator access to the alerts (S9-B9 `anomaly list`, `anomaly review`): a review is audited and happens once.</summary>
public sealed class AnomalyStore(NpgsqlDataSource db)
{
    public sealed record Anomaly(long Id, string Kind, Guid? AttemptId, long? AccountId, string? HostId, DateTime CreatedAt, DateTime? ReviewedAt, string Detail);

    public async Task<IReadOnlyList<Anomaly>> ListAsync(bool openOnly, CancellationToken ct)
    {
        await using var conn = await DbFailure.OpenAsync(db, ct);
        return (await conn.QueryAsync<Anomaly>(new CommandDefinition(
            """
            select id as Id, kind as Kind, attempt_id as AttemptId, account_id as AccountId, host_id as HostId, created_at as CreatedAt, reviewed_at as ReviewedAt, detail::text as Detail
            from anomaly_events where not @openOnly or reviewed_at is null order by id
            """, new { openOnly }, cancellationToken: ct))).ToList();
    }

    /// <summary>Marks <paramref name="id"/> reviewed with <paramref name="note"/> and audits it (anomaly_review), in one transaction.</summary>
    public async Task ReviewAsync(long id, string note, OperatorContext op, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(note) || note.Length > 1000 || note.Any(char.IsControl))
            throw new RecoveryException(RecoveryError.MissingCheck, "A review note is 1 to 1000 printable characters.");
        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var row = await conn.QuerySingleOrDefaultAsync<(long? Account, Guid? Attempt, string? Host, DateTime? Reviewed)?>(new CommandDefinition(
            "select account_id, attempt_id, host_id, reviewed_at from anomaly_events where id = @id", new { id }, tx, cancellationToken: ct))
            ?? throw new RecoveryException(RecoveryError.NotFound, "No such anomaly.");
        if (row.Reviewed is not null) throw new RecoveryException(RecoveryError.NotAllowed, "The anomaly is already reviewed.");
        if (row.Account is { } account) await SettlementStore.LockAccountAsync(conn, tx, account, ct);
        await conn.ExecuteAsync(new CommandDefinition("update anomaly_events set reviewed_at = clock_timestamp(), reviewed_note = @note where id = @id and reviewed_at is null",
            new { id, note }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            """
            insert into operator_actions (correlation_id, operator, action, attempt_id, account_id, host_id, reason, checks, result)
            values (@correlationId, @operator, 'anomaly_review', @Attempt, @Account, @Host, @reason, '{}'::jsonb, @result::jsonb)
            """, new { correlationId = op.CorrelationId, @operator = op.Operator, row.Attempt, row.Account, row.Host, reason = op.Reason,
                result = JsonSerializer.Serialize(new { anomalyId = id }) }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }
}
