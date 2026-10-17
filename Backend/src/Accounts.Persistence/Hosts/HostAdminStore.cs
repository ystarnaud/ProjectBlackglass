using System.Data;
using System.Globalization;
using Dapper;
using Npgsql;
using Blackglass.Accounts.Domain.Auth;
using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.Domain.Hosts;
using Blackglass.Accounts.Persistence.Accounts;
using Blackglass.Accounts.Persistence.Attempts;
using static Blackglass.Accounts.Persistence.Accounts.OperatorAudit;

namespace Blackglass.Accounts.Persistence.Hosts;

public sealed record RegisteredHost(string HostId, int Capacity, int PortBase, OpaqueSecret Secret);

public sealed record RotatedHostSecret(string HostId, OpaqueSecret Secret, DateTime RotatedAt);

/// <summary>A host as the operator sees it before a change: live slots are its attempts whose slot release is unconfirmed.</summary>
public sealed record HostSummary(string HostId, string Kind, bool Enabled, bool Drained, int Capacity, int? PortBase, long LiveSlots,
    DateTime? LastHeartbeatAt, string? BuildRevision, string? CatalogueVersion, bool SecretRevoked = false);

/// <summary>A disabled host and how many of its attempts' process credentials the disable revoked.</summary>
public sealed record DisabledHost(HostSummary Host, int RevokedCredentials);

public enum HostSwitch { Drain, Enable, Disable }

/// <summary>A mission template version a build may run.</summary>
public sealed record TemplateRef(string TemplateId, int Version)
{
    public override string ToString() => $"{TemplateId}:{Version.ToString(CultureInfo.InvariantCulture)}";
}

public sealed record BuildSummary(BuildIdentity Build, bool Enabled, IReadOnlyList<TemplateRef> Templates);

public sealed record AllowedBuildResult(BuildIdentity Build, bool Enabled, bool Created, bool ReEnabled, int TemplatesAdded, IReadOnlyList<TemplateRef> Templates);

/// <summary>
/// The operator verbs for mission hosts and builds (owner rulings B3, B4). Agent hosts exist only through
/// <see cref="RegisterAsync"/>: its 256-bit <c>hs1_</c> secret is generated here, stored as SHA-256, returned once for the
/// CLI to print, and never logged or audited. Every verb is one READ COMMITTED transaction with its <c>operator_actions</c> row
/// (naming the host in <c>host_id</c> for a host verb), so a failed audit rolls the change back. Values are checked before
/// any connection. No retry; a lock or statement timeout propagates as busy.
/// </summary>
public sealed class HostAdminStore(NpgsqlDataSource db)
{
    /// <summary>An agent host's largest capacity: every unit must fit in one heartbeat's complete inventory (Checkpoint 1 M4).</summary>
    public const int MaxCapacity = Blackglass.Accounts.Domain.Attempts.HeartbeatRequest.MaxUnits;
    public const int MinPortBase = 1024;
    public const int MaxTemplates = 32;

    /// <summary>InvalidHost unless the id has 0007's form, 1 &lt;= capacity &lt;= <see cref="MaxCapacity"/>, and the ports
    /// [portBase, portBase + capacity) lie in 1024..65535.</summary>
    public static void ValidateRegistration(string? hostId, int capacity, int portBase)
    {
        DevHosts.ValidateHostId(hostId);
        ValidateCapacity(capacity);
        if (portBase < MinPortBase || portBase + capacity > 65536)
            throw new MissionAdminException(MissionAdminError.InvalidHost,
                $"The port base must leave the host's ports (port base to port base + capacity - 1) within {MinPortBase} to 65535; nothing was changed.");
    }

    public static void ValidateCapacity(int capacity)
    {
        if (capacity is < 1 or > MaxCapacity)
            throw new MissionAdminException(MissionAdminError.InvalidHost, $"The capacity must be a whole number, 1 to {MaxCapacity}; nothing was changed.");
    }

    /// <summary>InvalidBuild unless the revision and catalogue version have <see cref="BuildIdentity"/>'s forms.</summary>
    public static BuildIdentity ParseBuild(string? revision, string? catalogueVersion) =>
        BuildIdentity.TryParse(revision, catalogueVersion, out var build)
            ? build!
            : throw new MissionAdminException(MissionAdminError.InvalidBuild,
                "The revision must be 1 to 64 letters, digits, '.', '_' or '-' (a letter or digit first) and the catalogue version sha256:<64 lowercase hex>; nothing was changed.");

