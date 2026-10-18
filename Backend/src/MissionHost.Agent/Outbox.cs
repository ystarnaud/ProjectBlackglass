using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Blackglass.Accounts.Domain.Settlement;

namespace Blackglass.MissionHost.Agent;

/// <summary>One participant's result file found in an attempt's outbox, read and validated (A10-D1).</summary>
/// <param name="Kind">"ready" (published by the process) or "written" (complete and durable; relayed only after a verified exit).</param>
/// <param name="Relayable">The file names this attempt and participant (as a parsed manifest, or as a malformed body that still
/// claims them, which the backend stores and quarantines): it is sent, and its participant counts as present.</param>
public sealed record OutboxFile(string Account, string Kind, byte[] Bytes, string Sha256, bool Relayable);

/// <summary>An outbox scan. <see cref="Complete"/> is true only when the attempt's directory was enumerated without error and
/// every candidate file was read and validated: a scan that isn't complete is never evidence of absence.</summary>
public sealed record OutboxScan(bool Complete, IReadOnlyList<OutboxFile> Files, IReadOnlyList<string> Anomalies)
{
    public bool Present(string account) => Files.Any(f => f.Account == account && f.Relayable);
}

/// <summary>
/// Reads an attempt's outbox (OutboxDir/&lt;attempt&gt;, written only by its mission unit; the agent has group read access only)
/// under the publish contract (Stage 9 §H as amended by A10-D1):
/// - "&lt;account&gt;.manifest.tmp": incomplete, never read, never a result;
/// - "&lt;account&gt;.manifest.ready": published, read and relayed at once, even while the process runs;
/// - "&lt;account&gt;.manifest.written": complete but unpublished; read only once the unit's exit is verified (<c>includeWritten</c>).
/// Each candidate is opened without following links (<see cref="Unix.ReadUntrusted"/>) and validated before it counts: its name
/// (a canonical Base58 participant of the claim), then its content (a format-v1 manifest naming this attempt and that account, or
/// a malformed body that still claims them). Nothing in the outbox is ever written, renamed or deleted by the agent.
/// </summary>
public sealed partial class OutboxScanner(string outboxDir, long maxBytes, Func<string, long, Unix.ReadResult>? reader = null)
{
    readonly Func<string, long, Unix.ReadResult> read = reader ?? Unix.ReadUntrusted;

    [GeneratedRegex(@"^([1-9A-HJ-NP-Za-km-z]{1,11})\.manifest\.(tmp|written|ready)\z", RegexOptions.CultureInvariant)]
    private static partial Regex FileName();

    public string DirectoryOf(Guid attempt) => Path.Combine(outboxDir, AttemptIds.Text(attempt));

    public OutboxScan Scan(Guid attempt, IReadOnlyCollection<string> roster, bool includeWritten)
    {
        var anomalies = new List<string>();
        var files = new List<OutboxFile>();
        var directory = DirectoryOf(attempt);
        List<string> names;
        try
        {
            // A missing directory is never "no results": the unit's root ExecStartPre creates it, so its absence is unexplained.
            if (!Directory.Exists(directory)) return new OutboxScan(false, [], ["outbox_missing"]);
            names = Directory.EnumerateFileSystemEntries(directory).Select(p => Path.GetFileName(p)!).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new OutboxScan(false, [], ["outbox_unreadable"]);
        }
        var complete = true;
        foreach (var name in names.Order(StringComparer.Ordinal))
        {
            var match = FileName().Match(name);
            if (!match.Success || !AccountIds.TryParse(match.Groups[1].Value, out _))
            {
                anomalies.Add("unexpected_entry");
                continue;
            }
            var account = match.Groups[1].Value;
            var kind = match.Groups[2].Value;
            if (!roster.Contains(account))
            {
                anomalies.Add("not_a_participant");
                continue;
            }
            if (kind == "tmp" || (kind == "written" && !includeWritten)) continue;
            switch (read(Path.Combine(directory, name), maxBytes))
            {
                case Unix.ReadResult.Read { Bytes: var bytes }:
                    var relayable = Identifies(bytes, attempt, account);
                    if (!relayable)
                    {
                        anomalies.Add("result_identity_invalid");
                        complete = false; // something was written for this participant that isn't a usable result: never absence
                    }
                    files.Add(new OutboxFile(account, kind, bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)), relayable));
                    break;
                case Unix.ReadResult.Refused r:
                    anomalies.Add("result_refused_" + r.Reason);
                    complete = false;
                    break;
                case Unix.ReadResult.Failed:
                    anomalies.Add("result_unreadable");
                    complete = false;
                    break;
            }
        }
        return new OutboxScan(complete, files, anomalies);
    }

    /// <summary>The content names this attempt and account: a parsed format-v1 manifest, or a malformed body whose claimed ids
    /// (the backend stores it and quarantines the participant, Stage 9 §C) are these.</summary>
    public static bool Identifies(byte[] bytes, Guid attempt, string account)
    {
        if (!AccountIds.TryParse(account, out var accountId)) return false;
        return ResultManifest.Parse(bytes) switch
        {
            ParseResult.Parsed p => p.Manifest.AttemptId == attempt && p.Manifest.AccountId == accountId,
            ParseResult.Malformed m => m.ClaimedAttemptId == attempt && m.ClaimedAccountId == accountId,
            _ => false,
        };
    }
}

