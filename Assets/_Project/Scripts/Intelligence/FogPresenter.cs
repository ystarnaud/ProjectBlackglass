using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Blackglass
{
    /// <summary>
    /// Draws the fog of war. When a mission begins it builds, under the mission root, one collider-free box over the floor
    /// plan, and keeps a FogField (a brightness grid) in step with the knowledge model: an Unknown region is hidden, a
    /// Discovered one is dim, an Observed one is clear, and a wall takes the brightest region beside it, so the walls of a
    /// known room stay visible from outside. The field is feathered, so the edge of the dark is a smooth ramp on the known
    /// side, and eased over time, so a reveal sweeps open. A shader on the box darkens whatever is drawn at each point by
    /// the grid. Presentation only (decision 037): knowledge lives in MissionIntelligence, never in these renderers. The
    /// developer truth view and fog-off missions show everything.
    /// </summary>
    public sealed class FogPresenter : MonoBehaviour
    {
        /// <summary>How clear a region is that the player has seen but cannot see now.</summary>
        public const float DiscoveredLevel = 0.45f;

        const KnowledgeState NeverPainted = (KnowledgeState)(-1);
        const float CellsPerMetre = 4f;
        // A little taller than the walls (3 m), so nothing inside shows above the darkness.
        const float VolumeHeight = 4f;

        sealed class Wall
        {
            public Rect Rect;
            public int[] Regions;
        }

        [SerializeField] IntelligenceService intelligence;
        [SerializeField] Material fogMaterial;
        [Tooltip("How far the dark fades out into known ground, in metres.")]
        [SerializeField, Min(0f)] float featherMetres = 1.5f;
        [Tooltip("Seconds for a full change between dark and clear when something is revealed.")]
        [SerializeField, Min(0f)] float fadeSeconds = 0.4f;

        static readonly int VisTexId = Shader.PropertyToID("_VisTex");
        static readonly int VisRectId = Shader.PropertyToID("_VisRect");

        readonly List<Wall> walls = new List<Wall>();
        readonly List<int> buffer = new List<int>();
        RegionMap map;
        FogField field;
        byte[] bytes;
        Texture2D texture;
        Material material;
        GameObject volume;
        Transform container;
        KnowledgeState[] painted;
        int snapFrame = -1;
        bool depthRequested;

        internal bool IsShown => volume != null && volume.activeSelf;
        internal bool IsSettled => field == null || field.IsSettled;
        internal Material Material => material;
        internal Texture2D VisibilityTexture => texture;
        internal float TargetAt(Vector3 world) => field.TargetAt(world.x, world.z);
        internal float CurrentAt(Vector3 world) => field.CurrentAt(world.x, world.z);

        internal void Initialize(IntelligenceService service, Material fog)
        {
            intelligence = service;
            fogMaterial = fog;
        }

        void OnEnable()
        {
            if (intelligence == null)
                return;
            intelligence.MissionBegun += Build;
            intelligence.Changed += Refresh;
        }

        void OnDisable()
        {
            if (intelligence != null)
            {
                intelligence.MissionBegun -= Build;
                intelligence.Changed -= Refresh;
            }
            Clear();
        }

        // The mist eases on unscaled time, so a reveal finishes even while the game is paused.
        void Update()
        {
            if (!IsShown)
                return;
            if (!depthRequested)
                depthRequested = RequestDepthTexture();
            if (!field.IsSettled && field.Step(Time.unscaledDeltaTime, fadeSeconds))
                Upload();
        }

        void Build(MissionLayout layout, RegionMap regionMap, Transform root)
        {
            Clear();
            map = regionMap;
            painted = new KnowledgeState[map.Count];
            for (var i = 0; i < painted.Length; i++)
                painted[i] = NeverPainted;
            snapFrame = Time.frameCount;
            container = new GameObject("Fog").transform;
            if (root != null)
                container.SetParent(root, false);

            var min = layout.ToWorld(0f, 0f);
            var max = layout.ToWorld(layout.Width, layout.Height);
            var area = Rect.MinMaxRect(min.x, min.z, max.x, max.z);
            field = new FogField(area, CellsPerMetre);
            bytes = new byte[field.Width * field.Height];
            texture = new Texture2D(field.Width, field.Height, TextureFormat.R8, false)
            {
                name = "FogOfWarGrid",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };

            foreach (var box in layout.Boxes)
            {
                if (box.Kind != MissionBoxKind.Wall)
                    continue;
                map.RegionsAdjacentTo(box.Footprint, buffer);
                var lo = layout.ToWorld(box.Footprint.xMin, box.Footprint.yMin);
                var hi = layout.ToWorld(box.Footprint.xMax, box.Footprint.yMax);
                walls.Add(new Wall { Rect = Rect.MinMaxRect(lo.x, lo.z, hi.x, hi.z), Regions = buffer.ToArray() });
            }

            if (fogMaterial != null)
                BuildVolume(area);
            depthRequested = RequestDepthTexture();
            // Nothing is painted yet: Begin runs the squad's first sight pass right after this and reports each change. Paints in
            // this same frame snap instead of easing, so a mission opens with its first view already clear, not fading in.
        }

        void BuildVolume(Rect area)
        {
            material = new Material(fogMaterial) { name = fogMaterial.name + " (Instance)" };
            material.SetTexture(VisTexId, texture);
            material.SetVector(VisRectId, new Vector4(area.xMin, area.yMin, 1f / area.width, 1f / area.height));
            volume = GameObject.CreatePrimitive(PrimitiveType.Cube);
            volume.name = "FogVolume";
            DestroyImmediate(volume.GetComponent<Collider>());
            volume.transform.SetParent(container, false);
            volume.transform.position = new Vector3(area.center.x, VolumeHeight * 0.5f, area.center.y);
            volume.transform.localScale = new Vector3(area.width, VolumeHeight, area.height);
            var renderer = volume.GetComponent<Renderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            volume.SetActive(false);   // shown by the first paint
        }

        void Refresh()
        {
            if (field == null)
                return;
            var show = intelligence != null && intelligence.IsFogActive && !intelligence.TruthView;
            if (volume != null && volume.activeSelf != show)
                volume.SetActive(show);
            if (!show)
                return;
            if (!Repaint())
                return;
            field.Feather(featherMetres);
            if (Time.frameCount == snapFrame)
                field.SnapToTarget();
            Upload();
        }

        // Paints the field from the knowledge model. False when no region changed since the last paint.
        bool Repaint()
        {
            var changed = false;
            for (var region = 0; region < map.Count; region++)
            {
                var state = intelligence.StateOfRegion(region);
                if (state != painted[region])
                    changed = true;
                painted[region] = state;
            }
            if (!changed)
                return false;
            field.Clear();
            for (var region = 0; region < map.Count; region++)
                field.Paint(map.WorldRect(region), LevelOf(painted[region]));
            foreach (var wall in walls)
            {
                var level = 1f;
                if (wall.Regions.Length > 0)
                {
                    level = 0f;
                    foreach (var region in wall.Regions)
                        level = Mathf.Max(level, LevelOf(painted[region]));
                }
                field.Paint(wall.Rect, level);
            }
            return true;
        }

        static float LevelOf(KnowledgeState state)
        {
            switch (state)
            {
                case KnowledgeState.Observed:
                    return 1f;
                case KnowledgeState.Discovered:
                    return DiscoveredLevel;
                default:
                    return 0f;
            }
        }

        void Upload()
        {
            field.CopyTo(bytes);
            texture.SetPixelData(bytes, 0);
            texture.Apply(false);
        }

        // The shader reads the camera's depth texture; ask for it on the game camera so the fog works at any quality level.
        static bool RequestDepthTexture()
        {
            var camera = Camera.main;
            if (camera == null)
                return false;
            camera.GetUniversalAdditionalCameraData().requiresDepthTexture = true;
            return true;
        }

        void Clear()
        {
            walls.Clear();
            field = null;
            map = null;
            painted = null;
            volume = null;
            if (container != null)
                Destroy(container.gameObject);
            container = null;
            if (material != null)
                Destroy(material);
            material = null;
            if (texture != null)
                Destroy(texture);
            texture = null;
        }
    }
}
