using UnityEngine;

namespace Blackglass
{
    /// <summary>Screen-space maths for box selection. Screen coordinates are pixels with the origin bottom-left.</summary>
    public static class ScreenBox
    {
        /// <summary>The rectangle spanned by two corners given in any order.</summary>
        public static Rect FromCorners(Vector2 a, Vector2 b) =>
            Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));

        /// <summary>
        /// True when a point from Camera.WorldToScreenPoint lies inside the box (edges included)
        /// and in front of the camera (positive z).
        /// </summary>
        public static bool Contains(Rect box, Vector3 screenPoint) =>
            screenPoint.z > 0f &&
            screenPoint.x >= box.xMin && screenPoint.x <= box.xMax &&
            screenPoint.y >= box.yMin && screenPoint.y <= box.yMax;

        /// <summary>Converts a screen rectangle to IMGUI coordinates (origin top-left).</summary>
        public static Rect ToGuiRect(Rect screenRect, float screenHeight) =>
            new Rect(screenRect.xMin, screenHeight - screenRect.yMax, screenRect.width, screenRect.height);
    }
}
