namespace Blackglass.Accounts.Persistence.Accounts;

/// <summary>Who performed an operator action, why, and the correlation id that ties its audit row to its log lines.
/// Recorded in operator_actions with every change.</summary>
public sealed record OperatorContext(string Operator, string Reason, Guid CorrelationId)
{
    public const int MaxOperatorLength = 200;
    public const int MaxReasonLength = 1000;

    public static OperatorContext Create(string? op, string? reason)
    {
        if (string.IsNullOrWhiteSpace(op) || op.Trim().Length > MaxOperatorLength)
            throw new ArgumentException($"An operator name (1 to {MaxOperatorLength} characters) is required.", nameof(op));
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > MaxReasonLength)
            throw new ArgumentException($"A reason (1 to {MaxReasonLength} characters) is required.", nameof(reason));
        return new OperatorContext(op.Trim(), reason.Trim(), Guid.CreateVersion7());
    }
}
