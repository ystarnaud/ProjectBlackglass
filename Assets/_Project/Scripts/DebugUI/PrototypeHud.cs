using UnityEngine;
using UnityEngine.Serialization;

namespace Blackglass
{
    /// <summary>Debug-only on-screen text (IMGUI). Not production UI. Works while paused.</summary>
    public sealed class PrototypeHud : MonoBehaviour
    {
        const string ControlHints =
            "WASD: pan camera   Q/E: rotate   Right-drag: rotate/tilt   Wheel: zoom   V: takeover (WASD moves character)\n" +
            "Left-click unit: select (Shift: add/remove)   Left-drag: box select   Esc: clear selection\n" +
            "Left-click ground/dummy: controlled character moves/attacks (paused: selected units)   Shift: queue   X: stop selected\n" +
            "Space: tactical pause   Tab / Shift+Tab: switch controlled character";
        // Order labels float this far above a unit's centre (the capsule is 2 m tall).
        const float UnitLabelHeight = 1.5f;

        [SerializeField] TacticalPause tacticalPause;
        [SerializeField] Health target;
        [SerializeField] string targetLabel = "Training Dummy";
        [SerializeField] Camera viewCamera;
        [SerializeField] UnitSelection selection;
        [SerializeField] PlayerCommandInput commandInput;
        [SerializeField, FormerlySerializedAs("primary")] ActiveCharacter activeCharacter;

        GUIStyle pausedStyle;
        GUIStyle unitLabelStyle;

        /// <summary>Short summary of a unit's orders, such as "Move +2". Empty when idle.</summary>
        internal static string DescribeOrders(UnitCommand current, int pendingCount)
        {
            if (current == null)
                return string.Empty;
            var orderName = current.GetType().Name;
            if (orderName.EndsWith("Command"))
                orderName = orderName.Substring(0, orderName.Length - "Command".Length);
            return pendingCount > 0 ? $"{orderName} +{pendingCount}" : orderName;
        }

        /// <summary>One status line for the active character, such as "Controlled: Ana | Takeover ON (V) | Manual control".</summary>
        internal static string DescribeActive(string unitName, bool takeoverOn, bool isPaused, bool hasOrders)
        {
            var mode = !takeoverOn ? "Takeover OFF (V)" : isPaused ? "Takeover ON (after pause)" : "Takeover ON (V)";
            var activity = hasOrders ? "Following orders" : takeoverOn && !isPaused ? "Manual control" : "Idle";
            return $"Controlled: {unitName} | {mode} | {activity}";
        }

        void OnGUI()
        {
            GUI.Label(new Rect(10f, 10f, 820f, 80f), ControlHints);

            if (target != null)
            {
                var status = target.IsAlive ? $"{target.Current} / {target.Max}" : "destroyed";
                GUI.Label(new Rect(10f, 95f, 320f, 22f), $"{targetLabel}: {status}");
            }

            if (selection != null)
                GUI.Label(new Rect(10f, 115f, 320f, 22f), $"Selected: {selection.Selected.Count}");

            if (activeCharacter != null && activeCharacter.HasUnit)
            {
                var unit = activeCharacter.Unit;
                GUI.Label(new Rect(10f, 135f, 640f, 22f),
                    DescribeActive(unit.name, activeCharacter.IsTakeoverOn, activeCharacter.IsPaused, unit.CurrentCommand != null));
            }

            DrawUnitLabels();

            if (commandInput != null && commandInput.IsDragging)
                GUI.Box(ScreenBox.ToGuiRect(commandInput.DragRect, Screen.height), GUIContent.none);

            if (tacticalPause != null && tacticalPause.IsPaused)
            {
                pausedStyle ??= new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.UpperCenter,
                    fontSize = 22,
                    fontStyle = FontStyle.Bold,
                };
                GUI.Label(new Rect(0f, 165f, Screen.width, 40f), "TACTICAL PAUSE - Space to resume", pausedStyle);
            }
        }

        void DrawUnitLabels()
        {
            if (selection == null || viewCamera == null)
                return;
            unitLabelStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter };
            foreach (var selectable in selection.Roster)
            {
                if (selectable == null || !selectable.isActiveAndEnabled)
                    continue;
                var unit = selectable.Unit;
                var text = DescribeOrders(unit.CurrentCommand, unit.PendingCommands.Count);
                if (text.Length == 0)
                    continue;
                var screen = viewCamera.WorldToScreenPoint(unit.transform.position + Vector3.up * UnitLabelHeight);
                if (screen.z <= 0f)
                    continue;
                GUI.Label(new Rect(screen.x - 60f, Screen.height - screen.y - 11f, 120f, 22f), text, unitLabelStyle);
            }
        }
    }
}
