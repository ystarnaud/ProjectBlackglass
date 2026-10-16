using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using static Blackglass.Tests.ContractVectors;

namespace Blackglass.Tests
{
    /// <summary>
    /// Runs the shared inventory vectors (Backend/contracts/inventory, scope "shared") against InventorySession, one NUnit case
    /// per vector case. The backend's xUnit InventoryVectorTests run the same file, shared and backend cases, against
    /// InventoryEditor. Scope "backend" cases cover what the owner ruled differently for the backend (exact transfers, retired
    /// items, over-capacity bags) and are never run here; Unity's offline partial transfers stay as they are.
    ///
    /// The runner is an adapter, not a rule change. A stack operation in a vector names a definition and a quantity; Unity
    /// addresses entries, so the runner moves units from the definition's last entry first, over as many calls as it takes, and
    /// stops at the first failure. Unity applies operations one at a time and has no batch rollback, so a refused case compares
    /// the code and index only (atomicity is a backend property, tested there).
    /// </summary>
    public class InventoryContractTests
    {
        const string ScopeShared = "shared";
        const string ScopeBackend = "backend";

        static List<Dictionary<string, object>> AllCases() => Arr(Load("inventory", "vectors.json")["cases"]).Select(Obj).ToList();

        public static IEnumerable<TestCaseData> SharedCases()
        {
            foreach (var c in AllCases())
            {
                if (c.TryGetValue("scope", out var scope) && Str(scope) == ScopeShared)
                    yield return new TestCaseData(c).SetName(Str(c["name"]));
            }
        }

        readonly List<UnityEngine.Object> created = new List<UnityEngine.Object>();
        readonly Dictionary<string, ItemDefinition> definitions = new Dictionary<string, ItemDefinition>(StringComparer.Ordinal);
        readonly Dictionary<string, string> labelToInstance = new Dictionary<string, string>(StringComparer.Ordinal);
        readonly Dictionary<string, string> instanceToLabel = new Dictionary<string, string>(StringComparer.Ordinal);
        InventorySession session;
        string caseName;

        [SetUp]
        public void SetUp()
        {
            definitions.Clear();
            labelToInstance.Clear();
            instanceToLabel.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var asset in created)
            {
                if (asset != null)
                    UnityEngine.Object.DestroyImmediate(asset);
            }
            created.Clear();
        }

        [Test]
        public void The_file_has_known_scopes_and_the_pinned_case_counts()
        {
            var cases = AllCases();
            var names = new HashSet<string>();
            var shared = 0;
            var backend = 0;
            foreach (var c in cases)
            {
                var name = Str(c["name"]);
                Assert.That(names.Add(name), Is.True, "duplicate case name " + name);
                Assert.That(c.ContainsKey("scope"), Is.True, name + ": a case needs a scope");
                var scope = Str(c["scope"]);
                Assert.That(scope, Is.EqualTo(ScopeShared).Or.EqualTo(ScopeBackend), name + ": scope must be shared or backend");
                if (scope == ScopeShared)
                    shared++;
                else
                    backend++;
            }
            // Exact pins: moving a case between scopes, or adding or removing one, must update these numbers (and the same
            // pins in the backend InventoryVectorTests) deliberately, in the same change.
            Assert.That(shared, Is.EqualTo(54), "the shared vector count changed: a reclassification or an added or removed case must update this pin deliberately");
            Assert.That(backend, Is.EqualTo(35), "the backend-only vector count changed: a reclassification or an added or removed case must update this pin deliberately");
        }

