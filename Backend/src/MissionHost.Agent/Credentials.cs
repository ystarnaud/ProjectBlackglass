using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Blackglass.Accounts.Domain.Auth;

namespace Blackglass.MissionHost.Agent;

/// <summary>The broker's answer: <see cref="Ok"/> only for its exact "ok"; otherwise its error code, "unavailable" (no broker or
/// no answer in time), "bad_answer" (anything unexpected) or "bad_request" (the agent refused to send it).</summary>
public sealed record BrokerAnswer(bool Ok, string Code);

/// <summary>
/// The root credential broker (security correction I3; deploy/mission/blackglass-credential.socket): the agent hands it the
/// attempt, the acknowledged generation and the process credential, and it writes the root-only file the unit's LoadCredential
/// reads. No path ever crosses to root. A unit is started only after an "ok".
/// </summary>
public interface ICredentialBroker
{
    Task<BrokerAnswer> PrepareAsync(Guid attempt, int generation, string credential, CancellationToken ct);
    Task<BrokerAnswer> DiscardAsync(Guid attempt, CancellationToken ct);
}

/// <summary>One request per connection to the broker's Unix socket: written whole, then the write side closed, then at most a
/// short answer read until the broker closes, all within the timeout. The credential is never logged.</summary>
public sealed partial class SocketCredentialBroker(string socketPath, TimeSpan timeout) : ICredentialBroker
{
    const int MaxAnswer = 64;

    [GeneratedRegex(@"^(ok|error [a-z_]{1,32})\n\z", RegexOptions.CultureInvariant)]
    private static partial Regex AnswerLine();

    public Task<BrokerAnswer> PrepareAsync(Guid attempt, int generation, string credential, CancellationToken ct)
    {
        if (attempt == Guid.Empty || generation < 1 || !OpaqueSecret.TryParse(credential, OpaqueSecret.ProcessCredentialPrefix, out _))
            return Task.FromResult(new BrokerAnswer(false, "bad_request"));
        return SendAsync($"prepare {AttemptIds.Text(attempt)} {generation}\n{credential}\n", ct);
    }

    public Task<BrokerAnswer> DiscardAsync(Guid attempt, CancellationToken ct) =>
        attempt == Guid.Empty ? Task.FromResult(new BrokerAnswer(false, "bad_request")) : SendAsync($"discard {AttemptIds.Text(attempt)}\n", ct);

    async Task<BrokerAnswer> SendAsync(string request, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var bytes = Encoding.ASCII.GetBytes(request);
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), deadline.Token);
            await socket.SendAsync(bytes, SocketFlags.None, deadline.Token);
            socket.Shutdown(SocketShutdown.Send);
            var answer = new byte[MaxAnswer + 1];
            var total = 0;
            while (total < answer.Length)
            {
                var read = await socket.ReceiveAsync(answer.AsMemory(total), SocketFlags.None, deadline.Token);
                if (read == 0) break;
                total += read;
            }
            var text = Encoding.ASCII.GetString(answer, 0, total);
            if (total > MaxAnswer || !AnswerLine().IsMatch(text)) return new BrokerAnswer(false, "bad_answer");
            return text == "ok\n" ? new BrokerAnswer(true, "ok") : new BrokerAnswer(false, text[6..^1]);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new BrokerAnswer(false, "unavailable"); }
        catch (SocketException) { return new BrokerAnswer(false, "unavailable"); }
        catch (IOException) { return new BrokerAnswer(false, "unavailable"); }
        finally { Array.Clear(bytes); }
    }
}
