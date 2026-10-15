using Dapper;
using Npgsql;
using Blackglass.Accounts.Domain.Auth;

namespace Blackglass.Accounts.Persistence.Accounts;

public sealed record IssuedSession(Guid SessionId, long AccountId, Guid CredentialId, DateTimeOffset ExpiresAt, OpaqueSecret AccessToken);

public sealed record AuthenticatedSession(Guid SessionId, long AccountId);

/// <summary>
/// Development login and token authentication (spec §4.2). Every check runs in PostgreSQL against its clock: an expired,
/// revoked, or suspended-account session is refused by the same query on every request, with no cache, so revocation is
/// immediate. Only SHA-256 hashes cross this boundary inward.
/// </summary>
public sealed class SessionStore(NpgsqlDataSource db)
{
    // Login is one transaction that locks in the lifecycle's order: the account row, then the credential row.
    // Every AccountAdminStore call locks the account row FOR UPDATE first, so a login and a rotation, revocation or
    // suspension of the same account serialize: a login that waited re-reads the committed revocation and fails, and a
    // session committed first is revoked by the revocation that follows. The session's foreign key alone isn't enough:
    // its FOR KEY SHARE doesn't conflict with the revocation's FOR NO KEY UPDATE.
    const string FindAccountSql = "select account_id from dev_credentials where secret_hash = @keyHash";
    const string LockAccountSql = "select status from accounts where account_id = @accountId for share";
    const string LockCredentialSql =
        "select credential_id from dev_credentials where secret_hash = @keyHash and account_id = @accountId and revoked_at is null for share";
    const string InsertSessionSql = """
        insert into auth_sessions (session_id, account_id, credential_id, token_hash, expires_at)
        values (@sessionId, @accountId, @credentialId, @tokenHash, statement_timestamp() + make_interval(mins => @minutes))
        returning expires_at
        """;

    const string AuthenticateSql = """
        select s.session_id as SessionId, s.account_id as AccountId
        from auth_sessions s
        join dev_credentials c on c.credential_id = s.credential_id
        join accounts a on a.account_id = s.account_id
        where s.token_hash = @tokenHash and s.revoked_at is null and s.expires_at > now()
          and c.revoked_at is null and a.status = 'active'
        """;

    /// <summary>Exchanges a development key's hash for a new session; null for an unknown or revoked key or a suspended
    /// account, all alike.</summary>
    public async Task<IssuedSession?> LoginAsync(byte[] accountKeyHash, int lifetimeMinutes, CancellationToken ct = default)
    {
        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var accountId = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(FindAccountSql, new { keyHash = accountKeyHash }, tx, cancellationToken: ct));
        if (accountId is null) return null;
        var status = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(LockAccountSql, new { accountId }, tx, cancellationToken: ct));
        if (status != "active") return null;
        var credentialId = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(LockCredentialSql, new { keyHash = accountKeyHash, accountId }, tx, cancellationToken: ct));
        if (credentialId is null) return null;
        var sessionId = Guid.CreateVersion7();
        var token = OpaqueSecret.New(OpaqueSecret.AccessTokenPrefix);
        var expiresAt = await conn.ExecuteScalarAsync<DateTime>(new CommandDefinition(InsertSessionSql,
            new { sessionId, accountId, credentialId, tokenHash = token.Hash(), minutes = lifetimeMinutes }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return new IssuedSession(sessionId, accountId.Value, credentialId.Value, Utc(expiresAt), token);
    }

    public async Task<AuthenticatedSession?> AuthenticateAsync(byte[] tokenHash, CancellationToken ct = default)
    {
        await using var conn = await DbFailure.OpenAsync(db, ct);
        return await conn.QuerySingleOrDefaultAsync<AuthenticatedSession>(new CommandDefinition(AuthenticateSql, new { tokenHash }, cancellationToken: ct));
    }

    /// <summary>Logout: revokes one live session of one account. False when it was already revoked or isn't that
    /// account's. The row is locked by its own statement first, so the stamp (fixed when its statement starts) comes
    /// after any wait, e.g. behind an operator revoking the same session.</summary>
    public async Task<bool> RevokeAsync(Guid sessionId, long accountId, CancellationToken ct = default)
    {
        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var locked = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "select session_id from auth_sessions where session_id = @sessionId and account_id = @accountId and revoked_at is null for update",
            new { sessionId, accountId }, tx, cancellationToken: ct));
        if (locked is null) return false;
        await conn.ExecuteAsync(new CommandDefinition(
            "update auth_sessions set revoked_at = greatest(statement_timestamp(), created_at) where session_id = @sessionId",
            new { sessionId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return true;
    }

    static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