        [TestCaseSource(nameof(SharedCases))]
        public void Shared_case_matches_InventorySession(Dictionary<string, object> c)
        {
            caseName = Str(c["name"]);
            Assert.That(Str(c["scope"]), Is.EqualTo(ScopeShared));
            var capacity = Int(c["bagCapacity"]);
            BuildCatalogue(c, capacity);
            var operatives = Arr(c["operatives"]).Select(Str).ToList();
            Assert.That(operatives.Distinct().Count(), Is.EqualTo(operatives.Count), caseName + ": duplicate operative label");
            BuildInitialState(Obj(c["initial"]), operatives, capacity);

            var operations = Arr(c["operations"]).Select(Obj).ToList();
            var expected = Obj(c["expected"]);

            string refusalCode = null;
            var refusalIndex = -1;
            for (var i = 0; i < operations.Count; i++)
            {
                refusalCode = Apply(operations[i]);
                if (refusalCode != null)
                {
                    refusalIndex = i;
                    break;
                }
            }

            if (expected.TryGetValue("refused", out var refusedValue))
            {
                var refused = Obj(refusedValue);
                Assert.That(refusalCode, Is.Not.Null, $"{caseName}: the vector expects a refusal but every operation applied");
                Assert.That(refusalIndex, Is.EqualTo(Int(refused["index"])), $"{caseName}: index of the first refusal (code {refusalCode})");
                Assert.That(refusalCode, Is.EqualTo(Str(refused["code"])), $"{caseName}: refusal code at operation {refusalIndex}");
                return;
            }

            Assert.That(refusalCode, Is.Null, $"{caseName}: operation {refusalIndex} was refused with {refusalCode}");
            var wanted = Obj(expected["state"]);
            Assert.That(RenderActual(operatives), Is.EqualTo(RenderExpected(wanted, operatives)), caseName + ": the final state");
        }

        // ---- setup ----

        void BuildCatalogue(Dictionary<string, object> c, int capacity)
        {
            foreach (var entry in Arr(c["items"]).Select(Obj))
            {
                var id = Str(entry["id"]);
                var category = (ItemCategory)Enum.Parse(typeof(ItemCategory), Str(entry["category"]), true);
                var slot = (ItemSlot)Enum.Parse(typeof(ItemSlot), Str(entry["slot"]), true);
                var maxStack = Int(entry["maxStack"]);
                Assert.That(Bool(entry["stackable"]), Is.EqualTo(maxStack > 1), $"{caseName}: {id} stackable and maxStack disagree");
                var definition = ItemDefinition.Create(id, id, category, slot, maxStack, healAmount: Int(entry["healAmount"]));
                created.Add(definition);
                definitions.Add(id, definition);
            }
            var catalogue = ItemCatalogue.Create(definitions.Values.ToArray());
            created.Add(catalogue);
            session = new InventorySession(catalogue, capacity);
        }

        void BuildInitialState(Dictionary<string, object> initial, List<string> operatives, int capacity)
        {
            var bags = Obj(initial["bags"]);
            Assert.That(bags.Keys, Is.EquivalentTo(operatives), caseName + ": the bags must be exactly the operatives");

            // Creation order: the stash, then each bag in operative order; instances before stacks.
            AddHeld(session.Session.Stash, Obj(initial["stash"]), "stash");
            foreach (var label in operatives)
                AddHeld(session.Session.EnsureLoadout(label, capacity).Bag, Obj(bags[label]), label);

            foreach (var label in operatives)
            {
                var loadout = session.Session.Loadout(label);
                var equipped = Obj(Obj(bags[label])["equipped"]);
                foreach (var slot in new[] { "weapon", "armor", "utility" })
                {
                    if (equipped[slot] == null)
                        continue;
                    var result = loadout.Equipment.TryEquip(loadout.Bag, session.Catalogue, InstanceOf(Str(equipped[slot])));
                    Assert.That(result.Ok, Is.True, $"{caseName}: {label} could not equip {Str(equipped[slot])} in setup: {result.Reason}");
                }
            }
        }

        void AddHeld(ItemInventory inventory, Dictionary<string, object> held, string where)
        {
            foreach (var e in Arr(held["instances"]).Select(Arr))
            {
                var label = Str(e[0]);
                var definition = definitions[Str(e[1])];
                Assert.That(definition.IsStackable, Is.False, $"{caseName}: {label} is an instance of a stackable definition");
                var entriesBefore = inventory.Count;
                Assert.That(inventory.Add(definition, 1), Is.EqualTo(0), $"{caseName}: {label} did not fit in {where}");
                Assert.That(inventory.Count, Is.EqualTo(entriesBefore + 1), caseName + ": " + label);
                var instanceId = inventory.Entries[inventory.Count - 1].InstanceId;
                Assert.That(labelToInstance.ContainsKey(label), Is.False, $"{caseName}: label {label} appears twice");
                labelToInstance.Add(label, instanceId);
                instanceToLabel.Add(instanceId, label);
            }
            foreach (var e in Arr(held["stacks"]).Select(Arr))
            {
                var definition = definitions[Str(e[0])];
                Assert.That(definition.IsStackable, Is.True, $"{caseName}: {definition.Id} is stacked but not stackable");
                Assert.That(inventory.Add(definition, Int(e[1])), Is.EqualTo(0), $"{caseName}: {Int(e[1])} x {definition.Id} did not fit in {where}");
            }
        }

