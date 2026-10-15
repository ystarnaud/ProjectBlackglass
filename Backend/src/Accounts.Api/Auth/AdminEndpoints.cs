using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Blackglass.Accounts.Domain.Accounts;
using Blackglass.Accounts.Persistence.Accounts;

namespace Blackglass.Accounts.Api.Auth;

/// <summary>
/// /dev/v1/* operator routes (spec §16.4): mapped only when BLACKGLASS_ADMIN_ROUTES=enabled; one shared rate window,
/// applied first; then exactly one X-Admin-Key header, compared in constant time on SHA-256, before the body is read. A
/// player's bearer token plays no part. "Loopback only" is the API's own publication on 127.0.0.1: inside the container
/// every request arrives from the Docker bridge gateway, so an in-app address check would prove nothing.
/// </summary>
public static class AdminEndpoints
{
    public const string KeyHeader = "X-Admin-Key";
    public const string CorrelationHeader = "X-Correlation-Id";

    public sealed record CreateAccountRequest(string? Handle, string? DisplayName, string? Operator, string? Reason);

    public sealed record CreateAccountResponse([property: JsonConverter(typeof(AccountIdJsonConverter))] long AccountId, Guid CredentialId, string AccountKey);

    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static void MapAdmin(this WebApplication app, AuthSettings settings)
    {
        if (!settings.AdminRoutesEnabled) return;
        var admin = app.MapGroup("/dev/v1").RequireRateLimiting(RateLimits.Admin);
        admin.MapPost("/accounts", async (HttpContext http, AccountAdminStore store, ILoggerFactory logs) =>
        {
            var log = logs.CreateLogger("Blackglass.Admin");
            var correlationId = Guid.CreateVersion7();
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers[CorrelationHeader] = correlationId.ToString();
            if (!KeyMatches(http.Request.Headers[KeyHeader], settings.AdminKeyHash!))
            {
                log.LogWarning("Admin request refused: missing or wrong admin key ({Path}, correlation {CorrelationId})", http.Request.Path, correlationId);
                return UniformUnauthorized.Result;
            }

            CreateAccountRequest? body;
            try { body = await http.Request.ReadFromJsonAsync<CreateAccountRequest>(Web, http.RequestAborted); }
            catch (Exception e) when (e is JsonException or InvalidOperationException) { body = null; }
            if (body is null) return Problem(400, "InvalidBody");

            OperatorContext op;
            try { op = OperatorContext.Create(body.Operator, body.Reason) with { CorrelationId = correlationId }; }
            catch (ArgumentException) { return Problem(422, "InvalidOperator"); }
            try
            {
                var issued = await store.CreateAccountAsync(body.Handle, body.DisplayName, op, http.RequestAborted);
                log.LogInformation("Admin created account {AccountId} (operator {Operator}, correlation {CorrelationId})",
                    AccountId.Format(issued.AccountId), op.Operator, correlationId);
                return Results.Json(new CreateAccountResponse(issued.AccountId, issued.CredentialId, issued.AccountKey.Text), Web, statusCode: StatusCodes.Status201Created);
            }
            catch (AccountAdminException e)
            {
                log.LogWarning("Admin request refused: {Error} (correlation {CorrelationId})", e.Error, correlationId);
                return e.Error == AccountAdminError.HandleTaken ? Problem(409, "HandleTaken") : Problem(422, e.Error.ToString());
            }
        });
    }

    static bool KeyMatches(Microsoft.Extensions.Primitives.StringValues presented, byte[] expectedHash) =>
        presented.Count == 1 && presented[0] is { } key
        && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(key)), expectedHash);

    static IResult Problem(int status, string code) =>
        Results.Problem(statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code });
}
