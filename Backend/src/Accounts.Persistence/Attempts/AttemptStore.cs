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

    /// <summary>The participant started, or the attempt is past the states a release may cancel, or ended in a way this release did not write.</summary>
    InvalidState,
}

/// <summary>A refused read or release. The message is the reason's name only.</summary>
public sealed class AttemptException(AttemptError error) : Exception(error.ToString())
{
    public AttemptError Error { get; } = error;
}

/// <summary>The stored <c>cancel_reason</c> and <c>release_reason</c> values (0008's lists).</summary>
public static class CancelReasons
{
    /// <summary>The player cancelled before start (S7-D7).</summary>
    public const string PlayerCancel = "player_cancel";

    /// <summary>No host assigned the attempt before its deadline (S7-D6).</summary>
    public const string NoCapacity = "no_capacity";

    /// <summary>The claimed attempt did not report ready before its launch deadline.</summary>
    public const string LaunchTimeout = "launch_timeout";

    /// <summary>The participant did not start before the join deadline.</summary>
    public const string JoinTimeout = "join_timeout";

    /// <summary>The agent reported the unit gone before any start.</summary>
    public const string ProcessExited = "process_exited";

    /// <summary>The process reported a failure before any start.</summary>
    public const string ProcessFailed = "process_failed";

    /// <summary>The ready report's loot pool was outside the catalogue's table, or its hash was wrong (spec §7.4, R3).</summary>
    public const string InvalidPool = "invalid_pool";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [PlayerCancel, NoCapacity, LaunchTimeout, JoinTimeout, ProcessExited, ProcessFailed, InvalidPool], StringComparer.Ordinal);
}

/// <summary>
/// One participation as GET /v1/attempts/{id} shows it: the attempt's and the participant's states, the assignment deadline
/// (UTC), the hold's amount and state, and the insurance snapshot's policy version (both null when uninsured). Never the seed
/// or the retention salt.
/// </summary>
public sealed record AttemptView(Guid AttemptId, string State, string ParticipantState, DateTime AssignDeadline, int EnergyAmount, string EnergyState,
    string? InsurancePolicyId, int? InsurancePolicyVersion, DateTime? LaunchDeadline = null, DateTime? JoinDeadline = null, string? HostId = null, int? MissionPort = null);

/// <summary>
/// What a release did, or, when <paramref name="Replayed"/>, what the earlier release stored: the participant's state
/// (<c>released</c>), the energy given back (the hold's amount, 0 for a zero-cost template), the wallet balance right after the
/// <c>release:&lt;attempt&gt;</c> entry (null for a zero amount, which writes no entry and takes no wallet lock, so no balance
/// is known exactly), and the participant's stored release reason (the winner's, when a cancel and a deadline raced).
/// </summary>
public sealed record ReleaseOutcome(string ParticipantState, int Released, int? BalanceAfter, string CancelReason, bool Replayed);

/// <summary>Which deadline made an attempt due.</summary>
public enum DeadlineKind { Assign, Launch, Join }

/// <summary>An attempt past one of its deadlines (and its creator, informational: a release names its participants itself).</summary>
public sealed record DueAttempt(Guid AttemptId, long AccountId, DeadlineKind Kind = DeadlineKind.Assign);

/// <summary>What the deadline release of one due attempt found.</summary>
public enum ExpireResult
{
    /// <summary>Released now: at least one participant, with the deadline's reason.</summary>
    Released,

    /// <summary>Already released (a cancel, an exit or another worker committed first): the stored outcome, nothing written.</summary>
    AlreadyEnded,

    /// <summary>Moved past the deadline's states since it was selected, or (join) every participant started: nothing written.</summary>
    NotAdmitted,
}

/// <param name="Due">How many attempts the selections returned (at most the batch each).</param>
/// <param name="Failures">One safe description per due attempt whose release failed and rolled back (a SQLSTATE and
/// constraint, or an exception type), never a server message; that attempt cools down before it is selected again.</param>
/// <param name="Unresponsive">Running attempts whose process lease lapsed, moved to unresponsive (never a void, D-11).</param>
public sealed record SweepReport(int Due, int Released, int AlreadyEnded, int NotAdmitted, IReadOnlyList<string> Failures, int Unresponsive = 0);

