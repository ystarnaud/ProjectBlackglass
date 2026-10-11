using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>Test items, a catalogue and a SquadInventory with a mission already begun, for tests that need no director.</summary>
    internal sealed class InventoryKit
    {
        public readonly ItemDefinition Medkit;
        public readonly ItemDefinition Rifle;
        public readonly ItemDefinition Vest;
        public readonly ItemCatalogue Catalogue;
        public readonly SquadInventory Inventory;
        readonly TestWorld world;

        public InventoryKit(TestWorld world, int bagCapacity = 8, params string[] operativeIds)
        {
            this.world = world;
            var archetype = world.Track(CombatArchetype.Create("Ranged", CombatRole.Ranged, 8f, 15, 1f));
            Medkit = world.Track(ItemDefinition.Create("medkit", "Medkit", ItemCategory.Consumable, ItemSlot.None, 3, default, null, 40));
            Rifle = world.Track(ItemDefinition.Create("rifle", "Service Rifle", ItemCategory.Weapon, ItemSlot.Weapon, weapon: archetype));
            Vest = world.Track(ItemDefinition.Create("vest", "Vest", ItemCategory.Armor, ItemSlot.Armor));
            Catalogue = world.Track(ItemCatalogue.Create(Medkit, Rifle, Vest));
            var host = world.Track(new GameObject("Inventory"));
            host.SetActive(false);   // Awake would build the session before it has a catalogue
            Inventory = host.AddComponent<SquadInventory>();
            Inventory.Initialize(Catalogue, null, null, bagCapacity, null);
            host.SetActive(true);
            foreach (var id in operativeIds)
                Inventory.Core.EnsureOperative(id);
        }

        /// <summary>Starts the mission working state (a copy of the session), as the director does before spawning.</summary>
        public void BeginMission(string missionId = "kit-mission") => Inventory.BeginMission(missionId);

        /// <summary>The operative's bag in the working state during a mission, else in the session.</summary>
        public ItemInventory Bag(string operativeId)
        {
            var state = Inventory.Core.Working ?? Inventory.Core.Session;
            return state.Loadout(operativeId).Bag;
        }

        /// <summary>Adds a UnitItems to the unit, bound to this inventory and operative.</summary>
        public UnitItems Equip(CommandableUnit unit, string operativeId)
        {
            var items = unit.gameObject.AddComponent<UnitItems>();
            items.Bind(Inventory, operativeId);
            return items;
        }

        /// <summary>A searched (or not) loot container with these contents, at ground level; no collider, so it carves nothing.</summary>
        public LootContainer CreateContainer(Vector3 ground, bool searched, params (ItemDefinition item, int quantity)[] items)
        {
            var host = world.Track(GameObject.CreatePrimitive(PrimitiveType.Cube));
            host.name = "TestLootContainer";
            host.transform.position = ground + Vector3.up * 0.4f;
            Object.DestroyImmediate(host.GetComponent<Collider>());
            var container = host.AddComponent<LootContainer>();
            var contents = new ItemInventory(0);
            foreach (var (item, quantity) in items)
                contents.Add(item, quantity);
            container.InitializeWith(contents, searched);
            return container;
        }
    }
}
