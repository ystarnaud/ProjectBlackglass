using Dapper;
using Npgsql;
using Blackglass.Accounts.Domain.Auth;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Accounts;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

[Trait("Requires", "Postgres")]
public class SessionStoreTests : IAsyncLifetime
{
    TestDatabase db = null!;
    AccountAdminStore admin = null!;
    SessionStore sessions = null!;

    public async Task InitializeAsync() { db = await MigratedAsync(); admin = new AccountAdminStore(db.AsApp()); sessions = new SessionStore(db.AsApp()); }

    public async Task DisposeAsync()
    {
        Assert.Empty(await InvariantFailuresAsync(db));
        await db.DisposeAsync();
    }

    [Fact]
    public async Task A_valid_key_logs_in_and_the_token_authenticates_the_same_account()
    {
        var account = await admin.CreateAccountAsync("login-1", null, Op);
        var session = await sessions.LoginAsync(account.AccountKey.Hash(), 60);
        Assert.NotNull(session);
        Assert.Equal(account.AccountId, session!.AccountId);
        Assert.Equal(account.CredentialId, session.CredentialId);
        Assert.Equal(7, session.SessionId.Version);
        Assert.InRange(session.ExpiresAt - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(59), TimeSpan.FromMinutes(61));
        Assert.Equal(TimeSpan.Zero, session.ExpiresAt.Offset);
        var authenticated = await sessions.AuthenticateAsync(session.AccessToken.Hash());
        Assert.Equal((session.SessionId, account.AccountId), (authenticated!.SessionId, authenticated.AccountId));
        Assert.Equal(session.AccessToken.Hash(), await ScalarAsSuperuserAsync<byte[]>(db, "select token_hash from auth_sessions where session_id = @s", new { s = session.SessionId }));
    }

    [Fact]
    public async Task An_unknown_key_does_not_log_in() =>
        Assert.Null(await sessions.LoginAsync(OpaqueSecret.New("dk1").Hash(), 60));

    [Fact]
    public async Task An_unknown_token_does_not_authenticate() =>
        Assert.Null(await sessions.AuthenticateAsync(OpaqueSecret.New("at1").Hash()));

    [Fact]
    public async Task A_revoked_key_does_not_log_in()
    {
        var account = await admin.CreateAccountAsync("login-2", null, Op);
        await admin.RevokeCredentialAsync(account.CredentialId, Op);
        Assert.Null(await sessions.LoginAsync(account.AccountKey.Hash(), 60));
    }

    [Fact]
    public async Task A_rotated_key_no_longer_logs_in_and_the_new_one_does()
    {
        var account = await admin.CreateAccountAsync(null, null, Op);
        var rotated = await admin.RotateCredentialAsync(account.AccountId, Op);
        Assert.Null(await sessions.LoginAsync(account.AccountKey.Hash(), 60));
        Assert.Equal(rotated.CredentialId, (await sessions.LoginAsync(rotated.AccountKey.Hash(), 60))!.CredentialId);
    }

    [Fact]
    public async Task A_suspended_account_does_not_log_in()
    {
        var account = await admin.CreateAccountAsync("login-3", null, Op);
        await admin.SuspendAsync(account.AccountId, Op);
        Assert.Null(await sessions.LoginAsync(account.AccountKey.Hash(), 60));
    }

    [Fact]
    public async Task An_expired_token_does_not_authenticate()
    {
        var account = await admin.CreateAccountAsync("expiry", null, Op);
        var session = (await sessions.LoginAsync(account.AccountKey.Hash(), 60))!;
        await ExecuteAsSuperuserAsync(db, "update auth_sessions set created_at = now() - interval '2 hours', expires_at = now() - interval '1 second' where session_id = @s", new { s = session.SessionId });
        Assert.Null(await sessions.AuthenticateAsync(session.AccessToken.Hash()));
    }

    [Fact]
    public async Task The_lifetime_is_the_configured_number_of_minutes_on_the_database_clock()
    {
        var account = await admin.CreateAccountAsync(null, null, Op);
        var session = (await sessions.LoginAsync(account.AccountKey.Hash(), 5))!;
        Assert.Equal(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(await ScalarAsSuperuserAsync<double>(db,
            "select extract(epoch from expires_at - created_at) from auth_sessions where session_id = @s", new { s = session.SessionId })));
    }

