using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    // Attack and Stop only: Move needs a NavMesh and is covered by GroupOrdersPlayModeTests.
    public class GroupOrdersTests
    {
        readonly List<GameObject> hosts = new List<GameObject>();
        Health target;

        [SetUp]
        public void SetUp()
        {
            var targetHost = new GameObject("Target");
            hosts.Add(targetHost);
            target = targetHost.AddComponent<Health>();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var host in hosts)
                Object.DestroyImmediate(host);
            hosts.Clear();
        }

        CommandableUnit CreateUnit()
        {
            var host = new GameObject("Unit");
            hosts.Add(host);
            return host.AddComponent<CommandableUnit>();
        }

        [Test]
        public void Attack_GivesEveryUnitTheSameOrder()
        {
            var units = new[] { CreateUnit(), CreateUnit(), CreateUnit() };
            var attack = new AttackCommand(target);

            Assert.That(GroupOrders.Issue(units, attack, IssueMode.Replace), Is.EqualTo(3));
            foreach (var unit in units)
                Assert.That(unit.CurrentCommand, Is.SameAs(attack));
        }

        [Test]
        public void Attack_Append_QueuesBehindExistingOrders()
        {
            var units = new[] { CreateUnit(), CreateUnit() };
            var first = new AttackCommand(target);
            GroupOrders.Issue(units, first, IssueMode.Replace);
            var otherHost = new GameObject("Other");
            hosts.Add(otherHost);
            var second = new AttackCommand(otherHost.AddComponent<Health>());

            Assert.That(GroupOrders.Issue(units, second, IssueMode.Append), Is.EqualTo(2));
            foreach (var unit in units)
            {
                Assert.That(unit.CurrentCommand, Is.SameAs(first));
                Assert.That(unit.PendingCommands, Is.EqualTo(new UnitCommand[] { second }));
            }
        }

        [Test]
        public void Stop_ClearsEveryUnit()
        {
            var units = new[] { CreateUnit(), CreateUnit() };
            GroupOrders.Issue(units, new AttackCommand(target), IssueMode.Replace);

            Assert.That(GroupOrders.Issue(units, new StopCommand(), IssueMode.Replace), Is.EqualTo(2));
            foreach (var unit in units)
                Assert.That(unit.CurrentCommand, Is.Null);
        }

        [Test]
        public void NullUnits_AreSkipped()
        {
            var first = CreateUnit();
            var second = CreateUnit();
            var attack = new AttackCommand(target);

            Assert.That(GroupOrders.Issue(new[] { first, null, second }, attack, IssueMode.Replace), Is.EqualTo(2));
            Assert.That(first.CurrentCommand, Is.SameAs(attack));
            Assert.That(second.CurrentCommand, Is.SameAs(attack));
        }

        [Test]
        public void EmptyGroup_DoesNothing()
        {
            Assert.That(GroupOrders.Issue(new CommandableUnit[0], new AttackCommand(target), IssueMode.Replace), Is.EqualTo(0));
        }

        [Test]
        public void NullArguments_Throw()
        {
            Assert.Throws<ArgumentNullException>(() => GroupOrders.Issue(null, new StopCommand(), IssueMode.Replace));
            Assert.Throws<ArgumentNullException>(() => GroupOrders.Issue(new[] { CreateUnit() }, null, IssueMode.Replace));
        }
    }
}
