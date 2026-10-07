using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class SquadRosterTests
    {
        readonly List<Object> created = new List<Object>();
        ProgressionTrack track;
        OperativeDefinition alpha, bravo, charlie;
        GameObject host;
        SquadRoster roster;

        T Own<T>(T asset) where T : Object
        {
            created.Add(asset);
            return asset;
        }

        [SetUp]
        public void SetUp()
        {
            var combat = Own(AdvancementChoice.Create("combat", "Combat Training", "", new StatModifiers { attackDamage = 0.15f }));
            var hull = Own(AdvancementChoice.Create("survivability", "Reinforced", "", new StatModifiers { maxHealth = 25 }));
            track = Own(ProgressionTrack.Create(new[] { 0, 100, 250, 450, 700 }, new[] { combat, hull }, 150, 50));
            var assault = Own(OperativeRole.Create("Assault", "", new StatModifiers { attackDamage = 0.2f }));
            var archetype = Own(CombatArchetype.Create("Ranged", CombatRole.Ranged, 8f, 15, 1f));
            var prefab = Own(new GameObject("UnitPrefab"));
            alpha = Own(OperativeDefinition.Create("id-alpha", "Alpha", assault, prefab, 130, 5f, archetype, new AbilityDefinition[0]));
            bravo = Own(OperativeDefinition.Create("id-bravo", "Bravo", assault, prefab, 80, 6.5f, archetype, new AbilityDefinition[0]));
            charlie = Own(OperativeDefinition.Create("id-charlie", "Charlie", assault, prefab, 100, 5.5f, archetype, new AbilityDefinition[0]));
            host = Own(new GameObject("Roster"));
            roster = host.AddComponent<SquadRoster>();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in created)
            {
                if (o != null)
                    Object.DestroyImmediate(o);
            }
            created.Clear();
        }

        [Test]
        public void Initialize_MakesOneMemberPerDefinition_InOrder_WithTheDefinitionsIds()
        {
            roster.Initialize(new[] { alpha, bravo, charlie }, track);

            Assert.That(roster.Count, Is.EqualTo(3));
            Assert.That(roster.Members.Select(m => m.Id), Is.EqualTo(new[] { "id-alpha", "id-bravo", "id-charlie" }));
            Assert.That(roster.Members.Select(m => m.Definition), Is.EqualTo(new[] { alpha, bravo, charlie }));
            Assert.That(roster.Members.Select(m => m.State.OperativeId), Is.EqualTo(new[] { "id-alpha", "id-bravo", "id-charlie" }));
            Assert.That(roster.Track, Is.SameAs(track));
            Assert.That(roster.States().Select(s => s.OperativeId), Is.EqualTo(new[] { "id-alpha", "id-bravo", "id-charlie" }));
        }

        [Test]
        public void Initialize_AgainRebuildsFreshStates()
        {
            roster.Initialize(new[] { alpha }, track);
            roster.AwardExperience("id-alpha", 100);
            roster.Initialize(new[] { alpha, bravo }, track);

            Assert.That(roster.Count, Is.EqualTo(2));
            Assert.That(roster.Find("id-alpha").State.Experience, Is.EqualTo(0));
        }

        [Test]
        public void Initialize_RefusesADuplicateId_LoudlyAndKeepsTheFirst()
        {
            var twin = Own(OperativeDefinition.Create("id-alpha", "Twin", alpha.Role, alpha.UnitPrefab, 90, 5f, alpha.Archetype, new AbilityDefinition[0]));
            LogAssert.Expect(LogType.Error, new Regex("id-alpha"));

            roster.Initialize(new[] { alpha, twin, bravo }, track);

            Assert.That(roster.Members.Select(m => m.Definition.DisplayName), Is.EqualTo(new[] { "Alpha", "Bravo" }));
        }

        [Test]
        public void Initialize_RefusesABlankIdOrAMissingDefinition()
        {
            var blank = Own(OperativeDefinition.Create("", "Blank", alpha.Role, alpha.UnitPrefab, 90, 5f, alpha.Archetype, new AbilityDefinition[0]));
            LogAssert.Expect(LogType.Error, new Regex("Blank"));
            LogAssert.Expect(LogType.Error, new Regex("missing"));

            roster.Initialize(new[] { blank, null, bravo }, track);

            Assert.That(roster.Members.Select(m => m.Definition.DisplayName), Is.EqualTo(new[] { "Bravo" }));
        }

        [Test]
        public void Find_ByIdOrNull()
        {
            roster.Initialize(new[] { alpha, bravo }, track);
            Assert.That(roster.Find("id-bravo").Definition, Is.SameAs(bravo));
            Assert.That(roster.Find("nope"), Is.Null);
            Assert.That(roster.Find(null), Is.Null);
        }

        [Test]
        public void AwardExperience_GoesToOneOperativeOnly()
        {
            roster.Initialize(new[] { alpha, bravo, charlie }, track);

            Assert.That(roster.AwardExperience("id-bravo", 120), Is.True);

            Assert.That(roster.Find("id-alpha").State.Experience, Is.EqualTo(0));
            Assert.That(roster.Find("id-bravo").State.Experience, Is.EqualTo(120));
            Assert.That(roster.Find("id-charlie").State.Experience, Is.EqualTo(0));
            Assert.That(roster.Rank(roster.Find("id-bravo")), Is.EqualTo(2));
            Assert.That(roster.Rank(roster.Find("id-alpha")), Is.EqualTo(1));
            Assert.That(roster.PendingPicks(roster.Find("id-bravo")), Is.EqualTo(1));
        }

        [Test]
        public void AwardExperience_UnknownIdOrNonPositiveAmount_IsRefused_AndRaisesNothing()
        {
            roster.Initialize(new[] { alpha }, track);
            var changed = new List<string>();
            roster.Changed += changed.Add;

            Assert.That(roster.AwardExperience("nope", 50), Is.False);
            Assert.That(roster.AwardExperience("id-alpha", 0), Is.False);
            Assert.That(roster.AwardExperience("id-alpha", -5), Is.False);

            Assert.That(changed, Is.Empty);
            Assert.That(roster.Find("id-alpha").State.Experience, Is.EqualTo(0));
        }

        [Test]
        public void Changed_NamesTheOperative_ForXpPicksAndReset()
        {
            roster.Initialize(new[] { alpha, bravo }, track);
            var changed = new List<string>();
            roster.Changed += changed.Add;

            roster.AwardExperience("id-alpha", 100);
            roster.TryPickChoice("id-alpha", "combat");
            roster.ResetProgression("id-alpha");

            Assert.That(changed, Is.EqualTo(new[] { "id-alpha", "id-alpha", "id-alpha" }));
        }

        [Test]
        public void TryPickChoice_NeedsAPendingPick_AndAKnownChoice()
        {
            roster.Initialize(new[] { alpha }, track);
            Assert.That(roster.TryPickChoice("id-alpha", "combat"), Is.False, "no XP yet");
            roster.AwardExperience("id-alpha", 100);
            Assert.That(roster.TryPickChoice("id-alpha", "nope"), Is.False);
            Assert.That(roster.TryPickChoice("nope", "combat"), Is.False);
            Assert.That(roster.TryPickChoice("id-alpha", "combat"), Is.True);
            Assert.That(roster.Find("id-alpha").State.ChoiceIds, Is.EqualTo(new[] { "combat" }));
        }

        [Test]
        public void Evaluate_UsesTheMembersOwnState()
        {
            roster.Initialize(new[] { alpha, bravo }, track);
            roster.AwardExperience("id-alpha", 100);
            roster.TryPickChoice("id-alpha", "survivability");

            Assert.That(roster.Evaluate(roster.Find("id-alpha")).MaxHealth, Is.EqualTo(155));
            Assert.That(roster.Evaluate(roster.Find("id-bravo")).MaxHealth, Is.EqualTo(80));
        }

        [Test]
        public void ResetProgression_AffectsOnlyThatOperative()
        {
            roster.Initialize(new[] { alpha, bravo }, track);
            roster.AwardExperience("id-alpha", 250);
            roster.AwardExperience("id-bravo", 250);

            Assert.That(roster.ResetProgression("id-alpha"), Is.True);
            Assert.That(roster.ResetProgression("nope"), Is.False);

            Assert.That(roster.Find("id-alpha").State.Experience, Is.EqualTo(0));
            Assert.That(roster.Find("id-bravo").State.Experience, Is.EqualTo(250));
        }

        [Test]
        public void AwardMissionCompletion_GivesEveryoneTheTracksAmount_AndRaisesChangedForEach()
        {
            roster.Initialize(new[] { alpha, bravo, charlie }, track);
            var changed = new List<string>();
            roster.Changed += changed.Add;

            roster.AwardMissionCompletion();

            Assert.That(roster.Members.Select(m => m.State.Experience), Is.EqualTo(new[] { 150, 150, 150 }));
            Assert.That(changed, Is.EqualTo(new[] { "id-alpha", "id-bravo", "id-charlie" }));
        }

        [Test]
        public void HandleMissionFinished_AwardsOnSuccessOnly()
        {
            roster.Initialize(new[] { alpha, bravo }, track);

            roster.HandleMissionFinished(MissionPhase.Failure);
            roster.HandleMissionFinished(MissionPhase.Active);
            Assert.That(roster.Members.Select(m => m.State.Experience), Is.EqualTo(new[] { 0, 0 }));

            roster.HandleMissionFinished(MissionPhase.Success);
            Assert.That(roster.Members.Select(m => m.State.Experience), Is.EqualTo(new[] { 150, 150 }));
        }

        [Test]
        public void ANullTrack_WithASquad_LogsAnErrorAndBuildsNothing()
        {
            LogAssert.Expect(LogType.Error, new Regex("progression track"));
            roster.Initialize(new[] { alpha }, null);
            Assert.That(roster.Count, Is.EqualTo(0));
        }
    }
}
