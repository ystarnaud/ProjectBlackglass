using System;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class HealthSetMaxTests
    {
        GameObject host;
        Health health;

        [SetUp]
        public void SetUp()
        {
            host = new GameObject("HealthSetMax");
            health = host.AddComponent<Health>();
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(host);

        [Test]
        public void RaisingTheMax_KeepsTheDamageTaken_SoCurrentRisesByTheDifference()
        {
            health.TakeDamage(30);

            health.SetMax(125);

            Assert.That(health.Max, Is.EqualTo(125));
            Assert.That(health.Current, Is.EqualTo(95));
        }

        [Test]
        public void LoweringTheMax_KeepsTheDamageTaken_WhileTheUnitStaysAlive()
        {
            health.TakeDamage(30);

            health.SetMax(80);

            Assert.That(health.Max, Is.EqualTo(80));
            Assert.That(health.Current, Is.EqualTo(50));
        }

        [Test]
        public void LoweringTheMaxBelowTheDamageTaken_LeavesALivingUnitAtOneHitPoint_NeverDeadNeverZero()
        {
            health.TakeDamage(90);
            var deaths = 0;
            health.Died += () => deaths++;

            health.SetMax(50);

            Assert.That(health.IsAlive, Is.True);
            Assert.That(health.Current, Is.EqualTo(1));
            Assert.That(deaths, Is.EqualTo(0));
        }

        [Test]
        public void ADeadUnit_StaysDead_AtZeroHitPoints()
        {
            health.TakeDamage(100);

            health.SetMax(500);

            Assert.That(health.IsAlive, Is.False);
            Assert.That(health.Current, Is.EqualTo(0));
            Assert.That(health.Heal(10), Is.EqualTo(0));
        }

        [Test]
        public void AFreshUnit_SetMax_IsFullAtTheNewMax()
        {
            health.SetMax(155);
            Assert.That(health.Max, Is.EqualTo(155));
            Assert.That(health.Current, Is.EqualTo(155));
        }

        [Test]
        public void SetMax_BelowOne_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => health.SetMax(0));
            Assert.That(health.Max, Is.EqualTo(100));
        }
    }
}
