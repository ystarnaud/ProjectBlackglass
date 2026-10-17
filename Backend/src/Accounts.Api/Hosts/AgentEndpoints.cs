using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Blackglass.Accounts.Api.Auth;
using Blackglass.Accounts.Domain.Attempts;
using Blackglass.Accounts.Persistence.Hosts;

namespace Blackglass.Accounts.Api.Hosts;

/// <summary>
/// The host agent's routes, /agent/v1/* (spec §16.2; plan revision 2 §C). Each needs the <see cref="MissionClaims.HostPolicy"/>
/// (an enabled agent host's current hs1_ secret; anything else is the uniform 401) and takes the host from it alone. Before any
/// connection: 415 unless the body is JSON, 413 over <see cref="MissionBodies.MaxBodyBytes"/>, 400 InvalidBody for a body the
/// strict reader refuses. A database failure flows to the database failure middleware (503 or 500). Rate-limited per host.
/// </summary>
public static class AgentEndpoints
{
    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public const string IdempotencyKeyHeader = "Idempotency-Key";

    public static void MapAgent(this WebApplication app, Attempts.AttemptSettings settings)
    {
        var agent = app.MapGroup("/agent/v1").RequireAuthorization(MissionClaims.HostPolicy).RequireRateLimiting(RateLimits.PerHost)
            .WithMetadata(new RequestSizeLimitAttribute(MissionBodies.MaxBodyBytes));

        // POST /agent/v1/heartbeat: {buildRevision, catalogueVersion, inventory:{complete, units}, outboxPending} → {drained, stop}.
        agent.MapPost("/heartbeat", async (HttpContext http, HostStore hosts) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var (report, refusal) = await MissionBodies.ReadAsync(http, HeartbeatRequest.Parse);
            if (refusal is not null) return refusal;
            var result = await hosts.HeartbeatAsync(MissionClaims.HostOf(http.User), MissionClaims.HostSecretHashOf(http.User), report!, http.RequestAborted);
            if (result is null) return UniformUnauthorized.Result; // disabled since authentication
            return Results.Json(new HeartbeatResponse(result.Drained, result.Stop.Select(a => a.ToString("D")).ToArray()), Web);
        });

