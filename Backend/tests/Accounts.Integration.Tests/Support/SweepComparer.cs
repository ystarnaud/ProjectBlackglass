using Blackglass.Accounts.Persistence.Attempts;

namespace Blackglass.Accounts.IntegrationTests.Support;

/// <summary>Compares sweep reports by their counts and failure descriptions.</summary>
internal sealed class SweepComparer : IEqualityComparer<SweepReport>
{
    public static readonly SweepComparer Instance = new();
    public bool Equals(SweepReport? x, SweepReport? y) =>
        x is not null && y is not null && (x.Due, x.Released, x.AlreadyEnded, x.NotAdmitted) == (y.Due, y.Released, y.AlreadyEnded, y.NotAdmitted)
        && x.Failures.SequenceEqual(y.Failures);
    public int GetHashCode(SweepReport obj) => obj.Due;
}
