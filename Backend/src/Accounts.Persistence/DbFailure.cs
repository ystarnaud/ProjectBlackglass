using Npgsql;

namespace Blackglass.Accounts.Persistence;

public enum DbFailureKind
{
    /// <summary>The database can't be reached or can't serve: 503 (CLI exit 69).</summary>
    Unavailable,
    /// <summary>A lock or statement timeout, a deadlock or a serialization failure: nothing was committed and a retry may
    /// succeed. 503 with Retry-After (CLI exit 75).</summary>
    Busy,
    /// <summary>Any other server error: a bug or a broken schema, never an outage. 500 (CLI exit 1).</summary>
    Unexpected,
}

/// <summary>Opening a connection failed: whatever the server answered (refused, shutting down, not accepting connections,
/// unknown role or database), the database is unavailable to this process.</summary>
public sealed class DatabaseUnavailableException(NpgsqlException inner) : NpgsqlException("The database is unavailable.", inner);

/// <summary>
/// Classifies a database failure for the API's responses and the admin CLI's exit codes. Only an unreachable database is
/// "unavailable"; known business conflicts (a taken handle, input the database refuses) are mapped by the stores before
/// they get here. <see cref="SafeDescription"/> is what may be logged or printed: the SQLSTATE and constraint name, never
/// the server's message text or detail, which can quote values.
/// </summary>
public static class DbFailure
{
    public static DbFailureKind? Classify(Exception e) => e switch
    {
        DatabaseUnavailableException => DbFailureKind.Unavailable,
        PostgresException { InvariantSeverity: "FATAL" or "PANIC" } => DbFailureKind.Unavailable, // the server ended the connection
        PostgresException pg => ClassifySqlState(pg.SqlState),
        NpgsqlException => DbFailureKind.Unavailable, // no server error at all: a broken socket, an I/O or connect timeout, an exhausted pool
        _ => null,
    };

    static DbFailureKind ClassifySqlState(string sqlState) => sqlState switch
    {
        PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.QueryCanceled
            or PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected => DbFailureKind.Busy,
        PostgresErrorCodes.AdminShutdown or PostgresErrorCodes.CrashShutdown or PostgresErrorCodes.CannotConnectNow => DbFailureKind.Unavailable,
        _ when sqlState.StartsWith("08", StringComparison.Ordinal) // connection exception
            || sqlState.StartsWith("53", StringComparison.Ordinal) // insufficient resources
            || sqlState.StartsWith("58", StringComparison.Ordinal) => DbFailureKind.Unavailable, // system error (I/O)
        _ => DbFailureKind.Unexpected,
    };

    public static string SafeDescription(NpgsqlException e) => e switch
    {
        DatabaseUnavailableException { InnerException: NpgsqlException inner } => $"connection failed: {SafeDescription(inner)}",
        PostgresException pg => pg.ConstraintName is null ? $"SQLSTATE {pg.SqlState}" : $"SQLSTATE {pg.SqlState} (constraint {pg.ConstraintName})",
        _ => e.InnerException is null ? e.GetType().Name : $"{e.GetType().Name} ({e.InnerException.GetType().Name})",
    };

    /// <summary>Opens a connection, reporting any failure to do so as <see cref="DatabaseUnavailableException"/>.</summary>
    public static async Task<NpgsqlConnection> OpenAsync(NpgsqlDataSource db, CancellationToken ct)
    {
        try { return await db.OpenConnectionAsync(ct); }
        catch (NpgsqlException e) { throw new DatabaseUnavailableException(e); }
    }
}
