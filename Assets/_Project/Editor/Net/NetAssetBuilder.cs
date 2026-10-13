#if UNITY_EDITOR
using System.IO;
using FishNet.Component.Transforming;
using FishNet.Object;
using FishNet.Observing;
using UnityEditor;
using UnityEngine;

namespace Blackglass.EditorTools
{
    /// <summary>
    /// Builds the networked avatar prefab (networking experiment, decisions 046 and 047): a NetworkObject, a server-authoritative
    /// NetworkTransform, a NetworkObserver whose only condition is NetVisibilityCondition (ignoring the manager's defaults), and a
    /// NetUnitState, with no renderer or collider. Importing the prefab makes FishNet's asset postprocessor add it to
    /// Assets/DefaultPrefabObjects.asset. Idempotent.
    /// </summary>
    public static class NetAssetBuilder
    {
        public const string AvatarPath = "Assets/_Project/Prefabs/Net/NetUnitAvatar.prefab";
        public const string ConditionPath = "Assets/_Project/Prefabs/Net/NetVisibilityCondition.asset";
        public const string PrefabCollectionPath = "Assets/DefaultPrefabObjects.asset";

        [MenuItem("Blackglass/Net/Build Net Assets")]
        public static void BuildMenu() => Build();

        /// <summary>Unity -batchmode -executeMethod entry.</summary>
        public static void BuildFromCommandLine() => Build();

        public static void Build()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AvatarPath));
            var condition = AssetDatabase.LoadAssetAtPath<NetVisibilityCondition>(ConditionPath);
            if (condition == null)
            {
                condition = ScriptableObject.CreateInstance<NetVisibilityCondition>();
                AssetDatabase.CreateAsset(condition, ConditionPath);
            }

            var avatar = new GameObject("NetUnitAvatar");
            avatar.AddComponent<NetworkObject>();
            var networkTransform = avatar.AddComponent<NetworkTransform>();
            var observer = avatar.AddComponent<NetworkObserver>();
            avatar.AddComponent<NetUnitState>();

            // FishNet defaults a NetworkTransform to client-authoritative; ownership must never grant a client the transform.
            var transformSettings = new SerializedObject(networkTransform);
            transformSettings.FindProperty("_clientAuthoritative").boolValue = false;
            transformSettings.ApplyModifiedPropertiesWithoutUndo();

            var observerSettings = new SerializedObject(observer);
            observerSettings.FindProperty("_overrideType").intValue = (int)NetworkObserver.ConditionOverrideType.IgnoreManager;
            var conditions = observerSettings.FindProperty("_observerConditions");
            conditions.arraySize = 1;
            conditions.GetArrayElementAtIndex(0).objectReferenceValue = condition;
            observerSettings.ApplyModifiedPropertiesWithoutUndo();

            PrefabUtility.SaveAsPrefabAsset(avatar, AvatarPath);
            Object.DestroyImmediate(avatar);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("NetAssetBuilder: saved " + AvatarPath + " and " + ConditionPath + ".");
        }
    }
}
#endif
