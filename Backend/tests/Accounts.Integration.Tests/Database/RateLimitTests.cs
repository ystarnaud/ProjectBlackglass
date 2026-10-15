using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Blackglass.Accounts.Domain.Auth;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Accounts;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>Spec §19 (provisional limits). Behind the tunnel every request shares one source address, so no limit here
/// is per IP: login is per presented key plus one global window, authenticated requests are per account.</summary>
[Trait("Requires", "Postgres")]
public class RateLimitTests : IAsyncLifetime
{
    TestDatabase db = null!;
    public async Task InitializeAsync() => db = await MigratedAsync();
    public async Task DisposeAsync() => await db.DisposeAsync();

    static Dictionary<string, string?> Limits(int login, int requests, int loginGlobal = 1000) => new()
    {
        ["BLACKGLASS_DEV_AUTH"] = "enabled",
        ["BLACKGLASS_RATE_LOGIN_PER_MINUTE"] = login.ToString(CultureInfo.InvariantCulture),
        ["BLACKGLASS_RATE_LOGIN_GLOBAL_PER_MINUTE"] = loginGlobal.ToString(CultureInfo.InvariantCulture),
        ["BLACKGLASS_RATE_REQUESTS_PER_MINUTE"] = requests.ToString(CultureInfo.InvariantCulture),
    };

    static async Task<HttpStatusCode> LoginAsync(HttpClient http, string key) =>
        (await http.PostAsJsonAsync("/v1/auth/dev/login", new { accountKey = key })).StatusCode;

    [Fact]
    public async Task Logins_with_one_key_are_limited_per_minute()
    {
        await using var api = await ApiServer.StartAsync(db, Limits(login: 2, requests: 100));
        var key = (await new AccountAdminStore(db.AsApp()).CreateAccountAsync("throttled", null, Op)).AccountKey.Text;
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++) codes.Add(await LoginAsync(api.Http, key));
        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests }, codes);
    }

    [Fact]
    public async Task Another_key_is_not_limited_by_the_first()
    {
        await using var api = await ApiServer.StartAsync(db, Limits(login: 1, requests: 100));
        var admin = new AccountAdminStore(db.AsApp());
        var a = (await admin.CreateAccountAsync("first", null, Op)).AccountKey.Text;
        var b = (await admin.CreateAccountAsync("second", null, Op)).AccountKey.Text;
        Assert.Equal(HttpStatusCode.OK, await LoginAsync(api.Http, a));
        Assert.Equal(HttpStatusCode.TooManyRequests, await LoginAsync(api.Http, a));
        Assert.Equal(HttpStatusCode.OK, await LoginAsync(api.Http, b));
    }

    [Fact]
    public async Task Fabricated_keys_cannot_bypass_the_global_login_limit()
    {
        await using var api = await ApiServer.StartAsync(db, Limits(login: 10, requests: 100, loginGlobal: 5));
        var real = (await new AccountAdminStore(db.AsApp()).CreateAccountAsync(null, null, Op)).AccountKey.Text;
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++) codes.Add(await LoginAsync(api.Http, OpaqueSecret.New("dk1").Text)); // a new key every time
        Assert.All(codes, c => Assert.Equal(HttpStatusCode.Unauthorized, c));
        Assert.Equal(HttpStatusCode.TooManyRequests, await LoginAsync(api.Http, OpaqueSecret.New("dk1").Text));
        Assert.Equal(HttpStatusCode.TooManyRequests, await LoginAsync(api.Http, real)); // the window is shared by everyone
    }

    [Fact]
    public async Task Repeating_one_refused_key_does_not_drain_the_global_window()
    {
        await using var api = await ApiServer.StartAsync(db, Limits(login: 2, requests: 100, loginGlobal: 4));
        var real = (await new AccountAdminStore(db.AsApp()).CreateAccountAsync(null, null, Op)).AccountKey.Text;
        var hammered = OpaqueSecret.New("dk1").Text;
        for (var i = 0; i < 20; i++) await LoginAsync(api.Http, hammered); // 2 reach the global window, 18 stop at their own
        Assert.Equal(HttpStatusCode.OK, await LoginAsync(api.Http, real));
    }

    [Fact]
    public async Task Authenticated_requests_are_limited_per_account_with_retry_after()
    {
        await using var api = await ApiServer.StartAsync(db, Limits(login: 10, requests: 2));
        var admin = new AccountAdminStore(db.AsApp());
        var chatty = await TokenAsync(api.Http, (await admin.CreateAccountAsync("chatty", null, Op)).AccountKey.Text);
        var quiet = await TokenAsync(api.Http, (await admin.CreateAccountAsync("quiet", null, Op)).AccountKey.Text);
        HttpResponseMessage last = null!;
        for (var i = 0; i < 3; i++) last = await api.Http.SendAsync(Me(chatty));
        Assert.Equal(HttpStatusCode.TooManyRequests, last.StatusCode);
        Assert.Equal("60", last.Headers.GetValues("Retry-After").Single());
        Assert.Equal(HttpStatusCode.OK, (await api.Http.SendAsync(Me(quiet))).StatusCode); // another account has its own window
    }

    static HttpRequestMessage Me(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    static async Task<string> TokenAsync(HttpClient http, string key) =>
        JsonDocument.Parse(await (await http.PostAsJsonAsync("/v1/auth/dev/login", new { accountKey = key })).Content.ReadAsStringAsync())
            .RootElement.GetProperty("accessToken").GetString()!;
}
