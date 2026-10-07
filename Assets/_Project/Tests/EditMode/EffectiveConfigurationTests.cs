using System;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class EffectiveConfigurationTests
    {
        AdvancementChoice combat, hull, speed, focus;
        ProgressionTrack track;
        OperativeRole assault;
        CombatArchetype archetype;
        AbilityDefinition aimed;
        GameObject prefab;
        OperativeDefinition darius;

        [SetUp]
        public void SetUp()
        {
            combat = AdvancementChoice.Create("combat", "Combat Training", "", new StatModifiers { attackDamage = 0.15f });
            hull = AdvancementChoice.Create("survivability", "Reinforced", "", new StatModifiers { maxHealth = 25 });
            speed = AdvancementChoice.Create("mobility", "Fleet-footed", "", new StatModifiers { moveSpeed = 0.75f });
            focus = AdvancementChoice.Create("ability", "Focus", "", new StatModifiers { abilityPower = 0.2f, abilityCooldownReduction = 0.15f });
            track = ProgressionTrack.Create(new[] { 0, 100, 250, 450, 700 }, new[] { combat, hull, speed, focus }, 150, 50);
            assault = OperativeRole.Create("Assault", "", new StatModifiers { attackDamage = 0.2f });
            archetype = CombatArchetype.Create("Ranged", CombatRole.Ranged, 8f, 15, 1f);
            aimed = AbilityDefinition.Create("Aimed Shot", AbilityTargetMode.Unit, 14f, true, AbilityCoverRule.Applies, 6f, AbilityEffect.Damage, 45);
            prefab = new GameObject("UnitPrefab");
            darius = OperativeDefinition.Create("darius-id", "Darius", assault, prefab, 130, 5f, archetype, new[] { aimed });
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in new Object[] { track, combat, hull, speed, focus, assault, archetype, aimed, prefab, darius })
                Object.DestroyImmediate(o);
        }

        PersistentOperativeState Advance(string id, int xp, params string[] picks)
        {
            var state = new PersistentOperativeState(id);
            state.AddExperience(xp);
            foreach (var pick in picks)
                Assert.That(state.TryPickChoice(track, pick), Is.True, pick);
            return state;
        }

        [Test]
        public void Base_IsTheDefinitionAndItsArchetypeAlone_NoRoleNoPicks()
        {
            var config = EffectiveConfiguration.Base(darius);

            Assert.That(config.Rank, Is.EqualTo(1));
            Assert.That(config.MaxHealth, Is.EqualTo(130));
            Assert.That(config.MoveSpeed, Is.EqualTo(5f));
            Assert.That(config.AttackRole, Is.EqualTo(CombatRole.Ranged));
            Assert.That(config.AttackRange, Is.EqualTo(8f));
            Assert.That(config.AttackDamage, Is.EqualTo(15));
            Assert.That(config.AttackInterval, Is.EqualTo(1f));
            Assert.That(config.AbilityPower, Is.EqualTo(1f));
            Assert.That(config.AbilityCooldownMultiplier, Is.EqualTo(1f));
        }

        [Test]
        public void Evaluate_AFreshOperative_GetsOnlyTheRoleBonus()
        {
            var config = EffectiveConfiguration.Evaluate(darius, new PersistentOperativeState("a"), track);

            Assert.That(config.Rank, Is.EqualTo(1));
            Assert.That(config.AttackDamage, Is.EqualTo(18), "15 +20% role bonus");
            Assert.That(config.MaxHealth, Is.EqualTo(130));
            Assert.That(config.MoveSpeed, Is.EqualTo(5f));
        }

        [Test]
        public void Evaluate_EachChoiceChangesItsOwnStat()
        {
            Assert.That(EffectiveConfiguration.Evaluate(darius, Advance("a", 100, "combat"), track).AttackDamage, Is.EqualTo(20), "15 * (1 + 0.2 + 0.15) = 20.25");
            Assert.That(EffectiveConfiguration.Evaluate(darius, Advance("a", 100, "survivability"), track).MaxHealth, Is.EqualTo(155));
            Assert.That(EffectiveConfiguration.Evaluate(darius, Advance("a", 100, "mobility"), track).MoveSpeed, Is.EqualTo(5.75f).Within(0.0001f));
            var focused = EffectiveConfiguration.Evaluate(darius, Advance("a", 100, "ability"), track);
            Assert.That(focused.AbilityPower, Is.EqualTo(1.2f).Within(0.0001f));
            Assert.That(focused.AbilityCooldownMultiplier, Is.EqualTo(0.85f).Within(0.0001f));
        }

        [Test]
        public void Evaluate_PicksStack()
        {
            var config = EffectiveConfiguration.Evaluate(darius, Advance("a", 450, "combat", "survivability", "mobility"), track);

            Assert.That(config.Rank, Is.EqualTo(4));
            Assert.That(config.AttackDamage, Is.EqualTo(20), "15 * (1 + 0.2 + 0.15) = 20.25");
            Assert.That(config.MaxHealth, Is.EqualTo(155));
            Assert.That(config.MoveSpeed, Is.EqualTo(5.75f).Within(0.0001f));
        }

        [Test]
        public void Evaluate_TheSamePickTwice_AddsItsModifierTwice()
        {
            var config = EffectiveConfiguration.Evaluate(darius, Advance("a", 250, "survivability", "survivability"), track);
            Assert.That(config.MaxHealth, Is.EqualTo(180));
        }

        [Test]
        public void Evaluate_CooldownReductionIsCappedAtSeventyFivePercent()
        {
            var huge = AdvancementChoice.Create("huge", "Huge", "", new StatModifiers { abilityCooldownReduction = 0.9f });
            var big = ProgressionTrack.Create(new[] { 0, 100 }, new[] { huge }, 150, 50);
            try
            {
                var state = new PersistentOperativeState("a");
                state.AddExperience(100);
                state.TryPickChoice(big, "huge");
                Assert.That(EffectiveConfiguration.Evaluate(darius, state, big).AbilityCooldownMultiplier, Is.EqualTo(0.25f).Within(0.0001f));
            }
            finally
            {
                Object.DestroyImmediate(big);
                Object.DestroyImmediate(huge);
            }
        }

        [Test]
        public void Evaluate_ASkippedUnknownChoiceId_IsIgnored_NotAnError()
        {
            var other = AdvancementChoice.Create("only-in-the-old-track", "Old", "", new StatModifiers { maxHealth = 999 });
            var oldTrack = ProgressionTrack.Create(new[] { 0, 100 }, new[] { other }, 150, 50);
            try
            {
                var state = new PersistentOperativeState("a");
                state.AddExperience(100);
                state.TryPickChoice(oldTrack, "only-in-the-old-track");

                var config = EffectiveConfiguration.Evaluate(darius, state, track);

                Assert.That(config.MaxHealth, Is.EqualTo(130), "the stale pick is skipped");
            }
            finally
            {
                Object.DestroyImmediate(oldTrack);
                Object.DestroyImmediate(other);
            }
        }

        [Test]
        public void Evaluate_NullStateOrTrackOrRole_IsTolerated()
        {
            Assert.That(EffectiveConfiguration.Evaluate(darius, null, track).AttackDamage, Is.EqualTo(18));
            Assert.That(EffectiveConfiguration.Evaluate(darius, new PersistentOperativeState("a"), null).Rank, Is.EqualTo(1));
            var roleless = OperativeDefinition.Create("x", "X", null, prefab, 100, 5f, archetype, new AbilityDefinition[0]);
            try
            {
                Assert.That(EffectiveConfiguration.Evaluate(roleless, null, null).AttackDamage, Is.EqualTo(15));
            }
            finally
            {
                Object.DestroyImmediate(roleless);
            }
        }

        [Test]
        public void Evaluate_ADefinitionWithoutAnArchetype_Throws()
        {
            var bare = OperativeDefinition.Create("x", "X", assault, prefab, 100, 5f, null, new AbilityDefinition[0]);
            try
            {
                Assert.Throws<ArgumentException>(() => EffectiveConfiguration.Evaluate(bare, null, track));
                Assert.Throws<ArgumentNullException>(() => EffectiveConfiguration.Evaluate(null, null, track));
            }
            finally
            {
                Object.DestroyImmediate(bare);
            }
        }

        [Test]
        public void Evaluate_ClampsToPlayableMinimums()
        {
            var crippling = AdvancementChoice.Create("c", "C", "", new StatModifiers { maxHealth = -500, moveSpeed = -50f, attackDamage = -5f });
            var weak = ProgressionTrack.Create(new[] { 0, 10 }, new[] { crippling }, 150, 50);
            try
            {
                var state = new PersistentOperativeState("a");
                state.AddExperience(10);
                state.TryPickChoice(weak, "c");
                var config = EffectiveConfiguration.Evaluate(darius, state, weak);
                Assert.That(config.MaxHealth, Is.EqualTo(1));
                Assert.That(config.MoveSpeed, Is.EqualTo(0.5f));
                Assert.That(config.AttackDamage, Is.EqualTo(0));
            }
            finally
            {
                Object.DestroyImmediate(weak);
                Object.DestroyImmediate(crippling);
            }
        }

        // The data-safety rule: two operatives sharing one definition never share progression, and the shared assets never change.
        [Test]
        public void TwoOperativesSharingOneDefinition_ProgressIndependently_AndTheAssetsStayUntouched()
        {
            var a = Advance("a", 250, "combat", "survivability");
            var b = new PersistentOperativeState("b");

            var ea = EffectiveConfiguration.Evaluate(darius, a, track);
            var eb = EffectiveConfiguration.Evaluate(darius, b, track);

            Assert.That(ea.Rank, Is.EqualTo(3));
            Assert.That(ea.MaxHealth, Is.EqualTo(155));
            Assert.That(ea.AttackDamage, Is.EqualTo(20));
            Assert.That(eb.Rank, Is.EqualTo(1));
            Assert.That(eb.MaxHealth, Is.EqualTo(130), "B did not inherit A's survivability pick");
            Assert.That(eb.AttackDamage, Is.EqualTo(18), "B did not inherit A's combat pick");
            Assert.That(b.Experience, Is.EqualTo(0));
            Assert.That(b.ChoiceIds, Is.Empty);

            Assert.That(darius.BaseMaxHealth, Is.EqualTo(130));
            Assert.That(darius.BaseMoveSpeed, Is.EqualTo(5f));
            Assert.That(archetype.Damage, Is.EqualTo(15));
            Assert.That(assault.Bonus.attackDamage, Is.EqualTo(0.2f));
            Assert.That(combat.Modifiers.attackDamage, Is.EqualTo(0.15f));
            Assert.That(hull.Modifiers.maxHealth, Is.EqualTo(25));
        }
    }
}
