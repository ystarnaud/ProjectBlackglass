using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Blackglass.Accounts.Api.Auth;
using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.Domain.Ids;
using Blackglass.Accounts.Domain.Inventory;
using Blackglass.Accounts.Persistence.Player;

namespace Blackglass.Accounts.Api.Player;

/// <summary>
/// The player's roster, inventory, inventory edits and advancement picks (Stages 4 and 5). Every route needs a session and
/// takes the account from it alone: no path, query, header or body member names one, and an unknown body member is refused.
/// GET routes never write (an unprovisioned account is 409 NotProvisioned). An operative or item of another account is
/// answered exactly like one that does not exist (404 OperativeNotFound for a pick; 422 UnknownOperative or NotFound for an
/// edit). A database failure is not caught here: it flows to the database failure middleware (503, busy 503 with
/// Retry-After, or 500). Nothing here retries a write (owner ruling S5-D6).
/// </summary>
public static class PlayerEndpoints
{
    /// <summary>The largest body POST /v1/inventory/operations reads (owner ruling S5-D6, provisional): 16 KiB.</summary>
    public const int InventoryMaxBodyBytes = 16 * 1024;

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
            if (!http.Request.HasJsonContentType()) return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType); // as login does

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

        // One atomic, version-checked batch (S5-D1, S5-D6): 200 {version, inventory}; 400 for a malformed request, with the
        // operation's index when one operation is malformed; 409 for a stale version, an unprovisioned account or a locked
        // inventory; 422 {code, index} when the rules refuse an operation. The store has refused or applied the whole batch.
        app.MapPost("/v1/inventory/operations", async (HttpContext http, InventoryEditStore edits, AuthSettings settings, ILoggerFactory logs) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!http.Request.HasJsonContentType()) return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType); // as login does

            InventoryOperationsRequest? body;
            try { body = await http.Request.ReadFromJsonAsync<InventoryOperationsRequest>(RequestOptions, http.RequestAborted); }
            catch (Exception e) when (e is JsonException or InvalidOperationException) { body = null; }
            catch (BadHttpRequestException e) { return Results.StatusCode(e.StatusCode); } // 413 over InventoryMaxBodyBytes
            if (body is not { ExpectedVersion: { } expectedVersion, Operations: { } requested } || requested.Count == 0)
                return Problem(StatusCodes.Status400BadRequest, "InvalidBody"); // an empty batch is never a state change
            if (requested.Count > settings.InventoryMaxOperations) return Problem(StatusCodes.Status400BadRequest, "TooManyOperations");

            var operations = new List<InventoryOperation>(requested.Count);
            for (var index = 0; index < requested.Count; index++)
            {
                if (ParseOperation(requested[index]) is not { } operation) return Problem(StatusCodes.Status400BadRequest, "InvalidOperation", index);
                operations.Add(operation);
            }

            try
            {
                var (version, view) = await edits.ApplyAsync(AccountClaims.AccountOf(http.User), expectedVersion, operations, http.RequestAborted);
                return Json(new InventoryOperationsResponse(version, InventoryResponse.From(view)));
            }
            catch (PlayerException e) { return Refusal(e); }
            catch (InventoryRefusedException e) { return Problem(StatusCodes.Status422UnprocessableEntity, e.Code.ToString(), e.Index); }
            catch (InventoryInvariantException e)
            {
                // A bug or corruption, never a client error: the store rolled the batch back. The client learns nothing about
                // it; the log gets the message (only ids, catalogue names and counts, never a token) and no request data.
                logs.CreateLogger("Blackglass.Inventory").LogError(e, "Inventory invariant failure for {Path}", http.Request.Path);
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }
        }).RequireAuthorization().RequireRateLimiting(RateLimits.PerAccount).WithMetadata(new RequestSizeLimitAttribute(InventoryMaxBodyBytes));
    }

    static IResult Json<T>(T value) => Results.Json(value, Web);

    /// <summary>
    /// The status of every <see cref="PlayerError"/>, on every route (owner ruling S5-D5: InventoryLocked is 409 everywhere).
    /// A switch with no default arm, so a new member is a compile error until it is mapped here.
    /// </summary>
    // CS8524 (an undefined numeric value) is silenced on purpose: it throws SwitchExpressionException, never a silent status.
    // CS8509 (a named member left out) stays an error, which is the point of having no default arm.
#pragma warning disable CS8524
    public static int StatusFor(PlayerError error) => error switch
    {
        PlayerError.InventoryLocked => StatusCodes.Status409Conflict,
        PlayerError.VersionMismatch => StatusCodes.Status409Conflict,
        PlayerError.NotProvisioned => StatusCodes.Status409Conflict,
        PlayerError.OperativeNotFound => StatusCodes.Status404NotFound,
        PlayerError.NoPendingPick => StatusCodes.Status422UnprocessableEntity,
        PlayerError.UnknownChoice => StatusCodes.Status422UnprocessableEntity,
    };
#pragma warning restore CS8524

    static IResult Refusal(PlayerException e) => Problem(StatusFor(e.Error), e.Error.ToString());

    /// <summary>
    /// The domain operation for a wire operation, or null when it has the wrong members for its verb or an id that is not the
    /// canonical Base58 spelling. Each verb takes exactly its members; a stack quantity is the rules' to judge (InvalidQuantity).
    /// </summary>
    static InventoryOperation? ParseOperation(OperationDto? dto)
    {
        if (dto is null || !Base58Id.TryParse(dto.OperativeId, out var operative)) return null;
        switch (dto.Op)
        {
            case "equip" or "equipFromStash":
                if (dto.Slot is not null || dto.DefinitionId is not null || dto.Quantity is not null || !Base58Id.TryParse(dto.ItemId, out var item)) return null;
                return dto.Op == "equip" ? new Equip(operative, item) : new EquipFromStash(operative, item);
            case "unequip":
                if (dto.ItemId is not null || dto.DefinitionId is not null || dto.Quantity is not null) return null;
                return dto.Slot switch
                {
                    "weapon" => new Unequip(operative, ItemSlot.Weapon),
                    "armor" => new Unequip(operative, ItemSlot.Armor),
                    "utility" => new Unequip(operative, ItemSlot.Utility),
                    _ => null,
                };
            case "toStash" or "toBag":
                if (dto.Slot is not null) return null;
                var to = dto.Op == "toBag" ? Destination.Bag : Destination.Stash;
                if (dto.ItemId is not null)
                    return dto.DefinitionId is null && dto.Quantity is null && Base58Id.TryParse(dto.ItemId, out var instance) ? new MoveInstance(operative, instance, to) : null;
                return dto.DefinitionId is { Length: > 0 } definition && dto.Quantity is { } quantity ? new MoveStack(operative, definition, quantity, to) : null;
            default:
                return null;
        }
    }

    static IResult Problem(int status, string code, int? index = null)
    {
        var extensions = new Dictionary<string, object?> { ["code"] = code };
        if (index is { } at) extensions["index"] = at;
        return Results.Problem(statusCode: status, extensions: extensions);
    }
}
