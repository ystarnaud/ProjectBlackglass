using System;
using System.Collections.Generic;
using System.Linq;
using Blackglass.BackendTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    /// <summary>
    /// The golden vector's catalogue as in-memory assets. Lists are deliberately scrambled (the export sorts sets by id),
    /// and every display name carries an optional prefix (the export must not see display names).
    /// </summary>
    internal sealed class GoldenAssets : IDisposable
    {
        public const string One = "00000000-0000-4000-8000-000000000001";
        public const string Two = "00000000-0000-4000-8000-000000000002";
        public const string Three = "00000000-0000-4000-8000-000000000003";

        readonly List<Object> created = new List<Object>();
        readonly string prefix;
        public readonly CombatArchetype Archetype;
        public readonly GameObject Prefab;
        public readonly ItemDefinition Rifle, Vest, Boots, Kit;
        public readonly AbilityDefinition AbilityOne, AbilityTwo;
        public readonly OperativeRole Alpha, Beta;
        public readonly OperativeDefinition OperativeOne, OperativeTwo;
        public readonly List<OperativeDefinition> Operatives = new List<OperativeDefinition>();
        public ItemCatalogue Items;
        public LootTable Loot;
        public ProgressionTrack Progression;
        public StarterLoadout Starter;

        public GoldenAssets(string displayPrefix = "")
        {
            prefix = displayPrefix;
            Archetype = Keep(CombatArchetype.Create(prefix + "Archetype", CombatRole.Ranged, 10f, 10, 1f));
            Prefab = Keep(new GameObject(prefix + "Prefab"));
            Rifle = Item("item.alpha-rifle", ItemCategory.Weapon, ItemSlot.Weapon, 1, 0, Archetype);
            Vest = Item("item.beta-vest", ItemCategory.Armor, ItemSlot.Armor);
            Boots = Item("item.delta-boots", ItemCategory.Utility, ItemSlot.Utility);
            Kit = Item("item.gamma-kit", ItemCategory.Consumable, ItemSlot.None, 3, 10);
            Items = Keep(ItemCatalogue.Create(Kit, Boots, Rifle, Vest));
            Loot = Keep(LootTable.Create(7, 2, 1, 3, new[] { Entry(Kit, 1, 2, 5), Entry(Rifle, 1, 1, 1) }));
            Progression = Keep(ProgressionTrack.Create(new[] { 0, 10, 30 }, new[] { Choice("choice.two"), Choice("choice.one") }, 20, 5));
            AbilityOne = Ability("ability.one");
            AbilityTwo = Ability("ability.two");
            Alpha = Role("role.alpha");
            Beta = Role("role.beta");
            OperativeOne = Operative(One, Alpha, AbilityTwo, AbilityOne);
            OperativeTwo = Operative(Two, Beta);
            Operatives.Add(OperativeTwo);
            Operatives.Add(OperativeOne);
            Starter = StarterFor(new[] { Grant(Rifle, 1, true), Grant(Kit, 2) }, new[] { Grant(Vest, 1), Grant(Kit, 4) }, OperativeOne);
        }

        public CatalogueSources Sources()
        {
            var sources = new CatalogueSources { Items = Items, LootTable = Loot, Progression = Progression, Starter = Starter };
            sources.Operatives.AddRange(Operatives);
            return sources;
        }

        public T Keep<T>(T asset) where T : Object
        {
            created.Add(asset);
            return asset;
        }

        public ItemDefinition Item(string id, ItemCategory category, ItemSlot slot, int maxStack = 1, int heal = 0, CombatArchetype weapon = null) =>
            Keep(ItemDefinition.Create(id, prefix + id, category, slot, maxStack, default, weapon, heal));

        public AbilityDefinition Ability(string id)
        {
            var ability = Keep(AbilityDefinition.Create(prefix + id, AbilityTargetMode.Unit, 10f, true, AbilityCoverRule.Applies, 5f, AbilityEffect.Damage, 10));
            ability.SetId(id);
            ability.name = id;
            return ability;
        }

        public OperativeRole Role(string id)
        {
            var role = Keep(OperativeRole.Create(prefix + id, "", default));
            role.SetId(id);
            role.name = id;
            return role;
        }

        public AdvancementChoice Choice(string id)
        {
            var choice = Keep(AdvancementChoice.Create(id, prefix + id, "", default));
            choice.name = id;
            return choice;
        }

        public OperativeDefinition Operative(string id, OperativeRole role, params AbilityDefinition[] abilities)
        {
            var operative = Keep(OperativeDefinition.Create(id, prefix + id, role, Prefab, 100, 5f, Archetype, abilities));
            operative.name = "operative-" + id;
            return operative;
        }

        public StarterLoadout StarterFor(StarterGrant[] grants, StarterGrant[] stash, params OperativeDefinition[] operatives)
        {
            var entries = operatives.Select(o => new StarterOperative { operative = o, items = grants }).ToArray();
            var starter = Keep(StarterLoadout.Create(entries, stash));
            starter.name = "starter";
            return starter;
        }

        public static LootEntry Entry(ItemDefinition item, int min, int max, int weight) =>
            new LootEntry { item = item, minQuantity = min, maxQuantity = max, weight = weight };

        public static StarterGrant Grant(ItemDefinition item, int quantity, bool equip = false) =>
            new StarterGrant { item = item, quantity = quantity, equip = equip };

        public void Dispose()
        {
            foreach (var asset in created)
            {
                if (asset != null)
                    Object.DestroyImmediate(asset);
            }
        }
    }

    public class CatalogueBuilderTests
    {
        GoldenAssets a;

        [SetUp]
        public void SetUp() => a = new GoldenAssets();

        [TearDown]
        public void TearDown() => a.Dispose();

        static void AssertRefused(CatalogueSources sources, string kind, string fragment)
        {
            var errors = new List<string>();
            Assert.That(CatalogueBuilder.Build(sources, errors), Is.Null, "the build should be refused");
            Assert.That(errors.Exists(e => e.StartsWith(kind, StringComparison.Ordinal) && e.Contains(fragment)), Is.True,
                $"expected a '{kind}' error containing '{fragment}', got:\n" + string.Join("\n", errors));
        }

        static void SetChoices(ProgressionTrack track, params AdvancementChoice[] choices)
        {
            var serialized = new SerializedObject(track);
            var list = serialized.FindProperty("choices");
            list.arraySize = choices.Length;
            for (var i = 0; i < choices.Length; i++)
                list.GetArrayElementAtIndex(i).objectReferenceValue = choices[i];
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        [Test]
        public void Build_FromTheGoldenAssets_WritesTheGoldenVectorByteForByte()
        {
            var errors = new List<string>();
            var document = CatalogueBuilder.Build(a.Sources(), errors);
            Assert.That(document, Is.Not.Null, string.Join("\n", errors));
            Assert.That(CanonicalCatalogueWriter.ToBytes(document).SequenceEqual(SharedVectors.GoldenBytes()), Is.True,
                CanonicalCatalogueWriter.Write(document));
        }

        [Test]
        public void Build_IgnoresDisplayNames()
        {
            using (var renamed = new GoldenAssets("Renamed "))
            {
                var errors = new List<string>();
                var original = CanonicalCatalogueWriter.ToBytes(CatalogueBuilder.Build(a.Sources(), errors));
                var other = CanonicalCatalogueWriter.ToBytes(CatalogueBuilder.Build(renamed.Sources(), errors));
                Assert.That(errors, Is.Empty);
                Assert.That(other.SequenceEqual(original), Is.True);
            }
        }

        [Test]
        public void Build_ListsEveryMissingSource()
        {
            var errors = new List<string>();
            Assert.That(CatalogueBuilder.Build(new CatalogueSources(), errors), Is.Null);
            Assert.That(errors.Count, Is.EqualTo(5), string.Join("\n", errors));
            Assert.That(errors.TrueForAll(e => e.StartsWith("missing: ", StringComparison.Ordinal)), Is.True);
        }

        [Test]
        public void Build_RefusesAnEmptyCatalogueEntry()
        {
            a.Items = a.Keep(ItemCatalogue.Create(a.Kit, null, a.Boots, a.Rifle, a.Vest));
            AssertRefused(a.Sources(), "missing:", "entry 1 is empty");
        }

        [Test]
        public void Build_RefusesAnInvalidItem()
        {
            a.Items = a.Keep(ItemCatalogue.Create(a.Kit, a.Boots, a.Rifle, a.Vest, a.Item("item.empty-kit", ItemCategory.Consumable, ItemSlot.None, 3, 0)));
            AssertRefused(a.Sources(), "malformed:", "a consumable needs a heal amount of at least 1");
        }

        [Test]
        public void Build_RefusesANegativeHealAmount()
        {
            // ItemDefinition.IsValid only checks consumables' healing; the backend refuses a negative amount on any item.
            a.Items = a.Keep(ItemCatalogue.Create(a.Kit, a.Boots, a.Rifle, a.Vest, a.Item("item.odd-vest", ItemCategory.Armor, ItemSlot.Armor, 1, -1)));
            AssertRefused(a.Sources(), "malformed:", "the heal amount is negative");
        }

        [Test]
        public void Build_SortsStarterOperatives_AndKeepsGrantAndStashOrder()
        {
            a.Starter = a.Keep(StarterLoadout.Create(new[]
            {
                new StarterOperative { operative = a.OperativeTwo, items = new[] { GoldenAssets.Grant(a.Kit, 2), GoldenAssets.Grant(a.Rifle, 1, true) } },
                new StarterOperative { operative = a.OperativeOne, items = new[] { GoldenAssets.Grant(a.Vest, 1) } },
            }, new[] { GoldenAssets.Grant(a.Kit, 4), GoldenAssets.Grant(a.Vest, 1) }));
            var errors = new List<string>();
            var document = CatalogueBuilder.Build(a.Sources(), errors);
            Assert.That(document, Is.Not.Null, string.Join("\n", errors));
            Assert.That(document.Starter.Operatives.Select(o => o.OperativeId), Is.EqualTo(new[] { GoldenAssets.One, GoldenAssets.Two }));
            Assert.That(document.Starter.Operatives[1].Grants.Select(g => g.ItemId), Is.EqualTo(new[] { "item.gamma-kit", "item.alpha-rifle" }));
            Assert.That(document.Starter.Stash.Select(g => g.ItemId), Is.EqualTo(new[] { "item.gamma-kit", "item.beta-vest" }));
        }

        [Test]
        public void Build_RefusesAMalformedItemId()
        {
            a.Items = a.Keep(ItemCatalogue.Create(a.Kit, a.Boots, a.Rifle, a.Vest, a.Item("Item.Upper", ItemCategory.Armor, ItemSlot.Armor)));
            AssertRefused(a.Sources(), "malformed:", "id 'Item.Upper'");
        }

        [Test]
        public void Build_RefusesTwoItemsWithOneId()
        {
            a.Items = a.Keep(ItemCatalogue.Create(a.Kit, a.Boots, a.Rifle, a.Vest, a.Item("item.beta-vest", ItemCategory.Armor, ItemSlot.Armor)));
            AssertRefused(a.Sources(), "duplicate:", "item id 'item.beta-vest' is on both");
        }

        [Test]
        public void Build_RefusesAnItemListedTwice()
        {
            a.Items = a.Keep(ItemCatalogue.Create(a.Kit, a.Boots, a.Rifle, a.Vest, a.Vest));
            AssertRefused(a.Sources(), "duplicate:", "is listed more than once");
        }

        [Test]
        public void Build_RefusesAnInvalidLootTable()
        {
            a.Loot = a.Keep(LootTable.Create(7, 2, 1, 3, new LootEntry[0]));
            AssertRefused(a.Sources(), "malformed:", "there are no entries");
        }

        [Test]
        public void Build_RefusesSevenContainers()
        {
            a.Loot = a.Keep(LootTable.Create(7, 7, 1, 3, new[] { GoldenAssets.Entry(a.Kit, 1, 2, 5) }));
            AssertRefused(a.Sources(), "malformed:", "the container count must be 0 to 6");
        }

        [Test]
        public void Build_AcceptsSixContainers()
        {
            a.Loot = a.Keep(LootTable.Create(7, 6, 1, 3, new[] { GoldenAssets.Entry(a.Kit, 1, 2, 5) }));
            var errors = new List<string>();
            var document = CatalogueBuilder.Build(a.Sources(), errors);
            Assert.That(document, Is.Not.Null, string.Join("\n", errors));
            Assert.That(document.LootTable.ContainerCount, Is.EqualTo(6));
        }

        [Test]
        public void Build_RefusesLootOutsideTheCatalogue()
        {
            var stray = a.Item("item.stray", ItemCategory.Armor, ItemSlot.Armor);
            a.Loot = a.Keep(LootTable.Create(7, 2, 1, 3, new[] { GoldenAssets.Entry(stray, 1, 1, 1) }));
            AssertRefused(a.Sources(), "incompatible:", "which is not in the item catalogue");
        }

        [Test]
        public void Build_RefusesAMalformedChoiceId()
        {
            a.Progression = a.Keep(ProgressionTrack.Create(new[] { 0, 10, 30 }, new[] { a.Choice("Choice One") }, 20, 5));
            AssertRefused(a.Sources(), "malformed:", "id 'Choice One'");
        }

        [Test]
        public void Build_RefusesAnEmptyChoiceSlot()
        {
            SetChoices(a.Progression, a.Choice("choice.one"), null);
            AssertRefused(a.Sources(), "missing:", "choice 1 is empty");
        }

        [Test]
        public void Build_RefusesTwoChoicesWithOneId()
        {
            SetChoices(a.Progression, a.Choice("choice.one"), a.Choice("choice.one"));
            AssertRefused(a.Sources(), "duplicate:", "choice id 'choice.one'");
        }

        [Test]
        public void Build_RefusesAnInvalidOperative()
        {
            a.Operatives.Add(a.Keep(OperativeDefinition.Create(GoldenAssets.Three, "Three", a.Alpha, null, 100, 5f, a.Archetype, new AbilityDefinition[0])));
            AssertRefused(a.Sources(), "malformed:", "there is no unit prefab");
        }

        [Test]
        public void Build_RefusesAnUppercaseOperativeId()
        {
            a.Operatives.Add(a.Operative("00000000-0000-4000-8000-00000000000A", a.Alpha));
            AssertRefused(a.Sources(), "malformed:", "id '00000000-0000-4000-8000-00000000000A'");
        }

        [Test]
        public void Build_RefusesTwoOperativesWithOneId()
        {
            var copy = a.Operative(GoldenAssets.One, a.Beta);
            copy.name = "copied-operative";
            a.Operatives.Add(copy);
            AssertRefused(a.Sources(), "duplicate:", $"operative id '{GoldenAssets.One}' is on both operative-{GoldenAssets.One} and copied-operative");
        }

        [Test]
        public void Build_RefusesARoleWithoutAnId()
        {
            a.Alpha.SetId("");
            AssertRefused(a.Sources(), "missing:", "role asset role.alpha (used by operative");
        }

        [Test]
        public void Build_RefusesAnAbilityWithoutAnId()
        {
            a.AbilityOne.SetId("");
            AssertRefused(a.Sources(), "missing:", "ability asset ability.one");
        }

        [Test]
        public void Build_RefusesTwoAbilityAssetsWithOneId()
        {
            a.Operatives.Add(a.Operative(GoldenAssets.Three, a.Beta, a.Ability("ability.one")));
            AssertRefused(a.Sources(), "duplicate:", "ability id 'ability.one' is on both");
        }

        [Test]
        public void Build_RefusesTheSameAbilityTwiceOnOneOperative()
        {
            a.Operatives.Add(a.Operative(GoldenAssets.Three, a.Beta, a.AbilityOne, a.AbilityOne));
            AssertRefused(a.Sources(), "duplicate:", "lists ability ability.one more than once");
        }

        [Test]
        public void Build_RefusesAStarterOperativeThatIsNotExported()
        {
            var outsider = a.Operative(GoldenAssets.Three, a.Beta);
            a.Starter = a.StarterFor(new StarterGrant[0], new StarterGrant[0], outsider);
            AssertRefused(a.Sources(), "incompatible:", "which is not an exported operative definition");
        }

        [Test]
        public void Build_RefusesAStarterOperativeListedTwice()
        {
            a.Starter = a.StarterFor(new StarterGrant[0], new StarterGrant[0], a.OperativeOne, a.OperativeOne);
            AssertRefused(a.Sources(), "duplicate:", "lists operative");
        }

        [Test]
        public void Build_RefusesAGrantWithoutAnItem()
        {
            a.Starter = a.StarterFor(new[] { GoldenAssets.Grant(null, 1) }, new StarterGrant[0], a.OperativeOne);
            AssertRefused(a.Sources(), "missing:", "grant 0 has no item");
        }

        [Test]
        public void Build_RefusesAGrantOutsideTheCatalogue()
        {
            var stray = a.Item("item.stray", ItemCategory.Armor, ItemSlot.Armor);
            a.Starter = a.StarterFor(new[] { GoldenAssets.Grant(stray, 1) }, new StarterGrant[0], a.OperativeOne);
            AssertRefused(a.Sources(), "incompatible:", "is not in the item catalogue");
        }

        [Test]
        public void Build_RefusesAQuantityBelowOne()
        {
            a.Starter = a.StarterFor(new[] { GoldenAssets.Grant(a.Kit, 0) }, new StarterGrant[0], a.OperativeOne);
            AssertRefused(a.Sources(), "malformed:", "quantity 0 is below 1");
        }

        [Test]
        public void Build_RefusesEquippingAConsumable()
        {
            a.Starter = a.StarterFor(new[] { GoldenAssets.Grant(a.Kit, 1, true) }, new StarterGrant[0], a.OperativeOne);
            AssertRefused(a.Sources(), "incompatible:", "cannot be equipped");
        }

        [Test]
        public void Build_RefusesTwoEquipsInOneSlot()
        {
            a.Starter = a.StarterFor(new[] { GoldenAssets.Grant(a.Rifle, 1, true), GoldenAssets.Grant(a.Rifle, 1, true) }, new StarterGrant[0], a.OperativeOne);
            AssertRefused(a.Sources(), "incompatible:", "a second item to equip in the Weapon slot");
        }

        [Test]
        public void Build_RefusesAnEquippedStashGrant()
        {
            a.Starter = a.StarterFor(new StarterGrant[0], new[] { GoldenAssets.Grant(a.Vest, 1, true) }, a.OperativeOne);
            AssertRefused(a.Sources(), "incompatible:", "a stash item cannot be equipped");
        }

        [Test]
        public void Build_ReportsEveryProblemAtOnce()
        {
            a.Alpha.SetId("");
            var stray = a.Item("item.stray", ItemCategory.Armor, ItemSlot.Armor);
            a.Loot = a.Keep(LootTable.Create(7, 2, 1, 3, new[] { GoldenAssets.Entry(stray, 1, 1, 1) }));
            AssertRefused(a.Sources(), "missing:", "role asset role.alpha");
            AssertRefused(a.Sources(), "incompatible:", "which is not in the item catalogue");
        }
    }
}
