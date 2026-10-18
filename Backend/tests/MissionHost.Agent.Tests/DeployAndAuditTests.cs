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

    /// <summary>Renders the slot templates as install.sh does (render-slot-units.sh), into a temporary directory.</summary>
    static (int Exit, string Dir, string Error) Render(string portBase, string slots)
    {
        var dir = Path.Combine(Path.GetTempPath(), "bg-units-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var run = Process.Start(new ProcessStartInfo("/bin/bash", [Path.Combine(AppContext.BaseDirectory, "deploy", "render-slot-units.sh"), portBase, slots, dir])
            { RedirectStandardError = true, RedirectStandardOutput = true })!;
        run.WaitForExit();
        return (run.ExitCode, dir, run.StandardError.ReadToEnd());
    }

    [Fact]
    public void Each_slot_template_runs_its_own_dynamic_user_sandboxed_with_a_systemd_managed_private_outbox()
    {
        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, "deploy", "blackglass-mission@.service"))); // the shared-uid template is gone (C1)
        if (!OperatingSystem.IsLinux()) return;
        var (exit, dir, error) = Render("7000", "64");
        Assert.True(exit == 0, error);
        Assert.Equal(64, Directory.GetFiles(dir).Length);
        var text = File.ReadAllText(Path.Combine(dir, "blackglass-mission-07@.service"));
        Assert.DoesNotContain("@SLOT@", text, StringComparison.Ordinal);
        Assert.DoesNotContain("@PORT@", text, StringComparison.Ordinal);
        var unit = UnitFile(text);
        string One(string key) => Assert.Single(unit[key]);
        // C1: this slot's own dynamic uid; the outbox and logs are systemd's private, per-unit directories.
        Assert.Equal(("yes", "blackglass-m07", "no", "0022"), (One("DynamicUser"), One("User"), One("Restart"), One("UMask")));
        Assert.False(unit.ContainsKey("Group"));
        Assert.Equal(("blackglass-outbox/%i", "0755", "blackglass-mission/%i", "0750"), (One("StateDirectory"), One("StateDirectoryMode"), One("LogsDirectory"), One("LogsDirectoryMode")));
        Assert.Equal(["!/var/lib/private/blackglass-outbox/%i", "!/var/lib/blackglass/exits/%i.exit"], unit["AssertPathExists"]); // an attempt runs at most once
        foreach (var gone in new[] { "TemporaryFileSystem", "BindPaths", "ReadWritePaths" }) Assert.False(unit.ContainsKey(gone), gone);
        foreach (var on in new[] { "NoNewPrivileges", "ProtectHome", "PrivateTmp", "PrivateDevices", "RestrictSUIDSGID", "ProtectKernelModules", "ProtectKernelTunables", "RemoveIPC" })
            Assert.Equal("yes", One(on));
        Assert.Equal(("strict", "", "AF_INET AF_INET6 AF_UNIX", "invisible"), (One("ProtectSystem"), One("CapabilityBoundingSet"), One("RestrictAddressFamilies"), One("ProtectProc")));
        // I3: the credential comes only from the root broker's store.
        Assert.Equal("process:/run/blackglass/credentials/%i/process", One("LoadCredential"));
        Assert.Equal("+/opt/blackglass/agent/current/slot-guard 07 %i", One("ExecStartPre"));
        Assert.Equal("/opt/blackglass/mission/current/run-mission %i", One("ExecStart"));
        Assert.Equal("+/opt/blackglass/agent/current/record-exit %i", One("ExecStopPost"));
        foreach (var hidden in new[] { "-/etc/blackglass", "-/var/lib/blackglass", "-/run/blackglass", "-/var/log/blackglass", "-/var/crash", "-/var/lib/apport" })
            Assert.Contains(hidden, One("InaccessiblePaths").Split(' '));
        // Review 2: no core image a later attempt in the slot could read (M-5); no shared IPC namespace (M-6); failed units collected,
        // so the inventory stays complete (M-7; the exit status lives in record-exit's record, single start in the asserts).
        Assert.Equal(("0", "yes", "inactive-or-failed"), (One("LimitCORE"), One("PrivateIPC"), One("CollectMode")));
        // CollectMode is a [Unit] setting: systemd ignores it (with only a log line) anywhere else (found in the lab).
        Assert.True(text.IndexOf("\nCollectMode=", StringComparison.Ordinal) < text.IndexOf("\n[Service]", StringComparison.Ordinal));
        // Listeners: no stream listener at all (seccomp; enforced on Ubuntu 24.04), and only this slot's UDP port (BPF; where built).
        Assert.Equal(("~listen", "EPERM"), (One("SystemCallFilter"), One("SystemCallErrorNumber")));
        Assert.Equal(("udp:7007", "any"), (One("SocketBindAllow"), One("SocketBindDeny")));
        Assert.Equal(("2G", "200%", "256"), (One("MemoryMax"), One("CPUQuota"), One("TasksMax")));
        Assert.All(unit.Where(kv => kv.Key.StartsWith("Exec", StringComparison.Ordinal)).SelectMany(kv => kv.Value.Select(v => (kv.Key, v))),
            e => Assert.True(!e.v.StartsWith('+') || e.Key is "ExecStartPre" or "ExecStopPost"));
        var last = UnitFile(File.ReadAllText(Path.Combine(dir, "blackglass-mission-63@.service")));
        Assert.Equal(("blackglass-m63", "udp:7063", "+/opt/blackglass/agent/current/slot-guard 63 %i"), (Assert.Single(last["User"]), Assert.Single(last["SocketBindAllow"]), Assert.Single(last["ExecStartPre"])));
        Directory.Delete(dir, recursive: true);
    }

    [Theory]
    [InlineData("7000", "65")]
    [InlineData("7000", "0")]
    [InlineData("65500", "64")]
    [InlineData("1023", "8")]
    [InlineData("7000x", "8")]
    [InlineData("07000", "8")]
    public void The_slot_templates_are_rendered_only_for_valid_ports_and_at_most_64_slots(string portBase, string slots)
    {
        if (!OperatingSystem.IsLinux()) return;
        var (exit, dir, _) = Render(portBase, slots);
        Assert.NotEqual(0, exit);
        Assert.Empty(Directory.GetFiles(dir));
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void The_agent_unit_is_unprivileged_and_writes_only_its_own_directories()
    {
        var unit = UnitFile(Deploy("blackglass-agent.service"));
        string One(string key) => Assert.Single(unit[key]);
        Assert.Equal(("blackglass-agent", "always", "yes", "strict", ""), (One("User"), One("Restart"), One("NoNewPrivileges"), One("ProtectSystem"), One("CapabilityBoundingSet")));
        Assert.Equal("/var/lib/blackglass/agent /var/lib/blackglass/relay", One("ReadWritePaths"));
        Assert.Equal("/var/lib/blackglass/exits", One("ReadOnlyPaths"));
        // The mission units' private outboxes, read-only (C1): writes fail with EROFS whatever the files' modes.
        Assert.Equal("/var/lib/private/blackglass-outbox:/var/lib/blackglass/outbox", One("BindReadOnlyPaths"));
        Assert.Equal(("~listen", "0"), (One("SystemCallFilter"), One("Environment").Split('=')[1]));
        Assert.Equal("/opt/blackglass/agent/current/blackglass-agent run --config /etc/blackglass/agent/agent.json", One("ExecStart"));
        Assert.Equal(("256M", "64"), (One("MemoryMax"), One("TasksMax")));
        Assert.Equal("any", One("SocketBindDeny"));
    }

    [GeneratedRegex(@"/\^(?<pattern>[^/]+)\$/\.test\(unit\)", RegexOptions.CultureInvariant)]
    private static partial Regex RuleRegex();

    [Theory]
    [InlineData("blackglass-mission-07@0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41.service", true)]
    [InlineData("blackglass-mission-00@0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41.service", true)]
    [InlineData("blackglass-mission-63@0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41.service", true)]
    [InlineData("blackglass-mission-64@0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41.service", false)]
    [InlineData("blackglass-mission@0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41.service", false)] // the retired shared-uid template
    [InlineData("blackglass-mission-07@0192F1A4-5C2E-7B1D-9A0E-3F6C1D2B8E41.service", false)]
    [InlineData("blackglass-mission-07@0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41.service.d", false)]
    [InlineData("blackglass-mission-07@0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41.serviceX", false)]
    [InlineData("blackglass-mission-07@NOT-A-UUID.service", false)]
    [InlineData("blackglass-credential@0.service", false)]
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
                     "d /var/lib/blackglass/outbox        0755 root              root",
                     "d /var/lib/blackglass/exits         0750 root              blackglass-agent",
                     "d /var/lib/blackglass/agent         0700 blackglass-agent  blackglass-agent",
                     "d /var/lib/blackglass/relay         0700 blackglass-agent  blackglass-agent",
                     "d /var/lib/private                  0700 root              root",
                     "d /var/lib/private/blackglass-outbox 0755 root             root",
                     "d /run/blackglass/credentials       0700 root              root",
                 })
            Assert.Contains(expected, tmpfiles, StringComparison.Ordinal);
        Assert.DoesNotContain("/run/blackglass/launch", tmpfiles, StringComparison.Ordinal); // the agent-writable handoff is gone (I3)
        var users = Deploy(Path.Combine("sysusers.d", "blackglass.conf"));
        // The agent and the tunnel only: mission units run as systemd dynamic users, one per slot (C1), never a static shared user.
        Assert.Equal(2, Regex.Matches(users, @"^u blackglass-\w+ .*/usr/sbin/nologin$", RegexOptions.Multiline).Count);
        Assert.DoesNotContain("blackglass-mission", users, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"^[ug]\s+blackglass-m\d", users);
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
    public void The_credential_broker_is_root_only_for_the_agents_group_one_connection_at_a_time_and_writes_only_its_store()
    {
        var socket = UnitFile(Deploy("blackglass-credential.socket"));
        string S(string key) => Assert.Single(socket[key]);
        Assert.Equal(("/run/blackglass/credential.sock", "root", "blackglass-agent", "0660", "yes"), (S("ListenStream"), S("SocketUser"), S("SocketGroup"), S("SocketMode"), S("Accept")));
        Assert.True(int.Parse(S("MaxConnections"), System.Globalization.CultureInfo.InvariantCulture) <= 16);
        var service = UnitFile(Deploy("blackglass-credential@.service"));
        string One(string key) => Assert.Single(service[key]);
        Assert.False(service.ContainsKey("User")); // root: the store is root's own
        Assert.Equal(("/opt/blackglass/agent/current/blackglass-credential-broker", "socket", "socket"), (One("ExecStart"), One("StandardInput"), One("StandardOutput")));
        Assert.Equal(("strict", "/run/blackglass/credentials", "yes", "AF_UNIX", "", "yes"),
            (One("ProtectSystem"), One("ReadWritePaths"), One("PrivateNetwork"), One("RestrictAddressFamilies"), One("CapabilityBoundingSet"), One("NoNewPrivileges")));
        foreach (var hidden in new[] { "-/var/lib/private", "-/etc/blackglass", "-/etc/shadow", "-/etc/gshadow", "-/etc/ssh", "-/etc/sudoers", "-/etc/sudoers.d" })
            Assert.Contains(hidden, One("InaccessiblePaths").Split(' ')); // root without capabilities still owns these (review 2, M-1)
        Assert.Equal("15s", One("RuntimeMaxSec"));
    }

    [Fact]
    public void The_scripts_parse_need_their_confirmation_and_never_delete_evidence()
    {
        foreach (var script in new[] { "install.sh", "uninstall.sh", "verify.sh", "publish-agent.sh", "render-slot-units.sh", "record-exit", "slot-guard", Path.Combine("stub", "run-mission") })
        {
            var text = Deploy(script);
            if (OperatingSystem.IsLinux())
            {
                using var check = Process.Start(new ProcessStartInfo(script is "record-exit" or "slot-guard" ? "/bin/sh" : "/bin/bash", ["-n", Path.Combine(AppContext.BaseDirectory, "deploy", script)])
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
        Assert.Contains("grep -Eqx '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}'", Deploy("slot-guard"), StringComparison.Ordinal);
        Assert.Contains("[0-5][0-9]|6[0-3]) ;;", Deploy("slot-guard"), StringComparison.Ordinal);
        Assert.DoesNotContain("BLACKGLASS_", Deploy("slot-guard"), StringComparison.Ordinal); // no executable from the environment (review 2, M-3)
        Assert.DoesNotContain("BLACKGLASS_", Deploy("record-exit"), StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_keeps_its_test_requests_from_users_outside_the_agents_group()
    {
        var verify = Deploy("verify.sh");
        // Root and the agent's group only (the broker tests send the requests as the agent); other users can't open one by its path.
        Assert.Contains("chgrp blackglass-agent \"$work\" && chmod 2750 \"$work\"", verify, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"chmod\s+0?7[0-7]1\s+""\$work""", verify);
        Assert.DoesNotMatch(@"chmod\s+0?6[0-7]4\b", verify);
        Assert.Contains("check \"an unrelated user cannot read a request even by its path\"", verify, StringComparison.Ordinal);
        Assert.Contains("check \"the verification directory is root:blackglass-agent 2750\"", verify, StringComparison.Ordinal);
    }

    /// <summary>Runs a copy of slot-guard whose /usr/bin/systemctl (list-units: the listing; show: the unit's dynamic uid) and
    /// /usr/bin/ps are fakes printing the given text.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    static (int Exit, bool SystemctlCalled) Guard(string slot, string attempt, string listing = "", string slotUid = "61001", string processUids = "0\n996\n")
    {
        var dir = Path.Combine(Path.GetTempPath(), "bg-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            void Fake(string name, string body)
            {
                File.WriteAllText(Path.Combine(dir, name), "#!/bin/sh\n" + body + "\n");
                File.SetUnixFileMode(Path.Combine(dir, name), (UnixFileMode)0b111_101_101);
            }
            Fake("systemctl", $"echo called >> {dir}/calls; case \" $* \" in *\" show \"*) printf '{slotUid}\\n' ;; *) printf '{listing}' ;; esac");
            Fake("ps", $"printf '{processUids}'");
            var script = Deploy("slot-guard").Replace("/usr/bin/systemctl", Path.Combine(dir, "systemctl"), StringComparison.Ordinal)
                .Replace("/usr/bin/ps", Path.Combine(dir, "ps"), StringComparison.Ordinal);
            Assert.DoesNotContain("/usr/bin/id", script, StringComparison.Ordinal); // no NSS lookup: systemd's own answer (found in the lab)
            File.WriteAllText(Path.Combine(dir, "slot-guard"), script);
            using var run = Process.Start(new ProcessStartInfo("/bin/sh", [Path.Combine(dir, "slot-guard"), slot, attempt]) { RedirectStandardError = true })!;
            run.WaitForExit();
            return (run.ExitCode, File.Exists(Path.Combine(dir, "calls")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    const string GuardSelf = "blackglass-mission-07@0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41.service loaded activating start-pre x\\n";

    [Theory]
    [InlineData("07", "0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41", true)]
    [InlineData("64", "0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41", false)]
    [InlineData("7", "0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41", false)]
    [InlineData("07", "../../etc", false)]
    [InlineData("07;id", "0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41", false)]
    [InlineData("07", "x/../../etc/foo\n0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41", false)] // a valid second line never saves the first (review 2, M-3)
    [InlineData("07\n12", "0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41", false)]
    public void The_slot_guard_refuses_malformed_arguments_before_asking_systemd(string slot, string attempt, bool valid)
    {
        if (!OperatingSystem.IsLinux()) return;
        var (exit, called) = Guard(slot, attempt, GuardSelf);
        Assert.Equal(valid, exit == 0);
        Assert.Equal(valid, called);
    }

    [Theory]
    [InlineData("blackglass-mission-07@0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e42.service loaded active running x\\n")] // another attempt in the slot
    [InlineData("blackglass-mission-07@0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e42.service loaded refreshing x x\\n")] // any state but inactive or failed is live
    [InlineData("blackglass-mission-07@0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e42.service loaded maintenance x x\\n")]
    [InlineData("blackglass-mission-12@0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41.service loaded active running x\\n")] // the same attempt live in another slot
    public void The_slot_guard_refuses_a_second_live_unit(string other)
    {
        if (!OperatingSystem.IsLinux()) return;
        Assert.Equal(0, Guard("07", "0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41", GuardSelf).Exit); // itself only
        Assert.Equal(0, Guard("07", "0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41", GuardSelf + "blackglass-mission-07@0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e42.service loaded failed failed x\\n").Exit);
        Assert.NotEqual(0, Guard("07", "0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41", GuardSelf + other).Exit);
    }

    [Fact]
    public void The_slot_guard_refuses_while_a_process_still_runs_as_the_slots_uid()
    {
        if (!OperatingSystem.IsLinux()) return;
        // A process that outlived its unit's final SIGKILL would share a uid with the next attempt in the slot (review 2, M-4).
        Assert.NotEqual(0, Guard("07", "0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41", GuardSelf, slotUid: "61001", processUids: "0\\n61001\\n").Exit);
        Assert.Equal(0, Guard("07", "0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41", GuardSelf, slotUid: "61001", processUids: "0\\n610010\\n").Exit);
        Assert.NotEqual(0, Guard("07", "0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41", GuardSelf, slotUid: "").Exit); // the slot's user unknown: refused
        Assert.NotEqual(0, Guard("07", "0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41", GuardSelf, slotUid: "[not set]").Exit);
    }

    // ---- the agent's sources ----

    static IEnumerable<(string File, string Text)> Sources() =>
        Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "source"), "*.cs", SearchOption.AllDirectories).Select(f => (Path.GetFileName(f), File.ReadAllText(f)));

    static IEnumerable<(string File, string Text)> BrokerSources() =>
        Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "broker-source"), "*.cs", SearchOption.AllDirectories).Select(f => (Path.GetFileName(f), File.ReadAllText(f)));

    [Fact]
    public void The_root_broker_runs_nothing_reads_no_path_it_is_given_and_never_logs_the_credential()
    {
        var all = string.Concat(BrokerSources().Select(s => s.Text));
        Assert.True(BrokerSources().Count() >= 4);
        foreach (var launch in new[] { "Process.Start", "ProcessStartInfo", "UseShellExecute", "execve", "system(" })
            Assert.DoesNotContain(launch, all, StringComparison.Ordinal);
        Assert.DoesNotContain("/bin/", all, StringComparison.Ordinal);
        foreach (var line in all.Split('\n').Where(l => l.Contains("WriteLine(", StringComparison.Ordinal)))
            foreach (var forbidden in new[] { "{credential", "{p.Credential", "{request", "Encoding.ASCII.GetString(request" })
                Assert.False(line.Contains(forbidden, StringComparison.Ordinal), $"a broker log line names {forbidden}: {line.Trim()}");
        Assert.Contains("\"/run/blackglass/credentials\"", all, StringComparison.Ordinal); // its one fixed store
    }

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
