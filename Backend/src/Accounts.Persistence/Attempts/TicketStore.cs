using System.Data;
using Dapper;
using Npgsql;
using Blackglass.Accounts.Domain.Auth;

namespace Blackglass.Accounts.Persistence.Attempts;

public abstract record MintResult
{
    /// <summary>The new ticket, returned once (only its SHA-256 is stored).</summary>
    public sealed record Minted(OpaqueSecret Ticket, DateTime ExpiresAt) : MintResult;
    /// <summary>No such attempt has the account as a participant (an unknown and a foreign attempt are the same).</summary>
    public sealed record NotFound : MintResult;
    /// <summary>The attempt is not ready or running, or the participant was released or settled.</summary>
    public sealed record InvalidState : MintResult;
}

public abstract record RedeemResult
{
    /// <summary>The participant's account and its snapshot operatives in the snapshot's order (canonical Base58 ids).</summary>
    public sealed record Redeemed(long AccountId, IReadOnlyList<string> OperativeIds) : RedeemResult
    {
        public bool Equals(Redeemed? other) => other is not null && AccountId == other.AccountId && OperativeIds.SequenceEqual(other.OperativeIds);
        public override int GetHashCode() => AccountId.GetHashCode();
    }
    /// <summary>Malformed, unknown, expired, revoked, or redeemed by another session: the uniform 401.</summary>
    public sealed record Unknown : RedeemResult;
    public sealed record WrongAttempt : RedeemResult;
    public sealed record WrongSession : RedeemResult;
    /// <summary>The attempt is not ready or running, or the participant was released or settled.</summary>
    public sealed record InvalidState : RedeemResult;
}

/// <summary>
/// Join tickets (spec §7.5; owner ruling B7): 256-bit <c>jt1_</c> secrets stored as SHA-256, bound to (attempt, account) at
/// mint and to the attempt's process session at redemption, single-use. Lock orders (Global Constraints): mint takes the attempt
/// FOR SHARE, the participant FOR UPDATE, its live tickets (revoked), then the clock; redeem reads the ticket unlocked, then
/// the attempt FOR SHARE, the participant FOR SHARE, the ticket FOR UPDATE, then the clock. join_tickets references the
/// participant only, so no lock on accounts follows the attempt lock. No retry.
/// </summary>
public sealed class TicketStore(NpgsqlDataSource db)
{
    static readonly string[] Joinable = ["ready", "running"];
    static readonly string[] Ticketed = ["admitted", "joined", "left"];

