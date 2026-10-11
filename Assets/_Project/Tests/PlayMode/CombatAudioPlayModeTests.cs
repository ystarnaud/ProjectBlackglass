using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CombatAudioPlayModeTests
    {
        TestWorld world;
        CombatAudio audio;
        CosmeticRandom random;
        readonly List<AudioClip> clips = new List<AudioClip>();

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            audio = world.Track(new GameObject("Audio")).AddComponent<CombatAudio>();
            audio.Initialize(10);
            random = new CosmeticRandom(3);
        }

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            foreach (var clip in clips)
                Object.DestroyImmediate(clip);
            clips.Clear();
        }

        AudioCue Cue(float seconds = 0.5f, int priority = 128, float minInterval = 0f)
        {
            var clip = AudioClip.Create("clip", Mathf.Max(1, (int)(44100 * seconds)), 1, 44100, false);
            clips.Add(clip);
            return AudioCue.Create(1f, priority, minInterval, clip);
        }

        [UnityTest]
        public IEnumerator Play_UsesASpatialVoice_AtTheGivenPosition_WithTheCuesFalloff()
        {
            var cue = Cue();
            Assert.That(audio.Play(cue, new Vector3(3f, 0f, 4f), 0.5f, random), Is.True);
            yield return null;
            Assert.That(audio.ActiveVoices, Is.EqualTo(1));
            var source = audio.Sources[0];
            Assert.That(source.transform.position, Is.EqualTo(new Vector3(3f, 0f, 4f)));
            Assert.That(source.spatialBlend, Is.EqualTo(1f));
            Assert.That(source.volume, Is.EqualTo(0.5f).Within(0.01f));
            Assert.That(source.maxDistance, Is.EqualTo(cue.MaxDistance));
        }

        [UnityTest]
        public IEnumerator EmptyOrNullCue_IsANoOp()
        {
            Assert.That(audio.Play(null, Vector3.zero, 1f, random), Is.False);
            Assert.That(audio.Play(new AudioCue(), Vector3.zero, 1f, random), Is.False);
            Assert.That(audio.ActiveVoices, Is.EqualTo(0));
            yield return null;
        }

        [UnityTest]
        public IEnumerator VoiceCap_IsEnforced()
        {
            for (var i = 0; i < 25; i++)
                audio.Play(Cue(2f), Vector3.zero, 1f, random);
            yield return null;
            Assert.That(audio.ActiveVoices, Is.LessThanOrEqualTo(10));
            Assert.That(audio.VoiceCount, Is.EqualTo(10));
        }

        [UnityTest]
        public IEnumerator SameCuePlayedTogether_DoesNotStackGain()
        {
            var cue = Cue(1f);
            for (var i = 0; i < 4; i++)
                audio.Play(cue, Vector3.zero, 1f, random);
            yield return null;
            var total = 0f;
            foreach (var source in audio.Sources)
                if (source.clip != null)
                    total += source.volume;
            Assert.That(total, Is.LessThan(3f), "four simultaneous plays of one cue are quieter than four full-volume sounds");
        }

        [UnityTest]
        public IEnumerator Pause_HoldsTheVoices_NeverReleasesThem_AndResumeFinishesThem()
        {
            audio.Play(Cue(0.5f), Vector3.zero, 1f, random);
            yield return null;
            audio.SetPaused(true);
            yield return new WaitForSecondsRealtime(0.8f);
            Assert.That(audio.IsPaused, Is.True);
            Assert.That(audio.ActiveVoices, Is.EqualTo(1), "a paused voice is not finished");
            Assert.That(audio.Play(Cue(), Vector3.zero, 1f, random), Is.False, "nothing new starts during a pause");

            audio.SetPaused(false);
            yield return new WaitForSecondsRealtime(0.7f);
            Assert.That(audio.ActiveVoices, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator RepeatedPauseResume_DoesNotDuplicateOrLeak()
        {
            audio.Play(Cue(0.4f), Vector3.zero, 1f, random);
            for (var i = 0; i < 10; i++)
            {
                audio.SetPaused(true);
                audio.SetPaused(true);
                audio.SetPaused(false);
            }
            Assert.That(audio.ActiveVoices, Is.EqualTo(1));
            yield return new WaitForSecondsRealtime(0.6f);
            Assert.That(audio.ActiveVoices, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator StopAll_ReleasesEverything_EvenWhilePaused()
        {
            audio.Play(Cue(2f), Vector3.zero, 1f, random);
            audio.SetPaused(true);
            audio.StopAll();
            Assert.That(audio.ActiveVoices, Is.EqualTo(0));
            yield return null;
        }

        [UnityTest]
        public IEnumerator ASoundDroppedForWantOfAVoice_DoesNotBlockALaterPlayOfTheSameCue()
        {
            for (var i = 0; i < 10; i++)
                Assert.That(audio.Play(Cue(0.3f, priority: 10), Vector3.zero, 1f, random), Is.True);
            var cue = Cue(0.2f, priority: 200, minInterval: 5f);
            Assert.That(audio.Play(cue, Vector3.zero, 1f, random), Is.False, "every voice is more important");
            yield return new WaitForSecondsRealtime(0.5f);
            Assert.That(audio.ActiveVoices, Is.EqualTo(0));
            Assert.That(audio.Play(cue, Vector3.zero, 1f, random), Is.True, "the dropped play did not consume the 5 s interval");
        }

        [UnityTest]
        public IEnumerator MinInterval_DropsARepeatedPlay()
        {
            var cue = Cue(0.5f, minInterval: 0.5f);
            Assert.That(audio.Play(cue, Vector3.zero, 1f, random), Is.True);
            Assert.That(audio.Play(cue, Vector3.zero, 1f, random), Is.False);
            yield return null;
        }
    }
}
