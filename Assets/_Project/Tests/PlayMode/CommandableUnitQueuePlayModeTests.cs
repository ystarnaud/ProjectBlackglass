using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CommandableUnitQueuePlayModeTests
    {
        TestWorld world;

        [SetUp]
        public void SetUp() => world = new TestWorld();

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        [UnityTest]
        public IEnumerator AppendedMoves_RunInOrder()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(-6f, 0f, -6f));
            yield return null;
            var a = new Vector3(-6f, 0f, 6f);
            var b = new Vector3(6f, 0f, 6f);

            Assert.That(unit.Issue(new MoveCommand(a)), Is.True);
            Assert.That(unit.Issue(new MoveCommand(b), IssueMode.Append), Is.True);
            Assert.That(unit.PendingCommands.Count, Is.EqualTo(1));

            yield return TestWorld.WaitUntil(() => unit.PendingCommands.Count == 0, 10f);
            Assert.That(unit.PendingCommands, Is.Empty, "First move never finished");
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, a), Is.LessThan(0.5f), "Second move started before the unit reached the first destination");

            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 10f);
            Assert.That(unit.CurrentCommand, Is.Null, "Second move never finished");
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, b), Is.LessThan(0.3f));
        }

        [UnityTest]
        public IEnumerator OrdersQueuedWhilePaused_WaitThenRunInOrder()
        {
            world.CreateEnvironment();
            var pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            var unit = world.CreateUnit(new Vector3(-6f, 0f, -6f));
            yield return null;
            var start = unit.transform.position;
            var a = new MoveCommand(new Vector3(-6f, 0f, 4f));
            var b = new MoveCommand(new Vector3(4f, 0f, 4f));

            pause.Pause();
            Assert.That(unit.Issue(a), Is.True);
            Assert.That(unit.Issue(b, IssueMode.Append), Is.True);
            yield return new WaitForSecondsRealtime(1f);

            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, start), Is.LessThan(0.01f), "Unit moved while paused");
            Assert.That(unit.CurrentCommand, Is.SameAs(a));
            Assert.That(unit.PendingCommands, Is.EqualTo(new UnitCommand[] { b }));

            pause.Resume();
            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == b, 10f);
            Assert.That(unit.CurrentCommand, Is.SameAs(b), "First move never finished");
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, a.Destination), Is.LessThan(0.5f));

            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 10f);
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, b.Destination), Is.LessThan(0.3f));
        }

        [UnityTest]
        public IEnumerator Stop_ClearsOrdersAndHaltsTheUnit()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(-6f, 0f, -6f));
            yield return null;
            unit.Issue(new MoveCommand(new Vector3(6f, 0f, 6f)));
            unit.Issue(new MoveCommand(new Vector3(-6f, 0f, 6f)), IssueMode.Append);
            yield return new WaitForSeconds(0.5f);

            Assert.That(unit.Issue(new StopCommand()), Is.True);
            Assert.That(unit.CurrentCommand, Is.Null);
            Assert.That(unit.PendingCommands, Is.Empty);

            var stoppedAt = unit.transform.position;
            yield return new WaitForSeconds(0.5f);
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, stoppedAt), Is.LessThan(0.5f));
        }

        [UnityTest]
        public IEnumerator QueuedAttackThenMove_KillsTargetThenWalks()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(0f, 0f, -4f));
            var dummy = world.CreateDummy(new Vector3(0f, 0f, 4f));
            yield return null;
            var destination = new Vector3(-5f, 0f, -5f);

            Assert.That(unit.Issue(new AttackCommand(dummy)), Is.True);
            Assert.That(unit.Issue(new MoveCommand(destination), IssueMode.Append), Is.True);

            yield return TestWorld.WaitUntil(() => !dummy.IsAlive && unit.CurrentCommand == null, 20f);

            Assert.That(dummy.IsAlive, Is.False);
            Assert.That(unit.CurrentCommand, Is.Null, "Queued move never finished");
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, destination), Is.LessThan(0.3f));
        }

        [UnityTest]
        public IEnumerator QueuedAttack_OnTargetThatDiedBeforeItsTurn_IsSkipped()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(-6f, 0f, -6f));
            var dummy = world.CreateDummy(new Vector3(6f, 0f, 6f));
            yield return null;
            var last = new Vector3(6f, 0f, -6f);

            unit.Issue(new MoveCommand(new Vector3(-6f, 0f, 0f)));
            Assert.That(unit.Issue(new AttackCommand(dummy), IssueMode.Append), Is.True);
            unit.Issue(new MoveCommand(last), IssueMode.Append);
            dummy.TakeDamage(1000);

            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 20f);

            Assert.That(unit.CurrentCommand, Is.Null, "Queue stalled on the dead target");
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, last), Is.LessThan(0.3f));
        }

        [UnityTest]
        public IEnumerator MoveToWalledOffPoint_Completes_AndTheNextOrderRuns()
        {
            // A closed box of walls around (6, 0, 6): the floor inside is NavMesh, but unreachable from outside.
            world.CreateEnvironment(
                (new Vector3(6f, 1f, 8.5f), new Vector3(6f, 2f, 1f)),
                (new Vector3(6f, 1f, 3.5f), new Vector3(6f, 2f, 1f)),
                (new Vector3(3.5f, 1f, 6f), new Vector3(1f, 2f, 6f)),
                (new Vector3(8.5f, 1f, 6f), new Vector3(1f, 2f, 6f)));
            var unit = world.CreateUnit(new Vector3(-6f, 0f, -6f));
            yield return null;
            var next = new Vector3(-6f, 0f, 0f);

            Assert.That(unit.Issue(new MoveCommand(new Vector3(6f, 0f, 6f))), Is.True);
            Assert.That(unit.Issue(new MoveCommand(next), IssueMode.Append), Is.True);

            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 25f);

            Assert.That(unit.CurrentCommand, Is.Null, "The walled-off move never completed, so the queue stalled");
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, next), Is.LessThan(0.3f));
        }

        [UnityTest]
        public IEnumerator TwoUnitsMovedToTheSamePoint_BothMovesComplete()
        {
            world.CreateEnvironment();
            var first = world.CreateUnit(new Vector3(-6f, 0f, -6f));
            var second = world.CreateUnit(new Vector3(6f, 0f, -6f));
            yield return null;
            var shared = new Vector3(0f, 0f, 0f);
            var firstNext = new Vector3(-6f, 0f, 6f);
            var secondNext = new Vector3(6f, 0f, 6f);

            Assert.That(first.Issue(new MoveCommand(shared)), Is.True);
            Assert.That(first.Issue(new MoveCommand(firstNext), IssueMode.Append), Is.True);
            Assert.That(second.Issue(new MoveCommand(shared)), Is.True);
            Assert.That(second.Issue(new MoveCommand(secondNext), IssueMode.Append), Is.True);

            yield return TestWorld.WaitUntil(() => first.CurrentCommand == null && second.CurrentCommand == null, 20f);

            Assert.That(first.CurrentCommand, Is.Null, "First unit's queue stalled");
            Assert.That(second.CurrentCommand, Is.Null, "Second unit's queue stalled");
            Assert.That(TestWorld.HorizontalDistance(first.transform.position, firstNext), Is.LessThan(0.3f));
            Assert.That(TestWorld.HorizontalDistance(second.transform.position, secondNext), Is.LessThan(0.3f));
        }

        [UnityTest]
        public IEnumerator Move_OntoAnIdleUnit_Completes_AndTheNextOrderRuns()
        {
            world.CreateEnvironment();
            var blocker = world.CreateUnit(new Vector3(0f, 0f, 0f));
            var unit = world.CreateUnit(new Vector3(-6f, 0f, -6f));
            yield return null;
            var next = new Vector3(6f, 0f, -6f);

            Assert.That(unit.Issue(new MoveCommand(blocker.transform.position)), Is.True);
            Assert.That(unit.Issue(new MoveCommand(next), IssueMode.Append), Is.True);

            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 20f);

            Assert.That(unit.CurrentCommand, Is.Null, "The move onto the idle unit never completed, so the queue stalled");
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, next), Is.LessThan(0.3f));
        }

        [UnityTest]
        public IEnumerator Append_MoveOffNavMesh_IsRejectedWithWarning()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(-6f, 0f, -6f));
            yield return null;
            var first = new MoveCommand(new Vector3(6f, 0f, 6f));
            unit.Issue(first);

            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("cannot queue a move"));
            Assert.That(unit.Issue(new MoveCommand(new Vector3(100f, 0f, 100f)), IssueMode.Append), Is.False);

            Assert.That(unit.CurrentCommand, Is.SameAs(first));
            Assert.That(unit.PendingCommands, Is.Empty);
        }
    }
}