    /// <summary>
    /// A new ticket for the account's participation, expiring <paramref name="ticketSeconds"/> after the database clock; the
    /// participant's earlier live tickets are revoked in the same transaction, so at most one is live. NotFound for an unknown
    /// or foreign attempt; InvalidState unless the attempt is ready or running and the participant admitted, joined or left
    /// (the reconnect path, Stage 15).
    /// </summary>
    public async Task<MintResult> MintAsync(long accountId, Guid attemptId, int ticketSeconds, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ticketSeconds, 1);
        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var state = await conn.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            """
            select a.state from mission_attempts a
            where a.attempt_id = @attemptId and exists (select 1 from attempt_participants p where p.attempt_id = a.attempt_id and p.account_id = @accountId)
            for share of a
            """, new { attemptId, accountId }, tx, cancellationToken: ct));
        if (state is null) return new MintResult.NotFound();
        var participant = await conn.QuerySingleAsync<string>(new CommandDefinition(
            "select state from attempt_participants where attempt_id = @attemptId and account_id = @accountId for update", new { attemptId, accountId }, tx, cancellationToken: ct));
        if (!Joinable.Contains(state) || !Ticketed.Contains(participant)) return new MintResult.InvalidState();
        await conn.ExecuteAsync(new CommandDefinition(
            """
            update join_tickets set revoked_at = clock_timestamp()
            where attempt_id = @attemptId and account_id = @accountId and redeemed_at is null and revoked_at is null
            """, new { attemptId, accountId }, tx, cancellationToken: ct));
        var ticket = OpaqueSecret.New(OpaqueSecret.JoinTicketPrefix);
        var expiresAt = await conn.ExecuteScalarAsync<DateTime>(new CommandDefinition(
            """
            insert into join_tickets (ticket_hash, attempt_id, account_id, expires_at)
            values (@hash, @attemptId, @accountId, clock_timestamp() + make_interval(secs => @ticketSeconds)) returning expires_at
            """, new { hash = ticket.Hash(), attemptId, accountId, ticketSeconds = (double)ticketSeconds }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return new MintResult.Minted(ticket, Utc(expiresAt));
    }

    /// <summary>
    /// The process redeems a client's ticket for <paramref name="attemptId"/> (its own, from its credential) under
    /// <paramref name="session"/>. In order: a malformed or unknown ticket is Unknown; another attempt's is WrongAttempt; the
    /// attempt's session must be <paramref name="session"/> (WrongSession); the attempt ready or running and the participant
    /// admitted, joined or left (InvalidState); the ticket already redeemed by this session answers the same again; redeemed by
    /// another session, revoked or expired (database clock) is Unknown. Otherwise it is marked redeemed by this session.
    /// </summary>
    public async Task<RedeemResult> RedeemAsync(Guid attemptId, Guid session, string ticketText, CancellationToken ct)
    {
        if (!OpaqueSecret.TryParse(ticketText, OpaqueSecret.JoinTicketPrefix, out var ticket)) return new RedeemResult.Unknown();
        var hash = ticket!.Hash();
        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var owner = await conn.QuerySingleOrDefaultAsync<(Guid AttemptId, long AccountId)?>(new CommandDefinition(
            "select attempt_id, account_id from join_tickets where ticket_hash = @hash", new { hash }, tx, cancellationToken: ct));
        if (owner is not { } t) return new RedeemResult.Unknown();
        if (t.AttemptId != attemptId) return new RedeemResult.WrongAttempt();

        var attempt = await conn.QuerySingleAsync<(string State, Guid? Session)>(new CommandDefinition(
            "select state, process_session_id from mission_attempts where attempt_id = @attemptId for share", new { attemptId }, tx, cancellationToken: ct));
        if (attempt.Session != session) return new RedeemResult.WrongSession();
        var participant = await conn.QuerySingleAsync<(string State, string Operatives)>(new CommandDefinition(
            """
            select state, coalesce((select json_agg(o ->> 'operativeId' order by n)::text
                                    from jsonb_array_elements(loadout_snapshot -> 'operatives') with ordinality as x(o, n)), '[]')
            from attempt_participants where attempt_id = @attemptId and account_id = @AccountId for share
            """, new { attemptId, t.AccountId }, tx, cancellationToken: ct));
        if (!Joinable.Contains(attempt.State) || !Ticketed.Contains(participant.State)) return new RedeemResult.InvalidState();
        var row = await conn.QuerySingleAsync<(bool Expired, DateTime? RedeemedAt, Guid? RedeemedBy, DateTime? RevokedAt)>(new CommandDefinition(
            "select expires_at <= clock_timestamp(), redeemed_at, redeemed_by_session, revoked_at from join_tickets where ticket_hash = @hash for update",
            new { hash }, tx, cancellationToken: ct));
        var redeemed = new RedeemResult.Redeemed(t.AccountId, System.Text.Json.JsonSerializer.Deserialize<string[]>(participant.Operatives)!);
        if (row.RedeemedAt is not null) return row.RedeemedBy == session ? redeemed : new RedeemResult.Unknown();
        if (row.RevokedAt is not null || row.Expired) return new RedeemResult.Unknown();
        await conn.ExecuteAsync(new CommandDefinition(
            "update join_tickets set redeemed_at = clock_timestamp(), redeemed_by_session = @session where ticket_hash = @hash and redeemed_at is null",
            new { hash, session }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return redeemed;
    }

    static DateTime Utc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : throw new InvalidOperationException($"A timestamptz was read as {value.Kind}, not UTC.");
}
