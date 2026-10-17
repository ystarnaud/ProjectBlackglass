using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;
using Blackglass.Accounts.Api.Auth;
using Blackglass.Accounts.Domain.Attempts;
using Blackglass.Accounts.Persistence.Attempts;

namespace Blackglass.Accounts.Api.Attempts;

/// <summary>
/// POST /v1/attempts (Stage 7 Task 6; spec §7.3, §16.1): admission. Needs a session and takes the account from it alone; the
/// request names no account, attempt or participant. Before any connection, in this order: 415 unless the body is JSON, 413
/// over <see cref="AttemptSettings.DeployMaxBodyBytes"/> (read before parsing), 400 InvalidIdempotencyKey unless the
/// <c>Idempotency-Key</c> header is exactly one canonical UUID, and 400 with the domain's code for a body
/// <see cref="DeployRequest.Parse"/> refuses (InvalidBody, DuplicateOperative, InvalidOperativeId). Then the store answers 201
/// with the stored body (byte-identical on a replay) or a refusal as ProblemDetails <c>{code[, index]}</c> through
/// <see cref="StatusFor"/>; 503 ServiceUnavailable carries Retry-After. A database failure is not caught here: it flows to the
/// database failure middleware (503, busy 503 with Retry-After, or 500). Nothing here retries. The route has its own
/// per-account rate limit (<see cref="RateLimits.Deploy"/>).
/// <para>
/// GET /v1/attempts/{attemptId} and POST /v1/attempts/{attemptId}/cancel (Task 7; S7-D6, S7-D7): the path id is the canonical
/// lower-case UUID; any other spelling, an unknown attempt and another account's attempt are the same 404
/// <c>AttemptNotFound</c>. The read answers <see cref="AttemptResponse"/> (no join deadline until Stage 8; never the seed or
/// the salt). The cancel answers 200 <see cref="CancelResponse"/> for a release and for a repeat (the stored outcome), and 409
/// InvalidState after start. Both use the general per-account rate limit.
/// </para>
/// </summary>
public static class AttemptEndpoints
{
    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public const string IdempotencyKeyHeader = "Idempotency-Key";

    /// <summary>Retry-After on 503 ServiceUnavailable (no healthy capacity), in seconds; provisional.</summary>
    public const string ServiceUnavailableRetryAfterSeconds = "5";

    public static void MapAttempts(this WebApplication app, AttemptSettings settings)
    {
        app.MapPost("/v1/attempts", async (HttpContext http, AdmissionStore admissions) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!http.Request.HasJsonContentType()) return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType); // as login does

            byte[]? body;
            try { body = await ReadCappedAsync(http.Request, settings.DeployMaxBodyBytes, http.RequestAborted); }
            catch (BadHttpRequestException e) { return Results.StatusCode(e.StatusCode); } // 413 over the request size limit
            if (body is null) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            if (!TryParseKey(http.Request.Headers[IdempotencyKeyHeader], out var key)) return Problem(StatusCodes.Status400BadRequest, "InvalidIdempotencyKey");
            DeployRequest request;
            try { request = DeployRequest.Parse(body); }
            catch (DeployRequestException e) { return Problem(StatusCodes.Status400BadRequest, e.Error.ToString()); }

            try
            {
                var admission = await admissions.AdmitAsync(AccountClaims.AccountOf(http.User), key, request, http.RequestAborted);
                return Results.Text(admission.Body, "application/json", Encoding.UTF8, StatusCodes.Status201Created);
            }
            catch (AdmissionException e) { return Refusal(http, e); }
        }).RequireAuthorization().RequireRateLimiting(RateLimits.Deploy).WithMetadata(new RequestSizeLimitAttribute(settings.DeployMaxBodyBytes));

        app.MapGet("/v1/attempts/{attemptId}", async (string attemptId, HttpContext http, AttemptStore attempts) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!TryParseAttemptId(attemptId, out var id)) return NotFound();
            var view = await attempts.GetAsync(AccountClaims.AccountOf(http.User), id, http.RequestAborted);
            return view is null ? NotFound() : Results.Json(AttemptResponse.From(view), Web);
        }).RequireAuthorization().RequireRateLimiting(RateLimits.PerAccount);

        // POST /v1/attempts/{attemptId}/tickets, body {} -> 201 {ticket, ticketExpiresAt} (B7: only a POST mints; earlier live
        // tickets of this participant are revoked). The ticket is shown here once (no-store).
        app.MapPost("/v1/attempts/{attemptId}/tickets", async (string attemptId, HttpContext http, TicketStore tickets) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var (_, refusal) = await Hosts.MissionBodies.ReadAsync(http, b => { EmptyRequest.Parse(b); return string.Empty; });
            if (refusal is not null) return refusal;
            if (!TryParseAttemptId(attemptId, out var id)) return NotFound();
            return await tickets.MintAsync(AccountClaims.AccountOf(http.User), id, settings.TicketSeconds, http.RequestAborted) switch
            {
                MintResult.Minted m => Results.Json(new TicketResponse(m.Ticket.Text, m.ExpiresAt), Web, statusCode: StatusCodes.Status201Created),
                MintResult.NotFound => NotFound(),
                _ => Problem(StatusCodes.Status409Conflict, "InvalidState"),
            };
        }).RequireAuthorization().RequireRateLimiting(RateLimits.PerAccount).WithMetadata(new RequestSizeLimitAttribute(1024));

        app.MapPost("/v1/attempts/{attemptId}/cancel", async (string attemptId, HttpContext http, AttemptStore attempts) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!TryParseAttemptId(attemptId, out var id)) return NotFound();
            try
            {
                var outcome = await attempts.CancelAsync(AccountClaims.AccountOf(http.User), id, http.RequestAborted);
                return Results.Json(new CancelResponse(outcome.ParticipantState, new CancelEnergyDto(outcome.Released, outcome.BalanceAfter)), Web);
            }
            catch (AttemptException e) { return Problem(StatusFor(e.Error), e.Error == AttemptError.NotFound ? AttemptNotFoundCode : e.Error.ToString()); }
        }).RequireAuthorization().RequireRateLimiting(RateLimits.PerAccount);
    }

    /// <summary>The code of the one 404 the attempt routes answer, for an unknown attempt, another account's or a malformed id.</summary>
    public const string AttemptNotFoundCode = "AttemptNotFound";

    /// <summary>The status of every <see cref="AttemptError"/>; no default arm, so a new member is a compile error until mapped.</summary>
