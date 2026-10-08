#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class OperativeSpawnPlayModeTests
    {
        MissionRig rig;
        OperativeKit kit;
        SquadRoster roster;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
            kit?.Dispose();
            kit = null;
        }

        IEnumerator Start(int seed = 12345)
        {
            rig = new MissionRig();
            kit = OperativeKit.Build();
            roster = rig.AddRoster(kit.Definitions, kit.Track);
            yield return rig.Generate(seed);
            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready), string.Join("\n", rig.Director.Report.Failures));
        }

        [UnityTest]
        public IEnumerator Spawn_MakesOneUnitPerOperative_BoundToItsPersistentState()
        {
            yield return Start();
            var friendlies = rig.Director.Friendlies;

            Assert.That(friendlies, Has.Count.EqualTo(3));
            for (var i = 0; i < 3; i++)
            {
                var identity = friendlies[i].GetComponent<UnitIdentity>();
                Assert.That(identity, Is.Not.Null, friendlies[i].name);
                Assert.That(identity.OperativeId, Is.EqualTo(kit.Definitions[i].Id));
                Assert.That(identity.State, Is.SameAs(roster.Members[i].State));
                Assert.That(identity.Definition, Is.SameAs(kit.Definitions[i]));
                Assert.That(identity.DisplayName, Is.EqualTo(kit.Definitions[i].DisplayName));
                Assert.That(friendlies[i].name, Is.EqualTo($"FriendlyUnit_{i + 1}"), "scene object names are unchanged");
            }
            Assert.That(rig.Active.Unit, Is.SameAs(friendlies[0]));
        }

        [UnityTest]
        public IEnumerator Spawn_AppliesEachOperativesEffectiveConfiguration_ToTheRealComponents()
        {
            yield return Start();

            for (var i = 0; i < 3; i++)
            {
                var unit = rig.Director.Friendlies[i];
                var effective = roster.Evaluate(roster.Members[i]);
                var health = unit.GetComponent<Health>();
                var attacker = unit.GetComponent<UnitAttacker>();

                Assert.That(health.Max, Is.EqualTo(effective.MaxHealth), unit.name);
                Assert.That(health.Current, Is.EqualTo(effective.MaxHealth), unit.name);
                Assert.That(unit.GetComponent<UnitMover>().Speed, Is.EqualTo(effective.MoveSpeed).Within(0.001f), unit.name);
                Assert.That(unit.GetComponent<UnityEngine.AI.NavMeshAgent>().speed, Is.EqualTo(effective.MoveSpeed).Within(0.001f), unit.name);
                Assert.That(attacker.Damage, Is.EqualTo(effective.AttackDamage), unit.name + " (Awake must not reset the archetype damage)");
                Assert.That(attacker.Range, Is.EqualTo(effective.AttackRange), unit.name);
                Assert.That(attacker.Cooldown, Is.EqualTo(effective.AttackInterval), unit.name);
                Assert.That(attacker.Role, Is.EqualTo(effective.AttackRole), unit.name);
                Assert.That(unit.GetComponent<UnitIdentity>().Effective.MaxHealth, Is.EqualTo(effective.MaxHealth));
            }
        }

        [UnityTest]
        public IEnumerator TheThreeOperatives_PlayDifferently()
        {
            yield return Start();
            var units = rig.Director.Friendlies;

            Assert.That(units.Select(u => u.GetComponent<Health>().Max), Is.EqualTo(new[] { 130, 80, 100 }));
            Assert.That(units.Select(u => u.GetComponent<UnitMover>().Speed).ToArray(),
                Is.EqualTo(new[] { 5f, 7f, 5.5f }).Within(0.001f), "Recon's role adds 0.5 m/s to 6.5");
            Assert.That(units.Select(u => u.GetComponent<UnitAttacker>().Range).ToArray(), Is.EqualTo(new[] { 8f, 16f, 8f }));
            Assert.That(units.Select(u => u.GetComponent<UnitAbilities>().Count), Is.EqualTo(new[] { 2, 1, 2 }));
            Assert.That(units[2].GetComponent<UnitAbilities>().Definition(0).DisplayName, Is.EqualTo("Mend"));
            Assert.That(units[2].GetComponent<UnitAbilities>().PowerMultiplier, Is.EqualTo(1.15f).Within(0.001f), "Support's role bonus");
            Assert.That(units[0].GetComponent<UnitAbilities>().PowerMultiplier, Is.EqualTo(1f));
        }

        [UnityTest]
        public IEnumerator HostilesAreNotOperatives()
        {
            yield return Start();
            foreach (var hostile in rig.Director.Hostiles)
                Assert.That(hostile.TryGetComponent<UnitIdentity>(out _), Is.False, hostile.name);
        }

        [UnityTest]
        public IEnumerator ALivePick_ChangesTheRunningUnit_KeepingDamageTaken()
        {
            yield return Start();
            var unit = rig.Director.Friendlies[0];
            var health = unit.GetComponent<Health>();
            var attacker = unit.GetComponent<UnitAttacker>();
            health.TakeDamage(30);
            var damageBefore = attacker.Damage;

            roster.AwardExperience("test-alpha", 100);
            Assert.That(roster.TryPickChoice("test-alpha", "survivability"), Is.True);

            Assert.That(health.Max, Is.EqualTo(155));
            Assert.That(health.Current, Is.EqualTo(125), "the 30 damage taken stays taken");

            roster.AwardExperience("test-alpha", 150);
            Assert.That(roster.TryPickChoice("test-alpha", "combat"), Is.True);
            Assert.That(attacker.Damage, Is.GreaterThan(damageBefore));
            Assert.That(attacker.Damage, Is.EqualTo(roster.Evaluate(roster.Members[0]).AttackDamage));
        }

        [UnityTest]
        public IEnumerator ADeadUnit_IgnoresLaterRosterChanges_WithoutErrors()
        {
            yield return Start();
            var unit = rig.Director.Friendlies[1];
            var identity = unit.GetComponent<UnitIdentity>();
            var maxBefore = identity.Effective.MaxHealth;
            var health = unit.GetComponent<Health>();
            health.TakeDamage(health.Max);
            Assert.That(unit.gameObject.activeSelf, Is.False, "a dead unit is deactivated");

            Assert.DoesNotThrow(() =>
            {
                roster.AwardExperience("test-bravo", 100);
                roster.TryPickChoice("test-bravo", "survivability");
            });

            Assert.That(roster.Find("test-bravo").State.Experience, Is.EqualTo(100), "the persistent record is updated");
            Assert.That(identity.Effective.MaxHealth, Is.EqualTo(maxBefore), "the corpse is not re-configured");
            Assert.That(identity.OperativeId, Is.EqualTo("test-bravo"), "identity survives death");
        }

        [UnityTest]
        public IEnumerator WithoutARoster_TheLegacySlotsStillSpawnTheSquad()
        {
            rig = new MissionRig();
            yield return rig.Generate(12345);
            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready));
            Assert.That(rig.Director.Friendlies, Has.Count.EqualTo(3));
            foreach (var unit in rig.Director.Friendlies)
                Assert.That(unit.TryGetComponent<UnitIdentity>(out _), Is.False);
            Assert.That(rig.Director.Friendlies[1].GetComponent<UnitAttacker>().Archetype.DisplayName, Is.EqualTo("Marksman"));
        }

        [UnityTest]
        public IEnumerator ARosterOfTwo_ProducesATwoMemberSquad()
        {
            rig = new MissionRig();
            kit = OperativeKit.Build();
            roster = rig.AddRoster(kit.Definitions.Take(2).ToArray(), kit.Track);
            yield return rig.Generate(12345);

            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready), string.Join("\n", rig.Director.Report.Failures));
            Assert.That(rig.Director.Friendlies, Has.Count.EqualTo(2));
            Assert.That(rig.Director.Friendlies.Select(u => u.GetComponent<UnitIdentity>().OperativeId), Is.EqualTo(new[] { "test-alpha", "test-bravo" }));
        }
    }
}
#endif
