#if UNITY_EDITOR
using System.Collections;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CombatPresenterPlayModeTests
    {
        static readonly UnityEngine.TestTools.Utils.Vector3EqualityComparer V3 = new UnityEngine.TestTools.Utils.Vector3EqualityComparer(1e-4f);
        PresentationRig rig;

        [TearDown]
        public void TearDown() => rig?.Dispose();

        static UnitAttacker AttackerOf(CommandableUnit unit) => unit.GetComponent<UnitAttacker>();

        [UnityTest]
        public IEnumerator ValidHit_ShowsMuzzleTracerAndCharacterImpact_AndDoesNotChangeTheDamage()
        {
            rig = new PresentationRig();
            var shooter = rig.CreateShooter(new Vector3(0f, 0f, -6f));
            var target = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            yield return null;
            var visual = shooter.GetComponent<UnitWeaponVisual>();
            var muzzleAtShot = visual.Muzzle.position;

            Assert.That(AttackerOf(shooter).TryAttack(target), Is.True);

            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Muzzle), Is.EqualTo(1));
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Tracer), Is.EqualTo(1));
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Impact), Is.EqualTo(1));
            Assert.That(target.Current, Is.EqualTo(target.Max - 10), "gameplay damage is exactly the attacker's");
            var muzzleEffect = FindActive(EffectCategory.Muzzle);
            Assert.That(muzzleEffect.transform.position, Is.EqualTo(muzzleAtShot).Using(V3), "the flash starts at the muzzle at the instant of the shot, before the recoil");
        }

        static PooledEffect FindActive(EffectCategory category)
        {
            foreach (var effect in Object.FindObjectsByType<PooledEffect>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                if (effect.IsActive && effect.Category == category)
                    return effect;
            return null;
        }

        [UnityTest]
        public IEnumerator RejectedAttack_ShowsNothing()
        {
            rig = new PresentationRig();
            var shooter = rig.CreateShooter(new Vector3(0f, 0f, -6f));
            var far = rig.CreateTarget(new Vector3(0f, 0f, 60f));
            yield return null;
            Assert.That(AttackerOf(shooter).TryAttack(far), Is.False);
            Assert.That(rig.Presenter.ActiveEffects, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator CoverMiss_ShowsMuzzleAndTracer_AndALandingOnTheCover_NeverACharacterImpact()
        {
            rig = new PresentationRig(withWall: true);
            yield return rig.PutDefenderInCover();
            var shooter = rig.CreateShooter(new Vector3(0f, 0f, 5f));
            AttackerOf(shooter).HitRoll = () => 0.99f;
            yield return new WaitForFixedUpdate();
            var defenderHealth = rig.Defender.GetComponent<Health>();

            Assert.That(AttackerOf(shooter).TryAttack(defenderHealth), Is.True);

            Assert.That(defenderHealth.Current, Is.EqualTo(defenderHealth.Max), "Precondition: it was a cover miss");
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Muzzle), Is.EqualTo(1));
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Tracer), Is.EqualTo(1));
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Impact), Is.EqualTo(1), "the shot lands on the wall");
            Assert.That(rig.Presenter.ConsumeCharacterImpact(defenderHealth, Time.frameCount), Is.False, "no body hit is depicted");
        }

        [UnityTest]
        public IEnumerator Melee_ShowsNoMuzzleOrTracer_ButAHitShowsTheCharacterImpact()
        {
            rig = new PresentationRig();
            var attacker = rig.CreateShooter(new Vector3(0f, 0f, 0f), weapon: false, role: CombatRole.Melee);
            var target = rig.CreateTarget(new Vector3(0f, 0f, 1.2f));
            yield return null;
            Assert.That(AttackerOf(attacker).TryAttack(target), Is.True);
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Muzzle), Is.EqualTo(0));
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Tracer), Is.EqualTo(0));
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Impact), Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator AKillingShot_StillShowsItsMuzzleAndImpact_FromTheSnapshot()
        {
            rig = new PresentationRig();
            var shooter = rig.CreateShooter(new Vector3(0f, 0f, -6f), damage: 5000);
            var target = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            yield return null;
            Assert.That(AttackerOf(shooter).TryAttack(target), Is.True);
            Assert.That(target.IsAlive, Is.False, "Precondition: the shot killed and deactivated its target");
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Muzzle), Is.EqualTo(1));
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Impact), Is.EqualTo(1), "the impact comes from the captured aim point, not the dead unit");
        }

        [UnityTest]
        public IEnumerator Muzzle_FollowsTheWeaponsCurrentPose_AndKeepsWorldScale()
        {
            rig = new PresentationRig();
            var shooter = rig.CreateShooter(new Vector3(0f, 0f, -6f));
            var target = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            var visual = shooter.GetComponent<UnitWeaponVisual>();
            var socket = visual.Current.transform.parent;
            socket.localScale = Vector3.one * 0.01f;      // an imported hierarchy with a scale correction
            yield return null;

            socket.localPosition = new Vector3(-0.4f, 0.9f, 0.2f);
            var muzzleAtShot = visual.Muzzle.position;
            AttackerOf(shooter).TryAttack(target);

            var effect = FindActive(EffectCategory.Muzzle);
            Assert.That(effect.transform.position, Is.EqualTo(muzzleAtShot).Using(V3));
            Assert.That(effect.transform.lossyScale, Is.EqualTo(Vector3.one * 1.5f).Using(V3),
                "the effect keeps its profile size in world units, whatever the socket scale");
        }

        [UnityTest]
        public IEnumerator MissingMuzzle_StillShowsTheEffects_WithOneWarning()
        {
            rig = new PresentationRig();
            var shooter = rig.CreateShooter(new Vector3(0f, 0f, -6f), withMuzzle: false);
            var target = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            yield return null;
            LogAssert.Expect(LogType.Warning, new Regex("without a Muzzle"));
            AttackerOf(shooter).TryAttack(target);
            yield return new WaitForSeconds(0.15f);
            AttackerOf(shooter).TryAttack(target);
            yield return new WaitForSeconds(0.15f);
            AttackerOf(shooter).TryAttack(target);
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Muzzle), Is.GreaterThanOrEqualTo(1));
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator UnitWithNoWeaponVisualOrProfile_FiresWithoutErrors_AndUsesTheFallbackImpact()
        {
            rig = new PresentationRig();
            var shooter = rig.CreateShooter(new Vector3(0f, 0f, -6f), weapon: false);
            var target = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            yield return null;
            Assert.That(AttackerOf(shooter).TryAttack(target), Is.True);
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Muzzle), Is.EqualTo(0), "the fallback profile has no muzzle effect");
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Impact), Is.EqualTo(1));
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator DisabledPresentation_ShowsNothing_AndGameplayIsIdentical()
        {
            rig = new PresentationRig();
            var shooter = rig.CreateShooter(new Vector3(0f, 0f, -6f));
            var target = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            yield return null;
            rig.Presenter.Enabled = false;
            Assert.That(AttackerOf(shooter).TryAttack(target), Is.True);
            Assert.That(rig.Presenter.ActiveEffects, Is.EqualTo(0));
            Assert.That(target.Current, Is.EqualTo(target.Max - 10));
        }

        [UnityTest]
        public IEnumerator DeliveringTheSameRecordTwice_PresentsOnce()
        {
            rig = new PresentationRig();
            var shooter = rig.CreateShooter(new Vector3(0f, 0f, -6f));
            var target = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            yield return null;
            var record = new ShotRecord(7, 1, shooter.GetComponent<Health>(), target, shooter.transform.position + Vector3.up * 0.5f,
                target.transform.position + Vector3.up * ShotRecord.BodyHeight, ShotOutcome.Hit, false, null);
            var presentation = shooter.GetComponent<UnitPresentation>();
            presentation.Deliver(record);
            presentation.Deliver(record);
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Muzzle), Is.EqualTo(1));
        }

        // A missing gate input means do not show: a record without a shooter has no observed shooter.
        [UnityTest]
        public IEnumerator ARecordWithoutASource_ShowsNoMuzzle()
        {
            rig = new PresentationRig();
            var shooter = rig.CreateShooter(new Vector3(0f, 0f, -6f));
            var target = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            yield return null;
            Assert.That(rig.Presenter.Observes(null), Is.False);
            var record = new ShotRecord(7, 1, null, target, shooter.transform.position + Vector3.up * 0.5f,
                target.transform.position + Vector3.up * ShotRecord.BodyHeight, ShotOutcome.Hit, false, null);
            shooter.GetComponent<UnitPresentation>().Deliver(record);
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Muzzle), Is.EqualTo(0));
        }

        // Teardown order is not guaranteed: Clear (called from OnDisable) must survive its audio child or pool container being gone.
        [UnityTest]
        public IEnumerator Clear_AfterTheAudioChildAndThePoolContainerWereDestroyed_DoesNotThrow()
        {
            rig = new PresentationRig();
            var shooter = rig.CreateShooter(new Vector3(0f, 0f, -6f));
            var target = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            yield return null;
            AttackerOf(shooter).TryAttack(target);
            Assert.That(rig.Presenter.ActiveEffects, Is.GreaterThan(0), "Precondition: effects are playing");
            Object.DestroyImmediate(rig.Presenter.Audio.gameObject);
            Object.DestroyImmediate(GameObject.Find("PresentationPool"));
            Assert.DoesNotThrow(() => rig.Presenter.Clear());
            Assert.That(rig.Presenter.ActiveEffects, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator Clear_ReleasesEveryEffect_AndResetsTheBudgets()
        {
            rig = new PresentationRig();
            var shooter = rig.CreateShooter(new Vector3(0f, 0f, -6f));
            var target = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            yield return null;
            AttackerOf(shooter).TryAttack(target);
            Assert.That(rig.Presenter.ActiveEffects, Is.GreaterThan(0));
            rig.Presenter.Clear();
            Assert.That(rig.Presenter.ActiveEffects, Is.EqualTo(0));
            foreach (EffectCategory category in System.Enum.GetValues(typeof(EffectCategory)))
                Assert.That(rig.Presenter.Budget.Active(category), Is.EqualTo(0), category.ToString());
            Assert.That(rig.Presenter.Budget.LightsActive, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator Recoil_KicksTheWeaponBack_RecoversExactly_AndFreezesWhilePaused()
        {
            rig = new PresentationRig();
            var shooter = rig.CreateShooter(new Vector3(0f, 0f, -6f));
            var target = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            var visual = shooter.GetComponent<UnitWeaponVisual>();
            yield return null;
            var model = visual.Current.transform;
            var basePosition = model.localPosition;

            AttackerOf(shooter).TryAttack(target);
            var kicked = Vector3.Distance(model.position, model.parent.TransformPoint(basePosition));
            Assert.That(kicked, Is.EqualTo(0.05f).Within(0.005f), "the profile's 5 cm kick, in world units");
            Assert.That(Vector3.Dot(model.position - model.parent.TransformPoint(basePosition), visual.Muzzle.forward), Is.LessThan(0f), "backwards along the barrel");

            rig.Pause.Pause();
            var frozen = model.localPosition;
            yield return new WaitForSecondsRealtime(0.4f);
            Assert.That(model.localPosition, Is.EqualTo(frozen), "recoil does not move while paused");
            rig.Pause.Resume();
            yield return new WaitForSeconds(0.5f);
            Assert.That(model.localPosition, Is.EqualTo(basePosition).Using(V3));
        }

        [UnityTest]
        public IEnumerator FogOverride_HiddenShooter_ShowsNoMuzzle_AndAFriendFiringIntoHiddenGroundShowsNoTracerOrImpact()
        {
            rig = new PresentationRig();
            var friend = rig.CreateShooter(new Vector3(-6f, 0f, 0f));
            var hidden = rig.CreateShooter(new Vector3(12f, 0f, -6f));
            var visibleTarget = rig.CreateTarget(new Vector3(-6f, 0f, 8f));
            var hiddenTarget = rig.CreateTarget(new Vector3(12f, 0f, 8f));
            yield return null;
            // x > 5 is hidden ground; units there are not observed.
            rig.Presenter.UseKnowledge(p => p.x > 5f ? PointVisibility.Hidden : PointVisibility.Visible, u => u.transform.position.x <= 5f);

            AttackerOf(hidden).TryAttack(hiddenTarget);
            Assert.That(rig.Presenter.ActiveEffects, Is.EqualTo(0), "nothing from a hidden shooter at a hidden target");

            AttackerOf(friend).TryAttack(visibleTarget);
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Muzzle), Is.EqualTo(1));
            yield return null;   // the frame's late updates settle (the visible victim's default-burst check) before the effects are cleared
            rig.Presenter.Clear();

            yield return new WaitForSeconds(0.2f);   // the friend's attack cooldown (0.1 s) must have elapsed
            // The same friend fires at a target that is itself in hidden ground: flash yes, tracer and impact no.
            var far = rig.CreateTarget(new Vector3(-6f, 0f, 20f));
            far.transform.position = new Vector3(8f, 1f, 0f);
            Physics.SyncTransforms();
            AttackerOf(friend).TryAttack(far);
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Muzzle), Is.EqualTo(1));
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Tracer), Is.EqualTo(0));
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Impact), Is.EqualTo(0));
        }

        // The real knowledge model never observes a dead unit, and gameplay applies damage before the shot is announced.
        [UnityTest]
        public IEnumerator KillingShot_AtAnObservedVictim_StillShowsMuzzleAndImpact_WhenDeadUnitsAreNotObserved()
        {
            rig = new PresentationRig();
            var shooter = rig.CreateShooter(new Vector3(0f, 0f, -6f), damage: 5000);
            var target = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            yield return null;
            rig.Presenter.UseKnowledge(p => PointVisibility.Visible, u => u.IsAlive);

            Assert.That(AttackerOf(shooter).TryAttack(target), Is.True);

            Assert.That(target.IsAlive, Is.False, "Precondition: the shot killed its target");
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Muzzle), Is.EqualTo(1));
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Impact), Is.EqualTo(1), "a dead victim at a visible point still takes its impact");
        }

        [UnityTest]
        public IEnumerator KillingShot_WhoseAimPointIsInHiddenGround_ShowsNoImpact()
        {
            rig = new PresentationRig();
            var shooter = rig.CreateShooter(new Vector3(-6f, 0f, 0f), damage: 5000);
            var target = rig.CreateTarget(new Vector3(8f, 0f, 0f));
            yield return null;
            rig.Presenter.UseKnowledge(p => p.x > 5f ? PointVisibility.Hidden : PointVisibility.Visible, u => u.IsAlive && u.transform.position.x <= 5f);

            Assert.That(AttackerOf(shooter).TryAttack(target), Is.True);

            Assert.That(target.IsAlive, Is.False, "Precondition: the shot killed its target");
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Muzzle), Is.EqualTo(1), "the observed shooter's flash is shown");
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Impact), Is.EqualTo(0), "the body impact is in hidden ground");
        }

        [UnityTest]
        public IEnumerator Death_IsHeardOnlyWhereThePlayerCanSeeIt_EvenThoughTheUnitIsNoLongerObserved()
        {
            rig = new PresentationRig();
            var clip = AudioClip.Create("death", 22050, 1, 44100, false);
            try
            {
                rig.FallbackProfile.SetDeathCue(AudioCue.Create(1f, 128, 0f, clip));
                // No UnitPresentation on it: its own death hook would play this cue itself, and this test drives ShowDeath directly.
                var dead = rig.World.CreateDummy(new Vector3(0f, 0f, 4f));
                yield return null;
                rig.Presenter.UseKnowledge(p => p.x > 5f ? PointVisibility.Hidden : PointVisibility.Visible, u => u.IsAlive && u.transform.position.x <= 5f);
                dead.TakeDamage(dead.Max);
                Assert.That(dead.IsAlive, Is.False, "Precondition");

                rig.Presenter.ShowDeath(dead, new Vector3(8f, 1f, 4f));
                Assert.That(rig.Presenter.Audio.ActiveVoices, Is.EqualTo(0), "a death in hidden ground is silent");

                rig.Presenter.ShowDeath(dead, new Vector3(0f, 1f, 4f));
                Assert.That(rig.Presenter.Audio.ActiveVoices, Is.EqualTo(1), "a death at a visible point is heard");
            }
            finally
            {
                Object.Destroy(clip);
            }
        }

        [UnityTest]
        public IEnumerator PresentationNeverMakesAnEffectAGameplayObstacle()
        {
            rig = new PresentationRig();
            var shooter = rig.CreateShooter(new Vector3(0f, 0f, -6f));
            var target = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            yield return null;
            AttackerOf(shooter).TryAttack(target);
            foreach (var effect in Object.FindObjectsByType<PooledEffect>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                Assert.That(effect.GetComponentsInChildren<Collider>(true), Is.Empty, effect.name);
                Assert.That(effect.GetComponentsInChildren<UnityEngine.AI.NavMeshObstacle>(true), Is.Empty, effect.name);
            }
        }
    }
}
#endif
