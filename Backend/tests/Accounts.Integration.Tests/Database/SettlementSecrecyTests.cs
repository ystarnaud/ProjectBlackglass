using System.Net.Http.Headers;
using System.Text;
using Dapper;
using Microsoft.Extensions.Logging;
using Blackglass.Accounts.Api.Admin;
using Blackglass.Accounts.Domain.Ids;
using Blackglass.Accounts.Domain.Settlement;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Hosts;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>Stage 9 secrecy (plan Task 10): across settlement, quarantine, a malformed body, a revoked attempt's refusal, the recorded
/// hash, an evidence void, the receipt and the recovery CLI, no seed, salt, host secret, process credential or raw manifest reaches a
/// log line (any level), an anomaly's detail, an operator action, a receipt or the CLI's output.</summary>
[Trait("Requires", "Postgres")]
public class SettlementSecrecyTests
{
    [Fact]
    public async Task No_secret_or_manifest_reaches_logs_anomalies_actions_receipts_or_cli_output()
    {
        await using var db = await MigratedAsync();
        var kit = await MissionKit.CreateAsync(db);
        var logs = new CapturingLoggerProvider();
        var outputs = new List<string>();
        var secrets = new List<string>();
        var manifests = new List<string>();
        await using (var api = await ApiServer.StartAsync(db, new() { ["BLACKGLASS_DEV_AUTH"] = "enabled" }, b =>
                     {
                         b.Logging.ClearProviders();
                         b.Logging.SetMinimumLevel(LogLevel.Trace);
                         b.Logging.AddProvider(logs);
                     }))
        {
            var hostSecret = (await new HostAdminStore(db.AsApp()).RotateSecretAsync("h1", Op)).Secret.Text;
            secrets.Add(hostSecret);
            async Task<string> Post(string path, string body, string bearer)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                var response = await api.Http.SendAsync(request);
                var text = await response.Content.ReadAsStringAsync();
                outputs.Add(text);
                return text;
            }
            var settled = await ManifestKit.StartAsync(kit, db);
            var quarantined = await ManifestKit.StartAsync(kit, db);
            var voided = await ManifestKit.StartAsync(kit, db);
            foreach (var r in new[] { settled, quarantined, voided }) secrets.Add(r.Mission.Credential.Text);
            var good = Encoding.UTF8.GetString(ManifestKit.Manifest(settled).ToCanonicalBytes());
            var bad = Encoding.UTF8.GetString((ManifestKit.Manifest(quarantined) with { Integrity = "ledgerMismatch" }).ToCanonicalBytes());
            manifests.AddRange([good, bad]);
            await Post($"/server/v1/attempts/{settled.Attempt}/participants/{Base58Id.Format(settled.Account)}/recorded",
                $"{{\"manifestHash\":\"{ResultManifest.HashOf(Encoding.UTF8.GetBytes(good))}\"}}", settled.Mission.Credential.Text);
            await Post("/agent/v1/results", good, hostSecret);
            await Post("/agent/v1/results", good, hostSecret);
            await Post("/agent/v1/results", bad, hostSecret);
            await Post("/agent/v1/results", "{\"formatVersion\":1,\"oops\":true}", hostSecret);
            await Post("/agent/v1/results", good, "hs1_" + new string('Q', 43));
            await Post($"/server/v1/attempts/{settled.Attempt}/participants/{Base58Id.Format(settled.Account)}/recorded",
                $"{{\"manifestHash\":\"sha256:{new string('d', 64)}\"}}", settled.Mission.Credential.Text);
            await Post($"/agent/v1/attempts/{voided.Attempt}/exited",
                $"{{\"exitCode\":137,\"signal\":null,\"results\":[{{\"accountId\":\"{Base58Id.Format(voided.Account)}\",\"present\":false}}],\"outbox\":{{\"complete\":true}}}}", hostSecret);
            var token = System.Text.Json.JsonDocument.Parse(await (await api.Http.PostAsync("/v1/auth/dev/login",
                new StringContent($"{{\"accountKey\":\"{settled.AccountKey}\"}}", Encoding.UTF8, "application/json"))).Content.ReadAsStringAsync()).RootElement.GetProperty("accessToken").GetString()!;
            secrets.AddRange([token, settled.AccountKey]);
            using (var receipt = new HttpRequestMessage(HttpMethod.Get, $"/v1/attempts/{settled.Attempt}/receipt"))
            {
                receipt.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                outputs.Add(await (await api.Http.SendAsync(receipt)).Content.ReadAsStringAsync());
            }
            // A revoked attempt's relay after a rotation: refused, with only a payload-free anomaly.
            var revoked = await ManifestKit.StartAsync(kit, db);
            secrets.Add(revoked.Mission.Credential.Text);
            var admin = new HostAdminStore(db.AsApp());
            await admin.DisableAsync("h1", Op);
            var rotated = (await admin.RotateSecretAsync("h1", Op)).Secret.Text;
            secrets.Add(rotated);
            await admin.SwitchAsync("h1", HostSwitch.Enable, Op);
            var revokedManifest = Encoding.UTF8.GetString(ManifestKit.Manifest(revoked).ToCanonicalBytes());
            manifests.Add(revokedManifest);
            await Post("/agent/v1/results", revokedManifest, rotated);

            // The recovery CLI on the revoked attempt and the quarantined participant.
            var file = Path.Combine(Path.GetTempPath(), $"bg-import-{Guid.NewGuid():N}.json");
            try
            {
                await File.WriteAllTextAsync(file, revokedManifest);
                async Task Cli(params string[] args)
                {
                    var (o, e) = (new StringWriter(), new StringWriter());
                    await AdminCommand.RunAsync([.. args, "--operator", "ops", "--reason", "secrecy test", "--yes"], db.EnvFor("app"), o, e, default);
                    outputs.Add(o.ToString() + e.ToString());
                }
                var target = new[] { "--attempt", revoked.Attempt.ToString(), "--account-base58", Base58Id.Format(revoked.Account) };
                await Cli(["attempt", "import-result", .. target, "--file", file, "--evidence", "outbox copy", "--host-trust-reviewed"]);
                var imported = await ScalarAsSuperuserAsync<long>(db, "select max(submission_id) from result_submissions where attempt_id = @Attempt", new { revoked.Attempt });
                await Cli(["attempt", "revalidate", .. target, "--submission", imported.ToString(System.Globalization.CultureInfo.InvariantCulture), "--host-trust-reviewed"]);
                await Cli("attempt", "show", "--attempt", revoked.Attempt.ToString());
                await Cli("attempt", "show", "--attempt", quarantined.Attempt.ToString());
                await Cli("anomaly", "list");
            }
            finally { File.Delete(file); }
        }

