using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Blackglass.Accounts.Domain.Auth;

namespace Blackglass.MissionHost.Agent;

/// <summary>A claim the backend granted, validated before anything is built from it (A10-D2, A10-D4).</summary>
public sealed record ClaimGrant(string Attempt, int MissionPort, string ProcessCredential, int Generation, DateTime LaunchDeadline, bool Recovered,
    string[] Participants);

/// <summary>The kinds of answer every agent route can give besides its own: a refused secret, or no definitive answer.</summary>
public enum Transport
{
    /// <summary>401: the host secret was refused (A10-D8). Never evidence about any mission.</summary>
    Unauthorized,
    /// <summary>A timeout, a refused or dropped connection (the tunnel), 429, 5xx: retried, never evidence of anything.</summary>
    Unavailable,
}

public abstract record ClaimAnswer
{
    public sealed record Granted(ClaimGrant Grant) : ClaimAnswer;
    public sealed record NoWork : ClaimAnswer;
    public sealed record HostUnavailable : ClaimAnswer;
    public sealed record InvalidState : ClaimAnswer;
    /// <summary>A 200 whose body the agent can't trust (a bad attempt id, credential or participant): never launched.</summary>
    public sealed record Malformed(string Reason) : ClaimAnswer;
    public sealed record Failed(Transport Kind) : ClaimAnswer;
    public sealed record Refused(int Status, string? Code) : ClaimAnswer;
}

public abstract record LaunchingAnswer
{
    public sealed record Acknowledged(string State) : LaunchingAnswer;
    public sealed record Superseded : LaunchingAnswer;
    public sealed record InvalidState : LaunchingAnswer;
    public sealed record NotFound : LaunchingAnswer;
    public sealed record Failed(Transport Kind) : LaunchingAnswer;
    public sealed record Refused(int Status, string? Code) : LaunchingAnswer;
}

public abstract record HeartbeatAnswer
{
    public sealed record Ok(bool Drained, string[] Stop) : HeartbeatAnswer;
    public sealed record Failed(Transport Kind) : HeartbeatAnswer;
    public sealed record Refused(int Status, string? Code) : HeartbeatAnswer;
}

public abstract record ExitedAnswer
{
    public sealed record Applied(string Outcome) : ExitedAnswer;
    /// <summary>409 ConflictingReport, 400 IncompleteResults or UnknownParticipant, 404: definitive, an anomaly, never resent.</summary>
    public sealed record Rejected(int Status, string? Code) : ExitedAnswer;
    public sealed record Failed(Transport Kind) : ExitedAnswer;
}

/// <summary>A relayed result's answer (§C). <see cref="Definitive"/> outcomes are recorded once and the file is never resent.</summary>
public abstract record RelayAnswer
{
    public sealed record Definitive(string Outcome, int Status, string? Code) : RelayAnswer;
    public sealed record Failed(Transport Kind) : RelayAnswer;
}

/// <summary>The agent's backend: the /agent/v1 routes (spec §16.2), behind an interface for the tests' fakes.</summary>
public interface IBackend
{
    Task<ClaimAnswer> ClaimAsync(Guid key, CancellationToken ct);
    Task<LaunchingAnswer> LaunchingAsync(string attempt, int generation, CancellationToken ct);
    Task<HeartbeatAnswer> HeartbeatAsync(HeartbeatBody body, CancellationToken ct);
    Task<ExitedAnswer> ExitedAsync(string attempt, ExitedBody body, CancellationToken ct);
    Task<RelayAnswer> RelayAsync(byte[] manifest, CancellationToken ct);
}

/// <summary>A unit in the heartbeat inventory; <see cref="Slot"/> is the agent's own (its unit name's NN) and is never sent.</summary>
public sealed record UnitEntry(string AttemptId, string ActiveState, int Slot);
public sealed record HeartbeatBody(string BuildRevision, string CatalogueVersion, bool InventoryComplete, IReadOnlyList<UnitEntry> Units, int OutboxPending);
public sealed record ExitedBody(int? ExitCode, string? Signal, IReadOnlyList<(string Account, bool Present)> Results, bool OutboxComplete);

/// <summary>
/// The HTTP implementation. Every request carries "Authorization: Bearer &lt;host secret&gt;" (read from <see cref="HostSecret"/> at
/// send time) and nothing else secret; no request or response body is logged. A 401 is <see cref="Transport.Unauthorized"/>;
/// a timeout, a connection failure, 408, 429 and 5xx are <see cref="Transport.Unavailable"/> (spec §9.2: never definitive).
/// </summary>
public sealed class BackendClient(HttpClient http, HostSecret secret, Func<bool>? listenerTrusted = null) : IBackend
{
    static readonly MediaTypeHeaderValue JsonType = new("application/json");

