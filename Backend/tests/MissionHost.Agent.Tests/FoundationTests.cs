using System.Diagnostics;
using System.Text;
using Blackglass.Accounts.Domain.Auth;
using Blackglass.MissionHost.Agent.Tests.Support;

namespace Blackglass.MissionHost.Agent.Tests;

/// <summary>Task 1: identifiers, configuration, durable files, the host secret and untrusted reads.</summary>
public class FoundationTests
{
    // ---- attempt ids and unit names (A10-D4) ----

    [Theory]
    [InlineData("0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41", true)]
    [InlineData("0192F1A4-5C2E-7B1D-9A0E-3F6C1D2B8E41", false)] // upper case
    [InlineData("{0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41}", false)]
    [InlineData("0192f1a45c2e7b1d9a0e3f6c1d2b8e41", false)]
    [InlineData(" 0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41", false)]
    [InlineData("0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41\n", false)]
    [InlineData("0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e4", false)]
    [InlineData("0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e411", false)]
    [InlineData("00000000-0000-0000-0000-000000000000", false)] // nil
    [InlineData("../../etc/passwd", false)]
    [InlineData("0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41;reboot", false)]
    [InlineData("", false)]
    public void Only_a_canonical_lowercase_uuid_is_an_attempt_id(string text, bool accepted)
    {
        Assert.Equal(accepted, AttemptIds.TryParse(text, out var id));
        if (accepted) Assert.Equal("blackglass-mission-07@" + text + ".service", new MissionUnit(7, id).Name);
    }

    // ---- slot units (security correction C1): blackglass-mission-NN@<full attempt id>.service ----

    [Theory]
    [InlineData("blackglass-mission-07@0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41.service", 7)]
    [InlineData("blackglass-mission-00@0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41.service", 0)]
    [InlineData("blackglass-mission-63@0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41.service", 63)]
    [InlineData("blackglass-mission@0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41.service", -1)] // the retired shared-uid template
    [InlineData("blackglass-mission-64@0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41.service", -1)]
    [InlineData("blackglass-mission-7@0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41.service", -1)]
    [InlineData("blackglass-mission-007@0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41.service", -1)]
    [InlineData("blackglass-mission-07@0192F1A4-5c2e-7b1d-9a0e-3f6c1d2b8e41.service", -1)]
    [InlineData("blackglass-mission-07@0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41.service.d", -1)]
    [InlineData("blackglass-mission-07@0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41.timer", -1)]
    [InlineData("blackglass-mission-07@00000000-0000-0000-0000-000000000000.service", -1)]
    [InlineData("blackglass-agent.service", -1)]
    [InlineData("ssh.service", -1)]
    [InlineData("blackglass-mission-07@.service", -1)]
    [InlineData("xblackglass-mission-07@0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41.service", -1)]
    public void Only_a_slot_unit_for_a_canonical_attempt_parses(string unit, int slot)
    {
        Assert.Equal(slot >= 0, MissionUnit.TryParse(unit, out var parsed));
        if (slot >= 0) Assert.Equal((slot, unit), (parsed.Slot, parsed.Name));
    }

