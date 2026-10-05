using System;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class UnitCoverTests
    {
        GameObject unitHost;
        GameObject otherHost;
        UnitCover cover;
        UnitCover other;
        CoverLocation pointA;
        CoverLocation pointB;

        [SetUp]
        public void SetUp()
        {
            pointA = new CoverLocation("A", new Vector3(0f, 0f, 0f), Vector3.forward, null);
            pointB = new CoverLocation("B", new Vector3(5f, 0f, 0f), Vector3.forward, null);
            unitHost = new GameObject("Unit");
            unitHost.transform.position = new Vector3(0f, 1f, 0f);   // on A, at pivot height
            cover = unitHost.AddComponent<UnitCover>();
            otherHost = new GameObject("Other");
            otherHost.transform.position = new Vector3(5f, 1f, 0f);
            other = otherHost.AddComponent<UnitCover>();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(unitHost);
            Object.DestroyImmediate(otherHost);
        }

        [Test]
        public void StartsWithNoCover_AndIsNotWired()
        {
            Assert.That(cover.Point, Is.Null);
            Assert.That(cover.Status, Is.EqualTo(CoverStatus.None));
            Assert.That(cover.OccupiedByOrder, Is.False);
            Assert.That(cover.IsWired, Is.False);
            Assert.Throws<ArgumentNullException>(() => cover.TryReserve(null));
        }

        [Test]
        public void TryReserve_ClaimsThePoint()
        {
            Assert.That(cover.TryReserve(pointA), Is.True);
            Assert.That(cover.Point, Is.SameAs(pointA));
            Assert.That(cover.Status, Is.EqualTo(CoverStatus.Reserved));
            Assert.That(pointA.Claimant, Is.SameAs(cover));
            Assert.That(cover.OccupiedByOrder, Is.False);
        }

        [Test]
        public void TryReserve_RefusedWhenAnotherUnitHoldsIt_ChangesNothing()
        {
            Assert.That(other.TryReserve(pointA), Is.True);
            Assert.That(cover.TryReserve(pointB), Is.True);
            Assert.That(cover.TryReserve(pointA), Is.False);
            Assert.That(cover.Point, Is.SameAs(pointB), "A refused reservation must not drop the point already held");
            Assert.That(cover.Status, Is.EqualTo(CoverStatus.Reserved));
            Assert.That(pointA.Claimant, Is.SameAs(other));
        }

        [Test]
        public void TryReserve_ASecondPoint_ReleasesTheFirst()
        {
            cover.TryReserve(pointA);
            Assert.That(cover.TryReserve(pointB), Is.True);
            Assert.That(cover.Point, Is.SameAs(pointB));
            Assert.That(pointA.IsClaimed, Is.False, "A unit never holds two points");
            Assert.That(pointB.Claimant, Is.SameAs(cover));
        }

        [Test]
        public void TryOccupy_OnlyWithinTheRadius_AndMarksTheOrder()
        {
            Assert.That(cover.TryOccupy(), Is.False, "Nothing reserved");
            cover.TryReserve(pointB);   // 5 m away
            Assert.That(cover.TryOccupy(), Is.False);
            Assert.That(cover.Status, Is.EqualTo(CoverStatus.Reserved));
            cover.TryReserve(pointA);   // under the unit
            Assert.That(cover.TryOccupy(), Is.True);
            Assert.That(cover.Status, Is.EqualTo(CoverStatus.Occupied));
            Assert.That(cover.OccupiedByOrder, Is.True);
            Assert.That(pointA.IsOccupied, Is.True);
        }

        [Test]
        public void TryReserve_TheOccupiedOwnPoint_KeepsItOccupied()
        {
            cover.TryReserve(pointA);
            cover.TryOccupy();
            Assert.That(cover.TryReserve(pointA), Is.True);
            Assert.That(cover.Status, Is.EqualTo(CoverStatus.Occupied));
            Assert.That(cover.OccupiedByOrder, Is.True);
        }

        [Test]
        public void ReleaseReservation_LeavesAnOccupancyAlone_AndRelease_ClearsEverything()
        {
            cover.TryReserve(pointA);
            cover.ReleaseReservation();
            Assert.That(cover.Status, Is.EqualTo(CoverStatus.None));
            Assert.That(pointA.IsClaimed, Is.False);

            cover.TryReserve(pointA);
            cover.TryOccupy();
            cover.ReleaseReservation();
            Assert.That(cover.Status, Is.EqualTo(CoverStatus.Occupied), "Only a reservation is released");
            Assert.That(pointA.Claimant, Is.SameAs(cover));

            cover.Release();
            Assert.That(cover.Status, Is.EqualTo(CoverStatus.None));
            Assert.That(cover.Point, Is.Null);
            Assert.That(cover.OccupiedByOrder, Is.False);
            Assert.That(pointA.IsClaimed, Is.False);
            cover.Release();   // idempotent
        }

        [Test]
        public void HitChance_FallsBackToOne_AndProtection_NeedsAnOccupancy()
        {
            Assert.That(cover.HitChance, Is.EqualTo(1f));
            Assert.That(cover.IsProtectedFrom(new Vector3(0f, 1f, 5f)), Is.False);
            pointA = new CoverLocation("A", new Vector3(0f, 0f, 0f), Vector3.forward, null, 0.3f);
            cover.TryReserve(pointA);
            Assert.That(cover.HitChance, Is.EqualTo(0.3f));
            Assert.That(cover.IsProtectedFrom(new Vector3(0f, 1f, 5f)), Is.False, "Reserved is not occupied: no protection, and no obstacle ray is cast");
        }
    }
}
