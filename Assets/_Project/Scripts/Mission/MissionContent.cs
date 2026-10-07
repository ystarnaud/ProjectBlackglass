using System.Collections.Generic;
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

        /// <summary>Adds the terminal under `geometry`. Call it from the builder's pre-bake hook.</summary>
        public static MissionInteractable AddTerminal(Transform geometry, ObjectivePlan plan, MissionLayout layout,
            MissionSettings settings, Material material)
        {
            var terminal = GameObject.CreatePrimitive(PrimitiveType.Cube);
            terminal.name = "Terminal";
            terminal.transform.SetParent(geometry, false);
            terminal.transform.position = layout.TileCenter(plan.TerminalTile) + Vector3.up * (TerminalHeight * 0.5f);
            terminal.transform.localScale = new Vector3(TerminalSize, TerminalHeight, TerminalSize);
            if (material != null)
                terminal.GetComponent<Renderer>().sharedMaterial = material;
            // Not walkable, like every obstacle: no island forms on top and the mesh has a hole around it.
            var modifier = terminal.AddComponent<NavMeshModifier>();
            modifier.overrideArea = true;
            modifier.area = NotWalkableArea;
            var interactable = terminal.AddComponent<MissionInteractable>();
            interactable.Initialize(InteractionRange, settings.interactionSeconds);
            terminal.AddComponent<ObjectiveMarker>().Bind(() => ObjectiveMarker.TerminalColour(interactable));
            return interactable;
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
