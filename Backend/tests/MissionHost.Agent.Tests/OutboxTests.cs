using System.Text;
using Blackglass.MissionHost.Agent.Tests.Support;

namespace Blackglass.MissionHost.Agent.Tests;

/// <summary>Task 7: the outbox read-only under the publish contract (A10-D1) and relay to definitive answers (§C).</summary>
public class OutboxTests : IDisposable
{
    readonly AgentDirs dirs = new();
    readonly string attempt = Guid.CreateVersion7().ToString("D");
    Guid Id => Guid.Parse(attempt);
    OutboxScanner Scanner => new(dirs.Config.OutboxDir, 4 * 1024 * 1024);

    public void Dispose() => dirs.Dispose();

    [Fact]
    public void A_ready_result_is_relayable_a_written_one_only_after_exit_and_a_tmp_one_never()
    {
        dirs.WriteResult(attempt, "JG", "ready", Manifests.For(attempt, "JG"));
        dirs.WriteResult(attempt, "JH", "written", Manifests.For(attempt, "JH"));
        dirs.WriteResult(attempt, "JJ", "tmp", Manifests.For(attempt, "JJ")[..40]);
        string[] roster = ["JG", "JH", "JJ"];
        var alive = Scanner.Scan(Id, roster, includeWritten: false);
        Assert.True(alive.Complete);
        Assert.Equal(["JG"], alive.Files.Select(f => f.Account));
        var exited = Scanner.Scan(Id, roster, includeWritten: true);
        Assert.True(exited.Complete);
        Assert.Equal([("JG", "ready"), ("JH", "written")], exited.Files.Select(f => (f.Account, f.Kind)));
        Assert.True(exited.Present("JG") && exited.Present("JH"));
        Assert.False(exited.Present("JJ")); // a .tmp is never a result
    }

    [Fact]
    public void A_file_naming_another_attempt_or_participant_is_never_a_result_and_never_absence()
    {
        dirs.WriteResult(attempt, "JG", "ready", Manifests.For(Guid.CreateVersion7().ToString("D"), "JG")); // another attempt
        dirs.WriteResult(attempt, "JH", "ready", Manifests.For(attempt, "JG")); // another participant
        var scan = Scanner.Scan(Id, ["JG", "JH"], includeWritten: true);
        Assert.False(scan.Complete);
        Assert.All(scan.Files, f => Assert.False(f.Relayable));
        Assert.Contains("result_identity_invalid", scan.Anomalies);
    }

    [Fact]
    public void A_malformed_body_still_claiming_this_participant_is_relayed_for_quarantine_and_one_claiming_nothing_is_not()
    {
        var claiming = Encoding.UTF8.GetString(Manifests.For(attempt, "JG")).Replace("\"formatVersion\":1", "\"formatVersion\":2", StringComparison.Ordinal);
        dirs.WriteResult(attempt, "JG", "ready", Encoding.UTF8.GetBytes(claiming));
        dirs.WriteResult(attempt, "JH", "ready", "not json"u8.ToArray());
        var scan = Scanner.Scan(Id, ["JG", "JH"], includeWritten: true);
        Assert.True(scan.Present("JG"));
        Assert.False(scan.Present("JH"));
        Assert.False(scan.Complete);
    }

    [Fact]
    public void Unknown_names_and_non_participants_are_anomalies_and_a_missing_directory_is_never_complete()
    {
        Assert.False(Scanner.Scan(Id, ["JG"], includeWritten: true).Complete); // no directory yet
        dirs.WriteResult(attempt, "JG", "ready", Manifests.For(attempt, "JG"));
        File.WriteAllText(Path.Combine(dirs.OutboxOf(attempt), "notes.txt"), "x");
        dirs.WriteResult(attempt, "JK", "ready", Manifests.For(attempt, "JK"));
        var scan = Scanner.Scan(Id, ["JG"], includeWritten: true);
        Assert.True(scan.Complete);
        Assert.Equal(["JG"], scan.Files.Select(f => f.Account));
        Assert.Contains("unexpected_entry", scan.Anomalies);
        Assert.Contains("not_a_participant", scan.Anomalies);
    }

    [Fact]
    public void A_link_or_unreadable_result_makes_the_scan_incomplete()
    {
        if (!OperatingSystem.IsLinux()) return;
        Directory.CreateDirectory(dirs.OutboxOf(attempt));
        File.CreateSymbolicLink(Path.Combine(dirs.OutboxOf(attempt), "JG.manifest.ready"), dirs.Config.HostSecretFile);
        var unreadable = dirs.WriteResult(attempt, "JH", "ready", Manifests.For(attempt, "JH"));
        File.SetUnixFileMode(unreadable, UnixFileMode.None);
        var scan = Scanner.Scan(Id, ["JG", "JH"], includeWritten: true);
        Assert.False(scan.Complete);
        Assert.DoesNotContain(scan.Files, f => f.Relayable);
        Assert.Contains("result_refused_symbolic_link", scan.Anomalies);
    }

    [Fact]
    public async Task Scanning_and_relaying_never_change_the_outbox()
    {
        dirs.WriteResult(attempt, "JG", "ready", Manifests.For(attempt, "JG"));
        dirs.WriteResult(attempt, "JH", "written", Manifests.For(attempt, "JH"));
        dirs.WriteResult(attempt, "JJ", "tmp", "partial"u8.ToArray());
        string Snapshot() => string.Join("|", Directory.GetFiles(dirs.OutboxOf(attempt)).Order(StringComparer.Ordinal)
            .Select(f => Path.GetFileName(f) + ":" + Convert.ToHexString(File.ReadAllBytes(f)) + ":" + File.GetLastWriteTimeUtc(f).Ticks));
        var before = Snapshot();
        var log = new CapturedLog();
        using var agent = Agents.Create(dirs, new FakeBackend(), new FakeSystemd(dirs.Config.OutboxDir), log);
        await agent.Relayer.RelayAsync(Id, agent.Scanner.Scan(Id, ["JG", "JH", "JJ"], includeWritten: true), default);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public async Task Each_content_is_relayed_once_to_a_definitive_answer_and_failures_are_retried()
    {
        dirs.WriteResult(attempt, "JG", "ready", Manifests.For(attempt, "JG"));
        var backend = new FakeBackend { FailAll = Transport.Unavailable };
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-17T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        using var agent = Agents.Create(dirs, backend, new FakeSystemd(dirs.Config.OutboxDir), new CapturedLog(), clock);
        var scan = agent.Scanner.Scan(Id, ["JG"], includeWritten: false);
        Assert.False(await agent.Relayer.RelayAsync(Id, scan, default));
        Assert.False(await agent.Relayer.RelayAsync(Id, scan, default)); // backing off: not even sent
        Assert.Equal(1, backend.Calls.Count(c => c == "relay"));
        clock.Advance(TimeSpan.FromSeconds(1));
        backend.FailAll = null;
        backend.OnRelay = _ => new RelayAnswer.Definitive(RelayOutcomes.Revoked, 409, "AttemptRevoked");
        Assert.True(await agent.Relayer.RelayAsync(Id, scan, default));
        Assert.True(await agent.Relayer.RelayAsync(Id, scan, default));
        Assert.Equal(2, backend.Calls.Count(c => c == "relay")); // never resent after the definitive answer
        Assert.Equal("revoked", agent.Store.RelayEntries(attempt, "JG").Single().Outcome);
        Assert.True(File.Exists(Path.Combine(dirs.OutboxOf(attempt), "JG.manifest.ready"))); // kept for the operator
    }
}
