using Microsoft.AspNetCore.Authentication;
using Npgsql;
using Blackglass.Accounts.Api.Attempts;
using Blackglass.Accounts.Api.Auth;
using Blackglass.Accounts.Api.Energy;
using Blackglass.Accounts.Api.Hosts;
using Blackglass.Accounts.Api.Player;
using Blackglass.Accounts.Domain.Attempts;
using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.Persistence;
using Blackglass.Accounts.Persistence.Accounts;
using Blackglass.Accounts.Persistence.Attempts;
using Blackglass.Accounts.Persistence.Energy;
using Blackglass.Accounts.Persistence.Hosts;
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

        EnergySettings energy;
        try { energy = EnergySettings.FromEnvironment(env); }
        catch (EnergySettingsException e) { error.WriteLine($"Configuration error: {e.Message}"); return new(ExitCodes.Failure, null); }

        AttemptSettings attempts;
        try { attempts = AttemptSettings.FromEnvironment(env); }
        catch (AttemptSettingsException e) { error.WriteLine($"Configuration error: {e.Message}"); return new(ExitCodes.Failure, null); }

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
        // The mandatory, fail-closed participation lock (S7-D4) for inventory reads, edits and picks: the only implementation.
        builder.Services.AddSingleton<IInventoryLock, ParticipationLock>();
        builder.Services.AddSingleton(sp => new PlayerReadStore(dataSource, sp.GetRequiredService<IInventoryLock>(), catalogue));
        builder.Services.AddSingleton(sp => new PickStore(dataSource, sp.GetRequiredService<IInventoryLock>(), catalogue));
        builder.Services.AddSingleton(sp => new InventoryEditStore(dataSource, sp.GetRequiredService<IInventoryLock>(), catalogue));
        builder.Services.AddSingleton<LoginThrottle>();
        // The fail-closed host-health check (S7-D2) with this process's development-host setting and lease, for admission.
        builder.Services.AddSingleton(attempts);
        builder.Services.AddSingleton(new ServiceHealth(attempts.DevHostsEnabled, attempts.HostLeaseSeconds));
        // Admission (Task 6): the seed and retention salt come from the operating system's CSPRNG; resolved when the store is
        // first used, so a test's configure can substitute known secrets.
        builder.Services.AddSingleton<IAttemptSecrets, CryptoAttemptSecrets>();
        builder.Services.AddSingleton(sp => new AdmissionStore(dataSource, sp.GetRequiredService<ServiceHealth>(), sp.GetRequiredService<IAttemptSecrets>(),
            catalogue, attempts.AssignDeadlineSeconds));
        // The attempt read, the player cancel and the assignment deadline release (Task 7). The deadline worker is always on
        // (S7-D6: it is what releases an expired attempt exactly once); its first sweep waits one interval. Resolved through its
        // own registration, so a test's configure can replace the worker (and its sweep) as a whole.
        builder.Services.AddSingleton(new AttemptStore(dataSource));
        var sweepInterval = TimeSpan.FromSeconds(attempts.AttemptSweepSeconds);
        builder.Services.AddSingleton(sp => new AssignDeadlineWorker(ct => sp.GetRequiredService<AttemptStore>().SweepAsync(AssignDeadlineWorker.BatchSize, ct),
            sweepInterval, sp.GetRequiredService<ILoggerFactory>()));
        builder.Services.AddHostedService(sp => sp.GetRequiredService<AssignDeadlineWorker>());
        if (energy.ReconcileSeconds != 0)
        {
            // The periodic reconciliation (E4): its first sweep waits one interval, so startup never waits for one. Resolved
            // through its own registration, so a test's configure can replace the service (and its sweep) as a whole.
            var interval = TimeSpan.FromSeconds(energy.ReconcileSeconds);
            builder.Services.AddSingleton(new EnergyReconciler(dataSource));
            builder.Services.AddSingleton(sp => new EnergyReconcileService(sp.GetRequiredService<EnergyReconciler>().RunAsync, interval, sp.GetRequiredService<ILoggerFactory>()));
            builder.Services.AddHostedService(sp => sp.GetRequiredService<EnergyReconcileService>());
        }
        // Stage 8 (B3): three credentials, three schemes. The default authentication forwards by the token's prefix, so the
        // per-host and per-process rate limits see their principal before authorization; every route still requires exactly
        // its own scheme (the default policy is the account's Bearer scheme alone), so no credential opens another's routes.
        builder.Services.AddSingleton(new HostStore(dataSource, attempts.HostLeaseSeconds));
        builder.Services.AddSingleton(new ProcessStore(dataSource));
        builder.Services.AddAuthentication(o => { o.DefaultScheme = CredentialSelector; o.DefaultChallengeScheme = AccountClaims.Scheme; })
            .AddPolicyScheme(CredentialSelector, null, o => o.ForwardDefaultSelector = SelectScheme)
            .AddScheme<AuthenticationSchemeOptions, BearerAuthenticationHandler>(AccountClaims.Scheme, null)
            .AddScheme<AuthenticationSchemeOptions, HostAuthenticationHandler>(MissionClaims.HostScheme, null)
            .AddScheme<AuthenticationSchemeOptions, ProcessAuthenticationHandler>(MissionClaims.ProcessScheme, null);
        builder.Services.AddAuthorization(o =>
        {
            o.DefaultPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(AccountClaims.Scheme).RequireAuthenticatedUser().Build();
            o.AddMissionPolicies();
        });
        builder.Services.AddAccountRateLimits(auth, attempts.DeployPerMinute);
        configure?.Invoke(builder);
        var app = builder.Build();
        app.Lifetime.ApplicationStopped.Register(dataSource.Dispose);
        if (attempts.DevHostsEnabled)
            app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(AttemptSettings.LogCategory)
                .LogWarning("{Setting}=enabled: development host fixtures count as capacity: never in production.", AttemptSettings.DevHostsVar);
        app.UseDatabaseFailureResponses();
        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseAuthorization();
        app.MapHealth(catalog.LatestVersion);
        app.MapAuth(auth);
        app.MapAdmin(auth);
        app.MapPlayer(auth);
        app.MapAttempts(attempts);
        app.MapAgent(attempts);
        await app.StartAsync(ct);
        return new(ExitCodes.Ok, app);
    }

    const string CredentialSelector = "Credential";

    /// <summary>The scheme a request's credential belongs to, by its prefix alone (the scheme's handler then parses it strictly).</summary>
    static string SelectScheme(HttpContext http)
    {
        var header = http.Request.Headers.Authorization;
        if (header.Count == 1 && header[0] is { } value)
        {
            if (value.StartsWith("Bearer " + Domain.Auth.OpaqueSecret.HostSecretPrefix + "_", StringComparison.Ordinal)) return MissionClaims.HostScheme;
            if (value.StartsWith("Bearer " + Domain.Auth.OpaqueSecret.ProcessCredentialPrefix + "_", StringComparison.Ordinal)) return MissionClaims.ProcessScheme;
        }
        return AccountClaims.Scheme;
    }

    internal static string Describe(NpgsqlException e) => e is PostgresException pg ? $"{pg.SqlState} {pg.MessageText}" : e.Message;
}
