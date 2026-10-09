using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Answers "is this screen point over a HUD panel?" for the pointer blocker and the hover target. The HUD registers the
    /// rects that take space (panels, the squad's cards, the chips); a rect counts only while it is active in the
    /// hierarchy, so a hidden panel never blocks. Points are screen pixels: the HUD canvas is screen-space overlay.
    /// </summary>
    public sealed class HudPointerGate
    {
        readonly List<RectTransform> rects = new List<RectTransform>();

        public void Register(RectTransform r)
        {
            if (r == null)
                throw new ArgumentNullException(nameof(r));
            if (!rects.Contains(r))
                rects.Add(r);
        }

        public bool IsOver(Vector2 screen)
        {
            for (var i = 0; i < rects.Count; i++)
            {
                var rect = rects[i];
                if (rect != null && rect.gameObject.activeInHierarchy
                    && RectTransformUtility.RectangleContainsScreenPoint(rect, screen, null))
                    return true;
            }
            return false;
        }
    }
}
