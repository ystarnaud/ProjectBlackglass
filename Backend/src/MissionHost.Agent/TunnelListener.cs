using System.Globalization;

namespace Blackglass.MissionHost.Agent;

/// <summary>
/// The security review's I1: the loopback port the agent sends its host secret to (the tunnel, A10-D3) is trusted only while the
/// listening socket on it belongs to the tunnel's user, read from /proc/net/tcp (IPv4 listening sockets and their uid). While
/// anything else holds the port (a squatter during a tunnel restart), nothing is sent: the request is "unavailable", retried.
/// </summary>
public static class TunnelListener
{
    /// <summary>The uid owning the IPv4 LISTEN socket on 127.0.0.1:<paramref name="port"/> in a /proc/net/tcp text, or null.</summary>
    public static uint? ListenerUid(string procNetTcp, int port)
    {
        var wanted = $"0100007F:{port:X4}"; // 127.0.0.1, little-endian, and the port in big-endian hex
        foreach (var line in procNetTcp.Split('\n').Skip(1))
        {
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 8 || f[1] != wanted || f[3] != "0A") continue; // 0A: LISTEN
            return uint.TryParse(f[7], NumberStyles.None, CultureInfo.InvariantCulture, out var uid) ? uid : null;
        }
        return null;
    }

    /// <summary>A user's uid from an /etc/passwd text, or null.</summary>
    public static uint? UidOf(string passwd, string user)
    {
        foreach (var line in passwd.Split('\n'))
        {
            var f = line.Split(':');
            if (f.Length >= 3 && f[0] == user && uint.TryParse(f[2], NumberStyles.None, CultureInfo.InvariantCulture, out var uid)) return uid;
        }
        return null;
    }

    /// <summary>The check the agent's HTTP client runs before every request: the configured port's listener is the tunnel user's.</summary>
    public static Func<bool> Check(int port, string user, AgentLog log)
    {
        var expected = UidOf(File.ReadAllText("/etc/passwd"), user) ?? throw new AgentConfigException($"The tunnel user '{user}' does not exist.");
        var lastAlert = DateTime.MinValue;
        return () =>
        {
            uint? actual;
            try { actual = ListenerUid(File.ReadAllText("/proc/net/tcp"), port); }
            catch (IOException) { actual = null; }
            if (actual == expected) return true;
            if (actual is not null && DateTime.UtcNow - lastAlert > TimeSpan.FromMinutes(5))
            {
                lastAlert = DateTime.UtcNow;
                log.Alert("tunnel_port_held_by_another_user", ("port", port), ("uid", actual)); // nothing is sent to it
            }
            return false;
        };
    }
}
