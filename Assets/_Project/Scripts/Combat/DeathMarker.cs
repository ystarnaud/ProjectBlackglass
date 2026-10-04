using UnityEngine;
using UnityEngine.Rendering;

namespace Blackglass
{
    /// <summary>
    /// Debug death presentation: when the unit dies, leaves a flat disc where it fell. The disc is a root object
    /// (Health deactivates the unit right after Died) and has no collider, so clicks on it reach the ground.
    /// </summary>
    [RequireComponent(typeof(Health))]
    public sealed class DeathMarker : MonoBehaviour
    {
        [SerializeField] Material markerMaterial;
        [SerializeField, Min(0.1f)] float diameter = 1.2f;
        // The prototype ground is flat at y = 0; the disc floats just above it, under the queue lines (0.05).
        [SerializeField] float groundHeight = 0.03f;

        Health health;

        /// <summary>The marker spawned by this unit's death, or null while it lives.</summary>
        internal GameObject LastMarker { get; private set; }

        internal void Initialize(Material material) => markerMaterial = material;

        void Awake() => health = GetComponent<Health>();

        void OnEnable() => health.Died += OnDied;

        void OnDisable() => health.Died -= OnDied;

        void OnDied()
        {
            var marker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            marker.name = $"{name} (dead)";
            DestroyImmediate(marker.GetComponent<Collider>());
            var position = transform.position;
            position.y = groundHeight;
            marker.transform.SetPositionAndRotation(position, Quaternion.identity);
            marker.transform.localScale = new Vector3(diameter, 0.01f, diameter);
            var markerRenderer = marker.GetComponent<Renderer>();
            if (markerMaterial != null)
                markerRenderer.sharedMaterial = markerMaterial;
            markerRenderer.shadowCastingMode = ShadowCastingMode.Off;
            markerRenderer.receiveShadows = false;
            LastMarker = marker;
        }
    }
}