/// <summary>
/// The deadline worker's memory of items whose release keeps failing (Stage 7 CP2 M3): after its n-th consecutive failure an
/// attempt is left out of the selections for min(2^n, 64) sweeps, so it can't hold the head of the batch; a success forgets
/// it. In memory and bounded (<see cref="MaxEntries"/>; past that, a new failing item is simply not cooled down). One per
/// worker; not thread-safe (the worker's sweeps never overlap).
/// </summary>
public sealed class SweepCooldown
{
    public const int MaxEntries = 10_000;
    readonly Dictionary<Guid, (int Failures, long Until)> items = new();
    long sweep;

    public int Count => items.Count;

    internal void BeginSweep() => sweep++;

    internal Guid[] Excluded() => items.Where(i => i.Value.Until >= sweep).Select(i => i.Key).ToArray(); // the next n sweeps after a failure

    internal void Failed(Guid attempt)
    {
        var failures = items.TryGetValue(attempt, out var known) ? known.Failures + 1 : 1;
        if (!items.ContainsKey(attempt) && items.Count >= MaxEntries) return;
        items[attempt] = (failures, sweep + Math.Min(1L << Math.Min(failures, 6), 64));
    }

    internal void Succeeded(Guid attempt) => items.Remove(attempt);
}

/// <summary>
/// The attempt read, the player cancel before start, and the release behind every pre-start exit: the assignment, launch and
/// join deadlines (this store's sweep), the exit and failure reports and an invalid pool (their stores call
/// <see cref="ReleaseParticipantsLockedAsync"/>). One READ COMMITTED transaction per release, the documented lock order,
/// guarded transitions, and each participant's reserved energy given back exactly once with its ledger entry. A release never
/// retries; a failure rolls everything back and propagates (a 23514 is a bug, answered 500; a timeout is busy).
/// </summary>
public sealed class AttemptStore(NpgsqlDataSource db, int processLeaseSeconds = 30)
{
    readonly NpgsqlDataSource db = db ?? throw new ArgumentNullException(nameof(db));
    readonly int processLeaseSeconds = processLeaseSeconds >= 1 ? processLeaseSeconds : throw new ArgumentOutOfRangeException(nameof(processLeaseSeconds));

    /// <summary>The attempt states a release may cancel (the §7.2 edges to cancelled).</summary>
    public static readonly string[] PreStartStates = ["admitted", "assigned", "launching", "ready"];

    /// <summary>The participant states after a start (0008's attempt_participant_started).</summary>
    static readonly string[] StartedStates = ["joined", "left", "result_pending", "unresponsive", "quarantined", "settled"];

    /// <summary>
    /// The account's participation in <paramref name="attemptId"/>, or null when the attempt is unknown or the account is not
    /// its participant (the two are indistinguishable). One statement, so one snapshot. Selects neither the seed nor the salt.
    /// </summary>
    public async Task<AttemptView?> GetAsync(long accountId, Guid attemptId, CancellationToken ct = default)
    {
        await using var conn = await DbFailure.OpenAsync(db, ct);
        var row = await conn.QuerySingleOrDefaultAsync<ViewRow>(new CommandDefinition(
            """
            select a.attempt_id as AttemptId, a.state as State, p.state as ParticipantState, a.assign_deadline as AssignDeadline,
              h.amount as EnergyAmount, h.state as EnergyState, i.policy_id as InsurancePolicyId, i.policy_version as InsurancePolicyVersion,
              a.launch_deadline as LaunchDeadline, a.join_deadline as JoinDeadline, a.host_id as HostId, a.mission_port as MissionPort
            from mission_attempts a
              join attempt_participants p on p.attempt_id = a.attempt_id and p.account_id = @accountId
              join energy_holds h on h.attempt_id = p.attempt_id and h.account_id = p.account_id
              left join attempt_insurance i on i.attempt_id = p.attempt_id and i.account_id = p.account_id
            where a.attempt_id = @attemptId
            """, new { accountId, attemptId }, cancellationToken: ct));
        return row is null ? null
            : new AttemptView(row.AttemptId, row.State, row.ParticipantState, Utc(row.AssignDeadline), row.EnergyAmount, row.EnergyState,
                row.InsurancePolicyId, row.InsurancePolicyVersion, row.LaunchDeadline is { } l ? Utc(l) : null, row.JoinDeadline is { } j ? Utc(j) : null,
                row.HostId, row.MissionPort);
    }

