using Dapper;
using Npgsql;
using Blackglass.Accounts.Domain.Energy;

namespace Blackglass.Accounts.Persistence.Energy;

/// <summary>The outcome of opening a wallet. Opened is false when the wallet already existed: then Balance and PolicyVersion
/// are advisory, read without the wallet's row lock (the policy lock is a leaf), so a materialization committing at the same
/// time can leave them one regeneration behind. Report them as "already opened", never as the wallet's authoritative state.</summary>
public sealed record WalletOpen(bool Opened, int Balance, int PolicyVersion);

/// <summary>
/// Opens an account's wallet (owner rulings E1, E3) inside the caller's READ COMMITTED transaction: it never opens, commits
/// or rolls one back, so a failure anywhere leaves nothing once the caller rolls back. The caller holds the accounts row
/// (just inserted at account creation, or FOR UPDATE for <c>energy open</c>), which serializes openers of one account.
/// Lock order: that row, then the shared policy lock, then the clock and the policies (one statement), then the wallet
/// insert. The advisory lock is a leaf, so the existing wallet is read without a row lock.
/// </summary>
public static class EnergyProvisioner
{
    /// <summary>
    /// Picks <see cref="EnergyMath.ApplicableAt"/> at the database clock and inserts the wallet with that version's opening
    /// balance, anchored at the clock (on conflict do nothing); when it inserted, also the <c>open</c> entry with delta =
    /// balance_after = the opening balance. When the wallet already exists nothing is written, and the Balance and
    /// PolicyVersion returned are an unlocked, advisory read (see <see cref="WalletOpen"/>). InvalidOperationException outside
    /// READ COMMITTED.
    /// </summary>
    public static async Task<WalletOpen> OpenAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long accountId, CancellationToken ct)
    {
        EnergyPolicies.RequireReadCommitted(tx);
        var (now, policies) = await EnergyPolicies.LockSharedAndReadFromAsync(conn, tx, fromVersion: 1, ct);
        var wallet = EnergyMath.Open(EnergyMath.ApplicableAt(policies, now), now);
        var inserted = await conn.ExecuteAsync(new CommandDefinition(
            """
            insert into energy_wallets (account_id, balance, regen_anchor, regen_progress_us, policy_version)
            values (@accountId, @Balance, @RegenAnchor, @ProgressMicros, @PolicyVersion)
            on conflict (account_id) do nothing
            """, new { accountId, wallet.Balance, wallet.RegenAnchor, wallet.ProgressMicros, wallet.PolicyVersion }, tx, cancellationToken: ct));
        if (inserted == 0)
        {
            var stored = await conn.QuerySingleAsync<(int Balance, int PolicyVersion)>(new CommandDefinition(
                "select balance, policy_version from energy_wallets where account_id = @accountId", new { accountId }, tx, cancellationToken: ct));
            return new WalletOpen(false, stored.Balance, stored.PolicyVersion);
        }
        // Inserted while this transaction holds the new wallet row, as every ledger entry must be (the chain trigger).
        await conn.ExecuteAsync(new CommandDefinition(
            "insert into energy_ledger (account_id, kind, delta, balance_after, idempotency_key) values (@accountId, 'open', @balance, @balance, 'open')",
            new { accountId, balance = wallet.Balance }, tx, cancellationToken: ct));
        return new WalletOpen(true, wallet.Balance, wallet.PolicyVersion);
    }
}
