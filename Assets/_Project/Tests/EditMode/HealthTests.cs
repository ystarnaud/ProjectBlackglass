using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class HealthTests
    {
        GameObject host;
        Health health;
        List<int> damaged;
        int deaths;

        [SetUp]
        public void SetUp()
        {
            host = new GameObject("HealthTest");
            health = host.AddComponent<Health>();
            damaged = new List<int>();
            deaths = 0;
            health.Damaged += damaged.Add;
            health.Died += () => deaths++;
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(host);

        [Test]
        public void NewHealth_IsFullAndAlive()
        {
            Assert.That(health.Max, Is.EqualTo(100));
            Assert.That(health.Current, Is.EqualTo(100));
            Assert.That(health.IsAlive, Is.True);
        }

        [Test]
        public void TakeDamage_ReducesCurrentAndReportsAmount()
        {
            health.TakeDamage(25);
            Assert.That(health.Current, Is.EqualTo(75));
            Assert.That(damaged, Is.EqualTo(new[] { 25 }));
            Assert.That(deaths, Is.EqualTo(0));
        }

        [Test]
        public void TakeDamage_PastZero_RaisesDiedOnce()
        {
            health.TakeDamage(150);
            health.TakeDamage(10);
            Assert.That(health.Current, Is.EqualTo(0));
            Assert.That(health.IsAlive, Is.False);
            Assert.That(deaths, Is.EqualTo(1));
            Assert.That(damaged, Is.EqualTo(new[] { 150 }));
        }

        [Test]
        public void TakeDamage_HugeValue_DoesNotOverflow()
        {
            health.TakeDamage(60);
            health.TakeDamage(int.MaxValue);
            Assert.That(health.Current, Is.EqualTo(0));
            Assert.That(deaths, Is.EqualTo(1));
        }

        [Test]
        public void TakeDamage_Zero_ChangesNothing()
        {
            health.TakeDamage(0);
            Assert.That(health.Current, Is.EqualTo(100));
            Assert.That(damaged, Is.Empty);
        }

        [Test]
        public void TakeDamage_Negative_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => health.TakeDamage(-5));
            Assert.That(health.Current, Is.EqualTo(100));
        }

        [Test]
        public void Damaged_HandlerThatKillsTheTarget_RaisesDiedOnce()
        {
            var reentered = false;
            health.Damaged += _ =>
            {
                if (reentered)
                    return;
                reentered = true;
                health.TakeDamage(1000);
            };

            health.TakeDamage(10);

            Assert.That(deaths, Is.EqualTo(1));
            Assert.That(health.Current, Is.EqualTo(0));
            Assert.That(health.IsAlive, Is.False);
        }

        [Test]
        public void Death_DeactivatesTheGameObject()
        {
            health.TakeDamage(100);
            Assert.That(host.activeSelf, Is.False);
        }

        Health CreateAttacker(out GameObject host)
        {
            host = new GameObject("Attacker");
            return host.AddComponent<Health>();
        }

        [Test]
        public void TakeDamage_WithAnAttacker_RaisesAttackedBy_AfterDamaged()
        {
            var attacker = CreateAttacker(out var attackerHost);
            var order = new List<string>();
            health.Damaged += _ => order.Add("damaged");
            health.AttackedBy += by => order.Add(by == attacker ? "attackedBy" : "wrongAttacker");

            health.TakeDamage(10, attacker);

            Assert.That(order, Is.EqualTo(new[] { "damaged", "attackedBy" }));
            Assert.That(health.Current, Is.EqualTo(90));
            Object.DestroyImmediate(attackerHost);
        }

        [Test]
        public void TakeDamage_WithoutAnAttacker_DoesNotRaiseAttackedBy()
        {
            var attacked = 0;
            health.AttackedBy += _ => attacked++;
            health.TakeDamage(10);
            Assert.That(attacked, Is.EqualTo(0));
            Assert.That(damaged, Is.EqualTo(new[] { 10 }));
        }

        [Test]
        public void TakeDamage_ZeroOrOnADeadTarget_DoesNotRaiseAttackedBy()
        {
            var attacker = CreateAttacker(out var attackerHost);
            var attacked = 0;
            health.AttackedBy += _ => attacked++;

            health.TakeDamage(0, attacker);
            Assert.That(attacked, Is.EqualTo(0), "Zero damage is not a hit");

            health.TakeDamage(health.Max, attacker);
            Assert.That(attacked, Is.EqualTo(1), "The killing blow is a hit");
            health.TakeDamage(5, attacker);
            Assert.That(attacked, Is.EqualTo(1), "A dead target is not hit again");
            Object.DestroyImmediate(attackerHost);
        }

        [Test]
        public void TakeDamage_KillingBlow_RaisesAttackedByBeforeDied()
        {
            var attacker = CreateAttacker(out var attackerHost);
            var order = new List<string>();
            health.AttackedBy += _ => order.Add("attackedBy");
            health.Died += () => order.Add("died");

            health.TakeDamage(health.Max, attacker);

            Assert.That(order, Is.EqualTo(new[] { "attackedBy", "died" }));
            Object.DestroyImmediate(attackerHost);
        }

        [Test]
        public void TakeDamage_KillingBlow_IsAlreadyDeadInsideAttackedBy()
        {
            var attacker = CreateAttacker(out var attackerHost);
            bool? aliveDuringCallback = null;
            health.AttackedBy += _ => aliveDuringCallback = health.IsAlive;

            health.TakeDamage(health.Max, attacker);

            Assert.That(aliveDuringCallback, Is.False, "Handlers of the killing blow must see a dead target");
            Object.DestroyImmediate(attackerHost);
        }

        [Test]
        public void Initialize_SetsTheMaximum_AndRestoresFullHealth()
        {
            health.TakeDamage(30);
            health.Initialize(40);
            Assert.That(health.Max, Is.EqualTo(40));
            Assert.That(health.Current, Is.EqualTo(40));
            Assert.That(health.IsAlive, Is.True);
        }
    }
}
