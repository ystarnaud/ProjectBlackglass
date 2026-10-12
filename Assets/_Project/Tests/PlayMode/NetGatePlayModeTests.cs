#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>The gate on a generated mission: real units, a real NavMesh, the real Issue path.</summary>
    public class NetGatePlayModeTests
    {
        MissionRig rig;
        ServerCommandGate gate;
        NetConnection owner;
        CommandableUnit friend;
        CommandableUnit enemy;
        int friendId;
        int enemyId;
        uint counter;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
        }

        IEnumerator Ready()
        {
            rig = new MissionRig();
            var slots = MissionRig.FriendlySlots();
            rig.SetFriendlySlots(new[] { slots[2], slots[0] });
            yield return rig.Generate(12345);
            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready), string.Join("\n", rig.Director.Report.Failures));
            var units = NetUnits.FromMission(rig.Director);
            gate = new ServerCommandGate(units, rig.Director.InstanceId);
            friend = rig.Director.Friendlies[0];
            enemy = rig.Director.Hostiles[0];
            friendId = units.IdOf(friend);
            enemyId = units.IdOf(enemy);
            owner = new NetConnection("account-a", new[] { friendId, units.IdOf(rig.Director.Friendlies[1]) });
            counter = 0;
        }

        NetResult Send(NetRequest request)
        {
            counter++;
            return gate.Handle(owner, new NetEnvelope(rig.Director.InstanceId, counter, counter, request), Time.realtimeSinceStartupAsDouble);
        }

        // Where the hostile stands is a point the generator proved walkable, so a move there is a legal order.
        Vector3 Walkable()
        {
            Assert.That(friend.GetComponent<UnitMover>().TrySnap(enemy.transform.position, out var point), Is.True);
            return point;
        }

        [UnityTest]
        public IEnumerator AMoveTo_IsAccepted_AndRunsThroughIssue()
        {
            yield return Ready();
            var result = Send(new NetMoveTo(friendId, Walkable()));
            Assert.That(result.Reason, Is.EqualTo(NetRejectReason.None));
            Assert.That(friend.CurrentCommand, Is.InstanceOf<MoveCommand>());
        }

        [UnityTest]
        public IEnumerator AnAppendedMoveTo_QueuesBehindTheCurrentOrder()
        {
            yield return Ready();
            Assert.That(Send(new NetMoveTo(friendId, Walkable())).IsAccepted, Is.True);
            Assert.That(Send(new NetMoveTo(friendId, Walkable(), append: true)).IsAccepted, Is.True);
            Assert.That(friend.PendingCommands, Has.Count.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator AnAttackOnAHostile_IsAccepted_ThroughIssue()
        {
            yield return Ready();
            var result = Send(new NetAttack(friendId, enemyId));
            Assert.That(result.Reason, Is.EqualTo(NetRejectReason.None));
            Assert.That(friend.CurrentCommand, Is.InstanceOf<AttackCommand>());
        }

        [UnityTest]
        public IEnumerator AStop_ClearsTheOrders()
        {
            yield return Ready();
            Assert.That(Send(new NetMoveTo(friendId, Walkable())).IsAccepted, Is.True);
            Assert.That(Send(new NetStop(friendId)).IsAccepted, Is.True);
            Assert.That(friend.CurrentCommand, Is.Null);
            Assert.That(friend.PendingCommands, Is.Empty);
        }

        [UnityTest]
        public IEnumerator AnUnreachableMoveTo_IsOrderRefused_AndChangesNothing()
        {
            yield return Ready();
            var commandBefore = friend.CurrentCommand;
            var pendingBefore = friend.PendingCommands.Count;
            var result = Send(new NetMoveTo(friendId, new Vector3(10000f, 0f, 10000f)));
            Assert.That(result.Reason, Is.EqualTo(NetRejectReason.OrderRefused));
            Assert.That(friend.CurrentCommand, Is.SameAs(commandBefore), "the unit's own autonomy may have given it an order; the refused one changed nothing");
            Assert.That(friend.PendingCommands, Has.Count.EqualTo(pendingBefore));
        }

        [UnityTest]
        public IEnumerator AnOrderForADeadUnit_IsUnknownUnit()
        {
            yield return Ready();
            var point = Walkable();
            friend.GetComponent<Health>().TakeDamage(100000);
            var result = Send(new NetMoveTo(friendId, point));
            Assert.That(result.Reason, Is.EqualTo(NetRejectReason.UnknownUnit));
        }
    }
}
#endif
