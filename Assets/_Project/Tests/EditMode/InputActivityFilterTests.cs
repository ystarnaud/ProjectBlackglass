using System;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class InputActivityFilterTests
    {
        [Test]
        public void StickBelowEngage_IsNotMeaningful()
        {
            var filter = new InputActivityFilter();
            Assert.That(filter.IsStickEngagement(1, new Vector2(0.5f, 0f)), Is.False);
        }

        [Test]
        public void StickAtEngage_IsMeaningfulOnce()
        {
            var filter = new InputActivityFilter();
            Assert.That(filter.IsStickEngagement(1, new Vector2(0.6f, 0f)), Is.True);
            Assert.That(filter.IsStickEngagement(1, new Vector2(0.6f, 0f)), Is.False, "Same deflection again is not new input");
        }

        [Test]
        public void StickMustFallBelowRelease_BeforeItCanEngageAgain()
        {
            var filter = new InputActivityFilter();
            Assert.That(filter.IsStickEngagement(1, new Vector2(0.9f, 0f)), Is.True);
            Assert.That(filter.IsStickEngagement(1, new Vector2(0.45f, 0f)), Is.False, "Between release and engage: still engaged");
            Assert.That(filter.IsStickEngagement(1, new Vector2(0.9f, 0f)), Is.False, "Never released, so no new engagement");
            Assert.That(filter.IsStickEngagement(1, new Vector2(0.2f, 0f)), Is.False, "Released");
            Assert.That(filter.IsStickEngagement(1, new Vector2(0.9f, 0f)), Is.True, "Pushed again");
        }

        [Test]
        public void SticksAreTrackedIndependently()
        {
            var filter = new InputActivityFilter();
            Assert.That(filter.IsStickEngagement(1, new Vector2(0.9f, 0f)), Is.True);
            Assert.That(filter.IsStickEngagement(2, new Vector2(0.9f, 0f)), Is.True);
        }

        [Test]
        public void DriftNeverEngages()
        {
            var filter = new InputActivityFilter();
            for (var i = 0; i < 200; i++)
            {
                var wobble = new Vector2(Mathf.Sin(i) * 0.3f, Mathf.Cos(i) * 0.3f);
                Assert.That(filter.IsStickEngagement(1, wobble), Is.False);
            }
        }

        [TestCase(0f, 1f, true)]
        [TestCase(1f, 1f, false)]
        [TestCase(0f, 0.4f, false)]
        [TestCase(0.4f, 0.6f, true)]
        [TestCase(1f, 0f, false)]
        public void ButtonPress_IsAnEdgeAcrossThePressPoint(float before, float after, bool expected)
        {
            Assert.That(new InputActivityFilter().IsButtonPress(before, after), Is.EqualTo(expected));
        }

        [Test]
        public void MouseMotion_NeedsAFewPixels()
        {
            var filter = new InputActivityFilter();
            Assert.That(filter.IsMouseMotion(new Vector2(1f, 1f)), Is.False);
            Assert.That(filter.IsMouseMotion(new Vector2(2f, 0f)), Is.False);
            Assert.That(filter.IsMouseMotion(new Vector2(3f, 3f)), Is.True);
        }

        [Test]
        public void AnyScroll_IsMeaningful()
        {
            var filter = new InputActivityFilter();
            Assert.That(filter.IsScroll(Vector2.zero), Is.False);
            Assert.That(filter.IsScroll(new Vector2(0f, -120f)), Is.True);
        }

        [Test]
        public void Reset_ForgetsEngagement()
        {
            var filter = new InputActivityFilter();
            filter.IsStickEngagement(1, new Vector2(0.9f, 0f));
            filter.Reset();
            Assert.That(filter.IsStickEngagement(1, new Vector2(0.9f, 0f)), Is.True);
        }

        [Test]
        public void Constructor_RejectsReleaseAboveEngage()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new InputActivityFilter(stickEngage: 0.4f, stickRelease: 0.5f));
        }
    }
}
