using Dapper;
using Npgsql;

namespace Blackglass.Accounts.Persistence.Attempts;

/// <summary>
/// The fail-closed, database-backed service-health check (owner ruling S7-D2). Healthy only when some mission host is enabled,
/// not drained, has a heartbeat newer than the lease (by the database clock) and has free capacity. <c>agent</c> hosts
/// (Stage 8's registration) always count; <c>dev_fixture</c> hosts count only when the process runs with development host
/// fixtures enabled (BLACKGLASS_DEV_HOSTS=enabled, never in production). With no such host the answer is false, and the
/// caller refuses (503) before it takes any lock or creates anything.
/// </summary>
/// <remarks>
/// One statement that takes no row lock and writes nothing, so admission can run it with no transaction, before its first
/// lock (S7-D2: a health failure reserves no energy, regenerates nothing, leaves the wallet untouched, creates no attempt and
/// locks no inventory). Nothing here catches: a failed query or a broken connection propagates, so the caller answers 500
/// or 503, never "healthy". No retry.
/// </remarks>
public sealed class ServiceHealth
{
    public ServiceHealth(bool devHostsEnabled, int leaseSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(leaseSeconds, 1);
        (DevHostsEnabled, LeaseSeconds) = (devHostsEnabled, leaseSeconds);
    }

    public bool DevHostsEnabled { get; }
    public int LeaseSeconds { get; }

    /// <summary>This process's check (its development-host setting and lease); see the static overload.</summary>
    public Task<bool> CheckAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct) =>
        CheckAsync(conn, tx, DevHostsEnabled, LeaseSeconds, ct);

    /// <summary>
    /// Whether some host can take an attempt now. <paramref name="tx"/> may be null (the usual case: before any lock).
    /// <para>Capacity: a host has room when <c>capacity</c> exceeds the live attempts assigned to it. Stage 7 assigns no attempt
    /// to a host (<c>mission_attempts</c> has no host column yet), so that count is 0 and the test is <c>capacity &gt; 0</c>.
    /// Stage 8, which adds the assignment, replaces the 0 in this statement with the count of the host's live attempts (a
    /// correlated subquery on its assignment column), still without a lock.</para>
    /// </summary>
    public static async Task<bool> CheckAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, bool devHostsEnabled, int leaseSeconds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentOutOfRangeException.ThrowIfLessThan(leaseSeconds, 1);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            select exists (
              select 1 from mission_hosts h
              where h.enabled and not h.drained
                and h.last_heartbeat_at > clock_timestamp() - make_interval(secs => @lease)
                and (h.kind = 'agent' or (@dev and h.kind = 'dev_fixture'))
                and h.capacity > 0)
            """, new { lease = (double)leaseSeconds, dev = devHostsEnabled }, tx, cancellationToken: ct));
    }
}
