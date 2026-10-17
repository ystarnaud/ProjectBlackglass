using Dapper;
using Npgsql;

namespace Blackglass.Accounts.Persistence.Settlement;

/// <summary>A participant's receipt view (plan §C): the stored bytes, a pending state, or why there is none.</summary>
public abstract record ReceiptView
{
    /// <summary>The receipt exactly as the settlement or void transaction wrote it.</summary>
    public sealed record Settled(string Receipt) : ReceiptView;

    /// <param name="State">running, result_pending, unresponsive or under_review.</param>
    public sealed record Pending(string State) : ReceiptView;

    /// <summary>The participant was released before its start: no receipt will ever exist.</summary>
    public sealed record NoReceipt : ReceiptView;

    /// <summary>Unknown attempt, or not this account's: indistinguishable to the caller.</summary>
    public sealed record NotFound : ReceiptView;
}

/// <summary>
/// GET /v1/attempts/{id}/receipt's read (S9-B6, S9-B19): the account comes from the bearer session only. One unlocked read; the
/// receipt is immutable once written, so a client sees only committed rewards.
/// </summary>
public sealed class ReceiptStore(NpgsqlDataSource db)
{
    public async Task<ReceiptView> ReceiptAsync(long accountId, Guid attemptId, CancellationToken ct)
    {
        await using var conn = await DbFailure.OpenAsync(db, ct);
        var row = await conn.QuerySingleOrDefaultAsync<(string State, string AttemptState, bool Revoked, string? Receipt)?>(new CommandDefinition(
            """
            select p.state, a.state, a.credential_revoked_at is not null, s.receipt
            from attempt_participants p
            join mission_attempts a on a.attempt_id = p.attempt_id
            left join participant_settlements s on s.attempt_id = p.attempt_id and s.account_id = p.account_id
            where p.attempt_id = @attemptId and p.account_id = @accountId
            """, new { attemptId, accountId }, cancellationToken: ct));
        if (row is not { } r) return new ReceiptView.NotFound();
        if (r.Receipt is not null) return new ReceiptView.Settled(r.Receipt);
        if (r.State == "released") return new ReceiptView.NoReceipt();
        if (r.State == "quarantined" || r.Revoked) return new ReceiptView.Pending("under_review");
        if (r.State == "result_pending") return new ReceiptView.Pending("result_pending");
        if (r.AttemptState == "unresponsive") return new ReceiptView.Pending("unresponsive");
        return new ReceiptView.Pending("running");
    }
}
