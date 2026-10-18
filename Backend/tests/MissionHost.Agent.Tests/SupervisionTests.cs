using Blackglass.MissionHost.Agent.Tests.Support;

namespace Blackglass.MissionHost.Agent.Tests;

/// <summary>Tasks 6, 8, 9 and 11: exit evidence, the heartbeat, the host secret's standing, recovery, long outages and the emergency stop.</summary>
public class SupervisionTests : IDisposable
{
    readonly AgentDirs dirs = new();
    readonly FakeBackend backend = new();
    readonly FakeSystemd systemd;
    readonly CapturedLog log = new();
    readonly ManualClock clock = new(DateTimeOffset.UtcNow);

    public SupervisionTests() => systemd = new FakeSystemd(dirs.Config.OutboxDir);

    public void Dispose() => dirs.Dispose();

    AgentHost NewAgent() => Agents.Create(dirs, backend, systemd, log, clock);

    async Task<(AgentHost Agent, string Attempt)> StartedAsync(params string[] participants)
    {
        var a = backend.Admit(participants: participants.Length > 0 ? participants : ["JG", "JH"]);
        var agent = NewAgent();
        Assert.Equal(ClaimPhase.Started, (await agent.Claims.ClaimNewAsync(default)).Phase);
        return (agent, a.Id);
    }

    Task StepAllAsync(AgentHost agent) => Task.WhenAll(agent.Store.All().Where(Supervisor.Supervised).Select(r => agent.Supervisor.StepAsync(r, default)));

    // ---- exit evidence ----

    [Fact]
    public async Task While_the_unit_runs_only_ready_files_are_relayed_and_nothing_is_reported()
    {
        var (agent, attempt) = await StartedAsync();
        using var _ = agent;
        dirs.WriteResult(attempt, "JG", "ready", Manifests.For(attempt, "JG"));
        dirs.WriteResult(attempt, "JH", "written", Manifests.For(attempt, "JH"));
        await StepAllAsync(agent);
        Assert.Single(backend.Relayed);
        Assert.Empty(backend.Exits);
    }

    [Fact]
    public async Task After_a_verified_exit_written_results_are_relayed_first_then_every_participant_is_reported()
    {
        var (agent, attempt) = await StartedAsync("JG", "JH", "JJ");
        using var _ = agent;
        dirs.WriteResult(attempt, "JG", "ready", Manifests.For(attempt, "JG"));
        dirs.WriteResult(attempt, "JH", "written", Manifests.For(attempt, "JH")); // crash between publication steps 2 and 4
        dirs.WriteResult(attempt, "JJ", "tmp", "half"u8.ToArray()); // crash during step 1: no result
        File.WriteAllText(Path.Combine(dirs.Config.ExitDir, attempt + ".exit"), "code=killed\nstatus=KILL\n");
        systemd.Exit(Guid.Parse(attempt), signal: 9);
        await StepAllAsync(agent);
        Assert.Equal(2, backend.Relayed.Count);
        var (_, body) = backend.Exits.Single();
        Assert.Equal((null, "SIGKILL", true), (body.ExitCode, body.Signal, body.OutboxComplete));
        Assert.Equal([("JG", true), ("JH", true), ("JJ", false)], body.Results);
        var relayIndex = backend.Calls.FindLastIndex(c => c == "relay");
        Assert.True(relayIndex < backend.Calls.FindIndex(c => c.StartsWith("exited:", StringComparison.Ordinal))); // results before absence
        Assert.Equal(ClaimPhase.Reported, agent.Store.All().Single().Phase);
        Assert.True(File.Exists(Path.Combine(dirs.OutboxOf(attempt), "JH.manifest.written"))); // relayed in place, never renamed
    }

    [Fact]
    public async Task No_exit_report_is_sent_while_any_result_lacks_a_definitive_answer()
    {
        var (agent, attempt) = await StartedAsync("JG");
        using var _ = agent;
        dirs.WriteResult(attempt, "JG", "written", Manifests.For(attempt, "JG"));
        systemd.Exit(Guid.Parse(attempt), code: 0);
        backend.FailAll = Transport.Unavailable; // 503, a timeout, the tunnel down
        for (var i = 0; i < 5; i++)
        {
            await StepAllAsync(agent);
            clock.Advance(TimeSpan.FromMinutes(5));
        }
        Assert.Empty(backend.Exits);
        backend.FailAll = null;
        await StepAllAsync(agent);
        Assert.Equal([("JG", true)], backend.Exits.Single().Body.Results);
    }

