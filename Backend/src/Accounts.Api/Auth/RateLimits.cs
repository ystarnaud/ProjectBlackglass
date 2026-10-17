using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Blackglass.Accounts.Api.Auth;

/// <summary>Authenticated requests are limited per account (spec §19, provisional); requests without an account share
/// one window. The /dev/v1 admin routes share one window, checked before the admin key. POST /v1/attempts has its own
/// per-account window (BLACKGLASS_RATE_DEPLOY_PER_MINUTE, provisional 6) instead of the general one. A refusal is 429 with
/// Retry-After.</summary>
public static class RateLimits
{
    public const string PerAccount = "per-account";
    public const string Admin = "admin";
    public const string Deploy = "deploy";
    public const string PerHost = "per-host";
    public const string PerProcess = "per-process";

    /// <summary>Agent requests per host per minute (provisional): heartbeats every 10 s, long-poll claims and reports fit far below it.</summary>
    public const int HostPerMinute = 600;

    /// <summary>Process requests per attempt per minute (provisional): heartbeats, redeems and starts for up to 16 participants.</summary>
    public const int ProcessPerMinute = 600;

    public static IServiceCollection AddAccountRateLimits(this IServiceCollection services, AuthSettings settings, int deployPerMinute) =>
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = (context, _) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = "60";
                return ValueTask.CompletedTask;
            };
            o.AddPolicy(PerAccount, http => RateLimitPartition.GetFixedWindowLimiter(
                http.User.FindFirst(AccountClaims.AccountId)?.Value ?? "anonymous",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = settings.RequestsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            o.AddPolicy(Admin, _ => RateLimitPartition.GetFixedWindowLimiter("admin",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = settings.AdminPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            o.AddPolicy(Deploy, http => RateLimitPartition.GetFixedWindowLimiter(
                "deploy:" + (http.User.FindFirst(AccountClaims.AccountId)?.Value ?? "anonymous"),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = deployPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            o.AddPolicy(PerHost, http => RateLimitPartition.GetFixedWindowLimiter(
                "host:" + (http.User.FindFirst(MissionClaims.HostId)?.Value ?? "anonymous"),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = HostPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            o.AddPolicy(PerProcess, http => RateLimitPartition.GetFixedWindowLimiter(
                "process:" + (http.User.FindFirst(MissionClaims.AttemptId)?.Value ?? "anonymous"),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = ProcessPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
}
