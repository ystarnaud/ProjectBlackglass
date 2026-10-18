namespace Blackglass.MissionHost.Agent;

/// <summary>
/// Supervision of started attempts (spec §7.4, §9.2; Stage 9 S9-B7 and §H as amended by A10-D1; the owner's exit-evidence rules):
/// - while the unit runs, its published (.ready) results are relayed;
/// - once systemd shows the unit terminal (the agent's own verification), its exit status is taken from the exit record (or from
///   systemd, if it still holds it; otherwise unknown), the outbox is scanned including complete .written files, and every
///   relayable file is relayed to a definitive answer before anything else;
/// - only then is "exited" sent, listing every participant of the claim with present = a valid result file exists, and
///   outbox.complete only for a scan that was complete; an incomplete scan is later rescanned and, once complete, sent again as
///   the supplementary observation.
/// A 503, a timeout, a lost tunnel or a failed scan never produces a report of absence: they only delay it.
/// </summary>
public sealed class Supervisor(IBackend backend, ISystemd systemd, StateStore store, OutboxScanner scanner, Relayer relayer, AuthGate auth,
    TimeProvider clock, string exitDir, AgentLog log)
{
    readonly Dictionary<Guid, DateTimeOffset> nextRescan = new();
    static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(30);

    public static bool Supervised(ClaimRecord r) =>
        r.Phase is ClaimPhase.Started or ClaimPhase.StartFailed || (r.Phase == ClaimPhase.Reported && r.ReportedIncomplete);

    /// <summary>At startup, before any exit report: relays every published file of every supervised attempt (spec §9.2 step 4).</summary>
    public async Task RelayPublishedAsync(CancellationToken ct)
    {
        foreach (var record in store.All().Where(Supervised))
        {
            var attempt = Guid.Parse(record.Attempt!);
            await relayer.RelayAsync(attempt, scanner.Scan(attempt, record.Participants, includeWritten: false), ct);
        }
    }

    public async Task<ClaimRecord> StepAsync(ClaimRecord record, CancellationToken ct)
    {
        var attempt = Guid.Parse(record.Attempt!);
        if (record.Phase == ClaimPhase.Reported) return await RescanAsync(record, attempt, ct);

        var exit = record.Exit;
        if (record.Phase == ClaimPhase.Started && exit is null)
        {
            var state = await systemd.ShowAsync(attempt, ct);
            if (state is null) return record; // systemd didn't answer: nothing is concluded
            if (!state.Terminal)
            {
                await relayer.RelayAsync(attempt, scanner.Scan(attempt, record.Participants, includeWritten: false), ct);
                return record;
            }
            exit = ExitRecords.Read(exitDir, attempt) ?? (state.Exit.Known ? state.Exit : ExitStatus.Unknown);
            record = store.Save(record with { Exit = exit });
            log.Info("unit_exited", ("attempt", record.Attempt), ("exit_code", exit.ExitCode), ("signal", exit.Signal), ("result", state.Result));
        }
        else if (record.Phase == ClaimPhase.StartFailed && exit is null)
        {
            exit = ExitRecords.Read(exitDir, attempt) ?? ExitStatus.Unknown;
            record = store.Save(record with { Exit = exit });
        }

        // The unit is gone (verified above): complete .written files are results now, relayed in place (A10-D1).
        var scan = scanner.Scan(attempt, record.Participants, includeWritten: true);
        if (!await relayer.RelayAsync(attempt, scan, ct)) return record; // results first, then the report
        return await ReportAsync(record, attempt, exit!, scan, ct);
    }

    async Task<ClaimRecord> ReportAsync(ClaimRecord record, Guid attempt, ExitStatus exit, OutboxScan scan, CancellationToken ct)
    {
        if (!auth.MaySend) return record;
        var body = new ExitedBody(exit.ExitCode, exit.Signal, record.Participants.Select(a => (a, scan.Present(a))).ToList(), scan.Complete);
        switch (await backend.ExitedAsync(record.Attempt!, body, ct))
        {
            case ExitedAnswer.Applied a:
                auth.OnAuthenticated();
                log.Info("exit_reported", ("attempt", record.Attempt), ("applied", a.Outcome), ("outbox_complete", scan.Complete),
                    ("present", body.Results.Count(r => r.Present)), ("participants", body.Results.Count));
                var incomplete = !scan.Complete && record.Phase == ClaimPhase.Started;
                if (incomplete) nextRescan[attempt] = clock.GetUtcNow() + RescanInterval;
                return store.Save(record with { Phase = ClaimPhase.Reported, ReportedIncomplete = incomplete });
            case ExitedAnswer.Rejected r:
                auth.OnAuthenticated();
                log.Alert("exit_report_rejected", ("attempt", record.Attempt), ("status", r.Status), ("code", r.Code));
                return store.Save(record with { Phase = ClaimPhase.Reported, ReportedIncomplete = false, Note = $"exit_rejected:{r.Status}:{r.Code}" });
            case ExitedAnswer.Failed { Kind: var kind }:
                if (kind == Transport.Unauthorized) auth.OnUnauthorized();
                return record;
            default:
                return record;
        }
    }

    /// <summary>After a report of an incomplete scan: rescan now and then; a complete rescan, its results relayed, is sent as the
    /// supplementary observation (the same exit status, outbox.complete true).</summary>
    async Task<ClaimRecord> RescanAsync(ClaimRecord record, Guid attempt, CancellationToken ct)
    {
        if (nextRescan.TryGetValue(attempt, out var due) && clock.GetUtcNow() < due) return record;
        nextRescan[attempt] = clock.GetUtcNow() + RescanInterval;
        var scan = scanner.Scan(attempt, record.Participants, includeWritten: true);
        if (!scan.Complete) return record;
        if (!await relayer.RelayAsync(attempt, scan, ct)) return record;
        if (!auth.MaySend) return record;
        var exit = record.Exit ?? ExitStatus.Unknown;
        var body = new ExitedBody(exit.ExitCode, exit.Signal, record.Participants.Select(a => (a, scan.Present(a))).ToList(), true);
        switch (await backend.ExitedAsync(record.Attempt!, body, ct))
        {
            case ExitedAnswer.Applied a:
                auth.OnAuthenticated();
                log.Info("exit_observation_reported", ("attempt", record.Attempt), ("applied", a.Outcome));
                nextRescan.Remove(attempt);
                return store.Save(record with { ReportedIncomplete = false });
            case ExitedAnswer.Rejected r:
                auth.OnAuthenticated();
                log.Alert("exit_observation_rejected", ("attempt", record.Attempt), ("status", r.Status), ("code", r.Code));
                return store.Save(record with { ReportedIncomplete = false, Note = $"observation_rejected:{r.Status}:{r.Code}" });
            case ExitedAnswer.Failed { Kind: Transport.Unauthorized }:
                auth.OnUnauthorized();
                return record;
            default:
                return record;
        }
    }
}

