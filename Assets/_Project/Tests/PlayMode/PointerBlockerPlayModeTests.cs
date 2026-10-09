#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class PointerBlockerPlayModeTests : InputTestFixture
    {
        static readonly Vector3 GroundPoint = new Vector3(2f, 0f, 2f);

        Mouse mouse;
        TestWorld world;
        Camera viewCamera;
        SelectableUnit unitA;
        SelectableUnit unitB;
        UnitSelection selection;
        PlayerCommandInput input;
        PointerBlocker blocker;
        float split;

        public override void Setup()
        {
            base.Setup();
            mouse = InputSystem.AddDevice<Mouse>();
            world = new TestWorld();
            world.CreateEnvironment();
            unitA = world.CreateFriendly(new Vector3(-8f, 0f, -6f));
            unitB = world.CreateFriendly(new Vector3(-4f, 0f, -6f));

            var cameraObject = world.Track(new GameObject("Camera"));
            cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 25f, -20f), Quaternion.Euler(50f, 0f, 0f));
            viewCamera = cameraObject.AddComponent<Camera>();

            var systems = world.Track(new GameObject("Systems"));
            systems.SetActive(false);
            var pause = systems.AddComponent<TacticalPause>();
            selection = systems.AddComponent<UnitSelection>();
            selection.Initialize(unitA, unitB);
            var activeCharacter = systems.AddComponent<ActiveCharacter>();
            activeCharacter.Initialize(null, pause);
            blocker = systems.AddComponent<PointerBlocker>();
            var actions = TestControls.Load();
            input = systems.AddComponent<PlayerCommandInput>();
            input.Initialize(viewCamera, selection, pause,
                TestControls.Ref(actions, "Commands/Command"),
                TestControls.Ref(actions, "Commands/PointerPosition"),
                TestControls.Ref(actions, "Commands/ToggleTacticalPause"),
                TestControls.Ref(actions, "Commands/QueueModifier"),
                TestControls.Ref(actions, "Commands/Stop"),
                TestControls.Ref(actions, "Commands/Cancel"),
                activeCharacter);
            systems.SetActive(true);
            split = ScreenPointOf(GroundPoint).x - 60f;
        }

        public override void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
            base.TearDown();
        }

        Vector2 ScreenPointOf(Vector3 worldPoint) => viewCamera.WorldToScreenPoint(worldPoint);

        // The "HUD" covers everything left of the split.
        void InstallBlocker() => blocker.SetTest(p => p.x < split);

        IEnumerator LeftClickAt(Vector2 screenPoint)
        {
            Set(mouse.position, screenPoint);
            yield return null;
            Press(mouse.leftButton);
            yield return null;
            Release(mouse.leftButton);
            yield return null;
        }

        [UnityTest]
        public IEnumerator AClickOverTheHud_IssuesNoOrder_AndKeepsTheSelection()
        {
            yield return null;
            input.SetPointerBlocker(blocker);
            InstallBlocker();
            selection.Select(unitA);

            yield return LeftClickAt(new Vector2(split - 30f, ScreenPointOf(GroundPoint).y));

            Assert.That(unitA.Unit.CurrentCommand, Is.Null);
            Assert.That(selection.Selected, Is.EqualTo(new[] { unitA }));
        }

        // A friendly unit right of the split (off the HUD), with a box around it: from (startX, -40) to (+60, +40) pixels.
        SelectableUnit BoxedUnit(out Vector2 around)
        {
            var boxed = world.CreateFriendly(new Vector3(4f, 0f, -2f));
            selection.AddToRoster(boxed);
            around = ScreenPointOf(boxed.transform.position);
            Assert.That(around.x, Is.GreaterThan(split + 40f), "precondition: the unit is off the HUD");
            return boxed;
        }

        IEnumerator DragFrom(float startX, Vector2 around)
        {
            Set(mouse.position, new Vector2(startX, around.y - 40f));
            yield return null;
            Press(mouse.leftButton);
            yield return null;
            Set(mouse.position, new Vector2(around.x + 60f, around.y + 40f));
            yield return null;
            Release(mouse.leftButton);
            yield return null;
        }

        [UnityTest]
        public IEnumerator ADragThatBeganOverTheHud_SelectsNothing()
        {
            yield return null;
            input.SetPointerBlocker(blocker);
            InstallBlocker();
            var boxed = BoxedUnit(out var around);
            selection.Select(unitB);
            yield return null;

            yield return DragFrom(split - 30f, around);

            Assert.That(input.IsDragging, Is.False);
            Assert.That(selection.Selected, Is.EqualTo(new[] { unitB }), $"The box must not have selected {boxed.name} inside it");
        }

        [UnityTest]
        public IEnumerator TheSameDragOffTheHud_SelectsTheUnitInTheBox()
        {
            yield return null;
            input.SetPointerBlocker(blocker);
            InstallBlocker();
            var boxed = BoxedUnit(out var around);
            selection.Select(unitB);
            yield return null;

            yield return DragFrom(split + 10f, around);

            Assert.That(selection.Selected, Is.EqualTo(new[] { boxed }), "the box geometry does catch the unit");
        }

        [UnityTest]
        public IEnumerator AClickOffTheHud_IssuesTheNormalOrder()
        {
            yield return null;
            input.SetPointerBlocker(blocker);
            InstallBlocker();
            selection.Select(unitA);

            yield return LeftClickAt(ScreenPointOf(GroundPoint));

            Assert.That(unitA.Unit.CurrentCommand, Is.TypeOf<MoveCommand>());
        }

        [UnityTest]
        public IEnumerator AReleaseAfterABlockedPress_DoesNotLeakIntoTheNextClick()
        {
            yield return null;
            input.SetPointerBlocker(blocker);
            InstallBlocker();
            selection.Select(unitA);
            yield return LeftClickAt(new Vector2(split - 30f, 100f));

            yield return LeftClickAt(ScreenPointOf(GroundPoint));

            Assert.That(unitA.Unit.CurrentCommand, Is.TypeOf<MoveCommand>());
        }

        [UnityTest]
        public IEnumerator WithNoBlockerAssigned_ClicksBehaveAsBefore()
        {
            yield return null;
            selection.Select(unitA);

            yield return LeftClickAt(new Vector2(split - 30f, ScreenPointOf(GroundPoint).y));
            Assert.That(unitA.Unit.CurrentCommand, Is.TypeOf<MoveCommand>());
        }

        [UnityTest]
        public IEnumerator ResolveAt_ReportsWhatAClickThereWouldTarget_WithoutActing()
        {
            yield return null;
            selection.Select(unitB);

            var ground = input.ResolveAt(ScreenPointOf(GroundPoint));
            var friendly = input.ResolveAt(ScreenPointOf(unitA.transform.position));

            Assert.That(ground.Kind, Is.EqualTo(PointerTargetKind.Ground));
            Assert.That(friendly.Kind, Is.EqualTo(PointerTargetKind.Friendly));
            Assert.That(friendly.Friendly, Is.SameAs(unitA));
            Assert.That(selection.Selected, Is.EqualTo(new[] { unitB }));
            Assert.That(unitB.Unit.CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator PointerScreenPosition_ReadsTheMouse_AndModifierIsHeldReadsTheQueueKey()
        {
            var keyboard = InputSystem.AddDevice<Keyboard>();
            yield return null;
            Set(mouse.position, new Vector2(123f, 45f));
            yield return null;
            Assert.That(input.PointerScreenPosition, Is.EqualTo(new Vector2(123f, 45f)));
            Assert.That(input.ModifierIsHeld, Is.False);
            Press(keyboard.leftShiftKey);
            yield return null;
            Assert.That(input.ModifierIsHeld, Is.True);
        }
    }
}
#endif
