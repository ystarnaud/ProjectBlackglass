using System.Data;
using Dapper;
using Npgsql;
using Blackglass.Accounts.Domain.Attempts;

namespace Blackglass.Accounts.Persistence.Attempts;

public sealed record AuthenticatedProcess(Guid AttemptId);

/// <summary>
/// The mission process side of an attempt (spec §16.3; plan revision 2 R1, R4, R6). A process authenticates with its attempt's
/// <c>pc1_</c> credential: one indexed lookup of the SHA-256 per request (no cache), valid only while the attempt is neither
/// cancelled nor settled. The route's attempt must be the credential's (the authentication handler compares them). There is
/// no exception after cancellation: stop instructions reach the process through its host agent only. No host row is ever
/// locked here.
/// </summary>
public sealed class ProcessStore(NpgsqlDataSource db)
{
    public async Task<AuthenticatedProcess?> AuthenticateAsync(byte[] credentialHash, CancellationToken ct)
    {
        await using var conn = await DbFailure.OpenAsync(db, ct);
        var attempt = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "select attempt_id from mission_attempts where process_credential_hash = @credentialHash and state not in ('cancelled', 'settled')",
            new { credentialHash }, cancellationToken: ct));
        return attempt is { } id ? new AuthenticatedProcess(id) : null;
    }

    /// <summary>
    /// The process's failure report (B11 revised, R6), in the release lock order: before any start, every admitted participant
    /// is released with <c>process_failed</c> (the attempt is cancelled; its slot stays until the agent confirms the unit gone);
    /// after a start nothing is cancelled, released or refunded. Either way the report (session and reason) is stored once as
    /// evidence. The body's session must be the attempt's (WrongSession otherwise, nothing written). An identical repeat answers
    /// the stored outcome; a different reason is Conflicting. (Over HTTP a repeat after the cancellation meets the uniform 401:
    /// the credential ended with its attempt, R4.)
    /// </summary>
    public async Task<ExitReportResult> FailedAsync(Guid attemptId, FailedRequest report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);
        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var session = await conn.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            "select process_session_id from mission_attempts where attempt_id = @attemptId", new { attemptId }, tx, cancellationToken: ct));
        if (session != report.ProcessSessionId) return new ExitReportResult.WrongSession();

        async Task<ExitReportResult?> StoredAsync()
        {
            var stored = await conn.QuerySingleOrDefaultAsync<(string Reason, string Outcome)?>(new CommandDefinition(
                "select reason, outcome from attempt_exit_reports where attempt_id = @attemptId and source = 'process'", new { attemptId }, tx, cancellationToken: ct));
            return stored is { } s ? s.Reason == report.Reason ? new ExitReportResult.Applied(s.Outcome) : new ExitReportResult.Conflicting() : null;
        }
        if (await StoredAsync() is { } early) return early;
        var batch = await AttemptStore.ReleaseParticipantsLockedAsync(conn, tx, attemptId, null, CancelReasons.ProcessFailed, AttemptStore.PreStartStates, ct)
            ?? throw new InvalidOperationException("An attempt has no participants.");
        if (await StoredAsync() is { } raced) return raced;
        var outcome = batch.ReleasedAny ? "cancelled" : "none";
        await conn.ExecuteAsync(new CommandDefinition(
            """
            insert into attempt_exit_reports (attempt_id, source, process_session_id, attempt_state, any_started, reason, outcome)
            values (@attemptId, 'process', @ProcessSessionId, @StateBefore, @AnyStarted, @Reason, @outcome)
            """, new { attemptId, report.ProcessSessionId, batch.StateBefore, batch.AnyStarted, report.Reason, outcome }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return new ExitReportResult.Applied(outcome);
    }
}
