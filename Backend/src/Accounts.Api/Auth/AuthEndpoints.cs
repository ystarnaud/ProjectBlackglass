using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Blackglass.Accounts.Domain.Accounts;
using Blackglass.Accounts.Domain.Auth;
using Blackglass.Accounts.Persistence;
using Blackglass.Accounts.Persistence.Accounts;
using Blackglass.Accounts.Persistence.Player;

namespace Blackglass.Accounts.Api.Auth;

public static class AuthEndpoints
{
    public sealed record LoginRequest(string? AccountKey);

    public sealed record LoginResponse(string AccessToken, DateTimeOffset ExpiresAt,
        [property: JsonConverter(typeof(AccountIdJsonConverter))] long AccountId);

    /// <param name="Energy">The projected energy balance; null when the account has no wallet yet (owner ruling E3), never a zero balance.</param>
    /// <param name="ActiveAttemptId">The attempt of the account's active participation (Stage 7), or null.</param>
    /// <param name="Insurance">The account-level coverage at the read's database now (Stage 7).</param>
    public sealed record MeResponse([property: JsonConverter(typeof(AccountIdJsonConverter))] long AccountId, long? InventoryVersion, EnergyDto? Energy,
        Guid? ActiveAttemptId, InsuranceDto Insurance);

    /// <summary>The account-level coverage (Stage 7): whether it is in force at the read's database now (starts_at &lt;= now
    /// &lt; expires_at), and the policy and expiry of the coverage row (an expired one included; UTC, ISO-8601 with 'Z'). With
    /// no coverage at all: <c>{active: false, policyId: null, expiresAt: null}</c>.</summary>
    public sealed record InsuranceDto(bool Active, string? PolicyId, DateTime? ExpiresAt);

    /// <summary>A wallet projected to the database clock (Stage 6): the balance (which may exceed the cap, E6), the cap of the
    /// policy it is measured under, and when the next unit lands (UTC, ISO-8601 with 'Z'), or null at or above the cap.</summary>
    public sealed record EnergyDto(int Balance, int Cap, DateTime? NextRegenAt);

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

        app.MapGet("/v1/me", async (HttpContext http, PlayerReadStore players) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var account = AccountClaims.AccountOf(http.User);
            var me = await players.GetMeAsync(account, http.RequestAborted);
            var energy = me.Energy is { } e ? new EnergyDto(e.Balance, e.Cap, e.NextRegenAt) : null;
            return Results.Ok(new MeResponse(account, me.InventoryVersion, energy, me.ActiveAttemptId,
                new InsuranceDto(me.Insurance.Active, me.Insurance.PolicyId, me.Insurance.ExpiresAt)));
        }).RequireAuthorization().RequireRateLimiting(RateLimits.PerAccount);
    }

    /// <summary>
    /// A database failure during authentication or a handler, by kind (DbFailure): unavailable → 503; busy (a lock or
    /// statement timeout, nothing committed) → 503 with Retry-After; anything else → 500, never presented as an outage.
    /// Never a 401 that would tell a client its credential is bad, never unauthenticated access; an empty body, and a log
    /// line with the SQLSTATE and constraint only (a server message can quote values). Known conflicts (a taken handle,
    /// input the database refuses) are AccountAdminExceptions answered by their routes, not failures.
    /// </summary>
    public static void UseDatabaseFailureResponses(this WebApplication app) =>
        app.Use(async (context, next) =>
        {
            try { await next(context); }
            catch (NpgsqlException e) when (!context.Response.HasStarted)
            {
                var kind = DbFailure.Classify(e)!.Value;
                var log = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Blackglass.Database");
                if (kind == DbFailureKind.Unexpected)
                    log.LogError("Unexpected database error for {Path}: {Failure}", context.Request.Path, DbFailure.SafeDescription(e));
                else
                    log.LogWarning("Database {Kind} for {Path}: {Failure}", kind == DbFailureKind.Busy ? "busy" : "unavailable", context.Request.Path, DbFailure.SafeDescription(e));
                context.Response.Clear();
                context.Response.StatusCode = kind == DbFailureKind.Unexpected ? StatusCodes.Status500InternalServerError : StatusCodes.Status503ServiceUnavailable;
                if (kind == DbFailureKind.Busy) context.Response.Headers.RetryAfter = "1";
            }
        });
}