        // POST /agent/v1/claim + Idempotency-Key, body {} → 200 {attemptId, missionPort, processCredential, credentialGeneration,
        // launchDeadline, recovered}, or 204 after the long poll. A retry of the key while the attempt is still assigned is a
        // recovery (R1): the same attempt with a new credential and the next generation (recovered: true). The plaintext
        // credential appears in this response only (never stored, logged or cached: no-store).
        agent.MapPost("/claim", async (HttpContext http, HostStore hosts) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var (_, refusal) = await MissionBodies.ReadAsync(http, b => { EmptyRequest.Parse(b); return string.Empty; });
            if (refusal is not null) return refusal;
            var header = http.Request.Headers[IdempotencyKeyHeader];
            if (header.Count != 1 || !CanonicalUuid.TryParse(header[0], out var key)) return MissionBodies.Problem(StatusCodes.Status400BadRequest, "InvalidIdempotencyKey");
            var result = await hosts.ClaimWaitingAsync(MissionClaims.HostOf(http.User), MissionClaims.HostSecretHashOf(http.User), key, settings.LaunchDeadlineSeconds,
                TimeSpan.FromSeconds(settings.ClaimWaitSeconds), TimeSpan.FromMilliseconds(settings.ClaimPollMs), http.RequestAborted);
            return result switch
            {
                ClaimResult.Claimed c => Results.Json(new ClaimResponse(c.AttemptId.ToString("D"), c.MissionPort, c.Credential.Text, c.CredentialGeneration,
                    c.LaunchDeadline, c.Recovered), Web),
                ClaimResult.NoCandidate => Results.NoContent(),
                ClaimResult.HostUnavailable => MissionBodies.Problem(StatusCodes.Status409Conflict, "HostUnavailable"),
                ClaimResult.InvalidState => MissionBodies.Problem(StatusCodes.Status409Conflict, "InvalidState"),
                _ => UniformUnauthorized.Result,
            };
        });

        // POST /agent/v1/attempts/{attemptId}/launching, body {credentialGeneration} → 200 {state}.
        agent.MapPost("/attempts/{attemptId}/launching", async (string attemptId, HttpContext http, HostStore hosts) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var (body, refusal) = await MissionBodies.ReadAsync(http, LaunchingRequest.Parse);
            if (refusal is not null) return refusal;
            if (!CanonicalUuid.TryParse(attemptId, out var id)) return MissionBodies.Problem(StatusCodes.Status404NotFound, "AttemptNotFound");
            return await hosts.LaunchingAsync(MissionClaims.HostOf(http.User), MissionClaims.HostSecretHashOf(http.User), id, body!.CredentialGeneration, http.RequestAborted) switch
            {
                LaunchingResult.Accepted a => Results.Json(new StateResponse(a.State), Web),
                LaunchingResult.NotFound => MissionBodies.Problem(StatusCodes.Status404NotFound, "AttemptNotFound"),
                LaunchingResult.CredentialSuperseded => MissionBodies.Problem(StatusCodes.Status409Conflict, "CredentialSuperseded"),
                LaunchingResult.Unauthenticated => UniformUnauthorized.Result,
                _ => MissionBodies.Problem(StatusCodes.Status409Conflict, "InvalidState"),
            };
        });

        // POST /agent/v1/attempts/{attemptId}/exited, body {exitCode, signal, results:[{accountId, present}]} → {applied}.
        agent.MapPost("/attempts/{attemptId}/exited", async (string attemptId, HttpContext http, HostStore hosts) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var (body, refusal) = await MissionBodies.ReadAsync(http, ExitedRequest.Parse);
            if (refusal is not null) return refusal;
            if (!CanonicalUuid.TryParse(attemptId, out var id)) return MissionBodies.Problem(StatusCodes.Status404NotFound, "AttemptNotFound");
            return ReportResult(await hosts.ExitedAsync(MissionClaims.HostOf(http.User), MissionClaims.HostSecretHashOf(http.User), id, body!, http.RequestAborted));
        });
    }

    /// <summary>The answer to an exit or failure report, shared with the process route.</summary>
    public static IResult ReportResult(Blackglass.Accounts.Persistence.Attempts.ExitReportResult result) => result switch
    {
        Blackglass.Accounts.Persistence.Attempts.ExitReportResult.Applied a => Results.Json(new AppliedResponse(a.Outcome), Web),
        Blackglass.Accounts.Persistence.Attempts.ExitReportResult.Conflicting => MissionBodies.Problem(StatusCodes.Status409Conflict, "ConflictingReport"),
        Blackglass.Accounts.Persistence.Attempts.ExitReportResult.UnknownParticipant => MissionBodies.Problem(StatusCodes.Status400BadRequest, "UnknownParticipant"),
        Blackglass.Accounts.Persistence.Attempts.ExitReportResult.IncompleteResults => MissionBodies.Problem(StatusCodes.Status400BadRequest, "IncompleteResults"),
        Blackglass.Accounts.Persistence.Attempts.ExitReportResult.WrongSession => MissionBodies.Problem(StatusCodes.Status409Conflict, "WrongSession"),
        Blackglass.Accounts.Persistence.Attempts.ExitReportResult.Ended => UniformUnauthorized.Result,
        _ => MissionBodies.Problem(StatusCodes.Status404NotFound, "AttemptNotFound"),
    };

    public sealed record AppliedResponse(string Applied);

    public sealed record HeartbeatResponse(bool Drained, string[] Stop);

    public sealed record ClaimResponse(string AttemptId, int MissionPort, string ProcessCredential, int CredentialGeneration, DateTime LaunchDeadline, bool Recovered);

    public sealed record StateResponse(string State);
}

/// <summary>Reading agent and process bodies, shared by both route groups.</summary>
public static class MissionBodies
{
    /// <summary>The largest agent or process body read (provisional): a ready report holds at most 256 pool lines.</summary>
    public const int MaxBodyBytes = 64 * 1024;

    public delegate T ParseBody<out T>(ReadOnlySpan<byte> utf8Json);

    /// <summary>The strictly parsed body, or the refusal: 415 unless JSON, 413 over the route's size limit, 400 InvalidBody.</summary>
    public static async Task<(T? Body, IResult? Refusal)> ReadAsync<T>(HttpContext http, ParseBody<T> parse) where T : class
    {
        if (!http.Request.HasJsonContentType()) return (null, Results.StatusCode(StatusCodes.Status415UnsupportedMediaType));
        byte[] body;
        try
        {
            using var buffer = new MemoryStream();
            await http.Request.Body.CopyToAsync(buffer, http.RequestAborted);
            body = buffer.ToArray();
        }
        catch (BadHttpRequestException e) { return (null, Results.StatusCode(e.StatusCode)); }
        try { return (parse(body), null); }
        catch (RequestFormatException) { return (null, Problem(StatusCodes.Status400BadRequest, "InvalidBody")); }
    }

    public static IResult Problem(int status, string code) =>
        Results.Problem(statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code });
}
