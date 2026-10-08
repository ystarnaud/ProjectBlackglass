using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class OperativeDefinitionTests
    {
        OperativeRole role;
        CombatArchetype archetype;
        AbilityDefinition ability;
        GameObject prefab;

        [SetUp]
        public void SetUp()
        {
            role = OperativeRole.Create("Assault", "", default);
            archetype = CombatArchetype.Create("Ranged", CombatRole.Ranged, 8f, 15, 1f);
            ability = AbilityDefinition.Create("Aimed Shot", AbilityTargetMode.Unit, 14f, true, AbilityCoverRule.Applies, 6f, AbilityEffect.Damage, 45);
            prefab = new GameObject("UnitPrefab");
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(role);
            Object.DestroyImmediate(archetype);
            Object.DestroyImmediate(ability);
            Object.DestroyImmediate(prefab);
        }

        OperativeDefinition Make(string id = "id-1", OperativeRole r = null, GameObject p = null, int health = 130,
            CombatArchetype a = null, params AbilityDefinition[] abilities)
        {
            if (abilities.Length == 0)
                abilities = new[] { ability };
            return OperativeDefinition.Create(id, "Darius", r != null ? r : role, p != null ? p : prefab, health, 5f,
                a != null ? a : archetype, abilities);
        }

        [Test]
        public void Create_ExposesTheAuthoredValues()
        {
            var definition = Make();
            try
            {
                Assert.That(definition.Id, Is.EqualTo("id-1"));
                Assert.That(definition.DisplayName, Is.EqualTo("Darius"));
                Assert.That(definition.Role, Is.SameAs(role));
                Assert.That(definition.UnitPrefab, Is.SameAs(prefab));
                Assert.That(definition.BaseMaxHealth, Is.EqualTo(130));
                Assert.That(definition.BaseMoveSpeed, Is.EqualTo(5f));
                Assert.That(definition.Archetype, Is.SameAs(archetype));
                Assert.That(definition.Abilities, Is.EqualTo(new[] { ability }));
                Assert.That(definition.IsValid(out var problem), Is.True, problem);
            }
            finally
            {
                Object.DestroyImmediate(definition);
            }
        }

        [Test]
        public void IsValid_ADestroyedAbilityAsset_CountsAsAnEmptySlot()
        {
            var doomed = AbilityDefinition.Create("Doomed", AbilityTargetMode.Unit, 14f, true, AbilityCoverRule.Applies, 6f, AbilityEffect.Damage, 45);
            var definition = OperativeDefinition.Create("a", "x", role, prefab, 100, 5f, archetype, new[] { ability, doomed });
            try
            {
                Assert.That(definition.IsValid(out var before), Is.True, before);
                Object.DestroyImmediate(doomed);

                Assert.That(definition.IsValid(out var problem), Is.False);
                StringAssert.Contains("ability", problem);
            }
            finally
            {
                Object.DestroyImmediate(definition);
            }
        }

        [Test]
        public void IsValid_AMissingAbilityList_SaysSo()
        {
            var definition = OperativeDefinition.Create("a", "x", role, prefab, 100, 5f, archetype, null);
            try
            {
                definition.GetType().GetField("abilities", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(definition, null);
                Assert.That(definition.IsValid(out var problem), Is.False);
                StringAssert.Contains("missing", problem);
            }
            finally
            {
                Object.DestroyImmediate(definition);
            }
        }

        [Test]
        public void IsValid_NamesTheFirstProblem()
        {
            var created = new System.Collections.Generic.List<OperativeDefinition>();
            try
            {
                OperativeDefinition Add(OperativeDefinition d) { created.Add(d); return d; }

                Assert.That(Add(OperativeDefinition.Create("", "x", role, prefab, 100, 5f, archetype, new AbilityDefinition[0])).IsValid(out var p1), Is.False);
                StringAssert.Contains("id", p1);
                Assert.That(Add(OperativeDefinition.Create("a", "x", null, prefab, 100, 5f, archetype, new AbilityDefinition[0])).IsValid(out var p2), Is.False);
                StringAssert.Contains("role", p2);
                Assert.That(Add(OperativeDefinition.Create("a", "x", role, null, 100, 5f, archetype, new AbilityDefinition[0])).IsValid(out var p3), Is.False);
                StringAssert.Contains("prefab", p3);
                Assert.That(Add(OperativeDefinition.Create("a", "x", role, prefab, 100, 5f, null, new AbilityDefinition[0])).IsValid(out var p4), Is.False);
                StringAssert.Contains("archetype", p4);
                Assert.That(Add(OperativeDefinition.Create("a", "x", role, prefab, 0, 5f, archetype, new AbilityDefinition[0])).IsValid(out var p5), Is.False);
                StringAssert.Contains("health", p5);
                Assert.That(Add(OperativeDefinition.Create("a", "x", role, prefab, 100, 5f, archetype, new AbilityDefinition[] { ability, null })).IsValid(out var p6), Is.False);
                StringAssert.Contains("ability", p6);
                var five = new[] { ability, ability, ability, ability, ability };
                Assert.That(Add(OperativeDefinition.Create("a", "x", role, prefab, 100, 5f, archetype, five)).IsValid(out var p7), Is.False);
                StringAssert.Contains("4", p7);
                Assert.That(Add(OperativeDefinition.Create("a", " ", role, prefab, 100, 5f, archetype, new AbilityDefinition[0])).IsValid(out var p8), Is.False);
                StringAssert.Contains("name", p8);
            }
            finally
            {
                foreach (var d in created)
                    Object.DestroyImmediate(d);
            }
        }
    }
}
