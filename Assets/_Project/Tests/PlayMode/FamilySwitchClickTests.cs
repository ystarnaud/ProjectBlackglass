#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.XInput;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>
    /// Changing the active input family re-resolves the asset's bindings, which cancels actions that are in progress.
    /// A left mouse button that is still held must not be taken for a release (and so for a click) by that cancel.
    /// </summary>
    public class FamilySwitchClickTests : InputTestFixture
    {
        static readonly Vector3 GroundPoint = new Vector3(2f, 0f, 2f);

        Keyboard keyboard;
        Mouse mouse;
        Gamepad pad;
        InputActionAsset actions;
        TestWorld world;
        Camera viewCamera;
        SelectableUnit unit;
        UnitSelection selection;

        public override void Setup()
        {
            base.Setup();
            keyboard = InputSystem.AddDevice<Keyboard>();
            mouse = InputSystem.AddDevice<Mouse>();
            pad = InputSystem.AddDevice<XInputController>();
            actions = TestControls.Load();
            world = new TestWorld();
            world.CreateEnvironment();
            unit = world.CreateFriendly(new Vector3(-8f, 0f, -6f));

            var cameraObject = world.Track(new GameObject("Camera"));
            cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 25f, -20f), Quaternion.Euler(50f, 0f, 0f));
            viewCamera = cameraObject.AddComponent<Camera>();

            var systems = world.Track(new GameObject("Systems"));
            systems.SetActive(false);
            var pause = systems.AddComponent<TacticalPause>();
            selection = systems.AddComponent<UnitSelection>();
            selection.Initialize(unit);
            var active = systems.AddComponent<ActiveCharacter>();
            active.Initialize(null, pause);
            var device = systems.AddComponent<ActiveInputDevice>();
            device.Initialize(actions);
            var input = systems.AddComponent<PlayerCommandInput>();
            input.Initialize(viewCamera, selection, pause,
                TestControls.Ref(actions, "Commands/Command"),
                TestControls.Ref(actions, "Commands/PointerPosition"),
                TestControls.Ref(actions, "Commands/ToggleTacticalPause"),
                TestControls.Ref(actions, "Commands/QueueModifier"),
                TestControls.Ref(actions, "Commands/Stop"),
                TestControls.Ref(actions, "Commands/Cancel"),
                active);
            systems.SetActive(true);
        }

        public override void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
            TestControls.Reset(actions);
            base.TearDown();
        }

        IEnumerator Tap(ButtonControl button)
        {
            Press(button);
            yield return null;
            Release(button);
            yield return null;
        }

        [UnityTest]
        public IEnumerator SwitchingToAPad_WhileTheLeftButtonIsHeld_IssuesNoOrder_AndANormalClickStillDoes()
        {
            selection.Select(unit);
            yield return null;
            Set(mouse.position, (Vector2)viewCamera.WorldToScreenPoint(GroundPoint));
            yield return null;
            Press(mouse.leftButton);
            yield return null;

            yield return Tap(pad.startButton); // wakes the pad: the bindings are re-resolved on the next frame
            yield return null;
            yield return null;
            Assert.That(unit.Unit.CurrentCommand, Is.Null, "The cancel caused by the switch is not a click");

            Release(mouse.leftButton);
            yield return null;
            Assert.That(unit.Unit.CurrentCommand, Is.Null, "Releasing the button afterwards is not a click either");

            yield return Tap(keyboard.f12Key); // back to keyboard/mouse
            yield return null;
            Press(mouse.leftButton);
            yield return null;
            Release(mouse.leftButton);
            yield return null;
            Assert.That(unit.Unit.CurrentCommand, Is.TypeOf<MoveCommand>(), "A normal click still orders");
        }

        [UnityTest]
        public IEnumerator SwitchingBetweenKeyboardMouseAndAPad_WithoutAHeldButton_IssuesNoOrder()
        {
            selection.Select(unit);
            Set(mouse.position, (Vector2)viewCamera.WorldToScreenPoint(GroundPoint));
            yield return Tap(pad.startButton);
            yield return null;
            yield return Tap(keyboard.f12Key);
            yield return null;

            Assert.That(unit.Unit.CurrentCommand, Is.Null);
        }
    }
}
#endif
