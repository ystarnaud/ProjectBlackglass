using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CoverLocationTests
    {
        GameObject obstacleHost;
        CoverLocation location;
        readonly List<GameObject> extraHosts = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            obstacleHost = GameObject.CreatePrimitive(PrimitiveType.Cube);
            location = new CoverLocation("Cover", new Vector3(1f, 0f, 2f), new Vector3(3f, 4f, 4f), null);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var extra in extraHosts)
                Object.DestroyImmediate(extra);
            extraHosts.Clear();
            Object.DestroyImmediate(obstacleHost);
        }

        [Test]
        public void Facing_IsFlatAndNormalised()
        {
            Assert.That(location.Facing.y, Is.EqualTo(0f));
            Assert.That(location.Facing.magnitude, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(location.Facing.x, Is.EqualTo(0.6f).Within(1e-4f));
            Assert.That(location.Facing.z, Is.EqualTo(0.8f).Within(1e-4f));
        }

        [Test]
        public void Facing_StraightUpOrDown_FallsBackToWorldForward()
        {
            var straight = new CoverLocation("Up", Vector3.zero, Vector3.up, null);
            Assert.That(straight.Facing, Is.EqualTo(Vector3.forward));
        }

        [Test]
        public void Constructor_StoresTheData_DefaultsAreLowFaceHalf()
        {
            Assert.That(location.Name, Is.EqualTo("Cover"));
            Assert.That(location.Position, Is.EqualTo(new Vector3(1f, 0f, 2f)));
            Assert.That(location.HitChance, Is.EqualTo(0.5f));
            Assert.That(location.Height, Is.EqualTo(CoverHeight.Low));
            Assert.That(location.Placement, Is.EqualTo(CoverPlacement.Face));
            Assert.That(location.Obstacle, Is.Null);
            Assert.That(location.HasPeek, Is.False);
            Assert.That(location.PeekDirection, Is.EqualTo(Vector3.zero));
            Assert.That(location.PeekPoint, Is.EqualTo(Vector3.zero));
            var collider = obstacleHost.GetComponent<Collider>();
            var tuned = new CoverLocation("Tuned", Vector3.zero, Vector3.forward, collider, 0.25f);
            Assert.That(tuned.Obstacle, Is.SameAs(collider));
            Assert.That(tuned.HitChance, Is.EqualTo(0.25f));
        }

        [Test]
        public void Peek_IsKeptOnlyWithADirection_AndTheDirectionIsFlatAndNormalised()
        {
            var corner = new CoverLocation("Corner", Vector3.zero, Vector3.forward, null, 0.5f, CoverHeight.Tall,
                CoverPlacement.Corner, new Vector3(2f, 3f, 0f), new Vector3(1.25f, 0f, 0f));
            Assert.That(corner.HasPeek, Is.True);
            Assert.That(corner.PeekDirection, Is.EqualTo(Vector3.right));
            Assert.That(corner.PeekPoint, Is.EqualTo(new Vector3(1.25f, 0f, 0f)));
            var plain = new CoverLocation("NoPeek", Vector3.zero, Vector3.forward, null, 0.5f, CoverHeight.Tall,
                CoverPlacement.Corner, Vector3.zero, new Vector3(9f, 9f, 9f));
            Assert.That(plain.HasPeek, Is.False);
            Assert.That(plain.PeekPoint, Is.EqualTo(Vector3.zero), "A peek point without a direction is discarded");
        }

        [Test]
        public void HitChance_IsClampedToZeroOne()
        {
            Assert.That(new CoverLocation("A", Vector3.zero, Vector3.forward, null, 2f).HitChance, Is.EqualTo(1f));
            Assert.That(new CoverLocation("B", Vector3.zero, Vector3.forward, null, -1f).HitChance, Is.EqualTo(0f));
        }

        [Test]
        public void ProtectsFrom_WithoutAnObstacle_IsFalse_AndWarnsOnce()
        {
            LogAssert.Expect(LogType.Warning, new Regex("no obstacle"));
            Assert.That(location.ProtectsFrom(new Vector3(0f, 1f, 5f), new Vector3(0f, 1f, 0f)), Is.False);
            Assert.That(location.ProtectsFrom(new Vector3(0f, 1f, 5f), new Vector3(0f, 1f, 0f)), Is.False);
        }

        [Test]
        public void IsValid_TrueWithoutAnObstacle_FalseAfterRetire()
        {
            Assert.That(location.IsValid, Is.True, "A location that never had an obstacle is valid (test points)");
            location.Retire();
            Assert.That(location.IsValid, Is.False);
        }

        [Test]
        public void IsValid_FollowsTheObstacle_DeactivatedDisabledOrDestroyedIsInvalid()
        {
            var collider = obstacleHost.GetComponent<Collider>();
            var wired = new CoverLocation("Wired", Vector3.zero, Vector3.forward, collider);
            Assert.That(wired.IsValid, Is.True);
            collider.enabled = false;
            Assert.That(wired.IsValid, Is.False, "A disabled collider does not protect");
            collider.enabled = true;
            obstacleHost.SetActive(false);
            Assert.That(wired.IsValid, Is.False, "An inactive obstacle does not protect");
            obstacleHost.SetActive(true);
            Assert.That(wired.IsValid, Is.True);
            Object.DestroyImmediate(obstacleHost);
            Assert.That(wired.IsValid, Is.False, "A destroyed obstacle does not protect");
            obstacleHost = GameObject.CreatePrimitive(PrimitiveType.Cube);   // for TearDown
        }

        [Test]
        public void Retire_ClearsTheClaimant_AndTheLocationCannotBeValidAgain()
        {
            var a = NewUnit("A");
            location.TryClaim(a);
            location.Retire();
            Assert.That(location.Claimant, Is.Null);
            Assert.That(location.IsClaimed, Is.False);
            Assert.That(location.IsValid, Is.False);
        }

        UnitCover NewUnit(string name)
        {
            var unitHost = new GameObject(name);
            unitHost.transform.position = location.Position + Vector3.up;
            extraHosts.Add(unitHost);
            return unitHost.AddComponent<UnitCover>();
        }

        [Test]
        public void TryClaim_SucceedsWhenUnclaimedOrOwn_FailsForAnotherUnit()
        {
            var a = NewUnit("A");
            var b = NewUnit("B");
            Assert.That(location.IsClaimed, Is.False);
            Assert.That(location.TryClaim(a), Is.True);
            Assert.That(location.TryClaim(a), Is.True, "Re-claiming an own point is fine");
            Assert.That(location.TryClaim(b), Is.False, "One unit per point");
            Assert.That(location.Claimant, Is.SameAs(a));
            Assert.That(location.IsClaimed, Is.True);
        }

        [Test]
        public void ARetiredLocation_RefusesAClaim_AndNoUnitCanReserveIt()
        {
            var a = NewUnit("A");
            location.Retire();
            Assert.That(location.TryClaim(a), Is.False);
            Assert.That(location.Claimant, Is.Null);
            Assert.That(a.TryReserve(location), Is.False);
            Assert.That(a.Point, Is.Null);
            Assert.That(a.Status, Is.EqualTo(CoverStatus.None));
        }

        [Test]
        public void Release_ByTheWrongClaimant_IsANoOp()
        {
            var a = NewUnit("A");
            var b = NewUnit("B");
            location.TryClaim(a);
            location.Release(b);
            Assert.That(location.Claimant, Is.SameAs(a));
            location.Release(a);
            Assert.That(location.Claimant, Is.Null);
            Assert.That(location.IsClaimed, Is.False);
        }

        [Test]
        public void IsClaimedBy_NamesTheClaimant()
        {
            var a = NewUnit("A");
            var b = NewUnit("B");
            Assert.That(location.IsClaimedBy(a), Is.False);
            location.TryClaim(a);
            Assert.That(location.IsClaimedBy(a), Is.True);
            Assert.That(location.IsClaimedBy(b), Is.False);
            Assert.That(location.IsClaimedBy(null), Is.False);
        }

        [Test]
        public void IsOccupied_FollowsTheClaimantsStatus()
        {
            var a = NewUnit("A");   // standing on the point (same x/z), so TryOccupy is within the radius
            Assert.That(a.TryReserve(location), Is.True);
            Assert.That(location.IsOccupied, Is.False, "Reserved is not occupied");
            Assert.That(a.TryOccupy(), Is.True);
            Assert.That(location.IsOccupied, Is.True);
        }

        [Test]
        public void Registry_ListsThePointsItIsGiven()
        {
            var host = new GameObject("Registry");
            extraHosts.Add(host);
            var registry = host.AddComponent<CoverRegistry>();
            Assert.That(registry.Points, Is.Empty);
            registry.Initialize(location);
            Assert.That(registry.Points, Is.EqualTo(new[] { location }));
        }
    }
}
