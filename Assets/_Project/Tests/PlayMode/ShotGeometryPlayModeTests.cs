#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine.TestTools;
using UnityEngine;

namespace Blackglass.Tests
{
    public class ShotGeometryPlayModeTests
    {
        TestWorld world;
        Health shooter;
        Health target;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            shooter = world.CreateDummy(new Vector3(0f, 0f, -8f));
            target = world.CreateDummy(new Vector3(0f, 0f, 4f));
            Physics.SyncTransforms();
        }

        // Destroy is deferred to the end of the frame: wait one, so the next test never raycasts against this one's walls.
        [UnityTearDown]
        public IEnumerator TearDown()
        {
            world.Dispose();
            yield return null;
        }

        ShotRecord Record(Vector3 aim, ShotOutcome outcome, bool melee = false) =>
            new ShotRecord(1, 1, shooter, target, aim - Vector3.forward * 5f, aim, outcome, melee, null);

        GameObject Wall(Vector3 position, Vector3 scale, ImpactSurfaceKind kind)
        {
            var wall = world.CreateObstacle(position, scale);
            wall.AddComponent<ImpactSurface>().Set(kind);
            Physics.SyncTransforms();
            return wall;
        }

        static readonly Vector3 Muzzle = new Vector3(0f, 1.4f, -7f);

        [Test]
        public void OpenHit_DrawsTheTracerToTheAimPoint_AndAnImpactOnTheCharacter()
        {
            var aim = target.transform.position + Vector3.up * ShotRecord.BodyHeight;
            var end = ShotGeometry.Resolve(Record(aim, ShotOutcome.Hit), Muzzle, new CosmeticRandom(1));
            Assert.That(end.HasTracer && end.HasImpact, Is.True);
            Assert.That(end.End, Is.EqualTo(end.ImpactPoint), "the streak ends where the impact is: on the body's surface");
            Assert.That(Vector3.Distance(end.End, aim), Is.LessThan(0.6f));
            Assert.That(end.Surface, Is.EqualTo(ImpactSurfaceKind.Character));
            Assert.That(Vector3.Dot(end.ImpactNormal, (aim - Muzzle).normalized), Is.LessThan(-0.9f), "the normal faces back at the shooter");
        }

        // The target is a 1 m wide, 2 m tall capsule (radius 0.5) standing at z = 4 whose axis is the recorded aim point's column.
        [Test]
        public void HitOnACapsule_PutsTheImpactOnItsSurface_FacingTheShooter()
        {
            var aim = target.transform.position + Vector3.up * ShotRecord.BodyHeight;
            var end = ShotGeometry.Resolve(Record(aim, ShotOutcome.Hit), Muzzle, new CosmeticRandom(1));
            var offAxis = new Vector2(end.ImpactPoint.x - target.transform.position.x, end.ImpactPoint.z - target.transform.position.z).magnitude;
            Assert.That(end.HasImpact && end.Surface == ImpactSurfaceKind.Character, Is.True);
            Assert.That(offAxis, Is.EqualTo(0.5f).Within(0.02f), "on the capsule's surface, not on its axis");
            Assert.That(end.ImpactPoint.z, Is.LessThan(target.transform.position.z), "the side that faces the shooter");
            Assert.That(Vector3.Dot(end.ImpactNormal, (Muzzle - end.ImpactPoint).normalized), Is.GreaterThan(0.9f), "the normal faces back at the shooter");
            Assert.That(end.End, Is.EqualTo(end.ImpactPoint), "the tracer ends where the impact is");
        }

        [Test]
        public void Melee_HitOnACapsule_AlsoLandsOnItsSurface()
        {
            var aim = target.transform.position + Vector3.up * ShotRecord.BodyHeight;
            var melee = new Vector3(0f, 1.2f, 2.5f);
            var end = ShotGeometry.Resolve(Record(aim, ShotOutcome.Hit, melee: true), melee, new CosmeticRandom(1));
            var offAxis = new Vector2(end.ImpactPoint.x - target.transform.position.x, end.ImpactPoint.z - target.transform.position.z).magnitude;
            Assert.That(end.HasTracer, Is.False);
            Assert.That(offAxis, Is.EqualTo(0.5f).Within(0.02f));
        }

        [Test]
        public void TargetWithoutACollider_FallsBackToTheAimPoint_AndTheReverseDirection()
        {
            Object.DestroyImmediate(target.GetComponent<Collider>());
            Physics.SyncTransforms();
            var aim = target.transform.position + Vector3.up * ShotRecord.BodyHeight;
            var end = ShotGeometry.Resolve(Record(aim, ShotOutcome.Hit), Muzzle, new CosmeticRandom(1));
            Assert.That(end.HasImpact, Is.True);
            Assert.That(end.ImpactPoint, Is.EqualTo(aim));
            Assert.That(end.End, Is.EqualTo(aim));
            Assert.That(Vector3.Dot(end.ImpactNormal, (aim - Muzzle).normalized), Is.LessThan(-0.999f));
        }

