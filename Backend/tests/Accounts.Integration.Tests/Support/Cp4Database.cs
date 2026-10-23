using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Npgsql;

namespace Blackglass.Accounts.IntegrationTests.Support;

/// <summary>The cluster as the CP4 run sees it before any write: name, major version, the data directory's system identifier
/// (pg_control_system), and whether the connection is the cluster superuser.</summary>
public sealed record Cp4ClusterIdentity(string ClusterName, int ServerVersionNum, string SystemIdentifier, string CurrentUser, bool IsSuperuser);

public sealed record Cp4Role(string Name, bool Super, bool CreateRole, bool CreateDb);

/// <summary>One catalogue row's identity: any change to the row (an ALTER, a password) gives it a new xmin.</summary>
public sealed record Cp4CatalogRow(string Name, uint Oid, uint Xmin);

/// <summary>The read-only fingerprint of the shared cluster taken before and after CP4: every database and role row, and when
/// the server started (a restart changes it).</summary>
public sealed record Cp4Baseline(DateTime PostmasterStart, IReadOnlyList<Cp4CatalogRow> Databases, IReadOnlyList<Cp4CatalogRow> Roles);

/// <summary>What CP4 created, written next to the run so a crashed run can still be cleaned up by name and OID.</summary>
public sealed record Cp4DatabaseRecord(string Name, uint Oid, string SystemIdentifier, DateTime CreatedUtc);

/// <summary>A condition CP4 does not repair: the run stops and the operator decides.</summary>
public sealed class Cp4Refusal(string message) : Exception(message);

/// <summary>The few cluster-level statements CP4 may issue. Everything but the two database statements only reads.</summary>
public interface ICp4ClusterAdmin
{
    Task<Cp4ClusterIdentity> IdentityAsync();
    Task<IReadOnlyList<Cp4Role>> RolesAsync(IReadOnlyList<string> names);
    Task<uint?> DatabaseOidAsync(string name);
    Task CreateDatabaseAsync(string name, string owner);
    Task DropDatabaseAsync(string name);
    Task<Cp4Baseline> BaselineAsync();
}

/// <summary>
/// Stage 13 CP4's safe use of the shared test cluster (the owner's mandatory correction to TestCluster's start-up): it never
/// drops other runs' databases and never creates or changes a role. The cluster's identity (the unchanged
/// <see cref="TestCluster.EnsureTestCluster"/> check, exactly blackglass-test, PostgreSQL 17, the expected system identifier,
/// the superuser) and the two application roles are checked before the one write that makes its uniquely named database; the
/// record of that database (name and OID) is written at once, and only a database matching both is ever removed.
/// </summary>
public static partial class Cp4Database
{
    public const string Owner = "accounts_migrator";
    public static readonly IReadOnlyList<string> RequiredRoles = ["accounts_app", "accounts_migrator"];

