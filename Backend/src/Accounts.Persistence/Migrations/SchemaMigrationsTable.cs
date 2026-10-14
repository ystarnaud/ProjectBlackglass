using Dapper;
using Npgsql;

namespace Blackglass.Accounts.Persistence.Migrations;

public static class SchemaMigrationsTable
{
    public const string CreateSql = """
        create table if not exists schema_migrations (
          version    integer primary key check (version >= 0),
          name       text not null,
          checksum   bytea not null check (length(checksum) = 32),
          applied_at timestamptz not null default now(),
          applied_by text not null default current_user)
        """;

    /// <summary>The applied rows, or null when schema_migrations doesn't exist yet.</summary>
    public static async Task<IReadOnlyList<AppliedMigration>?> ReadAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var exists = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select to_regclass('public.schema_migrations') is not null", cancellationToken: ct));
        if (!exists) return null;
        return (await conn.QueryAsync<AppliedMigration>(new CommandDefinition(
            "select version, name, checksum from schema_migrations order by version", cancellationToken: ct))).AsList();
    }
}
