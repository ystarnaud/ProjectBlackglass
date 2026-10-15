using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Blackglass.Accounts.Api.Auth;

/// <summary>Authenticated requests are limited per account (spec §19, provisional); requests without an account share
/// one window. The /dev/v1 admin routes share one window, checked before the admin key. A refusal is 429 with
/// Retry-After.</summary>
public static class RateLimits
{
    public const string PerAccount = "per-account";
    public const string Admin = "admin";

    public static IServiceCollection AddAccountRateLimits(this IServiceCollection services, AuthSettings settings) =>
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
        });
}
