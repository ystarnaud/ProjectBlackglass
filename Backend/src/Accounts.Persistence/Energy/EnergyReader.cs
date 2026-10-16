using Dapper;
using Npgsql;
using Blackglass.Accounts.Domain.Energy;

namespace Blackglass.Accounts.Persistence.Energy;

/// <summary>A wallet projected to the database clock: the balance (which may exceed the cap, E6), the cap of the policy the
/// projection ends on, and when the next unit lands (UTC), or null at or above that cap, where regeneration is paused.</summary>
public sealed record EnergyView(int Balance, int Cap, DateTime? NextRegenAt);

/// <summary>
/// The read side of energy (<c>GET /v1/me</c>; owner rulings E2, E3; plan Checkpoint 1 F1). It never writes: the projection
/// is <see cref="EnergyMath.Materialize"/> in memory, and no wallet, entry or anchor changes. An account without a wallet is
/// a null view, never a zero balance, and nothing is created.
/// </summary>
public static class EnergyReader
{
    /// <summary>What <see cref="LockSharedAndReadMeAsync"/> read: the inventory version (null without a provisioned
    /// inventory), the database clock, the wallet (null without one) and the policies from the wallet's version upward
    /// (every version when there is no wallet), ascending.</summary>
    internal sealed record MeSnapshot(long? InventoryVersion, DateTime Now, WalletState? Wallet, IReadOnlyList<EnergyPolicy> Policies);

    /// <summary>
    /// Takes the shared publication lock (its own statement), then reads the inventory version, the wallet,
    /// <c>clock_timestamp()</c> and the policies in ONE later statement, without locking any row. The same rationale as
    /// <see cref="EnergyPolicies.LockSharedAndReadFromAsync"/>, which cannot serve here because it needs the wallet's version
    /// first, and a second statement would be a second snapshot:
    /// <list type="bullet">
    /// <item>In READ COMMITTED each statement takes its snapshot when it begins, so this one is taken after the lock is held:
    /// every publication committed before the lock was granted is visible, none can commit while it is held, and the clock is
    /// read in that same statement. A REPEATABLE READ snapshot would be taken at the lock statement, before the wait, and a
    /// publication committed during the wait would be missing while the clock is read later: a retroactive projection.</item>
    /// <item>One statement is one snapshot, so the inventory version and the wallet always come from the same instant: a
    /// write that commits during the read is seen whole or not at all.</item>
    /// </list>
    /// Requires a READ COMMITTED transaction; InvalidOperationException otherwise, before any statement. Never opens, commits
    /// or rolls back.
    /// </summary>
    internal static async Task<MeSnapshot> LockSharedAndReadMeAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long accountId, CancellationToken ct)
    {
        EnergyPolicies.RequireReadCommitted(tx);
        await conn.ExecuteAsync(new CommandDefinition(
            "select pg_advisory_xact_lock_shared(@key)", new { key = EnergyPolicies.PublicationLockKey }, tx, cancellationToken: ct));
        // The materialized CTE evaluates the volatile clock once, for every row. The outer joins keep one row (the clock and the
        // inventory version) when there is no wallet; the policies start at the wallet's version, or at the first without one.
        var rows = (await conn.QueryAsync<MeRow>(new CommandDefinition(
            """
            with clock as materialized (select clock_timestamp() as now),
            wallet as (
              select balance, regen_anchor, regen_progress_us, policy_version from energy_wallets where account_id = @accountId)
            select clock.now as Now,
              (select i.version from inventories i where i.account_id = @accountId and i.starter_granted) as InventoryVersion,
              w.balance as Balance, w.regen_anchor as RegenAnchor, w.regen_progress_us as ProgressMicros, w.policy_version as PolicyVersion,
              p.version as Version, p.cap as Cap, p.opening_balance as OpeningBalance, p.regen_interval as RegenInterval,
              p.regen_amount as RegenAmount, p.void_refund_pct as VoidRefundPct, p.effective_at as EffectiveAt
            from clock
              left join wallet w on true
              left join energy_policies p on p.version >= coalesce(w.policy_version, 1)
            order by p.version
            """, new { accountId }, tx, cancellationToken: ct))).ToList();

        var first = rows[0];
        var now = Utc(first.Now);
        var policies = rows.Where(r => r.Version is not null)
            .Select(r => new EnergyPolicy(r.Version!.Value, r.Cap!.Value, r.OpeningBalance!.Value, r.RegenInterval!.Value, r.RegenAmount!.Value,
                r.VoidRefundPct!.Value, Utc(r.EffectiveAt!.Value)))
            .ToList();
        WalletState? wallet = null;
        if (first.PolicyVersion is { } version)
        {
            if (policies.Count == 0 || policies[0].Version != version)
                throw new InvalidOperationException($"Energy policy version {version} is missing.");
            wallet = new WalletState(first.Balance!.Value, Utc(first.RegenAnchor!.Value), first.ProgressMicros!.Value, version);
        }
        return new MeSnapshot(first.InventoryVersion, now, wallet, policies);
    }

    /// <summary>
    /// The wallet brought up to <paramref name="now"/> in memory (<see cref="EnergyMath.Materialize"/>), with the cap of the
    /// policy it ends on and <see cref="EnergyMath.NextRegenAt"/> under that policy. <paramref name="policies"/> is the
    /// wallet's version followed by every later one, ascending. Pure: nothing is written.
    /// </summary>
    public static EnergyView Project(WalletState wallet, IReadOnlyList<EnergyPolicy> policies, DateTime now)
    {
        var after = EnergyMath.Materialize(wallet, policies, now).After;
        var current = policies.Single(p => p.Version == after.PolicyVersion);
        return new EnergyView(after.Balance, current.Cap, EnergyMath.NextRegenAt(after, current, now));
    }

    /// <summary>Npgsql reads timestamptz as UTC; anything else is a broken mapping, never silently converted.</summary>
    static DateTime Utc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : throw new InvalidOperationException($"A timestamptz was read as {value.Kind}, not UTC.");

    sealed record MeRow(DateTime Now, long? InventoryVersion, int? Balance, DateTime? RegenAnchor, long? ProgressMicros, int? PolicyVersion,
        int? Version, int? Cap, int? OpeningBalance, TimeSpan? RegenInterval, int? RegenAmount, int? VoidRefundPct, DateTime? EffectiveAt);
}
