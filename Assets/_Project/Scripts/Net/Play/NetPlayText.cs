using UnityEngine;

namespace Blackglass
{
    /// <summary>The play HUD's words and panel rectangles (IMGUI screen space: origin top left). Pure.</summary>
    public static class NetPlayText
    {
        public const float SquadRowHeight = 26f;
        public static readonly Rect ObjectivesPanel = new Rect(10f, 40f, 420f, 130f);

        public static Rect SquadPanel(int squadCount, float screenHeight)
        {
            var height = 30f + SquadRowHeight * Mathf.Max(1, squadCount);
            return new Rect(10f, screenHeight - height - 40f, 420f, height);
        }

        /// <summary>True when a click at this IMGUI point lands on a HUD panel and must not command the world.</summary>
        public static bool Covers(Vector2 guiPoint, int squadCount, float screenHeight) =>
            ObjectivesPanel.Contains(guiPoint) || SquadPanel(squadCount, screenHeight).Contains(guiPoint);

        public static string ObjectiveLine(NetObjective objective)
        {
            switch (objective.State)
            {
                case ObjectiveState.Completed:
                    return "[x] " + objective.Title;
                case ObjectiveState.Failed:
                    return "[!] " + objective.Title + " (failed)";
                case ObjectiveState.Inactive:
                    return "[ ] " + objective.Title + (objective.Kind == NetObjectiveKind.Extraction ? " (locked)" : " (not yet)");
                default:
                    return "[ ] " + objective.Title + (objective.Kind == NetObjectiveKind.Extraction ? " (open: reach the green zone)" : string.Empty);
            }
        }

        public static string ResultBanner(NetMissionResult result) =>
            result == NetMissionResult.Success ? "MISSION SUCCESS" : result == NetMissionResult.Failure ? "MISSION FAILED" : null;

        public static string MapStatus(NetClientRecord record) =>
            record.Map != null ? null : record.MapProblem != null ? "Map refused: " + record.MapProblem : "No map authorized for this mission";

        public const string Help =
            "Left click: select / move / attack   Shift: queue a move, or add/remove an operative   X: stop   Tab: next   Ctrl+A: all   F: follow (WASD steers)   Esc: free camera   " +
            "WASD: pan   Q/E or right-drag: rotate   Wheel: zoom";
    }
}