    [Fact]
    public async Task Logout_revokes_the_session_once_and_only_for_its_own_account()
    {
        var account = await admin.CreateAccountAsync("logout", null, Op);
        var other = await admin.CreateAccountAsync(null, null, Op);
        var session = (await sessions.LoginAsync(account.AccountKey.Hash(), 60))!;
        Assert.False(await sessions.RevokeAsync(session.SessionId, other.AccountId)); // the session id alone is not enough
        Assert.NotNull(await sessions.AuthenticateAsync(session.AccessToken.Hash()));
        Assert.True(await sessions.RevokeAsync(session.SessionId, account.AccountId));
        Assert.False(await sessions.RevokeAsync(session.SessionId, account.AccountId));
        Assert.Null(await sessions.AuthenticateAsync(session.AccessToken.Hash()));
    }

    [Fact]
    public async Task Logout_revokes_only_the_current_session()
    {
        var account = await admin.CreateAccountAsync(null, null, Op);
        var first = (await sessions.LoginAsync(account.AccountKey.Hash(), 60))!;
        var second = (await sessions.LoginAsync(account.AccountKey.Hash(), 60))!;
        await sessions.RevokeAsync(first.SessionId, account.AccountId);
        Assert.Null(await sessions.AuthenticateAsync(first.AccessToken.Hash()));
        Assert.NotNull(await sessions.AuthenticateAsync(second.AccessToken.Hash()));
    }

    [Fact]
    public async Task Reactivation_does_not_restore_revoked_sessions()
    {
        var account = await admin.CreateAccountAsync("revive", null, Op);
        var session = (await sessions.LoginAsync(account.AccountKey.Hash(), 60))!;
        await admin.SuspendAsync(account.AccountId, Op);
        await admin.ReactivateAsync(account.AccountId, Op);
        Assert.Null(await sessions.AuthenticateAsync(session.AccessToken.Hash()));
        Assert.NotNull(await sessions.LoginAsync(account.AccountKey.Hash(), 60)); // the key itself still works after reactivation
    }

    [Theory]
    [InlineData("update accounts set status = 'suspended' where account_id = @a")]
    [InlineData("update dev_credentials set revoked_at = now() where account_id = @a")]
    public async Task Authentication_also_refuses_a_session_whose_credential_or_account_was_changed_directly(string sql)
    {
        // Belt and braces: the cascade is the stores' job, but authentication checks the credential and the account too.
        var account = await admin.CreateAccountAsync(null, null, Op);
        var session = (await sessions.LoginAsync(account.AccountKey.Hash(), 60))!;
        await ExecuteAsSuperuserAsync(db, sql, new { a = account.AccountId });
        Assert.Null(await sessions.AuthenticateAsync(session.AccessToken.Hash()));
        await ExecuteAsSuperuserAsync(db, "update auth_sessions set revoked_at = now()"); // keep the cascade invariant for DisposeAsync
    }