        string InstanceOf(string label)
        {
            Assert.That(labelToInstance.TryGetValue(label, out var id), Is.True, $"{caseName}: label {label} is not in the initial state");
            return id;
        }

        // A label that names no item (the vector's "ghost") becomes an id that exists nowhere.
        string InstanceOrGhost(string label) => labelToInstance.TryGetValue(label, out var id) ? id : "ghost-" + label;

        // ---- operations ----

        /// <summary>Applies one operation through InventorySession. Returns null when it succeeded, else the vector's refusal code.</summary>
        string Apply(Dictionary<string, object> o)
        {
            var kind = Str(o["op"]);
            var operative = Str(o["operative"]);   // "opX" is simply an operative id the session does not know
            switch (kind)
            {
                case "equip":
                    return Code(session.Equip(operative, InstanceOrGhost(Str(o["item"]))));
                case "unequip":
                    return Code(session.Unequip(operative, (ItemSlot)Enum.Parse(typeof(ItemSlot), Str(o["slot"]), true)));
                case "equipFromStash":
                    return Code(session.EquipFromStash(operative, InstanceOrGhost(Str(o["item"]))));
                case "toStash":
                case "toBag":
                    var toStash = kind == "toStash";
                    if (o.ContainsKey("item"))
                        return Code(Transfer(toStash, operative, InstanceOrGhost(Str(o["item"])), 1));
                    return MoveStack(toStash, operative, Str(o["definition"]), Int(o["quantity"]));
                default:
                    Assert.Fail($"{caseName}: unknown operation {kind}");
                    return null;
            }
        }

        TransferResult Transfer(bool toStash, string operative, string instanceId, int quantity) =>
            toStash ? session.ToStash(operative, instanceId, quantity) : session.FromStash(operative, instanceId, quantity);

        /// <summary>
        /// The addressing adapter: units of one definition are taken from its last entry first, an entry at a time, until the
        /// quantity is moved or a call fails. A quantity below 1 is passed through so Unity itself says InvalidQuantity.
        /// </summary>
        string MoveStack(bool toStash, string operative, string definitionId, int quantity)
        {
            // A stack operation addresses stackable definitions only, as the backend does: a non-stackable definition is moved by
            // its item id, so LastEntry below can never pick up an instance entry.
            Assert.That(definitions.TryGetValue(definitionId, out var stackDefinition) && stackDefinition.IsStackable, Is.True,
                $"{caseName}: a stack operation names {definitionId}, which is not a stackable definition (misclassified vector)");

            var loadout = session.Session.Loadout(operative);
            var source = toStash ? loadout?.Bag : session.Session.Stash;

            ItemEntry LastEntry() => source?.Entries.LastOrDefault(e => e.DefinitionId == definitionId);

            if (quantity < 1)
                return Code(Transfer(toStash, operative, LastEntry()?.InstanceId ?? "none", quantity));

            var remaining = quantity;
            while (remaining > 0)
            {
                var entry = LastEntry();
                if (entry == null)
                {
                    if (remaining == quantity)
                        return "NotFound";   // nothing of that definition at the source
                    Assert.Fail($"{caseName}: asks for {quantity} x {definitionId} but only {quantity - remaining} are held (misclassified vector)");
                }
                var chunk = Math.Min(remaining, entry.Quantity);
                var code = Code(Transfer(toStash, operative, entry.InstanceId, chunk));
                if (code != null)
                    return code;
                remaining -= chunk;
            }
            return null;
        }

        string Code(TransferResult result)
        {
            if (result.IsPartial)
                Assert.Fail($"{caseName}: Unity moved only part of the request ({result.Moved} of {result.Requested}); a shared case must not depend on partial transfers (misclassified vector)");
            if (result.Succeeded)
                return null;
            switch (result.Failure)
            {
                case TransferFailure.InvalidQuantity: return "InvalidQuantity";
                case TransferFailure.NotFound: return "NotFound";
                case TransferFailure.UnknownItem: return "UnknownItem";
                case TransferFailure.DestinationFull: return "DestinationFull";
                case TransferFailure.Equipped: return "Equipped";
                default:
                    Assert.Fail($"{caseName}: transfer failure {result.Failure} has no vector code");
                    return null;
            }
        }

