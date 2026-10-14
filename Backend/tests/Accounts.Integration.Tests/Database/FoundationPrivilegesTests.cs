using Dapper;
using Npgsql;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Migrations;

namespace Blackglass.Accounts.IntegrationTests.Database;

[Trait("Requires", "Postgres")]
public class FoundationPrivilegesTests : IAsyncLifetime
{
    TestDatabase db = null!;

    public async Task InitializeAsync()
    {
        db = await (await TestCluster.Instance).CreateDatabaseAsync();
        // The real foundation, then a table the migrator creates afterwards (as a later migration would).
        await new Migrator(db.AsMigrator(), Catalogs.RealPlus(("0001_probe.sql", "create table probe (id int primary key, label text not null);"))).MigrateAsync();
    }

    public async Task DisposeAsync() => await db.DisposeAsync();

    static async Task<string> SqlStateOf(NpgsqlDataSource ds, string sql)
    {
        await using var conn = await ds.OpenConnectionAsync();
        var e = await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(sql));
        return e.SqlState;
    }

    [Fact]
    public async Task App_reads_schema_migrations()
    {
        await using var conn = await db.AsApp().OpenConnectionAsync();
        Assert.Equal(2, await conn.ExecuteScalarAsync<int>("select count(*) from schema_migrations"));
    }

    [Theory]
    [InlineData("create table x (id int)")]
    [InlineData("insert into schema_migrations (version, name, checksum) values (99, 'x', decode(repeat('00', 32), 'hex'))")]
    [InlineData("update schema_migrations set name = 'x'")]
    [InlineData("delete from schema_migrations")]
    [InlineData("truncate probe")]
    [InlineData("drop table probe")]
    [InlineData("create temp table x (id int)")]
    public async Task App_cannot_change_structure_or_history(string sql) =>
        Assert.Equal("42501", await SqlStateOf(db.AsApp(), sql)); // insufficient_privilege (drop: must be owner, also 42501)

    [Fact]
    public async Task App_gets_dml_on_tables_the_migrator_creates_later()
    {
        await using var conn = await db.AsApp().OpenConnectionAsync();
        await conn.ExecuteAsync("insert into probe (id, label) values (1, 'a')");
        await conn.ExecuteAsync("update probe set label = 'b' where id = 1");
        Assert.Equal("b", await conn.ExecuteScalarAsync<string>("select label from probe where id = 1"));
        await conn.ExecuteAsync("delete from probe where id = 1");
    }

    [Fact]
    public async Task Public_cannot_connect()
    {
        var e = await Assert.ThrowsAsync<PostgresException>(async () => { await using var c = await db.AsOutsider().OpenConnectionAsync(); });
        Assert.Equal("42501", e.SqlState);
    }

    [Fact]
    public async Task Migrator_cannot_create_roles_or_databases()
    {
        Assert.Equal("42501", await SqlStateOf(db.AsMigrator(), "create role bg_should_not_exist"));
        Assert.Equal("42501", await SqlStateOf(db.AsMigrator(), "create database bg_should_not_exist"));
    }
}
