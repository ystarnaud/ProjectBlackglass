using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CoverCombatPlayModeTests
    {
        TestWorld world;
        GameObject environment;
        CoverPoint point;
        CoverRegistry registry;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            // A 0.9 m wall from z -0.25 to 0.25 (x -2..2); the point 0.75 m south of it. A shooter north of the wall
            // sees over it (the 1.5 m eye line to a 1 m pivot stays above 1 m) while its eye-to-feet ray crosses it.
            environment = world.CreateEnvironment((new Vector3(0f, 0.45f, 0f), new Vector3(4f, 0.9f, 0.5f)));
            point = world.CreateCoverPoint(new Vector3(0f, 0f, -1f), Vector3.forward, TestWorld.ObstacleCollider(environment));
            registry = world.CreateRegistry(point);
        }

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        static Health HealthOf(Component unit) => unit.GetComponent<Health>();
        static UnitCover CoverOf(Component unit) => unit.GetComponent<UnitCover>();
        static UnitAttacker AttackerOf(Component unit) => unit.GetComponent<UnitAttacker>();

        // Ranged units retaliate from where they stand (range 8, sight over the wall), so a covered defender stays put.
        CommandableUnit Ranged(Vector3 at) => world.CreateFighter(at, damage: 10, cooldown: 0.3f, role: CombatRole.Ranged, range: 8f, registry: registry);

        IEnumerator PutInCover(CommandableUnit unit)
        {
            Assert.That(unit.Issue(new MoveToCoverCommand(point)), Is.True);
            yield return TestWorld.WaitUntil(() => CoverOf(unit).Status == CoverStatus.Occupied, 10f);
            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.Occupied), "Precondition: the defender is in cover");
            yield return new WaitForFixedUpdate();
        }

        [UnityTest]
        public IEnumerator AlwaysMissRoll_CoveredTarget_TakesNoDamage_AndAlwaysHitRoll_Does()
        {
            var defender = Ranged(new Vector3(0f, 0f, -3f));
            yield return PutInCover(defender);
            var shooter = Ranged(new Vector3(0f, 0f, 5f));
            AttackerOf(shooter).HitRoll = () => 0.99f;
            yield return new WaitForFixedUpdate();
            Assert.That(AttackerOf(shooter).IsTargetInCover(HealthOf(defender), out var chance), Is.True, "Precondition: the wall is between them");
            Assert.That(chance, Is.EqualTo(0.5f));

            Assert.That(shooter.Issue(new AttackCommand(HealthOf(defender))), Is.True);
            yield return TestWorld.WaitUntil(() => AttackerOf(shooter).ShotsFired >= 3, 4f);
            Assert.That(AttackerOf(shooter).ShotsFired, Is.GreaterThanOrEqualTo(3), "Precondition: shots were fired");
            Assert.That(AttackerOf(shooter).Hits, Is.EqualTo(0));
            Assert.That(HealthOf(defender).Current, Is.EqualTo(HealthOf(defender).Max), "Every shot at a covered target missed");

            AttackerOf(shooter).HitRoll = () => 0f;
            yield return TestWorld.WaitUntil(() => HealthOf(defender).Current < HealthOf(defender).Max, 2f);
            Assert.That(HealthOf(defender).Current, Is.EqualTo(HealthOf(defender).Max - 10), "A winning roll lands as usual");
            Assert.That(AttackerOf(shooter).Hits, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ShotFromTheFlank_IgnoresCover()
        {
            var defender = Ranged(new Vector3(0f, 0f, -3f));
            yield return PutInCover(defender);
            // East of the defender, past the wall's end (x 2): the eye-to-feet ray never crosses the wall.
            var flanker = Ranged(new Vector3(6f, 0f, -1f));
            AttackerOf(flanker).HitRoll = () => 0.99f;
            yield return new WaitForFixedUpdate();
            Assert.That(AttackerOf(flanker).IsTargetInCover(HealthOf(defender)), Is.False, "No obstacle between them");

            Assert.That(flanker.Issue(new AttackCommand(HealthOf(defender))), Is.True);
            yield return TestWorld.WaitUntil(() => HealthOf(defender).Current < HealthOf(defender).Max, 2f);

            Assert.That(HealthOf(defender).Current, Is.LessThan(HealthOf(defender).Max), "Cover does not protect from the wrong direction");
        }

        [UnityTest]
        public IEnumerator MeleeAttacker_IgnoresCover()
        {
            var defender = Ranged(new Vector3(0f, 0f, -3f));
            yield return PutInCover(defender);
            var brawler = world.CreateFighter(new Vector3(0f, 0f, -2.5f), cooldown: 0.3f, registry: registry);   // 1.5 m south, in melee range
            AttackerOf(brawler).HitRoll = () => 0.99f;
            yield return null;

            Assert.That(brawler.Issue(new AttackCommand(HealthOf(defender))), Is.True);
            yield return TestWorld.WaitUntil(() => HealthOf(defender).Current < HealthOf(defender).Max, 2f);

            Assert.That(HealthOf(defender).Current, Is.LessThan(HealthOf(defender).Max), "Melee is unaffected by cover");
        }

        [UnityTest]
        public IEnumerator ATallWall_StillBlocksTheShotEntirely()
        {
            // Swap the waist-high wall for a 2 m one, 20 m wide so no firing position within 4 m sees past it.
            Object.Destroy(environment);
            Object.Destroy(point.gameObject);
            yield return null;
            environment = world.CreateEnvironment((new Vector3(0f, 1f, 0f), new Vector3(20f, 2f, 0.5f)));
            point = world.CreateCoverPoint(new Vector3(0f, 0f, -1f), Vector3.forward, TestWorld.ObstacleCollider(environment));
            registry.Initialize(point);
            var defender = Ranged(new Vector3(0f, 0f, -3f));
            yield return PutInCover(defender);
            var shooter = Ranged(new Vector3(0f, 0f, 5f));
            AttackerOf(shooter).HitRoll = () => 0f;
            yield return new WaitForFixedUpdate();

            Assert.That(shooter.Issue(new AttackCommand(HealthOf(defender))), Is.True);
            yield return new WaitForSeconds(1.5f);

            Assert.That(AttackerOf(shooter).ShotsFired, Is.EqualTo(0), "No sight, no shot: cover never comes up");
            Assert.That(HealthOf(defender).Current, Is.EqualTo(HealthOf(defender).Max));
            Assert.That(shooter.AttackPhase, Is.Not.EqualTo(AttackPhase.Attack));
        }

        [UnityTest]
        public IEnumerator Miss_RaisesMissed_CountsTheShot_AndShowsTheAttackLine()
        {
            var defender = Ranged(new Vector3(0f, 0f, -3f));
            yield return PutInCover(defender);
            var shooter = Ranged(new Vector3(0f, 0f, 5f));
            AttackerOf(shooter).HitRoll = () => 0.99f;
            var lineObject = new GameObject("AttackLine");
            lineObject.transform.SetParent(shooter.transform, false);
            var line = lineObject.AddComponent<LineRenderer>();
            shooter.gameObject.SetActive(false);
            var view = shooter.gameObject.AddComponent<AttackLineView>();
            view.Initialize(line);
            shooter.gameObject.SetActive(true);
            var misses = 0;
            AttackerOf(shooter).Missed += _ => misses++;
            yield return new WaitForFixedUpdate();

            Assert.That(shooter.Issue(new AttackCommand(HealthOf(defender))), Is.True);
            yield return TestWorld.WaitUntil(() => misses >= 1, 3f);

            Assert.That(misses, Is.GreaterThanOrEqualTo(1), "Missed must be raised");
            Assert.That(AttackerOf(shooter).ShotsFired, Is.EqualTo(misses));
            Assert.That(AttackerOf(shooter).Hits, Is.EqualTo(0));
            Assert.That(view.IsShowing, Is.True, "The attack line shows on a miss too");
        }
    }
}
