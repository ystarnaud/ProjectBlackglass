using Dapper;
using Npgsql;
using Blackglass.Accounts.Persistence.Player;

namespace Blackglass.Accounts.IntegrationTests.Support;

/// <summary>Every account is in a mission. Stage 5 production code never locks anyone, so tests substitute this.</summary>
public sealed class AlwaysLocked : IInventoryLock
{
    public int Calls { get; private set; }

    public Task<bool> IsLockedAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long accountId, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(true);
    }
}

/// <summary>
/// Reports <paramref name="answer"/> and, when it is called, probes the account's inventories row from a second connection
/// with <c>for update nowait</c>. SqlState 55P03 (lock_not_available) means the caller's transaction holds the row lock.
/// </summary>
public sealed class RowProbe(NpgsqlDataSource probeSource, bool answer = false) : IInventoryLock
{
    /// <summary>The probe's outcome per call: the SqlState of the failure, or "free" when the row could be locked.</summary>
    public List<string> Outcomes { get; } = [];

    public async Task<bool> IsLockedAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long accountId, CancellationToken ct)
    {
        await using var probe = await probeSource.OpenConnectionAsync(ct);
        await using var probeTx = await probe.BeginTransactionAsync(ct);
        try
        {
            await probe.ExecuteAsync("select 1 from inventories where account_id = @accountId for update nowait", new { accountId }, probeTx);
            Outcomes.Add("free");
        }
        catch (PostgresException e) { Outcomes.Add(e.SqlState); }
        return answer;
    }
}
