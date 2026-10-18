using Blackglass.MissionHost.Agent.Tests.Support;

namespace Blackglass.MissionHost.Agent.Tests;

/// <summary>The security review's code findings (I1, I2, M1, M2, M12, M13), each reproduced first.</summary>
public class SecurityReviewTests : IDisposable
{
    readonly AgentDirs dirs = new();
    readonly FakeBackend backend = new();
    readonly FakeSystemd systemd;
    readonly CapturedLog log = new();

    public SecurityReviewTests() => systemd = new FakeSystemd(dirs.Config.OutboxDir);

    public void Dispose() => dirs.Dispose();

    AgentHost NewAgent() => Agents.Create(dirs, backend, systemd, log);

    /// <summary>systemd whose start command fails (times out) although PID1 goes on starting the unit, and whose show
    /// fails the first <c>showFailures</c> times.</summary>
    sealed class SlowSystemd(FakeSystemd inner, int showFailures) : ISystemd
    {
        int failures = showFailures;
        public readonly List<string> Stops = [];
        public Task<bool> StartAsync(MissionUnit unit, CancellationToken ct) { inner.StartAsync(unit, ct); return Task.FromResult(false); }
        public Task<bool> StopAsync(MissionUnit unit, CancellationToken ct) { Stops.Add(unit.Attempt.ToString("D")); return inner.StopAsync(unit, ct); }
        public Task<UnitInventory> ListAsync(CancellationToken ct) => inner.ListAsync(ct);
        public Task<UnitState?> ShowAsync(MissionUnit unit, CancellationToken ct) => failures-- > 0 ? Task.FromResult<UnitState?>(null) : inner.ShowAsync(unit, ct);
    }

    [Fact]
    public async Task I2_a_start_that_timed_out_is_stopped_and_reported_only_once_systemd_shows_it_terminal()
    {
        var a = backend.Admit(participants: ["JG"]);
        var slow = new SlowSystemd(systemd, showFailures: 3); // two during the start step, one after the stop
        var secret = new HostSecret(dirs.Config.HostSecretFile);
        secret.Reload();
        using var agent = new AgentHost(dirs.Config with { RelayBackoffMinMs = 10, RelayBackoffMaxMs = 20 }, backend, slow, new StoreBroker(dirs.Root), secret, log.Log);
        var record = await agent.Claims.ClaimNewAsync(default); // start "failed" (timed out) and show didn't answer
        Assert.Equal(ClaimPhase.StartFailed, record.Phase);
        dirs.WriteResult(a.Id, "JG", "written", Manifests.For(a.Id, "JG")); // the live process's unpublished result
        await agent.Supervisor.StepAsync(agent.Store.All().Single(), default); // first, stop the unit (cancels a queued start)
        Assert.Equal([a.Id], slow.Stops);
        Assert.Empty(backend.Exits);
        Assert.Empty(backend.Relayed); // the .written of a possibly live process was never read
        await agent.Supervisor.StepAsync(agent.Store.All().Single(), default); // show doesn't answer: nothing is concluded
        Assert.Empty(backend.Exits);
        Assert.Empty(backend.Relayed);
        await agent.Supervisor.StepAsync(agent.Store.All().Single(), default); // terminal after the stop: relay, then report
        Assert.Single(backend.Relayed);
        Assert.Equal("SIGTERM", backend.Exits.Single().Body.Signal);
    }

    [Fact]
    public async Task M1_a_credential_lost_with_run_is_rewritten_from_the_record_before_start()
    {
        var a = backend.Admit();
        using (var first = NewAgent())
        {
            systemd.CrashAt = null;
            backend.CrashAt = null;
            var record = await first.Claims.AdvanceAsync(first.Store.Save(new ClaimRecord { Key = Guid.NewGuid(), Phase = ClaimPhase.Keyed, CreatedAt = DateTime.UtcNow }), default);
            Assert.Equal(ClaimPhase.Started, record.Phase); // baseline
        }
        var b = backend.Admit();
        using var agent = NewAgent();
        var key = Guid.NewGuid();
        var claimed = agent.Store.Save(new ClaimRecord { Key = key, Phase = ClaimPhase.Keyed, CreatedAt = DateTime.UtcNow });
        backend.CrashAt = "launching:after";
        Assert.False(await Agents.SurvivesAsync(() => agent.Claims.AdvanceAsync(claimed, default)));
        var acknowledged = agent.Store.Save(agent.Store.All().Single(r => r.Key == key) with { Phase = ClaimPhase.Acknowledged });
        acknowledged = agent.Store.Save(acknowledged with { Phase = ClaimPhase.CredentialReady }); // prepared, then a reboot wiped /run
        var broker = new StoreBroker(dirs.Root); // the broker's store the agent uses (the same directory)
        Assert.Null(broker.Credential(Guid.Parse(b.Id)));
        systemd.OnStart = attempt => Assert.NotNull(broker.Credential(attempt)); // LoadCredential finds it
        Assert.Equal(ClaimPhase.Started, (await agent.Claims.AdvanceAsync(acknowledged, default)).Phase);
    }

