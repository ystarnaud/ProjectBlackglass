using System.Text;

namespace Blackglass.MissionHost.Agent;

/// <summary>
/// The process credential handed to a unit (spec §4.3, §7.4): LaunchDir/&lt;attempt&gt;/process, 0400 in a 0700 directory, both the
/// agent's, on tmpfs. The template's LoadCredential=process:… has systemd (root) copy it into the unit's private credential
/// directory at start; the file is deleted once the unit started (or definitely didn't), and never logged.
/// </summary>
public sealed class CredentialHandoff(string launchDir)
{
    public string PathOf(Guid attempt) => Path.Combine(launchDir, AttemptIds.Text(attempt), "process");

    public bool Exists(Guid attempt) => File.Exists(PathOf(attempt));

    public void Write(Guid attempt, string credential)
    {
        AtomicFile.EnsurePrivateDirectory(launchDir);
        var directory = Path.Combine(launchDir, AttemptIds.Text(attempt));
        AtomicFile.EnsurePrivateDirectory(directory);
        var path = PathOf(attempt);
        AtomicFile.Write(path, Encoding.UTF8.GetBytes(credential));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead);
    }

    public void Delete(Guid attempt)
    {
        var path = PathOf(attempt);
        if (File.Exists(path))
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            AtomicFile.Delete(path);
        }
        var directory = Path.GetDirectoryName(path)!;
        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
    }
}

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
/// launching is reported; the acknowledgement before the credential file; the credential file before systemctl start. A restart
/// resumes from the persisted phase. No unit starts with a generation the backend hasn't acknowledged, an attempt is never started
/// twice (an existing outbox directory, exit record or unit proves it started), and the credential is deleted once the unit has it.
/// </summary>
public sealed class ClaimFlow(IBackend backend, ISystemd systemd, StateStore store, CredentialHandoff handoff, AuthGate auth, LaunchGate gate,
    TimeProvider clock, string outboxDir, string exitDir, AgentLog log)
{
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
            var next = record.Phase switch
            {
                ClaimPhase.Keyed => await ClaimAsync(record, ct),
                ClaimPhase.Claimed => await LaunchingAsync(record, ct),
                ClaimPhase.Acknowledged => WriteCredential(record),
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
                log.Info("claim_granted", ("attempt", g.Attempt), ("generation", g.Generation), ("port", g.MissionPort), ("participants", g.Participants.Length),
                    ("recovered", g.Recovered));
                return store.Save(record with
                {
                    Phase = ClaimPhase.Claimed, Attempt = g.Attempt, MissionPort = g.MissionPort, Generation = g.Generation, LaunchDeadline = g.LaunchDeadline,
                    Participants = g.Participants, ProcessCredential = g.ProcessCredential,
                });
            case ClaimAnswer.NoWork or ClaimAnswer.HostUnavailable:
                auth.OnAuthenticated();
                if (record.Attempt is null) { store.Delete(record.Key); return null; }
                return record;
            case ClaimAnswer.InvalidState:
                auth.OnAuthenticated();
                log.Warn("claim_key_invalid_state", ("key", record.Key));
                return Abandon(record, "invalid_state");
            case ClaimAnswer.Malformed m:
                log.Alert("claim_answer_malformed", ("reason", m.Reason)); // never launched; the backend's launch deadline cancels it
                return Abandon(record, "malformed_answer:" + m.Reason);
            case ClaimAnswer.Refused r:
                log.Alert("claim_refused", ("status", r.Status), ("code", r.Code));
                return Abandon(record, $"refused:{r.Status}");
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
        if (clock.GetUtcNow().UtcDateTime > record.LaunchDeadline) return Abandon(record, "launch_deadline_passed");
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
                return Abandon(record, "launching_refused");
            case LaunchingAnswer.Refused r:
                log.Alert("launching_refused", ("attempt", record.Attempt), ("status", r.Status), ("code", r.Code));
                return Abandon(record, $"launching_refused:{r.Status}");
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
                return Abandon(record, "invalid_state_after_supersede");
            case ClaimAnswer.Granted:
                log.Alert("claim_recovery_other_attempt", ("attempt", record.Attempt));
                return Abandon(record, "recovery_other_attempt");
            case ClaimAnswer.Failed { Kind: Transport.Unauthorized }:
                auth.OnUnauthorized();
                return record;
            default:
                return record; // retried later: launching with the old generation answers Superseded again
        }
    }

    ClaimRecord WriteCredential(ClaimRecord record)
    {
        handoff.Write(Guid.Parse(record.Attempt!), record.ProcessCredential!);
        return store.Save(record with { Phase = ClaimPhase.CredentialReady });
    }

    /// <summary>True when the attempt's unit evidently ran: the outbox directory its root ExecStartPre creates, its exit record,
    /// or systemd still holding an invocation.</summary>
    async Task<bool> EverStartedAsync(Guid attempt, CancellationToken ct)
    {
        if (Directory.Exists(Path.Combine(outboxDir, AttemptIds.Text(attempt))) || File.Exists(Path.Combine(exitDir, AttemptIds.Text(attempt) + ".exit"))) return true;
        return await systemd.ShowAsync(attempt, ct) is { } s && (s.Running || s.InvocationId.Length > 0);
    }

    async Task<ClaimRecord?> StartAsync(ClaimRecord record, CancellationToken ct)
    {
        var attempt = Guid.Parse(record.Attempt!);
        using (await gate.EnterAsync(ct))
        {
            if (await EverStartedAsync(attempt, ct))
            {
                log.Info("unit_already_started", ("attempt", record.Attempt));
                return Started(record, attempt, ClaimPhase.Started);
            }
            if (clock.GetUtcNow().UtcDateTime > record.LaunchDeadline) return Abandon(record, "launch_deadline_passed");
            var ok = await systemd.StartAsync(attempt, ct);
            var state = await systemd.ShowAsync(attempt, ct);
            var started = ok || state is { Running: true } || state is { InvocationId.Length: > 0 };
            if (started) log.Info("unit_started", ("attempt", record.Attempt), ("unit", AttemptIds.Unit(attempt)));
            else log.Alert("unit_start_failed", ("attempt", record.Attempt), ("unit", AttemptIds.Unit(attempt)));
            return Started(record, attempt, started ? ClaimPhase.Started : ClaimPhase.StartFailed);
        }
    }

    ClaimRecord Started(ClaimRecord record, Guid attempt, ClaimPhase phase)
    {
        var saved = store.Save(record with { Phase = phase, ProcessCredential = null });
        handoff.Delete(attempt);
        return saved;
    }

    ClaimRecord Abandon(ClaimRecord record, string note)
    {
        var saved = store.Save(record with { Phase = ClaimPhase.Abandoned, ProcessCredential = null, Note = note });
        if (record.Attempt is { } a && AttemptIds.TryParse(a, out var attempt)) handoff.Delete(attempt);
        log.Info("claim_abandoned", ("key", record.Key), ("attempt", record.Attempt), ("note", note));
        return saved;
    }
}
