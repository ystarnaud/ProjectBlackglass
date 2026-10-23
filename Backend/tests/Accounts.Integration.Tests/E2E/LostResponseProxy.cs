using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Blackglass.Accounts.IntegrationTests.E2E;

/// <summary>One relayed request: method, path without its query, the API's status (0 when it was unreachable) and what the
/// relay did with the answer. Never a header or a body: those carry credentials and tickets.</summary>
public sealed record ProxyLogEntry(DateTime Utc, string Method, string Path, int Status, string Outcome);

/// <summary>
/// Stage 13 CP4's loopback relay between the mission server and the API (test-only, 127.0.0.1 on both sides): one request per
/// connection, forwarded byte for byte with <c>Connection: close</c>, and its answer returned unchanged, except the one answer
/// chosen with <see cref="DropNextAnswer"/>, which is read from the API (so the API committed whatever it did) and then lost: the
/// client's connection closes with no answer, exactly as a response lost on the network. An unreachable API closes the client's
/// connection the same way.
/// </summary>
public sealed class LostResponseProxy : IAsyncDisposable
{
    const int MaxHead = 64 * 1024;
    const int MaxBody = 4 * 1024 * 1024;
    static readonly TimeSpan UpstreamTimeout = TimeSpan.FromSeconds(60);

    readonly TcpListener listener = new(IPAddress.Loopback, 0);
    readonly int upstreamPort;
    readonly CancellationTokenSource stop = new();
    readonly Task accepting;
    readonly List<ProxyLogEntry> log = [];
    readonly object gate = new();
    string? dropSuffix;
    Action? onDropped;

    public LostResponseProxy(int upstreamPort)
    {
        this.upstreamPort = upstreamPort;
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        accepting = AcceptAsync();
    }

    public int Port { get; }
    public string BaseUrl => $"http://127.0.0.1:{Port.ToString(CultureInfo.InvariantCulture)}";

    public IReadOnlyList<ProxyLogEntry> Log { get { lock (gate) return [.. log]; } }

    /// <summary>The next request whose path ends with <paramref name="pathSuffix"/> reaches the API; its answer is lost and
    /// <paramref name="dropped"/> runs once.</summary>
    public void DropNextAnswer(string pathSuffix, Action dropped)
    {
        lock (gate) (dropSuffix, onDropped) = (pathSuffix, dropped);
    }

    async Task AcceptAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(stop.Token); }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
            _ = Task.Run(() => RelayAsync(client));
        }
    }

    async Task RelayAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            var downstream = client.GetStream();
            var request = await ReadRequestAsync(downstream);
            if (request is null) return;
            var (method, path, head, body) = request.Value;

            byte[] answer;
            using (var upstream = new TcpClient())
            {
                try { await upstream.ConnectAsync(IPAddress.Loopback, upstreamPort, stop.Token); }
                catch (SocketException)
                {
                    Record(method, path, 0, "upstream_unreachable");
                    return;
                }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                timeout.CancelAfter(UpstreamTimeout);
                var up = upstream.GetStream();
                await up.WriteAsync(head, timeout.Token);
                await up.WriteAsync(body, timeout.Token);
                answer = await ReadToEndAsync(up, timeout.Token);
            }
            var status = StatusOf(answer);

            Action? dropped = null;
            lock (gate)
            {
                if (dropSuffix is not null && path.EndsWith(dropSuffix, StringComparison.Ordinal))
                {
                    dropped = onDropped;
                    (dropSuffix, onDropped) = (null, null);
                }
            }
            if (dropped is not null)
            {
                Record(method, path, status, "dropped");
                dropped();
                return; // the client's connection closes with no answer
            }
            Record(method, path, status, "answered"); // before the client can see the answer
            await downstream.WriteAsync(answer, stop.Token);
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
    }

    void Record(string method, string path, int status, string outcome)
    {
        lock (gate) log.Add(new ProxyLogEntry(DateTime.UtcNow, method, path, status, outcome));
    }

    /// <summary>The request line, the head rewritten for one request per connection (Connection: close, no Expect), and the body
    /// (Content-Length only). A client expecting 100-continue gets it before its body is read. Null for anything else.</summary>
    static async Task<(string Method, string Path, byte[] Head, byte[] Body)?> ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new List<byte>();
        var one = new byte[1];
        while (!EndsWithBlankLine(buffer))
        {
            if (buffer.Count >= MaxHead || await stream.ReadAsync(one) == 0) return null;
            buffer.Add(one[0]);
        }
        var lines = Encoding.ASCII.GetString([.. buffer]).Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length != 3) return null;
        var path = requestLine[1].Split('?')[0];

        var length = 0;
        var expectsContinue = false;
        var kept = new StringBuilder(lines[0]).Append("\r\n");
        foreach (var line in lines.Skip(1).Where(l => l.Length > 0))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) return null;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) return null;
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                && (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out length) || length > MaxBody)) return null;
            if (name.Equals("Expect", StringComparison.OrdinalIgnoreCase)) { expectsContinue = value.Equals("100-continue", StringComparison.OrdinalIgnoreCase); continue; }
            if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase) || name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase)) continue;
            kept.Append(line).Append("\r\n");
        }
        kept.Append("Connection: close\r\n\r\n");

        if (expectsContinue) await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray());
        var body = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = await stream.ReadAsync(body.AsMemory(read));
            if (n == 0) return null;
            read += n;
        }
        return (requestLine[0], path, Encoding.ASCII.GetBytes(kept.ToString()), body);
    }

    static bool EndsWithBlankLine(List<byte> b) =>
        b.Count >= 4 && b[^4] == '\r' && b[^3] == '\n' && b[^2] == '\r' && b[^1] == '\n';

    static async Task<byte[]> ReadToEndAsync(NetworkStream stream, CancellationToken ct)
    {
        using var all = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int n;
        while ((n = await stream.ReadAsync(chunk, ct)) > 0)
        {
            all.Write(chunk, 0, n);
            if (all.Length > MaxBody) throw new IOException("The API's answer is too large for the relay.");
        }
        return all.ToArray();
    }

    /// <summary>"HTTP/1.1 200 OK" → 200; 0 when the answer is empty or unreadable.</summary>
    static int StatusOf(byte[] answer)
    {
        var line = Encoding.ASCII.GetString(answer, 0, Math.Min(answer.Length, 64)).Split("\r\n")[0].Split(' ');
        return line.Length >= 2 && int.TryParse(line[1], NumberStyles.None, CultureInfo.InvariantCulture, out var status) ? status : 0;
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        listener.Stop();
        try { await accepting; } catch (OperationCanceledException) { }
        stop.Dispose();
    }
}