    /// <summary>
    /// The player cancel before this participant's start (S7-D7; Stage 8: in any pre-start attempt state, B6): releases this
    /// participant only, in its own transaction; the attempt is cancelled when no participant remains. NotFound for an unknown
    /// or foreign attempt; InvalidState after this participant's start (or when the attempt runs with another participant); a
    /// repeat returns the stored outcome.
    /// </summary>
    public async Task<ReleaseOutcome> CancelAsync(long accountId, Guid attemptId, CancellationToken ct = default)
    {
        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var batch = await ReleaseParticipantsLockedAsync(conn, tx, attemptId, [accountId], CancelReasons.PlayerCancel, PreStartStates, ct)
            ?? throw new AttemptException(AttemptError.NotFound);
        var mine = batch.Participants.Single();
        if (mine.Outcome is null) throw new AttemptException(AttemptError.InvalidState); // disposing rolls back (nothing was written)
        await tx.CommitAsync(ct);
        return mine.Outcome;
    }

    /// <summary>One participant in a <see cref="ReleaseBatch"/>: its outcome (released now, or the stored release), or null when
    /// it could not be released (it started, or the attempt was not in an allowed state while it was still admitted).</summary>
    public sealed record ParticipantRelease(long AccountId, ReleaseOutcome? Outcome);

    /// <param name="StateBefore">The attempt's state when locked.</param>
    /// <param name="AttemptState">The attempt's state after this release.</param>
    public sealed record ReleaseBatch(string StateBefore, string AttemptState, IReadOnlyList<ParticipantRelease> Participants, bool AnyStarted)
    {
        public bool ReleasedAny => Participants.Any(p => p.Outcome is { Replayed: false });
    }

