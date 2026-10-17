using System.Data;
using Dapper;
using Npgsql;
using Blackglass.Accounts.Domain.Attempts;
using Blackglass.Accounts.Persistence.Accounts;
using static Blackglass.Accounts.Persistence.Accounts.OperatorAudit;

namespace Blackglass.Accounts.Persistence.Attempts;

/// <summary>
/// Versioned, immutable mission templates (owner ruling S7-D1): the backend's admission rules and energy cost. A published
/// version never changes (0007's triggers and grants); rebalancing publishes a new version through the audited operator verb
/// <c>mission template publish</c>. Admission reads the latest version with <see cref="LatestAsync"/>. Nothing here retries.
/// </summary>
public sealed class MissionTemplates(NpgsqlDataSource db)
{
    /// <summary>The proposed values as a template (version 1 stands in for the version the transaction mints), or
    /// InvalidTemplate naming the rule. A zero cost is valid. Run before any connection.</summary>
    public static MissionTemplate ValidateProposed(string? templateId, int energyCost, int minOperatives, int maxOperatives)
    {
        try { return MissionTemplate.Create(templateId ?? "", 1, energyCost, minOperatives, maxOperatives); }
        catch (ArgumentException e) { throw new MissionAdminException(MissionAdminError.InvalidTemplate, e.Message + " Nothing was published."); }
    }

    /// <summary>
    /// The latest published version of <paramref name="templateId"/>, or null when none was ever published. Unlocked: a
    /// published version is immutable, so this is a plain read on the caller's connection and transaction (which may be null).
    /// A stored row that MissionTemplate refuses (only a hand-inserted one could be) throws ArgumentException: admission fails
    /// closed rather than use it.
    /// </summary>
    public static async Task<MissionTemplate?> LatestAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string templateId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conn);
        var row = await conn.QuerySingleOrDefaultAsync<MissionTemplate>(new CommandDefinition(
            """
            select template_id as TemplateId, version as Version, energy_cost as EnergyCost, min_operatives as MinOperatives, max_operatives as MaxOperatives
            from mission_templates where template_id = @templateId order by version desc limit 1
            """, new { templateId }, tx, cancellationToken: ct));
        return row?.Validate();
    }

    /// <summary>The latest version for the operator's preview, on its own connection.</summary>
    public async Task<MissionTemplate?> DescribeLatestAsync(string templateId, CancellationToken ct = default)
    {
        await using var conn = await DbFailure.OpenAsync(db, ct);
        return await LatestAsync(conn, null, templateId, ct);
    }

    /// <summary>
    /// Publishes the next version of a template (InvalidTemplate before any connection). One READ COMMITTED transaction: the
    /// per-template advisory lock pg_advisory_xact_lock(hashtext('mission-template:' || template_id)), then the version
    /// coalesce(max(version), 0) + 1 for that template, then the insert, then the audit <c>mission_template_publish</c>
    /// {templateId, version, energyCost, minOperatives, maxOperatives} on no account. The primary key is the backstop: a 23505
    /// on it (an insert that bypassed the lock) is TemplateVersionConflict, never retried. Takes no energy or inventory lock.
    /// </summary>
    public async Task<MissionTemplate> PublishAsync(string templateId, int energyCost, int minOperatives, int maxOperatives, OperatorContext op, CancellationToken ct = default)
    {
        ValidateProposed(templateId, energyCost, minOperatives, maxOperatives);

        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "select pg_advisory_xact_lock(hashtext('mission-template:' || @templateId))", new { templateId }, tx, cancellationToken: ct));
        var version = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "select coalesce(max(version), 0) + 1 from mission_templates where template_id = @templateId", new { templateId }, tx, cancellationToken: ct));
        try
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                insert into mission_templates (template_id, version, energy_cost, min_operatives, max_operatives)
                values (@templateId, @version, @energyCost, @minOperatives, @maxOperatives)
                """, new { templateId, version, energyCost, minOperatives, maxOperatives }, tx, cancellationToken: ct));
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation && e.ConstraintName == "mission_templates_pkey")
        {
            throw new MissionAdminException(MissionAdminError.TemplateVersionConflict,
                $"Version {version} of template {templateId} was taken by a concurrent insert: nothing was published. Check the latest version, then retry.");
        }
        await AuditAsync(conn, tx, op, "mission_template_publish", null, null, new { templateId, version, energyCost, minOperatives, maxOperatives }, ct);
        await tx.CommitAsync(ct);
        return new MissionTemplate(templateId, version, energyCost, minOperatives, maxOperatives);
    }
}
