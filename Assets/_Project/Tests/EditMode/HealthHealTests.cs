using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class HealthHealTests
    {
        GameObject host;
        Health health;
        List<int> healed;

        [SetUp]
        public void SetUp()
        {
            host = new GameObject("HealTest");
            health = host.AddComponent<Health>();
            healed = new List<int>();
            health.Healed += healed.Add;
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(host);

        [Test]
        public void Heal_RestoresDamage_AndReportsTheAmountActuallyRestored()
        {
            health.TakeDamage(60);

            var restored = health.Heal(25);

            Assert.That(restored, Is.EqualTo(25));
            Assert.That(health.Current, Is.EqualTo(65));
            Assert.That(healed, Is.EqualTo(new[] { 25 }));
        }

        [Test]
        public void Heal_NeverGoesAboveMax_AndReportsOnlyWhatWasMissing()
        {
            health.TakeDamage(10);

            var restored = health.Heal(40);

            Assert.That(restored, Is.EqualTo(10));
            Assert.That(health.Current, Is.EqualTo(health.Max));
            Assert.That(healed, Is.EqualTo(new[] { 10 }));
        }

        [Test]
        public void Heal_AtFullHealth_DoesNothingAndRaisesNothing()
        {
            Assert.That(health.Heal(30), Is.EqualTo(0));
            Assert.That(health.Current, Is.EqualTo(health.Max));
            Assert.That(healed, Is.Empty);
        }

        [Test]
        public void Heal_Zero_DoesNothing()
        {
            health.TakeDamage(20);
            Assert.That(health.Heal(0), Is.EqualTo(0));
            Assert.That(health.Current, Is.EqualTo(80));
            Assert.That(healed, Is.Empty);
        }

        [Test]
        public void Heal_TheDead_DoesNothing()
        {
            health.TakeDamage(health.Max);

            Assert.That(health.Heal(50), Is.EqualTo(0));
            Assert.That(health.IsAlive, Is.False);
            Assert.That(health.Current, Is.EqualTo(0));
            Assert.That(healed, Is.Empty);
        }

        [Test]
        public void Heal_Negative_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => health.Heal(-1));
        }
    }
}
