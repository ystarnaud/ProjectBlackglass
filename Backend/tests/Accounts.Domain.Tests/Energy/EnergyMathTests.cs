using Blackglass.Accounts.Domain.Energy;

namespace Blackglass.Accounts.Domain.Tests.Energy;

public class EnergyMathTests
{
    static readonly DateTime T0 = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
    static readonly TimeSpan Week = TimeSpan.FromDays(7);
    static readonly TimeSpan OneMicro = TimeSpan.FromTicks(10);

    /// <summary>Version 1 as seeded (E7): cap 50, opening 50, 1 per 10 minutes, effective long before T0.</summary>
    static readonly EnergyPolicy V1 = Policy(1, effectiveAt: T0.AddDays(-30));

    static EnergyPolicy Policy(int version, int cap = 50, TimeSpan? interval = null, int amount = 1, DateTime? effectiveAt = null, int? opening = null) =>
        new(version, cap, opening ?? cap, interval ?? Min(10), amount, 100, effectiveAt ?? T0.AddDays(-30));

    static TimeSpan Min(double minutes) => TimeSpan.FromMinutes(minutes);
    static long Us(TimeSpan span) => span.Ticks / 10;
    static WalletState Wallet(int balance, DateTime? anchor = null, TimeSpan? progress = null, int version = 1) =>
        new(balance, anchor ?? T0, Us(progress ?? TimeSpan.Zero), version);

    // ---- Intervals ----

    [Fact]
    public void Less_than_one_interval_accrues_progress_only()
    {
        var result = EnergyMath.Materialize(Wallet(10), [V1], T0 + Min(9));

        Assert.Equal(new WalletState(10, T0 + Min(9), Us(Min(9)), 1), result.After);
        Assert.Equal(0, result.Gained);
        Assert.Null(result.ConsumedAnchor);
    }

    [Fact]
    public void Exactly_one_interval_gains_one_amount()
    {
        var policy = Policy(1, amount: 2);
        var result = EnergyMath.Materialize(Wallet(10), [policy], T0 + Min(10));

        Assert.Equal(new WalletState(12, T0 + Min(10), 0, 1), result.After);
        Assert.Equal(2, result.Gained);
        Assert.Equal(T0, result.ConsumedAnchor);
    }

    [Fact]
    public void A_partial_interval_is_kept_as_progress()
    {
        var result = EnergyMath.Materialize(Wallet(10), [V1], T0 + Min(25));

        Assert.Equal(new WalletState(12, T0 + Min(25), Us(Min(5)), 1), result.After);
        Assert.Equal(2, result.Gained);
    }

    [Fact]
    public void Progress_carries_into_the_next_materialization()
    {
        var stored = EnergyMath.Materialize(Wallet(10, progress: Min(7)), [V1], T0 + Min(4));
        Assert.Equal(new WalletState(11, T0 + Min(4), Us(Min(1)), 1), stored.After);

        var first = EnergyMath.Materialize(Wallet(10), [V1], T0 + Min(6));
        Assert.Equal(0, first.Gained);
        var second = EnergyMath.Materialize(first.After, [V1], T0 + Min(11));
        Assert.Equal(new WalletState(11, T0 + Min(11), Us(Min(1)), 1), second.After);
        Assert.Equal(1, second.Gained);
        Assert.Equal(T0 + Min(6), second.ConsumedAnchor);
    }

    [Fact]
    public void Gains_stop_at_the_cap_and_progress_resets()
    {
        var result = EnergyMath.Materialize(Wallet(48), [V1], T0 + Min(35));

        Assert.Equal(new WalletState(50, T0 + Min(35), 0, 1), result.After);
        Assert.Equal(2, result.Gained);
        Assert.Equal(T0, result.ConsumedAnchor);
    }

    [Fact]
    public void Reaching_the_cap_exactly_resets_progress()
    {
        // 49 + one unit is exactly the cap; the 5 minutes left over are not kept.
        var result = EnergyMath.Materialize(Wallet(49), [V1], T0 + Min(15));

        Assert.Equal(new WalletState(50, T0 + Min(15), 0, 1), result.After);
        Assert.Equal(1, result.Gained);
    }

