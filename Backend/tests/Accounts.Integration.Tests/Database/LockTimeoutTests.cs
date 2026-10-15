using System.Diagnostics;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence;
using Blackglass.Accounts.Persistence.Accounts;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>
/// Bounded waits (spec §15.3): the API's and the admin CLI's connections carry lock_timeout and statement_timeout, so a
/// login, logout, authentication or lifecycle call stuck behind a lock fails instead of waiting forever. It fails
/// closed: the statement is cancelled, its transaction rolls back, and nothing it did before the wait is committed. The
/// blocker here is another transaction that simply keeps a lock.
/// </summary>
[Trait("Requires", "Postgres")]
public class LockTimeoutTests : IAsyncLifetime
{
    static readonly DbTimeouts Short = new(LockTimeoutMs: 300, StatementTimeoutMs: 10_000);
    TestDatabase db = null!;
    AccountAdminStore unbounded = null!;
    IssuedCredential account = null!;
    IssuedSession session = null!;

    public async Task InitializeAsync()
    {
        db = await MigratedAsync();
        unbounded = new AccountAdminStore(db.AsApp());
        account = await unbounded.CreateAccountAsync(null, null, Op);
        session = (await new SessionStore(db.AsApp()).LoginAsync(account.AccountKey.Hash(), 60))!;
    }

    public async Task DisposeAsync()
    {
        try { Assert.Empty(await InvariantFailuresAsync(db)); }
        finally { await db.DisposeAsync(); }
    }

