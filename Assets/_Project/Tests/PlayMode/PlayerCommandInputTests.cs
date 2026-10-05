#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class PlayerCommandInputTests : InputTestFixture
    {
        static readonly Vector3 GroundPoint = new Vector3(2f, 0f, 2f);
        static readonly Vector3 OtherGroundPoint = new Vector3(-2f, 0f, 2f);
        static readonly Vector3 CoverGroundPoint = new Vector3(-6f, 0f, 2f);

        Keyboard keyboard;
        Mouse mouse;
        TestWorld world;
        Camera viewCamera;
        SelectableUnit unitA;
        SelectableUnit unitB;
        SelectableUnit unitC;
        Health dummy;
        TacticalPause pause;
        UnitSelection selection;
        PlayerCommandInput input;
        ActiveCharacter activeCharacter;
        CoverLocation coverPoint;
        CoverRegistry registry;

        public override void Setup()
        {
            base.Setup();
            keyboard = InputSystem.AddDevice<Keyboard>();
            mouse = InputSystem.AddDevice<Mouse>();
            world = new TestWorld();

            world.CreateEnvironment();
            unitA = world.CreateFriendly(new Vector3(-8f, 0f, -6f));
            unitB = world.CreateFriendly(new Vector3(-4f, 0f, -6f));
            unitC = world.CreateFriendly(new Vector3(4f, 0f, -6f));
            dummy = world.CreateDummy(new Vector3(6f, 0f, 6f));

            // A loose waist-high wall north of the cover point (the environment is already baked; the box is only a
            // collider for the point). Clicks at CoverGroundPoint reach the ground: the wall is farther from the camera.
            var wall = world.CreateObstacle(CoverGroundPoint + new Vector3(0f, 0.45f, 1f), new Vector3(2f, 0.9f, 0.5f));
            coverPoint = world.CreateCoverPoint(CoverGroundPoint, Vector3.forward, wall.GetComponent<Collider>());
            registry = world.CreateRegistry(coverPoint);

            var cameraObject = world.Track(new GameObject("Camera"));
            cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 25f, -20f), Quaternion.Euler(50f, 0f, 0f));
            viewCamera = cameraObject.AddComponent<Camera>();

            var systems = world.Track(new GameObject("Systems"));
            systems.SetActive(false);
            pause = systems.AddComponent<TacticalPause>();
            selection = systems.AddComponent<UnitSelection>();
            selection.Initialize(unitA, unitB, unitC);
            // No unit yet: Phase 2 behaviour (orders go to the selection) until a test picks a primary character.
            activeCharacter = systems.AddComponent<ActiveCharacter>();
            activeCharacter.Initialize(null, pause);
            var actions = TestControls.Load();
            input = systems.AddComponent<PlayerCommandInput>();
            input.Initialize(viewCamera, selection, pause,
                TestControls.Ref(actions, "Commands/Command"),
                TestControls.Ref(actions, "Commands/PointerPosition"),
                TestControls.Ref(actions, "Commands/TogglePause"),
                TestControls.Ref(actions, "Commands/Modifier"),
                TestControls.Ref(actions, "Commands/Stop"),
                TestControls.Ref(actions, "Commands/ClearSelection"),
                activeCharacter, registry);
            systems.SetActive(true);
        }

        public override void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
            base.TearDown();
        }

        Vector2 ScreenPointOf(Vector3 worldPoint) => viewCamera.WorldToScreenPoint(worldPoint);
        Vector2 ScreenPointOf(Component component) => ScreenPointOf(component.transform.position);

        IEnumerator LeftClickAt(Vector2 screenPoint)
        {
            Set(mouse.position, screenPoint);
            yield return null;
            Press(mouse.leftButton);
            yield return null;
            Release(mouse.leftButton);
            yield return null;
        }

        IEnumerator ShiftLeftClickAt(Vector2 screenPoint)
        {
            Press(keyboard.leftShiftKey);
            yield return null;
            yield return LeftClickAt(screenPoint);
            Release(keyboard.leftShiftKey);
            yield return null;
        }

        IEnumerator LeftDrag(Vector2 from, Vector2 to, bool shift = false)
        {
            if (shift)
                Press(keyboard.leftShiftKey);
            Set(mouse.position, from);
            yield return null;
            Press(mouse.leftButton);
            yield return null;
            Set(mouse.position, to);
            yield return null;
            Release(mouse.leftButton);
            yield return null;
            if (shift)
                Release(keyboard.leftShiftKey);
            yield return null;
        }

        IEnumerator Tap(KeyControl key)
        {
            Press(key);
            yield return null;
            Release(key);
            yield return null;
        }

        // Screen box around two units, with a margin.
        (Vector2 from, Vector2 to) BoxAround(Component first, Component second)
        {
            var a = ScreenPointOf(first);
            var b = ScreenPointOf(second);
            var margin = new Vector2(20f, 20f);
            return (Vector2.Min(a, b) - margin, Vector2.Max(a, b) + margin);
        }

        [UnityTest]
        public IEnumerator ClickFriendly_SelectsOnlyThatUnit()
        {
            yield return null;
            yield return LeftClickAt(ScreenPointOf(unitA));
            Assert.That(selection.Selected, Is.EqualTo(new[] { unitA }));

            yield return LeftClickAt(ScreenPointOf(unitB));
            Assert.That(selection.Selected, Is.EqualTo(new[] { unitB }));
            Assert.That(unitA.IsSelected, Is.False);
            Assert.That(unitA.Unit.CurrentCommand, Is.Null, "Selecting must not issue orders");
        }

        [UnityTest]
        public IEnumerator ShiftClickFriendly_AddsThenRemoves()
        {
            yield return null;
            yield return LeftClickAt(ScreenPointOf(unitA));
            yield return ShiftLeftClickAt(ScreenPointOf(unitB));
            Assert.That(selection.Selected, Is.EqualTo(new[] { unitA, unitB }));

            yield return ShiftLeftClickAt(ScreenPointOf(unitA));
            Assert.That(selection.Selected, Is.EqualTo(new[] { unitB }));
        }

        [UnityTest]
        public IEnumerator BoxDrag_SelectsTheUnitsInsideTheBox()
        {
            yield return null;
            var (from, to) = BoxAround(unitA, unitB);
            Set(mouse.position, from);
            yield return null;
            Press(mouse.leftButton);
            yield return null;
            Set(mouse.position, to);
            yield return null;
            Assert.That(input.IsDragging, Is.True);
            Assert.That(input.DragRect, Is.EqualTo(ScreenBox.FromCorners(from, to)));
            Release(mouse.leftButton);
            yield return null;

            Assert.That(input.IsDragging, Is.False);
            Assert.That(selection.Selected, Is.EquivalentTo(new[] { unitA, unitB }));
        }

        [UnityTest]
        public IEnumerator ShiftBoxDrag_AddsToTheSelection()
        {
            yield return null;
            yield return LeftClickAt(ScreenPointOf(unitC));
            var (from, to) = BoxAround(unitA, unitB);
            yield return LeftDrag(from, to, shift: true);

            Assert.That(selection.Selected, Is.EquivalentTo(new[] { unitA, unitB, unitC }));
        }

        [UnityTest]
        public IEnumerator EmptyBoxDrag_ClearsSelectionAndIssuesNoOrder()
        {
            yield return null;
            yield return LeftClickAt(ScreenPointOf(unitA));
            var start = ScreenPointOf(GroundPoint);
            yield return LeftDrag(start, start + new Vector2(40f, 0f));

            Assert.That(selection.Selected, Is.Empty);
            Assert.That(unitA.Unit.CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator Escape_ClearsSelection()
        {
            yield return null;
            yield return LeftClickAt(ScreenPointOf(unitA));
            yield return Tap(keyboard.escapeKey);
            Assert.That(selection.Selected, Is.Empty);
        }

        [UnityTest]
        public IEnumerator ClickGround_MovesOnlyTheSelectedUnits()
        {
            yield return null;
            yield return LeftClickAt(ScreenPointOf(unitA));
            yield return LeftClickAt(ScreenPointOf(GroundPoint));

            Assert.That(unitA.Unit.CurrentCommand, Is.TypeOf<MoveCommand>());
            var destination = ((MoveCommand)unitA.Unit.CurrentCommand).Destination;
            Assert.That(TestWorld.HorizontalDistance(destination, GroundPoint), Is.LessThan(0.1f));
            Assert.That(unitB.Unit.CurrentCommand, Is.Null);
            Assert.That(unitC.Unit.CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator ClickGround_WithNothingSelected_IssuesNothing()
        {
            yield return null;
            yield return LeftClickAt(ScreenPointOf(GroundPoint));
            Assert.That(unitA.Unit.CurrentCommand, Is.Null);
            Assert.That(unitB.Unit.CurrentCommand, Is.Null);
            Assert.That(unitC.Unit.CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator ClickDummy_SelectedUnitsAttackIt()
        {
            yield return null;
            yield return LeftClickAt(ScreenPointOf(unitA));
            yield return ShiftLeftClickAt(ScreenPointOf(unitB));
            yield return LeftClickAt(ScreenPointOf(dummy));

            foreach (var selected in new[] { unitA, unitB })
            {
                Assert.That(selected.Unit.CurrentCommand, Is.TypeOf<AttackCommand>(), selected.name);
                Assert.That(((AttackCommand)selected.Unit.CurrentCommand).Target, Is.SameAs(dummy));
            }
            Assert.That(unitC.Unit.CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator ShiftClickGround_AppendsTheOrder()
        {
            yield return null;
            yield return LeftClickAt(ScreenPointOf(unitA));
            yield return LeftClickAt(ScreenPointOf(GroundPoint));
            yield return ShiftLeftClickAt(ScreenPointOf(OtherGroundPoint));

            Assert.That(unitA.Unit.CurrentCommand, Is.TypeOf<MoveCommand>());
            Assert.That(unitA.Unit.PendingCommands, Has.Count.EqualTo(1));
            var queued = (MoveCommand)unitA.Unit.PendingCommands[0];
            Assert.That(TestWorld.HorizontalDistance(queued.Destination, OtherGroundPoint), Is.LessThan(0.1f));
        }

        [UnityTest]
        public IEnumerator X_StopsTheSelectedUnitsOnly()
        {
            yield return null;
            yield return LeftClickAt(ScreenPointOf(unitC));
            yield return LeftClickAt(ScreenPointOf(GroundPoint));
            yield return LeftClickAt(ScreenPointOf(unitA));
            yield return LeftClickAt(ScreenPointOf(GroundPoint));
            yield return ShiftLeftClickAt(ScreenPointOf(OtherGroundPoint));

            yield return Tap(keyboard.xKey);

            Assert.That(unitA.Unit.CurrentCommand, Is.Null);
            Assert.That(unitA.Unit.PendingCommands, Is.Empty);
            Assert.That(unitC.Unit.CurrentCommand, Is.TypeOf<MoveCommand>(), "An unselected unit was stopped");
        }

        [UnityTest]
        public IEnumerator X_WithNothingSelected_DoesNothing()
        {
            yield return null;
            yield return Tap(keyboard.xKey);
            Assert.That(selection.Selected, Is.Empty);
        }

        [UnityTest]
        public IEnumerator ShiftClickFriendly_WithUnitsSelected_NeverIssuesOrders()
        {
            yield return null;
            yield return LeftClickAt(ScreenPointOf(unitA));
            yield return LeftClickAt(ScreenPointOf(GroundPoint));
            var orderBefore = unitA.Unit.CurrentCommand;

            yield return ShiftLeftClickAt(ScreenPointOf(unitB));

            Assert.That(selection.Selected, Is.EqualTo(new[] { unitA, unitB }));
            Assert.That(unitA.Unit.CurrentCommand, Is.SameAs(orderBefore));
            Assert.That(unitA.Unit.PendingCommands, Is.Empty);
            Assert.That(unitB.Unit.CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator RightClickOnDummy_IssuesNothing()
        {
            yield return null;
            yield return LeftClickAt(ScreenPointOf(unitA));
            Set(mouse.position, ScreenPointOf(dummy));
            yield return null;
            Press(mouse.rightButton);
            yield return null;
            Release(mouse.rightButton);
            yield return null;

            Assert.That(unitA.Unit.CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator LeftClickOnSky_ChangesNothing()
        {
            yield return null;
            yield return LeftClickAt(ScreenPointOf(unitA));
            yield return LeftClickAt(ScreenPointOf(new Vector3(0f, 500f, 2000f)));

            Assert.That(unitA.Unit.CurrentCommand, Is.Null);
            Assert.That(selection.Selected, Is.EqualTo(new[] { unitA }));
        }

        [UnityTest]
        public IEnumerator Space_TogglesTacticalPause()
        {
            yield return null;
            yield return Tap(keyboard.spaceKey);
            Assert.That(pause.IsPaused, Is.True);
            yield return Tap(keyboard.spaceKey);
            Assert.That(pause.IsPaused, Is.False);
        }

        [UnityTest]
        public IEnumerator OrdersClickedWhilePaused_WaitUntilResume()
        {
            yield return null;
            pause.Pause();
            var start = unitA.transform.position;

            yield return LeftClickAt(ScreenPointOf(unitA));
            yield return LeftClickAt(ScreenPointOf(GroundPoint));
            yield return ShiftLeftClickAt(ScreenPointOf(OtherGroundPoint));
            yield return new WaitForSecondsRealtime(0.5f);

            Assert.That(selection.Selected, Is.EqualTo(new[] { unitA }), "Selection must work while paused");
            Assert.That(unitA.Unit.CurrentCommand, Is.TypeOf<MoveCommand>());
            Assert.That(unitA.Unit.PendingCommands, Has.Count.EqualTo(1));
            Assert.That(TestWorld.HorizontalDistance(unitA.transform.position, start), Is.LessThan(0.01f), "Unit moved while paused");

            pause.Resume();
            yield return TestWorld.WaitUntil(() => TestWorld.HorizontalDistance(unitA.transform.position, start) > 1f, 5f);
            Assert.That(TestWorld.HorizontalDistance(unitA.transform.position, start), Is.GreaterThan(1f));
        }

        // --- With a primary character (Phase 3). These tests give the fixture's ActiveCharacter a unit.

        void MakeActive(SelectableUnit unit) => activeCharacter.Initialize(unit.Unit, pause);

        [UnityTest]
        public IEnumerator RealTimeClickGround_WithASelection_OrdersTheSelectionNotTheActive()
        {
            yield return null;
            MakeActive(unitA);
            yield return LeftClickAt(ScreenPointOf(unitB));
            yield return LeftClickAt(ScreenPointOf(GroundPoint));

            Assert.That(unitB.Unit.CurrentCommand, Is.TypeOf<MoveCommand>());
            var destination = ((MoveCommand)unitB.Unit.CurrentCommand).Destination;
            Assert.That(TestWorld.HorizontalDistance(destination, GroundPoint), Is.LessThan(0.1f));
            Assert.That(unitA.Unit.CurrentCommand, Is.Null, "The active character is not ordered when a selection exists");
            Assert.That(selection.Selected, Is.EqualTo(new[] { unitB }));
        }

        [UnityTest]
        public IEnumerator RealTimeClickDummy_WithNothingSelected_TheActiveAttacks()
        {
            yield return null;
            MakeActive(unitA);
            Assert.That(selection.Selected, Is.Empty, "Precondition: nothing selected");
            yield return LeftClickAt(ScreenPointOf(dummy));

            Assert.That(unitA.Unit.CurrentCommand, Is.TypeOf<AttackCommand>());
            Assert.That(((AttackCommand)unitA.Unit.CurrentCommand).Target, Is.SameAs(dummy));
            Assert.That(unitB.Unit.CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator RealTimeClickGround_WithTheSquadSelected_OrdersAllOfThem()
        {
            yield return null;
            MakeActive(unitA);
            yield return LeftClickAt(ScreenPointOf(unitA));
            yield return ShiftLeftClickAt(ScreenPointOf(unitB));
            yield return ShiftLeftClickAt(ScreenPointOf(unitC));
            Assert.That(selection.Selected, Is.EquivalentTo(new[] { unitA, unitB, unitC }), "Precondition: the squad is selected");

            yield return LeftClickAt(ScreenPointOf(GroundPoint));

            foreach (var unit in new[] { unitA, unitB, unitC })
                Assert.That(unit.Unit.CurrentCommand, Is.TypeOf<MoveCommand>(), $"{unit.name} was not ordered in real time");
        }

        [UnityTest]
        public IEnumerator RealTimeShiftClick_WithNothingSelected_QueuesForTheActive()
        {
            yield return null;
            MakeActive(unitA);
            Assert.That(selection.Selected, Is.Empty, "Precondition: nothing selected");
            yield return LeftClickAt(ScreenPointOf(GroundPoint));
            yield return ShiftLeftClickAt(ScreenPointOf(OtherGroundPoint));

            Assert.That(unitA.Unit.CurrentCommand, Is.TypeOf<MoveCommand>());
            Assert.That(unitA.Unit.PendingCommands, Has.Count.EqualTo(1));
            var queued = (MoveCommand)unitA.Unit.PendingCommands[0];
            Assert.That(TestWorld.HorizontalDistance(queued.Destination, OtherGroundPoint), Is.LessThan(0.1f));
        }

        [UnityTest]
        public IEnumerator PausedClickGround_WithAPrimary_OrdersTheSelection()
        {
            yield return null;
            MakeActive(unitA);
            pause.Pause();
            yield return LeftClickAt(ScreenPointOf(unitB));
            yield return LeftClickAt(ScreenPointOf(GroundPoint));

            Assert.That(unitB.Unit.CurrentCommand, Is.TypeOf<MoveCommand>());
            Assert.That(unitA.Unit.CurrentCommand, Is.Null, "Paused clicks must order the selection, not the primary");
        }

        [UnityTest]
        public IEnumerator PausedClickGround_WithNothingSelected_OrdersNobody()
        {
            yield return null;
            MakeActive(unitA);
            pause.Pause();
            Assert.That(selection.Selected, Is.Empty, "Precondition: nothing selected");
            yield return LeftClickAt(ScreenPointOf(GroundPoint));

            foreach (var unit in new[] { unitA, unitB, unitC })
                Assert.That(unit.Unit.CurrentCommand, Is.Null, $"{unit.name}: Paused clicks order the selection only, never the active character");
        }

        [UnityTest]
        public IEnumerator RealTimeClickGround_WithTheActiveUnitDisabled_AndNothingSelected_OrdersNobody()
        {
            yield return null;
            MakeActive(unitA);
            unitA.Unit.enabled = false;
            Assert.That(selection.Selected, Is.Empty, "Precondition: nothing selected");
            yield return LeftClickAt(ScreenPointOf(GroundPoint));

            foreach (var unit in new[] { unitA, unitB, unitC })
                Assert.That(unit.Unit.CurrentCommand, Is.Null, $"{unit.name} must not be ordered");
        }

        [UnityTest]
        public IEnumerator RealTimeClickFriendly_WithAPrimary_SelectsItAndOrdersNobody()
        {
            yield return null;
            MakeActive(unitA);
            yield return LeftClickAt(ScreenPointOf(unitB));

            Assert.That(selection.Selected, Is.EqualTo(new[] { unitB }));
            Assert.That(unitA.Unit.CurrentCommand, Is.Null);
            Assert.That(unitB.Unit.CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator RealTimeClickOnThePrimary_SelectsIt()
        {
            yield return null;
            MakeActive(unitA);
            yield return LeftClickAt(ScreenPointOf(unitA));

            Assert.That(selection.Selected, Is.EqualTo(new[] { unitA }));
            Assert.That(unitA.Unit.CurrentCommand, Is.Null, "Clicking the primary character must not order it onto itself");
        }

        [UnityTest]
        public IEnumerator RealTimeBoxDrag_WithAPrimary_StillSelects()
        {
            yield return null;
            MakeActive(unitA);
            var (from, to) = BoxAround(unitA, unitB);
            yield return LeftDrag(from, to);

            Assert.That(selection.Selected, Is.EquivalentTo(new[] { unitA, unitB }));
            Assert.That(unitA.Unit.CurrentCommand, Is.Null);
            Assert.That(unitB.Unit.CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator RealTimeX_WithAPrimary_StopsTheSelectionNotThePrimary()
        {
            yield return null;
            MakeActive(unitA);
            yield return LeftClickAt(ScreenPointOf(GroundPoint));          // the primary character walks
            pause.Pause();
            yield return LeftClickAt(ScreenPointOf(unitC));
            yield return LeftClickAt(ScreenPointOf(OtherGroundPoint));     // the selected companion walks
            pause.Resume();
            yield return null;
            Assert.That(unitC.Unit.CurrentCommand, Is.TypeOf<MoveCommand>(), "Precondition: the companion has an order");

            yield return Tap(keyboard.xKey);

            Assert.That(unitC.Unit.CurrentCommand, Is.Null, "X did not stop the selected companion");
            Assert.That(unitA.Unit.CurrentCommand, Is.TypeOf<MoveCommand>(), "X stopped the unselected primary character");
        }

        [UnityTest]
        public IEnumerator RealTimeClick_WithTheActiveUnitDisabled_AndASelection_OrdersTheSelection()
        {
            yield return null;
            MakeActive(unitA);
            unitA.Unit.enabled = false;
            yield return LeftClickAt(ScreenPointOf(unitB));
            yield return LeftClickAt(ScreenPointOf(GroundPoint));

            Assert.That(unitB.Unit.CurrentCommand, Is.TypeOf<MoveCommand>(), "With a selection, clicks order the selection whether or not the active unit can act");
            Assert.That(unitA.Unit.CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator RealTimeClickDummy_AfterSwitchingTheActiveCharacter_TheNewOneAttacks()
        {
            yield return null;
            MakeActive(unitA);
            activeCharacter.SetUnit(unitC.Unit);
            yield return LeftClickAt(ScreenPointOf(dummy));

            Assert.That(unitC.Unit.CurrentCommand, Is.TypeOf<AttackCommand>(), "The attack must come from the new active character");
            Assert.That(((AttackCommand)unitC.Unit.CurrentCommand).Target, Is.SameAs(dummy));
            Assert.That(unitA.Unit.CurrentCommand, Is.Null, "The previous active character must not attack");
        }

        [UnityTest]
        public IEnumerator ClickNearACoverPoint_OrdersTheSelectionIntoCover()
        {
            yield return LeftClickAt(ScreenPointOf(unitA));
            Assert.That(selection.Selected, Is.EqualTo(new[] { unitA }), "Precondition: unit A is selected");
            Assert.That(input.IsCoverWired, Is.True);

            yield return LeftClickAt(ScreenPointOf(CoverGroundPoint + new Vector3(0.4f, 0f, 0f)));

            Assert.That(unitA.Unit.CurrentCommand, Is.TypeOf<MoveToCoverCommand>(), "A click within 1 m of a cover point is a cover order");
            Assert.That(((MoveToCoverCommand)unitA.Unit.CurrentCommand).Point, Is.SameAs(coverPoint));
            Assert.That(unitA.Unit.Cover.Status, Is.EqualTo(CoverStatus.Reserved));
            Assert.That(unitB.Unit.CurrentCommand, Is.Null);
        }
    }
}
#endif
