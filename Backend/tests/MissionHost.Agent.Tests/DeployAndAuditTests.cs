using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Blackglass.MissionHost.Agent.Tests;

/// <summary>Tasks 10 and 11: the deployment files say what the rulings require (read as text: they are installed only at the
/// owner's infrastructure checkpoint), and the agent's sources keep their security rules.</summary>
public partial class DeployAndAuditTests
{
    static string Deploy(string relative) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "deploy", relative));

    static Dictionary<string, List<string>> UnitFile(string text)
    {
        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('[')) continue;
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            var key = line[..eq];
            if (!values.TryGetValue(key, out var list)) values[key] = list = [];
            list.Add(line[(eq + 1)..]);
        }
        return values;
    }

    [Fact]
    public void The_mission_template_runs_unprivileged_sandboxed_and_sees_only_its_own_outbox()
    {
        var unit = UnitFile(Deploy("blackglass-mission@.service"));
        string One(string key) => Assert.Single(unit[key]);
        Assert.Equal(("blackglass-mission", "blackglass-mission", "no", "0027"), (One("User"), One("Group"), One("Restart"), One("UMask")));
        foreach (var on in new[] { "NoNewPrivileges", "ProtectHome", "PrivateTmp", "PrivateDevices", "RestrictSUIDSGID", "ProtectKernelModules", "ProtectKernelTunables" })
            Assert.Equal("yes", One(on));
        Assert.Equal(("strict", "", "AF_INET AF_INET6 AF_UNIX"), (One("ProtectSystem"), One("CapabilityBoundingSet"), One("RestrictAddressFamilies")));
        Assert.Equal("process:/run/blackglass/launch/%i/process", One("LoadCredential"));
        Assert.Equal("/var/lib/blackglass/outbox:ro", One("TemporaryFileSystem"));
        Assert.Equal("/var/lib/blackglass/outbox/%i", One("BindPaths"));
        Assert.Equal("/var/lib/blackglass/outbox/%i /var/log/blackglass/%i", One("ReadWritePaths"));
        Assert.Equal(["+/usr/bin/install -d -o blackglass-mission -g blackglass-agent -m 2750 /var/lib/blackglass/outbox/%i",
            "+/usr/bin/install -d -o blackglass-mission -g blackglass-mission -m 0750 /var/log/blackglass/%i"], unit["ExecStartPre"]);
        Assert.Equal("/opt/blackglass/mission/current/run-mission %i", One("ExecStart"));
        Assert.Equal("+/opt/blackglass/agent/current/record-exit %i", One("ExecStopPost"));
        Assert.Contains("-/etc/blackglass", One("InaccessiblePaths"), StringComparison.Ordinal);
        Assert.Contains("-/run/blackglass", One("InaccessiblePaths"), StringComparison.Ordinal);
        Assert.Equal(("2G", "200%", "256"), (One("MemoryMax"), One("CPUQuota"), One("TasksMax")));
        Assert.Equal(("udp", "any"), (One("SocketBindAllow"), One("SocketBindDeny"))); // no TCP listener: the tunnel port can't be taken (I1)
        // Root runs only the fixed ExecStartPre=+ and ExecStopPost=+ commands.
        Assert.All(unit.Where(kv => kv.Key.StartsWith("Exec", StringComparison.Ordinal)).SelectMany(kv => kv.Value.Select(v => (kv.Key, v))),
            e => Assert.True(!e.v.StartsWith('+') || e.Key is "ExecStartPre" or "ExecStopPost"));
    }

    [Fact]
    public void The_agent_unit_is_unprivileged_and_writes_only_its_own_directories()
    {
        var unit = UnitFile(Deploy("blackglass-agent.service"));
        string One(string key) => Assert.Single(unit[key]);
        Assert.Equal(("blackglass-agent", "always", "yes", "strict", ""), (One("User"), One("Restart"), One("NoNewPrivileges"), One("ProtectSystem"), One("CapabilityBoundingSet")));
        Assert.Equal("/var/lib/blackglass/agent /var/lib/blackglass/relay /run/blackglass/launch", One("ReadWritePaths"));
        Assert.Equal("/var/lib/blackglass/outbox /var/lib/blackglass/exits", One("ReadOnlyPaths"));
        Assert.Equal("/opt/blackglass/agent/current/blackglass-agent run --config /etc/blackglass/agent/agent.json", One("ExecStart"));
        Assert.Equal(("256M", "64"), (One("MemoryMax"), One("TasksMax")));
        Assert.Equal("any", One("SocketBindDeny"));
    }

    [GeneratedRegex(@"/\^(?<pattern>[^/]+)\$/\.test\(unit\)", RegexOptions.CultureInvariant)]
    private static partial Regex RuleRegex();

    [Theory]
    [InlineData("blackglass-mission@0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41.service", true)]
    [InlineData("blackglass-mission@0192F1A4-5C2E-7B1D-9A0E-3F6C1D2B8E41.service", false)]
    [InlineData("blackglass-mission@0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41.service.d", false)]
    [InlineData("blackglass-mission@0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41.serviceX", false)]
    [InlineData("blackglass-mission@NOT-A-UUID.service", false)]
    [InlineData("blackglass-agent.service", false)]
    [InlineData("ssh.service", false)]
    public void The_polkit_rule_admits_only_canonical_mission_units_for_start_and_stop(string unit, bool admitted)
    {
        var rule = Deploy("60-blackglass-mission.rules");
        var pattern = "^" + RuleRegex().Match(rule).Groups["pattern"].Value + "$";
        Assert.Equal(admitted, Regex.IsMatch(unit, pattern, RegexOptions.CultureInvariant));
        Assert.Contains("subject.user !== \"blackglass-agent\"", rule, StringComparison.Ordinal);
        Assert.Contains("(verb === \"start\" || verb === \"stop\")", rule, StringComparison.Ordinal);
        Assert.Contains("return polkit.Result.NO;", rule, StringComparison.Ordinal); // anything else the agent asks systemd for is refused
        Assert.DoesNotContain("enable", rule.Split("polkit.addRule")[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Directories_and_users_have_the_documented_owners_and_modes()
    {
        var tmpfiles = Deploy(Path.Combine("tmpfiles.d", "blackglass.conf"));
        foreach (var expected in new[]
                 {
                     "d /var/lib/blackglass/outbox        0750 root              blackglass-agent",
                     "d /var/lib/blackglass/exits         0750 root              blackglass-agent",
                     "d /var/lib/blackglass/agent         0700 blackglass-agent  blackglass-agent",
                     "d /var/lib/blackglass/relay         0700 blackglass-agent  blackglass-agent",
                     "d /run/blackglass/launch            0700 blackglass-agent  blackglass-agent",
                 })
            Assert.Contains(expected, tmpfiles, StringComparison.Ordinal);
        var users = Deploy(Path.Combine("sysusers.d", "blackglass.conf"));
        Assert.Equal(3, Regex.Matches(users, @"^u blackglass-\w+ .*/usr/sbin/nologin$", RegexOptions.Multiline).Count);
    }

    [Fact]
    public void The_tunnel_is_restricted_pinned_and_forward_only()
    {
        var ssh = Deploy(Path.Combine("tunnel", "ssh_config"));
        foreach (var line in new[] { "StrictHostKeyChecking yes", "UserKnownHostsFile /etc/blackglass/tunnel/known_hosts", "GlobalKnownHostsFile /dev/null",
                     "LocalForward 127.0.0.1:5081 127.0.0.1:5080", "ForwardAgent no", "ForwardX11 no", "BatchMode yes", "ExitOnForwardFailure yes",
                     "IdentitiesOnly yes", "SessionType none", "UpdateHostKeys no" })
            Assert.Contains("  " + line + "\n", ssh.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.DoesNotContain("StrictHostKeyChecking no", ssh, StringComparison.Ordinal);
        Assert.DoesNotContain("accept-new", ssh, StringComparison.Ordinal);
        var sshd = Deploy(Path.Combine("tunnel", "services-vm-sshd.conf"));
        foreach (var line in new[] { "AllowTcpForwarding local", "PermitOpen 127.0.0.1:5080", "PermitTTY no", "X11Forwarding no", "AllowAgentForwarding no",
                     "ForceCommand /bin/false", "PermitListen none", "AuthenticationMethods publickey" })
            Assert.Contains(line, sshd, StringComparison.Ordinal);
        Assert.Contains("restrict,port-forwarding,permitopen=\"127.0.0.1:5080\",command=\"/bin/false\"", Deploy(Path.Combine("tunnel", "services-vm-authorized_keys")), StringComparison.Ordinal);
        var unit = UnitFile(Deploy(Path.Combine("tunnel", "blackglass-tunnel.service")));
        Assert.Equal(("blackglass-tunnel", "always", "120"), (Assert.Single(unit["User"]), Assert.Single(unit["Restart"]), Assert.Single(unit["RestartMaxDelaySec"])));
        Assert.Equal(("tcp:5081", "any"), (Assert.Single(unit["SocketBindAllow"]), Assert.Single(unit["SocketBindDeny"])));
    }

    [Fact]
    public void The_scripts_parse_need_their_confirmation_and_never_delete_evidence()
    {
        foreach (var script in new[] { "install.sh", "uninstall.sh", "verify.sh", "publish-agent.sh", "record-exit", Path.Combine("stub", "run-mission") })
        {
            var text = Deploy(script);
            if (OperatingSystem.IsLinux())
            {
                using var check = Process.Start(new ProcessStartInfo(script.EndsWith("record-exit", StringComparison.Ordinal) ? "/bin/sh" : "/bin/bash", ["-n", Path.Combine(AppContext.BaseDirectory, "deploy", script)])
                    { RedirectStandardError = true })!;
                check.WaitForExit();
                Assert.True(check.ExitCode == 0, $"{script}: {check.StandardError.ReadToEnd()}");
            }
            Assert.DoesNotContain("StrictHostKeyChecking=no", text, StringComparison.Ordinal);
        }
        foreach (var script in new[] { "install.sh", "uninstall.sh", "verify.sh" }) Assert.Contains("refusing without --yes", Deploy(script), StringComparison.Ordinal);
        var uninstall = Deploy("uninstall.sh");
        Assert.DoesNotMatch(@"rm\s+-[a-z]*r[a-z]*\s+/var/lib/blackglass", uninstall);
        Assert.DoesNotMatch(@"rm\s[^\n]*/opt/blackglass/server", Deploy("install.sh") + uninstall + Deploy("verify.sh"));
        Assert.Contains("grep -Eqx '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}'", Deploy("record-exit"), StringComparison.Ordinal);
    }

    // ---- the agent's sources ----

    static IEnumerable<(string File, string Text)> Sources() =>
        Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "source"), "*.cs", SearchOption.AllDirectories).Select(f => (Path.GetFileName(f), File.ReadAllText(f)));

    [Fact]
    public void The_agent_never_uses_a_shell_and_builds_no_command_line_from_text()
    {
        var all = string.Concat(Sources().Select(s => s.Text));
        Assert.DoesNotContain("UseShellExecute = true", all, StringComparison.Ordinal);
        Assert.DoesNotContain("/bin/sh", all, StringComparison.Ordinal);
        Assert.DoesNotContain("bash", all, StringComparison.Ordinal);
        Assert.DoesNotContain(".Arguments =", all, StringComparison.Ordinal); // only ArgumentList, element by element
        Assert.Single(Sources(), s => s.Text.Contains("new ProcessStartInfo(", StringComparison.Ordinal));
    }

    [GeneratedRegex(@"log\.(Info|Warn|Alert)\((?<args>[^;]*)\);", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex LogCall();

    [Fact]
    public void No_log_call_names_a_secret_a_credential_or_result_content()
    {
        var calls = Sources().SelectMany(s => LogCall().Matches(s.Text).Select(m => (s.File, Args: m.Groups["args"].Value))).ToList();
        Assert.True(calls.Count > 20);
        foreach (var (file, args) in calls)
            foreach (var forbidden in new[] { "Credential", "secret.", "AuthorizationValue", "Bytes", "ProcessCredential", "manifest)" })
                Assert.False(args.Contains(forbidden, StringComparison.Ordinal), $"{file}: a log call mentions {forbidden}: {args}");
    }
}
