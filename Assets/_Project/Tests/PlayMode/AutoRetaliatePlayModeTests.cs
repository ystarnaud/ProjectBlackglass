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
            yield return new WaitForSeconds(0.6f);   // the aggressor chases and hits again (0.2 s cooldown)

            Assert.That(victim.CurrentCommand, Is.SameAs(retreat), "Later hits must not replace the retreat order");
            Assert.That(TestWorld.HorizontalDistance(victim.transform.position, start), Is.GreaterThan(1.5f), "The unit did not retreat");
        }

        [UnityTest]
        public IEnumerator UnitWithAHeldMoveIntent_DoesNotRetaliate()
        {
            yield return null;
            victim.SetMoveIntent(Vector3.forward * 0.01f);   // held, barely moving, so it stays in range
            aggressor.Issue(new AttackCommand(victimHealth));
            yield return WaitForFirstHit();
            yield return null;

            Assert.That(victimHealth.Current, Is.LessThan(victimHealth.Max), "Precondition: the victim was hit");
            Assert.That(victim.CurrentCommand, Is.Null, "Held keys win: no retaliation order");
        }

        [UnityTest]
        public IEnumerator KillingBlow_DoesNotMakeTheVictimRetaliate()
        {
            yield return null;
            var fragile = world.CreateFighter(new Vector3(1.5f, 0f, 1.5f), maxHealth: 10);
            var fragileHealth = fragile.GetComponent<Health>();
            aggressor.Issue(new AttackCommand(fragileHealth));
            yield return TestWorld.WaitUntil(() => !fragileHealth.IsAlive, 2f);
            yield return null;

            Assert.That(fragileHealth.IsAlive, Is.False, "Precondition: the first hit killed it");
            Assert.That(fragile.CurrentCommand, Is.Null, "A corpse must not carry a retaliation order");
            Assert.That(aggressorHealth.Current, Is.EqualTo(aggressorHealth.Max), "The dead unit must not have struck back");
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
    }
}