    [GeneratedRegex(@"^bg_t_[0-9]{14}_cp4_[0-9a-f]{8}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static bool IsCp4Name(string? name) => name is not null && NamePattern().IsMatch(name);

    /// <param name="suffix">Eight lowercase hex characters (tests pin it; a run uses random ones).</param>
    public static async Task<Cp4DatabaseRecord> CreateAsync(ICp4ClusterAdmin cluster, string expectedSystemIdentifier, string recordPath,
        Func<DateTime> utcNow, Func<string>? suffix = null)
    {
        if (File.Exists(recordPath))
            throw new Cp4Refusal($"A CP4 database record from an earlier run exists ({recordPath}): clean that run up first; nothing is repaired automatically.");
        var identity = await VerifyAsync(cluster, expectedSystemIdentifier);

        var roles = await cluster.RolesAsync(RequiredRoles);
        foreach (var name in RequiredRoles)
        {
            var role = roles.SingleOrDefault(r => r.Name == name) ?? throw new Cp4Refusal($"The role {name} is missing: CP4 never creates roles.");
            if (role.Super || role.CreateRole || role.CreateDb)
                throw new Cp4Refusal($"The role {name} has elevated attributes: unexpected state, never altered by CP4.");
        }

        var now = utcNow();
        var database = $"bg_t_{now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}_cp4_{(suffix ?? RandomSuffix)()}";
        if (!IsCp4Name(database)) throw new Cp4Refusal("The generated database name is not a CP4 name.");
        if (await cluster.DatabaseOidAsync(database) is not null) throw new Cp4Refusal($"A database named {database} already exists.");

        await cluster.CreateDatabaseAsync(database, Owner);
        var oid = await cluster.DatabaseOidAsync(database) ?? throw new Cp4Refusal($"{database} is absent right after its creation.");
        var record = new Cp4DatabaseRecord(database, oid, identity.SystemIdentifier, now);
        WriteRecord(recordPath, record);
        return record;
    }

    /// <summary>Removes the recorded database if it is still the one CP4 created (same name, same OID, same cluster), then
    /// deletes the record. False when it was already gone.</summary>
    public static async Task<bool> DropAsync(ICp4ClusterAdmin cluster, string expectedSystemIdentifier, string recordPath)
    {
        var record = ReadRecord(recordPath);
        if (!IsCp4Name(record.Name)) throw new Cp4Refusal("The record does not name a CP4 database: nothing removed.");
        if (record.SystemIdentifier != expectedSystemIdentifier) throw new Cp4Refusal("The record was made on another cluster: nothing removed.");
        await VerifyAsync(cluster, expectedSystemIdentifier);

        var oid = await cluster.DatabaseOidAsync(record.Name);
        if (oid is null)
        {
            File.Delete(recordPath);
            return false;
        }
        if (oid != record.Oid) throw new Cp4Refusal($"{record.Name} now has OID {oid}, not the recorded {record.Oid}: another database, never removed by CP4.");
        await cluster.DropDatabaseAsync(record.Name);
        if (await cluster.DatabaseOidAsync(record.Name) is not null) throw new Cp4Refusal($"{record.Name} is still present after its removal.");
        File.Delete(recordPath);
        return true;
    }

    static async Task<Cp4ClusterIdentity> VerifyAsync(ICp4ClusterAdmin cluster, string expectedSystemIdentifier)
    {
        var identity = await cluster.IdentityAsync();
        TestCluster.EnsureTestCluster(identity.ClusterName); // the existing check, unchanged
        if (identity.ClusterName != TestCluster.ExpectedClusterName)
            throw new Cp4Refusal($"cluster_name is '{identity.ClusterName}', not '{TestCluster.ExpectedClusterName}'.");
        if (identity.ServerVersionNum is < 170000 or > 179999) throw new Cp4Refusal($"The server is not PostgreSQL 17 ({identity.ServerVersionNum}).");
        if (string.IsNullOrEmpty(expectedSystemIdentifier) || identity.SystemIdentifier != expectedSystemIdentifier)
            throw new Cp4Refusal("The cluster's system identifier is not the expected one (BLACKGLASS_CP4_SYSTEM_ID).");
        if (!identity.IsSuperuser) throw new Cp4Refusal($"The connection ({identity.CurrentUser}) is not the cluster superuser.");
        return identity;
    }

    public static void WriteRecord(string path, Cp4DatabaseRecord record)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(record, Json));
        File.Move(temp, path, overwrite: false);
    }

    public static Cp4DatabaseRecord ReadRecord(string path) =>
        JsonSerializer.Deserialize<Cp4DatabaseRecord>(File.ReadAllText(path), Json) ?? throw new Cp4Refusal("The CP4 database record is empty.");

    /// <summary>Every difference between two fingerprints of the shared cluster, in a fixed order; empty when it is unchanged.</summary>
    public static IReadOnlyList<string> Differences(Cp4Baseline before, Cp4Baseline after)
    {
        var changes = new List<string>();
        if (before.PostmasterStart != after.PostmasterStart) changes.Add("postmaster restarted");
        Compare("database", before.Databases, after.Databases, changes);
        Compare("role", before.Roles, after.Roles, changes);
        return changes;
    }

    static void Compare(string kind, IReadOnlyList<Cp4CatalogRow> before, IReadOnlyList<Cp4CatalogRow> after, List<string> changes)
    {
        var old = before.ToDictionary(r => r.Name, StringComparer.Ordinal);
        var now = after.ToDictionary(r => r.Name, StringComparer.Ordinal);
        foreach (var r in after.Where(r => !old.ContainsKey(r.Name))) changes.Add($"{kind} added: {r.Name} (oid {r.Oid})");
        foreach (var r in before.Where(r => !now.ContainsKey(r.Name))) changes.Add($"{kind} removed: {r.Name} (oid {r.Oid})");
        foreach (var r in before.Where(r => now.ContainsKey(r.Name)))
        {
            var n = now[r.Name];
            if (n.Oid != r.Oid) changes.Add($"{kind} replaced: {r.Name} (oid {r.Oid} -> {n.Oid})");
            else if (n.Xmin != r.Xmin) changes.Add($"{kind} changed: {r.Name} (xmin {r.Xmin} -> {n.Xmin})");
        }
    }

    static string RandomSuffix() => Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4));

    /// <summary>This file's path, for the source audit (Cp4DatabaseSafetyTests).</summary>
    public static string SourcePath() => Here();

    static string Here([CallerFilePath] string path = "") => path;
}

/// <summary>The test cluster's address and passwords, read exactly as the integration tests read them (the
/// BLACKGLASS_TEST_PG_* variables, ~/.blackglass/testdb-secrets), with no connection made and nothing written.</summary>
public sealed class Cp4Cluster
{
    public string Host { get; }
    public int Port { get; }
    public string SuperuserPassword { get; }
    public string MigratorPassword { get; }
    public string AppPassword { get; }

