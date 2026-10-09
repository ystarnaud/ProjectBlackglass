#if UNITY_EDITOR
using UnityEditor;

namespace Blackglass.Tests.AssetPipeline
{
    /// <summary>A throwaway folder under Assets for importer tests. Always call Clean() in TearDown.</summary>
    internal static class ScratchFolder
    {
        public const string Path = "Assets/_AssetStudioScratch";

        public static void Clean()
        {
            if (AssetDatabase.IsValidFolder(Path))
                AssetDatabase.DeleteAsset(Path);
            AssetDatabase.Refresh();
        }

        /// <summary>Absolute path of a file of the existing Darius art, used as importer input (for example "Models/Darius Stand Idle.fbx").</summary>
        public static string Darius(string relativeToDarius) =>
            System.IO.Path.GetFullPath(System.IO.Path.Combine(UnityEngine.Application.dataPath, "Art", "Characters", "Darius", relativeToDarius));

        /// <summary>Absolute path of the shared rifle model, used as a prop-shaped importer input.</summary>
        public static string RifleModel() =>
            System.IO.Path.GetFullPath(System.IO.Path.Combine(UnityEngine.Application.dataPath, "Art", "Weapons", "Rifle", "Models", "Rifle.fbx"));
    }
}
#endif
