#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.DualShock;
using UnityEngine.InputSystem.DualShock.LowLevel;
using UnityEngine.InputSystem.Switch;
using UnityEngine.InputSystem.XInput;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class PrototypeSceneControllerTests : InputTestFixture
    {
        // The pad buttons whose simulation has to work on every family, including the DualSense.
        enum PadKey { Start, Select, South, East }

        Keyboard keyboard;
        Mouse mouse;
        InputActionAsset actions;
        CommandableUnit[] squad;
        ActiveInputDevice family;
        TacticalCursor cursor;
        TacticalPause pause;
        UnitSelection selection;
        ActiveCharacter active;
        Camera viewCamera;
        CoverRegistry registry;
        Vector3 orderPoint;

        public override void Setup()
        {
            base.Setup();
            orderPoint = Vector3.zero; // the fixture instance is shared by every test
            keyboard = InputSystem.AddDevice<Keyboard>();
            mouse = InputSystem.AddDevice<Mouse>();
            actions = TestControls.Load();
        }

        public override void TearDown()
        {
            Time.timeScale = 1f;
            PrototypeSceneTests.DestroySceneObjects();
            TestControls.Reset(actions);
            base.TearDown();
        }

        // Loaded from each test, after InputTestFixture has isolated the input system (see PrototypeSceneTests).
        IEnumerator LoadScene()
        {
            yield return SceneManager.LoadSceneAsync("Prototype", LoadSceneMode.Single);
            yield return null;
            squad = PrototypeSceneTests.FindSquad();
            family = Object.FindFirstObjectByType<ActiveInputDevice>();
            cursor = Object.FindFirstObjectByType<TacticalCursor>();
            pause = Object.FindFirstObjectByType<TacticalPause>();
            selection = Object.FindFirstObjectByType<UnitSelection>();
            active = Object.FindFirstObjectByType<ActiveCharacter>();
            viewCamera = Object.FindFirstObjectByType<Camera>();
            registry = Object.FindFirstObjectByType<CoverRegistry>();
            Assert.That(family, Is.Not.Null, "The scene has no ActiveInputDevice: run the scene builder");
            Assert.That(cursor, Is.Not.Null, "The scene has no TacticalCursor: run the scene builder");
        }

        // A simulated DualSense discards delta state events (its event pre-processor only accepts whole reports, as a
        // real pad sends), so Press/Release/Set never reach it; these helpers queue a whole report for it instead.
        // Only one key or the stick is ever held at a time in these tests, so the report carries just that.
        void SetKey(Gamepad pad, PadKey key, bool down)
        {
            if (pad is DualSenseGamepadHID)
            {
                InputSystem.QueueStateEvent(pad, DualSenseReport(Vector2.zero, down ? key : null));
                return;
            }

            var button = key switch
            {
                PadKey.Start => pad.startButton,
                PadKey.Select => pad.selectButton,
                PadKey.South => pad.buttonSouth,
                _ => pad.buttonEast,
            };
            if (down)
                Press(button);
            else
                Release(button);
        }

        void SetLeftStick(Gamepad pad, Vector2 value)
        {
            if (pad is DualSenseGamepadHID)
                InputSystem.QueueStateEvent(pad, DualSenseReport(value, null));
            else
                Set(pad.leftStick, value);
        }

        static DualSenseHIDInputReport DualSenseReport(Vector2 leftStick, PadKey? pressed) => new DualSenseHIDInputReport
        {
            leftStickX = (byte)Mathf.RoundToInt(127.5f + leftStick.x * 127.5f),
            leftStickY = (byte)Mathf.RoundToInt(127.5f - leftStick.y * 127.5f), // HID Y points down
            rightStickX = 128,
            rightStickY = 128,
            buttons0 = (byte)(8 // the hat: 8 is released
                | (pressed == PadKey.South ? 1 << 5 : 0) // Cross
                | (pressed == PadKey.East ? 1 << 6 : 0)), // Circle
            buttons1 = (byte)((pressed == PadKey.Select ? 1 << 4 : 0) // Share, the layout's select button
                | (pressed == PadKey.Start ? 1 << 5 : 0)), // Options, the layout's start button
        };

        IEnumerator Tap(Gamepad pad, PadKey key)
        {
            SetKey(pad, key, true);
            yield return null;
            SetKey(pad, key, false);
            yield return null;
        }

        // Tapping Menu (start) is the harmless "wake" input: it is unbound, so it only announces the device.
        IEnumerator Wake(Gamepad pad) => Tap(pad, PadKey.Start);

        IEnumerator WakeKeyboard()
        {
            Press(keyboard.f12Key);
            yield return null;
            Release(keyboard.f12Key);
            yield return null;
        }

        IEnumerator Tap(ButtonControl button)
        {
            Press(button);
            yield return null;
            Release(button);
            yield return null;
        }

        Vector2 ScreenPointOf(Vector3 worldPoint) => viewCamera.WorldToScreenPoint(worldPoint);

        IEnumerator CursorOn(Vector3 worldPoint)
        {
            cursor.SetScreenPosition(ScreenPointOf(worldPoint));
            yield return null;
        }

        // Finds open, reachable ground near a unit by trying offsets until the cursor reads Ground there; sets orderPoint.
        IEnumerator FindOrderPoint(CommandableUnit near)
        {
            var origin = near.transform.position;
            origin.y = 0f;
            var mover = near.GetComponent<UnitMover>();
            foreach (var offset in new[]
                     {
                         new Vector3(0f, 0f, 3f), new Vector3(3f, 0f, 0f), new Vector3(-3f, 0f, 0f), new Vector3(0f, 0f, -3f),
                         new Vector3(4f, 0f, 4f), new Vector3(-4f, 0f, 4f), new Vector3(4f, 0f, -4f), new Vector3(-4f, 0f, -4f),
                     })
            {
                yield return CursorOn(origin + offset);
                if (cursor.Target.Kind == PointerTargetKind.Ground && mover.CanMoveTo(cursor.Target.Point))
                {
                    orderPoint = cursor.Target.Point;
                    yield break;
                }
            }
            Assert.Fail("No open ground near " + near.name);
        }

        static PadKey Confirm(bool nintendo) => nintendo ? PadKey.East : PadKey.South;

        static PadKey Cancel(bool nintendo) => nintendo ? PadKey.South : PadKey.East;

        [UnityTest]
        public IEnumerator Scene_StartsKeyboardMouse_AControllerWakesIt_AndKeyboardTakesItBack()
        {
            yield return LoadScene();
            Assert.That(family.Family, Is.EqualTo(InputFamily.KeyboardMouse));
            Assert.That(actions.bindingMask, Is.EqualTo((InputBinding?)InputBinding.MaskByGroup("KeyboardMouse")));

            var pad = InputSystem.AddDevice<XInputController>();
            yield return Wake(pad);
            Assert.That(family.Family, Is.EqualTo(InputFamily.Xbox));

            yield return WakeKeyboard();
            Assert.That(family.Family, Is.EqualTo(InputFamily.KeyboardMouse));
        }

        [UnityTest]
        public IEnumerator Scene_PadDriving_AnalogMovement_CharacterSwitching_AndControlToggle()
        {
            yield return LoadScene();
            var pad = InputSystem.AddDevice<XInputController>();
            yield return Wake(pad);

            Assert.That(active.IsTakeoverOn, Is.False);
            yield return Tap(pad.buttonNorth);
            Assert.That(active.IsTakeoverOn, Is.True, "North toggles character control");

            var unit = active.Unit;
            var start = unit.transform.position;
            Set(pad.leftStick, new Vector2(0f, 0.6f));
            yield return new WaitForSecondsRealtime(0.5f);
            Set(pad.leftStick, Vector2.zero);
            yield return null;
            var moved = TestWorld.HorizontalDistance(unit.transform.position, start);
            Assert.That(moved, Is.InRange(0.3f, 2.4f), "Half deflection moves, but slower than full speed (5 m/s)");

            yield return Tap(pad.rightShoulder);
            Assert.That(active.Unit, Is.Not.SameAs(unit), "RB switches character");
            yield return Tap(pad.leftShoulder);
            Assert.That(active.Unit, Is.SameAs(unit), "LB switches back");
        }

        [UnityTest]
        public IEnumerator Scene_PadTactical_PauseSelectMoveQueueAndCancel_WithSimulationStillPaused()
        {
            yield return LoadScene();
            var pad = InputSystem.AddDevice<XInputController>();
            yield return Wake(pad);

            yield return Tap(pad, PadKey.Select);
            Assert.That(pause.IsPaused, Is.True, "View pauses");

            var unit = squad[0];
            yield return CursorOn(unit.transform.position);
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Friendly));
            yield return Tap(pad, PadKey.South);
            Assert.That(selection.Selected.Select(s => s.Unit), Is.EquivalentTo(new[] { unit }));

            yield return FindOrderPoint(unit);
            var first = orderPoint;
            yield return Tap(pad, PadKey.South);
            Assert.That(unit.CurrentCommand, Is.TypeOf<MoveCommand>());

            Press(pad.leftTrigger);
            yield return null;
            yield return CursorOn(first + new Vector3(1.5f, 0f, 0f));
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Ground));
            yield return Tap(pad, PadKey.South);
            Release(pad.leftTrigger);
            yield return null;
            Assert.That(unit.PendingCommands, Has.Count.EqualTo(1), "LT queues the second order");

            var held = unit.transform.position;
            yield return new WaitForSecondsRealtime(0.3f);
            Assert.That(unit.transform.position, Is.EqualTo(held), "Simulation must stay paused while the controller plans");
            Assert.That(Time.timeScale, Is.EqualTo(0f));

            yield return Tap(pad, PadKey.East);
            Assert.That(selection.Selected, Is.Empty, "East cancels (clears the selection)");
            Assert.That(pause.IsPaused, Is.True);
        }

        [UnityTest]
        public IEnumerator Scene_PadCover_CursorSnapsToGeneratedCover_AndMoveToCoverQueues()
        {
            yield return LoadScene();
            yield return TestWorld.WaitUntil(() => registry.Points.Count > 0, 2f);
            var pad = InputSystem.AddDevice<XInputController>();
            yield return Wake(pad);
            yield return Tap(pad, PadKey.Select);

            var unit = squad[0];
            yield return CursorOn(unit.transform.position);
            yield return Tap(pad, PadKey.South);

            var hostiles = PrototypeSceneTests.FindHostiles();
            CoverLocation chosen = null;
            foreach (var point in registry.Points.Where(p => p.IsValid && p.Height == CoverHeight.Low && p.Obstacle != null
                                                              && p.Obstacle.name.StartsWith("LowWall")))
            {
                var crowded = squad.Any(u => CoverRules.FlatDistance(u.transform.position, point.Position) < 2.5f)
                    || hostiles.Any(h => CoverRules.FlatDistance(h.transform.position, point.Position) < 2.5f);
                if (crowded)
                    continue;
                yield return CursorOn(point.Position);
                if (cursor.Target.Kind == PointerTargetKind.Cover && cursor.Target.Cover == point)
                {
                    chosen = point;
                    break;
                }
            }
            Assert.That(chosen, Is.Not.Null, "The cursor could not snap to any open low-wall cover location");

            yield return Tap(pad, PadKey.South);
            Assert.That(unit.CurrentCommand, Is.TypeOf<MoveToCoverCommand>());

            yield return FindOrderPoint(unit);
            Press(pad.leftTrigger);
            yield return null;
            yield return CursorOn(orderPoint);
            yield return Tap(pad, PadKey.South);
            Release(pad.leftTrigger);
            yield return null;
            Assert.That(unit.PendingCommands, Has.Count.EqualTo(1), "A command can be queued behind MoveToCover");
            Assert.That(pause.IsPaused, Is.True);
        }

        [UnityTest]
        public IEnumerator Scene_RepeatedDeviceSwitching_LeavesNoStuckControlsAndLogsNoErrors()
        {
            yield return LoadScene();
            var xbox = InputSystem.AddDevice<XInputController>();
            var ps = InputSystem.AddDevice<DualSenseGamepadHID>();

            for (var i = 0; i < 10; i++)
            {
                var pad = i % 2 == 0 ? (Gamepad)xbox : ps;
                yield return Wake(pad);
                Assert.That(family.Family, Is.EqualTo(i % 2 == 0 ? InputFamily.Xbox : InputFamily.PlayStation), "Iteration " + i);
                SetLeftStick(pad, new Vector2(0f, 0.9f));
                yield return null;
                yield return WakeKeyboard();
                SetLeftStick(pad, Vector2.zero);
                yield return null;
            }

            Assert.That(family.Family, Is.EqualTo(InputFamily.KeyboardMouse));
            Assert.That(actions.FindAction("Character/Move", true).ReadValue<Vector2>(), Is.EqualTo(Vector2.zero));
            Assert.That(active.Unit.MoveIntent, Is.EqualTo(Vector3.zero));
            Assert.That(Time.timeScale, Is.EqualTo(1f));
        }

        [UnityTest]
        public IEnumerator Scene_KeyboardMouse_StillWorksAfterControllerUse()
        {
            yield return LoadScene();
            var pad = InputSystem.AddDevice<XInputController>();
            yield return Wake(pad);
            yield return WakeKeyboard();

            Set(mouse.position, ScreenPointOf(squad[1].transform.position));
            yield return null;
            Press(mouse.leftButton);
            yield return null;
            Release(mouse.leftButton);
            yield return null;
            Assert.That(selection.Selected.Select(s => s.Unit), Is.EquivalentTo(new[] { squad[1] }), "Left-click selects");

            var before = active.Unit;
            yield return Tap(keyboard.tabKey);
            Assert.That(active.Unit, Is.Not.SameAs(before), "Tab switches character");

            yield return Tap(keyboard.vKey);
            Assert.That(active.IsTakeoverOn, Is.True, "V toggles character control");
            var follow = active.IsFollowOn;
            yield return Tap(keyboard.fKey);
            Assert.That(active.IsFollowOn, Is.EqualTo(!follow), "F toggles follow");

            yield return Tap(keyboard.spaceKey);
            Assert.That(pause.IsPaused, Is.True, "Space pauses");
            yield return Tap(keyboard.spaceKey);
            Assert.That(pause.IsPaused, Is.False);
        }

        IEnumerator FamilySmoke<T>(InputFamily expected, bool nintendo) where T : Gamepad
        {
            yield return LoadScene();
            var pad = InputSystem.AddDevice<T>();
            yield return Wake(pad);
            Assert.That(family.Family, Is.EqualTo(expected));

            yield return Tap(pad, PadKey.Select);
            Assert.That(pause.IsPaused, Is.True, "The pause button works on every family");

            var unit = squad[1];
            yield return CursorOn(unit.transform.position);
            yield return Tap(pad, Confirm(nintendo));
            Assert.That(selection.Selected.Select(s => s.Unit), Is.EquivalentTo(new[] { unit }));

            yield return FindOrderPoint(unit);
            yield return Tap(pad, Confirm(nintendo));
            Assert.That(unit.CurrentCommand, Is.TypeOf<MoveCommand>());

            yield return Tap(pad, Cancel(nintendo));
            Assert.That(selection.Selected, Is.Empty);
        }

        [UnityTest]
        public IEnumerator Scene_XboxFamily_CanPauseSelectOrderAndCancel()
        {
            yield return FamilySmoke<XInputController>(InputFamily.Xbox, false);
        }

        [UnityTest]
        public IEnumerator Scene_PlayStationFamily_CanPauseSelectOrderAndCancel()
        {
            yield return FamilySmoke<DualSenseGamepadHID>(InputFamily.PlayStation, false);
        }

        [UnityTest]
        public IEnumerator Scene_NintendoFamily_CanPauseSelectOrderAndCancel_WithEastAsConfirm()
        {
            yield return FamilySmoke<SwitchProControllerHID>(InputFamily.Nintendo, true);
        }

        [UnityTest]
        public IEnumerator Scene_GenericGamepadFamily_CanPauseSelectOrderAndCancel()
        {
            yield return FamilySmoke<Gamepad>(InputFamily.GenericGamepad, false);
        }
    }
}
#endif