    // ---- Above the cap (E6) ----

    [Fact]
    public void At_the_cap_nothing_is_gained_and_the_anchor_follows_now()
    {
        var now = T0 + TimeSpan.FromDays(1);
        var result = EnergyMath.Materialize(Wallet(50, progress: Min(3)), [V1], now);

        Assert.Equal(new WalletState(50, now, 0, 1), result.After);
        Assert.Equal(0, result.Gained);
        Assert.Null(result.ConsumedAnchor);
    }

    [Fact]
    public void Above_the_cap_the_balance_is_kept_and_never_reduced()
    {
        var now = T0 + TimeSpan.FromDays(365);
        var result = EnergyMath.Materialize(Wallet(80, progress: Min(9)), [V1], now);

        Assert.Equal(new WalletState(80, now, 0, 1), result.After);
        Assert.Equal(0, result.Gained);
        Assert.Null(result.ConsumedAnchor);
    }

    [Fact]
    public void NextRegenAt_is_null_at_or_above_the_cap_and_now_plus_the_remaining_progress_below_it()
    {
        Assert.Null(EnergyMath.NextRegenAt(Wallet(50), V1, T0));
        Assert.Null(EnergyMath.NextRegenAt(Wallet(80), V1, T0));
        Assert.Equal(T0 + Min(10), EnergyMath.NextRegenAt(Wallet(0), V1, T0));
        Assert.Equal(T0 + Min(7), EnergyMath.NextRegenAt(Wallet(49, progress: Min(3)), V1, T0));
        Assert.Equal(T0 + OneMicro, EnergyMath.NextRegenAt(Wallet(49, progress: Min(10) - OneMicro), V1, T0));

        // Straight after a materialization.
        var materialized = EnergyMath.Materialize(Wallet(10), [V1], T0 + Min(25)).After;
        Assert.Equal(T0 + Min(30), EnergyMath.NextRegenAt(materialized, V1, T0 + Min(25)));
    }

    // ---- Transitions (E2) ----

    [Fact]
    public void Time_before_a_new_policy_is_credited_under_the_old_one()
    {
        var v2 = Policy(2, interval: Min(5), effectiveAt: T0 + Min(30));

        var before = EnergyMath.Materialize(Wallet(10), [V1, v2], T0 + Min(29));
        Assert.Equal(new WalletState(12, T0 + Min(29), Us(Min(9)), 1), before.After);

        var at = EnergyMath.Materialize(Wallet(10), [V1, v2], T0 + Min(30));
        Assert.Equal(new WalletState(13, T0 + Min(30), 0, 2), at.After);
        Assert.Equal(3, at.Gained);
    }

    [Fact]
    public void Time_after_a_new_policy_is_credited_under_the_new_one()
    {
        var v2 = Policy(2, interval: Min(5), effectiveAt: T0 + Min(30));

        var result = EnergyMath.Materialize(Wallet(10), [V1, v2], T0 + Min(42));

        // 30 minutes at 10 minutes (+3), then 12 minutes at 5 minutes (+2, 2 minutes kept).
        Assert.Equal(new WalletState(15, T0 + Min(42), Us(Min(2)), 2), result.After);
        Assert.Equal(5, result.Gained);
        Assert.Equal(T0, result.ConsumedAnchor);
    }

    [Fact]
    public void Progress_is_carried_across_a_transition_proportionally()
    {
        var slower = Policy(2, interval: Min(20), effectiveAt: T0 + Min(5));
        var half = EnergyMath.Materialize(Wallet(10), [V1, slower], T0 + Min(5));
        Assert.Equal(new WalletState(10, T0 + Min(5), Us(Min(10)), 2), half.After);

        var faster = Policy(2, interval: Min(3), effectiveAt: T0 + Min(7));
        var seven = EnergyMath.Materialize(Wallet(10), [V1, faster], T0 + Min(7));
        Assert.Equal(new WalletState(10, T0 + Min(7), Us(Min(2) + TimeSpan.FromSeconds(6)), 2), seven.After);

        // 420,000,001 µs * 3/10 = 126,000,000.3 µs: floored to the microsecond.
        var floored = EnergyMath.Materialize(Wallet(10, progress: Min(7) + OneMicro, anchor: T0), [V1, Policy(2, interval: Min(3), effectiveAt: T0)], T0);
        Assert.Equal(126_000_000, floored.After.ProgressMicros);
        Assert.Equal(2, floored.After.PolicyVersion);
    }

