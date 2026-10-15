using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Blackglass.Accounts.Domain.Accounts;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Accounts;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>
/// Review Focus 7 (IDOR): account ids are sequential and public by design (decision 048 amendment), so account A,
/// knowing or guessing B's id (B = A + 1), must get nothing of B's from any route, header or body. Every client
/// request's account is the validated session's.
/// </summary>
[Trait("Requires", "Postgres")]
public class AuthorizationTests : IAsyncLifetime
{
    static readonly string AdminKey = NewAdminKey();
    TestDatabase db = null!;
    ApiServer api = null!;
    IssuedCredential a = null!, b = null!;
    string tokenA = null!, tokenB = null!;

    public async Task InitializeAsync()
    {
        db = await MigratedAsync();
        api = await ApiServer.StartAsync(db, new()
        {
            ["BLACKGLASS_DEV_AUTH"] = "enabled", ["BLACKGLASS_ADMIN_ROUTES"] = "enabled",
            ["BLACKGLASS_RATE_ADMIN_PER_MINUTE"] = "1000", // this class sends many refused admin requests on purpose
        }, adminKey: AdminKey);
        var admin = new AccountAdminStore(db.AsApp());
        a = await admin.CreateAccountAsync("alice", null, Op);
        b = await admin.CreateAccountAsync("bob", null, Op);
        Assert.Equal(a.AccountId + 1, b.AccountId); // B's id is the obvious guess
        tokenA = await TokenAsync(a);
        tokenB = await TokenAsync(b);
    }

    public async Task DisposeAsync()
    {
        await api.DisposeAsync();
        try { Assert.Empty(await InvariantFailuresAsync(db)); }
        finally { await db.DisposeAsync(); }
    }

    string IdA => AccountId.Format(a.AccountId);
    string IdB => AccountId.Format(b.AccountId);

    async Task<string> TokenAsync(IssuedCredential account) =>
        JsonDocument.Parse(await (await api.Http.PostAsJsonAsync("/v1/auth/dev/login", new { accountKey = account.AccountKey.Text })).Content.ReadAsStringAsync())
            .RootElement.GetProperty("accessToken").GetString()!;

    static HttpRequestMessage As(string? token, HttpMethod method, string path, string? json = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return request;
    }

    async Task<string> MeAsync(HttpRequestMessage request)
    {
        var response = await api.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task A_guessed_id_in_the_query_a_header_or_a_body_never_changes_whose_account_me_returns()
    {
        var expected = $"{{\"accountId\":\"{IdA}\"}}";
        Assert.Equal(expected, await MeAsync(As(tokenA, HttpMethod.Get, $"/v1/me?accountId={IdB}")));
        Assert.Equal(expected, await MeAsync(As(tokenA, HttpMethod.Get, $"/v1/me?account_id={b.AccountId}&sub={b.AccountId}")));
        var header = As(tokenA, HttpMethod.Get, "/v1/me");
        header.Headers.Add("X-Account-Id", IdB);
        Assert.Equal(expected, await MeAsync(header));
        Assert.Equal(expected, await MeAsync(As(tokenA, HttpMethod.Get, "/v1/me", $"{{\"accountId\":\"{IdB}\"}}")));
    }

    [Fact]
    public async Task There_is_no_route_that_reads_an_account_by_id_so_a_guess_and_your_own_id_get_the_same_404()
    {
        foreach (var path in new[] { $"/v1/accounts/{IdB}", $"/v1/accounts/{IdA}", $"/v1/me/{IdB}", $"/v1/accounts/{b.AccountId}" })
        {
            var response = await api.Http.SendAsync(As(tokenA, HttpMethod.Get, path));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Logout_with_a_guessed_id_in_the_body_or_query_revokes_only_the_callers_session()
    {
        var response = await api.Http.SendAsync(As(tokenA, HttpMethod.Post, $"/v1/auth/logout?accountId={IdB}", $"{{\"accountId\":\"{IdB}\"}}"));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Http.SendAsync(As(tokenA, HttpMethod.Get, "/v1/me"))).StatusCode);
        Assert.Equal($"{{\"accountId\":\"{IdB}\"}}", await MeAsync(As(tokenB, HttpMethod.Get, "/v1/me")));
    }

    [Fact]
    public async Task Knowing_an_account_id_is_not_a_credential_anywhere()
    {
        // Every id from 1 to a little past the last one, in Base58 and decimal, as a bearer token, a login key and an admin key.
        for (var guess = 1L; guess <= b.AccountId + 3; guess++)
        {
            foreach (var form in new[] { AccountId.Format(guess), guess.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            {
                Assert.Equal(HttpStatusCode.Unauthorized, (await api.Http.SendAsync(As(form, HttpMethod.Get, "/v1/me"))).StatusCode);
                Assert.Equal(HttpStatusCode.Unauthorized, (await api.Http.PostAsJsonAsync("/v1/auth/dev/login", new { accountKey = form })).StatusCode);
                var adminCreate = As(null, HttpMethod.Post, "/dev/v1/accounts", "{\"operator\":\"o\",\"reason\":\"r\"}");
                adminCreate.Headers.Add("X-Admin-Key", form);
                Assert.Equal(HttpStatusCode.Unauthorized, (await api.Http.SendAsync(adminCreate)).StatusCode);
            }
        }
        Assert.Equal(2L, await ScalarAsSuperuserAsync<long>(db, "select count(*) from accounts"));
    }

    [Fact]
    public async Task A_valid_but_nonexistent_id_and_a_malformed_one_get_the_same_answers_as_a_real_one()
    {
        // No client route accepts an account id, so none can say whether an id exists: all three look alike.
        var answers = new List<(HttpStatusCode, string)>();
        foreach (var id in new[] { IdB, AccountId.Format(999_999), "0OIl" })
        {
            var response = await api.Http.SendAsync(As(tokenA, HttpMethod.Get, $"/v1/accounts/{id}"));
            answers.Add((response.StatusCode, await response.Content.ReadAsStringAsync()));
        }
        Assert.Single(answers.Distinct());
    }

    [Fact]
    public async Task A_player_token_cannot_reach_an_admin_operation()
    {
        var request = As(tokenA, HttpMethod.Post, "/dev/v1/accounts", "{\"operator\":\"o\",\"reason\":\"r\"}");
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Http.SendAsync(request)).StatusCode);
        Assert.Equal(2L, await ScalarAsSuperuserAsync<long>(db, "select count(*) from accounts"));
    }
}
