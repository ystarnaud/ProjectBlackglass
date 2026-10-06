using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class AbilityDefinitionTests
    {
        AbilityDefinition created;

        [TearDown]
        public void TearDown()
        {
            if (created != null)
                Object.DestroyImmediate(created);
        }

        [Test]
        public void Create_ExposesEveryConfiguredValue()
        {
            created = AbilityDefinition.Create("Aimed Shot", AbilityTargetMode.Unit, 14f, true,
                AbilityCoverRule.Applies, 6f, AbilityEffect.Damage, 45);

            Assert.That(created.DisplayName, Is.EqualTo("Aimed Shot"));
            Assert.That(created.TargetMode, Is.EqualTo(AbilityTargetMode.Unit));
            Assert.That(created.TargetSide, Is.EqualTo(AbilityTargetSide.Hostile));
            Assert.That(created.Range, Is.EqualTo(14f));
            Assert.That(created.RequiresLineOfSight, Is.True);
            Assert.That(created.CoverRule, Is.EqualTo(AbilityCoverRule.Applies));
            Assert.That(created.Cooldown, Is.EqualTo(6f));
            Assert.That(created.Effect, Is.EqualTo(AbilityEffect.Damage));
            Assert.That(created.Amount, Is.EqualTo(45));
            Assert.That(created.Radius, Is.EqualTo(0f));
        }

        [Test]
        public void Create_AGroundAbility_IsHostileAreaDamage()
        {
            created = AbilityDefinition.Create("Blast", AbilityTargetMode.Ground, 12f, true,
                AbilityCoverRule.Ignored, 10f, AbilityEffect.Damage, 35, 3f);

            Assert.That(created.TargetSide, Is.EqualTo(AbilityTargetSide.Hostile), "Area damage is hostile-only: no friendly fire");
            Assert.That(created.Radius, Is.EqualTo(3f));
        }

        [Test]
        public void Create_AUnitDamageAbility_IsHostile()
        {
            created = AbilityDefinition.Create("Friendly Fire", AbilityTargetMode.Unit, 10f, true,
                AbilityCoverRule.Applies, 5f, AbilityEffect.Damage, 20);

            Assert.That(created.TargetSide, Is.EqualTo(AbilityTargetSide.Hostile), "Damage never reaches the caster's side");
        }

        [Test]
        public void Create_AUnitHealAbility_IsFriendly()
        {
            created = AbilityDefinition.Create("Reverse Mend", AbilityTargetMode.Unit, 8f, false,
                AbilityCoverRule.Ignored, 8f, AbilityEffect.Heal, 40);

            Assert.That(created.TargetSide, Is.EqualTo(AbilityTargetSide.Friendly), "Healing never helps the other side");
        }

        [Test]
        public void Create_TheShippedAimedShotAndMend_KeepTheirSides()
        {
            created = AbilityDefinition.Create("Aimed Shot", AbilityTargetMode.Unit, 14f, true,
                AbilityCoverRule.Applies, 6f, AbilityEffect.Damage, 45);
            var mend = AbilityDefinition.Create("Mend", AbilityTargetMode.Unit, 8f, false,
                AbilityCoverRule.Ignored, 8f, AbilityEffect.Heal, 40);
            try
            {
                Assert.That(created.TargetSide, Is.EqualTo(AbilityTargetSide.Hostile));
                Assert.That(mend.TargetSide, Is.EqualTo(AbilityTargetSide.Friendly));
            }
            finally
            {
                Object.DestroyImmediate(mend);
            }
        }

        [Test]
        public void Create_AGroundHeal_IsRejected()
        {
            Assert.Throws<ArgumentException>(() => AbilityDefinition.Create("Aura", AbilityTargetMode.Ground,
                8f, false, AbilityCoverRule.Ignored, 5f, AbilityEffect.Heal, 10, 3f));
        }

        [Test]
        public void Create_AGroundAbilityWithoutARadius_IsRejected()
        {
            Assert.Throws<ArgumentException>(() => AbilityDefinition.Create("Blast", AbilityTargetMode.Ground,
                12f, true, AbilityCoverRule.Ignored, 10f, AbilityEffect.Damage, 35, 0f));
        }

        [Test]
        public void Create_ARangeThatIsNotPositive_IsRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => AbilityDefinition.Create("Nothing", AbilityTargetMode.Unit,
                0f, false, AbilityCoverRule.Applies, 1f, AbilityEffect.Damage, 1));
        }

        // Writes the STORED fields as a hand-edited asset would hold them. Reflection, not SerializedObject: applying
        // serialized changes runs OnValidate in the Editor, which would normalise the very values under test.
        static void WriteStored(AbilityDefinition definition, string field, object value) =>
            typeof(AbilityDefinition).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(definition, value);

        static object ReadStored(AbilityDefinition definition, string field) =>
            typeof(AbilityDefinition).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(definition);

        [Test]
        public void TargetSide_OfAUnitDamageAbility_IsHostile_EvenWhenTheStoredSideSaysFriendly()
        {
            created = AbilityDefinition.Create("Aimed Shot", AbilityTargetMode.Unit, 14f, true,
                AbilityCoverRule.Applies, 6f, AbilityEffect.Damage, 45);

            WriteStored(created, "targetSide", AbilityTargetSide.Friendly);

            Assert.That(ReadStored(created, "targetSide"), Is.EqualTo(AbilityTargetSide.Friendly), "Precondition: the stored side is the unsafe one");
            Assert.That(created.Effect, Is.EqualTo(AbilityEffect.Damage));
            Assert.That(created.TargetSide, Is.EqualTo(AbilityTargetSide.Hostile), "The getter derives the side, so a build cannot friendly-fire");
        }

        [Test]
        public void Effect_OfAGroundAbility_IsDamage_AndItsSideHostile_EvenWhenTheStoredEffectSaysHeal()
        {
            created = AbilityDefinition.Create("Blast", AbilityTargetMode.Ground, 12f, true,
                AbilityCoverRule.Ignored, 10f, AbilityEffect.Damage, 35, 3f);

            WriteStored(created, "effect", AbilityEffect.Heal);

            Assert.That(ReadStored(created, "effect"), Is.EqualTo(AbilityEffect.Heal), "Precondition: the stored effect is the unsafe one");
            Assert.That(created.Effect, Is.EqualTo(AbilityEffect.Damage), "A ground ability is always area damage");
            Assert.That(created.TargetSide, Is.EqualTo(AbilityTargetSide.Hostile));
        }

        [Test]
        public void TargetSide_OfAUnitHealAbility_IsFriendly_EvenWhenTheStoredSideSaysHostile()
        {
            created = AbilityDefinition.Create("Mend", AbilityTargetMode.Unit, 8f, false,
                AbilityCoverRule.Ignored, 8f, AbilityEffect.Heal, 40);

            WriteStored(created, "targetSide", AbilityTargetSide.Hostile);

            Assert.That(ReadStored(created, "targetSide"), Is.EqualTo(AbilityTargetSide.Hostile), "Precondition");
            Assert.That(created.TargetSide, Is.EqualTo(AbilityTargetSide.Friendly));
        }
    }
}