    /// <summary>"&lt;template id&gt;:&lt;version&gt;[,…]": 1 to <see cref="MaxTemplates"/> distinct entries, ids in the
    /// catalogue id form, versions 1 or more. Anything else is InvalidBuild.</summary>
    public static IReadOnlyList<TemplateRef> ParseTemplates(string? text)
    {
        static MissionAdminException Invalid() => new(MissionAdminError.InvalidBuild,
            "--templates must list 1 to 32 distinct <template id>:<version> entries separated by commas; nothing was changed.");
        if (string.IsNullOrEmpty(text)) throw Invalid();
        var parts = text.Split(',');
        if (parts.Length > MaxTemplates) throw Invalid();
        var list = new List<TemplateRef>(parts.Length);
        foreach (var part in parts)
        {
            var colon = part.LastIndexOf(':');
            if (colon <= 0 || !CanonicalCatalogueJson.IsWellFormedId(part[..colon])
                || !int.TryParse(part.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version < 1)
                throw Invalid();
            var entry = new TemplateRef(part[..colon], version);
            if (list.Contains(entry)) throw Invalid();
            list.Add(entry);
        }
        return list;
    }

    public async Task<HostSummary?> DescribeAsync(string hostId, CancellationToken ct = default)
    {
        await using var conn = await DbFailure.OpenAsync(db, ct);
        return await DescribeAsync(conn, null, hostId, ct);
    }

    static async Task<HostSummary?> DescribeAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string hostId, CancellationToken ct)
    {
        var row = await conn.QuerySingleOrDefaultAsync<HostRow>(new CommandDefinition(
            """
            select h.host_id as HostId, h.kind as Kind, h.enabled as Enabled, h.drained as Drained, h.capacity as Capacity, h.port_base as PortBase,
                   (select count(*) from mission_attempts a where a.host_id = h.host_id and a.slot_released_at is null) as LiveSlots,
                   h.last_heartbeat_at as LastHeartbeatAt, h.build_revision as BuildRevision, h.catalogue_version as CatalogueVersion,
                   h.secret_revoked as SecretRevoked
            from mission_hosts h where h.host_id = @hostId
            """, new { hostId }, tx, cancellationToken: ct));
        return row?.ToSummary();
    }

    sealed class HostRow
    {
        public string HostId { get; init; } = "";
        public string Kind { get; init; } = "";
        public bool Enabled { get; init; }
        public bool Drained { get; init; }
        public int Capacity { get; init; }
        public int? PortBase { get; init; }
        public long LiveSlots { get; init; }
        public DateTime? LastHeartbeatAt { get; init; }
        public string? BuildRevision { get; init; }
        public string? CatalogueVersion { get; init; }
        public bool SecretRevoked { get; init; }

        public HostSummary ToSummary() => new(HostId, Kind, Enabled, Drained, Capacity, PortBase, LiveSlots, LastHeartbeatAt, BuildRevision, CatalogueVersion, SecretRevoked);
    }

