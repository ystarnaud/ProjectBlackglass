using Blackglass.MissionHost.CredentialBroker;

// blackglass-credential-broker: started by systemd for one connection to /run/blackglass/credential.sock (Accept=yes), as root,
// with the connection on stdin and stdout (deploy/mission/blackglass-credential@.service). It takes no arguments, reads at most
// one request, answers it and exits. Its log goes to the journal (stderr) and never contains the credential.
if (args.Length != 0) { Console.Error.WriteLine("credential_broker_usage: no arguments are accepted"); return 64; }
Native.RestrictUmask();
var log = Console.Error;
var peer = Native.PeerUid(0);
uint? agent = null;
try { agent = Broker.AgentUid(File.ReadAllText("/etc/passwd")); } catch (IOException) { } catch (UnauthorizedAccessException) { }
using var output = Console.OpenStandardOutput();
if (peer is null || agent is null)
{
    log.WriteLine($"credential_broker_refused reason={(peer is null ? "peer_unknown" : "no_agent_user")}");
    output.Write("error peer_refused\n"u8);
    return 1;
}

// Read the request: at most MaxBytes + 1 (one more byte than allowed makes it too large), until the agent half-closes.
var buffer = new byte[BrokerRequest.MaxBytes + 1];
var total = 0;
using (var input = Console.OpenStandardInput())
using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
{
    try
    {
        while (total < buffer.Length)
        {
            var read = await input.ReadAsync(buffer.AsMemory(total), deadline.Token);
            if (read == 0) break;
            total += read;
        }
    }
    catch (OperationCanceledException)
    {
        log.WriteLine("credential_broker_refused reason=timeout");
        output.Write("error malformed\n"u8);
        return 1;
    }
}
var answer = Broker.Handle(buffer.AsSpan(0, total), peer.Value, agent.Value, new CredentialStore(BrokerOptions.Production, log), log);
Array.Clear(buffer);
output.Write(answer);
return 0;
