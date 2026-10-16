using System.Data;
using Dapper;
using Npgsql;
using Blackglass.Accounts.Domain.Energy;

namespace Blackglass.Accounts.Persistence.Energy;

/// <summary>
/// The energy policy versions and the publication lock (owner ruling E2; plan §A, "the transition algorithm").
/// Publication takes <see cref="PublicationLockKey"/> exclusively (0006's <c>energy_policy_effective_at</c> trigger takes it
/// itself before it reads the clock for <c>effective_at</c>); every materialization and projection takes it shared, after its
/// wallet lock, in a READ COMMITTED transaction, and only then reads the clock and the policies, in one later statement. Every
/// publication that committed before the shared lock was granted is then visible to that statement, and none can commit
/// while the lock is held, so no reader ever sees a <c>now</c> later than a version it did not see. The lock is a leaf: a
/// holder takes no further lock that a queued publisher or another shared holder could hold.
/// </summary>
public static class EnergyPolicies
{
    /// <summary>"BlkEnPol": the transaction advisory lock of policy publication. It MUST equal the constant in 0006's
    /// <c>energy_policy_effective_at</c> (4786318449935675244).</summary>
    public const long PublicationLockKey = 0x426C_6B45_6E50_6F6C;

    /// <summary>
    /// Takes the shared publication lock (its own statement), then reads <c>clock_timestamp()</c> and every version with
    /// Version &gt;= <paramref name="fromVersion"/>, ascending, in one statement that begins after the lock is held: the
    /// clock is the caller's <c>now</c>, and the versions are final for it. Requires a READ COMMITTED transaction (a
    /// REPEATABLE READ snapshot could predate the lock wait); InvalidOperationException otherwise, before any statement.
    /// Never opens, commits or rolls back.
    /// </summary>
    internal static async Task<(DateTime Now, IReadOnlyList<EnergyPolicy> Policies)> LockSharedAndReadFromAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, int fromVersion, CancellationToken ct)
    {
        RequireReadCommitted(tx);
        await conn.ExecuteAsync(new CommandDefinition(
            "select pg_advisory_xact_lock_shared(@key)", new { key = PublicationLockKey }, tx, cancellationToken: ct));
        // The materialized CTE evaluates the volatile clock once, for every row.
        var rows = (await conn.QueryAsync<PolicyRow>(new CommandDefinition(
            """
            with clock as materialized (select clock_timestamp() as now)
            select clock.now as Now, p.version as Version, p.cap as Cap, p.opening_balance as OpeningBalance, p.regen_interval as RegenInterval,
              p.regen_amount as RegenAmount, p.void_refund_pct as VoidRefundPct, p.effective_at as EffectiveAt
            from clock cross join energy_policies p
            where p.version >= @fromVersion
            order by p.version
            """, new { fromVersion }, tx, cancellationToken: ct))).ToList();
        if (rows.Count == 0 || rows[0].Version != fromVersion)
            throw new InvalidOperationException($"Energy policy version {fromVersion} is missing.");
        var now = Utc(rows[0].Now);
        var policies = rows.Select(r => new EnergyPolicy(r.Version, r.Cap, r.OpeningBalance, r.RegenInterval, r.RegenAmount, r.VoidRefundPct, Utc(r.EffectiveAt))).ToList();
        return (now, policies);
    }

    /// <summary>The advisory-lock protocol and the ledger chain trigger both rely on READ COMMITTED (each statement sees what
    /// committed before it began).</summary>
    internal static void RequireReadCommitted(NpgsqlTransaction tx)
    {
        ArgumentNullException.ThrowIfNull(tx);
        if (tx.IsolationLevel != IsolationLevel.ReadCommitted)
            throw new InvalidOperationException($"Energy writes need a READ COMMITTED transaction, not {tx.IsolationLevel}.");
    }

    /// <summary>Npgsql reads timestamptz as UTC; anything else is a broken mapping, never silently converted.</summary>
    static DateTime Utc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : throw new InvalidOperationException($"A timestamptz was read as {value.Kind}, not UTC.");

    sealed record PolicyRow(DateTime Now, int Version, int Cap, int OpeningBalance, TimeSpan RegenInterval, int RegenAmount, int VoidRefundPct, DateTime EffectiveAt);
}
