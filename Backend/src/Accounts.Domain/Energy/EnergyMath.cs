using System.Globalization;

namespace Blackglass.Accounts.Domain.Energy;

/// <summary>Raised when a credit would take a balance past int.MaxValue. Sums are checked, never wrapped or clamped.</summary>
public sealed class EnergyLimitException(string message) : Exception(message);

/// <summary>
/// The energy rules: lazy regeneration with non-retroactive policy transitions (E2) and a cap that limits natural
/// regeneration only (E6). Times are UTC with whole microseconds and are handled internally as long microseconds; the
/// products that can pass long (the proportional step, n × amount) use Int128. Pure: no database, no clock.
/// </summary>
public static class EnergyMath
{
    static readonly long MaxIntervalMicros = TimeSpan.FromDays(7).Ticks / TicksPerMicro;
    const long TicksPerMicro = 10;

    /// <summary>
    /// Brings the wallet up to `now` (plan §A, "the transition algorithm"). `policies` is the wallet's own version followed
    /// by every later version, ascending by Version with strictly increasing EffectiveAt. Each later version effective at
    /// or before `now` is applied in order: time up to its EffectiveAt is credited under the previous version, progress is
    /// rescaled proportionally (floored), and the wallet moves to it. A clock at or behind the anchor accounts no time, so
    /// with no transition the very same wallet comes back. ArgumentException on any malformed input.
    /// </summary>
    public static Regeneration Materialize(WalletState wallet, IReadOnlyList<EnergyPolicy> policies, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(wallet);
        var nowMicros = Micros(now, nameof(now));
        ValidatePolicies(policies, nameof(policies));
        var anchorMicros = Micros(wallet.RegenAnchor, nameof(wallet));
        if (policies[0].Version != wallet.PolicyVersion)
            throw new ArgumentException($"The first policy is version {policies[0].Version}, not the wallet's version {wallet.PolicyVersion}.", nameof(policies));
        if (wallet.Balance < 0) throw new ArgumentException("A wallet balance is never negative.", nameof(wallet));
        if (wallet.ProgressMicros < 0 || wallet.ProgressMicros >= IntervalMicros(policies[0]))
            throw new ArgumentException("Wallet progress must be at least 0 and below its policy's interval.", nameof(wallet));

        var state = new Segment(wallet.Balance, anchorMicros, wallet.ProgressMicros);
        var current = policies[0];
        for (var i = 1; i < policies.Count && Micros(policies[i].EffectiveAt) <= nowMicros; i++)
        {
            var next = policies[i];
            state.Advance(current, Micros(next.EffectiveAt));
            state.Progress = (long)((Int128)state.Progress * IntervalMicros(next) / IntervalMicros(current));
            current = next;
        }
        state.Advance(current, nowMicros);

        var after = new WalletState(state.Balance, FromMicros(state.Anchor), state.Progress, current.Version);
        if (after == wallet) return new Regeneration(wallet, 0, null);
        var gained = checked((int)state.Gained);
        return new Regeneration(after, gained, gained > 0 ? wallet.RegenAnchor : null);
    }

    /// <summary>The latest policy with EffectiveAt &lt;= now, else the first (version 1). `policies` ascending, non-empty.</summary>
    public static EnergyPolicy ApplicableAt(IReadOnlyList<EnergyPolicy> policies, DateTime now)
    {
        var nowMicros = Micros(now, nameof(now));
        ValidatePolicies(policies, nameof(policies));
        var applicable = policies[0];
        foreach (var policy in policies)
        {
            if (Micros(policy.EffectiveAt) > nowMicros) break;
            applicable = policy;
        }
        return applicable;
    }

    /// <summary>A new wallet (E1, E3): the policy's opening balance, anchored at `now`, with no progress.</summary>
    public static WalletState Open(EnergyPolicy policy, DateTime now)
    {
        var nowMicros = Micros(now, nameof(now));
        ValidatePolicy(policy, nameof(policy));
        return new WalletState(policy.OpeningBalance, FromMicros(nowMicros), 0, policy.Version);
    }

    /// <summary>
    /// For a wallet just materialized at `now` under `current` (its own version): when the next unit lands,
    /// now + (interval − progress), or null at or above the cap, where regeneration is paused.
    /// </summary>
    public static DateTime? NextRegenAt(WalletState materialized, EnergyPolicy current, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(materialized);
        var nowMicros = Micros(now, nameof(now));
        ValidatePolicy(current, nameof(current));
        if (materialized.PolicyVersion != current.Version)
            throw new ArgumentException($"The wallet is measured under version {materialized.PolicyVersion}, not {current.Version}.", nameof(current));
        var interval = IntervalMicros(current);
        if (materialized.ProgressMicros < 0 || materialized.ProgressMicros >= interval)
            throw new ArgumentException("Wallet progress must be at least 0 and below its policy's interval.", nameof(materialized));

        if (materialized.Balance >= current.Cap) return null;
        return FromMicros(nowMicros + interval - materialized.ProgressMicros);
    }

