using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>The projection survives the wire unchanged, and every malformed payload decodes to nothing with a reason.</summary>
    public class NetMapWireTests
    {
        static NetMap FixtureMap()
        {
            Assert.That(MissionGenerator.TryAttempt(ServerFixture.Settings().Validated(), 1, out var layout, out var reason), Is.True, reason);
            return NetMapProjection.Map(layout, new[] { layout.FriendlySpawns[0] }, ServerFixture.Settings().intelligence, false, false, out _);
        }

        [Test]
        public void TheFixtureMap_RoundTripsExactly()
        {
            var map = FixtureMap();
            var back = NetMapWire.FromMessage(NetMapWire.ToMessage(map), out var problem);
            Assert.That(problem, Is.Null);
            Assert.That((back.Width, back.Height), Is.EqualTo((map.Width, map.Height)));
            Assert.That(back.Floors, Is.EqualTo(map.Floors));
            Assert.That(back.Boxes.Select(b => (b.Kind, b.Footprint, b.Rise)), Is.EqualTo(map.Boxes.Select(b => (b.Kind, b.Footprint, b.Rise))));
            Assert.That(back.Containers, Is.EqualTo(map.Containers));
        }

        [Test]
        public void AnotherFormat_DecodesToNothing()
        {
            var message = NetMapWire.ToMessage(FixtureMap());
            message.Format = 2;
            Assert.That(NetMapWire.FromMessage(message, out var problem), Is.Null);
            StringAssert.Contains("format", problem);
        }

        [Test]
        public void MissingArrays_DecodeToNothing()
        {
            var message = NetMapWire.ToMessage(FixtureMap());
            message.Boxes = null;
            Assert.That(NetMapWire.FromMessage(message, out var problem), Is.Null);
            StringAssert.Contains("missing", problem);
        }

        [Test]
        public void OversizedArrays_AreRefusedBeforeDecoding()
        {
            var message = NetMapWire.ToMessage(FixtureMap());
            message.Floors = new NetRectWire[NetMap.MaxFloors + 1];
            Assert.That(NetMapWire.FromMessage(message, out var problem), Is.Null);
            StringAssert.Contains("too large", problem);
        }

        [TestCase((byte)0)] [TestCase((byte)6)]
        public void AnUnknownBoxKindByte_DecodesToNothing(byte kind)
        {
            var message = NetMapWire.ToMessage(FixtureMap());
            message.Boxes[0].Kind = kind;
            Assert.That(NetMapWire.FromMessage(message, out var problem), Is.Null);
            StringAssert.Contains("kind", problem);
        }

        [Test]
        public void AFloorOutsideTheGrid_DecodesToNothing()
        {
            var message = NetMapWire.ToMessage(FixtureMap());
            message.Floors[0].X = 500;
            Assert.That(NetMapWire.FromMessage(message, out var problem), Is.Null);
            StringAssert.Contains("outside", problem);
        }

        [Test]
        public void Objectives_RoundTrip_AndBadBytesDecodeToNothing()
        {
            var list = new List<NetObjective>
            {
                new NetObjective(NetObjectiveKind.Eliminate, ObjectiveState.Active, "Eliminate security team", false, Vector3.zero, 0f),
                new NetObjective(NetObjectiveKind.Extraction, ObjectiveState.Inactive, "Extraction", true, new Vector3(-9.5f, 0f, 4.5f), 2f),
            };
            var message = NetMapWire.ToMessage(list);
            var back = NetMapWire.FromMessage(message, out var problem);
            Assert.That(problem, Is.Null);
            Assert.That(back.Select(o => (o.Kind, o.State, o.Title, o.HasPoint, o.Point, o.Radius)), Is.EqualTo(list.Select(o => (o.Kind, o.State, o.Title, o.HasPoint, o.Point, o.Radius))));
            message.Objectives[0].State = 9;
            Assert.That(NetMapWire.FromMessage(message, out problem), Is.Null);
            StringAssert.Contains("state", problem);
            message.Objectives = null;
            Assert.That(NetMapWire.FromMessage(message, out problem), Is.Null);
            StringAssert.Contains("missing", problem);
        }

        // Owner ruling: every briefing payload is versioned; another format decodes to nothing.
        [Test]
        public void AnotherObjectivesFormat_DecodesToNothing()
        {
            var message = NetMapWire.ToMessage(new List<NetObjective>
            {
                new NetObjective(NetObjectiveKind.Eliminate, ObjectiveState.Active, "Eliminate security team", false, Vector3.zero, 0f),
            });
            Assert.That(message.Format, Is.EqualTo(NetMap.Format));
            message.Format = 2;
            Assert.That(NetMapWire.FromMessage(message, out var problem), Is.Null);
            StringAssert.Contains("format", problem);
        }

        [Test]
        public void TheMissionEnd_RoundTrips_AndAnotherFormatOrAnUnknownResultIsNone()
        {
            var message = NetMapWire.ToMessage(NetMissionResult.Success);
            Assert.That(message.Format, Is.EqualTo(NetMap.Format));
            Assert.That(NetMapWire.FromMessage(message), Is.EqualTo(NetMissionResult.Success));
            Assert.That(NetMapWire.FromMessage(NetMapWire.ToMessage(NetMissionResult.Failure)), Is.EqualTo(NetMissionResult.Failure));
            message.Format = 2;
            Assert.That(NetMapWire.FromMessage(message), Is.EqualTo(NetMissionResult.None));
            Assert.That(NetMapWire.FromMessage(new NetMissionEndMessage { Format = NetMap.Format, Result = 7 }), Is.EqualTo(NetMissionResult.None));
            Assert.That(NetMapWire.FromMessage(new NetMissionEndMessage { Format = NetMap.Format, Result = 0 }), Is.EqualTo(NetMissionResult.None));
        }

        [Test]
        public void AShot_RoundTrips_AndAnotherFormatOrAnUnknownUnitIsRefused()
        {
            var message = NetMapWire.ToMessage(new NetShot(3, 1, true));
            Assert.That(message.Format, Is.EqualTo(NetMap.Format));
            Assert.That(NetMapWire.FromMessage(message, out var shot), Is.True);
            Assert.That((shot.AttackerId, shot.TargetId, shot.Hit), Is.EqualTo((3, 1, true)));
            message.Format = 2;
            Assert.That(NetMapWire.FromMessage(message, out _), Is.False);
            Assert.That(NetMapWire.FromMessage(new NetShotMessage { Format = NetMap.Format, AttackerId = 0, TargetId = 1 }, out _), Is.False);
            Assert.That(NetMapWire.FromMessage(new NetShotMessage { Format = NetMap.Format, AttackerId = 3, TargetId = -1 }, out _), Is.False);
        }
    }
}
