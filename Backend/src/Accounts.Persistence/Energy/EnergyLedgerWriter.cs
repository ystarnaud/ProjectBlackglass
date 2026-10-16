using Dapper;
using Npgsql;
using Blackglass.Accounts.Domain.Energy;

namespace Blackglass.Accounts.Persistence.Energy;

/// <summary>A wallet brought up to <paramref name="Now"/> (the database clock read after the locks), measured under
/// <paramref name="Current"/>; <paramref name="Gained"/> is what this materialization credited (0 when nothing).</summary>
public sealed record Materialized(WalletState Wallet, EnergyPolicy Current, DateTime Now, int Gained);

/// <summary>
/// Writes regeneration to a wallet and its ledger, inside the caller's READ COMMITTED transaction (plan §A, E2, E6).
/// Rules every energy writer follows (Global Constraints, Locking and Ledger):
/// <list type="bullet">
/// <item>Lock the wallet row FOR UPDATE first, then take the shared policy lock, then read the clock and the policies in one
/// later statement: no regeneration uses a clock read before a lock wait, or a policy set that could still change.</item>
/// <item>After taking the shared policy lock, wait only on (a) the transaction's own rows, (b) policy rows (never
/// write-locked), or (c) a transaction that already holds the shared lock and has no further waits (a concurrent first use of
/// one grant key); never on a transaction that has not yet been granted the lock. Each grant inserts exactly one global key,
/// which keeps (c) at depth 1; a writer that inserts several global keys after the lock could deadlock with another holder.</item>
/// <item>Insert ledger entries only while holding the wallet row lock: the chain trigger and the commit check compare with the
/// account's latest entry, which is exact only when entry ids follow that lock.</item>
/// <item>Write a wallet's balance, anchor, progress and version together, in one UPDATE (the progress trigger checks the
/// progress against the interval of the version written with it).</item>
/// <item>A 23514 from an energy constraint is a bug, never busy: it propagates unchanged, and nothing here retries.</item>
/// </list>
/// </summary>
public static class EnergyLedgerWriter
{
    /// <summary>
    /// The wallet FOR UPDATE (null, with nothing locked or written, when there is none); then the shared policy lock, the
    /// clock and the policies from the wallet's version; <see cref="EnergyMath.Materialize"/>. An unchanged wallet is not
    /// written. A changed one is updated in one statement; when Gained &gt; 0 the <c>regen</c> entry follows, keyed by the
    /// consumed anchor, with delta = Gained and balance_after = the new balance (a change of anchor, progress or version
    /// alone writes no entry: the balance, and so the commit check, is unchanged). Never commits. InvalidOperationException
    /// outside READ COMMITTED.
    /// </summary>
    public static async Task<Materialized?> MaterializeLockedAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long accountId, CancellationToken ct)
    {
        EnergyPolicies.RequireReadCommitted(tx);
        var wallet = await conn.QuerySingleOrDefaultAsync<WalletState>(new CommandDefinition(
            """
            select balance as Balance, regen_anchor as RegenAnchor, regen_progress_us as ProgressMicros, policy_version as PolicyVersion
            from energy_wallets where account_id = @accountId for update
            """, new { accountId }, tx, cancellationToken: ct));
        if (wallet is null) return null;

        var (now, policies) = await EnergyPolicies.LockSharedAndReadFromAsync(conn, tx, wallet.PolicyVersion, ct);
        var regeneration = EnergyMath.Materialize(wallet, policies, now);
        var after = regeneration.After;
        var current = policies.Single(p => p.Version == after.PolicyVersion);
        if (after == wallet) return new Materialized(wallet, current, now, 0);

        var updated = await conn.ExecuteAsync(new CommandDefinition(
            """
            update energy_wallets set balance = @Balance, regen_anchor = @RegenAnchor, regen_progress_us = @ProgressMicros, policy_version = @PolicyVersion
            where account_id = @accountId
            """, new { accountId, after.Balance, after.RegenAnchor, after.ProgressMicros, after.PolicyVersion }, tx, cancellationToken: ct));
        if (updated != 1) throw new InvalidOperationException("The locked energy wallet was not updated exactly once.");
        if (regeneration.Gained > 0)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "insert into energy_ledger (account_id, kind, delta, balance_after, idempotency_key) values (@accountId, 'regen', @delta, @balanceAfter, @key)",
                new { accountId, delta = regeneration.Gained, balanceAfter = after.Balance, key = EnergyMath.RegenKey(regeneration.ConsumedAnchor!.Value) },
                tx, cancellationToken: ct));
        }
        return new Materialized(after, current, now, regeneration.Gained);
    }
}
