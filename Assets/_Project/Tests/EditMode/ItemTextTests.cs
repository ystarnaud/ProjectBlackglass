using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class ItemTextTests
    {
        readonly List<Object> made = new List<Object>();
        ItemDefinition rifle, marksman, vest, boots, medkit;

        T Track<T>(T o) where T : Object { made.Add(o); return o; }

        [SetUp]
        public void SetUp()
        {
            var ranged = Track(CombatArchetype.Create("Ranged", CombatRole.Ranged, 8f, 15, 1f));
            var mark = Track(CombatArchetype.Create("Marksman", CombatRole.Ranged, 16f, 40, 2.5f));
            rifle = Track(ItemDefinition.Create("rifle", "Service Rifle", ItemCategory.Weapon, ItemSlot.Weapon, weapon: ranged));
            marksman = Track(ItemDefinition.Create("marksman", "Marksman Rifle", ItemCategory.Weapon, ItemSlot.Weapon, weapon: mark));
            vest = Track(ItemDefinition.Create("vest", "Vest", ItemCategory.Armor, ItemSlot.Armor, modifiers: new StatModifiers { maxHealth = 20 }));
            boots = Track(ItemDefinition.Create("boots", "Boots", ItemCategory.Utility, ItemSlot.Utility, modifiers: new StatModifiers { moveSpeed = 0.5f }));
            medkit = Track(ItemDefinition.Create("medkit", "Medkit", ItemCategory.Consumable, ItemSlot.None, 3, default, null, 40));
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in made) Object.DestroyImmediate(o);
            made.Clear();
        }

        [Test]
        public void Describe_ShowsTheNumbersThatMatter()
        {
            StringAssert.Contains("Damage 15", ItemText.Describe(rifle));
            StringAssert.Contains("Range 8", ItemText.Describe(rifle));
            StringAssert.Contains("+20 max health", ItemText.Describe(vest));
            StringAssert.Contains("+0.5 m/s speed", ItemText.Describe(boots));
            StringAssert.Contains("Restores 40", ItemText.Describe(medkit));
        }

        [Test]
        public void Compare_ShowsBeforeAndAfter_ForWeapons()
        {
            var text = ItemText.Compare(marksman, rifle);

            StringAssert.Contains("Damage 15 -> 40", text);
            StringAssert.Contains("Range 8 -> 16", text);
            StringAssert.Contains("Interval 1 -> 2.5", text);
        }

        [Test]
        public void Compare_AgainstNothing_ShowsNone_AndForArmorShowsTheModifier()
        {
            StringAssert.Contains("none", ItemText.Compare(rifle, null));
            StringAssert.Contains("+20 max health", ItemText.Compare(vest, null));
        }

        [Test]
        public void Compare_OfAConsumable_IsEmpty()
        {
            Assert.That(ItemText.Compare(medkit, rifle), Is.Empty);
        }

        [Test]
        public void Modifiers_ListsOnlyWhatIsNonZero_WithSigns()
        {
            var text = ItemText.Modifiers(new StatModifiers { attackDamage = 0.15f, abilityCooldownReduction = 0.1f });

            StringAssert.Contains("+15% attack damage", text);
            StringAssert.Contains("-10% ability cooldown", text);
            Assert.That(ItemText.Modifiers(default), Is.Empty);
        }
    }
}
