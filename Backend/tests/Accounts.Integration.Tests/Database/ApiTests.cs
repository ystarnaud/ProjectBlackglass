using System.Net;
using Dapper;
using Blackglass.Accounts.Api;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Migrations;

namespace Blackglass.Accounts.IntegrationTests.Database;

[Trait("Requires", "Postgres")]
public class ApiTests
{
    static Dictionary<string, string?> ServeEnv(TestDatabase db)
    {
        var env = db.EnvFor("app");
        env["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        return env;
    }

    static async Task<TestDatabase> MigratedAsync()
    {
        var db = await (await TestCluster.Instance).CreateDatabaseAsync();
        await new Migrator(db.AsMigrator(), MigrationCatalog.Embedded).MigrateAsync();
        return db;
    }

    [Fact]
    public async Task Health_is_ok_with_the_schema_version()
    {
        await using var db = await MigratedAsync();
        var start = await ServeCommand.StartAsync(ServeEnv(db), TextWriter.Null, default);
        await using var app = start.App!;
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var response = await http.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"{{\"status\":\"ok\",\"schemaVersion\":{MigrationCatalog.Embedded.LatestVersion}}}", await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/anything-else")).StatusCode);
        await app.StopAsync();
    }

    [Fact]
    public async Task Health_is_503_while_the_database_refuses_connections_then_recovers()
    {
        await using var db = await MigratedAsync();
        var start = await ServeCommand.StartAsync(ServeEnv(db), TextWriter.Null, default);
        await using var app = start.App!;
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var cluster = await TestCluster.Instance;
        // Connect to the maintenance database: a database cannot disallow connections while the session is inside it.
        await using (var admin = cluster.DataSource("postgres", "postgres", cluster.SuperuserPassword))
        await using (var su = await admin.OpenConnectionAsync())
        {
            await su.ExecuteAsync($"alter database \"{db.Name}\" allow_connections false");
            await su.ExecuteAsync($"select pg_terminate_backend(pid) from pg_stat_activity where datname = '{db.Name}' and usename = 'accounts_app'");
        }
        var down = await http.GetAsync("/health");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, down.StatusCode);
        var body = await down.Content.ReadAsStringAsync();
        Assert.Equal("{\"status\":\"unavailable\",\"schemaVersion\":null}", body);
        Assert.DoesNotContain(db.Password("app"), body);

        await using (var admin = cluster.DataSource("postgres", "postgres", cluster.SuperuserPassword))
        await using (var conn = await admin.OpenConnectionAsync())
            await conn.ExecuteAsync($"alter database \"{db.Name}\" allow_connections true");
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health")).StatusCode);
        await app.StopAsync();
    }

    [Fact]
    public async Task Serve_refuses_a_database_that_is_behind()
    {
        await using var db = await (await TestCluster.Instance).CreateDatabaseAsync();
        var err = new StringWriter();
        var start = await ServeCommand.StartAsync(ServeEnv(db), err, default);
        Assert.Equal(ExitCodes.SchemaNotCurrent, start.ExitCode);
        Assert.Null(start.App);
        Assert.Contains("Refusing to start: schema Behind", err.ToString());
    }

    [Fact]
    public async Task Serve_refuses_a_drifted_database()
    {
        await using var db = await MigratedAsync();
        await using (var su = await db.AsSuperuser().OpenConnectionAsync())
            await su.ExecuteAsync("update schema_migrations set checksum = decode(repeat('00', 32), 'hex') where version = 0");
        var err = new StringWriter();
        var start = await ServeCommand.StartAsync(ServeEnv(db), err, default);
        Assert.Equal(ExitCodes.SchemaNotCurrent, start.ExitCode);
        Assert.Contains("Drift", err.ToString());
    }

    [Fact]
    public async Task Serve_refuses_a_database_that_is_ahead()
    {
        await using var db = await (await TestCluster.Instance).CreateDatabaseAsync();
        await new Migrator(db.AsMigrator(), Catalogs.RealPlus(($"{MigrationCatalog.Embedded.LatestVersion + 1:D4}_future.sql", "create table future (id int);"))).MigrateAsync();
        var err = new StringWriter();
        var start = await ServeCommand.StartAsync(ServeEnv(db), err, default);
        Assert.Equal(ExitCodes.SchemaNotCurrent, start.ExitCode);
        Assert.Null(start.App);
        Assert.Contains("Refusing to start: schema Ahead", err.ToString());
    }

    [Fact]
    public async Task Serve_exits_1_when_the_database_is_unreachable_and_prints_no_password()
    {
        await using var db = await (await TestCluster.Instance).CreateDatabaseAsync();
        var env = ServeEnv(db);
        env["BLACKGLASS_DB_PORT"] = "1";
        var err = new StringWriter();
        Assert.Equal(ExitCodes.Failure, (await ServeCommand.StartAsync(env, err, default)).ExitCode);
        Assert.DoesNotContain(db.Password("app"), err.ToString());
    }

    [Fact]
    public async Task Wrong_password_fails_without_echoing_any_password()
    {
        await using var db = await MigratedAsync();
        var env = ServeEnv(db);
        File.WriteAllText(env["BLACKGLASS_DB_PASSWORD_FILE"]!, "wrong-password-XYZ");
        var err = new StringWriter();
        Assert.Equal(ExitCodes.Failure, (await ServeCommand.StartAsync(env, err, default)).ExitCode);
        Assert.DoesNotContain("wrong-password-XYZ", err.ToString());
        Assert.DoesNotContain(db.Password("app"), err.ToString());
        Assert.Contains("28P01", err.ToString()); // invalid_password: the operator can tell what failed
    }

    [Fact]
    public async Task Migrate_command_applies_then_reports_nothing_to_do()
    {
        await using var db = await (await TestCluster.Instance).CreateDatabaseAsync();
        var out1 = new StringWriter();
        Assert.Equal(ExitCodes.Ok, await Cli.RunAsync(["migrate"], db.EnvFor("migrator"), out1, TextWriter.Null));
        Assert.Contains("Applied 0000", out1.ToString());
        var out2 = new StringWriter();
        Assert.Equal(ExitCodes.Ok, await Cli.RunAsync(["migrate"], db.EnvFor("migrator"), out2, TextWriter.Null));
        Assert.Contains("nothing applied", out2.ToString());
        Assert.DoesNotContain(db.Password("migrator"), out1.ToString() + out2.ToString());
    }

    [Fact]
    public async Task Migrate_command_exits_2_on_drift()
    {
        await using var db = await MigratedAsync();
        await using (var su = await db.AsSuperuser().OpenConnectionAsync())
            await su.ExecuteAsync("update schema_migrations set name = 'tampered' where version = 0");
        var err = new StringWriter();
        Assert.Equal(ExitCodes.SchemaMismatch, await Cli.RunAsync(["migrate"], db.EnvFor("migrator"), TextWriter.Null, err));
        Assert.Contains("Refusing to migrate", err.ToString());
    }
}