    [Fact]
    public void A_lower_cap_keeps_a_higher_balance_and_pauses_regeneration()
    {
        var v2 = Policy(2, cap: 30, effectiveAt: T0 + Min(5));
        var now = T0 + TimeSpan.FromDays(5);

        var result = EnergyMath.Materialize(Wallet(40), [V1, v2], now);

        Assert.Equal(new WalletState(40, now, 0, 2), result.After);
        Assert.Equal(0, result.Gained);
        Assert.Null(result.ConsumedAnchor);
        Assert.Null(EnergyMath.NextRegenAt(result.After, v2, now));
    }

    [Fact]
    public void A_higher_cap_resumes_regeneration_from_the_effective_time()
    {
        var v2 = Policy(2, cap: 60, effectiveAt: T0 + Min(60));

        var result = EnergyMath.Materialize(Wallet(50, progress: Min(4)), [V1, v2], T0 + Min(85));

        // Paused at 50 until the new cap is effective; 25 minutes under it then give +2 with 5 minutes kept.
        Assert.Equal(new WalletState(52, T0 + Min(85), Us(Min(5)), 2), result.After);
        Assert.Equal(2, result.Gained);
        Assert.Equal(T0, result.ConsumedAnchor);
    }

    [Fact]
    public void Several_transitions_in_one_materialization_apply_in_order()
    {
        var v2 = Policy(2, interval: Min(5), effectiveAt: T0 + Min(30));
        var v3 = Policy(4, cap: 100, interval: Min(20), amount: 3, effectiveAt: T0 + Min(40));
        var v4 = Policy(7, interval: TimeSpan.FromSeconds(1), effectiveAt: T0 + Min(71));

        var result = EnergyMath.Materialize(Wallet(0, progress: Min(4)), [V1, v2, v3, v4], T0 + Min(70));

        // V1: 4 + 30 = 34 min → +3, 4 min kept → 2 min of 5. V2: 2 + 10 = 12 → +2, 2 min kept → 8 min of 20.
        // V3 (amount 3): 8 + 30 = 38 → one unit, +3, 18 min kept. Version 7 is not yet effective.
        Assert.Equal(new WalletState(8, T0 + Min(70), Us(Min(18)), 4), result.After);
        Assert.Equal(8, result.Gained);
        Assert.Equal(T0, result.ConsumedAnchor);
    }

    [Fact]
    public void A_faster_policy_never_pays_for_time_before_its_effective_time()
    {
        var hourly = Policy(1, interval: Min(60), effectiveAt: T0.AddDays(-1));
        var everyMinute = Policy(2, interval: Min(1), effectiveAt: T0 + Min(59));

        var result = EnergyMath.Materialize(Wallet(0), [hourly, everyMinute], T0 + Min(60));

        // 59 minutes of an hour carry over as 59 seconds of a minute; one more minute gives +1 with 59 s kept.
        Assert.Equal(new WalletState(1, T0 + Min(60), Us(TimeSpan.FromSeconds(59)), 2), result.After);
        Assert.Equal(1, result.Gained);
    }

    [Fact]
    public void A_version_effective_at_or_before_the_anchor_switches_with_no_time_to_account()
    {
        var v2 = Policy(2, interval: Min(20), effectiveAt: T0 - Min(60));
        var wallet = Wallet(10, progress: Min(6));

        // The clock is behind the version too: nothing applies yet.
        var behind = EnergyMath.Materialize(wallet, [V1, v2], T0 - Min(120));
        Assert.Same(wallet, behind.After);

        var atAnchor = EnergyMath.Materialize(wallet, [V1, v2], T0);
        Assert.Equal(new WalletState(10, T0, Us(Min(12)), 2), atAnchor.After);
        Assert.Equal(0, atAnchor.Gained);
        Assert.Null(atAnchor.ConsumedAnchor);

        var later = EnergyMath.Materialize(wallet, [V1, v2], T0 + Min(10));
        Assert.Equal(new WalletState(11, T0 + Min(10), Us(Min(2)), 2), later.After);
        Assert.Equal(T0, later.ConsumedAnchor);

        var exactlyAtAnchor = Policy(2, interval: Min(20), effectiveAt: T0);
        Assert.Equal(new WalletState(10, T0, Us(Min(12)), 2), EnergyMath.Materialize(wallet, [V1, exactlyAtAnchor], T0).After);
    }

