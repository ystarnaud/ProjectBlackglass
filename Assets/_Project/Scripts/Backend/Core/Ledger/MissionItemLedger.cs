using System;
using System.Collections.Generic;
using System.Linq;

namespace Blackglass.Backend.Core
{
    public enum LedgerEventResult
    {
        Applied,
        /// <summary>The operative's participant is frozen: its facts don't change (a collect still lowers the container line).</summary>
        Frozen,
        /// <summary>Refused: an integrity failure was recorded. A use beyond holdings still consumed what was held.</summary>
        Refused,
    }

    public sealed class LedgerCheck
    {
        internal LedgerCheck(IList<string> codes) => Codes = codes.Distinct().ToList();

        public bool Ok => Codes.Count == 0;
        public IReadOnlyList<string> Codes { get; }
    }

    /// <summary>
    /// The mission's item provenance (spec §8.2): it never reads a Unity object. Stage 12 tells it what the runtime applied
    /// (collect, use, equip) and hands it plain copies of the runtime inventories to verify against.
    /// - An operative's bag holds owned items (one per snapshot item id; the runtime entry id is the Base58 item id), one owned
    ///   stack per definition, and loot lots, one per (container, line), each with the event sequence of its first collect.
    /// - A container holds one line per entry right after its fill (line = the entry's index), tracked by entry id.
    /// - A use takes loot lots of that definition first, oldest first by (sequence, container, line), then the owned stack.
    /// - Integrity is per participant and sticky; a known failure can never come out as Ok. Facts stay well-formed after one.
    /// - Freeze verifies the participant's runtime state (fail closed) and snapshots its facts; later events change no fact.
    /// Not thread-safe: the simulation thread is its only user.
    /// </summary>
    public sealed class MissionItemLedger
    {
        public const int MaxFailureCodes = 32;

        sealed class Lot
        {
            public string DefinitionId;
            public int Quantity;
            public long Sequence;
        }

        sealed class EquipRef
        {
            public string ItemId;
            public (int ContainerIndex, int Line) Line;
        }

        sealed class Operative
        {
            public string Id;
            public string AccountId;
            public int Slot;
            public readonly Dictionary<string, string> OwnedItems = new Dictionary<string, string>(StringComparer.Ordinal); // item id → definition
            public readonly Dictionary<string, int> OwnedStacks = new Dictionary<string, int>(StringComparer.Ordinal);
            public readonly Dictionary<string, int> ConsumedOwned = new Dictionary<string, int>(StringComparer.Ordinal);
            public readonly Dictionary<(int, int), Lot> Lots = new Dictionary<(int, int), Lot>();
            public readonly Dictionary<(int, int), int> Collected = new Dictionary<(int, int), int>();
            public readonly Dictionary<(int, int), int> ConsumedLoot = new Dictionary<(int, int), int>();
            public readonly Dictionary<string, (int, int)> CollectedItems = new Dictionary<string, (int, int)>(StringComparer.Ordinal); // entry id → line
            public readonly Dictionary<EquipSlot, EquipRef> Equipment = new Dictionary<EquipSlot, EquipRef>();
        }

        sealed class Participant
        {
            public string AccountId;
            public readonly List<Operative> Operatives = new List<Operative>();
            public readonly List<string> FailureCodes = new List<string>();
            public bool Mismatch;
            public ParticipantFacts Frozen;
        }

        sealed class Line
        {
            public int Index;
            public string EntryId;
            public string DefinitionId;
            public int Initial;
            public int Remaining;
        }

        readonly LedgerCatalogue catalogue;
        readonly Dictionary<string, Participant> participants = new Dictionary<string, Participant>(StringComparer.Ordinal);
        readonly Dictionary<string, Operative> operatives = new Dictionary<string, Operative>(StringComparer.Ordinal);
        readonly SortedDictionary<int, List<Line>> containers = new SortedDictionary<int, List<Line>>();
        readonly HashSet<string> entryIds = new HashSet<string>(StringComparer.Ordinal);
        readonly HashSet<string> itemIds = new HashSet<string>(StringComparer.Ordinal);
        readonly HashSet<int> slots = new HashSet<int>();
        long sequence;

