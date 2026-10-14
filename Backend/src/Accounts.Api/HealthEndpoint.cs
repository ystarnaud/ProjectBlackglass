using Dapper;
using Npgsql;

namespace Blackglass.Accounts.Api;

public static class HealthEndpoint
{
    public sealed record HealthResponse(string Status, int? SchemaVersion);

    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    /// <summary>GET /health: 200 when the database answers within the timeout, otherwise 503. The body never carries
    /// error details.</summary>
    public static void MapHealth(this WebApplication app, int schemaVersion) =>
        app.MapGet("/health", async (NpgsqlDataSource db, CancellationToken requestAborted) =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
            timeout.CancelAfter(Timeout);
            try
            {
                await using var conn = await db.OpenConnectionAsync(timeout.Token);
                await conn.ExecuteScalarAsync<int>(new CommandDefinition("select 1", cancellationToken: timeout.Token));
                return Results.Json(new HealthResponse("ok", schemaVersion));
            }
            catch (Exception e) when (e is NpgsqlException or OperationCanceledException or TimeoutException)
            {
                return Results.Json(new HealthResponse("unavailable", null), statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
}
