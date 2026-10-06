#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CameraGamepadTests : InputTestFixture
    {
        Gamepad pad;
        TestWorld world;
        InputActionAsset actions;
        TacticalCameraController controller;
        TacticalPause pause;
        ActiveCharacter activeCharacter;

        public override void Setup()
        {
            base.Setup();
            pad = InputSystem.AddDevice<Gamepad>();
            world = new TestWorld();
            actions = TestControls.Load();
            TestControls.UseGroup(actions, "Gamepad");

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
                activeCharacter,
                TestControls.Ref(actions, "Camera/Look"),
                TestControls.Ref(actions, "Camera/CameraModifier"));
            rig.SetActive(true);
            pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
        }

        public override void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
            TestControls.Reset(actions);
            base.TearDown();
        }

        void Drive()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(Vector3.zero);
            activeCharacter.Initialize(unit, pause);
            activeCharacter.SetTakeover(true);
        }

        IEnumerator HoldStick(Vector2 value, float seconds)
        {
            Set(pad.rightStick, value);
            yield return new WaitForSecondsRealtime(seconds);
            Set(pad.rightStick, Vector2.zero);
            yield return null;
        }

        [UnityTest]
        public IEnumerator RightStickX_WhileDriving_RotatesTheCamera()
        {
            Drive();
            var start = controller.Yaw;
            yield return HoldStick(new Vector2(1f, 0f), 0.3f);

            Assert.That(Mathf.Abs(Mathf.DeltaAngle(start, controller.Yaw)), Is.GreaterThan(15f));
        }

        [UnityTest]
        public IEnumerator RightStickUp_WhileDriving_TiltsTowardTheHorizon()
        {
            Drive();
            var start = controller.Pitch;
            yield return HoldStick(new Vector2(0f, 1f), 0.3f);

            Assert.That(controller.Pitch, Is.LessThan(start - 5f));
        }

        void PauseWithoutAUnit()
        {
            activeCharacter.Initialize(null, pause);
            pause.Pause();
        }

        [UnityTest]
        public IEnumerator RightStickX_InRealTimeWithTakeoverOff_RotatesTheCamera()
        {
            var start = controller.Yaw;
            yield return HoldStick(new Vector2(1f, 0f), 0.3f);

            Assert.That(Mathf.Abs(Mathf.DeltaAngle(start, controller.Yaw)), Is.GreaterThan(15f));
        }

        [UnityTest]
        public IEnumerator RightStick_InRealTimeWithTakeoverOff_AndRightTriggerHeld_HandsTheStickToTheCursor()
        {
            var start = controller.Yaw;
            Press(pad.rightTrigger);
            yield return null;
            yield return HoldStick(new Vector2(1f, 0f), 0.3f);
            Release(pad.rightTrigger);
            yield return null;

            Assert.That(controller.Yaw, Is.EqualTo(start).Within(0.01f));
        }

        [UnityTest]
        public IEnumerator RightStick_WhilePaused_LeavesTheCameraAlone()
        {
            PauseWithoutAUnit();
            var start = controller.Yaw;
            var startPitch = controller.Pitch;
            yield return HoldStick(new Vector2(1f, 1f), 0.3f);

            Assert.That(controller.Yaw, Is.EqualTo(start).Within(0.01f));
            Assert.That(controller.Pitch, Is.EqualTo(startPitch).Within(0.01f));
        }

        [UnityTest]
        public IEnumerator RightTriggerHeld_WhilePaused_LetsTheRightStickRotateTheCamera()
        {
            PauseWithoutAUnit();
            var start = controller.Yaw;
            Press(pad.rightTrigger);
            yield return null;
            yield return HoldStick(new Vector2(1f, 0f), 0.3f);
            Release(pad.rightTrigger);
            yield return null;

            Assert.That(Mathf.Abs(Mathf.DeltaAngle(start, controller.Yaw)), Is.GreaterThan(15f));
        }

        [UnityTest]
        public IEnumerator RightTriggerHeld_WhilePaused_StillRotates_OnUnscaledTime()
        {
            Drive();
            pause.Pause();
            var start = controller.Yaw;
            Press(pad.rightTrigger);
            yield return null;
            yield return HoldStick(new Vector2(1f, 0f), 0.3f);
            Release(pad.rightTrigger);
            yield return null;

            Assert.That(Mathf.Abs(Mathf.DeltaAngle(start, controller.Yaw)), Is.GreaterThan(15f));
        }

        [UnityTest]
        public IEnumerator RightTriggerHeld_WhileDriving_HandsTheStickToTheCursor_SoTheCameraStays()
        {
            Drive();
            var start = controller.Yaw;
            Press(pad.rightTrigger);
            yield return null;
            yield return HoldStick(new Vector2(1f, 0f), 0.3f);
            Release(pad.rightTrigger);
            yield return null;

            Assert.That(controller.Yaw, Is.EqualTo(start).Within(0.01f));
        }

        [UnityTest]
        public IEnumerator StickDrift_BelowTheDeadZone_DoesNotRotateOrTilt()
        {
            Drive();
            var start = controller.Yaw;
            var startPitch = controller.Pitch;
            yield return HoldStick(new Vector2(0.12f, 0.1f), 0.4f);

            Assert.That(controller.Yaw, Is.EqualTo(start).Within(0.01f));
            Assert.That(controller.Pitch, Is.EqualTo(startPitch).Within(0.01f));
        }

        [UnityTest]
        public IEnumerator LeftStick_PansTheCamera_InProportionToTheDeflection()
        {
            var start = controller.transform.position;
            Set(pad.leftStick, new Vector2(0f, 1f));
            yield return new WaitForSecondsRealtime(0.25f);
            Set(pad.leftStick, Vector2.zero);
            yield return null;
            var full = controller.transform.position.z - start.z;

            var mid = controller.transform.position;
            Set(pad.leftStick, new Vector2(0f, 0.6f));
            yield return new WaitForSecondsRealtime(0.25f);
            Set(pad.leftStick, Vector2.zero);
            yield return null;
            var partial = controller.transform.position.z - mid.z;

            Assert.That(full, Is.GreaterThan(0.5f));
            Assert.That(partial, Is.GreaterThan(0.1f));
            Assert.That(partial, Is.LessThan(full * 0.8f), "Half deflection should pan clearly slower than full");
        }

        [UnityTest]
        public IEnumerator LeftStick_WhileDriving_DoesNotPan()
        {
            Drive();
            yield return new WaitForSecondsRealtime(0.6f);
            var start = controller.transform.position;
            Set(pad.leftStick, new Vector2(1f, 0f));
            yield return new WaitForSecondsRealtime(0.3f);
            Set(pad.leftStick, Vector2.zero);
            yield return null;

            Assert.That(TestWorld.HorizontalDistance(controller.transform.position, start), Is.LessThan(0.3f));
        }

        [UnityTest]
        public IEnumerator StickClicks_ZoomOutAndIn()
        {
            var start = controller.Distance;
            Press(pad.leftStickButton);
            yield return null;
            Release(pad.leftStickButton);
            yield return null;
            var zoomedOut = controller.Distance;
            Press(pad.rightStickButton);
            yield return null;
            Release(pad.rightStickButton);
            yield return null;

            Assert.That(zoomedOut, Is.GreaterThan(start), "L3 zooms out");
            Assert.That(controller.Distance, Is.LessThan(zoomedOut), "R3 zooms in");
        }
    }
}
#endif
