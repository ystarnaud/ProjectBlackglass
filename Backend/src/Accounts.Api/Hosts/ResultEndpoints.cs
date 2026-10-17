using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Blackglass.Accounts.Api.Auth;
using Blackglass.Accounts.Domain.Settlement;
using Blackglass.Accounts.Persistence.Settlement;

namespace Blackglass.Accounts.Api.Hosts;

/// <summary>
/// POST /agent/v1/results (plan §C, S9-B19 option (a)): one relayed manifest, with the host credential. The host scheme
/// authenticates before the body is read; the body is read up to the 64 KiB cap (a longer one is kept as its length only, up
/// to <see cref="MaxRequestBytes"/>, past which Kestrel answers 413 and nothing is stored); the strict parse runs before any
/// connection; then one transaction decides. Answers carry the disposition and bounded codes only, never the body, a receipt,
/// an item, an inventory or a payout: 200 settled or duplicate, 202 quarantined or held, 409 and 422 with a code, the uniform
/// 401. A database failure flows to the middleware (503 with Retry-After, or 500); nothing here retries.
/// </summary>
public static class ResultEndpoints
{
    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>The largest request the route reads at all (provisional): a body between the 64 KiB cap and this is stored as
    /// its length only (422 TooLarge).</summary>
    public const int MaxRequestBytes = 4 * 1024 * 1024;

    public static void MapResults(this WebApplication app)
    {
        app.MapPost("/agent/v1/results", async (HttpContext http, SettlementStore settlements) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!http.Request.HasJsonContentType()) return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
            ResultBody body;
            try { body = await ReadAsync(http); }
            catch (BadHttpRequestException e) { return Results.StatusCode(e.StatusCode); }
            var outcome = await settlements.SubmitAsync(MissionClaims.HostOf(http.User), MissionClaims.HostSecretHashOf(http.User), body, http.RequestAborted);
            return outcome switch
            {
                ResultOutcome.Settled s => Results.Json(new Answer("settled", s.Outcome, null), Web),
                ResultOutcome.Duplicate d => Results.Json(new Answer("duplicate", d.Outcome, null), Web),
                ResultOutcome.Quarantined q => Results.Json(new Answer("quarantined", null, [.. q.Codes]), Web, statusCode: StatusCodes.Status202Accepted),
                ResultOutcome.Held => Results.Json(new Answer("held", null, null), Web, statusCode: StatusCodes.Status202Accepted),
                ResultOutcome.Refused r => MissionBodies.Problem(StatusCodes.Status409Conflict, r.Code),
                ResultOutcome.Malformed m => MissionBodies.Problem(StatusCodes.Status422UnprocessableEntity, m.Code),
                _ => UniformUnauthorized.Result,
            };
        }).RequireAuthorization(MissionClaims.HostPolicy).RequireRateLimiting(RateLimits.PerHost).WithMetadata(new RequestSizeLimitAttribute(MaxRequestBytes));
    }

    static async Task<ResultBody> ReadAsync(HttpContext http)
    {
        var buffer = new byte[ResultManifest.MaxBytes + 1];
        var kept = 0;
        long total = 0;
        var scratch = new byte[16 * 1024];
        while (true)
        {
            var read = await http.Request.Body.ReadAsync(scratch, http.RequestAborted);
            if (read == 0) break;
            var copy = Math.Min(read, buffer.Length - kept);
            Array.Copy(scratch, 0, buffer, kept, copy);
            kept += copy;
            total += read;
        }
        return total > ResultManifest.MaxBytes ? ResultBody.Oversized((int)Math.Min(total, int.MaxValue)) : ResultBody.Read(buffer[..kept]);
    }

    /// <summary>The minimal answer (S9-B19 (a)): the disposition, with the outcome for 200 and the V-rule codes for a quarantine.</summary>
    public sealed record Answer(string Disposition, [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Outcome,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string[]? Codes);
}
