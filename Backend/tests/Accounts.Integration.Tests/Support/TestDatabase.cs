using Dapper;
using Npgsql;

namespace Blackglass.Accounts.IntegrationTests.Support;

/// <summary>One fresh database owned by accounts_migrator, dropped on dispose.</summary>
public sealed class TestDatabase : IAsyncDisposable
{
    readonly TestCluster cluster;
    readonly Dictionary<string, NpgsqlDataSource> sources = new();
    readonly List<SecretFile> passwordFiles = new();
    readonly List<NpgsqlDataSource> boundedSources = new();

    public string Name { get; }

    internal TestDatabase(TestCluster cluster, string name) => (this.cluster, Name) = (cluster, name);

    NpgsqlDataSource Source(string user, string password) =>
        sources.TryGetValue(user, out var ds) ? ds : sources[user] = cluster.DataSource(Name, user, password);

    public NpgsqlDataSource AsSuperuser() => Source("postgres", cluster.SuperuserPassword);
    public NpgsqlDataSource AsMigrator() => Source("accounts_migrator", cluster.MigratorPassword);
    public NpgsqlDataSource AsApp() => Source("accounts_app", cluster.AppPassword);
    public NpgsqlDataSource AsOutsider() => Source("bg_test_outsider", cluster.OutsiderPassword);

    /// <summary>The app role through the production factory (DbSettings.CreateDataSource), with these wait bounds.</summary>
    public NpgsqlDataSource AsAppWith(Blackglass.Accounts.Persistence.DbTimeouts timeouts)
    {
        var ds = new Blackglass.Accounts.Persistence.DbSettings("127.0.0.1", cluster.Port, Name, "accounts_app", cluster.AppPassword)
            .CreateDataSource("blackglass-tests", maxPoolSize: 5, timeouts: timeouts);
        boundedSources.Add(ds);
        return ds;
    }

    public string Password(string role) => role switch
    {
        "migrator" => cluster.MigratorPassword,
        "app" => cluster.AppPassword,
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    /// <summary>The BLACKGLASS_DB_* environment the API commands read, with the password in a <see cref="SecretFile"/>
    /// deleted with this database.</summary>
    public Dictionary<string, string?> EnvFor(string role)
    {
        var file = SecretFile.Write(Password(role));
        passwordFiles.Add(file);
        return new()
        {
            ["BLACKGLASS_DB_HOST"] = "127.0.0.1",
            ["BLACKGLASS_DB_PORT"] = cluster.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["BLACKGLASS_DB_NAME"] = Name,
            ["BLACKGLASS_DB_USER"] = role == "migrator" ? "accounts_migrator" : "accounts_app",
            ["BLACKGLASS_DB_PASSWORD_FILE"] = file.Path,
        };
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var f in passwordFiles) f.Dispose(); // first: nothing below may leave a password behind
        foreach (var ds in sources.Values.Concat(boundedSources)) await ds.DisposeAsync();
        await using var admin = cluster.DataSource("postgres", "postgres", cluster.SuperuserPassword);
        await using var conn = await admin.OpenConnectionAsync();
        await conn.ExecuteAsync($"drop database if exists \"{Name}\" with (force)");
    }
}
