using System;
using System.Collections.Generic;

namespace Blackglass
{
    /// <summary>
    /// SplitMix64. Used for every mission layout decision instead of System.Random or UnityEngine.Random, so a seed
    /// means the same mission after any runtime or Unity upgrade and generation never disturbs combat randomness.
    /// A class, so a copy can never silently fork the sequence.
    /// </summary>
    public sealed class SeededRandom
    {
        const ulong Gamma = 0x9E3779B97F4A7C15UL;

        ulong state;

        public SeededRandom(ulong seed) => state = seed;

        /// <summary>The generator for one attempt of one seed: attempt n always gets the same stream.</summary>
        public static SeededRandom ForAttempt(int seed, int attempt) => ForStream(seed, attempt, 0x632BE59BD9B4E019UL);

        /// <summary>
        /// The objective placer's stream for one attempt of one seed: independent of the layout stream, so the layout never
        /// depends on where objectives go and the placement never depends on any layout draw count.
        /// </summary>
        public static SeededRandom ForObjectives(int seed, int attempt) => ForStream(seed, attempt, 0x0B1EC71F0C0FFEE1UL);

        static SeededRandom ForStream(int seed, int attempt, ulong salt)
        {
            unchecked
            {
                // The outer Finish hashes the seed too: without it, seed + 1 would start one step into the stream of seed.
                return new SeededRandom(Finish((ulong)(uint)seed * Gamma + Finish((ulong)(uint)attempt + salt)));
            }
        }

        static ulong Finish(ulong z)
        {
            unchecked
            {
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        public ulong NextULong()
        {
            unchecked
            {
                state += Gamma;
                return Finish(state);
            }
        }

        /// <summary>0 (inclusive) to maxExclusive (exclusive).</summary>
        public int NextInt(int maxExclusive)
        {
            if (maxExclusive <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxExclusive), maxExclusive, "The upper bound must be positive.");
            return (int)(NextULong() % (ulong)maxExclusive);
        }

        /// <summary>min (inclusive) to maxExclusive (exclusive).</summary>
        public int NextInt(int min, int maxExclusive)
        {
            if (maxExclusive <= min)
                throw new ArgumentOutOfRangeException(nameof(maxExclusive), maxExclusive, "The range is empty.");
            return min + NextInt(maxExclusive - min);
        }

        /// <summary>0 (inclusive) to 1 (exclusive), 24 bits of precision.</summary>
        public float NextFloat() => (NextULong() >> 40) / 16777216f;

        public bool Chance(float probability) => NextFloat() < probability;

        /// <summary>Fisher-Yates.</summary>
        public void Shuffle<T>(IList<T> list)
        {
            for (var i = list.Count - 1; i > 0; i--)
            {
                var j = NextInt(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }
    }
}
