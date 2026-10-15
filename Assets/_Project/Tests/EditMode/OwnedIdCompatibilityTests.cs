using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>
    /// The backend identifies owned operatives and owned item instances by canonical Base58 strings ("2", "NQm6nKp8qFC").
    /// Unity's models already key everything by string, so these tests verify (and no gameplay code changes) that such ids
    /// pass through the persistent state, the inventory and the equipment slots unchanged. Base58 is case-sensitive.
    /// </summary>
    public class OwnedIdCompatibilityTests
    {
        const string ShortOperative = "2";
        const string LongOperative = "NQm6nKp8qFC";
        const string ItemId = "5Hx7TqZb3Rm";
        const string OtherItemId = "9kWvE2sLpAd";

        readonly List<Object> created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var asset in created)
            {
                if (asset != null)
                    Object.DestroyImmediate(asset);
            }
            created.Clear();
        }

        T Keep<T>(T asset) where T : Object
        {
            created.Add(asset);
            return asset;
        }

        [TestCase(ShortOperative)]
        [TestCase(LongOperative)]
        public void A_Base58_operative_id_round_trips_PersistentOperativeState(string id)
        {
            var track = Keep(ProgressionTrack.Create(new[] { 0, 100, 250 }, new[] { Keep(AdvancementChoice.Create("combat", "Combat", "", default)) }, 150, 50));
            var state = new PersistentOperativeState(id);
            state.AddExperience(120);
            Assert.That(state.TryPickChoice(track, "combat"), Is.True);

            var restored = JsonUtility.FromJson<PersistentOperativeState>(JsonUtility.ToJson(state));

            Assert.That(restored.OperativeId, Is.EqualTo(id));
            Assert.That(restored.Experience, Is.EqualTo(120));
            Assert.That(restored.ChoiceIds, Is.EqualTo(new[] { "combat" }));
            Assert.That(restored.Rank(track), Is.EqualTo(2));
            Assert.That(restored.PendingPicks(track), Is.EqualTo(0));
        }

        [Test]
        public void A_Base58_operative_id_keys_the_loadouts_case_sensitively()
        {
            var state = new InventoryState();
            state.EnsureLoadout(ShortOperative, 4);
            state.EnsureLoadout(LongOperative, 4);

            Assert.That(state.Loadout(ShortOperative).OperativeId, Is.EqualTo(ShortOperative));
            Assert.That(state.Loadout(LongOperative).OperativeId, Is.EqualTo(LongOperative));
            Assert.That(state.Loadout(LongOperative.ToLowerInvariant()), Is.Null, "Base58 ids differ by case");
            Assert.That(state.Loadouts.Count, Is.EqualTo(2));

            var restored = JsonUtility.FromJson<InventoryState>(JsonUtility.ToJson(state));
            Assert.That(restored.Loadout(ShortOperative), Is.Not.Null);
            Assert.That(restored.Loadout(LongOperative), Is.Not.Null);
            Assert.That(state.Clone().Loadout(LongOperative), Is.Not.Null);
        }

        [Test]
        public void SeedStarter_accepts_Base58_operative_ids()
        {
            var rifle = Keep(ItemDefinition.Create("item.rifle", "Rifle", ItemCategory.Weapon, ItemSlot.Weapon, 1, default,
                Keep(CombatArchetype.Create("Archetype", CombatRole.Ranged, 10f, 10, 1f))));
            var catalogue = Keep(ItemCatalogue.Create(rifle));
            var grants = new Dictionary<string, StarterGrant[]>
            {
                [ShortOperative] = new[] { new StarterGrant { item = rifle, quantity = 1, equip = true } },
                [LongOperative] = new[] { new StarterGrant { item = rifle, quantity = 1, equip = false } },
            };
            var starter = Keep(StarterLoadout.Create(grants, new StarterGrant[0]));
            var session = new InventorySession(catalogue, 4);
            var problems = new List<string>();

            session.SeedStarter(starter, new[] { ShortOperative, LongOperative }, problems);

            Assert.That(problems, Is.Empty);
            Assert.That(session.EquippedFor(session.Session, ShortOperative).Weapon, Is.SameAs(rifle));
            Assert.That(session.EquippedFor(session.Session, LongOperative).Weapon, Is.Null);
            Assert.That(session.Session.Loadout(LongOperative).Bag.CountOf("item.rifle"), Is.EqualTo(1));
        }

        [Test]
        public void A_Base58_item_id_works_as_an_InventoryState_instance_id()
        {
            var rifle = Keep(ItemDefinition.Create("item.rifle", "Rifle", ItemCategory.Weapon, ItemSlot.Weapon, 1, default,
                Keep(CombatArchetype.Create("Archetype", CombatRole.Ranged, 10f, 10, 1f))));
            var catalogue = Keep(ItemCatalogue.Create(rifle));
            var session = new InventorySession(catalogue, 4);
            session.EnsureOperative(LongOperative);

            var entry = new ItemEntry(ItemId, "item.rifle", 1);
            Assert.That(entry.InstanceId, Is.EqualTo(ItemId));
            Assert.That(session.Session.Stash.TryAddEntry(entry), Is.True);
            Assert.That(session.Session.Stash.TryAddEntry(new ItemEntry(ItemId, "item.rifle", 1)), Is.False, "an instance id is unique in a container");
            Assert.That(session.Session.Stash.Find(ItemId), Is.SameAs(entry));
            Assert.That(session.Session.Stash.Find(ItemId.ToLowerInvariant()), Is.Null, "Base58 ids differ by case");

            // stash -> bag keeps the instance id
            var toBag = session.FromStash(LongOperative, ItemId, 1);
            Assert.That(toBag.Succeeded, Is.True);
            var loadout = session.Session.Loadout(LongOperative);
            Assert.That(loadout.Bag.Find(ItemId), Is.SameAs(entry));
            Assert.That(session.Session.Stash.Find(ItemId), Is.Null);

            // equip it, by its Base58 instance id
            Assert.That(session.Equip(LongOperative, ItemId).Ok, Is.True);
            Assert.That(loadout.Equipment.InstanceIdIn(ItemSlot.Weapon), Is.EqualTo(ItemId));
            Assert.That(session.EquippedFor(session.Session, LongOperative).Weapon, Is.SameAs(rifle));

            // an equipped entry cannot go back to the stash until it is unequipped
            Assert.That(session.ToStash(LongOperative, ItemId, 1).Failure, Is.EqualTo(TransferFailure.Equipped));
            Assert.That(session.Unequip(LongOperative, ItemSlot.Weapon).Ok, Is.True);
            var toStash = session.ToStash(LongOperative, ItemId, 1);
            Assert.That(toStash.Succeeded, Is.True);
            Assert.That(session.Session.Stash.Find(ItemId), Is.SameAs(entry));
            Assert.That(loadout.Bag.Find(ItemId), Is.Null);

            // one step stash -> bag -> equipped
            Assert.That(session.EquipFromStash(LongOperative, ItemId).Ok, Is.True);
            Assert.That(loadout.Equipment.InstanceIdIn(ItemSlot.Weapon), Is.EqualTo(ItemId));
        }

        [Test]
        public void Base58_item_ids_survive_Clone_and_serialization()
        {
            var state = new InventoryState();
            var loadout = state.EnsureLoadout(LongOperative, 4);
            Assert.That(loadout.Bag.Add(Keep(ItemDefinition.Create("item.vest", "Vest", ItemCategory.Armor, ItemSlot.Armor)), 1), Is.EqualTo(0));
            Assert.That(state.Stash.TryAddEntry(new ItemEntry(OtherItemId, "item.vest", 1)), Is.True);

            foreach (var copy in new[] { state.Clone(), JsonUtility.FromJson<InventoryState>(JsonUtility.ToJson(state)) })
            {
                Assert.That(copy.Stash.Find(OtherItemId), Is.Not.Null);
                Assert.That(copy.Stash.Find(OtherItemId).InstanceId, Is.EqualTo(OtherItemId));
                Assert.That(copy.Loadout(LongOperative).Bag.Count, Is.EqualTo(1));
            }
        }
    }
}
