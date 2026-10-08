using System.Collections.Generic;
using System.Linq;
using Unity.AI.Navigation;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The mission's objective content as scene objects: the terminal prop (a solid cube added to the geometry before the
    /// NavMesh bake, so agents route around it), the extraction zone anchor, and (later) markers and the runtime. It builds no
    /// rooms or walls; the layout and the plan decide where things go.
    /// </summary>
    public static class MissionContent
    {
        public const float InteractionRange = 1.8f;
        public const float TerminalSize = 0.8f;
        public const float TerminalHeight = 1.2f;
        public const float ZoneRadius = 2f;
        const int NotWalkableArea = 1;

        public const string VisualRootName = "VisualRoot";
        const string DisplayName = "Display";

        /// <summary>
        /// Adds the terminal under `geometry`. Call it from the builder's pre-bake hook. The root is a plain gameplay object
        /// (collider, Not Walkable modifier, interactable, marker); what the player sees is a child VisualRoot, so the look can
        /// change without touching objective behaviour.
        /// </summary>
        public static MissionInteractable AddTerminal(Transform geometry, ObjectivePlan plan, MissionLayout layout,
            MissionSettings settings, Material material, EnvironmentTheme theme = null) =>
            BuildTerminal(geometry, "Terminal", "Terminal", plan.TerminalTile, layout, settings.interactionSeconds, material, theme);

        /// <summary>
        /// The camera-control terminal: built like the data terminal (solid, Not Walkable, marker) but available from the
        /// start, because using it is the player's choice and no objective opens it. Call it from the builder's pre-bake hook.
        /// </summary>
        public static MissionInteractable AddCameraTerminal(Transform geometry, SecurityPlan security, MissionLayout layout,
            MissionSettings settings, Material material, EnvironmentTheme theme = null)
        {
            var interactable = BuildTerminal(geometry, "CameraTerminal", "Camera control", security.TerminalTile, layout,
                settings.interactionSeconds, material, theme);
            interactable.SetAvailable(true);
            return interactable;
        }

        static MissionInteractable BuildTerminal(Transform geometry, string objectName, string label, Vector2Int tile,
            MissionLayout layout, float seconds, Material material, EnvironmentTheme theme)
        {
            var terminal = new GameObject(objectName);
            terminal.transform.SetParent(geometry, false);
            terminal.transform.position = layout.TileCenter(tile) + Vector3.up * (TerminalHeight * 0.5f);
            var box = terminal.AddComponent<BoxCollider>();
            box.size = new Vector3(TerminalSize, TerminalHeight, TerminalSize);
            // Not walkable, like every obstacle: no island forms on top and the mesh has a hole around it.
            var modifier = terminal.AddComponent<NavMeshModifier>();
            modifier.overrideArea = true;
            modifier.area = NotWalkableArea;
            var interactable = terminal.AddComponent<MissionInteractable>();
            interactable.Initialize(InteractionRange, seconds, label);

            var visualRoot = new GameObject(VisualRootName).transform;
            visualRoot.SetParent(terminal.transform, false);
            visualRoot.localPosition = new Vector3(0f, -TerminalHeight * 0.5f, 0f);
            var tint = AddTerminalVisual(visualRoot, tile, layout, theme, material);
            terminal.AddComponent<ObjectiveMarker>().Bind(() => ObjectiveMarker.TerminalColour(interactable), tint);
            return interactable;
        }

        static Renderer[] AddTerminalVisual(Transform visualRoot, Vector2Int tile, MissionLayout layout, EnvironmentTheme theme, Material material)
        {
            GameObject instance;
            var prefab = theme != null ? theme.Resolve(EnvironmentElement.Terminal, tile, layout.Seed) : null;
            if (prefab != null)
            {
                instance = Object.Instantiate(prefab, visualRoot);
                instance.name = prefab.name;
                if (!instance.activeSelf)
                    instance.SetActive(true);
                instance.transform.localPosition = Vector3.zero;
                instance.transform.localRotation = Quaternion.identity;
            }
            else
            {
                instance = GameObject.CreatePrimitive(PrimitiveType.Cube);
                instance.name = "Placeholder";
                instance.transform.SetParent(visualRoot, false);
                instance.transform.localPosition = new Vector3(0f, TerminalHeight * 0.5f, 0f);
                instance.transform.localScale = new Vector3(TerminalSize, TerminalHeight, TerminalSize);
                if (material != null)
                    instance.GetComponent<Renderer>().sharedMaterial = material;
            }
            EnvironmentVisualBuilder.StripColliders(instance);
            var all = instance.GetComponentsInChildren<Renderer>();
            var displays = all.Where(r => r.gameObject.name == DisplayName).ToArray();
            return displays.Length > 0 ? displays : all;
        }

        /// <summary>
        /// The extraction zone under the mission root, on the floor at the planned tile: a flat disc the size of the zone and
        /// a thin beacon, neither solid. Its marker shows it locked (grey) until CreateRuntime binds it to the objective.
        /// </summary>
        public static Transform CreateZone(MissionLayout layout, ObjectivePlan plan, Transform root)
        {
            var zone = new GameObject("ExtractionZone");
            zone.transform.SetParent(root, false);
            zone.transform.position = layout.TileCenter(plan.ExtractionTile);
            Visual(zone.transform, "Disc", new Vector3(ZoneRadius * 2f, 0.02f, ZoneRadius * 2f), 0.03f);
            Visual(zone.transform, "Beacon", new Vector3(0.25f, 1.5f, 0.25f), 1.5f);
            zone.AddComponent<ObjectiveMarker>().Bind(() => ObjectiveMarker.ZoneColour(ObjectiveState.Inactive));
            return zone.transform;
        }

        /// <summary>
        /// The security cameras under `root`: a small housing and lens per mount (renderers only, never a collider), tinted
        /// grey until the network is hacked and green after. Returns the network the intelligence service reads.
        /// </summary>
        public static CameraNetwork CreateCameras(SecurityPlan security, MissionLayout layout, IntelligenceSettings intelligence,
            Transform root, Material material)
        {
            var specs = new List<CameraSpec>();
            var lenses = new List<Renderer[]>();
            for (var i = 0; i < security.Cameras.Count; i++)
            {
                var position = security.CameraPosition(layout, i);
                var forward = security.CameraForward(i);
                var camera = new GameObject($"SecurityCamera_{i + 1}");
                camera.transform.SetParent(root, false);
                camera.transform.SetPositionAndRotation(position, Quaternion.LookRotation(forward));
                CameraPart(camera.transform, "Housing", Vector3.zero, new Vector3(0.3f, 0.2f, 0.45f), material);
                var lens = CameraPart(camera.transform, "Lens", new Vector3(0f, 0f, 0.27f), new Vector3(0.12f, 0.12f, 0.1f), material);
                lenses.Add(new[] { lens });
                specs.Add(new CameraSpec(security.CameraDeviceId(i), position, forward, intelligence.cameraRange,
                    intelligence.cameraFov * 0.5f, camera));
            }
            var network = new CameraNetwork(specs);
            for (var i = 0; i < specs.Count; i++)
            {
                specs[i].Visual.AddComponent<ObjectiveMarker>().Bind(
                    () => ObjectiveMarker.ZoneColour(network.Compromised ? ObjectiveState.Completed : ObjectiveState.Inactive), lenses[i]);
            }
            return network;
        }

        static Renderer CameraPart(Transform parent, string name, Vector3 localPosition, Vector3 scale, Material material)
        {
            var part = GameObject.CreatePrimitive(PrimitiveType.Cube);
            part.name = name;
            Object.DestroyImmediate(part.GetComponent<Collider>());
            part.transform.SetParent(parent, false);
            part.transform.localPosition = localPosition;
            part.transform.localScale = scale;
            var renderer = part.GetComponent<Renderer>();
            if (material != null)
                renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return renderer;
        }

        static void Visual(Transform parent, string name, Vector3 scale, float height)
        {
            var part = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            part.name = name;
            Object.DestroyImmediate(part.GetComponent<Collider>());
            part.transform.SetParent(parent, false);
            part.transform.localPosition = new Vector3(0f, height, 0f);
            part.transform.localScale = scale;
            var renderer = part.GetComponent<Renderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        /// <summary>
        /// The runtime for a built and populated mission: the goals the settings ask for (eliminate the hostile group, hack the
        /// terminal), and the extraction as a locked reach-zone objective at the planned zone. Not started: the caller starts it.
        /// </summary>
        public static MissionRuntime CreateRuntime(GeneratedMission mission, MissionSettings settings,
            IReadOnlyList<Health> hostileGroup, IReadOnlyList<Health> squad)
        {
            var goals = new List<MissionObjective>();
            if (settings.eliminateHostiles)
                goals.Add(new EliminateHostilesObjective("eliminate", "Eliminate security team", hostileGroup));
            if (mission.Terminal != null)
                goals.Add(new InteractObjective("hack", "Access data terminal", mission.Terminal));
            var extraction = new ReachZoneObjective("extract", "Extraction", mission.ExtractionZone.position, ZoneRadius,
                settings.extractionUnits, squad, isRequired: false);
            if (mission.ExtractionZone.TryGetComponent<ObjectiveMarker>(out var marker))
                marker.Bind(() => ObjectiveMarker.ZoneColour(extraction.State));
            return new MissionRuntime(goals, extraction, squad);
        }
    }
}
