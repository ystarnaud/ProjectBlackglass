using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CommandQueueViewTests
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
        public IEnumerator ShowsALineThroughEveryOrder_AndMarkersAtMoveDestinations()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(-6f, 0f, -6f));
            var dummy = world.CreateDummy(new Vector3(6f, 0f, 6f));
            var view = unit.gameObject.AddComponent<CommandQueueView>();
            yield return null;
            Assert.That(view.LinePointCount, Is.EqualTo(0), "Idle unit shows a line");

            unit.Issue(new MoveCommand(new Vector3(-6f, 0f, 4f)));
            unit.Issue(new MoveCommand(new Vector3(4f, 0f, -6f)), IssueMode.Append);
            unit.Issue(new AttackCommand(dummy), IssueMode.Append);
            yield return null;

            Assert.That(view.LinePointCount, Is.EqualTo(4), "unit + two move destinations + attack target");
            Assert.That(view.ActiveMarkerCount, Is.EqualTo(2));
            foreach (var marker in view.Markers)
                Assert.That(marker.GetComponent<Collider>(), Is.Null, "Markers must never block click raycasts");

            unit.Issue(new StopCommand());
            yield return null;

            Assert.That(view.LinePointCount, Is.EqualTo(0));
            Assert.That(view.ActiveMarkerCount, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator WhilePaused_ShowsNewOrdersImmediately()
        {
            world.CreateEnvironment();
            var pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            var unit = world.CreateUnit(new Vector3(-6f, 0f, -6f));
            var view = unit.gameObject.AddComponent<CommandQueueView>();
            yield return null;

            pause.Pause();
            unit.Issue(new MoveCommand(new Vector3(4f, 0f, 4f)));
            unit.Issue(new MoveCommand(new Vector3(-4f, 0f, 4f)), IssueMode.Append);
            yield return null;

            Assert.That(view.LinePointCount, Is.EqualTo(3));
            Assert.That(view.ActiveMarkerCount, Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator CoverOrder_ShowsAMarkerAtThePoint_AndAVanishedPointDrawsNothing()
        {
            var environment = world.CreateEnvironment((new Vector3(0f, 0.45f, 0f), new Vector3(4f, 0.9f, 0.5f)));
            var point = world.CreateCoverPoint(new Vector3(0f, 0f, -1f), Vector3.forward, TestWorld.ObstacleCollider(environment));
            var pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            var unit = world.CreateUnit(new Vector3(0f, 0f, -8f));
            var view = unit.gameObject.AddComponent<CommandQueueView>();
            yield return null;
            // Paused, so the walk never starts and the order stays current while its point is destroyed.
            pause.Pause();
            Assert.That(unit.Issue(new MoveToCoverCommand(point)), Is.True);
            yield return null;
            Assert.That(view.LinePointCount, Is.EqualTo(2), "unit + the cover point");
            Assert.That(view.ActiveMarkerCount, Is.EqualTo(1));

            Object.Destroy(point.gameObject);
            yield return null;
            yield return null;

            Assert.That(unit.CurrentCommand, Is.TypeOf<MoveToCoverCommand>(), "Precondition: paused, the order is still current");
            Assert.That(view.LinePointCount, Is.EqualTo(0), "A vanished point draws nothing, and nothing throws");
            Assert.That(view.ActiveMarkerCount, Is.EqualTo(0));
        }
    }
}