    [Fact]
    public void Materializing_in_steps_equals_materializing_once_across_transitions()
    {
        var random = new Random(6006);
        for (var i = 0; i < 4000; i++)
        {
            var (wallet, policies, unit) = RandomCase(random);
            var t1 = RandomTime(random, unit);
            var t2 = RandomTime(random, unit);
            if (t2 < t1) (t1, t2) = (t2, t1);

            var once = EnergyMath.Materialize(wallet, policies, t2);
            var first = EnergyMath.Materialize(wallet, policies, t1);
            var second = EnergyMath.Materialize(first.After, From(policies, first.After.PolicyVersion), t2);

            var context = $"case {i}: {wallet} t1={t1:O} t2={t2:O} policies={string.Join("; ", policies)}";
            Assert.True(once.After == second.After, $"{context}\n once={once.After}\n steps={second.After}");
            Assert.True(once.Gained == first.Gained + second.Gained, context);
            AssertForwardOnly(wallet, once, policies, context);
            AssertForwardOnly(wallet, first, policies, context);
            AssertForwardOnly(first.After, second, policies, context);
        }
    }

    [Fact]
    public void Materializing_twice_at_the_same_time_changes_nothing_the_second_time()
    {
        var random = new Random(6007);
        for (var i = 0; i < 4000; i++)
        {
            var (wallet, policies, unit) = RandomCase(random);
            var now = RandomTime(random, unit);

            var first = EnergyMath.Materialize(wallet, policies, now);
            var again = EnergyMath.Materialize(first.After, From(policies, first.After.PolicyVersion), now);

            var context = $"case {i}: {wallet} now={now:O} policies={string.Join("; ", policies)}";
            Assert.True(first.After == again.After, context);
            Assert.True(again.Gained == 0, context);
            Assert.True(again.ConsumedAnchor is null, context);
        }
    }

    // ---- Clock and limits ----

    [Fact]
    public void A_clock_behind_the_anchor_changes_nothing()
    {
        var wallet = Wallet(10, progress: Min(3));

        foreach (var now in new[] { T0 - TimeSpan.FromDays(1), T0 - OneMicro, T0 })
        {
            var result = EnergyMath.Materialize(wallet, [V1], now);
            Assert.Equal(wallet, result.After);
            Assert.Equal(0, result.Gained);
            Assert.Null(result.ConsumedAnchor);
        }
    }

    [Fact]
    public void A_years_old_anchor_fills_to_the_cap_without_overflow()
    {
        var cap = int.MaxValue - 1;
        var flood = Policy(1, cap: cap, interval: OneMicro, amount: 1_000_000, effectiveAt: DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc));

        var years = EnergyMath.Materialize(Wallet(0, anchor: T0.AddYears(-50)), [flood], T0);
        Assert.Equal(new WalletState(cap, T0, 0, 1), years.After);
        Assert.Equal(cap, years.Gained);