        float OffAxis(Vector3 point) =>
            new Vector2(point.x - target.transform.position.x, point.z - target.transform.position.z).magnitude;

        // What Health does right after Died, before the shot is announced: the unit is inactive, so nothing can be raycast,
        // but its capsule component still reports its radius.
        [Test]
        public void KillingBlow_OnADeactivatedTarget_PutsTheImpactOnTheCapsuleSurface_FacingTheShooter()
        {
            var aim = target.transform.position + Vector3.up * ShotRecord.BodyHeight;
            target.gameObject.SetActive(false);
            Physics.SyncTransforms();
            var end = ShotGeometry.Resolve(Record(aim, ShotOutcome.Hit), Muzzle, new CosmeticRandom(1));
            Assert.That(end.HasImpact && end.Surface == ImpactSurfaceKind.Character, Is.True);
            Assert.That(OffAxis(end.ImpactPoint), Is.EqualTo(0.5f).Within(0.02f), "radius from the axis, not on it");
            Assert.That(end.ImpactPoint.z, Is.LessThan(target.transform.position.z), "the side that faces the shooter");
            Assert.That(end.ImpactPoint.y, Is.EqualTo(aim.y).Within(1e-4f));
            Assert.That(Vector3.Dot(end.ImpactNormal, (aim - Muzzle).normalized), Is.LessThan(-0.999f), "normal is the reverse direction");
            Assert.That(end.End, Is.EqualTo(end.ImpactPoint));
        }

        [Test]
        public void KillingBlow_OnAScaledDeactivatedTarget_UsesTheScaledRadius()
        {
            target.transform.localScale = new Vector3(2f, 1f, 2f);
            var aim = target.transform.position + Vector3.up * ShotRecord.BodyHeight;
            target.gameObject.SetActive(false);
            var end = ShotGeometry.Resolve(Record(aim, ShotOutcome.Hit), Muzzle, new CosmeticRandom(1));
            Assert.That(OffAxis(end.ImpactPoint), Is.EqualTo(1f).Within(0.02f));
        }

        [Test]
        public void DeactivatedTargetWithNoCapsule_FallsBackToTheAimPoint()
        {
            Object.DestroyImmediate(target.GetComponent<Collider>());
            var aim = target.transform.position + Vector3.up * ShotRecord.BodyHeight;
            target.gameObject.SetActive(false);
            var end = ShotGeometry.Resolve(Record(aim, ShotOutcome.Hit), Muzzle, new CosmeticRandom(1));
            Assert.That(end.HasImpact, Is.True);
            Assert.That(end.ImpactPoint, Is.EqualTo(aim));
            Assert.That(end.End, Is.EqualTo(aim));
        }

        [Test]
        public void DestroyedTarget_FallsBackToTheAimPoint()
        {
            var aim = target.transform.position + Vector3.up * ShotRecord.BodyHeight;
            var record = Record(aim, ShotOutcome.Hit);
            Object.DestroyImmediate(target.gameObject);
            var end = ShotGeometry.Resolve(record, Muzzle, new CosmeticRandom(1));
            Assert.That(end.HasImpact, Is.True);
            Assert.That(end.ImpactPoint, Is.EqualTo(aim));
        }

        [Test]
        public void TargetWithItsCapsuleDisabled_UsesTheCapsuleSurface()
        {
            target.GetComponent<Collider>().enabled = false;
            Physics.SyncTransforms();
            var aim = target.transform.position + Vector3.up * ShotRecord.BodyHeight;
            var end = ShotGeometry.Resolve(Record(aim, ShotOutcome.Hit), Muzzle, new CosmeticRandom(1));
            Assert.That(OffAxis(end.ImpactPoint), Is.EqualTo(0.5f).Within(0.02f));
        }

        [Test]
        public void ALiveTarget_WhoseRayMissesItsCapsule_KeepsTheAimPoint()
        {
            // An aim point well off to the side of the (live, enabled) capsule: the ray never enters it.
            var aim = target.transform.position + new Vector3(3f, ShotRecord.BodyHeight, 0f);
            var end = ShotGeometry.Resolve(Record(aim, ShotOutcome.Hit), Muzzle, new CosmeticRandom(1));
            Assert.That(end.ImpactPoint, Is.EqualTo(aim));
        }

