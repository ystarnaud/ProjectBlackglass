using System;
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
            created = AbilityDefinition.Create("Aimed Shot", AbilityTargetMode.Unit, AbilityTargetSide.Hostile, 14f, true,
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
        public void Create_AGroundAbility_IsAlwaysHostileAreaDamage()
        {
            created = AbilityDefinition.Create("Blast", AbilityTargetMode.Ground, AbilityTargetSide.Friendly, 12f, true,
                AbilityCoverRule.Ignored, 10f, AbilityEffect.Damage, 35, 3f);

            Assert.That(created.TargetSide, Is.EqualTo(AbilityTargetSide.Hostile), "Area damage is hostile-only: no friendly fire");
            Assert.That(created.Radius, Is.EqualTo(3f));
        }

        [Test]
        public void Create_AGroundHeal_IsRejected()
        {
            Assert.Throws<ArgumentException>(() => AbilityDefinition.Create("Aura", AbilityTargetMode.Ground,
                AbilityTargetSide.Friendly, 8f, false, AbilityCoverRule.Ignored, 5f, AbilityEffect.Heal, 10, 3f));
        }

        [Test]
        public void Create_AGroundAbilityWithoutARadius_IsRejected()
        {
            Assert.Throws<ArgumentException>(() => AbilityDefinition.Create("Blast", AbilityTargetMode.Ground,
                AbilityTargetSide.Hostile, 12f, true, AbilityCoverRule.Ignored, 10f, AbilityEffect.Damage, 35, 0f));
        }

        [Test]
        public void Create_ARangeThatIsNotPositive_IsRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => AbilityDefinition.Create("Nothing", AbilityTargetMode.Unit,
                AbilityTargetSide.Hostile, 0f, false, AbilityCoverRule.Applies, 1f, AbilityEffect.Damage, 1));
        }
    }
}