        public MissionItemLedger(LedgerCatalogue catalogue) => this.catalogue = catalogue ?? throw new ArgumentNullException(nameof(catalogue));

        // ---- setup ----

        /// <summary>A participant and its admission snapshot. A malformed snapshot (a backend invariant failure) is an ArgumentException.</summary>
        public void AddParticipant(string accountId, IReadOnlyList<OperativeSnapshot> snapshot)
        {
            if (!Base58Id.TryParse(accountId, out _)) throw new ArgumentException("The account id is not canonical Base58.", nameof(accountId));
            if (participants.ContainsKey(accountId)) throw new ArgumentException("The participant is already registered.", nameof(accountId));
            if (snapshot == null || snapshot.Count == 0 || snapshot.Count > ResultManifestV1.MaxOperatives)
                throw new ArgumentException("A participant has 1 to 8 operatives.", nameof(snapshot));
            var newOperatives = new List<Operative>();
            var newItems = new HashSet<string>(StringComparer.Ordinal);
            var newSlots = new HashSet<int>();
            foreach (var s in snapshot)
            {
                if (s == null || !Base58Id.TryParse(s.OperativeId, out _)) throw Bad("an operative id is not canonical Base58");
                if (operatives.ContainsKey(s.OperativeId) || newOperatives.Any(o => o.Id == s.OperativeId)) throw Bad("an operative id repeats");
                if (s.Slot < 0 || s.Slot >= ResultManifestV1.MaxOperatives) throw Bad("a slot is outside 0..7");
                if (slots.Contains(s.Slot) || !newSlots.Add(s.Slot)) throw Bad("a slot repeats");
                var o = new Operative { Id = s.OperativeId, AccountId = accountId, Slot = s.Slot };
                foreach (var item in s.Items)
                {
                    if (!Base58Id.TryParse(item.ItemId, out _)) throw Bad("an item id is not canonical Base58");
                    if (itemIds.Contains(item.ItemId) || entryIds.Contains(item.ItemId) || !newItems.Add(item.ItemId)) throw Bad("an item id repeats");
                    if (!catalogue.TryGet(item.DefinitionId, out var d)) throw Bad("an item's definition is unknown");
                    if (d.Stackable) throw Bad("an item's definition is stackable");
                    o.OwnedItems.Add(item.ItemId, item.DefinitionId);
                }
                foreach (var stack in s.Stacks)
                {
                    if (!catalogue.TryGet(stack.DefinitionId, out var d)) throw Bad("a stack's definition is unknown");
                    if (!d.Stackable) throw Bad("a stack's definition is not stackable");
                    if (stack.Quantity < 1) throw Bad("a stack holds less than one");
                    if (o.OwnedStacks.ContainsKey(stack.DefinitionId)) throw Bad("two stacks of one definition");
                    o.OwnedStacks.Add(stack.DefinitionId, stack.Quantity);
                }
                foreach (EquipSlot slot in Enum.GetValues(typeof(EquipSlot)))
                {
                    var itemId = s.EquippedIn(slot);
                    if (itemId == null) continue;
                    if (!o.OwnedItems.TryGetValue(itemId, out var definition)) throw Bad("an equipped item is not the operative's");
                    catalogue.TryGet(definition, out var d);
                    if (d.Slot != slot) throw Bad("an item is equipped in another slot than its definition's");
                    o.Equipment[slot] = new EquipRef { ItemId = itemId };
                }
                newOperatives.Add(o);
            }
            var p = new Participant { AccountId = accountId };
            foreach (var o in newOperatives.OrderBy(o => o.Slot))
            {
                p.Operatives.Add(o);
                operatives.Add(o.Id, o);
                slots.Add(o.Slot);
            }
            foreach (var id in newItems) itemIds.Add(id);
            participants.Add(accountId, p);
        }

        static ArgumentException Bad(string why) => new ArgumentException("Malformed snapshot: " + why + ".");

