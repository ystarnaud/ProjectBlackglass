using Dapper;
using Npgsql;

namespace Blackglass.Accounts.IntegrationTests.Support;

/// <summary>One fresh database owned by accounts_migrator, dropped on dispose.</summary>
public sealed class TestDatabase : IAsyncDisposable
{
    readonly TestCluster cluster;
    readonly Dictionary<string, NpgsqlDataSource> sources = new();
    readonly List<string> tempFiles = new();

    public string Name { get; }

    internal TestDatabase(TestCluster cluster, string name) => (this.cluster, Name) = (cluster, name);

    NpgsqlDataSource Source(string user, string password) =>
        sources.TryGetValue(user, out var ds) ? ds : sources[user] = cluster.DataSource(Name, user, password);

    public NpgsqlDataSource AsSuperuser() => Source("postgres", cluster.SuperuserPassword);
    public NpgsqlDataSource AsMigrator() => Source("accounts_migrator", cluster.MigratorPassword);
    public NpgsqlDataSource AsApp() => Source("accounts_app", cluster.AppPassword);
    public NpgsqlDataSource AsOutsider() => Source("bg_test_outsider", cluster.OutsiderPassword);

    public string Password(string role) => role switch
    {
        "migrator" => cluster.MigratorPassword,
        "app" => cluster.AppPassword,
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    /// <summary>The BLACKGLASS_DB_* environment the API commands read, with the password in a temp file.</summary>
    public Dictionary<string, string?> EnvFor(string role)
    {
        var file = Path.Combine(Path.GetTempPath(), $"bg-test-pw-{Guid.NewGuid():N}");
        File.WriteAllText(file, Password(role));
        tempFiles.Add(file);
        return new()
        {
            ["BLACKGLASS_DB_HOST"] = "127.0.0.1",
            ["BLACKGLASS_DB_PORT"] = cluster.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["BLACKGLASS_DB_NAME"] = Name,
            ["BLACKGLASS_DB_USER"] = role == "migrator" ? "accounts_migrator" : "accounts_app",
            ["BLACKGLASS_DB_PASSWORD_FILE"] = file,
        };
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var ds in sources.Values) await ds.DisposeAsync();
        foreach (var f in tempFiles) File.Delete(f);
        await using var admin = cluster.DataSource("postgres", "postgres", cluster.SuperuserPassword);
        await using var conn = await admin.OpenConnectionAsync();
        await conn.ExecuteAsync($"drop database if exists \"{Name}\" with (force)");
    }
}
