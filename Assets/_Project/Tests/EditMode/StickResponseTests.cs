using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class StickResponseTests
    {
        [Test]
        public void Curve_OfZero_IsZero() => Assert.That(StickResponse.Curve(Vector2.zero, 2f), Is.EqualTo(Vector2.zero));

        [Test]
        public void Curve_KeepsTheDirection_AndShapesTheMagnitude()
        {
            var shaped = StickResponse.Curve(new Vector2(0.6f, 0.8f) * 0.5f, 2f);
            Assert.That(shaped.normalized.x, Is.EqualTo(0.6f).Within(1e-4f));
            Assert.That(shaped.magnitude, Is.EqualTo(0.25f).Within(1e-4f));
        }

        [Test]
        public void Curve_ClampsMagnitudeToOne() =>
            Assert.That(StickResponse.Curve(new Vector2(3f, 0f), 1.5f).magnitude, Is.EqualTo(1f).Within(1e-4f));

        [Test]
        public void Curve_NeverBelowLinear_ForAnExponentUnderOne() =>
            Assert.That(StickResponse.Curve(new Vector2(0.5f, 0f), 0.2f).magnitude, Is.EqualTo(0.5f).Within(1e-4f));

        [TestCase(false, false, false)]
        [TestCase(true, false, true)]
        [TestCase(false, true, true)]
        [TestCase(true, true, false)]
        public void RightStickRole_IsRunningXorModifier(bool running, bool modifier, bool cameraOwns) =>
            Assert.That(StickRole.CameraOwnsRightStick(running, modifier), Is.EqualTo(cameraOwns));

        [Test]
        public void RightStickRole_WithNoActiveCharacter_TreatsTheGameAsRunning()
        {
            Assert.That(StickRole.CameraOwnsRightStick((ActiveCharacter)null, false), Is.True);
            Assert.That(StickRole.CameraOwnsRightStick((ActiveCharacter)null, true), Is.False);
        }
    }
}
