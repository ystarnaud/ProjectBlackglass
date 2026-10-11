using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class NoWeaponPlayModeTests
    {
        TestWorld world;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            world.CreateEnvironment();
        }

        [TearDown]
        public void TearDown() => world.Dispose();

        static void Disarm(CommandableUnit unit) =>
            unit.GetComponent<UnitAttacker>().ApplyEffective(CombatRole.Melee, 0f, 0, 0f, weaponPresent: false);

        [UnityTest]
        public IEnumerator AttackOrder_IsRefusedWithTheReason_AndTheUnitKeepsItsOrders()
        {
            var unit = world.CreateFighter(new Vector3(0f, 0f, 0f), role: CombatRole.Ranged, range: 8f);
            var dummy = world.CreateDummy(new Vector3(3f, 0f, 0f));
            yield return null;
            Assert.That(unit.Issue(new MoveCommand(new Vector3(-4f, 0f, 0f))), Is.True);
            Disarm(unit);

            var accepted = unit.Issue(new AttackCommand(dummy));

            Assert.That(accepted, Is.False);
            Assert.That(unit.LastRefusal, Is.EqualTo(CommandRefusal.NoWeapon));
            Assert.That(unit.CurrentCommand, Is.TypeOf<MoveCommand>(), "orders are unchanged");
        }

        [UnityTest]
        public IEnumerator QueuedAttack_IsRefused_Too()
        {
            var unit = world.CreateFighter(new Vector3(0f, 0f, 0f), role: CombatRole.Ranged, range: 8f);
            var dummy = world.CreateDummy(new Vector3(3f, 0f, 0f));
            yield return null;
            unit.Issue(new MoveCommand(new Vector3(-4f, 0f, 0f)));
            Disarm(unit);

            Assert.That(unit.Issue(new AttackCommand(dummy), IssueMode.Append), Is.False);
            Assert.That(unit.PendingCommands, Is.Empty);
        }

        [UnityTest]
        public IEnumerator TryAttack_NeverFires_WithoutAWeapon()
        {
            var unit = world.CreateFighter(new Vector3(0f, 0f, 0f), role: CombatRole.Melee, range: 2f);
            var dummy = world.CreateDummy(new Vector3(1f, 0f, 0f));
            yield return null;
            var attacker = unit.GetComponent<UnitAttacker>();
            Disarm(unit);

            Assert.That(attacker.HasWeapon, Is.False);
            Assert.That(attacker.TryAttack(dummy), Is.False);
            Assert.That(dummy.Current, Is.EqualTo(dummy.Max));
        }

        [UnityTest]
        public IEnumerator Rearming_RestoresTheAttack()
        {
            var unit = world.CreateFighter(new Vector3(0f, 0f, 0f), role: CombatRole.Melee, range: 2f);
            var dummy = world.CreateDummy(new Vector3(1f, 0f, 0f));
            yield return null;
            var attacker = unit.GetComponent<UnitAttacker>();
            Disarm(unit);

            attacker.ApplyEffective(CombatRole.Melee, 2f, 25, 1f);

            Assert.That(attacker.HasWeapon, Is.True);
            Assert.That(unit.Issue(new AttackCommand(dummy)), Is.True);
        }

        [UnityTest]
        public IEnumerator AutoRetaliate_FromAnUnarmedUnit_DoesNothingAndDoesNotThrow()
        {
            var unit = world.CreateFighter(new Vector3(0f, 0f, 0f), role: CombatRole.Melee, range: 2f);
            var enemy = world.CreateFighter(new Vector3(1f, 0f, 0f));
            yield return null;
            Disarm(unit);

            unit.GetComponent<Health>().TakeDamage(5, enemy.GetComponent<Health>());
            yield return null;

            Assert.That(unit.CurrentCommand, Is.Null);
        }
    }
}
