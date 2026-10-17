using System.Data;
using Dapper;
using Npgsql;
using Blackglass.Accounts.Domain.Energy;
using Blackglass.Accounts.Persistence.Energy;

namespace Blackglass.Accounts.Persistence.Attempts;

/// <summary>Why an attempt read or release was refused, with nothing written.</summary>
public enum AttemptError
{
    /// <summary>No attempt with that id has the account as a participant: an unknown attempt and another account's are the same.</summary>
    NotFound,

    /// <summary>The attempt is past admission (assigned or later), or ended in a way this release did not write.</summary>
    InvalidState,
}

/// <summary>A refused read or release. The message is the reason's name only.</summary>
public sealed class AttemptException(AttemptError error) : Exception(error.ToString())
{
    public AttemptError Error { get; } = error;
}

/// <summary>The stored <c>mission_attempts.cancel_reason</c> values of a release.</summary>
public static class CancelReasons
{
    /// <summary>The player cancelled before start (S7-D7).</summary>
    public const string PlayerCancel = "player_cancel";

    /// <summary>No host assigned the attempt before its deadline (S7-D6).</summary>
    public const string NoCapacity = "no_capacity";
}

/// <summary>
/// One participation as GET /v1/attempts/{id} shows it: the attempt's and the participant's states, the assignment deadline
/// (UTC), the hold's amount and state, and the insurance snapshot's policy version (both null when uninsured). Never the seed
/// or the retention salt.
/// </summary>
public sealed record AttemptView(Guid AttemptId, string State, string ParticipantState, DateTime AssignDeadline, int EnergyAmount, string EnergyState,
    string? InsurancePolicyId, int? InsurancePolicyVersion);

/// <summary>
/// What a release did, or, when <paramref name="Replayed"/>, what the earlier release stored: the participant's state
/// (<c>released</c>), the energy given back (the hold's amount, 0 for a zero-cost template), the wallet balance right after the
/// <c>release:&lt;attempt&gt;</c> entry (null for a zero amount, which writes no entry and takes no wallet lock, so no balance
/// is known exactly), and the attempt's stored cancel reason (the winner's, when a cancel and the deadline worker raced).
/// </summary>
public sealed record ReleaseOutcome(string ParticipantState, int Released, int? BalanceAfter, string CancelReason, bool Replayed);

/// <summary>An admitted attempt past its assignment deadline, and its participant (Stage 7: the account that created it).</summary>
public sealed record DueAttempt(Guid AttemptId, long AccountId);

/// <summary>What the deadline release of one due attempt found.</summary>
public enum ExpireResult
{
    /// <summary>Released now, with <c>cancel_reason = 'no_capacity'</c>.</summary>
    Released,

    /// <summary>Already released (a player cancel or another worker committed first): the stored outcome, nothing written.</summary>
    AlreadyEnded,

    /// <summary>Moved past admission since it was selected (a later state is never cancelled): nothing written.</summary>
    NotAdmitted,
}

/// <param name="Due">How many attempts the selection returned (at most the batch).</param>
/// <param name="Failures">One safe description per due attempt whose release failed and rolled back (a SQLSTATE and
/// constraint, or an exception type), never a server message; that attempt stays due for the next sweep.</param>
public sealed record SweepReport(int Due, int Released, int AlreadyEnded, int NotAdmitted, IReadOnlyList<string> Failures);

/// <summary>
/// The attempt read, the player cancel before start and the assignment deadline release (Stage 7 Task 7; owner rulings
/// S7-D6 and S7-D7). Every release goes through <see cref="ReleaseLockedAsync"/>: one READ COMMITTED transaction, the
/// documented lock order, guarded transitions, and the reserved energy given back exactly once with its ledger entry. A
/// release never retries; a failure rolls everything back and propagates (a 23514 is a bug, answered 500; a timeout is busy).
/// </summary>
public sealed class AttemptStore(NpgsqlDataSource db)
{
    readonly NpgsqlDataSource db = db ?? throw new ArgumentNullException(nameof(db));

