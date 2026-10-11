using System.Collections.Generic;

namespace Blackglass
{
    /// <summary>Chooses which voice a new sound uses.</summary>
    public static class VoicePicker
    {
        /// <summary>
        /// A free voice; else the least important busy voice (largest priority number, oldest first) that is not more
        /// important than the new sound; else -1 (the new sound is dropped).
        /// </summary>
        public static int Pick(IReadOnlyList<bool> busy, IReadOnlyList<int> priority, IReadOnlyList<long> order, int newPriority)
        {
            for (var i = 0; i < busy.Count; i++)
                if (!busy[i])
                    return i;
            var best = -1;
            for (var i = 0; i < busy.Count; i++)
            {
                if (priority[i] < newPriority)
                    continue;
                if (best < 0 || priority[i] > priority[best] || (priority[i] == priority[best] && order[i] < order[best]))
                    best = i;
            }
            return best;
        }
    }
}
