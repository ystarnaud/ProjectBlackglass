using UnityEngine;
using UnityEngine.Rendering;

namespace Blackglass
{
    /// <summary>
    /// Draws a hostile only while the player may see it (decision 037). Sets Renderer.forceRenderingOff on every renderer under
    /// the hostile (not Renderer.enabled, which other code toggles), so the unit, its attack line and any other drawn child
    /// vanish together while its colliders, AI and Health go on exactly as before. While the hostile is last known it keeps a
    /// small disc-and-post marker at the position it was last seen; the marker is not a target and has no collider. Added to
    /// every hostile by the spawner. The developer truth view draws everything and no markers. A weapon model the unit's
    /// UnitWeaponVisual places later (its default appears in Awake, after this Bind) is taken in at once with the current state.
    /// </summary>
    public sealed class HostilePresenter : MonoBehaviour
    {
        public const string MarkerName = "LastKnownMarker";
        const float MarkerHeight = 0.06f;
        const float MarkerDiameter = 0.9f;

        IntelligenceService intelligence;
        Health health;
        Renderer[] renderers = new Renderer[0];
        UnitWeaponVisual weapon;
        Transform markerParent;
        GameObject marker;
        bool drawn = true;

        public bool IsHidden => !drawn;
        internal GameObject Marker => marker;

        /// <summary>Wires the presenter; `parentForMarker` (the mission's Actors object) owns the marker, so regeneration destroys it.</summary>
        public void Bind(IntelligenceService service, Transform parentForMarker)
        {
            intelligence = service;
            markerParent = parentForMarker;
            health = GetComponent<Health>();
            renderers = GetComponentsInChildren<Renderer>(true);
            drawn = true;
            Apply(IsShown());
            if (weapon == null && TryGetComponent(out weapon))
                weapon.Changed += OnWeaponChanged;
        }

        // The hand model changed: collect the renderers again and give every one of them the state already in force.
        void OnWeaponChanged()
        {
            renderers = GetComponentsInChildren<Renderer>(true);
            foreach (var renderer in renderers)
            {
                if (renderer != null)
                    renderer.forceRenderingOff = !drawn;
            }
        }

        void LateUpdate()
        {
            Apply(IsShown());
            UpdateMarker();
        }

        // Health deactivates a dead unit: it leaves no marker.
        void OnDisable() => HideMarker();

        void OnDestroy()
        {
            if (weapon != null)
                weapon.Changed -= OnWeaponChanged;
            if (marker != null)
                Destroy(marker);
        }

        bool IsShown() => intelligence == null || health == null || intelligence.IsUnitShown(health);

        void Apply(bool shown)
        {
            if (shown == drawn)
                return;
            drawn = shown;
            foreach (var renderer in renderers)
            {
                if (renderer != null)
                    renderer.forceRenderingOff = !shown;
            }
        }

        void UpdateMarker()
        {
            var at = default(Vector3);
            var wanted = intelligence != null && health != null && health.IsAlive && !intelligence.TruthView
                && intelligence.StateOfEnemy(health) == KnowledgeState.Discovered && intelligence.TryLastKnown(health, out at);
            if (!wanted)
            {
                HideMarker();
                return;
            }
            if (marker == null)
                marker = CreateMarker();
            marker.transform.position = new Vector3(at.x, MarkerHeight, at.z);
            if (!marker.activeSelf)
                marker.SetActive(true);
        }

        void HideMarker()
        {
            if (marker != null && marker.activeSelf)
                marker.SetActive(false);
        }

        GameObject CreateMarker()
        {
            var root = new GameObject(MarkerName);
            if (markerParent != null)
                root.transform.SetParent(markerParent, false);
            var disc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            disc.name = "Disc";
            DestroyImmediate(disc.GetComponent<Collider>());
            disc.transform.SetParent(root.transform, false);
            disc.transform.localScale = new Vector3(MarkerDiameter, 0.01f, MarkerDiameter);
            var post = GameObject.CreatePrimitive(PrimitiveType.Cube);
            post.name = "Post";
            DestroyImmediate(post.GetComponent<Collider>());
            post.transform.SetParent(root.transform, false);
            post.transform.localPosition = new Vector3(0f, 0.6f, 0f);
            post.transform.localScale = new Vector3(0.1f, 1.2f, 0.1f);
            foreach (var renderer in root.GetComponentsInChildren<Renderer>())
            {
                if (intelligence != null && intelligence.MarkerMaterial != null)
                    renderer.sharedMaterial = intelligence.MarkerMaterial;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }
            return root;
        }
    }
}