    /// <summary>
    /// The account's participation in <paramref name="attemptId"/>, or null when the attempt is unknown or the account is not
    /// its participant (the two are indistinguishable). One statement, so one snapshot: the attempt, participant, hold and
    /// insurance snapshot are seen as of one instant. Selects neither the seed nor the salt.
    /// </summary>
    public async Task<AttemptView?> GetAsync(long accountId, Guid attemptId, CancellationToken ct = default)
    {
        await using var conn = await DbFailure.OpenAsync(db, ct);
        var row = await conn.QuerySingleOrDefaultAsync<ViewRow>(new CommandDefinition(
            """
            select a.attempt_id as AttemptId, a.state as State, p.state as ParticipantState, a.assign_deadline as AssignDeadline,
              h.amount as EnergyAmount, h.state as EnergyState, i.policy_id as InsurancePolicyId, i.policy_version as InsurancePolicyVersion
            from mission_attempts a
              join attempt_participants p on p.attempt_id = a.attempt_id and p.account_id = @accountId
              join energy_holds h on h.attempt_id = p.attempt_id and h.account_id = p.account_id
              left join attempt_insurance i on i.attempt_id = p.attempt_id and i.account_id = p.account_id
            where a.attempt_id = @attemptId
            """, new { accountId, attemptId }, cancellationToken: ct));
        return row is null ? null
            : new AttemptView(row.AttemptId, row.State, row.ParticipantState, Utc(row.AssignDeadline), row.EnergyAmount, row.EnergyState,
                row.InsurancePolicyId, row.InsurancePolicyVersion);
    }

    /// <summary>
    /// The player cancel before start (S7-D7): <see cref="ReleaseLockedAsync"/> with <c>player_cancel</c> in its own
    /// transaction. NotFound for an unknown or foreign attempt, InvalidState after start; a repeat returns the stored outcome.
    /// </summary>
    public async Task<ReleaseOutcome> CancelAsync(long accountId, Guid attemptId, CancellationToken ct = default)
    {
        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var outcome = await ReleaseLockedAsync(conn, tx, attemptId, accountId, CancelReasons.PlayerCancel, ct);
        await tx.CommitAsync(ct);
        return outcome;
    }

    /// <summary>
    /// Releases <paramref name="accountId"/>'s participation in an admitted attempt, inside the caller's READ COMMITTED
    /// transaction (never commits). In the documented lock order (Global Constraints, Release):
    /// <list type="number">
    /// <item>the attempt FOR UPDATE, only when the account is its participant (another account's attempt is the same NotFound
    /// as an unknown one, and its row is never locked);</item>
    /// <item>the attempt's participants FOR UPDATE (Stage 7 attempts are solo; anything else is refused as a bug);</item>
    /// <item>the participant's hold FOR UPDATE;</item>
    /// <item>the account's inventories row FOR UPDATE: the inventory lock (S7-D4) changes while it is held, so an edit or a
    /// pick waiting on it sees the terminal state, and an admission waiting on it sees the participation released;</item>
    /// <item>for a positive amount only: the wallet FOR UPDATE and its materialization
    /// (<see cref="EnergyLedgerWriter.MaterializeLockedAsync"/>: the shared policy lock, then the clock), then
    /// <see cref="EnergyMath.Credit"/> (checked; it may exceed the cap, E6), one wallet UPDATE and the
    /// <c>release:&lt;attempt&gt;</c> entry (delta +amount, the attempt id). A 0-amount hold takes no wallet or policy lock and
    /// writes no entry;</item>
    /// <item>the guarded transitions, each of which must touch exactly one row: the attempt <c>admitted → cancelled</c> with
    /// <paramref name="reason"/> and <c>ended_at = clock_timestamp()</c> in one UPDATE, the participant
    /// <c>admitted → released</c>, the hold <c>reserved → released</c> (its <c>updated_at</c> is the trigger's).</item>
    /// </list>
    /// When the locked attempt is not admitted: already cancelled with this participant and hold released is the stored
    /// outcome (Replayed, nothing written); anything else is InvalidState, so a later state is never cancelled. The attempt,
    /// participant, loadout and insurance snapshots, hold and deploy request all remain.
    /// </summary>
    public static async Task<ReleaseOutcome> ReleaseLockedAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid attemptId, long accountId, string reason,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(tx);
        if (reason is not (CancelReasons.PlayerCancel or CancelReasons.NoCapacity)) throw new ArgumentOutOfRangeException(nameof(reason));
        EnergyPolicies.RequireReadCommitted(tx);