    [Fact]
    public void M2_an_entry_that_names_a_participant_but_breaks_the_naming_contract_makes_the_scan_incomplete()
    {
        var attempt = Guid.CreateVersion7().ToString("D");
        dirs.WriteResult(attempt, "JG", "ready", Manifests.For(attempt, "JG"));
        File.WriteAllBytes(Path.Combine(dirs.OutboxOf(attempt), "JH.json"), Manifests.For(attempt, "JH")); // an old contract's name
        File.WriteAllBytes(Path.Combine(dirs.OutboxOf(attempt), "JJ.manifest.READY"), Manifests.For(attempt, "JJ"));
        var scan = new OutboxScanner(dirs.Config.OutboxDir, 1 << 20).Scan(Guid.Parse(attempt), ["JG", "JH", "JJ"], includeWritten: true);
        Assert.False(scan.Complete); // never absence evidence for JH or JJ
        Assert.True(scan.Present("JG"));
    }

    [Fact]
    public void M12_an_outbox_flooded_with_entries_is_incomplete_and_never_fully_listed()
    {
        var attempt = Guid.CreateVersion7().ToString("D");
        Directory.CreateDirectory(dirs.OutboxOf(attempt));
        for (var i = 0; i < OutboxScanner.MaxEntries + 10; i++) File.WriteAllText(Path.Combine(dirs.OutboxOf(attempt), $"junk{i}"), "");
        var scan = new OutboxScanner(dirs.Config.OutboxDir, 1 << 20).Scan(Guid.Parse(attempt), ["JG"], includeWritten: true);
        Assert.False(scan.Complete);
        Assert.Contains("outbox_too_many_entries", scan.Anomalies);
    }

    [Fact]
    public async Task M13_a_stop_directive_for_an_attempt_not_yet_started_abandons_it_and_nothing_starts_later()
    {
        var a = backend.Admit();
        using var agent = NewAgent();
        backend.CrashAt = "launching:after";
        Assert.False(await Agents.SurvivesAsync(() => agent.Claims.ClaimNewAsync(default)));
        var record = agent.Store.Save(agent.Store.All().Single() with { Phase = ClaimPhase.Acknowledged });
        backend.HeartbeatReply = new HeartbeatAnswer.Ok(false, [a.Id]);
        await agent.Heartbeat.BeatAsync(default);
        Assert.Equal(ClaimPhase.Abandoned, agent.Store.All().Single().Phase);
        await agent.Claims.AdvanceAsync(record, default); // a stale copy of the record must not start it
        Assert.DoesNotContain(systemd.Calls, c => c.StartsWith("start:", StringComparison.Ordinal));
    }

    [Fact]
    public void I1_the_tunnel_port_counts_only_when_its_listener_belongs_to_the_tunnel_user()
    {
        const string table = "  sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode\n"
            + "   0: 0100007F:13D9 00000000:0000 0A 00000000:00000000 00:00000000 00000000   997        0 23456 1 0000000000000000 100 0 0 10 0\n"
            + "   1: 0100007F:1F90 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 23457 1 0000000000000000 100 0 0 10 0\n"
            + "   2: 0100007F:13D9 0100007F:C350 01 00000000:00000000 00:00000000 00000000   999        0 23458 1 0000000000000000 100 0 0 10 0\n";
        Assert.Equal(997u, TunnelListener.ListenerUid(table, 5081));
        Assert.Equal(1000u, TunnelListener.ListenerUid(table, 8080));
        Assert.Null(TunnelListener.ListenerUid(table, 5082));
        Assert.Equal(997u, TunnelListener.UidOf("root:x:0:0::/root:/bin/bash\nblackglass-tunnel:x:997:997::/var/lib/blackglass/tunnel:/usr/sbin/nologin\n", "blackglass-tunnel"));
        Assert.Null(TunnelListener.UidOf("root:x:0:0::/root:/bin/bash\n", "blackglass-tunnel"));
    }

    [Fact]
    public async Task I1_no_request_is_sent_while_the_tunnel_port_belongs_to_someone_else()
    {
        var sent = 0;
        var handler = new CountingHandler(() => sent++);
        var secret = new HostSecret(dirs.Config.HostSecretFile);
        secret.Reload();
        var trusted = false;
        var client = new BackendClient(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5081/") }, secret, () => trusted);
        Assert.Equal(Transport.Unavailable, Assert.IsType<HeartbeatAnswer.Failed>(await client.HeartbeatAsync(new HeartbeatBody("r1", "sha256:" + new string('5', 64), true, [], 0), default)).Kind);
        Assert.Equal(Transport.Unavailable, Assert.IsType<RelayAnswer.Failed>(await client.RelayAsync("{}"u8.ToArray(), default)).Kind);
        Assert.Equal(0, sent); // the secret never left
        trusted = true;
        await client.RelayAsync("{}"u8.ToArray(), default);
        Assert.Equal(1, sent);
    }

    sealed class CountingHandler(Action onSend) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            onSend();
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }
}
