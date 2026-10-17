using Dapper;
using Npgsql;
using Blackglass.Accounts.Domain.Attempts;

namespace Blackglass.Accounts.Persistence.Attempts;

/// <summary>An exit or failure report's outcome (B11 revised, R6).</summary>
public abstract record ExitReportResult
{
    /// <summary>Stored (now, or by an identical earlier report): "cancelled" before any start, "none" after one.</summary>
    public sealed record Applied(string Outcome) : ExitReportResult;
    /// <summary>A different report of the same source is already stored: nothing written, the evidence kept.</summary>
    public sealed record Conflicting : ExitReportResult;
    /// <summary>Not this host's attempt, or no such attempt.</summary>
    public sealed record NotFound : ExitReportResult;
    /// <summary>A result names an account that is not a participant of the attempt.</summary>
    public sealed record UnknownParticipant : ExitReportResult;
    /// <summary>The body names another process session than the attempt's.</summary>
    public sealed record WrongSession : ExitReportResult;
}

/// <summary>Reading and comparing stored exit evidence (<c>attempt_exit_reports</c>, <c>attempt_exit_results</c>).</summary>
internal static class ExitReports
{
    internal sealed record StoredAgent(int? ExitCode, string? Signal, string Outcome, IReadOnlySet<(long, bool)> Results);

    internal static async Task<StoredAgent?> StoredAgentAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid attemptId, CancellationToken ct)
    {
        var row = await conn.QuerySingleOrDefaultAsync<(int? ExitCode, string? Signal, string Outcome)?>(new CommandDefinition(
            "select exit_code, signal, outcome from attempt_exit_reports where attempt_id = @attemptId and source = 'agent'", new { attemptId }, tx, cancellationToken: ct));
        if (row is not { } r) return null;
        var results = (await conn.QueryAsync<(long, bool)>(new CommandDefinition(
            "select account_id, present from attempt_exit_results where attempt_id = @attemptId", new { attemptId }, tx, cancellationToken: ct))).ToHashSet();
        return new StoredAgent(r.ExitCode, r.Signal, r.Outcome, results);
    }

    internal static bool Same(StoredAgent stored, ExitedRequest report) =>
        stored.ExitCode == report.ExitCode && stored.Signal == report.Signal
        && stored.Results.SetEquals(report.Results.Select(r => (r.AccountId, r.Present)));
}
