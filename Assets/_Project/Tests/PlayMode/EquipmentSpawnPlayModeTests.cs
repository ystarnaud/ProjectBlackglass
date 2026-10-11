#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class EquipmentSpawnPlayModeTests
    {
        const string Items = "Assets/_Project/Data/Items/";
        const string Ops = "Assets/_Project/Data/Operatives/";
        MissionRig rig;
        SquadRoster roster;
        SquadInventory inventory;
        ItemCatalogue catalogue;

        static T Load<T>(string path) where T : Object => AssetDatabase.LoadAssetAtPath<T>(path);

        [SetUp]
        public void SetUp()
        {
            rig = new MissionRig();
            var squad = new[] { "Darius", "Kestrel", "Sable" }
                .Select(n => Load<OperativeDefinition>($"{Ops}Definitions/{n}.asset")).ToArray();
            roster = rig.AddRoster(squad, Load<ProgressionTrack>(Ops + "Progression.asset"));
            catalogue = Load<ItemCatalogue>(Items + "ItemCatalogue.asset");
            inventory = rig.AddInventory(catalogue, Load<StarterLoadout>(Items + "StarterLoadout.asset"), roster);
        }

        [TearDown]
        public void TearDown()
        {
            rig.Dispose();
            rig = null;
        }

        UnitAttacker AttackerOf(int index) => rig.Director.Friendlies[index].GetComponent<UnitAttacker>();

        UnitWeaponVisual VisualOf(int index) => rig.Director.Friendlies[index].GetComponent<UnitWeaponVisual>();

        // The roster units (Darius_Player, EnemyUnit_Friendly) carry a WeaponSocket.
        Transform SocketOf(int index) =>
            rig.Director.Friendlies[index].GetComponentsInChildren<Transform>(true).First(t => t.name == UnitWeaponVisual.SocketName);

        [UnityTest]
        public IEnumerator Starter_KeepsEveryOperativesCurrentWeaponNumbers()
        {
            yield return rig.Generate(12345);

            var darius = AttackerOf(0);
            var kestrel = AttackerOf(1);
            Assert.That(darius.HasWeapon, Is.True);
            Assert.That(darius.Range, Is.EqualTo(8f));
            Assert.That(kestrel.Range, Is.EqualTo(16f), "Kestrel starts with the marksman rifle");
            Assert.That(kestrel.Cooldown, Is.EqualTo(2.5f));
        }

        [UnityTest]
        public IEnumerator DifferentWeapons_OnDifferentOperatives_ChangeOnlyThoseOperatives()
        {
            var dariusId = roster.Members[0].Id;
            var blade = inventory.Core.Session.Stash.Entries.First(e => e.DefinitionId == "item.combat-blade");
            Assert.That(inventory.Core.FromStash(dariusId, blade.InstanceId, 1).Moved, Is.EqualTo(1));
            Assert.That(inventory.Core.Equip(dariusId, blade.InstanceId).Ok, Is.True);

            yield return rig.Generate(12345);

            Assert.That(AttackerOf(0).Role, Is.EqualTo(CombatRole.Melee));
            Assert.That(AttackerOf(0).Range, Is.EqualTo(2f));
            Assert.That(AttackerOf(1).Range, Is.EqualTo(16f), "Kestrel is unchanged");
            Assert.That(AttackerOf(2).Range, Is.EqualTo(8f), "Sable shares the rifle definition and is unchanged");
            var archetype = Load<CombatArchetype>("Assets/_Project/Data/Archetypes/Melee.asset");
            Assert.That(archetype.Range, Is.EqualTo(2f), "the shared asset was not modified");

            // The hand shows what is equipped: the blade for Darius, the longer marksman rifle for Kestrel.
            var held = VisualOf(0).Current;
            Assert.That(held != null, Is.True, "Darius holds a weapon model");
            Assert.That(held.name, Is.EqualTo(UnitWeaponVisual.ManagedName));
            Assert.That(held.transform.parent, Is.SameAs(SocketOf(0)));
            Assert.That(held.transform.Find("Blade"), Is.Not.Null, "the blade visual (BladePlaceholder), not the rifle");
            Assert.That(held.transform.Find("RifleModel"), Is.Null);
            Assert.That(SocketOf(0).childCount, Is.EqualTo(1));
            Assert.That(VisualOf(1).Current.transform.Find("RifleModel"), Is.Not.Null);
            Assert.That(VisualOf(1).Current.transform.localScale.z, Is.EqualTo(1.25f).Within(1e-4f), "Kestrel shows the marksman rifle");
        }

        [UnityTest]
        public IEnumerator ArmorAndBoots_RaiseHealthAndSpeed_AtSpawn_WithoutHealingLater()
        {
            var id = roster.Members[0].Id;
            foreach (var definitionId in new[] { "item.light-vest", "item.servo-boots" })
            {
                var entry = inventory.Core.Session.Stash.Entries.First(e => e.DefinitionId == definitionId);
                inventory.Core.FromStash(id, entry.InstanceId, 1);
                Assert.That(inventory.Core.Equip(id, entry.InstanceId).Ok, Is.True);
            }
            var baseHealth = roster.Evaluate(roster.Members[0]).MaxHealth;

            yield return rig.Generate(12345);

            var unit = rig.Director.Friendlies[0];
            var health = unit.GetComponent<Health>();
            Assert.That(health.Max, Is.EqualTo(baseHealth + 20));
            Assert.That(health.Current, Is.EqualTo(health.Max), "a mission starts at full effective health");
            health.TakeDamage(30);
            roster.AwardExperience(id, 10);   // a roster change re-applies the configuration
            yield return null;
            Assert.That(health.Max, Is.EqualTo(baseHealth + 20), "re-applying does not add the bonus again");
            Assert.That(health.Current, Is.EqualTo(health.Max - 30), "re-applying does not heal");
        }

        [UnityTest]
        public IEnumerator EmptyWeaponSlot_SpawnsAnUnarmedUnit_ThatSaysSo()
        {
            var id = roster.Members[0].Id;
            Assert.That(inventory.Core.Unequip(id, ItemSlot.Weapon).Ok, Is.True);

            yield return rig.Generate(12345);

            var unit = rig.Director.Friendlies[0];
            Assert.That(AttackerOf(0).HasWeapon, Is.False);
            Assert.That(unit.Issue(new AttackCommand(rig.Director.Hostiles[0].GetComponent<Health>())), Is.False);
            Assert.That(unit.LastRefusal, Is.EqualTo(CommandRefusal.NoWeapon));
            Assert.That(VisualOf(0).Current == null, Is.True, "an unarmed operative holds no weapon model");
            Assert.That(SocketOf(0).childCount, Is.EqualTo(0), "nothing is left in the hand");
            Assert.That(unit.TryGetComponent<UnitAbilities>(out var abilities) && abilities.Count > 0, Is.True, "abilities are unaffected");
        }

        [UnityTest]
        public IEnumerator Regeneration_DoesNotRegrantStarterItems()
        {
            var id = roster.Members[0].Id;
            var before = inventory.Core.Session.Loadout(id).Bag.Count;

            yield return rig.Generate(12345);
            yield return rig.Generate(777);
            yield return rig.Generate(12345);

            Assert.That(inventory.Core.Session.Loadout(id).Bag.Count, Is.EqualTo(before));
            Assert.That(inventory.Core.Session.Stash.CountOf("item.medkit"), Is.EqualTo(4));
        }

        [UnityTest]
        public IEnumerator UnitIdentity_ReportsTheInventory_AndNoRosterMeansNoEquipmentSystem()
        {
            yield return rig.Generate(12345);

            Assert.That(rig.Director.Friendlies[0].GetComponent<UnitIdentity>().Inventory == inventory, Is.True);

            // A second rig built beside a live one generates into the first one's geometry; replace it instead (TearDown disposes it).
            rig.Dispose();
            rig = new MissionRig();
            yield return rig.Generate(12345);
            Assert.That(rig.Director.Friendlies[0].GetComponent<UnitAttacker>().HasWeapon, Is.True);
        }
    }
}
#endif
