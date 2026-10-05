#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class UnitCoverPlayModeTests
    {
        TestWorld world;
        CoverLocation point;
        CoverRegistry registry;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            // A 0.9 m wall from z -0.25 to 0.25 and one point 0.75 m south of it.
            var environment = world.CreateEnvironment((new Vector3(0f, 0.45f, 0f), new Vector3(4f, 0.9f, 0.5f)));
            point = world.CreateCoverPoint(new Vector3(0f, 0f, -1f), Vector3.forward, TestWorld.ObstacleCollider(environment));
            registry = world.CreateRegistry(point);
        }

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        static UnitCover CoverOf(Component unit) => unit.GetComponent<UnitCover>();

        IEnumerator WalkOntoThePoint(CommandableUnit unit)
        {
            Assert.That(unit.Issue(new MoveCommand(point.Position)), Is.True);
            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 10f);
            Assert.That(unit.CurrentCommand, Is.Null, "Precondition: the unit arrived");
            yield return TestWorld.WaitUntil(() => CoverOf(unit).Status == CoverStatus.Occupied, 2f);
        }

        [UnityTest]
        public IEnumerator PlainMoveOntoAFreePoint_OccupiesItAfterStopping_WithoutTheOrderFlag()
        {
            var unit = world.CreateFighter(new Vector3(0f, 0f, -6f), registry: registry);
            yield return WalkOntoThePoint(unit);

            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.Occupied));
            Assert.That(CoverOf(unit).Point, Is.SameAs(point));
            Assert.That(CoverOf(unit).OccupiedByOrder, Is.False, "Standing there is not an ordered occupancy");
            Assert.That(point.Claimant, Is.SameAs(CoverOf(unit)));
        }

        [UnityTest]
        public IEnumerator WalkingThroughAFreePoint_NeverClaimsIt()
        {
            var unit = world.CreateFighter(new Vector3(4f, 0f, -1f), registry: registry);
            Assert.That(unit.Issue(new MoveCommand(new Vector3(-4f, 0f, -1f))), Is.True);   // straight across the point
            var everClaimed = false;
            var deadline = Time.realtimeSinceStartup + 6f;
            while (unit.CurrentCommand != null && Time.realtimeSinceStartup < deadline)
            {
                everClaimed |= point.IsClaimed;
                yield return null;
            }
            yield return new WaitForSeconds(0.5f);

            Assert.That(unit.CurrentCommand, Is.Null, "Precondition: the walk finished");
            Assert.That(everClaimed, Is.False, "A moving unit claims nothing");
            Assert.That(point.IsClaimed, Is.False, "3 m away at the end: nothing to claim");
        }

        [UnityTest]
        public IEnumerator OccupiedUnit_MovingAway_ReleasesThePoint()
        {
            var unit = world.CreateFighter(new Vector3(0f, 0f, -6f), registry: registry);
            yield return WalkOntoThePoint(unit);
            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.Occupied), "Precondition");

            Assert.That(unit.Issue(new MoveCommand(new Vector3(0f, 0f, -6f))), Is.True);
            yield return TestWorld.WaitUntil(() => CoverOf(unit).Status == CoverStatus.None, 3f);

            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.None), "Beyond 1 m the point is released");
            Assert.That(point.IsClaimed, Is.False);
        }

        [UnityTest]
        public IEnumerator DyingUnit_ReleasesItsPoint()
        {
            var unit = world.CreateFighter(new Vector3(0f, 0f, -6f), registry: registry);
            yield return WalkOntoThePoint(unit);
            Assert.That(point.IsClaimed, Is.True, "Precondition");

            unit.GetComponent<Health>().TakeDamage(1000);
            yield return null;

            Assert.That(point.IsClaimed, Is.False, "A corpse holds no cover");
            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.None));
        }

        [UnityTest]
        public IEnumerator DyingUnit_ThatIsNotDeactivated_DoesNotReclaimCover()
        {
            var unit = world.CreateFighter(new Vector3(0f, 0f, -6f), registry: registry);
            yield return WalkOntoThePoint(unit);
            Assert.That(point.IsClaimed, Is.True, "Precondition");

            var health = unit.GetComponent<Health>();
            var serialized = new UnityEditor.SerializedObject(health);
            serialized.FindProperty("disableOnDeath").boolValue = false;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            health.TakeDamage(1000);
            yield return null;
            yield return null;

            Assert.That(unit.gameObject.activeInHierarchy, Is.True, "Precondition: the corpse stays active");
            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.None));
            Assert.That(point.IsClaimed, Is.False, "A corpse standing on a free point must not claim it again");
            Assert.That(CoverOf(unit).TryReserve(point), Is.False, "No order can reserve cover for a corpse");
        }

        [UnityTest]
        public IEnumerator DestroyedPoint_IsReleased()
        {
            var unit = world.CreateFighter(new Vector3(0f, 0f, -6f), registry: registry);
            yield return WalkOntoThePoint(unit);
            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.Occupied), "Precondition");

            point.Retire();
            yield return null;
            yield return null;

            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.None), "Cover that no longer exists is let go");
            Assert.That(CoverOf(unit).Point, Is.Null);
        }

        [UnityTest]
        public IEnumerator SteeringAway_ReleasesThePoint()
        {
            var unit = world.CreateFighter(new Vector3(0f, 0f, -6f), registry: registry);
            yield return WalkOntoThePoint(unit);
            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.Occupied), "Precondition");

            unit.SetMoveIntent(Vector3.back);   // direct control walks it south
            yield return TestWorld.WaitUntil(() => CoverOf(unit).Status == CoverStatus.None, 3f);
            unit.SetMoveIntent(Vector3.zero);

            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.None));
            Assert.That(point.IsClaimed, Is.False);
        }
    }
}
#endif