#pragma warning disable CS8524
    public static int StatusFor(AttemptError error) => error switch
    {
        AttemptError.NotFound => StatusCodes.Status404NotFound,
        AttemptError.InvalidState => StatusCodes.Status409Conflict,
    };
#pragma warning restore CS8524

    static IResult NotFound() => Problem(StatusCodes.Status404NotFound, AttemptNotFoundCode);

    /// <summary>Exactly the canonical lower-case 8-4-4-4-12 spelling of a UUID, as the attempt id is answered.</summary>
    static bool TryParseAttemptId(string text, out Guid id) =>
        Guid.TryParseExact(text, "D", out id) && id.ToString("D") == text;

    /// <summary>
    /// The status of every <see cref="AdmissionError"/>. A switch with no default arm, so a new member is a compile error until
    /// it is mapped here. UnknownAccount (the session's account row missing, which never happens) is the uniform 401.
    /// </summary>
    // CS8524 (an undefined numeric value) is silenced on purpose: it throws SwitchExpressionException, never a silent status.
#pragma warning disable CS8524
    public static int StatusFor(AdmissionError error) => error switch
    {
        AdmissionError.UnknownAccount => StatusCodes.Status401Unauthorized,
        AdmissionError.NotProvisioned => StatusCodes.Status409Conflict,
        AdmissionError.IdempotencyKeyReused => StatusCodes.Status409Conflict,
        AdmissionError.AttemptActive => StatusCodes.Status409Conflict,
        AdmissionError.InsufficientEnergy => StatusCodes.Status409Conflict,
        AdmissionError.ServiceUnavailable => StatusCodes.Status503ServiceUnavailable,
        AdmissionError.UnknownTemplate => StatusCodes.Status422UnprocessableEntity,
        AdmissionError.UnknownOperative => StatusCodes.Status422UnprocessableEntity,
        AdmissionError.OperativeCount => StatusCodes.Status422UnprocessableEntity,
        AdmissionError.RetiredOperative => StatusCodes.Status422UnprocessableEntity,
        AdmissionError.UnknownItem => StatusCodes.Status422UnprocessableEntity,
    };
#pragma warning restore CS8524

    static IResult Refusal(HttpContext http, AdmissionException e)
    {
        var status = StatusFor(e.Error);
        if (status == StatusCodes.Status401Unauthorized) return UniformUnauthorized.Result;
        if (e.Error == AdmissionError.ServiceUnavailable) http.Response.Headers.RetryAfter = ServiceUnavailableRetryAfterSeconds;
        return Problem(status, e.Error.ToString(), e.Index);
    }

    /// <summary>Exactly one header value, the canonical lower-case 8-4-4-4-12 spelling of a UUID.</summary>
    static bool TryParseKey(StringValues values, out Guid key)
    {
        key = default;
        return values.Count == 1 && values[0] is { Length: 36 } text && Guid.TryParseExact(text, "D", out key) && key.ToString("D") == text;
    }

    /// <summary>The whole body, or null when it is longer than <paramref name="limit"/> bytes. The route's request size limit
    /// (the same number) normally refuses first: Kestrel throws a 413 BadHttpRequestException on the read and closes the
    /// connection cleanly, which an early return on Content-Length would not (the unread body would break keep-alive).</summary>
    static async Task<byte[]?> ReadCappedAsync(HttpRequest request, int limit, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > limit) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    static IResult Problem(int status, string code, int? index = null)
    {
        var extensions = new Dictionary<string, object?> { ["code"] = code };
        if (index is { } at) extensions["index"] = at;
        return Results.Problem(statusCode: status, extensions: extensions);
    }
}
