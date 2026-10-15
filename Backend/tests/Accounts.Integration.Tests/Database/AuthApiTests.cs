using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Blackglass.Accounts.Domain.Accounts;
using Blackglass.Accounts.Domain.Auth;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Accounts;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

[Trait("Requires", "Postgres")]
public class AuthApiTests : IAsyncLifetime
{
    static readonly Dictionary<string, string?> DevOn = new() { ["BLACKGLASS_DEV_AUTH"] = "enabled" };
    TestDatabase db = null!;
    AccountAdminStore admin = null!;

    public async Task InitializeAsync() { db = await MigratedAsync(); admin = new AccountAdminStore(db.AsApp()); }

    public async Task DisposeAsync()
    {
        try { Assert.Empty(await InvariantFailuresAsync(db)); }
        finally { await db.DisposeAsync(); }
    }

    static async Task<(HttpStatusCode Status, JsonElement Body, HttpResponseMessage Response)> LoginAsync(HttpClient http, string? key)
    {
        var response = await http.PostAsJsonAsync("/v1/auth/dev/login", new { accountKey = key });
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone(), response);
    }

    static async Task<string> TokenAsync(HttpClient http, OpaqueSecret key) =>
        (await LoginAsync(http, key.Text)).Body.GetProperty("accessToken").GetString()!;

    static HttpRequestMessage With(HttpMethod method, string path, string? token)
    {
        var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    static HttpRequestMessage Me(string? token) => With(HttpMethod.Get, "/v1/me", token);
    static HttpRequestMessage Logout(string? token) => With(HttpMethod.Post, "/v1/auth/logout", token);

    /// <summary>The one 401 every authentication failure gets: no body and a bare Bearer challenge.</summary>
    static async Task AssertUniform401Async(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().ToString());
    }

    [Fact]
    public async Task Dev_login_is_not_mapped_unless_enabled()
    {
        await using var api = await ApiServer.StartAsync(db);
        var account = await admin.CreateAccountAsync("off", null, Op);
        Assert.Equal(HttpStatusCode.NotFound, (await LoginAsync(api.Http, account.AccountKey.Text)).Status);
    }

    [Fact]
    public async Task Login_returns_a_token_its_expiry_and_the_base58_account_id_and_me_returns_that_id_with_the_inventory_version()
    {
        await using var api = await ApiServer.StartAsync(db, DevOn);
        var account = await admin.CreateAccountAsync("me-1", null, Op);
        var (status, body, response) = await LoginAsync(api.Http, account.AccountKey.Text);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal(JsonValueKind.String, body.GetProperty("accountId").ValueKind);
        Assert.Equal(AccountId.Format(account.AccountId), body.GetProperty("accountId").GetString());
        Assert.InRange(body.GetProperty("expiresAt").GetDateTimeOffset() - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(59), TimeSpan.FromMinutes(61));
        Assert.Equal(["accessToken", "expiresAt", "accountId"], body.EnumerateObject().Select(p => p.Name).ToArray());
        var token = body.GetProperty("accessToken").GetString()!;
        Assert.True(OpaqueSecret.TryParse(token, OpaqueSecret.AccessTokenPrefix, out _));

        var me = await api.Http.SendAsync(Me(token));
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal($"{{\"accountId\":\"{AccountId.Format(account.AccountId)}\",\"inventoryVersion\":1}}", await me.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Every_bad_login_is_the_same_401_whether_or_not_the_key_or_account_exists()
    {
        await using var api = await ApiServer.StartAsync(db, DevOn);
        var revoked = await admin.CreateAccountAsync(null, null, Op);
        await admin.RevokeCredentialAsync(revoked.CredentialId, Op);
        var suspended = await admin.CreateAccountAsync(null, null, Op);
        await admin.SuspendAsync(suspended.AccountId, Op);
        foreach (var key in new[] { OpaqueSecret.New("dk1").Text, "not-a-key", "", null, OpaqueSecret.New("at1").Text,
                     revoked.AccountKey.Text, suspended.AccountKey.Text, AccountId.Format(suspended.AccountId) })
        {
            var (_, _, response) = await LoginAsync(api.Http, key);
            await AssertUniform401Async(response);
            Assert.True(response.Headers.CacheControl!.NoStore);
        }
    }

    [Fact]
    public async Task An_oversized_login_body_is_refused_before_it_is_read()
    {
        await using var api = await ApiServer.StartAsync(db, DevOn);
        var body = new StringContent($"{{\"accountKey\":\"{new string('A', 100_000)}\"}}", System.Text.Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await api.Http.PostAsync("/v1/auth/dev/login", body)).StatusCode);
    }

    [Theory]
    [InlineData("{not json", "application/json")]
    [InlineData("[]", "application/json")]
    [InlineData("{\"accountKey\":5}", "application/json")]
    [InlineData("{\"accountKey\":[\"dk1_x\"]}", "application/json")]
    [InlineData("dk1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "text/plain")]
    public async Task A_malformed_login_body_is_a_client_error_never_a_500(string json, string mediaType)
    {
        await using var api = await ApiServer.StartAsync(db, DevOn);
        var response = await api.Http.PostAsync("/v1/auth/dev/login", new StringContent(json, System.Text.Encoding.UTF8, mediaType));
        Assert.InRange((int)response.StatusCode, 400, 415);
    }

    [Fact]
    public async Task Me_without_a_token_is_the_uniform_401()
    {
        await using var api = await ApiServer.StartAsync(db, DevOn);
        await AssertUniform401Async(await api.Http.SendAsync(Me(null)));
    }

    [Theory]
    [InlineData("Bearer at1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB")]   // non-canonical trailing bits
    [InlineData("bearer at1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]   // lower-case scheme
    [InlineData("Bearer  at1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]  // two spaces
    [InlineData("Bearer\tat1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("Bearer at1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA ")]  // trailing space
    [InlineData("Bearer at1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("Bearer dk1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]   // an account key is not a token
    [InlineData("Bearer")]
    [InlineData("Bearer ")]
    [InlineData("Bearer BukQL")]                                              // an account id is not a token
    public async Task Malformed_authorization_values_are_the_uniform_401(string header)
    {
        await using var api = await ApiServer.StartAsync(db, DevOn);
        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/me");
        request.Headers.TryAddWithoutValidation("Authorization", header);
        await AssertUniform401Async(await api.Http.SendAsync(request));
    }

    [Fact]
    public async Task Two_authorization_headers_or_a_huge_one_are_refused_not_500()
    {
        await using var api = await ApiServer.StartAsync(db, DevOn);
        var account = await admin.CreateAccountAsync("dup", null, Op);
        var token = await TokenAsync(api.Http, account.AccountKey);
        var twice = new HttpRequestMessage(HttpMethod.Get, "/v1/me");
        twice.Headers.TryAddWithoutValidation("Authorization", new[] { "Bearer " + token, "Bearer " + token });
        await AssertUniform401Async(await api.Http.SendAsync(twice));
        var big = new HttpRequestMessage(HttpMethod.Get, "/v1/me");
        big.Headers.TryAddWithoutValidation("Authorization", "Bearer " + new string('A', 20_000));
        await AssertUniform401Async(await api.Http.SendAsync(big));
        var huge = new HttpRequestMessage(HttpMethod.Get, "/v1/me");
        huge.Headers.TryAddWithoutValidation("Authorization", "Bearer " + new string('A', 1_000_000));
        Assert.Equal(HttpStatusCode.RequestHeaderFieldsTooLarge, (await api.Http.SendAsync(huge)).StatusCode);
    }

    [Fact]
    public async Task Two_separate_authorization_fields_are_401_even_when_both_are_valid()
    {
        await using var api = await ApiServer.StartAsync(db, DevOn);
        var account = await admin.CreateAccountAsync(null, null, Op);
        var token = await TokenAsync(api.Http, account.AccountKey);
        Assert.Equal(200, await api.SendRawAsync($"GET /v1/me HTTP/1.1\nAuthorization: Bearer {token}\n"));
        Assert.Equal(401, await api.SendRawAsync($"GET /v1/me HTTP/1.1\nAuthorization: Bearer {token}\nAuthorization: Bearer {token}\n"));
    }

    [Theory]
    [InlineData("logout")]
    [InlineData("rotate")]
    [InlineData("revoke-credential")]
    [InlineData("revoke-sessions")]
    [InlineData("suspend")]
    [InlineData("expire")]
    public async Task A_revoked_token_fails_on_the_next_request(string how)
    {
        await using var api = await ApiServer.StartAsync(db, DevOn);
        var account = await admin.CreateAccountAsync("rev-" + how, null, Op);
        var token = await TokenAsync(api.Http, account.AccountKey);
        Assert.Equal(HttpStatusCode.OK, (await api.Http.SendAsync(Me(token))).StatusCode);
        switch (how)
        {
            case "logout": Assert.Equal(HttpStatusCode.NoContent, (await api.Http.SendAsync(Logout(token))).StatusCode); break;
            case "rotate": await admin.RotateCredentialAsync(account.AccountId, Op); break;
            case "revoke-credential": await admin.RevokeCredentialAsync(account.CredentialId, Op); break;
            case "revoke-sessions": await admin.RevokeSessionsAsync(account.AccountId, Op); break;
            case "suspend": await admin.SuspendAsync(account.AccountId, Op); break;
            case "expire":
                await ExecuteAsSuperuserAsync(db, "update auth_sessions set created_at = now() - interval '2 hours', expires_at = now() - interval '1 second' where account_id = @a", new { a = account.AccountId });
                break;
        }
        await AssertUniform401Async(await api.Http.SendAsync(Me(token)));
    }

    [Fact]
    public async Task Reactivation_does_not_resurrect_a_token()
    {
        await using var api = await ApiServer.StartAsync(db, DevOn);
        var account = await admin.CreateAccountAsync(null, null, Op);
        var token = await TokenAsync(api.Http, account.AccountKey);
        await admin.SuspendAsync(account.AccountId, Op);
        await admin.ReactivateAsync(account.AccountId, Op);
        await AssertUniform401Async(await api.Http.SendAsync(Me(token)));
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(api.Http, account.AccountKey.Text)).Status);
    }

    /// <summary>The selected repeat behaviour (plan Revision 2, Task 7): logout needs a valid session, so a repeat is
    /// the uniform 401 any unknown token gets. It changes nothing and tells the caller nothing.</summary>
    [Fact]
    public async Task Repeated_logout_is_the_uniform_401_and_changes_nothing()
    {
        await using var api = await ApiServer.StartAsync(db, DevOn);
        var account = await admin.CreateAccountAsync(null, null, Op);
        var token = await TokenAsync(api.Http, account.AccountKey);
        var other = await TokenAsync(api.Http, account.AccountKey);
        Assert.Equal(HttpStatusCode.NoContent, (await api.Http.SendAsync(Logout(token))).StatusCode);
        var revokedAt = await ScalarAsSuperuserAsync<DateTime>(db, "select max(revoked_at) from auth_sessions");
        await AssertUniform401Async(await api.Http.SendAsync(Logout(token)));
        await AssertUniform401Async(await api.Http.SendAsync(Logout(OpaqueSecret.New("at1").Text))); // an arbitrary token: the same answer
        Assert.Equal(revokedAt, await ScalarAsSuperuserAsync<DateTime>(db, "select max(revoked_at) from auth_sessions"));
        Assert.Equal(HttpStatusCode.OK, (await api.Http.SendAsync(Me(other))).StatusCode);
    }

    [Fact]
    public async Task Logout_revokes_only_the_current_session()
    {
        await using var api = await ApiServer.StartAsync(db, DevOn);
        var account = await admin.CreateAccountAsync(null, null, Op);
        var stranger = await admin.CreateAccountAsync(null, null, Op);
        var first = await TokenAsync(api.Http, account.AccountKey);
        var second = await TokenAsync(api.Http, account.AccountKey);
        var theirs = await TokenAsync(api.Http, stranger.AccountKey);
        Assert.Equal(HttpStatusCode.NoContent, (await api.Http.SendAsync(Logout(first))).StatusCode);
        await AssertUniform401Async(await api.Http.SendAsync(Me(first)));
        Assert.Equal(HttpStatusCode.OK, (await api.Http.SendAsync(Me(second))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.Http.SendAsync(Me(theirs))).StatusCode);
    }

    [Fact]
    public async Task Authentication_during_a_database_outage_is_503_never_401()
    {
        await using var api = await ApiServer.StartAsync(db, DevOn);
        var account = await admin.CreateAccountAsync("outage", null, Op);
        var token = await TokenAsync(api.Http, account.AccountKey);
        await ApiServer.SetDatabaseReachableAsync(db, false);
        try
        {
            var me = await api.Http.SendAsync(Me(token));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, me.StatusCode);
            Assert.Equal("", await me.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await api.Http.SendAsync(Me(OpaqueSecret.New("at1").Text))).StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await LoginAsync(api.Http, account.AccountKey.Text)).Status);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await LoginAsync(api.Http, OpaqueSecret.New("dk1").Text)).Status);
        }
        finally { await ApiServer.SetDatabaseReachableAsync(db, true); }
        Assert.Equal(HttpStatusCode.OK, (await api.Http.SendAsync(Me(token))).StatusCode);
    }

    [Fact]
    public async Task The_account_never_comes_from_the_body_or_query()
    {
        await using var api = await ApiServer.StartAsync(db, DevOn);
        var mine = await admin.CreateAccountAsync("mine", null, Op);
        var other = await admin.CreateAccountAsync("other", null, Op);
        var token = await TokenAsync(api.Http, mine.AccountKey);
        var request = Me(token);
        request.RequestUri = new Uri($"/v1/me?accountId={AccountId.Format(other.AccountId)}", UriKind.Relative);
        Assert.Equal($"{{\"accountId\":\"{AccountId.Format(mine.AccountId)}\",\"inventoryVersion\":1}}", await (await api.Http.SendAsync(request)).Content.ReadAsStringAsync());
    }
}
