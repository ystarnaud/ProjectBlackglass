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
            return interactable;
        }

        /// <summary>The extraction zone's anchor under the mission root, on the floor at the planned tile.</summary>
        public static Transform CreateZone(MissionLayout layout, ObjectivePlan plan, Transform root)
        {
            var zone = new GameObject("ExtractionZone");
            zone.transform.SetParent(root, false);
            zone.transform.position = layout.TileCenter(plan.ExtractionTile);
            return zone.transform;
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
            return new MissionRuntime(goals, extraction, squad);
        }
    }
}
