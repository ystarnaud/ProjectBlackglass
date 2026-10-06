using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CoverDiscoveryPlayModeTests
    {
        TestWorld world;
        GameObject environment;
        CoverRegistry registry;
        CoverDiscovery discovery;

        // Obstacle 0: a 4 m low wall at the origin. Obstacle 1: a 6 m tall wall at z 10. Obstacle 2: an untagged low crate
        // (a tall crate would give no cover, so it could not show that tagging it adds locations).
        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            environment = world.CreateEnvironment(
                (new Vector3(0f, 0.45f, 0f), new Vector3(4f, 0.9f, 0.5f)),
                (new Vector3(0f, 1f, 10f), new Vector3(6f, 2f, 1f)),
                (new Vector3(-12f, 0.45f, -8f), new Vector3(2f, 0.9f, 2f)));
            Tag(0);
            Tag(1);
            var systems = world.Track(new GameObject("Systems"));
            registry = systems.AddComponent<CoverRegistry>();
            discovery = systems.AddComponent<CoverDiscovery>();
            discovery.Initialize(registry, discoverAtStart: false);
        }

        [TearDown]
        public void TearDown() => world.Dispose();

        CoverSurface Tag(int obstacleIndex) =>
            TestWorld.ObstacleCollider(environment, obstacleIndex).gameObject.AddComponent<CoverSurface>();

        [UnityTest]
        public IEnumerator Discover_BuildsLocationsFromTaggedBoxesOnly()
        {
            yield return new WaitForFixedUpdate();
            var count = discovery.Discover();

            Assert.That(count, Is.GreaterThan(0));
            Assert.That(registry.Points, Has.Count.EqualTo(count));
            var tagged = new[] { TestWorld.ObstacleCollider(environment, 0), TestWorld.ObstacleCollider(environment, 1) };
            Assert.That(registry.Points.All(l => tagged.Contains(l.Obstacle)), Is.True, "Nothing comes from the untagged box");
            Assert.That(registry.Points.Any(l => l.Obstacle == tagged[0] && l.Height == CoverHeight.Low), Is.True);
            Assert.That(registry.Points.Count(l => l.Obstacle == tagged[1] && l.Placement == CoverPlacement.Corner), Is.EqualTo(4));
        }

        [UnityTest]
        public IEnumerator EveryDiscoveredLocation_LiesOnTheNavMesh()
        {
            yield return new WaitForFixedUpdate();
            discovery.Discover();
            foreach (var location in registry.Points)
                Assert.That(NavMesh.SamplePosition(location.Position, out _, 0.5f, NavMesh.AllAreas), Is.True, $"{location.Name} at {location.Position}");
        }

        [UnityTest]
        public IEnumerator Discover_AfterAddingASurface_AddsItsLocations_AndAfterRemovingOneDropsThem()
        {
            yield return new WaitForFixedUpdate();
            var before = discovery.Discover();
            var untagged = TestWorld.ObstacleCollider(environment, 2);
            var surface = untagged.gameObject.AddComponent<CoverSurface>();
            var withThird = discovery.Discover();
            Assert.That(withThird, Is.GreaterThan(before));
            Assert.That(registry.Points.Any(l => l.Obstacle == untagged), Is.True);

            Object.Destroy(surface);
            yield return null;
            var without = discovery.Discover();
            Assert.That(without, Is.EqualTo(before));
            Assert.That(registry.Points.Any(l => l.Obstacle == untagged), Is.False);
        }

        [UnityTest]
        public IEnumerator RepeatedDiscover_GivesTheSameLocations_NoDuplicates()
        {
            yield return new WaitForFixedUpdate();
            var first = discovery.Discover();
            var firstNames = registry.Points.Select(l => l.Name).ToList();
            var second = discovery.Discover();
            Assert.That(second, Is.EqualTo(first));
            Assert.That(registry.Points.Select(l => l.Name), Is.EqualTo(firstNames));
        }

        [UnityTest]
        public IEnumerator DiscoverAtStart_RunsOnceOnTheFirstFrame()
        {
            var systems = world.Track(new GameObject("StartSystems"));
            systems.SetActive(false);
            var otherRegistry = systems.AddComponent<CoverRegistry>();
            var other = systems.AddComponent<CoverDiscovery>();
            other.Initialize(otherRegistry);   // discoverAtStart defaults to true
            systems.SetActive(true);
            yield return null;
            Assert.That(otherRegistry.Points, Is.Not.Empty);
        }

        [UnityTest]
        public IEnumerator ACornerProtectsFromAcrossTheWall_AndNotFromItsPeekPoint()
        {
            yield return new WaitForFixedUpdate();
            discovery.Discover();
            var corner = registry.Points.First(l => l.Placement == CoverPlacement.Corner && l.HasPeek);
            var standPivot = corner.Position + Vector3.up;
            var peekPivot = corner.PeekPoint + Vector3.up;
            // An attacker straight across the wall from the stand point, 8 m beyond it along Facing.
            var attacker = corner.Position + corner.Facing * 8f + Vector3.up;

            Assert.That(corner.ProtectsFrom(attacker, standPivot), Is.True, "Behind the wall end: protected");
            Assert.That(corner.ProtectsFrom(attacker, peekPivot), Is.False, "Stepped out past the end: exposed");
            var behind = corner.Position - corner.Facing * 8f + Vector3.up;
            Assert.That(corner.ProtectsFrom(behind, standPivot), Is.False, "From behind the defender the wall is not between them");
        }

        [UnityTest]
        public IEnumerator AnEndCapColumnProtectsFromAlongTheWall_AndNotFromTheSide()
        {
            yield return new WaitForFixedUpdate();
            discovery.Discover();
            var wall = TestWorld.ObstacleCollider(environment, 1);
            var columns = registry.Points.Where(l => l.Obstacle == wall && l.Placement == CoverPlacement.Column).ToList();
            Assert.That(columns, Has.Count.EqualTo(2), "the 6 m tall wall's two 1 m end caps");
            var east = columns.Single(l => l.Facing.x < -0.99f);
            Assert.That(east.Position.x, Is.EqualTo(3.75f).Within(0.01f), "0.75 m beyond the east end, on the wall's axis");
            Assert.That(east.Position.z, Is.EqualTo(10f).Within(0.01f));
            var standPivot = east.Position + Vector3.up;

            Assert.That(east.ProtectsFrom(east.Position + east.Facing * 9f + Vector3.up, standPivot), Is.True,
                "an attacker beyond the far end, in line with the wall, is blocked by the whole wall");
            Assert.That(east.ProtectsFrom(east.Position + Vector3.forward * 8f + Vector3.up, standPivot), Is.False,
                "from the side the wall is not between them");
        }
    }
}
