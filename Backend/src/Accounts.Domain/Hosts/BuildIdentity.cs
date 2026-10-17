using System.Text.RegularExpressions;

namespace Blackglass.Accounts.Domain.Hosts;

/// <summary>
/// A mission server build: its revision (1 to 64 characters, a letter or digit first, then letters, digits, '.', '_' or
/// '-') and the catalogue version it was built with ("sha256:" and 64 lowercase hex digits, spec §4.6). A build runs an
/// attempt only when the pair is allowlisted for the attempt's template version and the catalogue versions are equal
/// (owner ruling B4).
/// </summary>
public sealed partial record BuildIdentity(string Revision, string CatalogueVersion)
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RevisionPattern();

    [GeneratedRegex(@"^sha256:[0-9a-f]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CataloguePattern();

    public static bool IsWellFormedRevision(string? revision) => revision is not null && RevisionPattern().IsMatch(revision);

    public static bool IsWellFormedCatalogueVersion(string? version) => version is not null && CataloguePattern().IsMatch(version);

    public static bool TryParse(string? revision, string? catalogueVersion, out BuildIdentity? build)
    {
        build = IsWellFormedRevision(revision) && IsWellFormedCatalogueVersion(catalogueVersion) ? new BuildIdentity(revision!, catalogueVersion!) : null;
        return build is not null;
    }
}
