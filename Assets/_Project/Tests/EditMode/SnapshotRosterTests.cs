using System.Collections.Generic;
using System.Linq;
using Blackglass.Backend.Core;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    /// <summary>
    /// Stage 12 Task 5: the server's roster is the admission snapshot's operatives: the backend's ids, XP and picks in slot
    /// order, with this build's definitions. It never awards XP (the backend does, dead operatives included).
    /// </summary>
    public sealed class SnapshotRosterTests
    {
        readonly List<Object> made = new List<Object>();
        ProgressionTrack track;
        OperativeDefinition recon, assault;

        T Track<T>(T o) where T : Object { made.Add(o); return o; }

        [SetUp]
        public void SetUp()
        {
            track = Track(ProgressionTrack.Create(new[] { 0, 100, 250 }, new[]
            {
                Track(AdvancementChoice.Create("combat", "Combat", "", default)), Track(AdvancementChoice.Create("ability", "Ability", "", default)),
            }, 150, 50));
            var role = Track(OperativeRole.Create("Recon", "", default));
            var prefab = Track(new GameObject("Prefab"));
            var archetype = Track(CombatArchetype.Create("Ranged", CombatRole.Ranged, 8f, 15, 1f));
            recon = Track(OperativeDefinition.Create("def-recon", "Recon", role, prefab, 100, 5f, archetype, new AbilityDefinition[0]));
            assault = Track(OperativeDefinition.Create("def-assault", "Assault", role, prefab, 120, 5f, archetype, new AbilityDefinition[0]));
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in made) Object.DestroyImmediate(o);
            made.Clear();
        }

        [Test]
        public void Restore_keeps_the_backends_id_experience_and_picks()
        {
            var state = PersistentOperativeState.Restore("C", 260, new[] { "combat", "combat" }, track, out var problem);
            Assert.That(problem, Is.Null);
            Assert.That(state.OperativeId, Is.EqualTo("C"));
            Assert.That(state.Experience, Is.EqualTo(260));
            Assert.That(state.ChoiceIds, Is.EqualTo(new[] { "combat", "combat" }));
            Assert.That(state.Rank(track), Is.EqualTo(3));
            Assert.That(state.PendingPicks(track), Is.EqualTo(0));
        }

        [TestCase(0, new[] { "combat" })]          // rank 1 has no pick
        [TestCase(120, new[] { "combat", "ability" })] // rank 2 has one
        [TestCase(260, new[] { "stealth" })]       // unknown to the track
        [TestCase(-1, new string[0])]
        public void Restore_refuses_progression_the_track_does_not_allow(int experience, string[] choices)
        {
            Assert.That(PersistentOperativeState.Restore("C", experience, choices, track, out var problem), Is.Null);
            Assert.That(problem, Is.EqualTo("bad_progression"));
        }

        AttemptStart Start(int experience = 0, int rank = 1, string[] choices = null, string definition1 = "def-assault")
        {
            var snapshot = AttemptTestData.LedgerSnapshot("05-two-operatives-defeat.json");
            snapshot.operatives[0].definitionId = "def-recon";
            snapshot.operatives[1].definitionId = definition1;
            snapshot.operatives[1].experience = experience;
            snapshot.operatives[1].rank = rank;
            snapshot.operatives[1].choices = choices ?? new string[0];
            Assert.That(AttemptStart.TryCreate(AttemptTestData.AttemptId, AttemptTestData.Response(snapshot), AttemptTestData.GoldenCatalogueVersion,
                id => id == "def-recon" || id == "def-assault" || id == "def-other", AttemptTestData.GoldenLedgerCatalogue(), out var start, out var refusal), Is.True, refusal);
            return start;
        }

        SquadRoster Roster()
        {
            var go = Track(new GameObject("Roster"));
            go.SetActive(false);
            var roster = go.AddComponent<SquadRoster>();
            go.SetActive(true);
            return roster;
        }

        [Test]
        public void The_roster_is_the_snapshot_in_slot_order_with_this_builds_definitions()
        {
            var start = Start(260, 3, new[] { "combat", "ability" });
            Assert.That(AttemptSquad.TryMembers(start, new[] { assault, recon }, track, out var members, out var problem), Is.True, problem);
            var roster = Roster();
            roster.InitializeFromSnapshot(members, track);
            Assert.That(roster.SnapshotMode, Is.True);
            Assert.That(roster.Members.Select(m => m.Id), Is.EqualTo(new[] { "C", "D" }));
            Assert.That(roster.Members.Select(m => m.Definition), Is.EqualTo(new[] { recon, assault }));
            Assert.That(roster.Members[1].State.Experience, Is.EqualTo(260));
            Assert.That(roster.Members[1].State.ChoiceIds, Is.EqualTo(new[] { "combat", "ability" }));
            Assert.That(roster.Rank(roster.Members[1]), Is.EqualTo(3));
        }

        [Test]
        public void A_stated_rank_that_disagrees_with_the_track_is_bad_progression()
        {
            Assert.That(AttemptSquad.TryMembers(Start(260, 2, new[] { "combat" }), new[] { assault, recon }, track, out _, out var problem), Is.False);
            Assert.That(problem, Is.EqualTo("bad_progression"));
        }

        [Test]
        public void A_definition_this_scene_lacks_is_unknown_operative_definition()
        {
            Assert.That(AttemptSquad.TryMembers(Start(definition1: "def-other"), new[] { assault, recon }, track, out _, out var problem), Is.False);
            Assert.That(problem, Is.EqualTo("unknown_operative_definition"));
        }

        [Test]
        public void A_snapshot_roster_never_awards_experience()
        {
            AttemptSquad.TryMembers(Start(), new[] { assault, recon }, track, out var members, out _);
            var roster = Roster();
            roster.InitializeFromSnapshot(members, track);
            roster.HandleMissionFinished(MissionPhase.Success);
            roster.AwardMissionCompletion();
            Assert.That(roster.Members.Select(m => m.State.Experience), Is.EqualTo(new[] { 0, 0 }));
        }

        [Test]
        public void A_snapshot_roster_survives_a_later_awake()
        {
            AttemptSquad.TryMembers(Start(), new[] { assault, recon }, track, out var members, out _);
            var go = Track(new GameObject("Roster"));
            go.SetActive(false);
            var roster = go.AddComponent<SquadRoster>();
            roster.InitializeFromSnapshot(members, track);
            go.SetActive(true); // Awake now: it must not rebuild from the (empty) starting squad
            Assert.That(roster.Members.Select(m => m.Id), Is.EqualTo(new[] { "C", "D" }));
        }
    }
}
