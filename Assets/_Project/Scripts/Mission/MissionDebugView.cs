using System;
using System.Text;
using UnityEngine;

namespace Blackglass
{
    /// <summary>The text the mission debug view shows. Pure, so it is tested without a scene.</summary>
    public static class MissionDebugText
    {
        public const string Hints = "F6: regenerate this seed   F7: new random seed   (set a seed in the MissionDirector Inspector, then F6)";

        public static string Describe(MissionState state, MissionReport report)
        {
            var text = new StringBuilder();
            text.Append(FormattableString.Invariant($"Mission: {state} | Seed {report.Seed}"));
            if (report.AttemptsMade > 0)
                text.Append(FormattableString.Invariant($" | attempt {report.AttemptsMade}/{report.MaxAttempts}"));
            if (state == MissionState.Ready)
            {
                text.Append(FormattableString.Invariant($"\n{report.Rooms} rooms, {report.Connections} connections, floor {report.FloorTiles} m2"));
                text.Append(FormattableString.Invariant($"\nNav {report.NavArea:0} m2, {report.PathsChecked} paths ok"));
                text.Append(FormattableString.Invariant($"\nCover {report.CoverTotal} (low {report.CoverLow}, tall {report.CoverTall}, corner {report.CoverCorner}, column {report.CoverColumn})"));
                text.Append(FormattableString.Invariant($"\nSpawns {report.FriendlySpawns} friendly / {report.HostileSpawns} hostile, {report.TeamSeparation:0.0} m apart"));
                text.Append(FormattableString.Invariant($"\nBounds {report.Bounds.size.x:0} x {report.Bounds.size.z:0} m"));
            }
            else if (state == MissionState.Failed && !string.IsNullOrEmpty(report.Failure))
            {
                text.Append('\n').Append(report.Failure);
            }
            return text.ToString();
        }

        /// <summary>The state line only: no room, spawn, cover or bounds data while the fog hides the mission.</summary>
        public static string DescribeHidden(MissionState state, MissionReport report)
        {
            var text = FormattableString.Invariant($"Mission: {state} | Seed {report.Seed}");
            if (report.AttemptsMade > 0)
                text += FormattableString.Invariant($" | attempt {report.AttemptsMade}/{report.MaxAttempts}");
            if (state == MissionState.Failed && !string.IsNullOrEmpty(report.Failure))
                text += "\n" + report.Failure;
            return text;
        }
    }

    /// <summary>Debug-only IMGUI text about the generated mission. Not production UI. Works while paused.</summary>
    public sealed class MissionDebugView : MonoBehaviour
    {
        [SerializeField] MissionDirector director;
        // Optional: with fog on, the room, spawn and cover data stay out of the text (decision 037).
        [SerializeField] IntelligenceService intelligence;

        GUIStyle style;

        internal void SetIntelligence(IntelligenceService service) => intelligence = service;

        // The developer overlay (F1) shows or hides this debug text; no overlay assigned means shown.
        [SerializeField] DeveloperOverlay overlay;

        internal void SetOverlay(DeveloperOverlay o) => overlay = o;
        internal bool IsDrawing => DeveloperOverlay.Shows(overlay);

        void OnGUI()
        {
            if (!IsDrawing)
                return;
            if (director == null)
                return;
            style ??= new GUIStyle(GUI.skin.label) { fontSize = 14, alignment = TextAnchor.UpperRight, wordWrap = true };
            var hidden = intelligence != null && intelligence.IsFogActive && !intelligence.TruthView;
            var text = (hidden ? MissionDebugText.DescribeHidden(director.State, director.Report) : MissionDebugText.Describe(director.State, director.Report)) + "\n" + MissionDebugText.Hints;
            var width = Mathf.Min(560f, Screen.width * 0.45f);
            var area = new Rect(Screen.width - width - 10f, 10f, width, Screen.height * 0.5f);
            GUI.color = Color.black;
            GUI.Label(new Rect(area.x + 1f, area.y + 1f, area.width, area.height), text, style);
            GUI.color = Color.white;
            GUI.Label(area, text, style);
        }
    }
}
