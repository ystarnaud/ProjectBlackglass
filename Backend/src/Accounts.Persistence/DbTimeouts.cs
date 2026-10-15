using System.Globalization;

namespace Blackglass.Accounts.Persistence;

/// <summary>
/// Bounds on database waits for the API and the admin CLI (spec §15.3): lock_timeout for any lock wait (row or table),
/// statement_timeout for any statement, set on every connection of their data sources. A timeout cancels the statement,
/// the open transaction rolls back, and nothing it did is committed. The migrator is not bounded this way: it keeps its
/// own lock_timeout and a migration may run long. Provisional configuration (spec §19).
/// </summary>
public sealed record DbTimeouts(int LockTimeoutMs, int StatementTimeoutMs)
{
    public const string LockTimeoutVar = "BLACKGLASS_DB_LOCK_TIMEOUT_MS";
    public const string StatementTimeoutVar = "BLACKGLASS_DB_STATEMENT_TIMEOUT_MS";

    public static DbTimeouts FromEnvironment(IReadOnlyDictionary<string, string?> env)
    {
        var timeouts = new DbTimeouts(Number(env, LockTimeoutVar, 5_000, 1, 60_000), Number(env, StatementTimeoutVar, 15_000, 1, 600_000));
        if (timeouts.LockTimeoutMs > timeouts.StatementTimeoutMs)
            throw new DbSettingsException($"{LockTimeoutVar} can't exceed {StatementTimeoutVar}.");
        return timeouts;
    }

    /// <summary>The libpq startup options that set both bounds for a connection's session.</summary>
    internal string StartupOptions =>
        string.Create(CultureInfo.InvariantCulture, $"-c lock_timeout={LockTimeoutMs} -c statement_timeout={StatementTimeoutMs}");

    static int Number(IReadOnlyDictionary<string, string?> env, string name, int fallback, int min, int max)
    {
        if (!env.TryGetValue(name, out var v) || string.IsNullOrEmpty(v)) return fallback;
        if (int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= min && n <= max) return n;
        throw new DbSettingsException($"{name} must be a whole number of milliseconds from {min} to {max}.");
    }
}
