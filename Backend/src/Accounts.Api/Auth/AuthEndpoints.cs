using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Blackglass.Accounts.Domain.Accounts;
using Blackglass.Accounts.Domain.Auth;
using Blackglass.Accounts.Persistence.Accounts;

namespace Blackglass.Accounts.Api.Auth;

public static class AuthEndpoints
{
    public sealed record LoginRequest(string? AccountKey);

    public sealed record LoginResponse(string AccessToken, DateTimeOffset ExpiresAt,
        [property: JsonConverter(typeof(AccountIdJsonConverter))] long AccountId);

    public sealed record MeResponse([property: JsonConverter(typeof(AccountIdJsonConverter))] long AccountId);

    /// <summary>The largest body login or the admin routes read: a key is 47 characters, an admin request a few hundred.</summary>
    public const int MaxBodyBytes = 4096;

    public static void MapAuth(this WebApplication app, AuthSettings settings)
    {
        if (settings.DevAuthEnabled)
        {
            app.MapPost("/v1/auth/dev/login", async (LoginRequest? body, HttpContext http, SessionStore sessions, LoginThrottle throttle, ILoggerFactory logs) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                if (!OpaqueSecret.TryParse(body?.AccountKey, OpaqueSecret.AccountKeyPrefix, out var key))
                    return UniformUnauthorized.Result;
                var hash = key!.Hash();
                if (!throttle.TryAcquire(hash))
                {
                    http.Response.Headers.RetryAfter = "60";
                    return Results.StatusCode(StatusCodes.Status429TooManyRequests);
                }
                var session = await sessions.LoginAsync(hash, settings.TokenLifetimeMinutes, http.RequestAborted);
                var log = logs.CreateLogger("Blackglass.Auth");
                if (session is null)
                {
                    log.LogInformation("Login refused for key {KeyHash}", OpaqueSecret.HashPrefix(hash));
                    return UniformUnauthorized.Result;
                }
                log.LogInformation("Login for account {AccountId}, session {SessionId}", AccountId.Format(session.AccountId), session.SessionId);
                return Results.Ok(new LoginResponse(session.AccessToken.Text, session.ExpiresAt, session.AccountId));
            }).WithMetadata(new RequestSizeLimitAttribute(MaxBodyBytes));
        }

        // Logout needs a valid session. A repeat (or any unknown token) is the uniform 401 from the handler: harmless and
        // indistinguishable from a token that never existed.
        app.MapPost("/v1/auth/logout", async (HttpContext http, SessionStore sessions) =>
        {
            await sessions.RevokeAsync(AccountClaims.SessionOf(http.User), AccountClaims.AccountOf(http.User), http.RequestAborted);
            return Results.NoContent();
        }).RequireAuthorization().RequireRateLimiting(RateLimits.PerAccount);

        app.MapGet("/v1/me", (HttpContext http) => Results.Ok(new MeResponse(AccountClaims.AccountOf(http.User))))
            .RequireAuthorization().RequireRateLimiting(RateLimits.PerAccount);
    }

    /// <summary>A database outage during authentication or a handler is 503 with an empty body: never a 401 that would
    /// tell a client its credential is bad, never unauthenticated access, never error details.</summary>
    public static void UseDatabaseOutageAs503(this WebApplication app) =>
        app.Use(async (context, next) =>
        {
            try { await next(context); }
            catch (NpgsqlException e) when (!context.Response.HasStarted)
            {
                context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Blackglass.Database")
                    .LogWarning("Database unavailable for {Path}: {Kind} {SqlState}", context.Request.Path, e.GetType().Name, (e as PostgresException)?.SqlState);
                context.Response.Clear();
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            }
        });
}
