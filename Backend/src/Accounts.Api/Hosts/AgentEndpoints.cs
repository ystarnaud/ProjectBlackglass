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

    public static void MapAgent(this WebApplication app)
    {
        var agent = app.MapGroup("/agent/v1").RequireAuthorization(MissionClaims.HostPolicy).RequireRateLimiting(RateLimits.PerHost)
            .WithMetadata(new RequestSizeLimitAttribute(MissionBodies.MaxBodyBytes));

        // POST /agent/v1/heartbeat: {buildRevision, catalogueVersion, inventory:{complete, units}, outboxPending} → {drained, stop}.
        agent.MapPost("/heartbeat", async (HttpContext http, HostStore hosts) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var (report, refusal) = await MissionBodies.ReadAsync(http, HeartbeatRequest.Parse);
            if (refusal is not null) return refusal;
            var result = await hosts.HeartbeatAsync(MissionClaims.HostOf(http.User), report!, http.RequestAborted);
            if (result is null) return UniformUnauthorized.Result; // disabled since authentication
            return Results.Json(new HeartbeatResponse(result.Drained, result.Stop.Select(a => a.ToString("D")).ToArray()), Web);
        });
    }

    public sealed record HeartbeatResponse(bool Drained, string[] Stop);
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
