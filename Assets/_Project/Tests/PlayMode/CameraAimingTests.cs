#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CameraAimingTests : InputTestFixture
    {
        Gamepad pad;
        TestWorld world;
        InputActionAsset actions;
        TacticalCameraController controller;
        TacticalCursor cursor;

        public override void Setup()
        {
            base.Setup();
            pad = InputSystem.AddDevice<Gamepad>();
            world = new TestWorld();
            actions = TestControls.Load();
            TestControls.UseGroup(actions, "Gamepad");

            var pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            var activeCharacter = world.Track(new GameObject("Player")).AddComponent<ActiveCharacter>();
            // Only its Aiming flag is read by the camera; it needs no scene wiring.
            cursor = world.Track(new GameObject("Cursor")).AddComponent<TacticalCursor>();
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
                TestControls.Ref(actions, "Camera/CameraModifier"),
                cursor);
            rig.SetActive(true);
            world.CreateEnvironment();
            activeCharacter.Initialize(world.CreateUnit(Vector3.zero), pause);
        }

        public override void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
            TestControls.Reset(actions);
            base.TearDown();
        }

        IEnumerator HoldStick(Vector2 value, float seconds)
        {
            Set(pad.rightStick, value);
            yield return new WaitForSecondsRealtime(seconds);
            Set(pad.rightStick, Vector2.zero);
            yield return null;
        }

        [UnityTest]
        public IEnumerator WhileAnAbilityIsArmed_TheRightStickDoesNotRotateTheCamera()
        {
            var start = controller.Yaw;
            cursor.Aiming = true;
            yield return HoldStick(new Vector2(1f, 0f), 0.3f);

            Assert.That(controller.Yaw, Is.EqualTo(start).Within(0.01f));
        }

        [UnityTest]
        public IEnumerator OnceDisarmed_TheRightStickRotatesTheCameraAgain()
        {
            cursor.Aiming = true;
            yield return null;
            cursor.Aiming = false;
            var start = controller.Yaw;
            yield return HoldStick(new Vector2(1f, 0f), 0.3f);

            Assert.That(Mathf.Abs(Mathf.DeltaAngle(start, controller.Yaw)), Is.GreaterThan(15f));
        }
    }
}
#endif
