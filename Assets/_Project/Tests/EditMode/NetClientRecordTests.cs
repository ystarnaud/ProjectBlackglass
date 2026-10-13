using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class NetClientRecordTests
    {
        NetClientRecord record;

        [SetUp]
        public void SetUp() => record = new NetClientRecord();

        [Test]
        public void ALastKnownMessage_AddsARecord_AndObservedClearsIt()
        {
            record.Knowledge(1d, 3, NetKnowledge.LastKnown, new Vector3(4f, 0f, 2f));
            Assert.That(record.LastKnown[3], Is.EqualTo(new Vector3(4f, 0f, 2f)));
            record.Knowledge(2d, 3, NetKnowledge.Observed, Vector3.zero);
            Assert.That(record.LastKnown.ContainsKey(3), Is.False);
            Assert.That(record.KnowledgeMessages, Is.EqualTo(2));
        }

        [Test]
        public void ALaterLastKnown_ReplacesTheRecord_SoThereIsOnePerUnit()
        {
            record.Knowledge(1d, 3, NetKnowledge.LastKnown, Vector3.one);
            record.Knowledge(2d, 3, NetKnowledge.LastKnown, Vector3.right);
            Assert.That(record.LastKnown, Has.Count.EqualTo(1));
            Assert.That(record.LastKnown[3], Is.EqualTo(Vector3.right));
        }

        [Test]
        public void TheDecodeLog_KeepsTimesAndPositions()
        {
            record.Note(0.5d, "NetWelcomeMessage");
            record.Knowledge(1d, 3, NetKnowledge.LastKnown, Vector3.one);
            Assert.That(record.DecodedMessages, Has.Count.EqualTo(2));
            Assert.That(record.DecodedMessages[0].Type, Is.EqualTo("NetWelcomeMessage"));
            Assert.That(record.DecodedMessages[0].HasPosition, Is.False);
            Assert.That(record.DecodedMessages[1].Type, Is.EqualTo("NetKnowledgeMessage"));
            Assert.That(record.DecodedMessages[1].HasPosition, Is.True);
            Assert.That(record.DecodedMessages[1].Position, Is.EqualTo(Vector3.one));
            Assert.That(record.DecodedMessages[1].Time, Is.EqualTo(1d));
        }

        [Test]
        public void Contents_AreKeptAsReceived()
        {
            Assert.That(record.Contents, Is.Null);
            record.Container(1d, new List<NetItem> { new NetItem("fixture-medkit", 2, "fixture-item-0001") });
            Assert.That(record.Contents, Has.Count.EqualTo(1));
            Assert.That(record.Contents[0].Quantity, Is.EqualTo(2));
            Assert.That(record.ContainerMessages, Is.EqualTo(1));
        }

        [Test]
        public void TheFixtureStep_NeverGoesBack()
        {
            record.Step(1d, 4);
            record.Step(2d, 2);
            Assert.That(record.FixtureStep, Is.EqualTo(4));
        }

        [Test]
        public void Sight_RecordsEachAvatarInstanceOnce()
        {
            Assert.That(record.Sight(1d, 100, 3, true, Vector3.one), Is.True);
            Assert.That(record.Sight(2d, 100, 3, true, Vector3.right), Is.False);
            Assert.That(record.Sight(3d, 101, 3, true, Vector3.right), Is.True, "a respawned avatar is a new instance");
            Assert.That(record.Sightings, Has.Count.EqualTo(2));
            Assert.That(record.Sightings[0].Hostile, Is.True);
        }

        [Test]
        public void TheLogs_AreBounded_AndSayWhenTheyAreFull()
        {
            for (var i = 0; i < NetClientRecord.Limit + 5; i++)
                record.Note(i, "NetResultMessage");
            Assert.That(record.DecodedMessages, Has.Count.EqualTo(NetClientRecord.Limit));
            Assert.That(record.Truncated, Is.True);
        }

        [Test]
        public void Clear_ForgetsEverything()
        {
            record.Knowledge(1d, 3, NetKnowledge.LastKnown, Vector3.one);
            record.Container(1d, new List<NetItem>());
            record.Step(1d, 2);
            record.Sight(1d, 100, 3, true, Vector3.one);
            record.Clear();
            Assert.That(record.LastKnown, Is.Empty);
            Assert.That(record.Contents, Is.Null);
            Assert.That(record.FixtureStep, Is.EqualTo(0));
            Assert.That(record.Sightings, Is.Empty);
            Assert.That(record.DecodedMessages, Is.Empty);
            Assert.That(record.KnowledgeMessages + record.ContainerMessages, Is.EqualTo(0));
            Assert.That(record.Sight(2d, 100, 3, true, Vector3.one), Is.True, "the instance can be seen again after a clear");
        }
    }
}
