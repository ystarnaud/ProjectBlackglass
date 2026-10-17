using System.Data;
using Dapper;
using Npgsql;
using Blackglass.Accounts.Persistence.Energy;

namespace Blackglass.Accounts.Persistence.Attempts;

public abstract record StartResult
{
    /// <summary>The participant's start, committed now or (Replayed) earlier: its stored started_at, never a second charge.</summary>
    public sealed record Started(DateTime StartedAt, bool Replayed) : StartResult;
    /// <summary>No ticket for this participant was redeemed by the attempt's session.</summary>
    public sealed record NotRedeemed : StartResult;
    /// <summary>The participant was released, or the attempt cancelled.</summary>
    public sealed record Cancelled : StartResult;
    /// <summary>A first start after the join deadline (database clock): refused, nothing written (B8).</summary>
    public sealed record DeadlinePassed : StartResult;
    public sealed record WrongSession : StartResult;
    /// <summary>The account is not a participant of the attempt.</summary>
    public sealed record NotFound : StartResult;
    /// <summary>The attempt is not ready or running (nothing to start yet), or the participant can't start from its state.</summary>
    public sealed record InvalidState : StartResult;
}

/// <summary>
/// The backend-committed start of one participant (spec §7.5; owner rulings B8, B9): the only path that charges, exactly once.
/// One READ COMMITTED transaction in the Start lock order (Global Constraints): the participant's accounts row FOR KEY SHARE,
/// the attempt FOR UPDATE, the participant FOR UPDATE, its hold FOR UPDATE, then for a positive hold its wallet FOR UPDATE and
/// its materialization (the shared policy lock, then the clock), else the clock directly. The participant takes no inventories
/// lock: it stays active. No automatic retry; a failure rolls everything back and propagates.
/// </summary>
public sealed class StartStore(NpgsqlDataSource db)
{
    /// <summary>
    /// Starts <paramref name="accountId"/> in <paramref name="attemptId"/> for <paramref name="session"/>. In order: not a
    /// participant is NotFound; another session is WrongSession; an already started participant answers its stored start (before
    /// any deadline check: a lost acknowledgement retried late still gets its committed result, B8); a released participant or
    /// a cancelled attempt is Cancelled; an attempt not ready or running is InvalidState; no ticket of this participant redeemed
    /// by this session is NotRedeemed; the database clock (read after the locks) past the join deadline is DeadlinePassed.
    /// Otherwise: the participant <c>admitted → joined</c> with started_at, its hold <c>reserved → charged</c> with, for a
    /// positive hold, one <c>charge:&lt;attempt&gt;</c> entry of delta 0 at the materialized balance (B9), and the attempt
    /// <c>ready → running</c> with started_at when it was ready.
    /// </summary>
    public async Task<StartResult> StartAsync(Guid attemptId, long accountId, Guid session, CancellationToken ct)
    {
        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        if (!await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                "select exists (select 1 from attempt_participants where attempt_id = @attemptId and account_id = @accountId)", new { attemptId, accountId }, tx, cancellationToken: ct)))
            return new StartResult.NotFound();

        await conn.ExecuteAsync(new CommandDefinition("select 1 from accounts where account_id = @accountId for key share", new { accountId }, tx, cancellationToken: ct));
        var attempt = await conn.QuerySingleAsync<(string State, Guid? Session, DateTime? JoinDeadline)>(new CommandDefinition(
            "select state, process_session_id, join_deadline from mission_attempts where attempt_id = @attemptId for update", new { attemptId }, tx, cancellationToken: ct));
        if (attempt.Session != session) return new StartResult.WrongSession();
        var participant = await conn.QuerySingleAsync<(string State, DateTime? StartedAt)>(new CommandDefinition(
            "select state, started_at from attempt_participants where attempt_id = @attemptId and account_id = @accountId for update",
            new { attemptId, accountId }, tx, cancellationToken: ct));
        if (participant.StartedAt is { } stored) return new StartResult.Started(Utc(stored), Replayed: true);
        if (participant.State == "released" || attempt.State == "cancelled") return new StartResult.Cancelled();
        if (attempt.State is not ("ready" or "running") || participant.State != "admitted") return new StartResult.InvalidState();
        if (!await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                """
                select exists (select 1 from join_tickets
                               where attempt_id = @attemptId and account_id = @accountId and redeemed_by_session = @session)
                """, new { attemptId, accountId, session }, tx, cancellationToken: ct)))
            return new StartResult.NotRedeemed();

        var hold = await conn.QuerySingleAsync<(int Amount, string State)>(new CommandDefinition(
            "select amount, state from energy_holds where attempt_id = @attemptId and account_id = @accountId for update", new { attemptId, accountId }, tx, cancellationToken: ct));
        if (hold.State != "reserved") throw new InvalidOperationException("An admitted participant's hold is not reserved.");
        DateTime now;
        int? balance = null;
        if (hold.Amount > 0)
        {
            var materialized = await EnergyLedgerWriter.MaterializeLockedAsync(conn, tx, accountId, ct)
                ?? throw new InvalidOperationException("A positive energy hold has no wallet to charge.");
            (now, balance) = (materialized.Now, materialized.Wallet.Balance);
        }
        else now = Utc(await conn.ExecuteScalarAsync<DateTime>(new CommandDefinition("select clock_timestamp()", transaction: tx, cancellationToken: ct)));
        if (attempt.JoinDeadline is not { } deadline || now > Utc(deadline)) return new StartResult.DeadlinePassed(); // disposing rolls back

        await ExactlyOneAsync(conn, tx, ct,
            "update attempt_participants set state = 'joined', started_at = @now where attempt_id = @attemptId and account_id = @accountId and state = 'admitted'",
            new { attemptId, accountId, now });
        await ExactlyOneAsync(conn, tx, ct,
            "update energy_holds set state = 'charged' where attempt_id = @attemptId and account_id = @accountId and state = 'reserved'", new { attemptId, accountId });
        if (balance is { } after)
            await ExactlyOneAsync(conn, tx, ct,
                """
                insert into energy_ledger (account_id, attempt_id, kind, delta, balance_after, idempotency_key)
                values (@accountId, @attemptId, 'charge', 0, @after, @key)
                """, new { accountId, attemptId, after, key = ChargeKey(attemptId) });
        if (attempt.State == "ready")
            await ExactlyOneAsync(conn, tx, ct,
                "update mission_attempts set state = 'running', started_at = @now where attempt_id = @attemptId and state = 'ready'", new { attemptId, now });
        await tx.CommitAsync(ct);
        return new StartResult.Started(now, Replayed: false);
    }

    /// <summary>The ledger key of a participant's charge (0008: exactly 'charge:' and the attempt, unique per account).</summary>
    public static string ChargeKey(Guid attemptId) => "charge:" + attemptId.ToString("D");

    static async Task ExactlyOneAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct, string sql, object args)
    {
        var affected = await conn.ExecuteAsync(new CommandDefinition(sql, args, tx, cancellationToken: ct));
        if (affected != 1) throw new InvalidOperationException($"A start write touched {affected} rows instead of 1.");
    }

    static DateTime Utc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : throw new InvalidOperationException($"A timestamptz was read as {value.Kind}, not UTC.");
}
