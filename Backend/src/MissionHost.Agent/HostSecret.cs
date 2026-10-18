using Blackglass.Accounts.Domain.Auth;

namespace Blackglass.MissionHost.Agent;

/// <summary>
/// The host secret (spec §4.3, A10-D8): read from its protected file (mode 0600 or stricter, owner blackglass-agent; the
/// operator replaces it atomically with a rename), validated as an hs1_ secret, kept only in memory and only ever sent as the
/// Authorization header. It is re-read on SIGHUP and after a 401. Its text never reaches a log, a file the agent writes, an
/// exception message or a command line.
/// </summary>
public sealed class HostSecret(string path)
{
    readonly object gate = new();
    string? current;
    DateTime lastThrottled = DateTime.MinValue;

    public enum ReloadResult { Changed, Unchanged, Invalid }

    /// <summary>True once a valid secret was read.</summary>
    public bool Loaded { get { lock (gate) return current is not null; } }

    /// <summary>The Authorization header value: "Bearer hs1_…".</summary>
    internal string AuthorizationValue
    {
        get { lock (gate) return "Bearer " + (current ?? throw new InvalidOperationException("No host secret is loaded.")); }
    }

    /// <summary>Reads the file again. An unreadable, too open or malformed file keeps the previous secret (Invalid).</summary>
    public ReloadResult Reload()
    {
        var read = ReadFile();
        lock (gate)
        {
            if (read is null) return ReloadResult.Invalid;
            if (read == current) return ReloadResult.Unchanged;
            current = read;
            return ReloadResult.Changed;
        }
    }

    /// <summary>A reload caused by a 401: at most once per <paramref name="minimumInterval"/>, so a burst of 401s reads the
    /// file once (null: throttled, nothing read).</summary>
    public ReloadResult? ReloadThrottled(TimeSpan minimumInterval)
    {
        lock (gate)
        {
            if (DateTime.UtcNow - lastThrottled < minimumInterval) return null;
            lastThrottled = DateTime.UtcNow;
        }
        return Reload();
    }

    string? ReadFile()
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.LinkTarget is not null) return null;
            if (!OperatingSystem.IsWindows())
            {
                const UnixFileMode open = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
                if ((File.GetUnixFileMode(path) & open) != 0) return null; // readable by others: refused, never used
            }
            if (info.Length > 256) return null;
            var text = File.ReadAllText(path);
            if (text.EndsWith('\n')) text = text[..^1];
            return OpaqueSecret.TryParse(text, OpaqueSecret.HostSecretPrefix, out _) ? text : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