        /// <summary>
        /// A container's entries right after its fill, in roll order: an online attempt fills one entry per successful roll
        /// (LootContainer.InitializeForAttempt, gate B25), so line i is roll i. Never registered from a merged fill.
        /// </summary>
        public void RegisterContainer(int containerIndex, IReadOnlyList<ContainerEntry> entriesAfterFill)
        {
            if (containerIndex < 0 || containerIndex > ResultManifestV1.MaxContainerOrLine) throw new ArgumentException("The container index is outside 0..65535.", nameof(containerIndex));
            if (containers.ContainsKey(containerIndex)) throw new ArgumentException("The container is already registered.", nameof(containerIndex));
            if (entriesAfterFill == null || entriesAfterFill.Count > ResultManifestV1.MaxContainerOrLine + 1) throw new ArgumentException("Too many entries.", nameof(entriesAfterFill));
            var lines = new List<Line>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < entriesAfterFill.Count; i++)
            {
                var e = entriesAfterFill[i];
                if (e == null || e.EntryId.Length == 0 || entryIds.Contains(e.EntryId) || itemIds.Contains(e.EntryId) || !ids.Add(e.EntryId))
                    throw new ArgumentException("Container entry ids must be unique and never an owned item id.", nameof(entriesAfterFill));
                if (!catalogue.TryGet(e.DefinitionId, out var d)) throw new ArgumentException("A container entry's definition is unknown.", nameof(entriesAfterFill));
                if (e.Quantity < 1 || (!d.Stackable && e.Quantity != 1)) throw new ArgumentException("A container entry's quantity is invalid.", nameof(entriesAfterFill));
                lines.Add(new Line { Index = i, EntryId = e.EntryId, DefinitionId = e.DefinitionId, Initial = e.Quantity, Remaining = e.Quantity });
            }
            foreach (var id in ids) entryIds.Add(id);
            containers.Add(containerIndex, lines);
        }

        /// <summary>The registered pool, sorted by (container, line): the ready report's lootPool and LootPoolHash's input.</summary>
        public IReadOnlyList<PoolLine> PoolLines() =>
            containers.SelectMany(c => c.Value.Select(l => new PoolLine(c.Key, l.Index, l.DefinitionId, l.Initial))).ToList();

        public bool IsFrozen(string accountId) => participants.TryGetValue(accountId ?? "", out var p) && p.Frozen != null;

        // ---- events ----

        /// <summary>Units the runtime moved from a container entry into an operative's bag (ItemTransfer.Move's Moved).</summary>
        public LedgerEventResult Collect(string operativeId, int containerIndex, string entryId, int quantity)
        {
            if (!TryOperative(operativeId, out var o, out var p)) return LedgerEventResult.Refused;
            var line = containers.TryGetValue(containerIndex, out var lines) ? lines.FirstOrDefault(l => l.EntryId == entryId) : null;
            if (line == null) return Refuse(p, "unknown_container_entry");
            if (quantity < 1) return Refuse(p, "quantity_invalid");
            if (quantity > line.Remaining) return Refuse(p, "line_exceeded");
            line.Remaining -= quantity; // shared world state: it follows real takes even after this participant's freeze
            if (p.Frozen != null) return LedgerEventResult.Frozen;
            sequence++;
            var key = (containerIndex, line.Index);
            if (o.Lots.TryGetValue(key, out var lot)) lot.Quantity += quantity;
            else o.Lots.Add(key, new Lot { DefinitionId = line.DefinitionId, Quantity = quantity, Sequence = sequence });
            o.Collected[key] = (o.Collected.TryGetValue(key, out var c) ? c : 0) + quantity;
            catalogue.TryGet(line.DefinitionId, out var d);
            if (!d.Stackable) o.CollectedItems[entryId] = key;
            return LedgerEventResult.Applied;
        }

