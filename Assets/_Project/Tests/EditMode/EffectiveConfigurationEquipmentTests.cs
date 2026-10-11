using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class EffectiveConfigurationEquipmentTests
    {
        readonly List<Object> made = new List<Object>();
        OperativeDefinition darius;
        ProgressionTrack track;
        ItemDefinition rifle, marksman, blade, vest, boots;

        T Track<T>(T o) where T : Object { made.Add(o); return o; }

        [SetUp]
        public void SetUp()
        {
            var combat = Track(AdvancementChoice.Create("combat", "Combat Training", "", new StatModifiers { attackDamage = 0.15f }));
            track = Track(ProgressionTrack.Create(new[] { 0, 100, 250 }, new[] { combat }, 150, 50));
            var assault = Track(OperativeRole.Create("Assault", "", new StatModifiers { attackDamage = 0.2f }));
            var ranged = Track(CombatArchetype.Create("Ranged", CombatRole.Ranged, 8f, 15, 1f));
            var mark = Track(CombatArchetype.Create("Marksman", CombatRole.Ranged, 16f, 40, 2.5f));
            var melee = Track(CombatArchetype.Create("Melee", CombatRole.Melee, 2f, 25, 1f));
            var prefab = Track(new GameObject("UnitPrefab"));
            darius = Track(OperativeDefinition.Create("darius-id", "Darius", assault, prefab, 130, 5f, ranged, new AbilityDefinition[0]));
            rifle = Track(ItemDefinition.Create("rifle", "Service Rifle", ItemCategory.Weapon, ItemSlot.Weapon, weapon: ranged));
            marksman = Track(ItemDefinition.Create("marksman", "Marksman Rifle", ItemCategory.Weapon, ItemSlot.Weapon, weapon: mark));
            blade = Track(ItemDefinition.Create("blade", "Combat Blade", ItemCategory.Weapon, ItemSlot.Weapon, weapon: melee));
            vest = Track(ItemDefinition.Create("vest", "Vest", ItemCategory.Armor, ItemSlot.Armor, modifiers: new StatModifiers { maxHealth = 20 }));
            boots = Track(ItemDefinition.Create("boots", "Servo Boots", ItemCategory.Utility, ItemSlot.Utility, modifiers: new StatModifiers { moveSpeed = 0.5f }));
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in made) Object.DestroyImmediate(o);
            made.Clear();
        }

        static PersistentOperativeState State() => new PersistentOperativeState("darius-id");

        [Test]
        public void DefaultEquipment_MeansNoEquipmentSystem_AndMatchesTheOldResult()
        {
            var before = EffectiveConfiguration.Evaluate(darius, State(), track);
            var withDefault = EffectiveConfiguration.Evaluate(darius, State(), track, default);

            Assert.That(withDefault.HasWeapon, Is.True);
            Assert.That(withDefault.AttackRange, Is.EqualTo(before.AttackRange));
            Assert.That(withDefault.AttackDamage, Is.EqualTo(before.AttackDamage));
            Assert.That(withDefault.MaxHealth, Is.EqualTo(before.MaxHealth));
            Assert.That(withDefault.WeaponName, Is.EqualTo("Ranged"));
        }

        [Test]
        public void EquippedWeapon_ReplacesTheDefinitionsArchetype_NeverAddsToIt()
        {
            var config = EffectiveConfiguration.Evaluate(darius, State(), track, new EquippedItems(marksman, null, null));

            Assert.That(config.HasWeapon, Is.True);
            Assert.That(config.WeaponName, Is.EqualTo("Marksman Rifle"));
            Assert.That(config.AttackRange, Is.EqualTo(16f), "the marksman's range, not the default's plus the marksman's");
            Assert.That(config.AttackInterval, Is.EqualTo(2.5f));
            Assert.That(config.AttackRole, Is.EqualTo(CombatRole.Ranged));
            // 40 damage, +20% from the Assault role.
            Assert.That(config.AttackDamage, Is.EqualTo(48));
        }

        [Test]
        public void AMeleeWeaponItem_GivesAMeleeUnit()
        {
            var config = EffectiveConfiguration.Evaluate(darius, State(), track, new EquippedItems(blade, null, null));

            Assert.That(config.AttackRole, Is.EqualTo(CombatRole.Melee));
            Assert.That(config.AttackRange, Is.EqualTo(2f));
            Assert.That(config.AttackDamage, Is.EqualTo(30));
        }

        [Test]
        public void ArmorAndUtility_AddTheirModifiers_ToHealthAndSpeed()
        {
            var plain = EffectiveConfiguration.Evaluate(darius, State(), track, new EquippedItems(rifle, null, null));
            var kitted = EffectiveConfiguration.Evaluate(darius, State(), track, new EquippedItems(rifle, vest, boots));

            Assert.That(kitted.MaxHealth, Is.EqualTo(plain.MaxHealth + 20));
            Assert.That(kitted.MoveSpeed, Is.EqualTo(plain.MoveSpeed + 0.5f).Within(1e-4f));
            Assert.That(kitted.AttackDamage, Is.EqualTo(plain.AttackDamage));
        }

        [Test]
        public void EmptyWeaponSlot_MeansNoWeapon_NotTheDefaultArchetype()
        {
            var config = EffectiveConfiguration.Evaluate(darius, State(), track, new EquippedItems(null, vest, null));

            Assert.That(config.HasWeapon, Is.False);
            Assert.That(config.AttackDamage, Is.EqualTo(0));
            Assert.That(config.WeaponName, Is.Empty);
            Assert.That(config.MaxHealth, Is.EqualTo(130 + 20), "armor still applies");
        }

        [Test]
        public void EvaluatingAgain_GivesTheSameResult_AndUnequippingReturnsToBase()
        {
            var equipped = new EquippedItems(rifle, vest, boots);
            var first = EffectiveConfiguration.Evaluate(darius, State(), track, equipped);
            for (var i = 0; i < 5; i++)
                EffectiveConfiguration.Evaluate(darius, State(), track, equipped);
            var again = EffectiveConfiguration.Evaluate(darius, State(), track, equipped);
            var bare = EffectiveConfiguration.Evaluate(darius, State(), track, new EquippedItems(rifle, null, null));

            Assert.That(again.MaxHealth, Is.EqualTo(first.MaxHealth));
            Assert.That(again.MoveSpeed, Is.EqualTo(first.MoveSpeed));
            Assert.That(bare.MaxHealth, Is.EqualTo(130));
            Assert.That(bare.MoveSpeed, Is.EqualTo(5f));
        }

        [Test]
        public void Progression_StillStacksOnTheEquippedWeapon()
        {
            var state = State();
            state.AddExperience(100);
            Assert.That(state.TryPickChoice(track, "combat"), Is.True);

            var config = EffectiveConfiguration.Evaluate(darius, state, track, new EquippedItems(rifle, null, null));

            // 15 base damage, +20% role +15% pick = +35%, rounded away from zero: 20.25 -> 20.
            Assert.That(config.AttackDamage, Is.EqualTo(20));
            Assert.That(config.Rank, Is.EqualTo(2));
        }
    }
}
