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
        GameObject otherTargetHost;
        CommandableUnit unit;
        Health target;
        Health otherTarget;

        [SetUp]
        public void SetUp()
        {
            unitHost = new GameObject("Unit");
            unit = unitHost.AddComponent<CommandableUnit>();
            targetHost = new GameObject("Target");
            target = targetHost.AddComponent<Health>();
            otherTargetHost = new GameObject("OtherTarget");
            otherTarget = otherTargetHost.AddComponent<Health>();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(unitHost);
            Object.DestroyImmediate(targetHost);
            Object.DestroyImmediate(otherTargetHost);
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

        [Test]
        public void Issue_UnknownMode_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => unit.Issue(new AttackCommand(target), (IssueMode)99));
            Assert.That(unit.CurrentCommand, Is.Null);
        }

        [Test]
        public void Append_WhileIdle_BecomesCurrent()
        {
            var attack = new AttackCommand(target);
            Assert.That(unit.Issue(attack, IssueMode.Append), Is.True);
            Assert.That(unit.CurrentCommand, Is.SameAs(attack));
            Assert.That(unit.PendingCommands, Is.Empty);
        }

        [Test]
        public void Append_WhileBusy_QueuesBehindCurrent()
        {
            var first = new AttackCommand(target);
            var second = new AttackCommand(otherTarget);
            unit.Issue(first);
            Assert.That(unit.Issue(second, IssueMode.Append), Is.True);
            Assert.That(unit.CurrentCommand, Is.SameAs(first));
            Assert.That(unit.PendingCommands, Is.EqualTo(new UnitCommand[] { second }));
        }

        [Test]
        public void Append_DeadTarget_IsRejectedAndQueueUnchanged()
        {
            var first = new AttackCommand(target);
            unit.Issue(first);
            otherTarget.TakeDamage(otherTarget.Max);
            Assert.That(unit.Issue(new AttackCommand(otherTarget), IssueMode.Append), Is.False);
            Assert.That(unit.CurrentCommand, Is.SameAs(first));
            Assert.That(unit.PendingCommands, Is.Empty);
        }

        [Test]
        public void Replace_DropsPendingOrders()
        {
            unit.Issue(new AttackCommand(target));
            unit.Issue(new AttackCommand(otherTarget), IssueMode.Append);
            var replacement = new AttackCommand(otherTarget);
            Assert.That(unit.Issue(replacement), Is.True);
            Assert.That(unit.CurrentCommand, Is.SameAs(replacement));
            Assert.That(unit.PendingCommands, Is.Empty);
        }

        [Test]
        public void Replace_AttackOnCurrentTarget_KeepsItAndDropsPending()
        {
            unit.Issue(new AttackCommand(target));
            unit.Issue(new AttackCommand(otherTarget), IssueMode.Append);
            var again = new AttackCommand(target);
            Assert.That(unit.Issue(again), Is.True);
            Assert.That(unit.CurrentCommand, Is.SameAs(again));
            Assert.That(unit.PendingCommands, Is.Empty);
        }

        [Test]
        public void Replace_RejectedCommand_KeepsCurrentAndPending()
        {
            var first = new AttackCommand(target);
            var second = new AttackCommand(otherTarget);
            unit.Issue(first);
            unit.Issue(second, IssueMode.Append);
            var deadHost = new GameObject("Dead");
            var dead = deadHost.AddComponent<Health>();
            dead.TakeDamage(dead.Max);

            Assert.That(unit.Issue(new AttackCommand(dead)), Is.False);
            Assert.That(unit.CurrentCommand, Is.SameAs(first));
            Assert.That(unit.PendingCommands, Is.EqualTo(new UnitCommand[] { second }));
            Object.DestroyImmediate(deadHost);
        }

        [TestCase(IssueMode.Replace)]
        [TestCase(IssueMode.Append)]
        public void Stop_ClearsCurrentAndPending(IssueMode mode)
        {
            unit.Issue(new AttackCommand(target));
            unit.Issue(new AttackCommand(otherTarget), IssueMode.Append);
            Assert.That(unit.Issue(new StopCommand(), mode), Is.True);
            Assert.That(unit.CurrentCommand, Is.Null);
            Assert.That(unit.PendingCommands, Is.Empty);
        }

        [Test]
        public void Stop_WhileIdle_IsAccepted()
        {
            Assert.That(unit.Issue(new StopCommand()), Is.True);
            Assert.That(unit.CurrentCommand, Is.Null);
        }

        [Test]
        public void StartsWithNoMoveIntent()
        {
            Assert.That(unit.MoveIntent, Is.EqualTo(Vector3.zero));
        }

        [Test]
        public void SetMoveIntent_FlattensAndClampsToLengthOne()
        {
            unit.SetMoveIntent(new Vector3(3f, 5f, 4f));
            Assert.That(unit.MoveIntent.x, Is.EqualTo(0.6f).Within(1e-5f));
            Assert.That(unit.MoveIntent.y, Is.EqualTo(0f));
            Assert.That(unit.MoveIntent.z, Is.EqualTo(0.8f).Within(1e-5f));
        }

        [Test]
        public void SetMoveIntent_KeepsShortDirections_AndZeroClearsIt()
        {
            unit.SetMoveIntent(new Vector3(0.3f, 0f, 0.4f));
            Assert.That(unit.MoveIntent.magnitude, Is.EqualTo(0.5f).Within(1e-5f));
            unit.SetMoveIntent(Vector3.zero);
            Assert.That(unit.MoveIntent, Is.EqualTo(Vector3.zero));
        }

        [Test]
        public void IsAlive_WithoutHealth_IsTrue()
        {
            Assert.That(unit.IsAlive, Is.True);
        }

        [Test]
        public void Issue_OnADeadUnit_IsRejectedForEveryCommand()
        {
            var own = unitHost.AddComponent<Health>();
            own.TakeDamage(own.Max);
            Assert.That(unit.IsAlive, Is.False);

            Assert.That(unit.Issue(new AttackCommand(target)), Is.False);
            Assert.That(unit.Issue(new MoveCommand(Vector3.one)), Is.False);
            Assert.That(unit.Issue(new StopCommand()), Is.False);
            Assert.That(unit.Issue(new AttackCommand(target), IssueMode.Append), Is.False);
            Assert.That(unit.CurrentCommand, Is.Null);
            Assert.That(unit.PendingCommands, Is.Empty);
        }
    }
}
