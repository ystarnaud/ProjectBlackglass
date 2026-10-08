using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class AbilityDefinitionRevealTests
    {
        readonly List<UnityEngine.Object> created = new List<UnityEngine.Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in created)
                UnityEngine.Object.DestroyImmediate(o);
            created.Clear();
        }

        AbilityDefinition Scan(float radius = 12f, float seconds = 6f)
        {
            var definition = AbilityDefinition.Create("Recon Scan", AbilityTargetMode.Ground, 18f, false, AbilityCoverRule.Ignored, 25f,
                AbilityEffect.Reveal, 0, radius, seconds);
            created.Add(definition);
            return definition;
        }

        [Test]
        public void AGroundReveal_KeepsItsEffect_AndExposesItsNumbers()
        {
            var scan = Scan();
            Assert.That(scan.Effect, Is.EqualTo(AbilityEffect.Reveal));
            Assert.That(scan.TargetMode, Is.EqualTo(AbilityTargetMode.Ground));
            Assert.That(scan.Radius, Is.EqualTo(12f));
            Assert.That(scan.RevealSeconds, Is.EqualTo(6f));
            Assert.That(scan.Range, Is.EqualTo(18f));
            Assert.That(scan.RequiresLineOfSight, Is.False);
            Assert.That(scan.Cooldown, Is.EqualTo(25f));
            Assert.That(scan.TargetSide, Is.EqualTo(AbilityTargetSide.Hostile), "a reveal is not a heal: it never targets friendlies");
        }

        [Test]
        public void AUnitReveal_IsRefused()
        {
            Assert.That(() => AbilityDefinition.Create("Bad", AbilityTargetMode.Unit, 10f, true, AbilityCoverRule.Applies, 5f,
                AbilityEffect.Reveal, 0, 3f, 4f), Throws.ArgumentException);
        }

        [Test]
        public void AGroundRevealNeedsARadius()
        {
            Assert.That(() => Scan(radius: 0f), Throws.ArgumentException);
        }

        [Test]
        public void AGroundHeal_IsStillRefused()
        {
            Assert.That(() => AbilityDefinition.Create("Bad", AbilityTargetMode.Ground, 10f, true, AbilityCoverRule.Applies, 5f,
                AbilityEffect.Heal, 10, 3f), Throws.ArgumentException);
        }

        [Test]
        public void ADamageAbility_HasNoRevealSeconds()
        {
            var blast = AbilityDefinition.Create("Blast", AbilityTargetMode.Ground, 12f, true, AbilityCoverRule.Ignored, 10f,
                AbilityEffect.Damage, 35, 3f);
            created.Add(blast);
            Assert.That(blast.Effect, Is.EqualTo(AbilityEffect.Damage));
            Assert.That(blast.RevealSeconds, Is.EqualTo(0f));
        }

        [Test]
        public void ARevealStoredOnAUnitModeAsset_ReadsAsDamage()
        {
            var asset = ScriptableObject.CreateInstance<AbilityDefinition>();
            created.Add(asset);
            var serialized = new UnityEditor.SerializedObject(asset);
            serialized.FindProperty("effect").enumValueIndex = (int)AbilityEffect.Reveal;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            Assert.That(asset.TargetMode, Is.EqualTo(AbilityTargetMode.Unit));
            Assert.That(asset.Effect, Is.Not.EqualTo(AbilityEffect.Reveal), "a hand-edited asset cannot make a unit reveal");
        }

        [Test]
        public void ThePreview_DescribesAReveal_WithNoVictimList()
        {
            var scan = Scan();
            var preview = new AbilityPreview(scan, PointerTargetKind.Ground, null, new Vector3(2f, 0f, 3f), true,
                new AbilityCheck(AbilityFailure.None, 5f, true, false, 1f), false);
            var text = AbilityDescriptions.Preview(preview, new List<Health>());
            Assert.That(text, Does.Contain("Recon Scan @ (2.0, 3.0)"));
            Assert.That(text, Does.Contain("reveals 12.0 m for 6.0 s"));
            Assert.That(text, Does.Contain("no LOS needed"));
            Assert.That(text, Does.Not.Contain("hits"));
            Assert.That(text, Does.Not.Contain("cover"));
            Assert.That(text, Does.EndWith("OK"));
        }
    }
}
