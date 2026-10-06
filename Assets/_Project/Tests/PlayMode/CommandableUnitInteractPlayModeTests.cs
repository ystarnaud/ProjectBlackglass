using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class CommandableUnitInteractPlayModeTests
    {
        static readonly Vector3 TerminalGround = new Vector3(6f, 0f, 0f);
        static readonly Vector3 MovePoint = new Vector3(0f, 0f, 8f);

        TestWorld world;
        TacticalPause pause;
        CommandableUnit unit;
        UnitInteractor interactor;
        MissionInteractable terminal;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            world.CreateEnvironment();
            pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            unit = Worker(new Vector3(4f, 0f, 0f));
            interactor = unit.GetComponent<UnitInteractor>();
            terminal = Terminal(TerminalGround, 1f);
        }

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        CommandableUnit Worker(Vector3 ground)
        {
            var worker = world.CreateFighter(ground);
            worker.gameObject.AddComponent<UnitInteractor>();
            return worker;
        }

        // A plain marker cube with no collider (so it carves nothing) at ground + 0.6 m, available at once.
        MissionInteractable Terminal(Vector3 ground, float duration)
        {
            var host = world.Track(GameObject.CreatePrimitive(PrimitiveType.Cube));
            host.name = "TestTerminal";
            host.transform.position = ground + Vector3.up * 0.6f;
            Object.DestroyImmediate(host.GetComponent<Collider>());
            var made = host.AddComponent<MissionInteractable>();
            made.Initialize(1.8f, duration);
            made.SetAvailable(true);
            return made;
        }

        // Long enough that a test can act while the unit is still working.
        void Slow() => terminal.Initialize(1.8f, 6f);

        IEnumerator WorkingStarted(MissionInteractable target, float seconds = 10f) =>
            TestWorld.WaitUntil(() => target.Progress > 0.1f, seconds);

        [UnityTest]
        public IEnumerator Interact_WalksIntoRange_WorksForTheDuration_ThenCompletesAndTheUnitIsIdle()
        {
            var far = Worker(new Vector3(-10f, 0f, 0f));
            yield return null;

            Assert.That(far.Issue(new InteractCommand(terminal)), Is.True);
            Assert.That(far.CurrentCommand, Is.TypeOf<InteractCommand>());
            yield return TestWorld.WaitUntil(() => terminal.IsCompleted, 15f);

            Assert.That(terminal.IsCompleted, Is.True);
            yield return null;
            Assert.That(far.CurrentCommand, Is.Null);
            Assert.That(TestWorld.HorizontalDistance(far.transform.position, terminal.Position), Is.LessThanOrEqualTo(terminal.Range + 0.2f));
            Assert.That(far.GetComponent<UnitInteractor>().IsWorking, Is.False);
            Assert.That(terminal.User == null, Is.True);
        }

        [UnityTest]
        public IEnumerator TheInteractionFreezesDuringTacticalPause_AndResumesWhereItWas()
        {
            terminal.Initialize(1.8f, 1.5f);
            yield return null;
            unit.Issue(new InteractCommand(terminal));
            yield return WorkingStarted(terminal);

            pause.Pause();
            var frozen = terminal.Progress;
            yield return new WaitForSecondsRealtime(0.4f);

            Assert.That(terminal.Progress, Is.EqualTo(frozen).Within(1e-4f), "no progress while paused");
            Assert.That(terminal.User, Is.EqualTo(unit), "the claim is kept through a pause");
            Assert.That(terminal.IsCompleted, Is.False);

            pause.Resume();
            yield return TestWorld.WaitUntil(() => terminal.IsCompleted, 10f);
            Assert.That(terminal.IsCompleted, Is.True);
        }

        [UnityTest]
        public IEnumerator AnInteractOrderIssuedWhilePaused_IsAccepted_ButNothingRunsUntilResume()
        {
            yield return null;
            pause.Pause();

            Assert.That(unit.Issue(new InteractCommand(terminal)), Is.True);
            yield return new WaitForSecondsRealtime(0.3f);

            Assert.That(terminal.Progress, Is.Zero);
            Assert.That(unit.CurrentCommand, Is.TypeOf<InteractCommand>());

            pause.Resume();
            yield return TestWorld.WaitUntil(() => terminal.IsCompleted, 10f);
            Assert.That(terminal.IsCompleted, Is.True);
        }

        [UnityTest]
        public IEnumerator Stop_CancelsTheInteraction_ReleasesTheClaim_AndLosesProgress()
        {
            Slow();
            yield return null;
            unit.Issue(new InteractCommand(terminal));
            yield return WorkingStarted(terminal);

            unit.Issue(new StopCommand());
            yield return null;

            Assert.That(unit.CurrentCommand, Is.Null);
            Assert.That(terminal.Progress, Is.Zero);
            Assert.That(terminal.User == null, Is.True);
            Assert.That(interactor.IsWorking, Is.False);
        }

        [UnityTest]
        public IEnumerator AReplacingOrder_CancelsTheInteraction()
        {
            Slow();
            yield return null;
            unit.Issue(new InteractCommand(terminal));
            yield return WorkingStarted(terminal);

            Assert.That(unit.Issue(new MoveCommand(MovePoint)), Is.True);
            yield return null;

            Assert.That(unit.CurrentCommand, Is.TypeOf<MoveCommand>());
            Assert.That(terminal.Progress, Is.Zero);
            Assert.That(terminal.User == null, Is.True);
        }

        [UnityTest]
        public IEnumerator DirectControlSteering_CancelsTheInteraction()
        {
            Slow();
            yield return null;
            unit.Issue(new InteractCommand(terminal));
            yield return WorkingStarted(terminal);

            unit.SetMoveIntent(Vector3.forward);
            yield return null;
            yield return null;
            unit.SetMoveIntent(Vector3.zero);

            Assert.That(unit.CurrentCommand, Is.Null);
            Assert.That(terminal.Progress, Is.Zero);
            Assert.That(terminal.User == null, Is.True);
        }

        [UnityTest]
        public IEnumerator ADeadUnit_ReleasesTheTerminal_AndTakesNoFurtherOrders()
        {
            Slow();
            yield return null;
            unit.Issue(new InteractCommand(terminal));
            yield return WorkingStarted(terminal);

            var health = unit.GetComponent<Health>();
            health.TakeDamage(health.Max);
            yield return null;

            Assert.That(terminal.Progress, Is.Zero);
            Assert.That(terminal.User == null, Is.True);
            Assert.That(unit.Issue(new InteractCommand(terminal)), Is.False, "a dead unit takes no orders");
        }

        [UnityTest]
        public IEnumerator ATerminalDestroyedMidWork_EndsTheOrder_AndTheNextQueuedOrderRuns()
        {
            Slow();
            yield return null;
            Assert.That(unit.Issue(new InteractCommand(terminal)), Is.True);
            Assert.That(unit.Issue(new MoveCommand(MovePoint), IssueMode.Append), Is.True);
            yield return WorkingStarted(terminal);

            Object.Destroy(terminal.gameObject);
            yield return TestWorld.WaitUntil(() => unit.CurrentCommand is MoveCommand, 3f);
            Assert.That(unit.CurrentCommand, Is.TypeOf<MoveCommand>());
            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 15f);

            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, MovePoint), Is.LessThan(1f));
            Assert.That(interactor.IsWorking, Is.False);
        }

        [UnityTest]
        public IEnumerator ATerminalDestroyedWhileTheUnitWalks_LeavesTheUnitIdle()
        {
            var far = Worker(new Vector3(-10f, 0f, 0f));
            yield return null;
            far.Issue(new InteractCommand(terminal));
            yield return new WaitForSeconds(0.3f);

            Object.Destroy(terminal.gameObject);
            yield return TestWorld.WaitUntil(() => far.CurrentCommand == null, 3f);

            Assert.That(far.CurrentCommand, Is.Null, "the unit must not keep walking toward a vanished terminal");
        }

        [UnityTest]
        public IEnumerator ATerminalMadeUnavailableMidWork_EndsTheOrder_AndRecordsWhy()
        {
            Slow();
            yield return null;
            unit.Issue(new InteractCommand(terminal));
            yield return WorkingStarted(terminal);

            terminal.SetAvailable(false);
            yield return null;
            yield return null;

            Assert.That(unit.CurrentCommand, Is.Null);
            Assert.That(interactor.LastFailure, Is.EqualTo(InteractionFailure.NotAvailable));
            Assert.That(terminal.User == null, Is.True);
        }

        [UnityTest]
        public IEnumerator ACompletedTerminal_RefusesFurtherOrders()
        {
            terminal.Initialize(1.8f, 0f);
            yield return null;
            unit.Issue(new InteractCommand(terminal));
            yield return TestWorld.WaitUntil(() => terminal.IsCompleted, 10f);

            Assert.That(unit.Issue(new InteractCommand(terminal)), Is.False);
            Assert.That(unit.Issue(new MoveCommand(MovePoint)), Is.True);
            Assert.That(unit.Issue(new InteractCommand(terminal), IssueMode.Append), Is.False);
        }

        [UnityTest]
        public IEnumerator AnUnreachableTerminal_IsRefused()
        {
            yield return null;
            var offMap = Terminal(new Vector3(100f, 0f, 100f), 1f);

            Assert.That(unit.Issue(new InteractCommand(offMap)), Is.False);
            Assert.That(unit.CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator AnImmediateOrderIsRefused_WhileAnotherUnitWorks_AndTheFirstIsUndisturbed()
        {
            Slow();
            var other = Worker(new Vector3(4f, 0f, -4f));
            yield return null;
            unit.Issue(new InteractCommand(terminal));
            yield return WorkingStarted(terminal);

            Assert.That(other.Issue(new InteractCommand(terminal)), Is.False);
            yield return new WaitForSeconds(0.2f);

            Assert.That(terminal.User, Is.EqualTo(unit));
            Assert.That(unit.CurrentCommand, Is.TypeOf<InteractCommand>());
        }

        [UnityTest]
        public IEnumerator AQueuedInteract_BehindABusyTerminal_FailsCleanlyWhenItsTurnComes()
        {
            Slow();
            var other = Worker(new Vector3(-10f, 0f, 0f));
            yield return null;
            unit.Issue(new InteractCommand(terminal));
            yield return WorkingStarted(terminal);

            Assert.That(other.Issue(new MoveCommand(new Vector3(0f, 0f, 4f))), Is.True);
            Assert.That(other.Issue(new InteractCommand(terminal), IssueMode.Append), Is.True, "queued orders get only the static checks");
            yield return TestWorld.WaitUntil(() => other.CurrentCommand == null, 15f);

            Assert.That(other.CurrentCommand, Is.Null, "the second unit must not get stuck");
            Assert.That(terminal.User, Is.EqualTo(unit));
            Assert.That(unit.CurrentCommand, Is.TypeOf<InteractCommand>());
        }

        [UnityTest]
        public IEnumerator InteractIsQueuedBetweenOtherOrders_MoveThenInteractThenMove()
        {
            terminal.Initialize(1.8f, 0.5f);
            var end = new Vector3(-4f, 0f, 8f);
            yield return null;

            Assert.That(unit.Issue(new MoveCommand(new Vector3(2f, 0f, 2f))), Is.True);
            Assert.That(unit.Issue(new InteractCommand(terminal), IssueMode.Append), Is.True);
            Assert.That(unit.Issue(new MoveCommand(end), IssueMode.Append), Is.True);
            yield return TestWorld.WaitUntil(() => terminal.IsCompleted, 15f);
            Assert.That(terminal.IsCompleted, Is.True);
            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 20f);

            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, end), Is.LessThan(1.2f));
        }

        [UnityTest]
        public IEnumerator AUnitWithoutAnInteractor_RefusesInteract()
        {
            yield return null;
            var plain = world.CreateFighter(new Vector3(4f, 0f, 4f));

            Assert.That(plain.Issue(new InteractCommand(terminal)), Is.False);
            Assert.That(plain.Issue(new InteractCommand(terminal), IssueMode.Append), Is.False);
        }

        [UnityTest]
        public IEnumerator ReissuingInteractOnTheTerminalBeingWorked_KeepsTheProgress()
        {
            Slow();
            yield return null;
            unit.Issue(new InteractCommand(terminal));
            yield return WorkingStarted(terminal);
            var before = terminal.Progress;

            Assert.That(unit.Issue(new InteractCommand(terminal)), Is.True);
            yield return null;

            Assert.That(terminal.Progress, Is.GreaterThanOrEqualTo(before));
            Assert.That(terminal.User, Is.EqualTo(unit));
        }
    }
}
