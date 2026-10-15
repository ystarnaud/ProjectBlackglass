using Dapper;
using Npgsql;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Accounts;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

[Trait("Requires", "Postgres")]
public class AccountAdminStoreTests : IAsyncLifetime
{
    TestDatabase db = null!;
    AccountAdminStore store = null!;

    public async Task InitializeAsync() { db = await MigratedAsync(); store = new AccountAdminStore(db.AsApp()); }

    public async Task DisposeAsync()
    {
        Assert.Empty(await InvariantFailuresAsync(db)); // revocations cascade after every test
        await db.DisposeAsync();
    }

    Task<T> ScalarAsync<T>(string sql, object? args = null) => ScalarAsSuperuserAsync<T>(db, sql, args);

    [Fact]
    public async Task Creating_an_account_gets_a_postgres_id_stores_only_the_key_hash_and_records_the_action()
    {
        var op = Op;
        var issued = await store.CreateAccountAsync("tester-1", "Tester One", op);
        Assert.Equal(1L, issued.AccountId);
        Assert.Equal(7, issued.CredentialId.Version);
        Assert.StartsWith("dk1_", issued.AccountKey.Text);
        Assert.Equal(issued.AccountKey.Hash(), await ScalarAsync<byte[]>("select secret_hash from dev_credentials where credential_id = @c", new { c = issued.CredentialId }));
        Assert.Equal("tester-1", await ScalarAsync<string>("select subject from account_identities where account_id = @a", new { a = issued.AccountId }));
        Assert.Equal("Tester One", await ScalarAsync<string>("select display_name from accounts where account_id = @a", new { a = issued.AccountId }));
        var audit = (await QueryAuditAsync()).Single();
        Assert.Equal(("account_create", issued.AccountId, issued.CredentialId, op.CorrelationId, "test-operator", "integration test"),
            (audit.Action, audit.AccountId, audit.CredentialId, audit.CorrelationId, audit.Operator, audit.Reason));
        Assert.Contains("\"starter\"", audit.Result); // provisioning adds the starter summary to the audited result (Stage 4, D4)
    }

    [Fact]
    public async Task Handle_and_display_name_are_optional()
    {
        var issued = await store.CreateAccountAsync(null, null, Op);
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from account_identities"));
        Assert.Null(await ScalarAsync<string?>("select display_name from accounts where account_id = @a", new { a = issued.AccountId }));
    }

    [Fact]
    public async Task Created_accounts_get_sequential_ids()
    {
        var ids = new List<long>();
        for (var i = 0; i < 3; i++) ids.Add((await store.CreateAccountAsync(null, null, Op)).AccountId);
        Assert.Equal(new long[] { 1, 2, 3 }, ids);
    }

    [Fact]
    public async Task A_taken_handle_is_refused_and_nothing_is_written()
    {
        await store.CreateAccountAsync("taken", null, Op);
        var e = await Assert.ThrowsAsync<AccountAdminException>(() => store.CreateAccountAsync("taken", null, Op));
        Assert.Equal(AccountAdminError.HandleTaken, e.Error);
        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from accounts"));
        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from dev_credentials"));
        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from operator_actions"));
    }

    [Theory]
    [InlineData("Not Valid", null, AccountAdminError.InvalidHandle)]
    [InlineData("", null, AccountAdminError.InvalidHandle)]
    [InlineData(null, " padded", AccountAdminError.InvalidDisplayName)]
    [InlineData(null, "two\nlines", AccountAdminError.InvalidDisplayName)]
    public async Task Invalid_input_is_refused_before_any_write(string? handle, string? displayName, AccountAdminError error)
    {
        var e = await Assert.ThrowsAsync<AccountAdminException>(() => store.CreateAccountAsync(handle, displayName, Op));
        Assert.Equal(error, e.Error);
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from accounts"));
    }

    [Fact]
    public async Task Rotation_revokes_the_old_credential_and_its_sessions_and_issues_one_new_credential()
    {
        var first = await store.CreateAccountAsync("rotator", null, Op);
        await InsertSessionAsync(db, first.AccountId, first.CredentialId);
        await InsertSessionAsync(db, first.AccountId, first.CredentialId);
        var second = await store.RotateCredentialAsync(first.AccountId, Op);
        Assert.Equal(first.AccountId, second.AccountId);
        Assert.NotEqual(first.CredentialId, second.CredentialId);
        Assert.NotEqual(first.AccountKey.Text, second.AccountKey.Text);
        Assert.Equal(second.CredentialId, await ScalarAsync<Guid>("select credential_id from dev_credentials where account_id = @a and revoked_at is null", new { a = first.AccountId }));
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from auth_sessions where revoked_at is null"));
        var audit = (await QueryAuditAsync()).Last();
        Assert.Equal(("credential_rotate", second.CredentialId), (audit.Action, audit.CredentialId));
        Assert.Contains("\"revokedSessions\": 2", audit.Result);
    }

