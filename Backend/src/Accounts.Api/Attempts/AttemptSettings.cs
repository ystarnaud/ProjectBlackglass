using System.Globalization;

namespace Blackglass.Accounts.Api.Attempts;

public sealed class AttemptSettingsException(string message) : Exception(message);

/// <summary>
/// Mission-attempt settings from the environment (Stage 7; every value provisional, spec §19). serve validates them at
/// startup, before the database, and the admin CLI's host verbs read them. Unset or empty means the default; anything else
/// outside its bounds refuses to run. A refusal names the setting and never echoes its value. Later Stage 7 tasks add the
/// admission settings here.
/// </summary>
public sealed class AttemptSettings
{
    public const string DevHostsVar = "BLACKGLASS_DEV_HOSTS";
    public const string HostLeaseSecondsVar = "BLACKGLASS_HOST_LEASE_SECONDS";
    public const int DefaultHostLeaseSeconds = 30;

    /// <summary>The log category of serve's attempt messages, such as the development host warning.</summary>
    public const string LogCategory = "Blackglass.Attempts";

    /// <summary>Whether development host fixtures (<c>mission_hosts.kind = 'dev_fixture'</c>) count as capacity in the health
    /// check, and whether the admin CLI may register them (owner ruling S7-D2): exactly "enabled" or "disabled", default
    /// disabled. Never enabled in production.</summary>
    public bool DevHostsEnabled { get; private init; }

    /// <summary>How recent a host's heartbeat must be to count (S7-D2): 5 to 600 seconds, default 30.</summary>
    public int HostLeaseSeconds { get; private init; }

    public static AttemptSettings FromEnvironment(IReadOnlyDictionary<string, string?> env)
    {
        var devHosts = !env.TryGetValue(DevHostsVar, out var d) || string.IsNullOrEmpty(d) ? false
            : d == "enabled" ? true
            : d == "disabled" ? false
            : throw new AttemptSettingsException($"{DevHostsVar} must be 'enabled' or 'disabled'.");
        var lease = DefaultHostLeaseSeconds;
        if (env.TryGetValue(HostLeaseSecondsVar, out var l) && !string.IsNullOrEmpty(l))
        {
            if (!int.TryParse(l, NumberStyles.None, CultureInfo.InvariantCulture, out lease) || lease is < 5 or > 600)
                throw new AttemptSettingsException($"{HostLeaseSecondsVar} must be a whole number of seconds, 5 to 600.");
        }
        return new AttemptSettings { DevHostsEnabled = devHosts, HostLeaseSeconds = lease };
    }
}
