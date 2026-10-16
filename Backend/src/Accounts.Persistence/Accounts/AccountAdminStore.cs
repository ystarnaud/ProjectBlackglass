using System.Text.Json;
using Dapper;
using Npgsql;
using Blackglass.Accounts.Domain.Auth;
using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.Persistence.Player;

namespace Blackglass.Accounts.Persistence.Accounts;

/// <param name="Starter">What account creation provisioned; null for a rotation.</param>
public sealed record IssuedCredential(long AccountId, Guid CredentialId, OpaqueSecret AccountKey, ProvisionResult? Starter = null);

/// <summary>What an operator is about to change (the admin CLI shows it before asking for confirmation).</summary>
public sealed record AccountSummary(long AccountId, string Status, string? Handle, string? DisplayName, DateTime CreatedAt, Guid? LiveCredentialId, int LiveSessions, bool StarterGranted);

/// <summary>What one starter grant inserted. It is the grant itself, never a count of what the account holds now.</summary>
public sealed record StarterGrant(int Operatives, int Instances, int Stacks);

/// <summary>
/// The outcome of a starter backfill. <see cref="CatalogueVersion"/> is the version this call granted from, or, when the
/// account already had its starter, the version recorded at that first grant. <see cref="Grant"/> is what this call
/// inserted and is null when it granted nothing: current holdings change with every inventory edit and are never reported
/// as the starter.
/// </summary>
public sealed record BackfillResult(bool Granted, string CatalogueVersion, StarterGrant? Grant);

public sealed record CredentialSummary(Guid CredentialId, long AccountId, bool Live);

/// <summary>
/// The operator credential lifecycle (spec §4.2): create an account (PostgreSQL mints its id) with its optional dev
/// handle and first key, rotate, revoke a credential, revoke sessions, suspend and reactivate. Each call is one
/// transaction that locks the account row first (so operators, and logins, on one account serialize) and appends one
/// operator_actions row; a failed audit rolls the change back. Keys are returned once and never stored or recorded in
/// plain form.
/// Revocations are stamped with statement_timestamp(), not now(): now() is the transaction's start, and a call that waited
/// for a lock would otherwise stamp a credential or session revoked before it was created. statement_timestamp() is
/// itself fixed when its statement starts, so no revoking statement may wait: the rows it changes are locked first, by
/// an earlier statement (the account lock, then FOR UPDATE on the credentials and sessions; a concurrent logout holds a
/// session row). greatest(…, created_at) keeps a revocation possible even after the clock steps back: a security action
/// never fails on time.
/// The catalogue is read only by the calls that grant a starter (account creation and the backfill), first, before they
/// open a connection: the account-security calls never touch it, so they work whatever state it is in.
/// </summary>
public sealed class AccountAdminStore(NpgsqlDataSource db, Func<GameCatalogue> loadStarterCatalogue)
{
    /// <param name="catalogue">The catalogue starters are granted from; null means the embedded one, loaded when first needed.</param>
    public AccountAdminStore(NpgsqlDataSource db, GameCatalogue? catalogue = null)
        : this(db, catalogue is null ? static () => GameCatalogue.Embedded : () => catalogue) { }

    const string LockSessionsOfCredentials =
        "select session_id from auth_sessions where revoked_at is null and credential_id = any(@credentials) order by session_id for update";
    const string RevokeSessionsOfCredentials =
        "update auth_sessions set revoked_at = greatest(statement_timestamp(), created_at) where revoked_at is null and credential_id = any(@credentials)";

