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
    }
}
