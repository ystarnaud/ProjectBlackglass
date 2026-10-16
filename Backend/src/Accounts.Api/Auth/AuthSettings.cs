using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Blackglass.Accounts.Api.Auth;

public sealed class AuthSettingsException(string message) : Exception(message);

/// <summary>
/// Authentication settings from the environment (spec §4.2, §19). The dev provider and the admin routes are off unless set
/// to exactly "enabled"; any unknown value refuses to start. The admin key is read from a file and kept only as SHA-256.
/// Every limit is provisional configuration (spec §19).
/// </summary>
public sealed class AuthSettings
{
    public const string DevAuthVar = "BLACKGLASS_DEV_AUTH";
    public const string AdminRoutesVar = "BLACKGLASS_ADMIN_ROUTES";
    public const string AdminKeyFileVar = "BLACKGLASS_ADMIN_KEY_FILE";
    public const string TokenLifetimeVar = "BLACKGLASS_TOKEN_LIFETIME_MINUTES";
    public const string LoginRateVar = "BLACKGLASS_RATE_LOGIN_PER_MINUTE";
    public const string RequestRateVar = "BLACKGLASS_RATE_REQUESTS_PER_MINUTE";
    public const string LoginGlobalRateVar = "BLACKGLASS_RATE_LOGIN_GLOBAL_PER_MINUTE";
    public const string AdminRateVar = "BLACKGLASS_RATE_ADMIN_PER_MINUTE";
    public const string InventoryMaxOperationsVar = "BLACKGLASS_INVENTORY_MAX_OPERATIONS";
    public const string InventoryMaxBodyBytesVar = "BLACKGLASS_INVENTORY_MAX_BODY_BYTES";
    public const int MinAdminKeyLength = 32;

    public bool DevAuthEnabled { get; private init; }
    public bool AdminRoutesEnabled { get; private init; }
    public byte[]? AdminKeyHash { get; private init; }
    public int TokenLifetimeMinutes { get; private init; }
    public int LoginPerMinute { get; private init; }
    public int RequestsPerMinute { get; private init; }
    /// <summary>All logins together, whatever key they present: fabricated keys can't get around it.</summary>
    public int LoginGlobalPerMinute { get; private init; }
    /// <summary>All /dev/v1 admin requests together, checked before the admin key.</summary>
    public int AdminPerMinute { get; private init; }
    /// <summary>The most operations one inventory batch may hold (owner ruling S5-D6, provisional): 1 to 256, default 32.</summary>
    public int InventoryMaxOperations { get; private init; }
    /// <summary>The largest body POST /v1/inventory/operations reads (owner ruling S5-D6, provisional): 1024 to 1048576 bytes, default 16 KiB.</summary>
    public int InventoryMaxBodyBytes { get; private init; }

    public static AuthSettings FromEnvironment(IReadOnlyDictionary<string, string?> env)
    {
        var admin = Switch(env, AdminRoutesVar);
        return new AuthSettings
        {
            DevAuthEnabled = Switch(env, DevAuthVar),
            AdminRoutesEnabled = admin,
            AdminKeyHash = admin ? ReadAdminKeyHash(env) : null,
            TokenLifetimeMinutes = Number(env, TokenLifetimeVar, 60, 1, 1440),
            LoginPerMinute = Number(env, LoginRateVar, 10, 1, 1000),
            RequestsPerMinute = Number(env, RequestRateVar, 120, 1, 100_000),
            LoginGlobalPerMinute = Number(env, LoginGlobalRateVar, 60, 1, 100_000),
            AdminPerMinute = Number(env, AdminRateVar, 10, 1, 1000),
            InventoryMaxOperations = Number(env, InventoryMaxOperationsVar, 32, 1, 256),
            InventoryMaxBodyBytes = Number(env, InventoryMaxBodyBytesVar, 16 * 1024, 1024, 1024 * 1024),
        };
    }

    static bool Switch(IReadOnlyDictionary<string, string?> env, string name) =>
        !env.TryGetValue(name, out var v) || string.IsNullOrEmpty(v) ? false
        : v == "enabled" ? true
        : v == "disabled" ? false
        : throw new AuthSettingsException($"{name} must be 'enabled' or 'disabled'.");

    static int Number(IReadOnlyDictionary<string, string?> env, string name, int fallback, int min, int max)
    {
        if (!env.TryGetValue(name, out var v) || string.IsNullOrEmpty(v)) return fallback;
        if (int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= min && n <= max) return n;
        throw new AuthSettingsException($"{name} must be a whole number from {min} to {max}.");
    }

    static byte[] ReadAdminKeyHash(IReadOnlyDictionary<string, string?> env)
    {
        if (!env.TryGetValue(AdminKeyFileVar, out var path) || string.IsNullOrWhiteSpace(path))
            throw new AuthSettingsException($"{AdminRoutesVar}=enabled needs {AdminKeyFileVar}.");
        string key;
        try { key = File.ReadAllText(path).Trim(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new AuthSettingsException($"{AdminKeyFileVar} can't be read ({e.GetType().Name})."); }
        if (key.Length < MinAdminKeyLength) throw new AuthSettingsException($"The admin key must be at least {MinAdminKeyLength} characters.");
        return SHA256.HashData(Encoding.UTF8.GetBytes(key));
    }
}
