using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class ObservationRulesTests
    {
        [Test]
        public void InCircle_IsFlatAndInclusive()
        {
            Assert.That(ObservationRules.InCircle(Vector3.zero, 5f, new Vector3(3f, 9f, 4f)), Is.True, "height is ignored; 5 m exactly is in");
            Assert.That(ObservationRules.InCircle(Vector3.zero, 5f, new Vector3(3f, 0f, 4.1f)), Is.False);
        }

        [TestCase(0f, 5f, true)]
        [TestCase(44f, 5f, true)]
        [TestCase(46f, 5f, false)]
        [TestCase(0f, 12.5f, false)]
        [TestCase(180f, 5f, false)]
        public void InCone_NeedsRangeAndAnAngleWithinTheHalfAngle(float degreesOffAxis, float distance, bool expected)
        {
            var direction = Quaternion.Euler(0f, degreesOffAxis, 0f) * Vector3.forward;
            var point = new Vector3(1f, 2.6f, 1f) + direction * distance;
            Assert.That(ObservationRules.InCone(new Vector3(1f, 2.6f, 1f), Vector3.forward, 45f, 12f, point), Is.EqualTo(expected));
        }

        [Test]
        public void InCone_ForwardWithAHeightComponentIsFlattened()
        {
            Assert.That(ObservationRules.InCone(Vector3.zero, new Vector3(0f, -0.5f, 1f), 45f, 12f, new Vector3(0f, 0f, 6f)), Is.True);
        }

        [Test]
        public void InCone_AtTheOriginItselfIsIn()
        {
            Assert.That(ObservationRules.InCone(Vector3.zero, Vector3.forward, 45f, 12f, new Vector3(0f, 1f, 0f)), Is.True);
        }
    }
}
