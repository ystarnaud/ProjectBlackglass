#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class TacticalCameraControllerTests : InputTestFixture
    {
        Keyboard keyboard;
        Mouse mouse;
        TestWorld world;
        TacticalCameraController controller;
        TacticalPause pause;
        PrimaryCharacter primaryCharacter;

        public override void Setup()
        {
            base.Setup();
            keyboard = InputSystem.AddDevice<Keyboard>();
            mouse = InputSystem.AddDevice<Mouse>();
            world = new TestWorld();

            var actions = TestControls.Load();
            // No unit yet: the camera behaves as before until a test gives the primary character a unit.
            primaryCharacter = world.Track(new GameObject("Player")).AddComponent<PrimaryCharacter>();
            var rig = world.Track(new GameObject("CameraRig"));
            rig.SetActive(false);
            var cameraObject = new GameObject("Camera");
            cameraObject.transform.SetParent(rig.transform, false);
            var viewCamera = cameraObject.AddComponent<Camera>();
            controller = rig.AddComponent<TacticalCameraController>();
            controller.Initialize(viewCamera,
                TestControls.Ref(actions, "Camera/Pan"),
                TestControls.Ref(actions, "Camera/Rotate"),
                TestControls.Ref(actions, "Camera/RotateDrag"),
                TestControls.Ref(actions, "Camera/PointerPosition"),
                TestControls.Ref(actions, "Camera/Zoom"),
                primaryCharacter);
            rig.SetActive(true);

            pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
        }

        public override void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
            base.TearDown();
        }

        [UnityTest]
        public IEnumerator HoldingW_PansForward()
        {
            var start = controller.transform.position;
            Press(keyboard.wKey);
            yield return new WaitForSecondsRealtime(0.2f);
            Release(keyboard.wKey);
            yield return null;

            Assert.That(controller.transform.position.z, Is.GreaterThan(start.z + 0.5f));
            Assert.That(controller.transform.position.x, Is.EqualTo(start.x).Within(0.01f));
        }

        [UnityTest]
        public IEnumerator HoldingW_PansWhilePaused()
        {
            pause.Pause();
            var start = controller.transform.position;
            Press(keyboard.wKey);
            yield return new WaitForSecondsRealtime(0.2f);
            Release(keyboard.wKey);
            yield return null;

            Assert.That(controller.transform.position.z, Is.GreaterThan(start.z + 0.5f));
        }

        [UnityTest]
        public IEnumerator ScrollUp_ZoomsInOneStepWhilePaused()
        {
            pause.Pause();
            yield return null;
            var start = controller.Distance;

            Set(mouse.scroll, new Vector2(0f, 120f));
            yield return null;
            yield return null;

            Assert.That(controller.Distance, Is.EqualTo(start - 2f).Within(0.001f));
        }

        [UnityTest]
        public IEnumerator Zoom_IsClampedToRange()
        {
            for (var i = 0; i < 30; i++)
            {
                Set(mouse.scroll, new Vector2(0f, 120f));
                yield return null;
                Set(mouse.scroll, Vector2.zero);
                yield return null;
            }
            Assert.That(controller.Distance, Is.EqualTo(5f).Within(0.001f));
        }

        [UnityTest]
        public IEnumerator HoldingE_RotatesWhilePaused()
        {
            pause.Pause();
            var startYaw = controller.Yaw;
            Press(keyboard.eKey);
            yield return new WaitForSecondsRealtime(0.2f);
            Release(keyboard.eKey);
            yield return null;

            Assert.That(Mathf.DeltaAngle(startYaw, controller.Yaw), Is.GreaterThan(5f));
        }

        [UnityTest]
        public IEnumerator RightDrag_RotatesByPixelsTravelledAfterThreshold_WhilePaused()
        {
            pause.Pause();
            Set(mouse.position, new Vector2(100f, 100f));
            yield return null;
            var startYaw = controller.Yaw;

            Press(mouse.rightButton);
            yield return null;
            Set(mouse.position, new Vector2(110f, 100f));   // crosses the 6 px threshold, no rotation yet
            yield return null;
            Set(mouse.position, new Vector2(210f, 100f));   // +100 px of drag
            yield return null;
            Release(mouse.rightButton);
            yield return null;

            Assert.That(Mathf.DeltaAngle(startYaw, controller.Yaw), Is.EqualTo(25f).Within(0.5f));
            Assert.That(controller.Pitch, Is.EqualTo(55f).Within(0.001f), "Horizontal drag must not tilt");
        }

        IEnumerator RightDragVertically(float fromY, float toY)
        {
            Set(mouse.position, new Vector2(100f, fromY));
            yield return null;
            Press(mouse.rightButton);
            yield return null;
            var direction = Mathf.Sign(toY - fromY);
            Set(mouse.position, new Vector2(100f, fromY + 10f * direction));   // crosses the 6 px threshold, no tilt yet
            yield return null;
            Set(mouse.position, new Vector2(100f, toY));
            yield return null;
            Release(mouse.rightButton);
            yield return null;
        }

        [UnityTest]
        public IEnumerator RightDragUp_TiltsTowardHorizon_WhilePaused()
        {
            pause.Pause();
            var startYaw = controller.Yaw;

            yield return RightDragVertically(100f, 210f);   // +100 px of upward drag after the threshold

            Assert.That(controller.Pitch, Is.EqualTo(30f).Within(0.5f));
            Assert.That(controller.Yaw, Is.EqualTo(startYaw).Within(0.001f), "Vertical drag must not rotate");
        }

        [UnityTest]
        public IEnumerator RightDragDown_TiltsTowardTopDown()
        {
            yield return RightDragVertically(300f, 190f);   // -100 px of downward drag after the threshold

            Assert.That(controller.Pitch, Is.EqualTo(80f).Within(0.5f));
        }

        [UnityTest]
        public IEnumerator Tilt_IsClampedToRange()
        {
            yield return RightDragVertically(100f, 1100f);
            Assert.That(controller.Pitch, Is.EqualTo(25f).Within(0.001f));

            yield return RightDragVertically(1100f, 100f);
            Assert.That(controller.Pitch, Is.EqualTo(85f).Within(0.001f));
        }

        [UnityTest]
        public IEnumerator SmallRightClick_DoesNotRotate()
        {
            Set(mouse.position, new Vector2(100f, 100f));
            yield return null;
            var startYaw = controller.Yaw;

            Press(mouse.rightButton);
            yield return null;
            Set(mouse.position, new Vector2(104f, 100f));
            yield return null;
            Release(mouse.rightButton);
            yield return null;

            Assert.That(controller.Yaw, Is.EqualTo(startYaw).Within(0.001f));
        }

        // --- Following the primary character (Phase 3).

        // A unit standing at (8, 0, 6), made the primary character with takeover on.
        CommandableUnit CreateDrivenUnit()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(8f, 0f, 6f));
            primaryCharacter.Initialize(unit, pause);
            primaryCharacter.SetTakeover(true);
            return unit;
        }

        float DistanceToRig(Component unit) =>
            TestWorld.HorizontalDistance(controller.transform.position, unit.transform.position);

        [UnityTest]
        public IEnumerator Driving_FollowsThePrimaryCharacter()
        {
            var unit = CreateDrivenUnit();
            yield return new WaitForSecondsRealtime(0.6f);

            Assert.That(DistanceToRig(unit), Is.LessThan(0.2f), "The camera did not follow the primary character");
            Assert.That(controller.transform.position.y, Is.EqualTo(0f).Within(0.001f), "Following must not lift the pivot");
        }

        [UnityTest]
        public IEnumerator Driving_IgnoresPan()
        {
            var unit = CreateDrivenUnit();
            yield return new WaitForSecondsRealtime(0.6f);

            Press(keyboard.wKey);
            yield return new WaitForSecondsRealtime(0.3f);
            Release(keyboard.wKey);
            yield return null;

            Assert.That(DistanceToRig(unit), Is.LessThan(0.2f), "W panned the camera away while driving");
        }

        [UnityTest]
        public IEnumerator PausedWithTakeoverOn_PansFreely()
        {
            CreateDrivenUnit();
            yield return new WaitForSecondsRealtime(0.6f);
            pause.Pause();
            var start = controller.transform.position;

            Press(keyboard.wKey);
            yield return new WaitForSecondsRealtime(0.2f);
            Release(keyboard.wKey);
            yield return null;

            Assert.That(controller.transform.position.z, Is.GreaterThan(start.z + 0.5f));
        }

        [UnityTest]
        public IEnumerator TakeoverOff_PansFreely_AndDoesNotFollow()
        {
            CreateDrivenUnit();
            primaryCharacter.SetTakeover(false);
            var start = controller.transform.position;

            Press(keyboard.wKey);
            yield return new WaitForSecondsRealtime(0.2f);
            Release(keyboard.wKey);
            yield return null;

            Assert.That(controller.transform.position.z, Is.GreaterThan(start.z + 0.5f));
            Assert.That(controller.transform.position.x, Is.EqualTo(start.x).Within(0.01f), "The camera drifted toward the unit");
        }

        [UnityTest]
        public IEnumerator Resume_EasesBackToThePrimaryCharacter()
        {
            var unit = CreateDrivenUnit();
            yield return new WaitForSecondsRealtime(0.6f);
            pause.Pause();
            Press(keyboard.sKey);
            yield return new WaitForSecondsRealtime(0.3f);
            Release(keyboard.sKey);
            yield return null;
            Assert.That(DistanceToRig(unit), Is.GreaterThan(1f), "Precondition: the camera panned away while paused");

            pause.Resume();
            yield return new WaitForSecondsRealtime(0.6f);

            Assert.That(DistanceToRig(unit), Is.LessThan(0.2f), "The camera did not return to the primary character");
        }
    }
}
#endif
