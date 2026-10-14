using Npgsql;
using Blackglass.Accounts.Persistence;
using Blackglass.Accounts.Persistence.Migrations;

namespace Blackglass.Accounts.Api;

public sealed record ServeStart(int ExitCode, WebApplication? App);

public static class ServeCommand
{
    public static async Task<ServeStart> StartAsync(string[] args, IReadOnlyDictionary<string, string?> env, TextWriter error, CancellationToken ct)
    {
        DbSettings settings;
        try { settings = DbSettings.FromEnvironment(env); }
        catch (DbSettingsException e) { error.WriteLine($"Configuration error: {e.Message}"); return new(ExitCodes.Failure, null); }

        var dataSource = settings.CreateDataSource("blackglass-api");
        var catalog = MigrationCatalog.Embedded;
        SchemaStatus status;
        try { status = await SchemaGuard.CheckAsync(dataSource, catalog, ct); }
        catch (NpgsqlException e)
        {
            await dataSource.DisposeAsync();
            error.WriteLine($"Database unavailable at startup ({settings}): {Describe(e)}");
            return new(ExitCodes.Failure, null);
        }

        // Behind, drifted and ahead all refuse (ruling 4): no implicit compatibility with a newer schema.
        if (status.State != SchemaState.Current)
        {
            await dataSource.DisposeAsync();
            error.WriteLine(status.State == SchemaState.Behind
                ? $"Refusing to start: schema Behind: {status.Detail} Run the migrate command after a backup."
                : $"Refusing to start: schema {status.State}: {status.Detail}");
            return new(ExitCodes.SchemaNotCurrent, null);
        }

        var builder = WebApplication.CreateBuilder(args);
        if (ApiHost.ResolveUrls(env) is { } urls) builder.WebHost.UseUrls(urls);
        builder.Services.AddSingleton(dataSource);
        var app = builder.Build();
        app.Lifetime.ApplicationStopped.Register(dataSource.Dispose);
        app.MapHealth(catalog.LatestVersion);
        await app.StartAsync(ct);
        return new(ExitCodes.Ok, app);
    }

    internal static string Describe(NpgsqlException e) => e is PostgresException pg ? $"{pg.SqlState} {pg.MessageText}" : e.Message;
}
