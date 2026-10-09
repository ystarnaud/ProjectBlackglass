using UnityEditor;
using UnityEngine;

namespace Blackglass.AssetPipeline
{
    public static class ModelPrefabWriter
    {
        /// <summary>
        /// Writes (or updates in place, keeping the GUID) a prefab: an identity root named rootName with the model as one child named
        /// childName carrying the scale. When an avatar is given, the child's Animator uses it with no root motion.
        /// The model importer is configured without colliders, so the prefab has none; callers' tests assert it.
        /// </summary>
        public static GameObject Write(string prefabPath, string rootName, GameObject modelAsset, string childName, float scale, Avatar avatar)
        {
            var root = new GameObject(rootName);
            try
            {
                var child = (GameObject)PrefabUtility.InstantiatePrefab(modelAsset, root.transform);
                child.name = childName;
                child.transform.localPosition = Vector3.zero;
                child.transform.localRotation = Quaternion.identity;
                child.transform.localScale = Vector3.one * scale;
                if (avatar != null)
                {
                    var animator = child.GetComponent<Animator>();
                    if (animator == null) animator = child.AddComponent<Animator>();
                    animator.avatar = avatar;
                    animator.applyRootMotion = false;
                }
                return PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
    }
}
