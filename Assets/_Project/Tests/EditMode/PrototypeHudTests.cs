using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class PrototypeHudTests
    {
        [Test]
        public void DescribeOrders_Idle_IsEmpty()
        {
            Assert.That(PrototypeHud.DescribeOrders(null, 0), Is.Empty);
        }

        [Test]
        public void DescribeOrders_NamesTheCurrentOrder()
        {
            Assert.That(PrototypeHud.DescribeOrders(new MoveCommand(Vector3.zero), 0), Is.EqualTo("Move"));
        }

        [Test]
        public void DescribeOrders_CountsPendingOrders()
        {
            var host = new GameObject("Target");
            var attack = new AttackCommand(host.AddComponent<Health>());
            Assert.That(PrototypeHud.DescribeOrders(attack, 2), Is.EqualTo("Attack +2"));
            Object.DestroyImmediate(host);
        }

        [TestCase(false, false, false, "Primary: Hero | Takeover OFF (V) | Idle")]
        [TestCase(false, false, true, "Primary: Hero | Takeover OFF (V) | Following orders")]
        [TestCase(false, true, false, "Primary: Hero | Takeover OFF (V) | Idle")]
        [TestCase(false, true, true, "Primary: Hero | Takeover OFF (V) | Following orders")]
        [TestCase(true, false, false, "Primary: Hero | Takeover ON (V) | Manual control")]
        [TestCase(true, false, true, "Primary: Hero | Takeover ON (V) | Following orders")]
        [TestCase(true, true, false, "Primary: Hero | Takeover ON (after pause) | Idle")]
        [TestCase(true, true, true, "Primary: Hero | Takeover ON (after pause) | Following orders")]
        public void DescribePrimary_ShowsModeAndActivity(bool takeoverOn, bool isPaused, bool hasOrders, string expected)
        {
            Assert.That(PrototypeHud.DescribePrimary("Hero", takeoverOn, isPaused, hasOrders), Is.EqualTo(expected));
        }
    }
}
