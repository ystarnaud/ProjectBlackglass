using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Blackglass
{
    /// <summary>The text of the mission panel, banner and marker labels. Pure, so it is tested without a scene.</summary>
    public static class MissionHudText
    {
        public static string Line(MissionObjective objective) => $"{Prefix(objective)} {objective.Describe()}";

        static string Prefix(MissionObjective objective)
        {
            switch (objective.State)
            {
                case ObjectiveState.Completed:
                    return "[x]";
                case ObjectiveState.Failed:
                    return "[FAILED]";
                case ObjectiveState.Inactive:
                    return "[LOCKED]";
                default:
                    return "[ ]";
            }
        }

        public static string PhaseLabel(MissionPhase phase)
        {
            switch (phase)
            {
                case MissionPhase.Active:
                    return "in progress";
                case MissionPhase.ExtractionOpen:
                    return "extraction open";
                case MissionPhase.Success:
                    return "success";
                case MissionPhase.Failure:
                    return "failed";
                default:
                    return "-";
            }
        }

        /// <summary>
        /// The MISSION panel: the phase, then one line per objective the player knows about. With `listUnknown`, an unknown
        /// objective that has a vague title is listed by it ("[?] Locate the data terminal"), never by its real title.
        /// </summary>
        public static string Panel(MissionRuntime runtime, bool listUnknown = false)
        {
            var text = new StringBuilder("MISSION - ").Append(PhaseLabel(runtime.Phase));
            foreach (var objective in runtime.Objectives)
            {
                if (objective.IsKnown)
                    text.Append('\n').Append(Line(objective));
                else if (listUnknown && !string.IsNullOrEmpty(objective.VagueTitle))
                    text.Append("\n[?] ").Append(objective.VagueTitle);
            }
            return text.ToString();
        }

        public static string Banner(MissionPhase phase)
        {
            switch (phase)
            {
                case MissionPhase.Success:
                    return "MISSION SUCCESS - squad extracted";
                case MissionPhase.Failure:
                    return "MISSION FAILED";
                default:
                    return string.Empty;
            }
        }

        /// <summary>The label drawn at an objective's world marker; empty when it has none to show.</summary>
        public static string MarkerLabel(MissionObjective objective)
        {
            if (!objective.IsKnown || !objective.HasTarget || objective.State == ObjectiveState.Completed)
                return string.Empty;
            return objective.State == ObjectiveState.Inactive ? $"{objective.Title} (locked)" : objective.Title;
        }
    }

    /// <summary>
    /// Debug-only IMGUI for the mission: the objective panel, the success/failure banner, the context prompt when a terminal
    /// is within reach, and a label over each known objective's marker. Reads only director.Runtime (nothing is kept here).
    /// Not production UI. Works while paused.
    /// </summary>
    public sealed class MissionHud : MonoBehaviour
    {
        [SerializeField] MissionDirector director;
        [SerializeField] PlayerCommandInput commandInput;
        [SerializeField] Camera viewCamera;
        [SerializeField] ActiveInputDevice inputDevice;
        [SerializeField] InputActionAsset controls;
        // Optional: with fog on and showUnknownObjectives, unknown objectives are listed by their vague titles.
        [SerializeField] IntelligenceService intelligence;

        internal void SetIntelligence(IntelligenceService service) => intelligence = service;

        // The developer overlay (F1) shows or hides this debug text; no overlay assigned means shown.
        [SerializeField] DeveloperOverlay overlay;

        internal void SetOverlay(DeveloperOverlay o) => overlay = o;
        internal bool IsDrawing => DeveloperOverlay.Shows(overlay);

        GUIStyle panelStyle;
        GUIStyle bannerStyle;
        GUIStyle labelStyle;

        void OnGUI()
        {
            if (!IsDrawing)
                return;
            if (director == null || director.Runtime == null)
                return;
            var runtime = director.Runtime;
            panelStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 14 };
            bannerStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperCenter, fontSize = 30, fontStyle = FontStyle.Bold };
            labelStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperCenter };

            var panel = MissionHudText.Panel(runtime, intelligence != null && intelligence.ListsUnknownObjectives);
            var lines = panel.Split('\n').Length;
            GUI.Label(new Rect(10f, 190f, 420f, 24f * lines), panel, panelStyle);

            var banner = MissionHudText.Banner(runtime.Phase);
            if (banner.Length > 0)
                GUI.Label(new Rect(0f, 255f, Screen.width, 50f), banner, bannerStyle);

            DrawPrompt(runtime);
            DrawMarkerLabels(runtime);
        }

        // "R: Interact with Terminal" or, for a controller, the Confirm button's label.
        void DrawPrompt(MissionRuntime runtime)
        {
            if (commandInput == null || runtime.IsOver)
                return;
            var family = inputDevice != null ? inputDevice.Family : InputFamily.KeyboardMouse;
            var terminal = commandInput.PromptInteractable(family.IsController());
            if (terminal == null)
                return;
            var path = family.IsController() ? "Commands/Confirm" : "Commands/Interact";
            var key = controls != null ? PromptResolver.GetPrompt(controls.FindAction(path), family) : "-";
            GUI.Label(new Rect(0f, Screen.height - 80f, Screen.width, 30f), $"{key}: Interact with {terminal.DisplayName}", labelStyle);
        }

        void DrawMarkerLabels(MissionRuntime runtime)
        {
            if (viewCamera == null)
                return;
            foreach (var objective in runtime.Objectives)
            {
                var label = MissionHudText.MarkerLabel(objective);
                if (label.Length == 0)
                    continue;
                var screen = viewCamera.WorldToScreenPoint(objective.TargetPosition + Vector3.up * 2f);
                if (screen.z <= 0f)
                    continue;
                GUI.Label(new Rect(screen.x - 110f, Screen.height - screen.y - 22f, 220f, 24f), label, labelStyle);
            }
        }
    }
}
