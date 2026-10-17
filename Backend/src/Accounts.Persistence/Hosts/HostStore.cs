using System.Data;
using Dapper;
using Npgsql;
using Blackglass.Accounts.Domain.Attempts;

namespace Blackglass.Accounts.Persistence.Hosts;

public sealed record AuthenticatedHost(string HostId);

/// <summary>A heartbeat's answer: whether the host is drained, and the ended attempts whose units it still reports.</summary>
public sealed record HeartbeatResult(bool Drained, IReadOnlyList<Guid> Stop);

/// <summary>
/// The agent side of a mission host (spec §16.2; owner rulings B3, B6; plan revision 2 R2, R7). A host authenticates with its
/// <c>hs1_</c> secret: one indexed lookup of the SHA-256 per request, no cache, so a rotation or a disable applies to the next
/// request. Lock order (Global Constraints): the host's own <c>mission_hosts</c> row FOR UPDATE, then the clock, then attempts
/// FOR UPDATE SKIP LOCKED. Nothing here locks an attempt before a host. No retry.
/// </summary>
public sealed class HostStore(NpgsqlDataSource db)
{
    /// <summary>The ended states whose units an agent must stop and whose slots a confirmation frees.</summary>
    static readonly string[] Ended = ["cancelled", "settled"];

    /// <summary>The enabled agent host whose current secret hashes to <paramref name="secretHash"/>, or null. A disabled host,
    /// a development fixture and an unknown or rotated secret are all null (the uniform 401).</summary>
    public async Task<AuthenticatedHost?> AuthenticateAsync(byte[] secretHash, CancellationToken ct)
    {
        await using var conn = await DbFailure.OpenAsync(db, ct);
        var hostId = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "select host_id from mission_hosts where secret_hash = @secretHash and kind = 'agent' and enabled", new { secretHash }, cancellationToken: ct));
        return hostId is null ? null : new AuthenticatedHost(hostId);
    }

    /// <summary>
    /// One heartbeat, one READ COMMITTED transaction: the host row FOR UPDATE (still enabled), the clock, the stamp and the
    /// reported build. Only when the report is a complete inventory (R2), the slot of each of this host's ended attempts that
    /// it no longer lists is released, once (attempts locked FOR UPDATE SKIP LOCKED: one an exit report is working on is
    /// left for the next heartbeat). The answer lists this host's ended attempts that the report still names, for the agent
    /// to stop (R4). Units of other hosts and unknown attempts are ignored. A host disabled since authentication gets null.
    /// </summary>
    public async Task<HeartbeatResult?> HeartbeatAsync(string hostId, HeartbeatRequest report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);
        var listed = report.Units.Select(u => u.AttemptId).ToArray();
        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var drained = await conn.QuerySingleOrDefaultAsync<bool?>(new CommandDefinition(
            "select drained from mission_hosts where host_id = @hostId and kind = 'agent' and enabled for update", new { hostId }, tx, cancellationToken: ct));
        if (drained is null) return null;
        await conn.ExecuteAsync(new CommandDefinition(
            "update mission_hosts set last_heartbeat_at = clock_timestamp(), build_revision = @Revision, catalogue_version = @CatalogueVersion where host_id = @hostId",
            new { hostId, report.Build.Revision, report.Build.CatalogueVersion }, tx, cancellationToken: ct));
        if (report.InventoryComplete)
            await conn.ExecuteAsync(new CommandDefinition(
                """
                update mission_attempts set slot_released_at = clock_timestamp()
                where attempt_id in (select attempt_id from mission_attempts
                                     where host_id = @hostId and slot_released_at is null and state = any(@Ended) and attempt_id <> all(@listed)
                                     for update skip locked)
                """, new { hostId, Ended, listed }, tx, cancellationToken: ct));
        var stop = (await conn.QueryAsync<Guid>(new CommandDefinition(
            "select attempt_id from mission_attempts where host_id = @hostId and state = any(@Ended) and attempt_id = any(@listed) order by attempt_id",
            new { hostId, Ended, listed }, tx, cancellationToken: ct))).ToList();
        await tx.CommitAsync(ct);
        return new HeartbeatResult(drained.Value, stop);
    }
}
