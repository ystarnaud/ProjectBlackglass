#if UNITY_EDITOR || BLACKGLASS_NET_FIXTURE
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The server's projection of what a client may draw and know about a mission (decision 052). Editor and fixture builds only
    /// (owner ruling D1): a release server does not contain it. First slice: only fog on,
    /// map knowledge Full and objective knowledge All, with no terminal and no cameras; anything else projects nothing. The map is
    /// a whitelist copy (grid size, floor rectangles, boxes, container tiles); the objectives are the known ones, by title.
    /// </summary>
    public static class NetMapProjection
    {
        public static string Problem(IntelligenceSettings settings, bool hasTerminal, bool hasCameras)
        {
            if (settings == null)
                return "no intelligence settings";
            if (!settings.fogEnabled)
                return "fog is off";
            if (settings.map != MapKnowledge.Full)
                return "map knowledge " + settings.map + " is not projected (only Full)";
            if (settings.objectives != ObjectiveKnowledge.All)
                return "objective knowledge " + settings.objectives + " is not projected (only All)";
            if (hasTerminal)
                return "a terminal is not projected in this slice";
            if (hasCameras)
                return "security cameras are not projected in this slice";
            return null;
        }

        public static NetMap Map(MissionLayout layout, IReadOnlyList<Vector2Int> containerTiles, IntelligenceSettings settings,
            bool hasTerminal, bool hasCameras, out string refusal)
        {
            refusal = layout == null ? "no layout" : Problem(settings, hasTerminal, hasCameras);
            if (refusal != null)
                return null;
            var boxes = new List<NetMapBox>(layout.Boxes.Count);
            foreach (var box in layout.Boxes)
                boxes.Add(new NetMapBox(box.Kind, box.Footprint, box.Height));
            var tiles = new List<Vector2Int>();
            if (containerTiles != null)
                tiles.AddRange(containerTiles);
            var map = new NetMap(layout.Width, layout.Height, new List<RectInt>(layout.FloorRects), boxes, tiles);
            refusal = NetMapValidation.Problem(map);
            return refusal == null ? map : null;
        }

        /// <summary>The known objectives in runtime order (goals, then extraction); empty when the configuration is not projected.</summary>
        public static List<NetObjective> Objectives(IReadOnlyList<MissionObjective> objectives, IntelligenceSettings settings,
            bool hasTerminal, bool hasCameras)
        {
            var result = new List<NetObjective>();
            if (objectives == null || Problem(settings, hasTerminal, hasCameras) != null)
                return result;
            foreach (var objective in objectives)
            {
                if (objective == null || !objective.IsKnown || result.Count == NetObjective.MaxCount)
                    continue;
                var title = objective.Title.Length > NetObjective.MaxTitle ? objective.Title.Substring(0, NetObjective.MaxTitle) : objective.Title;
                if (objective is ReachZoneObjective zone)
                    result.Add(new NetObjective(NetObjectiveKind.Extraction, objective.State, title, true, zone.Center, zone.Radius));
                else
                    result.Add(new NetObjective(objective.Type == ObjectiveType.Interact ? NetObjectiveKind.Interact : NetObjectiveKind.Eliminate,
                        objective.State, title, false, Vector3.zero, 0f));
            }
            return result;
        }
    }
}
#endif