    [Fact]
    public async Task An_incomplete_scan_is_reported_as_such_and_the_complete_rescan_follows_as_the_observation()
    {
        var (agent, attempt) = await StartedAsync("JG");
        using var _ = agent;
        systemd.Exit(Guid.Parse(attempt), code: 1);
        Directory.Delete(dirs.OutboxOf(attempt)); // the scan can't enumerate: never evidence of absence
        await StepAllAsync(agent);
        Assert.Equal((false, 1), (backend.Exits.Single().Body.OutboxComplete, backend.Exits.Single().Body.ExitCode));
        Assert.True(agent.Store.All().Single().ReportedIncomplete);
        Directory.CreateDirectory(dirs.OutboxOf(attempt));
        clock.Advance(TimeSpan.FromMinutes(1));
        await StepAllAsync(agent);
        Assert.Equal(2, backend.Exits.Count);
        Assert.Equal((true, 1, false), (backend.Exits[1].Body.OutboxComplete, backend.Exits[1].Body.ExitCode, backend.Exits[1].Body.Results.Single().Present));
        Assert.False(agent.Store.All().Single().ReportedIncomplete);
    }

    [Fact]
    public async Task A_forgotten_unit_without_an_exit_record_is_reported_with_no_status_never_an_invented_one()
    {
        var (agent, attempt) = await StartedAsync("JG");
        using var _ = agent;
        systemd.Forget(Guid.Parse(attempt));
        await StepAllAsync(agent);
        var body = backend.Exits.Single().Body;
        Assert.Equal((null, null), (body.ExitCode, body.Signal));
    }

    [Fact]
    public async Task A_rejected_exit_report_is_an_alert_and_is_not_resent()
    {
        var (agent, attempt) = await StartedAsync("JG");
        using var _ = agent;
        systemd.Exit(Guid.Parse(attempt));
        backend.OnExited = (_, _) => new ExitedAnswer.Rejected(409, "ConflictingReport");
        await StepAllAsync(agent);
        await StepAllAsync(agent);
        Assert.Single(backend.Exits);
        Assert.Contains("exit_report_rejected", log.Text, StringComparison.Ordinal);
    }

    // ---- restart, reboot, long outage ----

    [Fact]
    public async Task At_restart_every_published_result_is_relayed_before_any_exit_report()
    {
        var (agent, attempt) = await StartedAsync("JG");
        agent.Dispose();
        dirs.WriteResult(attempt, "JG", "ready", Manifests.For(attempt, "JG"));
        systemd.Exit(Guid.Parse(attempt));
        backend.Calls.Clear();
        using var restarted = NewAgent();
        await restarted.RecoverAsync(default);
        await restarted.SuperviseOnceAsync(default);
        Assert.Equal("relay", backend.Calls.First(c => c == "relay" || c.StartsWith("exited:", StringComparison.Ordinal)));
        Assert.Single(backend.Relayed); // not again during supervision
        Assert.Single(backend.Exits);
    }

    [Fact]
    public async Task After_a_reboot_the_persisted_claims_carry_on_and_a_lost_unit_is_reported_without_a_status()
    {
        var (agent, attempt) = await StartedAsync("JG");
        agent.Dispose();
        systemd.Units.Clear(); // the reboot: systemd remembers nothing, /run is empty
        if (Directory.Exists(dirs.Config.LaunchDir)) Directory.Delete(dirs.Config.LaunchDir, recursive: true);
        dirs.WriteResult(attempt, "JG", "written", Manifests.For(attempt, "JG"));
        using var restarted = NewAgent();
        await restarted.RecoverAsync(default);
        await restarted.SuperviseOnceAsync(default);
        var body = backend.Exits.Single().Body;
        Assert.Equal((null, null, true, true), (body.ExitCode, body.Signal, body.OutboxComplete, body.Results.Single().Present));
        Assert.DoesNotContain(systemd.Calls, c => c.StartsWith("start:", StringComparison.Ordinal) && systemd.Calls.Count(x => x == c) > 1);
    }

