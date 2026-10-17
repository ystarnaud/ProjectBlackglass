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

    [Fact]
    public void Admission_settings_defaults_bounds_and_refusals()
    {
        // Provisional (spec §19): a 60 s assignment deadline (S7-D6), a 4 KiB deploy body, 6 deploys a minute per account.
        Assert.Equal("BLACKGLASS_ASSIGN_DEADLINE_SECONDS", AttemptSettings.AssignDeadlineSecondsVar);
        Assert.Equal("BLACKGLASS_DEPLOY_MAX_BODY_BYTES", AttemptSettings.DeployMaxBodyBytesVar);
        Assert.Equal("BLACKGLASS_RATE_DEPLOY_PER_MINUTE", AttemptSettings.DeployRateVar);
        foreach (var env in new[] { Env(), Env((AttemptSettings.AssignDeadlineSecondsVar, ""), (AttemptSettings.DeployMaxBodyBytesVar, null), (AttemptSettings.DeployRateVar, "")) })
        {
            var defaults = AttemptSettings.FromEnvironment(env);
            Assert.Equal((60, 4096, 6), (defaults.AssignDeadlineSeconds, defaults.DeployMaxBodyBytes, defaults.DeployPerMinute));
        }

        // Inclusive bounds: the deadline 10..3600 s, the body 1024..65536 bytes, the rate 1..1000 a minute.
        foreach (var (name, text, read) in new (string, string, Func<AttemptSettings, int>)[]
                 {
                     (AttemptSettings.AssignDeadlineSecondsVar, "10", s => s.AssignDeadlineSeconds), (AttemptSettings.AssignDeadlineSecondsVar, "3600", s => s.AssignDeadlineSeconds),
                     (AttemptSettings.DeployMaxBodyBytesVar, "1024", s => s.DeployMaxBodyBytes), (AttemptSettings.DeployMaxBodyBytesVar, "65536", s => s.DeployMaxBodyBytes),
                     (AttemptSettings.DeployRateVar, "1", s => s.DeployPerMinute), (AttemptSettings.DeployRateVar, "1000", s => s.DeployPerMinute),
                 })
            Assert.Equal(int.Parse(text, System.Globalization.CultureInfo.InvariantCulture), read(AttemptSettings.FromEnvironment(Env((name, text)))));

        foreach (var (name, value) in new[]
                 {
                     (AttemptSettings.AssignDeadlineSecondsVar, "9"), (AttemptSettings.AssignDeadlineSecondsVar, "3601"), (AttemptSettings.AssignDeadlineSecondsVar, "+60"),
                     (AttemptSettings.AssignDeadlineSecondsVar, "soon-value-xyz"),
                     (AttemptSettings.DeployMaxBodyBytesVar, "1023"), (AttemptSettings.DeployMaxBodyBytesVar, "65537"), (AttemptSettings.DeployMaxBodyBytesVar, "4 KiB"),
                     (AttemptSettings.DeployRateVar, "0"), (AttemptSettings.DeployRateVar, "1001"), (AttemptSettings.DeployRateVar, "-6"), (AttemptSettings.DeployRateVar, "lots-value-xyz"),
                 })
        {
            var e = Assert.Throws<AttemptSettingsException>(() => AttemptSettings.FromEnvironment(Env((name, value))));
            Assert.Contains(name, e.Message);
            if (value.Contains("-value-", StringComparison.Ordinal)) Assert.DoesNotContain(value, e.Message);
        }
    }
}
