using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Blackglass.Accounts.Api.Energy;
using Blackglass.Accounts.Domain.Accounts;
using Blackglass.Accounts.Domain.Auth;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Accounts;
using Blackglass.Accounts.Persistence.Energy;
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
                // The inventory edit route (Stage 5): refused by the rules, by the body, by the content type and by the token.
                foreach (var (body, type) in new[]
                         {
                             ("{\"expectedVersion\":1,\"operations\":[{\"op\":\"equip\",\"operativeId\":\"2\",\"itemId\":\"3\"}]}", "application/json"),
                             ("{\"expectedVersion\":9,\"operations\":[{\"op\":\"equip\",\"operativeId\":\"2\",\"itemId\":\"3\"}]}", "application/json"),
                             ("{\"expectedVersion\":1,\"operations\":[{\"op\":\"equip\",\"operativeId\":\"2\",\"itemId\":\"3\"}],\"accountId\":\"2\"}", "application/json"),
                             ("{\"expectedVersion\":1,\"operations\":[{\"op\":\"nope\"}]}", "application/json"),
                             ("{\"expectedVersion\":1,\"operations\":[]}", "application/json"),
                             ("{not json", "application/json"),
                             ("{\"expectedVersion\":1,\"operations\":[]}", "text/plain"),
                         })
                {
                    var edit = Bearer(HttpMethod.Post, "/v1/inventory/operations", bearer);
                    edit.Content = new StringContent(body, System.Text.Encoding.UTF8, type);
                    await api.Http.SendAsync(edit);
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

    /// <summary>Stage 6 (E4): the reconciliation sweep inside serve logs counts on success and only the SQLSTATE on failure:
    /// never the server's message or detail (which can quote values), a connection detail or a password.</summary>
    [Fact]
    public async Task The_energy_reconciliation_logs_counts_and_sqlstates_only()
    {
        await using var db = await MigratedAsync();
        const string Leak = "server-message-quoting-a-value-k3x";
        long account;
        await using (var conn = await db.AsApp().OpenConnectionAsync())
        await using (var tx = await conn.BeginTransactionAsync())
        {
            account = await conn.ExecuteScalarAsync<long>("insert into accounts (status) values ('active') returning account_id", transaction: tx);
            await conn.ExecuteAsync(
                "insert into energy_wallets (account_id, balance, regen_anchor, regen_progress_us, policy_version) values (@account, 50, clock_timestamp(), 0, 1); " +
                "insert into energy_ledger (account_id, kind, delta, balance_after, idempotency_key) values (@account, 'open', 50, 50, 'open')", new { account }, tx);
            await tx.CommitAsync();
        }
        await using (var su = await db.AsSuperuser().OpenConnectionAsync())
        {
            // A mismatch to find, and an anomaly write that fails with a message and detail that must not be logged.
            await su.ExecuteAsync(
                "begin; set local session_replication_role = replica; update energy_wallets set balance = 51 where account_id = @account; commit;", new { account });
            await su.ExecuteAsync(
                $$"""
                create function fail_anomaly() returns trigger language plpgsql as $$ begin
                  raise exception '{{Leak}} %', new.detail using detail = '{{Leak}}', errcode = 'P0001'; end $$;
                create trigger fail_anomaly before insert on anomaly_events for each row execute function fail_anomaly();
                """);
        }
        var logs = new CapturingLoggerProvider();
        await using (var api = await ApiServer.StartAsync(db, configure: b =>
                     {
                         b.Logging.ClearProviders();
                         b.Logging.SetMinimumLevel(LogLevel.Trace);
                         b.Logging.AddProvider(logs);
                         b.Services.AddSingleton(sp => new EnergyReconcileService(new EnergyReconciler(sp.GetRequiredService<Npgsql.NpgsqlDataSource>()).RunAsync,
                             TimeSpan.FromMilliseconds(200), sp.GetRequiredService<ILoggerFactory>()));
                     }))
        {
            await WaitForAsync(() => logs.Text.Contains("Energy reconciliation failed"));
            await using (var su = await db.AsSuperuser().OpenConnectionAsync())
                await su.ExecuteAsync("drop trigger fail_anomaly on anomaly_events; drop function fail_anomaly()");
            await WaitForAsync(() => logs.Text.Contains("anomalies recorded"));
        }

        var text = logs.Text;
        Assert.Contains("Blackglass.Energy: Energy reconciliation failed: SQLSTATE P0001", text);
        Assert.Contains("Blackglass.Energy: Energy reconciliation checked 1 wallets: 2 mismatches, 2 anomalies recorded", text);
        Assert.DoesNotContain(Leak, text);
        Assert.DoesNotContain("\"balance\"", text);
        Assert.DoesNotContain(db.Password("app"), text);
        Assert.DoesNotContain(db.Name, text);
    }

    static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The expected log line never came.");
            await Task.Delay(20);
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
