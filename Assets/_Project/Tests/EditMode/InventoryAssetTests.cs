using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Blackglass.Tests
{
    public class InventoryAssetTests
    {
        const string Root = "Assets/_Project/Data/Items/";

        static T Load<T>(string path) where T : Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            Assert.That(asset != null, Is.True, "missing asset " + path);
            return asset;
        }

        [Test]
        public void Catalogue_HoldsTheSixPrototypeItems_AllValid_WithStableIds()
        {
            var catalogue = Load<ItemCatalogue>(Root + "ItemCatalogue.asset");

            var errors = new List<string>();
            Assert.That(catalogue.Validate(errors), Is.True, string.Join("\n", errors));
            foreach (var id in new[] { "item.service-rifle", "item.marksman-rifle", "item.combat-blade", "item.light-vest", "item.servo-boots", "item.medkit" })
                Assert.That(catalogue.Find(id) != null, Is.True, id);
            Assert.That(catalogue.Items.Count, Is.EqualTo(6));
        }

        [Test]
        public void Weapons_ReuseTheExistingArchetypes_AndTradeOffRangeForSpeed()
        {
            var catalogue = Load<ItemCatalogue>(Root + "ItemCatalogue.asset");
            var ranged = Load<CombatArchetype>("Assets/_Project/Data/Archetypes/Ranged.asset");
            var marksman = Load<CombatArchetype>("Assets/_Project/Data/Archetypes/Marksman.asset");
            var melee = Load<CombatArchetype>("Assets/_Project/Data/Archetypes/Melee.asset");

            Assert.That(catalogue.Find("item.service-rifle").Weapon == ranged, Is.True);
            Assert.That(catalogue.Find("item.marksman-rifle").Weapon == marksman, Is.True);
            Assert.That(catalogue.Find("item.combat-blade").Weapon == melee, Is.True);
            Assert.That(marksman.Range, Is.GreaterThan(ranged.Range));
            Assert.That(marksman.AttackInterval, Is.GreaterThan(ranged.AttackInterval), "the longer weapon attacks more slowly");
        }

        [Test]
        public void ArmorUtilityAndMedkit_AreModest_AndTheMedkitMatchesMend()
        {
            var catalogue = Load<ItemCatalogue>(Root + "ItemCatalogue.asset");
            var mend = Load<AbilityDefinition>("Assets/_Project/Data/Abilities/Mend.asset");

            Assert.That(catalogue.Find("item.light-vest").Modifiers.maxHealth, Is.EqualTo(20));
            Assert.That(catalogue.Find("item.servo-boots").Modifiers.moveSpeed, Is.EqualTo(0.5f));
            var medkit = catalogue.Find("item.medkit");
            Assert.That(medkit.IsStackable, Is.True);
            Assert.That(medkit.MaxStack, Is.EqualTo(3));
            Assert.That(medkit.HealAmount, Is.EqualTo(mend.Amount));
        }

        [Test]
        public void EveryWeapon_HasAVisual_WithAPrefab()
        {
            var catalogue = Load<ItemCatalogue>(Root + "ItemCatalogue.asset");
            foreach (var item in catalogue.Items)
            {
                if (item.Category != ItemCategory.Weapon)
                    continue;
                Assert.That(item.WeaponVisual != null, Is.True, item.Id);
                Assert.That(item.WeaponVisual.Prefab != null, Is.True, item.Id);
            }
        }

        [Test]
        public void Starter_SeedsTheThreeOperatives_WithoutProblems_AndKeepsTheirCurrentWeapons()
        {
            var catalogue = Load<ItemCatalogue>(Root + "ItemCatalogue.asset");
            var starter = Load<StarterLoadout>(Root + "StarterLoadout.asset");
            var session = new InventorySession(catalogue, 8);
            var ids = new List<string>();
            foreach (var name in new[] { "Darius", "Kestrel", "Sable" })
            {
                var definition = Load<OperativeDefinition>($"Assets/_Project/Data/Operatives/Definitions/{name}.asset");
                ids.Add(definition.Id);
                var problems = new List<string>();
                session.SeedStarter(starter, new[] { definition.Id }, problems);
                Assert.That(problems, Is.Empty, name);
                var weapon = session.EquippedFor(session.Session, definition.Id).Weapon;
                Assert.That(weapon != null && weapon.Weapon == definition.Archetype, Is.True,
                    $"{name} starts with the weapon matching its archetype, so existing behaviour is kept");
            }

            Assert.That(session.Session.Stash.CountOf("item.medkit"), Is.EqualTo(4));
            Assert.That(session.Session.Stash.CountOf("item.combat-blade"), Is.EqualTo(1));
            Assert.That(session.Session.Loadout(ids[0]).Bag.CountOf("item.medkit"), Is.EqualTo(2));
        }

        [Test]
        public void LootTable_IsValid_AndUsesOnlyCatalogueItems()
        {
            var catalogue = Load<ItemCatalogue>(Root + "ItemCatalogue.asset");
            var table = Load<LootTable>(Root + "LootTable.asset");

            Assert.That(table.IsValid(out var problem), Is.True, problem);
            foreach (var entry in table.Entries)
                Assert.That(catalogue.Find(entry.item.Id) == entry.item, Is.True, entry.item.Id);
        }
    }
}
