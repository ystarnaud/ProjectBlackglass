using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Blackglass.Accounts.Domain.Accounts;
using Blackglass.Accounts.Domain.Auth;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Accounts;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>Spec §4.4 and §18.2 B2: no key, token, admin key or full hash in any log line, at any level, on any path
/// (success, refusal, malformed input, admin, outage). Account-lookup lines carry the Base58 id.</summary>
[Trait("Requires", "Postgres")]
public class LogSecrecyTests
{
    static readonly string AdminKey = NewAdminKey();

    [Fact]
    public async Task No_key_token_admin_key_or_full_hash_reaches_any_log_line()
    {
        await using var db = await MigratedAsync();
        var logs = new CapturingLoggerProvider();
        var settings = new Dictionary<string, string?> { ["BLACKGLASS_DEV_AUTH"] = "enabled", ["BLACKGLASS_ADMIN_ROUTES"] = "enabled" };
        var secrets = new List<string>();
        var hashes = new List<byte[]>();
        await using (var api = await ApiServer.StartAsync(db, settings, b =>
                     {
                         b.Logging.ClearProviders();
                         b.Logging.SetMinimumLevel(LogLevel.Trace);
                         b.Logging.AddProvider(logs);
                     }, adminKey: AdminKey))
        {
            // An account created through the admin route, and one through the store.
            var create = new HttpRequestMessage(HttpMethod.Post, "/dev/v1/accounts") { Content = JsonContent.Create(new { handle = "logged", @operator = "ops", reason = "log test" }) };
            create.Headers.Add("X-Admin-Key", AdminKey);
            var created = JsonDocument.Parse(await (await api.Http.SendAsync(create)).Content.ReadAsStringAsync()).RootElement;
            var routeKey = created.GetProperty("accountKey").GetString()!;
            var wrongAdmin = new HttpRequestMessage(HttpMethod.Post, "/dev/v1/accounts") { Content = JsonContent.Create(new { @operator = "o", reason = "r" }) };
            wrongAdmin.Headers.Add("X-Admin-Key", "wrong-" + AdminKey);
            await api.Http.SendAsync(wrongAdmin);

            var account = await new AccountAdminStore(db.AsApp()).CreateAccountAsync("quiet", null, Op);
            var login = JsonDocument.Parse(await (await api.Http.PostAsJsonAsync("/v1/auth/dev/login", new { accountKey = account.AccountKey.Text })).Content.ReadAsStringAsync()).RootElement;
            var token = login.GetProperty("accessToken").GetString()!;
            var routeToken = JsonDocument.Parse(await (await api.Http.PostAsJsonAsync("/v1/auth/dev/login", new { accountKey = routeKey })).Content.ReadAsStringAsync()).RootElement
                .GetProperty("accessToken").GetString()!;
            var wrongKey = OpaqueSecret.New("dk1");
            await api.Http.PostAsJsonAsync("/v1/auth/dev/login", new { accountKey = wrongKey.Text });
            await api.Http.PostAsJsonAsync("/v1/auth/dev/login", new { accountKey = "dk1_" + new string('Z', 43) });
            var unknownToken = OpaqueSecret.New("at1");
            foreach (var bearer in new[] { token, unknownToken.Text, "garbage", routeToken })
                await api.Http.SendAsync(Bearer(HttpMethod.Get, "/v1/me", bearer));
            // The player routes (Stage 4): served, refused for the token, refused for the id or the body, and refused by the rules.
            foreach (var bearer in new[] { token, unknownToken.Text, "garbage", routeToken })
            {
                var roster = await api.Http.SendAsync(Bearer(HttpMethod.Get, "/v1/roster", bearer));
                var inventory = await api.Http.SendAsync(Bearer(HttpMethod.Get, "/v1/inventory", bearer));
                var served = bearer == token || bearer == routeToken;
                Assert.Equal(served, roster.IsSuccessStatusCode && inventory.IsSuccessStatusCode);
                foreach (var (id, body) in new[] { ("2", "{\"choiceId\":\"combat\",\"expectedVersion\":1}"), ("2", "{\"choiceId\":\"combat\",\"expectedVersion\":1,\"accountId\":\"2\"}"), ("0", "{not json") })
                {
                    var pick = Bearer(HttpMethod.Post, $"/v1/operatives/{id}/choices", bearer);
                    pick.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
                    await api.Http.SendAsync(pick);
                }
            }
            await api.Http.SendAsync(Bearer(HttpMethod.Post, "/v1/auth/logout", token));
            await api.Http.SendAsync(Bearer(HttpMethod.Post, "/v1/auth/logout", token)); // the repeat

            // During an outage too.
            await ApiServer.SetDatabaseReachableAsync(db, false);
            try
            {
                await api.Http.SendAsync(Bearer(HttpMethod.Get, "/v1/me", routeToken));
                await api.Http.SendAsync(Bearer(HttpMethod.Get, "/v1/roster", routeToken));
                await api.Http.PostAsJsonAsync("/v1/auth/dev/login", new { accountKey = account.AccountKey.Text });
            }
            finally { await ApiServer.SetDatabaseReachableAsync(db, true); }

            var lookups = new[] { AccountId.Format(account.AccountId), created.GetProperty("accountId").GetString()! };
            var text = logs.Text;
            Assert.Contains("Authentication failed", text); // the capture works and failures are logged
            Assert.Contains("Admin request refused", text);
            Assert.Contains("Database unavailable", text);
            Assert.All(lookups, id => Assert.Contains($"account {id}", text)); // account-lookup lines use the Base58 id

            secrets.AddRange([account.AccountKey.Text[4..], routeKey[4..], token[4..], routeToken[4..], wrongKey.Text[4..], unknownToken.Text[4..], AdminKey]);
            hashes.AddRange([account.AccountKey.Hash(), wrongKey.Hash(), unknownToken.Hash(), Parse(token).Hash(), Parse(routeToken).Hash(), Parse(routeKey, "dk1").Hash(),
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(AdminKey))]);
            foreach (var secret in secrets) Assert.DoesNotContain(secret, text);
            foreach (var hash in hashes)
            {
                Assert.DoesNotContain(Convert.ToHexStringLower(hash)[..9], text); // nothing longer than the 8-hex prefix
                Assert.DoesNotContain(Convert.ToHexString(hash)[..9], text);
                Assert.DoesNotContain(Convert.ToBase64String(hash)[..12], text);
            }
            Assert.DoesNotContain(db.Password("app"), text);
        }
    }

    static OpaqueSecret Parse(string text, string prefix = "at1") =>
        OpaqueSecret.TryParse(text, prefix, out var secret) ? secret! : throw new InvalidOperationException("not a secret");

    static HttpRequestMessage Bearer(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        return request;
    }
}
