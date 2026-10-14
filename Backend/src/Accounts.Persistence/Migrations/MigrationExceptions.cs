namespace Blackglass.Accounts.Persistence.Migrations;

public sealed class MigrationCatalogException(string message) : Exception(message);

public sealed class SchemaMismatchException(SchemaStatus status) : Exception($"schema {status.State}: {status.Detail}")
{
    public SchemaStatus Status { get; } = status;
}

public sealed class MigrationFailedException(string fileName, Npgsql.PostgresException inner)
    : Exception($"{fileName} failed: {inner.SqlState} {inner.MessageText}", inner)
{
    public string FileName { get; } = fileName;
}

public sealed class MigratorBusyException(TimeSpan waited)
    : Exception($"Another migrator holds the migration lock on this database; gave up after {waited.TotalSeconds:0} s.");
