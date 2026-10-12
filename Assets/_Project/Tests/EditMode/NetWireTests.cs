using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class NetWireTests
    {
        const string Attempt = "attempt-1";

        [Test]
        public void EveryRequest_RoundTripsThroughTheMessage()
        {
            uint n = 0;
            foreach (var request in new NetRequest[]
            {
                new NetMoveIntent(3, new Vector3(1f, 0f, -0.5f)),
                new NetMoveTo(2, new Vector3(4f, 0f, 5f), true),
                new NetMoveTo(2, new Vector3(4f, 0f, 5f)),
                new NetAttack(1, 7),
                new NetStop(5),
            })
            {
                n++;
                var envelope = NetWire.ToEnvelope(NetWire.ToMessage(Attempt, n, n + 100, request));

                Assert.That(envelope.AttemptId, Is.EqualTo(Attempt));
                Assert.That(envelope.Sequence, Is.EqualTo(n));
                Assert.That(envelope.CommandId, Is.EqualTo(n + 100));
                Assert.That(envelope.Request, Is.TypeOf(request.GetType()));
                switch (request)
                {
                    case NetMoveIntent intent:
                        var steer = (NetMoveIntent)envelope.Request;
                        Assert.That(steer.UnitId, Is.EqualTo(intent.UnitId));
                        Assert.That(steer.Direction, Is.EqualTo(intent.Direction));
                        break;
                    case NetMoveTo move:
                        var walk = (NetMoveTo)envelope.Request;
                        Assert.That(walk.UnitId, Is.EqualTo(move.UnitId));
                        Assert.That(walk.Destination, Is.EqualTo(move.Destination));
                        Assert.That(walk.Append, Is.EqualTo(move.Append));
                        break;
                    case NetAttack attack:
                        var shoot = (NetAttack)envelope.Request;
                        Assert.That(shoot.UnitId, Is.EqualTo(attack.UnitId));
                        Assert.That(shoot.TargetId, Is.EqualTo(attack.TargetId));
                        break;
                    case NetStop stop:
                        Assert.That(((NetStop)envelope.Request).UnitId, Is.EqualTo(stop.UnitId));
                        break;
                }
            }
        }

        [TestCase((byte)0)]
        [TestCase((byte)99)]
        public void AnUnknownKind_HasNoRequest(byte kind)
        {
            var message = new NetRequestMessage { AttemptId = Attempt, Sequence = 1, CommandId = 1, Kind = kind };
            Assert.That(NetWire.ToEnvelope(message).Request, Is.Null);
        }

        [TestCase(NetWire.MoveIntent)]
        [TestCase(NetWire.Attack)]
        [TestCase(NetWire.Stop)]
        public void AppendOnANonMoveRequest_HasNoRequest(byte kind)
        {
            var message = new NetRequestMessage { AttemptId = Attempt, Sequence = 1, CommandId = 1, Kind = kind, UnitId = 1, Append = true };
            Assert.That(NetWire.ToRequest(message), Is.Null, "only a move-to may append");
        }

        [Test]
        public void ANonFiniteVector_PassesThrough_ForTheGateToRefuse()
        {
            var message = NetWire.ToMessage(Attempt, 1, 1, new NetMoveTo(1, new Vector3(float.NaN, 0f, 0f)));
            var request = NetWire.ToRequest(message);
            Assert.That(request, Is.TypeOf<NetMoveTo>());
            Assert.That(float.IsNaN(((NetMoveTo)request).Destination.x), Is.True);
        }

        [Test]
        public void ANullRequest_BecomesKindZero()
        {
            Assert.That(NetWire.ToMessage(Attempt, 1, 1, null).Kind, Is.EqualTo((byte)0));
        }
    }
}
