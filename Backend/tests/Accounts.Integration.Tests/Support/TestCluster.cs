using System.Globalization;
using Dapper;
using Npgsql;

namespace Blackglass.Accounts.IntegrationTests.Support;

/// <summary>The Blackglass integration-test cluster, reached through the SSH tunnel on 127.0.0.1 (host from BLACKGLASS_TEST_PG_HOST,
/// port from BLACKGLASS_TEST_PG_PORT, default 55432). The VM runner (tools/vm-test.sh) sets the host to the cluster container's own
/// address: a connection from its loopback would match the image's trust rule and skip password authentication. Passwords are read from ~/.blackglass/testdb-secrets (or
/// BLACKGLASS_TEST_PG_SECRETS), filled by `tools/testdb.sh up`. Refuses any cluster but blackglass-test.</summary>
public sealed class TestCluster
{
    public const string ExpectedClusterName = "blackglass-test";
    const string DatabasePrefix = "bg_t_";
    static readonly Lazy<Task<TestCluster>> Shared = new(ConnectAsync);

    public static Task<TestCluster> Instance => Shared.Value;

    public string Host { get; }
    public int Port { get; }
    public string SuperuserPassword { get; }
    public string MigratorPassword { get; }
    public string AppPassword { get; }
    public string OutsiderPassword { get; } = Guid.NewGuid().ToString("N");

    TestCluster(string host, int port, string su, string migrator, string app) =>
        (Host, Port, SuperuserPassword, MigratorPassword, AppPassword) = (host, port, su, migrator, app);

    public static void EnsureTestCluster(string clusterName)
    {
        if (clusterName != ExpectedClusterName)
            throw new InvalidOperationException($"Refusing to run destructive tests: cluster_name is '{clusterName}', not '{ExpectedClusterName}'. Check the SSH tunnel.");
    }

    public NpgsqlDataSource DataSource(string database, string user, string password) =>
        NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder
        {
            Host = Host, Port = Port, Database = database, Username = user, Password = password,
            MaxPoolSize = 5, Timeout = 10, ApplicationName = "blackglass-tests",
        });

    public async Task<TestDatabase> CreateDatabaseAsync()
    {
        var name = $"{DatabasePrefix}{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid().ToString("N")[..8]}";
        await using var admin = DataSource("postgres", "postgres", SuperuserPassword);
        await using var conn = await admin.OpenConnectionAsync();
        await conn.ExecuteAsync($"create database \"{name}\" owner accounts_migrator");
        return new TestDatabase(this, name);
    }

    static async Task<TestCluster> ConnectAsync()
    {
        var dir = Environment.GetEnvironmentVariable("BLACKGLASS_TEST_PG_SECRETS")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".blackglass", "testdb-secrets");
        if (!Directory.Exists(dir))
            throw new InvalidOperationException($"No test-cluster secrets at {dir}. Run `bash Backend/tools/testdb.sh up`, then keep `bash Backend/tools/testdb.sh tunnel` open.");
        string Read(string name) => File.ReadAllText(Path.Combine(dir, name)).Trim();
        var port = int.Parse(Environment.GetEnvironmentVariable("BLACKGLASS_TEST_PG_PORT") ?? "55432", CultureInfo.InvariantCulture);
        var host = Environment.GetEnvironmentVariable("BLACKGLASS_TEST_PG_HOST") ?? "127.0.0.1";
        var cluster = new TestCluster(host, port, Read("pg_superuser_password"), Read("pg_migrator_password"), Read("pg_app_password"));

        await using var admin = cluster.DataSource("postgres", "postgres", cluster.SuperuserPassword);
        await using var conn = await admin.OpenConnectionAsync();
        EnsureTestCluster(await conn.ExecuteScalarAsync<string>("select current_setting('cluster_name')") ?? "");

        // Leftovers from crashed runs: test databases older than two hours.
        var cutoff = DateTime.UtcNow.AddHours(-2).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        foreach (var stale in await conn.QueryAsync<string>(
                     "select datname from pg_database where datname like 'bg\\_t\\_%' and substr(datname, 6, 14) < @cutoff", new { cutoff }))
            await conn.ExecuteAsync($"drop database if exists \"{stale}\" with (force)");

        // A login role with no grants, to prove PUBLIC has no access to application databases.
        await conn.ExecuteAsync("""
            do $$ begin
              if not exists (select from pg_roles where rolname = 'bg_test_outsider') then
                create role bg_test_outsider login nosuperuser nocreatedb nocreaterole;
              end if;
            end $$;
            """);
        await conn.ExecuteAsync($"alter role bg_test_outsider password '{cluster.OutsiderPassword}'");
        return cluster;
    }
}
