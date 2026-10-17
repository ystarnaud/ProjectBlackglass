using System.Data;
using System.Security.Cryptography;
using Dapper;
using Npgsql;
using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.Persistence.Accounts;
using static Blackglass.Accounts.Persistence.Accounts.OperatorAudit;

namespace Blackglass.Accounts.Persistence.Attempts;

public sealed record RegisteredDevHost(string HostId, int Capacity);

public sealed record DevHostHeartbeat(string HostId, DateTime LastHeartbeatAt);

/// <summary>
/// Development host fixtures (owner ruling S7-D2 and the controller ruling): <c>mission_hosts</c> rows of kind
/// <c>dev_fixture</c>, for isolated tests and the throwaway smoke stack only. They are never verified mission capacity: the
/// health check counts them only in a process run with BLACKGLASS_DEV_HOSTS=enabled, and the admin CLI refuses these verbs
/// unless its own process has that setting (the gate is the CLI's; this store is reached only through it and tests). There is
/// no agent route: Stage 8 registers <c>agent</c> hosts. Each operation is one transaction with its audit row. No retry.
/// </summary>
public sealed class DevHosts(NpgsqlDataSource db)
{
    /// <summary>The largest fixture capacity (a provisional sanity bound for a development fixture).</summary>
    public const int MaxCapacity = 1000;

    /// <summary>InvalidHost unless the id has 0007's form (the catalogue definition id form, at most 64 characters).</summary>
    public static void ValidateHostId(string? hostId)
    {
        if (!CanonicalCatalogueJson.IsWellFormedId(hostId))
            throw new MissionAdminException(MissionAdminError.InvalidHost,
                "The host id must be lower-case letters and digits in groups joined by '.' or '-', at most 64 characters; nothing was changed.");
    }

    /// <summary>InvalidHost unless 1 &lt;= capacity &lt;= <see cref="MaxCapacity"/>.</summary>
    public static void ValidateCapacity(int capacity)
    {
        if (capacity is < 1 or > MaxCapacity)
            throw new MissionAdminException(MissionAdminError.InvalidHost, $"The capacity must be a whole number, 1 to {MaxCapacity}; nothing was changed.");
    }

    /// <summary>Whether any host (of either kind) has this id, for the operator's preview.</summary>
    public async Task<bool> ExistsAsync(string hostId, CancellationToken ct = default)
    {
        await using var conn = await DbFailure.OpenAsync(db, ct);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select exists (select 1 from mission_hosts where host_id = @hostId)", new { hostId }, cancellationToken: ct));
    }

    /// <summary>
    /// Inserts an enabled <c>dev_fixture</c> host with no heartbeat. Its secret is 32 random bytes of which only the SHA-256 is
    /// stored; the secret itself is wiped and never returned, printed, logged or audited (no agent route uses it in Stage 7).
    /// Audited as <c>host_register_dev</c> {operation: register, hostId, kind, capacity, enabled}. An existing id is HostExists.
    /// </summary>
    public async Task<RegisteredDevHost> RegisterAsync(string hostId, int capacity, OperatorContext op, CancellationToken ct = default)
    {
        ValidateHostId(hostId);
        ValidateCapacity(capacity);
        var secret = RandomNumberGenerator.GetBytes(32);
        byte[] secretHash;
        try { secretHash = SHA256.HashData(secret); }
        finally { CryptographicOperations.ZeroMemory(secret); }

        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "insert into mission_hosts (host_id, kind, secret_hash, capacity, enabled) values (@hostId, 'dev_fixture', @secretHash, @capacity, true)",
                new { hostId, secretHash, capacity }, tx, cancellationToken: ct));
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation && e.ConstraintName == "mission_hosts_pkey")
        {
            throw new MissionAdminException(MissionAdminError.HostExists, $"A host with id {hostId} already exists: nothing was changed.");
        }
        await AuditAsync(conn, tx, op, "host_register_dev", null, null, new { operation = "register", hostId, kind = "dev_fixture", capacity, enabled = true }, ct);
        await tx.CommitAsync(ct);
        return new RegisteredDevHost(hostId, capacity);
    }

    /// <summary>
    /// Stamps a development fixture's heartbeat with the database clock (the smoke stack's stand-in for Stage 8's agent
    /// heartbeat, so it needs no superuser SQL). Only a <c>dev_fixture</c> row is touched: an agent host or an unknown id is
    /// UnknownDevHost, nothing written. 0007's operator action list has no heartbeat action, so this is audited as
    /// <c>host_register_dev</c> {operation: heartbeat, hostId, lastHeartbeatAt}.
    /// </summary>
    public async Task<DevHostHeartbeat> HeartbeatAsync(string hostId, OperatorContext op, CancellationToken ct = default)
    {
        ValidateHostId(hostId);
        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var stamped = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "update mission_hosts set last_heartbeat_at = clock_timestamp() where host_id = @hostId and kind = 'dev_fixture' returning last_heartbeat_at",
            new { hostId }, tx, cancellationToken: ct))
            ?? throw new MissionAdminException(MissionAdminError.UnknownDevHost, $"No development host fixture has id {hostId}: nothing was changed.");
        if (stamped.Kind != DateTimeKind.Utc) throw new InvalidOperationException($"A timestamptz was read as {stamped.Kind}, not UTC.");
        await AuditAsync(conn, tx, op, "host_register_dev", null, null, new { operation = "heartbeat", hostId, lastHeartbeatAt = stamped }, ct);
        await tx.CommitAsync(ct);
        return new DevHostHeartbeat(hostId, stamped);
    }
}
