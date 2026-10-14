namespace Blackglass.Accounts.Domain.Catalogue;

/// <summary>An invalid catalogue: every problem found, each starting with missing:, duplicate:, malformed: or incompatible:.</summary>
public sealed class CatalogueException(IReadOnlyList<string> errors)
    : Exception("Invalid catalogue:\n" + string.Join("\n", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
