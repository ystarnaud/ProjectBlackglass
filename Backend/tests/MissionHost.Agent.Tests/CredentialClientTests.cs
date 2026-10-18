using System.Net.Sockets;
using System.Text;
using Blackglass.Accounts.Domain.Auth;
using Blackglass.MissionHost.CredentialBroker;

namespace Blackglass.MissionHost.Agent.Tests;

/// <summary>
/// The agent's side of the root credential broker (security correction I3): one request per connection over the broker's Unix
/// socket, only "prepare &lt;attempt&gt; &lt;generation&gt;\n&lt;pc1 credential&gt;\n" or "discard &lt;attempt&gt;\n"; anything but a
/// well-formed "ok" is a failure the claim flow treats as "do not start".
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class CredentialClientTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "bg-client-" + Guid.NewGuid().ToString("N"));
    readonly string socketPath;
    readonly Guid attempt = Guid.CreateVersion7();

    public CredentialClientTests()
    {
        Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.CreateDirectory(Path.Combine(dir, "credentials"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        socketPath = Path.Combine(dir, "credential.sock");
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>A broker on the socket: each connection's request (until the client half-closes) goes through <paramref name="answer"/>.</summary>
    sealed class Server : IDisposable
    {
        readonly Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        readonly CancellationTokenSource stop = new();
        public readonly List<byte[]> Requests = [];

        public Server(string path, Func<byte[], byte[]?> answer)
        {
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(4);
            _ = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    Socket client;
                    try { client = await listener.AcceptAsync(stop.Token); } catch (Exception) { return; }
                    using (client)
                    {
                        var buffer = new MemoryStream();
                        var chunk = new byte[256];
                        int read;
                        while ((read = await client.ReceiveAsync(chunk)) > 0) buffer.Write(chunk, 0, read);
                        lock (Requests) Requests.Add(buffer.ToArray());
                        if (answer(buffer.ToArray()) is { } reply) await client.SendAsync(reply);
                        else await Task.Delay(TimeSpan.FromSeconds(5), stop.Token).ContinueWith(_ => { });
                    }
                }
            });
        }

        public void Dispose() { stop.Cancel(); listener.Dispose(); }
    }

    Server RealBroker(StringWriter log)
    {
        var store = new CredentialStore(new BrokerOptions(Path.Combine(dir, "credentials"), Native.EffectiveUid), log);
        return new Server(socketPath, request => Broker.Handle(request, Native.EffectiveUid, Native.EffectiveUid, store, log));
    }

    SocketCredentialBroker Client(TimeSpan? timeout = null) => new(socketPath, timeout ?? TimeSpan.FromSeconds(5));

    [Fact]
    public async Task A_credential_goes_to_the_broker_as_exactly_the_request_and_lands_in_its_store()
    {
        var log = new StringWriter();
        using var server = RealBroker(log);
        var credential = OpaqueSecret.New(OpaqueSecret.ProcessCredentialPrefix).Text;
        Assert.Equal(new BrokerAnswer(true, "ok"), await Client().PrepareAsync(attempt, 2, credential, default));
        Assert.Equal($"prepare {attempt:D} 2\n{credential}\n", Encoding.ASCII.GetString(server.Requests.Single()));
        var stored = Path.Combine(dir, "credentials", attempt.ToString("D"), "process");
        Assert.Equal(credential, File.ReadAllText(stored));
        Assert.Equal(new BrokerAnswer(true, "ok"), await Client().DiscardAsync(attempt, default));
        Assert.Equal($"discard {attempt:D}\n", Encoding.ASCII.GetString(server.Requests[1]));
        Assert.False(File.Exists(stored));
        Assert.DoesNotContain(credential, log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refusal_is_a_failure_with_its_code()
    {
        var log = new StringWriter();
        using var server = RealBroker(log);
        var client = Client();
        Assert.True((await client.PrepareAsync(attempt, 2, OpaqueSecret.New("pc1").Text, default)).Ok);
        Assert.Equal(new BrokerAnswer(false, "stale"), await client.PrepareAsync(attempt, 1, OpaqueSecret.New("pc1").Text, default));
        Assert.Equal(new BrokerAnswer(false, "conflict"), await client.PrepareAsync(attempt, 2, OpaqueSecret.New("pc1").Text, default));
    }

    [Fact]
    public async Task No_broker_a_garbled_answer_or_no_answer_is_never_success()
    {
        var credential = OpaqueSecret.New("pc1").Text;
        Assert.Equal(new BrokerAnswer(false, "unavailable"), await Client().PrepareAsync(attempt, 1, credential, default)); // nothing listening
        using (new Server(socketPath, _ => "yes\n"u8.ToArray()))
            Assert.Equal(new BrokerAnswer(false, "bad_answer"), await Client().PrepareAsync(attempt, 1, credential, default));
        File.Delete(socketPath);
        using (new Server(socketPath, _ => Encoding.ASCII.GetBytes("ok\n" + new string('x', 200))))
            Assert.Equal(new BrokerAnswer(false, "bad_answer"), await Client().PrepareAsync(attempt, 1, credential, default));
        File.Delete(socketPath);
        using (new Server(socketPath, _ => null))
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Assert.Equal(new BrokerAnswer(false, "unavailable"), await Client(TimeSpan.FromMilliseconds(300)).PrepareAsync(attempt, 1, credential, default));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4));
        }
    }

    [Fact]
    public async Task The_client_sends_nothing_but_a_process_credential_for_a_real_attempt()
    {
        using var server = new Server(socketPath, _ => "ok\n"u8.ToArray());
        Assert.Equal(new BrokerAnswer(false, "bad_request"), await Client().PrepareAsync(attempt, 1, OpaqueSecret.New("hs1").Text, default)); // the host secret: never
        Assert.Equal(new BrokerAnswer(false, "bad_request"), await Client().PrepareAsync(attempt, 1, "/etc/shadow", default));
        Assert.Equal(new BrokerAnswer(false, "bad_request"), await Client().PrepareAsync(attempt, 0, OpaqueSecret.New("pc1").Text, default));
        Assert.Equal(new BrokerAnswer(false, "bad_request"), await Client().PrepareAsync(Guid.Empty, 1, OpaqueSecret.New("pc1").Text, default));
        Assert.Empty(server.Requests);
    }
}
