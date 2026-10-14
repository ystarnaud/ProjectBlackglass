using Dapper;
using Blackglass.Accounts.IntegrationTests.Support;

namespace Blackglass.Accounts.IntegrationTests.Database;

[Trait("Requires", "Postgres")]
public class TestClusterTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task Cluster_is_postgresql_17()
    {
        await using var conn = await fixture.Db.AsSuperuser().OpenConnectionAsync();
        var version = await conn.ExecuteScalarAsync<int>("select current_setting('server_version_num')::int");
        Assert.InRange(version, 170000, 179999);
    }

    [Fact]
    public async Task Roles_have_no_elevated_attributes()
    {
        await using var conn = await fixture.Db.AsSuperuser().OpenConnectionAsync();
        var rows = (await conn.QueryAsync<(string Name, bool Super, bool CreateRole, bool CreateDb)>(
            "select rolname, rolsuper, rolcreaterole, rolcreatedb from pg_roles where rolname in ('accounts_migrator','accounts_app') order by rolname")).ToList();
        Assert.Equal(["accounts_app", "accounts_migrator"], rows.Select(r => r.Name));
        Assert.All(rows, r => Assert.False(r.Super || r.CreateRole || r.CreateDb));
    }

    [Fact]
    public async Task Test_database_is_owned_by_the_migrator()
    {
        await using var conn = await fixture.Db.AsSuperuser().OpenConnectionAsync();
        var owner = await conn.ExecuteScalarAsync<string>(
            "select pg_get_userbyid(datdba) from pg_database where datname = current_database()");
        Assert.Equal("accounts_migrator", owner);
    }
}
