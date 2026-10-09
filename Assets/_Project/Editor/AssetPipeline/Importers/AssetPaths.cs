using System.IO;
using UnityEditor;
using UnityEngine;

namespace Blackglass.AssetPipeline
{
    public static class AssetPaths
    {
        public static string ProjectRoot { get { return Directory.GetParent(Application.dataPath).FullName; } }

        public static string Full(string assetPath)
        {
            return Path.Combine(ProjectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
        }

        /// <summary>Creates every missing folder of "Assets/A/B" through the AssetDatabase.</summary>
        public static void EnsureFolder(string assetFolder)
        {
            var parts = assetFolder.Split('/');
            var current = parts[0];
            for (var i = 1; i < parts.Length; i++)
            {
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }
    }
}
