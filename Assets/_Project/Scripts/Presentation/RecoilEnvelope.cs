using UnityEngine;

namespace Blackglass
{
    /// <summary>A weapon kick that eases back over a time the caller advances (scaled time, so it freezes with the pause).</summary>
    public sealed class RecoilEnvelope
    {
        float distance;
        float recover = 1f;
        float elapsed;

        public bool IsActive { get; private set; }

        /// <summary>Metres of backward displacement right now.</summary>
        public float Offset
        {
            get
            {
                if (!IsActive)
                    return 0f;
                var left = 1f - elapsed / recover;
                return distance * left * left;
            }
        }

        public void Kick(float meters, float recoverSeconds)
        {
            distance = Mathf.Max(0f, meters);
            recover = Mathf.Max(0.0001f, recoverSeconds);
            elapsed = 0f;
            IsActive = distance > 0f;
        }

        public void Advance(float dt)
        {
            if (!IsActive || dt <= 0f)
                return;
            elapsed += dt;
            if (elapsed >= recover)
                IsActive = false;
        }

        public void Reset()
        {
            IsActive = false;
            elapsed = 0f;
        }
    }
}
