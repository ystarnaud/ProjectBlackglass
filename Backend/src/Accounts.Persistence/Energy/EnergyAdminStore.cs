using System.Data;
using Dapper;
using Npgsql;
using Blackglass.Accounts.Domain.Energy;
using Blackglass.Accounts.Persistence.Accounts;
using static Blackglass.Accounts.Persistence.Accounts.OperatorAudit;

namespace Blackglass.Accounts.Persistence.Energy;

/// <param name="Regenerated">What the materialization before the credit added (0 for a replay).</param>
/// <param name="Replayed">True when the idempotency key had already granted this amount to this account: nothing at all was
/// written (no wallet change, no ledger entry, no audit row), and BalanceAfter is the balance that first grant left.</param>
public sealed record GrantResult(int Amount, int BalanceAfter, int Regenerated, bool Replayed);

/// <summary>
/// The operator energy verbs (owner rulings E2, E3, E5, E6): open a wallet, grant development energy, publish a policy. Each
/// is one READ COMMITTED transaction (the advisory-lock protocol and the ledger chain need it) that appends one
/// operator_actions row; a failed audit rolls everything back. Lock order: the accounts row FOR UPDATE, then the wallet FOR
/// UPDATE (grants), then the shared policy lock (inside <see cref="EnergyLedgerWriter"/> and <see cref="EnergyProvisioner"/>);
/// a publication takes only the exclusive policy lock. Values are checked before any connection is opened, except a grant's
/// configurable maximum, which applies only to a new key and is checked after the key lookup. A 23514 from an
/// energy constraint is a bug and propagates unchanged; nothing here retries (a grant's idempotency key makes an operator's
/// retry safe). A suspended account is served like any other: energy is property, not access.
/// </summary>
public sealed class EnergyAdminStore(NpgsqlDataSource db, int grantMax)
{
    /// <summary>The longest regeneration interval a policy may have (0006's CHECK): 7 days.</summary>
    public const int MaxIntervalSeconds = 7 * 24 * 60 * 60;

    readonly int grantMax = grantMax >= 1 ? grantMax : throw new ArgumentOutOfRangeException(nameof(grantMax), "The largest grant must be at least 1.");

    /// <summary>InvalidAmount unless amount &gt;= 1: the structural check, independent of configuration, made before any
    /// connection. The configurable maximum is not checked here: it applies only to a new key, inside the grant's transaction
    /// (a replay of a grant made under a higher maximum must still replay).</summary>
    public static void ValidateGrantAmount(int amount)
    {
        if (amount < 1)
            throw new EnergyException(EnergyError.InvalidAmount, "The amount must be a whole number of at least 1; nothing was changed.");
    }

    /// <summary>InvalidAmount unless amount &lt;= grantMax (BLACKGLASS_ENERGY_GRANT_MAX), for a new key only.</summary>
    static void RequireWithinGrantMax(int amount, int grantMax)
    {
        if (amount > grantMax)
            throw new EnergyException(EnergyError.InvalidAmount, $"The amount must be a whole number 1 to {grantMax} (BLACKGLASS_ENERGY_GRANT_MAX) for a new grant; nothing was changed.");
    }

    /// <summary>InvalidPolicy unless the values pass 0006's CHECKs: cap &gt; 0, 0 &lt;= opening balance &lt;= cap, an interval of
    /// whole seconds from 1 s to 7 days, amount &gt;= 1, void refund 0..100 %.</summary>
    public static void ValidatePolicy(int cap, int openingBalance, TimeSpan interval, int amount, int voidRefundPct)
    {
        string? problem =
            cap < 1 ? "The cap must be at least 1."
            : openingBalance < 0 || openingBalance > cap ? "The opening balance must be 0 to the cap."
            : interval.Ticks % TimeSpan.TicksPerSecond != 0 || interval < TimeSpan.FromSeconds(1) || interval > TimeSpan.FromSeconds(MaxIntervalSeconds)
                ? $"The interval must be a whole number of seconds, 1 to {MaxIntervalSeconds} (7 days)."
            : amount < 1 ? "The amount regenerated per interval must be at least 1."
            : voidRefundPct < 0 || voidRefundPct > 100 ? "The void refund must be 0 to 100 percent."
            : null;
        if (problem is not null) throw new EnergyException(EnergyError.InvalidPolicy, problem + " Nothing was published.");
    }

    /// <summary>The ledger key of a development grant: canonical lower-case UUID text (0006's per-kind key CHECK).</summary>
    public static string DevGrantKey(Guid idempotencyKey) => "dev_grant:" + idempotencyKey.ToString("D");

