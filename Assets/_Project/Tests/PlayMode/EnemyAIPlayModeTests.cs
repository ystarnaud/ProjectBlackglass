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
        public IEnumerator UnreachableVisibleFriendly_IsRechasedWithoutErrors()
        {
            // Low walls ring the friendly: agents cannot climb 1 m (0.8 m, just over their 0.75 m climb,
            // was still crossed once voxelized), but a 1.5 m eye sees over them.
            world.CreateEnvironment(
                (new Vector3(0f, 0.5f, -8f), new Vector3(6f, 1f, 0.5f)),
                (new Vector3(0f, 0.5f, -2f), new Vector3(6f, 1f, 0.5f)),
                (new Vector3(-3f, 0.5f, -5f), new Vector3(0.5f, 1f, 6f)),
                (new Vector3(3f, 0.5f, -5f), new Vector3(0.5f, 1f, 6f)));
            var friendly = world.CreateFighter(new Vector3(0f, 0f, -5f));
            var hostile = world.CreateHostile(new Vector3(0f, 0f, 4f), encounter);
            Arm(new[] { hostile }, friendly);

            yield return new WaitForSeconds(3f);

            Assert.That(HealthOf(friendly).Current, Is.EqualTo(HealthOf(friendly).Max), "The hostile must not reach the ringed friendly");
            Assert.That(hostile.State, Is.Not.EqualTo(EnemyState.Dead));
            Assert.That(TestWorld.HorizontalDistance(hostile.transform.position, friendly.transform.position), Is.LessThan(6f),
                "The hostile should have walked up to the ring");
            // Any logged error during the 3 s fails the test on its own.
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

        [UnityTest]
        public IEnumerator HasLineOfSight_UnitsNeverBlock_WallsDo()
        {
            world.CreateEnvironment((new Vector3(5f, 1f, 0f), new Vector3(1f, 2f, 6f)));
            var seen = world.CreateFighter(new Vector3(0f, 0f, -4f));
            var blocker = world.CreateFighter(new Vector3(0f, 0f, -2f));
            var behindWall = world.CreateFighter(new Vector3(8f, 0f, 0f));
            yield return new WaitForFixedUpdate();   // colliders take their positions
            var buffer = new RaycastHit[8];
            var eye = new Vector3(0f, 1.5f, 1f);

            Assert.That(EnemyAI.HasLineOfSight(eye, HealthOf(seen), ~0, buffer), Is.True, "A unit in between must not block sight");
            Assert.That(EnemyAI.HasLineOfSight(eye, HealthOf(blocker), ~0, buffer), Is.True);
            Assert.That(EnemyAI.HasLineOfSight(eye, HealthOf(behindWall), ~0, buffer), Is.False, "A wall must block sight");
        }
    }
}
