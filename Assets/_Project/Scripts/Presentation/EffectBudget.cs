using System;
using UnityEngine;

namespace Blackglass
{
    public enum EffectCategory
    {
        Muzzle,
        Tracer,
        Impact,
        Area,
        Pulse,
    }

    public enum PresentationQuality
    {
        Low,
        Medium,
        High,
    }

    /// <summary>
    /// Explicit caps on concurrently *active* presentation effects and lights. Exhaustion degrades cosmetically: the caller
    /// skips the request. Pure counters, no scene dependency.
    /// </summary>
    public sealed class EffectBudget
    {
        static readonly int[] BaseCaps = { 12, 24, 24, 4, 8 };   // Muzzle, Tracer, Impact, Area, Pulse
        const int BaseLightCap = 6;

        readonly int[] caps = new int[BaseCaps.Length];
        readonly int[] active = new int[BaseCaps.Length];

        public EffectBudget(PresentationQuality quality)
        {
            var scale = quality == PresentationQuality.High ? 1f : quality == PresentationQuality.Medium ? 0.6f : 0.3f;
            for (var i = 0; i < caps.Length; i++)
                caps[i] = Mathf.Max(1, Mathf.RoundToInt(BaseCaps[i] * scale));
            LightCap = Mathf.Max(1, Mathf.RoundToInt(BaseLightCap * scale));
        }

        public int LightCap { get; }
        public int LightsActive { get; private set; }
        /// <summary>Requests refused since the last Reset (one aggregated counter, never logged per frame).</summary>
        public int Refused { get; private set; }

        public int Cap(EffectCategory category) => caps[(int)category];
        public int Active(EffectCategory category) => active[(int)category];

        public bool TryAcquire(EffectCategory category)
        {
            var i = (int)category;
            if (active[i] >= caps[i])
            {
                Refused++;
                return false;
            }
            active[i]++;
            return true;
        }

        public void Release(EffectCategory category)
        {
            var i = (int)category;
            if (active[i] > 0)
                active[i]--;
        }

        public bool TryAcquireLight()
        {
            if (LightsActive >= LightCap)
                return false;
            LightsActive++;
            return true;
        }

        public void ReleaseLight()
        {
            if (LightsActive > 0)
                LightsActive--;
        }

        public void Reset()
        {
            Array.Clear(active, 0, active.Length);
            LightsActive = 0;
            Refused = 0;
        }
    }
}
