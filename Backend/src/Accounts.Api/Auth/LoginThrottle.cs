using System.Threading.RateLimiting;
using Blackglass.Accounts.Domain.Auth;

namespace Blackglass.Accounts.Api.Auth;

/// <summary>
/// Login limits (spec §19, provisional). Behind the SSH tunnel and docker-proxy every request reaches the container from
/// one bridge address, so a per-IP limit would be one global limit by accident: logins are limited per presented key
/// (its hash prefix), then by a global window that every well-formed key, real or fabricated, draws from. Repeating one
/// key is stopped by its own window without draining the global one; inventing keys drains the global one, and once it
/// is empty they create no per-key limiters (Review B: memory stays bounded however many keys are invented). Per-IP
/// limits wait for Phase 14's HAProxy (forwarded addresses).
/// </summary>
public sealed class LoginThrottle : IDisposable
{
    readonly PartitionedRateLimiter<string> perKey;
    readonly FixedWindowRateLimiter global;
    int partitionsCreated;

    public LoginThrottle(AuthSettings settings)
    {
        perKey = PartitionedRateLimiter.Create<string, string>(key =>
            RateLimitPartition.GetFixedWindowLimiter(key, _ =>
            {
                Interlocked.Increment(ref partitionsCreated);
                return new FixedWindowRateLimiterOptions { PermitLimit = settings.LoginPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 };
            }));
        global = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        { PermitLimit = settings.LoginGlobalPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
    }

    /// <summary>Per-key limiters created so far (each lives about a minute after its window refills).</summary>
    public int PartitionsCreated => Volatile.Read(ref partitionsCreated);

    public bool TryAcquire(byte[] keyHash)
    {
        // Look at the global window first without spending it: once it is empty, no new per-key partition is created,
        // so the partitions a flood of invented keys can create per minute stay bounded by the global limit.
        if (global.GetStatistics() is not { CurrentAvailablePermits: > 0 }) return false;
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
