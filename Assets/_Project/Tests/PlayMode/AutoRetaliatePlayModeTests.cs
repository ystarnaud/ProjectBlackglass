using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class AutoRetaliatePlayModeTests
    {
        TestWorld world;
        CommandableUnit victim;
        CommandableUnit aggressor;
        Health victimHealth;
        Health aggressorHealth;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            world.CreateEnvironment();
            // Adjacent, inside each other's 2 m range.
            victim = world.CreateFighter(new Vector3(0f, 0f, 0f));
            aggressor = world.CreateFighter(new Vector3(0f, 0f, 1.5f), cooldown: 0.2f);
            victimHealth = victim.GetComponent<Health>();
            aggressorHealth = aggressor.GetComponent<Health>();
        }

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        static bool IsAttacking(CommandableUnit unit, Health target) =>
            unit.CurrentCommand is AttackCommand attack && attack.Target == target;

        IEnumerator WaitForFirstHit() => TestWorld.WaitUntil(() => victimHealth.Current < victimHealth.Max, 2f);

        [UnityTest]
        public IEnumerator IdleUnitHit_AttacksItsAttacker()
        {
            yield return null;
            aggressor.Issue(new AttackCommand(victimHealth));
            yield return WaitForFirstHit();
            yield return null;

            Assert.That(victimHealth.Current, Is.LessThan(victimHealth.Max), "Precondition: the victim was hit");
            Assert.That(IsAttacking(victim, aggressorHealth), Is.True, "The idle victim must attack back");
            yield return TestWorld.WaitUntil(() => aggressorHealth.Current < aggressorHealth.Max, 2f);
            Assert.That(aggressorHealth.Current, Is.LessThan(aggressorHealth.Max), "The retaliation never landed");
        }

        [UnityTest]
        public IEnumerator UnitWithAMoveOrder_KeepsMoving_WhenHit()
        {
            yield return null;
            victim.Issue(new MoveCommand(new Vector3(0f, 0f, -12f)));
            aggressor.Issue(new AttackCommand(victimHealth));
            yield return WaitForFirstHit();
            yield return null;

            Assert.That(victimHealth.Current, Is.LessThan(victimHealth.Max), "Precondition: the victim was hit");
            Assert.That(victim.CurrentCommand, Is.TypeOf<MoveCommand>(), "An order must win over retaliation");
            Assert.That(victim.PendingCommands, Is.Empty);
        }

        [UnityTest]
        public IEnumerator AfterStop_UnitRetaliatesOnTheNextHit()
        {
            yield return null;
            victim.Issue(new MoveCommand(new Vector3(0f, 0f, -12f)));
            yield return null;
            victim.Issue(new StopCommand());
            aggressor.Issue(new AttackCommand(victimHealth));
            yield return WaitForFirstHit();
            yield return null;

            Assert.That(IsAttacking(victim, aggressorHealth), Is.True, "A stopped unit is idle and fights back when hit");
        }

        [UnityTest]
        public IEnumerator RetaliatingUnit_OrderedAway_Retreats_AndStaysOnItsOrderWhileHit()
        {
            yield return null;
            aggressor.Issue(new AttackCommand(victimHealth));
            yield return WaitForFirstHit();
            yield return null;
            Assert.That(IsAttacking(victim, aggressorHealth), Is.True, "Precondition: the victim is retaliating");
            var start = victim.transform.position;

            var retreat = new MoveCommand(new Vector3(0f, 0f, -12f));
            Assert.That(victim.Issue(retreat), Is.True);
            var healthAtRetreat = victimHealth.Current;
            // The aggressor chases and hits again (0.2 s cooldown); wait for that hit rather than a fixed time.
            yield return TestWorld.WaitUntil(() => victimHealth.Current < healthAtRetreat, 1.5f);
            yield return TestWorld.WaitUntil(() => TestWorld.HorizontalDistance(victim.transform.position, start) > 1f, 1.5f);

            Assert.That(victim.CurrentCommand, Is.SameAs(retreat), "Later hits must not replace the retreat order");
            Assert.That(TestWorld.HorizontalDistance(victim.transform.position, start), Is.GreaterThan(1f), "The unit did not retreat");
            Assert.That(victimHealth.Current, Is.LessThan(healthAtRetreat), "Precondition: the aggressor hit the retreating unit again");
        }

        [UnityTest]
        public IEnumerator UnitWithAHeldMoveIntent_DoesNotRetaliate()
        {
            yield return null;
            victim.SetMoveIntent(Vector3.forward * 0.01f);   // held, barely moving, so it stays in range
            // CommandableUnit drops any order on the next frame while keys are held, so a later null check alone
            // cannot tell "never issued" from "issued then dropped". Handlers run in subscription order, so
            // AutoRetaliate has already reacted when this one samples the unit inside the hit.
            var hitSeen = false;
            UnitCommand commandAtHit = null;
            victimHealth.AttackedBy += _ =>
            {
                hitSeen = true;
                commandAtHit = victim.CurrentCommand;
            };
            aggressor.Issue(new AttackCommand(victimHealth));
            yield return WaitForFirstHit();
            yield return null;

            Assert.That(victimHealth.Current, Is.LessThan(victimHealth.Max), "Precondition: the victim was hit");
            Assert.That(hitSeen, Is.True, "Precondition: the hit event reached the test handler");
            Assert.That(commandAtHit, Is.Null, "Held keys win: no retaliation order may be issued at the hit");
            Assert.That(victim.CurrentCommand, Is.Null, "Held keys win: no retaliation order");
        }

        [UnityTest]
        public IEnumerator KillingBlow_DoesNotMakeTheVictimRetaliate()
        {
            yield return null;
            var fragile = world.CreateFighter(new Vector3(1.5f, 0f, 1.5f), maxHealth: 10);
            var fragileHealth = fragile.GetComponent<Health>();
            var hitSeen = false;
            UnitCommand commandAtHit = null;
            // Subscribed after the fighter exists, so AutoRetaliate's handler has already run when this samples.
            fragileHealth.AttackedBy += _ =>
            {
                hitSeen = true;
                commandAtHit = fragile.CurrentCommand;
            };
            aggressor.Issue(new AttackCommand(fragileHealth));
            yield return TestWorld.WaitUntil(() => !fragileHealth.IsAlive, 2f);
            yield return null;

            Assert.That(fragileHealth.IsAlive, Is.False, "Precondition: the first hit killed it");
            Assert.That(fragile.CurrentCommand, Is.Null, "A corpse must not carry a retaliation order");
            Assert.That(aggressorHealth.Current, Is.EqualTo(aggressorHealth.Max), "The dead unit must not have struck back");
            Assert.That(hitSeen, Is.True, "Precondition: the killing blow was observed");
            Assert.That(commandAtHit, Is.Null, "A dying unit must not issue a retaliation order inside the hit");
        }

        [UnityTest]
        public IEnumerator Retaliation_ChasesAnAttackerThatSteppedOutOfRange()
        {
            yield return null;
            aggressor.Issue(new AttackCommand(victimHealth));
            yield return WaitForFirstHit();
            aggressor.Issue(new MoveCommand(new Vector3(0f, 0f, 8f)));
            yield return new WaitForSeconds(1.5f);

            Assert.That(IsAttacking(victim, aggressorHealth), Is.True, "The retaliation order must keep going");
            Assert.That(TestWorld.HorizontalDistance(victim.transform.position, aggressor.transform.position), Is.LessThan(3f),
                "The retaliating unit did not chase");
        }

        // Cover away from the fixture's duel. The loose box is only there so the point has an obstacle; the hold rule
        // is about the order, not the geometry, and the shooter's roll always hits.
        (CoverLocation point, CoverRegistry registry) LooseCover(Vector3 standAt)
        {
            var box = world.CreateObstacle(standAt + new Vector3(0f, 0.45f, 1f), new Vector3(2f, 0.9f, 0.5f));
            var point = world.CreateCoverPoint(standAt, Vector3.forward, box.GetComponent<Collider>());
            return (point, world.CreateRegistry(point));
        }

        [UnityTest]
        public IEnumerator UnitHoldingOrderedCover_HitByADistantShooter_StaysPut()
        {
            yield return null;
            var (point, registry) = LooseCover(new Vector3(6f, 0f, 0f));
            var covered = world.CreateFighter(new Vector3(6f, 0f, -3f), registry: registry);
            var coveredHealth = covered.GetComponent<Health>();
            Assert.That(covered.Issue(new MoveToCoverCommand(point)), Is.True);
            yield return TestWorld.WaitUntil(() => covered.Cover.Status == CoverStatus.Occupied, 5f);
            Assert.That(covered.Cover.OccupiedByOrder, Is.True, "Precondition: ordered cover");
            var shooter = world.CreateFighter(new Vector3(6f, 0f, 6f), cooldown: 0.2f, role: CombatRole.Ranged, range: 8f, registry: registry);
            shooter.GetComponent<UnitAttacker>().HitRoll = () => 0f;
            yield return new WaitForFixedUpdate();

            shooter.Issue(new AttackCommand(coveredHealth));
            yield return TestWorld.WaitUntil(() => coveredHealth.Current < coveredHealth.Max, 2f);
            yield return null;

            Assert.That(coveredHealth.Current, Is.LessThan(coveredHealth.Max), "Precondition: it was hit");
            Assert.That(covered.CurrentCommand, Is.Null, "A melee unit holding ordered cover does not charge a shooter it cannot reach");
            Assert.That(covered.Cover.Status, Is.EqualTo(CoverStatus.Occupied));
        }

        [UnityTest]
        public IEnumerator UnitHoldingOrderedCover_HitByAnAdjacentMeleeAttacker_FightsBackInPlace()
        {
            yield return null;
            var (point, registry) = LooseCover(new Vector3(6f, 0f, 0f));
            var covered = world.CreateFighter(new Vector3(6f, 0f, -3f), registry: registry);
            var coveredHealth = covered.GetComponent<Health>();
            Assert.That(covered.Issue(new MoveToCoverCommand(point)), Is.True);
            yield return TestWorld.WaitUntil(() => covered.Cover.Status == CoverStatus.Occupied, 5f);
            Assert.That(covered.Cover.OccupiedByOrder, Is.True, "Precondition: ordered cover");
            var brawler = world.CreateFighter(new Vector3(6f, 0f, -1.5f), cooldown: 0.2f, registry: registry);   // 1.5 m away
            var brawlerHealth = brawler.GetComponent<Health>();
            yield return null;

            brawler.Issue(new AttackCommand(coveredHealth));
            yield return TestWorld.WaitUntil(() => coveredHealth.Current < coveredHealth.Max, 2f);
            yield return null;

            Assert.That(IsAttacking(covered, brawlerHealth), Is.True, "An attacker it can hit from cover is fought back");
            yield return TestWorld.WaitUntil(() => brawlerHealth.Current < brawlerHealth.Max, 2f);
            Assert.That(brawlerHealth.Current, Is.LessThan(brawlerHealth.Max));
            Assert.That(covered.Cover.Status, Is.EqualTo(CoverStatus.Occupied), "...without leaving the point");
        }

        [UnityTest]
        public IEnumerator UnitStandingOnAPointByChance_ChargesTheShooterAsBefore()
        {
            yield return null;
            var (point, registry) = LooseCover(new Vector3(6f, 0f, 0f));
            var standing = world.CreateFighter(new Vector3(6f, 0f, -3f), registry: registry);
            var standingHealth = standing.GetComponent<Health>();
            Assert.That(standing.Issue(new MoveCommand(point.Position)), Is.True);
            yield return TestWorld.WaitUntil(() => standing.Cover.Status == CoverStatus.Occupied, 6f);
            Assert.That(standing.Cover.OccupiedByOrder, Is.False, "Precondition: incidental cover");
            var shooter = world.CreateFighter(new Vector3(6f, 0f, 6f), cooldown: 0.2f, role: CombatRole.Ranged, range: 8f, registry: registry);
            var shooterHealth = shooter.GetComponent<Health>();
            shooter.GetComponent<UnitAttacker>().HitRoll = () => 0f;
            yield return new WaitForFixedUpdate();

            shooter.Issue(new AttackCommand(standingHealth));
            yield return TestWorld.WaitUntil(() => standingHealth.Current < standingHealth.Max, 2f);
            yield return null;

            Assert.That(IsAttacking(standing, shooterHealth), Is.True, "Cover a unit merely stands on does not change retaliation");
            yield return TestWorld.WaitUntil(() => standing.Cover.Status == CoverStatus.None, 3f);
            Assert.That(standing.Cover.Status, Is.EqualTo(CoverStatus.None), "It charged out of the point");
        }
    }
}