    Cp4Cluster(string host, int port, string su, string migrator, string app) => (Host, Port, SuperuserPassword, MigratorPassword, AppPassword) = (host, port, su, migrator, app);

    public static Cp4Cluster FromSecrets()
    {
        var dir = Environment.GetEnvironmentVariable("BLACKGLASS_TEST_PG_SECRETS")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".blackglass", "testdb-secrets");
        if (!Directory.Exists(dir)) throw new Cp4Refusal($"No test-cluster secrets at {dir}.");
        string Read(string name) => File.ReadAllText(Path.Combine(dir, name)).Trim();
        var port = int.Parse(Environment.GetEnvironmentVariable("BLACKGLASS_TEST_PG_PORT") ?? "55432", CultureInfo.InvariantCulture);
        var host = Environment.GetEnvironmentVariable("BLACKGLASS_TEST_PG_HOST") ?? "127.0.0.1";
        return new Cp4Cluster(host, port, Read("pg_superuser_password"), Read("pg_migrator_password"), Read("pg_app_password"));
    }

    public NpgsqlDataSource DataSource(string database, string user, string password) =>
        NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder
        {
            Host = Host, Port = Port, Database = database, Username = user, Password = password,
            MaxPoolSize = 5, Timeout = 10, ApplicationName = "blackglass-cp4",
        });
}

/// <summary>The real <see cref="ICp4ClusterAdmin"/>: the cluster superuser on the maintenance database.</summary>
public sealed class NpgsqlCp4ClusterAdmin(Cp4Cluster cluster) : ICp4ClusterAdmin, IAsyncDisposable
{
    readonly NpgsqlDataSource admin = cluster.DataSource("postgres", "postgres", cluster.SuperuserPassword);

    public async Task<Cp4ClusterIdentity> IdentityAsync()
    {
        await using var conn = await admin.OpenConnectionAsync();
        var row = await conn.QuerySingleAsync<(string Name, int Version, string SystemId, string User, bool Super)>("""
            select current_setting('cluster_name'), current_setting('server_version_num')::int, (select system_identifier::text from pg_control_system()),
                   current_user::text, (select rolsuper from pg_roles where rolname = current_user)
            """);
        return new Cp4ClusterIdentity(row.Name, row.Version, row.SystemId, row.User, row.Super);
    }

    public async Task<IReadOnlyList<Cp4Role>> RolesAsync(IReadOnlyList<string> names)
    {
        await using var conn = await admin.OpenConnectionAsync();
        return (await conn.QueryAsync<(string, bool, bool, bool)>("select rolname::text, rolsuper, rolcreaterole, rolcreatedb from pg_roles where rolname = any(@names)",
            new { names = names.ToArray() })).Select(r => new Cp4Role(r.Item1, r.Item2, r.Item3, r.Item4)).ToList();
    }

    public async Task<uint?> DatabaseOidAsync(string name)
    {
        await using var conn = await admin.OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<uint?>("select oid from pg_database where datname = @name", new { name });
    }

    public async Task CreateDatabaseAsync(string name, string owner)
    {
        if (!Cp4Database.IsCp4Name(name) || owner != Cp4Database.Owner) throw new Cp4Refusal("Only a CP4 database owned by the migrator is ever made.");
        await using var conn = await admin.OpenConnectionAsync();
        await conn.ExecuteAsync($"create database \"{name}\" owner {Cp4Database.Owner}");
    }

    public async Task DropDatabaseAsync(string name)
    {
        if (!Cp4Database.IsCp4Name(name)) throw new Cp4Refusal("Only a CP4 database is ever removed.");
        await using var conn = await admin.OpenConnectionAsync();
        await conn.ExecuteAsync($"drop database \"{name}\" with (force)");
    }

    public async Task<Cp4Baseline> BaselineAsync()
    {
        await using var conn = await admin.OpenConnectionAsync();
        var start = await conn.ExecuteScalarAsync<DateTime>("select pg_postmaster_start_time()");
        var databases = (await conn.QueryAsync<(string, uint, uint)>("select datname::text, oid, xmin from pg_database order by datname"))
            .Select(r => new Cp4CatalogRow(r.Item1, r.Item2, r.Item3)).ToList();
        var roles = (await conn.QueryAsync<(string, uint, uint)>("select rolname::text, oid, xmin from pg_authid order by rolname"))
            .Select(r => new Cp4CatalogRow(r.Item1, r.Item2, r.Item3)).ToList();
        return new Cp4Baseline(start.ToUniversalTime(), databases, roles);
    }

    public ValueTask DisposeAsync() => admin.DisposeAsync();
}
