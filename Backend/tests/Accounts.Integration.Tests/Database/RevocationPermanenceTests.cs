using Dapper;
using Npgsql;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Accounts;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>
/// Migration 0002: a revocation is permanent. The app role may set revoked_at on a live credential or session (its
/// only UPDATE grant on those tables), but never clear or re-date it, whatever SQL it runs; and it can't switch the
/// guard off. Issuance, rotation, logout, suspension and reactivation are unaffected.
/// </summary>
[Trait("Requires", "Postgres")]
public class RevocationPermanenceTests : IAsyncLifetime
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

    public async Task DisposeAsync() => await db.DisposeAsync();

    async Task<PostgresException> RefusedAsApp(string sql, object? args = null)
    {
        await using var conn = await db.AsApp().OpenConnectionAsync();
        return await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(sql, args));
    }

    Task<long> CountAsync(string sql, object? args = null) => ScalarAsSuperuserAsync<long>(db, sql, args);

    async Task<(IssuedCredential Account, IssuedSession Session)> RevokedAsync()
    {
        var account = await admin.CreateAccountAsync(null, null, Op);
        var session = (await sessions.LoginAsync(account.AccountKey.Hash(), 60))!;
        await admin.RotateCredentialAsync(account.AccountId, Op); // revokes the credential and its session
        return (account, session);
    }

    [Theory]
    [InlineData("update dev_credentials set revoked_at = null where credential_id = @credential")]
    [InlineData("update dev_credentials set revoked_at = revoked_at + interval '1 second' where credential_id = @credential")]
    [InlineData("update dev_credentials set revoked_at = created_at where credential_id = @credential")]
    [InlineData("update auth_sessions set revoked_at = null where session_id = @session")]
    [InlineData("update auth_sessions set revoked_at = revoked_at + interval '1 day' where session_id = @session")]
    [InlineData("update auth_sessions set revoked_at = case when true then null::timestamptz end where session_id = @session")]
    public async Task The_app_role_cannot_clear_or_redate_a_revocation(string sql)
    {
        var (account, session) = await RevokedAsync();
        var e = await RefusedAsApp(sql, new { credential = account.CredentialId, session = session.SessionId });
        Assert.Equal((PostgresErrorCodes.CheckViolation, "revocation_is_final"), (e.SqlState, e.ConstraintName));
        Assert.Equal(0L, await CountAsync("select count(*) from dev_credentials where credential_id = @c and revoked_at is null", new { c = account.CredentialId }));
        Assert.Equal(0L, await CountAsync("select count(*) from auth_sessions where session_id = @s and revoked_at is null", new { s = session.SessionId }));
    }

    [Fact]
    public async Task A_bulk_unrevoke_fails_as_a_whole_and_changes_nothing()
    {
        var (revoked, _) = await RevokedAsync();
        var live = (await sessions.LoginAsync((await admin.RotateCredentialAsync(revoked.AccountId, Op)).AccountKey.Hash(), 60))!;
        await RefusedAsApp("update auth_sessions set revoked_at = null");
        await RefusedAsApp("update dev_credentials set revoked_at = null");
        Assert.Equal(1L, await CountAsync("select count(*) from auth_sessions where revoked_at is null")); // only the new live one
        Assert.Equal(1L, await CountAsync("select count(*) from dev_credentials where revoked_at is null"));
        Assert.NotNull(await sessions.AuthenticateAsync(live.AccessToken.Hash()));
    }

    [Fact]
    public async Task The_old_key_and_token_stay_dead_after_an_attempted_unrevoke()
    {
        var (account, session) = await RevokedAsync();
        await RefusedAsApp("update dev_credentials set revoked_at = null where credential_id = @c", new { c = account.CredentialId });
        await RefusedAsApp("update auth_sessions set revoked_at = null where session_id = @s", new { s = session.SessionId });
        Assert.Null(await sessions.LoginAsync(account.AccountKey.Hash(), 60));
        Assert.Null(await sessions.AuthenticateAsync(session.AccessToken.Hash()));
    }

    [Fact]
    public async Task Revoking_a_live_row_still_works_and_reasserting_the_same_value_is_harmless()
    {
        var account = await admin.CreateAccountAsync(null, null, Op);
        var session = (await sessions.LoginAsync(account.AccountKey.Hash(), 60))!;
        await using var conn = await db.AsApp().OpenConnectionAsync();
        Assert.Equal(1, await conn.ExecuteAsync("update auth_sessions set revoked_at = statement_timestamp() where session_id = @s", new { s = session.SessionId }));
        Assert.Equal(1, await conn.ExecuteAsync("update auth_sessions set revoked_at = revoked_at where session_id = @s", new { s = session.SessionId }));
        Assert.Equal(1, await conn.ExecuteAsync("update dev_credentials set revoked_at = statement_timestamp() where credential_id = @c", new { c = account.CredentialId }));
    }

    [Fact]
    public async Task A_session_the_app_role_inserts_for_a_revoked_credential_never_authenticates()
    {
        var (account, _) = await RevokedAsync();
        var token = Blackglass.Accounts.Domain.Auth.OpaqueSecret.New(Blackglass.Accounts.Domain.Auth.OpaqueSecret.AccessTokenPrefix);
        await using (var conn = await db.AsApp().OpenConnectionAsync())
            await conn.ExecuteAsync("insert into auth_sessions (session_id, account_id, credential_id, token_hash, expires_at) values (gen_random_uuid(), @a, @c, @h, now() + interval '1 hour')",
                new { a = account.AccountId, c = account.CredentialId, h = token.Hash() });
        Assert.Null(await sessions.AuthenticateAsync(token.Hash()));
    }

    [Theory]
    [InlineData("alter table dev_credentials disable trigger all")]
    [InlineData("alter table auth_sessions disable trigger auth_sessions_revocation_is_final")]
    [InlineData("drop trigger dev_credentials_revocation_is_final on dev_credentials")]
    [InlineData("drop function revocation_is_final() cascade")]
    [InlineData("create or replace function revocation_is_final() returns trigger language plpgsql as 'begin return new; end'")]
    [InlineData("alter table inventories disable trigger inventories_forward_only")]
    [InlineData("alter table inventories disable trigger all")]
    [InlineData("drop trigger inventories_forward_only on inventories")]
    [InlineData("drop function inventory_is_forward_only() cascade")]
    [InlineData("create or replace function inventory_is_forward_only() returns trigger language plpgsql as 'begin return new; end'")]
    [InlineData("set session_replication_role = replica")]
    public async Task The_app_role_cannot_switch_the_guard_off(string sql) =>
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await RefusedAsApp(sql)).SqlState);

    [Fact]
    public async Task The_guard_triggers_are_enabled_on_their_tables()
    {
        var triggers = await ScalarAsSuperuserAsync<string>(db, """
            select string_agg(tgrelid::regclass || ':' || tgname || ':' || tgenabled::text, ',' order by tgname)
            from pg_trigger where not tgisinternal
            """);
        Assert.Equal("auth_sessions:auth_sessions_revocation_is_final:O,dev_credentials:dev_credentials_revocation_is_final:O,"
            + "energy_ledger:energy_ledger_chain:O,energy_ledger:energy_ledger_is_append_only:O,energy_ledger:energy_ledger_matches_wallet:O,"
            + "energy_ledger:energy_ledger_no_truncate:O,energy_policies:energy_policies_no_truncate:O,energy_policies:energy_policy_effective_at:O,"
            + "energy_policies:energy_policy_is_immutable:O,energy_wallets:energy_wallet_forward_only:O,energy_wallets:energy_wallet_matches_ledger:O,"
            + "energy_wallets:energy_wallet_progress_below_interval:O,"
            + "inventories:inventories_forward_only:O", triggers);
    }
}
