using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Npgsql;
using Blackglass.Accounts.Domain.Insurance;
using Blackglass.Accounts.Persistence.Accounts;
using static Blackglass.Accounts.Persistence.Accounts.OperatorAudit;

namespace Blackglass.Accounts.Persistence.Insurance;

/// <summary>A published policy version, with the SHA-256 (lower-case hex) of its canonical terms.</summary>
public sealed record PublishedPolicy(string PolicyId, int Version, string TermsSha256);

/// <summary>
/// What an activation recorded. StartsAt to ExpiresAt is the period this activation bought: from the later of the previous
/// expiry and the database clock, to the new expiry (for a fresh coverage StartsAt is also the coverage's start; an extension
/// keeps the coverage's earlier start). Every field comes from the stored activation row, so a replay returns exactly what
/// the first call returned, with Replayed true; a replay wrote nothing at all (no activation, no coverage change, no audit).
/// </summary>
public sealed record ActivationResult(Guid ActivationId, string PolicyId, int PolicyVersion, DateTime StartsAt, DateTime ExpiresAt, bool Replayed);

/// <summary>
/// The operator insurance verbs (owner ruling S7-D3, spec §6.2): publish a policy version and activate a coverage. Each is one
/// READ COMMITTED transaction that appends one operator_actions row; a failed audit rolls everything back. Inputs are checked
/// before any connection. Neither verb touches an energy table or lock, nor loads the catalogue. Nothing here retries: a
/// publication conflict is reported, and an activation's idempotency key makes an operator's retry safe. A 23514 from an
/// insurance constraint is a bug and propagates unchanged. A suspended account is served like any other (coverage is
/// property, not access). Stage 7 has no payout or settlement code.
/// </summary>
public sealed partial class InsuranceAdminStore(NpgsqlDataSource db)
{
    /// <summary>The longest activation: 3650 days (CoverageMath.MaxDuration, a provisional sanity bound).</summary>
    public const int MaxDurationSeconds = 315_360_000;

    public const int MaxPolicyIdLength = 64;

