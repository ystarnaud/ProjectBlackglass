namespace Blackglass.MissionHost.Agent;

/// <summary>
/// The running agent: recovery first (resume unfinished claims; relay every published file before any exit report), then three
/// loops: the heartbeat, supervision (relays, exits, rescans) and claims (only with a fresh heartbeat, not drained, not blocked by
/// a refused secret, under the local capacity). Each loop survives its own failures; an exception is logged and retried, never a
/// reason to conclude anything about a mission.
/// </summary>
public sealed class AgentHost : IDisposable
{
    readonly AgentConfig config;
    readonly AgentLog log;
    readonly TimeProvider clock;
    readonly LaunchGate gate = new();
    DateTimeOffset nextDiskAlert = DateTimeOffset.MinValue;

    public StateStore Store { get; }
    public AuthGate Auth { get; }
    public ClaimFlow Claims { get; }
    public Supervisor Supervisor { get; }
    public Heartbeat Heartbeat { get; }
    public Relayer Relayer { get; }
    public OutboxScanner Scanner { get; }

    public AgentHost(AgentConfig config, IBackend backend, ISystemd systemd, ICredentialBroker broker, HostSecret secret, AgentLog log, TimeProvider? clock = null)
    {
        this.config = config;
        this.log = log;
        this.clock = clock ?? TimeProvider.System;
        Store = new StateStore(config.StateDir, config.RelayDir, log);
        Auth = new AuthGate(secret, this.clock, TimeSpan.FromSeconds(config.AuthRetrySeconds), log);
        Scanner = new OutboxScanner(config.OutboxDir, config.MaxResultBytes);
        Relayer = new Relayer(backend, Store, Auth, new Backoff(TimeSpan.FromMilliseconds(config.RelayBackoffMinMs), TimeSpan.FromMilliseconds(config.RelayBackoffMaxMs)),
            this.clock, log);
        var slots = new MissionSlots(config.PortBase, config.LocalCapacity);
        Claims = new ClaimFlow(backend, systemd, Store, broker, Auth, gate, slots, this.clock, config.OutboxDir, config.ExitDir, log);
        Supervisor = new Supervisor(backend, systemd, Store, Scanner, Relayer, Auth, slots, this.clock, config.ExitDir, log);
        Heartbeat = new Heartbeat(backend, systemd, Store, Auth, gate, new BuildInfo(config.ServerBuildInfo, config.CatalogueVersion), OutboxPending, this.clock, log,
            broker);
    }

    int OutboxPending() => Relayer.CountPending(Store.All().Where(Supervisor.Supervised)
        .Select(r => (r.Attempt!, Scanner.Scan(Guid.Parse(r.Attempt!), r.Participants, includeWritten: r.Exit is not null))));

    /// <summary>Startup: unfinished claims resume from their persisted phase, then every published file is relayed before any
    /// exit is reported (spec §9.2 step 4).</summary>
    public async Task RecoverAsync(CancellationToken ct)
    {
        foreach (var record in Claims.Unfinished()) await Guard("recover_claim", () => Claims.AdvanceAsync(record, ct));
        await Guard("recover_relay", () => Supervisor.RelayPublishedAsync(ct));
    }

    /// <summary>One pass of supervision: every supervised attempt one step.</summary>
    public async Task SuperviseOnceAsync(CancellationToken ct)
    {
        foreach (var record in Store.All().Where(Supervisor.Supervised)) await Guard("supervise", () => Supervisor.StepAsync(record, ct));
        CheckDisk();
    }

    /// <summary>One claim attempt when allowed: unfinished claims first, then a new one.</summary>
    public async Task ClaimOnceAsync(CancellationToken ct)
    {
        foreach (var record in Claims.Unfinished()) await Guard("advance_claim", () => Claims.AdvanceAsync(record, ct));
        if (!MayClaim()) return;
        await Guard("claim", () => Claims.ClaimNewAsync(ct));
    }

    public bool MayClaim()
    {
        var fresh = Heartbeat.LastOk is { } ok && clock.GetUtcNow() - ok < TimeSpan.FromSeconds(Math.Max(30, config.HeartbeatSeconds * 3));
        var active = Store.All().Count(r => r.Phase is not (ClaimPhase.Reported or ClaimPhase.Abandoned));
        return fresh && !Heartbeat.Drained && !Auth.Blocked && active < config.LocalCapacity && Claims.Unfinished().All(r => r.Phase != ClaimPhase.Keyed);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        log.Info("agent_started", ("host", config.HostId), ("api", config.ApiBaseUrl.Authority));
        await RecoverAsync(ct);
        await Task.WhenAll(
            Loop("heartbeat", TimeSpan.FromSeconds(config.HeartbeatSeconds), () => BeatAndRecordAsync(ct), ct),
            Loop("supervision", TimeSpan.FromMilliseconds(config.ScanIntervalMs), () => SuperviseOnceAsync(ct), ct),
            Loop("claims", TimeSpan.FromSeconds(1), () => ClaimOnceAsync(ct), ct));
    }

    async Task BeatAndRecordAsync(CancellationToken ct)
    {
        await Heartbeat.BeatAsync(ct);
        Store.WriteStatus(new { lastHeartbeatOk = Heartbeat.LastOk, drained = Heartbeat.Drained, secretRejected = Auth.Blocked }, config.StateDir);
    }

    async Task Loop(string name, TimeSpan interval, Func<Task> body, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Guard(name, body);
            try { await Task.Delay(interval, clock, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    async Task Guard(string what, Func<Task> body)
    {
        try { await body(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            log.Warn("loop_error", ("where", what), ("error", e.GetType().Name)); // the message may hold paths; the type is enough here
        }
    }

    void CheckDisk()
    {
        try
        {
            var drive = new DriveInfo(config.OutboxDir);
            if (drive.TotalSize <= 0) return;
            var used = 100 - (int)(drive.AvailableFreeSpace * 100 / drive.TotalSize);
            if (used < config.OutboxAlertPercent || clock.GetUtcNow() < nextDiskAlert) return;
            nextDiskAlert = clock.GetUtcNow() + TimeSpan.FromHours(1);
            log.Alert("outbox_disk_usage", ("used_percent", used), ("threshold", config.OutboxAlertPercent));
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { }
    }

    public void Dispose() => gate.Dispose();
}