    [Fact]
    public async Task Concurrent_logins_with_one_key_create_independent_sessions()
    {
        var account = await admin.CreateAccountAsync("parallel", null, Op);
        var issued = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => sessions.LoginAsync(account.AccountKey.Hash(), 60)));
        Assert.Equal(20, issued.Select(s => s!.SessionId).Distinct().Count());
        Assert.All(issued, s => Assert.Equal(account.CredentialId, s!.CredentialId));
        foreach (var s in issued) Assert.NotNull(await sessions.AuthenticateAsync(s!.AccessToken.Hash()));
    }

    /// <summary>
    /// Review Focus 6, deterministic: an operator transaction holds the account lock (as every lifecycle call does first),
    /// a login starts and must wait for it, the operator revokes and commits, and the login then fails. Without the
    /// login's locks it would not wait, and would commit a live session on the credential being revoked.
    /// </summary>
    [Theory]
    [InlineData("rotate")]
    [InlineData("revoke-credential")]
    [InlineData("suspend")]
    public async Task Login_waits_for_an_in_flight_revocation_and_then_fails(string how)
    {
        var account = await admin.CreateAccountAsync(null, null, Op);
        await using var conn = await db.AsApp().OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync("select account_id from accounts where account_id = @a for update", new { a = account.AccountId }, tx);

        var login = sessions.LoginAsync(account.AccountKey.Hash(), 60);
        await WaitUntilABackendWaitsOnALockAsync();
        Assert.False(login.IsCompleted);

        if (how == "suspend")
            await conn.ExecuteAsync("update accounts set status = 'suspended' where account_id = @a", new { a = account.AccountId }, tx);
        else
            await conn.ExecuteAsync("update dev_credentials set revoked_at = statement_timestamp() where account_id = @a", new { a = account.AccountId }, tx);
        await conn.ExecuteAsync("update auth_sessions set revoked_at = statement_timestamp() where account_id = @a and revoked_at is null", new { a = account.AccountId }, tx);
        await tx.CommitAsync();

        Assert.Null(await login);
        Assert.Equal(0L, await ScalarAsSuperuserAsync<long>(db, "select count(*) from auth_sessions"));
    }

    [Fact]
    public async Task Concurrent_logins_and_rotations_leave_no_live_session_on_a_revoked_credential()
    {
        var account = await admin.CreateAccountAsync("churn", null, Op);
        var key = account.AccountKey;
        var issued = new List<IssuedSession?>();
        for (var round = 0; round < 10; round++)
        {
            var logins = Enumerable.Range(0, 4).Select(_ => sessions.LoginAsync(key.Hash(), 60)).ToArray();
            var rotation = admin.RotateCredentialAsync(account.AccountId, Op);
            await Task.WhenAll(logins.Cast<Task>().Append(rotation));
            issued.AddRange(await Task.WhenAll(logins));
            key = (await rotation).AccountKey;
        }
        Assert.Empty(await InvariantFailuresAsync(db));
        foreach (var s in issued.Where(s => s is not null))
            Assert.Null(await sessions.AuthenticateAsync(s!.AccessToken.Hash())); // every key they used has since been rotated
    }

    [Fact]
    public async Task Concurrent_logins_and_a_suspension_leave_no_live_session()
    {
        var account = await admin.CreateAccountAsync(null, null, Op);
        var logins = Enumerable.Range(0, 20).Select(_ => sessions.LoginAsync(account.AccountKey.Hash(), 60)).ToArray();
        var suspension = admin.SuspendAsync(account.AccountId, Op);
        await Task.WhenAll(logins.Cast<Task>().Append(suspension));
        Assert.Empty(await InvariantFailuresAsync(db));
        foreach (var s in (await Task.WhenAll(logins)).Where(s => s is not null))
            Assert.Null(await sessions.AuthenticateAsync(s!.AccessToken.Hash()));
        Assert.Null(await sessions.LoginAsync(account.AccountKey.Hash(), 60));
    }

    [Fact]
    public async Task Concurrent_logins_and_a_credential_revocation_leave_no_live_session()
    {
        var account = await admin.CreateAccountAsync(null, null, Op);
        var logins = Enumerable.Range(0, 20).Select(_ => sessions.LoginAsync(account.AccountKey.Hash(), 60)).ToArray();
        var revocation = admin.RevokeCredentialAsync(account.CredentialId, Op);
        await Task.WhenAll(logins.Cast<Task>().Append(revocation));
        Assert.Empty(await InvariantFailuresAsync(db));
        foreach (var s in (await Task.WhenAll(logins)).Where(s => s is not null))
            Assert.Null(await sessions.AuthenticateAsync(s!.AccessToken.Hash()));
    }

    [Fact]
    public async Task Once_logout_returns_no_concurrent_or_later_authentication_succeeds()
    {
        var account = await admin.CreateAccountAsync(null, null, Op);
        var session = (await sessions.LoginAsync(account.AccountKey.Hash(), 60))!;
        var during = Enumerable.Range(0, 20).Select(_ => sessions.AuthenticateAsync(session.AccessToken.Hash())).ToArray();
        Assert.True(await sessions.RevokeAsync(session.SessionId, account.AccountId));
        await Task.WhenAll(during); // each either ran before the revocation committed or was refused; none may fail
        for (var i = 0; i < 5; i++) Assert.Null(await sessions.AuthenticateAsync(session.AccessToken.Hash()));
    }

    async Task WaitUntilABackendWaitsOnALockAsync()
    {
        await using var su = await db.AsSuperuser().OpenConnectionAsync();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (await su.ExecuteScalarAsync<long>(
                   "select count(*) from pg_stat_activity where datname = current_database() and wait_event_type = 'Lock'") == 0)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("No login waited on the account lock: the login takes no lock.");
            await Task.Yield();
        }
    }
}