    /// <summary>
    /// Releases the named participants of <paramref name="attemptId"/> (all of them when <paramref name="only"/> is null) that
    /// are still admitted, inside the caller's READ COMMITTED transaction (never commits), when the attempt is in one of
    /// <paramref name="allowed"/>. Null when the attempt doesn't exist or (with <paramref name="only"/>) an account isn't its
    /// participant: nothing is locked or written. Lock order (Global Constraints, Release; spec §15.3), in ascending account id
    /// throughout, the participant set read unlocked first (insert-only, fixed at admission):
    /// <list type="number">
    /// <item>the participants' accounts FOR KEY SHARE (a positive release writes a wallet twice, and the second write re-checks
    /// the account foreign key: taken after the wallet and the policy lock, it could deadlock with a same-account grant);</item>
    /// <item>the attempt FOR UPDATE;</item>
    /// <item>every participant of the attempt FOR UPDATE (whether any remains decides the attempt's cancellation);</item>
    /// <item>the released participants' holds FOR UPDATE, then their inventories rows FOR UPDATE (the inventory lock changes
    /// while they are held);</item>
    /// <item>every positive hold's wallet FOR UPDATE, all before any materialization; then each materialization
    /// (<see cref="EnergyLedgerWriter.MaterializeLockedAsync"/>: the shared policy lock, then the clock), the checked credit,
    /// one wallet UPDATE and the <c>release:&lt;attempt&gt;</c> entry per account;</item>
    /// <item>when no participant remains active or started, the attempt <c>→ cancelled</c> with <paramref name="reason"/> and
    /// <c>ended_at</c>; then each participant <c>admitted → released</c> with its release reason, and each hold
    /// <c>reserved → released</c>.</item>
    /// </list>
    /// A participant already released returns its stored outcome (Replayed); one that started, or any participant while the
    /// attempt is outside <paramref name="allowed"/>, gets a null outcome and nothing is written for it.
    /// </summary>
    public static async Task<ReleaseBatch?> ReleaseParticipantsLockedAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid attemptId, IReadOnlyCollection<long>? only,
        string reason, IReadOnlyCollection<string> allowed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(tx);
        if (!CancelReasons.All.Contains(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        EnergyPolicies.RequireReadCommitted(tx);

        var all = (await conn.QueryAsync<long>(new CommandDefinition(
            "select account_id from attempt_participants where attempt_id = @attemptId order by account_id", new { attemptId }, tx, cancellationToken: ct))).ToArray();
        if (all.Length == 0 || (only is not null && only.Any(a => Array.IndexOf(all, a) < 0))) return null;
        var targets = (only ?? all).Distinct().Order().ToArray();

        await conn.ExecuteAsync(new CommandDefinition(
            "select 1 from accounts where account_id = any(@targets) order by account_id for key share", new { targets }, tx, cancellationToken: ct));
        var state = await conn.QuerySingleAsync<string>(new CommandDefinition(
            "select state from mission_attempts where attempt_id = @attemptId for update", new { attemptId }, tx, cancellationToken: ct));
        var participants = (await conn.QueryAsync<(long AccountId, string State, string? ReleaseReason)>(new CommandDefinition(
            "select account_id, state, release_reason from attempt_participants where attempt_id = @attemptId order by account_id for update",
            new { attemptId }, tx, cancellationToken: ct))).ToDictionary(p => p.AccountId);
        var anyStarted = participants.Values.Any(p => StartedStates.Contains(p.State));

        var releasable = allowed.Contains(state) ? targets.Where(a => participants[a].State == "admitted").ToArray() : [];
        var holds = (await conn.QueryAsync<(long AccountId, int Amount, string State)>(new CommandDefinition(
            "select account_id, amount, state from energy_holds where attempt_id = @attemptId and account_id = any(@targets) order by account_id for update",
            new { attemptId, targets }, tx, cancellationToken: ct))).ToDictionary(h => h.AccountId);

        var results = new Dictionary<long, ReleaseOutcome?>();
        foreach (var account in targets.Except(releasable))
        {
            var p = participants[account];
            results[account] = p.State == "released" && holds[account].State == "released"
                ? new ReleaseOutcome("released", holds[account].Amount, await ReleasedBalanceAsync(conn, tx, attemptId, account, holds[account].Amount, ct),
                    p.ReleaseReason ?? throw new InvalidOperationException("A released participant has no release reason."), Replayed: true)
                : null;
        }
        var before = state;
        if (releasable.Length == 0) return new ReleaseBatch(before, state, targets.Select(a => new ParticipantRelease(a, results[a])).ToList(), anyStarted);

        foreach (var account in releasable)
            if (holds[account].State != "reserved") throw new InvalidOperationException("An admitted participant's hold is not reserved.");
        await conn.ExecuteAsync(new CommandDefinition(
            "select 1 from inventories where account_id = any(@releasable) order by account_id for update", new { releasable }, tx, cancellationToken: ct));

        var positive = releasable.Where(a => holds[a].Amount > 0).ToArray();
        var balances = new Dictionary<long, int>();
        if (positive.Length > 0)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "select 1 from energy_wallets where account_id = any(@positive) order by account_id for update", new { positive }, tx, cancellationToken: ct));
            foreach (var account in positive)
            {
                var materialized = await EnergyLedgerWriter.MaterializeLockedAsync(conn, tx, account, ct)
                    ?? throw new InvalidOperationException("A positive energy hold has no wallet to release into.");
                var after = EnergyMath.Credit(materialized.Wallet.Balance, holds[account].Amount);
                await ExactlyOneAsync(conn, tx, ct, "update energy_wallets set balance = @after where account_id = @account", new { account, after });
                await ExactlyOneAsync(conn, tx, ct,
                    """
                    insert into energy_ledger (account_id, attempt_id, kind, delta, balance_after, idempotency_key)
                    values (@account, @attemptId, 'release', @delta, @after, @key)
                    """, new { account, attemptId, delta = holds[account].Amount, after, key = ReleaseKey(attemptId) });
                balances[account] = after;
            }
        }

        // The attempt is cancelled when nothing remains to play: every participant released, none started.
        var remaining = participants.Values.Any(p => !releasable.Contains(p.AccountId) && (p.State == "admitted" || StartedStates.Contains(p.State)));
        if (!remaining)
        {
            await ExactlyOneAsync(conn, tx, ct,
                "update mission_attempts set state = 'cancelled', cancel_reason = @reason, ended_at = clock_timestamp() where attempt_id = @attemptId and state = any(@allowed)",
                new { attemptId, reason, allowed = allowed.ToArray() });
            state = "cancelled";
        }
        foreach (var account in releasable)
        {
            await ExactlyOneAsync(conn, tx, ct,
                "update attempt_participants set state = 'released', release_reason = @reason where attempt_id = @attemptId and account_id = @account and state = 'admitted'",
                new { attemptId, account, reason });
            await ExactlyOneAsync(conn, tx, ct,
                "update energy_holds set state = 'released' where attempt_id = @attemptId and account_id = @account and state = 'reserved'",
                new { attemptId, account });
            results[account] = new ReleaseOutcome("released", holds[account].Amount, balances.TryGetValue(account, out var b) ? b : null, reason, Replayed: false);
        }
        return new ReleaseBatch(before, state, targets.Select(a => new ParticipantRelease(a, results[a])).ToList(), anyStarted);
    }

    /// <summary>
    /// The attempts past <paramref name="kind"/>'s deadline by the database clock (a deadline exactly at now is due), oldest
    /// deadline first, at most <paramref name="limit"/>, minus <paramref name="excluded"/>, selected FOR UPDATE SKIP LOCKED in
    /// a short transaction that commits at once (an attempt a release is working on is skipped, never waited for). Assign:
    /// admitted. Launch: assigned or launching. Join: ready or running with a participant still admitted. Never any other
    /// state: a running attempt is never released here (D-11), only its unstarted participants by the join deadline.
    /// </summary>
    public async Task<IReadOnlyList<DueAttempt>> DueAsync(DeadlineKind kind, int limit, Guid[]? excluded = null, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var sql = kind switch
        {
            DeadlineKind.Assign => """
                select attempt_id, created_by from mission_attempts
                where state = 'admitted' and assign_deadline <= clock_timestamp() and attempt_id <> all(@excluded)
                order by assign_deadline limit @limit for update skip locked
                """,
            DeadlineKind.Launch => """
                select attempt_id, created_by from mission_attempts
                where state in ('assigned', 'launching') and launch_deadline <= clock_timestamp() and attempt_id <> all(@excluded)
                order by launch_deadline limit @limit for update skip locked
                """,
            DeadlineKind.Join => """
                select a.attempt_id, a.created_by from mission_attempts a
                where a.state in ('ready', 'running') and a.join_deadline <= clock_timestamp() and a.attempt_id <> all(@excluded)
                  and exists (select 1 from attempt_participants p where p.attempt_id = a.attempt_id and p.state = 'admitted')
                order by a.join_deadline limit @limit for update of a skip locked
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var due = (await conn.QueryAsync<(Guid AttemptId, long AccountId)>(new CommandDefinition(sql, new { limit, excluded = excluded ?? [] }, tx, cancellationToken: ct)))
            .Select(r => new DueAttempt(r.AttemptId, r.AccountId, kind)).ToList();
        await tx.CommitAsync(ct);
        return due;
    }

    /// <summary>The admitted attempts past their assignment deadline (Stage 7's selection).</summary>
    public Task<IReadOnlyList<DueAttempt>> DueAsync(int limit, CancellationToken ct = default) => DueAsync(DeadlineKind.Assign, limit, null, ct);

    /// <summary>
    /// The deadline release of one selected attempt, in its own transaction: every participant still admitted, with
    /// <c>no_capacity</c>, <c>launch_timeout</c> or <c>join_timeout</c>. It re-locks the attempt, so a release that committed in
    /// between makes this the stored outcome (AlreadyEnded), and an attempt that moved past the deadline's states is left
    /// alone (NotAdmitted, rolled back).
    /// </summary>
    public async Task<ExpireResult> ExpireAsync(DueAttempt due, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(due);
        var (reason, allowed) = due.Kind switch
        {
            DeadlineKind.Assign => (CancelReasons.NoCapacity, new[] { "admitted" }),
            DeadlineKind.Launch => (CancelReasons.LaunchTimeout, new[] { "assigned", "launching" }),
            DeadlineKind.Join => (CancelReasons.JoinTimeout, new[] { "ready", "running" }),
            _ => throw new ArgumentOutOfRangeException(nameof(due)),
        };
        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var batch = await ReleaseParticipantsLockedAsync(conn, tx, due.AttemptId, null, reason, allowed, ct)
            ?? throw new InvalidOperationException("A due attempt has no participants.");
        if (batch.ReleasedAny)
        {
            await tx.CommitAsync(ct);
            return ExpireResult.Released;
        }
        return batch.AttemptState == "cancelled" && batch.Participants.All(p => p.Outcome is { Replayed: true }) ? ExpireResult.AlreadyEnded : ExpireResult.NotAdmitted;
    }

    /// <summary>One sweep with no memory of earlier failures (tests and one-off runs).</summary>
    public Task<SweepReport> SweepAsync(int limit, CancellationToken ct = default) => SweepAsync(limit, new SweepCooldown(), ct);

    /// <summary>
    /// One deadline sweep: the assign, launch and join selections (each at most <paramref name="limit"/>, minus the attempts
    /// cooling down), then <see cref="ExpireAsync"/> for each due attempt, each in its own transaction. A release that fails
    /// rolls back alone, is reported by its safe description and cools down; the others still run. A failing selection
    /// propagates. Cancellation propagates.
    /// </summary>
    public async Task<SweepReport> SweepAsync(int limit, SweepCooldown cooldown, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cooldown);
        cooldown.BeginSweep();
        var excluded = cooldown.Excluded();
        var due = new List<DueAttempt>();
        foreach (var kind in new[] { DeadlineKind.Assign, DeadlineKind.Launch, DeadlineKind.Join })
            due.AddRange(await DueAsync(kind, limit, excluded, ct));
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
                cooldown.Succeeded(attempt.AttemptId);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (NpgsqlException e) { failures.Add(DbFailure.SafeDescription(e)); cooldown.Failed(attempt.AttemptId); }
            catch (Exception e) { failures.Add(e.GetType().Name); cooldown.Failed(attempt.AttemptId); } // a bug in one release must not stop the others
        }
        var unresponsive = await MarkUnresponsiveAsync(limit, ct);
        return new SweepReport(due.Count, released, ended, notAdmitted, failures, unresponsive);
    }

    /// <summary>
    /// The process lease (spec §7.6): running attempts whose last process heartbeat (or, before the first, their start) is older
    /// than the lease by the database clock become unresponsive, at most <paramref name="limit"/> per sweep, in one short
    /// transaction that locks only those attempt rows (SKIP LOCKED). Nothing else changes: no participant, hold, energy or slot;
    /// the next process heartbeat makes the attempt running again. Never a void (D-11).
    /// </summary>
    public async Task<int> MarkUnresponsiveAsync(int limit, CancellationToken ct = default)
    {
        await using var conn = await DbFailure.OpenAsync(db, ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            """
            update mission_attempts set state = 'unresponsive'
            where state = 'running' and attempt_id in (
              select attempt_id from mission_attempts
              where state = 'running' and coalesce(last_process_heartbeat_at, started_at) < clock_timestamp() - make_interval(secs => @lease)
              order by coalesce(last_process_heartbeat_at, started_at) limit @limit for update skip locked)
            """, new { lease = (double)processLeaseSeconds, limit }, cancellationToken: ct));
    }

    /// <summary>The ledger key of a participant's release (0006/0007: unique per account, naming the attempt).</summary>
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
        string? InsurancePolicyId, int? InsurancePolicyVersion, DateTime? LaunchDeadline, DateTime? JoinDeadline, string? HostId, int? MissionPort);
}
