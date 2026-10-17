using Dapper;
using Npgsql;

namespace Blackglass.Accounts.Persistence.Attempts;

/// <summary>
/// The fail-closed, database-backed service-health check (owner rulings S7-D2, B5). Healthy only when some mission host can
/// take the requested template now: enabled, not drained, a heartbeat newer than the lease (by the database clock), and
/// fewer live slots than its capacity. A live slot is an attempt assigned to the host whose slot release is not confirmed,
/// whatever the attempt's state (B6: a cancelled attempt's unit may still hold its port). An <c>agent</c> host must also report
/// a build that is enabled, allowlisted for the template version and built with exactly the requested catalogue version (B4).
/// <c>dev_fixture</c> hosts count only when the process runs with development host fixtures enabled (BLACKGLASS_DEV_HOSTS,
/// never in production); they never claim, so no build is checked. With no such host the answer is false, and the caller
/// refuses (503) before it takes any lock or creates anything.
/// </summary>
/// <remarks>
/// One statement that takes no row lock and writes nothing, so admission can run it with no transaction, before its first
/// lock (S7-D2). Nothing here catches: a failed query or a broken connection propagates, so the caller answers 500 or 503,
/// never "healthy". No retry. A template version published between this check and admission is a documented, harmless race:
/// the attempt waits and its assignment deadline releases it.
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
    public Task<bool> CheckAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string templateId, int templateVersion, string catalogueVersion, CancellationToken ct) =>
        CheckAsync(conn, tx, DevHostsEnabled, LeaseSeconds, templateId, templateVersion, catalogueVersion, ct);

    /// <summary>Whether some host can run <paramref name="templateId"/> v<paramref name="templateVersion"/> under
    /// <paramref name="catalogueVersion"/> now. <paramref name="tx"/> may be null (the usual case: before any lock).</summary>
    public static async Task<bool> CheckAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, bool devHostsEnabled, int leaseSeconds, string templateId, int templateVersion,
        string catalogueVersion, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentOutOfRangeException.ThrowIfLessThan(leaseSeconds, 1);
        ArgumentException.ThrowIfNullOrEmpty(templateId);
        ArgumentException.ThrowIfNullOrEmpty(catalogueVersion);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            select exists (
              select 1 from mission_hosts h
              where h.enabled and not h.drained
                and h.last_heartbeat_at > clock_timestamp() - make_interval(secs => @lease)
                and h.capacity > (select count(*) from mission_attempts a where a.host_id = h.host_id and a.slot_released_at is null)
                and ((h.kind = 'agent'
                      and exists (select 1 from allowed_builds b
                                  join allowed_build_templates t on t.build_revision = b.build_revision and t.catalogue_version = b.catalogue_version
                                  where b.enabled and b.build_revision = h.build_revision and b.catalogue_version = h.catalogue_version
                                    and b.catalogue_version = @catalogueVersion and t.template_id = @templateId and t.template_version = @templateVersion))
                     or (@dev and h.kind = 'dev_fixture')))
            """, new { lease = (double)leaseSeconds, dev = devHostsEnabled, templateId, templateVersion, catalogueVersion }, tx, cancellationToken: ct));
    }
}