    /// <summary>Runs the call while another transaction keeps <paramref name="lockSql"/>; returns what the call threw.</summary>
    async Task<PostgresException> BlockedAsync(string lockSql, Func<Task> call)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync(lockSql, new { a = account.AccountId, s = session.SessionId, c = account.CredentialId }, tx);
        var clock = Stopwatch.StartNew();
        var e = await Assert.ThrowsAsync<PostgresException>(() => call().WaitAsync(TimeSpan.FromSeconds(10))); // unbounded: TimeoutException, a failure not a hang
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8), $"the call waited {clock.Elapsed}"); // bounded, not the blocker's lifetime
        await tx.RollbackAsync();
        return e;
    }

    async Task AssertNothingChangedAsync()
    {
        await using var su = await db.AsSuperuser().OpenConnectionAsync();
        Assert.Equal("active", await su.ExecuteScalarAsync<string>("select status from accounts where account_id = @a", new { a = account.AccountId }));
        Assert.Equal(1L, await su.ExecuteScalarAsync<long>("select count(*) from dev_credentials"));
        Assert.Equal(0L, await su.ExecuteScalarAsync<long>("select count(*) from dev_credentials where revoked_at is not null"));
        Assert.Equal(1L, await su.ExecuteScalarAsync<long>("select count(*) from auth_sessions"));
        Assert.Equal(0L, await su.ExecuteScalarAsync<long>("select count(*) from auth_sessions where revoked_at is not null"));
        Assert.Equal(1L, await su.ExecuteScalarAsync<long>("select count(*) from operator_actions")); // the creation's only
    }

    Func<Task> Lifecycle(string how, AccountAdminStore store) => how switch
    {
        "rotate" => () => store.RotateCredentialAsync(account.AccountId, Op),
        "revoke-credential" => () => store.RevokeCredentialAsync(account.CredentialId, Op),
        "revoke-sessions" => () => store.RevokeSessionsAsync(account.AccountId, Op),
        "suspend" => () => store.SuspendAsync(account.AccountId, Op),
        "reactivate" => () => store.ReactivateAsync(account.AccountId, Op),
        _ => throw new ArgumentOutOfRangeException(nameof(how)),
    };

    [Theory]
    [InlineData("rotate")]
    [InlineData("revoke-credential")]
    [InlineData("revoke-sessions")]
    [InlineData("suspend")]
    [InlineData("reactivate")]
    public async Task A_lifecycle_call_behind_a_held_account_lock_times_out_and_changes_nothing(string how)
    {
        var e = await BlockedAsync("select 1 from accounts where account_id = @a for update", Lifecycle(how, new AccountAdminStore(db.AsAppWith(Short))));
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, e.SqlState);
        await AssertNothingChangedAsync();
    }

    /// <summary>Blocked on a session row, after the account lock. Suspend has already set the status by then, so its
    /// case proves the timeout undoes earlier writes; rotate and revoke-sessions have written nothing yet.</summary>
    [Theory]
    [InlineData("suspend")]
    [InlineData("revoke-sessions")]
    [InlineData("rotate")]
    public async Task A_lifecycle_call_blocked_on_a_session_row_times_out_and_changes_nothing(string how)
    {
        var e = await BlockedAsync("select 1 from auth_sessions where session_id = @s for update", Lifecycle(how, new AccountAdminStore(db.AsAppWith(Short))));
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, e.SqlState);
        await AssertNothingChangedAsync();
    }

    [Fact]
    public async Task A_login_behind_a_held_account_lock_times_out_and_creates_no_session()
    {
        var sessions = new SessionStore(db.AsAppWith(Short));
        var e = await BlockedAsync("select 1 from accounts where account_id = @a for update", () => sessions.LoginAsync(account.AccountKey.Hash(), 60));
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, e.SqlState);
        await AssertNothingChangedAsync();
    }

    [Fact]
    public async Task A_logout_behind_a_held_session_lock_times_out_and_the_session_stays_live()
    {
        var sessions = new SessionStore(db.AsAppWith(Short));
        var e = await BlockedAsync("select 1 from auth_sessions where session_id = @s for update", () => sessions.RevokeAsync(session.SessionId, account.AccountId));
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, e.SqlState);
        await AssertNothingChangedAsync();
    }

    [Fact]
    public async Task Authentication_behind_a_table_lock_times_out()
    {
        var sessions = new SessionStore(db.AsAppWith(Short));
        var e = await BlockedAsync("lock table auth_sessions in access exclusive mode", () => sessions.AuthenticateAsync(session.AccessToken.Hash()));
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, e.SqlState);
    }

    [Fact]
    public async Task The_statement_bound_cancels_a_slow_statement_that_waits_on_no_lock()
    {
        await using var conn = await db.AsAppWith(new DbTimeouts(LockTimeoutMs: 100, StatementTimeoutMs: 300)).OpenConnectionAsync();
        var clock = Stopwatch.StartNew();
        var e = await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync("select pg_sleep(5)"));
        Assert.Equal(PostgresErrorCodes.QueryCanceled, e.SqlState);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(4), $"the statement ran {clock.Elapsed}");
    }

    [Theory]
    [InlineData(null, null, "5s", "15s")]
    [InlineData("1234", "5678", "1234ms", "5678ms")]
    public async Task Serve_bounds_its_connections_from_the_environment(string? lockMs, string? statementMs, string lockShown, string statementShown)
    {
        await using var api = await ApiServer.StartAsync(db, new() { [DbTimeouts.LockTimeoutVar] = lockMs, [DbTimeouts.StatementTimeoutVar] = statementMs });
        await using var conn = await api.App.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        Assert.Equal((lockShown, statementShown), (await conn.ExecuteScalarAsync<string>("show lock_timeout"), await conn.ExecuteScalarAsync<string>("show statement_timeout")));
    }

    [Fact]
    public async Task Serve_and_the_admin_cli_refuse_an_unbounded_wait_setting()
    {
        var env = db.EnvFor("app");
        env[DbTimeouts.LockTimeoutVar] = "0";
        var error = new StringWriter();
        Assert.Equal(Blackglass.Accounts.Api.ExitCodes.Failure, (await Blackglass.Accounts.Api.ServeCommand.StartAsync(env, error, default)).ExitCode);
        Assert.Contains(DbTimeouts.LockTimeoutVar, error.ToString());
        error = new StringWriter();
        Assert.Equal(Blackglass.Accounts.Api.ExitCodes.Failure,
            await Blackglass.Accounts.Api.Cli.RunAsync(["admin", "account", "create", "--operator", "o", "--reason", "r"], env, TextWriter.Null, error));
        Assert.Contains(DbTimeouts.LockTimeoutVar, error.ToString());
    }

    [Fact]
    public async Task The_client_command_timeout_outlasts_the_largest_configured_statement_bound()
    {
        await using var conn = await db.AsAppWith(new DbTimeouts(60_000, 600_000)).OpenConnectionAsync();
        Assert.True(conn.CommandTimeout > 600, $"CommandTimeout {conn.CommandTimeout} s would fire before the 600 s server bound");
        await using var bounded = await db.AsAppWith(new DbTimeouts(300, 2_000)).OpenConnectionAsync();
        Assert.True(bounded.CommandTimeout > 2, $"CommandTimeout {bounded.CommandTimeout} s");
    }

    [Fact]
    public async Task The_bounds_are_set_on_every_connection_of_the_data_source()
    {
        await using var conn = await db.AsAppWith(new DbTimeouts(1234, 5678)).OpenConnectionAsync();
        Assert.Equal(("1234ms", "5678ms"), (await conn.ExecuteScalarAsync<string>("show lock_timeout"), await conn.ExecuteScalarAsync<string>("show statement_timeout")));
    }
}