        string Code(EditResult result)
        {
            if (result.Ok)
                return null;
            var reason = result.Reason;
            if (reason == "That item is not in this bag." || reason == "That item is no longer in the stash.") return "NotFound";
            if (reason == "Unknown operative.") return "UnknownOperative";
            if (reason.StartsWith("Unknown item '", StringComparison.Ordinal) && reason.EndsWith("' cannot be equipped.", StringComparison.Ordinal)) return "UnknownItem";
            if (reason.EndsWith(" can't be equipped.", StringComparison.Ordinal)) return "NotEquippable";
            if (reason == "Nothing is equipped there.") return "NothingEquipped";
            if (reason == "The bag is full.") return "DestinationFull";
            Assert.Fail($"{caseName}: Unity's reason text has no vector code (did a message change?): \"{reason}\"");
            return null;
        }

        // ---- comparison: per location the instance labels, the per-definition totals and entry counts, the equipped labels ----

        string RenderActual(List<string> operatives)
        {
            var lines = new List<string>();
            RenderInventory(lines, "stash", session.Session.Stash);
            foreach (var label in operatives)
            {
                var loadout = session.Session.Loadout(label);
                Assert.That(loadout, Is.Not.Null, $"{caseName}: {label} lost its loadout");
                RenderInventory(lines, label, loadout.Bag);
                foreach (var slot in new[] { ItemSlot.Weapon, ItemSlot.Armor, ItemSlot.Utility })
                {
                    var id = loadout.Equipment.InstanceIdIn(slot);
                    var equipped = string.IsNullOrEmpty(id) ? "-" : instanceToLabel.TryGetValue(id, out var l) ? l : "?" + id;
                    lines.Add($"{label} equipped {slot.ToString().ToLowerInvariant()} {equipped}");
                }
            }
            return string.Join("\n", lines);
        }

        void RenderInventory(List<string> lines, string where, ItemInventory inventory)
        {
            var instances = new List<string>();
            var totals = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var entries = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var entry in inventory.Entries)
            {
                var definition = definitions[entry.DefinitionId];
                if (definition.IsStackable)
                {
                    totals.TryGetValue(entry.DefinitionId, out var total);
                    totals[entry.DefinitionId] = total + entry.Quantity;
                    entries.TryGetValue(entry.DefinitionId, out var count);
                    entries[entry.DefinitionId] = count + 1;
                    continue;
                }
                var label = instanceToLabel.TryGetValue(entry.InstanceId, out var known) ? known : "?" + entry.InstanceId;
                instances.Add($"{label}:{entry.DefinitionId}");
            }
            instances.Sort(StringComparer.Ordinal);
            lines.AddRange(instances.Select(i => $"{where} instance {i}"));
            lines.AddRange(totals.Select(t => $"{where} stack {t.Key} x{t.Value} in {entries[t.Key]} entries"));
        }

        string RenderExpected(Dictionary<string, object> state, List<string> operatives)
        {
            var lines = new List<string>();
            RenderHeld(lines, "stash", Obj(state["stash"]));
            var bags = Obj(state["bags"]);
            foreach (var label in operatives)
            {
                var bag = Obj(bags[label]);
                RenderHeld(lines, label, bag);
                var equipped = Obj(bag["equipped"]);
                foreach (var slot in new[] { "weapon", "armor", "utility" })
                    lines.Add($"{label} equipped {slot} {(equipped[slot] == null ? "-" : Str(equipped[slot]))}");
            }
            return string.Join("\n", lines);
        }

        void RenderHeld(List<string> lines, string where, Dictionary<string, object> held)
        {
            var instances = Arr(held["instances"]).Select(Arr).Select(e => $"{Str(e[0])}:{Str(e[1])}").ToList();
            instances.Sort(StringComparer.Ordinal);
            lines.AddRange(instances.Select(i => $"{where} instance {i}"));
            var stacks = Arr(held["stacks"]).Select(Arr).OrderBy(e => Str(e[0]), StringComparer.Ordinal);
            foreach (var e in stacks)
            {
                var total = Int(e[1]);
                Assert.That(total, Is.GreaterThanOrEqualTo(1), $"{caseName}: an expected stack holds at least one unit");
                var maxStack = definitions[Str(e[0])].MaxStack;
                lines.Add($"{where} stack {Str(e[0])} x{total} in {(total + maxStack - 1) / maxStack} entries");   // ceil(total / maxStack)
            }
        }
    }
}
