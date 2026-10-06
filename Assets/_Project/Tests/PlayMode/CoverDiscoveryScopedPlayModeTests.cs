using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CoverDiscoveryScopedPlayModeTests
    {
        TestWorld world;
        GameObject environment;
        BoxCollider outside;
        CoverRegistry registry;
        CoverDiscovery discovery;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            // Obstacle 0 (under the environment root): a 4 m low wall. `outside` is another tagged box NOT under the root.
            environment = world.CreateEnvironment((new Vector3(0f, 0.45f, 0f), new Vector3(4f, 0.9f, 0.5f)));
            TestWorld.ObstacleCollider(environment, 0).gameObject.AddComponent<CoverSurface>();
            var loose = world.CreateObstacle(new Vector3(-12f, 0.45f, -8f), new Vector3(4f, 0.9f, 0.5f));
            loose.AddComponent<CoverSurface>();
            outside = loose.GetComponent<BoxCollider>();
            var systems = world.Track(new GameObject("Systems"));
            registry = systems.AddComponent<CoverRegistry>();
            discovery = systems.AddComponent<CoverDiscovery>();
            discovery.Initialize(registry, discoverAtStart: false);
        }

        [TearDown]
        public void TearDown() => world.Dispose();

        [UnityTest]
        public IEnumerator Discover_WithARoot_UsesOnlyTheSurfacesUnderIt()
        {
            yield return null;
            var count = discovery.Discover(environment.transform);
            Assert.That(count, Is.GreaterThan(0));
            Assert.That(registry.Points.All(p => p.Obstacle != outside), Is.True);
        }

        [UnityTest]
        public IEnumerator Discover_WithoutArguments_StillFindsEverySurfaceInTheScene()
        {
            yield return null;
            discovery.Discover();
            Assert.That(registry.Points.Any(p => p.Obstacle == outside), Is.True);
        }

        [UnityTest]
        public IEnumerator Discover_WithAReachabilityFilter_DropsWhatItRejects()
        {
            yield return null;
            var all = discovery.Discover(environment.transform);
            var filtered = discovery.Discover(environment.transform, point => point.x > 0f);
            Assert.That(filtered, Is.LessThan(all));
            Assert.That(filtered, Is.GreaterThan(0));
            Assert.That(registry.Points.All(p => p.Position.x > 0f), Is.True);
        }
    }
}
