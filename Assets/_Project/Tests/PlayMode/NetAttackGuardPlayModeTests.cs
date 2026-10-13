#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>The attack-chase mitigation (spec section 3.5): only gate-accepted attacks end when their connection loses sight.</summary>
    public class NetAttackGuardPlayModeTests
    {
        sealed class Switch : INetIntelSource
        {
            public bool On = true;
            public bool Observes(NetConnection connection, int hostileId) => On;
        }

        readonly List<NetKnowledgeChange> changes = new List<NetKnowledgeChange>();
        TestWorld world;
        CommandableUnit attacker;
        CommandableUnit bystander;
        CommandableUnit target;
        Switch intel;
        NetVisibility visibility;
        NetAttackGuard guard;
        NetConnection owner;

        [TearDown]
        public void TearDown()
        {
            world?.Dispose();
            world = null;
        }

        IEnumerator Ready()
        {
            world = new TestWorld();
            world.CreateEnvironment();
            attacker = world.CreateFighter(new Vector3(-6f, 0f, 0f));
            bystander = world.CreateFighter(new Vector3(-6f, 0f, 3f));
            target = world.CreateFighter(new Vector3(6f, 0f, 0f));
            target.GetComponent<AutoRetaliate>().enabled = false;
            yield return null;
            var units = NetUnits.FromMission(new List<CommandableUnit> { attacker, bystander }, new List<CommandableUnit> { target });
            intel = new Switch();
            visibility = new NetVisibility(units, intel);
            guard = new NetAttackGuard(units, visibility);
            owner = new NetConnection("account-a", new[] { 1, 2 });
            visibility.Add(owner);
            Tick();
        }

        void Tick()
        {
            changes.Clear();
            visibility.Update(changes);
        }

        AttackCommand Attack(CommandableUnit unit)
        {
            var command = new AttackCommand(target.GetComponent<Health>());
            Assert.That(unit.Issue(command, IssueMode.Replace), Is.True, "Precondition: the attack is accepted");
            return command;
        }

        [UnityTest]
        public IEnumerator DropCurrent_EndsTheGivenAttack_AndTheQueuedOrderStarts()
        {
            yield return Ready();
            var attack = Attack(attacker);
            var move = new MoveCommand(new Vector3(-6f, 0f, -3f));
            Assert.That(attacker.Issue(move, IssueMode.Append), Is.True);

            Assert.That(attacker.DropCurrent(attack), Is.True);
            Assert.That(attacker.CurrentCommand, Is.SameAs(move), "orders queued behind the attack carry on");
        }

        [UnityTest]
        public IEnumerator DropCurrent_OfAnotherCommand_OrNull_ChangesNothing()
        {
            yield return Ready();
            var attack = Attack(attacker);
            Assert.That(attacker.DropCurrent(new AttackCommand(target.GetComponent<Health>())), Is.False);
            Assert.That(attacker.DropCurrent(null), Is.False);
            Assert.That(attacker.CurrentCommand, Is.SameAs(attack));
        }

        [UnityTest]
        public IEnumerator LosingSight_EndsAGuardedAttack()
        {
            yield return Ready();
            var attack = Attack(attacker);
            guard.Accepted(owner, 1, 3, attack);
            Tick();
            Assert.That(guard.Tick(), Is.EqualTo(0), "still observed: the attack goes on");
            Assert.That(attacker.CurrentCommand, Is.SameAs(attack));

            intel.On = false;
            Tick();
            Assert.That(guard.Tick(), Is.EqualTo(1));
            Assert.That(attacker.CurrentCommand, Is.Null);
            Assert.That(guard.Count, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator AnAttackTheGuardNeverSaw_KeepsChasing()
        {
            yield return Ready();
            var guarded = Attack(attacker);
            guard.Accepted(owner, 1, 3, guarded);
            var unguarded = Attack(bystander);   // as enemy AI, companion assist, retaliation or an ability would issue it

            intel.On = false;
            Tick();
            guard.Tick();
            Assert.That(attacker.CurrentCommand, Is.Null);
            Assert.That(bystander.CurrentCommand, Is.SameAs(unguarded), "only gate-accepted attacks are guarded");
        }

        [UnityTest]
        public IEnumerator AReplacedOrder_IsForgotten_AndNeverDropped()
        {
            yield return Ready();
            var attack = Attack(attacker);
            guard.Accepted(owner, 1, 3, attack);
            var move = new MoveCommand(new Vector3(-6f, 0f, -3f));
            Assert.That(attacker.Issue(move, IssueMode.Replace), Is.True);

            intel.On = false;
            Tick();
            Assert.That(guard.Tick(), Is.EqualTo(0));
            Assert.That(guard.Count, Is.EqualTo(0));
            Assert.That(attacker.CurrentCommand, Is.SameAs(move));
        }

        [UnityTest]
        public IEnumerator Forget_EndsThatConnectionsGuardedAttacks()
        {
            yield return Ready();
            var attack = Attack(attacker);
            var move = new MoveCommand(new Vector3(-6f, 0f, -3f));
            Assert.That(attacker.Issue(move, IssueMode.Append), Is.True);
            guard.Accepted(owner, 1, 3, attack);

            guard.Forget(owner);
            Assert.That(guard.Count, Is.EqualTo(0));
            Assert.That(attacker.CurrentCommand, Is.SameAs(move), "the departed connection's chase ends; the queued order carries on");
        }

        [UnityTest]
        public IEnumerator Forget_DropsOnlyThatConnectionsRecords()
        {
            yield return Ready();
            var other = new NetConnection("account-c", new[] { 2 });
            visibility.Add(other);
            Tick();
            var first = Attack(attacker);
            guard.Accepted(owner, 1, 3, first);
            var second = Attack(bystander);
            guard.Accepted(other, 2, 3, second);

            guard.Forget(owner);
            Assert.That(guard.Count, Is.EqualTo(1));
            Tick();
            guard.Tick();
            Assert.That(attacker.CurrentCommand, Is.Null);
            Assert.That(bystander.CurrentCommand, Is.SameAs(second), "the other connection still observes: its attack continues");
        }

        [UnityTest]
        public IEnumerator AUnitHasAtMostOneRecord()
        {
            yield return Ready();
            guard.Accepted(owner, 1, 3, Attack(attacker));
            guard.Accepted(owner, 1, 3, Attack(attacker));
            Assert.That(guard.Count, Is.EqualTo(1));
        }
    }
}
#endif
