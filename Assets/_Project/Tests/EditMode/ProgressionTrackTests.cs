using System;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class ProgressionTrackTests
    {
        AdvancementChoice combat;
        AdvancementChoice hull;
        ProgressionTrack track;

        [SetUp]
        public void SetUp()
        {
            combat = AdvancementChoice.Create("combat", "Combat Training", "+15% attack damage", new StatModifiers { attackDamage = 0.15f });
            hull = AdvancementChoice.Create("survivability", "Reinforced", "+25 max health", new StatModifiers { maxHealth = 25 });
            track = ProgressionTrack.Create(new[] { 0, 100, 250, 450, 700 }, new[] { combat, hull }, 150, 50);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(track);
            Object.DestroyImmediate(combat);
            Object.DestroyImmediate(hull);
        }

        [TestCase(-5, 1)]
        [TestCase(0, 1)]
        [TestCase(99, 1)]
        [TestCase(100, 2)]
        [TestCase(249, 2)]
        [TestCase(250, 3)]
        [TestCase(449, 3)]
        [TestCase(450, 4)]
        [TestCase(700, 5)]
        [TestCase(100000, 5)]
        public void RankFor_UsesTheThresholds(int xp, int rank) => Assert.That(track.RankFor(xp), Is.EqualTo(rank));

        [Test]
        public void MaxRank_IsTheNumberOfThresholds() => Assert.That(track.MaxRank, Is.EqualTo(5));

        [Test]
        public void XpForRank_ReturnsTheThreshold_AndClampsOutOfRangeRanks()
        {
            Assert.That(track.XpForRank(1), Is.EqualTo(0));
            Assert.That(track.XpForRank(3), Is.EqualTo(250));
            Assert.That(track.XpForRank(5), Is.EqualTo(700));
            Assert.That(track.XpForRank(0), Is.EqualTo(0));
            Assert.That(track.XpForRank(99), Is.EqualTo(700));
        }

        [Test]
        public void TryGetNextThreshold_GivesTheNextRanksXp_AndFalseAtMax()
        {
            Assert.That(track.TryGetNextThreshold(0, out var next), Is.True);
            Assert.That(next, Is.EqualTo(100));
            Assert.That(track.TryGetNextThreshold(130, out next), Is.True);
            Assert.That(next, Is.EqualTo(250));
            Assert.That(track.TryGetNextThreshold(700, out _), Is.False);
            Assert.That(track.TryGetNextThreshold(9999, out _), Is.False);
        }

        [Test]
        public void FindChoice_ByStableId_OrNull()
        {
            Assert.That(track.FindChoice("combat"), Is.SameAs(combat));
            Assert.That(track.FindChoice("survivability"), Is.SameAs(hull));
            Assert.That(track.FindChoice("nope"), Is.Null);
            Assert.That(track.FindChoice(null), Is.Null);
            Assert.That(track.FindChoice(""), Is.Null);
        }

        [Test]
        public void ExposesTheConfiguredNumbers()
        {
            Assert.That(track.MissionCompletionXp, Is.EqualTo(150));
            Assert.That(track.DebugXpStep, Is.EqualTo(50));
            Assert.That(track.Choices, Has.Count.EqualTo(2));
        }

        [Test]
        public void Create_RejectsBadThresholds()
        {
            Assert.Throws<ArgumentException>(() => ProgressionTrack.Create(new int[0], new AdvancementChoice[0], 150, 50));
            Assert.Throws<ArgumentException>(() => ProgressionTrack.Create(new[] { 10, 100 }, new AdvancementChoice[0], 150, 50), "must start at 0");
            Assert.Throws<ArgumentException>(() => ProgressionTrack.Create(new[] { 0, 100, 100 }, new AdvancementChoice[0], 150, 50), "strictly increasing");
            Assert.Throws<ArgumentException>(() => ProgressionTrack.Create(new[] { 0, 100, 50 }, new AdvancementChoice[0], 150, 50));
        }

        [Test]
        public void Create_RejectsDuplicateOrMissingChoices()
        {
            var twin = AdvancementChoice.Create("combat", "Twin", "", default);
            try
            {
                Assert.Throws<ArgumentException>(() => ProgressionTrack.Create(new[] { 0, 100 }, new[] { combat, twin }, 150, 50));
                Assert.Throws<ArgumentException>(() => ProgressionTrack.Create(new[] { 0, 100 }, new AdvancementChoice[] { combat, null }, 150, 50));
            }
            finally
            {
                Object.DestroyImmediate(twin);
            }
        }

        [Test]
        public void AdvancementChoice_RejectsABlankId()
        {
            Assert.Throws<ArgumentException>(() => AdvancementChoice.Create("", "x", "", default));
            Assert.Throws<ArgumentException>(() => AdvancementChoice.Create("  ", "x", "", default));
            Assert.Throws<ArgumentException>(() => AdvancementChoice.Create(null, "x", "", default));
        }

        [Test]
        public void OperativeRole_ExposesItsBonus()
        {
            var role = OperativeRole.Create("Assault", "Front line", new StatModifiers { attackDamage = 0.2f });
            try
            {
                Assert.That(role.DisplayName, Is.EqualTo("Assault"));
                Assert.That(role.Description, Is.EqualTo("Front line"));
                Assert.That(role.Bonus.attackDamage, Is.EqualTo(0.2f));
            }
            finally
            {
                Object.DestroyImmediate(role);
            }
        }
    }
}
