using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class UnitAttackerTests
    {
        GameObject host;
        GameObject targetHost;
        UnitAttacker attacker;
        Health target;

        [SetUp]
        public void SetUp()
        {
            host = new GameObject("Attacker");
            attacker = host.AddComponent<UnitAttacker>();
            targetHost = new GameObject("Target");
            target = targetHost.AddComponent<Health>();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(host);
            Object.DestroyImmediate(targetHost);
        }

        [Test]
        public void Defaults_AreMelee_WithoutASightRequirement()
        {
            Assert.That(attacker.Role, Is.EqualTo(CombatRole.Melee));
            Assert.That(attacker.NeedsLineOfSight, Is.False);
            Assert.That(attacker.Range, Is.EqualTo(2f));
        }

        [Test]
        public void Initialize_WithARangedRole_NeedsSight()
        {
            attacker.Initialize(8f, 15, 1f, CombatRole.Ranged);
            Assert.That(attacker.Role, Is.EqualTo(CombatRole.Ranged));
            Assert.That(attacker.NeedsLineOfSight, Is.True);
            Assert.That(attacker.Range, Is.EqualTo(8f));
            Assert.That(attacker.Damage, Is.EqualTo(15));
        }

        [Test]
        public void IsInRangeFrom_IsHorizontal_AndInclusive()
        {
            attacker.Initialize(8f, 15, 1f, CombatRole.Ranged);
            targetHost.transform.position = new Vector3(0f, 1f, 0f);
            Assert.That(attacker.IsInRangeFrom(new Vector3(8f, 1f, 0f), target), Is.True, "exactly at the range");
            Assert.That(attacker.IsInRangeFrom(new Vector3(8f, 30f, 0f), target), Is.True, "height is ignored");
            Assert.That(attacker.IsInRangeFrom(new Vector3(8.1f, 1f, 0f), target), Is.False);
            Assert.That(attacker.IsInRangeFrom(Vector3.zero, null), Is.False);
        }

        [Test]
        public void CanAttackFrom_Melee_IsRangeOnly()
        {
            // No colliders exist here, so a ranged check would also pass; melee must not even ask.
            targetHost.transform.position = new Vector3(0f, 1f, 0f);
            Assert.That(attacker.CanAttackFrom(new Vector3(1.5f, 1f, 0f), target), Is.True);
            Assert.That(attacker.CanAttackFrom(new Vector3(2.5f, 1f, 0f), target), Is.False);
            Assert.That(attacker.CanAttack(null), Is.False);
        }
    }
}
