using NUnit.Framework;

namespace Blackglass.Tests
{
    public class CombatAudioLogicTests
    {
        [Test]
        public void Throttle_RefusesPlaysCloserThanTheInterval()
        {
            var throttle = new CueThrottle();
            Assert.That(throttle.TryPlay(1, 0f, 0.06f, out _), Is.True);
            Assert.That(throttle.TryPlay(1, 0.03f, 0.06f, out _), Is.False);
            Assert.That(throttle.TryPlay(1, 0.07f, 0.06f, out _), Is.True);
            Assert.That(throttle.TryPlay(2, 0.03f, 0.06f, out _), Is.True, "another cue has its own interval");
        }

        [Test]
        public void Throttle_CrowdedPlaysOfTheSameCue_GetQuieter()
        {
            var throttle = new CueThrottle();
            throttle.TryPlay(1, 0.00f, 0f, out var first);
            throttle.TryPlay(1, 0.02f, 0f, out var second);
            throttle.TryPlay(1, 0.04f, 0f, out var third);
            Assert.That(first, Is.EqualTo(1f));
            Assert.That(second, Is.LessThan(first));
            Assert.That(third, Is.LessThan(second));
            throttle.TryPlay(1, 1.0f, 0f, out var later);
            Assert.That(later, Is.EqualTo(1f), "the crowd window passed");
        }

        [Test]
        public void Throttle_MinIntervalLongerThanTheCrowdWindow_StillRefusesRepeats()
        {
            var throttle = new CueThrottle();
            Assert.That(throttle.TryPlay(1, 0f, 0.5f, out _), Is.True);
            Assert.That(throttle.TryPlay(1, 0.3f, 0.5f, out _), Is.False);
            Assert.That(throttle.TryPlay(1, 0.6f, 0.5f, out _), Is.True);
        }

        [Test]
        public void Throttle_ARefusedPlay_DoesNotExtendTheInterval()
        {
            var throttle = new CueThrottle();
            Assert.That(throttle.TryPlay(1, 0f, 0.5f, out _), Is.True);
            Assert.That(throttle.TryPlay(1, 0.4f, 0.5f, out _), Is.False);
            Assert.That(throttle.TryPlay(1, 0.55f, 0.5f, out _), Is.True, "measured from the last accepted play (0), not the refused one (0.4)");
        }

        [Test]
        public void Throttle_Clear_ForgetsEverything()
        {
            var throttle = new CueThrottle();
            throttle.TryPlay(1, 0f, 5f, out _);
            throttle.Clear();
            Assert.That(throttle.TryPlay(1, 0.01f, 5f, out _), Is.True);
        }

        [Test]
        public void Picker_PrefersAFreeVoice()
        {
            Assert.That(VoicePicker.Pick(new[] { true, false, true }, new[] { 10, 10, 10 }, new long[] { 1, 2, 3 }, 128), Is.EqualTo(1));
        }

        [Test]
        public void Picker_StealsTheLeastImportantThenOldest_ButNeverAMoreImportantVoice()
        {
            var busy = new[] { true, true, true };
            Assert.That(VoicePicker.Pick(busy, new[] { 50, 200, 200 }, new long[] { 5, 9, 3 }, 128), Is.EqualTo(2), "priority 200 is less important; the older of the two is voice 2");
            Assert.That(VoicePicker.Pick(busy, new[] { 130, 160, 190 }, new long[] { 1, 2, 3 }, 128), Is.EqualTo(2), "the least important one");
            Assert.That(VoicePicker.Pick(busy, new[] { 10, 20, 30 }, new long[] { 1, 2, 3 }, 5), Is.EqualTo(2), "a more important new sound steals the least important voice");
        }

        [Test]
        public void Picker_DropsTheNewSound_WhenEveryVoiceIsMoreImportant()
        {
            Assert.That(VoicePicker.Pick(new[] { true, true }, new[] { 10, 20 }, new long[] { 1, 2 }, 200), Is.EqualTo(-1));
        }
    }
}
