using System;
using System.Collections.Generic;

namespace Blackglass
{
    public enum InventoryMode
    {
        /// <summary>No mission is running: equipment and the stash can be edited.</summary>
        Loadout,
        /// <summary>A mission working state exists: pickups and item use change it; equipment is locked.</summary>
        InMission,
    }

    /// <summary>
    /// The inventory rules, with no scene dependency. Holds the persistent session state and, while a mission runs, a deep
    /// copy of it (the working state). Pickups and consumable use change only the working state; a Success settlement
    /// replaces the session with it exactly once, and a failure, an abort or a restart discards it. Equipment and stash
    /// edits act on the session and are refused while a mission runs. Nothing here touches XP or progression.
    /// </summary>
    public sealed class InventorySession
    {
        public const string LockedReason = "Unequipping and the stash are locked during a mission.";

        readonly ItemCatalogue catalogue;
        readonly int bagCapacity;
        readonly HashSet<string> seededOperatives = new HashSet<string>();
        readonly Dictionary<string, Dictionary<string, int>> startCounts = new Dictionary<string, Dictionary<string, int>>();
        bool stashSeeded;

        public InventorySession(ItemCatalogue catalogue, int bagCapacity)
        {
            if (bagCapacity < 0)
                throw new ArgumentOutOfRangeException(nameof(bagCapacity), bagCapacity, "A bag cannot have a negative limit.");
            this.catalogue = catalogue;
            this.bagCapacity = bagCapacity;
            Session = new InventoryState();
        }

        public ItemCatalogue Catalogue => catalogue;
        public int BagCapacity => bagCapacity;
        /// <summary>The persistent state. Replaced (never mutated by pickups) when a mission settles.</summary>
        public InventoryState Session { get; private set; }
        /// <summary>The running mission's copy, or null outside a mission.</summary>
        public InventoryState Working { get; private set; }
        /// <summary>The working state during a mission, else the session: what the player is looking at.</summary>
        public InventoryState Active => Working ?? Session;
        public InventoryMode Mode => Working != null ? InventoryMode.InMission : InventoryMode.Loadout;
        public string ActiveMissionId { get; private set; }

        /// <summary>Raised after any change to the session or the working state made through this class.</summary>
        public event Action Changed;

        /// <summary>Raised with the operative's id after what they have equipped changed, so a live unit can re-apply its gear.</summary>
        public event Action<string> EquipmentChanged;

        public void NotifyChanged() => Changed?.Invoke();

        public void EnsureOperative(string operativeId)
        {
            if (Session.Loadout(operativeId) == null)
            {
                Session.EnsureLoadout(operativeId, bagCapacity);
                Changed?.Invoke();
            }
        }

        // ---- mission boundary ----

        /// <summary>Starts a mission: the working state is a deep copy of the session. A previous unsettled mission is discarded.</summary>
        public bool BeginMission(string missionId)
        {
            if (string.IsNullOrWhiteSpace(missionId))
                throw new ArgumentException("A mission needs an id.", nameof(missionId));
            Working = Session.Clone();
            ActiveMissionId = missionId;
            RecordStart();
            Changed?.Invoke();
            return true;
        }

        /// <summary>Drops the working state (generation failed, restart, clear). The session is untouched.</summary>
        public void AbortMission()
        {
            if (Working == null)
                return;
            Working = null;
            ActiveMissionId = null;
            startCounts.Clear();
            Changed?.Invoke();
        }

        /// <summary>
        /// Ends the mission with this id. Success replaces the session with the working state; anything else discards it.
        /// Returns true only when items were committed. A call for any other id, or a second call, does nothing.
        /// </summary>
        public bool Settle(string missionId, bool success)
        {
            if (Working == null || string.IsNullOrEmpty(missionId) || missionId != ActiveMissionId)
                return false;
            if (success)
                Session = Working;
            Working = null;
            ActiveMissionId = null;
            startCounts.Clear();
            Changed?.Invoke();
            return success;
        }

