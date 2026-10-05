using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class EnemyAIPlayModeTests
    {
        TestWorld world;
        Encounter encounter;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            encounter = world.CreateEncounter();
        }

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        static Health HealthOf(Component unit) => unit.GetComponent<Health>();

        // Wires the sides once every unit exists.
        void Arm(EnemyAI[] hostiles, params CommandableUnit[] friendlies) =>
            encounter.Initialize(friendlies.Select(f => HealthOf(f)), hostiles.Select(h => HealthOf(h)));

        IEnumerator WaitForState(EnemyAI ai, EnemyState state, float timeout) =>
            TestWorld.WaitUntil(() => ai.State == state, timeout);

        [UnityTest]
        public IEnumerator FriendlyBeyondTheRadius_HostileStaysIdle()
        {
            world.CreateEnvironment();
            var friendly = world.CreateFighter(new Vector3(0f, 0f, -14f));
            var hostile = world.CreateHostile(new Vector3(0f, 0f, 2f), encounter, detectionRange: 12f);
            Arm(new[] { hostile }, friendly);

            yield return new WaitForSeconds(0.8f);

            Assert.That(hostile.State, Is.EqualTo(EnemyState.Idle));
            Assert.That(hostile.Target, Is.Null);
            Assert.That(hostile.GetComponent<CommandableUnit>().CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator FriendlyInsideTheRadius_HostileAcquiresChasesAndAttacks()
        {
            world.CreateEnvironment();
            var friendly = world.CreateFighter(new Vector3(0f, 0f, -6f));
            var hostile = world.CreateHostile(new Vector3(0f, 0f, 4f), encounter);
            Arm(new[] { hostile }, friendly);

            yield return WaitForState(hostile, EnemyState.Chase, 1f);
            Assert.That(hostile.State, Is.EqualTo(EnemyState.Chase), "The hostile never acquired its target");
            Assert.That(hostile.Target, Is.SameAs(HealthOf(friendly)));

            yield return WaitForState(hostile, EnemyState.Attack, 6f);
            Assert.That(hostile.State, Is.EqualTo(EnemyState.Attack), "The hostile never reached attack range");
            yield return TestWorld.WaitUntil(() => HealthOf(friendly).Current < HealthOf(friendly).Max, 2f);
            Assert.That(HealthOf(friendly).Current, Is.EqualTo(90), "One hostile hit deals 10");
        }

        [UnityTest]
        public IEnumerator FriendlyBehindAWall_IsNotSeen_UntilItStepsOut()
        {
            // A 2 m high, 6 m wide wall (x -3..3) between them: the eye (1.5 m) cannot see over it.
            world.CreateEnvironment((new Vector3(0f, 1f, 0f), new Vector3(6f, 2f, 1f)));
            var friendly = world.CreateFighter(new Vector3(0f, 0f, -4f));
            var hostile = world.CreateHostile(new Vector3(0f, 0f, 4f), encounter);
            Arm(new[] { hostile }, friendly);

            yield return new WaitForSeconds(0.8f);
            Assert.That(hostile.State, Is.EqualTo(EnemyState.Idle), "A wall must block line of sight");

            // The sight line from the eye (0, 1.5, 4) to the friendly's centre (8, 1, -3) crosses the wall's slab at
            // x 4.0..5.1, clear of its x = 3 end, and the horizontal distance (10.6 m) stays inside the 12 m radius.
            friendly.Issue(new MoveCommand(new Vector3(8f, 0f, -3f)));
            yield return WaitForState(hostile, EnemyState.Chase, 6f);
            Assert.That(hostile.State, Is.Not.EqualTo(EnemyState.Idle), "The hostile must see the friendly once it clears the wall");
            Assert.That(hostile.Target, Is.SameAs(HealthOf(friendly)));
        }

        [UnityTest]
        public IEnumerator Hostile_PicksTheNearestVisibleFriendly()
        {
            world.CreateEnvironment();
            var near = world.CreateFighter(new Vector3(0f, 0f, -5f));
            var far = world.CreateFighter(new Vector3(0f, 0f, -9f));
            var hostile = world.CreateHostile(new Vector3(0f, 0f, 0f), encounter);
            Arm(new[] { hostile }, far, near);   // list order must not matter

            yield return WaitForState(hostile, EnemyState.Chase, 1f);
            Assert.That(hostile.Target, Is.SameAs(HealthOf(near)));
        }

        [UnityTest]
        public IEnumerator TargetDies_HostileReacquiresTheNextFriendly()
        {
            world.CreateEnvironment();
            var fragile = world.CreateFighter(new Vector3(0f, 0f, -3f), maxHealth: 10);
            var other = world.CreateFighter(new Vector3(6f, 0f, -3f));
            var hostile = world.CreateHostile(new Vector3(0f, 0f, 1f), encounter);
            Arm(new[] { hostile }, fragile, other);

            yield return TestWorld.WaitUntil(() => !HealthOf(fragile).IsAlive, 6f);
            Assert.That(HealthOf(fragile).IsAlive, Is.False, "The fragile friendly should have died first");

            yield return TestWorld.WaitUntil(() => hostile.Target == HealthOf(other), 2f);
            Assert.That(hostile.Target, Is.SameAs(HealthOf(other)), "The hostile must reacquire the other friendly");
            Assert.That(hostile.State, Is.EqualTo(EnemyState.Chase).Or.EqualTo(EnemyState.Attack));
        }

        [UnityTest]
        public IEnumerator NoLivingFriendly_HostileIdlesWithoutErrors()
        {
            world.CreateEnvironment();
            var friendly = world.CreateFighter(new Vector3(0f, 0f, -3f));
            var hostile = world.CreateHostile(new Vector3(0f, 0f, 1f), encounter);
            Arm(new[] { hostile }, friendly);
            HealthOf(friendly).TakeDamage(1000);

            yield return new WaitForSeconds(0.8f);

            Assert.That(hostile.State, Is.EqualTo(EnemyState.Idle));
            Assert.That(hostile.Target, Is.Null);
        }

        [UnityTest]
        public IEnumerator DeadHostile_DoesNothing()
        {
            world.CreateEnvironment();
            var friendly = world.CreateFighter(new Vector3(0f, 0f, -3f));
            var hostile = world.CreateHostile(new Vector3(0f, 0f, 1f), encounter);
            Arm(new[] { hostile }, friendly);
            HealthOf(hostile).TakeDamage(1000);

            yield return new WaitForSeconds(0.8f);

            Assert.That(hostile.State, Is.EqualTo(EnemyState.Dead));
            Assert.That(hostile.Target, Is.Null);
            Assert.That(HealthOf(friendly).Current, Is.EqualTo(HealthOf(friendly).Max));
        }

        [UnityTest]
        public IEnumerator HostileHitThroughAWall_RetaliatesAtOnce()
        {
            // A thin (0.2 m), 2 m tall wall: it blocks sight (the ray crosses it at y 1.25), and the NavMesh, eroded
            // 0.5 m for the agent radius, still reaches to 0.6 m of it, so the agents stay at +-0.9: 1.8 m apart,
            // inside melee range. A thicker wall would push them out of range when they snap onto the NavMesh.
            world.CreateEnvironment((new Vector3(0f, 1f, 0f), new Vector3(10f, 2f, 0.2f)));
            var friendly = world.CreateFighter(new Vector3(0f, 0f, -0.9f));
            var hostile = world.CreateHostile(new Vector3(0f, 0f, 0.9f), encounter);
            Arm(new[] { hostile }, friendly);
            yield return new WaitForSeconds(0.6f);
            Assert.That(TestWorld.HorizontalDistance(friendly.transform.position, hostile.transform.position), Is.LessThan(2f),
                "Precondition: the NavMesh must not have pushed the units out of melee range");
            Assert.That(hostile.State, Is.EqualTo(EnemyState.Idle), "Precondition: no line of sight through the wall");

            friendly.Issue(new AttackCommand(HealthOf(hostile)));
            yield return TestWorld.WaitUntil(() => HealthOf(hostile).Current < HealthOf(hostile).Max, 2f);
            yield return null;

            Assert.That(HealthOf(hostile).Current, Is.LessThan(HealthOf(hostile).Max), "Precondition: the friendly hit the hostile through the wall");
            Assert.That(hostile.Target, Is.SameAs(HealthOf(friendly)), "A hostile hit from a blind spot must fight back");
        }

        [UnityTest]
        public IEnumerator UnreachableVisibleFriendly_IsIgnored()
        {
            // Low walls ring the friendly: agents cannot climb 1 m, but a 1.5 m eye sees over them. Phase 4 chased
            // anyway and stood at the ring; now an unreachable friendly is not a target at all.
            world.CreateEnvironment(
                (new Vector3(0f, 0.5f, -8f), new Vector3(6f, 1f, 0.5f)),
                (new Vector3(0f, 0.5f, -2f), new Vector3(6f, 1f, 0.5f)),
                (new Vector3(-3f, 0.5f, -5f), new Vector3(0.5f, 1f, 6f)),
                (new Vector3(3f, 0.5f, -5f), new Vector3(0.5f, 1f, 6f)));
            var friendly = world.CreateFighter(new Vector3(0f, 0f, -5f));
            var hostile = world.CreateHostile(new Vector3(0f, 0f, 4f), encounter);
            Arm(new[] { hostile }, friendly);
            var start = hostile.transform.position;

            yield return new WaitForSeconds(1.5f);

            Assert.That(hostile.State, Is.EqualTo(EnemyState.Idle), "A friendly with no complete path is not a target");
            Assert.That(hostile.Target, Is.Null);
            Assert.That(TestWorld.HorizontalDistance(hostile.transform.position, start), Is.LessThan(0.1f), "The hostile did not move");
            Assert.That(HealthOf(friendly).Current, Is.EqualTo(HealthOf(friendly).Max));
        }

        [UnityTest]
        public IEnumerator UnreachableFriendly_IsSkippedForAFartherReachableOne()
        {
            world.CreateEnvironment(
                (new Vector3(0f, 0.5f, -8f), new Vector3(6f, 1f, 0.5f)),
                (new Vector3(0f, 0.5f, -2f), new Vector3(6f, 1f, 0.5f)),
                (new Vector3(-3f, 0.5f, -5f), new Vector3(0.5f, 1f, 6f)),
                (new Vector3(3f, 0.5f, -5f), new Vector3(0.5f, 1f, 6f)));
            var ringed = world.CreateFighter(new Vector3(0f, 0f, -5f));
            var reachable = world.CreateFighter(new Vector3(6f, 0f, -3f));   // 9.2 m away, clear of the ring
            var hostile = world.CreateHostile(new Vector3(0f, 0f, 4f), encounter);
            Arm(new[] { hostile }, ringed, reachable);

            yield return WaitForState(hostile, EnemyState.Chase, 1.5f);

            Assert.That(hostile.Target, Is.SameAs(HealthOf(reachable)), "The nearer but unreachable friendly must lose to the reachable one");
        }

        [UnityTest]
        public IEnumerator RangedHostile_AcquiresAtDetectionRange_StopsAtItsRange_AndFires()
        {
            world.CreateEnvironment();
            var friendly = world.CreateFighter(new Vector3(0f, 0f, -11f));
            var hostile = world.CreateHostile(Vector3.zero, encounter, damage: 8, cooldown: 1.5f, role: CombatRole.Ranged, range: 8f);
            Arm(new[] { hostile }, friendly);
            var distanceAtFirstHit = -1f;
            hostile.GetComponent<UnitAttacker>().Attacked += _ =>
            {
                if (distanceAtFirstHit < 0f)
                    distanceAtFirstHit = TestWorld.HorizontalDistance(hostile.transform.position, friendly.transform.position);
            };

            yield return WaitForState(hostile, EnemyState.Chase, 1f);
            Assert.That(hostile.Target, Is.SameAs(HealthOf(friendly)), "11 m is inside the 12 m detection range");
            yield return TestWorld.WaitUntil(() => distanceAtFirstHit >= 0f, 6f);

            Assert.That(distanceAtFirstHit, Is.GreaterThanOrEqualTo(6f).And.LessThanOrEqualTo(8.5f), "A ranged hostile fires from its range");
            Assert.That(HealthOf(friendly).Current, Is.EqualTo(HealthOf(friendly).Max - 8));
        }

        [UnityTest]
        public IEnumerator DestroyedFriendlyInTheList_IsIgnored()
        {
            world.CreateEnvironment();
            var doomed = world.CreateFighter(new Vector3(0f, 0f, -3f));
            var other = world.CreateFighter(new Vector3(0f, 0f, -6f));
            var hostile = world.CreateHostile(new Vector3(0f, 0f, 1f), encounter);
            Arm(new[] { hostile }, doomed, other);
            Object.DestroyImmediate(doomed.gameObject);

            yield return WaitForState(hostile, EnemyState.Chase, 1f);

            Assert.That(hostile.Target, Is.SameAs(HealthOf(other)));
        }

        static UnitCover CoverOf(Component unit) => unit.GetComponent<UnitCover>();

        // A 0.9 m wall from z -0.25 to 0.25 (x -2..2), one point 0.75 m north of it facing south (into the wall), and a
        // sturdy friendly 5 m south of the wall. From the point a ranged hostile shoots the friendly over the wall
        // (6 m, in range; the 1.5 m eye line clears 0.9 m) while the friendly's eye-to-feet ray crosses the wall.
        (CoverLocation point, CoverRegistry registry, CommandableUnit friendly) CoverLayout()
        {
            var environment = world.CreateEnvironment((new Vector3(0f, 0.45f, 0f), new Vector3(4f, 0.9f, 0.5f)));
            var point = world.CreateCoverPoint(new Vector3(0f, 0f, 1f), Vector3.back, TestWorld.ObstacleCollider(environment));
            var registry = world.CreateRegistry(point);
            var friendly = world.CreateFighter(new Vector3(0f, 0f, -5f), maxHealth: 300);
            return (point, registry, friendly);
        }

        [UnityTest]
        public IEnumerator RangedHostile_TakesNearbyCover_ThenFiresFromIt()
        {
            var (point, registry, friendly) = CoverLayout();
            // 10 m from the friendly: inside the 12 m detection range, outside the 8 m attack range; the point is 4 m away.
            var hostile = world.CreateHostile(new Vector3(0f, 0f, 5f), encounter, damage: 8, cooldown: 1.5f, role: CombatRole.Ranged, range: 8f, registry: registry);
            Arm(new[] { hostile }, friendly);
            var distanceAtFirstHit = -1f;
            hostile.GetComponent<UnitAttacker>().Attacked += _ =>
            {
                if (distanceAtFirstHit < 0f)
                    distanceAtFirstHit = TestWorld.HorizontalDistance(hostile.transform.position, friendly.transform.position);
            };

            yield return WaitForState(hostile, EnemyState.Cover, 1.5f);
            Assert.That(hostile.State, Is.EqualTo(EnemyState.Cover), "A ranged hostile with useful cover nearby walks to it first");
            Assert.That(hostile.Target, Is.SameAs(HealthOf(friendly)), "The queued attack already names the target");
            Assert.That(point.Claimant, Is.SameAs(CoverOf(hostile)));
            yield return TestWorld.WaitUntil(() => CoverOf(hostile).Status == CoverStatus.Occupied, 5f);
            Assert.That(CoverOf(hostile).Status, Is.EqualTo(CoverStatus.Occupied), "It arrived in cover");
            yield return TestWorld.WaitUntil(() => distanceAtFirstHit >= 0f, 6f);

            Assert.That(distanceAtFirstHit, Is.EqualTo(6f).Within(0.75f), "It fires from the point, 6 m from the friendly");
            Assert.That(CoverOf(hostile).Status, Is.EqualTo(CoverStatus.Occupied), "Still in cover after firing");
            Assert.That(hostile.State, Is.EqualTo(EnemyState.Attack));
        }

        [UnityTest]
        public IEnumerator RangedHostile_IgnoresATallLocation_AndAttacksWithoutCover()
        {
            // The same layout as CoverLayout, but the only location is Tall (a hiding spot): a hostile standing there
            // could not shoot, and without a peek behaviour it would leave it again, so it never takes one.
            var environment = world.CreateEnvironment((new Vector3(0f, 0.45f, 0f), new Vector3(4f, 0.9f, 0.5f)));
            var tall = world.CreateCoverPoint(new Vector3(0f, 0f, 1f), Vector3.back, TestWorld.ObstacleCollider(environment), 0.5f, CoverHeight.Tall);
            var registry = world.CreateRegistry(tall);
            var friendly = world.CreateFighter(new Vector3(0f, 0f, -5f), maxHealth: 300);
            var hostile = world.CreateHostile(new Vector3(0f, 0f, 5f), encounter, damage: 8, cooldown: 1.5f, role: CombatRole.Ranged, range: 8f, registry: registry);
            Arm(new[] { hostile }, friendly);

            yield return WaitForState(hostile, EnemyState.Chase, 1.5f);

            Assert.That(hostile.State, Is.EqualTo(EnemyState.Chase), "A plain attack: no Low location within reach");
            Assert.That(hostile.Target, Is.SameAs(HealthOf(friendly)), "Precondition: the hostile acquired the friendly");
            Assert.That(tall.IsClaimed, Is.False);
            Assert.That(CoverOf(hostile).Status, Is.EqualTo(CoverStatus.None));
        }

        [UnityTest]
        public IEnumerator MeleeHostile_InTheSameLayout_ChargesWithoutClaimingAPoint()
        {
            var (point, registry, friendly) = CoverLayout();
            var hostile = world.CreateHostile(new Vector3(0f, 0f, 5f), encounter, registry: registry);   // melee
            Arm(new[] { hostile }, friendly);
            var everCover = false;
            var everClaimed = false;

            var deadline = Time.realtimeSinceStartup + 8f;
            while (HealthOf(friendly).Current == HealthOf(friendly).Max && Time.realtimeSinceStartup < deadline)
            {
                everCover |= hostile.State == EnemyState.Cover;
                everClaimed |= point.IsClaimed;
                yield return null;
            }

            Assert.That(HealthOf(friendly).Current, Is.LessThan(HealthOf(friendly).Max), "Precondition: the melee hostile reached and hit the friendly");
            Assert.That(everCover, Is.False, "A melee hostile never seeks cover");
            Assert.That(everClaimed, Is.False, "...and claims no point on its way");
        }

        [UnityTest]
        public IEnumerator RangedHostile_WithTheOnlyPointClaimed_AttacksWithoutCover()
        {
            var (point, registry, friendly) = CoverLayout();
            var squatter = world.CreateFighter(new Vector3(0f, 0f, 3f), registry: registry);   // not in the encounter
            Assert.That(squatter.Issue(new MoveToCoverCommand(point)), Is.True);
            var hostile = world.CreateHostile(new Vector3(0f, 0f, 5f), encounter, damage: 8, cooldown: 1.5f, role: CombatRole.Ranged, range: 8f, registry: registry);
            Arm(new[] { hostile }, friendly);

            yield return WaitForState(hostile, EnemyState.Chase, 1.5f);
            Assert.That(hostile.State, Is.EqualTo(EnemyState.Chase), "No usable point: a plain attack, approaching to range");
            Assert.That(hostile.Target, Is.SameAs(HealthOf(friendly)));
            Assert.That(CoverOf(hostile).Status, Is.EqualTo(CoverStatus.None));
            yield return TestWorld.WaitUntil(() => HealthOf(friendly).Current < HealthOf(friendly).Max, 8f);

            Assert.That(HealthOf(friendly).Current, Is.LessThan(HealthOf(friendly).Max), "It still fights");
            Assert.That(point.Claimant, Is.SameAs(CoverOf(squatter)), "The squatter keeps the point");
        }

        [UnityTest]
        public IEnumerator RangedHostile_StandingOnAUsefulPoint_FiresFromIt_WithoutMoving()
        {
            var (point, registry, friendly) = CoverLayout();
            var hostile = world.CreateHostile(new Vector3(0f, 0f, 1f), encounter, damage: 8, cooldown: 1.5f, role: CombatRole.Ranged, range: 8f, registry: registry);
            Arm(new[] { hostile }, friendly);
            var start = hostile.transform.position;

            yield return TestWorld.WaitUntil(() => HealthOf(friendly).Current < HealthOf(friendly).Max, 6f);

            Assert.That(HealthOf(friendly).Current, Is.LessThan(HealthOf(friendly).Max), "Precondition: it fired");
            Assert.That(TestWorld.HorizontalDistance(hostile.transform.position, start), Is.LessThan(0.3f), "Its own point is useful: no walk");
            Assert.That(CoverOf(hostile).Point, Is.SameAs(point));
            Assert.That(CoverOf(hostile).Status, Is.EqualTo(CoverStatus.Occupied));
            Assert.That(CoverOf(hostile).OccupiedByOrder, Is.True, "The cover order on its own spot completed at once");
        }

        [UnityTest]
        public IEnumerator IdleHostile_StandingBesideAFreePoint_OccupiesIt()
        {
            var (point, registry, _) = CoverLayout();
            var hostile = world.CreateHostile(new Vector3(0.3f, 0f, 1f), encounter, registry: registry);
            Arm(new[] { hostile });   // no friendlies: it stays idle

            yield return TestWorld.WaitUntil(() => CoverOf(hostile).Status == CoverStatus.Occupied, 2f);

            Assert.That(hostile.State, Is.EqualTo(EnemyState.Idle));
            Assert.That(CoverOf(hostile).Status, Is.EqualTo(CoverStatus.Occupied));
            Assert.That(CoverOf(hostile).Point, Is.SameAs(point));
            Assert.That(CoverOf(hostile).OccupiedByOrder, Is.False, "It only happens to stand there");
        }
    }
}
