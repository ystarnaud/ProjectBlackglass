using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class NetProtocolTests
    {
        readonly List<GameObject> hosts = new List<GameObject>();

        [TearDown]
        public void TearDown() => NetTestKit.DestroyAll(hosts);

        CommandableUnit Unit(string name) => NetTestKit.MakeUnit(hosts, name);

        [Test]
        public void RequestTypes_AreExactlyTheFour_SoNoMessageCanSetHealthOrPosition()
        {
            var types = typeof(NetRequest).Assembly.GetTypes()
                .Where(t => !t.IsAbstract && typeof(NetRequest).IsAssignableFrom(t))
                .Select(t => t.Name).OrderBy(n => n).ToArray();
            Assert.That(types, Is.EqualTo(new[] { "NetAttack", "NetMoveIntent", "NetMoveTo", "NetStop" }));
        }

        [Test]
        public void Register_NumbersUnitsFromOne_AndKeepsTheirSides()
        {
            var units = new NetUnits();
            var friend = Unit("FriendlyUnit_1");
            var hostile = Unit("HostileUnit_1");
            Assert.That(units.Register(friend, false), Is.EqualTo(1));
            Assert.That(units.Register(hostile, true), Is.EqualTo(2));
            Assert.That(units.Count, Is.EqualTo(2));
            Assert.That(units.IsHostile(1), Is.False);
            Assert.That(units.IsHostile(2), Is.True);
            Assert.That(units.IdOf(hostile), Is.EqualTo(2));
            Assert.That(units.IdOf(Unit("Stranger")), Is.EqualTo(0));
            Assert.That(units.TryGet(1, out var found), Is.True);
            Assert.That(found, Is.SameAs(friend));
        }

        [TestCase(0)]
        [TestCase(-1)]
        [TestCase(int.MaxValue)]
        [TestCase(99)]
        public void TryGet_UnknownIds_ResolveToNothing(int id)
        {
            var units = new NetUnits();
            units.Register(Unit("FriendlyUnit_1"), false);
            Assert.That(units.TryGet(id, out var unit), Is.False);
            Assert.That(unit == null, Is.True);
            Assert.That(units.IsHostile(id), Is.False);
        }

        [Test]
        public void TryGet_ADestroyedUnit_ResolvesToNothing()
        {
            var units = new NetUnits();
            var friend = Unit("FriendlyUnit_1");
            var id = units.Register(friend, false);
            Object.DestroyImmediate(friend.gameObject);
            Assert.That(units.TryGet(id, out _), Is.False);
        }

        [Test]
        public void Ids_AreNeverReused()
        {
            var units = new NetUnits();
            var first = Unit("A");
            var firstId = units.Register(first, false);
            units.Register(Unit("B"), false);
            Object.DestroyImmediate(first.gameObject);
            Assert.That(units.Register(Unit("C"), false), Is.EqualTo(3));
            Assert.That(units.TryGet(firstId, out _), Is.False);
        }

        [Test]
        public void FromMission_RegistersFriendliesThenHostiles()
        {
            var friendlies = new List<CommandableUnit> { Unit("F1"), Unit("F2") };
            var hostiles = new List<CommandableUnit> { Unit("H1") };
            var units = NetUnits.FromMission(friendlies, hostiles);
            Assert.That(units.IdOf(friendlies[0]), Is.EqualTo(1));
            Assert.That(units.IdOf(friendlies[1]), Is.EqualTo(2));
            Assert.That(units.IdOf(hostiles[0]), Is.EqualTo(3));
            Assert.That(units.IsHostile(3), Is.True);
        }
    }
}
