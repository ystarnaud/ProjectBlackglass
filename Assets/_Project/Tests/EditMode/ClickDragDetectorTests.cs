using System;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class ClickDragDetectorTests
    {
        static readonly Vector2 Origin = new Vector2(100f, 100f);

        ClickDragDetector detector;

        [SetUp]
        public void SetUp() => detector = new ClickDragDetector(6f);

        [Test]
        public void PressAndReleaseInPlace_IsClick()
        {
            detector.Press(Origin);
            Assert.That(detector.Release(Origin), Is.True);
        }

        [Test]
        public void MovementUpToThreshold_IsStillClick()
        {
            var edge = Origin + new Vector2(6f, 0f);
            detector.Press(Origin);
            detector.Track(edge);
            Assert.That(detector.IsDragging, Is.False);
            Assert.That(detector.Release(edge), Is.True);
        }

        [Test]
        public void MovementPastThreshold_IsDrag()
        {
            var beyond = Origin + new Vector2(6.5f, 0f);
            detector.Press(Origin);
            detector.Track(beyond);
            Assert.That(detector.IsDragging, Is.True);
            Assert.That(detector.Release(beyond), Is.False);
        }

        [Test]
        public void ReturningToPressPoint_AfterDrag_IsStillDrag()
        {
            detector.Press(Origin);
            detector.Track(Origin + new Vector2(0f, 20f));
            detector.Track(Origin);
            Assert.That(detector.Release(Origin), Is.False);
        }

        [Test]
        public void FastFlick_ReleasedFarAway_IsDrag()
        {
            detector.Press(Origin);
            Assert.That(detector.Release(Origin + new Vector2(0f, 50f)), Is.False);
        }

        [Test]
        public void ReleaseWithoutPress_IsNotClick()
        {
            Assert.That(detector.Release(Origin), Is.False);
        }

        [Test]
        public void NewPress_ClearsPreviousDrag()
        {
            detector.Press(Origin);
            detector.Track(Origin + new Vector2(30f, 0f));
            detector.Release(Origin + new Vector2(30f, 0f));

            detector.Press(Origin);
            Assert.That(detector.IsDragging, Is.False);
            Assert.That(detector.Release(Origin), Is.True);
        }

        [Test]
        public void IsPressed_FollowsPressAndRelease()
        {
            Assert.That(detector.IsPressed, Is.False);
            detector.Press(Origin);
            Assert.That(detector.IsPressed, Is.True);
            detector.Release(Origin);
            Assert.That(detector.IsPressed, Is.False);
        }

        [Test]
        public void TrackWithoutPress_DoesNothing()
        {
            detector.Track(Origin + new Vector2(100f, 0f));
            Assert.That(detector.IsDragging, Is.False);
        }

        [Test]
        public void NegativeThreshold_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new ClickDragDetector(-1f));
        }
    }
}
