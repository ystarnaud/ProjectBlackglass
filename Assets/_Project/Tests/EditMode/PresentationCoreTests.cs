using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class PresentationCoreTests
    {
        // ---- budget ----
        [Test]
        public void Budget_RefusesAtTheCap_CountsTheRefusal_AndReleaseFreesASlot()
        {
            var budget = new EffectBudget(PresentationQuality.High);
            var cap = budget.Cap(EffectCategory.Muzzle);
            for (var i = 0; i < cap; i++)
                Assert.That(budget.TryAcquire(EffectCategory.Muzzle), Is.True);
            Assert.That(budget.TryAcquire(EffectCategory.Muzzle), Is.False);
            Assert.That(budget.Refused, Is.EqualTo(1));
            budget.Release(EffectCategory.Muzzle);
            Assert.That(budget.TryAcquire(EffectCategory.Muzzle), Is.True);
            Assert.That(budget.Active(EffectCategory.Muzzle), Is.EqualTo(cap));
        }

        [Test]
        public void Budget_LowQualityHasSmallerCaps_ButAtLeastOne()
        {
            var high = new EffectBudget(PresentationQuality.High);
            var low = new EffectBudget(PresentationQuality.Low);
            foreach (EffectCategory category in System.Enum.GetValues(typeof(EffectCategory)))
            {
                Assert.That(low.Cap(category), Is.LessThanOrEqualTo(high.Cap(category)));
                Assert.That(low.Cap(category), Is.GreaterThanOrEqualTo(1));
            }
            Assert.That(low.LightCap, Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public void Budget_LightsAreCappedSeparately_AndReleaseNeverGoesNegative()
        {
            var budget = new EffectBudget(PresentationQuality.High);
            for (var i = 0; i < budget.LightCap; i++)
                Assert.That(budget.TryAcquireLight(), Is.True);
            Assert.That(budget.TryAcquireLight(), Is.False);
            for (var i = 0; i < budget.LightCap + 3; i++)
                budget.ReleaseLight();
            Assert.That(budget.LightsActive, Is.EqualTo(0));
            budget.Release(EffectCategory.Tracer);
            Assert.That(budget.Active(EffectCategory.Tracer), Is.EqualTo(0));
        }

        [Test]
        public void Budget_Reset_ClearsEverything()
        {
            var budget = new EffectBudget(PresentationQuality.Medium);
            budget.TryAcquire(EffectCategory.Impact);
            budget.TryAcquireLight();
            budget.Reset();
            Assert.That(budget.Active(EffectCategory.Impact), Is.EqualTo(0));
            Assert.That(budget.LightsActive, Is.EqualTo(0));
            Assert.That(budget.Refused, Is.EqualTo(0));
        }

        // ---- cosmetic random ----
        [Test]
        public void CosmeticRandom_SameSeedSameSequence_AndRangeStaysInside()
        {
            var a = new CosmeticRandom(42);
            var b = new CosmeticRandom(42);
            for (var i = 0; i < 50; i++)
            {
                var v = a.Range(0.6f, 1.2f);
                Assert.That(v, Is.EqualTo(b.Range(0.6f, 1.2f)));
                Assert.That(v, Is.InRange(0.6f, 1.2f));
            }
            Assert.That(Mathf.Abs(new CosmeticRandom(1).Sign()), Is.EqualTo(1f));
        }

        [Test]
        public void CosmeticRandom_NeverTouchesUnityRandom()
        {
            Random.InitState(1234);
            var expected = new float[5];
            for (var i = 0; i < expected.Length; i++)
                expected[i] = Random.value;

            Random.InitState(1234);
            var cosmetic = new CosmeticRandom();
            for (var i = 0; i < 200; i++)
                cosmetic.Value();
            for (var i = 0; i < expected.Length; i++)
                Assert.That(Random.value, Is.EqualTo(expected[i]), "the gameplay random stream moved");
        }

        // ---- sequence filter ----
        [Test]
        public void SequenceFilter_AcceptsEachSequenceOnce_PerEmitter()
        {
            var filter = new SequenceFilter();
            Assert.That(filter.TryAccept(1, 1), Is.True);
            Assert.That(filter.TryAccept(1, 1), Is.False, "the same record twice");
            Assert.That(filter.TryAccept(1, 2), Is.True);
            Assert.That(filter.TryAccept(2, 1), Is.True, "another emitter has its own counter");
            Assert.That(filter.TryAccept(1, 1), Is.False);
            filter.Clear();
            Assert.That(filter.TryAccept(1, 1), Is.True);
        }

        // ---- recoil ----
        [Test]
        public void Recoil_KicksThenRecovers_OnlyAsTimeAdvances()
        {
            var recoil = new RecoilEnvelope();
            Assert.That(recoil.Offset, Is.EqualTo(0f));
            recoil.Kick(0.04f, 0.1f);
            Assert.That(recoil.Offset, Is.EqualTo(0.04f).Within(1e-6f));
            recoil.Advance(0f);
            Assert.That(recoil.Offset, Is.EqualTo(0.04f).Within(1e-6f), "no time, no movement (pause)");
            recoil.Advance(0.05f);
            Assert.That(recoil.Offset, Is.InRange(0.005f, 0.035f));
            recoil.Advance(0.2f);
            Assert.That(recoil.Offset, Is.EqualTo(0f));
            Assert.That(recoil.IsActive, Is.False);
        }

        [Test]
        public void Recoil_NegativeOrZeroKick_DoesNothing_AndResetClears()
        {
            var recoil = new RecoilEnvelope();
            recoil.Kick(-1f, 0.1f);
            Assert.That(recoil.IsActive, Is.False);
            recoil.Kick(0.03f, 0f);
            Assert.That(float.IsNaN(recoil.Offset), Is.False);
            recoil.Reset();
            Assert.That(recoil.Offset, Is.EqualTo(0f));
        }

        // ---- impulse ----
        [Test]
        public void Impulse_DecaysToZero_AndNeverExceedsTheMaximum()
        {
            var impulse = new ImpulseEnvelope();
            impulse.Add(5f, 1f, 2f);   // clamped to 1
            for (var t = 0f; t < ImpulseEnvelope.Duration; t += 0.016f)
            {
                Assert.That(impulse.Offset.magnitude, Is.LessThanOrEqualTo(ImpulseEnvelope.MaxOffset * 1.5f));
                impulse.Advance(0.016f);
            }
            impulse.Advance(1f);
            Assert.That(impulse.Offset, Is.EqualTo(Vector2.zero));
            Assert.That(impulse.Amplitude, Is.EqualTo(0f));
        }

        [Test]
        public void Impulse_FrozenWithoutAdvance_AndAWeakerImpulseDoesNotReplaceAStrongerOne()
        {
            var impulse = new ImpulseEnvelope();
            impulse.Add(1f, 0f, 0f);
            impulse.Advance(0.1f);
            var frozen = impulse.Offset;
            impulse.Advance(0f);
            Assert.That(impulse.Offset, Is.EqualTo(frozen));
            var amplitude = impulse.Amplitude;
            impulse.Add(0.1f, 3f, 3f);
            Assert.That(impulse.Amplitude, Is.EqualTo(amplitude), "a weaker impulse is ignored while a stronger one is running");
        }

        [Test]
        public void Impulse_ZeroStrength_IsIgnored()
        {
            var impulse = new ImpulseEnvelope();
            impulse.Add(0f, 1f, 1f);
            impulse.Advance(0.05f);
            Assert.That(impulse.Offset, Is.EqualTo(Vector2.zero));
        }
    }
}
