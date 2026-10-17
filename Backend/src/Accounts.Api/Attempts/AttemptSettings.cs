using System.Globalization;

namespace Blackglass.Accounts.Api.Attempts;

public sealed class AttemptSettingsException(string message) : Exception(message);

/// <summary>
/// Mission-attempt settings from the environment (Stage 7; every value provisional, spec §19). serve validates them at
/// startup, before the database, and the admin CLI's host verbs read them. Unset or empty means the default; anything else
/// outside its bounds refuses to run. A refusal names the setting and never echoes its value. The admission settings
/// (Task 6) and the deadline sweep interval (Task 7) are read by serve only; compose passes them to the api service alone.
/// </summary>
public sealed class AttemptSettings
{
    public const string DevHostsVar = "BLACKGLASS_DEV_HOSTS";
    public const string HostLeaseSecondsVar = "BLACKGLASS_HOST_LEASE_SECONDS";
    public const int DefaultHostLeaseSeconds = 30;
    public const string AssignDeadlineSecondsVar = "BLACKGLASS_ASSIGN_DEADLINE_SECONDS";
    public const int DefaultAssignDeadlineSeconds = 60;
    public const string DeployMaxBodyBytesVar = "BLACKGLASS_DEPLOY_MAX_BODY_BYTES";
    public const int DefaultDeployMaxBodyBytes = 4096;
    public const string DeployRateVar = "BLACKGLASS_RATE_DEPLOY_PER_MINUTE";
    public const int DefaultDeployPerMinute = 6;
    public const string AttemptSweepSecondsVar = "BLACKGLASS_ATTEMPT_SWEEP_SECONDS";
    public const int DefaultAttemptSweepSeconds = 15;
    // Stage 8 (spec §7.6, §19; provisional).
    public const string LaunchDeadlineSecondsVar = "BLACKGLASS_LAUNCH_DEADLINE_SECONDS";
    public const string JoinDeadlineSecondsVar = "BLACKGLASS_JOIN_DEADLINE_SECONDS";
    public const string ProcessLeaseSecondsVar = "BLACKGLASS_PROCESS_LEASE_SECONDS";
    public const string TicketSecondsVar = "BLACKGLASS_TICKET_SECONDS";
    public const string ClaimWaitSecondsVar = "BLACKGLASS_CLAIM_WAIT_SECONDS";
    public const string ClaimPollMsVar = "BLACKGLASS_CLAIM_POLL_MS";

    /// <summary>The log category of serve's attempt messages, such as the development host warning.</summary>
    public const string LogCategory = "Blackglass.Attempts";

    /// <summary>Whether development host fixtures (<c>mission_hosts.kind = 'dev_fixture'</c>) count as capacity in the health
    /// check, and whether the admin CLI may register them (owner ruling S7-D2): exactly "enabled" or "disabled", default
    /// disabled. Never enabled in production.</summary>
    public bool DevHostsEnabled { get; private init; }

    /// <summary>How recent a host's heartbeat must be to count (S7-D2): 5 to 600 seconds, default 30.</summary>
    public int HostLeaseSeconds { get; private init; }

    /// <summary>How long an admitted attempt may wait for assignment before the deadline worker cancels it (S7-D6): 10 to
    /// 3600 seconds, default 60. Admission sets <c>assign_deadline</c> to its database now plus this.</summary>
    public int AssignDeadlineSeconds { get; private init; }

    /// <summary>The largest POST /v1/attempts body read before parsing (Checkpoint 1): 1024 to 65536 bytes, default 4 KiB.</summary>
    public int DeployMaxBodyBytes { get; private init; }

    /// <summary>Deploy requests per account per minute (the route's own fixed window): 1 to 1000, default 6.</summary>
    public int DeployPerMinute { get; private init; }

    /// <summary>How often the assignment deadline worker sweeps (S7-D6): 1 to 3600 seconds, default 15. There is no "off": the
    /// worker is what releases an expired attempt's energy and inventory lock exactly once.</summary>
    public int AttemptSweepSeconds { get; private init; }

    /// <summary>How long a claimed attempt may take to report ready before the deadline worker cancels it: 10 to 3600 s, default 120.</summary>
    public int LaunchDeadlineSeconds { get; private init; }

    /// <summary>How long after ready a participant may start before it is released: 10 to 3600 s, default 120.</summary>
    public int JoinDeadlineSeconds { get; private init; }

    /// <summary>How recent a running attempt's process heartbeat must be, else it becomes unresponsive (never a void): 5 to 600 s, default 30.</summary>
    public int ProcessLeaseSeconds { get; private init; }

    /// <summary>A join ticket's lifetime: 10 to 600 s, default 120.</summary>
    public int TicketSeconds { get; private init; }

    /// <summary>How long a claim long-polls before it answers 204: 0 to 60 s, default 25.</summary>
    public int ClaimWaitSeconds { get; private init; }

    /// <summary>How often a waiting claim tries again: 50 to 10000 ms, default 1000.</summary>
    public int ClaimPollMs { get; private init; }

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
        return new AttemptSettings
        {
            DevHostsEnabled = devHosts,
            HostLeaseSeconds = lease,
            AssignDeadlineSeconds = Number(env, AssignDeadlineSecondsVar, DefaultAssignDeadlineSeconds, 10, 3600),
            DeployMaxBodyBytes = Number(env, DeployMaxBodyBytesVar, DefaultDeployMaxBodyBytes, 1024, 65536),
            DeployPerMinute = Number(env, DeployRateVar, DefaultDeployPerMinute, 1, 1000),
            AttemptSweepSeconds = Number(env, AttemptSweepSecondsVar, DefaultAttemptSweepSeconds, 1, 3600),
            LaunchDeadlineSeconds = Number(env, LaunchDeadlineSecondsVar, 120, 10, 3600),
            JoinDeadlineSeconds = Number(env, JoinDeadlineSecondsVar, 120, 10, 3600),
            ProcessLeaseSeconds = Number(env, ProcessLeaseSecondsVar, 30, 5, 600),
            TicketSeconds = Number(env, TicketSecondsVar, 120, 10, 600),
            ClaimWaitSeconds = Number(env, ClaimWaitSecondsVar, 25, 0, 60),
            ClaimPollMs = Number(env, ClaimPollMsVar, 1000, 50, 10000),
        };
    }

    static int Number(IReadOnlyDictionary<string, string?> env, string name, int fallback, int min, int max)
    {
        if (!env.TryGetValue(name, out var v) || string.IsNullOrEmpty(v)) return fallback;
        if (int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= min && n <= max) return n;
        throw new AttemptSettingsException($"{name} must be a whole number from {min} to {max}.");
    }
}
