namespace Blackglass.Accounts.Persistence.Migrations;

public sealed record AppliedMigration(int Version, string Name, byte[] Checksum);

public enum SchemaState { Current, Behind, Ahead, Drift }

public sealed record SchemaStatus(SchemaState State, int? DatabaseVersion, int ExpectedVersion, string Detail);

/// <summary>Compares the catalog with schema_migrations. The history must be exactly 0000, 0001, ... and every
/// version the catalog knows must match by name and checksum. Drift in the known part outranks "ahead".</summary>
public static class SchemaComparison
{
    public static SchemaStatus Compare(MigrationCatalog catalog, IReadOnlyList<AppliedMigration> applied)
    {
        var expected = catalog.LatestVersion;
        var rows = applied.OrderBy(a => a.Version).ToList();
        int? dbVersion = rows.Count == 0 ? null : rows[^1].Version;

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Version != i)
                return new(SchemaState.Drift, dbVersion, expected, $"schema_migrations is not contiguous: found {row.Version:D4} where {i:D4} was expected.");
            if (row.Version > expected)
                return new(SchemaState.Ahead, dbVersion, expected, $"the database has {expected + 1:D4}..{dbVersion:D4}, which this build doesn't know.");
            var script = catalog.Scripts[row.Version];
            if (row.Name != script.Name || !row.Checksum.AsSpan().SequenceEqual(script.Checksum))
                return new(SchemaState.Drift, dbVersion, expected, $"{script.FileName} differs from the applied {row.Version:D4}_{row.Name}.sql (name or checksum).");
        }

        return dbVersion == expected
            ? new(SchemaState.Current, dbVersion, expected, $"at {expected:D4}.")
            : new(SchemaState.Behind, dbVersion, expected, dbVersion is null ? $"no migrations applied; expected {expected:D4}." : $"at {dbVersion:D4}; expected {expected:D4}.");
    }
}
