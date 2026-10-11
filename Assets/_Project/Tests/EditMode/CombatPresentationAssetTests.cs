using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

namespace Blackglass.Tests
{
    public class CombatPresentationAssetTests
    {
        const string Root = "Assets/_Project/Presentation/";
        const string Visuals = "Assets/_Project/Data/Items/Visuals/";

        public static readonly string[] RiflePrefabs =
        {
            "MuzzleFlashRifle", "TracerRifle", "ImpactConcrete", "ImpactMetal", "ImpactCharacter", "ImpactDefault",
        };

        static T Load<T>(string path) where T : Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            Assert.That(asset, Is.Not.Null, "Missing " + path + ": run Blackglass/Presentation/Build Effects");
            return asset;
        }

        public static readonly string[] OtherPrefabs = { "HealPulse", "ExplosionGrenade" };

        [TestCaseSource(nameof(RiflePrefabs))]
        [TestCaseSource(nameof(OtherPrefabs))]
        public void EffectPrefab_IsPoolReady_NeverAGameplayObject_AndFollowsTheSimulationClock(string name)
        {
            var prefab = Load<GameObject>(Root + "Prefabs/" + name + ".prefab");
            Assert.That(prefab.GetComponent<PooledEffect>(), Is.Not.Null, "PooledEffect on the root");
            Assert.That(prefab.GetComponentsInChildren<Collider>(true), Is.Empty);
            Assert.That(prefab.GetComponentsInChildren<Rigidbody>(true), Is.Empty);
            Assert.That(prefab.GetComponentsInChildren<NavMeshObstacle>(true), Is.Empty);
            foreach (var ps in prefab.GetComponentsInChildren<ParticleSystem>(true))
            {
                Assert.That(ps.main.useUnscaledTime, Is.False, name + "/" + ps.name + " must use scaled time");
                Assert.That(ps.main.playOnAwake, Is.False, name + "/" + ps.name);
                Assert.That(ps.main.loop, Is.False, name + "/" + ps.name);
                Assert.That(ps.main.scalingMode, Is.EqualTo(ParticleSystemScalingMode.Hierarchy), name + "/" + ps.name);
                Assert.That(ps.main.simulationSpace, Is.EqualTo(ParticleSystemSimulationSpace.World), name + "/" + ps.name);
            }
            foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(true))
            {
                Assert.That(renderer.sharedMaterial, Is.Not.Null, name + "/" + renderer.name);
                Assert.That(renderer.sharedMaterial.shader.name, Does.StartWith("Universal Render Pipeline"), name + "/" + renderer.name);
                Assert.That(renderer.shadowCastingMode, Is.EqualTo(UnityEngine.Rendering.ShadowCastingMode.Off));
            }
        }

        [Test]
        public void DefaultProfile_PulseEffectIsTheHealPulsePrefab_AndKeepsItsChime()
        {
            var profile = Load<CombatPresentationProfile>(Root + "Profiles/DefaultCombatPresentation.asset");
            Assert.That(AssetDatabase.GetAssetPath(profile.PulseEffect), Is.EqualTo(Root + "Prefabs/HealPulse.prefab"));
            Assert.That(profile.PulseCue.IsEmpty, Is.False, "the heal chime stays");
        }

        [Test]
        public void MuzzleFlash_HasASmokePuffAndACappedLight()
        {
            var prefab = Load<GameObject>(Root + "Prefabs/MuzzleFlashRifle.prefab");
            Assert.That(prefab.GetComponentsInChildren<ParticleSystem>(true).Select(p => p.name), Does.Contain("Smoke").And.Contain("Flash"));
            var lights = prefab.GetComponentsInChildren<Light>(true);
            Assert.That(lights.Length, Is.EqualTo(1), "one cheap muzzle light");
            Assert.That(lights[0].shadows, Is.EqualTo(LightShadows.None));
            Assert.That(lights[0].range, Is.LessThanOrEqualTo(6f));
        }

        [Test]
        public void TracerPrefab_HasALineAndATracerView()
        {
            var prefab = Load<GameObject>(Root + "Prefabs/TracerRifle.prefab");
            Assert.That(prefab.GetComponent<LineRenderer>(), Is.Not.Null);
            Assert.That(prefab.GetComponent<TracerView>(), Is.Not.Null);
        }

        [Test]
        public void EffectMaterials_AreTransparentParticleMaterials_ThatDoNotWriteDepth()
        {
            foreach (var name in new[] { "FxGlow", "FxSmoke", "FxDust", "FxRing", "FxTracer", "FxSpark", "FxChip" })
            {
                var material = Load<Material>(Root + "Materials/" + name + ".mat");
                Assert.That(material.shader.name, Is.EqualTo("Universal Render Pipeline/Particles/Unlit"), name);
                Assert.That(material.GetFloat("_Surface"), Is.EqualTo(1f), name);
                Assert.That(material.GetFloat("_ZWrite"), Is.EqualTo(0f), name);
                Assert.That(material.renderQueue, Is.GreaterThanOrEqualTo(3000), name);
                Assert.That(material.GetTexture("_BaseMap"), Is.Not.Null, name);
            }
        }

        [TestCase("RifleCombatPresentation")]
        [TestCase("DefaultCombatPresentation")]
        public void Profile_HasNoProblems(string name)
        {
            Assert.That(Load<CombatPresentationProfile>(Root + "Profiles/" + name + ".asset").Problems(), Is.Empty);
        }

        [Test]
        public void RifleProfile_ReferencesTheEffects_ForEveryRequiredSurface()
        {
            var profile = Load<CombatPresentationProfile>(Root + "Profiles/RifleCombatPresentation.asset");
            Assert.That(profile.MuzzleEffect, Is.Not.Null);
            Assert.That(profile.Tracer.IsUsable, Is.True);
            foreach (var kind in new[] { ImpactSurfaceKind.Concrete, ImpactSurfaceKind.Metal, ImpactSurfaceKind.Character, ImpactSurfaceKind.Default })
            {
                var entry = profile.ImpactFor(kind);
                Assert.That(entry, Is.Not.Null, kind.ToString());
                Assert.That(entry.Surface, Is.EqualTo(kind), kind + " has its own entry, not the default");
                Assert.That(entry.Prefab, Is.Not.Null, kind.ToString());
            }
            Assert.That(profile.RecoilKick, Is.GreaterThan(0f));
        }

        [Test]
        public void DefaultProfile_ShowsAtLeastCharacterAndDefaultImpacts()
        {
            var profile = Load<CombatPresentationProfile>(Root + "Profiles/DefaultCombatPresentation.asset");
            Assert.That(profile.ImpactFor(ImpactSurfaceKind.Character).Surface, Is.EqualTo(ImpactSurfaceKind.Character));
            Assert.That(profile.ImpactFor(ImpactSurfaceKind.Default).Surface, Is.EqualTo(ImpactSurfaceKind.Default));
        }

        [TestCase("RifleVisual")]
        [TestCase("MarksmanRifleVisual")]
        public void RifleWeaponVisuals_UseTheSharedRifleProfile(string name)
        {
            var visual = Load<WeaponVisualAsset>(Visuals + name + ".asset");
            var profile = Load<CombatPresentationProfile>(Root + "Profiles/RifleCombatPresentation.asset");
            Assert.That(visual.Presentation, Is.EqualTo(profile));
        }

        [Test]
        public void RifleProfile_MapsTheFourSurfacesToFourDifferentPrefabs()
        {
            var profile = Load<CombatPresentationProfile>(Root + "Profiles/RifleCombatPresentation.asset");
            var expected = new (ImpactSurfaceKind Kind, string Prefab)[]
            {
                (ImpactSurfaceKind.Concrete, "ImpactConcrete"),
                (ImpactSurfaceKind.Metal, "ImpactMetal"),
                (ImpactSurfaceKind.Character, "ImpactCharacter"),
                (ImpactSurfaceKind.Default, "ImpactDefault"),
            };
            foreach (var (kind, prefab) in expected)
                Assert.That(AssetDatabase.GetAssetPath(profile.ImpactFor(kind).Prefab), Is.EqualTo(Root + "Prefabs/" + prefab + ".prefab"), kind.ToString());
            Assert.That(expected.Select(e => profile.ImpactFor(e.Kind).Prefab).Distinct().Count(), Is.EqualTo(4));
            Assert.That(AssetDatabase.GetAssetPath(profile.MuzzleEffect), Is.EqualTo(Root + "Prefabs/MuzzleFlashRifle.prefab"));
        }

        // ---- EnsureImpact / FillFrom: the builder's idempotence rests on "fill what is missing, never overwrite what is set" ----

        readonly System.Collections.Generic.List<Object> made = new System.Collections.Generic.List<Object>();

        GameObject NewPrefab(string name)
        {
            var go = new GameObject(name);
            made.Add(go);
            return go;
        }

        AudioCue NewCue()
        {
            var clip = AudioClip.Create("clip", 64, 1, 8000, false);
            made.Add(clip);
            return AudioCue.Create(1f, 128, 0.06f, clip);
        }

        CombatPresentationProfile NewProfile()
        {
            var profile = CombatPresentationProfile.CreateEmpty();
            made.Add(profile);
            return profile;
        }

        [TearDown]
        public void DestroyMade()
        {
            foreach (var o in made)
                if (o != null)
                    Object.DestroyImmediate(o);
            made.Clear();
        }

        [Test]
        public void EnsureImpact_KeepsTheFirstPrefab_AndNeverAddsASecondEntryForTheKind()
        {
            var profile = NewProfile();
            var a = NewPrefab("A");
            var b = NewPrefab("B");
            profile.EnsureImpact(ImpactSurfaceKind.Metal, a, null);
            profile.EnsureImpact(ImpactSurfaceKind.Metal, b, null);
            Assert.That(profile.Impacts.Count(e => e.Surface == ImpactSurfaceKind.Metal), Is.EqualTo(1));
            Assert.That(profile.ImpactFor(ImpactSurfaceKind.Metal).Prefab, Is.SameAs(a));
        }

        [Test]
        public void EnsureImpact_FillsAMissingPrefabThenAMissingCue_WithoutLosingTheEarlierOne()
        {
            var profile = NewProfile();
            profile.SetImpacts(SurfaceImpact.Create(ImpactSurfaceKind.Concrete, null));
            var a = NewPrefab("A");
            var cue = NewCue();
            Assert.That(profile.ImpactFor(ImpactSurfaceKind.Concrete).Prefab, Is.Null);
            Assert.That(profile.ImpactFor(ImpactSurfaceKind.Concrete).Cue.IsEmpty, Is.True);

            profile.EnsureImpact(ImpactSurfaceKind.Concrete, a, null);
            Assert.That(profile.ImpactFor(ImpactSurfaceKind.Concrete).Prefab, Is.SameAs(a));
            Assert.That(profile.ImpactFor(ImpactSurfaceKind.Concrete).Cue.IsEmpty, Is.True);

            profile.EnsureImpact(ImpactSurfaceKind.Concrete, null, cue);
            var entry = profile.ImpactFor(ImpactSurfaceKind.Concrete);
            Assert.That(entry.Prefab, Is.SameAs(a), "the prefab survives filling the cue");
            Assert.That(entry.Cue.IsEmpty, Is.False);
            Assert.That(profile.Impacts.Count, Is.EqualTo(1));
        }

        [Test]
        public void EnsureImpact_DoesNotOverwriteASetCue()
        {
            var profile = NewProfile();
            var first = NewCue();
            var second = NewCue();
            var prefab = NewPrefab("A");
            profile.EnsureImpact(ImpactSurfaceKind.Character, prefab, first);
            var clipBefore = profile.ImpactFor(ImpactSurfaceKind.Character).Cue.Pick(new CosmeticRandom(1));
            profile.EnsureImpact(ImpactSurfaceKind.Character, prefab, second);
            var clipAfter = profile.ImpactFor(ImpactSurfaceKind.Character).Cue.Pick(new CosmeticRandom(1));
            Assert.That(clipBefore, Is.Not.Null);
            Assert.That(clipAfter, Is.SameAs(clipBefore), "the first cue's clip is still the one used");
            Assert.That(profile.ImpactFor(ImpactSurfaceKind.Character).Cue, Is.SameAs(profile.Impacts.Single().Cue));
        }

        [Test]
        public void EnsureImpact_AddsDifferentKindsIndependently()
        {
            var profile = NewProfile();
            var a = NewPrefab("A");
            var b = NewPrefab("B");
            profile.EnsureImpact(ImpactSurfaceKind.Concrete, a, null);
            profile.EnsureImpact(ImpactSurfaceKind.Metal, b, null);
            Assert.That(profile.Impacts.Count, Is.EqualTo(2));
            Assert.That(profile.ImpactFor(ImpactSurfaceKind.Concrete).Prefab, Is.SameAs(a));
            Assert.That(profile.ImpactFor(ImpactSurfaceKind.Metal).Prefab, Is.SameAs(b));
        }
    }
}