        // The secrets that exist only in the database: every attempt's seed and salt.
        foreach (var (seed, salt) in await RowsAsSuperuserAsync<(int, byte[])>(db, "select seed, retention_salt from attempt_secrets"))
        {
            secrets.Add($"\"seed\":{seed}");
            secrets.Add($"seed={seed}");
            secrets.Add(Convert.ToHexStringLower(salt));
            secrets.Add(Convert.ToBase64String(salt));
        }
        var stored = new List<string>();
        stored.AddRange(await RowsAsSuperuserAsync<string>(db, "select detail::text from anomaly_events"));
        stored.AddRange(await RowsAsSuperuserAsync<string>(db, "select checks::text || result::text from operator_actions"));
        stored.AddRange(await RowsAsSuperuserAsync<string>(db, "select receipt from participant_settlements"));
        var places = new List<(string Where, string Text)> { ("log", logs.Text) };
        places.AddRange(stored.Select(x => ("database", x)));
        places.AddRange(outputs.Select(x => ("output", x)));
        Assert.False(string.IsNullOrEmpty(logs.Text));
        Assert.NotEmpty(stored);
        foreach (var (where, text) in places)
        {
            foreach (var secret in secrets) Assert.False(text.Contains(secret, StringComparison.Ordinal), $"a secret reached a {where} line");
            foreach (var manifest in manifests) Assert.False(text.Contains(manifest, StringComparison.Ordinal), $"a raw manifest reached a {where} line");
            Assert.False(text.Contains("\"layoutHash\"", StringComparison.Ordinal) && where != "output", $"a manifest member reached a {where} line");
        }
    }
}
