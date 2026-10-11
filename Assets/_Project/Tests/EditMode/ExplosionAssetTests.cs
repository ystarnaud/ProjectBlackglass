using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Blackglass.Tests
{
    public class ExplosionAssetTests
    {
        const string Root = "Assets/_Project/Presentation/";

        static GameObject Prefab() => AssetDatabase.LoadAssetAtPath<GameObject>(Root + "Prefabs/ExplosionGrenade.prefab");

        [Test]
        public void Explosion_IsLayered_AndHasNoColliderOrPhysics()
        {
            var prefab = Prefab();
            Assert.That(prefab, Is.Not.Null, "Run Blackglass/Presentation/Build Effects");
            var names = prefab.GetComponentsInChildren<ParticleSystem>(true).Select(p => p.name).ToList();
            Assert.That(names, Is.SupersetOf(new[] { "Flash", "Fireball", "Dust", "Sparks", "Fragments", "Smoke", "Ring" }));
            Assert.That(prefab.GetComponentsInChildren<Collider>(true), Is.Empty);
            Assert.That(prefab.GetComponentsInChildren<Rigidbody>(true), Is.Empty);
            foreach (var ps in prefab.GetComponentsInChildren<ParticleSystem>(true))
            {
                Assert.That(ps.collision.enabled, Is.False, ps.name + ": cosmetic fragments never collide");
                Assert.That(ps.main.useUnscaledTime, Is.False, ps.name);
                Assert.That(ps.main.scalingMode, Is.EqualTo(ParticleSystemScalingMode.Hierarchy), ps.name + " scales with the root");
            }
            Assert.That(prefab.GetComponentsInChildren<Light>(true).Length, Is.EqualTo(1));
        }

        [Test]
        public void TheRing_EndsExactlyAtTheReferenceRadius_SoItReadsAsTheRealBoundary()
        {
            var profile = AssetDatabase.LoadAssetAtPath<CombatPresentationProfile>(Root + "Profiles/BlastCombatPresentation.asset");
            Assert.That(profile, Is.Not.Null);
            var ring = Prefab().GetComponentsInChildren<ParticleSystem>(true).First(p => p.name == "Ring");
            var endFactor = ring.sizeOverLifetime.size.Evaluate(1f);
            var finalRadius = ring.main.startSize.constantMax * endFactor * 0.5f;
            Assert.That(finalRadius, Is.EqualTo(profile.ReferenceRadius).Within(0.01f));
            // A flat mesh quad draws at its true size; a HorizontalBillboard is drawn at about 1/sqrt(2) of it and would stop short of the radius.
            Assert.That(ring.GetComponent<ParticleSystemRenderer>().renderMode, Is.EqualTo(ParticleSystemRenderMode.Mesh));
        }

        [Test]
        public void BlastProfile_HasNoProblems_AndTheBlastAbilityUsesIt()
        {
            var profile = AssetDatabase.LoadAssetAtPath<CombatPresentationProfile>(Root + "Profiles/BlastCombatPresentation.asset");
            Assert.That(profile.Problems(), Is.Empty);
            Assert.That(profile.AreaEffect, Is.EqualTo(Prefab()));
            var blast = AssetDatabase.LoadAssetAtPath<AbilityDefinition>("Assets/_Project/Data/Abilities/Blast.asset");
            Assert.That(blast.Presentation, Is.EqualTo(profile));
            Assert.That(blast.Radius, Is.EqualTo(profile.ReferenceRadius), "the prefab was authored for the shipped Blast radius");
        }
    }
}