        /// <summary>Units of a consumable the runtime consumed (UnitItems.Used): loot first, oldest first, then owned.</summary>
        public LedgerEventResult Use(string operativeId, string definitionId, int quantity = 1)
        {
            if (!TryOperative(operativeId, out var o, out var p)) return LedgerEventResult.Refused;
            if (p.Frozen != null) return LedgerEventResult.Frozen;
            if (!catalogue.TryGet(definitionId, out var d) || !d.Stackable) return Refuse(p, "use_not_stackable");
            if (!d.Consumable) return Refuse(p, "use_not_consumable");
            if (quantity < 1) return Refuse(p, "quantity_invalid");
            sequence++;
            var left = quantity;
            var lots = o.Lots.Where(l => l.Value.DefinitionId == definitionId && l.Value.Quantity > 0)
                .OrderBy(l => l.Value.Sequence).ThenBy(l => l.Key.Item1).ThenBy(l => l.Key.Item2).ToList();
            foreach (var pair in lots)
            {
                var n = Math.Min(left, pair.Value.Quantity);
                pair.Value.Quantity -= n;
                o.ConsumedLoot[pair.Key] = (o.ConsumedLoot.TryGetValue(pair.Key, out var used) ? used : 0) + n;
                left -= n;
                if (left == 0) break;
            }
            if (left > 0 && o.OwnedStacks.TryGetValue(definitionId, out var owned) && owned > 0)
            {
                var n = Math.Min(left, owned);
                o.OwnedStacks[definitionId] = owned - n;
                o.ConsumedOwned[definitionId] = (o.ConsumedOwned.TryGetValue(definitionId, out var used) ? used : 0) + n;
                left -= n;
            }
            return left > 0 ? Refuse(p, "use_exceeds_held") : LedgerEventResult.Applied;
        }

        /// <summary>An entry the runtime equipped (UnitItems.TryEquip succeeded). The slot is the definition's; the old occupant stays held.</summary>
        public LedgerEventResult Equip(string operativeId, string entryId)
        {
            if (!TryOperative(operativeId, out var o, out var p)) return LedgerEventResult.Refused;
            if (p.Frozen != null) return LedgerEventResult.Frozen;
            string definition;
            EquipRef target;
            if (entryId != null && o.OwnedItems.TryGetValue(entryId, out definition)) target = new EquipRef { ItemId = entryId };
            else if (entryId != null && o.CollectedItems.TryGetValue(entryId, out var line) && o.Lots[line].Quantity > 0)
            {
                definition = o.Lots[line].DefinitionId;
                target = new EquipRef { Line = line };
            }
            else return Refuse(p, "equip_not_held");
            catalogue.TryGet(definition, out var d);
            if (!d.Slot.HasValue) return Refuse(p, "equip_no_slot");
            sequence++;
            o.Equipment[d.Slot.Value] = target;
            return LedgerEventResult.Applied;
        }

        bool TryOperative(string operativeId, out Operative o, out Participant p)
        {
            p = null;
            if (operativeId != null && operatives.TryGetValue(operativeId, out o))
            {
                p = participants[o.AccountId];
                return true;
            }
            o = null;
            foreach (var each in participants.Values) Mark(each, "unknown_operative"); // the event names nobody known: everyone is suspect
            return false;
        }

        LedgerEventResult Refuse(Participant p, string code)
        {
            Mark(p, code);
            return LedgerEventResult.Refused;
        }

        static void Mark(Participant p, string code)
        {
            if (p.Frozen != null) return;
            p.Mismatch = true;
            if (!p.FailureCodes.Contains(code) && p.FailureCodes.Count < MaxFailureCodes) p.FailureCodes.Add(code);
        }

        static bool IsCode(string code) => code != null && code.Length >= 1 && code.Length <= 40 && code.All(c => (c >= 'a' && c <= 'z') || c == '_');

        /// <summary>A failure the server detected elsewhere (for example a runtime inconsistency); marks only that participant.</summary>
        public void ReportIntegrityFailure(string accountId, string code)
        {
            if (!IsCode(code)) throw new ArgumentException("A failure code is [a-z_]{1,40}.", nameof(code));
            if (accountId == null || !participants.TryGetValue(accountId, out var p)) throw new ArgumentException("Unknown participant.", nameof(accountId));
            if (p.Frozen != null) throw new InvalidOperationException("The participant is frozen: its facts can no longer change.");
            Mark(p, code);
        }

