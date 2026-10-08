#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.TestTools.Utils;

namespace Blackglass.Tests
{
    public class MissionSecurityPlayModeTests
    {
        MissionRig rig;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
        }

        static MissionSettings WithCameras(int cameras) =>
            new MissionSettings { intelligence = new IntelligenceSettings { cameraCount = cameras } };

        [UnityTest]
        public IEnumerator WithCameras_TheMissionHasACameraTerminalAndCameras_AndTheRegistryHoldsBothTerminals()
        {
            rig = new MissionRig(WithCameras(3));
            yield return rig.Generate(12345);
            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready), string.Join("\n", rig.Director.Report.Failures));
            var mission = rig.Director.Current;

            Assert.That(mission.Security.HasTerminal, Is.True);
            Assert.That(mission.CameraTerminal, Is.Not.Null);
            Assert.That(mission.CameraTerminal.IsAvailable, Is.True, "usable from the start");
            Assert.That(mission.CameraTerminal.DisplayName, Is.EqualTo("Camera control"));
            Assert.That(mission.CameraTerminal.GetComponent<Collider>(), Is.Not.Null, "solid like the data terminal");
            Assert.That(mission.Network, Is.Not.Null);
            Assert.That(mission.Network.Cameras.Count, Is.EqualTo(mission.Security.Cameras.Count));
            Assert.That(mission.Network.Compromised, Is.False);
            Assert.That(rig.Interactables.Items, Is.EquivalentTo(new[] { mission.Terminal, mission.CameraTerminal }));
            Assert.That(rig.Director.Report.SecurityHash, Is.EqualTo(mission.Security.Hash));
        }

        [UnityTest]
        public IEnumerator TheCameraMounts_HaveNoColliders_AndHangAboveTheFloor()
        {
            rig = new MissionRig(WithCameras(3));
            yield return rig.Generate(12345);
            var network = rig.Director.Current.Network;
            foreach (var camera in network.Cameras)
            {
                Assert.That(camera.Visual, Is.Not.Null);
                Assert.That(camera.Visual.GetComponentsInChildren<Collider>(), Is.Empty, "a camera never blocks a ray or a click");
                Assert.That(camera.Position.y, Is.EqualTo(SecurityPlan.CameraHeight));
                Assert.That(camera.Visual.transform.IsChildOf(rig.Director.Current.Root.transform), Is.True, "destroyed with the mission");
            }
        }

        [UnityTest]
        public IEnumerator WithoutCameras_NothingIsAdded()
        {
            rig = new MissionRig(WithCameras(0));
            yield return rig.Generate(12345);
            var mission = rig.Director.Current;
            Assert.That(mission.Security, Is.SameAs(SecurityPlan.Empty));
            Assert.That(mission.CameraTerminal == null, Is.True);
            Assert.That(mission.Network, Is.Null);
            Assert.That(rig.Interactables.Items, Is.EqualTo(new[] { mission.Terminal }));
        }

        [UnityTest]
        public IEnumerator CamerasDoNotMoveTheLayoutOrTheObjectives()
        {
            foreach (var seed in new[] { 12345, 31 })
            {
                rig = new MissionRig(WithCameras(0));
                yield return rig.Generate(seed);
                var layoutHash = rig.Director.Report.LayoutHash;
                var objectiveHash = rig.Director.Report.ObjectiveHash;
                var hostile = rig.Director.Hostiles[0].transform.position;
                rig.Dispose();

                rig = new MissionRig(WithCameras(3));
                yield return rig.Generate(seed);
                Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready), string.Join("\n", rig.Director.Report.Failures));
                Assert.That(rig.Director.Report.LayoutHash, Is.EqualTo(layoutHash), $"seed {seed}");
                Assert.That(rig.Director.Report.ObjectiveHash, Is.EqualTo(objectiveHash), $"seed {seed}");
                Assert.That(rig.Director.Hostiles[0].transform.position, Is.EqualTo(hostile).Using(Vector3EqualityComparer.Instance), $"seed {seed}");
                rig.Dispose();
                rig = null;
            }
        }

        [UnityTest]
        public IEnumerator TheCameraTerminal_IsReachableAndAUnitCanStandInRange()
        {
            rig = new MissionRig(WithCameras(3));
            foreach (var seed in new[] { 12345, 1, 2, 3 })
            {
                yield return rig.Generate(seed);
                Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready), $"seed {seed}: " + string.Join("\n", rig.Director.Report.Failures));
                var mover = rig.Director.Friendlies[0].GetComponent<UnitMover>();
                var terminal = rig.Director.Current.CameraTerminal;
                Assert.That(mover.CanReach(terminal.Position), Is.True, $"seed {seed}");
                Assert.That(mover.TrySnap(terminal.Position, out var stand), Is.True);
                Assert.That(TestWorld.HorizontalDistance(stand, terminal.Position), Is.LessThanOrEqualTo(terminal.Range - 0.2f), $"seed {seed}");
            }
        }

        [UnityTest]
        public IEnumerator WorkingTheCameraTerminal_RaisesCompleted()
        {
            rig = new MissionRig(WithCameras(3));
            yield return rig.Generate(12345);
            var terminal = rig.Director.Current.CameraTerminal;
            var unit = rig.Director.Friendlies[0];
            var fired = 0;
            terminal.Completed += _ => fired++;
            Assert.That(terminal.TryBegin(unit), Is.True);
            terminal.Advance(unit, 100f);
            Assert.That(fired, Is.EqualTo(1));
            Assert.That(terminal.IsCompleted, Is.True);
        }

        [UnityTest]
        public IEnumerator Regenerating_ReplacesTheCamerasAndLeavesNoneBehind()
        {
            rig = new MissionRig(WithCameras(3));
            yield return rig.Generate(12345);
            var oldRoot = rig.Director.Current.Root;
            var oldCameras = rig.Director.Current.Network.Cameras.Select(c => c.Visual).ToArray();
            yield return rig.Generate(31);
            Assert.That(oldRoot == null, Is.True);
            Assert.That(oldCameras.All(c => c == null), Is.True);
            Assert.That(Object.FindObjectsByType<Transform>(FindObjectsSortMode.None).Count(t => t.name.StartsWith("SecurityCamera_")),
                Is.EqualTo(rig.Director.Current.Network.Cameras.Count));
        }
    }
}
#endif
