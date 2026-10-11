using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// A short decaying wobble for the camera. Strength is 0..1; the offset never exceeds MaxOffset metres. Advanced with
    /// scaled time by its owner, so it holds still while the game is paused. A new impulse replaces the running one only
    /// when it is stronger than what is left of it.
    /// </summary>
    public sealed class ImpulseEnvelope
    {
        public const float MaxOffset = 0.08f;
        public const float Duration = 0.5f;

        float strength;
        float elapsed = Duration;
        float phaseX;
        float phaseY;

        /// <summary>Current strength after decay (0..1).</summary>
        public float Amplitude
        {
            get
            {
                if (elapsed >= Duration)
                    return 0f;
                var left = 1f - elapsed / Duration;
                return strength * left * left;
            }
        }

        public Vector2 Offset
        {
            get
            {
                var amplitude = Amplitude;
                if (amplitude <= 0f)
                    return Vector2.zero;
                return amplitude * MaxOffset * new Vector2(Mathf.Sin(elapsed * 55f + phaseX), Mathf.Sin(elapsed * 71f + phaseY));
            }
        }

        public void Add(float newStrength, float newPhaseX, float newPhaseY)
        {
            newStrength = Mathf.Clamp01(newStrength);
            if (newStrength <= 0f || newStrength <= Amplitude)
                return;
            strength = newStrength;
            elapsed = 0f;
            phaseX = newPhaseX;
            phaseY = newPhaseY;
        }

        public void Advance(float dt)
        {
            if (dt > 0f && elapsed < Duration)
                elapsed += dt;
        }

        public void Reset() => elapsed = Duration;
    }
}
