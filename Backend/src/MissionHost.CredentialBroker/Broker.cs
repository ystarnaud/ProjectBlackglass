using System.Globalization;
using System.Text;

namespace Blackglass.MissionHost.CredentialBroker;

/// <summary>One connection: the peer must be the agent's user; the request is parsed strictly and answered "ok\n" or
/// "error &lt;code&gt;\n". The answer and the log never carry the credential.</summary>
public static class Broker
{
    public const string AgentUser = "blackglass-agent";

    public static string Wire(BrokerCode code) => code switch
    {
        BrokerCode.Ok => "ok",
        BrokerCode.Stale => "stale",
        BrokerCode.Conflict => "conflict",
        BrokerCode.UnsafeStore => "unsafe_store",
        BrokerCode.WriteFailed => "write_failed",
        _ => "bad_attempt",
    };

    public static byte[] Handle(ReadOnlySpan<byte> request, uint peerUid, uint agentUid, CredentialStore store, TextWriter log)
    {
        if (peerUid != agentUid)
        {
            log.WriteLine($"credential_peer_refused uid={peerUid.ToString(CultureInfo.InvariantCulture)}");
            return Answer("error peer_refused");
        }
        var code = BrokerRequest.Parse(request) switch
        {
            BrokerRequest.Prepare p => Wire(store.Prepare(p.Attempt, p.Generation, p.Credential)),
            BrokerRequest.Discard d => Wire(store.Discard(d.Attempt)),
            BrokerRequest.Invalid i => i.Code,
            _ => "malformed",
        };
        if (code != "ok") log.WriteLine($"credential_request_refused code={code}");
        return Answer(code == "ok" ? "ok" : "error " + code);
    }

    static byte[] Answer(string text) => Encoding.ASCII.GetBytes(text + "\n");

    /// <summary>The agent's uid from the password file (a static system user, sysusers.d): only the exact name, never root.</summary>
    public static uint? AgentUid(string passwd)
    {
        foreach (var line in passwd.Split('\n'))
        {
            var fields = line.Split(':');
            if (fields.Length >= 7 && fields[0] == AgentUser && uint.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var uid))
                return uid == 0 ? null : uid;
        }
        return null;
    }
}
