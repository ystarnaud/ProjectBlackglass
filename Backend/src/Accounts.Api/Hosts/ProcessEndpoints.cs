using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Blackglass.Accounts.Api.Auth;
using Blackglass.Accounts.Domain.Attempts;
using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.Domain.Ids;
using Blackglass.Accounts.Persistence.Attempts;

namespace Blackglass.Accounts.Api.Hosts;

/// <summary>
/// The mission process's routes, /server/v1/attempts/{attemptId}/* (spec §16.3; plan revision 2 §C). Each needs the
/// <see cref="MissionClaims.ProcessPolicy"/>: the attempt's current pc1_ credential, presented on that attempt's own route,
/// while the attempt is neither cancelled nor settled (anything else is the uniform 401, R4: there is no exception for the
/// heartbeat; stop instructions travel through the host agent). The attempt comes from the credential; bodies name the
/// process session, never a principal. Strict bodies (415, 413, 400 InvalidBody); a database failure flows to the
/// middleware. Every response is no-store. Rate-limited per attempt.
/// </summary>
public static class ProcessEndpoints
{
    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static void MapProcess(this WebApplication app, Attempts.AttemptSettings settings, GameCatalogue catalogue)
    {
        var process = app.MapGroup("/server/v1/attempts/{attemptId}").RequireAuthorization(MissionClaims.ProcessPolicy).RequireRateLimiting(RateLimits.PerProcess)
            .WithMetadata(new RequestSizeLimitAttribute(MissionBodies.MaxBodyBytes));

        // POST …/sessions, body {buildRevision, catalogueVersion} → the session, the seed (here only), the template, the catalogue,
        // the participants' snapshots and the launch deadline.
        process.MapPost("/sessions", async (HttpContext http, ProcessStore processes) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var (body, refusal) = await MissionBodies.ReadAsync(http, RegisterRequest.Parse);
            if (refusal is not null) return refusal;
            return await processes.RegisterAsync(MissionClaims.AttemptOf(http.User), body!, http.RequestAborted) switch
            {
                RegisterResult.Registered r => Results.Json(new RegisterResponse(r.ProcessSessionId.ToString("D"), r.Seed, new TemplateDto(r.TemplateId, r.TemplateVersion),
                    r.CatalogueVersion, r.Participants.Select(p => new ParticipantDto(Base58Id.Format(p.AccountId), JsonNode.Parse(p.LoadoutJson)!)).ToArray(),
                    new LaunchDeadlineDto(r.LaunchDeadline)), Web),
                RegisterResult.BuildNotAllowed => MissionBodies.Problem(StatusCodes.Status409Conflict, "BuildNotAllowed"),
                _ => MissionBodies.Problem(StatusCodes.Status409Conflict, "InvalidState"),
            };
        });

        // POST …/ready, body {processSessionId, layoutHash, lootTableVersion, lootHash, objectives, lootPool} → {joinDeadline}.
        process.MapPost("/ready", async (HttpContext http, ProcessStore processes) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var (body, refusal) = await MissionBodies.ReadAsync(http, ReadyReport.Parse);
            if (refusal is not null) return refusal;
            return await processes.ReadyAsync(MissionClaims.AttemptOf(http.User), body!, catalogue.Document.LootTable, settings.JoinDeadlineSeconds, http.RequestAborted) switch
            {
                ReadyResult.Ready r => Results.Json(new ReadyResponse(r.JoinDeadline), Web),
                ReadyResult.InvalidLootPool e => Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
                    extensions: new Dictionary<string, object?> { ["code"] = "InvalidLootPool", ["errors"] = e.Errors }),
                ReadyResult.InvalidObjectives e => Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
                    extensions: new Dictionary<string, object?> { ["code"] = "InvalidObjectives", ["errors"] = e.Errors }),
                ReadyResult.WrongSession => MissionBodies.Problem(StatusCodes.Status409Conflict, "WrongSession"),
                _ => MissionBodies.Problem(StatusCodes.Status409Conflict, "InvalidState"),
            };
        });

        // POST …/heartbeat, body {processSessionId, phase, participants:[{accountId, connected}]} → {}.
        process.MapPost("/heartbeat", async (HttpContext http, ProcessStore processes) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var (body, refusal) = await MissionBodies.ReadAsync(http, ProcessHeartbeatRequest.Parse);
            if (refusal is not null) return refusal;
            return await processes.HeartbeatAsync(MissionClaims.AttemptOf(http.User), body!, http.RequestAborted) switch
            {
                ProcessHeartbeatResult.Ok => Results.Text("{}", "application/json"),
                ProcessHeartbeatResult.UnknownParticipant => MissionBodies.Problem(StatusCodes.Status400BadRequest, "UnknownParticipant"),
                _ => MissionBodies.Problem(StatusCodes.Status409Conflict, "WrongSession"),
            };
        });

        // POST …/failed, body {processSessionId, reason} → {applied: "cancelled" | "none"} (B11 revised: evidence after a start).
        process.MapPost("/failed", async (HttpContext http, ProcessStore processes) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var (body, refusal) = await MissionBodies.ReadAsync(http, FailedRequest.Parse);
            if (refusal is not null) return refusal;
            return AgentEndpoints.ReportResult(await processes.FailedAsync(MissionClaims.AttemptOf(http.User), body!, http.RequestAborted));
        });
    }

    public sealed record TemplateDto(string Id, int Version);
    public sealed record ParticipantDto(string AccountId, JsonNode Loadout);
    public sealed record LaunchDeadlineDto(DateTime Launch);
    public sealed record RegisterResponse(string ProcessSessionId, int Seed, TemplateDto MissionTemplate, string CatalogueVersion, ParticipantDto[] Participants,
        LaunchDeadlineDto Deadlines);
    public sealed record ReadyResponse(DateTime JoinDeadline);
}
