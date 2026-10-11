#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>The presentation knowledge gate against the real IntelligenceService (fog on), including decision 037.</summary>
    public class PresentationFogRealKnowledgePlayModeTests
    {
        IntelRig intel;
        PresentationRig rig;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
            intel?.Dispose();
            intel = null;
        }

        // A friendly in room A, a ranged hostile 13 m away in room B (out of the 8 m observation range), the real service.
        // Both carry the rig's rifle, so their shots have a muzzle effect, a tracer and impacts to show or withhold.
        (Health shooter, UnitAttacker attacker) Setup()
        {
            intel = new IntelRig(corridor: true);
            var (shooter, attacker) = intel.AddShooter(IntelRig.InLineGround);
            shooter.GetComponent<AutoRetaliate>().enabled = false;   // a hidden hostile that is shot must not fire back by itself
            intel.Begin(IntelRig.Fog(s => { s.observationRange = 8f; s.exposureSeconds = 1f; }));
            rig = new PresentationRig(intelligence: intel.Service, world: intel.World, createEnvironment: false);
            rig.Equip(shooter.gameObject);
            shooter.gameObject.AddComponent<UnitPresentation>().Bind(rig.Presenter);
            rig.Equip(intel.Friendly.gameObject);
            intel.Friendly.gameObject.AddComponent<UnitPresentation>().Bind(rig.Presenter);
            intel.Friendly.GetComponent<UnitAttacker>().Initialize(40f, 10, 0f, CombatRole.Ranged);   // the friend can shoot back across the corridor
            Physics.SyncTransforms();
            return (shooter, attacker);
        }

        [UnityTest]
        public IEnumerator Decision037_AHostilesOwnShot_ShowsItsFlashAndTheFriendsHit_PresentationAddsNoReveal()
        {
            var (shooter, attacker) = Setup();
            yield return null;
            Assert.That(rig.Presenter.Observes(shooter), Is.False, "Precondition: the shooter is not observed before it fires");

            Assert.That(attacker.TryAttack(intel.FriendlyHealth), Is.True);

            Assert.That(rig.Presenter.Observes(shooter), Is.True, "037 exposed it inside Attacked, before Fired");
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Muzzle), Is.EqualTo(1));
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Impact), Is.EqualTo(1), "the visible friend shows its hit");

            yield return new WaitForSeconds(1.4f);
            Assert.That(rig.Presenter.Observes(shooter), Is.False, "exposure ran out: nothing presentation did kept it alive");
        }

        [UnityTest]
        public IEnumerator AVisibleFriendFiringIntoUnknownGround_ShowsItsFlash_ButNoTracerImpactOrHitSound()
        {
            var (shooter, _) = Setup();
            yield return null;
            Assert.That(rig.Presenter.Observes(shooter), Is.False);
            var friendAttacker = intel.Friendly.GetComponent<UnitAttacker>();
            // The friend shoots a unit standing in room B, which nobody has seen.
            var hostileHealth = shooter;
            var before = hostileHealth.Current;
            Assert.That(friendAttacker.TryAttack(hostileHealth), Is.True, "Precondition: the shot is fired");
            Assert.That(hostileHealth.Current, Is.LessThan(before), "Precondition: and it lands");
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Muzzle), Is.EqualTo(1));
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Tracer), Is.EqualTo(0), "the path ends in unobserved ground");
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Impact), Is.EqualTo(0), "the victim is not observed");
            Assert.That(rig.Presenter.Audio.ActiveVoices, Is.EqualTo(1), "the friend's own firing sound only, no hit sound");
            Assert.That(rig.Presenter.Observes(shooter), Is.False, "firing at a hidden unit did not reveal it");
        }

        [UnityTest]
        public IEnumerator ADiscoveredButUnobservedRoom_ShowsNoLiveExplosion()
        {
            Setup();
            yield return null;
            intel.Service.Model.RevealRegion(1);   // room B is on the map but nobody is looking at it
            Assert.That(intel.Service.StateOfRegion(1), Is.EqualTo(KnowledgeState.Discovered));
            var inRoomB = IntelRig.InLineGround;
            var blast = rig.World.CreateBlast();
            blast.SetPresentation(rig.RifleProfile);   // an explosion prefab with a light, a sound and a camera impulse to withhold
            var record = new AreaRecord(1, 1, intel.FriendlyHealth, blast, inRoomB, 3f);
            intel.Friendly.GetComponent<UnitPresentation>().Deliver(record);
            Assert.That(rig.Presenter.ActiveEffects, Is.EqualTo(0));
            Assert.That(rig.Presenter.Audio.ActiveVoices, Is.EqualTo(0));
            Assert.That(rig.Impulse.LocalOffset, Is.EqualTo(Vector3.zero));

            // The same record at the friend's own (observed) feet does show, so the silence above is the gate, not the record.
            var atFriend = new AreaRecord(1, 2, intel.FriendlyHealth, blast, IntelRig.FriendlyGround, 3f);
            intel.Friendly.GetComponent<UnitPresentation>().Deliver(atFriend);
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Area), Is.EqualTo(1), "Control: an observed point presents the same ability");
            Assert.That(rig.Presenter.Audio.ActiveVoices, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator TheTruthView_NeverUnlocksCombatEffects()
        {
            var (shooter, _) = Setup();
            yield return null;
            intel.Service.TruthView = true;
            Assert.That(intel.Service.IsUnitShown(shooter), Is.True, "Precondition: the truth view draws everything");
            Assert.That(rig.Presenter.Observes(shooter), Is.False);
            Assert.That(intel.Service.VisibilityOfPoint(IntelRig.InLineGround), Is.EqualTo(PointVisibility.Hidden));
            Assert.That(intel.Service.VisibilityOfPoint(IntelRig.FriendlyGround), Is.EqualTo(PointVisibility.Visible));
            Assert.That(intel.Service.VisibilityOfPoint(new Vector3(500f, 0f, 500f)), Is.EqualTo(PointVisibility.Outside));
        }

        [UnityTest]
        public IEnumerator HiddenOneShotEvents_AreDropped_NotReplayedWhenTheAreaIsLaterSeen()
        {
            rig = new PresentationRig();
            var shooter = rig.CreateShooter(new Vector3(12f, 0f, -6f));
            var target = rig.CreateTarget(new Vector3(12f, 0f, 8f));
            yield return null;
            rig.Presenter.UseKnowledge(p => p.x > 5f ? PointVisibility.Hidden : PointVisibility.Visible, u => u.transform.position.x <= 5f);
            Assert.That(shooter.GetComponent<UnitAttacker>().TryAttack(target), Is.True, "Precondition: the hidden shot is fired");
            Assert.That(rig.Presenter.ActiveEffects, Is.EqualTo(0));

            rig.Presenter.ClearKnowledgeOverride();   // the area becomes visible
            rig.Presenter.Regate();
            yield return null;
            Assert.That(rig.Presenter.ActiveEffects, Is.EqualTo(0), "nothing buffered, nothing replayed");
        }

        [UnityTest]
        public IEnumerator LingeringEffects_AreHiddenAndShownAgain_WithoutRestarting()
        {
            rig = new PresentationRig();
            var target = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            yield return null;
            target.TakeDamage(10);   // spawns a lingering default burst
            yield return null;
            Assert.That(rig.Presenter.ActiveEffects, Is.EqualTo(1), "Precondition: one default burst");
            var effect = Object.FindFirstObjectByType<PooledEffect>(FindObjectsInactive.Exclude);
            Assert.That(effect, Is.Not.Null);
            Assert.That(effect.HasGatePoint, Is.True, "Precondition: the burst is a lingering, gated effect");
            var time = effect.Particles[0].time;

            rig.Presenter.UseKnowledge(_ => PointVisibility.Hidden, _ => true);
            rig.Presenter.Regate();
            Assert.That(effect.IsVisible, Is.False);
            Assert.That(effect.IsActive, Is.True, "hidden, not released");

            rig.Presenter.ClearKnowledgeOverride();
            rig.Presenter.Regate();
            yield return null;
            Assert.That(effect.IsVisible, Is.True);
            Assert.That(effect.IsActive, Is.True);
            Assert.That(effect.Particles[0].time, Is.GreaterThanOrEqualTo(time), "it kept simulating; it was not restarted");
        }
    }
}
#endif
