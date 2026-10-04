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
        TacticalPause pause;
        ActiveCharacter active;
        DirectControlInput input;

        public override void Setup()
        {
            base.Setup();
            keyboard = InputSystem.AddDevice<Keyboard>();
            world = new TestWorld();

            world.CreateEnvironment();
            primaryUnit = world.CreateUnit(new Vector3(-6f, 0f, -6f));
            companion = world.CreateUnit(new Vector3(6f, 0f, -6f));

            var cameraObject = world.Track(new GameObject("Camera"));
            cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 25f, -20f), Quaternion.Euler(50f, 0f, 0f));
            viewCamera = cameraObject.AddComponent<Camera>();

            var systems = world.Track(new GameObject("Systems"));
            systems.SetActive(false);
            pause = systems.AddComponent<TacticalPause>();
            active = systems.AddComponent<ActiveCharacter>();
            active.Initialize(primaryUnit, pause);
            var actions = TestControls.Load();
            input = systems.AddComponent<DirectControlInput>();
            input.Initialize(active, viewCamera,
                TestControls.Ref(actions, "Character/Move"),
                TestControls.Ref(actions, "Character/Takeover"));
            systems.SetActive(true);
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

        IEnumerator Hold(KeyControl key, float seconds)
        {
            Press(key);
            yield return new WaitForSecondsRealtime(seconds);
            Release(key);
            yield return null;
        }

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
        public IEnumerator PrimaryUnitDeactivated_WhileDriving_StopsDrivingWithoutErrors()
        {
            yield return null;
            active.SetTakeover(true);
            yield return null;
            Press(keyboard.wKey);
            yield return new WaitForSecondsRealtime(0.1f);

            primaryUnit.gameObject.SetActive(false);
            yield return new WaitForSecondsRealtime(0.2f);

            Assert.That(active.IsDriving, Is.False);
            Assert.That(primaryUnit.MoveIntent, Is.EqualTo(Vector3.zero));
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
    }
}
#endif
