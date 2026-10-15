using Npgsql;
using Blackglass.Accounts.Persistence;

namespace Blackglass.Accounts.IntegrationTests.Unit;

/// <summary>Only an unreachable database is "unavailable"; a lock or statement timeout is "busy" (retry soon); every
/// other server error is "unexpected", never an outage; a non-database exception is not this classifier's.</summary>
public class DbFailureTests
{
    static PostgresException Pg(string sqlState) => new("message text", "ERROR", "ERROR", sqlState);

    [Fact]
    public void A_failure_to_open_a_connection_is_unavailable_whatever_the_server_said() =>
        Assert.Equal(DbFailureKind.Unavailable, DbFailure.Classify(new DatabaseUnavailableException(Pg("55000")))); // e.g. allow_connections off

    [Fact]
    public void A_broken_connection_with_no_server_error_is_unavailable()
    {
        Assert.Equal(DbFailureKind.Unavailable, DbFailure.Classify(new NpgsqlException("read failed", new IOException())));
        Assert.Equal(DbFailureKind.Unavailable, DbFailure.Classify(new NpgsqlException("timeout", new TimeoutException())));
    }

    [Theory]
    [InlineData("08006")] // connection failure
    [InlineData("08001")]
    [InlineData("57P01")] // admin shutdown (a terminated backend)
    [InlineData("57P02")] // crash shutdown
    [InlineData("57P03")] // cannot connect now
    [InlineData("53300")] // too many connections
    [InlineData("53100")] // disk full
    [InlineData("58030")] // I/O error
    public void Server_errors_meaning_the_database_cannot_serve_are_unavailable(string sqlState) =>
        Assert.Equal(DbFailureKind.Unavailable, DbFailure.Classify(Pg(sqlState)));

    [Theory]
    [InlineData("57P04", "FATAL")] // database dropped
    [InlineData("57P05", "FATAL")] // idle_session_timeout
    [InlineData("25P03", "FATAL")] // idle_in_transaction_session_timeout
    [InlineData("XX000", "PANIC")]
    public void A_fatal_server_error_ends_the_connection_and_is_unavailable(string sqlState, string severity) =>
        Assert.Equal(DbFailureKind.Unavailable, DbFailure.Classify(new PostgresException("m", severity, severity, sqlState)));

    [Theory]
    [InlineData("55P03")] // lock_timeout
    [InlineData("57014")] // statement_timeout (or a cancelled request)
    [InlineData("40001")] // serialization failure
    [InlineData("40P01")] // deadlock
    public void Lock_and_statement_timeouts_and_conflicts_that_a_retry_resolves_are_busy(string sqlState) =>
        Assert.Equal(DbFailureKind.Busy, DbFailure.Classify(Pg(sqlState)));

    [Theory]
    [InlineData("23505")] // unique violation
    [InlineData("23514")] // check violation (e.g. revocation_is_final)
    [InlineData("23503")] // foreign key
    [InlineData("42501")] // insufficient privilege
    [InlineData("42P01")] // undefined table
    [InlineData("22P02")] // invalid input
    [InlineData("P0001")] // raised by a function
    [InlineData("55000")] // object not in prerequisite state, during a query
    [InlineData("XX000")] // internal error
    public void Every_other_server_error_is_unexpected_never_an_outage(string sqlState) =>
        Assert.Equal(DbFailureKind.Unexpected, DbFailure.Classify(Pg(sqlState)));

    [Fact]
    public void A_non_database_exception_is_not_classified()
    {
        Assert.Null(DbFailure.Classify(new InvalidOperationException()));
        Assert.Null(DbFailure.Classify(new OperationCanceledException()));
    }

    [Fact]
    public void The_safe_description_names_the_sqlstate_and_constraint_but_never_the_message()
    {
        var e = new PostgresException("value \"secret-looking\" is bad", "ERROR", "ERROR", "23514", constraintName: "some_check");
        var text = DbFailure.SafeDescription(e);
        Assert.Contains("23514", text);
        Assert.Contains("some_check", text);
        Assert.DoesNotContain("secret-looking", text);
        Assert.DoesNotContain("read failed", DbFailure.SafeDescription(new NpgsqlException("read failed", new IOException())));
    }
}
