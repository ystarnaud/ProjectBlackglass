using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Blackglass.Backend.Core
{
    /// <summary>One line of the attempt's registered loot pool (the ready report's lootPool entry).</summary>
    public sealed class PoolLine
    {
        public PoolLine(int containerIndex, int line, string definitionId, int quantity)
        {
            ContainerIndex = containerIndex;
            Line = line;
            DefinitionId = definitionId ?? throw new ArgumentNullException(nameof(definitionId));
            Quantity = quantity;
        }

        public int ContainerIndex { get; }
        public int Line { get; }
        public string DefinitionId { get; }
        public int Quantity { get; }
    }

    /// <summary>What the ledger needs to know about a catalogue item. The slot is the definition's, never a caller's word.</summary>
    public sealed class LedgerItemDefinition
    {
        public LedgerItemDefinition(string id, bool stackable, bool consumable, EquipSlot? slot)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Stackable = stackable;
            Consumable = consumable;
            Slot = slot;
        }

        public string Id { get; }
        public bool Stackable { get; }
        public bool Consumable { get; }
        public EquipSlot? Slot { get; }
    }

    public sealed class LedgerCatalogue
    {
        readonly Dictionary<string, LedgerItemDefinition> definitions = new Dictionary<string, LedgerItemDefinition>(StringComparer.Ordinal);

        public LedgerCatalogue(IEnumerable<LedgerItemDefinition> definitions)
        {
            foreach (var d in definitions ?? throw new ArgumentNullException(nameof(definitions)))
            {
                if (d == null || this.definitions.ContainsKey(d.Id)) throw new ArgumentException("Definitions must be distinct and non-null.", nameof(definitions));
                this.definitions.Add(d.Id, d);
            }
        }

        public bool TryGet(string id, out LedgerItemDefinition definition)
        {
            definition = null;
            return id != null && definitions.TryGetValue(id, out definition);
        }
    }

    public sealed class SnapshotItem
    {
        public SnapshotItem(string itemId, string definitionId)
        {
            ItemId = itemId ?? throw new ArgumentNullException(nameof(itemId));
            DefinitionId = definitionId ?? throw new ArgumentNullException(nameof(definitionId));
        }

        public string ItemId { get; }
        public string DefinitionId { get; }
    }

    public sealed class SnapshotStack
    {
        public SnapshotStack(string definitionId, int quantity)
        {
            DefinitionId = definitionId ?? throw new ArgumentNullException(nameof(definitionId));
            Quantity = quantity;
        }

        public string DefinitionId { get; }
        public int Quantity { get; }
    }

    /// <summary>One operative of a participant's admission snapshot, as the backend sent it (Base58 ids).</summary>
    public sealed class OperativeSnapshot
    {
        public OperativeSnapshot(string operativeId, int slot, IReadOnlyList<SnapshotItem> items, IReadOnlyList<SnapshotStack> stacks,
            string weaponItemId, string armorItemId, string utilityItemId)
        {
            OperativeId = operativeId ?? throw new ArgumentNullException(nameof(operativeId));
            Slot = slot;
            Items = new ReadOnlyCollection<SnapshotItem>(new List<SnapshotItem>(items ?? throw new ArgumentNullException(nameof(items))));
            Stacks = new ReadOnlyCollection<SnapshotStack>(new List<SnapshotStack>(stacks ?? throw new ArgumentNullException(nameof(stacks))));
            WeaponItemId = string.IsNullOrEmpty(weaponItemId) ? null : weaponItemId;
            ArmorItemId = string.IsNullOrEmpty(armorItemId) ? null : armorItemId;
            UtilityItemId = string.IsNullOrEmpty(utilityItemId) ? null : utilityItemId;
        }

        public string OperativeId { get; }
        public int Slot { get; }
        public IReadOnlyList<SnapshotItem> Items { get; }
        public IReadOnlyList<SnapshotStack> Stacks { get; }
        public string WeaponItemId { get; }
        public string ArmorItemId { get; }
        public string UtilityItemId { get; }

        /// <summary>The equipped item id of a slot, or null.</summary>
        public string EquippedIn(EquipSlot slot) => slot == EquipSlot.Weapon ? WeaponItemId : slot == EquipSlot.Armor ? ArmorItemId : UtilityItemId;

        /// <summary>The one DTO-to-ledger boundary.</summary>
        public static IReadOnlyList<OperativeSnapshot> FromDto(LoadoutSnapshotDto dto)
        {
            if (dto == null) throw new ArgumentNullException(nameof(dto));
            return (dto.operatives ?? new SnapshotOperativeDto[0]).Select(o => new OperativeSnapshot(o.operativeId, o.slot,
                (o.bag?.instances ?? new SnapshotInstanceDto[0]).Select(i => new SnapshotItem(i.itemId, i.definitionId)).ToList(),
                (o.bag?.stacks ?? new SnapshotStackDto[0]).Select(s => new SnapshotStack(s.definitionId, s.quantity)).ToList(),
                o.equipment?.Weapon, o.equipment?.Armor, o.equipment?.Utility)).ToList();
        }
    }

    /// <summary>One entry of a container right after its fill; its index in the list is its pool line.</summary>
    public sealed class ContainerEntry
    {
        public ContainerEntry(string entryId, string definitionId, int quantity)
        {
            EntryId = entryId ?? throw new ArgumentNullException(nameof(entryId));
            DefinitionId = definitionId ?? throw new ArgumentNullException(nameof(definitionId));
            Quantity = quantity;
        }

        public string EntryId { get; }
        public string DefinitionId { get; }
        public int Quantity { get; }
    }

    /// <summary>A copy of one runtime inventory entry (an ItemEntry: instance id, definition, quantity).</summary>
    public sealed class RuntimeEntry
    {
        public RuntimeEntry(string entryId, string definitionId, int quantity)
        {
            EntryId = entryId ?? throw new ArgumentNullException(nameof(entryId));
            DefinitionId = definitionId ?? throw new ArgumentNullException(nameof(definitionId));
            Quantity = quantity;
        }

        public string EntryId { get; }
        public string DefinitionId { get; }
        public int Quantity { get; }
    }

    /// <summary>The entry ids in an operative's three equipment slots (null or "" = empty).</summary>
    public sealed class RuntimeEquipment
    {
        public RuntimeEquipment(string weaponEntryId, string armorEntryId, string utilityEntryId)
        {
            WeaponEntryId = string.IsNullOrEmpty(weaponEntryId) ? null : weaponEntryId;
            ArmorEntryId = string.IsNullOrEmpty(armorEntryId) ? null : armorEntryId;
            UtilityEntryId = string.IsNullOrEmpty(utilityEntryId) ? null : utilityEntryId;
        }

        public string WeaponEntryId { get; }
        public string ArmorEntryId { get; }
        public string UtilityEntryId { get; }

        public string In(EquipSlot slot) => slot == EquipSlot.Weapon ? WeaponEntryId : slot == EquipSlot.Armor ? ArmorEntryId : UtilityEntryId;
    }

    /// <summary>An operative's runtime bag and equipment at the freeze.</summary>
    public sealed class OperativeRuntimeState
    {
        public OperativeRuntimeState(string operativeId, IReadOnlyList<RuntimeEntry> bag, RuntimeEquipment equipment)
        {
            OperativeId = operativeId ?? throw new ArgumentNullException(nameof(operativeId));
            Bag = bag ?? throw new ArgumentNullException(nameof(bag));
            Equipment = equipment ?? throw new ArgumentNullException(nameof(equipment));
        }

        public string OperativeId { get; }
        public IReadOnlyList<RuntimeEntry> Bag { get; }
        public RuntimeEquipment Equipment { get; }
    }

    /// <summary>A container's runtime contents at the freeze.</summary>
    public sealed class ContainerRuntimeState
    {
        public ContainerRuntimeState(int containerIndex, IReadOnlyList<RuntimeEntry> entries)
        {
            ContainerIndex = containerIndex;
            Entries = entries ?? throw new ArgumentNullException(nameof(entries));
        }

        public int ContainerIndex { get; }
        public IReadOnlyList<RuntimeEntry> Entries { get; }
    }
}
