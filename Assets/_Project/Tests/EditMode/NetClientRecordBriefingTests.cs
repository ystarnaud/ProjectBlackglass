using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>What the client keeps of the briefing: the first valid map for good, the latest valid objectives, the first valid result.</summary>
    public class NetClientRecordBriefingTests
    {
        static NetMap Map(int width) => new NetMap(width, 4, new List<RectInt> { new RectInt(0, 0, 2, 2) }, new List<NetMapBox>(), new List<Vector2Int>());

        [Test]
        public void TheFirstValidMap_StandsForTheWholeAttempt()
        {
            var record = new NetClientRecord();
            record.ReceiveMap(1d, null, "map format 2 is not 1");
            Assert.That(record.Map, Is.Null);
            Assert.That(record.MapProblem, Does.Contain("format"));
            var first = Map(4);
            record.ReceiveMap(2d, first, null);
            record.ReceiveMap(3d, Map(5), null);
            Assert.That(record.Map, Is.SameAs(first));
            Assert.That(record.MapProblem, Is.Null);
            Assert.That(record.MapMessages, Is.EqualTo(3));
        }

        [Test]
        public void AnInvalidObjectiveList_KeepsThePreviousOne()
        {
            var record = new NetClientRecord();
            var list = new List<NetObjective> { new NetObjective(NetObjectiveKind.Eliminate, ObjectiveState.Active, "Eliminate", false, Vector3.zero, 0f) };
            record.ReceiveObjectives(1d, list, null);
            record.ReceiveObjectives(2d, null, "objective 0 has an unknown state");
            Assert.That(record.Objectives.Count, Is.EqualTo(1));
            Assert.That(record.ObjectivesProblem, Does.Contain("state"));
        }

        [Test]
        public void TheFirstValidResult_IsFinal_AndUnknownBytesAreIgnored()
        {
            var record = new NetClientRecord();
            record.ReceiveEnd(1d, 7);
            Assert.That(record.Result, Is.EqualTo(NetMissionResult.None));
            record.ReceiveEnd(2d, (byte)NetMissionResult.Failure);
            record.ReceiveEnd(3d, (byte)NetMissionResult.Success);
            Assert.That(record.Result, Is.EqualTo(NetMissionResult.Failure));
            Assert.That(record.EndMessages, Is.EqualTo(3));
        }

        [Test]
        public void Clear_ForgetsTheBriefing()
        {
            var record = new NetClientRecord();
            record.ReceiveMap(1d, Map(4), null);
            record.ReceiveEnd(2d, (byte)NetMissionResult.Success);
            record.Clear();
            Assert.That(record.Map, Is.Null);
            Assert.That(record.Result, Is.EqualTo(NetMissionResult.None));
            Assert.That(record.Objectives, Is.Empty);
            Assert.That(record.MapMessages, Is.Zero);
        }
    }
}
