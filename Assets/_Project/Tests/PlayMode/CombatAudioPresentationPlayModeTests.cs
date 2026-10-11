#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CombatAudioPresentationPlayModeTests
    {
        PresentationRig rig;

        [TearDown]
        public void TearDown() => rig?.Dispose();

        static UnitAttacker AttackerOf(CommandableUnit unit) => unit.GetComponent<UnitAttacker>();

        [UnityTest]
        public IEnumerator AShot_PlaysTheFiringSound_AndTheImpactSound_ThroughTheListenerPool()
        {
            rig = new PresentationRig();
            var shooter = rig.CreateShooter(new Vector3(0f, 0f, -6f));
            var target = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            yield return null;
            Assert.That(AttackerOf(shooter).TryAttack(target), Is.True);
            Assert.That(rig.Presenter.Audio.ActiveVoices, Is.EqualTo(2), "one firing sound, one impact sound");
        }

        [UnityTest]
        public IEnumerator ARejectedAttack_PlaysNothing()
        {
            rig = new PresentationRig();
            var shooter = rig.CreateShooter(new Vector3(0f, 0f, -6f));
            var far = rig.CreateTarget(new Vector3(0f, 0f, 80f));
            yield return null;
            AttackerOf(shooter).TryAttack(far);
            Assert.That(rig.Presenter.Audio.ActiveVoices, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator HiddenShooter_AndHiddenImpacts_PlayNoSound_NoHearingChannel()
        {
            rig = new PresentationRig();
            var hidden = rig.CreateShooter(new Vector3(12f, 0f, -6f));
            var hiddenTarget = rig.CreateTarget(new Vector3(12f, 0f, 8f));
            yield return null;
            rig.Presenter.UseKnowledge(p => p.x > 5f ? PointVisibility.Hidden : PointVisibility.Visible, u => u.transform.position.x <= 5f);
            Assert.That(AttackerOf(hidden).TryAttack(hiddenTarget), Is.True, "Precondition: the hidden shot is really fired");
            Assert.That(rig.Presenter.Audio.ActiveVoices, Is.EqualTo(0));
        }

        // Volumes of the voices currently holding the given clip.
        static System.Collections.Generic.List<float> VolumesOf(CombatAudio audio, AudioClip clip)
        {
            var volumes = new System.Collections.Generic.List<float>();
            foreach (var source in audio.Sources)
                if (source.clip == clip)
                    volumes.Add(source.volume);
            return volumes;
        }

        [UnityTest]
        public IEnumerator SixShootersInOneFrame_StayInsideTheVoiceBudget_AndTheCueGainDrops()
        {
            rig = new PresentationRig();
            var fireClip = AudioClip.Create("fire", 44100 / 2, 1, 44100, false);
            // No minimum interval, so all six shots of the same frame reach the crowd gain instead of the repeat throttle.
            rig.RifleProfile.SetFireCue(AudioCue.Create(1f, 100, 0f, fireClip));
            var target = rig.CreateTarget(new Vector3(0f, 0f, 12f));
            var shooters = new CommandableUnit[6];
            for (var i = 0; i < shooters.Length; i++)
                shooters[i] = rig.CreateShooter(new Vector3(-5f + 2f * i, 0f, -6f));
            yield return null;
            try
            {
                foreach (var shooter in shooters)
                    Assert.That(AttackerOf(shooter).TryAttack(target), Is.True, "every one of the six shots is fired");
                Assert.That(rig.Presenter.Audio.ActiveVoices, Is.LessThanOrEqualTo(rig.Presenter.Audio.VoiceCount));
                var volumes = VolumesOf(rig.Presenter.Audio, fireClip);
                Assert.That(volumes.Count, Is.EqualTo(6), "Precondition: six firing sounds got voices");
                Assert.That(volumes.Max(), Is.LessThanOrEqualTo(1f));
                Assert.That(volumes.Distinct().Count(), Is.GreaterThan(1), "the crowded plays are not all at the same gain");
                Assert.That(volumes.Min(), Is.LessThan(volumes.Max()), "later plays of the same cue are quieter");
                Assert.That(volumes.Sum(), Is.LessThan(6f * 1f - 0.5f), "six shots together are much quieter than six times one");
            }
            finally
            {
                Object.DestroyImmediate(fireClip);
            }
        }

        [UnityTest]
        public IEnumerator Pause_HoldsTheSounds_ResumeReleasesThem_AndClearStopsThemEvenWhilePaused()
        {
            rig = new PresentationRig();
            var shooter = rig.CreateShooter(new Vector3(0f, 0f, -6f));
            var target = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            yield return null;
            AttackerOf(shooter).TryAttack(target);
            var voices = rig.Presenter.Audio.ActiveVoices;
            rig.Pause.Pause();
            Assert.That(rig.Presenter.Audio.IsPaused, Is.True);
            yield return new WaitForSecondsRealtime(0.6f);
            Assert.That(rig.Presenter.Audio.ActiveVoices, Is.EqualTo(voices), "paused sounds are neither finished nor duplicated");
            rig.Presenter.Clear();
            Assert.That(rig.Presenter.Audio.ActiveVoices, Is.EqualTo(0));
            rig.Pause.Resume();
            Assert.That(rig.Presenter.Audio.IsPaused, Is.False);
        }

        [UnityTest]
        public IEnumerator Death_PlaysTheDeathSound_OnlyForAnObservedUnit()
        {
            rig = new PresentationRig();
            var visible = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            var hidden = rig.CreateTarget(new Vector3(3f, 0f, 4f));
            yield return null;
            // a dead unit is judged by where it died (ObservesAt), so the hidden one also stands on hidden ground
            rig.Presenter.UseKnowledge(p => p.x > 1.5f ? PointVisibility.Hidden : PointVisibility.Visible, u => u != hidden);
            hidden.TakeDamage(hidden.Max);
            Assert.That(rig.Presenter.Audio.ActiveVoices, Is.EqualTo(0));
            visible.TakeDamage(visible.Max);
            Assert.That(rig.Presenter.Audio.ActiveVoices, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator MissingClips_FailSafely()
        {
            rig = new PresentationRig();
            rig.RifleProfile.SetFireCue(new AudioCue());
            var shooter = rig.CreateShooter(new Vector3(0f, 0f, -6f));
            var target = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            yield return null;
            Assert.That(AttackerOf(shooter).TryAttack(target), Is.True);
            Assert.That(rig.Presenter.Audio.ActiveVoices, Is.EqualTo(1), "the impact sound still plays, the empty fire cue is silent");
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Muzzle), Is.EqualTo(1), "and the muzzle flash still shows");
            LogAssert.NoUnexpectedReceived();
        }
    }
}
#endif
