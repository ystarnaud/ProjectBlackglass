using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class UnitEffectiveSettersTests
    {
        GameObject host;

        [TearDown]
        public void TearDown()
        {
            if (host != null)
                Object.DestroyImmediate(host);
        }

        [Test]
        public void UnitMover_SetSpeed_ChangesTheFieldAndTheAgent()
        {
            host = new GameObject("Mover");
            var mover = host.AddComponent<UnitMover>();

            mover.SetSpeed(6.5f);

            Assert.That(mover.Speed, Is.EqualTo(6.5f));
            Assert.That(host.GetComponent<NavMeshAgent>().speed, Is.EqualTo(6.5f));
        }

        [Test]
        public void UnitMover_SetSpeed_NegativeBecomesZero()
        {
            host = new GameObject("Mover");
            var mover = host.AddComponent<UnitMover>();
            mover.SetSpeed(-3f);
            Assert.That(mover.Speed, Is.EqualTo(0f));
        }

        [Test]
        public void UnitAttacker_ApplyEffective_OverridesTheArchetypeNumbers_ButKeepsTheArchetype()
        {
            host = new GameObject("Attacker");
            var attacker = host.AddComponent<UnitAttacker>();
            var marksman = CombatArchetype.Create("Marksman", CombatRole.Ranged, 16f, 40, 2.5f);
            try
            {
                attacker.ApplyArchetype(marksman);

                attacker.ApplyEffective(CombatRole.Ranged, 16f, 46, 2.5f);

                Assert.That(attacker.Archetype, Is.SameAs(marksman));
                Assert.That(attacker.Damage, Is.EqualTo(46));
                Assert.That(attacker.Range, Is.EqualTo(16f));
                Assert.That(attacker.Cooldown, Is.EqualTo(2.5f));
                Assert.That(attacker.Role, Is.EqualTo(CombatRole.Ranged));
            }
            finally
            {
                Object.DestroyImmediate(marksman);
            }
        }

        [Test]
        public void UnitAttacker_ApplyArchetype_AfterEffective_PutsTheArchetypeBack()
        {
            host = new GameObject("Attacker");
            var attacker = host.AddComponent<UnitAttacker>();
            var melee = CombatArchetype.Create("Melee", CombatRole.Melee, 2f, 25, 1f);
            try
            {
                attacker.ApplyEffective(CombatRole.Ranged, 9f, 99, 3f);
                attacker.ApplyArchetype(melee);

                Assert.That(attacker.Damage, Is.EqualTo(25));
                Assert.That(attacker.Range, Is.EqualTo(2f));
                Assert.That(attacker.Role, Is.EqualTo(CombatRole.Melee));
            }
            finally
            {
                Object.DestroyImmediate(melee);
            }
        }
    }
}
