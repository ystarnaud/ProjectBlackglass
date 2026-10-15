using System.Net;
using System.Net.Http.Json;
using Blackglass.Accounts.IntegrationTests.Support;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>The test harness leaves no admin key or database password on disk: the API reads its admin key file once,
/// at startup, so the file is removed as soon as startup ends, whether it succeeded or failed; a test database's
/// password files go with the database.</summary>
[Trait("Requires", "Postgres")]
public class SecretCleanupTests
{
    [Fact]
    public async Task The_admin_key_file_is_gone_once_the_api_has_started_and_the_key_still_works()
    {
        await using var db = await MigratedAsync();
        var key = NewAdminKey();
        string? keyFile = null;
        await using var api = await ApiServer.StartAsync(db, new() { ["BLACKGLASS_ADMIN_ROUTES"] = "enabled" }, adminKey: key, keyFileCreated: p => keyFile = p);
        Assert.NotNull(keyFile);
        Assert.False(File.Exists(keyFile));
        var request = new HttpRequestMessage(HttpMethod.Post, "/dev/v1/accounts") { Content = JsonContent.Create(new { @operator = "o", reason = "r" }) };
        request.Headers.Add("X-Admin-Key", key);
        Assert.Equal(HttpStatusCode.Created, (await api.Http.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task The_admin_key_file_is_gone_when_startup_fails()
    {
        await using var db = await MigratedAsync();
        string? keyFile = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ApiServer.StartAsync(db, new() { ["BLACKGLASS_ADMIN_ROUTES"] = "enabled" },
            adminKey: "too-short", keyFileCreated: p => keyFile = p)); // under AuthSettings.MinAdminKeyLength: serve refuses
        Assert.NotNull(keyFile);
        Assert.False(File.Exists(keyFile));
    }

    [Fact]
    public async Task A_test_databases_password_files_are_gone_after_it_is_disposed()
    {
        var db = await (await TestCluster.Instance).CreateDatabaseAsync();
        var files = new[] { db.EnvFor("app"), db.EnvFor("migrator") }.Select(e => e["BLACKGLASS_DB_PASSWORD_FILE"]!).ToList();
        Assert.All(files, f => Assert.True(File.Exists(f)));
        await db.DisposeAsync();
        Assert.All(files, f => Assert.False(File.Exists(f)));
    }
}
