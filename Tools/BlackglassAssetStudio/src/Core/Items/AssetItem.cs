using Blackglass.AssetPipeline;

namespace Blackglass.AssetStudio;

/// <summary>One entry in the list: the original file, its classification and the manifest item the user edits.</summary>
public sealed class AssetItem
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string OriginalPath { get; init; } = "";
    public string ProfileId { get; set; } = ImportProfiles.Prop;
    public string ClassificationReason { get; set; } = "";
    public string? UnsupportedReason { get; init; }
    public ImportItem Import { get; init; } = new();
}
