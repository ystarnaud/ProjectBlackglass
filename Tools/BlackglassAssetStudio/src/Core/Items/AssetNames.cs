using Blackglass.AssetPipeline;

namespace Blackglass.AssetStudio;

public static class AssetNames
{
    /// <summary>A valid asset name from a file stem: invalid characters and spaces become '_', never empty.</summary>
    public static string FromFileStem(string stem)
    {
        var chars = (stem ?? "").Trim().Select(c =>
            char.IsWhiteSpace(c) || char.IsControl(c) || "<>:\"|?*/\\".Contains(c) ? '_' : c).ToArray();
        var name = new string(chars).Trim('.', '_', ' ');
        // Keep interior underscores but drop the ones that came from edge characters; a name of only dots collapses to the fallback.
        if (name.Length == 0) return "Asset";
        return PathRules.ValidateAssetName(name) == null ? name : "Asset";
    }
}
