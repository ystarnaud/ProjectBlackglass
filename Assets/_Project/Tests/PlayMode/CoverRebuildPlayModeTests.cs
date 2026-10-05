using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CoverRebuildPlayModeTests
    {
        TestWorld world;
        GameObject environment;
        CoverRegistry registry;
        CoverDiscovery discovery;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            environment = world.CreateEnvironment((new Vector3(0f, 0.45f, 0f), new Vector3(4f, 0.9f, 0.5f)));
            TestWorld.ObstacleCollider(environment).gameObject.AddComponent<CoverSurface>();
            var systems = world.Track(new GameObject("Systems"));
            registry = systems.AddComponent<CoverRegistry>();
            discovery = systems.AddComponent<CoverDiscovery>();
            discovery.Initialize(registry, discoverAtStart: false);
        }

        [TearDown]
        public void TearDown() => world.Dispose();

        CommandableUnit Fighter(Vector3 at) => world.CreateFighter(at, registry: registry);

        // The south-face location nearest x = -1 of the 4 m low wall.
        CoverLocation SouthPoint() => registry.Points.Where(l => l.Facing.z > 0.5f).OrderBy(l => l.Position.x).First();

        [UnityTest]
        public IEnumerator Rebuild_WhileAUnitOccupiesALocation_ReleasesIt_AndTheUnitReclaimsTheEquivalentOneBystanding()
        {
            yield return new WaitForFixedUpdate();
            discovery.Discover();
            var old = SouthPoint();
            var unit = Fighter(new Vector3(old.Position.x, 0f, old.Position.z - 5f));
            Assert.That(unit.Issue(new MoveToCoverCommand(old)), Is.True);
            yield return TestWorld.WaitUntil(() => unit.Cover.Status == CoverStatus.Occupied, 10f);
            Assert.That(unit.Cover.Status, Is.EqualTo(CoverStatus.Occupied), "Precondition");

            discovery.Discover();

            Assert.That(old.IsValid, Is.False);
            Assert.That(old.IsClaimed, Is.False, "The retired location holds no claim");
            yield return null;
            yield return null;
            yield return TestWorld.WaitUntil(() => unit.Cover.Status == CoverStatus.Occupied && unit.Cover.Point != old, 3f);
            Assert.That(unit.Cover.Point, Is.Not.SameAs(old));
            Assert.That(unit.Cover.Point.IsValid, Is.True, "Standing still on the new equivalent location claims it");
            Assert.That(unit.Cover.OccupiedByOrder, Is.False, "It is a chance occupancy, not an ordered one");
        }

        [UnityTest]
        public IEnumerator Rebuild_MidWalk_EndsTheCoverOrderSilently_AndTheNextQueuedOrderRuns()
        {
            yield return new WaitForFixedUpdate();
            discovery.Discover();
            var old = SouthPoint();
            var unit = Fighter(new Vector3(old.Position.x, 0f, old.Position.z - 10f));
            Assert.That(unit.Issue(new MoveToCoverCommand(old)), Is.True);
            var next = new MoveCommand(new Vector3(6f, 0f, -8f));
            Assert.That(unit.Issue(next, IssueMode.Append), Is.True);
            yield return null;

            discovery.Discover();
            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == next, 2f);

            Assert.That(unit.CurrentCommand, Is.SameAs(next), "The retired location ends the cover order and the queue moves on");
            Assert.That(unit.Cover.Status, Is.EqualTo(CoverStatus.None));
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator MoveToCover_OnARetiredLocation_IsRefused()
        {
            yield return new WaitForFixedUpdate();
            discovery.Discover();
            var old = SouthPoint();
            discovery.Discover();
            var unit = Fighter(new Vector3(0f, 0f, -8f));

            Assert.That(unit.Issue(new MoveToCoverCommand(old)), Is.False, "A retired location is not a valid destination");
            Assert.That(unit.Cover.Status, Is.EqualTo(CoverStatus.None));
        }

        [UnityTest]
        public IEnumerator RepeatedRebuilds_WithUnitsInCover_LeaveNoStaleClaims()
        {
            yield return new WaitForFixedUpdate();
            discovery.Discover();
            var units = new[]
            {
                Fighter(new Vector3(-1.5f, 0f, -5f)),
                Fighter(new Vector3(1.5f, 0f, -5f)),
            };
            var points = registry.Points.Where(l => l.Facing.z > 0.5f).OrderBy(l => l.Position.x).Take(2).ToList();
            for (var i = 0; i < units.Length; i++)
                Assert.That(units[i].Issue(new MoveToCoverCommand(points[i])), Is.True);
            yield return TestWorld.WaitUntil(() => units.All(u => u.Cover.Status == CoverStatus.Occupied), 10f);

            var retired = registry.Points.ToList();
            for (var round = 0; round < 5; round++)
            {
                discovery.Discover();
                yield return null;
                yield return null;
            }

            Assert.That(retired.Any(l => l.IsClaimed), Is.False, "No retired location keeps a claimant");
            var claimed = registry.Points.Where(l => l.IsClaimed).ToList();
            Assert.That(claimed.Count, Is.LessThanOrEqualTo(units.Length), "Claims never outnumber the units");
            Assert.That(claimed.All(l => units.Any(u => u.Cover.Point == l)), Is.True, "Every claim belongs to a unit that points at it");
            Assert.That(registry.Points.Select(l => l.Name).Distinct().Count(), Is.EqualTo(registry.Points.Count), "No duplicated locations");
        }
    }
}
