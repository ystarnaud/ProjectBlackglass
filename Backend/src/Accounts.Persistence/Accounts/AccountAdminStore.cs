using System.Text.Json;
using Dapper;
using Npgsql;
using Blackglass.Accounts.Domain.Auth;

namespace Blackglass.Accounts.Persistence.Accounts;

public sealed record IssuedCredential(long AccountId, Guid CredentialId, OpaqueSecret AccountKey);

/// <summary>
/// The operator credential lifecycle (spec §4.2): create an account (PostgreSQL mints its id) with its optional dev
/// handle and first key, rotate, revoke a credential, revoke sessions, suspend and reactivate. Each call is one
/// transaction that locks the account row first (so operators, and logins, on one account serialize) and appends one
/// operator_actions row; a failed audit rolls the change back. Keys are returned once and never stored or recorded in
/// plain form.
/// Revocations are stamped with statement_timestamp(), not now(): now() is the transaction's start, and a call that waited
/// for a lock would otherwise stamp a credential or session revoked before it was created. statement_timestamp() is
/// itself fixed when its statement starts, so no stamping statement may wait: the rows it changes are locked first, by
/// an earlier statement (the account lock, then FOR UPDATE on the credentials and sessions; a concurrent logout holds a
/// session row). greatest(…, created_at) keeps a revocation possible even after the clock steps back: a security action
/// never fails on time.
/// </summary>
public sealed class AccountAdminStore(NpgsqlDataSource db)
{
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
        await AuditAsync(conn, tx, op, "account_create", accountId, credentialId, new { handle, displayName }, ct);
        await tx.CommitAsync(ct);
        return new IssuedCredential(accountId, credentialId, key);
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
