using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Presentation only: keeps exactly one managed weapon model as a child of the unit's WeaponSocket (the right-hand
    /// attachment the character prefabs already carry). Show replaces only that child; the gameplay root, its scale and
    /// the unit's identity are never touched. The socket's own local transform is the per-character alignment; the weapon
    /// asset adds the per-weapon pose. A unit with no WeaponSocket (the capsule units) shows nothing and logs nothing.
    /// A prefab-assigned default shows until the first explicit Show (units with no loadout, hostiles).
    /// Changed fires whenever the managed child is placed or removed, so presenters that cache the unit's renderers (the
    /// fog of war's HostilePresenter) can take the new model in.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class UnitWeaponVisual : MonoBehaviour
    {
        public const string SocketName = "WeaponSocket";
        public const string ManagedName = "EquippedWeapon";

        [SerializeField] WeaponVisualAsset defaultVisual;

        Transform socket;
        GameObject current;
        WeaponVisualAsset shown;
        bool explicitlySet;

        /// <summary>The managed model currently in the hand, or null.</summary>
        public GameObject Current => current;

        /// <summary>Raised after the managed child was replaced or removed (Current is already the new one).</summary>
        public event Action Changed;

        void Awake()
        {
            if (!explicitlySet && defaultVisual != null)
                Place(defaultVisual);
        }

        /// <summary>Shows this weapon (null shows none). Showing the one already shown changes nothing.</summary>
        public void Show(WeaponVisualAsset asset)
        {
            explicitlySet = true;
            if (asset == shown && (current != null || asset == null || asset.Prefab == null))
                return;
            Place(asset);
        }

        void Place(WeaponVisualAsset asset)
        {
            if (current != null)
            {
                current.SetActive(false);            // not drawn for the rest of this frame
                current.transform.SetParent(null);   // leaves the socket at once; Destroy only runs at the end of the frame
                if (Application.isPlaying)
                    Destroy(current);
                else
                    DestroyImmediate(current);
                current = null;
            }
            shown = asset;
            var hand = asset != null && asset.Prefab != null ? Socket() : null;
            if (hand != null)
            {
                current = Instantiate(asset.Prefab, hand);
                current.name = ManagedName;
                current.transform.localPosition = asset.LocalPosition;
                current.transform.localRotation = Quaternion.Euler(asset.LocalEuler);
                current.transform.localScale = asset.LocalScale;
                EnvironmentVisualBuilder.StripColliders(current);
            }
            Changed?.Invoke();
        }

        Transform Socket()
        {
            if (socket != null)
                return socket;
            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                if (t.name == SocketName)
                    return socket = t;
            }
            return null;
        }
    }
}
