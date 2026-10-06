using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;
using Unity.AI.Navigation;

namespace Blackglass.Tests
{
    public class MissionBuilderPlayModeTests
    {
        TestWorld world;
        GeneratedMission mission;

        [SetUp]
        public void SetUp() => world = new TestWorld();

        [TearDown]
        public void TearDown()
        {
            DestroyMission();
            world.Dispose();
        }

        void DestroyMission()
        {
            if (mission != null && mission.Root != null)
                Object.DestroyImmediate(mission.Root);   // immediate: the surface removes its NavMesh data on disable
            mission = null;
        }

        MissionLayout Layout(int seed)
        {
            var result = MissionGenerator.Generate(new MissionSettings { seed = seed });
            Assert.That(result.Succeeded, Is.True, result.Describe());
            return result.Layout;
        }

        MissionLayout LayoutWith(System.Func<MissionLayout, bool> predicate)
        {
            for (var seed = 1; seed <= 60; seed++)
            {
                var layout = Layout(seed);
                if (predicate(layout))
                    return layout;
            }
            Assert.Fail("no seed in 1..60 gives such a layout");
            return null;
        }

        CoverRegistry Discover(GeneratedMission built)
        {
            var registry = world.CreateRegistry();
            var discovery = world.Track(new GameObject("Discovery")).AddComponent<CoverDiscovery>();
            discovery.Initialize(registry, discoverAtStart: false);
            var origin = built.Layout.TileCenter(built.Layout.FriendlySpawns[0]);
            discovery.Discover(built.Geometry, MissionNavigation.ReachableFrom(origin));
            return registry;
        }

        [UnityTest]
        public IEnumerator Build_CreatesTheOwnedRoot_WithOneCubePerFloorRectangleAndBox()
        {
            var layout = Layout(12345);
            mission = MissionBuilder.Build(layout, null, null);
            yield return null;

            Assert.That(mission.Root.name, Is.EqualTo(GeneratedMission.RootName));
            Assert.That(mission.Geometry.parent, Is.EqualTo(mission.Root.transform));
            Assert.That(mission.Actors.parent, Is.EqualTo(mission.Root.transform));
            Assert.That(mission.Geometry.childCount, Is.EqualTo(layout.FloorRects.Count + layout.Boxes.Count));
            foreach (var box in layout.Boxes)
            {
                var cube = mission.Geometry.Find(box.Name);
                Assert.That(cube, Is.Not.Null, box.Name);
                Assert.That(cube.GetComponent<CoverSurface>(), Is.Not.Null, box.Name);
                var modifier = cube.GetComponent<NavMeshModifier>();
                Assert.That(modifier != null && modifier.overrideArea && modifier.area == 1, Is.True, box.Name + " is not carved");
                Assert.That(cube.GetComponent<BoxCollider>().bounds.size.y, Is.EqualTo(box.Height).Within(0.01f));
            }
            Assert.That(mission.Geometry.GetComponentsInChildren<CoverSurface>(), Has.Length.EqualTo(layout.Boxes.Count));
        }

        [UnityTest]
        public IEnumerator Build_GivesANavMeshThatValidates_ForSeveralSeeds()
        {
            for (var seed = 1; seed <= 8; seed++)
            {
                mission = MissionBuilder.Build(Layout(seed), null, null);
                yield return null;
                Assert.That(MissionNavigation.Validate(mission.Layout, out var reason, out var report), Is.True, $"seed {seed}: {reason}");
                Assert.That(report.NavArea, Is.GreaterThan(report.FloorArea * MissionConstants.MinNavAreaRatio));
                Assert.That(report.PathsChecked, Is.GreaterThanOrEqualTo(mission.Layout.Rooms.Count));
                DestroyMission();
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator Validate_FailsWhenThereIsNoNavMesh()
        {
            yield return null;
            Assert.That(MissionNavigation.Validate(Layout(12345), out var reason, out _), Is.False);
            Assert.That(reason, Does.Contain("NavMesh"));
        }

        [UnityTest]
        public IEnumerator TallWallEnds_ProduceCornerLocations_AndEveryBaffleGetsFour()
        {
            var layout = LayoutWith(l => l.Boxes.Any(b => b.Kind == MissionBoxKind.Baffle));
            mission = MissionBuilder.Build(layout, null, null);
            yield return null;
            var registry = Discover(mission);

            var corners = registry.Points.Where(p => p.Placement == CoverPlacement.Corner && p.Height == CoverHeight.Tall).ToList();
            Assert.That(corners.Count, Is.GreaterThanOrEqualTo(4));
            foreach (var baffle in layout.Boxes.Where(b => b.Kind == MissionBoxKind.Baffle))
                Assert.That(corners.Count(p => p.Obstacle.gameObject.name == baffle.Name), Is.EqualTo(4), baffle.Name);
            Assert.That(corners.Any(p => p.Obstacle.gameObject.name.StartsWith("Wall_")), Is.True, "room walls end at door gaps too");
        }

        [UnityTest]
        public IEnumerator LowObstacles_ProduceLowFaceLocationsOnly()
        {
            var layout = LayoutWith(l => l.Boxes.Any(b => b.Kind == MissionBoxKind.LowWall));
            mission = MissionBuilder.Build(layout, null, null);
            yield return null;
            var registry = Discover(mission);

            var low = layout.Boxes.Where(b => b.Kind == MissionBoxKind.LowWall || b.Kind == MissionBoxKind.Crate).Select(b => b.Name).ToHashSet();
            var own = registry.Points.Where(p => low.Contains(p.Obstacle.gameObject.name)).ToList();
            Assert.That(own, Is.Not.Empty);
            Assert.That(own.All(p => p.Height == CoverHeight.Low && p.Placement == CoverPlacement.Face), Is.True);
        }

        [UnityTest]
        public IEnumerator EveryLocation_IsReachableFromTheFriendlySpawn()
        {
            mission = MissionBuilder.Build(Layout(12345), null, null);
            yield return null;
            var registry = Discover(mission);
            Assert.That(registry.Points, Is.Not.Empty);

            NavMesh.SamplePosition(mission.Layout.TileCenter(mission.Layout.FriendlySpawns[0]), out var start, 1f, NavMesh.AllAreas);
            var path = new NavMeshPath();
            foreach (var point in registry.Points)
            {
                Assert.That(NavMesh.SamplePosition(point.Position, out var hit, 0.5f, NavMesh.AllAreas), Is.True, point.Name);
                Assert.That(NavMesh.CalculatePath(start.position, hit.position, NavMesh.AllAreas, path) && path.status == NavMeshPathStatus.PathComplete,
                    Is.True, point.Name);
            }
        }

        [UnityTest]
        public IEnumerator SameSeedTwice_GivesTheSameCoverCount()
        {
            mission = MissionBuilder.Build(Layout(777), null, null);
            yield return null;
            var first = Discover(mission).Points.Count;
            DestroyMission();
            yield return null;
            mission = MissionBuilder.Build(Layout(777), null, null);
            yield return null;
            var second = Discover(mission).Points.Count;

            Assert.That(first, Is.GreaterThan(0));
            Assert.That(second, Is.EqualTo(first));
        }
    }
}
