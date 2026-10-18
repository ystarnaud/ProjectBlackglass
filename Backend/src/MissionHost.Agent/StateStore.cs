using System.Text.Json;
using System.Text.Json.Serialization;

namespace Blackglass.MissionHost.Agent;

/// <summary>Where one claim stands. Every transition is written durably before the step that depends on it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ClaimPhase>))]
public enum ClaimPhase
{
    /// <summary>The idempotency key exists on disk; no claim answer was received yet (a retry reuses the key).</summary>
    Keyed,
    /// <summary>The complete claim answer (attempt, port, credential, generation, participants) is on disk.</summary>
    Claimed,
    /// <summary>The backend acknowledged launching with this generation: the unit may be started.</summary>
    Acknowledged,
    /// <summary>The process credential file exists for LoadCredential; start is next.</summary>
    CredentialReady,
    /// <summary>The unit was started (or found started); the credential is gone. Supervised until it exits.</summary>
    Started,
    /// <summary>systemctl start failed: never started again; reported like an exit (the backend cancels before start).</summary>
    StartFailed,
    /// <summary>The exit report was applied (with a complete scan, or the supplementary one followed). Done.</summary>
    Reported,
    /// <summary>The claim ended without a launch (no candidate, cancelled, refused): nothing to supervise.</summary>
    Abandoned,
}

/// <summary>The exit status the agent observed for a unit: an exit code or a signal name, or neither when unknown.</summary>
public sealed record ExitStatus(int? ExitCode, string? Signal)
{
    public static readonly ExitStatus Unknown = new(null, null);
    public bool Known => ExitCode is not null || Signal is not null;
}

/// <summary>One claim's durable record (StateDir/claims/&lt;key&gt;.json, 0600). It holds the process credential only until
/// the unit has it (Started or StartFailed scrubs it).</summary>
public sealed record ClaimRecord
{
    public required Guid Key { get; init; }
    public required ClaimPhase Phase { get; init; }
    public string? Attempt { get; init; }
    public int MissionPort { get; init; }
    public int Generation { get; init; }
    public DateTime? LaunchDeadline { get; init; }
    public string[] Participants { get; init; } = [];
    public string? ProcessCredential { get; init; }
    public DateTime CreatedAt { get; init; }
    public ExitStatus? Exit { get; init; }
    /// <summary>The applied exit report declared an incomplete scan: a later complete rescan is sent as the supplementary observation.</summary>
    public bool ReportedIncomplete { get; init; }
    public string? Note { get; init; }
}

/// <summary>A participant's delivery state (RelayDir/&lt;attempt&gt;/&lt;account&gt;.json): one entry per distinct file content relayed.</summary>
public sealed record RelayEntry(string Sha256, string Kind, string Outcome, DateTime At);

/// <summary>
/// The agent's durable state, all in its own 0700 directories and written through <see cref="AtomicFile"/>: claims (StateDir/claims)
/// and delivery records (RelayDir). Nothing is ever written in the outbox.
/// </summary>
public sealed class StateStore
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    readonly object gate = new();
    readonly string claims, relay;
    readonly Dictionary<Guid, ClaimRecord> records = new();

    public StateStore(string stateDir, string relayDir, AgentLog log)
    {
        claims = Path.Combine(stateDir, "claims");
        relay = relayDir;
        AtomicFile.EnsurePrivateDirectory(stateDir);
        AtomicFile.EnsurePrivateDirectory(claims);
        AtomicFile.EnsurePrivateDirectory(relay);
        AtomicFile.RemoveTemporaries(claims);
        foreach (var file in Directory.EnumerateFiles(claims, "*.json"))
        {
            try
            {
                var record = JsonSerializer.Deserialize<ClaimRecord>(File.ReadAllBytes(file), Json);
                if (record is null || Path.GetFileNameWithoutExtension(file) != record.Key.ToString("D")) throw new JsonException("mismatched record");
                records[record.Key] = record;
            }
            catch (JsonException)
            {
                log.Alert("claim_record_unreadable", ("file", Path.GetFileName(file))); // kept for the operator, never deleted
            }
        }
    }

    public IReadOnlyList<ClaimRecord> All() { lock (gate) return records.Values.OrderBy(r => r.CreatedAt).ThenBy(r => r.Key).ToList(); }

    public ClaimRecord? ForAttempt(string attempt) { lock (gate) return records.Values.FirstOrDefault(r => r.Attempt == attempt); }

    public ClaimRecord? Get(Guid key) { lock (gate) return records.TryGetValue(key, out var r) ? r : null; }

    /// <summary>Writes the record durably, then makes it current. A failed write leaves the previous record (and throws).</summary>
    public ClaimRecord Save(ClaimRecord record)
    {
        lock (gate)
        {
            AtomicFile.Write(Path.Combine(claims, record.Key.ToString("D") + ".json"), JsonSerializer.SerializeToUtf8Bytes(record, Json));
            records[record.Key] = record;
            return record;
        }
    }

    /// <summary>Removes a claim record that never named an attempt (a key answered 204): nothing to supervise or keep.</summary>
    public void Delete(Guid key)
    {
        lock (gate)
        {
            if (records.TryGetValue(key, out var r) && r.Attempt is not null) throw new InvalidOperationException("A claim that names an attempt is kept.");
            AtomicFile.Delete(Path.Combine(claims, key.ToString("D") + ".json"));
            records.Remove(key);
        }
    }

    /// <summary>The definitive outcome already recorded for this exact content, or null.</summary>
    public string? RelayOutcome(string attempt, string account, string sha256)
    {
        lock (gate) return ReadRelay(attempt, account).FirstOrDefault(e => e.Sha256 == sha256)?.Outcome;
    }

    public IReadOnlyList<RelayEntry> RelayEntries(string attempt, string account) { lock (gate) return ReadRelay(attempt, account); }

    public void RecordRelay(string attempt, string account, RelayEntry entry)
    {
        lock (gate)
        {
            var directory = Path.Combine(relay, attempt);
            AtomicFile.EnsurePrivateDirectory(directory);
            var entries = ReadRelay(attempt, account).Where(e => e.Sha256 != entry.Sha256).Append(entry).ToList();
            AtomicFile.Write(Path.Combine(directory, account + ".json"), JsonSerializer.SerializeToUtf8Bytes(entries, Json));
        }
    }

    List<RelayEntry> ReadRelay(string attempt, string account)
    {
        var path = Path.Combine(relay, attempt, account + ".json");
        if (!File.Exists(path)) return [];
        return JsonSerializer.Deserialize<List<RelayEntry>>(File.ReadAllBytes(path), Json) ?? [];
    }

    /// <summary>A small status file (StateDir/status.json) for `blackglass-agent status`: no secrets.</summary>
    public void WriteStatus(object status, string stateDir) =>
        AtomicFile.Write(Path.Combine(stateDir, "status.json"), JsonSerializer.SerializeToUtf8Bytes(status, Json));
}
