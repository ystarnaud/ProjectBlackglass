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

        [TestCase(false, false, false, "Controlled: Hero | Takeover OFF (V) | Idle")]
        [TestCase(false, false, true, "Controlled: Hero | Takeover OFF (V) | Following orders")]
        [TestCase(false, true, false, "Controlled: Hero | Takeover OFF (V) | Idle")]
        [TestCase(false, true, true, "Controlled: Hero | Takeover OFF (V) | Following orders")]
        [TestCase(true, false, false, "Controlled: Hero | Takeover ON (V) | Manual control")]
        [TestCase(true, false, true, "Controlled: Hero | Takeover ON (V) | Following orders")]
        [TestCase(true, true, false, "Controlled: Hero | Takeover ON (after pause) | Idle")]
        [TestCase(true, true, true, "Controlled: Hero | Takeover ON (after pause) | Following orders")]
        public void DescribeActive_ShowsModeAndActivity(bool takeoverOn, bool isPaused, bool hasOrders, string expected)
        {
            Assert.That(PrototypeHud.DescribeActive("Hero", takeoverOn, isPaused, hasOrders), Is.EqualTo(expected));
        }
    }
}
