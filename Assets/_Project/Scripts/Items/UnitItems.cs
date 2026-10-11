using System;
using UnityEngine;

namespace Blackglass
{
    public enum ItemUseFailure
    {
        None,
        Dead,
        /// <summary>The unit has no working inventory (no mission is running, or it was never bound).</summary>
        NoLoadout,
        /// <summary>The entry is not in this operative's bag (it was used up or taken).</summary>
        NotOwned,
        /// <summary>The item cannot be used this way (not a consumable).</summary>
        NotUsable,
        /// <summary>It would do nothing now (healing at full health).</summary>
        NoEffect,
        /// <summary>The item has no equipment slot, or a mission-time swap was asked of one that is not in the bag.</summary>
        NotEquippable,
        /// <summary>It is already equipped.</summary>
        AlreadyEquipped,
    }

    public enum CollectFailure
    {
        None,
        Dead,
        NoLoadout,
        /// <summary>The container is gone (destroyed with its mission).</summary>
        NoContainer,
        NotSearched,
        OutOfRange,
        /// <summary>The entry (or the whole container) is already empty.</summary>
        NothingThere,
        /// <summary>Some or all items did not fit in the bag and stayed in the container.</summary>
        BagFull,
    }

    /// <summary>What a take did: units moved into the bag, units still in the container among those asked for, and why not all moved.</summary>
    public readonly struct CollectResult
    {
        public CollectResult(int taken, int left, CollectFailure failure)
        {
            Taken = taken;
            Left = left;
            Failure = failure;
        }

        public int Taken { get; }
        public int Left { get; }
        public CollectFailure Failure { get; }

        public string Describe()
        {
            switch (Failure)
            {
                case CollectFailure.None: return $"Took {Taken}.";
                case CollectFailure.BagFull: return Taken > 0 ? $"Bag full: took {Taken}, {Left} left in the container." : "Bag full: nothing taken.";
                case CollectFailure.NothingThere: return "Nothing left to take.";
                case CollectFailure.NotSearched: return "Search the container first.";
                case CollectFailure.OutOfRange: return "Too far from the container.";
                case CollectFailure.NoContainer: return "The container is gone.";
                case CollectFailure.Dead: return "Down: cannot take items.";
                default: return "Cannot take items now.";
            }
        }
    }

    /// <summary>
    /// A unit's item capability: the rules for using a consumable from its own working bag, and for collecting loot into
    /// it. CommandableUnit runs the orders; this component only answers "may I" and
    /// does the use: validate, heal through Health (the one healing implementation), then consume one unit. Selecting an
    /// item or asking Check never changes anything. Added to the friendly units at spawn, like UnitAbilities.
    /// </summary>
    public sealed class UnitItems : MonoBehaviour
    {
        SquadInventory inventory;
        string operativeId;
        Health health;

        public string OperativeId => operativeId;
        public ItemUseFailure LastUseFailure { get; private set; }
        /// <summary>Unscaled time of the last failure, so the panel can show it for a few seconds even while paused.</summary>
        public float LastUseFailureTime { get; private set; }
        public int UsedCount { get; private set; }
        /// <summary>How many use refusals were recorded; a reader compares counts, never times (two can share a frame).</summary>
        public int UseFailureCount { get; private set; }

        public event Action<ItemDefinition> Used;
        public event Action<ItemUseFailure> UseFailed;

        Health OwnHealth => health != null ? health : health = GetComponent<Health>();

        internal void Bind(SquadInventory items, string operative)
        {
            inventory = items;
            operativeId = operative;
        }

        OperativeLoadout Loadout => inventory != null ? inventory.WorkingLoadout(operativeId) : null;

        /// <summary>
        /// Whether the entry can be used now. `full` false skips the check that depends on where the unit will be and how
        /// hurt it is by then (used for an order queued behind others); execution always checks in full.
        /// </summary>
        public ItemUseFailure Check(string instanceId, bool full = true)
        {
            var own = OwnHealth;
            if (own == null)
                return ItemUseFailure.NotUsable;
            if (!own.IsAlive)
                return ItemUseFailure.Dead;
            var loadout = Loadout;
            if (loadout == null)
                return ItemUseFailure.NoLoadout;
            var entry = loadout.Bag.Find(instanceId);
            if (entry == null)
                return ItemUseFailure.NotOwned;
            var definition = inventory.Catalogue != null ? inventory.Catalogue.Find(entry.DefinitionId) : null;
            if (definition == null || definition.Category != ItemCategory.Consumable || definition.HealAmount < 1)
                return ItemUseFailure.NotUsable;
            if (full && own.Current >= own.Max)
                return ItemUseFailure.NoEffect;
            return ItemUseFailure.None;
        }

        /// <summary>Validates, heals, then consumes one unit. False (with the reason in LastUseFailure) when it cannot, consuming nothing.</summary>
        public bool TryUse(string instanceId)
        {
            var failure = Check(instanceId);
            if (failure != ItemUseFailure.None)
            {
                Record(failure);
                return false;
            }
            var loadout = Loadout;
            var entry = loadout.Bag.Find(instanceId);
            var definition = inventory.Catalogue.Find(entry.DefinitionId);
            var restored = OwnHealth.Heal(definition.HealAmount);
            if (restored <= 0)
            {
                Record(ItemUseFailure.NoEffect);
                return false;
            }
            loadout.Bag.Remove(instanceId, 1);
            UsedCount++;
            Used?.Invoke(definition);
            inventory.Core.NotifyChanged();
            return true;
        }

