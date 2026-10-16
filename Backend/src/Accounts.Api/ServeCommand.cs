using Microsoft.AspNetCore.Authentication;
using Npgsql;
using Blackglass.Accounts.Api.Auth;
using Blackglass.Accounts.Api.Player;
using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.Persistence;
using Blackglass.Accounts.Persistence.Accounts;
using Blackglass.Accounts.Persistence.Migrations;
using Blackglass.Accounts.Persistence.Player;

namespace Blackglass.Accounts.Api;

public sealed record ServeStart(int ExitCode, WebApplication? App);

public static class ServeCommand
{
    public static async Task<ServeStart> StartAsync(IReadOnlyDictionary<string, string?> env, TextWriter error, CancellationToken ct,
        Func<GameCatalogue>? loadCatalogue = null, Action<WebApplicationBuilder>? configure = null)
    {
        // The catalogue comes first: an image with an invalid catalogue never reads its settings or the database.
        GameCatalogue catalogue;
        try { catalogue = (loadCatalogue ?? (() => GameCatalogue.Embedded))(); }
        catch (CatalogueException e) { error.WriteLine($"Refusing to start: {e.Message}"); return new(ExitCodes.InvalidCatalogue, null); }

        DbSettings settings;
        DbTimeouts timeouts;
        try { (settings, timeouts) = (DbSettings.FromEnvironment(env), DbTimeouts.FromEnvironment(env)); }
        catch (DbSettingsException e) { error.WriteLine($"Configuration error: {e.Message}"); return new(ExitCodes.Failure, null); }

        AuthSettings auth;
        try { auth = AuthSettings.FromEnvironment(env); }
        catch (AuthSettingsException e) { error.WriteLine($"Configuration error: {e.Message}"); return new(ExitCodes.Failure, null); }

        var dataSource = settings.CreateDataSource("blackglass-api", timeouts: timeouts);
        var catalog = MigrationCatalog.Embedded;
        SchemaStatus status;
        try { status = await SchemaGuard.CheckAsync(dataSource, catalog, ct); }
        catch (NpgsqlException e)
        {
            await dataSource.DisposeAsync();
            error.WriteLine($"Database unavailable at startup ({settings}): {DbFailure.SafeDescription(e)}");
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

        var builder = WebApplication.CreateBuilder(); // no command-line arguments: see Cli
        if (ApiHost.ResolveUrls(env) is { } urls) builder.WebHost.UseUrls(urls);
        builder.Services.AddSingleton(dataSource);
        builder.Services.AddSingleton(catalogue);
        builder.Services.AddSingleton(auth);
        builder.Services.AddSingleton(new SessionStore(dataSource));
        builder.Services.AddSingleton(new AccountAdminStore(dataSource, catalogue));
        builder.Services.AddSingleton<IInventoryLock, NoParticipationsYet>(); // Stage 7 connects this to the participation records
        builder.Services.AddSingleton(sp => new PlayerReadStore(dataSource, catalogue, sp.GetRequiredService<IInventoryLock>()));
        builder.Services.AddSingleton(sp => new PickStore(dataSource, catalogue, sp.GetRequiredService<IInventoryLock>()));
        builder.Services.AddSingleton(sp => new InventoryEditStore(dataSource, catalogue, sp.GetRequiredService<IInventoryLock>()));
        builder.Services.AddSingleton<LoginThrottle>();
        builder.Services.AddAuthentication(AccountClaims.Scheme)
            .AddScheme<AuthenticationSchemeOptions, BearerAuthenticationHandler>(AccountClaims.Scheme, null);
        builder.Services.AddAuthorization();
        builder.Services.AddAccountRateLimits(auth);
        configure?.Invoke(builder);
        var app = builder.Build();
        app.Lifetime.ApplicationStopped.Register(dataSource.Dispose);
        app.UseDatabaseFailureResponses();
        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseAuthorization();
        app.MapHealth(catalog.LatestVersion);
        app.MapAuth(auth);
        app.MapAdmin(auth);
        app.MapPlayer();
        await app.StartAsync(ct);
        return new(ExitCodes.Ok, app);
    }

    internal static string Describe(NpgsqlException e) => e is PostgresException pg ? $"{pg.SqlState} {pg.MessageText}" : e.Message;
}
