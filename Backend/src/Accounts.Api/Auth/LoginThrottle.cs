using System.Threading.RateLimiting;
using Blackglass.Accounts.Domain.Auth;

namespace Blackglass.Accounts.Api.Auth;

/// <summary>
/// Login limits (spec §19, provisional). Behind the SSH tunnel and docker-proxy every request reaches the container from
/// one bridge address, so a per-IP limit would be one global limit by accident: logins are limited per presented key
/// (its hash prefix), then by a global window that every well-formed key, real or fabricated, draws from. Repeating one
/// key is stopped by its own window without draining the global one; inventing keys drains the global one. Per-IP
/// limits wait for Phase 14's HAProxy (forwarded addresses).
/// </summary>
public sealed class LoginThrottle(AuthSettings settings) : IDisposable
{
    readonly PartitionedRateLimiter<string> perKey = PartitionedRateLimiter.Create<string, string>(key =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        { PermitLimit = settings.LoginPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

    readonly FixedWindowRateLimiter global = new(new FixedWindowRateLimiterOptions
    { PermitLimit = settings.LoginGlobalPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });

    public bool TryAcquire(byte[] keyHash)
    {
        using var keyLease = perKey.AttemptAcquire(OpaqueSecret.HashPrefix(keyHash));
        if (!keyLease.IsAcquired) return false;
        using var globalLease = global.AttemptAcquire();
        return globalLease.IsAcquired;
    }

    public void Dispose()
    {
        perKey.Dispose();
        global.Dispose();
    }
}
