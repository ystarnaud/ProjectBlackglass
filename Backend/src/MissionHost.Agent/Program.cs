using System.Runtime.InteropServices;
using Blackglass.MissionHost.Agent;

// blackglass-agent run|status|emergency-stop [--config <path>] (spec §7.4, A10-D5, A10-D6). Exit codes: 0, 1 (failure), 4 (bad
// configuration or host secret), 64 (usage), 77 (emergency-stop not run as root).
const string DefaultConfig = "/etc/blackglass/agent/agent.json";
var verb = args.Length > 0 ? args[0] : "";
string? Option(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
bool Flag(string name) => args.Contains(name);

AgentConfig config;
try { config = AgentConfig.Load(Option("--config") ?? DefaultConfig); }
catch (AgentConfigException e)
{
    Console.Error.WriteLine($"configuration refused: {e.Message}");
    return 4;
}
var systemd = new Systemctl(config.Systemctl, new ProcessRunner());

switch (verb)
{
    case "status":
        return StatusCommand.Run(config, Console.Out);
    case "emergency-stop":
        return await EmergencyStop.RunAsync(systemd, Unix.IsRoot, Flag("--dry-run"), Flag("--yes"), Option("--reason"), Console.Out,
            Unix.IsRoot ? "/var/log/blackglass/emergency-stop.log" : null, TimeProvider.System, CancellationToken.None);
    case "run":
        break;
    default:
        Console.Error.WriteLine("usage: blackglass-agent run|status|emergency-stop [--config <path>]");
        return 64;
}

var log = AgentLog.Console;
var secret = new HostSecret(config.HostSecretFile);
if (secret.Reload() != HostSecret.ReloadResult.Changed)
{
    log.Alert("host_secret_unavailable", ("file", config.HostSecretFile)); // the path only, never the content
    return 4;
}
using var http = new HttpClient { BaseAddress = config.ApiBaseUrl, Timeout = TimeSpan.FromSeconds(config.RequestTimeoutSeconds) };
using var host = new AgentHost(config, new BackendClient(http, secret), systemd, secret, log);
using var stop = new CancellationTokenSource();
using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; stop.Cancel(); });
using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, c => { c.Cancel = true; stop.Cancel(); });
using var hangup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, c => { c.Cancel = true; host.Auth.OnReloadSignal(); });
try { await host.RunAsync(stop.Token); }
catch (OperationCanceledException) { }
log.Info("agent_stopped"); // running units are left alone: systemd keeps them, and a restart resumes from the state directory
return 0;
