#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class UnitFeedbackPlayModeTests
    {
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        PresentationRig rig;
        Material shared;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            if (shared != null)
                Object.DestroyImmediate(shared);
        }

        // A target with a skinned body (no mesh needed for property blocks) and an optional team tint.
        Health BodyTarget(Vector3 ground, Color? team, out SkinnedMeshRenderer body)
        {
            var target = rig.CreateTarget(ground);
            var bodyObject = new GameObject("Body");
            bodyObject.transform.SetParent(target.transform, false);
            body = bodyObject.AddComponent<SkinnedMeshRenderer>();
            shared = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            shared.SetColor(BaseColorId, Color.white);
            body.sharedMaterial = shared;
            if (team.HasValue)
                target.gameObject.AddComponent<UnitTeamTint>().SetTeamColor(team.Value);
            // The presentation caches bodies when it subscribes; re-enable so it sees the new child.
            var presentation = target.GetComponent<UnitPresentation>();
            presentation.enabled = false;
            presentation.enabled = true;
            return target;
        }

        static Color BlockColor(Renderer renderer)
        {
            var block = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(block);
            return block.GetColor(BaseColorId);
        }

        [UnityTest]
        public IEnumerator Damage_FlashesTheBody_ThenRestoresTheExactTeamTint_WithoutTouchingTheSharedMaterial()
        {
            rig = new PresentationRig();
            var team = new Color(0.5f, 0.6f, 1f, 1f);
            var target = BodyTarget(new Vector3(0f, 0f, 4f), team, out var body);
            yield return null;

            target.TakeDamage(10);
            Assert.That(BlockColor(body).r, Is.GreaterThan(team.r), "brighter than the team colour while flashing");
            yield return TestWorld.WaitUntil(() => Mathf.Abs(BlockColor(body).r - team.r) < 1e-4f, 2f);
            var restored = BlockColor(body);
            Assert.That(restored.r, Is.EqualTo(team.r).Within(1e-4f));
            Assert.That(restored.g, Is.EqualTo(team.g).Within(1e-4f));
            Assert.That(restored.b, Is.EqualTo(team.b).Within(1e-4f));
            Assert.That(shared.GetColor(BaseColorId), Is.EqualTo(Color.white), "the shared material is never modified");
        }

        [UnityTest]
        public IEnumerator WithoutATeamTint_TheFlashClearsItsPropertyBlock()
        {
            rig = new PresentationRig();
            var target = BodyTarget(new Vector3(0f, 0f, 4f), null, out var body);
            yield return null;
            target.TakeDamage(10);
            Assert.That(body.HasPropertyBlock(), Is.True);
            yield return TestWorld.WaitUntil(() => !body.HasPropertyBlock(), 2f);
            Assert.That(body.HasPropertyBlock(), Is.False, "back to the authored look");
        }

        [UnityTest]
        public IEnumerator Flash_FreezesWhilePaused()
        {
            rig = new PresentationRig();
            var target = BodyTarget(new Vector3(0f, 0f, 4f), Color.white, out var body);
            yield return null;
            target.TakeDamage(10);
            rig.Pause.Pause();
            var frozen = BlockColor(body);
            yield return new WaitForSecondsRealtime(0.5f);
            Assert.That(BlockColor(body), Is.EqualTo(frozen));
            rig.Pause.Resume();
            yield return TestWorld.WaitUntil(() => Mathf.Abs(BlockColor(body).r - 1f) < 1e-4f, 2f);
            Assert.That(BlockColor(body).r, Is.EqualTo(1f).Within(1e-4f));
        }

        [UnityTest]
        public IEnumerator KillingBlow_NeverLeavesTheBodyStuckBright()
        {
            rig = new PresentationRig();
            var target = BodyTarget(new Vector3(0f, 0f, 4f), new Color(0.5f, 0.6f, 1f, 1f), out var body);
            yield return null;
            target.TakeDamage(target.Max);
            Assert.That(target.IsAlive, Is.False);
            yield return null;
            Assert.That(BlockColor(body).r, Is.EqualTo(0.5f).Within(1e-4f), "restored when the unit deactivated");
        }

        // Found by the final review: the animation driver (subscribed to Died before the presentation, as on the spawned
        // friendly prefabs) detaches the visual into a corpse, so the tint's own Apply on the unit root no longer finds the body.
        [UnityTest]
        public IEnumerator KillingBlow_WhenTheBodyIsDetachedOnDeath_RestoresTheTeamTintOnTheCorpse()
        {
            rig = new PresentationRig();
            var team = new Color(0.5f, 0.6f, 1f, 1f);
            var target = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            var bodyObject = new GameObject("Body");
            bodyObject.transform.SetParent(target.transform, false);
            var body = bodyObject.AddComponent<SkinnedMeshRenderer>();
            shared = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            shared.SetColor(BaseColorId, Color.white);
            body.sharedMaterial = shared;
            target.gameObject.AddComponent<UnitTeamTint>().SetTeamColor(team);
            // Subscribed before the presentation, like UnitAnimationDriver.OnDied: the body leaves the unit as it dies.
            target.Died += () => bodyObject.transform.SetParent(null, true);
            var presentation = target.GetComponent<UnitPresentation>();
            presentation.enabled = false;
            presentation.enabled = true;
            rig.World.Track(bodyObject);
            yield return null;

            target.TakeDamage(target.Max);
            Assert.That(target.IsAlive, Is.False);
            Assert.That(bodyObject.transform.parent, Is.Null, "Precondition: the body was detached on death");
            yield return null;
            var color = BlockColor(body);
            Assert.That(color.r, Is.EqualTo(team.r).Within(1e-4f), "the corpse is not left stuck bright");
            Assert.That(color.g, Is.EqualTo(team.g).Within(1e-4f));
            Assert.That(color.b, Is.EqualTo(team.b).Within(1e-4f));
        }

        [UnityTest]
        public IEnumerator HiddenVictim_ShowsNoFlashAndNoBurst()
        {
            rig = new PresentationRig();
            var target = BodyTarget(new Vector3(0f, 0f, 4f), null, out var body);
            yield return null;
            rig.Presenter.UseKnowledge(_ => PointVisibility.Visible, _ => false);
            target.TakeDamage(10);
            yield return null;
            Assert.That(body.HasPropertyBlock(), Is.False);
            Assert.That(rig.Presenter.ActiveEffects, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator DamageWithNoShot_ShowsExactlyOneDefaultBurst()
        {
            rig = new PresentationRig();
            var target = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            yield return null;
            target.TakeDamage(10);
            yield return null;
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Impact), Is.EqualTo(1));
        }

        // Runs an action once, in LateUpdate after every default-order component (the unit's LateUpdate included): the
        // phase the preview panel's OnGUI buttons deal damage in. WaitForEndOfFrame is not evoked in batch mode.
        [DefaultExecutionOrder(32000)]
        sealed class AfterLateUpdate : MonoBehaviour
        {
            public System.Action Action;

            void LateUpdate()
            {
                var action = Action;
                Action = null;
                action?.Invoke();
            }
        }

        // Found by the final review: damage applied after the unit's LateUpdate used to drop its deferred burst, because
        // the pending frame had already passed.
        [UnityTest]
        public IEnumerator DamageAfterLateUpdate_StillShowsExactlyOneDefaultBurst_OnTheNextFrame()
        {
            rig = new PresentationRig();
            var target = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            var late = rig.World.Track(new GameObject("AfterLateUpdate")).AddComponent<AfterLateUpdate>();
            yield return null;
            var dealt = false;
            late.Action = () => { target.TakeDamage(10); dealt = true; };
            yield return null;   // the damage lands after this frame's unit LateUpdate
            Assert.That(dealt, Is.True, "Precondition: the damage was dealt in the late phase");
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Impact), Is.EqualTo(0), "Precondition: not yet presented");
            yield return null;
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Impact), Is.EqualTo(1), "shown on the next LateUpdate");
            yield return null;
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Impact), Is.EqualTo(1), "shown once, not again on a later frame");
        }

        [UnityTest]
        public IEnumerator ARifleHitAfterLateUpdate_StillShowsOneCharacterImpact_NotTwo()
        {
            rig = new PresentationRig();
            var shooter = rig.CreateShooter(new Vector3(0f, 0f, -6f));
            var target = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            var late = rig.World.Track(new GameObject("AfterLateUpdate")).AddComponent<AfterLateUpdate>();
            yield return null;
            var fired = false;
            late.Action = () => fired = shooter.GetComponent<UnitAttacker>().TryAttack(target);
            yield return null;
            Assert.That(fired, Is.True, "Precondition: the shot was fired in the late phase");
            Assert.That(target.Current, Is.LessThan(target.Max), "Precondition: it hit");
            yield return null;
            yield return null;
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Impact), Is.EqualTo(1), "the shot's impact covers the damage");
        }

        [UnityTest]
        public IEnumerator ARifleHit_ShowsOneCharacterImpact_NotTwo()
        {
            rig = new PresentationRig();
            var shooter = rig.CreateShooter(new Vector3(0f, 0f, -6f));
            var target = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            yield return null;
            shooter.GetComponent<UnitAttacker>().TryAttack(target);
            yield return null;
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Impact), Is.EqualTo(1), "the shot's impact covers the damage");
        }

        [UnityTest]
        public IEnumerator DefaultBurst_StartsOnTheBodySurface_OnTheSideFacingTheMainCamera()
        {
            rig = new PresentationRig();
            var cameraObject = rig.World.Track(new GameObject("MainCam") { tag = "MainCamera" });
            cameraObject.AddComponent<Camera>();
            cameraObject.transform.position = new Vector3(8f, 5f, 4f);   // east of the target, well above it
            var target = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            yield return null;
            target.TakeDamage(10);
            yield return null;

            var burst = Object.FindObjectsByType<PooledEffect>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .Single(e => e.Category == EffectCategory.Impact);
            var offset = burst.transform.position - target.transform.position;
            Assert.That(new Vector2(offset.x, offset.z).magnitude, Is.EqualTo(0.5f).Within(0.02f), "on the capsule's surface, not on its axis");
            Assert.That(offset.x, Is.GreaterThan(0.45f), "on the side that faces the camera");
            Assert.That(offset.y, Is.EqualTo(ShotRecord.BodyHeight).Within(0.01f), "at chest height");
            Assert.That(Vector3.Dot(burst.transform.forward, Vector3.right), Is.GreaterThan(0.8f), "pointing out of the body, towards the viewer");
        }

        [UnityTest]
        public IEnumerator DefaultBurst_WithoutAMainCamera_IsStillOnTheSurface()
        {
            rig = new PresentationRig();
            var target = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            yield return null;
            target.TakeDamage(10);
            yield return null;
            var burst = Object.FindObjectsByType<PooledEffect>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .Single(e => e.Category == EffectCategory.Impact);
            var offset = burst.transform.position - target.transform.position;
            Assert.That(new Vector2(offset.x, offset.z).magnitude, Is.EqualTo(0.5f).Within(0.02f));
        }

        [UnityTest]
        public IEnumerator KillingBlow_ShowsNoDefaultBurst()
        {
            rig = new PresentationRig();
            var target = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            yield return null;
            target.TakeDamage(target.Max);
            yield return null;
            Assert.That(target.IsAlive, Is.False);
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Impact), Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator Died_ThroughTheUnitsOwnHook_PlaysTheDeathCue_AtAnObservedPoint()
        {
            rig = new PresentationRig();
            var clip = AudioClip.Create("death", 22050, 1, 44100, false);
            try
            {
                rig.FallbackProfile.SetDeathCue(AudioCue.Create(1f, 128, 0f, clip));
                var target = rig.CreateTarget(new Vector3(0f, 0f, 4f));
                yield return null;
                target.TakeDamage(target.Max);
                Assert.That(rig.Presenter.Audio.ActiveVoices, Is.EqualTo(1), "heard where the player can see it");
            }
            finally
            {
                Object.Destroy(clip);
            }
        }

        [UnityTest]
        public IEnumerator Died_InHiddenGround_ThroughTheUnitsOwnHook_IsSilent()
        {
            rig = new PresentationRig();
            var clip = AudioClip.Create("death", 22050, 1, 44100, false);
            try
            {
                rig.FallbackProfile.SetDeathCue(AudioCue.Create(1f, 128, 0f, clip));
                var target = rig.CreateTarget(new Vector3(8f, 0f, 4f));
                yield return null;
                rig.Presenter.UseKnowledge(p => p.x > 5f ? PointVisibility.Hidden : PointVisibility.Visible, u => u.IsAlive && u.transform.position.x <= 5f);
                target.TakeDamage(target.Max);
                Assert.That(rig.Presenter.Audio.ActiveVoices, Is.EqualTo(0));
            }
            finally
            {
                Object.Destroy(clip);
            }
        }

        [UnityTest]
        public IEnumerator Healed_ThroughTheUnitsOwnHook_SpawnsThePulse()
        {
            rig = new PresentationRig();
            var target = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            yield return null;
            target.TakeDamage(30);
            yield return null;
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Pulse), Is.EqualTo(0));
            Assert.That(target.Heal(20), Is.GreaterThan(0));
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Pulse), Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator DisablingThePresentation_MidFlash_RestoresTheTint()
        {
            rig = new PresentationRig();
            var team = new Color(0.5f, 0.6f, 1f, 1f);
            var target = BodyTarget(new Vector3(0f, 0f, 4f), team, out var body);
            yield return null;
            target.TakeDamage(10);
            Assert.That(BlockColor(body).r, Is.GreaterThan(team.r), "Precondition: mid-flash");
            target.GetComponent<UnitPresentation>().enabled = false;
            Assert.That(BlockColor(body).r, Is.EqualTo(team.r).Within(1e-4f));
            Assert.That(BlockColor(body).b, Is.EqualTo(team.b).Within(1e-4f));
        }

        [UnityTest]
        public IEnumerator ATintAddedAfterTheFlashSubscribed_IsHonouredAndNotWiped()
        {
            rig = new PresentationRig();
            var target = BodyTarget(new Vector3(0f, 0f, 4f), null, out var body);   // no tint yet
            yield return null;
            var team = new Color(0.4f, 0.5f, 0.9f, 1f);
            target.gameObject.AddComponent<UnitTeamTint>().SetTeamColor(team);
            target.TakeDamage(10);
            Assert.That(BlockColor(body).r, Is.GreaterThan(team.r), "flash is built on the late tint");
            yield return TestWorld.WaitUntil(() => Mathf.Abs(BlockColor(body).r - team.r) < 1e-4f, 2f);
            Assert.That(BlockColor(body).r, Is.EqualTo(team.r).Within(1e-4f), "the tint survives the end of the flash");
        }

        [UnityTest]
        public IEnumerator PresentationOff_NoFlashNoBurst_AndHealthIsUnchanged()
        {
            rig = new PresentationRig();
            var target = BodyTarget(new Vector3(0f, 0f, 4f), null, out var body);
            yield return null;
            rig.Presenter.Enabled = false;
            target.TakeDamage(10);
            yield return null;
            Assert.That(body.HasPropertyBlock(), Is.False);
            Assert.That(rig.Presenter.ActiveEffects, Is.EqualTo(0));
            Assert.That(target.Current, Is.EqualTo(target.Max - 10));
        }
    }
}
#endif
