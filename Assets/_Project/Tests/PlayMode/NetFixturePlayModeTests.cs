#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>
    /// The development fixture through the real server (spec section 3.9). Host mode: these tests read the server's observer sets
    /// and the messages the one client decoded; that a separate client process holds nothing is proven by the VM harness.
    /// </summary>
    public class NetFixturePlayModeTests
    {
        const int Hostile = 3;
        const int Melee = 2;

        NetLoopbackRig rig;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            rig = new NetLoopbackRig();
            yield return rig.Start();
            rig.BeginNet(quiet: true, fixture: true);
            Assert.That(rig.Server.Running, Is.True, string.Join("\n", rig.Logs));
        }

        [UnityTearDown]
        public IEnumerator TearDown() => rig.Stop();

        int Client => rig.Server.ClientIds.Single();
        Vector3 HostilePosition => rig.Director.Hostiles[0].transform.position;

        static float Flat(Vector3 offset)
        {
            offset.y = 0f;
            return offset.magnitude;
        }

        IEnumerator Verb(NetFixtureVerb verb)
        {
            var before = rig.Client.Record.FixtureStep;
            rig.Client.SendFixture(verb);
            yield return TestWorld.WaitUntil(() => rig.Client.Record.FixtureStep > before, 5f);
            Assert.That(rig.Client.Record.FixtureStep, Is.EqualTo(before + 1), verb + " took effect");
            yield return null;   // the projection runs in the server's next frame
            yield return null;
        }

        IEnumerator Await(uint command, NetRejectReason expected)
        {
            yield return TestWorld.WaitUntil(() => rig.Client.TryGetResult(command, out _), 5f);
            Assert.That(rig.Client.TryGetResult(command, out var reason), Is.True, "no answer to command " + command);
            Assert.That(reason, Is.EqualTo(expected));
        }

        [UnityTest]
        public IEnumerator TheFixture_HasTwoWalkPoints_AtLeastThreeMetresApart()
        {
            yield return null;
            var points = rig.Server.Fixture.Points;
            Assert.That(points, Has.Count.EqualTo(2));
            Assert.That(Flat(points[0] - points[1]), Is.GreaterThanOrEqualTo(NetFixture.PointSpacing));
            foreach (var point in points)
                Assert.That(Flat(point - HostilePosition), Is.GreaterThanOrEqualTo(NetFixture.PointSpacing));
            Assert.That(rig.Server.Fixture.HostileId, Is.EqualTo(Hostile));
        }

        [UnityTest]
        public IEnumerator TheFifteenTransitions_OnTheServersObserverSets_AndTheClientsRecord()
        {
            yield return rig.Connect(NetLoopbackRig.TokenA);
            var record = rig.Client.Record;

            // 1-2: hidden at start.
            yield return new WaitForSecondsRealtime(0.5f);
            Assert.That(rig.Server.IsObserver(Client, Hostile), Is.False);
            Assert.That(record.KnowledgeMessages, Is.Zero);

            // 4-5: a grant reveals the hostile.
            yield return Verb(NetFixtureVerb.Observe);
            yield return TestWorld.WaitUntil(() => rig.Server.IsObserver(Client, Hostile), 2f);
            Assert.That(rig.Server.IsObserver(Client, Hostile), Is.True);

            // 7-8: it moves while observed and stays observed.
            var spawn = HostilePosition;
            yield return Verb(NetFixtureVerb.MoveHostile);
            Assert.That(Flat(HostilePosition - spawn), Is.GreaterThanOrEqualTo(NetFixture.PointSpacing - 0.01f));
            yield return null;
            var finalObserved = HostilePosition;
            Assert.That(rig.Server.IsObserver(Client, Hostile), Is.True);

            // 9-10: observation ends; one record at the final observed position.
            yield return Verb(NetFixtureVerb.Unobserve);
            yield return TestWorld.WaitUntil(() => !rig.Server.IsObserver(Client, Hostile) && record.LastKnown.ContainsKey(Hostile), 2f);
            Assert.That(rig.Server.IsObserver(Client, Hostile), Is.False);
            Assert.That(record.LastKnown, Has.Count.EqualTo(1));
            Assert.That(Vector3.Distance(record.LastKnown[Hostile], finalObserved), Is.LessThan(0.01f));

            // 11-12: it moves while hidden; the record stays.
            var frozen = record.LastKnown[Hostile];
            var messages = record.KnowledgeMessages;
            yield return Verb(NetFixtureVerb.MoveHostile);
            Assert.That(Flat(HostilePosition - finalObserved), Is.GreaterThanOrEqualTo(NetFixture.PointSpacing - 0.01f));
            yield return new WaitForSecondsRealtime(0.5f);
            Assert.That(record.LastKnown[Hostile], Is.EqualTo(frozen));
            Assert.That(record.KnowledgeMessages, Is.EqualTo(messages));
            Assert.That(rig.Server.IsObserver(Client, Hostile), Is.False);

            // 14-15: re-observation gives the current position and clears the record.
            yield return Verb(NetFixtureVerb.Observe);
            yield return TestWorld.WaitUntil(() => rig.Server.IsObserver(Client, Hostile) && !record.LastKnown.ContainsKey(Hostile), 2f);
            Assert.That(rig.Server.IsObserver(Client, Hostile), Is.True);
            Assert.That(record.LastKnown.ContainsKey(Hostile), Is.False);
        }

        [UnityTest]
        public IEnumerator AVisibilityFault_FailsClosed_AndLaterHostileMovesDoNotReachTheClient()
        {
            // One loopback client: that the other connection is unaffected is shown by NetFixtureTests (per connection) and the VM harness.
            yield return rig.Connect(NetLoopbackRig.TokenA);
            var record = rig.Client.Record;
            yield return Verb(NetFixtureVerb.Observe);
            yield return TestWorld.WaitUntil(() => rig.Server.IsObserver(Client, Hostile), 2f);
            Assert.That(rig.Server.IsObserver(Client, Hostile), Is.True);
            yield return new WaitForSecondsRealtime(0.5f);   // the reveal's own knowledge message arrives
            var messages = record.KnowledgeMessages;

            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(
                @"^\[Net\] visibility fails closed: the intel source threw InvalidOperationException: fixture fault; hostiles it cannot answer for are hidden$"));
            yield return Verb(NetFixtureVerb.Fault);
            yield return TestWorld.WaitUntil(() => !rig.Server.IsObserver(Client, Hostile) && record.LastKnown.ContainsKey(Hostile), 2f);
            Assert.That(rig.Server.IsObserver(Client, Hostile), Is.False);
            Assert.That(record.LastKnown, Has.Count.EqualTo(1));
            Assert.That(record.KnowledgeMessages, Is.EqualTo(messages + 1), "only the Observed to LastKnown transition");
            var frozen = record.LastKnown[Hostile];

            yield return Verb(NetFixtureVerb.MoveHostile);
            yield return new WaitForSecondsRealtime(0.5f);
            Assert.That(rig.Server.IsObserver(Client, Hostile), Is.False);
            Assert.That(record.LastKnown[Hostile], Is.EqualTo(frozen));
            Assert.That(record.KnowledgeMessages, Is.EqualTo(messages + 1));
        }

        [UnityTest]
        public IEnumerator AnOrdinaryClient_CannotDriveTheFixture_AndReceivesNothing()
        {
            yield return rig.Connect(NetLoopbackRig.TokenB);
            var rejected = rig.Server.Host.Rejected;
            rig.Client.SendFixture(NetFixtureVerb.Observe);
            rig.Client.SendFixture(NetFixtureVerb.Search);
            yield return new WaitForSecondsRealtime(1f);
            Assert.That(rig.Server.Host.Rejected, Is.EqualTo(rejected + 2), "both refused and counted");
            Assert.That(rig.Server.Fixture.Step, Is.EqualTo(0));
            Assert.That(rig.Client.Record.FixtureStep, Is.EqualTo(0));
            for (var id = 1; id <= 3; id++)
                Assert.That(rig.Server.IsObserver(Client, id), Is.False, "unit " + id);
            Assert.That(rig.Client.Record.Contents, Is.Null);
            Assert.That(rig.Client.Record.KnowledgeMessages, Is.Zero);
        }

        [UnityTest]
        public IEnumerator Search_SendsTheContents_OnlyAfterTheSearch()
        {
            yield return rig.Connect(NetLoopbackRig.TokenA);
            yield return new WaitForSecondsRealtime(0.5f);
            Assert.That(rig.Client.Record.Contents, Is.Null);
            Assert.That(rig.Client.Record.ContainerMessages, Is.Zero);
            yield return Verb(NetFixtureVerb.Search);
            yield return TestWorld.WaitUntil(() => rig.Client.Record.Contents != null, 2f);
            var contents = rig.Client.Record.Contents;
            Assert.That(contents, Has.Count.EqualTo(2));
            Assert.That(contents[0].DefinitionId, Is.EqualTo("fixture-medkit"));
            Assert.That(contents[0].Quantity, Is.EqualTo(2));
            Assert.That(contents[0].InstanceId, Is.EqualTo("fixture-item-0001"));
        }

        [UnityTest]
        public IEnumerator AGuessedAttackOnTheHiddenHostile_IsUnknownUnit_AndChangesNothing()
        {
            yield return rig.Connect(NetLoopbackRig.TokenA);
            yield return Await(rig.Client.Send(new NetAttack(1, Hostile)), NetRejectReason.UnknownUnit);
            yield return Await(rig.Client.Send(new NetAttack(1, 999)), NetRejectReason.UnknownUnit);
            Assert.That(rig.Director.Friendlies[0].CurrentCommand, Is.Null);
            Assert.That(rig.Director.Hostiles[0].GetComponent<Health>().Current, Is.EqualTo(rig.Director.Hostiles[0].GetComponent<Health>().Max));
        }

        [UnityTest]
        public IEnumerator LosingSight_EndsTheClientsAttack_AndTheIdIsUnknownAgain()
        {
            yield return rig.Connect(NetLoopbackRig.TokenA);
            yield return Verb(NetFixtureVerb.Observe);
            yield return TestWorld.WaitUntil(() => rig.Server.IsObserver(Client, Hostile), 2f);
            yield return Await(rig.Client.Send(new NetAttack(Melee, Hostile)), NetRejectReason.None);
            var melee = rig.Director.Friendlies[1];
            Assert.That(melee.CurrentCommand, Is.InstanceOf<AttackCommand>());

            yield return Verb(NetFixtureVerb.Unobserve);
            yield return TestWorld.WaitUntil(() => melee.CurrentCommand == null, 2f);
            Assert.That(melee.CurrentCommand, Is.Null, "the server ended the chase");
            var hostileHealth = rig.Director.Hostiles[0].GetComponent<Health>();
            Assert.That(hostileHealth.Current, Is.EqualTo(hostileHealth.Max), "the melee operative never reached it");
            yield return Await(rig.Client.Send(new NetAttack(Melee, Hostile)), NetRejectReason.UnknownUnit);
        }
    }
}
#endif
