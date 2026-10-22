#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using FishNet.Managing;
using FishNet.Transporting;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>
    /// The briefing over the real WebSocket transport in host mode (decision 052): the map and the known objectives reach a
    /// connection that owns units, after its welcome and never before; a connection that never said hello, one that owns no
    /// operative and one whose hello was refused receive no map, objectives, end or shot. These tests warp and damage units
    /// directly to drive the relay; that is not the acceptance path.
    /// </summary>
    public class NetBriefingPlayTests
    {
        NetLoopbackRig rig;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            rig = new NetLoopbackRig();
            yield return rig.Start();
        }

        [UnityTearDown]
        public IEnumerator TearDown() => rig.Stop();

        [UnityTest]
        public IEnumerator A_squad_owner_receives_the_public_map_and_known_objectives_after_its_welcome()
        {
            rig.BeginNet(quiet: true);
            yield return rig.Connect(NetLoopbackRig.TokenA);
            var record = rig.Client.Record;
            yield return TestWorld.WaitUntil(() => record.Map != null && record.Objectives.Count > 0, 10f);
            Assert.That(record.MapMessages, Is.EqualTo(1));
            var layout = rig.Director.Current.Layout;
            Assert.That(record.Map.Floors, Is.EqualTo(layout.FloorRects));
            Assert.That(record.Map.Boxes.Select(b => (b.Kind, b.Footprint, b.Rise)), Is.EqualTo(layout.Boxes.Select(b => (b.Kind, b.Footprint, b.Height))));
            Assert.That(record.Map.Containers, Is.EqualTo(rig.Director.Current.Loot.Containers.Select(c => c.Tile).OrderBy(t => t.y).ThenBy(t => t.x)));
            var types = record.DecodedMessages.Select(d => d.Type).ToList();
            Assert.That(types.IndexOf("NetWelcomeMessage"), Is.LessThan(types.IndexOf("NetMapMessage")), "nothing of the briefing before the welcome");
            Assert.That(record.Objectives.Select(o => (o.Kind, o.State)),
                Is.EqualTo(new[] { (NetObjectiveKind.Eliminate, ObjectiveState.Active), (NetObjectiveKind.Extraction, ObjectiveState.Inactive) }));
            var zone = rig.Director.Current.ExtractionZone.position;
            Assert.That(Vector3.Distance(record.Objectives[1].Point, zone), Is.LessThan(1e-4f));
            Assert.That(rig.Logs.Any(l => l.Contains("[Net] map projection on floors=")), Is.True);
        }

        [UnityTest]
        public IEnumerator Objective_changes_and_the_mission_end_reach_the_owner()
        {
            rig.BeginNet(quiet: true);
            yield return rig.Connect(NetLoopbackRig.TokenA);
            var record = rig.Client.Record;
            yield return TestWorld.WaitUntil(() => record.Objectives.Count == 2, 10f);
            rig.Director.Hostiles[0].GetComponent<Health>().TakeDamage(100000);
            yield return TestWorld.WaitUntil(() => record.Objectives.Count == 2 && record.Objectives[0].State == ObjectiveState.Completed
                && record.Objectives[1].State == ObjectiveState.Active, 10f);
            foreach (var friendly in rig.Director.Friendlies)
                friendly.GetComponent<Health>().TakeDamage(100000);
            yield return TestWorld.WaitUntil(() => record.Result != NetMissionResult.None, 10f);
            Assert.That(record.Result, Is.EqualTo(NetMissionResult.Failure));
            Assert.That(record.EndMessages, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator A_mission_whose_map_knowledge_is_not_full_sends_no_map_and_says_why()
        {
            rig.Director.Settings.intelligence.map = MapKnowledge.Partial;
            rig.Director.RegenerateSame();
            yield return TestWorld.WaitUntil(() => rig.Director.State == MissionState.Ready, 60f);
            rig.BeginNet(quiet: true);
            yield return rig.Connect(NetLoopbackRig.TokenA);
            yield return new WaitForSecondsRealtime(2f);
            Assert.That(rig.Logs.Any(l => l.Contains("[Net] map projection off: map knowledge Partial")), Is.True);
            // Owner ruling D1: outside the approved profile nothing of the briefing is sent, not even the end or a shot.
            yield return DriveShotObjectiveAndEnd();
            var record = rig.Client.Record;
            Assert.That((record.MapMessages, record.ObjectivesMessages, record.EndMessages, record.ShotMessages), Is.EqualTo((0, 0, 0, 0)));
        }

        // Owner ruling: the largest supported map crosses the real WebSocket transport (here in one process; the two-process
        // proof is NetPlayTwoProcessPlayTests).
        [UnityTest]
        public IEnumerator The_largest_map_crosses_the_websocket_transport()
        {
            var largest = NetMapSamples.Largest();
            Assert.That(NetMapValidation.Problem(largest), Is.Null, "the sample is a valid map at every limit");
            rig.BeginNet(quiet: true);
            rig.Server.BriefingMap = largest;
            yield return rig.Connect(NetLoopbackRig.TokenA);
            var record = rig.Client.Record;
            yield return TestWorld.WaitUntil(() => record.Map != null || record.MapProblem != null, 15f);
            Assert.That(record.MapProblem, Is.Null);
            Assert.That((record.Map.Width, record.Map.Height), Is.EqualTo((largest.Width, largest.Height)));
            Assert.That(record.Map.Floors, Is.EqualTo(largest.Floors));
            Assert.That(record.Map.Boxes.Select(b => (b.Kind, b.Footprint, b.Rise)), Is.EqualTo(largest.Boxes.Select(b => (b.Kind, b.Footprint, b.Rise))));
            Assert.That(record.Map.Containers, Is.EqualTo(largest.Containers));
        }

        [UnityTest]
        public IEnumerator Shots_between_units_the_owner_receives_reach_it_with_both_ids()
        {
            rig.BeginNet(quiet: true);
            yield return rig.Connect(NetLoopbackRig.TokenA);
            var shots = new System.Collections.Generic.List<NetShot>();
            rig.Client.ShotReceived += shots.Add;
            var hostile = rig.Director.Hostiles[0];
            var friendly = rig.Director.Friendlies[0];
            var hostileAttacker = hostile.GetComponent<UnitAttacker>();
            var placed = false;
            yield return PlaceInSight(friendly, hostileAttacker, ok => placed = ok);
            Assert.That(placed, Is.True, "an open spot in the hostile's sight");
            var hostileId = rig.Server.Units.IdOf(hostile);
            var friendlyId = rig.Server.Units.IdOf(friendly);
            // Fire only once the squad observes the hostile, so the relay's replication check passes for this connection.
            yield return TestWorld.WaitUntil(() => NetClient.VisibleStates().Any(s => s.UnitId == hostileId), 10f);
            Assert.That(friendly.GetComponent<UnitAttacker>().TryAttack(hostile.GetComponent<Health>()), Is.True, "the operative fires");
            yield return TestWorld.WaitUntil(() => shots.Count == 1, 5f);
            Assert.That(hostileAttacker.TryAttack(friendly.GetComponent<Health>()), Is.True, "the hostile fires back");
            yield return TestWorld.WaitUntil(() => shots.Count == 2, 5f);
            Assert.That(shots.Select(s => (s.AttackerId, s.TargetId)), Is.EqualTo(new[] { (friendlyId, hostileId), (hostileId, friendlyId) }));
            Assert.That(shots[0].Hit, Is.True, "the quiet server forces the squad's hits");
            Assert.That(rig.Client.Record.ShotMessages, Is.EqualTo(2));
        }

        // Checkpoint 1 review I1: a participant that owns the squad but does not observe the shooting hostile gets no shot. The
        // fixture source grants sight by verb only (no exposure by firing), so "unobserved while firing" is reachable here.
        [UnityTest]
        public IEnumerator A_shot_from_a_hostile_the_owner_does_not_observe_never_reaches_it()
        {
            rig.BeginNet(quiet: true, fixture: true);
            yield return rig.Connect(NetLoopbackRig.TokenA);
            var record = rig.Client.Record;
            yield return TestWorld.WaitUntil(() => record.Map != null, 10f);
            var shots = new System.Collections.Generic.List<NetShot>();
            rig.Client.ShotReceived += shots.Add;
            var hostile = rig.Director.Hostiles[0];
            var friendly = rig.Director.Friendlies[0];
            var hostileAttacker = hostile.GetComponent<UnitAttacker>();
            var placed = false;
            yield return PlaceInSight(friendly, hostileAttacker, ok => placed = ok);
            Assert.That(placed, Is.True, "an open spot in the hostile's sight");
            var hostileId = rig.Server.Units.IdOf(hostile);
            Assert.That(NetClient.VisibleStates().Any(s => s.UnitId == hostileId), Is.False, "the fixture has granted no sight");
            Assert.That(hostileAttacker.TryAttack(friendly.GetComponent<Health>()), Is.True, "the unobserved hostile fires");
            for (var frame = 0; frame < 30; frame++)
                yield return null;
            Assert.That(record.ShotMessages, Is.Zero, "no shot from a hostile this connection does not observe");
            Assert.That(shots, Is.Empty);

            rig.Client.SendFixture(NetFixtureVerb.Observe);
            yield return TestWorld.WaitUntil(() => NetClient.VisibleStates().Any(s => s.UnitId == hostileId), 10f);
            yield return TestWorld.WaitUntil(() => hostileAttacker.CooldownRemaining <= 0f, 10f);
            Assert.That(hostileAttacker.TryAttack(friendly.GetComponent<Health>()), Is.True, "the observed hostile fires");
            yield return TestWorld.WaitUntil(() => shots.Count == 1, 5f);
            Assert.That((shots[0].AttackerId, shots[0].TargetId), Is.EqualTo((hostileId, rig.Server.Units.IdOf(friendly))));
            Assert.That(record.ShotMessages, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator An_invalid_briefing_map_is_never_sent()
        {
            rig.BeginNet(quiet: true);
            rig.Server.BriefingMap = new NetMap(10, 10, new System.Collections.Generic.List<RectInt> { new RectInt(0, 0, 4, 4), new RectInt(2, 2, 4, 4) },
                new System.Collections.Generic.List<NetMapBox>(), new System.Collections.Generic.List<Vector2Int>());
            yield return rig.Connect(NetLoopbackRig.TokenA);
            yield return TestWorld.WaitUntil(() => rig.Client.Record.Objectives.Count > 0, 10f);
            yield return new WaitForSecondsRealtime(0.5f);
            Assert.That(rig.Client.Record.MapMessages, Is.Zero);
        }

        // Owner ruling: pre-hello, zero-owned and refused connections receive no protected payload.

        [UnityTest]
        public IEnumerator A_connection_that_owns_no_unit_receives_no_protected_payload()
        {
            rig.BeginNet(quiet: true);
            yield return rig.Connect(NetLoopbackRig.TokenB);
            Assert.That(rig.Client.State, Is.EqualTo(NetClientState.Ready));
            yield return DriveShotObjectiveAndEnd();
            var record = rig.Client.Record;
            Assert.That(rig.Client.State, Is.EqualTo(NetClientState.Ready), "still connected through the whole window");
            Assert.That((record.MapMessages, record.ObjectivesMessages, record.EndMessages, record.ShotMessages), Is.EqualTo((0, 0, 0, 0)));
        }

        [UnityTest]
        public IEnumerator A_refused_hello_receives_no_protected_payload()
        {
            rig.BeginNet(quiet: true);
            yield return rig.Connect("tok-not-in-the-file");
            Assert.That(rig.Client.State, Is.EqualTo(NetClientState.Refused));
            yield return DriveShotObjectiveAndEnd();
            var record = rig.Client.Record;
            Assert.That((record.MapMessages, record.ObjectivesMessages, record.EndMessages, record.ShotMessages), Is.EqualTo((0, 0, 0, 0)));
        }

        [UnityTest]
        public IEnumerator A_connection_that_never_says_hello_receives_no_protected_payload()
        {
            rig.BeginNet(quiet: true);
            var client = NetworkManager.Instances[0].ClientManager;
            var received = 0;
            System.Action<NetMapMessage, Channel> onMap = (m, c) => received++;
            System.Action<NetObjectivesMessage, Channel> onObjectives = (m, c) => received++;
            System.Action<NetMissionEndMessage, Channel> onEnd = (m, c) => received++;
            System.Action<NetShotMessage, Channel> onShot = (m, c) => received++;
            System.Action<NetWelcomeMessage, Channel> onWelcome = (m, c) => received++;
            client.RegisterBroadcast(onMap);
            client.RegisterBroadcast(onObjectives);
            client.RegisterBroadcast(onEnd);
            client.RegisterBroadcast(onShot);
            client.RegisterBroadcast(onWelcome);
            try
            {
                Assert.That(client.StartConnection(), Is.True);
                yield return TestWorld.WaitUntil(() => client.Started, 5f);
                Assert.That(rig.Server.ConnectionCount, Is.EqualTo(1), "connected, and no hello is ever sent");
                yield return DriveShotObjectiveAndEnd();
                Assert.That(client.Started, Is.True, "still connected through the whole window (inside the hello timeout)");
                Assert.That(received, Is.Zero);
            }
            finally
            {
                client.UnregisterBroadcast(onMap);
                client.UnregisterBroadcast(onObjectives);
                client.UnregisterBroadcast(onEnd);
                client.UnregisterBroadcast(onShot);
                client.UnregisterBroadcast(onWelcome);
                client.StopConnection();
            }
        }

        // A shot (the hostile fires at an operative placed beside it), an objective change (the hostile dies) and the mission
        // end (the squad dies), each confirmed on the server, then time for anything sent to arrive.
        IEnumerator DriveShotObjectiveAndEnd()
        {
            var hostile = rig.Director.Hostiles[0];
            var friendly = rig.Director.Friendlies[0];
            var attacker = hostile.GetComponent<UnitAttacker>();
            var placed = false;
            yield return PlaceInSight(friendly, attacker, ok => placed = ok);
            Assert.That(placed, Is.True, "an open spot in the hostile's sight");
            var shots = 0;
            attacker.Fired += _ => shots++;
            Assert.That(attacker.TryAttack(friendly.GetComponent<Health>()), Is.True, "the hostile fires");
            yield return null;
            hostile.GetComponent<Health>().TakeDamage(100000);
            yield return null;
            Assert.That(rig.Director.Runtime.Objectives[0].State, Is.EqualTo(ObjectiveState.Completed));
            foreach (var unit in rig.Director.Friendlies)
                unit.GetComponent<Health>().TakeDamage(100000);
            yield return TestWorld.WaitUntil(() => rig.Director.Phase == MissionPhase.Failure, 5f);
            Assert.That(shots, Is.EqualTo(1));
            yield return new WaitForSecondsRealtime(1f);
        }

        // Warps the operative to the first walkable point near the hostile from which the hostile can shoot it.
        static IEnumerator PlaceInSight(CommandableUnit mover, UnitAttacker hostile, System.Action<bool> done)
        {
            var agent = mover.GetComponent<NavMeshAgent>();
            var target = mover.GetComponent<Health>();
            var origin = hostile.transform.position;
            foreach (var radius in new[] { 2.5f, 4f, 6f })
            {
                for (var step = 0; step < 8; step++)
                {
                    var angle = step * Mathf.PI / 4f;
                    var candidate = origin + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
                    if (!NavMesh.SamplePosition(candidate, out var hit, 1.5f, NavMesh.AllAreas) || !agent.Warp(hit.position))
                        continue;
                    yield return new WaitForFixedUpdate();
                    if (hostile.CanAttack(target))
                    {
                        done(true);
                        yield break;
                    }
                }
            }
            done(false);
        }
    }
}
#endif