        void RecordStart()
        {
            startCounts.Clear();
            foreach (var loadout in Working.Loadouts)
            {
                var counts = new Dictionary<string, int>();
                foreach (var entry in loadout.Bag.Entries)
                {
                    counts.TryGetValue(entry.DefinitionId, out var current);
                    counts[entry.DefinitionId] = current + entry.Quantity;
                }
                startCounts[loadout.OperativeId] = counts;
            }
        }

        /// <summary>Units of this definition the operative carries beyond what it started the mission with (not yet secured). 0 outside a mission.</summary>
        public int UnsecuredCount(string operativeId, string definitionId)
        {
            if (Working == null)
                return 0;
            var loadout = Working.Loadout(operativeId);
            if (loadout == null)
                return 0;
            var start = 0;
            if (startCounts.TryGetValue(operativeId, out var counts))
                counts.TryGetValue(definitionId, out start);
            return Math.Max(0, loadout.Bag.CountOf(definitionId) - start);
        }

        // ---- loadout edits (session only, closed during a mission) ----

        public EditResult Equip(string operativeId, string instanceId)
        {
            if (Mode == InventoryMode.InMission)
                return EditResult.Fail(LockedReason);
            var loadout = Session.Loadout(operativeId);
            if (loadout == null)
                return EditResult.Fail("Unknown operative.");
            var result = loadout.Equipment.TryEquip(loadout.Bag, catalogue, instanceId);
            if (result.Ok)
            {
                Changed?.Invoke();
                EquipmentChanged?.Invoke(operativeId);
            }
            return result;
        }

        /// <summary>
        /// Equips an item from the operative's own bag in the running mission (the working state only; the session is
        /// untouched until a success settles). The timed swap order calls this when its time is up. Everything else about
        /// equipment stays locked in a mission: no unequipping and no stash moves.
        /// </summary>
        public EditResult EquipInMission(string operativeId, string instanceId)
        {
            if (Mode != InventoryMode.InMission)
                return EditResult.Fail("No mission is running.");
            var loadout = Working.Loadout(operativeId);
            if (loadout == null)
                return EditResult.Fail("Unknown operative.");
            var result = loadout.Equipment.TryEquip(loadout.Bag, catalogue, instanceId);
            if (result.Ok)
            {
                Changed?.Invoke();
                EquipmentChanged?.Invoke(operativeId);
            }
            return result;
        }

        public EditResult Unequip(string operativeId, ItemSlot slot)
        {
            if (Mode == InventoryMode.InMission)
                return EditResult.Fail(LockedReason);
            var loadout = Session.Loadout(operativeId);
            if (loadout == null)
                return EditResult.Fail("Unknown operative.");
            if (!loadout.Equipment.Unequip(slot))
                return EditResult.Fail("Nothing is equipped there.");
            Changed?.Invoke();
            return EditResult.Success;
        }

        public TransferResult ToStash(string operativeId, string instanceId, int quantity)
        {
            if (Mode == InventoryMode.InMission)
                return TransferResult.Failed(TransferFailure.Locked, quantity);
            var result = Session.MoveToStash(operativeId, instanceId, quantity, catalogue);
            if (result.Moved > 0)
                Changed?.Invoke();
            return result;
        }

        public TransferResult FromStash(string operativeId, string instanceId, int quantity)
        {
            if (Mode == InventoryMode.InMission)
                return TransferResult.Failed(TransferFailure.Locked, quantity);
            var result = Session.MoveToBag(operativeId, instanceId, quantity, catalogue);
            if (result.Moved > 0)
                Changed?.Invoke();
            return result;
        }

