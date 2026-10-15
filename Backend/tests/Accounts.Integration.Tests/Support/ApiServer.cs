using Dapper;
using Microsoft.AspNetCore.Builder;
using Blackglass.Accounts.Api;

namespace Blackglass.Accounts.IntegrationTests.Support;

/// <summary>The real API on an ephemeral loopback port against a test database, with extra settings.</summary>
public sealed class ApiServer : IAsyncDisposable
{
    public required WebApplication App { get; init; }
    public required HttpClient Http { get; init; }

    public static async Task<ApiServer> StartAsync(TestDatabase db, Dictionary<string, string?>? settings = null, Action<WebApplicationBuilder>? configure = null)
    {
        var env = db.EnvFor("app");
        env["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        foreach (var (k, v) in settings ?? new()) env[k] = v;
        var error = new StringWriter();
        var start = await ServeCommand.StartAsync(env, error, default, configure: configure);
        if (start.App is null) throw new InvalidOperationException($"serve failed ({start.ExitCode}): {error}");
        return new ApiServer { App = start.App, Http = new HttpClient { BaseAddress = new Uri(start.App.Urls.Single()) } };
    }

    /// <summary>Makes the database refuse connections and drops the app's open ones (an outage), or lifts that.</summary>
    public static async Task SetDatabaseReachableAsync(TestDatabase db, bool reachable)
    {
        var cluster = await TestCluster.Instance;
        // From the maintenance database: a database can't disallow connections while the session is inside it.
        await using var admin = cluster.DataSource("postgres", "postgres", cluster.SuperuserPassword);
        await using var su = await admin.OpenConnectionAsync();
        await su.ExecuteAsync($"alter database \"{db.Name}\" allow_connections {(reachable ? "true" : "false")}");
        if (!reachable)
            await su.ExecuteAsync("select pg_terminate_backend(pid) from pg_stat_activity where datname = @name and usename = 'accounts_app'", new { name = db.Name });
    }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
    }
}
