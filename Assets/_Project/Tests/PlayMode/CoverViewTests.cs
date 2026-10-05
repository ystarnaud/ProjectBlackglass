using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CoverViewTests
    {
        TestWorld world;
        TacticalPause pause;
        CoverLocation a;
        CoverLocation b;
        CoverRegistry registry;
        CoverView view;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            var environment = world.CreateEnvironment((new Vector3(0f, 0.45f, 0f), new Vector3(4f, 0.9f, 0.5f)));
            a = world.CreateCoverPoint(new Vector3(-1f, 0f, -1f), Vector3.forward, TestWorld.ObstacleCollider(environment));
            b = world.CreateCoverPoint(new Vector3(1f, 0f, -1f), Vector3.forward, TestWorld.ObstacleCollider(environment));
            registry = world.CreateRegistry(a, b);
            pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            var systems = world.Track(new GameObject("Systems"));
            systems.SetActive(false);
            view = systems.AddComponent<CoverView>();
            view.Initialize(registry, pause, null);
            systems.SetActive(true);
        }

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        [UnityTest]
        public IEnumerator BuildsOneColliderFreeMarkerPerPoint_HiddenInRealTime()
        {
            yield return null;
            Assert.That(view.Markers.Count, Is.EqualTo(2));
            foreach (var marker in view.Markers)
            {
                Assert.That(marker.GetComponentsInChildren<Collider>(true), Is.Empty, "Markers must never block click raycasts");
                Assert.That(marker.GetComponentsInChildren<Renderer>(true), Has.Length.EqualTo(2), "a disc and a nub");
            }
            Assert.That(view.VisibleMarkerCount, Is.EqualTo(0), "Unclaimed points are hidden in real time");
        }

        [UnityTest]
        public IEnumerator ShowsEveryPointWhilePaused_AndOnlyClaimedOnesInRealTime()
        {
            yield return null;
            pause.Pause();
            yield return null;
            Assert.That(view.VisibleMarkerCount, Is.EqualTo(2), "Paused: every point shows");

            pause.Resume();
            yield return null;
            Assert.That(view.VisibleMarkerCount, Is.EqualTo(0));

            var unit = world.CreateFighter(new Vector3(0f, 0f, -6f), registry: registry);
            Assert.That(unit.Issue(new MoveToCoverCommand(a)), Is.True);
            yield return null;
            Assert.That(view.VisibleMarkerCount, Is.EqualTo(1), "Only the claimed point shows in real time");
            Assert.That(view.Markers[0].activeSelf, Is.True);
            Assert.That(view.Markers[1].activeSelf, Is.False);
        }

        [UnityTest]
        public IEnumerator ColoursFollowTheState()
        {
            yield return null;
            pause.Pause();
            yield return null;
            Assert.That(view.ShownColor(0), Is.EqualTo(CoverView.AvailableColor));

            var unit = world.CreateFighter(new Vector3(0f, 0f, -6f), registry: registry);
            Assert.That(unit.Issue(new MoveToCoverCommand(a)), Is.True);
            yield return null;
            Assert.That(view.ShownColor(0), Is.EqualTo(CoverView.ReservedColor));
            Assert.That(view.ShownColor(1), Is.EqualTo(CoverView.AvailableColor));

            pause.Resume();
            yield return TestWorld.WaitUntil(() => unit.Cover.Status == CoverStatus.Occupied, 10f);
            yield return null;
            Assert.That(view.ShownColor(0), Is.EqualTo(CoverView.OccupiedColor));
        }
    }
}
