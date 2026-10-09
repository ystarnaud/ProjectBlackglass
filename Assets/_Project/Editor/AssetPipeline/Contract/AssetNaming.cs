#nullable disable
using System.IO;

namespace Blackglass.AssetPipeline
{
    /// <summary>Where an item's assets end up. Used by the app to detect conflicts and by Unity to write them.</summary>
    public static class AssetNaming
    {
        public static string ModelPath(ImportItem item)
        {
            return PathRules.Normalize(item.destinationFolder) + "/" + item.name + Path.GetExtension(item.sourcePath).ToLowerInvariant();
        }

        /// <summary>The prefab path, or null when the profile creates none (animations, or generatePrefab off).</summary>
        public static string PrefabPath(ImportItem item)
        {
            var folder = PathRules.Normalize(item.destinationFolder);
            switch (item.profile)
            {
                case ProfileIds.HumanoidCharacter:
                    return item.character.generatePrefab ? folder + "/" + item.name + "_Visual.prefab" : null;
                case ProfileIds.GenericProp:
                    return item.prop.generatePrefab ? folder + "/" + item.name + ".prefab" : null;
                case ProfileIds.EnvironmentModule:
                    return folder + "/" + item.name + ".prefab";
                default:
                    return null;
            }
        }
    }
}
