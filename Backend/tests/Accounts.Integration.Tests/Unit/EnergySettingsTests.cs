using Blackglass.Accounts.Api.Energy;

namespace Blackglass.Accounts.IntegrationTests.Unit;

public class EnergySettingsTests
{
    static Dictionary<string, string?> Env(params (string Key, string? Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);

    [Fact]
    public void EnergySettings_defaults_bounds_and_refusals()
    {
        // Defaults (provisional, §19): a sweep every hour, grants of at most 1000. Unset and empty both mean the default.
        var defaults = EnergySettings.FromEnvironment(Env());
        Assert.Equal((3600, 1000), (defaults.ReconcileSeconds, defaults.GrantMax));
        var empty = EnergySettings.FromEnvironment(Env((EnergySettings.ReconcileSecondsVar, ""), (EnergySettings.GrantMaxVar, null)));
        Assert.Equal((3600, 1000), (empty.ReconcileSeconds, empty.GrantMax));
        Assert.Equal("BLACKGLASS_ENERGY_RECONCILE_SECONDS", EnergySettings.ReconcileSecondsVar);
        Assert.Equal("BLACKGLASS_ENERGY_GRANT_MAX", EnergySettings.GrantMaxVar);

        // Bounds, inclusive: 0 turns the sweep off, else 60..86400; grants 1..1,000,000.
        foreach (var (text, seconds) in new[] { ("0", 0), ("60", 60), ("86400", 86400), ("900", 900) })
            Assert.Equal(seconds, EnergySettings.FromEnvironment(Env((EnergySettings.ReconcileSecondsVar, text))).ReconcileSeconds);
        foreach (var (text, max) in new[] { ("1", 1), ("1000000", 1_000_000), ("250", 250) })
            Assert.Equal(max, EnergySettings.FromEnvironment(Env((EnergySettings.GrantMaxVar, text))).GrantMax);

        // Refusals: out of range, signed, padded, or not a whole number. The message names the setting, never echoes the value.
        foreach (var (name, value) in new[]
                 {
                     (EnergySettings.ReconcileSecondsVar, "1"), (EnergySettings.ReconcileSecondsVar, "59"), (EnergySettings.ReconcileSecondsVar, "86401"),
                     (EnergySettings.ReconcileSecondsVar, "-60"), (EnergySettings.ReconcileSecondsVar, "+60"), (EnergySettings.ReconcileSecondsVar, " 3600"),
                     (EnergySettings.ReconcileSecondsVar, "1h-value-xyz"), (EnergySettings.ReconcileSecondsVar, "3600.0"),
                     (EnergySettings.GrantMaxVar, "0"), (EnergySettings.GrantMaxVar, "1000001"), (EnergySettings.GrantMaxVar, "-1"),
                     (EnergySettings.GrantMaxVar, "1000 "), (EnergySettings.GrantMaxVar, "lots-value-xyz"), (EnergySettings.GrantMaxVar, "99999999999"),
                 })
        {
            var e = Assert.Throws<EnergySettingsException>(() => EnergySettings.FromEnvironment(Env((name, value))));
            Assert.Contains(name, e.Message);
            if (value.Contains("-value-", StringComparison.Ordinal)) Assert.DoesNotContain(value, e.Message);
        }
    }
}
