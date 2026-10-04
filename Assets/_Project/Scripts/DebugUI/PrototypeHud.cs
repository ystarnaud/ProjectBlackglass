using UnityEngine;

namespace Blackglass
{
    /// <summary>Debug-only on-screen text (IMGUI). Not production UI. Works while paused.</summary>
    public sealed class PrototypeHud : MonoBehaviour
    {
        const string ControlHints =
            "WASD: pan   Q/E or right-drag: rotate   Wheel: zoom\n" +
            "Right-click ground: move   Right-click dummy: attack\n" +
            "Space: tactical pause";

        [SerializeField] TacticalPause tacticalPause;
        [SerializeField] Health target;
        [SerializeField] string targetLabel = "Training Dummy";

        GUIStyle pausedStyle;

        void OnGUI()
        {
            GUI.Label(new Rect(10f, 10f, 520f, 60f), ControlHints);

            if (target != null)
            {
                var status = target.IsAlive ? $"{target.Current} / {target.Max}" : "destroyed";
                GUI.Label(new Rect(10f, 75f, 320f, 22f), $"{targetLabel}: {status}");
            }

            if (tacticalPause != null && tacticalPause.IsPaused)
            {
                pausedStyle ??= new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.UpperCenter,
                    fontSize = 22,
                    fontStyle = FontStyle.Bold,
                };
                GUI.Label(new Rect(0f, 100f, Screen.width, 40f), "TACTICAL PAUSE - Space to resume", pausedStyle);
            }
        }
    }
}
