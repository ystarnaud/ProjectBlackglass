using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class CombatPresentationProfileTests
    {
        static GameObject Prefab(string name) => new GameObject(name);

        [Test]
        public void ImpactFor_PrefersTheExactSurface_ThenDefault_ThenNothing()
        {
            var profile = CombatPresentationProfile.CreateEmpty();
            var concrete = Prefab("Concrete");
            var fallback = Prefab("Default");
            profile.SetImpacts(SurfaceImpact.Create(ImpactSurfaceKind.Concrete, concrete), SurfaceImpact.Create(ImpactSurfaceKind.Default, fallback));
            Assert.That(profile.ImpactFor(ImpactSurfaceKind.Concrete).Prefab, Is.EqualTo(concrete));
            Assert.That(profile.ImpactFor(ImpactSurfaceKind.Metal).Prefab, Is.EqualTo(fallback));
            profile.SetImpacts(SurfaceImpact.Create(ImpactSurfaceKind.Concrete, concrete));
            Assert.That(profile.ImpactFor(ImpactSurfaceKind.Metal), Is.Null);
            Object.DestroyImmediate(concrete);
            Object.DestroyImmediate(fallback);
        }

        [Test]
        public void EmptyProfile_HasNoProblems_AndNothingToShow()
        {
            var profile = CombatPresentationProfile.CreateEmpty();
            Assert.That(profile.Problems(), Is.Empty);
            Assert.That(profile.MuzzleEffect, Is.Null);
            Assert.That(profile.Tracer.IsUsable, Is.False);
            Assert.That(profile.FireCue.IsEmpty, Is.True);
            Assert.That(profile.ImpactFor(ImpactSurfaceKind.Default), Is.Null);
        }

        [Test]
        public void Problems_ReportsAColliderOnAnEffectPrefab_DuplicateSurfaces_AndAnAreaWithoutAReferenceRadius()
        {
            var profile = CombatPresentationProfile.CreateEmpty();
            var bad = Prefab("Bad");
            bad.AddComponent<BoxCollider>();
            var ok = Prefab("Ok");
            profile.SetMuzzle(bad, 1f);
            profile.SetImpacts(SurfaceImpact.Create(ImpactSurfaceKind.Metal, ok), SurfaceImpact.Create(ImpactSurfaceKind.Metal, ok));
            profile.SetArea(ok, 0f, null, 0.5f);
            var problems = profile.Problems();
            Assert.That(problems, Has.Some.Contains("collider"));
            Assert.That(problems, Has.Some.Contains("Metal").And.Some.Contains("twice"));
            Assert.That(problems, Has.Some.Contains("reference radius"));
            Object.DestroyImmediate(bad);
            Object.DestroyImmediate(ok);
        }

        [Test]
        public void Problems_ReportsAnImpactEntryWithNeitherPrefabNorSound()
        {
            var profile = CombatPresentationProfile.CreateEmpty();
            profile.SetImpacts(SurfaceImpact.Create(ImpactSurfaceKind.Concrete, null));
            Assert.That(profile.Problems(), Has.Some.Contains("Concrete"));
        }

        [Test]
        public void AudioCue_Pick_SkipsMissingClips_AndIsNullWhenEmpty()
        {
            var empty = AudioCue.Create(1f, 128, 0.06f, null, null);
            Assert.That(empty.IsEmpty, Is.True);
            Assert.That(empty.Pick(new CosmeticRandom(1)), Is.Null);

            var clip = AudioClip.Create("c", 100, 1, 44100, false);
            var cue = AudioCue.Create(1f, 128, 0.06f, null, clip, null);
            Assert.That(cue.IsEmpty, Is.False);
            for (var i = 0; i < 10; i++)
                Assert.That(cue.Pick(new CosmeticRandom(i)), Is.EqualTo(clip));
            Object.DestroyImmediate(clip);
        }

        [Test]
        public void TracerStyle_IsUsableOnlyWithAPrefab()
        {
            Assert.That(new TracerStyle().IsUsable, Is.False);
            var prefab = Prefab("Tracer");
            Assert.That(TracerStyle.Create(prefab).IsUsable, Is.True);
            Object.DestroyImmediate(prefab);
        }

        [Test]
        public void WeaponVisualAsset_AndAbilityDefinition_CarryAnOptionalProfile()
        {
            var visual = WeaponVisualAsset.Create(null, Vector3.zero, Vector3.zero, Vector3.one);
            Assert.That(visual.Presentation, Is.Null);
            var profile = CombatPresentationProfile.CreateEmpty();
            visual.SetPresentation(profile);
            Assert.That(visual.Presentation, Is.EqualTo(profile));

            var ability = AbilityDefinition.Create("Blast", AbilityTargetMode.Ground, 12f, true, AbilityCoverRule.Ignored, 10f, AbilityEffect.Damage, 35, 3f);
            Assert.That(ability.Presentation, Is.Null);
            ability.SetPresentation(profile);
            Assert.That(ability.Presentation, Is.EqualTo(profile));
        }
    }
}
