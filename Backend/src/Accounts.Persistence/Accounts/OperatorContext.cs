namespace Blackglass.Accounts.Persistence.Accounts;

/// <summary>Who performed an operator action, why, and the correlation id that ties its audit row to its log lines.
/// Recorded in operator_actions with every change.</summary>
public sealed record OperatorContext(string Operator, string Reason, Guid CorrelationId)
{
    public const int MaxOperatorLength = 200;
    public const int MaxReasonLength = 1000;

    /// <summary>Refuses control characters too: both values reach audit rows and log lines, where a line break could
    /// forge a log line and a NUL is refused by PostgreSQL.</summary>
    public static OperatorContext Create(string? op, string? reason)
    {
        if (op is not null && op.Any(char.IsControl)) throw new ArgumentException("The operator name contains a control character.", nameof(op));
        if (reason is not null && reason.Any(char.IsControl)) throw new ArgumentException("The reason contains a control character.", nameof(reason));
        if (string.IsNullOrWhiteSpace(op) || op.Trim().Length > MaxOperatorLength)
            throw new ArgumentException($"An operator name (1 to {MaxOperatorLength} characters) is required.", nameof(op));
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > MaxReasonLength)
            throw new ArgumentException($"A reason (1 to {MaxReasonLength} characters) is required.", nameof(reason));
        return new OperatorContext(op.Trim(), reason.Trim(), Guid.CreateVersion7());
    }
}
