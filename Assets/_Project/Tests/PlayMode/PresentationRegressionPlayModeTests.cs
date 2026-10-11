#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>Presentation on or off never changes gameplay, and a re-enabled UnitPresentation replays nothing.</summary>
    public class PresentationRegressionPlayModeTests
    {
        struct Outcome
        {
            public int Shots, Hits, TargetHealth, SecondHealth, AbilityUses, EffectsSeen, AreasSeen;
            public bool AbilityAccepted;
            public float Cooldown, RandomA, RandomB, RandomC;
        }

        // The same scripted fight, with presentation on or off. Gameplay uses Random.value for every shot's roll, so the
        // stream position at the end proves presentation consumed none.
        IEnumerator Fight(bool presentation, System.Action<Outcome> done)
        {
            using (var rig = new PresentationRig(withWall: true))
            {
                rig.Presenter.Enabled = presentation;
                // Presentation is bound after the abilities are added (as the mission spawner does), so it hears the blast.
                var shooter = rig.CreateShooter(new Vector3(0f, 0f, 6f), damage: 7, cooldown: 0f, present: false);
                var target = rig.CreateTarget(new Vector3(0f, 0f, 9f));
                var second = rig.CreateTarget(new Vector3(3f, 0f, 9f));
                var encounter = rig.World.CreateEncounter();
                encounter.Initialize(new[] { shooter.GetComponent<Health>() }, new[] { target, second });
                var blast = rig.World.CreateBlast();
                blast.SetPresentation(rig.RifleProfile);   // the blast has an explosion to show when presentation is on
                var abilities = rig.World.AddAbilities(shooter, encounter, blast);
                shooter.gameObject.AddComponent<UnitPresentation>().Bind(rig.Presenter);
                yield return null;

                Random.InitState(4321);
                var attacker = shooter.GetComponent<UnitAttacker>();
                for (var i = 0; i < 12; i++)
                    attacker.TryAttack(i % 2 == 0 ? target : second);
                var accepted = abilities.TryUse(AbilityCommand.AtGround(blast, new Vector3(1f, 0f, 9f)));
                var areas = rig.Presenter.ActiveOf(EffectCategory.Area);
                target.Heal(5);
                var effects = rig.Presenter.ActiveEffects;
                yield return new WaitForSeconds(0.2f);

                done(new Outcome
                {
                    Shots = attacker.ShotsFired, Hits = attacker.Hits, TargetHealth = target.Current, SecondHealth = second.Current,
                    AbilityUses = abilities.UsedCount, AbilityAccepted = accepted, EffectsSeen = effects, AreasSeen = areas,
                    Cooldown = abilities.CooldownRemaining(0),
                    RandomA = Random.value, RandomB = Random.value, RandomC = Random.value,
                });
            }
        }

        [UnityTest]
        public IEnumerator PresentationOn_AndOff_GiveIdenticalGameplay_AndTheSameRandomStream()
        {
            Outcome on = default, off = default;
            yield return Fight(true, o => on = o);
            yield return null;   // the first rig's objects are destroyed at the end of the frame
            yield return Fight(false, o => off = o);

            Assert.That(on.EffectsSeen, Is.GreaterThan(0), "Precondition: with presentation on, the fight was presented");
            Assert.That(off.EffectsSeen, Is.EqualTo(0), "Precondition: with presentation off, nothing was");
            Assert.That(on.AbilityAccepted, Is.True, "Precondition: the blast was used");
            Assert.That(on.AreasSeen, Is.EqualTo(1), "Precondition: with presentation on, the blast itself was presented");
            Assert.That(off.AreasSeen, Is.EqualTo(0));
            Assert.That(on.AbilityAccepted, Is.EqualTo(off.AbilityAccepted));
            Assert.That(on.Shots, Is.EqualTo(off.Shots));
            Assert.That(on.Hits, Is.EqualTo(off.Hits));
            Assert.That(on.TargetHealth, Is.EqualTo(off.TargetHealth));
            Assert.That(on.SecondHealth, Is.EqualTo(off.SecondHealth));
            Assert.That(on.AbilityUses, Is.EqualTo(off.AbilityUses));
            Assert.That(on.Cooldown, Is.EqualTo(off.Cooldown).Within(0.25f), "cooldowns run on the same scaled clock");
            Assert.That(on.RandomA, Is.EqualTo(off.RandomA));
            Assert.That(on.RandomB, Is.EqualTo(off.RandomB));
            Assert.That(on.RandomC, Is.EqualTo(off.RandomC));
            Assert.That(on.Shots, Is.EqualTo(12));
        }

        [UnityTest]
        public IEnumerator DisablingAndReEnablingTheUnitPresentation_ReplaysNothing()
        {
            var rig = new PresentationRig();
            try
            {
                var shooter = rig.CreateShooter(new Vector3(0f, 0f, -6f));
                var target = rig.CreateTarget(new Vector3(0f, 0f, 4f));
                yield return null;
                Assert.That(shooter.GetComponent<UnitAttacker>().TryAttack(target), Is.True);
                Assert.That(rig.Presenter.ActiveEffects, Is.GreaterThan(0), "Precondition: the shot was presented");
                rig.Presenter.Clear();
                var presentation = shooter.GetComponent<UnitPresentation>();
                presentation.enabled = false;
                presentation.enabled = true;
                yield return null;
                Assert.That(rig.Presenter.ActiveEffects, Is.EqualTo(0));
                Assert.That(rig.Presenter.Audio.ActiveVoices, Is.EqualTo(0));
            }
            finally { rig.Dispose(); }
        }

        // Found by the test above: the unit's default burst is deferred to LateUpdate, and Clear used to forget only the
        // shot impacts it consults, so damage taken before a Clear still drew a burst after it.
        [UnityTest]
        public IEnumerator DamageTakenBeforeAClear_DrawsNoDeferredBurstAfterIt_ButDamageAfterItDoes()
        {
            var rig = new PresentationRig();
            try
            {
                var target = rig.CreateTarget(new Vector3(0f, 0f, 4f));
                yield return null;
                target.TakeDamage(10);
                rig.Presenter.Clear();
                yield return null;
                Assert.That(rig.Presenter.ActiveEffects, Is.EqualTo(0), "the pending burst did not survive the Clear");

                target.TakeDamage(10);
                yield return null;
                Assert.That(rig.Presenter.ActiveOf(EffectCategory.Impact), Is.EqualTo(1), "damage after the Clear still shows its burst");
            }
            finally { rig.Dispose(); }
        }
    }
}
#endif
