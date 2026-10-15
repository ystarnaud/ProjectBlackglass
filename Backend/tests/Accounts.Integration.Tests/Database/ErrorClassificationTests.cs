using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Blackglass.Accounts.Api;
using Blackglass.Accounts.Domain.Accounts;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence;
using Blackglass.Accounts.Persistence.Accounts;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>
/// Database failures by category (Stage 3 correction): an unreachable database is 503; a lock or statement timeout is
/// 503 with Retry-After (busy, retry soon) and changes nothing; a known conflict is its own safe answer (409 handle
/// taken, 422 for input the database refuses); any other SQL or application error is a 500, never a 503. No body or
/// log line carries a database message. The admin CLI mirrors this with exit codes 69, 75, 66/64 and 1.
/// </summary>
[Trait("Requires", "Postgres")]
public class ErrorClassificationTests : IAsyncLifetime
{
    static readonly string AdminKey = NewAdminKey();
    const string SecretLookingMessage = "the-database-said-something-private";
    TestDatabase db = null!;
    AccountAdminStore admin = null!;
    readonly CapturingLoggerProvider logs = new();

    public async Task InitializeAsync() { db = await MigratedAsync(); admin = new AccountAdminStore(db.AsApp()); }

    public async Task DisposeAsync() => await db.DisposeAsync();

    Task<ApiServer> StartAsync(Action<WebApplicationBuilder>? more = null) => ApiServer.StartAsync(db, new()
    {
        ["BLACKGLASS_DEV_AUTH"] = "enabled", ["BLACKGLASS_ADMIN_ROUTES"] = "enabled", [DbTimeouts.LockTimeoutVar] = "300",
    }, b =>
    {
        b.Logging.ClearProviders();
        b.Logging.SetMinimumLevel(LogLevel.Trace);
        b.Logging.AddProvider(logs);
        more?.Invoke(b);
    }, adminKey: AdminKey);

    static async Task<string> TokenAsync(ApiServer api, IssuedCredential account) =>
        JsonDocument.Parse(await (await api.Http.PostAsJsonAsync("/v1/auth/dev/login", new { accountKey = account.AccountKey.Text })).Content.ReadAsStringAsync())
            .RootElement.GetProperty("accessToken").GetString()!;

    static HttpRequestMessage Me(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    HttpRequestMessage CreateAccount(object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/dev/v1/accounts") { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Admin-Key", AdminKey);
        return request;
    }

