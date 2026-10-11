using System.Collections.Generic;

namespace Blackglass
{
    /// <summary>Rejects a presentation record that was already delivered (same emitter, same or older sequence).</summary>
    public sealed class SequenceFilter
    {
        readonly Dictionary<int, int> last = new Dictionary<int, int>();

        public bool TryAccept(int emitterId, int sequence)
        {
            if (last.TryGetValue(emitterId, out var seen) && sequence <= seen)
                return false;
            last[emitterId] = sequence;
            return true;
        }

        public void Clear() => last.Clear();
    }
}