        var attempt = await conn.QuerySingleOrDefaultAsync<(string State, string? CancelReason)?>(new CommandDefinition(
            """
            select a.state, a.cancel_reason from mission_attempts a
            where a.attempt_id = @attemptId
              and exists (select 1 from attempt_participants p where p.attempt_id = a.attempt_id and p.account_id = @accountId)
            for update
            """, new { attemptId, accountId }, tx, cancellationToken: ct))
            ?? throw new AttemptException(AttemptError.NotFound);
        var participants = (await conn.QueryAsync<(long AccountId, string State)>(new CommandDefinition(
            "select account_id, state from attempt_participants where attempt_id = @attemptId order by account_id for update",
            new { attemptId }, tx, cancellationToken: ct))).ToList();
        if (participants.Count != 1 || participants[0].AccountId != accountId)
            throw new InvalidOperationException("A Stage 7 attempt has exactly one participant.");
        var participant = participants[0].State;
        var hold = await conn.QuerySingleAsync<(int Amount, string State)>(new CommandDefinition(
            "select amount, state from energy_holds where attempt_id = @attemptId and account_id = @accountId for update",
            new { attemptId, accountId }, tx, cancellationToken: ct));

        if (attempt.State != AdmissionStore.AdmittedState)
        {
            if (attempt is { State: "cancelled", CancelReason: { } stored } && participant == "released" && hold.State == "released")
                return new ReleaseOutcome("released", hold.Amount, await ReleasedBalanceAsync(conn, tx, attemptId, accountId, hold.Amount, ct), stored, Replayed: true);
            throw new AttemptException(AttemptError.InvalidState);
        }
        if (participant != AdmissionStore.AdmittedState || hold.State != "reserved") throw new AttemptException(AttemptError.InvalidState);

        await conn.QueryAsync<long>(new CommandDefinition(
            "select account_id from inventories where account_id = @accountId for update", new { accountId }, tx, cancellationToken: ct));

        int? balanceAfter = null;
        if (hold.Amount > 0)
        {
            var materialized = await EnergyLedgerWriter.MaterializeLockedAsync(conn, tx, accountId, ct)
                ?? throw new InvalidOperationException("A positive energy hold has no wallet to release into.");
            var after = EnergyMath.Credit(materialized.Wallet.Balance, hold.Amount);
            await ExactlyOneAsync(conn, tx, ct, "update energy_wallets set balance = @after where account_id = @accountId", new { accountId, after });
            await ExactlyOneAsync(conn, tx, ct,
                """
                insert into energy_ledger (account_id, attempt_id, kind, delta, balance_after, idempotency_key)
                values (@accountId, @attemptId, 'release', @delta, @after, @key)
                """, new { accountId, attemptId, delta = hold.Amount, after, key = ReleaseKey(attemptId) });
            balanceAfter = after;
        }

