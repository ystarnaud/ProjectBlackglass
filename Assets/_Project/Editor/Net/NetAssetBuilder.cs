#if UNITY_EDITOR
using System.IO;
using FishNet.Component.Transforming;
using FishNet.Object;
using UnityEditor;
using UnityEngine;

namespace Blackglass.EditorTools
{
    /// <summary>
    /// Builds the networked avatar prefab (networking experiment, decision 046): a NetworkObject, a NetworkTransform and a
    /// NetUnitState, with no renderer or collider, because the server shows nothing and the client builds its own visuals.
    /// Importing the prefab makes FishNet's asset postprocessor add it to Assets/DefaultPrefabObjects.asset. Idempotent.
    /// </summary>
    public static class NetAssetBuilder
    {
        public const string AvatarPath = "Assets/_Project/Prefabs/Net/NetUnitAvatar.prefab";
        public const string PrefabCollectionPath = "Assets/DefaultPrefabObjects.asset";

        [MenuItem("Blackglass/Net/Build Net Assets")]
        public static void BuildMenu() => Build();

        /// <summary>Unity -batchmode -executeMethod entry.</summary>
        public static void BuildFromCommandLine() => Build();

        public static void Build()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AvatarPath));
            var avatar = new GameObject("NetUnitAvatar");
            avatar.AddComponent<NetworkObject>();
            avatar.AddComponent<NetworkTransform>();
            avatar.AddComponent<NetUnitState>();
            PrefabUtility.SaveAsPrefabAsset(avatar, AvatarPath);
            Object.DestroyImmediate(avatar);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("NetAssetBuilder: saved " + AvatarPath + ".");
        }
    }
}
#endif
