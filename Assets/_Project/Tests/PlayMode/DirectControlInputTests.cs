#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class DirectControlInputTests : InputTestFixture
    {
        Keyboard keyboard;
        TestWorld world;
        Camera viewCamera;
        CommandableUnit primaryUnit;
        CommandableUnit companion;
        CommandableUnit third;
        UnitSelection selection;
        TacticalPause pause;
        ActiveCharacter active;
        DirectControlInput input;

        public override void Setup()
        {
            base.Setup();
            keyboard = InputSystem.AddDevice<Keyboard>();
            world = new TestWorld();

            world.CreateEnvironment();
            primaryUnit = world.CreateFriendly(new Vector3(-6f, 0f, -6f)).Unit;
            companion = world.CreateFriendly(new Vector3(6f, 0f, -6f)).Unit;
            third = world.CreateFriendly(new Vector3(0f, 0f, -12f)).Unit;

            var cameraObject = world.Track(new GameObject("Camera"));
            cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 25f, -20f), Quaternion.Euler(50f, 0f, 0f));
            viewCamera = cameraObject.AddComponent<Camera>();

            var systems = world.Track(new GameObject("Systems"));
            systems.SetActive(false);
            pause = systems.AddComponent<TacticalPause>();
            selection = systems.AddComponent<UnitSelection>();
            selection.Initialize(SelectableOf(primaryUnit), SelectableOf(companion), SelectableOf(third));
            active = systems.AddComponent<ActiveCharacter>();
            active.Initialize(primaryUnit, pause, selection);
            var actions = TestControls.Load();
            input = systems.AddComponent<DirectControlInput>();
            input.Initialize(active, viewCamera,
                TestControls.Ref(actions, "Character/Move"),
                TestControls.Ref(actions, "Character/Takeover"),
                selection,
                TestControls.Ref(actions, "Character/CycleCharacter"),
                TestControls.Ref(actions, "Character/CycleReverse"),
                TestControls.Ref(actions, "Character/ToggleFollow"));
            systems.SetActive(true);
        }

        static SelectableUnit SelectableOf(CommandableUnit unit) => unit.GetComponent<SelectableUnit>();

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

        IEnumerator Hold(KeyControl key, float seconds)
        {
            Press(key);
            yield return new WaitForSecondsRealtime(seconds);
            Release(key);
            yield return null;
        }

        IEnumerator ShiftTab()
        {
            Press(keyboard.leftShiftKey);
            yield return null;
            yield return Tap(keyboard.tabKey);
            Release(keyboard.leftShiftKey);
            yield return null;
        }

        static float Moved(CommandableUnit unit, Vector3 from) => TestWorld.HorizontalDistance(unit.transform.position, from);

        // Two orders each for the active character and the companion, on paths that never cross.
        (MoveCommand primaryFirst, MoveCommand primarySecond, MoveCommand companionFirst, MoveCommand companionSecond) QueueOrdersForBoth()
        {
            var orders = (new MoveCommand(new Vector3(-6f, 0f, 2f)), new MoveCommand(new Vector3(-12f, 0f, 2f)),
                new MoveCommand(new Vector3(6f, 0f, 2f)), new MoveCommand(new Vector3(12f, 0f, 2f)));
            Assert.That(primaryUnit.Issue(orders.Item1), Is.True);
            Assert.That(primaryUnit.Issue(orders.Item2, IssueMode.Append), Is.True);
            Assert.That(companion.Issue(orders.Item3), Is.True);
            Assert.That(companion.Issue(orders.Item4, IssueMode.Append), Is.True);
            return orders;
        }

        // Waits until both units are idle, recording where each was when its first order finished.
        IEnumerator RunBothQueuesToTheEnd(Vector3[] positionsWhenFirstFinished)
        {
            var units = new[] { primaryUnit, companion };
            var recorded = new bool[units.Length];
            var deadline = Time.realtimeSinceStartup + 20f;
            while ((primaryUnit.CurrentCommand != null || companion.CurrentCommand != null) && Time.realtimeSinceStartup < deadline)
            {
                for (var i = 0; i < units.Length; i++)
                {
                    if (!recorded[i] && units[i].PendingCommands.Count == 0)
                    {
                        positionsWhenFirstFinished[i] = units[i].transform.position;
                        recorded[i] = true;
                    }
                }
                yield return null;
            }
        }

        void AssertRanInOrder(Vector3[] positionsWhenFirstFinished, MoveCommand primaryFirst, MoveCommand primarySecond,
            MoveCommand companionFirst, MoveCommand companionSecond)
        {
            Assert.That(primaryUnit.CurrentCommand, Is.Null, "The active character's orders never finished");
            Assert.That(companion.CurrentCommand, Is.Null, "The companion's orders never finished");
            Assert.That(TestWorld.HorizontalDistance(positionsWhenFirstFinished[0], primaryFirst.Destination), Is.LessThan(0.6f),
                "The active character's second order started before it reached the first destination");
            Assert.That(TestWorld.HorizontalDistance(positionsWhenFirstFinished[1], companionFirst.Destination), Is.LessThan(0.6f),
                "The companion's second order started before it reached the first destination");
            Assert.That(TestWorld.HorizontalDistance(primaryUnit.transform.position, primarySecond.Destination), Is.LessThan(0.3f));
            Assert.That(TestWorld.HorizontalDistance(companion.transform.position, companionSecond.Destination), Is.LessThan(0.3f));
        }

        [UnityTest]
        public IEnumerator F_TogglesFollow_AlsoWhilePaused_AndChangesNothingElse()
        {
            Assert.That(active.IsFollowOn, Is.True);
            Assert.That(input.IsFollowWired, Is.True);
            yield return Tap(keyboard.fKey);
            Assert.That(active.IsFollowOn, Is.False);
            yield return Tap(keyboard.fKey);
            Assert.That(active.IsFollowOn, Is.True);

            pause.Pause();
            yield return Tap(keyboard.fKey);
            Assert.That(active.IsFollowOn, Is.False, "F works while paused");
            Assert.That(active.IsTakeoverOn, Is.False, "F is not V");
            Assert.That(active.Unit, Is.SameAs(primaryUnit), "F is not Tab");
        }

        [UnityTest]
        public IEnumerator V_TogglesTakeover()
        {
            yield return null;
            Assert.That(active.IsTakeoverOn, Is.False);
            yield return Tap(keyboard.vKey);
            Assert.That(active.IsTakeoverOn, Is.True);
            yield return Tap(keyboard.vKey);
            Assert.That(active.IsTakeoverOn, Is.False);
        }

        [UnityTest]
        public IEnumerator V_WhilePaused_TakesEffectOnResume()
        {
            yield return null;
            pause.Pause();
            yield return Tap(keyboard.vKey);
            Assert.That(active.IsTakeoverOn, Is.True);
            Assert.That(active.IsDriving, Is.False);

            pause.Resume();
            Assert.That(active.IsDriving, Is.True);
        }

        [UnityTest]
        public IEnumerator TakeoverW_MovesThePrimaryForward_AndTheCompanionKeepsItsOrders()
        {
            yield return null;
            var first = new MoveCommand(new Vector3(6f, 0f, 8f));
            var second = new MoveCommand(new Vector3(0f, 0f, 8f));
            companion.Issue(first);
            companion.Issue(second, IssueMode.Append);
            active.SetTakeover(true);
            yield return null;
            var start = primaryUnit.transform.position;

            yield return Hold(keyboard.wKey, 0.5f);

            var travelled = primaryUnit.transform.position - start;
            Assert.That(travelled.z, Is.GreaterThan(1f), "W did not move the active character forward");
            Assert.That(Mathf.Abs(travelled.x), Is.LessThan(0.3f));
            Assert.That(companion.CurrentCommand, Is.SameAs(first), "Takeover cancelled the companion's order");
            Assert.That(companion.PendingCommands, Is.EqualTo(new UnitCommand[] { second }));
        }

        [UnityTest]
        public IEnumerator TakeoverW_ClearsThePrimarysOrders()
        {
            yield return null;
            primaryUnit.Issue(new MoveCommand(new Vector3(-14f, 0f, -6f)));
            primaryUnit.Issue(new MoveCommand(new Vector3(-14f, 0f, 6f)), IssueMode.Append);
            active.SetTakeover(true);
            yield return null;

            yield return Hold(keyboard.wKey, 0.2f);

            Assert.That(primaryUnit.CurrentCommand, Is.Null);
            Assert.That(primaryUnit.PendingCommands, Is.Empty);
        }

        [UnityTest]
        public IEnumerator FreeMode_W_DoesNotMoveThePrimary()
        {
            yield return null;
            var start = primaryUnit.transform.position;
            Press(keyboard.wKey);
            yield return new WaitForSecondsRealtime(0.4f);

            Assert.That(primaryUnit.MoveIntent, Is.EqualTo(Vector3.zero));
            Assert.That(TestWorld.HorizontalDistance(primaryUnit.transform.position, start), Is.LessThan(0.01f));
            Release(keyboard.wKey);
            yield return null;
        }

        [UnityTest]
        public IEnumerator W_IsRelativeToTheCameraYaw()
        {
            yield return null;
            viewCamera.transform.rotation = Quaternion.Euler(50f, 90f, 0f);
            active.SetTakeover(true);
            yield return null;
            var start = primaryUnit.transform.position;

            yield return Hold(keyboard.wKey, 0.4f);

            var travelled = primaryUnit.transform.position - start;
            Assert.That(travelled.x, Is.GreaterThan(1f), "With the camera facing +x, W must move the character toward +x");
            Assert.That(Mathf.Abs(travelled.z), Is.LessThan(0.3f));
        }

        [UnityTest]
        public IEnumerator KeysHeldAcrossResume_DoNotClearOrdersUntilPressedAgain()
        {
            yield return null;
            active.SetTakeover(true);
            pause.Pause();
            var first = new MoveCommand(new Vector3(-6f, 0f, 8f));
            var second = new MoveCommand(new Vector3(6f, 0f, 8f));
            primaryUnit.Issue(first);
            primaryUnit.Issue(second, IssueMode.Append);
            Press(keyboard.wKey);   // for example still held from panning the camera during pause
            yield return null;
            yield return null;

            pause.Resume();
            yield return new WaitForSecondsRealtime(0.3f);
            Assert.That(primaryUnit.CurrentCommand, Is.SameAs(first), "A key held across resume wiped the plan");
            Assert.That(primaryUnit.PendingCommands, Is.EqualTo(new UnitCommand[] { second }));

            Release(keyboard.wKey);
            yield return null;
            yield return Hold(keyboard.wKey, 0.2f);
            Assert.That(primaryUnit.CurrentCommand, Is.Null, "A fresh press must take over");
            Assert.That(primaryUnit.PendingCommands, Is.Empty);
        }

        [UnityTest]
        public IEnumerator V_WhileWIsHeld_DoesNotSteerUntilWIsPressedAgain()
        {
            yield return null;
            Press(keyboard.wKey);   // panning the camera in free mode
            yield return null;
            yield return Tap(keyboard.vKey);
            Assert.That(active.IsDriving, Is.True);
            var start = primaryUnit.transform.position;
            yield return new WaitForSecondsRealtime(0.3f);
            Assert.That(TestWorld.HorizontalDistance(primaryUnit.transform.position, start), Is.LessThan(0.01f),
                "Turning takeover on while W was held made the character run");

            Release(keyboard.wKey);
            yield return null;
            yield return Hold(keyboard.wKey, 0.3f);
            Assert.That(TestWorld.HorizontalDistance(primaryUnit.transform.position, start), Is.GreaterThan(0.5f));
        }

        [UnityTest]
        public IEnumerator Pausing_ZeroesTheIntent()
        {
            yield return null;
            active.SetTakeover(true);
            yield return null;
            Press(keyboard.wKey);
            yield return new WaitForSecondsRealtime(0.1f);
            Assert.That(primaryUnit.MoveIntent, Is.Not.EqualTo(Vector3.zero));

            pause.Pause();
            yield return null;
            Assert.That(primaryUnit.MoveIntent, Is.EqualTo(Vector3.zero));
            Release(keyboard.wKey);
            yield return null;
        }

        [UnityTest]
        public IEnumerator DisablingTheInput_WhileWIsHeld_ZeroesTheIntent()
        {
            yield return null;
            active.SetTakeover(true);
            yield return null;
            Press(keyboard.wKey);
            yield return new WaitForSecondsRealtime(0.1f);
            Assert.That(primaryUnit.MoveIntent, Is.Not.EqualTo(Vector3.zero));

            input.enabled = false;
            Assert.That(primaryUnit.MoveIntent, Is.EqualTo(Vector3.zero), "A disabled input left the character steering");
            yield return null;
            var stoppedAt = primaryUnit.transform.position;
            yield return new WaitForSecondsRealtime(0.2f);
            Assert.That(TestWorld.HorizontalDistance(primaryUnit.transform.position, stoppedAt), Is.LessThan(0.05f));
            Release(keyboard.wKey);
            yield return null;
        }

        [UnityTest]
        public IEnumerator PrimaryUnitDeactivated_WhileDriving_HandsControlToTheNextFriendlyWithoutErrors()
        {
            yield return null;
            active.SetTakeover(true);
            yield return null;
            Press(keyboard.wKey);
            yield return new WaitForSecondsRealtime(0.1f);

            primaryUnit.gameObject.SetActive(false);
            yield return new WaitForSecondsRealtime(0.2f);

            Assert.That(active.Unit, Is.SameAs(companion), "Control must pass to the next eligible friendly");
            Assert.That(primaryUnit.MoveIntent, Is.EqualTo(Vector3.zero), "The old unit must not keep an intent");
            Assert.That(active.IsDriving, Is.True, "Takeover stays on, so the new unit can be driven");
            Release(keyboard.wKey);
            yield return null;
            // Any error or exception logged meanwhile fails the test.
        }

        [UnityTest]
        public IEnumerator PauseCycling_KeepsBothQueues_AndRunsThemInOrder()
        {
            yield return null;
            active.SetTakeover(true);   // takeover on, but no keys: resuming must never clear orders
            pause.Pause();
            var (primaryFirst, primarySecond, companionFirst, companionSecond) = QueueOrdersForBoth();

            for (var i = 0; i < 10; i++)
            {
                pause.Resume();
                yield return null;
                pause.Pause();
                yield return new WaitForSecondsRealtime(0.02f);
            }
            Assert.That(primaryUnit.CurrentCommand, Is.Not.Null, "Pause cycling dropped the active character's orders");
            Assert.That(companion.CurrentCommand, Is.Not.Null, "Pause cycling dropped the companion's orders");
            pause.Resume();

            var positions = new Vector3[2];
            yield return RunBothQueuesToTheEnd(positions);
            AssertRanInOrder(positions, primaryFirst, primarySecond, companionFirst, companionSecond);
        }

        [UnityTest]
        public IEnumerator TakeoverCycling_KeepsBothQueues_AndRunsThemInOrder()
        {
            yield return null;
            pause.Pause();
            var (primaryFirst, primarySecond, companionFirst, companionSecond) = QueueOrdersForBoth();

            for (var i = 0; i < 6; i++)
            {
                yield return Tap(keyboard.vKey);
                if (i % 2 == 0)
                {
                    pause.Resume();
                    yield return null;
                    pause.Pause();
                }
            }

            Assert.That(primaryUnit.CurrentCommand, Is.SameAs(primaryFirst));
            Assert.That(primaryUnit.PendingCommands, Is.EqualTo(new UnitCommand[] { primarySecond }));
            Assert.That(companion.CurrentCommand, Is.SameAs(companionFirst));
            Assert.That(companion.PendingCommands, Is.EqualTo(new UnitCommand[] { companionSecond }));
            pause.Resume();

            var positions = new Vector3[2];
            yield return RunBothQueuesToTheEnd(positions);
            AssertRanInOrder(positions, primaryFirst, primarySecond, companionFirst, companionSecond);
        }

        // --- Switching the active character (Tab / Shift+Tab).

        [UnityTest]
        public IEnumerator Tab_MakesTheNextFriendlyActive_SelectsIt_AndWraps()
        {
            yield return null;
            selection.SetSelection(new[] { SelectableOf(primaryUnit), SelectableOf(companion), SelectableOf(third) });

            yield return Tap(keyboard.tabKey);
            Assert.That(active.Unit, Is.SameAs(companion));
            Assert.That(selection.Selected, Is.EqualTo(new[] { SelectableOf(companion) }), "Tab must select only the new active character");
            yield return Tap(keyboard.tabKey);
            Assert.That(active.Unit, Is.SameAs(third));
            yield return Tap(keyboard.tabKey);
            Assert.That(active.Unit, Is.SameAs(primaryUnit), "Tab did not wrap to the first friendly");
            Assert.That(selection.Selected, Is.EqualTo(new[] { SelectableOf(primaryUnit) }));
        }

        [UnityTest]
        public IEnumerator ShiftTab_GoesBackward_AndWraps()
        {
            yield return null;
            yield return ShiftTab();
            Assert.That(active.Unit, Is.SameAs(third), "Shift+Tab did not wrap to the last friendly");
            yield return ShiftTab();
            Assert.That(active.Unit, Is.SameAs(companion));
            yield return ShiftTab();
            Assert.That(active.Unit, Is.SameAs(primaryUnit));
            Assert.That(selection.Selected, Is.EqualTo(new[] { SelectableOf(primaryUnit) }));
        }

        [UnityTest]
        public IEnumerator Tab_SkipsInactiveAndDisabledFriendlies_AndKeepsTheLastEligibleOne()
        {
            yield return null;
            companion.gameObject.SetActive(false);
            yield return Tap(keyboard.tabKey);
            Assert.That(active.Unit, Is.SameAs(third), "Tab did not skip the inactive friendly");

            third.enabled = false;
            yield return Tap(keyboard.tabKey);
            Assert.That(active.Unit, Is.SameAs(primaryUnit), "Tab did not skip the disabled friendly");
            yield return Tap(keyboard.tabKey);
            Assert.That(active.Unit, Is.SameAs(primaryUnit), "With one eligible friendly, Tab must keep it");
            Assert.That(selection.Selected, Is.EqualTo(new[] { SelectableOf(primaryUnit) }));
        }

        [UnityTest]
        public IEnumerator TabWhileDriving_StopsTheOldCharacter_AndTheHeldKeyDrivesAnIdleNewOne()
        {
            yield return null;
            active.SetTakeover(true);
            yield return null;
            Press(keyboard.wKey);
            yield return new WaitForSecondsRealtime(0.2f);
            Assert.That(primaryUnit.MoveIntent, Is.Not.EqualTo(Vector3.zero), "Precondition: W drives the first friendly");

            yield return Tap(keyboard.tabKey);   // W stays held
            Assert.That(active.Unit, Is.SameAs(companion));
            Assert.That(primaryUnit.MoveIntent, Is.EqualTo(Vector3.zero), "The old character kept a stale move intent");
            var oldStoppedAt = primaryUnit.transform.position;
            var newStart = companion.transform.position;
            yield return new WaitForSecondsRealtime(0.4f);

            Assert.That(Moved(primaryUnit, oldStoppedAt), Is.LessThan(0.05f), "The old character kept moving");
            Assert.That(Moved(companion, newStart), Is.GreaterThan(0.5f), "The held key did not carry over to the idle new character");
            Release(keyboard.wKey);
            yield return null;
        }

        [UnityTest]
        public IEnumerator TabWhileWHeld_DoesNotWipeTheNewCharactersOrders_UntilPressedAgain()
        {
            yield return null;
            var first = new MoveCommand(new Vector3(6f, 0f, 8f));
            var second = new MoveCommand(new Vector3(0f, 0f, 8f));
            Assert.That(companion.Issue(first), Is.True);
            Assert.That(companion.Issue(second, IssueMode.Append), Is.True);
            active.SetTakeover(true);
            yield return null;
            Press(keyboard.wKey);
            yield return new WaitForSecondsRealtime(0.2f);

            yield return Tap(keyboard.tabKey);   // W stays held
            yield return new WaitForSecondsRealtime(0.3f);
            Assert.That(active.Unit, Is.SameAs(companion));
            Assert.That(companion.MoveIntent, Is.EqualTo(Vector3.zero), "A held key steered a character that had orders");
            Assert.That(companion.CurrentCommand, Is.SameAs(first), "A held key wiped the new character's orders");
            Assert.That(companion.PendingCommands, Is.EqualTo(new UnitCommand[] { second }));

            Release(keyboard.wKey);
            yield return null;
            yield return Hold(keyboard.wKey, 0.2f);
            Assert.That(companion.CurrentCommand, Is.Null, "A fresh press must take over the new character");
            Assert.That(companion.PendingCommands, Is.Empty);
        }

        [UnityTest]
        public IEnumerator Switching_KeepsTheOldCharactersOrders()
        {
            yield return null;
            var first = new MoveCommand(new Vector3(-6f, 0f, 8f));
            var second = new MoveCommand(new Vector3(-12f, 0f, 8f));
            Assert.That(primaryUnit.Issue(first), Is.True);
            Assert.That(primaryUnit.Issue(second, IssueMode.Append), Is.True);
            active.SetTakeover(true);
            yield return null;
            var start = primaryUnit.transform.position;

            yield return Tap(keyboard.tabKey);
            yield return new WaitForSecondsRealtime(0.3f);

            Assert.That(active.Unit, Is.SameAs(companion));
            Assert.That(primaryUnit.CurrentCommand, Is.SameAs(first), "Switching away cleared the old character's order");
            Assert.That(primaryUnit.PendingCommands, Is.EqualTo(new UnitCommand[] { second }));
            Assert.That(Moved(primaryUnit, start), Is.GreaterThan(0.5f), "The old character stopped following its orders");
        }

        [UnityTest]
        public IEnumerator TabWhilePaused_MovesNothing_KeepsEveryQueue_AndTheNewCharacterDrivesAfterResume()
        {
            yield return null;
            active.SetTakeover(true);
            pause.Pause();
            var (primaryFirst, primarySecond, companionFirst, companionSecond) = QueueOrdersForBoth();
            var units = new[] { primaryUnit, companion, third };
            var starts = new[] { primaryUnit.transform.position, companion.transform.position, third.transform.position };

            yield return Tap(keyboard.tabKey);
            yield return Tap(keyboard.tabKey);
            yield return new WaitForSecondsRealtime(0.3f);

            Assert.That(active.Unit, Is.SameAs(third));
            Assert.That(selection.Selected, Is.EqualTo(new[] { SelectableOf(third) }));
            for (var i = 0; i < units.Length; i++)
                Assert.That(Moved(units[i], starts[i]), Is.LessThan(0.01f), $"Friendly {i} moved while paused");
            Assert.That(primaryUnit.CurrentCommand, Is.SameAs(primaryFirst));
            Assert.That(primaryUnit.PendingCommands, Is.EqualTo(new UnitCommand[] { primarySecond }));
            Assert.That(companion.CurrentCommand, Is.SameAs(companionFirst));
            Assert.That(companion.PendingCommands, Is.EqualTo(new UnitCommand[] { companionSecond }));

            pause.Resume();
            yield return null;
            var thirdStart = third.transform.position;
            yield return Hold(keyboard.wKey, 0.3f);

            Assert.That(Moved(third, thirdStart), Is.GreaterThan(0.5f), "After resume, W must drive the character chosen while paused");
            Assert.That(primaryUnit.CurrentCommand, Is.Not.Null, "Driving the new character cancelled the first friendly's orders");
            Assert.That(companion.CurrentCommand, Is.Not.Null, "Driving the new character cancelled the companion's orders");
        }

        [UnityTest]
        public IEnumerator TabInFreeMode_SwitchesTheActiveCharacter_ButWMovesNobody()
        {
            yield return null;
            yield return Tap(keyboard.tabKey);
            Assert.That(active.Unit, Is.SameAs(companion));
            var units = new[] { primaryUnit, companion, third };
            var starts = new[] { primaryUnit.transform.position, companion.transform.position, third.transform.position };

            Press(keyboard.wKey);
            yield return new WaitForSecondsRealtime(0.3f);
            for (var i = 0; i < units.Length; i++)
            {
                Assert.That(units[i].MoveIntent, Is.EqualTo(Vector3.zero), $"Friendly {i} got a move intent in free mode");
                Assert.That(Moved(units[i], starts[i]), Is.LessThan(0.01f), $"Friendly {i} moved in free mode");
            }
            Release(keyboard.wKey);
            yield return null;
        }

        [UnityTest]
        public IEnumerator RapidTabbing_EndsInAConsistentState_WithoutErrors()
        {
            yield return null;
            active.SetTakeover(true);
            yield return null;
            Press(keyboard.wKey);
            yield return null;

            for (var i = 0; i < 50; i++)
            {
                var reverse = i % 3 == 0;
                if (reverse)
                {
                    Press(keyboard.leftShiftKey);
                    yield return null;
                }
                yield return Tap(keyboard.tabKey);
                if (reverse)
                {
                    Release(keyboard.leftShiftKey);
                    yield return null;
                }
            }

            // 33 steps forward and 17 back: net +16 over three friendlies, one step forward from the first.
            Assert.That(active.Unit, Is.SameAs(companion));
            Assert.That(selection.Selected, Is.EqualTo(new[] { SelectableOf(companion) }));
            Assert.That(primaryUnit.MoveIntent, Is.EqualTo(Vector3.zero), "A character switched away from kept its intent");
            Assert.That(third.MoveIntent, Is.EqualTo(Vector3.zero), "A character switched away from kept its intent");
            Release(keyboard.wKey);
            yield return null;
            Assert.That(companion.MoveIntent, Is.EqualTo(Vector3.zero));
            // Any error or exception logged meanwhile fails the test.
        }

        [UnityTest]
        public IEnumerator ActiveUnitDestroyed_WhileDriving_ControlPassesOn_AndTabMovesFurther()
        {
            yield return null;
            active.SetTakeover(true);
            yield return null;
            Press(keyboard.wKey);
            yield return new WaitForSecondsRealtime(0.1f);

            Object.Destroy(primaryUnit.gameObject);
            yield return new WaitForSecondsRealtime(0.1f);
            Release(keyboard.wKey);
            yield return null;
            Assert.That(active.Unit, Is.SameAs(companion), "A destroyed active unit hands control to the next friendly");

            yield return Tap(keyboard.tabKey);

            Assert.That(active.Unit, Is.SameAs(third));
            Assert.That(selection.Selected, Is.EqualTo(new[] { SelectableOf(third) }));
            // Any error or exception logged meanwhile fails the test.
        }
    }
}
#endif