    /// <summary>Holds <paramref name="lockSql"/> in a superuser transaction while <paramref name="during"/> runs.</summary>
    async Task<T> WhileLockedAsync<T>(string lockSql, object? args, Func<Task<T>> during)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync(lockSql, args, tx);
        try { return await during(); }
        finally { await tx.RollbackAsync(); }
    }

    static async Task AssertEmptyAsync(HttpResponseMessage response) => Assert.Equal("", await response.Content.ReadAsStringAsync());

    // ---- busy: 503 with Retry-After, nothing changed ----

    [Fact]
    public async Task A_login_stuck_behind_a_lock_is_503_busy_and_creates_no_session()
    {
        await using var api = await StartAsync();
        var account = await admin.CreateAccountAsync(null, null, Op);
        var response = await WhileLockedAsync("select 1 from accounts where account_id = @a for update", new { a = account.AccountId },
            () => api.Http.PostAsJsonAsync("/v1/auth/dev/login", new { accountKey = account.AccountKey.Text }));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
        await AssertEmptyAsync(response);
        Assert.Equal(0L, await ScalarAsSuperuserAsync<long>(db, "select count(*) from auth_sessions"));
        Assert.Contains("busy", logs.Text);
    }

    [Fact]
    public async Task Authentication_stuck_behind_a_lock_is_503_never_401()
    {
        await using var api = await StartAsync();
        var token = await TokenAsync(api, await admin.CreateAccountAsync(null, null, Op));
        var response = await WhileLockedAsync("lock table auth_sessions in access exclusive mode", null, () => api.Http.SendAsync(Me(token)));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
        Assert.Equal(HttpStatusCode.OK, (await api.Http.SendAsync(Me(token))).StatusCode);
    }

    // ---- known conflicts: their own safe codes ----

    [Fact]
    public async Task A_display_name_the_database_refuses_is_422_not_a_server_error()
    {
        await ExecuteAsSuperuserAsync(db, "alter table accounts drop constraint accounts_display_name_check, add constraint accounts_display_name_check check (display_name is distinct from 'Refused By Database')");
        await using var api = await StartAsync();
        var response = await api.Http.SendAsync(CreateAccount(new { displayName = "Refused By Database", @operator = "ops", reason = "test" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("InvalidDisplayName", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
        Assert.Equal(0L, await ScalarAsSuperuserAsync<long>(db, "select count(*) from accounts"));
    }

    [Fact]
    public async Task An_operator_reason_the_database_refuses_is_422_and_rolls_the_account_back()
    {
        await ExecuteAsSuperuserAsync(db, "alter table operator_actions drop constraint operator_actions_reason_check, add constraint operator_actions_reason_check check (reason <> 'refused by database')");
        await using var api = await StartAsync();
        var response = await api.Http.SendAsync(CreateAccount(new { @operator = "ops", reason = "refused by database" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("InvalidOperator", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
        Assert.Equal(0L, await ScalarAsSuperuserAsync<long>(db, "select count(*) from accounts"));
    }

    [Fact]
    public async Task A_taken_handle_is_409()
    {
        await using var api = await StartAsync();
        await api.Http.SendAsync(CreateAccount(new { handle = "taken", @operator = "ops", reason = "first" }));
        Assert.Equal(HttpStatusCode.Conflict, (await api.Http.SendAsync(CreateAccount(new { handle = "taken", @operator = "ops", reason = "second" }))).StatusCode);
    }

    // ---- unexpected: 500, never 503, no details ----

    [Fact]
    public async Task An_unexpected_sql_error_during_authentication_is_500_not_503_or_401()
    {
        await using var api = await StartAsync();
        var token = await TokenAsync(api, await admin.CreateAccountAsync(null, null, Op));
        await ExecuteAsSuperuserAsync(db, "revoke select on auth_sessions from accounts_app");
        var response = await api.Http.SendAsync(Me(token));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Null(response.Headers.RetryAfter);
        await AssertEmptyAsync(response);
        Assert.Contains("42501", logs.Text);
    }

    [Fact]
    public async Task An_unexpected_sql_error_is_500_and_its_message_reaches_neither_the_client_nor_the_logs()
    {
        await ExecuteAsSuperuserAsync(db, $"""
            create function bg_test_refuse() returns trigger language plpgsql as $$ begin raise exception '{SecretLookingMessage}'; end $$;
            create trigger bg_test_refuse before insert on auth_sessions for each row execute function bg_test_refuse();
            """);
        await using var api = await StartAsync();
        var account = await admin.CreateAccountAsync(null, null, Op);
        var response = await api.Http.PostAsJsonAsync("/v1/auth/dev/login", new { accountKey = account.AccountKey.Text });
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await AssertEmptyAsync(response);
        Assert.Contains("P0001", logs.Text);
        Assert.DoesNotContain(SecretLookingMessage, logs.Text);
    }

    [Fact]
    public async Task An_application_error_is_500_not_503()
    {
        await using var api = await StartAsync(b => b.Services.AddSingleton<IStartupFilter>(new ThrowAfterTheApp()));
        var response = await api.Http.GetAsync("/no-such-route-throws");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await AssertEmptyAsync(response);
    }

    /// <summary>Appends a terminal middleware that throws: reached only by a request no endpoint matched.</summary>
    sealed class ThrowAfterTheApp : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.Run(_ => throw new InvalidOperationException("an application bug"));
        };
    }

    // ---- the admin CLI ----

    async Task<(int Code, string Out, string Err)> CliAsync(Dictionary<string, string?> env, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await Cli.RunAsync(["admin", .. args], env, output, error);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task The_cli_reports_a_lock_timeout_as_busy_exit_75_and_changes_nothing()
    {
        var account = await admin.CreateAccountAsync(null, null, Op);
        var env = db.EnvFor("app");
        env[DbTimeouts.LockTimeoutVar] = "300";
        var (code, output, error) = await WhileLockedAsync("select 1 from accounts where account_id = @a for update", new { a = account.AccountId },
            () => CliAsync(env, "account", "suspend", "--account-base58", AccountId.Format(account.AccountId), "--operator", "ops", "--reason", "test", "--yes"));
        Assert.Equal(ExitCodes.Busy, code);
        Assert.DoesNotContain("correlationId=", output); // the target preview only (a plain read takes no lock)
        Assert.Contains("busy", error);
        Assert.Contains("nothing was changed", error);
        Assert.Equal("active", await ScalarAsSuperuserAsync<string>(db, "select status from accounts"));
    }

    [Fact]
    public async Task The_cli_reports_an_unreachable_database_as_exit_69()
    {
        var env = db.EnvFor("app");
        env["BLACKGLASS_DB_PORT"] = "1";
        var (code, output, error) = await CliAsync(env, "account", "create", "--operator", "ops", "--reason", "test");
        Assert.Equal((ExitCodes.Unavailable, ""), (code, output));
        Assert.Contains("unavailable", error);
    }

    [Fact]
    public async Task The_cli_reports_an_unexpected_sql_error_as_exit_1_with_its_sqlstate_only()
    {
        await ExecuteAsSuperuserAsync(db, $"""
            create function bg_test_refuse() returns trigger language plpgsql as $$ begin raise exception '{SecretLookingMessage}'; end $$;
            create trigger bg_test_refuse before insert on accounts for each row execute function bg_test_refuse();
            """);
        var (code, output, error) = await CliAsync(db.EnvFor("app"), "account", "create", "--operator", "ops", "--reason", "test");
        Assert.Equal((ExitCodes.Failure, ""), (code, output));
        Assert.Contains("P0001", error);
        Assert.DoesNotContain(SecretLookingMessage, error);
    }

    [Fact]
    public async Task The_cli_reports_a_display_name_the_database_refuses_as_usage()
    {
        await ExecuteAsSuperuserAsync(db, "alter table accounts drop constraint accounts_display_name_check, add constraint accounts_display_name_check check (display_name is distinct from 'Refused By Database')");
        var (code, output, _) = await CliAsync(db.EnvFor("app"), "account", "create", "--display-name", "Refused By Database", "--operator", "ops", "--reason", "test");
        Assert.Equal((ExitCodes.Usage, ""), (code, output));
    }
}
