using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class ItemDefinitionTests
    {
        readonly List<Object> made = new List<Object>();

        T Track<T>(T o) where T : Object { made.Add(o); return o; }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in made) Object.DestroyImmediate(o);
            made.Clear();
        }

        CombatArchetype Archetype() => Track(CombatArchetype.Create("Ranged", CombatRole.Ranged, 8f, 15, 1f));

        [Test]
        public void Weapon_NeedsAnArchetypeAndTheWeaponSlot()
        {
            var ok = Track(ItemDefinition.Create("rifle", "Service Rifle", ItemCategory.Weapon, ItemSlot.Weapon, weapon: Archetype()));
            var noArchetype = Track(ItemDefinition.Create("bad1", "Bad", ItemCategory.Weapon, ItemSlot.Weapon));
            var wrongSlot = Track(ItemDefinition.Create("bad2", "Bad", ItemCategory.Weapon, ItemSlot.Armor, weapon: Archetype()));

            Assert.That(ok.IsValid(out var problem), Is.True, problem);
            Assert.That(noArchetype.IsValid(out problem), Is.False);
            StringAssert.Contains("archetype", problem);
            Assert.That(wrongSlot.IsValid(out problem), Is.False);
            StringAssert.Contains("slot", problem);
        }

        [Test]
        public void Consumable_NeedsHealing_NoSlot_AndMayStack()
        {
            var medkit = Track(ItemDefinition.Create("medkit", "Medkit", ItemCategory.Consumable, ItemSlot.None, maxStack: 3, healAmount: 40));
            var noHeal = Track(ItemDefinition.Create("bad", "Bad", ItemCategory.Consumable, ItemSlot.None, maxStack: 3));
            var slotted = Track(ItemDefinition.Create("bad2", "Bad", ItemCategory.Consumable, ItemSlot.Utility, maxStack: 3, healAmount: 5));

            Assert.That(medkit.IsValid(out var problem), Is.True, problem);
            Assert.That(medkit.IsStackable, Is.True);
            Assert.That(noHeal.IsValid(out problem), Is.False);
            Assert.That(slotted.IsValid(out problem), Is.False);
        }

        [Test]
        public void ArmorAndUtility_MustMatchTheirSlot_AndNotStack()
        {
            var vest = Track(ItemDefinition.Create("vest", "Vest", ItemCategory.Armor, ItemSlot.Armor, modifiers: new StatModifiers { maxHealth = 20 }));
            var stackedVest = Track(ItemDefinition.Create("v2", "Vest", ItemCategory.Armor, ItemSlot.Armor, maxStack: 2));
            var wrong = Track(ItemDefinition.Create("u", "Boots", ItemCategory.Utility, ItemSlot.Armor));

            Assert.That(vest.IsValid(out var problem), Is.True, problem);
            Assert.That(vest.IsStackable, Is.False);
            Assert.That(stackedVest.IsValid(out problem), Is.False);
            Assert.That(wrong.IsValid(out problem), Is.False);
        }

        [Test]
        public void BlankIdOrName_IsInvalid()
        {
            var noId = Track(ItemDefinition.Create(" ", "X", ItemCategory.Armor, ItemSlot.Armor));
            var noName = Track(ItemDefinition.Create("x", "", ItemCategory.Armor, ItemSlot.Armor));

            Assert.That(noId.IsValid(out var problem), Is.False);
            StringAssert.Contains("id", problem);
            Assert.That(noName.IsValid(out problem), Is.False);
            StringAssert.Contains("name", problem);
        }

        [Test]
        public void Catalogue_FindsById_AndReportsDuplicatesAndInvalidEntries()
        {
            var vest = Track(ItemDefinition.Create("vest", "Vest", ItemCategory.Armor, ItemSlot.Armor));
            var twin = Track(ItemDefinition.Create("vest", "Other Vest", ItemCategory.Armor, ItemSlot.Armor));
            var bad = Track(ItemDefinition.Create("bad", "Bad", ItemCategory.Weapon, ItemSlot.Weapon));
            var good = Track(ItemCatalogue.Create(vest));
            var broken = Track(ItemCatalogue.Create(vest, twin, bad, null));

            Assert.That(good.Find("vest") == vest, Is.True);
            Assert.That(good.Find("nope") == null, Is.True);
            Assert.That(good.Find(null) == null, Is.True);
            Assert.That(good.Validate(new List<string>()), Is.True);

            var errors = new List<string>();
            Assert.That(broken.Validate(errors), Is.False);
            Assert.That(errors, Has.Count.EqualTo(3));
            Assert.That(string.Join("\n", errors), Does.Contain("duplicate").And.Contain("bad").And.Contain("empty"));
        }
    }
}
