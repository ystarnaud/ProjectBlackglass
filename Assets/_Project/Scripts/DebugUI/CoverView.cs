using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Blackglass
{
    /// <summary>
    /// Debug view of the scene's cover points: a flat disc at each stand point and a nub on the obstacle side. Shows
    /// every point while paused and only claimed points in real time, coloured by state (available white, reserved
    /// yellow, occupied cyan). Reads the registry every frame and never changes it. Lives on Systems. Markers have no
    /// colliders, so they never block click raycasts. Works while paused.
    /// </summary>
    public sealed class CoverView : MonoBehaviour
    {
        internal static readonly Color AvailableColor = Color.white;
        internal static readonly Color ReservedColor = Color.yellow;
        internal static readonly Color OccupiedColor = Color.cyan;
        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");

        [SerializeField] CoverRegistry registry;
        [SerializeField] TacticalPause tacticalPause;
        [SerializeField] Material markerMaterial;
        [SerializeField, Min(0.05f)] float discDiameter = 0.5f;
        // The prototype ground is flat at y = 0; markers float just above it.
        [SerializeField] float groundHeight = 0.05f;

        // One marker per registry point, in registry order (built once in Start).
        readonly List<GameObject> markers = new List<GameObject>();
        readonly List<Renderer[]> markerRenderers = new List<Renderer[]>();
        MaterialPropertyBlock block;

        internal IReadOnlyList<GameObject> Markers => markers;

        internal int VisibleMarkerCount
        {
            get
            {
                var count = 0;
                foreach (var marker in markers)
                {
                    if (marker.activeSelf)
                        count++;
                }
                return count;
            }
        }

        /// <summary>The colour a point's marker shows for its state.</summary>
        internal static Color ColorFor(CoverLocation point) =>
            !point.IsClaimed ? AvailableColor : point.IsOccupied ? OccupiedColor : ReservedColor;

        /// <summary>The colour currently applied to the marker at `index` (tests).</summary>
        internal Color ShownColor(int index)
        {
            block ??= new MaterialPropertyBlock();
            markerRenderers[index][0].GetPropertyBlock(block);
            return block.GetColor(BaseColor);
        }

        internal void Initialize(CoverRegistry coverRegistry, TacticalPause pause, Material material)
        {
            registry = coverRegistry;
            tacticalPause = pause;
            markerMaterial = material;
        }

        void Start()
        {
            if (registry == null)
                return;
            foreach (var point in registry.Points)
                markers.Add(CreateMarker(point));
        }

        void LateUpdate()
        {
            if (registry == null)
                return;
            var paused = tacticalPause != null && tacticalPause.IsPaused;
            for (var i = 0; i < markers.Count && i < registry.Points.Count; i++)
            {
                var point = registry.Points[i];
                var show = point != null && point.IsValid && (paused || point.IsClaimed);
                if (markers[i].activeSelf != show)
                    markers[i].SetActive(show);
                if (show)
                    Paint(i, ColorFor(point));
            }
        }

        // A disc at the stand point plus a nub 0.4 m toward the obstacle. Parented here for cleanup; positions are world.
        GameObject CreateMarker(CoverLocation point)
        {
            var root = new GameObject("CoverMarker");
            root.transform.SetParent(transform, false);
            if (point != null)
            {
                var disc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                disc.name = "Disc";
                DestroyImmediate(disc.GetComponent<Collider>());
                disc.transform.SetParent(root.transform, false);
                disc.transform.position = point.Position + Vector3.up * groundHeight;
                disc.transform.localScale = new Vector3(discDiameter, 0.01f, discDiameter);
                var nub = GameObject.CreatePrimitive(PrimitiveType.Cube);
                nub.name = "Nub";
                DestroyImmediate(nub.GetComponent<Collider>());
                nub.transform.SetParent(root.transform, false);
                nub.transform.SetPositionAndRotation(point.Position + point.Facing * 0.4f + Vector3.up * 0.1f, Quaternion.LookRotation(point.Facing));
                nub.transform.localScale = new Vector3(0.15f, 0.15f, 0.3f);
            }
            var renderers = root.GetComponentsInChildren<Renderer>();
            foreach (var markerRenderer in renderers)
            {
                if (markerMaterial != null)
                    markerRenderer.sharedMaterial = markerMaterial;
                markerRenderer.shadowCastingMode = ShadowCastingMode.Off;
                markerRenderer.receiveShadows = false;
            }
            markerRenderers.Add(renderers);
            root.SetActive(false);
            return root;
        }

        void Paint(int index, Color color)
        {
            block ??= new MaterialPropertyBlock();
            block.SetColor(BaseColor, color);
            foreach (var markerRenderer in markerRenderers[index])
                markerRenderer.SetPropertyBlock(block);
        }
    }
}