/// <summary>Exponential backoff with jitter between <c>min</c> and a cap that doubles per failure up to <c>max</c> (spec §9.2:
/// 1 s to 60 s, provisional).</summary>
public sealed class Backoff(TimeSpan min, TimeSpan max)
{
    public TimeSpan Next(int failures)
    {
        var cap = Math.Min(max.TotalMilliseconds, min.TotalMilliseconds * Math.Pow(2, Math.Min(failures, 30)));
        return TimeSpan.FromMilliseconds(min.TotalMilliseconds + Random.Shared.NextDouble() * Math.Max(0, cap - min.TotalMilliseconds));
    }
}

/// <summary>
/// Relays an attempt's relayable files (spec §9.2, Stage 9 §C): each distinct content once to a definitive answer, recorded in the
/// agent's own relay directory and never resent; 401 and every failure without a definitive answer are retried with backoff,
/// indefinitely. A file is never deleted or changed.
/// </summary>
public sealed class Relayer(IBackend backend, StateStore store, AuthGate auth, Backoff backoff, TimeProvider clock, AgentLog log)
{
    sealed record Pending(int Failures, DateTimeOffset NextTry);
    readonly Dictionary<string, Pending> pending = new(StringComparer.Ordinal);

    /// <summary>Relays what is due. Returns true when every relayable file in <paramref name="scan"/> has a definitive outcome.</summary>
    public async Task<bool> RelayAsync(Guid attempt, OutboxScan scan, CancellationToken ct)
    {
        var attemptText = AttemptIds.Text(attempt);
        var allDefinitive = true;
        foreach (var file in scan.Files.Where(f => f.Relayable))
        {
            if (store.RelayOutcome(attemptText, file.Account, file.Sha256) is not null) continue;
            var key = attemptText + "/" + file.Sha256;
            if (pending.TryGetValue(key, out var p) && clock.GetUtcNow() < p.NextTry) { allDefinitive = false; continue; }
            if (!auth.MaySend) { allDefinitive = false; continue; }
            var answer = await backend.RelayAsync(file.Bytes, ct);
            switch (answer)
            {
                case RelayAnswer.Definitive d:
                    auth.OnAuthenticated();
                    store.RecordRelay(attemptText, file.Account, new RelayEntry(file.Sha256, file.Kind, d.Outcome, clock.GetUtcNow().UtcDateTime));
                    pending.Remove(key);
                    (string, object?)[] fields = [("attempt", attemptText), ("account", file.Account), ("kind", file.Kind), ("outcome", d.Outcome),
                        ("status", d.Status), ("code", d.Code), ("sha256", file.Sha256[..8])];
                    if (d.Outcome is RelayOutcomes.Done or RelayOutcomes.UnderReview) log.Info("result_relayed", fields);
                    else log.Alert("result_relayed", fields); // kept for the operator (conflict, revoked, refused, limited, rejected)
                    break;
                case RelayAnswer.Failed f:
                    if (f.Kind == Transport.Unauthorized) auth.OnUnauthorized();
                    var failures = (pending.TryGetValue(key, out var prior) ? prior.Failures : 0) + 1;
                    pending[key] = new Pending(failures, clock.GetUtcNow() + backoff.Next(failures));
                    allDefinitive = false;
                    break;
            }
        }
        return allDefinitive;
    }

    /// <summary>Relayable files across the given scans without a definitive outcome (the heartbeat's outboxPending).</summary>
    public int CountPending(IEnumerable<(string Attempt, OutboxScan Scan)> scans) =>
        scans.Sum(s => s.Scan.Files.Count(f => f.Relayable && store.RelayOutcome(s.Attempt, f.Account, f.Sha256) is null));
}

/// <summary>
/// The host secret's standing (A10-D8). A 401 reloads the secret file (at most once per 10 s). A changed, valid secret is tried
/// at once; an unchanged or invalid one blocks new claims and launches, raises one operational alert, and lets requests through
/// only every <c>retryInterval</c> (no rapid loop) while local supervision and file preservation go on. A disable revokes the
/// old secret for good, so reloading it never unblocks anything: only an operator's rotation, re-enable and SIGHUP (or restart) do.
/// </summary>
public sealed class AuthGate(HostSecret secret, TimeProvider clock, TimeSpan retryInterval, AgentLog log)
{
    readonly object gate = new();
    DateTimeOffset nextTry = DateTimeOffset.MinValue;

    public bool Blocked { get; private set; }

    /// <summary>False while blocked, except once per retry interval.</summary>
    public bool MaySend
    {
        get
        {
            lock (gate)
            {
                if (!Blocked) return true;
                if (clock.GetUtcNow() < nextTry) return false;
                nextTry = clock.GetUtcNow() + retryInterval;
                return true;
            }
        }
    }

    public void OnUnauthorized()
    {
        var reload = secret.ReloadThrottled(TimeSpan.FromSeconds(10));
        lock (gate)
        {
            if (reload == HostSecret.ReloadResult.Changed)
            {
                log.Info("host_secret_reloaded", ("cause", "401"));
                return;
            }
            nextTry = clock.GetUtcNow() + retryInterval;
            if (Blocked) return;
            Blocked = true;
        }
        log.Alert("host_secret_rejected", ("effect", "claims_and_launches_stopped"), ("retry_seconds", (int)retryInterval.TotalSeconds));
    }

    public void OnAuthenticated()
    {
        lock (gate)
        {
            if (!Blocked) return;
            Blocked = false;
        }
        log.Info("host_secret_accepted");
    }

    /// <summary>SIGHUP: re-read the file; a changed secret is tried at once.</summary>
    public void OnReloadSignal()
    {
        var reload = secret.Reload();
        log.Info("host_secret_reload_signal", ("result", reload.ToString()));
        if (reload == HostSecret.ReloadResult.Changed)
            lock (gate) nextTry = DateTimeOffset.MinValue;
    }
}