    public async Task<IssuedCredential> CreateAccountAsync(string? handle, string? displayName, OperatorContext op, CancellationToken ct = default)
    {
        if (handle is not null && !AccountHandle.IsValid(handle))
            throw new AccountAdminException(AccountAdminError.InvalidHandle, "A handle must be lowercase letters and digits separated by single '.' or '-', at most 64 characters.");
        if (displayName is not null && !DisplayName.IsValid(displayName))
            throw new AccountAdminException(AccountAdminError.InvalidDisplayName, "A display name must be 1 to 64 characters, without control characters or surrounding spaces.");
        var starterCatalogue = loadStarterCatalogue();
        var credentialId = Guid.CreateVersion7();
        var key = OpaqueSecret.New(OpaqueSecret.AccountKeyPrefix);
        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        long accountId;
        try
        {
            accountId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                "insert into accounts (status, display_name) values ('active', @displayName) returning account_id", new { displayName }, tx, cancellationToken: ct));
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.CheckViolation && e.ConstraintName == "accounts_display_name_check")
        {
            // DisplayName.IsValid and the column check should agree; where they don't (e.g. a Unicode class), it's still bad input.
            throw new AccountAdminException(AccountAdminError.InvalidDisplayName, "The database refused that display name.");
        }
        if (handle is not null)
        {
            try
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "insert into account_identities (provider, subject, account_id) values ('dev', @handle, @accountId)", new { handle, accountId }, tx, cancellationToken: ct));
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation && e.ConstraintName == "account_identities_pkey")
            {
                throw new AccountAdminException(AccountAdminError.HandleTaken, "That handle is already taken."); // disposing tx rolls back
            }
        }
        await InsertCredentialAsync(conn, tx, accountId, credentialId, key, ct);
        // The starter is part of the same transaction (owner ruling D4): any failure rolls the account back with it.
        var starter = await StarterProvisioner.ProvisionAsync(conn, tx, accountId, starterCatalogue, ct);
        await AuditAsync(conn, tx, op, "account_create", accountId, credentialId,
            new { handle, displayName, starter = new { catalogueVersion = starter.CatalogueVersion, operatives = starter.Operatives, instances = starter.Instances, stacks = starter.Stacks } }, ct);
        await tx.CommitAsync(ct);
        return new IssuedCredential(accountId, credentialId, key, starter);
    }

    public async Task<IssuedCredential> RotateCredentialAsync(long accountId, OperatorContext op, CancellationToken ct = default)
    {
        var credentialId = Guid.CreateVersion7();
        var key = OpaqueSecret.New(OpaqueSecret.AccountKeyPrefix);
        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await LockAccountAsync(conn, tx, accountId, ct);
        var old = (await conn.QueryAsync<Guid>(new CommandDefinition(
            "select credential_id from dev_credentials where account_id = @accountId and revoked_at is null for update", new { accountId }, tx, cancellationToken: ct))).ToArray();
        await conn.ExecuteAsync(new CommandDefinition(LockSessionsOfCredentials, new { credentials = old }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "update dev_credentials set revoked_at = greatest(statement_timestamp(), created_at) where credential_id = any(@old) and revoked_at is null",
            new { old }, tx, cancellationToken: ct));
        var sessions = await conn.ExecuteAsync(new CommandDefinition(RevokeSessionsOfCredentials, new { credentials = old }, tx, cancellationToken: ct));
        await InsertCredentialAsync(conn, tx, accountId, credentialId, key, ct);
        await AuditAsync(conn, tx, op, "credential_rotate", accountId, credentialId, new { revokedCredentials = old, revokedSessions = sessions }, ct);
        await tx.CommitAsync(ct);
        return new IssuedCredential(accountId, credentialId, key);
    }

    /// <summary>Revokes one credential and every session it created; returns the number of sessions revoked. Revoking an
    /// already revoked credential changes nothing but is still recorded.</summary>
    public async Task<int> RevokeCredentialAsync(Guid credentialId, OperatorContext op, CancellationToken ct = default)
    {
        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        // A credential never moves to another account (primary key plus the app's column privileges), so reading its
        // account before taking the account lock is safe; the lock then serializes against the other lifecycle calls.
        var accountId = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "select account_id from dev_credentials where credential_id = @credentialId", new { credentialId }, tx, cancellationToken: ct))
            ?? throw new AccountAdminException(AccountAdminError.NotFound, "No such credential.");
        await LockAccountAsync(conn, tx, accountId, ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "select credential_id from dev_credentials where credential_id = @credentialId and revoked_at is null for update", new { credentialId }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(LockSessionsOfCredentials, new { credentials = new[] { credentialId } }, tx, cancellationToken: ct));
        var revoked = await conn.ExecuteAsync(new CommandDefinition(
            "update dev_credentials set revoked_at = greatest(statement_timestamp(), created_at) where credential_id = @credentialId and revoked_at is null", new { credentialId }, tx, cancellationToken: ct));
        var sessions = await conn.ExecuteAsync(new CommandDefinition(RevokeSessionsOfCredentials, new { credentials = new[] { credentialId } }, tx, cancellationToken: ct));
        await AuditAsync(conn, tx, op, "credential_revoke", accountId, credentialId, new { alreadyRevoked = revoked == 0, revokedSessions = sessions }, ct);
        await tx.CommitAsync(ct);
        return sessions;
    }

    public async Task<int> RevokeSessionsAsync(long accountId, OperatorContext op, CancellationToken ct = default)
    {
        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await LockAccountAsync(conn, tx, accountId, ct);
        var sessions = await RevokeAllSessionsAsync(conn, tx, accountId, ct);
        await AuditAsync(conn, tx, op, "sessions_revoke", accountId, null, new { revokedSessions = sessions }, ct);
        await tx.CommitAsync(ct);
        return sessions;
    }

    /// <summary>Suspends the account and revokes every live session; returns the number of sessions revoked.</summary>
    public async Task<int> SuspendAsync(long accountId, OperatorContext op, CancellationToken ct = default)
    {
        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await LockAccountAsync(conn, tx, accountId, ct);
        await conn.ExecuteAsync(new CommandDefinition("update accounts set status = 'suspended' where account_id = @accountId", new { accountId }, tx, cancellationToken: ct));
        var sessions = await RevokeAllSessionsAsync(conn, tx, accountId, ct);
        await AuditAsync(conn, tx, op, "account_suspend", accountId, null, new { revokedSessions = sessions }, ct);
        await tx.CommitAsync(ct);
        return sessions;
    }

    /// <summary>Reactivates the account. Sessions revoked earlier stay revoked; the live credential works again.</summary>
    public async Task ReactivateAsync(long accountId, OperatorContext op, CancellationToken ct = default)
    {
        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await LockAccountAsync(conn, tx, accountId, ct);
        await conn.ExecuteAsync(new CommandDefinition("update accounts set status = 'active' where account_id = @accountId", new { accountId }, tx, cancellationToken: ct));
        await AuditAsync(conn, tx, op, "account_reactivate", accountId, null, new { }, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// The explicit starter backfill for an account created before Stage 4 (owner ruling D4): grants the starter once, from
    /// this store's catalogue, and records that catalogue's version. Under the account lock, then the inventories row lock
    /// (inside <see cref="StarterProvisioner"/>), so concurrent backfills, and account creation, serialize: one grants, the
    /// rest find it granted. An account that already has its starter is left exactly as it is, whatever catalogue this
    /// store holds: a later catalogue never grants again or replaces a starter. The audit row is written either way
    /// (granted true with the catalogue version and the grant's counts; or granted false with the recorded catalogue version and
    /// no counts, since the account's current holdings are not the grant); a failure anywhere rolls everything back.
    /// A suspended account is backfilled too: the starter is property, not access.
    /// </summary>
    public async Task<BackfillResult> BackfillStarterAsync(long accountId, OperatorContext op, CancellationToken ct = default)
    {
        var starterCatalogue = loadStarterCatalogue();
        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await LockAccountAsync(conn, tx, accountId, ct);
        // When already granted, the provisioner returns the version recorded in the inventories row it holds FOR UPDATE; the
        // counts it also returns then describe current holdings, so they are dropped here and never reach a result or the audit.
        var state = await StarterProvisioner.ProvisionAsync(conn, tx, accountId, starterCatalogue, ct);
        var result = state.Granted
            ? new BackfillResult(true, state.CatalogueVersion, new StarterGrant(state.Operatives, state.Instances, state.Stacks))
            : new BackfillResult(false, state.CatalogueVersion, null);
        await AuditAsync(conn, tx, op, "starter_backfill", accountId, null,
            result.Grant is { } grant
                ? (object)new { granted = true, catalogueVersion = result.CatalogueVersion, operatives = grant.Operatives, instances = grant.Instances, stacks = grant.Stacks }
                : new { granted = false, catalogueVersion = result.CatalogueVersion }, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    /// <summary>Read only: the account an operator names, or null.</summary>
    public async Task<AccountSummary?> DescribeAccountAsync(long accountId, CancellationToken ct = default)
    {
        await using var conn = await DbFailure.OpenAsync(db, ct);
        return await conn.QuerySingleOrDefaultAsync<AccountSummary>(new CommandDefinition(
            """
            select a.account_id as AccountId, a.status as Status, i.subject as Handle, a.display_name as DisplayName, a.created_at as CreatedAt,
              (select c.credential_id from dev_credentials c where c.account_id = a.account_id and c.revoked_at is null) as LiveCredentialId,
              (select count(*)::int from auth_sessions s where s.account_id = a.account_id and s.revoked_at is null and s.expires_at > now()) as LiveSessions,
              coalesce((select v.starter_granted from inventories v where v.account_id = a.account_id), false) as StarterGranted
            from accounts a left join account_identities i on i.account_id = a.account_id and i.provider = 'dev'
            where a.account_id = @accountId
            """, new { accountId }, cancellationToken: ct));
    }

    /// <summary>Read only: the credential an operator names, or null.</summary>
    public async Task<CredentialSummary?> DescribeCredentialAsync(Guid credentialId, CancellationToken ct = default)
    {
        await using var conn = await DbFailure.OpenAsync(db, ct);
        return await conn.QuerySingleOrDefaultAsync<CredentialSummary>(new CommandDefinition(
            "select credential_id as CredentialId, account_id as AccountId, revoked_at is null as Live from dev_credentials where credential_id = @credentialId",
            new { credentialId }, cancellationToken: ct));
    }

    static async Task LockAccountAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long accountId, CancellationToken ct)
    {
        var found = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "select account_id from accounts where account_id = @accountId for update", new { accountId }, tx, cancellationToken: ct));
        if (found is null) throw new AccountAdminException(AccountAdminError.NotFound, "No such account.");
    }

    static async Task<int> RevokeAllSessionsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long accountId, CancellationToken ct)
    {
        await conn.ExecuteAsync(new CommandDefinition(
            "select session_id from auth_sessions where account_id = @accountId and revoked_at is null order by session_id for update", new { accountId }, tx, cancellationToken: ct));
        return await conn.ExecuteAsync(new CommandDefinition(
            "update auth_sessions set revoked_at = greatest(statement_timestamp(), created_at) where account_id = @accountId and revoked_at is null", new { accountId }, tx, cancellationToken: ct));
    }

    static Task InsertCredentialAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long accountId, Guid credentialId, OpaqueSecret key, CancellationToken ct) =>
        conn.ExecuteAsync(new CommandDefinition(
            "insert into dev_credentials (credential_id, account_id, secret_hash) values (@credentialId, @accountId, @hash)",
            new { credentialId, accountId, hash = key.Hash() }, tx, cancellationToken: ct));

    static async Task AuditAsync(NpgsqlConnection conn, NpgsqlTransaction tx, OperatorContext op, string action, long accountId, Guid? credentialId, object result, CancellationToken ct)
    {
        try
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                insert into operator_actions (correlation_id, operator, action, account_id, credential_id, reason, checks, result)
                values (@correlationId, @operator, @action, @accountId, @credentialId, @reason, '{}'::jsonb, @result::jsonb)
                """,
                new { correlationId = op.CorrelationId, @operator = op.Operator, action, accountId, credentialId, reason = op.Reason, result = JsonSerializer.Serialize(result) },
                tx, cancellationToken: ct));
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.CheckViolation && e.ConstraintName is "operator_actions_operator_check" or "operator_actions_reason_check")
        {
            // OperatorContext.Create and the column checks should agree; where they don't, it's bad input and the change rolls back.
            throw new AccountAdminException(AccountAdminError.InvalidOperator, "The database refused that operator name or reason.");
        }
    }
}