    [Fact]
    public async Task Revoking_a_credential_revokes_its_sessions_and_is_idempotent()
    {
        var a = await store.CreateAccountAsync("revoker", null, Op);
        await InsertSessionAsync(db, a.AccountId, a.CredentialId);
        Assert.Equal(1, await store.RevokeCredentialAsync(a.CredentialId, Op));
        Assert.Equal(0, await store.RevokeCredentialAsync(a.CredentialId, Op)); // already revoked: nothing more, still audited
        Assert.Equal(3L, await ScalarAsync<long>("select count(*) from operator_actions where account_id = @a", new { a = a.AccountId }));
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from dev_credentials where revoked_at is null"));
    }

    [Fact]
    public async Task Revoking_sessions_leaves_the_credential_live()
    {
        var a = await store.CreateAccountAsync(null, null, Op);
        await InsertSessionAsync(db, a.AccountId, a.CredentialId);
        Assert.Equal(1, await store.RevokeSessionsAsync(a.AccountId, Op));
        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from dev_credentials where revoked_at is null"));
    }

    [Fact]
    public async Task Suspension_revokes_sessions_and_reactivation_restores_none()
    {
        var a = await store.CreateAccountAsync("suspendee", null, Op);
        await InsertSessionAsync(db, a.AccountId, a.CredentialId);
        Assert.Equal(1, await store.SuspendAsync(a.AccountId, Op));
        Assert.Equal("suspended", await ScalarAsync<string>("select status from accounts where account_id = @a", new { a = a.AccountId }));
        await store.ReactivateAsync(a.AccountId, Op);
        Assert.Equal("active", await ScalarAsync<string>("select status from accounts where account_id = @a", new { a = a.AccountId }));
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from auth_sessions where revoked_at is null"));
        Assert.Equal(new[] { "account_create", "account_suspend", "account_reactivate" }, (await QueryAuditAsync()).Select(x => x.Action));
    }

    [Fact]
    public async Task Unknown_ids_are_not_found_and_write_nothing()
    {
        Assert.Equal(AccountAdminError.NotFound, (await Assert.ThrowsAsync<AccountAdminException>(() => store.RotateCredentialAsync(42, Op))).Error);
        Assert.Equal(AccountAdminError.NotFound, (await Assert.ThrowsAsync<AccountAdminException>(() => store.RevokeCredentialAsync(Guid.CreateVersion7(), Op))).Error);
        Assert.Equal(AccountAdminError.NotFound, (await Assert.ThrowsAsync<AccountAdminException>(() => store.RevokeSessionsAsync(42, Op))).Error);
        Assert.Equal(AccountAdminError.NotFound, (await Assert.ThrowsAsync<AccountAdminException>(() => store.SuspendAsync(42, Op))).Error);
        Assert.Equal(AccountAdminError.NotFound, (await Assert.ThrowsAsync<AccountAdminException>(() => store.ReactivateAsync(42, Op))).Error);
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from operator_actions"));
    }

    [Fact]
    public async Task An_audit_failure_rolls_back_the_change()
    {
        var a = await store.CreateAccountAsync("atomic", null, Op);
        var session = await InsertSessionAsync(db, a.AccountId, a.CredentialId);
        // A reason over 1000 characters bypasses OperatorContext.Create and fails the audit insert after the state change.
        var bad = new OperatorContext("op", new string('r', 1001), Guid.CreateVersion7());
        // The database's refusal (23514 on operator_actions_reason_check) is the known InvalidOperator conflict.
        Assert.Equal(AccountAdminError.InvalidOperator, (await Assert.ThrowsAsync<AccountAdminException>(() => store.SuspendAsync(a.AccountId, bad))).Error);
        await Assert.ThrowsAsync<AccountAdminException>(() => store.RotateCredentialAsync(a.AccountId, bad));
        await Assert.ThrowsAsync<AccountAdminException>(() => store.CreateAccountAsync("never", null, bad));
        Assert.Equal("active", await ScalarAsync<string>("select status from accounts where account_id = @a", new { a = a.AccountId }));
        Assert.Null(await ScalarAsync<DateTime?>("select revoked_at from auth_sessions where session_id = @session", new { session }));
        Assert.Equal(a.CredentialId, await ScalarAsync<Guid>("select credential_id from dev_credentials where revoked_at is null"));
        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from accounts"));
        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from operator_actions"));
    }

    [Fact]
    public async Task No_audit_row_holds_a_key_or_its_hash()
    {
        var a = await store.CreateAccountAsync("secretive", "Secretive", Op);
        var b = await store.RotateCredentialAsync(a.AccountId, Op);
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        var everything = string.Join("\n", await conn.QueryAsync<string>("select row_to_json(o)::text from operator_actions o"));
        foreach (var key in new[] { a.AccountKey, b.AccountKey })
        {
            Assert.DoesNotContain(key.Text[4..], everything);
            Assert.DoesNotContain(Convert.ToHexStringLower(key.Hash()), everything);
            Assert.DoesNotContain(Convert.ToBase64String(key.Hash()), everything);
        }
    }

