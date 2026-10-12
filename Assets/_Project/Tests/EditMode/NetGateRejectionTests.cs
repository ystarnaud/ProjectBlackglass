using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class NetGateRejectionTests
    {
        const string Attempt = "attempt-1";

        sealed class RogueRequest : NetRequest { }

        readonly List<GameObject> hosts = new List<GameObject>();
        CommandableUnit friendA;
        CommandableUnit friendB;
        CommandableUnit hostile;
        int idA;
        int idB;
        int idHostile;
        ServerCommandGate gate;
        NetConnection owner;
        NetConnection observer;
        uint counter;

        [SetUp]
        public void SetUp()
        {
            friendA = NetTestKit.MakeUnit(hosts, "FriendlyUnit_1");
            friendB = NetTestKit.MakeUnit(hosts, "FriendlyUnit_2");
            hostile = NetTestKit.MakeUnit(hosts, "HostileUnit_1");
            var units = new NetUnits();
            idA = units.Register(friendA, false);
            idB = units.Register(friendB, false);
            idHostile = units.Register(hostile, true);
            gate = new ServerCommandGate(units, Attempt);
            owner = new NetConnection("account-a", new[] { idA, idB });
            observer = new NetConnection("account-b", new int[0]);
            counter = 0;
        }

        [TearDown]
        public void TearDown() => NetTestKit.DestroyAll(hosts);

        NetResult Send(NetConnection connection, NetRequest request, uint? sequence = null, uint? commandId = null, string attempt = Attempt)
        {
            counter++;
            var seq = sequence ?? counter;
            return gate.Handle(connection, new NetEnvelope(attempt, seq, commandId ?? seq, request), 0d);
        }

        static NetMoveIntent Steer(int unitId) => new NetMoveIntent(unitId, Vector3.right);

        static string State(CommandableUnit unit) =>
            $"{unit.CurrentCommand}|{unit.PendingCommands.Count}|{unit.MoveIntent}|{unit.transform.position}";

        [Test]
        public void AMoveForAHostile_IsNotOwner()
        {
            var result = Send(owner, new NetMoveTo(idHostile, new Vector3(1f, 0f, 1f)));
            Assert.That(result.Reason, Is.EqualTo(NetRejectReason.NotOwner));
            Assert.That(hostile.CurrentCommand, Is.Null);
        }

        [Test]
        public void AnAttackOnAFriendly_IsWrongSide()
        {
            var result = Send(owner, new NetAttack(idA, idB));
            Assert.That(result.Reason, Is.EqualTo(NetRejectReason.WrongSide));
            Assert.That(friendA.CurrentCommand, Is.Null);
        }

        [Test]
        public void ADuplicateCommandId_IsRejected()
        {
            Assert.That(Send(owner, Steer(idA), 1, 7).IsAccepted, Is.True);
            Assert.That(Send(owner, Steer(idA), 2, 7).Reason, Is.EqualTo(NetRejectReason.DuplicateCommand));
        }

        [Test]
        public void AnOlderOrRepeatedSequence_IsStale()
        {
            Assert.That(Send(owner, Steer(idA), 5, 1).IsAccepted, Is.True);
            Assert.That(Send(owner, Steer(idA), 5, 2).Reason, Is.EqualTo(NetRejectReason.StaleSequence));
            Assert.That(Send(owner, Steer(idA), 4, 3).Reason, Is.EqualTo(NetRejectReason.StaleSequence));
            Assert.That(Send(owner, Steer(idA), 6, 4).IsAccepted, Is.True);
        }

        [Test]
        public void AWrongAttemptId_IsRejected_WithoutConsumingTheSequenceOrCommandId()
        {
            Assert.That(Send(owner, Steer(idA), 1, 1, "another-attempt").Reason, Is.EqualTo(NetRejectReason.WrongAttempt));
            Assert.That(Send(owner, Steer(idA), 1, 1).IsAccepted, Is.True);
        }

        [Test]
        public void MalformedRequests_AreRejected()
        {
            var cases = new List<KeyValuePair<string, NetResult>>
            {
                new KeyValuePair<string, NetResult>("no envelope", gate.Handle(owner, null, 0d)),
                new KeyValuePair<string, NetResult>("no connection", gate.Handle(null, new NetEnvelope(Attempt, 1, 1, Steer(idA)), 0d)),
                new KeyValuePair<string, NetResult>("no request", gate.Handle(owner, new NetEnvelope(Attempt, 1, 1, null), 0d)),
                new KeyValuePair<string, NetResult>("empty attempt", gate.Handle(owner, new NetEnvelope("", 1, 1, Steer(idA)), 0d)),
                new KeyValuePair<string, NetResult>("NaN direction", gate.Handle(owner, new NetEnvelope(Attempt, 1, 1, new NetMoveIntent(idA, new Vector3(float.NaN, 0f, 0f))), 0d)),
                new KeyValuePair<string, NetResult>("infinite destination", gate.Handle(owner, new NetEnvelope(Attempt, 1, 1, new NetMoveTo(idA, new Vector3(0f, float.PositiveInfinity, 0f))), 0d)),
                new KeyValuePair<string, NetResult>("unknown request type", gate.Handle(owner, new NetEnvelope(Attempt, 1, 1, new RogueRequest()), 0d)),
            };
            foreach (var pair in cases)
                Assert.That(pair.Value.Reason, Is.EqualTo(NetRejectReason.Malformed), pair.Key);
            Assert.That(Send(owner, Steer(idA), 1, 1).IsAccepted, Is.True, "a malformed request consumes nothing");
        }

        [Test]
        public void EveryRejection_ChangesNothing_AndNeverThrows()
        {
            Assert.That(Send(owner, Steer(idA), 1, 1).IsAccepted, Is.True);
            var before = new[] { State(friendA), State(friendB), State(hostile) };

            NetResult[] results = null;
            Assert.DoesNotThrow(() => results = new[]
            {
                Send(owner, Steer(idA), 2, 1),
                Send(owner, Steer(idA), 1, 2),
                Send(owner, Steer(idA), 3, 3, "another-attempt"),
                Send(owner, new NetMoveIntent(idA, new Vector3(float.NaN, 0f, 0f)), 4, 4),
                Send(owner, new NetMoveTo(idHostile, Vector3.one), 5, 5),
                Send(owner, new NetAttack(idA, idB), 6, 6),
                Send(owner, new NetStop(999), 7, 7),
                Send(observer, new NetStop(idA), 1, 1),
            });

            var expected = new[]
            {
                NetRejectReason.DuplicateCommand, NetRejectReason.StaleSequence, NetRejectReason.WrongAttempt, NetRejectReason.Malformed,
                NetRejectReason.NotOwner, NetRejectReason.WrongSide, NetRejectReason.UnknownUnit, NetRejectReason.NotOwner,
            };
            for (var i = 0; i < expected.Length; i++)
                Assert.That(results[i].Reason, Is.EqualTo(expected[i]), "request " + i);
            Assert.That(new[] { State(friendA), State(friendB), State(hostile) }, Is.EqualTo(before));
        }

        [Test]
        public void ANonOwner_IsRefusedForEveryPayload()
        {
            var requests = new NetRequest[] { Steer(idA), new NetMoveTo(idA, Vector3.one), new NetAttack(idA, idHostile), new NetStop(idA) };
            foreach (var request in requests)
                Assert.That(Send(observer, request).Reason, Is.EqualTo(NetRejectReason.NotOwner), request.GetType().Name);
            Assert.That(friendA.MoveIntent, Is.EqualTo(Vector3.zero));
            Assert.That(friendA.CurrentCommand, Is.Null);
        }

        [Test]
        public void UnknownAndDestroyedUnits_AreUnknownUnit()
        {
            foreach (var id in new[] { 0, -1, int.MaxValue, 999 })
                Assert.That(Send(owner, new NetStop(id)).Reason, Is.EqualTo(NetRejectReason.UnknownUnit), "id " + id);
            Assert.That(Send(owner, new NetAttack(idA, 999)).Reason, Is.EqualTo(NetRejectReason.UnknownUnit), "unknown target");

            Object.DestroyImmediate(friendB.gameObject);
            Assert.That(Send(owner, new NetStop(idB)).Reason, Is.EqualTo(NetRejectReason.UnknownUnit), "destroyed unit");
            Object.DestroyImmediate(hostile.gameObject);
            Assert.That(Send(owner, new NetAttack(idA, idHostile)).Reason, Is.EqualTo(NetRejectReason.UnknownUnit), "destroyed target");
        }

        [Test]
        public void AnAuthorisationFailure_ConsumesTheSequenceAndCommandId()
        {
            Assert.That(Send(observer, new NetStop(idA), 1, 1).Reason, Is.EqualTo(NetRejectReason.NotOwner));
            Assert.That(Send(observer, new NetStop(idA), 1, 2).Reason, Is.EqualTo(NetRejectReason.StaleSequence));
            Assert.That(Send(observer, new NetStop(idA), 2, 1).Reason, Is.EqualTo(NetRejectReason.DuplicateCommand));
        }

        [Test]
        public void AFreshnessFailure_ConsumesNothing()
        {
            Assert.That(Send(owner, Steer(idA), 5, 1).IsAccepted, Is.True);
            Assert.That(Send(owner, Steer(idA), 5, 9).Reason, Is.EqualTo(NetRejectReason.StaleSequence));
            Assert.That(Send(owner, Steer(idA), 6, 9).IsAccepted, Is.True, "command id 9 was never consumed");
        }

        [Test]
        public void TheCommandIdWindow_ForgetsTheOldest()
        {
            for (uint i = 1; i <= 257; i++)
                Assert.That(Send(owner, Steer(idA), i, i).IsAccepted, Is.True, "request " + i);
            Assert.That(Send(owner, Steer(idA), 258, 1).IsAccepted, Is.True, "command id 1 has left the 256-entry window");
            Assert.That(Send(owner, Steer(idA), 259, 3).Reason, Is.EqualTo(NetRejectReason.DuplicateCommand), "command id 3 is still inside it");
        }

        [Test]
        public void AGoodMoveIntent_IsAccepted_AndSetsTheClampedIntent()
        {
            var result = Send(owner, new NetMoveIntent(idA, new Vector3(2f, 5f, 0f)));
            Assert.That(result.IsAccepted, Is.True);
            Assert.That((friendA.MoveIntent - Vector3.right).magnitude, Is.LessThan(1e-4f));
        }

        [Test]
        public void AReplayedRequest_HasNoSecondEffect()
        {
            Assert.That(Send(owner, Steer(idA), 1, 1).IsAccepted, Is.True);
            friendA.SetMoveIntent(Vector3.zero);
            var replay = Send(owner, Steer(idA), 1, 1);
            Assert.That(replay.Reason, Is.EqualTo(NetRejectReason.StaleSequence));
            Assert.That(friendA.MoveIntent, Is.EqualTo(Vector3.zero), "the replay did not steer the unit again");

            // The other replay shape: a fresh sequence number carrying a command id that was already used.
            var reused = Send(owner, Steer(idA), 2, 1);
            Assert.That(reused.Reason, Is.EqualTo(NetRejectReason.DuplicateCommand));
            Assert.That(friendA.MoveIntent, Is.EqualTo(Vector3.zero), "the reused command id did not steer the unit again");
        }
    }
}
