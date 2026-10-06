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
        ActiveCharacter activeCharacter;

        public override void Setup()
        {
            base.Setup();
            keyboard = InputSystem.AddDevice<Keyboard>();
            mouse = InputSystem.AddDevice<Mouse>();
            world = new TestWorld();

            var actions = TestControls.Load();
            // No unit yet: the camera behaves as before until a test gives the primary character a unit.
            activeCharacter = world.Track(new GameObject("Player")).AddComponent<ActiveCharacter>();
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
                activeCharacter);
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
            activeCharacter.Initialize(unit, pause);
            activeCharacter.SetTakeover(true);
            return unit;
        }

        float DistanceToRig(Component unit) =>
            TestWorld.HorizontalDistance(controller.transform.position, unit.transform.position);

        [UnityTest]
        public IEnumerator Driving_FollowsTheActiveCharacter()
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
            activeCharacter.SetTakeover(false);
            var start = controller.transform.position;

            Press(keyboard.wKey);
            yield return new WaitForSecondsRealtime(0.2f);
            Release(keyboard.wKey);
            yield return null;

            Assert.That(controller.transform.position.z, Is.GreaterThan(start.z + 0.5f));
            Assert.That(controller.transform.position.x, Is.EqualTo(start.x).Within(0.01f), "The camera drifted toward the unit");
        }

        [UnityTest]
        public IEnumerator Resume_EasesBackToTheActiveCharacter()
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

        // --- Focusing a newly active character.

        [UnityTest]
        public IEnumerator SwitchingWhilePaused_GlidesToTheNewCharacter()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(8f, 0f, 6f));
            pause.Pause();
            activeCharacter.Initialize(unit, pause);
            yield return new WaitForSecondsRealtime(1f);

            Assert.That(DistanceToRig(unit), Is.LessThan(0.1f), "The camera did not focus the new active character");
        }

        [UnityTest]
        public IEnumerator PanInput_CancelsTheGlide()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(8f, 0f, 6f));
            pause.Pause();
            activeCharacter.Initialize(unit, pause);
            yield return null;   // the glide starts
            Press(keyboard.sKey);
            yield return new WaitForSecondsRealtime(0.2f);
            Release(keyboard.sKey);
            yield return null;
            var afterPan = controller.transform.position;

            yield return new WaitForSecondsRealtime(0.6f);
            Assert.That(TestWorld.HorizontalDistance(controller.transform.position, afterPan), Is.LessThan(0.01f),
                "The glide carried on after the player panned");
            Assert.That(DistanceToRig(unit), Is.GreaterThan(1f));
        }

        [UnityTest]
        public IEnumerator EnablingTheCamera_DoesNotGlideToTheCurrentCharacter()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(8f, 0f, 6f));
            controller.gameObject.SetActive(false);
            activeCharacter.Initialize(unit, pause);
            controller.gameObject.SetActive(true);
            var start = controller.transform.position;

            yield return new WaitForSecondsRealtime(0.5f);
            Assert.That(TestWorld.HorizontalDistance(controller.transform.position, start), Is.LessThan(0.01f),
                "The camera moved to the character that was already active when it started");
        }

        [UnityTest]
        public IEnumerator SwitchWhileDriving_FollowsTheNewCharacter()
        {
            CreateDrivenUnit();
            var second = world.CreateUnit(new Vector3(-8f, 0f, -6f));
            yield return new WaitForSecondsRealtime(0.6f);

            activeCharacter.SetUnit(second);
            yield return new WaitForSecondsRealtime(0.8f);
            Assert.That(DistanceToRig(second), Is.LessThan(0.2f), "The camera did not follow the new active character");
        }

        [UnityTest]
        public IEnumerator RightDragDuringAGlide_StillRotates()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(8f, 0f, 6f));
            pause.Pause();
            Set(mouse.position, new Vector2(100f, 100f));
            yield return null;
            var startYaw = controller.Yaw;

            activeCharacter.Initialize(unit, pause);
            Press(mouse.rightButton);
            yield return null;
            Set(mouse.position, new Vector2(110f, 100f));   // crosses the 6 px threshold, no rotation yet
            yield return null;
            Set(mouse.position, new Vector2(210f, 100f));   // +100 px of drag
            yield return null;
            Release(mouse.rightButton);
            yield return new WaitForSecondsRealtime(1f);

            Assert.That(Mathf.DeltaAngle(startYaw, controller.Yaw), Is.EqualTo(25f).Within(0.5f), "Right-drag did not rotate during the glide");
            Assert.That(DistanceToRig(unit), Is.LessThan(0.1f), "Rotating stopped the glide");
        }

        [UnityTest]
        public IEnumerator GlideToAUnitOutsideTheBounds_StopsAtTheEdge()
        {
            var settings = new UnityEditor.SerializedObject(controller);
            settings.FindProperty("boundsHalfSize").floatValue = 5f;
            settings.ApplyModifiedPropertiesWithoutUndo();
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(8f, 0f, 6f));
            activeCharacter.Initialize(unit, pause);

            yield return new WaitForSecondsRealtime(1f);
            var position = controller.transform.position;
            Assert.That(position.x, Is.EqualTo(5f).Within(0.05f));
            Assert.That(position.z, Is.EqualTo(5f).Within(0.05f));
        }

        [UnityTest]
        public IEnumerator DestroyingTheGlideTarget_LogsNoErrors_AndKeepsTheCameraWorking()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(8f, 0f, 6f));
            pause.Pause();
            activeCharacter.Initialize(unit, pause);
            yield return null;   // the glide starts
            var startYaw = controller.Yaw;

            Object.Destroy(unit.gameObject);
            Press(keyboard.eKey);
            yield return new WaitForSecondsRealtime(0.3f);
            Release(keyboard.eKey);
            yield return null;

            Assert.That(Mathf.DeltaAngle(startYaw, controller.Yaw), Is.GreaterThan(5f), "Q/E stopped working after the glide target was destroyed");
            // Any error or exception logged meanwhile fails the test.
        }

        [UnityTest]
        public IEnumerator GlideToAWalkingCharacter_SettlesOnce_AndDoesNotFollowIt()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(8f, 0f, 6f));
            Assert.That(unit.Issue(new MoveCommand(new Vector3(-12f, 0f, 6f))), Is.True);
            yield return new WaitForSecondsRealtime(0.2f);   // walking by the time it becomes active

            activeCharacter.Initialize(unit, pause);   // free mode: takeover off
            yield return new WaitForSecondsRealtime(1f);
            var settled = controller.transform.position;
            var unitAtSettle = unit.transform.position;
            yield return new WaitForSecondsRealtime(0.6f);

            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, unitAtSettle), Is.GreaterThan(1f), "Precondition: the character is still walking");
            Assert.That(TestWorld.HorizontalDistance(controller.transform.position, settled), Is.LessThan(0.01f),
                "In free mode the camera kept following the character after its one glide");
        }

        [UnityTest]
        public IEnumerator SetBounds_ClampsPanningToTheGivenRectangle()
        {
            controller.SetBounds(new Rect(-2f, -2f, 4f, 4f));
            Press(keyboard.wKey);
            yield return new WaitForSecondsRealtime(0.6f);
            Release(keyboard.wKey);
            yield return null;

            Assert.That(controller.transform.position.z, Is.EqualTo(2f).Within(0.01f));
        }

        [UnityTest]
        public IEnumerator FocusOn_SnapsAtOnce_ClampedToTheBounds_AndKeepsTheHeight()
        {
            controller.SetBounds(new Rect(10f, 10f, 20f, 20f));
            var height = controller.transform.position.y;
            controller.FocusOn(new Vector3(100f, 0f, 15f));
            var snapped = controller.transform.position;
            yield return null;

            Assert.That(snapped.x, Is.EqualTo(30f).Within(0.001f));
            Assert.That(snapped.z, Is.EqualTo(15f).Within(0.001f));
            Assert.That(snapped.y, Is.EqualTo(height).Within(0.001f));
            Assert.That(controller.transform.position, Is.EqualTo(snapped), "no glide after a snap");
        }

        [UnityTest]
        public IEnumerator WithoutSetBounds_TheOriginalSquareStillApplies()
        {
            Press(keyboard.wKey);
            yield return new WaitForSecondsRealtime(2.8f);
            Release(keyboard.wKey);
            yield return null;

            Assert.That(controller.transform.position.z, Is.EqualTo(25f).Within(0.01f));
        }
    }
}
#endif