    [Fact]
    public async Task Concurrent_rotations_leave_exactly_one_live_credential()
    {
        var a = await store.CreateAccountAsync("racer", null, Op);
        var issued = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => store.RotateCredentialAsync(a.AccountId, Op)));
        var live = await ScalarAsync<Guid>("select credential_id from dev_credentials where account_id = @a and revoked_at is null", new { a = a.AccountId });
        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from dev_credentials where account_id = @a and revoked_at is null", new { a = a.AccountId }));
        Assert.Contains(live, issued.Select(i => i.CredentialId));
        Assert.Equal(11L, await ScalarAsync<long>("select count(*) from operator_actions where account_id = @a", new { a = a.AccountId }));
    }

    [Fact]
    public async Task Concurrent_lifecycle_calls_on_one_account_serialize_and_each_is_audited()
    {
        var a = await store.CreateAccountAsync("racer-2", null, Op);
        for (var i = 0; i < 5; i++) await InsertSessionAsync(db, a.AccountId, a.CredentialId);
        await Task.WhenAll(
            store.RotateCredentialAsync(a.AccountId, Op), store.SuspendAsync(a.AccountId, Op), store.RevokeSessionsAsync(a.AccountId, Op),
            store.RotateCredentialAsync(a.AccountId, Op), store.RevokeCredentialAsync(a.CredentialId, Op));
        Assert.Equal(6L, await ScalarAsync<long>("select count(*) from operator_actions where account_id = @a", new { a = a.AccountId }));
        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from dev_credentials where account_id = @a and revoked_at is null", new { a = a.AccountId }));
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from auth_sessions where revoked_at is null"));
        Assert.Empty(await InvariantFailuresAsync(db));
    }

    [Fact]
    public async Task A_lifecycle_call_waits_for_the_account_lock_and_stamps_its_rows_after_the_wait()
    {
        var first = await store.CreateAccountAsync(null, null, Op);
        IssuedCredential second;
        await using (var conn = await db.AsApp().OpenConnectionAsync())
        await using (var tx = await conn.BeginTransactionAsync())
        {
            await conn.ExecuteAsync("select account_id from accounts where account_id = @a for update", new { a = first.AccountId }, tx);
            var rotation = store.RotateCredentialAsync(first.AccountId, Op);
            await WaitUntilABackendWaitsOnALockAsync(db);
            Assert.False(rotation.IsCompleted);
            await tx.CommitAsync();
            second = await rotation;
        }
        var revokedAt = await ScalarAsync<DateTime>("select revoked_at from dev_credentials where credential_id = @c", new { c = first.CredentialId });
        var createdAt = await ScalarAsync<DateTime>("select created_at from dev_credentials where credential_id = @c", new { c = second.CredentialId });
        var auditedAt = await ScalarAsync<DateTime>("select created_at from operator_actions where action = 'credential_rotate'");
        Assert.True(createdAt >= revokedAt, $"the successor {createdAt:O} was created before its predecessor was revoked {revokedAt:O}");
        Assert.True(auditedAt >= revokedAt, $"the audit row {auditedAt:O} precedes the change it records {revokedAt:O}");
    }

    /// <summary>A backward clock step must never block a security action: rows created "in the future" by the old clock
    /// can still be revoked (revoked_at is never earlier than created_at).</summary>
    [Fact]
    public async Task Revocations_still_succeed_after_the_clock_steps_back()
    {
        var a = await store.CreateAccountAsync(null, null, Op);
        await InsertSessionAsync(db, a.AccountId, a.CredentialId);
        await ExecuteAsSuperuserAsync(db, "update dev_credentials set created_at = now() + interval '1 hour'");
        await ExecuteAsSuperuserAsync(db, "update auth_sessions set created_at = now() + interval '1 hour', expires_at = now() + interval '2 hours'");
        Assert.Equal(1, await store.SuspendAsync(a.AccountId, Op));
        await store.ReactivateAsync(a.AccountId, Op);
        await store.RotateCredentialAsync(a.AccountId, Op);
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from auth_sessions where revoked_at is null"));
        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from dev_credentials where revoked_at is null"));
    }

    sealed record AuditRow(string Action, long? AccountId, Guid? CredentialId, Guid CorrelationId, string Operator, string Reason, string Result);

    async Task<IReadOnlyList<AuditRow>> QueryAuditAsync()
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        return (await conn.QueryAsync<AuditRow>(
            "select action, account_id as AccountId, credential_id as CredentialId, correlation_id as CorrelationId, operator, reason, result::text as Result from operator_actions order by id")).ToList();
    }
}
