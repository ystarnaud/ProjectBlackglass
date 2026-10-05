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

        [Test]
        public void IsTargetInCover_IsFalseForMelee_AndForATargetWithoutUnitCover()
        {
            targetHost.transform.position = new Vector3(0f, 1f, 1f);
            Assert.That(attacker.IsTargetInCover(target, out var chance), Is.False, "Melee never asks about cover");
            Assert.That(chance, Is.EqualTo(1f));
            attacker.Initialize(8f, 15, 1f, CombatRole.Ranged);
            Assert.That(attacker.IsTargetInCover(target), Is.False, "A target without a UnitCover is exposed");
            Assert.That(attacker.IsTargetInCover(null), Is.False);
        }

        [Test]
        public void IsTargetInCover_IsFalseWhileTheTargetOnlyReservesAPoint()
        {
            attacker.Initialize(8f, 15, 1f, CombatRole.Ranged);
            targetHost.transform.position = new Vector3(0f, 1f, 4f);
            var point = new CoverLocation("Cover", new Vector3(0f, 0f, 4f), Vector3.forward, null, 0.25f);
            var cover = targetHost.AddComponent<UnitCover>();
            Assert.That(cover.TryReserve(point), Is.True);

            Assert.That(attacker.IsTargetInCover(target, out var chance), Is.False, "Reserved is not occupied");
            Assert.That(chance, Is.EqualTo(1f));
        }

        [Test]
        public void TryAttack_CountsShotsAndHits_AndAnExposedTargetAlwaysTakesDamage()
        {
            targetHost.transform.position = new Vector3(0f, 1f, 1f);
            attacker.HitRoll = () => 0.999f;   // would miss a covered target; an exposed one is hit regardless
            Assert.That(attacker.ShotsFired, Is.EqualTo(0));
            Assert.That(attacker.TryAttack(target), Is.True);
            Assert.That(attacker.ShotsFired, Is.EqualTo(1));
            Assert.That(attacker.Hits, Is.EqualTo(1));
            Assert.That(target.Current, Is.EqualTo(target.Max - attacker.Damage));
            Assert.That(attacker.TryAttack(target), Is.False, "Cooling down: no shot");
            Assert.That(attacker.ShotsFired, Is.EqualTo(1));
        }
    }
}
