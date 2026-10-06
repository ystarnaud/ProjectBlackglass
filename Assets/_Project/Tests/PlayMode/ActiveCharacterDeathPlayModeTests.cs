#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class ActiveCharacterDeathPlayModeTests : InputTestFixture
    {
        Keyboard keyboard;
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
            world = new TestWorld();
            world.CreateEnvironment();
            first = world.CreateFriendlyFighter(new Vector3(-6f, 0f, -6f)).Unit;
            second = world.CreateFriendlyFighter(new Vector3(6f, 0f, -6f)).Unit;
            third = world.CreateFriendlyFighter(new Vector3(0f, 0f, -12f)).Unit;
            first.name = "First";
            second.name = "Second";
            third.name = "Third";

            var cameraObject = world.Track(new GameObject("Camera"));
            cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 25f, -20f), Quaternion.Euler(50f, 0f, 0f));
            var viewCamera = cameraObject.AddComponent<Camera>();

            var systems = world.Track(new GameObject("Systems"));
            systems.SetActive(false);
            pause = systems.AddComponent<TacticalPause>();
            selection = systems.AddComponent<UnitSelection>();
            selection.Initialize(SelectableOf(first), SelectableOf(second), SelectableOf(third));
            active = systems.AddComponent<ActiveCharacter>();
            active.Initialize(first, pause, selection);
            var actions = TestControls.Load();
            var input = systems.AddComponent<DirectControlInput>();
            input.Initialize(active, viewCamera,
                TestControls.Ref(actions, "Character/Move"),
                TestControls.Ref(actions, "Character/ToggleCharacterControl"),
                selection,
                TestControls.Ref(actions, "Character/NextCharacter"),
                TestControls.Ref(actions, "Character/CycleReverse"));
            systems.SetActive(true);
        }

        static SelectableUnit SelectableOf(CommandableUnit unit) => unit.GetComponent<SelectableUnit>();

        static void Kill(CommandableUnit unit)
        {
            var health = unit.GetComponent<Health>();
            health.TakeDamage(health.Max);
        }

        public override void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
            base.TearDown();
        }

        IEnumerator Tap(KeyControl key)
        {
            Press(key);
            yield return null;
            Release(key);
            yield return null;
        }

        [UnityTest]
        public IEnumerator ControlledCharacterDies_WhileDriving_ControlPassesToTheNextFriendly()
        {
            yield return Tap(keyboard.vKey);
            Press(keyboard.wKey);
            yield return new WaitForSeconds(0.3f);
            Assert.That(first.MoveIntent, Is.Not.EqualTo(Vector3.zero), "Precondition: W drives the first unit");
            var secondStart = second.transform.position;

            Kill(first);
            yield return null;
            yield return null;

            Assert.That(active.Unit, Is.SameAs(second), "Control must pass to the next eligible friendly");
            Assert.That(active.IsTakeoverOn, Is.True);
            Assert.That(first.MoveIntent, Is.EqualTo(Vector3.zero), "The dead unit must not keep an intent");
            Assert.That(first.gameObject.activeSelf, Is.False);

            // The new unit is idle, so the held key carries over (decision 012 hybrid rule).
            yield return new WaitForSeconds(0.5f);
            Release(keyboard.wKey);
            yield return null;
            Assert.That(TestWorld.HorizontalDistance(second.transform.position, secondStart), Is.GreaterThan(0.5f),
                "A held key carries over to an idle new unit");
        }

        [UnityTest]
        public IEnumerator ControlledCharacterDies_WhileWIsHeld_NextFriendlyKeepsItsOrders()
        {
            var plan = new MoveCommand(new Vector3(6f, 0f, 6f));
            Assert.That(second.Issue(plan), Is.True);
            yield return Tap(keyboard.vKey);
            Press(keyboard.wKey);
            yield return new WaitForSeconds(0.3f);

            Kill(first);
            yield return new WaitForSeconds(0.5f);

            Assert.That(active.Unit, Is.SameAs(second));
            Assert.That(second.CurrentCommand, Is.SameAs(plan), "A key held across the hand-over must not wipe the new unit's orders");

            Release(keyboard.wKey);
            yield return null;
            Press(keyboard.wKey);
            yield return new WaitForSeconds(0.3f);
            Release(keyboard.wKey);
            yield return null;
            Assert.That(second.CurrentCommand, Is.Null, "A fresh press after release drives the new unit and clears its orders");
        }

        [UnityTest]
        public IEnumerator DeadFriendlies_AreSkippedByTabAndShiftTab()
        {
            Kill(second);
            yield return null;

            yield return Tap(keyboard.tabKey);
            Assert.That(active.Unit, Is.SameAs(third), "Tab must skip the dead second unit");

            Press(keyboard.leftShiftKey);
            yield return null;
            yield return Tap(keyboard.tabKey);
            Release(keyboard.leftShiftKey);
            yield return null;
            Assert.That(active.Unit, Is.SameAs(first), "Shift+Tab must skip the dead second unit");
        }

        [UnityTest]
        public IEnumerator ControlledCharacterDies_TheSelectionIsNotReplaced()
        {
            selection.SetSelection(new[] { SelectableOf(second), SelectableOf(third) });
            yield return null;

            Kill(first);
            yield return null;
            yield return null;

            Assert.That(active.Unit, Is.SameAs(second));
            Assert.That(selection.Selected, Is.EqualTo(new[] { SelectableOf(second), SelectableOf(third) }),
                "Hand-over on death must leave the selection alone");
        }

        [UnityTest]
        public IEnumerator LastFriendlyDies_LeavesNoControlledCharacter_AndInputStaysSafe()
        {
            yield return Tap(keyboard.vKey);
            Kill(first);
            Kill(second);
            yield return null;
            Assert.That(active.Unit, Is.SameAs(third), "Precondition: the last friendly took over");
            selection.Select(SelectableOf(third));
            yield return null;

            Kill(third);
            yield return null;
            yield return null;

            Assert.That(active.Unit, Is.Null);
            Assert.That(active.HasUnit, Is.False);
            Assert.That(active.IsDriving, Is.False);
            Assert.That(selection.Selected, Is.Empty);

            Press(keyboard.wKey);
            yield return new WaitForSeconds(0.2f);
            Release(keyboard.wKey);
            yield return Tap(keyboard.vKey);
            yield return Tap(keyboard.tabKey);
            Press(keyboard.leftShiftKey);
            yield return Tap(keyboard.tabKey);
            Release(keyboard.leftShiftKey);
            yield return null;

            Assert.That(active.Unit, Is.Null, "Nothing is eligible, so nothing becomes active");
            // Any exception or logged error during the inputs above fails the test on its own.
        }
    }
}
#endif