    /// <summary>"regen:" + the consumed anchor in microseconds since 1970-01-01T00:00:00Z, invariant digits ('-' before 1970).</summary>
    public static string RegenKey(DateTime consumedAnchor) =>
        "regen:" + (Micros(consumedAnchor, nameof(consumedAnchor)) - UnixEpochMicros).ToString(CultureInfo.InvariantCulture);

    /// <summary>balance + amount. A non-positive amount is an ArgumentOutOfRangeException; a sum past int.MaxValue is an EnergyLimitException.</summary>
    public static int Credit(int balance, int amount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(balance);
        ArgumentOutOfRangeException.ThrowIfLessThan(amount, 1);
        var sum = (long)balance + amount;
        if (sum > int.MaxValue) throw new EnergyLimitException($"A credit of {amount} to {balance} passes the largest balance, {int.MaxValue}.");
        return (int)sum;
    }

    /// <summary>The working state of one replay. Gained is long; it never exceeds int.MaxValue because a balance only rises toward a cap.</summary>
    sealed class Segment(int balance, long anchor, long progress)
    {
        public int Balance = balance;
        public long Anchor = anchor;
        public long Progress = progress;
        public long Gained;

        /// <summary>advance(state, P, until U): credits the time from the anchor to `until` under `policy`; nothing if none elapsed.</summary>
        public void Advance(EnergyPolicy policy, long until)
        {
            if (until <= Anchor) return;
            var elapsed = until - Anchor;
            Anchor = until;
            if (Balance >= policy.Cap)
            {
                // Paused at or above the cap; the balance is never reduced.
                Progress = 0;
                return;
            }

            var interval = IntervalMicros(policy);
            var total = Progress + elapsed;
            var units = total / interval;
            var reached = Balance + (Int128)units * policy.RegenAmount;
            if (reached >= policy.Cap)
            {
                Gained += policy.Cap - Balance;
                Balance = policy.Cap;
                Progress = 0;
            }
            else
            {
                Gained += (long)(reached - Balance);
                Balance = (int)reached;
                Progress = total % interval;
            }
        }
    }

    static readonly long UnixEpochMicros = DateTime.UnixEpoch.Ticks / TicksPerMicro;

    static long IntervalMicros(EnergyPolicy policy) => policy.RegenInterval.Ticks / TicksPerMicro;

    static DateTime FromMicros(long micros) => new(micros * TicksPerMicro, DateTimeKind.Utc);

    /// <summary>Microseconds since 0001-01-01 for an already validated time.</summary>
    static long Micros(DateTime utc) => utc.Ticks / TicksPerMicro;

    /// <summary>Microseconds since 0001-01-01; ArgumentException unless the time is UTC with whole microseconds.</summary>
    static long Micros(DateTime time, string parameter)
    {
        if (time.Kind != DateTimeKind.Utc) throw new ArgumentException($"Times must be UTC, not {time.Kind}.", parameter);
        if (time.Ticks % TicksPerMicro != 0) throw new ArgumentException("Times must be whole microseconds.", parameter);
        return time.Ticks / TicksPerMicro;
    }

    /// <summary>Non-empty, each version valid, Version and EffectiveAt both strictly ascending.</summary>
    static void ValidatePolicies(IReadOnlyList<EnergyPolicy> policies, string parameter)
    {
        ArgumentNullException.ThrowIfNull(policies, parameter);
        if (policies.Count == 0) throw new ArgumentException("At least one policy is needed.", parameter);
        for (var i = 0; i < policies.Count; i++)
        {
            ValidatePolicy(policies[i], parameter);
            if (i == 0) continue;
            if (policies[i].Version <= policies[i - 1].Version)
                throw new ArgumentException($"Policy versions must ascend: {policies[i].Version} follows {policies[i - 1].Version}.", parameter);
            if (policies[i].EffectiveAt <= policies[i - 1].EffectiveAt)
                throw new ArgumentException($"Version {policies[i].Version} must take effect after version {policies[i - 1].Version}.", parameter);
        }
    }

    static void ValidatePolicy(EnergyPolicy policy, string parameter)
    {
        ArgumentNullException.ThrowIfNull(policy, parameter);
        Micros(policy.EffectiveAt, parameter);
        var ticks = policy.RegenInterval.Ticks;
        if (ticks <= 0 || ticks % TicksPerMicro != 0 || ticks / TicksPerMicro > MaxIntervalMicros)
            throw new ArgumentException($"Version {policy.Version}: the interval must be whole microseconds, above zero and at most 7 days.", parameter);
        if (policy.RegenAmount < 1) throw new ArgumentException($"Version {policy.Version}: the regeneration amount must be at least 1.", parameter);
        if (policy.Cap < 0 || policy.OpeningBalance < 0 || policy.OpeningBalance > policy.Cap)
            throw new ArgumentException($"Version {policy.Version}: need 0 <= opening balance <= cap.", parameter);
    }
}