        // ---- verification ----

        public LedgerCheck VerifyOperative(string operativeId, IReadOnlyList<RuntimeEntry> bag, RuntimeEquipment equipment)
        {
            if (!TryOperative(operativeId, out var o, out var p)) return new LedgerCheck(new[] { "unknown_operative" });
            var codes = CheckOperative(o, bag ?? throw new ArgumentNullException(nameof(bag)), equipment ?? throw new ArgumentNullException(nameof(equipment)));
            foreach (var code in codes) Mark(p, code);
            return new LedgerCheck(codes);
        }

        List<string> CheckOperative(Operative o, IReadOnlyList<RuntimeEntry> bag, RuntimeEquipment equipment)
        {
            var codes = new List<string>();
            var expected = new Dictionary<string, long>(StringComparer.Ordinal);
            void Add(string definition, long n) => expected[definition] = (expected.TryGetValue(definition, out var had) ? had : 0) + n;
            foreach (var item in o.OwnedItems) Add(item.Value, 1);
            foreach (var stack in o.OwnedStacks) Add(stack.Key, stack.Value);
            foreach (var lot in o.Lots) Add(lot.Value.DefinitionId, lot.Value.Quantity);
            var actual = new Dictionary<string, long>(StringComparer.Ordinal);
            var runtimeItems = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in bag)
            {
                actual[e.DefinitionId] = (actual.TryGetValue(e.DefinitionId, out var had) ? had : 0) + e.Quantity;
                if (!catalogue.TryGet(e.DefinitionId, out var d)) { codes.Add("bag_unknown_definition"); continue; }
                if (d.Stackable) continue;
                runtimeItems.Add(e.EntryId);
                if (!o.OwnedItems.ContainsKey(e.EntryId) && !HeldCollectedItem(o, e.EntryId)) codes.Add("bag_item_unmapped");
            }
            foreach (var definition in expected.Keys.Concat(actual.Keys).Distinct())
                if ((expected.TryGetValue(definition, out var x) ? x : 0) != (actual.TryGetValue(definition, out var y) ? y : 0)) codes.Add("bag_count_mismatch");
            foreach (var id in o.OwnedItems.Keys.Concat(o.CollectedItems.Keys.Where(id => HeldCollectedItem(o, id))))
                if (!runtimeItems.Contains(id)) codes.Add("bag_item_missing");
            foreach (EquipSlot slot in Enum.GetValues(typeof(EquipSlot)))
            {
                var runtime = equipment.In(slot);
                string ledger = null;
                if (o.Equipment.TryGetValue(slot, out var r))
                    ledger = r.ItemId ?? o.CollectedItems.Where(c => c.Value.Equals(r.Line)).Select(c => c.Key).FirstOrDefault();
                if (!string.Equals(runtime, ledger, StringComparison.Ordinal)) codes.Add("equipment_mismatch");
            }
            return codes.Distinct().ToList();
        }

        static bool HeldCollectedItem(Operative o, string entryId) => o.CollectedItems.TryGetValue(entryId, out var line) && o.Lots[line].Quantity > 0;

        public LedgerCheck VerifyContainer(int containerIndex, IReadOnlyList<RuntimeEntry> contents)
        {
            var codes = CheckContainer(containerIndex, contents ?? throw new ArgumentNullException(nameof(contents)));
            foreach (var code in codes)
                foreach (var p in participants.Values) Mark(p, code); // shared world state: every unfrozen participant is suspect
            return new LedgerCheck(codes);
        }

        List<string> CheckContainer(int containerIndex, IReadOnlyList<RuntimeEntry> contents)
        {
            if (!containers.TryGetValue(containerIndex, out var lines)) return new List<string> { "unknown_container" };
            var codes = new List<string>();
            var byId = new Dictionary<string, RuntimeEntry>(StringComparer.Ordinal);
            foreach (var e in contents)
            {
                if (byId.ContainsKey(e.EntryId) || !lines.Any(l => l.EntryId == e.EntryId)) { codes.Add("container_extra_entry"); continue; }
                byId.Add(e.EntryId, e);
            }
            foreach (var l in lines)
            {
                var present = byId.TryGetValue(l.EntryId, out var e);
                if (l.Remaining == 0 ? present : !present || e.Quantity != l.Remaining || e.DefinitionId != l.DefinitionId) codes.Add("container_line_mismatch");
            }
            return codes.Distinct().ToList();
        }

        // ---- the freeze ----

        /// <summary>
        /// The participant's outcome freeze: verifies each of its operatives and every registered container against the runtime
        /// state passed in (a missing state is a failure: fail closed), then snapshots its facts. Idempotent: a second call
        /// returns the same facts without verifying again.
        /// </summary>
        public ParticipantFacts Freeze(string accountId, IReadOnlyList<OperativeRuntimeState> operativeStates, IReadOnlyList<ContainerRuntimeState> containerStates)
        {
            if (accountId == null || !participants.TryGetValue(accountId, out var p)) throw new ArgumentException("Unknown participant.", nameof(accountId));
            if (p.Frozen != null) return p.Frozen;
            if (operativeStates == null) throw new ArgumentNullException(nameof(operativeStates));
            if (containerStates == null) throw new ArgumentNullException(nameof(containerStates));
            foreach (var o in p.Operatives)
            {
                var state = operativeStates.FirstOrDefault(s => s != null && s.OperativeId == o.Id);
                if (state == null) { Mark(p, "operative_state_missing"); continue; }
                foreach (var code in CheckOperative(o, state.Bag, state.Equipment)) Mark(p, code);
            }
            foreach (var index in containers.Keys)
            {
                var state = containerStates.FirstOrDefault(s => s != null && s.ContainerIndex == index);
                if (state == null) { Mark(p, "container_state_missing"); continue; }
                foreach (var code in CheckContainer(index, state.Entries))
                    foreach (var each in participants.Values) Mark(each, code);
            }
            p.Frozen = new ParticipantFacts(p.AccountId, p.Mismatch ? ManifestIntegrity.LedgerMismatch : ManifestIntegrity.Ok, p.FailureCodes,
                p.Operatives.Select(FactsOf).ToList());
            return p.Frozen;
        }

        static OperativeFacts FactsOf(Operative o)
        {
            var equipment = o.Equipment.OrderBy(e => e.Key)
                .Select(e => e.Value.ItemId != null
                    ? new LedgerEquipment(e.Key, e.Value.ItemId, null, null)
                    : new LedgerEquipment(e.Key, null, e.Value.Line.ContainerIndex, e.Value.Line.Line))
                .ToList();
            return new OperativeFacts(o.Id, o.Slot,
                o.ConsumedOwned.Where(c => c.Value > 0).OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => new OwnedUse(c.Key, c.Value)).ToList(),
                o.ConsumedLoot.Where(c => c.Value > 0).OrderBy(c => c.Key.Item1).ThenBy(c => c.Key.Item2)
                    .Select(c => new LootUse(c.Key.Item1, c.Key.Item2, o.Lots[c.Key].DefinitionId, c.Value)).ToList(),
                o.OwnedItems.Keys.OrderBy(id => id.Length).ThenBy(id => id, StringComparer.Ordinal).ToList(),
                o.OwnedStacks.Where(s => s.Value > 0).OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => new OwnedStackFact(s.Key, s.Value)).ToList(),
                o.Lots.Where(l => l.Value.Quantity > 0).OrderBy(l => l.Key.Item1).ThenBy(l => l.Key.Item2)
                    .Select(l => new LootLotFact(l.Key.Item1, l.Key.Item2, l.Value.Quantity)).ToList(),
                o.Collected.Where(c => c.Value > 0).OrderBy(c => c.Key.Item1).ThenBy(c => c.Key.Item2)
                    .Select(c => new LineCount(c.Key.Item1, c.Key.Item2, c.Value)).ToList(),
                equipment);
        }
    }
}
