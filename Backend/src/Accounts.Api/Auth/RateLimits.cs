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
        });
}
