#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.Switch;
using UnityEngine.InputSystem.XInput;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class ControllerCommandInputTests : InputTestFixture
    {
        static readonly Vector3 GroundPoint = new Vector3(2f, 0f, 2f);
        static readonly Vector3 OtherGroundPoint = new Vector3(-2f, 0f, 2f);
        static readonly Vector3 CoverGroundPoint = new Vector3(-6f, 0f, 2f);

        Gamepad pad;
        InputActionAsset actions;
        TestWorld world;
        Camera viewCamera;
        SelectableUnit unitA;
        SelectableUnit unitB;
        Health nearDummy;
        Health farDummy;
        CoverLocation coverPoint;
        TacticalPause pause;
        UnitSelection selection;
        ActiveCharacter active;
        TacticalCursor cursor;

        public override void Setup()
        {
            base.Setup();
            pad = InputSystem.AddDevice<XInputController>();
            actions = TestControls.Load();
            TestControls.UseGroup(actions, "Xbox");
            world = new TestWorld();
            world.CreateEnvironment();
            unitA = world.CreateFriendly(new Vector3(-8f, 0f, -6f));
            unitB = world.CreateFriendly(new Vector3(4f, 0f, -6f));
            nearDummy = world.CreateDummy(new Vector3(-10f, 0f, -6f));
            farDummy = world.CreateDummy(new Vector3(6f, 0f, 6f));
            var wall = world.CreateObstacle(CoverGroundPoint + new Vector3(0f, 0.45f, 1f), new Vector3(2f, 0.9f, 0.5f));
            coverPoint = world.CreateCoverPoint(CoverGroundPoint, Vector3.forward, wall.GetComponent<Collider>());
            var registry = world.CreateRegistry(coverPoint);
            var encounter = world.CreateEncounter();
            encounter.Initialize(new Health[0], new[] { farDummy, nearDummy });

            var cameraObject = world.Track(new GameObject("Camera"));
            cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 25f, -20f), Quaternion.Euler(50f, 0f, 0f));
            viewCamera = cameraObject.AddComponent<Camera>();

            var systems = world.Track(new GameObject("Systems"));
            systems.SetActive(false);
            pause = systems.AddComponent<TacticalPause>();
            selection = systems.AddComponent<UnitSelection>();
            selection.Initialize(unitA, unitB);
            active = systems.AddComponent<ActiveCharacter>();
            active.Initialize(null, pause, selection);
            cursor = systems.AddComponent<TacticalCursor>();
            cursor.Initialize(viewCamera, active, selection, encounter, registry, null,
                TestControls.Ref(actions, "Commands/CursorMove"),
                TestControls.Ref(actions, "Camera/CameraModifier"),
                TestControls.Ref(actions, "Commands/NextTarget"),
                TestControls.Ref(actions, "Commands/PreviousTarget"));
            var input = systems.AddComponent<PlayerCommandInput>();
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
            systems.SetActive(true);
        }

        public override void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
            TestControls.Reset(actions);
            base.TearDown();
        }

        Vector2 ScreenPointOf(Vector3 worldPoint) => viewCamera.WorldToScreenPoint(worldPoint);

        IEnumerator CursorOn(Vector3 worldPoint)
        {
            cursor.SetScreenPosition(ScreenPointOf(worldPoint));
            yield return null;
        }

        IEnumerator Tap(ButtonControl button)
        {
            Press(button);
            yield return null;
            Release(button);
            yield return null;
        }

        void DriveUnitA()
        {
            active.Initialize(unitA.Unit, pause, selection);
            active.SetTakeover(true);
        }

        [UnityTest]
        public IEnumerator Confirm_OnAFriendly_SelectsIt()
        {
            pause.Pause();
            yield return CursorOn(unitB.transform.position);
            yield return Tap(pad.buttonSouth);

            Assert.That(selection.Selected, Has.Count.EqualTo(1));
            Assert.That(selection.Selected[0], Is.SameAs(unitB));
        }

        [UnityTest]
        public IEnumerator QueueModifierPlusConfirm_BuildsAMultiSelection_AndTogglesOneOut()
        {
            pause.Pause();
            yield return CursorOn(unitA.transform.position);
            yield return Tap(pad.buttonSouth);
            Press(pad.leftTrigger);
            yield return null;
            yield return CursorOn(unitB.transform.position);
            yield return Tap(pad.buttonSouth);
            Assert.That(selection.Selected, Is.EquivalentTo(new[] { unitA, unitB }));

            yield return CursorOn(unitA.transform.position);
            yield return Tap(pad.buttonSouth);
            Release(pad.leftTrigger);
            yield return null;
            Assert.That(selection.Selected, Is.EquivalentTo(new[] { unitB }));
        }

        [UnityTest]
        public IEnumerator Confirm_OnGround_WhilePaused_OrdersTheSelectedUnits_AndSimulationStaysPaused()
        {
            selection.Select(unitA);
            pause.Pause();
            yield return CursorOn(GroundPoint);
            yield return Tap(pad.buttonSouth);

            Assert.That(unitA.Unit.CurrentCommand, Is.TypeOf<MoveCommand>());
            Assert.That(Vector3.Distance(((MoveCommand)unitA.Unit.CurrentCommand).Destination, GroundPoint), Is.LessThan(0.5f));
            Assert.That(pause.IsPaused, Is.True);
            Assert.That(unitB.Unit.CurrentCommand, Is.Null, "Only the selection is ordered");
        }

        [UnityTest]
        public IEnumerator Confirm_WithTheQueueModifier_AppendsInsteadOfReplacing()
        {
            selection.Select(unitA);
            pause.Pause();
            yield return CursorOn(GroundPoint);
            yield return Tap(pad.buttonSouth);
            var first = unitA.Unit.CurrentCommand;

            Press(pad.leftTrigger);
            yield return null;
            yield return CursorOn(OtherGroundPoint);
            yield return Tap(pad.buttonSouth);
            Release(pad.leftTrigger);
            yield return null;

            Assert.That(unitA.Unit.CurrentCommand, Is.SameAs(first));
            Assert.That(unitA.Unit.PendingCommands, Has.Count.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator Confirm_OnAHostile_IssuesAnAttack()
        {
            selection.Select(unitA);
            pause.Pause();
            yield return CursorOn(farDummy.transform.position);
            yield return Tap(pad.buttonSouth);

            Assert.That(unitA.Unit.CurrentCommand, Is.TypeOf<AttackCommand>());
            Assert.That(((AttackCommand)unitA.Unit.CurrentCommand).Target, Is.SameAs(farDummy));
        }

        [UnityTest]
        public IEnumerator Confirm_OnCover_IssuesMoveToCover_ThenAQueuedCommandFollows()
        {
            selection.Select(unitA);
            pause.Pause();
            yield return CursorOn(CoverGroundPoint);
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Cover));
            yield return Tap(pad.buttonSouth);
            Assert.That(unitA.Unit.CurrentCommand, Is.TypeOf<MoveToCoverCommand>());
            Assert.That(((MoveToCoverCommand)unitA.Unit.CurrentCommand).Point, Is.SameAs(coverPoint));

            Press(pad.leftTrigger);
            yield return null;
            yield return CursorOn(GroundPoint);
            yield return Tap(pad.buttonSouth);
            Release(pad.leftTrigger);
            yield return null;
            Assert.That(unitA.Unit.PendingCommands, Has.Count.EqualTo(1));
            Assert.That(unitA.Unit.PendingCommands[0], Is.TypeOf<MoveCommand>());
        }

        [UnityTest]
        public IEnumerator Cancel_ClearsTheSelection_AndIsHarmlessWhenEmpty()
        {
            selection.Select(unitA);
            pause.Pause();
            yield return Tap(pad.buttonEast);
            Assert.That(selection.Selected, Is.Empty);
            yield return Tap(pad.buttonEast);
            Assert.That(selection.Selected, Is.Empty);
        }

        [UnityTest]
        public IEnumerator Stop_StopsTheSelectedUnits()
        {
            selection.Select(unitA);
            Assert.That(unitA.Unit.Issue(new MoveCommand(GroundPoint)), Is.True);
            yield return Tap(pad.dpad.down);

            Assert.That(unitA.Unit.CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator ToggleTacticalPause_PausesAndResumes_FromTheController()
        {
            yield return Tap(pad.startButton);
            Assert.That(pause.IsPaused, Is.True);
            yield return Tap(pad.startButton);
            Assert.That(pause.IsPaused, Is.False);
        }

        // Real time with the camera owning the right stick: the cursor is hidden, so Confirm (Cross) attacks like Attack.
        [UnityTest]
        public IEnumerator Confirm_WhileTheCameraOwnsTheStick_AttacksTheBestHostileForTheActiveCharacter()
        {
            DriveUnitA();
            yield return null;
            Assert.That(cursor.IsActive, Is.False);
            yield return Tap(pad.buttonSouth);

            Assert.That(unitA.Unit.CurrentCommand, Is.TypeOf<AttackCommand>());
            Assert.That(((AttackCommand)unitA.Unit.CurrentCommand).Target, Is.SameAs(nearDummy), "The nearer one, two metres away");
        }

        [UnityTest]
        public IEnumerator Confirm_WhileTheCameraOwnsTheStick_AttacksInRealTimeWithTakeoverOff()
        {
            selection.Select(unitA);
            yield return null;
            Assert.That(cursor.IsActive, Is.False);
            yield return Tap(pad.buttonSouth);

            Assert.That(pause.IsPaused, Is.False);
            Assert.That(unitA.Unit.CurrentCommand, Is.TypeOf<AttackCommand>());
        }

        [UnityTest]
        public IEnumerator Confirm_WhileTheCameraOwnsTheStick_WithTheQueueModifier_Appends()
        {
            DriveUnitA();
            Assert.That(unitA.Unit.Issue(new MoveCommand(GroundPoint)), Is.True);
            var move = unitA.Unit.CurrentCommand;
            Press(pad.leftTrigger);
            yield return null;
            yield return Tap(pad.buttonSouth);
            Release(pad.leftTrigger);
            yield return null;

            Assert.That(unitA.Unit.CurrentCommand, Is.SameAs(move));
            Assert.That(unitA.Unit.PendingCommands, Has.Count.EqualTo(1));
            Assert.That(unitA.Unit.PendingCommands[0], Is.TypeOf<AttackCommand>());
        }

        [UnityTest]
        public IEnumerator Confirm_WhileTheCameraOwnsTheStick_WithNoLivingHostiles_DoesNothing()
        {
            DriveUnitA();
            nearDummy.TakeDamage(1000);
            farDummy.TakeDamage(1000);
            yield return null;
            yield return Tap(pad.buttonSouth);

            Assert.That(unitA.Unit.CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator Confirm_WithTheCursorActive_StillActsAtTheCursor_NotOnTheBestHostile()
        {
            selection.Select(unitA);
            pause.Pause();
            yield return CursorOn(GroundPoint);
            Assert.That(cursor.IsActive, Is.True);
            yield return Tap(pad.buttonSouth);

            Assert.That(unitA.Unit.CurrentCommand, Is.TypeOf<MoveCommand>(), "The cursor is on the ground, not on a hostile");

            yield return CursorOn(unitB.transform.position);
            yield return Tap(pad.buttonSouth);
            Assert.That(selection.Selected, Is.EquivalentTo(new[] { unitB }), "The cursor is on a friendly: it selects");
        }

        [UnityTest]
        public IEnumerator HoldingRightTrigger_WhileDriving_LetsConfirmOrderTheSelection()
        {
            DriveUnitA();
            selection.Select(unitB);
            Press(pad.rightTrigger);
            yield return null;
            yield return CursorOn(GroundPoint);
            yield return Tap(pad.buttonSouth);
            Release(pad.rightTrigger);
            yield return null;

            Assert.That(unitB.Unit.CurrentCommand, Is.TypeOf<MoveCommand>());
        }

        [UnityTest]
        public IEnumerator Attack_AtTheCursorHostile_OrdersTheSelection()
        {
            selection.Select(unitA);
            pause.Pause();
            yield return CursorOn(farDummy.transform.position);
            yield return Tap(pad.buttonWest);

            Assert.That(unitA.Unit.CurrentCommand, Is.TypeOf<AttackCommand>());
            Assert.That(((AttackCommand)unitA.Unit.CurrentCommand).Target, Is.SameAs(farDummy));
        }

        [UnityTest]
        public IEnumerator Attack_WhileDriving_PicksTheBestHostileForTheActiveCharacter()
        {
            DriveUnitA();
            yield return null;
            yield return Tap(pad.buttonWest);

            Assert.That(unitA.Unit.CurrentCommand, Is.TypeOf<AttackCommand>());
            Assert.That(((AttackCommand)unitA.Unit.CurrentCommand).Target, Is.SameAs(nearDummy), "The nearer one, two metres away");
        }

        [UnityTest]
        public IEnumerator Attack_UsesTheSoftTargetChosenWithTheDPad()
        {
            DriveUnitA();
            yield return null;
            yield return Tap(pad.dpad.right);
            yield return Tap(pad.dpad.right);
            Assert.That(cursor.SoftTarget, Is.SameAs(farDummy));
            yield return Tap(pad.buttonWest);

            Assert.That(((AttackCommand)unitA.Unit.CurrentCommand).Target, Is.SameAs(farDummy));
        }

        [UnityTest]
        public IEnumerator Attack_WithTheQueueModifier_Appends()
        {
            selection.Select(unitA);
            Assert.That(unitA.Unit.Issue(new MoveCommand(GroundPoint)), Is.True);
            var move = unitA.Unit.CurrentCommand;
            Press(pad.leftTrigger);
            yield return null;
            yield return Tap(pad.buttonWest);
            Release(pad.leftTrigger);
            yield return null;

            Assert.That(unitA.Unit.CurrentCommand, Is.SameAs(move));
            Assert.That(unitA.Unit.PendingCommands, Has.Count.EqualTo(1));
            Assert.That(unitA.Unit.PendingCommands[0], Is.TypeOf<AttackCommand>());
        }

        [UnityTest]
        public IEnumerator Attack_WithNoLivingHostiles_DoesNothing()
        {
            selection.Select(unitA);
            nearDummy.TakeDamage(1000);
            farDummy.TakeDamage(1000);
            yield return Tap(pad.buttonWest);

            Assert.That(unitA.Unit.CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator Nintendo_ConfirmIsEast_CancelIsSouth()
        {
            pause.Pause();
            InputSystem.RemoveDevice(pad);
            pad = InputSystem.AddDevice<SwitchProControllerHID>();
            TestControls.UseGroup(actions, "Nintendo");
            yield return null;

            yield return CursorOn(unitB.transform.position);
            yield return Tap(pad.buttonSouth);
            Assert.That(selection.Selected, Is.Empty, "South is Cancel on a Nintendo controller: it must not select");

            yield return Tap(pad.buttonEast);
            Assert.That(selection.Selected, Is.EquivalentTo(new[] { unitB }), "East confirms");

            yield return Tap(pad.buttonSouth);
            Assert.That(selection.Selected, Is.Empty, "South cancels");
        }
    }
}
#endif
