#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class AreaPresentationPlayModeTests
    {
        PresentationRig rig;

        [TearDown]
        public void TearDown() => rig?.Dispose();

        // A caster, an encounter with two hostile dummies (one inside, one outside the blast) and an ability with this radius.
        (UnitAbilities abilities, AbilityDefinition ability, Health inside, Health outside) BlastWorld(float radius, float cooldown = 10f)
        {
            var caster = rig.World.CreateFighter(new Vector3(0f, 0f, -6f), role: CombatRole.Ranged, range: 30f);
            caster.GetComponent<AutoRetaliate>().enabled = false;
            var encounter = rig.World.CreateEncounter();
            var inside = rig.World.CreateDummy(new Vector3(0.5f, 0f, 4f));
            inside.Initialize(1000);   // survives the six-blast test
            var outside = rig.World.CreateDummy(new Vector3(radius + 3f, 0f, 4f));
            inside.gameObject.AddComponent<UnitPresentation>().Bind(rig.Presenter);
            encounter.Initialize(new[] { caster.GetComponent<Health>() }, new[] { inside, outside });
            var ability = AbilityDefinition.Create("Blast", AbilityTargetMode.Ground, 15f, true, AbilityCoverRule.Ignored, cooldown,
                AbilityEffect.Damage, 35, radius);
            ability.SetPresentation(rig.RifleProfile);
            var abilities = rig.World.AddAbilities(caster, encounter, ability);
            caster.gameObject.AddComponent<UnitPresentation>().Bind(rig.Presenter);   // after the abilities exist, so it subscribes to their records
            Physics.SyncTransforms();
            return (abilities, ability, inside, outside);
        }

        PooledEffect AreaEffect() =>
            Object.FindObjectsByType<PooledEffect>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).First(e => e.Category == EffectCategory.Area);

        [UnityTest]
        public IEnumerator Blast_PlaysAtTheGameplayCentre_ScaledByTheGameplayRadius_AndDamageIsUnchanged()
        {
            rig = new PresentationRig();
            var (abilities, ability, inside, outside) = BlastWorld(6f);
            yield return null;

            Assert.That(abilities.TryUse(AbilityCommand.AtGround(ability, new Vector3(0f, 0f, 4f))), Is.True);

            var effect = AreaEffect();
            Assert.That(effect.transform.position.x, Is.EqualTo(0f).Within(1e-3f));
            Assert.That(effect.transform.position.z, Is.EqualTo(4f).Within(1e-3f));
            Assert.That(effect.transform.localScale, Is.EqualTo(Vector3.one * 2f), "radius 6 over a reference radius of 3");
            Assert.That(inside.Current, Is.EqualTo(inside.Max - 35));
            Assert.That(outside.Current, Is.EqualTo(outside.Max), "the blast radius is the gameplay radius only");
        }

        [UnityTest]
        public IEnumerator PresentationOff_DealsTheSameDamage_AndShowsNothing()
        {
            rig = new PresentationRig();
            var (abilities, ability, inside, _) = BlastWorld(3f);
            rig.Presenter.Enabled = false;
            yield return null;
            Assert.That(abilities.TryUse(AbilityCommand.AtGround(ability, new Vector3(0f, 0f, 4f))), Is.True);
            Assert.That(rig.Presenter.ActiveEffects, Is.EqualTo(0));
            Assert.That(inside.Current, Is.EqualTo(inside.Max - 35));
        }

        [UnityTest]
        public IEnumerator HiddenCentre_ShowsNothing_AndSendsNoImpulse()
        {
            rig = new PresentationRig();
            var (abilities, ability, _, _) = BlastWorld(3f);
            yield return null;
            rig.Presenter.UseKnowledge(_ => PointVisibility.Hidden, _ => true);
            abilities.TryUse(AbilityCommand.AtGround(ability, new Vector3(0f, 0f, 4f)));
            yield return null;   // a negative check needs a frame in which something could have happened
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Area), Is.EqualTo(0));
            Assert.That(rig.Impulse.LocalOffset, Is.EqualTo(Vector3.zero));
        }

        [UnityTest]
        public IEnumerator Impulse_ScalesWithTheSetting_AndZeroTurnsItOff()
        {
            rig = new PresentationRig();
            var (abilities, ability, _, _) = BlastWorld(3f, cooldown: 0f);
            yield return null;

            rig.Presenter.Settings.cameraShake = 0f;
            abilities.TryUse(AbilityCommand.AtGround(ability, new Vector3(0f, 0f, 4f)));
            yield return null;
            Assert.That(rig.Impulse.LocalOffset, Is.EqualTo(Vector3.zero), "shake 0 disables the impulse");

            rig.Presenter.Clear();
            rig.Presenter.Settings.cameraShake = 1f;
            abilities.TryUse(AbilityCommand.AtGround(ability, new Vector3(0f, 0f, 4f)));
            yield return null;
            Assert.That(rig.Impulse.LocalOffset.magnitude, Is.GreaterThan(0f));
            Assert.That(rig.Impulse.LocalOffset.magnitude, Is.LessThanOrEqualTo(ImpulseEnvelope.MaxOffset * 1.5f));
        }

        [UnityTest]
        public IEnumerator SixBlasts_KeepAtMostFourExplosionsActive_AndGameplayStillApplies()
        {
            rig = new PresentationRig();
            var (abilities, ability, inside, _) = BlastWorld(3f, cooldown: 0f);
            yield return null;
            for (var i = 0; i < 6; i++)
                Assert.That(abilities.TryUse(AbilityCommand.AtGround(ability, new Vector3(0f, 0f, 4f))), Is.True);
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Area), Is.LessThanOrEqualTo(rig.Presenter.Budget.Cap(EffectCategory.Area)));
            Assert.That(rig.Presenter.Budget.LightsActive, Is.LessThanOrEqualTo(rig.Presenter.Budget.LightCap));
            Assert.That(inside.Current, Is.EqualTo(inside.Max - 6 * 35), "every blast still hit");
        }

        [UnityTest]
        public IEnumerator TheGeneratedExplosion_ScalesItsExtentWithTheRadius()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Presentation/Prefabs/ExplosionGrenade.prefab");
            Assert.That(prefab, Is.Not.Null);
            // Measure at the same moment of two instances (scale 1 and scale 2).
            var small = Object.Instantiate(prefab);
            var large = Object.Instantiate(prefab);
            large.transform.localScale = Vector3.one * 2f;
            var smallDust = small.GetComponentsInChildren<ParticleSystem>(true).First(p => p.name == "Dust");
            var largeDust = large.GetComponentsInChildren<ParticleSystem>(true).First(p => p.name == "Dust");
            smallDust.Play();
            largeDust.Play();
            yield return new WaitForSeconds(0.4f);
            var smallParticles = new ParticleSystem.Particle[smallDust.main.maxParticles];
            var largeParticles = new ParticleSystem.Particle[largeDust.main.maxParticles];
            var smallCount = smallDust.GetParticles(smallParticles);
            var largeCount = largeDust.GetParticles(largeParticles);
            float Max(ParticleSystem.Particle[] p, int n)
            {
                var m = 0f;
                for (var i = 0; i < n; i++)
                    m = Mathf.Max(m, new Vector2(p[i].position.x, p[i].position.z).magnitude);
                return m;
            }
            var ratio = Max(largeParticles, largeCount) / Mathf.Max(0.01f, Max(smallParticles, smallCount));
            Object.Destroy(small);
            Object.Destroy(large);
            Assert.That(ratio, Is.InRange(1.6f, 2.4f), "twice the radius gives roughly twice the spread");
        }
    }
}
#endif
