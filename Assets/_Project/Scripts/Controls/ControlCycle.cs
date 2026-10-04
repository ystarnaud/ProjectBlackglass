using System;

namespace Blackglass
{
    /// <summary>Picks the next entry when cycling through a list, skipping entries that are not eligible.</summary>
    public static class ControlCycle
    {
        /// <summary>
        /// Index of the next eligible entry after <paramref name="current"/> in <paramref name="direction"/> (+1 forward,
        /// -1 backward), wrapping around. Returns current when it is the only eligible entry, and -1 when no entry is
        /// eligible. A current outside 0..count-1 (for example -1: not in the list) starts before the first entry going
        /// forward and after the last going backward. Checks each entry at most once.
        /// </summary>
        public static int NextIndex(int count, int current, int direction, Func<int, bool> isEligible)
        {
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count), count, "Count cannot be negative.");
            if (direction != 1 && direction != -1)
                throw new ArgumentOutOfRangeException(nameof(direction), direction, "Direction must be +1 or -1.");
            if (isEligible == null)
                throw new ArgumentNullException(nameof(isEligible));

            var start = current >= 0 && current < count ? current : direction > 0 ? -1 : count;
            for (var step = 1; step <= count; step++)
            {
                var index = ((start + step * direction) % count + count) % count;
                if (isEligible(index))
                    return index;
            }
            return -1;
        }
    }
}