/// <summary>
/// The host heartbeat (spec §16.2, Stage 8 R2 and R4): every few seconds, the installed build, a unit inventory taken under the
/// launch gate (complete only from a zero-exit, fully parsed enumeration, plus every acknowledged launch not yet listed), and the
/// count of published results not yet definitively relayed. The answer's drain flag pauses claims; its stop list stops units the
/// backend has ended (an authenticated directive, validated, and only for units that exist).
/// </summary>
public sealed class Heartbeat(IBackend backend, ISystemd systemd, StateStore store, AuthGate auth, LaunchGate gate, BuildInfo build,
    Func<int> outboxPending, TimeProvider clock, AgentLog log)
{
    bool buildAlerted;

    public bool Drained { get; private set; }
    public DateTimeOffset? LastOk { get; private set; }

    public async Task BeatAsync(CancellationToken ct)
    {
        if (!auth.MaySend) return;
        if (build.Read() is not { } identity)
        {
            if (!buildAlerted) log.Alert("build_info_unavailable");
            buildAlerted = true;
            return;
        }
        buildAlerted = false;
        UnitInventory inventory;
        using (await gate.EnterAsync(ct))
        {
            inventory = await systemd.ListAsync(ct);
            var listed = inventory.Units.Select(u => u.AttemptId).ToHashSet(StringComparer.Ordinal);
            var launching = store.All().Where(r => r.Phase is ClaimPhase.Acknowledged or ClaimPhase.CredentialReady && r.Attempt is not null && !listed.Contains(r.Attempt))
                .Select(r => new UnitEntry(r.Attempt!, "activating"));
            inventory = inventory with { Units = [.. inventory.Units, .. launching] };
        }
        var complete = inventory.Complete && inventory.Units.Count <= 64;
        var units = inventory.Units.Take(64).ToList();
        switch (await backend.HeartbeatAsync(new HeartbeatBody(identity.Revision, identity.CatalogueVersion, complete, units, outboxPending()), ct))
        {
            case HeartbeatAnswer.Ok ok:
                auth.OnAuthenticated();
                if (ok.Drained != Drained) log.Info("host_drain_changed", ("drained", ok.Drained));
                Drained = ok.Drained;
                LastOk = clock.GetUtcNow();
                foreach (var stop in ok.Stop)
                {
                    if (!AttemptIds.TryParse(stop, out var attempt) || units.All(u => u.AttemptId != stop)) continue;
                    var stopped = await systemd.StopAsync(attempt, ct);
                    log.Info("unit_stop_directive", ("attempt", stop), ("stopped", stopped));
                }
                break;
            case HeartbeatAnswer.Failed { Kind: Transport.Unauthorized }:
                auth.OnUnauthorized();
                break;
            case HeartbeatAnswer.Refused r:
                log.Alert("heartbeat_refused", ("status", r.Status), ("code", r.Code));
                break;
        }
    }
}
