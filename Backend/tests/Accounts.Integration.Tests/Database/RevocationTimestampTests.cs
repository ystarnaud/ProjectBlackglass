using Dapper;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Accounts;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>
/// statement_timestamp() is fixed when a statement starts, so a statement that itself waits for a lock would stamp its
/// rows before the wait. Every revocation and creation time must come after every lock its call waited for: the account
/// lock (each lifecycle call), and the row locks on the credentials and sessions it revokes (a concurrent logout holds
/// one). Each test holds the lock from another connection, notes the database clock just before releasing it, and checks
/// every stamp is at or after that moment. The blocker rolls back, the case where PostgreSQL applies the row it
/// computed before waiting (so clock_timestamp() in the UPDATE would not help either).
/// </summary>
[Trait("Requires", "Postgres")]
public class RevocationTimestampTests : IAsyncLifetime
{
    TestDatabase db = null!;
    AccountAdminStore admin = null!;
    SessionStore sessions = null!;

    public async Task InitializeAsync()
    {
        db = await MigratedAsync();
        admin = new AccountAdminStore(db.AsApp());
        sessions = new SessionStore(db.AsApp());
    }

    public async Task DisposeAsync()
    {
        try { Assert.Empty(await InvariantFailuresAsync(db)); }
        finally { await db.DisposeAsync(); }
    }

    Task Call(string how, IssuedCredential account, IssuedSession session) => how switch
    {
        "rotate" => admin.RotateCredentialAsync(account.AccountId, Op),
        "revoke-credential" => admin.RevokeCredentialAsync(account.CredentialId, Op),
        "revoke-sessions" => admin.RevokeSessionsAsync(account.AccountId, Op),
        "suspend" => admin.SuspendAsync(account.AccountId, Op),
        "reactivate" => admin.ReactivateAsync(account.AccountId, Op),
        "logout" => sessions.RevokeAsync(session.SessionId, account.AccountId),
        _ => throw new ArgumentOutOfRangeException(nameof(how)),
    };

    /// <summary>Runs the call while another transaction holds <paramref name="lockSql"/>, and returns the database
    /// clock just before that transaction rolled back.</summary>
    async Task<DateTime> RunBlockedAsync(string lockSql, object args, Func<Task> call)
    {
        await using var conn = await db.AsApp().OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        Assert.Single(await conn.QueryAsync(lockSql, args, tx));
        var running = call();
        await WaitUntilABackendWaitsOnALockAsync(db);
        Assert.False(running.IsCompleted);
        var released = await conn.ExecuteScalarAsync<DateTime>("select clock_timestamp()", transaction: tx);
        await tx.RollbackAsync();
        await running;
        return released;
    }

    /// <summary>Every revocation, every credential but the account's first, and every audit row but the creation's.</summary>
    async Task AssertEveryStampIsAtOrAfter(DateTime released, IssuedCredential account)
    {
        await using var su = await db.AsSuperuser().OpenConnectionAsync();
        var stamps = (await su.QueryAsync<(string What, DateTime At)>("""
            select 'credential revoked_at', revoked_at from dev_credentials where account_id = @a and revoked_at is not null
            union all select 'credential created_at', created_at from dev_credentials where account_id = @a and credential_id <> @first
            union all select 'session revoked_at', revoked_at from auth_sessions where account_id = @a and revoked_at is not null
            union all select 'audit created_at', created_at from operator_actions where account_id = @a and action <> 'account_create'
            """, new { a = account.AccountId, first = account.CredentialId })).ToList();
        Assert.NotEmpty(stamps);
        foreach (var (what, at) in stamps) Assert.True(at >= released, $"{what} {at:O} precedes the lock release {released:O}");
    }

    async Task<(IssuedCredential Account, IssuedSession Session)> AccountWithSessionAsync()
    {
        var account = await admin.CreateAccountAsync(null, null, Op);
        return (account, (await sessions.LoginAsync(account.AccountKey.Hash(), 60))!);
    }

    [Theory]
    [InlineData("rotate")]
    [InlineData("revoke-credential")]
    [InlineData("revoke-sessions")]
    [InlineData("suspend")]
    [InlineData("reactivate")]
    public async Task A_lifecycle_call_that_waited_for_the_account_lock_stamps_everything_after_the_wait(string how)
    {
        var (account, session) = await AccountWithSessionAsync();
        var released = await RunBlockedAsync("select account_id from accounts where account_id = @a for update", new { a = account.AccountId }, () => Call(how, account, session));
        await AssertEveryStampIsAtOrAfter(released, account);
    }

    [Theory]
    [InlineData("rotate")]
    [InlineData("revoke-credential")]
    [InlineData("revoke-sessions")]
    [InlineData("suspend")]
    [InlineData("logout")]
    public async Task A_revocation_that_waited_for_a_session_row_lock_stamps_the_session_after_the_wait(string how)
    {
        var (account, session) = await AccountWithSessionAsync();
        var released = await RunBlockedAsync("select session_id from auth_sessions where session_id = @s for update", new { s = session.SessionId }, () => Call(how, account, session));
        await AssertEveryStampIsAtOrAfter(released, account);
    }

    [Theory]
    [InlineData("rotate")]
    [InlineData("revoke-credential")]
    public async Task A_revocation_that_waited_for_a_credential_row_lock_stamps_after_the_wait(string how)
    {
        var (account, session) = await AccountWithSessionAsync();
        var released = await RunBlockedAsync("select credential_id from dev_credentials where credential_id = @c for update", new { c = account.CredentialId }, () => Call(how, account, session));
        await AssertEveryStampIsAtOrAfter(released, account);
    }
}
