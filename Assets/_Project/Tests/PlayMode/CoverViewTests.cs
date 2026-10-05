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
        GameObject environment;
        UnitSelection selection;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            environment = world.CreateEnvironment((new Vector3(0f, 0.45f, 0f), new Vector3(4f, 0.9f, 0.5f)));
            a = world.CreateCoverPoint(new Vector3(-1f, 0f, -1f), Vector3.forward, TestWorld.ObstacleCollider(environment));
            b = world.CreateCoverPoint(new Vector3(1f, 0f, -1f), Vector3.forward, TestWorld.ObstacleCollider(environment));
            registry = world.CreateRegistry(a, b);
            pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            selection = world.Track(new GameObject("Selection")).AddComponent<UnitSelection>();
            var systems = world.Track(new GameObject("Systems"));
            systems.SetActive(false);
            view = systems.AddComponent<CoverView>();
            view.Initialize(registry, pause, null, selection);
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

        [UnityTest]
        public IEnumerator MarkersAreRebuilt_WhenTheRegistryChanges()
        {
            yield return null;
            Assert.That(view.Markers.Count, Is.EqualTo(2));

            var c = world.CreateCoverPoint(new Vector3(3f, 0f, -1f), Vector3.forward, TestWorld.ObstacleCollider(environment));
            registry.Rebuild(new[] { a, b, c });
            yield return null;

            Assert.That(view.Markers.Count, Is.EqualTo(3));
            registry.Rebuild(new[] { c });
            yield return null;
            Assert.That(view.Markers.Count, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ShapeShowsTheType_LowDiscAndNub_TallTallerNub_CornerAddsAPeekArrow()
        {
            var tall = world.CreateCoverPoint(new Vector3(3f, 0f, -1f), Vector3.forward, TestWorld.ObstacleCollider(environment), 0.5f, CoverHeight.Tall);
            var corner = new CoverLocation("Corner", new Vector3(5f, 0f, -1f), Vector3.forward, TestWorld.ObstacleCollider(environment),
                0.5f, CoverHeight.Tall, CoverPlacement.Corner, Vector3.right, new Vector3(6.25f, 0f, -1f));
            registry.Rebuild(new[] { a, tall, corner });
            yield return null;

            Assert.That(view.Markers, Has.Count.EqualTo(3));
            Assert.That(view.Markers[0].GetComponentsInChildren<Renderer>(true), Has.Length.EqualTo(2), "Low: disc and nub");
            Assert.That(view.Markers[1].GetComponentsInChildren<Renderer>(true), Has.Length.EqualTo(2), "Tall: disc and nub");
            Assert.That(view.Markers[2].GetComponentsInChildren<Renderer>(true), Has.Length.EqualTo(3), "Corner: disc, nub and peek arrow");
            var lowNub = view.Markers[0].transform.Find("Nub").localScale.y;
            var tallNub = view.Markers[1].transform.Find("Nub").localScale.y;
            Assert.That(tallNub, Is.GreaterThan(lowNub), "A tall obstacle shows a taller nub");
            Assert.That(view.Markers[2].transform.Find("Peek"), Is.Not.Null);
        }

        [UnityTest]
        public IEnumerator TheSelectedUnitsDestination_IsGreen_OthersKeepTheirStateColour()
        {
            yield return null;
            var friendly = world.CreateFriendlyFighter(new Vector3(0f, 0f, -6f));
            friendly.Unit.Cover.Initialize(registry);
            selection.AddToRoster(friendly);
            selection.Select(friendly);
            Assert.That(selection.Selected, Has.Count.EqualTo(1), "Precondition: the unit is selected");
            Assert.That(friendly.Unit.Issue(new MoveToCoverCommand(a)), Is.True);
            Assert.That(a.IsClaimed, Is.True, "Precondition: the unit has reserved the point");
            pause.Pause();
            yield return null;

            Assert.That(view.ShownColor(0), Is.EqualTo(CoverView.SelectedColor), "The selected unit's reserved point is green");
            Assert.That(view.ShownColor(1), Is.EqualTo(CoverView.AvailableColor));

            selection.Clear();
            yield return null;
            Assert.That(view.ShownColor(0), Is.EqualTo(CoverView.ReservedColor), "Deselected: back to the state colour");
        }
    }
}