    [GeneratedRegex("^[a-z0-9]+([.-][a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex PolicyIdForm();

    /// <summary>InvalidPolicyId unless the id has 0007's form: lower-case letters and digits in groups joined by '.' or '-', at
    /// most 64 characters (the catalogue definition id form).</summary>
    public static void ValidatePolicyId(string? policyId)
    {
        if (policyId is null || policyId.Length > MaxPolicyIdLength || !PolicyIdForm().IsMatch(policyId))
            throw new InsuranceException(InsuranceError.InvalidPolicyId,
                $"The policy id must be lower-case letters and digits in groups joined by '.' or '-', at most {MaxPolicyIdLength} characters; nothing was changed.");
    }

    /// <summary>InvalidDuration unless 1 &lt;= seconds &lt;= <see cref="MaxDurationSeconds"/>.</summary>
    public static void ValidateDuration(int seconds)
    {
        if (seconds < 1 || seconds > MaxDurationSeconds)
            throw new InsuranceException(InsuranceError.InvalidDuration,
                $"The duration must be a whole number of seconds, 1 to {MaxDurationSeconds} (3650 days); nothing was changed.");
    }

    /// <summary><see cref="InsuranceTerms.Parse"/>, its refusal reported as InvalidTerms (the message names the member and rule).</summary>
    public static InsuranceTerms ParseTerms(string json)
    {
        try { return InsuranceTerms.Parse(json); }
        catch (InsuranceTermsException e) { throw new InsuranceException(InsuranceError.InvalidTerms, e.Message + " Nothing was published."); }
    }

    /// <summary>The audit's fingerprint of a terms text: SHA-256 of its UTF-8 bytes, lower-case hex.</summary>
    public static string TermsSha256(string canonicalTerms) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalTerms)));

    /// <summary>
    /// Publishes the next version of <paramref name="policyId"/> (InvalidPolicyId before any connection). One transaction:
    /// the per-policy advisory lock pg_advisory_xact_lock(hashtext('insurance-policy:' || policy_id)), then the version
    /// coalesce(max(version), 0) + 1 for that policy, then the insert of the canonical terms, then the audit
    /// <c>insurance_policy_publish</c> {policyId, version, termsSha256} on no account (the terms themselves are in the row).
    /// The primary key is the backstop: a 23505 on it (an insert that bypassed the lock) is PolicyVersionConflict, never retried.
    /// </summary>
    public async Task<PublishedPolicy> PublishPolicyAsync(string policyId, InsuranceTerms terms, OperatorContext op, CancellationToken ct = default)
    {
        ValidatePolicyId(policyId);
        ArgumentNullException.ThrowIfNull(terms);
        string canonical;
        try { canonical = terms.Canonical(); }
        catch (InsuranceTermsException e) { throw new InsuranceException(InsuranceError.InvalidTerms, e.Message + " Nothing was published."); }
        var sha = TermsSha256(canonical);

        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "select pg_advisory_xact_lock(hashtext('insurance-policy:' || @policyId))", new { policyId }, tx, cancellationToken: ct));
        var version = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "select coalesce(max(version), 0) + 1 from insurance_policies where policy_id = @policyId", new { policyId }, tx, cancellationToken: ct));
        try
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "insert into insurance_policies (policy_id, version, terms) values (@policyId, @version, @canonical::jsonb)",
                new { policyId, version, canonical }, tx, cancellationToken: ct));
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation && e.ConstraintName == "insurance_policies_pkey")
        {
            throw new InsuranceException(InsuranceError.PolicyVersionConflict,
                $"Version {version} of policy {policyId} was taken by a concurrent insert: nothing was published. Check the latest version, then retry.");
        }
        await AuditAsync(conn, tx, op, "insurance_policy_publish", null, null, new { policyId, version, termsSha256 = sha }, ct);
        await tx.CommitAsync(ct);
        return new PublishedPolicy(policyId, version, sha);
    }

    /// <summary>
    /// A development activation (S7-D3). The policy id and the duration (1 s to 3650 days) are checked before any connection.
    /// One transaction:
    /// <list type="number">
    /// <item>the accounts row FOR UPDATE (NotFound), then the account's coverage FOR UPDATE when it has one;</item>
    /// <item>the key looked up for this account first: the same policy and duration replays the stored activation (nothing
    /// written at all: no activation, no coverage change, no audit row), even if a newer policy version was published or the
    /// coverage has since expired; anything else is IdempotencyKeyReused, with nothing written;</item>
    /// <item>for a new key: the policy's latest version (UnknownPolicy), then clock_timestamp() (after the locks); a different
    /// policy whose coverage is still active (now &lt; expires_at) is CrossTierDeferred, nothing written; the same policy
    /// extends to greatest(expires_at, now) + duration, keeping a running coverage's start (<see cref="CoverageMath.Extend"/>);
    /// no coverage, or an expired one, starts fresh at now;</item>
    /// <item>the activation row (a new UUIDv7, previous_expires_at = the coverage's expiry before it or null, source
    /// dev_grant), then the coverage inserted or updated (its policy version moves to the activated one), then the audit
    /// <c>insurance_activate</c> {activationId, policyId, policyVersion, durationSeconds, previousExpiresAt, coverageStartsAt,
    /// expiresAt, idempotencyKey}.</item>
    /// </list>
    /// Concurrent uses of one key for one account serialize on the accounts row, so the later ones replay. The operator and
    /// reason are audit context only.
    /// </summary>
    public async Task<ActivationResult> ActivateAsync(long accountId, string policyId, int durationSeconds, Guid idempotencyKey, OperatorContext op,
        CancellationToken ct = default)
    {
        ValidatePolicyId(policyId);
        ValidateDuration(durationSeconds);
        var duration = TimeSpan.FromSeconds(durationSeconds);

        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await AccountAdminStore.LockAccountAsync(conn, tx, accountId, ct);
        var coverage = await conn.QuerySingleOrDefaultAsync<InsuranceCoverage>(new CommandDefinition(
            """
            select policy_id as PolicyId, policy_version as PolicyVersion, activation_id as ActivationId, starts_at as StartsAt, expires_at as ExpiresAt
            from insurance_coverages where account_id = @accountId for update
            """, new { accountId }, tx, cancellationToken: ct));

        // Served by unique (account_id, idempotency_key). This statement starts after both locks are held, so it sees every
        // activation of this account that committed before them.
        var prior = await conn.QuerySingleOrDefaultAsync<PriorActivation>(new CommandDefinition(
            """
            select activation_id as ActivationId, policy_id as PolicyId, policy_version as PolicyVersion, duration as Duration, resulting_expires_at as ResultingExpiresAt
            from insurance_activations where account_id = @accountId and idempotency_key = @idempotencyKey
            """, new { accountId, idempotencyKey }, tx, cancellationToken: ct));
        if (prior is not null)
        {
            if (prior.PolicyId != policyId || prior.Duration != duration)
                throw new InsuranceException(InsuranceError.IdempotencyKeyReused,
                    "That idempotency key was already used for another activation of this account (another policy or duration): nothing was changed. Use a new key for a new activation.");
            InsuranceReader.RequireUtc(prior.ResultingExpiresAt);
            await tx.RollbackAsync(ct); // a replay writes nothing: it only releases the row locks
            return new ActivationResult(prior.ActivationId, prior.PolicyId, prior.PolicyVersion, prior.ResultingExpiresAt - prior.Duration, prior.ResultingExpiresAt, true);
        }

        var version = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
            "select max(version) from insurance_policies where policy_id = @policyId", new { policyId }, tx, cancellationToken: ct))
            ?? throw new InsuranceException(InsuranceError.UnknownPolicy, $"No version of policy {policyId} was ever published: nothing was changed.");
        var now = await conn.ExecuteScalarAsync<DateTime>(new CommandDefinition("select clock_timestamp()", transaction: tx, cancellationToken: ct));
        InsuranceReader.RequireUtc(now);

        (DateTime StartsAt, DateTime ExpiresAt) window;
        if (coverage is not null)
        {
            InsuranceReader.RequireUtc(coverage.StartsAt);
            InsuranceReader.RequireUtc(coverage.ExpiresAt);
        }
        if (coverage is not null && coverage.PolicyId == policyId)
            window = CoverageMath.Extend(coverage.StartsAt, coverage.ExpiresAt, now, duration);
        else if (coverage is not null && now < coverage.ExpiresAt)
            throw new InsuranceException(InsuranceError.CrossTierDeferred,
                $"This account's coverage of policy {coverage.PolicyId} is active until {coverage.ExpiresAt:yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'}: switching to policy {policyId} while it is active is refused (cross-tier is deferred). Nothing was changed.");
        else
            window = CoverageMath.Extend(null, null, now, duration);

        var activationId = Guid.CreateVersion7();
        var previousExpiresAt = coverage?.ExpiresAt;
        // The activation first: the coverage's foreign key to it is immediate.
        await conn.ExecuteAsync(new CommandDefinition(
            """
            insert into insurance_activations (activation_id, account_id, policy_id, policy_version, duration, previous_expires_at, resulting_expires_at, source, idempotency_key)
            values (@activationId, @accountId, @policyId, @version, @duration, @previousExpiresAt, @expiresAt, 'dev_grant', @idempotencyKey)
            """, new { activationId, accountId, policyId, version, duration, previousExpiresAt, expiresAt = window.ExpiresAt, idempotencyKey }, tx, cancellationToken: ct));
        var written = await conn.ExecuteAsync(new CommandDefinition(
            coverage is null
                ? "insert into insurance_coverages (account_id, policy_id, policy_version, activation_id, starts_at, expires_at) values (@accountId, @policyId, @version, @activationId, @startsAt, @expiresAt)"
                : "update insurance_coverages set policy_id = @policyId, policy_version = @version, activation_id = @activationId, starts_at = @startsAt, expires_at = @expiresAt where account_id = @accountId",
            new { accountId, policyId, version, activationId, startsAt = window.StartsAt, expiresAt = window.ExpiresAt }, tx, cancellationToken: ct));
        if (written != 1) throw new InvalidOperationException("The coverage was not written exactly once.");
        await AuditAsync(conn, tx, op, "insurance_activate", accountId, null,
            new
            {
                activationId = activationId.ToString("D"), policyId, policyVersion = version, durationSeconds, previousExpiresAt, coverageStartsAt = window.StartsAt,
                expiresAt = window.ExpiresAt, idempotencyKey = idempotencyKey.ToString("D"),
            }, ct);
        await tx.CommitAsync(ct);
        return new ActivationResult(activationId, policyId, version, window.ExpiresAt - duration, window.ExpiresAt, false);
    }

    sealed record PriorActivation(Guid ActivationId, string PolicyId, int PolicyVersion, TimeSpan Duration, DateTime ResultingExpiresAt);
}
