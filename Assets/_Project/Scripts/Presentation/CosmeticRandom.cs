using System;

namespace Blackglass
{
    /// <summary>
    /// The random source for purely cosmetic choices (spark spread, miss deflection, shake phase). It is its own
    /// System.Random: it never reads or advances Unity's shared random stream, so gameplay, generation and loot are unaffected
    /// by whether presentation runs.
    /// </summary>
    public sealed class CosmeticRandom
    {
        readonly Random random;

        public CosmeticRandom(int seed) => random = new Random(seed);

        public CosmeticRandom() : this(Environment.TickCount ^ 0x5bd1e995) { }

        /// <summary>0 (inclusive) to 1 (exclusive).</summary>
        public float Value() => (float)random.NextDouble();

        public float Range(float min, float max) => min + (max - min) * Value();

        public bool Coin() => random.Next(2) == 0;

        /// <summary>-1 or +1.</summary>
        public float Sign() => Coin() ? 1f : -1f;
    }
}
