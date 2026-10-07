namespace Blackglass
{
    /// <summary>
    /// Stateless deterministic choices for visual decoration. Nothing here draws from a random stream, so a visual choice can
    /// be added or changed without shifting the layout, the objectives or any other visual choice.
    /// </summary>
    public static class VisualVariants
    {
        const ulong Gamma = 0x9E3779B97F4A7C15UL;

        public static ulong Hash(int seed, int salt, EnvironmentElement element, int x, int y)
        {
            unchecked
            {
                var h = Mix(0xD1B54A32D192ED03UL);
                h = Mix(h + Gamma + (ulong)(uint)seed);
                h = Mix(h + Gamma + (ulong)(uint)salt);
                h = Mix(h + Gamma + (ulong)(uint)(int)element);
                h = Mix(h + Gamma + (ulong)(uint)x);
                h = Mix(h + Gamma + (ulong)(uint)y);
                return h;
            }
        }

        /// <summary>Which of `count` variants this element at this tile uses; -1 when there are none.</summary>
        public static int Pick(int seed, int salt, EnvironmentElement element, UnityEngine.Vector2Int tile, int count)
        {
            if (count <= 0)
                return -1;
            return (int)(Hash(seed, salt, element, tile.x, tile.y) % (ulong)count);
        }

        static ulong Mix(ulong z)
        {
            unchecked
            {
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }
    }
}
