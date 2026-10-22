using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Draws the public map the server projected (decision 052) once, under one ClientMap root:
    /// - the environment theme's modules (EnvironmentVisualBuilder, visual-only);
    /// - the gameplay-shaped cubes, whose renderers show only without a theme;
    /// - the closed crates;
    /// - the extraction disc and beacon, coloured by the objective's state.
    /// It creates no collider, NavMesh or cover component, so it cannot change any simulation, even in a host-mode test where
    /// the server's mission shares the scene. A refused map draws nothing.
    /// </summary>
    public sealed class NetMapView : MonoBehaviour
    {
        public const string RootName = "ClientMap";
        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        static readonly Color CrateColour = new Color(0.85f, 0.6f, 0.15f);   // MissionContent's loot colour

        [SerializeField] NetClient client;
        [SerializeField] EnvironmentTheme theme;
        [SerializeField] Material groundMaterial;
        [SerializeField] Material obstacleMaterial;

        readonly List<Bounds> obstacles = new List<Bounds>();
        readonly List<Transform> cubes = new List<Transform>();
        readonly List<Renderer> zoneRenderers = new List<Renderer>();
        Transform root;
        MaterialPropertyBlock block;

        internal void Initialize(NetClient netClient, EnvironmentTheme environmentTheme, Material ground, Material obstacle)
        {
            client = netClient;
            theme = environmentTheme;
            groundMaterial = ground;
            obstacleMaterial = obstacle;
        }

        public MissionLayout Layout { get; private set; }
        public bool Built => Layout != null;
        /// <summary>Every box and crate, as world bounds: what a click ray can be stopped by.</summary>
        public IReadOnlyList<Bounds> Obstacles => obstacles;
        /// <summary>The cubes in the server's order: floors, then boxes, then crates.</summary>
        public IReadOnlyList<Transform> Cubes => cubes;
        /// <summary>The extraction zone's visual, or null before the objective arrives.</summary>
        public Transform Extraction { get; private set; }

        void LateUpdate()
        {
            if (client == null)
                return;
            if (!Built && client.Record.Map != null)
                Build(client.Record.Map);
            if (Built)
                DrawExtraction(client.Record.Objectives);
        }

        void OnDestroy()
        {
            if (root != null)
                Destroy(root.gameObject);
        }

        /// <summary>Draws the map once; false (and nothing drawn) for an invalid map or when one is already drawn.</summary>
        internal bool Build(NetMap map)
        {
            if (Built || NetMapValidation.Problem(map) != null)
                return false;
            var layout = NetMapGeometry.ToLayout(map);
            root = new GameObject(RootName).transform;
            var showCubes = theme == null;
            for (var i = 0; i < layout.FloorRects.Count; i++)
                Cube("Floor_" + (i + 1), NetMapGeometry.FloorBounds(layout, layout.FloorRects[i]), groundMaterial, showCubes, false, null);
            foreach (var box in layout.Boxes)
                Cube(box.Name, NetMapGeometry.BoxBounds(layout, box), obstacleMaterial, showCubes, true, null);
            for (var i = 0; i < map.Containers.Count; i++)
                Cube("LootContainer_" + (i + 1), NetMapGeometry.ContainerBounds(layout, map.Containers[i]), obstacleMaterial, true, true, CrateColour);
            if (theme != null)
                EnvironmentVisualBuilder.Build(layout, theme, root);
            Layout = layout;
            return true;
        }

        void Cube(string objectName, Bounds bounds, Material material, bool visible, bool obstacle, Color? colour)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = objectName;
            DestroyImmediate(cube.GetComponent<Collider>());   // at once: not even one physics step may see it
            cube.transform.SetParent(root, false);
            cube.transform.position = bounds.center;
            cube.transform.localScale = bounds.size;
            var renderer = cube.GetComponent<Renderer>();
            if (material != null)
                renderer.sharedMaterial = material;
            renderer.enabled = visible;
            if (colour.HasValue)
                Tint(renderer, colour.Value);
            if (obstacle)
                obstacles.Add(bounds);
            cubes.Add(cube.transform);
        }

        void DrawExtraction(IReadOnlyList<NetObjective> objectives)
        {
            foreach (var objective in objectives)
            {
                if (objective.Kind != NetObjectiveKind.Extraction || !objective.HasPoint)
                    continue;
                if (Extraction == null)
                    Extraction = CreateZone(objective.Radius);
                Extraction.position = objective.Point;
                var colour = ObjectiveMarker.ZoneColour(objective.State);
                foreach (var renderer in zoneRenderers)
                    Tint(renderer, colour);
                return;
            }
        }

        // MissionContent.CreateZone's look: a flat disc of the zone's radius and a thin beacon, with no collider.
        Transform CreateZone(float radius)
        {
            var zone = new GameObject("ExtractionZone").transform;
            zone.SetParent(root, false);
            ZonePart(zone, "Disc", new Vector3(radius * 2f, 0.02f, radius * 2f), 0.03f);
            ZonePart(zone, "Beacon", new Vector3(0.25f, 1.5f, 0.25f), 1.5f);
            return zone;
        }

        void ZonePart(Transform zone, string partName, Vector3 scale, float height)
        {
            var part = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            part.name = partName;
            DestroyImmediate(part.GetComponent<Collider>());
            part.transform.SetParent(zone, false);
            part.transform.localPosition = new Vector3(0f, height, 0f);
            part.transform.localScale = scale;
            var renderer = part.GetComponent<Renderer>();
            if (obstacleMaterial != null)
                renderer.sharedMaterial = obstacleMaterial;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            zoneRenderers.Add(renderer);
        }

        void Tint(Renderer renderer, Color colour)
        {
            if (block == null)
                block = new MaterialPropertyBlock();
            block.SetColor(BaseColor, colour);
            renderer.SetPropertyBlock(block);
        }
    }
}
