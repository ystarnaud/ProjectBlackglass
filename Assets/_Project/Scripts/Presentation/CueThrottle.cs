using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Limits how often one cue may start and lowers the gain of crowded plays, so several soldiers firing together do not
    /// stack volume. Pure; the caller supplies the clock.
    /// </summary>
    public sealed class CueThrottle
    {
        public const float CrowdWindow = 0.15f;

        // Recent plays, pruned to the crowd window; used only for the crowd gain.
        readonly Dictionary<int, List<float>> plays = new Dictionary<int, List<float>>();
        // The last accepted play per cue; used only for the min-interval check, so it is independent of the crowd window.
        readonly Dictionary<int, float> lastPlay = new Dictionary<int, float>();

        public bool TryPlay(int key, float now, float minInterval, out float gain)
        {
            gain = 1f;
            if (lastPlay.TryGetValue(key, out var last) && now - last < minInterval)
                return false;
            if (!plays.TryGetValue(key, out var list))
                plays[key] = list = new List<float>();
            for (var i = list.Count - 1; i >= 0; i--)
                if (list[i] < now - CrowdWindow)
                    list.RemoveAt(i);
            gain = 1f / Mathf.Sqrt(list.Count + 1);
            list.Add(now);
            lastPlay[key] = now;
            return true;
        }

        public void Clear()
        {
            plays.Clear();
            lastPlay.Clear();
        }
    }
}
