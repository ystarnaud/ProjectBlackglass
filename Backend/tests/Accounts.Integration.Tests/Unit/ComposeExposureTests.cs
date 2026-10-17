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
    public void Accounts_stack_keeps_dev_auth_and_admin_routes_off_by_default_and_the_admin_service_publishes_nothing()
    {
        var lines = ComposeText.Lines("compose.yaml");
        var api = ComposeText.Block(lines, "services", "api", "environment").Select(l => l.Trim()).ToList();
        Assert.Contains("BLACKGLASS_DEV_AUTH: ${BLACKGLASS_DEV_AUTH:-disabled}", api);
        Assert.Contains("BLACKGLASS_ADMIN_ROUTES: ${BLACKGLASS_ADMIN_ROUTES:-disabled}", api);
        Assert.Contains("BLACKGLASS_ADMIN_KEY_FILE: /run/secrets/api_admin_key", api);
        Assert.Contains("BLACKGLASS_INVENTORY_MAX_OPERATIONS: ${BLACKGLASS_INVENTORY_MAX_OPERATIONS:-}", api); // empty means the built-in default
        Assert.Contains("BLACKGLASS_INVENTORY_MAX_BODY_BYTES: ${BLACKGLASS_INVENTORY_MAX_BODY_BYTES:-}", api);
        Assert.Contains("BLACKGLASS_ENERGY_RECONCILE_SECONDS: ${BLACKGLASS_ENERGY_RECONCILE_SECONDS:-}", api); // Stage 6, provisional
        Assert.Contains("BLACKGLASS_ENERGY_GRANT_MAX: ${BLACKGLASS_ENERGY_GRANT_MAX:-}", api);
        Assert.Contains("- api_admin_key", ComposeText.Block(lines, "services", "api", "secrets").Select(l => l.Trim()));

        Assert.Contains("admin", ComposeText.ChildKeys(lines, "services"));
        Assert.Empty(Ports(lines, "admin"));
        Assert.Equal(["- tools"], ComposeText.Block(lines, "services", "admin", "profiles").Select(l => l.Trim()));
        Assert.Equal(["- db"], ComposeText.Block(lines, "services", "admin", "networks").Select(l => l.Trim()));
        var admin = ComposeText.Block(lines, "services", "admin", "environment").Select(l => l.Trim()).ToList();
        Assert.Contains("BLACKGLASS_DB_USER: accounts_app", admin);
        Assert.Contains("BLACKGLASS_DB_PASSWORD_FILE: /run/secrets/admin_db_password", admin);
        Assert.Contains("BLACKGLASS_ENERGY_RECONCILE_SECONDS: ${BLACKGLASS_ENERGY_RECONCILE_SECONDS:-}", admin);
        Assert.Contains("BLACKGLASS_ENERGY_GRANT_MAX: ${BLACKGLASS_ENERGY_GRANT_MAX:-}", admin);
        Assert.Equal(["- admin_db_password"], ComposeText.Block(lines, "services", "admin", "secrets").Select(l => l.Trim()));
        Assert.Equal(["driver: none"], ComposeText.Block(lines, "services", "admin", "logging").Select(l => l.Trim())); // its stdout carries a new key once

        // Still exactly one publication in the whole file: the API's 127.0.0.1:5080.
        Assert.Single(Regex.Matches(string.Join('\n', lines), @"""127\.0\.0\.1:\d+:\d+"""));
    }

    [Fact]
    public void The_compose_default_leaves_dev_hosts_disabled()
    {
        // S7-D2: development host fixtures never count as capacity unless an operator sets exactly "enabled" (never in
        // production); both the API (the health check) and the admin CLI (host register-dev) read the same setting.
        var lines = ComposeText.Lines("compose.yaml");
        foreach (var service in new[] { "api", "admin" })
        {
            var env = ComposeText.Block(lines, "services", service, "environment").Select(l => l.Trim()).ToList();
            Assert.Contains("BLACKGLASS_DEV_HOSTS: ${BLACKGLASS_DEV_HOSTS:-disabled}", env);
            Assert.Contains("BLACKGLASS_HOST_LEASE_SECONDS: ${BLACKGLASS_HOST_LEASE_SECONDS:-}", env); // empty means the built-in default
            Assert.Single(env, l => l.StartsWith("BLACKGLASS_DEV_HOSTS:", StringComparison.Ordinal));
        }
        Assert.DoesNotContain(ComposeText.Block(lines, "services", "migrate", "environment"), l => l.Contains("BLACKGLASS_DEV_HOSTS", StringComparison.Ordinal));
    }

    [Fact]
    public void The_api_passes_the_admission_settings_through_with_built_in_defaults()
    {
        // Task 6 (provisional, spec §19): empty means the built-in default (60 s, 4096 bytes, 6 a minute). Only the API admits.
        var lines = ComposeText.Lines("compose.yaml");
        var api = ComposeText.Block(lines, "services", "api", "environment").Select(l => l.Trim()).ToList();
        foreach (var name in new[] { "BLACKGLASS_ASSIGN_DEADLINE_SECONDS", "BLACKGLASS_DEPLOY_MAX_BODY_BYTES", "BLACKGLASS_RATE_DEPLOY_PER_MINUTE" })
        {
            Assert.Contains($"{name}: ${{{name}:-}}", api);
            Assert.DoesNotContain(ComposeText.Block(lines, "services", "admin", "environment"), l => l.Contains(name, StringComparison.Ordinal));
            Assert.DoesNotContain(ComposeText.Block(lines, "services", "migrate", "environment"), l => l.Contains(name, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Test_cluster_publishes_only_55432()
    {
        Assert.Equal(["- \"127.0.0.1:55432:5432\""], Ports(ComposeText.Lines("compose.test.yaml"), "postgres"));
    }
}
