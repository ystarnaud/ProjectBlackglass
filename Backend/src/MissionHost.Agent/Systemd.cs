using System.Diagnostics;
using System.Globalization;

namespace Blackglass.MissionHost.Agent;

public sealed record CommandResult(int ExitCode, string Stdout, bool TimedOut);

/// <summary>Runs one executable with a structured argument list: never a shell, never a string-built command line.</summary>
public interface IProcessRunner
{
    Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = false, CreateNoWindow = true,
        };
        foreach (var a in arguments) start.ArgumentList.Add(a);
        start.Environment.Clear(); // nothing inherited: a fixed, minimal environment
        start.Environment["LANG"] = "C.UTF-8";
        start.Environment["LC_ALL"] = "C.UTF-8";
        start.Environment["SYSTEMD_COLORS"] = "0";
        start.Environment["SYSTEMD_PAGER"] = "";
        start.Environment["SYSTEMD_URLIFY"] = "0";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The process did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct); // drained so the child never blocks; not logged
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return new CommandResult(-1, "", TimedOut: true);
        }
        await stderr;
        return new CommandResult(process.ExitCode, await stdout, TimedOut: false);
    }
}

/// <summary>A unit as systemctl show reports it.</summary>
public sealed record UnitState(string LoadState, string ActiveState, string SubState, string Result, int ExecMainCode, int ExecMainStatus, string InvocationId)
{
    public bool Terminal => ActiveState is "inactive" or "failed";
    public bool Running => ActiveState is "active" or "activating" or "deactivating" or "reloading" or "refreshing";

    /// <summary>The exit status systemd still holds (CLD_EXITED 1, CLD_KILLED 2, CLD_DUMPED 3), or unknown.</summary>
    public ExitStatus Exit => ExecMainCode switch
    {
        1 => new ExitStatus(ExecMainStatus is >= 0 and <= 255 ? ExecMainStatus : null, null),
        2 or 3 => new ExitStatus(null, Signals.Name(ExecMainStatus)),
        _ => ExitStatus.Unknown,
    };
}

/// <summary>A unit inventory: <see cref="Complete"/> only for a zero exit whose every line parsed (Stage 8 R2).</summary>
public sealed record UnitInventory(bool Complete, IReadOnlyList<UnitEntry> Units);

/// <summary>The only systemd operations the agent has (A10-D4): start, stop, list and show, of blackglass-mission@ units only.</summary>
public interface ISystemd
{
    Task<bool> StartAsync(Guid attempt, CancellationToken ct);
    Task<bool> StopAsync(Guid attempt, CancellationToken ct);
    Task<UnitInventory> ListAsync(CancellationToken ct);
    Task<UnitState?> ShowAsync(Guid attempt, CancellationToken ct);
}

/// <summary>
/// systemctl at a fixed path with fixed argument lists (A10-D4). Unit names come only from <see cref="AttemptIds.Unit"/>, after
/// "--", so no text from anywhere else reaches the command. The polkit rule (deploy/mission) allows the agent's user exactly
/// start and stop of these units; list and show need no privilege.
/// </summary>
public sealed class Systemctl(string executable, IProcessRunner runner) : ISystemd
{
    static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(30), QueryTimeout = TimeSpan.FromSeconds(10);
    public const string ListPattern = "blackglass-mission@*.service";
    public const string ShowProperties = "LoadState,ActiveState,SubState,Result,ExecMainCode,ExecMainStatus,InvocationID";
    static readonly string[] LoadStates = ["loaded", "not-found", "bad-setting", "error", "masked", "stub", "merged"];
    static readonly string[] ActiveStates = ["active", "reloading", "inactive", "failed", "activating", "deactivating", "maintenance", "refreshing"];

    public static IReadOnlyList<string> StartArguments(Guid attempt) => ["--no-ask-password", "start", "--", AttemptIds.Unit(attempt)];
    public static IReadOnlyList<string> StopArguments(Guid attempt) => ["--no-ask-password", "stop", "--", AttemptIds.Unit(attempt)];
    public static IReadOnlyList<string> ListArguments() =>
        ["--no-ask-password", "--no-pager", "--no-legend", "--plain", "--full", "--all", "list-units", "--", ListPattern];
    public static IReadOnlyList<string> ShowArguments(Guid attempt) =>
        ["--no-ask-password", "--no-pager", "show", "--property=" + ShowProperties, "--", AttemptIds.Unit(attempt)];

    public async Task<bool> StartAsync(Guid attempt, CancellationToken ct) => (await runner.RunAsync(executable, StartArguments(attempt), ControlTimeout, ct)) is { ExitCode: 0, TimedOut: false };

    public async Task<bool> StopAsync(Guid attempt, CancellationToken ct) => (await runner.RunAsync(executable, StopArguments(attempt), ControlTimeout, ct)) is { ExitCode: 0, TimedOut: false };

