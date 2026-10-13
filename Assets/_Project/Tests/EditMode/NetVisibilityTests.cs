using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class NetVisibilityTests
    {
        const int FriendA = 1;
        const int FriendB = 2;
        const int Hostile = 3;

        sealed class FakeIntel : INetIntelSource
        {
            public readonly HashSet<(NetConnection, int)> Seen = new HashSet<(NetConnection, int)>();
            public readonly List<int> Asked = new List<int>();

            public bool Observes(NetConnection connection, int hostileId)
            {
                Asked.Add(hostileId);
                return Seen.Contains((connection, hostileId));
            }
        }

        readonly List<GameObject> hosts = new List<GameObject>();
        readonly List<NetKnowledgeChange> changes = new List<NetKnowledgeChange>();
        CommandableUnit hostile;
        FakeIntel intel;
        NetVisibility visibility;
        NetConnection a;
        NetConnection b;

        [SetUp]
        public void SetUp()
        {
            var units = new NetUnits();
            units.Register(NetTestKit.MakeUnit(hosts, "FriendlyUnit_1"), false);
            units.Register(NetTestKit.MakeUnit(hosts, "FriendlyUnit_2"), false);
            hostile = NetTestKit.MakeUnit(hosts, "HostileUnit_1");
            units.Register(hostile, true);
            hostile.transform.position = new Vector3(10f, 0f, 0f);
            intel = new FakeIntel();
            visibility = new NetVisibility(units, intel);
            a = new NetConnection("account-a", new[] { FriendA, FriendB });
            b = new NetConnection("account-b", new int[0]);
            visibility.Add(a);
            visibility.Add(b);
            changes.Clear();
        }

        [TearDown]
        public void TearDown() => NetTestKit.DestroyAll(hosts);

        void Tick()
        {
            changes.Clear();
            visibility.Update(changes);
        }

        void See(NetConnection connection) => intel.Seen.Add((connection, Hostile));

        void Lose(NetConnection connection) => intel.Seen.Remove((connection, Hostile));

        void Kill() => hostile.GetComponent<Health>().TakeDamage(100000);

        [Test]
        public void AHostile_StartsUnknown_ForEveryConnection()
        {
            Tick();
            Assert.That(changes, Is.Empty);
            Assert.That(visibility.KnowledgeOf(a, Hostile), Is.EqualTo(NetKnowledge.Unknown));
            Assert.That(visibility.KnowledgeOf(b, Hostile), Is.EqualTo(NetKnowledge.Unknown));
            Assert.That(visibility.TryLastKnown(a, Hostile, out _), Is.False);
        }

        [Test]
        public void Observing_GivesOneObservedChange_ToThatConnectionOnly()
        {
            See(a);
            Tick();
            Assert.That(changes, Has.Count.EqualTo(1));
            Assert.That(changes[0].Connection, Is.SameAs(a));
            Assert.That(changes[0].UnitId, Is.EqualTo(Hostile));
            Assert.That(changes[0].Knowledge, Is.EqualTo(NetKnowledge.Observed));
            Assert.That(visibility.KnowledgeOf(a, Hostile), Is.EqualTo(NetKnowledge.Observed));
            Assert.That(visibility.KnowledgeOf(b, Hostile), Is.EqualTo(NetKnowledge.Unknown), "A's observation never widens B's view");
        }

        [Test]
        public void UpdatingAnUnchangedWorld_ReportsNothing()
        {
            See(a);
            Tick();
            Tick();
            Assert.That(changes, Is.Empty, "a transition is reported once");
            Lose(a);
            Tick();
            Tick();
            Assert.That(changes, Is.Empty);
        }

        [Test]
        public void LosingSight_GivesLastKnown_AtTheLastObservedPosition()
        {
            See(a);
            Tick();
            hostile.transform.position = new Vector3(12f, 0f, 3f);
            Tick();
            Assert.That(changes, Is.Empty, "movement while observed is not a transition");
            Lose(a);
            Tick();
            Assert.That(changes, Has.Count.EqualTo(1));
            Assert.That(changes[0].Knowledge, Is.EqualTo(NetKnowledge.LastKnown));
            Assert.That(changes[0].Position, Is.EqualTo(new Vector3(12f, 0f, 3f)));
            Assert.That(visibility.TryLastKnown(a, Hostile, out var at), Is.True);
            Assert.That(at, Is.EqualTo(new Vector3(12f, 0f, 3f)));
        }

        [Test]
        public void HiddenMovement_NeverMovesTheLastKnownRecord()
        {
            See(a);
            Tick();
            Lose(a);
            Tick();
            hostile.transform.position = new Vector3(-20f, 0f, 5f);
            Tick();
            Assert.That(changes, Is.Empty);
            Assert.That(visibility.TryLastKnown(a, Hostile, out var at), Is.True);
            Assert.That(at, Is.EqualTo(new Vector3(10f, 0f, 0f)));
        }

        [Test]
        public void ReObserving_ClearsTheRecord_AndReportsTheCurrentPosition()
        {
            See(a);
            Tick();
            Lose(a);
            Tick();
            hostile.transform.position = new Vector3(-20f, 0f, 5f);
            See(a);
            Tick();
            Assert.That(changes, Has.Count.EqualTo(1));
            Assert.That(changes[0].Knowledge, Is.EqualTo(NetKnowledge.Observed));
            Assert.That(changes[0].Position, Is.EqualTo(new Vector3(-20f, 0f, 5f)));
            Assert.That(visibility.TryLastKnown(a, Hostile, out _), Is.False, "the stale record is gone");
        }

        [Test]
        public void AHostileThatDiesWhileObserved_StaysObserved()
        {
            See(a);
            Tick();
            Kill();
            Lose(a);
            Tick();
            Assert.That(changes, Is.Empty);
            Assert.That(visibility.KnowledgeOf(a, Hostile), Is.EqualTo(NetKnowledge.Observed), "the death was witnessed");
            Assert.That(visibility.Replicates(a, Hostile), Is.True);
        }

        [Test]
        public void AHostileThatDiesWhileHidden_KeepsItsRecord_AndNothingNew()
        {
            See(a);
            Tick();
            Lose(a);
            Tick();
            Kill();
            See(a);
            See(b);
            Tick();
            Assert.That(changes, Is.Empty, "a dead hostile is never revealed");
            Assert.That(visibility.KnowledgeOf(a, Hostile), Is.EqualTo(NetKnowledge.LastKnown));
            Assert.That(visibility.KnowledgeOf(b, Hostile), Is.EqualTo(NetKnowledge.Unknown));
        }

        [Test]
        public void Replicates_FriendliesOnlyToTheirOwner_AndHostilesOnlyWhileObserved()
        {
            Tick();
            Assert.That(visibility.Replicates(a, FriendA), Is.True);
            Assert.That(visibility.Replicates(a, FriendB), Is.True);
            Assert.That(visibility.Replicates(b, FriendA), Is.False, "B owns nothing and sees nothing");
            Assert.That(visibility.Replicates(a, Hostile), Is.False);
            See(a);
            Tick();
            Assert.That(visibility.Replicates(a, Hostile), Is.True);
            Assert.That(visibility.Replicates(b, Hostile), Is.False);
            Lose(a);
            Tick();
            Assert.That(visibility.Replicates(a, Hostile), Is.False, "a last-known hostile sends nothing live");
            Assert.That(visibility.Replicates(a, 99), Is.False);
            Assert.That(visibility.Replicates(a, 0), Is.False);
        }

        [Test]
        public void CanSee_FriendliesAlways_HostilesOnlyWhileObserved()
        {
            Tick();
            Assert.That(visibility.CanSee(b, FriendA), Is.True, "friendlies are not hidden information");
            Assert.That(visibility.CanSee(a, Hostile), Is.False);
            See(a);
            Tick();
            Assert.That(visibility.CanSee(a, Hostile), Is.True);
            Assert.That(visibility.CanSee(b, Hostile), Is.False);
        }

        [Test]
        public void AConnectionNeverAdded_ReplicatesNothing_AndKnowsNothing()
        {
            var stranger = new NetConnection("account-x", new[] { FriendA });
            intel.Seen.Add((stranger, Hostile));
            Tick();
            Assert.That(changes.Any(c => c.Connection == stranger), Is.False);
            Assert.That(visibility.Replicates(stranger, FriendA), Is.False, "nothing before hello");
            Assert.That(visibility.KnowledgeOf(stranger, Hostile), Is.EqualTo(NetKnowledge.Unknown));
            Assert.That(visibility.Replicates(null, FriendA), Is.False);
        }

        [Test]
        public void ARemovedConnection_IsForgotten_AndTheOtherIsUnchanged()
        {
            See(a);
            See(b);
            Tick();
            visibility.Remove(a);
            Lose(a);
            Tick();
            Assert.That(changes, Is.Empty);
            Assert.That(visibility.ConnectionCount, Is.EqualTo(1));
            Assert.That(visibility.KnowledgeOf(a, Hostile), Is.EqualTo(NetKnowledge.Unknown));
            Assert.That(visibility.KnowledgeOf(b, Hostile), Is.EqualTo(NetKnowledge.Observed));
            visibility.Add(a);
            Tick();
            Assert.That(visibility.KnowledgeOf(a, Hostile), Is.EqualTo(NetKnowledge.Unknown), "a returning connection starts from nothing");
        }

        [Test]
        public void AFlappingSource_GivesAtMostOneChangePerConnectionAndHostilePerUpdate()
        {
            for (var i = 0; i < 20; i++)
            {
                if (i % 2 == 0)
                {
                    See(a);
                    See(b);
                }
                else
                {
                    Lose(a);
                    Lose(b);
                }
                Tick();
                Assert.That(changes.Count, Is.LessThanOrEqualTo(2));
            }
        }

        [Test]
        public void TheSourceIsAskedOnlyAboutLivingHostiles()
        {
            Tick();
            Assert.That(intel.Asked.Distinct(), Is.EquivalentTo(new[] { Hostile }));
            intel.Asked.Clear();
            Kill();
            Tick();
            Assert.That(intel.Asked, Is.Empty);
        }

        [Test]
        public void TheEverythingSight_SeesEveryUnit()
        {
            Assert.That(NetSight.Everything.CanSee(b, Hostile), Is.True);
            Assert.That(NetSight.Everything.CanSee(null, 99), Is.True);
        }
    }
}
