using Dapper;
using Npgsql;
using Blackglass.Accounts.Persistence.Attempts;

namespace Blackglass.Accounts.Persistence.Player;

/// <summary>
/// Answers whether an account is in a mission, and so cannot change its inventory or advance an operative (owner rulings
/// S5-D5 and S7-D4). It is asked on the caller's own connection and transaction: by a pick or an inventory edit while the
/// account's inventories row lock is held (so the answer cannot change under the operation), and by the inventory read inside
/// its snapshot. An implementation reads only; it never writes and never takes another lock. Production has exactly one
/// implementation, <see cref="ParticipationLock"/>; the interface exists so tests can force an answer.
/// </summary>
public interface IInventoryLock
{
    Task<bool> IsLockedAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long accountId, CancellationToken ct);
}

/// <summary>
/// The mandatory, database-backed inventory lock (S7-D4): an account is locked while it has a participation in any of
/// <see cref="AttemptStates.ActiveParticipant"/>, so only an authoritative terminal transition (<c>released</c>, later
/// <c>settled</c>) unlocks it. Release takes the inventories row lock after the participant, so an edit or a pick waiting on
/// that lock sees the terminal state.
/// <para>
/// Fail-closed: nothing here catches. A failed query (a missing privilege, a broken connection) or one that waits past the
/// connection's lock or statement timeout propagates, so the caller answers 500 or 503, never "unlocked" and never
/// <c>locked: false</c>. No retry.
/// </para>
/// </summary>
public sealed class ParticipationLock : IInventoryLock
{
    /// <summary>The whole check, with the active states as one array parameter: <c>@accountId</c>, <c>@activeStates</c>.</summary>
    public const string Query =
        "select exists (select 1 from attempt_participants where account_id = @accountId and state = any(@activeStates))";

    static readonly string[] ActiveStates = [.. AttemptStates.ActiveParticipant];

    public async Task<bool> IsLockedAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long accountId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(tx);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            Query, new { accountId, activeStates = ActiveStates }, tx, cancellationToken: ct));
    }
}
