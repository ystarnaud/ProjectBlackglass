namespace Blackglass.Accounts.Persistence.Insurance;

/// <summary>Why an operator insurance verb refused, with nothing written.</summary>
public enum InsuranceError
{
    /// <summary>A policy id outside the definition id form (lower-case letters and digits in groups joined by '.' or '-', at most 64).</summary>
    InvalidPolicyId,
    /// <summary>Terms that <see cref="Blackglass.Accounts.Domain.Insurance.InsuranceTerms.Parse"/> refuses, or a terms file that can't be read.</summary>
    InvalidTerms,
    /// <summary>A duration outside 1 to <see cref="InsuranceAdminStore.MaxDurationSeconds"/> seconds.</summary>
    InvalidDuration,
    /// <summary>No version of that policy was ever published.</summary>
    UnknownPolicy,
    /// <summary>The account already used that activation key with another policy or duration.</summary>
    IdempotencyKeyReused,
    /// <summary>The account's coverage of another policy is still active (S7-D3: switching tiers while active is refused).</summary>
    CrossTierDeferred,
    /// <summary>The next policy version was taken by a concurrent insert that bypassed the publication lock: reported, never retried.</summary>
    PolicyVersionConflict,
}

public sealed class InsuranceException(InsuranceError error, string message) : Exception(message)
{
    public InsuranceError Error { get; } = error;
}
