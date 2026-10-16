using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Blackglass.Accounts.Api.Auth;
using Blackglass.Accounts.Domain.Ids;
using Blackglass.Accounts.Persistence.Player;

namespace Blackglass.Accounts.Api.Player;

/// <summary>
/// The player's roster, inventory and advancement picks (Stage 4). Every route needs a session and takes the account from
/// it alone: no path, query, header or body member names one, and an unknown body member is refused. GET routes never
/// write (an unprovisioned account is 409 NotProvisioned). An operative of another account is answered exactly like one
/// that does not exist (404 OperativeNotFound). A database failure is not caught here: it flows to the database failure
/// middleware (503, busy 503 with Retry-After, or 500).
/// </summary>
public static class PlayerEndpoints
{
    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // Strict numbers: "1" is not an integer version. A member the request type lacks, an account id included, is a 400.
    static readonly JsonSerializerOptions RequestOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
    };

    public static void MapPlayer(this WebApplication app)
    {
        app.MapGet("/v1/roster", async (HttpContext http, PlayerReadStore store) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            try { return Json(RosterResponse.From(await store.GetRosterAsync(AccountClaims.AccountOf(http.User), http.RequestAborted))); }
            catch (PlayerException e) { return Refusal(e); }
        }).RequireAuthorization().RequireRateLimiting(RateLimits.PerAccount);

        app.MapGet("/v1/inventory", async (HttpContext http, PlayerReadStore store) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            try { return Json(InventoryResponse.From(await store.GetInventoryAsync(AccountClaims.AccountOf(http.User), http.RequestAborted))); }
            catch (PlayerException e) { return Refusal(e); }
        }).RequireAuthorization().RequireRateLimiting(RateLimits.PerAccount);

        app.MapPost("/v1/operatives/{operativeId}/choices", async (string operativeId, HttpContext http, PickStore picks) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!Base58Id.TryParse(operativeId, out var operative)) return Problem(StatusCodes.Status400BadRequest, "InvalidOperativeId");

            PickChoiceRequest? body;
            try { body = await http.Request.ReadFromJsonAsync<PickChoiceRequest>(RequestOptions, http.RequestAborted); }
            catch (Exception e) when (e is JsonException or InvalidOperationException) { body = null; }
            catch (BadHttpRequestException e) { return Results.StatusCode(e.StatusCode); } // e.g. 413 over MaxBodyBytes
            if (body is not { ChoiceId: { } choiceId, ExpectedVersion: { } expectedVersion }) return Problem(StatusCodes.Status400BadRequest, "InvalidBody");

            try
            {
                var (version, view) = await picks.PickAsync(AccountClaims.AccountOf(http.User), operative, choiceId, expectedVersion, http.RequestAborted);
                return Json(new PickChoiceResponse(version, OperativeDto.From(view)));
            }
            catch (PlayerException e) { return Refusal(e); }
        }).RequireAuthorization().RequireRateLimiting(RateLimits.PerAccount).WithMetadata(new RequestSizeLimitAttribute(AuthEndpoints.MaxBodyBytes));
    }

    static IResult Json<T>(T value) => Results.Json(value, Web);

    static IResult Refusal(PlayerException e) => Problem(e.Error switch
    {
        PlayerError.OperativeNotFound => StatusCodes.Status404NotFound,
        PlayerError.VersionMismatch or PlayerError.NotProvisioned or PlayerError.InventoryLocked => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status422UnprocessableEntity, // NoPendingPick, UnknownChoice
    }, e.Error.ToString());

    static IResult Problem(int status, string code) =>
        Results.Problem(statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code });
}
