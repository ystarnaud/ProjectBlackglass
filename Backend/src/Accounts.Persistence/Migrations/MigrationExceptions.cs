namespace Blackglass.Accounts.Persistence.Migrations;

public sealed class MigrationCatalogException(string message) : Exception(message);

public sealed class SchemaMismatchException(SchemaStatus status) : Exception($"schema {status.State}: {status.Detail}")
{
    public SchemaStatus Status { get; } = status;
}