    public async Task<UnitInventory> ListAsync(CancellationToken ct)
    {
        var result = await runner.RunAsync(executable, ListArguments(), QueryTimeout, ct);
        return result is { ExitCode: 0, TimedOut: false } ? ParseList(result.Stdout) : new UnitInventory(false, []);
    }

    /// <summary>Strict: every non-empty line is "UNIT LOAD ACTIVE SUB DESCRIPTION…" for a mission unit (an optional leading ●
    /// marker aside). Any other line makes the inventory incomplete; duplicate units too.</summary>
    public static UnitInventory ParseList(string stdout)
    {
        var units = new List<UnitEntry>();
        var complete = true;
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0) continue;
            var tokens = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).ToList();
            if (tokens.Count > 0 && tokens[0] is "●" or "*" or "x") tokens.RemoveAt(0);
            if (tokens.Count < 4 || !AttemptIds.TryParseUnit(tokens[0], out var attempt) || !LoadStates.Contains(tokens[1]) || !ActiveStates.Contains(tokens[2]))
            {
                complete = false;
                continue;
            }
            units.Add(new UnitEntry(AttemptIds.Text(attempt), tokens[2]));
        }
        if (units.Select(u => u.AttemptId).Distinct(StringComparer.Ordinal).Count() != units.Count) complete = false;
        return new UnitInventory(complete, units);
    }

    public async Task<UnitState?> ShowAsync(Guid attempt, CancellationToken ct)
    {
        var result = await runner.RunAsync(executable, ShowArguments(attempt), QueryTimeout, ct);
        return result is { ExitCode: 0, TimedOut: false } ? ParseShow(result.Stdout) : null;
    }

    public static UnitState? ParseShow(string stdout)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0) values[line[..eq]] = line[(eq + 1)..];
        }
        string Get(string k) => values.TryGetValue(k, out var v) ? v : "";
        int Int(string k) => int.TryParse(Get(k), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
        if (!LoadStates.Contains(Get("LoadState")) || !ActiveStates.Contains(Get("ActiveState"))) return null;
        return new UnitState(Get("LoadState"), Get("ActiveState"), Get("SubState"), Get("Result"), Int("ExecMainCode"), Int("ExecMainStatus"), Get("InvocationID"));
    }
}

/// <summary>Linux signal numbers (x86_64) to the names the exit report carries.</summary>
public static class Signals
{
    static readonly string[] Names =
    [
        "", "SIGHUP", "SIGINT", "SIGQUIT", "SIGILL", "SIGTRAP", "SIGABRT", "SIGBUS", "SIGFPE", "SIGKILL", "SIGUSR1", "SIGSEGV", "SIGUSR2", "SIGPIPE",
        "SIGALRM", "SIGTERM", "SIGSTKFLT", "SIGCHLD", "SIGCONT", "SIGSTOP", "SIGTSTP", "SIGTTIN", "SIGTTOU", "SIGURG", "SIGXCPU", "SIGXFSZ",
        "SIGVTALRM", "SIGPROF", "SIGWINCH", "SIGIO", "SIGPWR", "SIGSYS",
    ];

    public static string? Name(int number) => number >= 1 && number < Names.Length ? Names[number] : null;

    public static bool IsName(string? s) => s is not null && Names.Contains(s) && s.Length > 0;
}

/// <summary>
/// The exit records the template's root ExecStopPost step writes (deploy/mission/record-exit): ExitDir/&lt;attempt&gt;.exit with
/// "code=exited|killed|dumped" and "status=&lt;number or signal name&gt;", from systemd's $EXIT_CODE and $EXIT_STATUS. They
/// outlive systemd's garbage collection of the unit, which forgets a clean exit's status. Read-only to the agent.
/// </summary>
public static class ExitRecords
{
    public static ExitStatus? Read(string exitDir, Guid attempt)
    {
        var path = Path.Combine(exitDir, AttemptIds.Text(attempt) + ".exit");
        if (!OperatingSystem.IsLinux() || !File.Exists(path)) return null;
        if (Unix.ReadUntrusted(path, 4096) is not Unix.ReadResult.Read { Bytes: var bytes }) return null;
        return Parse(System.Text.Encoding.UTF8.GetString(bytes));
    }

    public static ExitStatus? Parse(string text)
    {
        string? code = null, status = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("code=", StringComparison.Ordinal)) code = line[5..];
            else if (line.StartsWith("status=", StringComparison.Ordinal)) status = line[7..];
        }
        if (code == "exited" && int.TryParse(status, NumberStyles.None, CultureInfo.InvariantCulture, out var exit) && exit is >= 0 and <= 255)
            return new ExitStatus(exit, null);
        if (code is "killed" or "dumped")
        {
            if (Signals.IsName(status)) return new ExitStatus(null, status);
            if (Signals.IsName("SIG" + status)) return new ExitStatus(null, "SIG" + status); // systemd may give "TERM"
            if (int.TryParse(status, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && Signals.Name(n) is { } name) return new ExitStatus(null, name);
        }
        return null;
    }
}
