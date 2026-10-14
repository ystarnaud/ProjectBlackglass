using System.Text.RegularExpressions;
using Blackglass.Accounts.IntegrationTests.Support;

namespace Blackglass.Accounts.IntegrationTests.Unit;

public class ComposeExposureTests
{
    public static TheoryData<string> Files => new() { "compose.yaml", "compose.test.yaml" };
    static readonly Regex LoopbackPort = new(@"^- ""127\.0\.0\.1:\d{1,5}:\d{1,5}""$");

    static List<string> Ports(IReadOnlyList<string> lines, string service) =>
        ComposeText.Block(lines, "services", service, "ports").Select(l => l.Trim()).ToList();

    [Theory]
    [MemberData(nameof(Files))]
    public void Every_published_port_is_loopback_in_the_short_quoted_form(string file)
    {
        var lines = ComposeText.Lines(file);
        var services = ComposeText.ChildKeys(lines, "services").ToList();
        Assert.NotEmpty(services);
        foreach (var service in services)
        {
            Assert.DoesNotContain(ComposeText.Block(lines, "services", service), l => l.Trim().StartsWith("ports:", StringComparison.Ordinal) && l.Trim() != "ports:");
            Assert.All(Ports(lines, service), p => Assert.Matches(LoopbackPort, p));
        }
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void No_forbidden_settings(string file)
    {
        var text = string.Join('\n', ComposeText.Lines(file).Where(l => !ComposeText.IsBlankOrComment(l)));
        foreach (var token in new[] { "network_mode", "0.0.0.0", "\"::", "[::]", "host_ip", "privileged", "POSTGRES_PASSWORD:" })
            Assert.DoesNotContain(token, text);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void Every_network_is_internal_or_pins_loopback_publishing(string file)
    {
        var lines = ComposeText.Lines(file);
        var networks = ComposeText.ChildKeys(lines, "networks").ToList();
        Assert.NotEmpty(networks);
        foreach (var network in networks)
        {
            var block = ComposeText.Block(lines, "networks", network).Select(l => l.Trim()).ToList();
            Assert.True(block.Contains("internal: true") || block.Contains("com.docker.network.bridge.host_binding_ipv4: \"127.0.0.1\""),
                $"{file}: network '{network}' is neither internal nor pinned to 127.0.0.1");
        }
    }

    [Fact]
    public void Accounts_stack_database_has_no_ports_and_sits_on_the_internal_network()
    {
        var lines = ComposeText.Lines("compose.yaml");
        Assert.Empty(Ports(lines, "postgres"));
        Assert.Contains("internal: true", ComposeText.Block(lines, "networks", "db").Select(l => l.Trim()));
        Assert.Equal(["- db"], ComposeText.Block(lines, "services", "postgres", "networks").Select(l => l.Trim()));
    }

    [Fact]
    public void Accounts_stack_publishes_exactly_the_api_on_5080()
    {
        var lines = ComposeText.Lines("compose.yaml");
        Assert.Equal(["- \"127.0.0.1:5080:8080\""], Ports(lines, "api"));
        Assert.Empty(Ports(lines, "migrate"));
    }

    [Fact]
    public void Test_cluster_publishes_only_55432()
    {
        Assert.Equal(["- \"127.0.0.1:55432:5432\""], Ports(ComposeText.Lines("compose.test.yaml"), "postgres"));
    }
}
