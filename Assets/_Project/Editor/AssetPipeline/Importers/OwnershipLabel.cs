using System;
using UnityEditor;

namespace Blackglass.AssetPipeline
{
    /// <summary>The "BlackglassStudio" AssetDatabase label marks assets this pipeline created, so re-imports may update them silently.</summary>
    public static class OwnershipLabel
    {
        public static bool IsOwned(string assetPath)
        {
            var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            return asset != null && Array.IndexOf(AssetDatabase.GetLabels(asset), ContractInfo.OwnershipLabel) >= 0;
        }

        public static void Mark(string assetPath)
        {
            var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (asset == null) return;
            var labels = AssetDatabase.GetLabels(asset);
            if (Array.IndexOf(labels, ContractInfo.OwnershipLabel) >= 0) return;
            var updated = new string[labels.Length + 1];
            Array.Copy(labels, updated, labels.Length);
            updated[labels.Length] = ContractInfo.OwnershipLabel;
            AssetDatabase.SetLabels(asset, updated);
        }
    }
}
