namespace Blackglass.MissionHost.Agent;

/// <summary>The installed server build the heartbeat reports: build-info.txt's revision and catalogueVersion (the latter from the
/// configuration until Stage 12's build writes it), each validated as the backend validates it.</summary>
public sealed class BuildInfo(string path, string? fallbackCatalogue)
{
    public (string Revision, string CatalogueVersion)? Read()
    {
        try
        {
            if (!File.Exists(path)) return null;
            string? revision = null, catalogue = null;
            foreach (var raw in File.ReadAllLines(path).Take(64))
            {
                var line = raw.Trim();
                if (line.StartsWith("revision=", StringComparison.Ordinal)) revision = line[9..];
                else if (line.StartsWith("catalogueVersion=", StringComparison.Ordinal)) catalogue = line[17..];
            }
            catalogue ??= fallbackCatalogue;
            return Blackglass.Accounts.Domain.Hosts.BuildIdentity.TryParse(revision, catalogue, out var build) ? (build!.Revision, build.CatalogueVersion) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>Serializes unit enumeration with launches (Stage 8 R2, Checkpoint 1 M5): a complete inventory never misses a unit
/// whose launch was acknowledged.</summary>
public sealed class LaunchGate : IDisposable
{
    readonly SemaphoreSlim gate = new(1, 1);

    public async Task<IDisposable> EnterAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        return new Release(gate);
    }

    public void Dispose() => gate.Dispose();

    sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        int released;
        public void Dispose() { if (Interlocked.Exchange(ref released, 1) == 0) gate.Release(); }
    }
}

/// <summary>
/// Claim and launch (Stage 8 R1, the owner's claim-durability requirements). Each step is persisted before the next one that
/// depends on it: the key before the claim is sent; the complete answer (credential, generation, port, participants) before
/// launching is reported; the acknowledgement before the credential is prepared; the root broker's confirmation before systemctl
/// start. A restart resumes from the persisted phase. No unit starts with a generation the backend hasn't acknowledged, an attempt
/// is never started twice (an existing outbox directory, exit record or unit proves it started), and the credential is discarded
/// once the unit has it.
/// Security corrections: the unit is the slot of the attempt's port (blackglass-mission-NN@attempt, its own dynamic uid; a port
/// outside the host's slots is never launched) and starts only when a complete inventory shows no other live unit in that slot
/// (C1); the credential goes to the root broker, never to a file the agent could swap, and nothing starts without its "ok" (I3).
/// </summary>
public sealed class ClaimFlow(IBackend backend, ISystemd systemd, StateStore store, ICredentialBroker broker, AuthGate auth, LaunchGate gate,
    MissionSlots slots, TimeProvider clock, string outboxDir, string exitDir, AgentLog log)
{
    readonly HashSet<string> busyLogged = new(StringComparer.Ordinal);

    static readonly ClaimPhase[] Pending = [ClaimPhase.Keyed, ClaimPhase.Claimed, ClaimPhase.Acknowledged, ClaimPhase.CredentialReady];

    public IEnumerable<ClaimRecord> Unfinished() => store.All().Where(r => Pending.Contains(r.Phase));

    /// <summary>A new claim: the key is written before it is sent.</summary>
    public async Task<ClaimRecord> ClaimNewAsync(CancellationToken ct)
    {
        var record = store.Save(new ClaimRecord { Key = Guid.NewGuid(), Phase = ClaimPhase.Keyed, CreatedAt = clock.GetUtcNow().UtcDateTime });
        return await AdvanceAsync(record, ct);
    }

    /// <summary>Takes a record as far as it can go now; returns it as it then stands (a Keyed record answered 204 is deleted).</summary>
    public async Task<ClaimRecord> AdvanceAsync(ClaimRecord record, CancellationToken ct)
    {
        while (true)
        {
            if (store.Get(record.Key) is { } stored) record = stored; // always the stored state, never a stale copy (M13)
            var next = record.Phase switch
            {
                ClaimPhase.Keyed => await ClaimAsync(record, ct),
                ClaimPhase.Claimed => await LaunchingAsync(record, ct),
                ClaimPhase.Acknowledged => await PrepareCredentialAsync(record, ct),
                ClaimPhase.CredentialReady => await StartAsync(record, ct),
                _ => null,
            };
            if (next is null || next == record) return next ?? record;
            record = next;
        }
    }

    async Task<ClaimRecord?> ClaimAsync(ClaimRecord record, CancellationToken ct)
    {
        if (!auth.MaySend) return record;
        switch (await backend.ClaimAsync(record.Key, ct))
        {
            case ClaimAnswer.Granted { Grant: var g }:
                auth.OnAuthenticated();
                if (store.ForAttempt(g.Attempt) is { } other && other.Key != record.Key)
                {
                    log.Alert("claim_attempt_already_tracked", ("attempt", g.Attempt));
                    return store.Save(record with { Phase = ClaimPhase.Abandoned, Note = "attempt_already_tracked" });
                }
                if (slots.SlotOf(g.MissionPort) is not { } slot)
                {
                    // No template exists for that port's slot: never launched (the backend's launch deadline cancels the attempt).
                    log.Alert("claim_port_outside_slots", ("attempt", g.Attempt), ("port", g.MissionPort), ("port_base", slots.PortBase), ("slots", slots.Count));
                    return store.Save(record with { Phase = ClaimPhase.Abandoned, Attempt = g.Attempt, MissionPort = g.MissionPort, Note = "port_outside_slots" });
                }
                log.Info("claim_granted", ("attempt", g.Attempt), ("generation", g.Generation), ("port", g.MissionPort), ("participants", g.Participants.Length),
                    ("recovered", g.Recovered));
                return store.Save(record with
                {
                    Phase = ClaimPhase.Claimed, Attempt = g.Attempt, MissionPort = g.MissionPort, Slot = slot, Generation = g.Generation, LaunchDeadline = g.LaunchDeadline,
                    Participants = g.Participants, ProcessCredential = g.ProcessCredential,
                });
            case ClaimAnswer.NoWork or ClaimAnswer.HostUnavailable:
                auth.OnAuthenticated();
                if (record.Attempt is null) { store.Delete(record.Key); return null; }
                return record;
            case ClaimAnswer.InvalidState:
                auth.OnAuthenticated();
                log.Warn("claim_key_invalid_state", ("key", record.Key));
                return await AbandonAsync(record, "invalid_state", ct);
            case ClaimAnswer.Malformed m:
                log.Alert("claim_answer_malformed", ("reason", m.Reason)); // never launched; the backend's launch deadline cancels it
                return await AbandonAsync(record, "malformed_answer:" + m.Reason, ct);
            case ClaimAnswer.Refused r:
                log.Alert("claim_refused", ("status", r.Status), ("code", r.Code));
                return await AbandonAsync(record, $"refused:{r.Status}", ct);
            case ClaimAnswer.Failed { Kind: var kind }:
                if (kind == Transport.Unauthorized) auth.OnUnauthorized();
                return record; // the same key is retried later
            default:
                return record;
        }
    }

    async Task<ClaimRecord?> LaunchingAsync(ClaimRecord record, CancellationToken ct)
    {
        if (!auth.MaySend) return record;
        if (clock.GetUtcNow().UtcDateTime > record.LaunchDeadline) return await AbandonAsync(record, "launch_deadline_passed", ct);
        switch (await backend.LaunchingAsync(record.Attempt!, record.Generation, ct))
        {
            case LaunchingAnswer.Acknowledged a:
                auth.OnAuthenticated();
                log.Info("launching_acknowledged", ("attempt", record.Attempt), ("generation", record.Generation), ("state", a.State));
                return store.Save(record with { Phase = ClaimPhase.Acknowledged });
            case LaunchingAnswer.Superseded:
                auth.OnAuthenticated();
                // A recovery took the attempt to a newer generation: claim again with the same key (Stage 8 R1).
                log.Warn("credential_superseded", ("attempt", record.Attempt), ("generation", record.Generation));
                return await ReclaimAsync(record, ct);
            case LaunchingAnswer.InvalidState or LaunchingAnswer.NotFound:
                auth.OnAuthenticated();
                return await AbandonAsync(record, "launching_refused", ct);
            case LaunchingAnswer.Refused r:
                log.Alert("launching_refused", ("attempt", record.Attempt), ("status", r.Status), ("code", r.Code));
                return await AbandonAsync(record, $"launching_refused:{r.Status}", ct);
            case LaunchingAnswer.Failed { Kind: var kind }:
                if (kind == Transport.Unauthorized) auth.OnUnauthorized();
                return record;
            default:
                return record;
        }
    }

    async Task<ClaimRecord?> ReclaimAsync(ClaimRecord record, CancellationToken ct)
    {
        switch (await backend.ClaimAsync(record.Key, ct))
        {
            case ClaimAnswer.Granted { Grant: var g } when g.Attempt == record.Attempt:
                log.Info("claim_recovered", ("attempt", g.Attempt), ("generation", g.Generation));
                return store.Save(record with { Generation = g.Generation, ProcessCredential = g.ProcessCredential, LaunchDeadline = g.LaunchDeadline });
            case ClaimAnswer.InvalidState:
                return await AbandonAsync(record, "invalid_state_after_supersede", ct);
            case ClaimAnswer.Granted:
                log.Alert("claim_recovery_other_attempt", ("attempt", record.Attempt));
                return await AbandonAsync(record, "recovery_other_attempt", ct);
            case ClaimAnswer.Failed { Kind: Transport.Unauthorized }:
                auth.OnUnauthorized();
                return record;
            default:
                return record; // retried later: launching with the old generation answers Superseded again
        }
    }

    /// <summary>The root broker stores the credential for the unit's LoadCredential; anything but its "ok" leaves the claim where
    /// it is (retried, until the launch deadline abandons it).</summary>
    async Task<ClaimRecord> PrepareCredentialAsync(ClaimRecord record, CancellationToken ct)
    {
        if (clock.GetUtcNow().UtcDateTime > record.LaunchDeadline) return await AbandonAsync(record, "launch_deadline_passed", ct);
        if (!await PreparedAsync(record, ct)) return record;
        return store.Save(record with { Phase = ClaimPhase.CredentialReady });
    }

    async Task<bool> PreparedAsync(ClaimRecord record, CancellationToken ct)
    {
        if (record.ProcessCredential is not { } credential) return false;
        var answer = await broker.PrepareAsync(Guid.Parse(record.Attempt!), record.Generation, credential, ct);
        if (!answer.Ok) log.Alert("credential_prepare_failed", ("attempt", record.Attempt), ("generation", record.Generation), ("code", answer.Code));
        return answer.Ok;
    }

    /// <summary>True when the attempt's unit evidently ran: the outbox directory systemd creates for it (its StateDirectory), its
    /// exit record, or systemd still holding an invocation.</summary>
    async Task<bool> EverStartedAsync(MissionUnit unit, CancellationToken ct)
    {
        var attempt = AttemptIds.Text(unit.Attempt);
        if (Directory.Exists(Path.Combine(outboxDir, attempt)) || File.Exists(Path.Combine(exitDir, attempt + ".exit"))) return true;
        return await systemd.ShowAsync(unit, ct) is { } s && (s.Running || s.InvocationId.Length > 0);
    }

    /// <summary>True when a complete inventory shows no live unit of another attempt in the unit's slot (two would share a uid).</summary>
    async Task<bool> SlotFreeAsync(MissionUnit unit, CancellationToken ct)
    {
        var inventory = await systemd.ListAsync(ct);
        if (!inventory.Complete) return false;
        var attempt = AttemptIds.Text(unit.Attempt);
        var busy = inventory.Units.Any(u => u.Slot == unit.Slot && u.AttemptId != attempt && u.ActiveState is not ("inactive" or "failed"));
        if (busy && busyLogged.Add(attempt)) log.Warn("slot_busy", ("attempt", attempt), ("slot", unit.Slot));
        return !busy;
    }

    async Task<ClaimRecord?> StartAsync(ClaimRecord record, CancellationToken ct)
    {
        if (slots.UnitOf(record) is not { } unit) return await AbandonAsync(record, "port_outside_slots", ct);
        using (await gate.EnterAsync(ct))
        {
            // Under the gate, the record as it stands now: a stop directive may have abandoned it (security review M13).
            if (store.ForAttempt(record.Attempt!) is { } current && current.Phase != ClaimPhase.CredentialReady) return current;
            if (await EverStartedAsync(unit, ct))
            {
                log.Info("unit_already_started", ("attempt", record.Attempt));
                return await StartedAsync(record, unit, ClaimPhase.Started, ct);
            }
            if (clock.GetUtcNow().UtcDateTime > record.LaunchDeadline) return await AbandonAsync(record, "launch_deadline_passed", ct);
            if (!await SlotFreeAsync(unit, ct)) return record; // waits for the slot (or the launch deadline)
            // Confirmed again right before the start: idempotent for the same generation, and /run (the broker's store) is empty
            // after a reboot while the record still holds the credential (security review M1). No "ok", no start.
            if (!await PreparedAsync(record, ct)) return record;
            var ok = await systemd.StartAsync(unit, ct);
            var state = await systemd.ShowAsync(unit, ct);
            var started = ok || state is { Running: true } || state is { InvocationId.Length: > 0 };
            if (started) log.Info("unit_started", ("attempt", record.Attempt), ("unit", unit.Name));
            else log.Alert("unit_start_failed", ("attempt", record.Attempt), ("unit", unit.Name));
            return await StartedAsync(record, unit, started ? ClaimPhase.Started : ClaimPhase.StartFailed, ct);
        }
    }

    async Task<ClaimRecord> StartedAsync(ClaimRecord record, MissionUnit unit, ClaimPhase phase, CancellationToken ct)
    {
        var saved = store.Save(record with { Phase = phase, ProcessCredential = null });
        await DiscardAsync(unit.Attempt, ct);
        return saved;
    }

    async Task<ClaimRecord> AbandonAsync(ClaimRecord record, string note, CancellationToken ct)
    {
        var saved = store.Save(record with { Phase = ClaimPhase.Abandoned, ProcessCredential = null, Note = note });
        if (record.Attempt is { } a && AttemptIds.TryParse(a, out var attempt)) await DiscardAsync(attempt, ct);
        log.Info("claim_abandoned", ("key", record.Key), ("attempt", record.Attempt), ("note", note));
        return saved;
    }

    /// <summary>Removes the broker's copy; a failure is logged and leaves only a root-only file on tmpfs (gone at the next boot).</summary>
    async Task DiscardAsync(Guid attempt, CancellationToken ct)
    {
        var answer = await broker.DiscardAsync(attempt, ct);
        if (!answer.Ok) log.Warn("credential_discard_failed", ("attempt", attempt), ("code", answer.Code));
    }
}