    [Fact]
    public async Task An_hour_without_the_backend_reports_no_absence_and_everything_settles_after()
    {
        var (agent, attempt) = await StartedAsync("JG");
        using var _ = agent;
        systemd.Exit(Guid.Parse(attempt), signal: 9);
        backend.FailAll = Transport.Unavailable;
        for (var minute = 0; minute < 60; minute++)
        {
            await agent.SuperviseOnceAsync(default);
            await agent.Heartbeat.BeatAsync(default);
            await agent.ClaimOnceAsync(default);
            clock.Advance(TimeSpan.FromMinutes(1));
        }
        Assert.Empty(backend.Exits);
        Assert.Empty(backend.Relayed);
        Assert.Equal(ClaimPhase.Started, agent.Store.All().Single().Phase);
        backend.FailAll = null;
        await agent.SuperviseOnceAsync(default);
        Assert.Equal(("SIGKILL", false), (backend.Exits.Single().Body.Signal, backend.Exits.Single().Body.Results.Single().Present));
    }

    // ---- the heartbeat (Stage 8 R2, R4) ----

    [Fact]
    public async Task The_inventory_includes_acknowledged_launches_and_is_incomplete_when_enumeration_fails()
    {
        backend.Admit();
        using var agent = NewAgent();
        backend.CrashAt = "launching:after";
        Assert.False(await Agents.SurvivesAsync(() => agent.Claims.ClaimNewAsync(default)));
        var record = agent.Store.All().Single();
        agent.Store.Save(record with { Phase = ClaimPhase.Acknowledged });
        await agent.Heartbeat.BeatAsync(default);
        var beat = backend.Heartbeats.Last();
        Assert.True(beat.InventoryComplete);
        Assert.Equal([(record.Attempt!, "activating")], beat.Units.Select(u => (u.AttemptId, u.ActiveState)));
        Assert.Equal(("r1", "sha256:" + new string('5', 64)), (beat.BuildRevision, beat.CatalogueVersion));
        systemd.ListFails = true;
        await agent.Heartbeat.BeatAsync(default);
        Assert.False(backend.Heartbeats.Last().InventoryComplete);
    }

    [Fact]
    public async Task A_stop_directive_stops_only_listed_mission_units_and_drain_pauses_claims()
    {
        var (agent, attempt) = await StartedAsync("JG");
        using var _ = agent;
        backend.HeartbeatReply = new HeartbeatAnswer.Ok(true, [attempt, Guid.CreateVersion7().ToString("D"), "../../etc", "0192F1A4-5C2E-7B1D-9A0E-3F6C1D2B8E41"]);
        await agent.Heartbeat.BeatAsync(default);
        Assert.Equal([$"stop:{attempt}"], systemd.Calls.Where(c => c.StartsWith("stop:", StringComparison.Ordinal)));
        Assert.True(agent.Heartbeat.Drained);
        Assert.False(agent.MayClaim());
    }

    [Fact]
    public async Task Without_build_information_no_heartbeat_is_sent_and_an_alert_is_raised()
    {
        File.Delete(dirs.Config.ServerBuildInfo);
        using var agent = NewAgent();
        await agent.Heartbeat.BeatAsync(default);
        Assert.Empty(backend.Heartbeats);
        Assert.Contains("build_info_unavailable", log.Text, StringComparison.Ordinal);
    }

    // ---- the host secret's standing (A10-D8) ----

