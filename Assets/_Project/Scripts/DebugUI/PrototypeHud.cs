using UnityEngine;

namespace Blackglass
{
    /// <summary>Debug-only on-screen text (IMGUI). Not production UI. Works while paused.</summary>
    public sealed class PrototypeHud : MonoBehaviour
    {
        const string ControlHints =
            "WASD: pan   Q/E: rotate   Right-drag: rotate/tilt   Wheel: zoom\n" +
            "Left-click unit: select (Shift: add/remove)   Left-drag: box select   Esc: clear selection\n" +
            "Left-click ground: move   Left-click dummy: attack   Shift: queue the order   X: stop\n" +
            "Space: tactical pause";
        // Order labels float this far above a unit's centre (the capsule is 2 m tall).
        const float UnitLabelHeight = 1.5f;

        [SerializeField] TacticalPause tacticalPause;
        [SerializeField] Health target;
        [SerializeField] string targetLabel = "Training Dummy";
        [SerializeField] Camera viewCamera;
        [SerializeField] UnitSelection selection;
        [SerializeField] PlayerCommandInput commandInput;

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

        void OnGUI()
        {
            GUI.Label(new Rect(10f, 10f, 640f, 80f), ControlHints);

            if (target != null)
            {
                var status = target.IsAlive ? $"{target.Current} / {target.Max}" : "destroyed";
                GUI.Label(new Rect(10f, 95f, 320f, 22f), $"{targetLabel}: {status}");
            }

            if (selection != null)
                GUI.Label(new Rect(10f, 115f, 320f, 22f), $"Selected: {selection.Selected.Count}");

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
                GUI.Label(new Rect(0f, 140f, Screen.width, 40f), "TACTICAL PAUSE - Space to resume", pausedStyle);
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
