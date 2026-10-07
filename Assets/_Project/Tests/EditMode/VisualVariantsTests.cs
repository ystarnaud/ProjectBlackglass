using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class VisualVariantsTests
    {
        [Test]
        public void Pick_IsDeterministic_AndInRange()
        {
            for (var x = 0; x < 20; x++)
            {
                var a = VisualVariants.Pick(5, 1, EnvironmentElement.Crate, new Vector2Int(x, 3), 3);
                var b = VisualVariants.Pick(5, 1, EnvironmentElement.Crate, new Vector2Int(x, 3), 3);
                Assert.That(a, Is.EqualTo(b));
                Assert.That(a, Is.InRange(0, 2));
            }
        }

        [Test]
        public void Pick_WithNoVariants_IsMinusOne() =>
            Assert.That(VisualVariants.Pick(5, 1, EnvironmentElement.Crate, Vector2Int.zero, 0), Is.EqualTo(-1));

        [Test]
        public void Pick_UsesEveryVariant_OverManyTiles()
        {
            var picks = Enumerable.Range(0, 300).Select(i =>
                VisualVariants.Pick(9, 0, EnvironmentElement.LowCover, new Vector2Int(i % 20, i / 20), 3)).Distinct().ToList();

            Assert.That(picks, Is.EquivalentTo(new[] { 0, 1, 2 }));
        }

        [Test]
        public void Pick_ChangesWithSaltSeedAndElement_ButNeverWithCallOrder()
        {
            var tiles = Enumerable.Range(0, 200).Select(i => new Vector2Int(i, i * 7 % 13)).ToList();
            int[] Picks(int seed, int salt, EnvironmentElement e) => tiles.Select(t => VisualVariants.Pick(seed, salt, e, t, 4)).ToArray();

            var baseline = Picks(1, 0, EnvironmentElement.Crate);
            Assert.That(Picks(1, 1, EnvironmentElement.Crate), Is.Not.EqualTo(baseline), "salt");
            Assert.That(Picks(2, 0, EnvironmentElement.Crate), Is.Not.EqualTo(baseline), "seed");
            Assert.That(Picks(1, 0, EnvironmentElement.LowCover), Is.Not.EqualTo(baseline), "element");
            Assert.That(Picks(1, 0, EnvironmentElement.Crate), Is.EqualTo(baseline), "repeatable after other calls");
        }
    }
}
