namespace Blackglass.Accounts.Api;

public static class ApiHost
{
    /// <summary>Loopback only (spec §4.5). In the container the image sets ASPNETCORE_HTTP_PORTS=8080 and Docker
    /// publishes that port on 127.0.0.1 only.</summary>
    public const string DefaultUrl = "http://127.0.0.1:5080";

    /// <summary>The URL to pass to UseUrls, or null to let ASP.NET Core read ASPNETCORE_HTTP_PORTS itself.</summary>
    public static string? ResolveUrls(IReadOnlyDictionary<string, string?> env)
    {
        if (env.TryGetValue("ASPNETCORE_URLS", out var urls) && !string.IsNullOrWhiteSpace(urls)) return urls;
        if (env.TryGetValue("ASPNETCORE_HTTP_PORTS", out var ports) && !string.IsNullOrWhiteSpace(ports)) return null;
        return DefaultUrl;
    }
}
