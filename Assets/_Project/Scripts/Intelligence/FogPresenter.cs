using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Blackglass
{
    /// <summary>
    /// Hides what the player has not found. When a mission begins it builds, under the mission root, one opaque dark volume
    /// per region and one per wall box (cubes with no collider, so rays, clicks and the NavMesh never see them), and keeps
    /// them in step with the knowledge model: an Unknown region is fogged, a Discovered one (known but not in sight) gets a
    /// translucent veil (or is simply clear when no veil material is set), an Observed one is clear. A wall is fogged only
    /// while every region beside it is Unknown, so the walls of a known room stay visible from outside. Presentation only
    /// (decision 037): knowledge lives in MissionIntelligence, never in these renderers, and the volumes work with any
    /// environment theme or none. The developer truth view and fog-off missions show everything.
    /// </summary>
    public sealed class FogPresenter : MonoBehaviour
    {
        // A little taller than the walls (3 m), so nothing inside shows above the top.
        const float VolumeHeight = 3.2f;
        // Wall volumes are a hair larger than the wall, so the two surfaces never fight.
        const float WallGrow = 0.04f;

        sealed class Volume
        {
            public GameObject Object;
            public Renderer Renderer;
            public int[] Regions;
            public bool IsWall;
        }

        [SerializeField] IntelligenceService intelligence;
        [SerializeField] Material fogMaterial;
        [SerializeField] Material veilMaterial;

        readonly List<Volume> volumes = new List<Volume>();
        readonly List<int> buffer = new List<int>();
        Transform container;

        internal int VolumeCount => volumes.Count;
        internal bool IsVolumeActive(int index) => volumes[index].Object != null && volumes[index].Object.activeSelf;
        internal bool IsWallVolume(int index) => volumes[index].IsWall;
        internal Material VolumeMaterial(int index) => volumes[index].Renderer.sharedMaterial;

        internal void Initialize(IntelligenceService service, Material fog, Material veil)
        {
            intelligence = service;
            fogMaterial = fog;
            veilMaterial = veil;
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

        void Build(MissionLayout layout, RegionMap map, Transform root)
        {
            Clear();
            container = new GameObject("Fog").transform;
            if (root != null)
                container.SetParent(root, false);
            for (var region = 0; region < map.Count; region++)
                volumes.Add(Make($"FogRegion_{region}", layout, map[region].Rect, new[] { region }, false));
            foreach (var box in layout.Boxes)
            {
                if (box.Kind != MissionBoxKind.Wall)
                    continue;
                map.RegionsAdjacentTo(box.Footprint, buffer);
                if (buffer.Count > 0)
                    volumes.Add(Make($"{box.Name}_Fog", layout, box.Footprint, buffer.ToArray(), true));
            }
            Refresh();
        }

        Volume Make(string objectName, MissionLayout layout, RectInt rect, int[] regions, bool isWall)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = objectName;
            DestroyImmediate(cube.GetComponent<Collider>());
            cube.transform.SetParent(container, false);
            var grow = isWall ? WallGrow : 0f;
            cube.transform.position = layout.RectCenter(rect) + Vector3.up * (VolumeHeight * 0.5f);
            cube.transform.localScale = new Vector3(rect.width + grow, VolumeHeight, rect.height + grow);
            var renderer = cube.GetComponent<Renderer>();
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return new Volume { Object = cube, Renderer = renderer, Regions = regions, IsWall = isWall };
        }

        void Refresh()
        {
            var show = intelligence != null && intelligence.IsFogActive && !intelligence.TruthView;
            foreach (var volume in volumes)
            {
                if (volume.Object == null)
                    continue;
                var active = false;
                var material = fogMaterial;
                if (show && volume.IsWall)
                {
                    active = true;
                    foreach (var region in volume.Regions)
                    {
                        if (intelligence.StateOfRegion(region) != KnowledgeState.Unknown)
                        {
                            active = false;
                            break;
                        }
                    }
                }
                else if (show)
                {
                    switch (intelligence.StateOfRegion(volume.Regions[0]))
                    {
                        case KnowledgeState.Unknown:
                            active = true;
                            break;
                        case KnowledgeState.Discovered:
                            active = veilMaterial != null;
                            material = veilMaterial;
                            break;
                    }
                }
                if (active && material != null && volume.Renderer.sharedMaterial != material)
                    volume.Renderer.sharedMaterial = material;
                if (volume.Object.activeSelf != active)
                    volume.Object.SetActive(active);
            }
        }

        void Clear()
        {
            volumes.Clear();
            if (container != null)
                Destroy(container.gameObject);
            container = null;
        }
    }
}
