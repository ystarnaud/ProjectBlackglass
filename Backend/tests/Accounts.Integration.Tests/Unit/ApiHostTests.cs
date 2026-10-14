using Blackglass.Accounts.Api;

namespace Blackglass.Accounts.IntegrationTests.Unit;

public class ApiHostTests
{
    [Fact]
    public void Without_configuration_the_api_binds_loopback_only()
    {
        Assert.Equal("http://127.0.0.1:5080", ApiHost.ResolveUrls(new Dictionary<string, string?>()));
        Assert.Equal(ApiHost.DefaultUrl, ApiHost.ResolveUrls(new Dictionary<string, string?>()));
    }

    [Fact]
    public void An_explicit_url_wins()
    {
        var env = new Dictionary<string, string?> { ["ASPNETCORE_URLS"] = "http://127.0.0.1:0" };
        Assert.Equal("http://127.0.0.1:0", ApiHost.ResolveUrls(env));
    }

    [Fact]
    public void Inside_the_container_the_image_port_setting_is_left_to_aspnet()
    {
        var env = new Dictionary<string, string?> { ["ASPNETCORE_HTTP_PORTS"] = "8080" };
        Assert.Null(ApiHost.ResolveUrls(env));
    }
}
