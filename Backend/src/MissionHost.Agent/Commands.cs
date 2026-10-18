using System.Globalization;
using System.Text.Json;

namespace Blackglass.MissionHost.Agent;

/// <summary>
/// The root-only emergency stop (A10-D5): stops every active Blackglass mission unit through systemd, needing no backend and no host
/// secret. It requires a reason and either --dry-run (lists what it would stop) or --yes; it touches only blackglass-mission-NN@
/// units, never the outbox, the relay or claim state, inventory, settlement, energy or insurance; running it twice stops nothing
/// more. Each run is appended to an audit log (and printed, so journald keeps it too).
/// </summary>
public static class EmergencyStop
{
    public const int NotRoot = 77, Usage = 64, PartialFailure = 1;
    static readonly string[] Stoppable = ["active", "activating", "deactivating", "reloading", "refreshing"];

    public static async Task<int> RunAsync(ISystemd systemd, bool isRoot, bool dryRun, bool confirmed, string? reason, TextWriter output, string? auditLog,
        TimeProvider clock, CancellationToken ct)
    {
        if (!isRoot) { output.WriteLine("emergency-stop must run as root (sudo)."); return NotRoot; }
        if (string.IsNullOrWhiteSpace(reason) || (!dryRun && !confirmed))
        {
            output.WriteLine("usage: blackglass-agent emergency-stop --reason <text> (--dry-run | --yes) [--config <path>]");
            return Usage;
        }
        var inventory = await systemd.ListAsync(ct);
        var targets = inventory.Units.Where(u => Stoppable.Contains(u.ActiveState)).Select(u => new MissionUnit(u.Slot, Guid.Parse(u.AttemptId))).ToList();
        if (!inventory.Complete) output.WriteLine("warning: the unit list was incomplete; only the units listed are handled.");
        var safeReason = new string(reason.Where(c => !char.IsControl(c)).Take(200).ToArray());
        void Audit(string line)
        {
            var stamped = $"{clock.GetUtcNow().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)} {line}";
            output.WriteLine(stamped);
            if (auditLog is not null) File.AppendAllText(auditLog, stamped + "\n");
        }
        Audit($"emergency-stop {(dryRun ? "dry-run" : "run")} units={targets.Count} reason=\"{safeReason}\"");
        if (dryRun)
        {
            foreach (var t in targets) output.WriteLine($"would stop {t.Name}");
            return 0;
        }
        var failed = 0;
        foreach (var t in targets)
        {
            var ok = await systemd.StopAsync(t, ct);
            if (!ok) failed++;
            Audit($"stop {t.Name} {(ok ? "ok" : "failed")}");
        }
        return failed == 0 ? 0 : PartialFailure;
    }
}

/// <summary>`blackglass-agent status`: claims, phases and delivery counts from the state directory. No secret, no credential, no
/// result content.</summary>
public static class StatusCommand
{
    public static int Run(AgentConfig config, TextWriter output)
    {
        var store = new StateStore(config.StateDir, config.RelayDir, new AgentLog(TextWriter.Null));
        var records = store.All();
        output.WriteLine($"host={config.HostId} claims={records.Count}");
        foreach (var group in records.GroupBy(r => r.Phase).OrderBy(g => g.Key)) output.WriteLine($"phase {group.Key}: {group.Count()}");
        foreach (var r in records.Where(r => r.Phase is not (ClaimPhase.Reported or ClaimPhase.Abandoned) || r.ReportedIncomplete))
        {
            var relayed = r.Participants.Sum(a => store.RelayEntries(r.Attempt ?? "", a).Count);
            output.WriteLine($"attempt={r.Attempt ?? "-"} phase={r.Phase} generation={r.Generation} participants={r.Participants.Length} relayed={relayed}"
                + $" exit={(r.Exit is null ? "-" : r.Exit.Signal ?? r.Exit.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown")}");
        }
        var status = Path.Combine(config.StateDir, "status.json");
        if (File.Exists(status))
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(status));
            foreach (var p in doc.RootElement.EnumerateObject()) output.WriteLine($"{p.Name}={p.Value}");
        }
        return 0;
    }
}