        // The whole DateTime range at 10^6 per microsecond is about 3 × 10^23: past long, not past Int128.
        var end = new DateTime(9999, 12, 31, 23, 59, 59, DateTimeKind.Utc);
        var range = EnergyMath.Materialize(Wallet(0, anchor: new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Utc)), [flood], end);
        Assert.Equal(new WalletState(cap, end, 0, 1), range.After);

        var nearlyFull = EnergyMath.Materialize(Wallet(cap - 1), [flood], T0 + OneMicro);
        Assert.Equal(new WalletState(cap, T0 + OneMicro, 0, 1), nearlyFull.After);
        Assert.Equal(1, nearlyFull.Gained);
    }

    [Fact]
    public void The_proportional_step_uses_Int128_at_the_largest_intervals()
    {
        const long week = 604_800_000_000;
        var weekly = Policy(1, interval: Week, effectiveAt: T0.AddDays(-30));
        var wallet = Wallet(10, progress: Week - OneMicro);

        Assert.Equal(week - 1, Switch(wallet, weekly, Policy(2, interval: Week, effectiveAt: T0)).ProgressMicros);
        Assert.Equal(0, Switch(wallet, weekly, Policy(2, interval: OneMicro, effectiveAt: T0)).ProgressMicros);
        // (N − 1)² / N = N − 2 + 1/N, floored.
        Assert.Equal(week - 2, Switch(wallet, weekly, Policy(2, interval: Week - OneMicro, effectiveAt: T0)).ProgressMicros);

        var micro = Policy(1, interval: OneMicro, effectiveAt: T0.AddDays(-30));
        Assert.Equal(0, Switch(Wallet(10), micro, Policy(2, interval: Week, effectiveAt: T0)).ProgressMicros);

        static WalletState Switch(WalletState wallet, EnergyPolicy from, EnergyPolicy to) =>
            EnergyMath.Materialize(wallet, [from, to], T0).After;
    }

    [Fact]
    public void Credit_refuses_a_result_above_int_MaxValue()
    {
        Assert.Equal(int.MaxValue, EnergyMath.Credit(int.MaxValue - 5, 5));
        Assert.Equal(1, EnergyMath.Credit(0, 1));
        Assert.Throws<EnergyLimitException>(() => EnergyMath.Credit(int.MaxValue - 5, 6));
        Assert.Throws<EnergyLimitException>(() => EnergyMath.Credit(int.MaxValue, 1));
        Assert.Throws<EnergyLimitException>(() => EnergyMath.Credit(int.MaxValue, int.MaxValue));
    }

    [Fact]
    public void Credit_refuses_a_non_positive_amount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EnergyMath.Credit(10, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => EnergyMath.Credit(10, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => EnergyMath.Credit(10, int.MinValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => EnergyMath.Credit(-1, 1));
    }

    // ---- Opening and keys ----

    [Fact]
    public void ApplicableAt_picks_the_latest_effective_version_else_version_1()
    {
        var v2 = Policy(2, cap: 60, effectiveAt: T0);
        var v3 = Policy(3, cap: 70, effectiveAt: T0.AddDays(1));
        EnergyPolicy[] policies = [V1, v2, v3];

        Assert.Same(V1, EnergyMath.ApplicableAt(policies, T0.AddYears(-1)));
        Assert.Same(V1, EnergyMath.ApplicableAt(policies, V1.EffectiveAt));
        Assert.Same(V1, EnergyMath.ApplicableAt(policies, T0 - OneMicro));
        Assert.Same(v2, EnergyMath.ApplicableAt(policies, T0));
        Assert.Same(v2, EnergyMath.ApplicableAt(policies, T0.AddDays(1) - OneMicro));
        Assert.Same(v3, EnergyMath.ApplicableAt(policies, T0.AddDays(2)));
        Assert.Same(V1, EnergyMath.ApplicableAt([V1], T0.AddYears(-10)));
    }

    [Fact]
    public void Open_uses_the_opening_balance_and_zero_progress()
    {
        Assert.Equal(new WalletState(50, T0, 0, 1), EnergyMath.Open(V1, T0));
        Assert.Equal(new WalletState(20, T0 + Min(1), 0, 3), EnergyMath.Open(Policy(3, opening: 20), T0 + Min(1)));
    }

    [Theory]
    [InlineData("1970-01-01T00:00:00.0000000Z", "regen:0")]
    [InlineData("1970-01-01T00:00:00.0000010Z", "regen:1")]
    [InlineData("1969-12-31T23:59:59.9999990Z", "regen:-1")]
    [InlineData("1969-07-20T20:17:40.0000000Z", "regen:-14182940000000")]
    [InlineData("1960-01-01T00:00:00.0000000Z", "regen:-315619200000000")]
    [InlineData("0001-01-01T00:00:00.0000000Z", "regen:-62135596800000000")]
    [InlineData("2000-01-01T00:00:00.0000000Z", "regen:946684800000000")]
    [InlineData("2001-09-09T01:46:40.0000000Z", "regen:1000000000000000")]
    [InlineData("2026-10-16T12:00:00.1234560Z", "regen:1792152000123456")]
    [InlineData("9999-12-31T23:59:59.9999990Z", "regen:253402300799999999")]
    public void RegenKey_is_microseconds_since_the_epoch(string anchor, string expected)
    {
        var utc = DateTime.Parse(anchor, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
        Assert.Equal(expected, EnergyMath.RegenKey(utc));
    }

    [Fact]
    public void Inputs_must_be_UTC_whole_microseconds_and_well_ordered()
    {
        var local = DateTime.SpecifyKind(T0, DateTimeKind.Local);
        var unspecified = DateTime.SpecifyKind(T0, DateTimeKind.Unspecified);
        var subMicro = T0.AddTicks(1);
        var wallet = Wallet(10);
        var v2 = Policy(2, effectiveAt: T0);

        // The clock.
        foreach (var bad in new[] { local, unspecified, subMicro })
        {
            Assert.Throws<ArgumentException>(() => EnergyMath.Materialize(wallet, [V1], bad));
            Assert.Throws<ArgumentException>(() => EnergyMath.ApplicableAt([V1], bad));
            Assert.Throws<ArgumentException>(() => EnergyMath.Open(V1, bad));
            Assert.Throws<ArgumentException>(() => EnergyMath.NextRegenAt(wallet, V1, bad));
            Assert.Throws<ArgumentException>(() => EnergyMath.RegenKey(bad));
        }

        // The wallet.
        Assert.Throws<ArgumentException>(() => EnergyMath.Materialize(wallet with { RegenAnchor = local }, [V1], T0));
        Assert.Throws<ArgumentException>(() => EnergyMath.Materialize(wallet with { RegenAnchor = subMicro }, [V1], T0));
        Assert.Throws<ArgumentException>(() => EnergyMath.Materialize(wallet with { Balance = -1 }, [V1], T0));
        Assert.Throws<ArgumentException>(() => EnergyMath.Materialize(wallet with { ProgressMicros = -1 }, [V1], T0));
        Assert.Throws<ArgumentException>(() => EnergyMath.Materialize(wallet with { ProgressMicros = Us(Min(10)) }, [V1], T0));
        Assert.Throws<ArgumentException>(() => EnergyMath.NextRegenAt(wallet with { PolicyVersion = 2 }, V1, T0));
        Assert.Throws<ArgumentException>(() => EnergyMath.NextRegenAt(wallet with { ProgressMicros = Us(Min(10)) }, V1, T0));

        // The policy list.
        Assert.Throws<ArgumentException>(() => EnergyMath.Materialize(wallet, [], T0));
        Assert.Throws<ArgumentException>(() => EnergyMath.Materialize(wallet, [v2], T0));
        Assert.Throws<ArgumentException>(() => EnergyMath.Materialize(wallet, [V1, v2 with { Version = 1 }], T0));
        Assert.Throws<ArgumentException>(() => EnergyMath.Materialize(wallet with { PolicyVersion = 2 }, [v2, V1], T0));
        Assert.Throws<ArgumentException>(() => EnergyMath.Materialize(wallet, [V1, v2 with { EffectiveAt = V1.EffectiveAt }], T0));
        Assert.Throws<ArgumentException>(() => EnergyMath.Materialize(wallet, [V1, v2 with { EffectiveAt = V1.EffectiveAt.AddDays(-1) }], T0));
        Assert.Throws<ArgumentException>(() => EnergyMath.Materialize(wallet, [V1, v2 with { EffectiveAt = local }], T0));
        Assert.Throws<ArgumentException>(() => EnergyMath.Materialize(wallet, [V1, v2 with { EffectiveAt = subMicro }], T0));
        Assert.Throws<ArgumentException>(() => EnergyMath.ApplicableAt([], T0));
        Assert.Throws<ArgumentException>(() => EnergyMath.ApplicableAt([v2, V1], T0));

        // The interval and the amounts.
        foreach (var interval in new[] { TimeSpan.Zero, -OneMicro, Week + OneMicro, TimeSpan.FromTicks(15) })
        {
            Assert.Throws<ArgumentException>(() => EnergyMath.Materialize(wallet, [V1 with { RegenInterval = interval }], T0));
            Assert.Throws<ArgumentException>(() => EnergyMath.Materialize(wallet, [V1, v2 with { RegenInterval = interval }], T0));
        }
        Assert.Throws<ArgumentException>(() => EnergyMath.Materialize(wallet, [V1 with { RegenAmount = 0 }], T0));
        Assert.Throws<ArgumentException>(() => EnergyMath.Materialize(wallet, [V1 with { Cap = -1, OpeningBalance = 0 }], T0));
        Assert.Throws<ArgumentException>(() => EnergyMath.Open(V1 with { OpeningBalance = 51 }, T0));
        Assert.Throws<ArgumentException>(() => EnergyMath.Open(V1 with { OpeningBalance = -1 }, T0));

        // The boundaries themselves are accepted.
        EnergyMath.Materialize(wallet, [V1 with { RegenInterval = Week }], T0);
        EnergyMath.Materialize(wallet with { ProgressMicros = 0 }, [V1 with { RegenInterval = OneMicro }], T0);
    }

    // ---- Property sweep helpers ----

    /// <summary>A valid wallet and policy sequence on one random time scale, so elapsed times, intervals and transitions interleave.</summary>
    static (WalletState Wallet, EnergyPolicy[] Policies, TimeSpan Unit) RandomCase(Random random)
    {
        TimeSpan[] units = [OneMicro, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(1), Min(1)];
        var unit = units[random.Next(units.Length)];

        var count = random.Next(1, 5);
        var policies = new EnergyPolicy[count];
        var version = random.Next(1, 6);
        var effective = T0 + unit * random.Next(-200, 100) + OneMicro * random.Next(0, 1000);
        for (var i = 0; i < count; i++)
        {
            var interval = random.Next(10) == 0 ? Week : unit * random.Next(1, 61) + OneMicro * random.Next(0, 3);
            if (interval > Week) interval = Week;
            var cap = random.Next(0, 101);
            policies[i] = new EnergyPolicy(version, cap, random.Next(0, cap + 1), interval, random.Next(1, 6), 100, effective);
            version += random.Next(1, 4);
            effective += unit * random.Next(0, 120) + OneMicro * random.Next(1, 1000);
        }

        var balance = random.Next(4) == 0 ? random.Next(100, 200) : random.Next(0, 101);
        var progress = random.NextInt64(0, Us(policies[0].RegenInterval));
        var wallet = new WalletState(balance, RandomTime(random, unit), progress, policies[0].Version);
        return (wallet, policies, unit);
    }

    static DateTime RandomTime(Random random, TimeSpan unit) =>
        T0 + unit * random.Next(-300, 600) + OneMicro * random.Next(0, 1000);

    static EnergyPolicy[] From(EnergyPolicy[] policies, int version) =>
        policies.SkipWhile(p => p.Version != version).ToArray();

    static void AssertForwardOnly(WalletState before, Regeneration result, EnergyPolicy[] policies, string context)
    {
        var after = result.After;
        Assert.True(after.RegenAnchor >= before.RegenAnchor, $"anchor regressed: {context}");
        Assert.True(after.PolicyVersion >= before.PolicyVersion, $"version regressed: {context}");
        Assert.True(after.Balance >= before.Balance, $"balance reduced: {context}");
        Assert.True(result.Gained == after.Balance - before.Balance, $"gain mismatch: {context}");
        Assert.True((result.Gained > 0) == (result.ConsumedAnchor is not null), $"consumed anchor: {context}");
        if (result.ConsumedAnchor is { } consumed) Assert.True(consumed == before.RegenAnchor, $"consumed anchor value: {context}");
        var interval = policies.Single(p => p.Version == after.PolicyVersion).RegenInterval;
        Assert.True(after.ProgressMicros >= 0 && after.ProgressMicros < Us(interval), $"progress out of range: {context}");
    }
}
