using System;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class CombatArchetypeTests
    {
        CombatArchetype melee;
        CombatArchetype ranged;
        CombatArchetype marksman;
        GameObject host;
        UnitAttacker attacker;

        [SetUp]
        public void SetUp()
        {
            melee = CombatArchetype.Create("Melee", CombatRole.Melee, 2f, 25, 1f);
            ranged = CombatArchetype.Create("Ranged", CombatRole.Ranged, 8f, 15, 1f);
            marksman = CombatArchetype.Create("Marksman", CombatRole.Ranged, 16f, 40, 2.5f);
            host = new GameObject("Attacker");
            attacker = host.AddComponent<UnitAttacker>();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(melee);
            Object.DestroyImmediate(ranged);
            Object.DestroyImmediate(marksman);
            Object.DestroyImmediate(host);
        }

        [Test]
        public void Create_ExposesTheConfiguredValues()
        {
            Assert.That(marksman.DisplayName, Is.EqualTo("Marksman"));
            Assert.That(marksman.Role, Is.EqualTo(CombatRole.Ranged));
            Assert.That(marksman.Range, Is.EqualTo(16f));
            Assert.That(marksman.Damage, Is.EqualTo(40));
            Assert.That(marksman.AttackInterval, Is.EqualTo(2.5f));
        }

        [Test]
        public void ApplyArchetype_CopiesTheValuesOntoTheAttacker()
        {
            attacker.ApplyArchetype(marksman);

            Assert.That(attacker.Archetype, Is.SameAs(marksman));
            Assert.That(attacker.Role, Is.EqualTo(CombatRole.Ranged));
            Assert.That(attacker.NeedsLineOfSight, Is.True);
            Assert.That(attacker.Range, Is.EqualTo(16f));
            Assert.That(attacker.Damage, Is.EqualTo(40));
            Assert.That(attacker.Cooldown, Is.EqualTo(2.5f));
        }

        [Test]
        public void TheThreeArchetypes_FeelDifferent_InRangeDamageIntervalAndSight()
        {
            Assert.That(melee.Range, Is.LessThan(ranged.Range));
            Assert.That(ranged.Range, Is.LessThan(marksman.Range));
            Assert.That(marksman.Damage, Is.GreaterThan(ranged.Damage));
            Assert.That(marksman.AttackInterval, Is.GreaterThan(ranged.AttackInterval));
            Assert.That(melee.Role, Is.EqualTo(CombatRole.Melee), "Melee needs no line of sight");
            Assert.That(ranged.Role, Is.EqualTo(CombatRole.Ranged));
            Assert.That(marksman.Role, Is.EqualTo(CombatRole.Ranged));

            attacker.ApplyArchetype(melee);
            Assert.That(attacker.NeedsLineOfSight, Is.False);
            attacker.ApplyArchetype(ranged);
            Assert.That(attacker.NeedsLineOfSight, Is.True);
        }

        [Test]
        public void ApplyArchetype_Null_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => attacker.ApplyArchetype(null));
        }

        [Test]
        public void WithoutAnArchetype_InitializeStillConfiguresTheAttacker()
        {
            attacker.Initialize(8f, 15, 1f, CombatRole.Ranged);

            Assert.That(attacker.Archetype, Is.Null);
            Assert.That(attacker.Range, Is.EqualTo(8f));
            Assert.That(attacker.Role, Is.EqualTo(CombatRole.Ranged));
        }

        [Test]
        public void Create_RejectsAnEmptyShape()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => CombatArchetype.Create("Nothing", CombatRole.Melee, 0f, 1, 1f));
            Assert.Throws<ArgumentOutOfRangeException>(() => CombatArchetype.Create("Nothing", CombatRole.Melee, 2f, -1, 1f));
            Assert.Throws<ArgumentOutOfRangeException>(() => CombatArchetype.Create("Nothing", CombatRole.Melee, 2f, 1, -1f));
        }
    }
}
