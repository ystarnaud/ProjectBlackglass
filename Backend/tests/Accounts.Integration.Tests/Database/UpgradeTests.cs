using Dapper;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Migrations;

namespace Blackglass.Accounts.IntegrationTests.Database;

[Trait("Requires", "Postgres")]
public class UpgradeTests
{
    static readonly string FixtureInvariants = Path.Combine(AppContext.BaseDirectory, "Fixtures", "invariants");

    public static TheoryData<int> RealVersions()
    {
        var data = new TheoryData<int>();
        foreach (var s in MigrationCatalog.Embedded.Scripts) data.Add(s.Version);
        return data;
    }

    /// <summary>Spec §15.4: every migration is applied to a database at the previous version, seeded, then checked.</summary>
    [Theory]
    [MemberData(nameof(RealVersions))]
    public async Task Every_real_migration_applies_on_top_of_its_predecessor(int version)
    {
        await using var db = await (await TestCluster.Instance).CreateDatabaseAsync();
        var migrator = new Migrator(db.AsMigrator(), MigrationCatalog.Embedded);
        if (version > 0) await migrator.MigrateAsync(version - 1);
        var seed = Path.Combine(AppContext.BaseDirectory, "Seeds", $"{version:D4}.sql");
        if (File.Exists(seed))
        {
            await using var conn = await db.AsMigrator().OpenConnectionAsync();
            await conn.ExecuteAsync(await File.ReadAllTextAsync(seed));
        }
        Assert.Equal([version], (await migrator.MigrateAsync(version)).Applied);
        Assert.Empty(await InvariantRunner.RunAsync(db.AsSuperuser(), InvariantRunner.RealDirectory));
    }

    [Fact]
    public async Task Seeded_rows_survive_an_expand_migration_and_invariants_hold()
    {
        await using var db = await (await TestCluster.Instance).CreateDatabaseAsync();
        var catalog = Catalogs.RealPlus(
            ("0001_probe.sql", "create table probe (id int primary key, label text not null);"),
            ("0002_probe_created_at.sql", "alter table probe add column created_at timestamptz not null default now();"));
        var migrator = new Migrator(db.AsMigrator(), catalog);
        await migrator.MigrateAsync(1);
        await using (var conn = await db.AsApp().OpenConnectionAsync())
            await conn.ExecuteAsync("insert into probe (id, label) select g, 'row ' || g from generate_series(1, 100) g");
        await migrator.MigrateAsync();
        await using (var conn = await db.AsApp().OpenConnectionAsync())
        {
            Assert.Equal(100, await conn.ExecuteScalarAsync<int>("select count(*) from probe where created_at is not null"));
        }
        Assert.Empty(await InvariantRunner.RunAsync(db.AsSuperuser(), InvariantRunner.RealDirectory, FixtureInvariants));
    }

    [Fact]
    public async Task The_runner_reports_violations()
    {
        await using var db = await (await TestCluster.Instance).CreateDatabaseAsync();
        await new Migrator(db.AsMigrator(), Catalogs.RealPlus(("0001_probe.sql", "create table probe (id int primary key, label text not null);"))).MigrateAsync();
        await using (var conn = await db.AsSuperuser().OpenConnectionAsync())
        {
            await conn.ExecuteAsync("insert into probe values (1, '')");
            await conn.ExecuteAsync("delete from schema_migrations where version = 0");
        }
        var failures = await InvariantRunner.RunAsync(db.AsSuperuser(), InvariantRunner.RealDirectory, FixtureInvariants);
        Assert.Equal(2, failures.Count);
        Assert.Contains(failures, f => f.StartsWith("0000_schema_migrations_contiguous.sql", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.StartsWith("probe_label_not_empty.sql", StringComparison.Ordinal));
    }
}
