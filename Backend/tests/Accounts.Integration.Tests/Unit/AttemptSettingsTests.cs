using Blackglass.Accounts.Api.Attempts;

namespace Blackglass.Accounts.IntegrationTests.Unit;

public class AttemptSettingsTests
{
    static Dictionary<string, string?> Env(params (string Key, string? Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);

    [Fact]
    public void AttemptSettings_defaults_bounds_and_refusals()
    {
        // Defaults (provisional): development host fixtures off, a 30-second host lease. Unset and empty both mean the default.
        Assert.Equal("BLACKGLASS_DEV_HOSTS", AttemptSettings.DevHostsVar);
        Assert.Equal("BLACKGLASS_HOST_LEASE_SECONDS", AttemptSettings.HostLeaseSecondsVar);
        foreach (var env in new[] { Env(), Env((AttemptSettings.DevHostsVar, ""), (AttemptSettings.HostLeaseSecondsVar, null)) })
        {
            var defaults = AttemptSettings.FromEnvironment(env);
            Assert.Equal((false, 30), (defaults.DevHostsEnabled, defaults.HostLeaseSeconds));
        }

        // Exactly "enabled" or "disabled"; the lease 5..600 inclusive.
        Assert.True(AttemptSettings.FromEnvironment(Env((AttemptSettings.DevHostsVar, "enabled"))).DevHostsEnabled);
        Assert.False(AttemptSettings.FromEnvironment(Env((AttemptSettings.DevHostsVar, "disabled"))).DevHostsEnabled);
        foreach (var (text, seconds) in new[] { ("5", 5), ("600", 600), ("45", 45) })
            Assert.Equal(seconds, AttemptSettings.FromEnvironment(Env((AttemptSettings.HostLeaseSecondsVar, text))).HostLeaseSeconds);

        // Refusals name the setting and never echo the value.
        foreach (var (name, value) in new[]
                 {
                     (AttemptSettings.DevHostsVar, "Enabled"), (AttemptSettings.DevHostsVar, "true"), (AttemptSettings.DevHostsVar, " enabled"),
                     (AttemptSettings.DevHostsVar, "yes-value-xyz"),
                     (AttemptSettings.HostLeaseSecondsVar, "4"), (AttemptSettings.HostLeaseSecondsVar, "601"), (AttemptSettings.HostLeaseSecondsVar, "0"),
                     (AttemptSettings.HostLeaseSecondsVar, "-30"), (AttemptSettings.HostLeaseSecondsVar, "+30"), (AttemptSettings.HostLeaseSecondsVar, "30 "),
                     (AttemptSettings.HostLeaseSecondsVar, "30.0"), (AttemptSettings.HostLeaseSecondsVar, "half-value-xyz"), (AttemptSettings.HostLeaseSecondsVar, "99999999999"),
                 })
        {
            var e = Assert.Throws<AttemptSettingsException>(() => AttemptSettings.FromEnvironment(Env((name, value))));
            Assert.Contains(name, e.Message);
            if (value.Contains("-value-", StringComparison.Ordinal)) Assert.DoesNotContain(value, e.Message);
        }
    }
}