    [Fact]
    public async Task A_rejected_unchanged_secret_stops_claims_alerts_once_retries_slowly_and_supervision_goes_on()
    {
        var (agent, attempt) = await StartedAsync("JG");
        using var _ = agent;
        backend.FailAll = Transport.Unauthorized;
        await agent.Heartbeat.BeatAsync(default);
        Assert.True(agent.Auth.Blocked);
        Assert.False(agent.MayClaim());
        var calls = backend.Calls.Count;
        for (var i = 0; i < 10; i++) await agent.Heartbeat.BeatAsync(default);
        Assert.Equal(calls, backend.Calls.Count); // no rapid loop
        Assert.Single(log.Text.Split('\n'), l => l.Contains("host_secret_rejected", StringComparison.Ordinal));
        Assert.DoesNotContain(systemd.Calls, c => c.StartsWith("stop:", StringComparison.Ordinal)); // a 401 never stops a mission
        systemd.Exit(Guid.Parse(attempt));
        dirs.WriteResult(attempt, "JG", "written", Manifests.For(attempt, "JG"));
        await StepAllAsync(agent); // the exit is observed and recorded locally
        Assert.NotNull(agent.Store.All().Single().Exit);
        Assert.True(File.Exists(Path.Combine(dirs.OutboxOf(attempt), "JG.manifest.written")));
        clock.Advance(TimeSpan.FromSeconds(61));
        await agent.Heartbeat.BeatAsync(default); // the slow retry
        Assert.Equal(calls + 1, backend.Calls.Count);
        Assert.DoesNotContain(dirs.SecretText, log.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rotated_secret_and_a_reload_signal_restore_claims_after_reauthorization()
    {
        using var agent = NewAgent();
        backend.FailAll = Transport.Unauthorized;
        await agent.Heartbeat.BeatAsync(default);
        Assert.True(agent.Auth.Blocked);
        dirs.WriteSecret(Blackglass.Accounts.Domain.Auth.OpaqueSecret.New("hs1").Text); // the operator rotates (atomic replace) and re-enables
        backend.FailAll = null;
        agent.Auth.OnReloadSignal(); // SIGHUP
        await agent.Heartbeat.BeatAsync(default);
        Assert.False(agent.Auth.Blocked);
        Assert.True(agent.MayClaim());
    }

    [Fact]
    public async Task A_401_with_a_changed_secret_on_disk_reloads_without_blocking()
    {
        using var agent = NewAgent();
        dirs.WriteSecret(Blackglass.Accounts.Domain.Auth.OpaqueSecret.New("hs1").Text);
        backend.FailAll = Transport.Unauthorized;
        await agent.Heartbeat.BeatAsync(default);
        Assert.False(agent.Auth.Blocked);
        Assert.Contains("host_secret_reloaded", log.Text, StringComparison.Ordinal);
    }

    // ---- the emergency stop (A10-D5) ----

    [Fact]
    public async Task The_emergency_stop_is_root_only_explicit_dry_runnable_audited_and_idempotent()
    {
        var (agent, attempt) = await StartedAsync("JG");
        using var _ = agent;
        dirs.WriteResult(attempt, "JG", "ready", Manifests.For(attempt, "JG"));
        var outboxBefore = File.ReadAllBytes(Path.Combine(dirs.OutboxOf(attempt), "JG.manifest.ready"));
        var claimsBefore = Directory.GetFiles(Path.Combine(dirs.Config.StateDir, "claims")).Select(File.ReadAllText).ToList();
        var output = new StringWriter();
        var audit = Path.Combine(dirs.Root, "emergency.log");
        Assert.Equal(EmergencyStop.NotRoot, await EmergencyStop.RunAsync(systemd, false, false, true, "test", output, audit, clock, default));
        Assert.Equal(EmergencyStop.Usage, await EmergencyStop.RunAsync(systemd, true, false, false, "test", output, audit, clock, default)); // no --yes
        Assert.Equal(EmergencyStop.Usage, await EmergencyStop.RunAsync(systemd, true, true, false, " ", output, audit, clock, default)); // no reason
        Assert.Equal(0, await EmergencyStop.RunAsync(systemd, true, true, false, "drill", output, audit, clock, default));
        Assert.Contains($"would stop blackglass-mission@{attempt}.service", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(systemd.Calls, c => c.StartsWith("stop:", StringComparison.Ordinal));
        Assert.Equal(0, await EmergencyStop.RunAsync(systemd, true, false, true, "host compromised", output, audit, clock, default));
        Assert.Equal(0, await EmergencyStop.RunAsync(systemd, true, false, true, "again", output, audit, clock, default));
        Assert.Single(systemd.Calls, c => c == $"stop:{attempt}"); // idempotent
        Assert.Contains("reason=\"host compromised\"", File.ReadAllText(audit), StringComparison.Ordinal);
        Assert.Equal(outboxBefore, File.ReadAllBytes(Path.Combine(dirs.OutboxOf(attempt), "JG.manifest.ready")));
        Assert.Equal(claimsBefore, Directory.GetFiles(Path.Combine(dirs.Config.StateDir, "claims")).Select(File.ReadAllText).ToList());
    }
}
