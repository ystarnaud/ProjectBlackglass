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
    public class InteractInputPlayModeTests : InputTestFixture
    {
        static readonly Vector3 TerminalGround = new Vector3(2f, 0f, 2f);
        static readonly Vector3 GroundPoint = new Vector3(-2f, 0f, 2f);

        Keyboard keyboard;
        Mouse mouse;
        Gamepad pad;
        InputActionAsset actions;
        TestWorld world;
        Camera viewCamera;
        SelectableUnit far;
        SelectableUnit near;
        MissionInteractable terminal;
        Health hostile;
        TacticalPause pause;
        UnitSelection selection;
        ActiveCharacter active;
        TacticalCursor cursor;
        PlayerCommandInput input;
        InteractableRegistry interactables;

        public override void Setup()
        {
            base.Setup();
            keyboard = InputSystem.AddDevice<Keyboard>();
            mouse = InputSystem.AddDevice<Mouse>();
            pad = InputSystem.AddDevice<XInputController>();
            actions = TestControls.Load();
            TestControls.UseGroup(actions, "Xbox");   // keyboard bindings are masked off by the group in use, so each test picks
            world = new TestWorld();
            world.CreateEnvironment();

            far = world.CreateFriendly(new Vector3(-8f, 0f, -6f));
            far.gameObject.AddComponent<UnitInteractor>();
            near = world.CreateFriendly(new Vector3(0f, 0f, 2f));   // 2 m from the terminal
            near.gameObject.AddComponent<UnitInteractor>();
            hostile = world.CreateDummy(new Vector3(8f, 0f, 8f));

            // A solid marker box outside the baked mesh (it carves nothing); the click ray hits its collider.
            var host = world.CreateObstacle(TerminalGround + Vector3.up * 0.6f, new Vector3(0.8f, 1.2f, 0.8f));
            terminal = host.AddComponent<MissionInteractable>();
            terminal.Initialize(1.8f, 1f);
            terminal.SetAvailable(true);
            interactables = world.Track(new GameObject("Interactables")).AddComponent<InteractableRegistry>();
            interactables.Initialize(terminal);

            var registry = world.CreateRegistry();
            var encounter = world.CreateEncounter();
            encounter.Initialize(new Health[0], new[] { hostile });

            var cameraObject = world.Track(new GameObject("Camera"));
            cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 25f, -20f), Quaternion.Euler(50f, 0f, 0f));
            viewCamera = cameraObject.AddComponent<Camera>();

            var systems = world.Track(new GameObject("Systems"));
            systems.SetActive(false);
            pause = systems.AddComponent<TacticalPause>();
            selection = systems.AddComponent<UnitSelection>();
            selection.Initialize(far, near);
            active = systems.AddComponent<ActiveCharacter>();
            active.Initialize(near.Unit, pause, selection);
            cursor = systems.AddComponent<TacticalCursor>();
            cursor.Initialize(viewCamera, active, selection, encounter, registry, null,
                TestControls.Ref(actions, "Commands/CursorMove"),
                TestControls.Ref(actions, "Camera/CameraModifier"),
                TestControls.Ref(actions, "Commands/NextTarget"),
                TestControls.Ref(actions, "Commands/PreviousTarget"));
            cursor.SetInteractables(interactables);
            input = systems.AddComponent<PlayerCommandInput>();
            input.Initialize(viewCamera, selection, pause,
                TestControls.Ref(actions, "Commands/Command"),
                TestControls.Ref(actions, "Commands/PointerPosition"),
                TestControls.Ref(actions, "Commands/ToggleTacticalPause"),
                TestControls.Ref(actions, "Commands/QueueModifier"),
                TestControls.Ref(actions, "Commands/Stop"),
                TestControls.Ref(actions, "Commands/Cancel"),
                active, registry, cursor,
                TestControls.Ref(actions, "Commands/Confirm"),
                TestControls.Ref(actions, "Commands/Attack"));
            input.WireInteraction(interactables, TestControls.Ref(actions, "Commands/Interact"));
            systems.SetActive(true);
        }

        public override void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
            TestControls.Reset(actions);
            base.TearDown();
        }

        // The keyboard and mouse bindings are masked off while a pad group is in use, and the reverse.
        void UseKeyboard() => TestControls.UseGroup(actions, "KeyboardMouse");

        Vector2 ScreenPointOf(Vector3 worldPoint) => viewCamera.WorldToScreenPoint(worldPoint);

        IEnumerator LeftClickAt(Vector2 screenPoint)
        {
            Set(mouse.position, screenPoint);
            yield return null;
            Press(mouse.leftButton);
            yield return null;
            Release(mouse.leftButton);
            yield return null;
        }

        IEnumerator Tap(ButtonControl button)
        {
            Press(button);
            yield return null;
            Release(button);
            yield return null;
        }

        IEnumerator CursorOn(Vector3 worldPoint)
        {
            cursor.SetScreenPosition(ScreenPointOf(worldPoint));
            yield return null;
        }

        static InteractCommand InteractOf(CommandableUnit unit) => unit.CurrentCommand as InteractCommand;

        // ---- mouse

        [UnityTest]
        public IEnumerator ClickingTheTerminal_OrdersTheSelectedUnitToInteract()
        {
            UseKeyboard();
            selection.Select(far);

            yield return LeftClickAt(ScreenPointOf(terminal.Position));

            Assert.That(InteractOf(far.Unit), Is.Not.Null);
            Assert.That(InteractOf(far.Unit).Target, Is.SameAs(terminal));
            Assert.That(near.Unit.CurrentCommand, Is.Null, "only the selection is ordered");
        }

        [UnityTest]
        public IEnumerator ShiftClickingTheTerminal_QueuesInteractBehindTheCurrentOrder()
        {
            UseKeyboard();
            selection.Select(far);
            yield return LeftClickAt(ScreenPointOf(GroundPoint));
            Assert.That(far.Unit.CurrentCommand, Is.TypeOf<MoveCommand>());

            Press(keyboard.leftShiftKey);
            yield return null;
            yield return LeftClickAt(ScreenPointOf(terminal.Position));
            Release(keyboard.leftShiftKey);
            yield return null;

            Assert.That(far.Unit.CurrentCommand, Is.TypeOf<MoveCommand>());
            Assert.That(far.Unit.PendingCommands, Has.Count.EqualTo(1));
            Assert.That(far.Unit.PendingCommands[0], Is.TypeOf<InteractCommand>());
        }

        [UnityTest]
        public IEnumerator ClickingTheTerminalWhilePaused_QueuesTheOrder_AndNothingProgresses()
        {
            UseKeyboard();
            selection.Select(near);
            pause.Pause();
            var start = near.transform.position;

            yield return LeftClickAt(ScreenPointOf(terminal.Position));
            yield return new WaitForSecondsRealtime(0.3f);

            Assert.That(InteractOf(near.Unit), Is.Not.Null, "planned while paused");
            Assert.That(terminal.Progress, Is.Zero);
            Assert.That(Vector3.Distance(near.transform.position, start), Is.LessThan(0.01f));
            Assert.That(pause.IsPaused, Is.True);
        }

        [UnityTest]
        public IEnumerator ClickingAUsedUpTerminal_IsNotAnInteractOrder()
        {
            UseKeyboard();
            terminal.SetAvailable(false);
            selection.Select(far);

            yield return LeftClickAt(ScreenPointOf(terminal.Position));

            Assert.That(far.Unit.CurrentCommand is InteractCommand, Is.False);
        }

        // ---- keyboard Interact

        [UnityTest]
        public IEnumerator KeyboardInteract_NearATerminal_OrdersTheActiveCharacter()
        {
            UseKeyboard();
            yield return Tap(keyboard.rKey);

            Assert.That(InteractOf(near.Unit), Is.Not.Null);
            Assert.That(InteractOf(near.Unit).Target, Is.SameAs(terminal));
            Assert.That(input.NearbyInteractable, Is.SameAs(terminal));
        }

        [UnityTest]
        public IEnumerator KeyboardInteract_FarFromTheTerminal_DoesNothing()
        {
            UseKeyboard();
            active.Initialize(far.Unit, pause, selection);
            yield return null;

            yield return Tap(keyboard.rKey);

            Assert.That(far.Unit.CurrentCommand, Is.Null);
            Assert.That(input.NearbyInteractable, Is.Null);
        }

        [UnityTest]
        public IEnumerator KeyboardInteract_WithNoAvailableTerminal_DoesNothing()
        {
            UseKeyboard();
            terminal.SetAvailable(false);

            yield return Tap(keyboard.rKey);

            Assert.That(near.Unit.CurrentCommand, Is.Null);
            Assert.That(input.NearbyInteractable, Is.Null);
        }

        // ---- who is ordered decides what is in reach (the prompt, the key and the context Confirm share one rule)

        [UnityTest]
        public IEnumerator Paused_WithNothingSelected_NoTerminalIsInReach_AndInteractDoesNothing_EvenNextToOne()
        {
            UseKeyboard();
            pause.Pause();
            yield return null;
            Assert.That(CoverRulesDistance(near, terminal), Is.EqualTo(2f).Within(0.1f), "the active character stands next to it");

            yield return Tap(keyboard.rKey);

            Assert.That(input.NearbyInteractable, Is.Null);
            Assert.That(near.Unit.CurrentCommand, Is.Null);
            Assert.That(far.Unit.CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator Paused_WithOnlyTheFarUnitSelected_NothingIsInReach_AndInteractDoesNothing()
        {
            UseKeyboard();
            selection.Select(far);
            pause.Pause();
            yield return null;

            yield return Tap(keyboard.rKey);

            Assert.That(input.NearbyInteractable, Is.Null);
            Assert.That(far.Unit.CurrentCommand, Is.Null);
            Assert.That(near.Unit.CurrentCommand, Is.Null, "the active character is not ordered while paused");
        }

        [UnityTest]
        public IEnumerator Paused_WithTheFarAndTheNearUnitSelected_TheNearOneFindsTheTerminal_AndBothAreOrdered()
        {
            UseKeyboard();
            selection.SetSelection(new[] { far, near });
            pause.Pause();
            yield return null;
            Assert.That(input.NearbyInteractable, Is.SameAs(terminal));

            yield return Tap(keyboard.rKey);

            Assert.That(InteractOf(far.Unit), Is.Not.Null);
            Assert.That(InteractOf(far.Unit).Target, Is.SameAs(terminal));
            Assert.That(InteractOf(near.Unit), Is.Not.Null);
            Assert.That(InteractOf(near.Unit).Target, Is.SameAs(terminal));
        }

        [UnityTest]
        public IEnumerator ATerminalAnotherLivingUnitIsWorking_IsNotInReach_AndPadConfirmAttacksInstead()
        {
            Assert.That(terminal.TryBegin(far.Unit), Is.True);
            yield return null;

            Assert.That(input.NearbyInteractable, Is.Null, "an order there would be refused as in use");
            Assert.That(input.PromptInteractable(true), Is.Null);
            yield return Tap(pad.buttonSouth);

            Assert.That(near.Unit.CurrentCommand, Is.TypeOf<AttackCommand>());
            Assert.That(((AttackCommand)near.Unit.CurrentCommand).Target, Is.SameAs(hostile));
        }

        [UnityTest]
        public IEnumerator ATerminalTheOrderedUnitIsWorkingItself_IsStillInReach()
        {
            Assert.That(terminal.TryBegin(near.Unit), Is.True);
            yield return null;

            Assert.That(input.NearbyInteractable, Is.SameAs(terminal));
        }

        [UnityTest]
        public IEnumerator PromptInteractable_ForTheKeyboard_IsWhatInteractWouldWorkOn()
        {
            yield return null;
            Assert.That(input.PromptInteractable(false), Is.SameAs(terminal));
            Assert.That(input.PromptInteractable(false), Is.SameAs(input.NearbyInteractable));

            pause.Pause();
            yield return null;
            Assert.That(input.NearbyInteractable, Is.Null, "paused with nothing selected");
            Assert.That(input.PromptInteractable(false), Is.Null);
        }

        [UnityTest]
        public IEnumerator PromptInteractable_ForAController_FollowsTheCursorWhenShown_AndTheContextConfirmWhenHidden()
        {
            Assert.That(cursor.IsActive, Is.False);
            yield return null;
            Assert.That(input.PromptInteractable(true), Is.SameAs(terminal), "cursor hidden next to the terminal");

            selection.Select(near);
            pause.Pause();
            yield return CursorOn(new Vector3(-4f, 0f, 6f));
            Assert.That(cursor.IsActive, Is.True);
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Ground));
            Assert.That(input.NearbyInteractable, Is.SameAs(terminal), "the selected unit stands next to it");
            Assert.That(input.PromptInteractable(true), Is.Null, "Confirm would act on the ground, not the terminal");

            yield return CursorOn(terminal.Position);
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Interactable));
            Assert.That(input.PromptInteractable(true), Is.SameAs(terminal));
        }

        static float CoverRulesDistance(SelectableUnit unit, MissionInteractable item) =>
            CoverRules.FlatDistance(unit.transform.position, item.Position);

        // ---- controller

        [UnityTest]
        public IEnumerator PadConfirm_NearATerminal_WithTheCursorHidden_Interacts()
        {
            Assert.That(cursor.IsActive, Is.False, "running with a character: the camera owns the right stick");

            yield return Tap(pad.buttonSouth);

            Assert.That(InteractOf(near.Unit), Is.Not.Null);
            Assert.That(InteractOf(near.Unit).Target, Is.SameAs(terminal));
        }

        [UnityTest]
        public IEnumerator PadConfirm_WithNoTerminalInReach_StillAttacksTheBestHostile()
        {
            active.Initialize(far.Unit, pause, selection);
            yield return null;

            yield return Tap(pad.buttonSouth);

            Assert.That(far.Unit.CurrentCommand, Is.TypeOf<AttackCommand>());
            Assert.That(((AttackCommand)far.Unit.CurrentCommand).Target, Is.SameAs(hostile));
        }

        [UnityTest]
        public IEnumerator PadConfirm_WithTheQueueModifier_AppendsInteract()
        {
            near.Unit.Issue(new MoveCommand(GroundPoint));
            Press(pad.leftTrigger);
            yield return null;

            yield return Tap(pad.buttonSouth);
            Release(pad.leftTrigger);
            yield return null;

            Assert.That(near.Unit.CurrentCommand, Is.TypeOf<MoveCommand>());
            Assert.That(near.Unit.PendingCommands, Has.Count.EqualTo(1));
            Assert.That(near.Unit.PendingCommands[0], Is.TypeOf<InteractCommand>());
        }

        [UnityTest]
        public IEnumerator PadCursor_OnTheTerminal_ConfirmOrdersTheSelectedUnitToInteract()
        {
            selection.Select(far);
            pause.Pause();

            yield return CursorOn(terminal.Position);
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Interactable));
            yield return Tap(pad.buttonSouth);

            Assert.That(InteractOf(far.Unit), Is.Not.Null);
            Assert.That(pause.IsPaused, Is.True, "the order waits for the resume");
        }

        [UnityTest]
        public IEnumerator PadCursor_SnapsToATerminalWithinTheSnapRadius_ButNotBeyondIt()
        {
            pause.Pause();

            yield return CursorOn(TerminalGround + new Vector3(1.0f, 0f, 0f));
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Interactable));

            yield return CursorOn(TerminalGround + new Vector3(3.0f, 0f, 0f));
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Ground));
        }

        [UnityTest]
        public IEnumerator PadCursor_DoesNotSnapToAnUnavailableTerminal()
        {
            pause.Pause();
            terminal.SetAvailable(false);

            yield return CursorOn(TerminalGround + new Vector3(1.0f, 0f, 0f));

            Assert.That(cursor.Target.Kind, Is.Not.EqualTo(PointerTargetKind.Interactable));
        }
    }
}
#endif
