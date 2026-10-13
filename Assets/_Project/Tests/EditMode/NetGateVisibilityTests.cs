using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class NetGateVisibilityTests
    {
        const string Attempt = "attempt-1";

        sealed class HiddenSight : INetSight
        {
            public readonly HashSet<int> Hidden = new HashSet<int>();
            public bool CanSee(NetConnection connection, int unitId) => !Hidden.Contains(unitId);
        }

        readonly List<GameObject> hosts = new List<GameObject>();
        CommandableUnit friendA;
        CommandableUnit hostile;
        HiddenSight sight;
        ServerCommandGate gate;
        NetConnection owner;
        NetConnection observer;
        uint counter;
        const int IdA = 1;
        const int IdB = 2;
        const int IdHostile = 3;

        [SetUp]
        public void SetUp()
        {
            friendA = NetTestKit.MakeUnit(hosts, "FriendlyUnit_1");
            var friendB = NetTestKit.MakeUnit(hosts, "FriendlyUnit_2");
            hostile = NetTestKit.MakeUnit(hosts, "HostileUnit_1");
            var units = NetUnits.FromMission(new List<CommandableUnit> { friendA, friendB }, new List<CommandableUnit> { hostile });
            sight = new HiddenSight();
            sight.Hidden.Add(IdHostile);
            gate = new ServerCommandGate(units, Attempt, sight);
            owner = new NetConnection("account-a", new[] { IdA, IdB });
            observer = new NetConnection("account-b", new int[0]);
            counter = 0;
        }

        [TearDown]
        public void TearDown() => NetTestKit.DestroyAll(hosts);

        NetResult Send(NetConnection connection, NetRequest request)
        {
            counter++;
            return gate.Handle(connection, new NetEnvelope(Attempt, counter, counter, request), 0d);
        }

        [Test]
        public void AGateWithoutASight_CannotBeBuilt()
        {
            Assert.Throws<ArgumentNullException>(() => new ServerCommandGate(new NetUnits(), Attempt, null));
        }

        [Test]
        public void AnAttackOnAHiddenHostile_IsUnknownUnit_LikeAMadeUpId()
        {
            Assert.That(Send(owner, new NetAttack(IdA, IdHostile)).Reason, Is.EqualTo(NetRejectReason.UnknownUnit));
            Assert.That(Send(owner, new NetAttack(IdA, 999)).Reason, Is.EqualTo(NetRejectReason.UnknownUnit));
            Assert.That(friendA.CurrentCommand, Is.Null, "nothing was ordered");
        }

        [Test]
        public void AMoveNamingAHiddenHostile_IsUnknownUnit_LikeAMadeUpId()
        {
            Assert.That(Send(owner, new NetMoveTo(IdHostile, Vector3.one)).Reason, Is.EqualTo(NetRejectReason.UnknownUnit));
            Assert.That(Send(owner, new NetMoveTo(999, Vector3.one)).Reason, Is.EqualTo(NetRejectReason.UnknownUnit));
            Assert.That(Send(owner, new NetStop(IdHostile)).Reason, Is.EqualTo(NetRejectReason.UnknownUnit));
            Assert.That(Send(owner, new NetMoveIntent(IdHostile, Vector3.right)).Reason, Is.EqualTo(NetRejectReason.UnknownUnit));
            Assert.That(hostile.CurrentCommand, Is.Null);
        }

        [Test]
        public void AVisibleHostile_StillAnswersNotOwner()
        {
            sight.Hidden.Clear();
            Assert.That(Send(owner, new NetMoveTo(IdHostile, Vector3.one)).Reason, Is.EqualTo(NetRejectReason.NotOwner));
        }

        [Test]
        public void ANonOwner_NamingAHiddenHostile_GetsUnknownUnit_AndAFriendlyTarget_GetsNotOwner()
        {
            Assert.That(Send(observer, new NetAttack(IdA, IdHostile)).Reason, Is.EqualTo(NetRejectReason.UnknownUnit));
            Assert.That(Send(observer, new NetAttack(IdA, IdB)).Reason, Is.EqualTo(NetRejectReason.NotOwner),
                "friendlies are not hidden information");
        }

        [Test]
        public void ARejectedAttack_IsNeverReportedAsAccepted()
        {
            var reported = 0;
            gate.AttackAccepted = (connection, unit, target, command) => reported++;
            Send(owner, new NetAttack(IdA, IdHostile));
            Send(owner, new NetAttack(IdA, IdB));
            Assert.That(reported, Is.EqualTo(0));
        }
    }
}
