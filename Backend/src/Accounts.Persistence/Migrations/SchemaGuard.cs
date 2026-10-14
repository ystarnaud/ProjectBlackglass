using Npgsql;

namespace Blackglass.Accounts.Persistence.Migrations;

/// <summary>The API's startup check (spec §15.4): reads schema_migrations as the application role.</summary>
public static class SchemaGuard
{
    public static async Task<SchemaStatus> CheckAsync(NpgsqlDataSource dataSource, MigrationCatalog catalog, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        return SchemaComparison.Compare(catalog, await SchemaMigrationsTable.ReadAsync(conn, ct) ?? []);
    }
}
