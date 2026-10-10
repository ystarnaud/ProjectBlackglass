using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Debug view of the controller's tactical cursor (IMGUI, placeholder look): a crosshair with a label naming what it is
    /// on, a bracket on the cover location or ground point it has chosen, and a bracket on the soft target. Shown only
    /// while the cursor is active. Works while paused. Not production UI.
    /// </summary>
    public sealed class TacticalCursorView : MonoBehaviour
    {
        [SerializeField] TacticalCursor cursor;
        [SerializeField] Camera viewCamera;
        [SerializeField, Min(8f)] float crosshairSize = 28f;
        // The developer overlay (F1) shows or hides the right-stick text; no overlay assigned means shown.
        // The crosshair and brackets are player-facing and stay drawn either way.
        [SerializeField] DeveloperOverlay overlay;

        internal void SetOverlay(DeveloperOverlay o) => overlay = o;
        internal bool IsDrawing => DeveloperOverlay.Shows(overlay);

        /// <summary>A short name for the target, e.g. "Hostile HostileUnit_1". Empty for nothing.</summary>
        internal static string Describe(PointerTarget target)
        {
            switch (target.Kind)
            {
                case PointerTargetKind.Friendly:
                    return $"Friendly {target.Friendly.name}";
                case PointerTargetKind.Hostile:
                    return $"Hostile {target.Hostile.name}";
                case PointerTargetKind.Cover:
                    return $"Cover {target.Cover.Name}";
                case PointerTargetKind.Interactable:
                    return $"Interact {target.Interactable.DisplayName}";
                case PointerTargetKind.Ground:
                    return "Ground";
                default:
                    return string.Empty;
            }
        }

        /// <summary>Who owns the right stick right now, for the debug HUD.</summary>
        internal static string DescribeRightStick(bool cursorActive) =>
            cursorActive ? "Right stick: Cursor" : "Right stick: Camera";

        void OnGUI()
        {
            if (cursor == null)
                return;
            // Below the debug HUD's prompt lines (y = 10, 30, 50).
            if (IsDrawing)
                GUI.Label(new Rect(Screen.width - 560f, 70f, 550f, 22f), DescribeRightStick(cursor.IsActive));
            if (viewCamera == null)
                return;

            var soft = cursor.SoftTarget;
            if (soft != null)
                DrawBracket(viewCamera.WorldToScreenPoint(soft.transform.position), 64f);
            if (!cursor.IsActive)
                return;

            var target = cursor.Target;
            if (target.Kind == PointerTargetKind.Cover)
                DrawBracket(viewCamera.WorldToScreenPoint(target.Cover.Position), 40f);

            var position = cursor.ScreenPosition;
            var x = position.x;
            var y = Screen.height - position.y;
            GUI.Box(new Rect(x - crosshairSize * 0.5f, y - 1f, crosshairSize, 2f), GUIContent.none);
            GUI.Box(new Rect(x - 1f, y - crosshairSize * 0.5f, 2f, crosshairSize), GUIContent.none);
            var text = Describe(target);
            if (text.Length > 0)
                GUI.Label(new Rect(x + 16f, y + 12f, 280f, 22f), text);
        }

        static void DrawBracket(Vector3 screen, float size)
        {
            if (screen.z <= 0f)
                return;
            GUI.Box(new Rect(screen.x - size * 0.5f, Screen.height - screen.y - size * 0.5f, size, size), GUIContent.none);
        }
    }
}
