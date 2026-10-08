using System;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class PersistentOperativeStateTests
    {
        AdvancementChoice combat;
        AdvancementChoice hull;
        ProgressionTrack track;
        PersistentOperativeState state;

        [SetUp]
        public void SetUp()
        {
            combat = AdvancementChoice.Create("combat", "Combat Training", "", new StatModifiers { attackDamage = 0.15f });
            hull = AdvancementChoice.Create("survivability", "Reinforced", "", new StatModifiers { maxHealth = 25 });
            track = ProgressionTrack.Create(new[] { 0, 100, 250, 450, 700 }, new[] { combat, hull }, 150, 50);
            state = new PersistentOperativeState("op-1");
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(track);
            Object.DestroyImmediate(combat);
            Object.DestroyImmediate(hull);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void Constructor_RejectsABlankId(string id) =>
            Assert.Throws<ArgumentException>(() => new PersistentOperativeState(id));

        [Test]
        public void New_StartsAtRankOne_WithNoXpPicksOrPendingChoice()
        {
            Assert.That(state.OperativeId, Is.EqualTo("op-1"));
            Assert.That(state.Experience, Is.EqualTo(0));
            Assert.That(state.ChoiceIds, Is.Empty);
            Assert.That(state.Rank(track), Is.EqualTo(1));
            Assert.That(state.PendingPicks(track), Is.EqualTo(0));
        }

        [Test]
        public void AddExperience_RaisesTheRankAtTheThreshold_AndGrantsOnePendingPickPerRank()
        {
            state.AddExperience(99);
            Assert.That(state.Rank(track), Is.EqualTo(1));
            Assert.That(state.PendingPicks(track), Is.EqualTo(0));

            state.AddExperience(1);
            Assert.That(state.Rank(track), Is.EqualTo(2));
            Assert.That(state.PendingPicks(track), Is.EqualTo(1));

            state.AddExperience(150);   // 250: two ranks gained in total
            Assert.That(state.Rank(track), Is.EqualTo(3));
            Assert.That(state.PendingPicks(track), Is.EqualTo(2));
        }

        [Test]
        public void AddExperience_ZeroChangesNothing_NegativeThrows()
        {
            state.AddExperience(0);
            Assert.That(state.Experience, Is.EqualTo(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => state.AddExperience(-1));
            Assert.That(state.Experience, Is.EqualTo(0));
        }

        [Test]
        public void AddExperience_PastIntMax_ClampsInsteadOfWrapping()
        {
            state.AddExperience(int.MaxValue);
            state.AddExperience(500);
            Assert.That(state.Experience, Is.EqualTo(int.MaxValue));
            Assert.That(state.Rank(track), Is.EqualTo(5));
        }

        [Test]
        public void TryPickChoice_NeedsAPendingPick()
        {
            Assert.That(state.TryPickChoice(track, "combat"), Is.False);
            Assert.That(state.ChoiceIds, Is.Empty);

            state.AddExperience(100);
            Assert.That(state.TryPickChoice(track, "combat"), Is.True);
            Assert.That(state.ChoiceIds, Is.EqualTo(new[] { "combat" }));
            Assert.That(state.PendingPicks(track), Is.EqualTo(0));
            Assert.That(state.TryPickChoice(track, "survivability"), Is.False, "the only pick is spent");
        }

        [Test]
        public void TryPickChoice_UnknownOrBlankId_IsRefused_AndKeepsThePendingPick()
        {
            state.AddExperience(100);
            Assert.That(state.TryPickChoice(track, "not-a-choice"), Is.False);
            Assert.That(state.TryPickChoice(track, null), Is.False);
            Assert.That(state.TryPickChoice(track, ""), Is.False);
            Assert.That(state.PendingPicks(track), Is.EqualTo(1));
            Assert.That(state.ChoiceIds, Is.Empty);
        }

        [Test]
        public void TryPickChoice_TheSameChoiceCanBePickedAgain_PicksStack()
        {
            state.AddExperience(250);
            Assert.That(state.TryPickChoice(track, "combat"), Is.True);
            Assert.That(state.TryPickChoice(track, "combat"), Is.True);
            Assert.That(state.ChoiceIds, Is.EqualTo(new[] { "combat", "combat" }));
            Assert.That(state.TryPickChoice(track, "combat"), Is.False);
        }

        [Test]
        public void PendingPicks_NeverGoNegative_WhenAShorterTrackIsUsed()
        {
            state.AddExperience(700);
            state.TryPickChoice(track, "combat");
            state.TryPickChoice(track, "combat");
            state.TryPickChoice(track, "survivability");
            var shorter = ProgressionTrack.Create(new[] { 0, 100 }, new[] { combat, hull }, 150, 50);
            try
            {
                Assert.That(state.Rank(shorter), Is.EqualTo(2));
                Assert.That(state.PendingPicks(shorter), Is.EqualTo(0), "3 picks made, only 1 allowed by the short track");
            }
            finally
            {
                Object.DestroyImmediate(shorter);
            }
        }

        [Test]
        public void ResetProgression_ClearsXpAndPicks_ButKeepsTheId()
        {
            state.AddExperience(250);
            state.TryPickChoice(track, "combat");

            state.ResetProgression();

            Assert.That(state.OperativeId, Is.EqualTo("op-1"));
            Assert.That(state.Experience, Is.EqualTo(0));
            Assert.That(state.ChoiceIds, Is.Empty);
            Assert.That(state.Rank(track), Is.EqualTo(1));
        }

        [Test]
        public void SerializesToJsonAndBack_WithoutAnyUnityObjectReference()
        {
            state.AddExperience(260);
            state.TryPickChoice(track, "combat");

            var json = JsonUtility.ToJson(state);
            var copy = JsonUtility.FromJson<PersistentOperativeState>(json);

            Assert.That(copy.OperativeId, Is.EqualTo("op-1"));
            Assert.That(copy.Experience, Is.EqualTo(260));
            Assert.That(copy.ChoiceIds, Is.EqualTo(new[] { "combat" }));
            Assert.That(copy.Rank(track), Is.EqualTo(3));
            Assert.That(json, Does.Not.Contain("instanceID"), "state must stay plain data (Phase 14 will save it)");
        }
    }
}
