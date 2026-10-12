using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class NetOrderTests
    {
        readonly List<GameObject> hosts = new List<GameObject>();

        [TearDown]
        public void TearDown() => NetTestKit.DestroyAll(hosts);

        [Test]
        public void KindOf_NamesTheCurrentOrder()
        {
            var health = NetTestKit.MakeUnit(hosts, "Target").GetComponent<Health>();

            Assert.That(NetOrder.KindOf(null), Is.EqualTo(NetOrderKind.None));
            Assert.That(NetOrder.KindOf(new MoveCommand(Vector3.zero)), Is.EqualTo(NetOrderKind.Move));
            Assert.That(NetOrder.KindOf(new AttackCommand(health)), Is.EqualTo(NetOrderKind.Attack));
            Assert.That(NetOrder.KindOf(new StopCommand()), Is.EqualTo(NetOrderKind.Other));
        }
    }
}
