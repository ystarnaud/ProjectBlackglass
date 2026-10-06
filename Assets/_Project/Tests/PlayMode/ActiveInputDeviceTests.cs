#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.DualShock;
using UnityEngine.InputSystem.DualShock.LowLevel;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.Switch;
using UnityEngine.InputSystem.XInput;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class ActiveInputDeviceTests : InputTestFixture
    {
        Keyboard keyboard;
        Mouse mouse;
        InputActionAsset actions;
        GameObject host;
        ActiveInputDevice active;
        int changes;

        public override void Setup()
        {
            base.Setup();
            changes = 0; // the fixture instance is shared by every test
            keyboard = InputSystem.AddDevice<Keyboard>();
            mouse = InputSystem.AddDevice<Mouse>();
            actions = TestControls.Load();
            host = new GameObject("Systems");
            host.SetActive(false);
            active = host.AddComponent<ActiveInputDevice>();
            active.Initialize(actions);
            active.Changed += _ => changes++;
            host.SetActive(true);
        }

        public override void TearDown()
        {
            Object.DestroyImmediate(host);
            TestControls.Reset(actions);
            base.TearDown();
        }

        // Tapping View/Create (select) is the harmless "wake" input: it is unbound (Menu/Options/start pauses), so it only announces the device.
        IEnumerator Wake(Gamepad pad)
        {
            PressSelect(pad, true);
            yield return null;
            PressSelect(pad, false);
            yield return null;
        }

        // The DualSense layout's event pre-processor discards delta state events (a real pad only sends whole reports),
        // so Press/Set cannot drive it; these helpers queue a whole DualSense report for it instead.
        void PressSelect(Gamepad pad, bool down)
        {
            if (pad is DualSenseGamepadHID)
                InputSystem.QueueStateEvent(pad, DualSenseReport(Vector2.zero, down));
            else if (down)
                Press(pad.selectButton);
            else
                Release(pad.selectButton);
        }

        void SetLeftStick(Gamepad pad, Vector2 value)
        {
            if (pad is DualSenseGamepadHID)
                InputSystem.QueueStateEvent(pad, DualSenseReport(value, false));
            else
                Set(pad.leftStick, value);
        }

        static DualSenseHIDInputReport DualSenseReport(Vector2 leftStick, bool select) => new DualSenseHIDInputReport
        {
            leftStickX = (byte)Mathf.RoundToInt(127.5f + leftStick.x * 127.5f),
            leftStickY = (byte)Mathf.RoundToInt(127.5f - leftStick.y * 127.5f), // HID Y points down
            rightStickX = 128,
            rightStickY = 128,
            buttons0 = 8, // d-pad released
            buttons1 = (byte)(select ? 1 << 4 : 0), // Share, the layout's select button
        };

        IEnumerator WakeKeyboard()
        {
            Press(keyboard.f12Key);
            yield return null;
            Release(keyboard.f12Key);
            yield return null;
        }

        static InputBinding Mask(string group) => InputBinding.MaskByGroup(group);

        IEnumerator AssertWakes<T>(InputFamily family, ControllerModel model, string group) where T : Gamepad
        {
            var pad = InputSystem.AddDevice<T>();
            yield return Wake(pad);
            Assert.That(active.Family, Is.EqualTo(family));
            Assert.That(active.Model, Is.EqualTo(model));
            Assert.That(active.Device, Is.SameAs(pad));
            Assert.That(actions.bindingMask, Is.EqualTo((InputBinding?)Mask(group)));
            Assert.That(actions.devices.HasValue, Is.True);
            Assert.That(actions.devices.Value, Has.Count.EqualTo(1));
            Assert.That(actions.devices.Value[0], Is.SameAs(pad));
        }

        [Test]
        public void StartsAsKeyboardMouse_AndAppliesTheKeyboardMouseMask()
        {
            Assert.That(active.Family, Is.EqualTo(InputFamily.KeyboardMouse));
            Assert.That(active.Device, Is.Null);
            Assert.That(actions.bindingMask, Is.EqualTo((InputBinding?)Mask("KeyboardMouse")));
            Assert.That(actions.devices.HasValue, Is.False);
        }

        [UnityTest]
        public IEnumerator XboxController_SwitchesToXbox()
        {
            yield return AssertWakes<XInputController>(InputFamily.Xbox, ControllerModel.Xbox, "Xbox");
        }

        [UnityTest]
        public IEnumerator DualSense_SwitchesToPlayStation()
        {
            yield return AssertWakes<DualSenseGamepadHID>(InputFamily.PlayStation, ControllerModel.DualSense, "PlayStation");
        }

        [UnityTest]
        public IEnumerator SwitchPro_SwitchesToNintendo()
        {
            yield return AssertWakes<SwitchProControllerHID>(InputFamily.Nintendo, ControllerModel.SwitchPro, "Nintendo");
        }

        [UnityTest]
        public IEnumerator UnknownGamepad_FallsBackToTheGenericGroup()
        {
            yield return AssertWakes<Gamepad>(InputFamily.GenericGamepad, ControllerModel.Unknown, "Gamepad");
        }

        [UnityTest]
        public IEnumerator KeyboardAfterController_ReturnsToKeyboardMouse()
        {
            var pad = InputSystem.AddDevice<XInputController>();
            yield return Wake(pad);
            yield return WakeKeyboard();

            Assert.That(active.Family, Is.EqualTo(InputFamily.KeyboardMouse));
            Assert.That(active.Device, Is.Null);
            Assert.That(actions.bindingMask, Is.EqualTo((InputBinding?)Mask("KeyboardMouse")));
            Assert.That(actions.devices.HasValue, Is.False, "Keyboard/mouse is not restricted to one device");
        }

        [UnityTest]
        public IEnumerator MouseMovement_ReturnsToKeyboardMouse()
        {
            var pad = InputSystem.AddDevice<XInputController>();
            yield return Wake(pad);
            Move(mouse.position, new Vector2(300f, 200f));
            yield return null;

            Assert.That(active.Family, Is.EqualTo(InputFamily.KeyboardMouse));
        }

        [UnityTest]
        public IEnumerator TinyMouseJitter_DoesNotTakeTheFamilyBackFromAController()
        {
            var pad = InputSystem.AddDevice<XInputController>();
            yield return Wake(pad);
            Set(mouse.delta, new Vector2(1f, 1f));
            yield return null;

            Assert.That(active.Family, Is.EqualTo(InputFamily.Xbox));
        }

        [UnityTest]
        public IEnumerator StickDrift_NeverSwitches_ButAFirmDeflectionDoes()
        {
            var pad = InputSystem.AddDevice<XInputController>();
            foreach (var drift in new[] { new Vector2(0.1f, 0f), new Vector2(0.3f, 0.2f), new Vector2(0.4f, 0.3f), new Vector2(0.5f, 0.1f) })
            {
                Set(pad.leftStick, drift);
                yield return null;
                Set(pad.rightStick, -drift);
                yield return null;
                Assert.That(active.Family, Is.EqualTo(InputFamily.KeyboardMouse), $"Drift {drift} switched the family");
            }
            Assert.That(changes, Is.EqualTo(0));

            Set(pad.leftStick, new Vector2(0.9f, 0f));
            yield return null;
            Assert.That(active.Family, Is.EqualTo(InputFamily.Xbox));
        }

        [UnityTest]
        public IEnumerator HeldStick_DoesNotTakeTheFamilyBackAfterKeyboardUse()
        {
            var pad = InputSystem.AddDevice<Gamepad>();
            InputSystem.QueueStateEvent(pad, new GamepadState { leftStick = new Vector2(0.9f, 0f) });
            yield return null;
            Assert.That(active.Family, Is.EqualTo(InputFamily.GenericGamepad));

            yield return WakeKeyboard();
            Assert.That(active.Family, Is.EqualTo(InputFamily.KeyboardMouse));

            // The pad keeps reporting the deflected stick (full-state reports, as a real pad sends).
            for (var i = 0; i < 5; i++)
            {
                InputSystem.QueueStateEvent(pad, new GamepadState { leftStick = new Vector2(0.95f, 0.02f * i) });
                yield return null;
            }
            Assert.That(active.Family, Is.EqualTo(InputFamily.KeyboardMouse), "A held stick re-claimed the family");

            InputSystem.QueueStateEvent(pad, new GamepadState());
            yield return null;
            InputSystem.QueueStateEvent(pad, new GamepadState { leftStick = new Vector2(0.9f, 0f) });
            yield return null;
            Assert.That(active.Family, Is.EqualTo(InputFamily.GenericGamepad), "Releasing and pushing again is new input");
        }

        [UnityTest]
        public IEnumerator HeldButton_DoesNotTakeTheFamilyBackAfterKeyboardUse()
        {
            var pad = InputSystem.AddDevice<Gamepad>();
            InputSystem.QueueStateEvent(pad, new GamepadState(GamepadButton.South));
            yield return null;
            Assert.That(active.Family, Is.EqualTo(InputFamily.GenericGamepad));

            yield return WakeKeyboard();
            for (var i = 0; i < 5; i++)
            {
                InputSystem.QueueStateEvent(pad, new GamepadState(GamepadButton.South) { leftStick = new Vector2(0.01f * i, 0f) });
                yield return null;
            }
            Assert.That(active.Family, Is.EqualTo(InputFamily.KeyboardMouse), "A held button re-claimed the family");
        }

        [UnityTest]
        public IEnumerator TwoControllers_TheLastWithMeaningfulInputWins_AndOnlyItIsBound()
        {
            var first = InputSystem.AddDevice<XInputController>();
            var second = InputSystem.AddDevice<XInputController>();
            yield return Wake(first);
            Assert.That(actions.devices.Value[0], Is.SameAs(first));

            yield return Wake(second);
            Assert.That(active.Device, Is.SameAs(second));
            Assert.That(actions.devices.Value, Has.Count.EqualTo(1));
            Assert.That(actions.devices.Value[0], Is.SameAs(second));
            Assert.That(active.Family, Is.EqualTo(InputFamily.Xbox));
        }

        [UnityTest]
        public IEnumerator RemovingTheActiveController_FallsBackToKeyboardMouse()
        {
            var pad = InputSystem.AddDevice<DualSenseGamepadHID>();
            yield return Wake(pad);
            InputSystem.RemoveDevice(pad);
            yield return null;

            Assert.That(active.Family, Is.EqualTo(InputFamily.KeyboardMouse));
            Assert.That(active.Device, Is.Null);
            Assert.That(actions.bindingMask, Is.EqualTo((InputBinding?)Mask("KeyboardMouse")));
            Assert.That(actions.devices.HasValue, Is.False);
        }

        [UnityTest]
        public IEnumerator RemovingAnotherController_ChangesNothing()
        {
            var active1 = InputSystem.AddDevice<XInputController>();
            var other = InputSystem.AddDevice<XInputController>();
            yield return Wake(active1);
            InputSystem.RemoveDevice(other);
            yield return null;

            Assert.That(active.Family, Is.EqualTo(InputFamily.Xbox));
            Assert.That(active.Device, Is.SameAs(active1));
        }

        [UnityTest]
        public IEnumerator Changed_FiresOncePerChange_NotPerEvent()
        {
            var pad = InputSystem.AddDevice<XInputController>();
            yield return Wake(pad);
            Assert.That(changes, Is.EqualTo(1));
            yield return Wake(pad);
            Assert.That(changes, Is.EqualTo(1), "The same controller again is not a change");
            yield return WakeKeyboard();
            Assert.That(changes, Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator RepeatedSwitching_LeavesNoStuckInput_AndLogsNoErrors()
        {
            var xbox = InputSystem.AddDevice<XInputController>();
            var ps = InputSystem.AddDevice<DualSenseGamepadHID>();
            var move = actions.FindAction("Character/Move", true);
            move.Enable();

            for (var i = 0; i < 12; i++)
            {
                yield return Wake(i % 2 == 0 ? (Gamepad)xbox : ps);
                SetLeftStick(i % 2 == 0 ? xbox : (Gamepad)ps, new Vector2(0f, 0.9f));
                yield return null;
                yield return WakeKeyboard();
                SetLeftStick(xbox, Vector2.zero);
                SetLeftStick(ps, Vector2.zero);
                yield return null;
            }

            Assert.That(active.Family, Is.EqualTo(InputFamily.KeyboardMouse));
            Assert.That(move.ReadValue<Vector2>(), Is.EqualTo(Vector2.zero), "Move is stuck after repeated switching");
            Assert.That(actions.bindingMask, Is.EqualTo((InputBinding?)Mask("KeyboardMouse")));
        }

        [UnityTest]
        public IEnumerator SwitchingToAController_WhileAKeyIsHeld_ReleasesTheKeyCleanly()
        {
            var move = actions.FindAction("Character/Move", true);
            move.Enable();
            Press(keyboard.wKey);
            yield return null;
            Assert.That(move.ReadValue<Vector2>().y, Is.GreaterThan(0.5f));

            var pad = InputSystem.AddDevice<XInputController>();
            yield return Wake(pad);
            yield return null;
            Assert.That(move.ReadValue<Vector2>(), Is.EqualTo(Vector2.zero), "The held W still drives Move after the switch");

            Release(keyboard.wKey);
            yield return WakeKeyboard();
            Press(keyboard.wKey);
            yield return null;
            Assert.That(move.ReadValue<Vector2>().y, Is.GreaterThan(0.5f), "Keyboard control did not come back");
            Release(keyboard.wKey);
        }

        [UnityTest]
        public IEnumerator AfterWaking_TheControllerDrivesActions()
        {
            var pad = InputSystem.AddDevice<XInputController>();
            var confirm = actions.FindAction("Commands/Confirm", true);
            confirm.Enable();
            var performed = 0;
            confirm.performed += _ => performed++;

            yield return Wake(pad);
            var before = performed;
            Press(pad.buttonSouth);
            yield return null;
            Release(pad.buttonSouth);
            yield return null;

            Assert.That(performed - before, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator Nintendo_ConfirmIsTheEastButton_NotSouth()
        {
            var pad = InputSystem.AddDevice<SwitchProControllerHID>();
            var confirm = actions.FindAction("Commands/Confirm", true);
            var cancel = actions.FindAction("Commands/Cancel", true);
            confirm.Enable();
            cancel.Enable();
            var confirmed = 0;
            var cancelled = 0;
            confirm.performed += _ => confirmed++;
            cancel.performed += _ => cancelled++;

            yield return Wake(pad);
            Press(pad.buttonEast);
            yield return null;
            Release(pad.buttonEast);
            yield return null;
            Press(pad.buttonSouth);
            yield return null;
            Release(pad.buttonSouth);
            yield return null;

            Assert.That(confirmed, Is.EqualTo(1), "East confirms on a Nintendo controller");
            Assert.That(cancelled, Is.EqualTo(1), "South cancels on a Nintendo controller");
        }

        [UnityTest]
        public IEnumerator Disabling_ClearsTheMaskAndTheDeviceRestriction()
        {
            var pad = InputSystem.AddDevice<XInputController>();
            yield return Wake(pad);
            host.SetActive(false);
            yield return null;

            Assert.That(actions.bindingMask.HasValue, Is.False);
            Assert.That(actions.devices.HasValue, Is.False);
        }
    }
}
#endif
