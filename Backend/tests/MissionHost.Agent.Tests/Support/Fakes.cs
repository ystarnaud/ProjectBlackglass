using System.Text;
using Blackglass.Accounts.Domain.Auth;
using Blackglass.MissionHost.CredentialBroker;

namespace Blackglass.MissionHost.Agent.Tests.Support;

/// <summary>A clock the test moves by hand.</summary>
public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    DateTimeOffset now = start;
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan by) => now += by;
}

/// <summary>A crash: the agent instance that threw is discarded, and a new one resumes from the same directories.</summary>
public sealed class SimulatedCrash(string point) : Exception($"simulated crash at {point}");

/// <summary>
/// A backend that keeps Stage 8's claim and launching rules (a key retried while assigned recovers with the next generation;
/// launching accepts only the current generation; a repeat once launching is acknowledged again) and records every call.
/// <see cref="CrashAt"/> makes one named point throw: "claim:before", "claim:after" (processed, answer lost), "launching:before",
/// "launching:after", and so on.
/// </summary>
public sealed class FakeBackend : IBackend
{
    public sealed class Attempt
    {
        public required string Id { get; init; }
        public string[] Participants { get; init; } = ["JG"];
        public Guid? Key { get; set; }
        public string State { get; set; } = "admitted";
        public int Generation { get; set; }
        public string? Credential { get; set; }
        public DateTime Deadline { get; set; } = DateTime.UtcNow.AddHours(1);
        /// <summary>The mission port the backend assigned: slot port - 7000 with the test host's port base.</summary>
        public int Port { get; set; } = 7000;
    }

    public readonly List<Attempt> Attempts = [];
    public readonly List<string> Calls = [];
    public readonly List<byte[]> Relayed = [];
    public readonly List<(string Attempt, ExitedBody Body)> Exits = [];
    public readonly List<HeartbeatBody> Heartbeats = [];
    public string? CrashAt { get; set; }
    public Transport? FailAll { get; set; }
    public Func<byte[], RelayAnswer> OnRelay { get; set; } = _ => new RelayAnswer.Definitive(RelayOutcomes.Done, 200, null);
    public Func<string, ExitedBody, ExitedAnswer> OnExited { get; set; } = (_, _) => new ExitedAnswer.Applied("none");
    public HeartbeatAnswer HeartbeatReply { get; set; } = new HeartbeatAnswer.Ok(false, []);
    /// <summary>Called as a claim is answered, with the agent's state as it stands then (for "persisted before" checks).</summary>
    public Action<Guid>? BeforeClaim { get; set; }
    public Action<string, int>? BeforeLaunching { get; set; }

    void Crash(string point)
    {
        if (CrashAt == point) { CrashAt = null; throw new SimulatedCrash(point); }
    }

    public Attempt Admit(string? id = null, params string[] participants)
    {
        // Distinct ports, as the backend allocates them (the lowest free one of the host's range): slot 0, 1, 2...
        var a = new Attempt { Id = id ?? Guid.CreateVersion7().ToString("D"), Participants = participants.Length > 0 ? participants : ["JG"], Port = 7000 + Attempts.Count };
        Attempts.Add(a);
        return a;
    }

    public Task<ClaimAnswer> ClaimAsync(Guid key, CancellationToken ct)
    {
        Calls.Add($"claim:{key}");
        BeforeClaim?.Invoke(key);
        Crash("claim:before");
        if (FailAll is { } f) return Task.FromResult<ClaimAnswer>(new ClaimAnswer.Failed(f));
        ClaimAnswer answer;
        var bound = Attempts.FirstOrDefault(a => a.Key == key);
        if (bound is not null)
            answer = bound.State == "assigned" ? Grant(bound, recovered: true) : new ClaimAnswer.InvalidState();
        else if (Attempts.FirstOrDefault(a => a.State == "admitted") is { } next)
        {
            next.Key = key;
            next.State = "assigned";
            answer = Grant(next, recovered: false);
        }
        else answer = new ClaimAnswer.NoWork();
        Crash("claim:after");
        return Task.FromResult(answer);
    }

