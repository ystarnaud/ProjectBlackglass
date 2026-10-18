using System.Text.Json;
using System.Text.RegularExpressions;

namespace Blackglass.MissionHost.Agent;

/// <summary>A configuration file the agent refuses (exit 4): the message names the setting, never a secret.</summary>
public sealed class AgentConfigException(string message) : Exception(message);

/// <summary>
/// The agent's configuration: a root-owned JSON file (/etc/blackglass/agent/agent.json), read once at startup. Every member is
/// required except the provisional values and <see cref="CatalogueVersion"/>; an unknown member is refused. The API is reached
/// only on loopback (the dedicated tunnel, A10-D3: http://127.0.0.1:5081). Every timer and size here is provisional (spec §19).
/// </summary>
public sealed partial record AgentConfig
{
    public required string HostId { get; init; }
    public required Uri ApiBaseUrl { get; init; }
    public required string HostSecretFile { get; init; }
    /// <summary>The agent's own state (claims, the last heartbeat): blackglass-agent, 0700.</summary>
    public required string StateDir { get; init; }
    /// <summary>Delivery state per attempt and participant: blackglass-agent, 0700. Never in the outbox (A10-D1).</summary>
    public required string RelayDir { get; init; }
    /// <summary>The per-attempt outbox root as the agent's unit sees it: a read-only bind of the mission units' systemd state
    /// directories (/var/lib/private/blackglass-outbox, security correction C1).</summary>
    public required string OutboxDir { get; init; }
    /// <summary>The exit records the template's root ExecStopPost step writes (root:blackglass-agent 0750; read-only to the agent).</summary>
    public required string ExitDir { get; init; }
    /// <summary>The host's first mission port (Stage 8's port_base): port P runs as slot P - PortBase, unit blackglass-mission-NN@.
    /// The backend's registration must use the same value and a capacity no larger than <see cref="LocalCapacity"/>.</summary>
    public required int PortBase { get; init; }
    /// <summary>The root credential broker's socket (security correction I3).</summary>
    public string CredentialSocket { get; init; } = "/run/blackglass/credential.sock";
    /// <summary>The installed server build's build-info.txt (revision=, and catalogueVersion= once Stage 12's build writes it).</summary>
    public required string ServerBuildInfo { get; init; }
    /// <summary>Used when build-info.txt names no catalogueVersion (builds before Stage 12).</summary>
    public string? CatalogueVersion { get; init; }
    public string Systemctl { get; init; } = "/usr/bin/systemctl";
    public int HeartbeatSeconds { get; init; } = 10;
    public int ScanIntervalMs { get; init; } = 1000;
    public int RelayBackoffMinMs { get; init; } = 1000;
    public int RelayBackoffMaxMs { get; init; } = 60_000;
    /// <summary>The slow retry while the host secret is refused (A10-D8): no rapid loop.</summary>
    public int AuthRetrySeconds { get; init; } = 60;
    public int RequestTimeoutSeconds { get; init; } = 40;
    /// <summary>The largest outbox file the agent reads; anything larger is an anomaly, never sent.</summary>
    public long MaxResultBytes { get; init; } = 4 * 1024 * 1024;
    /// <summary>Outbox disk use that raises an operational alert (the filesystem's used share, percent).</summary>
    public int OutboxAlertPercent { get; init; } = 80;
    /// <summary>The host's slots (installed slot templates, at most 64), and so the most attempts it supervises at once.</summary>
    public int LocalCapacity { get; init; } = 64;
    /// <summary>The user whose socket must hold the API's loopback port before anything is sent to it (security review I1);
    /// null only in tests against an in-process API.</summary>
    public string? TunnelUser { get; init; }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex HostIdPattern();

    static readonly string[] Members =
    [
        "hostId", "apiBaseUrl", "hostSecretFile", "stateDir", "relayDir", "outboxDir", "exitDir", "portBase", "credentialSocket", "serverBuildInfo", "catalogueVersion",
        "systemctl", "heartbeatSeconds", "scanIntervalMs", "relayBackoffMinMs", "relayBackoffMaxMs", "authRetrySeconds", "requestTimeoutSeconds",
        "maxResultBytes", "outboxAlertPercent", "localCapacity", "tunnelUser",
    ];

