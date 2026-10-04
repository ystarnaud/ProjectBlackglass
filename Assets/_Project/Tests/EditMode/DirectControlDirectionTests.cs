using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class DirectControlDirectionTests
    {
        static void AssertDirection(Vector3 actual, float x, float z)
        {
            Assert.That(actual.x, Is.EqualTo(x).Within(1e-4f), "x");
            Assert.That(actual.y, Is.EqualTo(0f), "y");
            Assert.That(actual.z, Is.EqualTo(z).Within(1e-4f), "z");
        }

        // W ("into the screen") follows the camera's yaw.
        [TestCase(0f, 0f, 1f)]
        [TestCase(90f, 1f, 0f)]
        [TestCase(180f, 0f, -1f)]
        [TestCase(-90f, -1f, 0f)]
        public void ToWorldDirection_ForwardFollowsTheCameraYaw(float yaw, float x, float z)
        {
            AssertDirection(DirectControlInput.ToWorldDirection(Vector2.up, yaw), x, z);
        }

        [Test]
        public void ToWorldDirection_RightIsTheCamerasRight()
        {
            AssertDirection(DirectControlInput.ToWorldDirection(Vector2.right, 0f), 1f, 0f);
            AssertDirection(DirectControlInput.ToWorldDirection(Vector2.right, 90f), 0f, -1f);
        }

        [Test]
        public void ToWorldDirection_ClampsDiagonalsToLengthOne()
        {
            var direction = DirectControlInput.ToWorldDirection(new Vector2(1f, 1f), 0f);
            Assert.That(direction.magnitude, Is.EqualTo(1f).Within(1e-4f));
            AssertDirection(direction, 0.70711f, 0.70711f);
        }

        [Test]
        public void ToWorldDirection_ZeroIsZero()
        {
            Assert.That(DirectControlInput.ToWorldDirection(Vector2.zero, 37f), Is.EqualTo(Vector3.zero));
        }
    }
}