    static ClaimAnswer Grant(Attempt a, bool recovered)
    {
        a.Generation++;
        a.Credential = OpaqueSecret.New(OpaqueSecret.ProcessCredentialPrefix).Text;
        return new ClaimAnswer.Granted(new ClaimGrant(a.Id, a.Port, a.Credential, a.Generation, a.Deadline, recovered, a.Participants));
    }

    public Task<LaunchingAnswer> LaunchingAsync(string attempt, int generation, CancellationToken ct)
    {
        Calls.Add($"launching:{attempt}:{generation}");
        BeforeLaunching?.Invoke(attempt, generation);
        Crash("launching:before");
        if (FailAll is { } f) return Task.FromResult<LaunchingAnswer>(new LaunchingAnswer.Failed(f));
        var a = Attempts.Single(x => x.Id == attempt);
        LaunchingAnswer answer = a.State switch
        {
            "assigned" or "launching" when generation != a.Generation => new LaunchingAnswer.Superseded(),
            "assigned" or "launching" => new LaunchingAnswer.Acknowledged("launching"),
            _ => new LaunchingAnswer.InvalidState(),
        };
        if (answer is LaunchingAnswer.Acknowledged) a.State = "launching";
        Crash("launching:after");
        return Task.FromResult(answer);
    }

    public bool Acknowledged(string attempt, int generation) => Attempts.Single(x => x.Id == attempt) is { State: "launching" } a && a.Generation == generation;

    public Task<HeartbeatAnswer> HeartbeatAsync(HeartbeatBody body, CancellationToken ct)
    {
        Calls.Add("heartbeat");
        Heartbeats.Add(body);
        return Task.FromResult(FailAll is { } f ? new HeartbeatAnswer.Failed(f) : HeartbeatReply);
    }

    public Task<ExitedAnswer> ExitedAsync(string attempt, ExitedBody body, CancellationToken ct)
    {
        Calls.Add($"exited:{attempt}:{body.OutboxComplete}");
        if (FailAll is { } f) return Task.FromResult<ExitedAnswer>(new ExitedAnswer.Failed(f));
        Exits.Add((attempt, body));
        return Task.FromResult(OnExited(attempt, body));
    }

    public Task<RelayAnswer> RelayAsync(byte[] manifest, CancellationToken ct)
    {
        Calls.Add("relay");
        if (FailAll is { } f) return Task.FromResult<RelayAnswer>(new RelayAnswer.Failed(f));
        Relayed.Add(manifest);
        return Task.FromResult(OnRelay(manifest));
    }
}

/// <summary>
/// systemd as the template would behave: start runs the unit's root ExecStartPre (creating the attempt's outbox directory) and
/// makes it active; the test ends it with <see cref="Exit"/>; an unknown unit shows as loaded and inactive with no invocation
/// (as after garbage collection or a reboot).
/// </summary>
public sealed class FakeSystemd(string outboxDir) : ISystemd
{
    public sealed record Unit(string ActiveState, string Result, int ExecMainCode, int ExecMainStatus, string InvocationId);
    public readonly Dictionary<Guid, Unit> Units = new();
    /// <summary>The slot each known unit runs in (its unit name's NN).</summary>
    public readonly Dictionary<Guid, int> UnitSlots = new();
    public readonly List<string> Calls = [];
    /// <summary>Every unit name start was called with.</summary>
    public readonly List<string> StartedUnits = [];
    public bool FailStart { get; set; }
    public string? CrashAt { get; set; }
    public bool ListFails { get; set; }
    public Action<Guid>? OnStart { get; set; }

    public Task<bool> StartAsync(MissionUnit unit, CancellationToken ct)
    {
        var attempt = unit.Attempt;
        Calls.Add($"start:{attempt:D}");
        StartedUnits.Add(unit.Name);
        UnitSlots[attempt] = unit.Slot;
        OnStart?.Invoke(attempt);
        if (FailStart) return Task.FromResult(false);
        if (!Units.TryGetValue(attempt, out var u) || u.ActiveState is "inactive" or "failed")
        {
            Directory.CreateDirectory(Path.Combine(outboxDir, attempt.ToString("D")));
            Units[attempt] = new Unit("active", "success", 0, 0, Guid.NewGuid().ToString("N"));
        }
        if (CrashAt == "start:after") { CrashAt = null; throw new SimulatedCrash("start:after"); }
        return Task.FromResult(true);
    }

