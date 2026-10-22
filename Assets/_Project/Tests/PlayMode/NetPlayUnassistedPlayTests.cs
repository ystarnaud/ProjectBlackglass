#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>
    /// Fixture 03 played to extraction through the play client's own command layer, with no operator aid. The "player" below
    /// decides only from what the client was sent:
    /// - the projected map: it explores room-sized floor rectangles nearest first;
    /// - the replicated avatars: it attacks a hostile it observes;
    /// - the last-known records: it walks to where the hostile was seen;
    /// - the objectives: it walks to the extraction point once extraction opens;
    /// - the end message.
    /// The server runs -attemptFixture and -net without -netQuiet: the hostile's AI, the squad's autonomy and the hit rolls are
    /// the normal ones. Nothing is warped, damaged directly or completed by a call. The server's state is read only for the final
    /// assertions.
    /// </summary>
    public class NetPlayUnassistedPlayTests
    {
        NetLoopbackRig rig;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            rig = new NetLoopbackRig();
            yield return rig.Start();
            rig.Director.Clear();
            yield return null;
            var fixture = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Backend", "contracts", "attempt-v1", "03-seeded-collect-and-use.json");
            rig.Bootstrap.Begin(new[] { "-attemptFixture", fixture });
            yield return TestWorld.WaitUntil(() => rig.Bootstrap.Session != null || rig.Bootstrap.HasExited, 60f);
            Assert.That(rig.Bootstrap.Session, Is.Not.Null, rig.Bootstrap.ExitReason);
            rig.BeginNet(quiet: false);
        }

        [UnityTearDown]
        public IEnumerator TearDown() => rig.Stop();

        [UnityTest, Timeout(480000)]
        public IEnumerator A_player_with_only_the_play_client_finds_engages_and_extracts()
        {
            var acceptedBefore = rig.Server.Host.Accepted;
            yield return rig.Connect(NetLoopbackRig.TokenA);
            var client = rig.Client;
            Assert.That(client.State, Is.EqualTo(NetClientState.Ready));
            var view = NetPlayKit.AddMapView(client.gameObject, client, false);
            var controls = client.gameObject.AddComponent<NetPlayControls>();
            controls.Initialize(client, null, null, view, null, null, null, null, null, null);
            var shots = 0;
            var shotsNamingUnseenUnits = 0;
            client.ShotReceived += shot =>
            {
                shots++;
                var visible = NetClient.VisibleStates().Select(s => s.UnitId).ToList();
                if (!visible.Contains(shot.AttackerId) || !visible.Contains(shot.TargetId))
                    shotsNamingUnseenUnits++;
            };
            yield return TestWorld.WaitUntil(() => view.Built && client.Record.Objectives.Count == 2 && Own(client).Count == client.OwnedCount, 15f);
            Assert.That(view.Built, Is.True, "the projected map arrived and was drawn");
            Assert.That(client.Record.Objectives.Count, Is.EqualTo(2), "the objectives arrived");
            controls.SelectAllLiving();

            // Explore: room-sized floor rectangles, nearest first from the squad, until a hostile is observed.
            var squadStart = Centre(Own(client));
            var waypoints = view.Layout.FloorRects.Where(r => r.width * r.height >= 16).Select(r => view.Layout.RectCenter(r))
                .Where(p => Vector3.Distance(p, squadStart) > 4f).ToList();
            var deadline = Time.realtimeSinceStartup + 360f;
            int hostileId = 0;
            var nextOrder = 0f;
            while (Time.realtimeSinceStartup < deadline && client.Record.Result == NetMissionResult.None)
            {
                var objectives = client.Record.Objectives;
                var extraction = objectives.FirstOrDefault(o => o.Kind == NetObjectiveKind.Extraction);
                var hostile = NetClient.VisibleStates().FirstOrDefault(s => s.Hostile && s.Alive);
                if (Time.realtimeSinceStartup >= nextOrder)
                {
                    nextOrder = Time.realtimeSinceStartup + 1f;
                    controls.SelectAllLiving();
                    if (extraction.State == ObjectiveState.Active)
                        controls.MoveSelected(extraction.Point, false);
                    else if (hostile != null)
                    {
                        hostileId = hostile.UnitId;
                        if (Own(client).Any(u => u.Order != NetOrderKind.Attack))
                            controls.AttackWithSelected(hostile.UnitId);
                    }
                    else if (hostileId != 0 && client.Record.LastKnown.TryGetValue(hostileId, out var seen))
                        controls.MoveSelected(seen, false);
                    else if (waypoints.Count > 0)
                    {
                        var here = Centre(Own(client));
                        var next = waypoints.OrderBy(p => Vector3.Distance(p, here)).First();
                        if (Vector3.Distance(here, next) < 2.5f)
                            waypoints.Remove(next);
                        else
                            controls.MoveSelected(next, false);
                    }
                }
                yield return null;
            }

            var session = rig.Bootstrap.Session;
            Assert.That(client.Record.Result, Is.EqualTo(NetMissionResult.Success), "the client was told the mission succeeded");
            Assert.That(session.IsFrozen, Is.True);
            Assert.That(session.Result.Outcome.ToString(), Is.EqualTo("Success"));
            Assert.That(session.Outcome.Manifest, Is.Not.Null, "a manifest was built at the freeze");
            Assert.That(rig.Server.Host.Accepted - acceptedBefore, Is.GreaterThan(0));
            Assert.That(shots, Is.GreaterThan(0), "the client was shown at least one shot");
            Assert.That(shotsNamingUnseenUnits, Is.Zero, "every shot named units this client was sent");
            Assert.That(client.Record.MapMessages, Is.EqualTo(1));
            Assert.That(client.Record.EndMessages, Is.EqualTo(1));
        }

        static List<NetUnitState> Own(NetClient client) => NetClient.VisibleStates()
            .Where(s => !s.Hostile && s.UnitId >= client.FirstOwnedId && s.UnitId < client.FirstOwnedId + client.OwnedCount && s.Alive).ToList();

        static Vector3 Centre(List<NetUnitState> units) =>
            units.Count == 0 ? Vector3.zero : units.Aggregate(Vector3.zero, (sum, u) => sum + u.transform.position) / units.Count;
    }
}
#endif
