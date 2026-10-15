using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Blackglass.Accounts.Api;
using Blackglass.Accounts.Domain.Accounts;
using Blackglass.Accounts.Persistence.Accounts;
using Blackglass.Accounts.IntegrationTests.Support;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

[Trait("Requires", "Postgres")]
public class AdminTests : IAsyncLifetime
{
    const string AdminKey = "admin-key-for-tests-0123456789abcdef";
    TestDatabase db = null!;
    public async Task InitializeAsync() => db = await MigratedAsync();

    public async Task DisposeAsync()
    {
        try { Assert.Empty(await InvariantFailuresAsync(db)); }
        finally { await db.DisposeAsync(); }
    }

    static Dictionary<string, string?> AdminOn(int perMinute = 100)
    {
        var file = Path.Combine(Path.GetTempPath(), $"bg-test-admin-{Guid.NewGuid():N}");
        File.WriteAllText(file, AdminKey);
        return new()
        {
            ["BLACKGLASS_ADMIN_ROUTES"] = "enabled", ["BLACKGLASS_ADMIN_KEY_FILE"] = file, ["BLACKGLASS_DEV_AUTH"] = "enabled",
            ["BLACKGLASS_RATE_ADMIN_PER_MINUTE"] = perMinute.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    async Task<(int Code, string Out, string Err)> CliAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await Cli.RunAsync(["admin", .. args], db.EnvFor("app"), output, error);
        return (code, output.ToString(), error.ToString());
    }

    static string Field(string output, string name) => Regex.Match(output, $"(?m)^{name}=(.*)$").Groups[1].Value.TrimEnd('\r');

    static HttpRequestMessage CreateRequest(object body, string? adminKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/dev/v1/accounts") { Content = JsonContent.Create(body) };
        if (adminKey is not null) request.Headers.Add("X-Admin-Key", adminKey);
        return request;
    }

    Task<long> AccountCountAsync() => ScalarAsSuperuserAsync<long>(db, "select count(*) from accounts");

    [Fact]
    public async Task The_cli_creates_an_account_and_prints_its_base58_id_and_key_once()
    {
        var (code, output, _) = await CliAsync("account", "create", "--handle", "cli-1", "--display-name", "CLI One", "--operator", "ops", "--reason", "test");
        Assert.Equal(ExitCodes.Ok, code);
        Assert.Matches("(?m)^accountKey=dk1_[A-Za-z0-9_-]{43}\r?$", output);
        Assert.Equal(AccountId.Format(1), Field(output, "accountId"));
        Assert.True(Guid.TryParse(Field(output, "credentialId"), out _));
        Assert.Equal(await ScalarAsSuperuserAsync<Guid>(db, "select correlation_id from operator_actions"), Guid.Parse(Field(output, "correlationId")));
        Assert.Equal("ops", await ScalarAsSuperuserAsync<string>(db, "select operator from operator_actions"));
    }

    [Fact]
    public async Task The_cli_runs_the_whole_lifecycle_by_base58_id()
    {
        var created = (await CliAsync("account", "create", "--operator", "ops", "--reason", "test")).Out;
        var id = Field(created, "accountId");
        var rotated = await CliAsync("credential", "rotate", "--account", id, "--operator", "ops", "--reason", "rotate");
        Assert.Equal((ExitCodes.Ok, id), (rotated.Code, Field(rotated.Out, "accountId")));
        Assert.NotEqual(Field(created, "accountKey"), Field(rotated.Out, "accountKey"));
        Assert.Equal(ExitCodes.Ok, (await CliAsync("credential", "revoke", "--credential", Field(rotated.Out, "credentialId"), "--operator", "ops", "--reason", "revoke")).Code);
        Assert.Equal(ExitCodes.Ok, (await CliAsync("sessions", "revoke", "--account", id, "--operator", "ops", "--reason", "sessions")).Code);
        var suspended = await CliAsync("account", "suspend", "--account", id, "--operator", "ops", "--reason", "suspend");
        Assert.Equal((ExitCodes.Ok, id), (suspended.Code, Field(suspended.Out, "accountId")));
        Assert.Equal(ExitCodes.Ok, (await CliAsync("account", "reactivate", "--account", id, "--operator", "ops", "--reason", "reactivate")).Code);
        Assert.Equal(6L, await ScalarAsSuperuserAsync<long>(db, "select count(*) from operator_actions"));
    }

    [Fact]
    public async Task The_cli_reports_usage_a_taken_handle_and_an_unknown_account()
    {
        await CliAsync("account", "create", "--handle", "cli-2", "--operator", "ops", "--reason", "test");
        Assert.Equal(ExitCodes.Conflict, (await CliAsync("account", "create", "--handle", "cli-2", "--operator", "ops", "--reason", "again")).Code);
        var unknown = await CliAsync("account", "suspend", "--account", AccountId.Format(999), "--operator", "ops", "--reason", "x");
        Assert.Equal(ExitCodes.NotFound, unknown.Code);
        Assert.Equal("", unknown.Out);
        Assert.Equal(ExitCodes.Usage, (await CliAsync("account", "create", "--handle", "cli-3")).Code);
        Assert.Equal(ExitCodes.Usage, (await CliAsync("account", "suspend", "--account", "1BukQL", "--operator", "ops", "--reason", "x")).Code);
        Assert.Equal(ExitCodes.Usage, (await CliAsync("account", "create", "--handle", "Not Valid", "--operator", "ops", "--reason", "x")).Code);
        Assert.Equal(1L, await AccountCountAsync());
    }

    [Fact]
    public async Task Admin_routes_are_not_mapped_unless_enabled()
    {
        await using var api = await ApiServer.StartAsync(db, new() { ["BLACKGLASS_DEV_AUTH"] = "enabled" });
        Assert.Equal(HttpStatusCode.NotFound, (await api.Http.SendAsync(CreateRequest(new { @operator = "o", reason = "r" }, AdminKey))).StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong-admin-key-0123456789abcdef0123")]
    [InlineData("admin-key-for-tests-0123456789abcdeF")]
    public async Task A_missing_or_wrong_admin_key_is_401_and_writes_nothing(string? key)
    {
        await using var api = await ApiServer.StartAsync(db, AdminOn());
        var response = await api.Http.SendAsync(CreateRequest(new { handle = "y", @operator = "o", reason = "r" }, key));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
        Assert.Equal(0L, await AccountCountAsync());
    }

    [Fact]
    public async Task The_key_is_checked_before_the_body_is_read()
    {
        await using var api = await ApiServer.StartAsync(db, AdminOn());
        var request = new HttpRequestMessage(HttpMethod.Post, "/dev/v1/accounts") { Content = new StringContent("{not json", System.Text.Encoding.UTF8, "application/json") };
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Http.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Two_admin_key_headers_are_401()
    {
        await using var api = await ApiServer.StartAsync(db, AdminOn());
        var request = CreateRequest(new { @operator = "o", reason = "r" }, null);
        request.Headers.TryAddWithoutValidation("X-Admin-Key", new[] { AdminKey, AdminKey });
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Http.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task A_player_token_never_authorizes_an_admin_route()
    {
        await using var api = await ApiServer.StartAsync(db, AdminOn());
        var player = await new AccountAdminStore(db.AsApp()).CreateAccountAsync(null, null, Op);
        var login = await api.Http.PostAsJsonAsync("/v1/auth/dev/login", new { accountKey = player.AccountKey.Text });
        var token = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.GetProperty("accessToken").GetString()!;
        var asBearer = CreateRequest(new { @operator = "o", reason = "r" }, null);
        asBearer.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Http.SendAsync(asBearer)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Http.SendAsync(CreateRequest(new { @operator = "o", reason = "r" }, token))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Http.SendAsync(CreateRequest(new { @operator = "o", reason = "r" }, player.AccountKey.Text))).StatusCode);
        Assert.Equal(1L, await AccountCountAsync());
    }

    [Fact]
    public async Task The_admin_route_creates_an_account_whose_key_logs_in()
    {
        await using var api = await ApiServer.StartAsync(db, AdminOn());
        var response = await api.Http.SendAsync(CreateRequest(new { handle = "route-1", displayName = "Route One", @operator = "ops", reason = "test" }, AdminKey));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Null(response.Headers.Location);
        var correlation = Guid.Parse(response.Headers.GetValues("X-Correlation-Id").Single());
        Assert.Equal(correlation, await ScalarAsSuperuserAsync<Guid>(db, "select correlation_id from operator_actions"));
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(["accountId", "credentialId", "accountKey"], body.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(JsonValueKind.String, body.GetProperty("accountId").ValueKind);
        Assert.Equal(AccountId.Format(1), body.GetProperty("accountId").GetString());
        var key = body.GetProperty("accountKey").GetString();
        Assert.Equal(HttpStatusCode.OK, (await api.Http.PostAsJsonAsync("/v1/auth/dev/login", new { accountKey = key })).StatusCode);
    }

    [Theory]
    [InlineData("taken", "Taken", 409, "HandleTaken")]
    [InlineData("Not Valid", null, 422, "InvalidHandle")]
    [InlineData(null, " padded", 422, "InvalidDisplayName")]
    public async Task Bad_create_requests_get_problem_codes(string? handle, string? displayName, int status, string code)
    {
        await using var api = await ApiServer.StartAsync(db, AdminOn());
        await api.Http.SendAsync(CreateRequest(new { handle = "taken", @operator = "ops", reason = "first" }, AdminKey));
        var response = await api.Http.SendAsync(CreateRequest(new { handle, displayName, @operator = "ops", reason = "test" }, AdminKey));
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(code, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
        Assert.Equal(1L, await AccountCountAsync());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"operator\":\"\",\"reason\":\"r\"}")]
    [InlineData("{\"operator\":\"o\"}")]
    [InlineData("{not json")]
    [InlineData("")]
    public async Task An_unusable_body_with_the_right_key_is_refused_without_writing(string json)
    {
        await using var api = await ApiServer.StartAsync(db, AdminOn());
        var request = new HttpRequestMessage(HttpMethod.Post, "/dev/v1/accounts") { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Admin-Key", AdminKey);
        var status = (int)(await api.Http.SendAsync(request)).StatusCode;
        Assert.InRange(status, 400, 422);
        Assert.Equal(0L, await AccountCountAsync());
    }

    [Fact]
    public async Task An_oversized_admin_body_is_refused_and_writes_nothing()
    {
        await using var api = await ApiServer.StartAsync(db, AdminOn());
        var response = await api.Http.SendAsync(CreateRequest(new { @operator = "ops", reason = new string('r', 100_000) }, AdminKey));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0L, await AccountCountAsync());
    }

    [Theory]
    [InlineData("ops\nAdmin created account 2 (operator root", "forging a log line")]
    [InlineData("nul\u0000", "a NUL")]
    [InlineData("ops", "two\nlines")]
    public async Task Control_characters_in_operator_or_reason_are_422_InvalidOperator(string op, string reason)
    {
        await using var api = await ApiServer.StartAsync(db, AdminOn());
        var response = await api.Http.SendAsync(CreateRequest(new { @operator = op, reason }, AdminKey));
        Assert.Equal(422, (int)response.StatusCode);
        Assert.Equal("InvalidOperator", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
        Assert.Equal(0L, await AccountCountAsync());
    }

    [Fact]
    public async Task Admin_requests_are_rate_limited_before_the_key_is_checked()
    {
        await using var api = await ApiServer.StartAsync(db, AdminOn(perMinute: 2));
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Http.SendAsync(CreateRequest(new { @operator = "o", reason = "r" }, "wrong-admin-key-0123456789abcdef0123"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Http.SendAsync(CreateRequest(new { @operator = "o", reason = "r" }, "wrong-admin-key-0123456789abcdef0123"))).StatusCode);
        var limited = await api.Http.SendAsync(CreateRequest(new { @operator = "o", reason = "r" }, AdminKey));
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(0L, await AccountCountAsync());
    }
}
