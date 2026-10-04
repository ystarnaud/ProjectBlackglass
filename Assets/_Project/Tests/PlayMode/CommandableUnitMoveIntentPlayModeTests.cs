using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CommandableUnitMoveIntentPlayModeTests
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
        public IEnumerator MoveIntent_ClearsTheCurrentMoveAndPendingOrders_ThenSteers()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(0f, 0f, -6f));
            var dummy = world.CreateDummy(new Vector3(-6f, 0f, 6f));
            yield return null;
            Assert.That(unit.Issue(new MoveCommand(new Vector3(0f, 0f, 8f))), Is.True);
            Assert.That(unit.Issue(new AttackCommand(dummy), IssueMode.Append), Is.True);
            var start = unit.transform.position;

            unit.SetMoveIntent(Vector3.right);
            yield return null;
            Assert.That(unit.CurrentCommand, Is.Null, "The current order was not cleared");
            Assert.That(unit.PendingCommands, Is.Empty, "Pending orders were not cleared");

            yield return new WaitForSeconds(0.5f);
            var travelled = unit.transform.position - start;
            Assert.That(travelled.x, Is.GreaterThan(1f), "The unit is not steering");
            Assert.That(Mathf.Abs(travelled.z), Is.LessThan(0.3f), "The unit kept following the cancelled move");
        }

        [UnityTest]
        public IEnumerator MoveIntent_ClearsAnAttackInProgress()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(0f, 0f, -1f));
            var dummy = world.CreateDummy(new Vector3(0f, 0f, 0.5f));
            yield return null;
            Assert.That(unit.Issue(new AttackCommand(dummy)), Is.True);
            yield return TestWorld.WaitUntil(() => dummy.Current < dummy.Max, 3f);
            Assert.That(dummy.Current, Is.LessThan(dummy.Max), "The attack never landed, so none was in progress");
            var startingHealth = dummy.Current;

            unit.SetMoveIntent(Vector3.left);
            yield return new WaitForSeconds(2.5f);

            Assert.That(unit.CurrentCommand, Is.Null);
            Assert.That(unit.transform.position.x, Is.LessThan(-1f), "The unit is not steering");
            Assert.That(dummy.Current, Is.EqualTo(startingHealth), "The cancelled attack still hit");
        }

        [UnityTest]
        public IEnumerator ZeroMoveIntent_LeavesOrdersAlone()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(0f, 0f, -6f));
            yield return null;
            var move = new MoveCommand(new Vector3(0f, 0f, 8f));
            var next = new MoveCommand(new Vector3(6f, 0f, 8f));
            unit.Issue(move);
            unit.Issue(next, IssueMode.Append);

            unit.SetMoveIntent(Vector3.zero);
            yield return new WaitForSeconds(0.5f);

            Assert.That(unit.CurrentCommand, Is.SameAs(move));
            Assert.That(unit.PendingCommands, Is.EqualTo(new UnitCommand[] { next }));
            Assert.That(unit.transform.position.z, Is.GreaterThan(-5f), "The move stopped running");
        }

        [UnityTest]
        public IEnumerator MoveIntentOnOneUnit_LeavesAnotherUnitsOrdersAlone()
        {
            world.CreateEnvironment();
            var primary = world.CreateUnit(new Vector3(-6f, 0f, -6f));
            var companion = world.CreateUnit(new Vector3(6f, 0f, -6f));
            yield return null;
            var first = new MoveCommand(new Vector3(6f, 0f, 8f));
            var second = new MoveCommand(new Vector3(0f, 0f, 8f));
            companion.Issue(first);
            companion.Issue(second, IssueMode.Append);
            primary.Issue(new MoveCommand(new Vector3(-6f, 0f, 8f)));

            primary.SetMoveIntent(Vector3.left);
            yield return new WaitForSeconds(0.8f);

            Assert.That(primary.CurrentCommand, Is.Null);
            Assert.That(companion.CurrentCommand, Is.SameAs(first), "The companion's order was cancelled");
            Assert.That(companion.PendingCommands, Is.EqualTo(new UnitCommand[] { second }));
            Assert.That(companion.transform.position.z, Is.GreaterThan(-4f), "The companion stopped walking");
        }

        [UnityTest]
        public IEnumerator OrderIssuedWhileTheIntentIsHeld_IsClearedOnTheNextSimulationFrame()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(0f, 0f, -6f));
            yield return null;
            unit.SetMoveIntent(Vector3.right);
            yield return null;

            Assert.That(unit.Issue(new MoveCommand(new Vector3(0f, 0f, 8f))), Is.True, "Orders are still accepted");
            Assert.That(unit.CurrentCommand, Is.Not.Null);
            yield return null;

            Assert.That(unit.CurrentCommand, Is.Null, "Keys must win while held");
        }

        [UnityTest]
        public IEnumerator ReleasingTheIntent_LeavesTheUnitIdleWhereItIs()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(0f, 0f, -6f));
            yield return null;
            unit.Issue(new MoveCommand(new Vector3(0f, 0f, 8f)));
            unit.SetMoveIntent(Vector3.right);
            yield return new WaitForSeconds(0.3f);

            unit.SetMoveIntent(Vector3.zero);
            yield return null;
            var stoppedAt = unit.transform.position;
            yield return new WaitForSeconds(0.3f);

            Assert.That(unit.CurrentCommand, Is.Null, "The cancelled order came back");
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, stoppedAt), Is.LessThan(0.05f), "The unit kept moving");
        }

        [UnityTest]
        public IEnumerator MoveIntentWhilePaused_NeitherMovesTheUnitNorClearsItsOrders()
        {
            world.CreateEnvironment();
            var pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            var unit = world.CreateUnit(new Vector3(0f, 0f, -6f));
            yield return null;
            pause.Pause();
            var move = new MoveCommand(new Vector3(0f, 0f, 8f));
            unit.Issue(move);
            var start = unit.transform.position;

            unit.SetMoveIntent(Vector3.right);
            yield return new WaitForSecondsRealtime(0.3f);

            Assert.That(unit.CurrentCommand, Is.SameAs(move));
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, start), Is.LessThan(0.001f));
        }
    }
}