    public Task<bool> StopAsync(MissionUnit unit, CancellationToken ct)
    {
        var attempt = unit.Attempt;
        Calls.Add($"stop:{attempt:D}");
        if (Units.TryGetValue(attempt, out var u) && u.ActiveState != "inactive") Units[attempt] = u with { ActiveState = "inactive", ExecMainCode = 2, ExecMainStatus = 15 };
        return Task.FromResult(true);
    }

    public void Exit(Guid attempt, int code = 0, int? signal = null)
    {
        var u = Units[attempt];
        Units[attempt] = signal is { } s ? u with { ActiveState = "failed", Result = "signal", ExecMainCode = 2, ExecMainStatus = s }
            : u with { ActiveState = code == 0 ? "inactive" : "failed", Result = code == 0 ? "success" : "exit-code", ExecMainCode = 1, ExecMainStatus = code };
    }

    /// <summary>systemd forgot the unit (garbage collection of a clean exit, or a reboot).</summary>
    public void Forget(Guid attempt) => Units.Remove(attempt);

    public Task<UnitInventory> ListAsync(CancellationToken ct)
    {
        Calls.Add("list");
        if (ListFails) return Task.FromResult(new UnitInventory(false, []));
        return Task.FromResult(new UnitInventory(true, Units.Select(kv => new UnitEntry(kv.Key.ToString("D"), kv.Value.ActiveState, UnitSlots.TryGetValue(kv.Key, out var slot) ? slot : 0)).ToList()));
    }

    public Task<UnitState?> ShowAsync(MissionUnit unit, CancellationToken ct)
    {
        var attempt = unit.Attempt;
        Calls.Add($"show:{attempt:D}");
        var u = Units.TryGetValue(attempt, out var x) ? x : new Unit("inactive", "success", 0, 0, "");
        return Task.FromResult<UnitState?>(new UnitState("loaded", u.ActiveState, u.ActiveState == "active" ? "running" : "dead", u.Result, u.ExecMainCode, u.ExecMainStatus, u.InvocationId));
    }
}

