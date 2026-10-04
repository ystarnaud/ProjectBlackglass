using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Decides whether a pointer press was a click or a drag, from how far the pointer
    /// moved away from the press point. Once a press becomes a drag it stays a drag.
    /// </summary>
    public sealed class ClickDragDetector
    {
        readonly float thresholdPixels;
        Vector2 pressPosition;

        public ClickDragDetector(float thresholdPixels = 6f)
        {
            if (thresholdPixels < 0f)
                throw new ArgumentOutOfRangeException(nameof(thresholdPixels), thresholdPixels, "Threshold cannot be negative.");
            this.thresholdPixels = thresholdPixels;
        }

        public bool IsPressed { get; private set; }
        public bool IsDragging { get; private set; }

        public void Press(Vector2 position)
        {
            IsPressed = true;
            IsDragging = false;
            pressPosition = position;
        }

        public void Track(Vector2 position)
        {
            if (!IsPressed || IsDragging)
                return;
            if ((position - pressPosition).sqrMagnitude > thresholdPixels * thresholdPixels)
                IsDragging = true;
        }

        /// <summary>Ends the press. Returns true when it was a click, false for a drag or when nothing was pressed.</summary>
        public bool Release(Vector2 position)
        {
            if (!IsPressed)
                return false;
            Track(position);
            var wasClick = !IsDragging;
            IsPressed = false;
            IsDragging = false;
            return wasClick;
        }
    }
}
