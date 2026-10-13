using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class NetFixtureTests
    {
        const int Hostile = 3;
        static readonly Vector3 P1 = new Vector3(4f, 0f, 0f);
        static readonly Vector3 P2 = new Vector3(0f, 0f, 4f);

        readonly List<GameObject> hosts = new List<GameObject>();
        readonly List<Vector3> moves = new List<Vector3>();
        CommandableUnit friend1;
        CommandableUnit friend2;
        NetUnits units;
        NetFixture fixture;
        NetConnection a;
        NetConnection b;

        [SetUp]
        public void SetUp()
        {
            friend1 = NetTestKit.MakeUnit(hosts, "FriendlyUnit_1");
            friend2 = NetTestKit.MakeUnit(hosts, "FriendlyUnit_2");
            units = NetUnits.FromMission(new List<CommandableUnit> { friend1, friend2 },
                new List<CommandableUnit> { NetTestKit.MakeUnit(hosts, "HostileUnit_1") });
            moves.Clear();
            fixture = new NetFixture(units, Hostile, new[] { P1, P2 }, moves.Add);
            a = new NetConnection("account-a", new[] { 1, 2 }, drivesFixture: true);
            b = new NetConnection("account-b", new int[0]);
        }

        [TearDown]
        public void TearDown() => NetTestKit.DestroyAll(hosts);

        [Test]
        public void Observe_GrantsOnlyTheSendingConnection()
        {
            Assert.That(fixture.Apply(a, NetFixtureVerb.Observe), Is.True);
            Assert.That(fixture.Observes(a, Hostile), Is.True);
            Assert.That(fixture.Observes(b, Hostile), Is.False, "A's grant never reveals the hostile to B");
            Assert.That(fixture.Step, Is.EqualTo(1));
        }

        [Test]
        public void Unobserve_RevokesOnlyThatConnection()
        {
            fixture.Apply(a, NetFixtureVerb.Observe);
            fixture.Apply(b, NetFixtureVerb.Observe);
            fixture.Apply(a, NetFixtureVerb.Unobserve);
            Assert.That(fixture.Observes(a, Hostile), Is.False);
            Assert.That(fixture.Observes(b, Hostile), Is.True);
        }

        [Test]
        public void Fault_MakesObservesThrow_ForThatConnectionOnly()
        {
            fixture.Apply(a, NetFixtureVerb.Observe);
            fixture.Apply(b, NetFixtureVerb.Observe);
            Assert.That(fixture.Apply(a, NetFixtureVerb.Fault), Is.True);
            Assert.That(fixture.Step, Is.EqualTo(3));
            var thrown = Assert.Throws<System.InvalidOperationException>(() => fixture.Observes(a, Hostile));
            Assert.That(thrown.Message, Is.EqualTo("fixture fault"));
            Assert.That(fixture.Observes(b, Hostile), Is.True, "B is unaffected");
        }

        [Test]
        public void Forget_ClearsTheFault_AndNoOtherVerbDoes()
        {
            fixture.Apply(a, NetFixtureVerb.Fault);
            fixture.Apply(a, NetFixtureVerb.Observe);
            fixture.Apply(a, NetFixtureVerb.Unobserve);
            fixture.Apply(a, NetFixtureVerb.MoveHostile);
            fixture.Apply(a, NetFixtureVerb.Search);
            Assert.Throws<System.InvalidOperationException>(() => fixture.Observes(a, Hostile));
            fixture.Forget(a);
            Assert.DoesNotThrow(() => fixture.Observes(a, Hostile));
        }

        [Test]
        public void TheFixture_SeesOnlyItsHostile()
        {
            fixture.Apply(a, NetFixtureVerb.Observe);
            Assert.That(fixture.Observes(a, 1), Is.False);
            Assert.That(fixture.Observes(a, 2), Is.False);
            Assert.That(fixture.Observes(null, Hostile), Is.False);
            Assert.That(fixture.HostileId, Is.EqualTo(Hostile));
        }

        [Test]
        public void MoveHostile_WalksThePointsInOrder()
        {
            for (var i = 0; i < 3; i++)
                Assert.That(fixture.Apply(a, NetFixtureVerb.MoveHostile), Is.True);
            Assert.That(moves, Is.EqualTo(new[] { P1, P2, P1 }));
            Assert.That(fixture.Step, Is.EqualTo(3));
        }

        [Test]
        public void MoveHostile_WithoutPoints_ChangesNothing()
        {
            var empty = new NetFixture(units, Hostile, new Vector3[0], moves.Add);
            Assert.That(empty.Apply(a, NetFixtureVerb.MoveHostile), Is.False);
            Assert.That(empty.Step, Is.EqualTo(0));
            Assert.That(moves, Is.Empty);
        }

        [Test]
        public void Search_NeedsALivingOwnedUnit()
        {
            Assert.That(fixture.Apply(b, NetFixtureVerb.Search), Is.False, "B owns no operative");
            friend1.GetComponent<Health>().TakeDamage(100000);
            friend2.GetComponent<Health>().TakeDamage(100000);
            Assert.That(fixture.Apply(a, NetFixtureVerb.Search), Is.False, "a squad with nobody alive cannot search");
            Assert.That(fixture.Step, Is.EqualTo(0));
        }

        [Test]
        public void TheContainer_ShowsItsContents_OnlyToTheSearcher()
        {
            Assert.That(fixture.Container.ContentsFor(a), Is.Null, "nothing before the search");
            Assert.That(fixture.Apply(a, NetFixtureVerb.Search), Is.True);
            Assert.That(fixture.Container.ContentsFor(a), Has.Count.EqualTo(2));
            Assert.That(fixture.Container.ContentsFor(b), Is.Null, "B never receives A's search");
            Assert.That(fixture.Container.IsSearchedBy(b), Is.False);
        }

        [Test]
        public void TheContainersContents_AreFixed()
        {
            fixture.Apply(a, NetFixtureVerb.Search);
            var contents = fixture.Container.ContentsFor(a);
            Assert.That(contents[0].DefinitionId, Is.EqualTo("fixture-medkit"));
            Assert.That(contents[0].Quantity, Is.EqualTo(2));
            Assert.That(contents[0].InstanceId, Is.EqualTo("fixture-item-0001"));
            Assert.That(contents[1].DefinitionId, Is.EqualTo("fixture-rifle-ammo"));
            Assert.That(contents[1].Quantity, Is.EqualTo(30));
            Assert.That(contents[1].InstanceId, Is.EqualTo("fixture-item-0002"));
            Assert.That(NetFixtureContainer.Id, Is.EqualTo(1));
        }

        [Test]
        public void AnUnknownVerb_OrNoConnection_ChangesNothing()
        {
            Assert.That(fixture.Apply(a, NetFixtureVerb.None), Is.False);
            Assert.That(fixture.Apply(a, (NetFixtureVerb)99), Is.False);
            Assert.That(fixture.Apply(null, NetFixtureVerb.Observe), Is.False);
            Assert.That(fixture.Step, Is.EqualTo(0));
        }

        [Test]
        public void Forget_DropsTheGrantAndTheSearch()
        {
            fixture.Apply(a, NetFixtureVerb.Observe);
            fixture.Apply(a, NetFixtureVerb.Search);
            fixture.Forget(a);
            Assert.That(fixture.Observes(a, Hostile), Is.False);
            Assert.That(fixture.Container.ContentsFor(a), Is.Null);
        }
    }
}
