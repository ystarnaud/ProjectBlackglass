#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>What survives a mission and what does not: persistent XP and picks do; runtime units and health do not.</summary>
    public class OperativePersistencePlayModeTests
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

        MissionDirector Director => rig.Director;

        IEnumerator Start(MissionSettings settings = null, int seed = 12345)
        {
            rig = new MissionRig(settings);
            kit = OperativeKit.Build();
            roster = rig.AddRoster(kit.Definitions, kit.Track);
            yield return rig.Generate(seed);
            Assert.That(Director.State, Is.EqualTo(MissionState.Ready), string.Join("\n", Director.Report.Failures));
        }

        static void Kill(Component unit)
        {
            var health = unit.GetComponent<Health>();
            health.TakeDamage(health.Max);
        }

        void DisarmHostiles()
        {
            foreach (var hostile in Director.Hostiles)
                hostile.GetComponent<EnemyAI>().enabled = false;
        }

        IEnumerator CompleteTheMission()
        {
            DisarmHostiles();
            foreach (var hostile in Director.Hostiles)
                Kill(hostile);
            yield return null;
            yield return null;
            Assert.That(Director.Phase, Is.EqualTo(MissionPhase.ExtractionOpen));
            Assert.That(Director.Friendlies[0].GetComponent<NavMeshAgent>().Warp(Director.Current.ExtractionZone.position), Is.True);
            yield return null;
            yield return null;
            Assert.That(Director.Phase, Is.EqualTo(MissionPhase.Success));
        }

        [UnityTest]
        public IEnumerator XpAndPicks_SurviveRegeneration_AndTheNewUnitsAreConfiguredFromThem()
        {
            yield return Start();
            roster.AwardExperience("test-alpha", 250);
            Assert.That(roster.TryPickChoice("test-alpha", "survivability"), Is.True);
            Assert.That(roster.TryPickChoice("test-alpha", "combat"), Is.True);
            var stateBefore = roster.Find("test-alpha").State;
            var oldUnit = Director.Friendlies[0];

            yield return rig.Generate(777);

            Assert.That(oldUnit == null, Is.True, "the mission unit was destroyed with its mission");
            var newUnit = Director.Friendlies[0];
            var identity = newUnit.GetComponent<UnitIdentity>();
            Assert.That(identity.OperativeId, Is.EqualTo("test-alpha"));
            Assert.That(identity.State, Is.SameAs(stateBefore), "the same persistent record, not a copy");
            Assert.That(stateBefore.Experience, Is.EqualTo(250));
            Assert.That(stateBefore.ChoiceIds, Is.EqualTo(new[] { "survivability", "combat" }));
            Assert.That(newUnit.GetComponent<Health>().Max, Is.EqualTo(155));
            Assert.That(newUnit.GetComponent<UnitAttacker>().Damage, Is.EqualTo(roster.Evaluate(roster.Members[0]).AttackDamage));
        }

        [UnityTest]
        public IEnumerator XpIsTrackedPerOperative_AndOthersAreUnaffected()
        {
            yield return Start();

            roster.AwardExperience("test-bravo", 120);
            roster.TryPickChoice("test-bravo", "mobility");

            Assert.That(roster.Members.Select(m => m.State.Experience), Is.EqualTo(new[] { 0, 120, 0 }));
            var units = Director.Friendlies;
            Assert.That(units[0].GetComponent<UnitMover>().Speed, Is.EqualTo(5f).Within(0.001f));
            Assert.That(units[1].GetComponent<UnitMover>().Speed, Is.EqualTo(6.5f + 0.5f + 0.75f).Within(0.001f));
            Assert.That(units[2].GetComponent<UnitMover>().Speed, Is.EqualTo(5.5f).Within(0.001f));
            Assert.That(units[0].GetComponent<UnitIdentity>().Effective.Rank, Is.EqualTo(1));
            Assert.That(units[1].GetComponent<UnitIdentity>().Effective.Rank, Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator ADeadOperative_KeepsItsPersistentIdentity_AndStartsTheNextMissionAtFullEffectiveHealth()
        {
            yield return Start();
            roster.AwardExperience("test-charlie", 100);
            roster.TryPickChoice("test-charlie", "survivability");
            var charlie = Director.Friendlies[2];
            var damaged = Director.Friendlies[0];
            damaged.GetComponent<Health>().TakeDamage(60);
            Kill(charlie);
            Assert.That(charlie.GetComponent<Health>().IsAlive, Is.False);

            var record = roster.Find("test-charlie");
            Assert.That(record, Is.Not.Null, "death does not remove the operative from the roster");
            Assert.That(record.State.Experience, Is.EqualTo(100));
            Assert.That(record.State.ChoiceIds, Is.EqualTo(new[] { "survivability" }));
            Assert.That(charlie.GetComponent<UnitIdentity>().OperativeId, Is.EqualTo("test-charlie"), "the corpse still knows who it was");

            Assert.That(Director.RegenerateSame(), Is.True);
            yield return TestWorld.WaitUntil(() => Director.State == MissionState.Ready, 20f);

            foreach (var unit in Director.Friendlies)
            {
                var health = unit.GetComponent<Health>();
                var effective = roster.Evaluate(roster.Find(unit.GetComponent<UnitIdentity>().OperativeId));
                Assert.That(health.IsAlive, Is.True, unit.name);
                Assert.That(health.Current, Is.EqualTo(effective.MaxHealth), unit.name + ": runtime damage does not carry over");
                Assert.That(health.Max, Is.EqualTo(effective.MaxHealth), unit.name);
            }
            Assert.That(Director.Friendlies[2].GetComponent<Health>().Max, Is.EqualTo(125), "Charlie's persistent pick is still in force");
        }

        [UnityTest]
        public IEnumerator Success_AwardsTheTracksXpToEveryOperative_Once_DeadOnesIncluded()
        {
            yield return Start(new MissionSettings { hackTerminal = false });
            Kill(Director.Friendlies[2]);   // a casualty; the mission still succeeds with the others

            yield return CompleteTheMission();
            yield return new WaitForSeconds(0.3f);

            Assert.That(roster.Members.Select(m => m.State.Experience), Is.EqualTo(new[] { 150, 150, 150 }));
        }

        [UnityTest]
        public IEnumerator Failure_AwardsNothing()
        {
            yield return Start();
            foreach (var friendly in Director.Friendlies)
                Kill(friendly);
            yield return null;
            yield return null;
            yield return null;

            Assert.That(Director.Phase, Is.EqualTo(MissionPhase.Failure));
            Assert.That(roster.Members.Select(m => m.State.Experience), Is.EqualTo(new[] { 0, 0, 0 }));
        }

        [UnityTest]
        public IEnumerator MissionFinished_IsRaisedOncePerMission()
        {
            yield return Start(new MissionSettings { hackTerminal = false });
            var finished = new System.Collections.Generic.List<MissionPhase>();
            Director.MissionFinished += finished.Add;

            yield return CompleteTheMission();
            foreach (var friendly in Director.Friendlies)
                Kill(friendly);
            yield return null;
            yield return null;

            Assert.That(finished, Is.EqualTo(new[] { MissionPhase.Success }), "success is terminal: later deaths raise nothing");
        }

        [UnityTest]
        public IEnumerator ARegeneratedMission_CanAwardAgain()
        {
            yield return Start(new MissionSettings { hackTerminal = false });
            yield return CompleteTheMission();
            Assert.That(roster.Members[0].State.Experience, Is.EqualTo(150));

            yield return rig.Generate(777);
            yield return CompleteTheMission();

            Assert.That(roster.Members[0].State.Experience, Is.EqualTo(300));
        }

        // Progression must never leak into generation: same seed, wildly different XP and picks, identical mission.
        [UnityTest]
        public IEnumerator TheSameSeed_GeneratesTheSameMission_WhateverTheOperativesHaveEarned()
        {
            yield return Start(seed: 777);
            var layoutHash = Director.Report.LayoutHash;
            var objectiveHash = Director.Report.ObjectiveHash;
            var friendlySpawns = Director.Current.Layout.FriendlySpawns.ToArray();
            var hostileSpawns = Director.Current.Layout.HostileSpawns.ToArray();

            foreach (var member in roster.Members)
            {
                roster.AwardExperience(member.Id, 700);
                roster.TryPickChoice(member.Id, "survivability");
                roster.TryPickChoice(member.Id, "mobility");
                roster.TryPickChoice(member.Id, "combat");
                roster.TryPickChoice(member.Id, "ability");
            }
            Assert.That(roster.Members.All(m => roster.PendingPicks(m) == 0), Is.True);

            yield return rig.Generate(777);

            Assert.That(Director.State, Is.EqualTo(MissionState.Ready));
            Assert.That(Director.Report.LayoutHash, Is.EqualTo(layoutHash));
            Assert.That(Director.Report.ObjectiveHash, Is.EqualTo(objectiveHash));
            Assert.That(Director.Current.Layout.FriendlySpawns.ToArray(), Is.EqualTo(friendlySpawns));
            Assert.That(Director.Current.Layout.HostileSpawns.ToArray(), Is.EqualTo(hostileSpawns));
        }
    }
}
#endif
