using System.Text;
using Blackglass.Accounts.Persistence.Migrations;

namespace Blackglass.Accounts.IntegrationTests.Unit;

public class SchemaComparisonTests
{
    static readonly MigrationCatalog Catalog = MigrationCatalog.Parse([
        ("0000_a.sql", Encoding.UTF8.GetBytes("select 0;")),
        ("0001_b.sql", Encoding.UTF8.GetBytes("select 1;")),
        ("0002_c.sql", Encoding.UTF8.GetBytes("select 2;")),
    ]);

    static AppliedMigration Row(int v) => new(v, Catalog.Scripts[v].Name, Catalog.Scripts[v].Checksum);

    [Fact]
    public void Empty_database_is_behind()
    {
        var s = SchemaComparison.Compare(Catalog, []);
        Assert.Equal(SchemaState.Behind, s.State);
        Assert.Null(s.DatabaseVersion);
        Assert.Equal(2, s.ExpectedVersion);
    }

    [Fact]
    public void Partial_prefix_is_behind() =>
        Assert.Equal(SchemaState.Behind, SchemaComparison.Compare(Catalog, [Row(0), Row(1)]).State);

    [Fact]
    public void Full_match_is_current()
    {
        var s = SchemaComparison.Compare(Catalog, [Row(2), Row(0), Row(1)]);
        Assert.Equal(SchemaState.Current, s.State);
        Assert.Equal(2, s.DatabaseVersion);
    }

    [Fact]
    public void Changed_checksum_is_drift_and_names_the_file()
    {
        var s = SchemaComparison.Compare(Catalog, [Row(0), Row(1) with { Checksum = new byte[32] }]);
        Assert.Equal(SchemaState.Drift, s.State);
        Assert.Contains("0001_b.sql", s.Detail);
    }

    [Fact]
    public void Renamed_file_is_drift() =>
        Assert.Equal(SchemaState.Drift, SchemaComparison.Compare(Catalog, [Row(0) with { Name = "other" }]).State);

    [Fact]
    public void Gap_in_history_is_drift() =>
        Assert.Equal(SchemaState.Drift, SchemaComparison.Compare(Catalog, [Row(0), Row(2)]).State);

    [Fact]
    public void Unknown_newer_versions_are_ahead()
    {
        var s = SchemaComparison.Compare(Catalog, [Row(0), Row(1), Row(2), new AppliedMigration(3, "d", new byte[32])]);
        Assert.Equal(SchemaState.Ahead, s.State);
        Assert.Equal(3, s.DatabaseVersion);
        Assert.Contains("0003", s.Detail);
    }

    [Fact]
    public void Drift_in_the_known_part_wins_over_ahead() =>
        Assert.Equal(SchemaState.Drift, SchemaComparison.Compare(Catalog,
            [Row(0) with { Checksum = new byte[32] }, Row(1), Row(2), new AppliedMigration(3, "d", new byte[32])]).State);
}