/// <summary>A temporary agent installation: the five directories, a 0600 host secret, a build-info.txt, and the config.</summary>
public sealed class AgentDirs : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "bg-agent-" + Guid.NewGuid().ToString("N"));
    public AgentConfig Config { get; }
    public string SecretText { get; private set; } = "";

    public AgentDirs()
    {
        Directory.CreateDirectory(Root);
        foreach (var d in new[] { "outbox", "exits", "server" }) Directory.CreateDirectory(Path.Combine(Root, d));
        File.WriteAllText(Path.Combine(Root, "server", "build-info.txt"), "revision=r1\ncatalogueVersion=sha256:" + new string('5', 64) + "\n");
        WriteSecret(OpaqueSecret.New(OpaqueSecret.HostSecretPrefix).Text);
        Config = new AgentConfig
        {
            HostId = "h1", ApiBaseUrl = new Uri("http://127.0.0.1:5081/"), HostSecretFile = Path.Combine(Root, "host-secret"),
            StateDir = Path.Combine(Root, "state"), RelayDir = Path.Combine(Root, "relay"), OutboxDir = Path.Combine(Root, "outbox"),
            ExitDir = Path.Combine(Root, "exits"), PortBase = 7000, ServerBuildInfo = Path.Combine(Root, "server", "build-info.txt"),
        };
    }

    /// <summary>Replaces the secret file atomically (the documented operator procedure: write a new file, rename it over).</summary>
    public void WriteSecret(string text)
    {
        SecretText = text;
        var temp = Path.Combine(Root, "host-secret.new");
        File.WriteAllText(temp, text + "\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, Path.Combine(Root, "host-secret"), overwrite: true);
    }

    public string OutboxOf(string attempt) => Path.Combine(Root, "outbox", attempt);

    /// <summary>A result file as the mission process would leave it (any of tmp, written, ready).</summary>
    public string WriteResult(string attempt, string account, string kind, byte[] bytes)
    {
        Directory.CreateDirectory(OutboxOf(attempt));
        var path = Path.Combine(OutboxOf(attempt), $"{account}.manifest.{kind}");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows())
            foreach (var p in Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories))
                try { File.SetUnixFileMode(p, Directory.Exists(p) ? (UnixFileMode)0b111_000_000 : UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}

/// <summary>Results built from the golden minimal manifest, renamed to the attempt and account a test needs.</summary>
public static class Manifests
{
    static readonly string Golden = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors", "minimal.json"));
    public const string GoldenAttempt = "0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41";

    public static byte[] For(string attempt, string account, int sequence = 8812) => Encoding.UTF8.GetBytes(Golden
        .Replace(GoldenAttempt, attempt, StringComparison.Ordinal)
        .Replace("\"accountId\":\"JG\"", $"\"accountId\":\"{account}\"", StringComparison.Ordinal)
        .Replace("\"outcomeSequence\":8812", $"\"outcomeSequence\":{sequence}", StringComparison.Ordinal));
}

/// <summary>A log that keeps every line, for assertions (and the secrecy checks).</summary>
public sealed class CapturedLog
{
    readonly StringWriter writer = new();
    public AgentLog Log { get; }
    public CapturedLog(TimeProvider? clock = null) => Log = new AgentLog(writer, clock);
    public string Text => writer.ToString();
}

/// <summary>Builds an agent over the directories; a new instance after a <see cref="SimulatedCrash"/> resumes from disk.</summary>
public static class Agents
{
    public static AgentHost Create(AgentDirs dirs, FakeBackend backend, FakeSystemd systemd, CapturedLog log, TimeProvider? clock = null, StoreBroker? broker = null)
    {
        var secret = new HostSecret(dirs.Config.HostSecretFile);
        secret.Reload();
        return new AgentHost(dirs.Config with { RelayBackoffMinMs = 10, RelayBackoffMaxMs = 20 }, backend, systemd, broker ?? new StoreBroker(dirs.Root), secret, log.Log, clock);
    }

    /// <summary>Runs a step, treating a simulated crash as the end of that agent instance.</summary>
    public static async Task<bool> SurvivesAsync(Func<Task> step)
    {
        try { await step(); return true; }
        catch (SimulatedCrash) { return false; }
    }
}

/// <summary>
/// The credential broker as the agent sees it, backed by the real root broker's store (CredentialStore) in a private directory of
/// the test's own user: the same validation, generation rules and files LoadCredential would read. <see cref="Fail"/> answers a
/// wire code instead (write_failed, unsafe_store, conflict, unavailable...). The store outlives an agent instance, like /run.
/// </summary>
public sealed class StoreBroker : ICredentialBroker
{
    readonly CredentialStore store;
    public string Root { get; }
    public readonly List<string> Calls = [];
    public string? Fail { get; set; }

    public StoreBroker(string agentRoot)
    {
        Root = Path.Combine(agentRoot, "credentials");
        if (!OperatingSystem.IsWindows() && !Directory.Exists(Root)) Directory.CreateDirectory(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        store = new CredentialStore(new BrokerOptions(Root, Native.EffectiveUid), TextWriter.Null);
    }

    public Task<BrokerAnswer> PrepareAsync(Guid attempt, int generation, string credential, CancellationToken ct)
    {
        Calls.Add($"prepare:{attempt:D}:{generation}");
        if (Fail is { } f) return Task.FromResult(new BrokerAnswer(false, f));
        var code = store.Prepare(attempt.ToString("D"), generation, credential);
        return Task.FromResult(new BrokerAnswer(code == BrokerCode.Ok, Broker.Wire(code)));
    }

    public Task<BrokerAnswer> DiscardAsync(Guid attempt, CancellationToken ct)
    {
        Calls.Add($"discard:{attempt:D}");
        if (Fail is { } f) return Task.FromResult(new BrokerAnswer(false, f));
        var code = store.Discard(attempt.ToString("D"));
        return Task.FromResult(new BrokerAnswer(code == BrokerCode.Ok, Broker.Wire(code)));
    }

    /// <summary>The credential the unit's LoadCredential would load now, or null.</summary>
    public string? Credential(Guid attempt)
    {
        var path = Path.Combine(Root, attempt.ToString("D"), "process");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }
}
