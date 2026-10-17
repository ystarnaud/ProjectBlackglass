using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Blackglass.Accounts.Api.Auth;
using Blackglass.Accounts.Domain.Attempts;
using Blackglass.Accounts.Persistence.Attempts;

namespace Blackglass.Accounts.Api.Hosts;

/// <summary>
/// The mission process's routes, /server/v1/attempts/{attemptId}/* (spec §16.3; plan revision 2 §C). Each needs the
/// <see cref="MissionClaims.ProcessPolicy"/>: the attempt's current pc1_ credential, presented on that attempt's own route,
/// while the attempt is neither cancelled nor settled (anything else is the uniform 401, R4). The attempt comes from the
/// credential; bodies name the process session, never a principal. Strict bodies (415, 413, 400 InvalidBody); a database
/// failure flows to the middleware. Rate-limited per attempt.
/// </summary>
public static class ProcessEndpoints
{
    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static void MapProcess(this WebApplication app)
    {
        var process = app.MapGroup("/server/v1/attempts/{attemptId}").RequireAuthorization(MissionClaims.ProcessPolicy).RequireRateLimiting(RateLimits.PerProcess)
            .WithMetadata(new RequestSizeLimitAttribute(MissionBodies.MaxBodyBytes));

        // POST …/failed, body {processSessionId, reason} → {applied: "cancelled" | "none"} (B11 revised: evidence after a start).
        process.MapPost("/failed", async (HttpContext http, ProcessStore processes) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var (body, refusal) = await MissionBodies.ReadAsync(http, FailedRequest.Parse);
            if (refusal is not null) return refusal;
            return AgentEndpoints.ReportResult(await processes.FailedAsync(MissionClaims.AttemptOf(http.User), body!, http.RequestAborted));
        });
    }
}
