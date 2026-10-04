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
        public void Death_DeactivatesTheGameObject()
        {
            health.TakeDamage(100);
            Assert.That(host.activeSelf, Is.False);
        }
    }
}