    [Fact]
    public void A_unit_is_built_only_from_a_slot_from_0_to_63_and_a_real_attempt()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MissionUnit(64, Guid.CreateVersion7()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MissionUnit(-1, Guid.CreateVersion7()));
        Assert.Throws<ArgumentException>(() => new MissionUnit(0, Guid.Empty));
    }

    [Theory]
    [InlineData(7000, 7000, 64, 0)]
    [InlineData(7063, 7000, 64, 63)]
    [InlineData(7064, 7000, 64, -1)]
    [InlineData(6999, 7000, 64, -1)]
    [InlineData(7007, 7000, 8, 7)]
    [InlineData(7008, 7000, 8, -1)]
    public void A_port_maps_to_its_slot_only_inside_the_hosts_range(int port, int portBase, int count, int slot) =>
        Assert.Equal(slot >= 0 ? slot : null, new MissionSlots(portBase, count).SlotOf(port));

    [Theory]
    [InlineData("JG", true)]
    [InlineData("1JG", false)] // a leading '1' is not canonical
    [InlineData("0", false)]
    [InlineData("O", false)]
    [InlineData("", false)]
    [InlineData("JG/../x", false)]
    public void Only_canonical_base58_is_an_account_id(string text, bool accepted) => Assert.Equal(accepted, AccountIds.TryParse(text, out _));

    // ---- configuration ----

    static string Config(string overrides = "") =>
        "{\"hostId\":\"h1\",\"apiBaseUrl\":\"http://127.0.0.1:5081/\",\"hostSecretFile\":\"/etc/blackglass/agent/host-secret\",\"stateDir\":\"/var/lib/blackglass/agent\","
        + "\"relayDir\":\"/var/lib/blackglass/relay\",\"outboxDir\":\"/var/lib/blackglass/outbox\",\"exitDir\":\"/var/lib/blackglass/exits\","
        + "\"portBase\":7000,\"serverBuildInfo\":\"/opt/blackglass/server/current/build-info.txt\"" + overrides + "}";

    [Fact]
    public void A_valid_configuration_loads_with_the_provisional_defaults()
    {
        var c = AgentConfig.Parse(Encoding.UTF8.GetBytes(Config()));
        Assert.Equal((10, 1000, 60_000, 60, 4 * 1024 * 1024L, "/usr/bin/systemctl"), (c.HeartbeatSeconds, c.RelayBackoffMinMs, c.RelayBackoffMaxMs, c.AuthRetrySeconds, c.MaxResultBytes, c.Systemctl));
        Assert.Equal((7000, 64, "/run/blackglass/credential.sock"), (c.PortBase, c.LocalCapacity, c.CredentialSocket));
    }

    [Theory]
    [InlineData(",\"extra\":1")]
    [InlineData(",\"heartbeatSeconds\":0")]
    [InlineData(",\"catalogueVersion\":\"sha256:XYZ\"")]
    [InlineData(",\"systemctl\":\"systemctl\"")]
    [InlineData(",\"launchDir\":\"/run/blackglass/launch\"")] // the agent-writable credential handoff is gone (security correction I3)
    [InlineData(",\"credentialSocket\":\"run/credential.sock\"")]
    [InlineData(",\"localCapacity\":65")]
    public void A_bad_setting_is_refused(string overrides) => Assert.Throws<AgentConfigException>(() => AgentConfig.Parse(Encoding.UTF8.GetBytes(Config(overrides))));

    [Theory]
    [InlineData("http://10.0.0.5:5081/")]
    [InlineData("https://127.0.0.1:5081/")]
    [InlineData("http://127.0.0.1:5081/agent")]
    [InlineData("http://user:pw@127.0.0.1:5081/")]
    [InlineData("http://localhost.example.com:5081/")]
    public void The_api_is_only_ever_the_loopback_tunnel(string url) =>
        Assert.Throws<AgentConfigException>(() => AgentConfig.Parse(Encoding.UTF8.GetBytes(Config().Replace("http://127.0.0.1:5081/", url))));

    [Theory]
    [InlineData("\"stateDir\":\"/var/lib/blackglass/agent\"", "\"stateDir\":\"relative/state\"")]
    [InlineData("\"stateDir\":\"/var/lib/blackglass/agent\"", "\"stateDir\":\"/var/lib/../../etc\"")]
    [InlineData("\"stateDir\":\"/var/lib/blackglass/agent\"", "\"stateDir\":\"/var/lib/blackglass/relay\"")] // two settings, one directory
    public void Directories_are_absolute_distinct_and_never_climb(string from, string to) =>
        Assert.Throws<AgentConfigException>(() => AgentConfig.Parse(Encoding.UTF8.GetBytes(Config().Replace(from, to))));

    [Theory]
    [InlineData("\"portBase\":7000", "\"portBase\":1023")]
    [InlineData("\"portBase\":7000", "\"portBase\":65500")] // 65500 + 64 slots runs past 65535
    [InlineData("\"portBase\":7000,", "")] // required
    public void The_port_base_is_required_and_keeps_every_slot_port_valid(string from, string to) =>
        Assert.Throws<AgentConfigException>(() => AgentConfig.Parse(Encoding.UTF8.GetBytes(Config().Replace(from, to))));

    // ---- durable files ----

    [Fact]
    public void An_atomic_write_leaves_only_the_final_file_private_and_a_crash_leftover_is_ignored_then_removed()
    {
        using var dirs = new AgentDirs();
        var dir = Path.Combine(dirs.Root, "private");
        AtomicFile.EnsurePrivateDirectory(dir);
        var path = Path.Combine(dir, "x.json");
        AtomicFile.Write(path, "one"u8);
        AtomicFile.Write(path, "two"u8);
        Assert.Equal("two", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(dir));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dir));
        }
        var leftover = path + ".agent-tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(leftover, "half");
        Assert.True(AtomicFile.IsTemporary(leftover));
        Assert.Equal(1, AtomicFile.RemoveTemporaries(dir));
        Assert.Equal([path], Directory.GetFiles(dir));
    }

    // ---- the host secret (A10-D8) ----

    [Fact]
    public void The_host_secret_is_read_validated_and_reloaded_only_when_it_changed()
    {
        using var dirs = new AgentDirs();
        var secret = new HostSecret(dirs.Config.HostSecretFile);
        Assert.Equal(HostSecret.ReloadResult.Changed, secret.Reload());
        Assert.Equal("Bearer " + dirs.SecretText, secret.AuthorizationValue);
        Assert.Equal(HostSecret.ReloadResult.Unchanged, secret.Reload());
        var rotated = OpaqueSecret.New(OpaqueSecret.HostSecretPrefix).Text;
        dirs.WriteSecret(rotated);
        Assert.Equal(HostSecret.ReloadResult.Changed, secret.Reload());
        Assert.Equal("Bearer " + rotated, secret.AuthorizationValue);
        dirs.WriteSecret("not-a-secret");
        Assert.Equal(HostSecret.ReloadResult.Invalid, secret.Reload());
        Assert.Equal("Bearer " + rotated, secret.AuthorizationValue); // the last valid secret is kept
        dirs.WriteSecret(OpaqueSecret.New(OpaqueSecret.ProcessCredentialPrefix).Text); // a process credential is not a host secret
        Assert.Equal(HostSecret.ReloadResult.Invalid, secret.Reload());
    }

    [Fact]
    public void A_host_secret_readable_by_others_or_behind_a_link_is_refused()
    {
        if (OperatingSystem.IsWindows()) return;
        using var dirs = new AgentDirs();
        File.SetUnixFileMode(dirs.Config.HostSecretFile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        Assert.Equal(HostSecret.ReloadResult.Invalid, new HostSecret(dirs.Config.HostSecretFile).Reload());
        File.SetUnixFileMode(dirs.Config.HostSecretFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var link = Path.Combine(dirs.Root, "link-secret");
        File.CreateSymbolicLink(link, dirs.Config.HostSecretFile);
        Assert.Equal(HostSecret.ReloadResult.Invalid, new HostSecret(link).Reload());
    }

    // ---- untrusted reads ----

    [Fact]
    public void An_untrusted_read_refuses_links_fifos_hard_links_directories_and_oversized_files()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var dirs = new AgentDirs();
        var regular = Path.Combine(dirs.Root, "regular");
        File.WriteAllText(regular, "content");
        Assert.Equal("content", Encoding.UTF8.GetString(Assert.IsType<Unix.ReadResult.Read>(Unix.ReadUntrusted(regular, 1024)).Bytes));
        var link = Path.Combine(dirs.Root, "link");
        File.CreateSymbolicLink(link, dirs.Config.HostSecretFile);
        Assert.Equal("symbolic_link", Assert.IsType<Unix.ReadResult.Refused>(Unix.ReadUntrusted(link, 1024)).Reason);
        var fifo = Path.Combine(dirs.Root, "fifo");
        Process.Start("mkfifo", [fifo])!.WaitForExit();
        Assert.Equal("not_a_regular_file", Assert.IsType<Unix.ReadResult.Refused>(Unix.ReadUntrusted(fifo, 1024)).Reason); // and it never blocked
        var hard = Path.Combine(dirs.Root, "hard");
        Process.Start("ln", [regular, hard])!.WaitForExit();
        Assert.Equal("hard_linked", Assert.IsType<Unix.ReadResult.Refused>(Unix.ReadUntrusted(hard, 1024)).Reason);
        Assert.Equal("not_a_regular_file", Assert.IsType<Unix.ReadResult.Refused>(Unix.ReadUntrusted(dirs.Root, 1024)).Reason);
        var big = Path.Combine(dirs.Root, "big");
        File.WriteAllBytes(big, new byte[2048]);
        Assert.Equal("too_large", Assert.IsType<Unix.ReadResult.Refused>(Unix.ReadUntrusted(big, 1024)).Reason);
        Assert.IsType<Unix.ReadResult.Failed>(Unix.ReadUntrusted(Path.Combine(dirs.Root, "missing"), 1024));
    }

    [Fact]
    public async Task A_credential_file_is_owner_read_only_and_gone_after_delete()
    {
        // Since security correction I3 the root broker's store holds it (LoadCredential's source), never an agent-writable file.
        using var dirs = new AgentDirs();
        var broker = new StoreBroker(dirs.Root);
        var attempt = Guid.CreateVersion7();
        var credential = OpaqueSecret.New(OpaqueSecret.ProcessCredentialPrefix).Text;
        Assert.True((await broker.PrepareAsync(attempt, 1, credential, default)).Ok);
        var path = Path.Combine(broker.Root, attempt.ToString("D"), "process");
        Assert.Equal(credential, File.ReadAllText(path));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead, File.GetUnixFileMode(path));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(path)!));
        }
        Assert.True((await broker.DiscardAsync(attempt, default)).Ok);
        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }
}