    /// <summary>
    /// The accounts row FOR UPDATE (NotFound), then <see cref="EnergyProvisioner.OpenAsync"/>, then the audit
    /// <c>energy_open</c>: {opened: true, balance, policyVersion} when it opened the wallet; {opened: false, advisory: {balance,
    /// policyVersion}} when the wallet already existed (those numbers are an unlocked read, Task 3 M1, never its state).
    /// </summary>
    public async Task<WalletOpen> OpenAsync(long accountId, OperatorContext op, CancellationToken ct = default)
    {
        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await AccountAdminStore.LockAccountAsync(conn, tx, accountId, ct);
        var wallet = await EnergyProvisioner.OpenAsync(conn, tx, accountId, ct);
        await AuditAsync(conn, tx, op, "energy_open", accountId, null,
            wallet.Opened
                ? new { opened = true, balance = wallet.Balance, policyVersion = wallet.PolicyVersion }
                : (object)new { opened = false, advisory = new { balance = wallet.Balance, policyVersion = wallet.PolicyVersion } }, ct);
        await tx.CommitAsync(ct);
        return wallet;
    }

    /// <summary>
    /// A development grant (E5). amount &gt;= 1 (InvalidAmount, before any connection). One transaction:
    /// <list type="number">
    /// <item>the accounts row FOR UPDATE (NotFound), then the wallet FOR UPDATE (NoWallet);</item>
    /// <item>the key looked up across every account, before any limit meant for new grants: the same account and amount is a
    /// replay (the stored balance_after, Regenerated 0, nothing written at all: no wallet change, no entry, no audit row), even
    /// when BLACKGLASS_ENERGY_GRANT_MAX has since been lowered below the amount; anything else is IdempotencyKeyReused, with
    /// nothing written;</item>
    /// <item>for a new key only: amount &lt;= grantMax (InvalidAmount, before the materialization, so nothing is written), then
    /// <see cref="EnergyLedgerWriter.MaterializeLockedAsync"/>, <see cref="EnergyMath.Credit"/> (BalanceLimit), one wallet
    /// update of the balance, the <c>dev_grant</c> entry and the audit <c>energy_grant</c> {amount, balanceAfter, regenerated,
    /// replayed: false, idempotencyKey}.</item>
    /// </list>
    /// A concurrent first use of one key for two accounts: the second cannot see the first's uncommitted entry, so its insert
    /// waits on the global unique index and fails once the first commits; that is IdempotencyKeyReused, rolled back. The
    /// operator and reason are audit context only: a replay is matched on the account and the amount.
    /// </summary>
    public async Task<GrantResult> GrantAsync(long accountId, int amount, Guid idempotencyKey, OperatorContext op, CancellationToken ct = default)
    {
        ValidateGrantAmount(amount);
        var key = DevGrantKey(idempotencyKey);
        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await AccountAdminStore.LockAccountAsync(conn, tx, accountId, ct);
        var locked = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "select account_id from energy_wallets where account_id = @accountId for update", new { accountId }, tx, cancellationToken: ct));
        if (locked is null)
            throw new EnergyException(EnergyError.NoWallet, "This account has no energy wallet: open one with `admin energy open` first. Nothing was changed.");

        // Served by the partial unique index energy_dev_grant_key_global. This statement starts after both locks are held, so
        // it sees every grant to this account that committed before them.
        var prior = await conn.QuerySingleOrDefaultAsync<PriorGrant>(new CommandDefinition(
            "select account_id as AccountId, delta as Delta, balance_after as BalanceAfter from energy_ledger where kind = 'dev_grant' and idempotency_key = @key",
            new { key }, tx, cancellationToken: ct));
        if (prior is not null)
        {
            if (prior.AccountId != accountId || prior.Delta != amount) throw KeyReused();
            await tx.RollbackAsync(ct); // a replay writes nothing: it only releases the two row locks
            return new GrantResult(amount, prior.BalanceAfter, 0, true);
        }

        // A new key: the current maximum applies, before the materialization, so a refusal writes nothing (not even a regen entry).
        RequireWithinGrantMax(amount, grantMax);

        var materialized = await EnergyLedgerWriter.MaterializeLockedAsync(conn, tx, accountId, ct)
            ?? throw new InvalidOperationException("The locked energy wallet was not found again.");
        int balanceAfter;
        try { balanceAfter = EnergyMath.Credit(materialized.Wallet.Balance, amount); }
        catch (EnergyLimitException e) { throw new EnergyException(EnergyError.BalanceLimit, e.Message + " Nothing was changed."); }

        // Deliberately a second wallet UPDATE when regeneration accrued: the materialization wrote its own (Task 3's API); the
        // chain trigger sees the regen entry first and the deferred commit check judges the final row.
        var updated = await conn.ExecuteAsync(new CommandDefinition(
            "update energy_wallets set balance = @balanceAfter where account_id = @accountId", new { accountId, balanceAfter }, tx, cancellationToken: ct));
        if (updated != 1) throw new InvalidOperationException("The locked energy wallet was not updated exactly once.");
        try
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "insert into energy_ledger (account_id, kind, delta, balance_after, idempotency_key) values (@accountId, 'dev_grant', @amount, @balanceAfter, @key)",
                new { accountId, amount, balanceAfter, key }, tx, cancellationToken: ct));
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation && e.ConstraintName == "energy_dev_grant_key_global")
        {
            throw KeyReused(); // disposing the transaction rolls back the materialization and the credit
        }
        await AuditAsync(conn, tx, op, "energy_grant", accountId, null, new { amount, balanceAfter, regenerated = materialized.Gained, replayed = false, idempotencyKey = idempotencyKey.ToString("D") }, ct);
        await tx.CommitAsync(ct);
        return new GrantResult(amount, balanceAfter, materialized.Gained, false);
    }

    /// <summary>
    /// Publishes the next policy version (E2, E7). InvalidPolicy before any connection when a value is outside its CHECK. One
    /// short transaction, begun only after the operator confirmed: the exclusive policy lock (explicitly; 0006's trigger
    /// re-enters it), the insert (PostgreSQL mints the version and effective_at), the audit <c>energy_policy_publish</c> {version,
    /// effectiveAt, cap, openingBalance, intervalSeconds, amount, voidRefundPct} on no account.
    /// </summary>
    public async Task<EnergyPolicy> PublishPolicyAsync(int cap, int openingBalance, TimeSpan interval, int amount, int voidRefundPct, OperatorContext op, CancellationToken ct = default)
    {
        ValidatePolicy(cap, openingBalance, interval, amount, voidRefundPct);
        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "select pg_advisory_xact_lock(@key)", new { key = EnergyPolicies.PublicationLockKey }, tx, cancellationToken: ct));
        var policy = await conn.QuerySingleAsync<EnergyPolicy>(new CommandDefinition(
            """
            insert into energy_policies (cap, opening_balance, regen_interval, regen_amount, void_refund_pct)
            values (@cap, @openingBalance, @interval, @amount, @voidRefundPct)
            returning version as Version, cap as Cap, opening_balance as OpeningBalance, regen_interval as RegenInterval, regen_amount as RegenAmount,
              void_refund_pct as VoidRefundPct, effective_at as EffectiveAt
            """, new { cap, openingBalance, interval, amount, voidRefundPct }, tx, cancellationToken: ct));
        RequireUtc(policy.EffectiveAt);
        await AuditAsync(conn, tx, op, "energy_policy_publish", null, null,
            new
            {
                version = policy.Version, effectiveAt = policy.EffectiveAt, cap = policy.Cap, openingBalance = policy.OpeningBalance,
                intervalSeconds = (long)policy.RegenInterval.TotalSeconds, amount = policy.RegenAmount, voidRefundPct = policy.VoidRefundPct,
            }, ct);
        await tx.CommitAsync(ct);
        return policy;
    }

    /// <summary>Read only, for the operator's preview: the stored balance, without regeneration (no lock, nothing written), or
    /// null when the account has no wallet.</summary>
    public async Task<int?> DescribeStoredBalanceAsync(long accountId, CancellationToken ct = default)
    {
        await using var conn = await DbFailure.OpenAsync(db, ct);
        return await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
            "select balance from energy_wallets where account_id = @accountId", new { accountId }, cancellationToken: ct));
    }

    /// <summary>Read only, for the operator's preview: the latest published policy version.</summary>
    public async Task<EnergyPolicy> DescribeLatestPolicyAsync(CancellationToken ct = default)
    {
        await using var conn = await DbFailure.OpenAsync(db, ct);
        var policy = await conn.QuerySingleAsync<EnergyPolicy>(new CommandDefinition(
            """
            select version as Version, cap as Cap, opening_balance as OpeningBalance, regen_interval as RegenInterval, regen_amount as RegenAmount,
              void_refund_pct as VoidRefundPct, effective_at as EffectiveAt
            from energy_policies order by version desc limit 1
            """, cancellationToken: ct));
        RequireUtc(policy.EffectiveAt);
        return policy;
    }

    static EnergyException KeyReused() => new(EnergyError.IdempotencyKeyReused,
        "That idempotency key was already used for another grant (another account or amount): nothing was changed. Use a new key for a new grant.");

    static void RequireUtc(DateTime value)
    {
        if (value.Kind != DateTimeKind.Utc) throw new InvalidOperationException($"A timestamptz was read as {value.Kind}, not UTC.");
    }

    sealed record PriorGrant(long AccountId, int Delta, int BalanceAfter);
}