        [Test]
        public void HitBehindAWall_TruncatesTheTracerAtTheWall_AndOmitsTheImpact()
        {
            Wall(new Vector3(0f, 1.5f, 0f), new Vector3(4f, 3f, 0.5f), ImpactSurfaceKind.Concrete);
            var aim = target.transform.position + Vector3.up * ShotRecord.BodyHeight;
            var end = ShotGeometry.Resolve(Record(aim, ShotOutcome.Hit), Muzzle, new CosmeticRandom(1));
            Assert.That(end.HasTracer, Is.True);
            Assert.That(end.End.z, Is.LessThan(0f), "the streak stops at the wall's near face");
            Assert.That(end.HasImpact, Is.False, "a body hit behind a wall is not invented");
        }

        [Test]
        public void CoverMiss_WithAWall_LandsOnTheWall_WithItsSurface()
        {
            Wall(new Vector3(0f, 0.45f, 2f), new Vector3(4f, 0.9f, 0.5f), ImpactSurfaceKind.Metal);
            var aim = target.transform.position + Vector3.up * ShotRecord.BodyHeight;
            var end = ShotGeometry.Resolve(Record(aim, ShotOutcome.CoverMiss), Muzzle, new CosmeticRandom(1));
            Assert.That(end.HasTracer && end.HasImpact, Is.True);
            Assert.That(end.Surface, Is.EqualTo(ImpactSurfaceKind.Metal));
            Assert.That(end.ImpactPoint.z, Is.InRange(1.7f, 2.3f));
        }

        [Test]
        public void CoverMiss_InTheOpen_PassesBesideTheTarget_AndNeverShowsABodyHit()
        {
            var aim = target.transform.position + Vector3.up * ShotRecord.BodyHeight;
            for (var seed = 0; seed < 20; seed++)
            {
                var end = ShotGeometry.Resolve(Record(aim, ShotOutcome.CoverMiss), Muzzle, new CosmeticRandom(seed));
                Assert.That(end.HasTracer, Is.True);
                Assert.That(end.Surface, Is.Not.EqualTo(ImpactSurfaceKind.Character));
                var along = Vector3.Dot(end.End - Muzzle, (aim - Muzzle).normalized);
                Assert.That(along, Is.GreaterThan((aim - Muzzle).magnitude - 0.5f), "the streak carries on past the target");
                Assert.That(Vector3.Distance(end.End, aim), Is.GreaterThan(0.4f), "it does not end on the body");
            }
        }

        [Test]
        public void Melee_HasNoTracer_AndAHitShowsOnlyTheCharacterImpact_AMissShowsNothing()
        {
            var aim = target.transform.position + Vector3.up * ShotRecord.BodyHeight;
            var hit = ShotGeometry.Resolve(Record(aim, ShotOutcome.Hit, melee: true), Muzzle, new CosmeticRandom(1));
            Assert.That(hit.HasTracer, Is.False);
            Assert.That(hit.HasImpact && hit.Surface == ImpactSurfaceKind.Character, Is.True);
            var miss = ShotGeometry.Resolve(Record(aim, ShotOutcome.CoverMiss, melee: true), Muzzle, new CosmeticRandom(1));
            Assert.That(miss.HasTracer || miss.HasImpact, Is.False);
        }

        [Test]
        public void PointBlankAndNonFiniteGeometry_ShowNothing_AndDoNotThrow()
        {
            var aim = new Vector3(1f, 1f, 1f);
            var same = ShotGeometry.Resolve(Record(aim, ShotOutcome.Hit), aim, new CosmeticRandom(1));
            Assert.That(same.HasTracer || same.HasImpact, Is.False);
            var nan = ShotGeometry.Resolve(Record(new Vector3(float.NaN, 0f, 0f), ShotOutcome.CoverMiss), Muzzle, new CosmeticRandom(1));
            Assert.That(nan.HasTracer || nan.HasImpact, Is.False);
            var inf = ShotGeometry.Resolve(Record(new Vector3(float.PositiveInfinity, 0f, 0f), ShotOutcome.Hit), Muzzle, new CosmeticRandom(1));
            Assert.That(inf.HasTracer || inf.HasImpact, Is.False);
        }

        [Test]
        public void UnitsInTheWay_NeverBlockTheStreak()
        {
            var blocker = world.CreateDummy(new Vector3(0f, 0f, 0f));
            Physics.SyncTransforms();
            var aim = target.transform.position + Vector3.up * ShotRecord.BodyHeight;
            var end = ShotGeometry.Resolve(Record(aim, ShotOutcome.Hit), Muzzle, new CosmeticRandom(1));
            Assert.That(blocker, Is.Not.Null);
            Assert.That(Vector3.Distance(end.End, aim), Is.LessThan(0.6f), "units never block (the LineOfSight convention): the streak reaches the target's body");
            Assert.That(end.HasImpact, Is.True);
        }
    }
}
#endif
