using Npgsql;

namespace Blackglass.Accounts.Persistence.Player;

/// <summary>
/// Answers whether an account is in a mission, and so cannot change its inventory or advance an operative (owner ruling
/// S5-D5). It is asked on the caller's own connection and transaction: by a pick or an inventory edit while the account's
/// inventories row lock is held (so the answer cannot change under the operation), and by the inventory read inside its
/// snapshot. An implementation reads only; it never writes and never takes another lock.
/// </summary>
public interface IInventoryLock
{
    Task<bool> IsLockedAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long accountId, CancellationToken ct);
}

/// <summary>
/// Stage 5: mission participations do not exist yet (spec section 7.1, Stage 7), so no account is ever locked.
/// Stage 7 MUST replace this with the PostgreSQL participation query before live mission admission: until then an account
/// deployed in a mission could still change its inventory.
/// </summary>
public sealed class NoParticipationsYet : IInventoryLock
{
    public Task<bool> IsLockedAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long accountId, CancellationToken ct) =>
        Task.FromResult(false);
}
