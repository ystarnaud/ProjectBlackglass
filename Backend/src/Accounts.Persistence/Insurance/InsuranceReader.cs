using Dapper;
using Npgsql;

namespace Blackglass.Accounts.Persistence.Insurance;

/// <summary>An account's coverage row as stored: active while starts_at &lt;= now &lt; expires_at (the reader never decides).</summary>
public sealed record InsuranceCoverage(string PolicyId, int PolicyVersion, Guid ActivationId, DateTime StartsAt, DateTime ExpiresAt);

/// <summary>One published policy version; Terms is the stored jsonb's text.</summary>
public sealed record InsurancePolicyVersion(string PolicyId, int Version, string Terms);

/// <summary>
/// Unlocked reads of the insurance tables for the operator's previews: what was committed when the statement ran, never a
/// decision (activation re-reads everything under its locks). Nothing here writes or materializes.
/// </summary>
public sealed class InsuranceReader(NpgsqlDataSource db)
{
    /// <summary>The account's coverage row, or null when it never had one (expired coverages are returned as stored).</summary>
    public async Task<InsuranceCoverage?> DescribeCoverageAsync(long accountId, CancellationToken ct = default)
    {
        await using var conn = await DbFailure.OpenAsync(db, ct);
        var coverage = await conn.QuerySingleOrDefaultAsync<InsuranceCoverage>(new CommandDefinition(
            """
            select policy_id as PolicyId, policy_version as PolicyVersion, activation_id as ActivationId, starts_at as StartsAt, expires_at as ExpiresAt
            from insurance_coverages where account_id = @accountId
            """, new { accountId }, cancellationToken: ct));
        if (coverage is not null) { RequireUtc(coverage.StartsAt); RequireUtc(coverage.ExpiresAt); }
        return coverage;
    }

    /// <summary>The latest published version of the policy, or null when none was ever published.</summary>
    public async Task<InsurancePolicyVersion?> DescribeLatestPolicyAsync(string policyId, CancellationToken ct = default)
    {
        await using var conn = await DbFailure.OpenAsync(db, ct);
        return await conn.QuerySingleOrDefaultAsync<InsurancePolicyVersion>(new CommandDefinition(
            """
            select policy_id as PolicyId, version as Version, terms::text as Terms
            from insurance_policies where policy_id = @policyId order by version desc limit 1
            """, new { policyId }, cancellationToken: ct));
    }

    internal static void RequireUtc(DateTime value)
    {
        if (value.Kind != DateTimeKind.Utc) throw new InvalidOperationException($"A timestamptz was read as {value.Kind}, not UTC.");
    }
}
