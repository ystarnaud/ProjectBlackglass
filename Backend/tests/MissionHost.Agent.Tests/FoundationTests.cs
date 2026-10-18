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
        if (accepted) Assert.Equal("blackglass-mission@" + text + ".service", AttemptIds.Unit(id));
    }

    [Theory]
    [InlineData("blackglass-mission@0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41.service", true)]
    [InlineData("blackglass-mission@0192F1A4-5c2e-7b1d-9a0e-3f6c1d2b8e41.service", false)]
    [InlineData("blackglass-mission@0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41.service.d", false)]
    [InlineData("blackglass-mission@0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41.timer", false)]
    [InlineData("blackglass-agent.service", false)]
    [InlineData("ssh.service", false)]
    [InlineData("blackglass-mission@.service", false)]
    [InlineData("xblackglass-mission@0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41.service", false)]
    public void Only_a_mission_unit_for_a_canonical_attempt_parses(string unit, bool accepted) =>
        Assert.Equal(accepted, AttemptIds.TryParseUnit(unit, out _));

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
        + "\"launchDir\":\"/run/blackglass/launch\",\"serverBuildInfo\":\"/opt/blackglass/server/current/build-info.txt\"" + overrides + "}";

    [Fact]
    public void A_valid_configuration_loads_with_the_provisional_defaults()
    {
        var c = AgentConfig.Parse(Encoding.UTF8.GetBytes(Config()));
        Assert.Equal((10, 1000, 60_000, 60, 4 * 1024 * 1024L, "/usr/bin/systemctl"), (c.HeartbeatSeconds, c.RelayBackoffMinMs, c.RelayBackoffMaxMs, c.AuthRetrySeconds, c.MaxResultBytes, c.Systemctl));
    }

    [Theory]
    [InlineData(",\"extra\":1")]
    [InlineData(",\"heartbeatSeconds\":0")]
    [InlineData(",\"catalogueVersion\":\"sha256:XYZ\"")]
    [InlineData(",\"systemctl\":\"systemctl\"")]
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
    public void A_credential_file_is_owner_read_only_and_gone_after_delete()
    {
        using var dirs = new AgentDirs();
        var handoff = new CredentialHandoff(dirs.Config.LaunchDir);
        var attempt = Guid.CreateVersion7();
        var credential = OpaqueSecret.New(OpaqueSecret.ProcessCredentialPrefix).Text;
        handoff.Write(attempt, credential);
        Assert.Equal(credential, File.ReadAllText(handoff.PathOf(attempt)));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead, File.GetUnixFileMode(handoff.PathOf(attempt)));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(handoff.PathOf(attempt))!));
        }
        handoff.Delete(attempt);
        Assert.False(File.Exists(handoff.PathOf(attempt)));
        Assert.False(Directory.Exists(Path.GetDirectoryName(handoff.PathOf(attempt))));
    }
}
