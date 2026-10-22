using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>A shot reaches a connection only while that connection receives both units' avatars (decision 052).</summary>
    public class NetShotRelayTests
    {
        const int FriendA = 1;
        const int FriendB = 2;
        const int Hostile = 3;

        sealed class Intel : INetIntelSource
        {
            public readonly HashSet<(NetConnection, int)> Seen = new HashSet<(NetConnection, int)>();
            public bool Observes(NetConnection connection, int hostileId) => Seen.Contains((connection, hostileId));
        }

        readonly List<GameObject> hosts = new List<GameObject>();
        readonly List<(NetConnection, NetShot)> sent = new List<(NetConnection, NetShot)>();
        Intel intel;
        NetVisibility visibility;
        NetConnection a;
        NetConnection b;
        NetShotRelay relay;

        [SetUp]
        public void SetUp()
        {
            var units = new NetUnits();
            units.Register(NetTestKit.MakeUnit(hosts, "FriendlyUnit_1"), false);
            units.Register(NetTestKit.MakeUnit(hosts, "FriendlyUnit_2"), false);
            units.Register(NetTestKit.MakeUnit(hosts, "HostileUnit_1"), true);
            intel = new Intel();
            visibility = new NetVisibility(units, intel);
            a = new NetConnection("account-a", new[] { FriendA, FriendB });
            b = new NetConnection("account-b", new int[0]);
            visibility.Add(a);
            visibility.Add(b);
            relay = new NetShotRelay();
            sent.Clear();
        }

        [TearDown]
        public void TearDown() => NetTestKit.DestroyAll(hosts);

        void Flush()
        {
            visibility.Update(new List<NetKnowledgeChange>());
            relay.Flush(new[] { a, b }, visibility.Replicates, (c, s) => sent.Add((c, s)));
        }

        [Test]
        public void AShotFromAnUnobservedHostile_IsSentToNobody()
        {
            relay.Add(new NetShot(Hostile, FriendA, true));
            Flush();
            Assert.That(sent, Is.Empty);
            Assert.That(relay.Pending, Is.Zero, "a shot never waits for a later frame");
        }

        [Test]
        public void AShotBetweenAnObservedHostileAndAnOwnUnit_IsSentToTheOwnerOnly()
        {
            intel.Seen.Add((a, Hostile));
            relay.Add(new NetShot(Hostile, FriendA, false));
            relay.Add(new NetShot(FriendB, Hostile, true));
            Flush();
            Assert.That(sent.Select(s => s.Item1), Is.All.SameAs(a));
            Assert.That(sent.Select(s => (s.Item2.AttackerId, s.Item2.TargetId, s.Item2.Hit)), Is.EqualTo(new[] { (Hostile, FriendA, false), (FriendB, Hostile, true) }));
        }

        [Test]
        public void ASendThatThrows_StillEmptiesTheQueue()
        {
            intel.Seen.Add((a, Hostile));
            relay.Add(new NetShot(Hostile, FriendA, true));
            visibility.Update(new List<NetKnowledgeChange>());
            Assert.Throws<System.InvalidOperationException>(() => relay.Flush(new[] { a }, visibility.Replicates, (c, s) => throw new System.InvalidOperationException()));
            Assert.That(relay.Pending, Is.Zero);
        }

        [Test]
        public void MoreThanTheCapInOneFrame_AreDroppedAndCounted()
        {
            for (var i = 0; i < NetShotRelay.MaxPending + 5; i++)
                relay.Add(new NetShot(FriendA, Hostile, true));
            Assert.That(relay.Pending, Is.EqualTo(NetShotRelay.MaxPending));
            Assert.That(relay.Dropped, Is.EqualTo(5));
        }
    }
}
