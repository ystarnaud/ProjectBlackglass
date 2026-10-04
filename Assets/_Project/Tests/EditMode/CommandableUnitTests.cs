using System;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class CommandableUnitTests
    {
        sealed class UnsupportedCommand : UnitCommand { }

        GameObject unitHost;
        GameObject targetHost;
        CommandableUnit unit;
        Health target;

        [SetUp]
        public void SetUp()
        {
            unitHost = new GameObject("Unit");
            unit = unitHost.AddComponent<CommandableUnit>();
            targetHost = new GameObject("Target");
            target = targetHost.AddComponent<Health>();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(unitHost);
            Object.DestroyImmediate(targetHost);
        }

        [Test]
        public void RequiredComponentsAreAdded()
        {
            Assert.That(unitHost.GetComponent<UnitMover>(), Is.Not.Null);
            Assert.That(unitHost.GetComponent<UnitAttacker>(), Is.Not.Null);
            Assert.That(unitHost.GetComponent<UnityEngine.AI.NavMeshAgent>(), Is.Not.Null);
        }

        [Test]
        public void Issue_Null_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => unit.Issue(null));
        }

        [Test]
        public void Issue_UnsupportedCommand_Throws()
        {
            Assert.Throws<ArgumentException>(() => unit.Issue(new UnsupportedCommand()));
            Assert.That(unit.CurrentCommand, Is.Null);
        }

        [Test]
        public void Issue_AttackOnLivingTarget_BecomesCurrentCommand()
        {
            var attack = new AttackCommand(target);
            Assert.That(unit.Issue(attack), Is.True);
            Assert.That(unit.CurrentCommand, Is.SameAs(attack));
        }

        [Test]
        public void Issue_AttackOnDeadTarget_IsRejected()
        {
            var attack = new AttackCommand(target);
            target.TakeDamage(target.Max);
            Assert.That(unit.Issue(attack), Is.False);
            Assert.That(unit.CurrentCommand, Is.Null);
        }

        [Test]
        public void Issue_MoveWithoutNavMesh_IsRejectedAndKeepsCurrentOrder()
        {
            var attack = new AttackCommand(target);
            unit.Issue(attack);
            UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("not on a NavMesh"));
            Assert.That(unit.Issue(new MoveCommand(Vector3.one)), Is.False);
            Assert.That(unit.CurrentCommand, Is.SameAs(attack));
        }

        [Test]
        public void Attacker_HasPrototypeDefaults()
        {
            var attacker = unitHost.GetComponent<UnitAttacker>();
            Assert.That(attacker.Range, Is.EqualTo(2f));
            Assert.That(attacker.Damage, Is.EqualTo(25));
            Assert.That(attacker.Cooldown, Is.EqualTo(1f));
        }

        [Test]
        public void Attacker_IsInRange_UsesHorizontalDistance()
        {
            var attacker = unitHost.GetComponent<UnitAttacker>();
            targetHost.transform.position = new Vector3(0f, 10f, 1.9f);
            Assert.That(attacker.IsInRange(target), Is.True);
            targetHost.transform.position = new Vector3(0f, 0f, 2.1f);
            Assert.That(attacker.IsInRange(target), Is.False);
        }
    }
}
