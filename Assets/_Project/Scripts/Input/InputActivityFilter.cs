using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// What counts as "the player used this device". Sticks count only when they cross a high engage threshold (well above
    /// the dead zone) and only once per crossing, so drift, tiny analog values and a stick left deflected never claim the
    /// active family. Buttons count on the press edge, so a button that stays held while its pad keeps reporting does not
    /// either. Mouse jitter below a few pixels is ignored. Pure logic, no Input System types.
    /// </summary>
    public sealed class InputActivityFilter
    {
        public const float DefaultStickEngage = 0.55f;
        public const float DefaultStickRelease = 0.35f;
        public const float DefaultButtonPress = 0.5f;
        public const float DefaultMouseDeltaPixels = 4f;

        readonly float stickEngage;
        readonly float stickRelease;
        readonly float buttonPress;
        readonly float mouseDeltaPixels;
        readonly Dictionary<int, bool> engagedSticks = new Dictionary<int, bool>();

        public InputActivityFilter(float stickEngage = DefaultStickEngage, float stickRelease = DefaultStickRelease,
            float buttonPress = DefaultButtonPress, float mouseDeltaPixels = DefaultMouseDeltaPixels)
        {
            if (stickRelease > stickEngage)
                throw new ArgumentOutOfRangeException(nameof(stickRelease), stickRelease, "Release must not exceed engage.");
            this.stickEngage = stickEngage;
            this.stickRelease = stickRelease;
            this.buttonPress = buttonPress;
            this.mouseDeltaPixels = mouseDeltaPixels;
        }

        /// <summary>
        /// True once when the stick crosses the engage threshold from a released state. It stays engaged (and returns
        /// false) until the stick falls below the release threshold.
        /// </summary>
        public bool IsStickEngagement(int stickKey, Vector2 value)
        {
            var magnitude = value.magnitude;
            engagedSticks.TryGetValue(stickKey, out var engaged);
            if (engaged)
            {
                if (magnitude < stickRelease)
                    engagedSticks[stickKey] = false;
                return false;
            }
            if (magnitude < stickEngage)
                return false;
            engagedSticks[stickKey] = true;
            return true;
        }

        /// <summary>True when a button's value crosses the press point upward in this event.</summary>
        public bool IsButtonPress(float before, float after) => before < buttonPress && after >= buttonPress;

        public bool IsMouseMotion(Vector2 delta) => delta.sqrMagnitude >= mouseDeltaPixels * mouseDeltaPixels;

        public bool IsScroll(Vector2 scroll) => scroll != Vector2.zero;

        public void Reset() => engagedSticks.Clear();
    }
}