        /// <summary>How long a swap order runs before the new item is equipped (a prototype value; the unit does nothing else meanwhile).</summary>
        public const float SwapSeconds = 1.5f;

        /// <summary>Whether the entry can be swapped in now: the unit is alive, owns it, it has a slot and is not already equipped.</summary>
        public ItemUseFailure CheckEquip(string instanceId)
        {
            var own = OwnHealth;
            if (own == null || !own.IsAlive)
                return ItemUseFailure.Dead;
            var loadout = Loadout;
            if (loadout == null)
                return ItemUseFailure.NoLoadout;
            var entry = loadout.Bag.Find(instanceId);
            if (entry == null)
                return ItemUseFailure.NotOwned;
            var definition = inventory.Catalogue != null ? inventory.Catalogue.Find(entry.DefinitionId) : null;
            if (definition == null || definition.Slot == ItemSlot.None)
                return ItemUseFailure.NotEquippable;
            if (loadout.Equipment.Contains(instanceId))
                return ItemUseFailure.AlreadyEquipped;
            return ItemUseFailure.None;
        }

        /// <summary>Equips the entry now (the swap order calls this when its time is up). False, with the reason recorded, when it cannot.</summary>
        public bool TryEquip(string instanceId)
        {
            var failure = CheckEquip(instanceId);
            if (failure == ItemUseFailure.None && !inventory.Core.EquipInMission(operativeId, instanceId).Ok)
                failure = ItemUseFailure.NotEquippable;
            if (failure != ItemUseFailure.None)
            {
                RecordEquip(failure);
                return false;
            }
            EquipCount++;
            return true;
        }

        /// <summary>The reason of the last refused or failed swap. Swaps have their own counters so a swap never reads as a use.</summary>
        public ItemUseFailure LastEquipFailure { get; private set; }
        /// <summary>How many swaps finished, and how many were refused or failed; a reader compares counts, never times.</summary>
        public int EquipCount { get; private set; }
        public int EquipFailureCount { get; private set; }

        internal void RecordEquip(ItemUseFailure failure)
        {
            LastEquipFailure = failure;
            EquipFailureCount++;
        }

        public CollectResult LastCollect { get; private set; }
        public float LastCollectTime { get; private set; }
        /// <summary>How many take outcomes were recorded (each sets LastCollect); a reader compares counts, never times.</summary>
        public int CollectCount { get; private set; }
        public event Action<CollectResult> Collected;

        /// <summary>Whether this unit may take from the container now; `requireRange` false is for an order that will walk first.</summary>
        public CollectFailure CheckCollect(LootContainer container, bool requireRange)
        {
            if (container == null)
                return CollectFailure.NoContainer;
            var own = OwnHealth;
            if (own == null || !own.IsAlive)
                return CollectFailure.Dead;
            if (Loadout == null)
                return CollectFailure.NoLoadout;
            if (!container.IsSearched)
                return CollectFailure.NotSearched;
            if (container.IsEmpty)
                return CollectFailure.NothingThere;
            if (requireRange && !InRange(container, transform.position))
                return CollectFailure.OutOfRange;
            return CollectFailure.None;
        }

        public bool InRange(LootContainer container, Vector3 from) =>
            container != null && container.Interactable != null && CoverRules.FlatDistance(from, container.Position) <= container.Interactable.Range;

        /// <summary>
        /// Moves one entry, or every entry that fits, from the container into this unit's bag. Each move re-reads the source
        /// (ItemTransfer), so a second collector cannot duplicate an entry. Never throws for a vanished container, a dead
        /// unit or a full bag: it returns the reason and changes nothing it cannot finish.
        /// </summary>
        public CollectResult TryCollect(LootContainer container, string instanceId)
        {
            var failure = CheckCollect(container, requireRange: true);
            if (failure != CollectFailure.None)
                return RecordCollect(new CollectResult(0, 0, failure));

            var bag = Loadout.Bag;
            var wanted = new System.Collections.Generic.List<string>();
            if (string.IsNullOrEmpty(instanceId))
            {
                foreach (var entry in container.Contents.Entries)
                    wanted.Add(entry.InstanceId);
            }
            else
            {
                wanted.Add(instanceId);
            }

            var taken = 0;
            var missing = 0;
            foreach (var id in wanted)
            {
                var source = container.Contents.Find(id);
                if (source == null)
                {
                    missing++;
                    continue;
                }
                taken += ItemTransfer.Move(container.Contents, bag, id, source.Quantity, inventory.Catalogue).Moved;
            }
            // What is still there is re-read from the container, so the figure is what really remains.
            var left = 0;
            foreach (var id in wanted)
            {
                var still = container.Contents.Find(id);
                if (still != null)
                    left += still.Quantity;
            }
            var outcome = left > 0 ? CollectFailure.BagFull
                : taken == 0 && missing > 0 ? CollectFailure.NothingThere
                : CollectFailure.None;
            if (taken > 0)
            {
                container.NotifyChanged();
                inventory.Core.NotifyChanged();
            }
            return RecordCollect(new CollectResult(taken, left, outcome));
        }

        internal void RecordCollect(CollectFailure failure) => RecordCollect(new CollectResult(0, 0, failure));

        CollectResult RecordCollect(CollectResult result)
        {
            LastCollect = result;
            LastCollectTime = Time.unscaledTime;
            CollectCount++;
            Collected?.Invoke(result);
            return result;
        }

        internal void Record(ItemUseFailure failure)
        {
            LastUseFailure = failure;
            LastUseFailureTime = Time.unscaledTime;
            UseFailureCount++;
            UseFailed?.Invoke(failure);
        }
    }
}
