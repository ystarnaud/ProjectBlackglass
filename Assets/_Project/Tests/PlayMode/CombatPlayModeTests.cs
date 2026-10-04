using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CombatPlayModeTests
    {
        TestWorld world;

        [SetUp]
        public void SetUp() => world = new TestWorld();

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        [UnityTest]
        public IEnumerator Attack_RaisesAttackedWithTheTarget_AndTellsTheTargetWhoHitIt()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(0f, 0f, 0f));
            var ownHealth = unit.gameObject.AddComponent<Health>();
            var attacker = unit.GetComponent<UnitAttacker>();
            var dummy = world.CreateDummy(new Vector3(0f, 0f, 1.5f));
            var hits = new List<Health>();
            var hitBy = new List<Health>();
            attacker.Attacked += hits.Add;
            dummy.AttackedBy += hitBy.Add;
            yield return null;

            unit.Issue(new AttackCommand(dummy));
            yield return TestWorld.WaitUntil(() => dummy.Current < dummy.Max, 2f);

            Assert.That(hits, Is.EqualTo(new[] { dummy }), "Attacked must fire once per hit with the target");
            Assert.That(hitBy, Is.EqualTo(new[] { ownHealth }), "The target must learn which Health hit it");
        }

        [UnityTest]
        public IEnumerator CooldownRemaining_IsZeroWhenReady_AndCountsDownAfterAHit()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(0f, 0f, 0f));
            var attacker = unit.GetComponent<UnitAttacker>();
            var dummy = world.CreateDummy(new Vector3(0f, 0f, 1.5f));
            yield return null;
            Assert.That(attacker.CooldownRemaining, Is.EqualTo(0f));

            unit.Issue(new AttackCommand(dummy));
            yield return TestWorld.WaitUntil(() => dummy.Current < dummy.Max, 2f);
            var justAfterHit = attacker.CooldownRemaining;
            // (t + 1f) - t is 1 up to float rounding, so allow a tolerance.
            Assert.That(justAfterHit, Is.EqualTo(1f).Within(0.01f), "A fresh hit must leave about a full cooldown");

            yield return new WaitForSeconds(0.4f);
            Assert.That(attacker.CooldownRemaining, Is.LessThan(justAfterHit - 0.3f));

            unit.Issue(new StopCommand());
            yield return new WaitForSeconds(0.7f);
            Assert.That(attacker.CooldownRemaining, Is.EqualTo(0f));
        }

        [UnityTest]
        public IEnumerator UnitKilled_DropsItsOrders_StopsMoving_AndRefusesNewOnes()
        {
            world.CreateEnvironment();
            var unit = world.CreateFighter(new Vector3(-8f, 0f, 0f));
            var dummy = world.CreateDummy(new Vector3(8f, 0f, 0f));
            yield return null;
            unit.Issue(new MoveCommand(new Vector3(8f, 0f, 4f)));
            unit.Issue(new AttackCommand(dummy), IssueMode.Append);
            yield return new WaitForSeconds(0.3f);
            Assert.That(unit.PendingCommands, Has.Count.EqualTo(1), "Precondition: a pending order exists");

            var health = unit.GetComponent<Health>();
            health.TakeDamage(health.Max);
            yield return null;

            Assert.That(unit.IsAlive, Is.False);
            Assert.That(unit.CurrentCommand, Is.Null, "Death must clear the current order");
            Assert.That(unit.PendingCommands, Is.Empty, "Death must clear pending orders");
            Assert.That(unit.gameObject.activeSelf, Is.False);
            Assert.That(unit.Issue(new MoveCommand(Vector3.zero)), Is.False);
            Assert.That(unit.CurrentCommand, Is.Null);
        }
    }
}
