using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class UnitAbilitiesCoverPlayModeTests
    {
        TestWorld world;
        CoverLocation point;
        CoverRegistry registry;
        Encounter encounter;
        AbilityDefinition aimed;
        AbilityDefinition blast;
        CommandableUnit shooter;
        UnitAbilities abilities;
        CommandableUnit defender;
        Health defenderHealth;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            // The CoverCombatPlayModeTests geometry: a 0.9 m wall from z -0.25 to 0.25; the point 0.75 m south of it. A
            // shooter north of the wall sees over it while its eye-to-feet ray crosses it.
            var environment = world.CreateEnvironment((new Vector3(0f, 0.45f, 0f), new Vector3(4f, 0.9f, 0.5f)));
            point = world.CreateCoverPoint(new Vector3(0f, 0f, -1f), Vector3.forward, TestWorld.ObstacleCollider(environment));
            registry = world.CreateRegistry(point);
            encounter = world.CreateEncounter();
            aimed = world.CreateAimedShot();
            blast = world.CreateBlast();
            defender = world.CreateFighter(new Vector3(0f, 0f, -3f), registry: registry);
            defenderHealth = defender.GetComponent<Health>();
            shooter = world.CreateFighter(new Vector3(0f, 0f, 5f));
            abilities = world.AddAbilities(shooter, encounter, aimed, blast);
            encounter.Initialize(new[] { shooter.GetComponent<Health>() }, new[] { defenderHealth });
        }

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        IEnumerator PutDefenderInCover()
        {
            yield return null;
            Assert.That(defender.Issue(new MoveToCoverCommand(point)), Is.True);
            yield return TestWorld.WaitUntil(() => defender.Cover.Status == CoverStatus.Occupied, 10f);
            Assert.That(defender.Cover.Status, Is.EqualTo(CoverStatus.Occupied), "Precondition: the defender is in cover");
            yield return new WaitForFixedUpdate();
        }

        [UnityTest]
        public IEnumerator Check_ReportsACoveredTarget_AndItsHitChance()
        {
            yield return PutDefenderInCover();

            var check = abilities.Check(aimed, defenderHealth, null);

            Assert.That(check.Failure, Is.EqualTo(AbilityFailure.None));
            Assert.That(check.TargetInCover, Is.True);
            Assert.That(check.HitChance, Is.EqualTo(0.5f));
        }

        [UnityTest]
        public IEnumerator AimedShot_AtACoveredTarget_MissesOnAHighRoll_ButStillSpendsTheCooldown()
        {
            yield return PutDefenderInCover();
            shooter.GetComponent<UnitAttacker>().HitRoll = () => 0.99f;
            var missed = 0;
            abilities.Missed += (_, __) => missed++;

            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(aimed, defenderHealth)), Is.True);

            Assert.That(defenderHealth.Current, Is.EqualTo(defenderHealth.Max), "Cover turned the shot away");
            Assert.That(missed, Is.EqualTo(1));
            Assert.That(abilities.IsReady(0), Is.False, "A missed shot still spends the cooldown");
            Assert.That(abilities.UsedCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator AimedShot_AtACoveredTarget_LandsOnALowRoll()
        {
            yield return PutDefenderInCover();
            shooter.GetComponent<UnitAttacker>().HitRoll = () => 0f;

            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(aimed, defenderHealth)), Is.True);

            Assert.That(defenderHealth.Current, Is.EqualTo(defenderHealth.Max - 45));
        }

        [UnityTest]
        public IEnumerator AimedShot_AtAnExposedTarget_AlwaysLands_WhateverTheRoll()
        {
            yield return null;
            shooter.GetComponent<UnitAttacker>().HitRoll = () => 0.99f;

            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(aimed, defenderHealth)), Is.True);

            Assert.That(defenderHealth.Current, Is.EqualTo(defenderHealth.Max - 45));
        }

        [UnityTest]
        public IEnumerator Blast_IgnoresCover_AndAlwaysLands()
        {
            yield return PutDefenderInCover();
            shooter.GetComponent<UnitAttacker>().HitRoll = () => 0.99f;

            Assert.That(abilities.TryUse(AbilityCommand.AtGround(blast, point.Position)), Is.True);

            Assert.That(defenderHealth.Current, Is.EqualTo(defenderHealth.Max - 35), "The blast's cover rule is Ignored");
        }
    }
}