    sealed record Response(HttpStatusCode Status, byte[] Body, string? Code);

    async Task<(Response? Response, Transport? Failure)> SendAsync(string path, byte[] body, Guid? idempotencyKey, CancellationToken ct)
    {
        if (listenerTrusted is not null && !listenerTrusted()) return (null, Transport.Unavailable); // I1: never to a squatter
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = JsonType;
        request.Headers.TryAddWithoutValidation("Authorization", secret.AuthorizationValue);
        if (idempotencyKey is { } key) request.Headers.Add("Idempotency-Key", key.ToString("D"));
        try
        {
            using var response = await http.SendAsync(request, ct);
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            var status = response.StatusCode;
            if (status == HttpStatusCode.Unauthorized) return (null, Transport.Unauthorized);
            if ((int)status >= 500 || status is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout) return (null, Transport.Unavailable);
            return (new Response(status, bytes, ProblemCode(bytes)), null);
        }
        catch (HttpRequestException) { return (null, Transport.Unavailable); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return (null, Transport.Unavailable); } // the request timeout
    }

    static string? ProblemCode(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    public async Task<ClaimAnswer> ClaimAsync(Guid key, CancellationToken ct)
    {
        var (r, failure) = await SendAsync("agent/v1/claim", "{}"u8.ToArray(), key, ct);
        if (failure is { } f) return new ClaimAnswer.Failed(f);
        return r!.Status switch
        {
            HttpStatusCode.OK => ParseGrant(r.Body),
            HttpStatusCode.NoContent => new ClaimAnswer.NoWork(),
            HttpStatusCode.Conflict when r.Code == "HostUnavailable" => new ClaimAnswer.HostUnavailable(),
            HttpStatusCode.Conflict when r.Code == "InvalidState" => new ClaimAnswer.InvalidState(),
            _ => new ClaimAnswer.Refused((int)r.Status, r.Code),
        };
    }

    /// <summary>Validates every field before the agent uses any (A10-D2, A10-D4).</summary>
    public static ClaimAnswer ParseGrant(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new ClaimAnswer.Malformed("not_an_object");
            if (!root.TryGetProperty("attemptId", out var a) || a.ValueKind != JsonValueKind.String || !AttemptIds.TryParse(a.GetString(), out var attempt))
                return new ClaimAnswer.Malformed("attempt_id");
            if (!root.TryGetProperty("missionPort", out var p) || !p.TryGetInt32(out var port) || port is < 1024 or > 65535) return new ClaimAnswer.Malformed("mission_port");
            if (!root.TryGetProperty("processCredential", out var c) || c.ValueKind != JsonValueKind.String
                || !OpaqueSecret.TryParse(c.GetString(), OpaqueSecret.ProcessCredentialPrefix, out _))
                return new ClaimAnswer.Malformed("process_credential");
            if (!root.TryGetProperty("credentialGeneration", out var g) || !g.TryGetInt32(out var generation) || generation < 1) return new ClaimAnswer.Malformed("generation");
            if (!root.TryGetProperty("launchDeadline", out var d) || d.ValueKind != JsonValueKind.String || !d.TryGetDateTime(out var deadline))
                return new ClaimAnswer.Malformed("launch_deadline");
            var recovered = root.TryGetProperty("recovered", out var rec) && rec.ValueKind == JsonValueKind.True;
            if (!root.TryGetProperty("participants", out var list) || list.ValueKind != JsonValueKind.Array) return new ClaimAnswer.Malformed("participants");
            var participants = new List<string>();
            foreach (var e in list.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.String || !AccountIds.TryParse(e.GetString(), out _)) return new ClaimAnswer.Malformed("participant_id");
                participants.Add(e.GetString()!);
            }
            if (participants.Count is 0 or > 16 || participants.Distinct(StringComparer.Ordinal).Count() != participants.Count)
                return new ClaimAnswer.Malformed("participant_count");
            return new ClaimAnswer.Granted(new ClaimGrant(AttemptIds.Text(attempt), port, c.GetString()!, generation, deadline.ToUniversalTime(), recovered,
                [.. participants]));
        }
        catch (JsonException) { return new ClaimAnswer.Malformed("not_json"); }
    }

    public async Task<LaunchingAnswer> LaunchingAsync(string attempt, int generation, CancellationToken ct)
    {
        if (!AttemptIds.TryParse(attempt, out var id)) throw new ArgumentException("Not a canonical attempt id.", nameof(attempt));
        var (r, failure) = await SendAsync($"agent/v1/attempts/{AttemptIds.Text(id)}/launching",
            Encoding.UTF8.GetBytes($"{{\"credentialGeneration\":{generation}}}"), null, ct);
        if (failure is { } f) return new LaunchingAnswer.Failed(f);
        return r!.Status switch
        {
            HttpStatusCode.OK => new LaunchingAnswer.Acknowledged(StateOf(r.Body)),
            HttpStatusCode.Conflict when r.Code == "CredentialSuperseded" => new LaunchingAnswer.Superseded(),
            HttpStatusCode.Conflict when r.Code == "InvalidState" => new LaunchingAnswer.InvalidState(),
            HttpStatusCode.NotFound => new LaunchingAnswer.NotFound(),
            _ => new LaunchingAnswer.Refused((int)r.Status, r.Code),
        };
    }

    static string StateOf(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : "unknown";
        }
        catch (JsonException) { return "unknown"; }
    }

    public async Task<HeartbeatAnswer> HeartbeatAsync(HeartbeatBody body, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            buildRevision = body.BuildRevision,
            catalogueVersion = body.CatalogueVersion,
            inventory = new { complete = body.InventoryComplete, units = body.Units.Select(u => new { attemptId = u.AttemptId, activeState = u.ActiveState }) },
            outboxPending = body.OutboxPending,
        });
        var (r, failure) = await SendAsync("agent/v1/heartbeat", json, null, ct);
        if (failure is { } f) return new HeartbeatAnswer.Failed(f);
        if (r!.Status != HttpStatusCode.OK) return new HeartbeatAnswer.Refused((int)r.Status, r.Code);
        try
        {
            using var doc = JsonDocument.Parse(r.Body);
            var drained = doc.RootElement.TryGetProperty("drained", out var d) && d.ValueKind == JsonValueKind.True;
            var stop = doc.RootElement.TryGetProperty("stop", out var s) && s.ValueKind == JsonValueKind.Array
                ? s.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToArray() : [];
            return new HeartbeatAnswer.Ok(drained, stop);
        }
        catch (JsonException) { return new HeartbeatAnswer.Refused(200, "unreadable"); }
    }

    public async Task<ExitedAnswer> ExitedAsync(string attempt, ExitedBody body, CancellationToken ct)
    {
        if (!AttemptIds.TryParse(attempt, out var id)) throw new ArgumentException("Not a canonical attempt id.", nameof(attempt));
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            exitCode = body.ExitCode,
            signal = body.Signal,
            results = body.Results.Select(x => new { accountId = x.Account, present = x.Present }),
            outbox = new { complete = body.OutboxComplete },
        });
        var (r, failure) = await SendAsync($"agent/v1/attempts/{AttemptIds.Text(id)}/exited", json, null, ct);
        if (failure is { } f) return new ExitedAnswer.Failed(f);
        if (r!.Status != HttpStatusCode.OK) return new ExitedAnswer.Rejected((int)r.Status, r.Code);
        try
        {
            using var doc = JsonDocument.Parse(r.Body);
            return new ExitedAnswer.Applied(doc.RootElement.TryGetProperty("applied", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString()! : "unknown");
        }
        catch (JsonException) { return new ExitedAnswer.Applied("unknown"); }
    }

    public async Task<RelayAnswer> RelayAsync(byte[] manifest, CancellationToken ct)
    {
        var (r, failure) = await SendAsync("agent/v1/results", manifest, null, ct);
        if (failure is { } f) return new RelayAnswer.Failed(f);
        return new RelayAnswer.Definitive(RelayOutcomes.Of((int)r!.Status, r.Code, Disposition(r.Body)), (int)r.Status, r.Code);
    }

    static string? Disposition(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("disposition", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>The §C table: a definitive relay answer → the delivery state recorded once (the file is never resent).</summary>
public static class RelayOutcomes
{
    public const string Done = "done", UnderReview = "under_review", Conflict = "conflict", Revoked = "revoked", Refused = "refused",
        Limited = "limited", Rejected = "rejected", TransportRefused = "transport_refused";

    public static string Of(int status, string? code, string? disposition) => status switch
    {
        200 => Done,
        202 => UnderReview,
        409 when code is "Conflict" or "AlreadySettledAsVoid" => Conflict,
        409 when code == "AttemptRevoked" => Revoked,
        409 when code == "SubmissionLimit" => Limited,
        409 => Refused,
        422 => Rejected,
        _ => TransportRefused, // 400, 404, 413, 415: an agent defect, kept for the operator, never resent automatically
    };
}
