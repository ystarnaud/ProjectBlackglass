using Blackglass.Accounts.Domain.Catalogue;

namespace Blackglass.Accounts.Domain.Attempts;

/// <summary>
/// One immutable, versioned mission template (owner ruling S7-D1): the backend's admission rules and energy cost for a
/// procedural mission. TemplateId follows the catalogue id pattern (lowercase letters and digits separated by single '.'
/// or '-', at most 64 characters). Version is at least 1. EnergyCost is at least 0: a zero-cost template is valid and
/// takes no energy path. MinOperatives is at least 1 and MaxOperatives from MinOperatives to 16. The cost and team sizes
/// are provisional configuration; MaxOperativesLimit is a sanity bound shared with the deploy request's list limit.
/// </summary>
public sealed record MissionTemplate(string TemplateId, int Version, int EnergyCost, int MinOperatives, int MaxOperatives)
{
    public const int MaxOperativesLimit = 16;

    public static bool IsWellFormedId(string? templateId) => CanonicalCatalogueJson.IsWellFormedId(templateId);

    public static MissionTemplate Create(string templateId, int version, int energyCost, int minOperatives, int maxOperatives) =>
        new MissionTemplate(templateId, version, energyCost, minOperatives, maxOperatives).Validate();

    /// <summary>This template when every rule holds; otherwise an ArgumentException naming the rule.</summary>
    public MissionTemplate Validate()
    {
        if (!IsWellFormedId(TemplateId))
            throw new ArgumentException("A template id is lowercase letters and digits separated by single '.' or '-', at most 64 characters.", nameof(TemplateId));
        if (Version < 1) throw new ArgumentException("A template version is at least 1.", nameof(Version));
        if (EnergyCost < 0) throw new ArgumentException("A template's energy cost is at least 0.", nameof(EnergyCost));
        if (MinOperatives < 1) throw new ArgumentException("A template needs at least one operative.", nameof(MinOperatives));
        if (MaxOperatives < MinOperatives || MaxOperatives > MaxOperativesLimit)
            throw new ArgumentException($"A template's maximum team is from its minimum to {MaxOperativesLimit}.", nameof(MaxOperatives));
        return this;
    }
}