        await ExactlyOneAsync(conn, tx, ct,
            "update mission_attempts set state = 'cancelled', cancel_reason = @reason, ended_at = clock_timestamp() where attempt_id = @attemptId and state = 'admitted'",
            new { attemptId, reason });
        await ExactlyOneAsync(conn, tx, ct,
            "update attempt_participants set state = 'released' where attempt_id = @attemptId and account_id = @accountId and state = 'admitted'",
            new { attemptId, accountId });
        await ExactlyOneAsync(conn, tx, ct,
            "update energy_holds set state = 'released' where attempt_id = @attemptId and account_id = @accountId and state = 'reserved'",
            new { attemptId, accountId });
        return new ReleaseOutcome("released", hold.Amount, balanceAfter, reason, Replayed: false);
    }

    /// <summary>
    /// The admitted attempts whose <c>assign_deadline &lt;= clock_timestamp()</c> (database time: a deadline exactly at now is
    /// due), oldest deadline first, at most <paramref name="limit"/>, selected FOR UPDATE SKIP LOCKED in a short transaction
    /// that commits at once: an attempt a release is working on (its row locked) is skipped, never waited for. The lock ends
    /// with the transaction, so two workers can still select one attempt; each release re-locks it and the later one sees the
    /// stored outcome.
    /// </summary>
    public async Task<IReadOnlyList<DueAttempt>> DueAsync(int limit, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var due = (await conn.QueryAsync<(Guid AttemptId, long AccountId)>(new CommandDefinition(
            """
            select attempt_id, created_by from mission_attempts
            where state = 'admitted' and assign_deadline <= clock_timestamp()
            order by assign_deadline limit @limit
            for update skip locked
            """, new { limit }, tx, cancellationToken: ct))).Select(r => new DueAttempt(r.AttemptId, r.AccountId)).ToList();
        await tx.CommitAsync(ct);
        return due;
    }

    /// <summary>
    /// The deadline release of one selected attempt (S7-D6): <see cref="ReleaseLockedAsync"/> with <c>no_capacity</c> in its
    /// own transaction. It re-locks the attempt, so a player cancel or another worker that committed in between makes this the
    /// stored outcome (AlreadyEnded), and an attempt that moved past admission is left alone (NotAdmitted, rolled back).
    /// </summary>
    public async Task<ExpireResult> ExpireAsync(DueAttempt due, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(due);
        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        ReleaseOutcome outcome;
        try { outcome = await ReleaseLockedAsync(conn, tx, due.AttemptId, due.AccountId, CancelReasons.NoCapacity, ct); }
        catch (AttemptException e) when (e.Error == AttemptError.InvalidState) { return ExpireResult.NotAdmitted; } // disposing rolls back
        await tx.CommitAsync(ct);
        return outcome.Replayed ? ExpireResult.AlreadyEnded : ExpireResult.Released;
    }

    /// <summary>
    /// One deadline sweep: <see cref="DueAsync"/>, then <see cref="ExpireAsync"/> for each due attempt, each in its own
    /// transaction. A release that fails rolls back alone and is reported by its safe description (the attempt stays due for
    /// the next sweep; nothing retries here); the others still run. A failing selection propagates. Cancellation propagates.
    /// </summary>
    public async Task<SweepReport> SweepAsync(int limit, CancellationToken ct = default)
    {
        var due = await DueAsync(limit, ct);
        int released = 0, ended = 0, notAdmitted = 0;
        var failures = new List<string>();
        foreach (var attempt in due)
        {
            try
            {
                switch (await ExpireAsync(attempt, ct))
                {
                    case ExpireResult.Released: released++; break;
                    case ExpireResult.AlreadyEnded: ended++; break;
                    case ExpireResult.NotAdmitted: notAdmitted++; break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (NpgsqlException e) { failures.Add(DbFailure.SafeDescription(e)); }
            catch (Exception e) { failures.Add(e.GetType().Name); } // a bug in one release must not stop the others
        }
        return new SweepReport(due.Count, released, ended, notAdmitted, failures);
    }

    /// <summary>The ledger key of an attempt's release (0006/0007: unique per account, naming the attempt).</summary>
    public static string ReleaseKey(Guid attemptId) => "release:" + attemptId.ToString("D");

    /// <summary>The balance the stored release entry recorded; null for a zero amount (no entry).</summary>
    static async Task<int?> ReleasedBalanceAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid attemptId, long accountId, int amount, CancellationToken ct)
    {
        if (amount == 0) return null;
        return await conn.QuerySingleOrDefaultAsync<int?>(new CommandDefinition(
            "select balance_after from energy_ledger where account_id = @accountId and idempotency_key = @key",
            new { accountId, key = ReleaseKey(attemptId) }, tx, cancellationToken: ct))
            ?? throw new InvalidOperationException("A released positive hold has no release entry.");
    }

    static async Task ExactlyOneAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct, string sql, object args)
    {
        var affected = await conn.ExecuteAsync(new CommandDefinition(sql, args, tx, cancellationToken: ct));
        if (affected != 1) throw new InvalidOperationException($"A release write touched {affected} rows instead of 1.");
    }

    /// <summary>Npgsql reads timestamptz as UTC; anything else is a broken mapping, never silently converted.</summary>
    static DateTime Utc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : throw new InvalidOperationException($"A timestamptz was read as {value.Kind}, not UTC.");

    sealed record ViewRow(Guid AttemptId, string State, string ParticipantState, DateTime AssignDeadline, int EnergyAmount, string EnergyState,
        string? InsurancePolicyId, int? InsurancePolicyVersion);
}