        /// <summary>
        /// Moves an equippable item from the stash into the operative's bag and equips it, as one step: either both happen or
        /// nothing does. A replaced item stays in the bag. Refused during a mission, for an item with no slot, and for a full bag.
        /// </summary>
        public EditResult EquipFromStash(string operativeId, string stashInstanceId)
        {
            if (Mode == InventoryMode.InMission)
                return EditResult.Fail(LockedReason);
            var loadout = Session.Loadout(operativeId);
            if (loadout == null)
                return EditResult.Fail("Unknown operative.");
            var entry = Session.Stash.Find(stashInstanceId);
            if (entry == null)
                return EditResult.Fail("That item is no longer in the stash.");
            var definition = catalogue != null ? catalogue.Find(entry.DefinitionId) : null;
            if (definition == null)
                return EditResult.Fail($"Unknown item '{entry.DefinitionId}' cannot be equipped.");
            if (definition.Slot == ItemSlot.None || definition.IsStackable)
                return EditResult.Fail($"{definition.DisplayName} can't be equipped.");
            if (loadout.Bag.RoomFor(definition) < 1)
                return EditResult.Fail("The bag is full.");
            var moved = Session.MoveToBag(operativeId, stashInstanceId, 1, catalogue);
            if (!moved.Succeeded)
                return EditResult.Fail(moved.Describe(definition.DisplayName));
            var equipped = loadout.Equipment.TryEquip(loadout.Bag, catalogue, stashInstanceId);
            if (!equipped.Ok)
                Session.MoveToStash(operativeId, stashInstanceId, 1, catalogue);   // undo: the item goes back where it was
            if (equipped.Ok)
                EquipmentChanged?.Invoke(operativeId);
            Changed?.Invoke();
            return equipped;
        }

        public EquippedItems EquippedFor(InventoryState state, string operativeId)
        {
            var loadout = state != null ? state.Loadout(operativeId) : null;
            return loadout != null ? loadout.Resolve(catalogue) : default;
        }

        // ---- starter seeding ----

        /// <summary>
        /// Grants the starter items once per operative id (and the stash once): calling it again, or after a mission,
        /// never grants anything twice. A grant that does not fit, or an item that is missing, adds a problem message.
        /// </summary>
        public void SeedStarter(StarterLoadout starter, IEnumerable<string> operativeIds, List<string> problems)
        {
            if (starter == null)
                return;
            foreach (var id in operativeIds)
            {
                if (string.IsNullOrWhiteSpace(id) || !seededOperatives.Add(id))
                    continue;
                var loadout = Session.EnsureLoadout(id, bagCapacity);
                foreach (var grant in starter.For(id))
                {
                    if (grant.item == null)
                    {
                        problems.Add($"Starter loadout for {id}: a grant has no item.");
                        continue;
                    }
                    var leftover = loadout.Bag.Add(grant.item, Math.Max(1, grant.quantity));
                    if (leftover > 0)
                        problems.Add($"Starter loadout for {id}: no room for {leftover} x {grant.item.DisplayName} ({grant.item.Id}).");
                    if (!grant.equip)
                        continue;
                    var entry = FirstOf(loadout.Bag, grant.item.Id);
                    if (entry == null)
                    {
                        problems.Add($"Starter loadout for {id}: could not equip {grant.item.DisplayName} ({grant.item.Id}).");
                        continue;
                    }
                    var equipped = loadout.Equipment.TryEquip(loadout.Bag, catalogue, entry.InstanceId);
                    if (!equipped.Ok)
                        problems.Add($"Starter loadout for {id}: {equipped.Reason}");
                }
            }
            if (!stashSeeded)
            {
                stashSeeded = true;
                foreach (var grant in starter.Stash)
                {
                    if (grant.item == null)
                    {
                        problems.Add("Starter loadout: a stash grant has no item.");
                        continue;
                    }
                    Session.Stash.Add(grant.item, Math.Max(1, grant.quantity));
                }
            }
            Changed?.Invoke();
        }

        static ItemEntry FirstOf(ItemInventory bag, string definitionId)
        {
            foreach (var entry in bag.Entries)
            {
                if (entry.DefinitionId == definitionId)
                    return entry;
            }
            return null;
        }
    }
}
