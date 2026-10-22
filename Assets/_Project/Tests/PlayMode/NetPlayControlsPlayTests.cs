#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>
    /// Clicks become exactly the existing requests, relative to what is drawn: open floor moves every selected operative to the
    /// point under the cursor, at any camera yaw or zoom; an obstacle or a HUD panel sends nothing; nothing is sent unless the
    /// client is ready, after the mission ended, or for a dead operative.
    /// </summary>
    public class NetPlayControlsPlayTests
    {
        NetLoopbackRig rig;
        NetPlayControls controls;
        NetMapView view;
        TacticalCameraController cameraRig;
        Camera viewCamera;
        readonly List<NetRequest> sent = new List<NetRequest>();

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            rig = new NetLoopbackRig();
            yield return rig.Start();
            rig.BeginNet(quiet: true);
            yield return rig.Connect(NetLoopbackRig.TokenA);
            view = NetPlayKit.AddMapView(rig.Client.gameObject, rig.Client, false);
            var rigObject = new GameObject("CameraRig");
            viewCamera = new GameObject("Camera").AddComponent<Camera>();
            viewCamera.transform.SetParent(rigObject.transform, false);
            cameraRig = rigObject.AddComponent<TacticalCameraController>();
            cameraRig.Initialize(viewCamera, null, null, null, null, null);
            controls = rig.Client.gameObject.AddComponent<NetPlayControls>();
            controls.Initialize(rig.Client, viewCamera, cameraRig, view, null, null, null, null, null, null);
            controls.Send = r => { sent.Add(r); return (uint)sent.Count; };
            yield return TestWorld.WaitUntil(() => view.Built && NetClient.VisibleStates().Count == rig.Client.OwnedCount, 10f);
            yield return null;
            sent.Clear();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Object.Destroy(cameraRig.gameObject);
            yield return rig.Stop();
        }

        // Floor tiles 2..15 m from the squad, at least 1 m from every obstacle and away from the units, nearest-open first.
        IEnumerable<Vector3> OpenFloorCandidates()
        {
            var layout = view.Layout;
            var own = NetClient.VisibleStates().Select(s => s.transform.position).ToList();
            var found = new List<(Vector3 Point, float Clearance)>();
            for (var y = 0; y < layout.Height; y++)
                for (var x = 0; x < layout.Width; x++)
                {
                    var c = layout.TileCenter(new Vector2Int(x, y));
                    if (!layout.IsFloor(x, y) || own.Any(p => Vector3.Distance(c, p) < 2f) || own.All(p => Vector3.Distance(c, p) > 15f))
                        continue;
                    var clearance = view.Obstacles.Count == 0 ? float.MaxValue : view.Obstacles.Min(o => o.SqrDistance(c));
                    if (clearance > 1f)
                        found.Add((c, clearance));
                }
            return found.OrderByDescending(f => f.Clearance).Select(f => f.Point);
        }

        Vector3 OpenFloorPoint()
        {
            foreach (var point in OpenFloorCandidates())
                return point;
            Assert.Fail("no open floor tile near the squad");
            return default;
        }

        // The first candidate the camera, placed over it at this yaw and zoom, sees as open floor (no wall in front of it).
        Vector3 VisibleOpenFloorPoint()
        {
            foreach (var point in OpenFloorCandidates())
            {
                cameraRig.FocusOn(point);
                if (NetPick.Ground(viewCamera.ScreenPointToRay(viewCamera.WorldToScreenPoint(point)), view.Layout, view.Obstacles, out _) == NetPick.GroundHit.Floor)
                    return point;
            }
            Assert.Fail("no open floor tile the camera sees");
            return default;
        }

        void Zoom(float distance)
        {
            var serialized = new UnityEditor.SerializedObject(cameraRig);
            serialized.FindProperty("distance").floatValue = distance;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        [UnityTest]
        public IEnumerator A_click_on_open_floor_moves_every_selected_operative_there([Values(0f, 135f)] float yaw, [Values(8f, 30f)] float distance)
        {
            cameraRig.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            Zoom(distance);
            var target = VisibleOpenFloorPoint();   // FocusOn places the camera at the new yaw and distance at once
            yield return null;
            Assert.That(NetPick.Ground(viewCamera.ScreenPointToRay(viewCamera.WorldToScreenPoint(target)), view.Layout, view.Obstacles, out _),
                Is.EqualTo(NetPick.GroundHit.Floor), "precondition: the camera sees the target tile");
            controls.SelectAllLiving();
            controls.CommandAtScreen(viewCamera.WorldToScreenPoint(target), false, false);
            var moves = sent.OfType<NetMoveTo>().ToList();
            Assert.That(moves.Select(m => m.UnitId), Is.EquivalentTo(Enumerable.Range(rig.Client.FirstOwnedId, rig.Client.OwnedCount)));
            foreach (var move in moves)
            {
                Assert.That(Vector3.Distance(move.Destination, target), Is.LessThan(0.05f));
                Assert.That(move.Append, Is.False);
            }
            Assert.That(sent.All(r => r is NetMoveTo || r is NetMoveIntent), Is.True, "only the existing requests");
        }

        [UnityTest]
        public IEnumerator A_click_on_an_obstacle_sends_nothing_and_says_so()
        {
            var box = rig.Director.Current.Layout.Boxes.First(b => b.Kind == MissionBoxKind.Wall);
            var top = NetMapGeometry.BoxBounds(view.Layout, view.Layout.Boxes.First(b => b.Footprint == box.Footprint));
            cameraRig.FocusOn(top.center);
            yield return null;
            controls.SelectAllLiving();
            controls.CommandAtScreen(viewCamera.WorldToScreenPoint(top.center + Vector3.up * (top.extents.y - 0.01f)), false, false);
            Assert.That(sent, Is.Empty);
            StringAssert.Contains("Blocked", controls.LastFeedback);
        }

        [UnityTest]
        public IEnumerator A_click_on_an_own_operative_selects_it_and_sends_nothing()
        {
            var own = NetClient.VisibleStates().OrderBy(s => s.UnitId).Last();
            cameraRig.FocusOn(own.transform.position);
            yield return null;
            controls.CommandAtScreen(viewCamera.WorldToScreenPoint(own.transform.position + Vector3.up * 1f), false, false);
            Assert.That(controls.Selection.Selected, Is.EqualTo(new[] { own.UnitId }));
            Assert.That(sent, Is.Empty);
        }

        [UnityTest]
        public IEnumerator Nothing_is_sent_unless_the_client_is_ready()
        {
            controls.SelectAllLiving();
            var point = OpenFloorPoint();   // before the avatars go away with the connection
            rig.Client.Disconnect();
            yield return TestWorld.WaitUntil(() => rig.Client.State == NetClientState.Disconnected, 5f);
            controls.MoveSelected(point, false);
            controls.StopSelected();
            Assert.That(sent, Is.Empty);
        }

        [UnityTest]
        public IEnumerator Nothing_is_sent_after_the_mission_ended()
        {
            controls.SelectAllLiving();
            rig.Client.Record.ReceiveEnd(0d, (byte)NetMissionResult.Success);
            controls.MoveSelected(OpenFloorPoint(), false);
            controls.StopSelected();
            yield return null;
            Assert.That(sent, Is.Empty);
        }

        [UnityTest]
        public IEnumerator A_dead_operative_is_dropped_and_never_ordered()
        {
            controls.SelectAllLiving();
            var dead = rig.Director.Friendlies[0];
            var deadId = rig.Server.Units.IdOf(dead);
            dead.GetComponent<Health>().TakeDamage(100000);
            yield return TestWorld.WaitUntil(() => NetClient.VisibleStates().Any(s => s.UnitId == deadId && !s.Alive), 5f);
            yield return null;
            Assert.That(controls.Selection.Contains(deadId), Is.False);
            controls.MoveSelected(OpenFloorPoint(), false);
            Assert.That(sent.OfType<NetMoveTo>().Any(m => m.UnitId == deadId), Is.False);
        }
    }
}
#endif
