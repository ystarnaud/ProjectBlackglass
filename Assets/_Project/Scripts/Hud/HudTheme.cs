using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The tactical HUD's colours and sizes (1920x1080 reference units). Dark and restrained: one cool accent for friendly,
    /// selected and ready; one warm accent for cooldown, pause and last known; red for hostile, failed and low health.
    /// </summary>
    public static class HudTheme
    {
        public static readonly Color Panel = new Color(0.04f, 0.05f, 0.07f, 0.78f);
        public static readonly Color PanelEdge = new Color(0.22f, 0.27f, 0.33f, 1f);
        public static readonly Color Text = new Color(0.86f, 0.89f, 0.92f, 1f);
        public static readonly Color TextDim = new Color(0.52f, 0.57f, 0.62f, 1f);
        public static readonly Color Accent = new Color(0.36f, 0.78f, 0.92f, 1f);     // friendly / selected / ready
        public static readonly Color Warn = new Color(0.96f, 0.72f, 0.28f, 1f);       // cooldown, pause, last known
        public static readonly Color Bad = new Color(0.92f, 0.36f, 0.34f, 1f);        // hostile, failed, low health
        public static readonly Color Good = new Color(0.46f, 0.82f, 0.52f, 1f);       // completed
        public const int Margin = 24, FontSmall = 14, FontBody = 16, FontLarge = 22, FontBanner = 34;
        /// <summary>The gap between two stacked zones in one screen corner (the target panel above the prompts).</summary>
        public const float ZoneGap = 16f;
    }
}
