using System.Text.RegularExpressions;
using Blackglass.AssetPipeline;

namespace Blackglass.AssetStudio;

/// <summary>Asset labels are stored in the asset's .meta file ("labels:" list), so ownership can be read without Unity.</summary>
public static class OwnershipProbe
{
    public static bool IsStudioOwned(string metaPath)
    {
        if (!File.Exists(metaPath)) return false;
        return Regex.IsMatch(File.ReadAllText(metaPath), @"^- " + Regex.Escape(ContractInfo.OwnershipLabel) + @"\s*$", RegexOptions.Multiline);
    }
}
