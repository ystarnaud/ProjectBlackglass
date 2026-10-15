using System.Globalization;
using Npgsql;

namespace Blackglass.Accounts.Persistence;

public sealed class DbSettingsException(string message) : Exception(message);

/// <summary>Connection settings for one database role. The password comes from a file (a Compose secret), never from
/// an environment value, and never appears in <see cref="ToString"/> or in any error message.</summary>
public sealed class DbSettings(string host, int port, string database, string username, string password)
{
    public const string HostVar = "BLACKGLASS_DB_HOST";
    public const string PortVar = "BLACKGLASS_DB_PORT";
    public const string NameVar = "BLACKGLASS_DB_NAME";
    public const string UserVar = "BLACKGLASS_DB_USER";
    public const string PasswordFileVar = "BLACKGLASS_DB_PASSWORD_FILE";

    public string Host { get; } = host;
    public int Port { get; } = port;
    public string Database { get; } = database;
    public string Username { get; } = username;

    public static DbSettings FromEnvironment(IReadOnlyDictionary<string, string?> env)
    {
        string Required(string name) =>
            env.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v : throw new DbSettingsException($"{name} is not set.");

        var portText = env.TryGetValue(PortVar, out var p) && !string.IsNullOrWhiteSpace(p) ? p : "5432";
        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
            throw new DbSettingsException($"{PortVar} must be a port number between 1 and 65535.");

        var passwordFile = Required(PasswordFileVar);
        string password;
        try { password = File.ReadAllText(passwordFile).TrimEnd('\r', '\n'); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new DbSettingsException($"The file named by {PasswordFileVar} can't be read ({e.GetType().Name}).");
        }
        if (password.Length == 0) throw new DbSettingsException($"The file named by {PasswordFileVar} is empty.");

        return new DbSettings(Required(HostVar), port, Required(NameVar), Required(UserVar), password);
    }

    /// <param name="timeouts">Wait bounds set on every connection (the API and the admin CLI); null for none (the migrator).</param>
    public NpgsqlDataSource CreateDataSource(string applicationName, int maxPoolSize = 20, DbTimeouts? timeouts = null) =>
        NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder
        {
            Host = Host, Port = Port, Database = Database, Username = Username, Password = password,
            ApplicationName = applicationName, Timeout = 5, MaxPoolSize = maxPoolSize, Options = timeouts?.StartupOptions,
            // The server's bounds decide; the client's own command timeout (default 30 s) must never fire first.
            CommandTimeout = timeouts is null ? 30 : timeouts.StatementTimeoutMs / 1000 + 30,
        });

    public override string ToString() => $"{Username}@{Host}:{Port}/{Database}";
}