    /// <summary>
    /// Inserts an enabled, undrained <c>agent</c> host with its port range and no heartbeat, and returns its new secret (shown
    /// once by the CLI). Audited as <c>host_register</c> {hostId, kind, capacity, portBase, enabled}: never the secret or its
    /// hash. An existing id (of either kind) is HostExists, nothing written.
    /// </summary>
    public async Task<RegisteredHost> RegisterAsync(string hostId, int capacity, int portBase, OperatorContext op, CancellationToken ct = default)
    {
        ValidateRegistration(hostId, capacity, portBase);
        var secret = OpaqueSecret.New(OpaqueSecret.HostSecretPrefix);
        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "insert into mission_hosts (host_id, kind, secret_hash, capacity, enabled, port_base) values (@hostId, 'agent', @hash, @capacity, true, @portBase)",
                new { hostId, hash = secret.Hash(), capacity, portBase }, tx, cancellationToken: ct));
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation && e.ConstraintName == "mission_hosts_pkey")
        {
            throw new MissionAdminException(MissionAdminError.HostExists, $"A host with id {hostId} already exists: nothing was changed.");
        }
        await AuditHostAsync(conn, tx, op, "host_register", hostId, new { hostId, kind = "agent", capacity, portBase, enabled = true }, ct);
        await tx.CommitAsync(ct);
        return new RegisteredHost(hostId, capacity, portBase, secret);
    }

    /// <summary>
    /// Replaces an agent host's secret: the old one is refused from the commit on (authentication is one indexed lookup per
    /// request, never cached). Stamps <c>secret_rotated_at</c> and clears <c>secret_revoked</c> (the recovery step after a
    /// disable; it does not enable the host, and it restores no process credential). A development fixture or an unknown id is
    /// UnknownHost. Audited as <c>host_rotate_secret</c> {hostId, rotatedAt}.
    /// </summary>
    public async Task<RotatedHostSecret> RotateSecretAsync(string hostId, OperatorContext op, CancellationToken ct = default)
    {
        DevHosts.ValidateHostId(hostId);
        var secret = OpaqueSecret.New(OpaqueSecret.HostSecretPrefix);
        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var rotatedAt = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "update mission_hosts set secret_hash = @hash, secret_rotated_at = clock_timestamp(), secret_revoked = false where host_id = @hostId and kind = 'agent' returning secret_rotated_at",
            new { hostId, hash = secret.Hash() }, tx, cancellationToken: ct))
            ?? throw new MissionAdminException(MissionAdminError.UnknownHost, $"No agent host has id {hostId}: nothing was changed.");
        await AuditHostAsync(conn, tx, op, "host_rotate_secret", hostId, new { hostId, rotatedAt = Utc(rotatedAt) }, ct);
        await tx.CommitAsync(ct);
        return new RotatedHostSecret(hostId, secret, Utc(rotatedAt));
    }

    /// <summary>
    /// Drain (planned maintenance: no new claims; the agent and its running processes keep working), enable (enabled and
    /// undrained; SecretRevoked while a disable's revocation awaits `host rotate-secret`) or disable (<see cref="DisableAsync"/>).
    /// Drain and enable change only those columns. Audited as <c>host_drain</c> or <c>host_enable</c> {hostId, enabled,
    /// drained}. An unknown id is UnknownHost.
    /// </summary>
    public async Task<HostSummary> SwitchAsync(string hostId, HostSwitch change, OperatorContext op, CancellationToken ct = default)
    {
        DevHosts.ValidateHostId(hostId);
        switch (change)
        {
            case HostSwitch.Drain:
                return await ChangeAsync(hostId, "update mission_hosts set drained = true where host_id = @hostId", new { hostId }, "host_drain", op, ct);
            case HostSwitch.Enable:
                if (await DescribeAsync(hostId, ct) is { SecretRevoked: true })
                    throw new MissionAdminException(MissionAdminError.SecretRevoked,
                        $"Host {hostId} was disabled and its secret revoked: run `host rotate-secret` and install the new secret before enabling it. Nothing was changed.");
                // The guard repeats in the UPDATE, so a disable committing in between can't be undone by this enable.
                return await ChangeAsync(hostId, "update mission_hosts set enabled = true, drained = false where host_id = @hostId and not secret_revoked",
                    new { hostId }, "host_enable", op, ct);
            case HostSwitch.Disable:
                return (await DisableAsync(hostId, op, ct)).Host;
            default:
                throw new ArgumentOutOfRangeException(nameof(change));
        }
    }

    /// <summary>
    /// Emergency isolation (owner decision of 2026-10-17; plan revision 2 R8), one READ COMMITTED transaction in the host lock
    /// order (the host row FOR UPDATE, then its attempts): the host is disabled (no claims), its secret replaced by the hash of an
    /// unknown random secret and marked revoked (its old secret is refused at once and never valid again; enabling needs `host
    /// rotate-secret` first), and every attempt assigned to it that is not yet cancelled or settled gets
    /// <c>credential_revoked_at</c> (written once): its process credential is revoked for good, whatever later happens to the
    /// host. Process routes refuse it at authentication and every process store re-checks it under the attempt lock, so no
    /// process write commits after the disable. Nothing is cancelled, released, voided or refunded, no slot is freed, and no
    /// attempt, participant, energy, insurance or inventory record changes otherwise. Audited as <c>host_disable</c>
    /// {operation: disable, hostId, revokedCredentials} with the operator's reason, in the same transaction. A development
    /// fixture is disabled the same way (it has no attempts). An unknown id is UnknownHost.
    /// </summary>
    public async Task<DisabledHost> DisableAsync(string hostId, OperatorContext op, CancellationToken ct = default)
    {
        DevHosts.ValidateHostId(hostId);
        var unknown = OpaqueSecret.New(OpaqueSecret.HostSecretPrefix); // its hash replaces the secret; the secret itself is discarded
        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        if (await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
                "select 1 from mission_hosts where host_id = @hostId for update", new { hostId }, tx, cancellationToken: ct)) is null)
            throw new MissionAdminException(MissionAdminError.UnknownHost, $"No host has id {hostId}: nothing was changed.");
        await conn.ExecuteAsync(new CommandDefinition(
            "update mission_hosts set enabled = false, secret_hash = @hash, secret_revoked = true where host_id = @hostId",
            new { hostId, hash = unknown.Hash() }, tx, cancellationToken: ct));
        var revoked = await conn.ExecuteAsync(new CommandDefinition(
            """
            update mission_attempts set credential_revoked_at = clock_timestamp()
            where attempt_id in (select attempt_id from mission_attempts
                                 where host_id = @hostId and credential_revoked_at is null and state not in ('cancelled', 'settled')
                                 order by attempt_id for update)
            """, new { hostId }, tx, cancellationToken: ct));
        var after = await DescribeAsync(conn, tx, hostId, ct) ?? throw new InvalidOperationException("The disabled host vanished.");
        await AuditHostAsync(conn, tx, op, "host_disable", hostId,
            new { operation = "disable", hostId, enabled = false, secretRevoked = true, revokedCredentials = revoked }, ct);
        await tx.CommitAsync(ct);
        return new DisabledHost(after, revoked);
    }

    /// <summary>Sets the capacity (1 to <see cref="MaxCapacity"/>; the ports must stay below 65536, else InvalidHost). A capacity
    /// below the live slots is allowed: the host simply gets no claim until enough slots are released. Audited as
    /// <c>host_capacity</c>.</summary>
    public async Task<HostSummary> SetCapacityAsync(string hostId, int capacity, OperatorContext op, CancellationToken ct = default)
    {
        DevHosts.ValidateHostId(hostId);
        ValidateCapacity(capacity);
        try { return await ChangeAsync(hostId, "update mission_hosts set capacity = @capacity where host_id = @hostId", new { hostId, capacity }, "host_capacity", op, ct); }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.CheckViolation && e.ConstraintName == "mission_host_port_range")
        {
            throw new MissionAdminException(MissionAdminError.InvalidHost, "That capacity would take the host's ports past 65535; nothing was changed.");
        }
    }

    async Task<HostSummary> ChangeAsync(string hostId, string sql, object args, string action, OperatorContext op, CancellationToken ct)
    {
        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        if (await conn.ExecuteAsync(new CommandDefinition(sql, args, tx, cancellationToken: ct)) != 1)
            throw await DescribeAsync(conn, tx, hostId, ct) is { SecretRevoked: true }
                ? new MissionAdminException(MissionAdminError.SecretRevoked, $"Host {hostId} was disabled and its secret revoked: run `host rotate-secret` first. Nothing was changed.")
                : new MissionAdminException(MissionAdminError.UnknownHost, $"No host has id {hostId}: nothing was changed.");
        var after = await DescribeAsync(conn, tx, hostId, ct) ?? throw new InvalidOperationException("The changed host vanished.");
        await AuditHostAsync(conn, tx, op, action, hostId, new { hostId, enabled = after.Enabled, drained = after.Drained, capacity = after.Capacity }, ct);
        await tx.CommitAsync(ct);
        return after;
    }

    public async Task<BuildSummary?> DescribeBuildAsync(BuildIdentity build, CancellationToken ct = default)
    {
        await using var conn = await DbFailure.OpenAsync(db, ct);
        return await DescribeBuildAsync(conn, null, build, ct);
    }

    static async Task<BuildSummary?> DescribeBuildAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, BuildIdentity build, CancellationToken ct)
    {
        var enabled = await conn.QuerySingleOrDefaultAsync<bool?>(new CommandDefinition(
            "select enabled from allowed_builds where build_revision = @Revision and catalogue_version = @CatalogueVersion", build, tx, cancellationToken: ct));
        if (enabled is null) return null;
        var templates = (await conn.QueryAsync<(string TemplateId, int Version)>(new CommandDefinition(
            """
            select template_id, template_version from allowed_build_templates
            where build_revision = @Revision and catalogue_version = @CatalogueVersion order by template_id, template_version
            """, build, tx, cancellationToken: ct))).Select(t => new TemplateRef(t.TemplateId, t.Version)).ToList();
        return new BuildSummary(build, enabled.Value, templates);
    }

    /// <summary>
    /// Allowlists a build for the given template versions: inserts it (enabled) or re-enables it, and adds the templates it does
    /// not list yet. Idempotent: a repeat adds nothing. A template version that doesn't exist is UnknownTemplate, nothing
    /// written. Audited as <c>build_allow</c> {buildRevision, catalogueVersion, created, reEnabled, templatesAdded}.
    /// </summary>
    public async Task<AllowedBuildResult> AllowBuildAsync(BuildIdentity build, IReadOnlyList<TemplateRef> templates, OperatorContext op, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(build);
        if (templates is null || templates.Count is < 1 or > MaxTemplates || templates.Distinct().Count() != templates.Count)
            throw new MissionAdminException(MissionAdminError.InvalidBuild, "A build is allowed for 1 to 32 distinct template versions; nothing was changed.");
        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var created = await conn.ExecuteAsync(new CommandDefinition(
            "insert into allowed_builds (build_revision, catalogue_version) values (@Revision, @CatalogueVersion) on conflict do nothing", build, tx, cancellationToken: ct)) == 1;
        var reEnabled = !created && await conn.ExecuteAsync(new CommandDefinition(
            "update allowed_builds set enabled = true where build_revision = @Revision and catalogue_version = @CatalogueVersion and not enabled", build, tx, cancellationToken: ct)) == 1;
        var added = 0;
        foreach (var t in templates)
        {
            try
            {
                added += await conn.ExecuteAsync(new CommandDefinition(
                    """
                    insert into allowed_build_templates (build_revision, catalogue_version, template_id, template_version)
                    values (@Revision, @CatalogueVersion, @TemplateId, @Version) on conflict do nothing
                    """, new { build.Revision, build.CatalogueVersion, t.TemplateId, t.Version }, tx, cancellationToken: ct));
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.ForeignKeyViolation && e.ConstraintName == "allowed_build_templates_template_fkey")
            {
                throw new MissionAdminException(MissionAdminError.UnknownTemplate, $"Mission template {t} doesn't exist: nothing was changed.");
            }
        }
        var after = await DescribeBuildAsync(conn, tx, build, ct) ?? throw new InvalidOperationException("The allowed build vanished.");
        await AuditHostAsync(conn, tx, op, "build_allow", null,
            new { buildRevision = build.Revision, catalogueVersion = build.CatalogueVersion, created, reEnabled, templatesAdded = added,
                  templates = templates.Select(t => t.ToString()).ToArray() }, ct);
        await tx.CommitAsync(ct);
        return new AllowedBuildResult(build, after.Enabled, created, reEnabled, added, after.Templates);
    }

    /// <summary>
    /// Disables a build: no new claim or registration on it. An attempt already running on it is untouched. An unknown build is
    /// UnknownBuild. Audited as <c>build_disable</c> {buildRevision, catalogueVersion}.
    /// </summary>
    public async Task<AllowedBuildResult> DisableBuildAsync(BuildIdentity build, OperatorContext op, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(build);
        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        if (await conn.ExecuteAsync(new CommandDefinition(
                "update allowed_builds set enabled = false where build_revision = @Revision and catalogue_version = @CatalogueVersion", build, tx, cancellationToken: ct)) != 1)
            throw new MissionAdminException(MissionAdminError.UnknownBuild, $"Build {build.Revision} ({build.CatalogueVersion}) is not allowlisted: nothing was changed.");
        var after = await DescribeBuildAsync(conn, tx, build, ct) ?? throw new InvalidOperationException("The allowed build vanished.");
        await AuditHostAsync(conn, tx, op, "build_disable", null, new { buildRevision = build.Revision, catalogueVersion = build.CatalogueVersion }, ct);
        await tx.CommitAsync(ct);
        return new AllowedBuildResult(build, after.Enabled, false, false, 0, after.Templates);
    }

    static DateTime Utc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : throw new InvalidOperationException($"A timestamptz was read as {value.Kind}, not UTC.");
}
