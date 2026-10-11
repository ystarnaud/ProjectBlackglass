using System.Collections.Generic;

namespace Blackglass
{
    public enum InventoryListKind { None, Bag, Stash, Slot, Container }

    /// <summary>What the panel has selected: a list and an entry's instance id (for a Slot, the equipped entry's id).</summary>
    public readonly struct InventorySelection
    {
        public InventorySelection(InventoryListKind kind, string instanceId)
        {
            Kind = kind;
            InstanceId = instanceId;
        }

        public InventoryListKind Kind { get; }
        public string InstanceId { get; }
        public bool IsNone => Kind == InventoryListKind.None || string.IsNullOrEmpty(InstanceId);
    }

    public struct InventoryTab { public string OperativeId, Name; public bool IsInspected, IsDown; }

    public struct InventorySlotView { public ItemSlot Slot; public string Title, ItemLabel, InstanceId; }

    public struct InventoryRow
    {
        public string InstanceId, Label, Tag;   // Tag: "", "NEW +n" (unsecured pickup)
        public int Quantity;
        public bool IsEquipped;
    }

    /// <summary>Whether an action is offered, usable, and if offered but not usable, why (shown as text).</summary>
    public struct InventoryAction
    {
        public bool Visible, Enabled;
        public string Reason;

        public static InventoryAction Hidden => default;
        public static InventoryAction Usable => new InventoryAction { Visible = true, Enabled = true, Reason = string.Empty };
        public static InventoryAction Blocked(string reason) => new InventoryAction { Visible = true, Enabled = false, Reason = reason };
    }

    /// <summary>
    /// Everything the inventory panel shows, as plain data rebuilt by InventoryViewBuilder. The panel draws it and never
    /// queries gameplay; every request carries an operative id and an instance id, never "whoever is controlled".
    /// </summary>
    public sealed class InventoryView
    {
        public readonly List<InventoryTab> Tabs = new List<InventoryTab>();
        public readonly InventorySlotView[] Slots = new InventorySlotView[3];
        public readonly List<InventoryRow> Bag = new List<InventoryRow>();
        public readonly List<InventoryRow> Stash = new List<InventoryRow>();
        public readonly List<InventoryRow> Container = new List<InventoryRow>();

        public bool HasInventory;
        public InventoryMode Mode;
        public string Header = string.Empty, InspectedId = string.Empty, InspectedName = string.Empty;
        public int BagCapacity;
        public bool HasContainer;
        public InventorySelection Selected;
        public string DetailTitle = string.Empty, DetailBody = string.Empty;
        public InventoryAction Equip, Unequip, ToStash, ToBag, Use, QueueUse, Take, QueueTake, TakeAll, QueueTakeAll;

        public void Clear()
        {
            Tabs.Clear();
            Bag.Clear();
            Stash.Clear();
            Container.Clear();
            for (var i = 0; i < Slots.Length; i++)
                Slots[i] = default;
            HasInventory = false;
            Mode = InventoryMode.Loadout;
            Header = string.Empty;
            InspectedId = string.Empty;
            InspectedName = string.Empty;
            BagCapacity = 0;
            HasContainer = false;
            Selected = default;
            DetailTitle = string.Empty;
            DetailBody = string.Empty;
            Equip = Unequip = ToStash = ToBag = Use = QueueUse = Take = QueueTake = TakeAll = QueueTakeAll = default;
        }
    }
}
