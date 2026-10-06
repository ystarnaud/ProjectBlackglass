#if UNITY_EDITOR
using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.DualShock;
using UnityEngine.InputSystem.DualShock.LowLevel;
using UnityEngine.InputSystem.Switch;
using UnityEngine.InputSystem.XInput;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class DirectControlGamepadTests : InputTestFixture
    {
        enum PadButton { North, DpadUp, RightShoulder }

        Keyboard keyboard;
        Gamepad pad;
        InputActionAsset actions;
        TestWorld world;
        CommandableUnit first;
        CommandableUnit second;
        CommandableUnit third;
        UnitSelection selection;
        TacticalPause pause;
        ActiveCharacter active;

        public override void Setup()
        {
            base.Setup();
            keyboard = InputSystem.AddDevice<Keyboard>();
            pad = InputSystem.AddDevice<XInputController>();
            world = new TestWorld();
            world.CreateEnvironment();
            first = world.CreateFriendlyFighter(new Vector3(-6f, 0f, -6f)).Unit;
            second = world.CreateFriendlyFighter(new Vector3(6f, 0f, -6f)).Unit;
            third = world.CreateFriendlyFighter(new Vector3(0f, 0f, -12f)).Unit;

            var cameraObject = world.Track(new GameObject("Camera"));
            cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 25f, -20f), Quaternion.Euler(50f, 0f, 0f));
            var viewCamera = cameraObject.AddComponent<Camera>();

            actions = TestControls.Load();
            TestControls.UseGroup(actions, "Xbox");
            var systems = world.Track(new GameObject("Systems"));
            systems.SetActive(false);
            pause = systems.AddComponent<TacticalPause>();
            selection = systems.AddComponent<UnitSelection>();
            selection.Initialize(first.GetComponent<SelectableUnit>(), second.GetComponent<SelectableUnit>(),
                third.GetComponent<SelectableUnit>());
            active = systems.AddComponent<ActiveCharacter>();
            active.Initialize(first, pause, selection);
            var input = systems.AddComponent<DirectControlInput>();
            input.Initialize(active, viewCamera,
                TestControls.Ref(actions, "Character/Move"),
                TestControls.Ref(actions, "Character/ToggleCharacterControl"),
                selection,
                TestControls.Ref(actions, "Character/NextCharacter"),
                TestControls.Ref(actions, "Character/CycleReverse"),
                TestControls.Ref(actions, "Character/ToggleFollow"),
                TestControls.Ref(actions, "Character/PreviousCharacter"));
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

        // A simulated DualSense discards delta state events (its event pre-processor only accepts whole reports, as a
        // real pad sends), so Press/Release never reach it; the same button is pressed with a whole report instead.
        IEnumerator Tap(Gamepad device, PadButton button)
        {
            if (device is DualSenseGamepadHID)
            {
                InputSystem.QueueStateEvent(device, DualSenseReport(button));
                yield return null;
                InputSystem.QueueStateEvent(device, DualSenseReport(null));
                yield return null;
            }
            else
            {
                yield return Tap(button switch
                {
                    PadButton.North => device.buttonNorth,
                    PadButton.DpadUp => device.dpad.up,
                    _ => device.rightShoulder,
                });
            }
        }

        static DualSenseHIDInputReport DualSenseReport(PadButton? pressed) => new DualSenseHIDInputReport
        {
            leftStickX = 128,
            leftStickY = 128,
            rightStickX = 128,
            rightStickY = 128,
            buttons0 = (byte)((pressed == PadButton.DpadUp ? 0 : 8) // the hat: 0 is up, 8 is released
                | (pressed == PadButton.North ? 1 << 7 : 0)), // Triangle
            buttons1 = (byte)(pressed == PadButton.RightShoulder ? 1 << 1 : 0), // R1
        };

        [UnityTest]
        public IEnumerator LeftStick_IsAnalog_HalfDeflectionIsSlowerThanFull()
        {
            active.SetTakeover(true);
            yield return null;

            Set(pad.leftStick, new Vector2(0f, 0.6f));
            yield return null;
            var half = first.MoveIntent;
            Set(pad.leftStick, new Vector2(0f, 1f));
            yield return null;
            var full = first.MoveIntent;

            Assert.That(half.z, Is.GreaterThan(0.3f).And.LessThan(0.8f), $"Half deflection gave {half}");
            Assert.That(full.magnitude, Is.EqualTo(1f).Within(0.02f));
            Assert.That(half.magnitude, Is.LessThan(full.magnitude), "Stick magnitude must not be flattened to 1");
        }

        [UnityTest]
        public IEnumerator LeftStick_Diagonal_KeepsItsDirectionAndNeverExceedsOne()
        {
            active.SetTakeover(true);
            yield return null;
            Set(pad.leftStick, new Vector2(1f, 1f));
            yield return null;

            Assert.That(first.MoveIntent.magnitude, Is.LessThanOrEqualTo(1.0001f));
            Assert.That(first.MoveIntent.x, Is.GreaterThan(0.3f));
            Assert.That(first.MoveIntent.z, Is.GreaterThan(0.3f));
        }

        [UnityTest]
        public IEnumerator StickDrift_DoesNotMoveTheCharacter()
        {
            active.SetTakeover(true);
            yield return null;
            Set(pad.leftStick, new Vector2(0.12f, 0.1f));
            yield return new WaitForSecondsRealtime(0.2f);

            Assert.That(first.MoveIntent, Is.EqualTo(Vector3.zero));
            Assert.That(first.transform.position.x, Is.EqualTo(-6f).Within(0.05f));
        }

        [UnityTest]
        public IEnumerator NextAndPreviousCharacter_CycleLikeTabAndShiftTab_AndWrap()
        {
            yield return Tap(pad.rightShoulder);
            Assert.That(active.Unit, Is.SameAs(second));
            Assert.That(selection.Selected, Has.Count.EqualTo(1));
            Assert.That(selection.Selected[0].Unit, Is.SameAs(second), "Switching selects the new active character");

            yield return Tap(pad.leftShoulder);
            Assert.That(active.Unit, Is.SameAs(first));
            yield return Tap(pad.leftShoulder);
            Assert.That(active.Unit, Is.SameAs(third), "Previous wraps from the first to the last");
            yield return Tap(pad.rightShoulder);
            Assert.That(active.Unit, Is.SameAs(first), "Next wraps from the last to the first");
        }

        [UnityTest]
        public IEnumerator NextCharacter_SkipsADeadCharacter()
        {
            second.GetComponent<Health>().TakeDamage(1000);
            yield return null;
            yield return Tap(pad.rightShoulder);

            Assert.That(active.Unit, Is.SameAs(third));
        }

        [UnityTest]
        public IEnumerator SwitchingCharacter_KeepsTheOldCharactersQueuedOrders()
        {
            var order = new MoveCommand(new Vector3(-6f, 0f, 2f));
            Assert.That(first.Issue(order), Is.True);
            yield return Tap(pad.rightShoulder);

            Assert.That(first.CurrentCommand, Is.SameAs(order));
        }

        [UnityTest]
        public IEnumerator ToggleCharacterControl_AndToggleFollow_WorkWhilePaused()
        {
            pause.Pause();
            Assert.That(active.IsTakeoverOn, Is.False);
            yield return Tap(pad.buttonNorth);
            Assert.That(active.IsTakeoverOn, Is.True);

            var follow = active.IsFollowOn;
            yield return Tap(pad.dpad.up);
            Assert.That(active.IsFollowOn, Is.EqualTo(!follow));
            Assert.That(pause.IsPaused, Is.True, "Controller toggles must not resume the game");
        }

        [UnityTest]
        public IEnumerator EveryFamily_TogglesControlAndFollow_FromItsOwnBindingGroup()
        {
            var families = new (Func<Gamepad> create, string group)[]
            {
                (() => InputSystem.AddDevice<DualSenseGamepadHID>(), "PlayStation"),
                (() => InputSystem.AddDevice<SwitchProControllerHID>(), "Nintendo"),
                (() => InputSystem.AddDevice<Gamepad>(), "Gamepad"),
            };
            foreach (var (create, group) in families)
            {
                var other = create();
                TestControls.UseGroup(actions, group);
                actions.devices = new InputDevice[] { other };
                yield return null;

                var control = active.IsTakeoverOn;
                yield return Tap(other, PadButton.North);
                Assert.That(active.IsTakeoverOn, Is.EqualTo(!control), $"{group}: north toggles character control");

                var follow = active.IsFollowOn;
                yield return Tap(other, PadButton.DpadUp);
                Assert.That(active.IsFollowOn, Is.EqualTo(!follow), $"{group}: D-pad up toggles follow");

                var before = active.Unit;
                yield return Tap(other, PadButton.RightShoulder);
                Assert.That(active.Unit, Is.Not.SameAs(before), $"{group}: right shoulder switches character");
            }
        }

        [UnityTest]
        public IEnumerator SwitchingToKeyboardMouseWhileTheStickIsHeld_LeavesNoMoveIntent_AndWasdWorks()
        {
            active.SetTakeover(true);
            yield return null;
            Set(pad.leftStick, new Vector2(0f, 1f));
            yield return null;
            Assert.That(first.MoveIntent.magnitude, Is.GreaterThan(0.9f));

            TestControls.UseGroup(actions, "KeyboardMouse");
            actions.devices = null;
            yield return null;
            yield return null;
            Assert.That(first.MoveIntent, Is.EqualTo(Vector3.zero), "The held stick still steers after the family changed");

            Set(pad.leftStick, Vector2.zero);
            yield return null;
            Press(keyboard.wKey);
            yield return null;
            yield return null;
            Assert.That(first.MoveIntent.magnitude, Is.GreaterThan(0.9f), "WASD does not drive after the switch");
            Release(keyboard.wKey);
            yield return null;
        }
    }
}
#endif
