using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;

namespace Blackglass.Tests
{
    /// <summary>The authored prototype data: valid, distinct, and shaped as the design says.</summary>
    public class OperativeAssetTests
    {
        const string Root = "Assets/_Project/Data/Operatives/";

        static T Load<T>(string path) where T : UnityEngine.Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(Root + path);
            Assert.That(asset, Is.Not.Null, "missing " + path + " (run Blackglass/Operatives/Create Prototype Operatives)");
            return asset;
        }

        static OperativeDefinition[] Squad() => new[]
        {
            Load<OperativeDefinition>("Definitions/Darius.asset"),
            Load<OperativeDefinition>("Definitions/Kestrel.asset"),
            Load<OperativeDefinition>("Definitions/Sable.asset"),
        };

        [Test]
        public void EveryDefinition_IsValid()
        {
            foreach (var definition in Squad())
            {
                Assert.That(definition.IsValid(out var problem), Is.True, definition.name + ": " + problem);
            }
        }

        [Test]
        public void TheIds_AreFixedUniqueGuids_AndNotTheDisplayNames()
        {
            var squad = Squad();
            Assert.That(squad.Select(d => d.Id), Is.EqualTo(new[]
            {
                "6f1d2a40-3b7c-4e95-8a1f-52c0d9e7b301",
                "a93e5c17-0d48-4f2b-b6a3-7e1c8d4f9a02",
                "2c84b7e9-51a6-4d03-9f7e-c3a0165d8b03",
            }));
            Assert.That(squad.Select(d => d.Id).Distinct().Count(), Is.EqualTo(3));
            foreach (var definition in squad)
            {
                Assert.That(Guid.TryParse(definition.Id, out _), Is.True, definition.name);
                Assert.That(definition.Id, Is.Not.EqualTo(definition.DisplayName));
            }
            Assert.That(squad.Select(d => d.DisplayName), Is.EqualTo(new[] { "Darius", "Kestrel", "Sable" }));
        }

        [Test]
        public void TheThreeRoles_AreAssaultReconSupport()
        {
            Assert.That(Squad().Select(d => d.Role.DisplayName), Is.EqualTo(new[] { "Assault", "Recon", "Support" }));
        }

        [Test]
        public void TheOperatives_AreConfiguredDifferently()
        {
            var squad = Squad();
            Assert.That(squad.Select(d => d.BaseMaxHealth), Is.EqualTo(new[] { 130, 80, 100 }));
            Assert.That(squad.Select(d => d.BaseMoveSpeed).ToArray(), Is.EqualTo(new[] { 5f, 6.5f, 5.5f }));
            Assert.That(squad.Select(d => d.Archetype.DisplayName), Is.EqualTo(new[] { "Ranged", "Marksman", "Ranged" }));
            Assert.That(squad.Select(d => d.Abilities.Select(a => a.DisplayName).ToArray()), Is.EqualTo(new[]
            {
                new[] { "Aimed Shot", "Blast" }, new[] { "Aimed Shot", "Recon Scan" }, new[] { "Mend", "Aimed Shot" },
            }));
            var configs = squad.Select(d => EffectiveConfiguration.Evaluate(d, new PersistentOperativeState(d.Id), Load<ProgressionTrack>("Progression.asset"))).ToArray();
            Assert.That(configs.Select(c => (c.MaxHealth, c.MoveSpeed, c.AttackRange, c.AttackDamage)).Distinct().Count(), Is.EqualTo(3));
        }

        [Test]
        public void TheUnitPrefabs_AreDariusForDarius_AndTheCapsuleForTheOthers()
        {
            var squad = Squad();
            Assert.That(AssetDatabase.GetAssetPath(squad[0].UnitPrefab), Does.EndWith("Darius_Player.prefab"));
            Assert.That(AssetDatabase.GetAssetPath(squad[1].UnitPrefab), Does.EndWith("FriendlyUnit.prefab"));
            Assert.That(AssetDatabase.GetAssetPath(squad[2].UnitPrefab), Does.EndWith("FriendlyUnit.prefab"));
        }

        [Test]
        public void TheTrack_HasFiveRanks_AndTheFourChoices()
        {
            var track = Load<ProgressionTrack>("Progression.asset");
            Assert.That(track.MaxRank, Is.EqualTo(5));
            Assert.That(track.XpForRank(5), Is.EqualTo(700));
            Assert.That(track.Choices.Select(c => c.Id), Is.EqualTo(new[] { "combat", "survivability", "mobility", "ability" }));
            Assert.That(track.MissionCompletionXp, Is.EqualTo(150));
            Assert.That(track.DebugXpStep, Is.EqualTo(50));
        }

        [Test]
        public void EveryChoice_ChangesSomething_AndNoChoiceIsAnAcrossTheBoardBoost()
        {
            var track = Load<ProgressionTrack>("Progression.asset");
            foreach (var choice in track.Choices)
            {
                var m = choice.Modifiers;
                var changed = new[] { m.maxHealth != 0, m.moveSpeed != 0f, m.attackDamage != 0f, m.abilityPower != 0f, m.abilityCooldownReduction != 0f };
                Assert.That(changed.Count(c => c), Is.GreaterThanOrEqualTo(1), choice.Id);
                Assert.That(changed.Count(c => c), Is.LessThanOrEqualTo(2), choice.Id + " should be a focused choice");
            }
            Assert.That(track.Choices.Select(c => c.Modifiers.maxHealth != 0 ? "health" : c.Modifiers.moveSpeed != 0f ? "speed"
                : c.Modifiers.attackDamage != 0f ? "damage" : "ability").Distinct().Count(), Is.EqualTo(4), "one choice per theme");
        }

        [Test]
        public void ThePrototypeSquad_DoesNotShareProgression_EvenThoughDefinitionsShareAssets()
        {
            var track = Load<ProgressionTrack>("Progression.asset");
            var squad = Squad();
            Assert.That(squad[0].Archetype, Is.SameAs(squad[2].Archetype), "Darius and Sable share the Ranged archetype asset");
            var a = new PersistentOperativeState(squad[0].Id);
            var b = new PersistentOperativeState(squad[2].Id);
            a.AddExperience(700);
            a.TryPickChoice(track, "combat");
            Assert.That(EffectiveConfiguration.Evaluate(squad[2], b, track).AttackDamage,
                Is.EqualTo(EffectiveConfiguration.Evaluate(squad[2], new PersistentOperativeState("fresh"), track).AttackDamage));
        }
    }
}
