using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The model a weapon item shows in a unit's hand: a prefab and the local pose of its root under the unit's
    /// WeaponSocket. The socket carries the per-character alignment; this carries the per-weapon difference (identity for
    /// the standard rifle). Shared, immutable data: it holds no state.
    /// </summary>
    [CreateAssetMenu(menuName = "Blackglass/Weapon Visual", fileName = "WeaponVisual")]
    public sealed class WeaponVisualAsset : ScriptableObject
    {
        [SerializeField] GameObject prefab;
        [SerializeField] Vector3 localPosition = Vector3.zero;
        [SerializeField] Vector3 localEuler = Vector3.zero;
        [SerializeField] Vector3 localScale = Vector3.one;

        public GameObject Prefab => prefab;
        public Vector3 LocalPosition => localPosition;
        public Vector3 LocalEuler => localEuler;
        public Vector3 LocalScale => localScale;

        internal static WeaponVisualAsset Create(GameObject prefab, Vector3 position, Vector3 euler, Vector3 scale)
        {
            var asset = CreateInstance<WeaponVisualAsset>();
            asset.prefab = prefab;
            asset.localPosition = position;
            asset.localEuler = euler;
            asset.localScale = scale;
            return asset;
        }
    }
}
