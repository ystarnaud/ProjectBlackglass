using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CoverPointTests
    {
        GameObject host;
        GameObject obstacleHost;
        CoverPoint point;
        readonly System.Collections.Generic.List<GameObject> extraHosts = new System.Collections.Generic.List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            host = new GameObject("Cover");
            point = host.AddComponent<CoverPoint>();
            obstacleHost = GameObject.CreatePrimitive(PrimitiveType.Cube);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var extra in extraHosts)
                Object.DestroyImmediate(extra);
            extraHosts.Clear();
            Object.DestroyImmediate(host);
            Object.DestroyImmediate(obstacleHost);
        }

        [Test]
        public void Forward_IsFlatAndNormalised()
        {
            host.transform.rotation = Quaternion.LookRotation(new Vector3(3f, 4f, 4f));
            Assert.That(point.Forward.y, Is.EqualTo(0f));
            Assert.That(point.Forward.magnitude, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(point.Forward.x, Is.EqualTo(0.6f).Within(1e-4f));
            Assert.That(point.Forward.z, Is.EqualTo(0.8f).Within(1e-4f));
        }

        [Test]
        public void Initialize_SetsTheObstacleAndHitChance_DefaultIsHalf()
        {
            Assert.That(point.HitChance, Is.EqualTo(0.5f));
            Assert.That(point.Obstacle, Is.Null);
            var collider = obstacleHost.GetComponent<Collider>();
            point.Initialize(collider, 0.25f);
            Assert.That(point.Obstacle, Is.SameAs(collider));
            Assert.That(point.HitChance, Is.EqualTo(0.25f));
            Assert.That(point.Position, Is.EqualTo(host.transform.position));
        }

        [Test]
        public void ProtectsFrom_WithoutAnObstacle_IsFalse_AndWarnsOnce()
        {
            LogAssert.Expect(LogType.Warning, new Regex("no obstacle"));
            Assert.That(point.ProtectsFrom(new Vector3(0f, 1f, 5f), new Vector3(0f, 1f, 0f)), Is.False);
            Assert.That(point.ProtectsFrom(new Vector3(0f, 1f, 5f), new Vector3(0f, 1f, 0f)), Is.False);
        }

        [Test]
        public void Registry_ListsThePointsItIsGiven()
        {
            var registry = host.AddComponent<CoverRegistry>();
            Assert.That(registry.Points, Is.Empty);
            registry.Initialize(point);
            Assert.That(registry.Points, Is.EqualTo(new[] { point }));
        }

        UnitCover NewUnit(string name)
        {
            var unitHost = new GameObject(name);
            unitHost.transform.position = host.transform.position + Vector3.up;
            extraHosts.Add(unitHost);
            return unitHost.AddComponent<UnitCover>();
        }

        [Test]
        public void TryClaim_SucceedsWhenUnclaimedOrOwn_FailsForAnotherUnit()
        {
            var a = NewUnit("A");
            var b = NewUnit("B");
            Assert.That(point.IsClaimed, Is.False);
            Assert.That(point.TryClaim(a), Is.True);
            Assert.That(point.TryClaim(a), Is.True, "Re-claiming an own point is fine");
            Assert.That(point.TryClaim(b), Is.False, "One unit per point");
            Assert.That(point.Claimant, Is.SameAs(a));
            Assert.That(point.IsClaimed, Is.True);
        }

        [Test]
        public void Release_ByTheWrongClaimant_IsANoOp()
        {
            var a = NewUnit("A");
            var b = NewUnit("B");
            point.TryClaim(a);
            point.Release(b);
            Assert.That(point.Claimant, Is.SameAs(a));
            point.Release(a);
            Assert.That(point.Claimant, Is.Null);
            Assert.That(point.IsClaimed, Is.False);
        }

        [Test]
        public void IsClaimedBy_NamesTheClaimant()
        {
            var a = NewUnit("A");
            var b = NewUnit("B");
            Assert.That(point.IsClaimedBy(a), Is.False);
            point.TryClaim(a);
            Assert.That(point.IsClaimedBy(a), Is.True);
            Assert.That(point.IsClaimedBy(b), Is.False);
            Assert.That(point.IsClaimedBy(null), Is.False);
        }

        [Test]
        public void IsOccupied_FollowsTheClaimantsStatus()
        {
            var a = NewUnit("A");   // standing on the point (same x/z), so TryOccupy is within the radius
            Assert.That(a.TryReserve(point), Is.True);
            Assert.That(point.IsOccupied, Is.False, "Reserved is not occupied");
            Assert.That(a.TryOccupy(), Is.True);
            Assert.That(point.IsOccupied, Is.True);
        }
    }
}
