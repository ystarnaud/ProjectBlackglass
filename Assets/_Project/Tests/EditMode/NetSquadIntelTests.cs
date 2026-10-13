using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class NetSquadIntelTests
    {
        readonly List<GameObject> hosts = new List<GameObject>();
        NetUnits units;
        CommandableUnit friend1;
        CommandableUnit friend2;

        [SetUp]
        public void SetUp()
        {
            friend1 = NetTestKit.MakeUnit(hosts, "FriendlyUnit_1");
            friend2 = NetTestKit.MakeUnit(hosts, "FriendlyUnit_2");
            units = NetUnits.FromMission(new List<CommandableUnit> { friend1, friend2 },
                new List<CommandableUnit> { NetTestKit.MakeUnit(hosts, "HostileUnit_1") });
        }

        [TearDown]
        public void TearDown() => NetTestKit.DestroyAll(hosts);

        static void Kill(CommandableUnit unit) => unit.GetComponent<Health>().TakeDamage(100000);

        [Test]
        public void OwnsSquad_OnlyWhenEveryLivingFriendlyIsOwned()
        {
            Assert.That(NetSquadIntel.OwnsSquad(new NetConnection("a", new[] { 1, 2 }), units, 2), Is.True);
            Assert.That(NetSquadIntel.OwnsSquad(new NetConnection("half", new[] { 1 }), units, 2), Is.False, "split ownership fails closed");
            Assert.That(NetSquadIntel.OwnsSquad(new NetConnection("b", new int[0]), units, 2), Is.False);
            Assert.That(NetSquadIntel.OwnsSquad(null, units, 2), Is.False);
        }

        [Test]
        public void OwnsSquad_IgnoresDeadFriendlies_ButNeedsOneOwned()
        {
            Kill(friend2);
            Assert.That(NetSquadIntel.OwnsSquad(new NetConnection("half", new[] { 1 }), units, 2), Is.True, "the dead do not feed the model");
            Assert.That(NetSquadIntel.OwnsSquad(new NetConnection("b", new int[0]), units, 2), Is.False);
        }

        [Test]
        public void WhenEveryFriendlyIsDead_AConnectionOwningNothing_StillGetsNothing()
        {
            Kill(friend1);
            Kill(friend2);
            Assert.That(NetSquadIntel.OwnsSquad(new NetConnection("b", new int[0]), units, 2), Is.False);
            Assert.That(NetSquadIntel.OwnsSquad(new NetConnection("a", new[] { 1, 2 }), units, 2), Is.True);
        }

        [Test]
        public void AMissingService_IsAProblem_AndNothingIsObserved_WithOneLogLine()
        {
            Assert.That(NetSquadIntel.Problem(null), Does.Contain("no IntelligenceService"));
            var logs = new List<string>();
            var source = new NetSquadIntel(null, units, 2, logs.Add);
            var owner = new NetConnection("a", new[] { 1, 2 });
            Assert.That(source.Observes(owner, 3), Is.False);
            Assert.That(source.Observes(owner, 3), Is.False);
            Assert.That(logs, Has.Count.EqualTo(1), "a failure is logged once, not every frame");
            StringAssert.Contains("fails closed", logs[0]);
        }
    }
}
