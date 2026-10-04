using System;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class EncounterTests
    {
        GameObject systems;
        Encounter encounter;
        GameObject[] hosts;

        [SetUp]
        public void SetUp()
        {
            systems = new GameObject("Systems");
            encounter = systems.AddComponent<Encounter>();
            hosts = Array.Empty<GameObject>();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(systems);
            foreach (var host in hosts)
            {
                if (host != null)
                    Object.DestroyImmediate(host);
            }
        }

        Health[] CreateUnits(params string[] names)
        {
            var units = new Health[names.Length];
            var created = new GameObject[names.Length];
            for (var i = 0; i < names.Length; i++)
            {
                created[i] = new GameObject(names[i]);
                units[i] = created[i].AddComponent<Health>();
            }
            var all = new GameObject[hosts.Length + created.Length];
            hosts.CopyTo(all, 0);
            created.CopyTo(all, hosts.Length);
            hosts = all;
            return units;
        }

        static void Kill(Health unit) => unit.TakeDamage(unit.Max);

        [TestCase(3, 3, 3, 3, EncounterOutcome.Ongoing)]
        [TestCase(3, 1, 3, 1, EncounterOutcome.Ongoing)]
        [TestCase(3, 2, 3, 0, EncounterOutcome.Victory)]
        [TestCase(3, 0, 3, 2, EncounterOutcome.Defeat)]
        [TestCase(3, 0, 3, 0, EncounterOutcome.Defeat)]
        [TestCase(0, 0, 0, 0, EncounterOutcome.Ongoing)]
        [TestCase(3, 3, 0, 0, EncounterOutcome.Ongoing)]
        [TestCase(0, 0, 3, 3, EncounterOutcome.Ongoing)]
        public void Resolve_FollowsTheRules(int friendlies, int livingFriendlies, int hostiles, int livingHostiles, EncounterOutcome expected)
        {
            Assert.That(Encounter.Resolve(friendlies, livingFriendlies, hostiles, livingHostiles), Is.EqualTo(expected));
        }

        [Test]
        public void Resolve_RejectsImpossibleCounts()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Encounter.Resolve(-1, 0, 3, 3));
            Assert.Throws<ArgumentOutOfRangeException>(() => Encounter.Resolve(3, 4, 3, 3));
            Assert.Throws<ArgumentOutOfRangeException>(() => Encounter.Resolve(3, 3, 3, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => Encounter.Resolve(3, 3, 2, 3));
        }

        [Test]
        public void Outcome_ReadsTheLists()
        {
            var friendlies = CreateUnits("F1", "F2");
            var hostiles = CreateUnits("H1", "H2");
            encounter.Initialize(friendlies, hostiles);
            Assert.That(encounter.Friendlies, Is.EqualTo(friendlies));
            Assert.That(encounter.Hostiles, Is.EqualTo(hostiles));
            Assert.That(encounter.Outcome, Is.EqualTo(EncounterOutcome.Ongoing));

            Kill(hostiles[0]);
            Assert.That(encounter.LivingHostiles, Is.EqualTo(1));
            Assert.That(encounter.Outcome, Is.EqualTo(EncounterOutcome.Ongoing));

            Kill(hostiles[1]);
            Assert.That(encounter.LivingHostiles, Is.EqualTo(0));
            Assert.That(encounter.Outcome, Is.EqualTo(EncounterOutcome.Victory));

            Kill(friendlies[0]);
            Kill(friendlies[1]);
            Assert.That(encounter.Outcome, Is.EqualTo(EncounterOutcome.Defeat), "Everyone dead is a defeat");
        }

        [Test]
        public void LivingCounts_IgnoreNullAndDestroyedEntries()
        {
            var friendlies = CreateUnits("F1", "F2", "F3");
            var hostiles = CreateUnits("H1");
            encounter.Initialize(new[] { friendlies[0], null, friendlies[1], friendlies[2] }, hostiles);
            Assert.That(encounter.Friendlies, Has.Count.EqualTo(4));
            Assert.That(encounter.LivingFriendlies, Is.EqualTo(3));

            Object.DestroyImmediate(friendlies[2].gameObject);
            Assert.That(encounter.LivingFriendlies, Is.EqualTo(2), "A destroyed unit counts as dead");
            Assert.That(encounter.Outcome, Is.EqualTo(EncounterOutcome.Ongoing));
        }

        [Test]
        public void EmptyEncounter_IsOngoing()
        {
            Assert.That(encounter.Friendlies, Is.Empty);
            Assert.That(encounter.Hostiles, Is.Empty);
            Assert.That(encounter.LivingFriendlies, Is.EqualTo(0));
            Assert.That(encounter.Outcome, Is.EqualTo(EncounterOutcome.Ongoing));
        }

        [Test]
        public void Initialize_Null_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => encounter.Initialize(null, Array.Empty<Health>()));
            Assert.Throws<ArgumentNullException>(() => encounter.Initialize(Array.Empty<Health>(), null));
        }
    }
}