    public static AgentConfig Load(string path)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new AgentConfigException($"The configuration file can't be read ({e.GetType().Name})."); }
        return Parse(bytes);
    }

    public static AgentConfig Parse(ReadOnlySpan<byte> json)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 4 }); }
        catch (JsonException) { throw new AgentConfigException("The configuration is not JSON."); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new AgentConfigException("The configuration must be one JSON object.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in root.EnumerateObject())
            {
                if (!Members.Contains(p.Name)) throw new AgentConfigException($"Unknown setting '{Clip(p.Name)}'.");
                if (!seen.Add(p.Name)) throw new AgentConfigException($"Setting '{p.Name}' appears twice.");
            }
            string Text(string name, bool required = true)
            {
                if (!root.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null)
                    return required ? throw new AgentConfigException($"Setting '{name}' is required.") : "";
                return v.ValueKind == JsonValueKind.String ? v.GetString()! : throw new AgentConfigException($"Setting '{name}' must be a string.");
            }
            string AbsolutePath(string name)
            {
                var value = Text(name);
                if (!value.StartsWith('/') || value.Contains("..", StringComparison.Ordinal) || value.Contains('\0', StringComparison.Ordinal) || value.Length > 256)
                    throw new AgentConfigException($"Setting '{name}' must be an absolute path without '..'.");
                return value.Length > 1 ? value.TrimEnd('/') : value;
            }
            int Number(string name, int fallback, int min, int max)
            {
                if (!root.TryGetProperty(name, out var v)) return fallback;
                if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var n) || n < min || n > max)
                    throw new AgentConfigException($"Setting '{name}' must be a whole number from {min} to {max}.");
                return n;
            }
            int RequiredNumber(string name, int min, int max) =>
                root.TryGetProperty(name, out _) ? Number(name, min, min, max) : throw new AgentConfigException($"Setting '{name}' is required.");

            var hostId = Text("hostId");
            if (!HostIdPattern().IsMatch(hostId)) throw new AgentConfigException("Setting 'hostId' is not a host id.");
            if (!Uri.TryCreate(Text("apiBaseUrl"), UriKind.Absolute, out var api) || api.Scheme != Uri.UriSchemeHttp
                || !(api.Host == "127.0.0.1" || api.Host == "[::1]") || api.PathAndQuery != "/" || !string.IsNullOrEmpty(api.UserInfo))
                throw new AgentConfigException("Setting 'apiBaseUrl' must be http://127.0.0.1:<port>/ (the loopback tunnel).");
            var catalogue = Text("catalogueVersion", required: false);
            if (catalogue.Length > 0 && !Blackglass.Accounts.Domain.Hosts.BuildIdentity.IsWellFormedCatalogueVersion(catalogue))
                throw new AgentConfigException("Setting 'catalogueVersion' must be sha256:<64 lowercase hex>.");
            var config = new AgentConfig
            {
                HostId = hostId,
                ApiBaseUrl = api,
                HostSecretFile = AbsolutePath("hostSecretFile"),
                StateDir = AbsolutePath("stateDir"),
                RelayDir = AbsolutePath("relayDir"),
                OutboxDir = AbsolutePath("outboxDir"),
                ExitDir = AbsolutePath("exitDir"),
                PortBase = RequiredNumber("portBase", 1024, 65535),
                CredentialSocket = root.TryGetProperty("credentialSocket", out _) ? AbsolutePath("credentialSocket") : "/run/blackglass/credential.sock",
                ServerBuildInfo = AbsolutePath("serverBuildInfo"),
                CatalogueVersion = catalogue.Length > 0 ? catalogue : null,
                Systemctl = root.TryGetProperty("systemctl", out _) ? AbsolutePath("systemctl") : "/usr/bin/systemctl",
                HeartbeatSeconds = Number("heartbeatSeconds", 10, 1, 60),
                ScanIntervalMs = Number("scanIntervalMs", 1000, 100, 60_000),
                RelayBackoffMinMs = Number("relayBackoffMinMs", 1000, 10, 60_000),
                RelayBackoffMaxMs = Number("relayBackoffMaxMs", 60_000, 100, 3_600_000),
                AuthRetrySeconds = Number("authRetrySeconds", 60, 1, 3600),
                RequestTimeoutSeconds = Number("requestTimeoutSeconds", 40, 5, 300),
                MaxResultBytes = Number("maxResultBytes", 4 * 1024 * 1024, 1024, 16 * 1024 * 1024),
                OutboxAlertPercent = Number("outboxAlertPercent", 80, 1, 100),
                LocalCapacity = Number("localCapacity", 64, 1, 64),
                TunnelUser = Text("tunnelUser", required: false) is { Length: > 0 } tunnel
                    ? (HostIdPattern().IsMatch(tunnel) ? tunnel : throw new AgentConfigException("Setting 'tunnelUser' is not a user name.")) : null,
            };
            if (config.RelayBackoffMaxMs < config.RelayBackoffMinMs) throw new AgentConfigException("Setting 'relayBackoffMaxMs' is below 'relayBackoffMinMs'.");
            if (config.PortBase + config.LocalCapacity > 65536) throw new AgentConfigException("Settings 'portBase' and 'localCapacity' run past port 65535.");
            var dirs = new[] { config.StateDir, config.RelayDir, config.OutboxDir, config.ExitDir };
            if (dirs.Distinct(StringComparer.Ordinal).Count() != dirs.Length) throw new AgentConfigException("The agent's directories must be distinct.");
            return config;
        }
    }

    static string Clip(string s) => s.Length <= 32 ? s : s[..32];
}
