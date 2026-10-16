using System.Globalization;

namespace Blackglass.Accounts.Api.Energy;

public sealed class EnergySettingsException(string message) : Exception(message);

/// <summary>
/// Energy settings from the environment (Stage 6; every value provisional, spec §19), read by the admin CLI's energy verbs
/// (and by serve once the reconciliation runs there). Unset or empty means the default; anything else outside its bounds
/// refuses to run. A refusal names the setting and never echoes its value.
/// </summary>
public sealed class EnergySettings
{
    public const string ReconcileSecondsVar = "BLACKGLASS_ENERGY_RECONCILE_SECONDS";
    public const string GrantMaxVar = "BLACKGLASS_ENERGY_GRANT_MAX";
    public const int DefaultReconcileSeconds = 3600;
    public const int DefaultGrantMax = 1000;

    /// <summary>Seconds between reconciliation sweeps (owner ruling E4): 0 turns them off, else 60 to 86400; default 3600.</summary>
    public int ReconcileSeconds { get; private init; }
    /// <summary>The largest single development grant (owner ruling E5): 1 to 1,000,000; default 1000.</summary>
    public int GrantMax { get; private init; }

    public static EnergySettings FromEnvironment(IReadOnlyDictionary<string, string?> env)
    {
        var reconcile = Number(env, ReconcileSecondsVar, DefaultReconcileSeconds);
        if (reconcile is not 0 and (< 60 or > 86_400))
            throw new EnergySettingsException($"{ReconcileSecondsVar} must be 0 (off) or a whole number from 60 to 86400.");
        var grantMax = Number(env, GrantMaxVar, DefaultGrantMax);
        if (grantMax is < 1 or > 1_000_000)
            throw new EnergySettingsException($"{GrantMaxVar} must be a whole number from 1 to 1000000.");
        return new EnergySettings { ReconcileSeconds = reconcile, GrantMax = grantMax };
    }

    static int Number(IReadOnlyDictionary<string, string?> env, string name, int fallback)
    {
        if (!env.TryGetValue(name, out var v) || string.IsNullOrEmpty(v)) return fallback;
        return int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? n
            : throw new EnergySettingsException($"{name} must be a whole number (digits only).");
    }
}
