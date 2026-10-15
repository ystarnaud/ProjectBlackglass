using Blackglass.Accounts.Api.Auth;
using Blackglass.Accounts.IntegrationTests.Support;

namespace Blackglass.Accounts.IntegrationTests.Unit;

public class AuthSettingsTests
{
    static Dictionary<string, string?> Env(params (string Key, string? Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);


    [Fact]
    public void Everything_is_off_by_default_with_the_spec_defaults()
    {
        var s = AuthSettings.FromEnvironment(Env());
        Assert.False(s.DevAuthEnabled);
        Assert.False(s.AdminRoutesEnabled);
        Assert.Null(s.AdminKeyHash);
        Assert.Equal((60, 10, 60, 120, 10), (s.TokenLifetimeMinutes, s.LoginPerMinute, s.LoginGlobalPerMinute, s.RequestsPerMinute, s.AdminPerMinute));
    }

    [Theory]
    [InlineData("BLACKGLASS_DEV_AUTH", "on")]
    [InlineData("BLACKGLASS_DEV_AUTH", "Enabled")]
    [InlineData("BLACKGLASS_ADMIN_ROUTES", "true")]
    [InlineData("BLACKGLASS_TOKEN_LIFETIME_MINUTES", "0")]
    [InlineData("BLACKGLASS_TOKEN_LIFETIME_MINUTES", "1441")]
    [InlineData("BLACKGLASS_TOKEN_LIFETIME_MINUTES", "60m")]
    [InlineData("BLACKGLASS_RATE_LOGIN_PER_MINUTE", "-1")]
    [InlineData("BLACKGLASS_RATE_REQUESTS_PER_MINUTE", "0")]
    [InlineData("BLACKGLASS_RATE_LOGIN_GLOBAL_PER_MINUTE", "0")]
    [InlineData("BLACKGLASS_RATE_LOGIN_GLOBAL_PER_MINUTE", "100001")]
    [InlineData("BLACKGLASS_RATE_ADMIN_PER_MINUTE", "0")]
    [InlineData("BLACKGLASS_RATE_ADMIN_PER_MINUTE", "1001")]
    [InlineData("BLACKGLASS_RATE_ADMIN_PER_MINUTE", " 5")]
    [InlineData("BLACKGLASS_DEV_AUTH", " enabled")]
    public void A_bad_value_refuses_to_start(string key, string value) =>
        Assert.Throws<AuthSettingsException>(() => AuthSettings.FromEnvironment(Env((key, value))));

    [Fact]
    public void Admin_routes_need_a_key_file()
    {
        var e = Assert.Throws<AuthSettingsException>(() => AuthSettings.FromEnvironment(Env(("BLACKGLASS_ADMIN_ROUTES", "enabled"))));
        Assert.Contains("BLACKGLASS_ADMIN_KEY_FILE", e.Message);
    }

    [Fact]
    public void A_short_admin_key_is_refused_without_revealing_it()
    {
        using var file = SecretFile.Write("short-secret-value");
        var e = Assert.Throws<AuthSettingsException>(() => AuthSettings.FromEnvironment(
            Env(("BLACKGLASS_ADMIN_ROUTES", "enabled"), ("BLACKGLASS_ADMIN_KEY_FILE", file.Path))));
        Assert.DoesNotContain("short-secret-value", e.Message);
    }

    [Fact]
    public void The_admin_key_is_kept_only_as_its_hash()
    {
        var key = new string('k', 48);
        using var file = SecretFile.Write(key + "\n");
        var s = AuthSettings.FromEnvironment(Env(("BLACKGLASS_ADMIN_ROUTES", "enabled"), ("BLACKGLASS_ADMIN_KEY_FILE", file.Path)));
        Assert.Equal(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)), s.AdminKeyHash);
    }

    [Fact]
    public void Enabled_dev_auth_and_custom_limits_are_read()
    {
        var s = AuthSettings.FromEnvironment(Env(("BLACKGLASS_DEV_AUTH", "enabled"), ("BLACKGLASS_TOKEN_LIFETIME_MINUTES", "5"),
            ("BLACKGLASS_RATE_LOGIN_PER_MINUTE", "2"), ("BLACKGLASS_RATE_REQUESTS_PER_MINUTE", "3"),
            ("BLACKGLASS_RATE_LOGIN_GLOBAL_PER_MINUTE", "4"), ("BLACKGLASS_RATE_ADMIN_PER_MINUTE", "6")));
        Assert.True(s.DevAuthEnabled);
        Assert.Equal((5, 2, 4, 3, 6), (s.TokenLifetimeMinutes, s.LoginPerMinute, s.LoginGlobalPerMinute, s.RequestsPerMinute, s.AdminPerMinute));
    }

    [Fact]
    public void An_unreadable_admin_key_file_refuses_to_start() =>
        Assert.Throws<AuthSettingsException>(() => AuthSettings.FromEnvironment(
            Env(("BLACKGLASS_ADMIN_ROUTES", "enabled"), ("BLACKGLASS_ADMIN_KEY_FILE", Path.Combine(Path.GetTempPath(), $"bg-missing-{Guid.NewGuid():N}")))));

    [Fact]
    public void Admin_routes_off_ignore_the_key_file() =>
        Assert.Null(AuthSettings.FromEnvironment(Env(("BLACKGLASS_ADMIN_KEY_FILE", "/nonexistent"))).AdminKeyHash);
}
