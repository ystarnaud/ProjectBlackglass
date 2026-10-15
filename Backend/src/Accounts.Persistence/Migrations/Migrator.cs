using System.Globalization;
using Dapper;
using Npgsql;

namespace Blackglass.Accounts.Persistence.Migrations;

public sealed record MigratorOptions
{
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan LockWait { get; init; } = TimeSpan.FromSeconds(60);
}

public sealed record MigrationResult(IReadOnlyList<int> Applied, int SchemaVersion);

/// <summary>Applies pending migrations forward-only (spec §15.4). A session-level advisory lock serializes migrators on
/// one database; each file runs in its own transaction with lock_timeout set; drift or an unknown newer version stops
/// everything before any file runs.</summary>
public sealed class Migrator(NpgsqlDataSource dataSource, MigrationCatalog catalog, MigratorOptions? options = null)
{
    /// <summary>"BLACKGLS" as a 64-bit key for pg_advisory_lock.</summary>
    public const long AdvisoryLockKey = 0x424C_4143_4B47_4C53;

    readonly MigratorOptions options = options ?? new MigratorOptions();

    public async Task<MigrationResult> MigrateAsync(int? targetVersion = null, CancellationToken ct = default)
    {
        var target = targetVersion ?? catalog.LatestVersion;
        if (target < 0 || target > catalog.LatestVersion) throw new ArgumentOutOfRangeException(nameof(targetVersion));

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await AcquireLockAsync(conn, ct);
        try
        {
            await conn.ExecuteAsync(new CommandDefinition(SchemaMigrationsTable.CreateSql, cancellationToken: ct));
            var applied = await SchemaMigrationsTable.ReadAsync(conn, ct) ?? [];
            var status = SchemaComparison.Compare(catalog, applied);
            if (status.State is SchemaState.Drift or SchemaState.Ahead) throw new SchemaMismatchException(status);

            var current = status.DatabaseVersion ?? -1;
            var done = new List<int>();
            foreach (var script in catalog.Scripts.Where(s => s.Version > current && s.Version <= target))
            {
                await ApplyAsync(conn, script, ct);
                done.Add(script.Version);
            }
            return new MigrationResult(done, done.Count > 0 ? done[^1] : current);
        }
        finally
        {
            // Npgsql also resets the session (releasing the lock) when the connection returns to the pool.
            if (conn.State == System.Data.ConnectionState.Open)
                await conn.ExecuteAsync("select pg_advisory_unlock(@key)", new { key = AdvisoryLockKey });
        }
    }

    async Task AcquireLockAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + options.LockWait;
        while (!await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                   "select pg_try_advisory_lock(@key)", new { key = AdvisoryLockKey }, cancellationToken: ct)))
        {
            if (DateTime.UtcNow >= deadline) throw new MigratorBusyException(options.LockWait);
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }
    }

    async Task ApplyAsync(NpgsqlConnection conn, MigrationScript script, CancellationToken ct)
    {
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            // set_config(…, true) is SET LOCAL with a bound parameter (SET itself takes no parameters).
            await conn.ExecuteAsync(new CommandDefinition(
                "select set_config('lock_timeout', @ms, true)",
                new { ms = ((long)options.LockTimeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) }, tx, cancellationToken: ct));
            await using (var cmd = new NpgsqlCommand(script.Sql, conn, tx) { CommandTimeout = 0 })
                await cmd.ExecuteNonQueryAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition(
                "insert into schema_migrations (version, name, checksum) values (@Version, @Name, @Checksum)",
                new { script.Version, script.Name, script.Checksum }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
        }
        catch (PostgresException e)
        {
            throw new MigrationFailedException(script.FileName, e); // disposing tx rolls back
        }
    }
}
