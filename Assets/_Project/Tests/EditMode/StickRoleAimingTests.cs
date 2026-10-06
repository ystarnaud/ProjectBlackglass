using NUnit.Framework;

namespace Blackglass.Tests
{
    public class StickRoleAimingTests
    {
        [TestCase(true, false)]
        [TestCase(true, true)]
        [TestCase(false, false)]
        [TestCase(false, true)]
        public void WhileAiming_TheCursorAlwaysOwnsTheStick(bool running, bool modifierHeld)
        {
            Assert.That(StickRole.CameraOwnsRightStick(running, modifierHeld, true), Is.False);
        }

        [TestCase(true, false, true)]
        [TestCase(true, true, false)]
        [TestCase(false, false, false)]
        [TestCase(false, true, true)]
        public void WhenNotAiming_TheOldRuleStands(bool running, bool modifierHeld, bool cameraOwns)
        {
            Assert.That(StickRole.CameraOwnsRightStick(running, modifierHeld), Is.EqualTo(cameraOwns));
            Assert.That(StickRole.CameraOwnsRightStick(running, modifierHeld, false), Is.EqualTo(cameraOwns));
        }

        [Test]
        public void WithNoActiveCharacter_AimingStillHandsTheStickToTheCursor()
        {
            Assert.That(StickRole.CameraOwnsRightStick((ActiveCharacter)null, false), Is.True);
            Assert.That(StickRole.CameraOwnsRightStick((ActiveCharacter)null, false, true), Is.False);
        }
    }
}
