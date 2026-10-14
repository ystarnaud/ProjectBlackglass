using Dapper;
using Npgsql;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Migrations;

namespace Blackglass.Accounts.IntegrationTests.Database;

[Trait("Requires", "Postgres")]
public class MigratorTests
{
    static readonly (string, string)[] Three =
    [
        ("0000_t0.sql", "create table t0 (id int primary key);"),
        ("0001_t1.sql", "create table t1 (id int primary key);"),
        ("0002_t1_note.sql", "alter table t1 add column note text not null default '';"),
    ];

    static async Task<List<(int Version, string Name, byte[] Checksum)>> Rows(TestDatabase db)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        return (await conn.QueryAsync<(int, string, byte[])>("select version, name, checksum from schema_migrations order by version")).ToList();
    }

    static async Task<bool> TableExists(TestDatabase db, string table)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<bool>("select to_regclass(@t) is not null", new { t = "public." + table });
    }

    [Fact]
    public async Task Applies_every_script_to_an_empty_database_in_order()
    {
        await using var db = await (await TestCluster.Instance).CreateDatabaseAsync();
        var catalog = Catalogs.Of(Three);
        var result = await new Migrator(db.AsMigrator(), catalog).MigrateAsync();
        Assert.Equal([0, 1, 2], result.Applied);
        Assert.Equal(2, result.SchemaVersion);
        var rows = await Rows(db);
        Assert.Equal(catalog.Scripts.Select(s => (s.Version, s.Name)), rows.Select(r => (r.Version, r.Name)));
        Assert.All(rows, r => Assert.Equal(catalog.Scripts[r.Version].Checksum, r.Checksum));
        Assert.True(await TableExists(db, "t1"));
    }

    [Fact]
    public async Task A_second_run_is_a_no_op()
    {
        await using var db = await (await TestCluster.Instance).CreateDatabaseAsync();
        var migrator = new Migrator(db.AsMigrator(), Catalogs.Of(Three));
        await migrator.MigrateAsync();
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        var before = await conn.QueryAsync<DateTime>("select applied_at from schema_migrations order by version");
        var again = await migrator.MigrateAsync();
        Assert.Empty(again.Applied);
        Assert.Equal(2, again.SchemaVersion);
        Assert.Equal(before, await conn.QueryAsync<DateTime>("select applied_at from schema_migrations order by version"));
    }

    [Fact]
    public async Task Target_version_stops_early_and_the_rest_applies_later()
    {
        await using var db = await (await TestCluster.Instance).CreateDatabaseAsync();
        var migrator = new Migrator(db.AsMigrator(), Catalogs.Of(Three));
        Assert.Equal([0, 1], (await migrator.MigrateAsync(targetVersion: 1)).Applied);
        Assert.Equal([2], (await migrator.MigrateAsync()).Applied);
    }

    [Fact]
    public async Task A_failing_script_rolls_back_alone_and_is_named()
    {
        await using var db = await (await TestCluster.Instance).CreateDatabaseAsync();
        var catalog = Catalogs.Of(("0000_t0.sql", "create table t0 (id int);"),
                                  ("0001_broken.sql", "create table t1 (id int);\nselect * from no_such_table;"));
        var e = await Assert.ThrowsAsync<MigrationFailedException>(() => new Migrator(db.AsMigrator(), catalog).MigrateAsync());
        Assert.Equal("0001_broken.sql", e.FileName);
        Assert.Equal([0], (await Rows(db)).Select(r => r.Version));
        Assert.False(await TableExists(db, "t1"));
    }

    [Fact]
    public async Task A_changed_applied_script_is_refused_and_nothing_else_runs()
    {
        await using var db = await (await TestCluster.Instance).CreateDatabaseAsync();
        await new Migrator(db.AsMigrator(), Catalogs.Of(Three[0], Three[1])).MigrateAsync();
        var changed = Catalogs.Of(Three[0], ("0001_t1.sql", "create table t1 (id bigint primary key);"), Three[2]);
        var e = await Assert.ThrowsAsync<SchemaMismatchException>(() => new Migrator(db.AsMigrator(), changed).MigrateAsync());
        Assert.Equal(SchemaState.Drift, e.Status.State);
        Assert.Equal([0, 1], (await Rows(db)).Select(r => r.Version));
    }

    [Fact]
    public async Task A_renamed_applied_script_is_refused()
    {
        await using var db = await (await TestCluster.Instance).CreateDatabaseAsync();
        await new Migrator(db.AsMigrator(), Catalogs.Of(Three[0])).MigrateAsync();
        var renamed = Catalogs.Of(("0000_renamed.sql", Three[0].Item2));
        Assert.Equal(SchemaState.Drift, (await Assert.ThrowsAsync<SchemaMismatchException>(() => new Migrator(db.AsMigrator(), renamed).MigrateAsync())).Status.State);
    }

    [Fact]
    public async Task A_database_ahead_of_the_build_is_refused()
    {
        await using var db = await (await TestCluster.Instance).CreateDatabaseAsync();
        await new Migrator(db.AsMigrator(), Catalogs.Of(Three)).MigrateAsync();
        var older = Catalogs.Of(Three[0], Three[1]);
        Assert.Equal(SchemaState.Ahead, (await Assert.ThrowsAsync<SchemaMismatchException>(() => new Migrator(db.AsMigrator(), older).MigrateAsync())).Status.State);
    }

    [Fact]
    public async Task A_crlf_copy_of_an_applied_script_is_not_drift()
    {
        await using var db = await (await TestCluster.Instance).CreateDatabaseAsync();
        await new Migrator(db.AsMigrator(), Catalogs.Of(("0000_t0.sql", "create table t0 (id int);\nselect 1;\n"))).MigrateAsync();
        var crlf = Catalogs.Of(("0000_t0.sql", "create table t0 (id int);\r\nselect 1;\r\n"));
        Assert.Empty((await new Migrator(db.AsMigrator(), crlf).MigrateAsync()).Applied);
    }

    [Fact]
    public async Task Concurrent_migrators_apply_each_script_exactly_once()
    {
        await using var db = await (await TestCluster.Instance).CreateDatabaseAsync();
        var catalog = Catalogs.Of(Three[0], ("0001_t1.sql", "select pg_sleep(1);\ncreate table t1 (id int primary key);"), Three[2]);
        var cluster = await TestCluster.Instance;
        await using var ds1 = cluster.DataSource(db.Name, "accounts_migrator", db.Password("migrator"));
        await using var ds2 = cluster.DataSource(db.Name, "accounts_migrator", db.Password("migrator"));
        var results = await Task.WhenAll(new Migrator(ds1, catalog).MigrateAsync(), new Migrator(ds2, catalog).MigrateAsync());
        Assert.Equal([0, 1, 2], results.SelectMany(r => r.Applied).Order());
        Assert.Equal([0, 1, 2], (await Rows(db)).Select(r => r.Version));
    }

    [Fact]
    public async Task A_busy_migrator_times_out_with_a_clear_error()
    {
        await using var db = await (await TestCluster.Instance).CreateDatabaseAsync();
        await using var holder = await db.AsMigrator().OpenConnectionAsync();
        await holder.ExecuteAsync("select pg_advisory_lock(@k)", new { k = Migrator.AdvisoryLockKey });
        var options = new MigratorOptions { LockWait = TimeSpan.FromSeconds(1) };
        await Assert.ThrowsAsync<MigratorBusyException>(() => new Migrator(db.AsMigrator(), Catalogs.Of(Three), options).MigrateAsync());
        Assert.False(await TableExists(db, "schema_migrations") && (await Rows(db)).Count > 0);
    }

    [Fact]
    public async Task Lock_timeout_applies_to_each_script()
    {
        await using var db = await (await TestCluster.Instance).CreateDatabaseAsync();
        await new Migrator(db.AsMigrator(), Catalogs.Of(Three[0])).MigrateAsync();
        await using var blocker = await db.AsSuperuser().OpenConnectionAsync();
        await using var tx = await blocker.BeginTransactionAsync();
        await blocker.ExecuteAsync("lock table t0 in access exclusive mode", transaction: tx);
        var catalog = Catalogs.Of(Three[0], ("0001_alter.sql", "alter table t0 add column x int;"));
        var options = new MigratorOptions { LockTimeout = TimeSpan.FromMilliseconds(500) };
        var e = await Assert.ThrowsAsync<MigrationFailedException>(() => new Migrator(db.AsMigrator(), catalog, options).MigrateAsync());
        Assert.Equal("55P03", ((PostgresException)e.InnerException!).SqlState);
    }
}
