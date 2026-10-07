using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Blackglass.Tests
{
    public class SeededRandomTests
    {
        [Test]
        public void SeedZero_StartsWithTheReferenceSplitMix64Value() =>
            Assert.That(new SeededRandom(0UL).NextULong(), Is.EqualTo(0xE220A8397B1DCDAFUL));

        [Test]
        public void SameSeed_GivesTheSameSequence()
        {
            var a = new SeededRandom(42UL);
            var b = new SeededRandom(42UL);
            for (var i = 0; i < 100; i++)
                Assert.That(a.NextULong(), Is.EqualTo(b.NextULong()));
        }

        [Test]
        public void ForAttempt_DiffersByAttemptAndBySeed_AndIsRepeatable()
        {
            Assert.That(SeededRandom.ForAttempt(12345, 1).NextULong(), Is.EqualTo(SeededRandom.ForAttempt(12345, 1).NextULong()));
            Assert.That(SeededRandom.ForAttempt(12345, 1).NextULong(), Is.Not.EqualTo(SeededRandom.ForAttempt(12345, 2).NextULong()));
            Assert.That(SeededRandom.ForAttempt(12345, 1).NextULong(), Is.Not.EqualTo(SeededRandom.ForAttempt(12346, 1).NextULong()));
        }

        [Test]
        public void ForAttempt_AdjacentSeeds_DoNotShareAShiftedStream()
        {
            const int length = 8;
            ulong[] Draw(int seed, int attempt)
            {
                var rng = SeededRandom.ForAttempt(seed, attempt);
                var values = new ulong[length];
                for (var i = 0; i < length; i++)
                    values[i] = rng.NextULong();
                return values;
            }

            for (var seed = 1; seed <= 50; seed++)
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                var a = Draw(seed, attempt);
                var b = Draw(seed + 1, attempt);
                Assert.That(a[0], Is.Not.EqualTo(b[0]), $"seed {seed} attempt {attempt}");
                for (var k = 1; k < length; k++)
                {
                    Assert.That(a[k], Is.Not.EqualTo(b[0]), $"seed {seed} attempt {attempt} shift {k}");
                    Assert.That(b[k], Is.Not.EqualTo(a[0]), $"seed {seed} attempt {attempt} shift -{k}");
                }
            }
        }

        [Test]
        public void NextInt_StaysInRange_AndCoversIt()
        {
            var rng = new SeededRandom(7UL);
            var seen = new HashSet<int>();
            for (var i = 0; i < 500; i++)
            {
                var value = rng.NextInt(3, 8);
                Assert.That(value, Is.InRange(3, 7));
                seen.Add(value);
            }
            Assert.That(seen, Has.Count.EqualTo(5));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => rng.NextInt(0));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => rng.NextInt(5, 5));
        }

        [Test]
        public void NextFloat_IsInZeroToOne_AndChanceRespectsTheExtremes()
        {
            var rng = new SeededRandom(9UL);
            for (var i = 0; i < 500; i++)
                Assert.That(rng.NextFloat(), Is.GreaterThanOrEqualTo(0f).And.LessThan(1f));
            Assert.That(Enumerable.Range(0, 100).All(_ => !rng.Chance(0f)), Is.True);
            Assert.That(Enumerable.Range(0, 100).All(_ => rng.Chance(1f)), Is.True);
        }

        [Test]
        public void Shuffle_KeepsTheSameItems_AndIsRepeatable()
        {
            var a = Enumerable.Range(0, 20).ToList();
            var b = Enumerable.Range(0, 20).ToList();
            new SeededRandom(5UL).Shuffle(a);
            new SeededRandom(5UL).Shuffle(b);
            Assert.That(a, Is.EqualTo(b));
            Assert.That(a.OrderBy(x => x), Is.EqualTo(Enumerable.Range(0, 20)));
            Assert.That(a, Is.Not.EqualTo(Enumerable.Range(0, 20).ToList()));
        }

        [Test]
        public void ForObjectives_IsDeterministic_AndIndependentOfTheLayoutStream()
        {
            Assert.That(SeededRandom.ForObjectives(5, 1).NextULong(), Is.EqualTo(SeededRandom.ForObjectives(5, 1).NextULong()));
            Assert.That(SeededRandom.ForObjectives(5, 1).NextULong(), Is.Not.EqualTo(SeededRandom.ForAttempt(5, 1).NextULong()));
            Assert.That(SeededRandom.ForObjectives(5, 1).NextULong(), Is.Not.EqualTo(SeededRandom.ForObjectives(5, 2).NextULong()));
            Assert.That(SeededRandom.ForObjectives(5, 1).NextULong(), Is.Not.EqualTo(SeededRandom.ForObjectives(6, 1).NextULong()));
        }
    }
}
