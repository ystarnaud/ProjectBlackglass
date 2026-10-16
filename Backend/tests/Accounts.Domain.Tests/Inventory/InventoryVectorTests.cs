using System.Text.Json;
using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.Domain.Inventory;

namespace Blackglass.Accounts.Domain.Tests.Inventory;

/// <summary>
/// The shared inventory vectors (Backend/contracts/inventory), written by hand from Unity's inventory rules plus the owner's
/// backend rulings. This runner executes every case, shared and backend, against InventoryEditor; Unity's EditMode
/// InventoryContractTests run the shared ones against InventorySession. Each case is its own test (the theory row is the case
/// name). Extra checks keep a case labelled shared honest: it must stay inside what Unity does identically.
/// </summary>
public class InventoryVectorTests
{
    const long GhostItem = 900_000_001;
    const long UnknownOperativeId = 900_000_002;

    static readonly JsonDocument File = JsonDocument.Parse(System.IO.File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "contracts", "inventory", "vectors.json")));

    static IEnumerable<JsonElement> RawCases => File.RootElement.GetProperty("cases").EnumerateArray();

    public static IEnumerable<object[]> CaseNames() => RawCases.Select(c => new object[] { c.GetProperty("name").GetString()! });

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Case_matches(string name)
    {
        var raw = RawCases.Single(c => c.GetProperty("name").GetString() == name);
        var built = Build(raw);
        var initialRendering = Render(built.Initial, built);

        var outcome = InventoryEditor.Apply(built.Initial, built.Catalogue, built.Operations);

        Assert.Equal(initialRendering, Render(built.Initial, built));   // a refused or applied batch never edits the caller's model
        if (built.ExpectedRefusal is { } expected)
        {
            Assert.True(!outcome.Applied, $"{name}: expected a refusal but the batch applied");
            Assert.Equal(expected.Index, outcome.RefusedIndex);
            Assert.Equal(expected.Code, outcome.Refusal!.Value.ToString());
        }
        else
        {
            Assert.True(outcome.Applied, $"{name}: refused at operation {outcome.RefusedIndex} with {outcome.Refusal}");
            Assert.Equal(Render(built.ExpectedState!, built), Render(outcome.After!, built));
        }

        if (built.Scope == "shared") AssertSharedStaysInsideUnity(built);
    }

    [Fact]
    public void The_shared_guard_tolerates_operations_after_the_refused_one()
    {
        var medkit = new CatalogueItem("item.medkit", ItemCategory.Consumable, ItemSlot.None, true, 3, 25);
        var catalogue = new CatalogueDocument(CatalogueDocument.SupportedFormatVersion, new InventorySettings(8), [medkit],
            new LootTable(1, 1, 1, 1, []), new Blackglass.Accounts.Domain.Catalogue.Progression([0, 100], 10, []), [], new Starter([], []));
        var initial = new InventoryModel([1], [], [new HeldStack(null, "item.medkit", 2)]);
        IReadOnlyList<InventoryOperation> operations =
            [new Equip(1, GhostItem), new MoveStack(1, "item.medkit", 1, Destination.Bag), new MoveStack(1, "item.medkit", 1, Destination.Bag)];
        var built = new Built("crafted", "shared", null, catalogue, initial, operations, null, new Refusal(0, "NotFound"),
            new Dictionary<string, long>(), new Dictionary<string, long> { ["op1"] = 1 });

        Assert.False(InventoryEditor.Apply(initial, catalogue, operations).Applied);
        Assert.Null(Record.Exception(() => AssertSharedStaysInsideUnity(built)));
    }

    [Fact]
    public void The_file_has_enough_cases_and_covers_every_refusal()
    {
        var cases = RawCases.ToList();
        Assert.True(cases.Count >= 40, "the vector file lost cases");
        var names = cases.Select(c => c.GetProperty("name").GetString()!).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.All(names, n => Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)*$", n));

        var built = cases.Select(Build).ToList();   // fails on a missing or unknown scope, or a malformed case
        Assert.Contains(built, b => b.Scope == "shared");
        Assert.Contains(built, b => b.Scope == "backend");

        var covered = built.Where(b => b.ExpectedRefusal is not null).Select(b => b.ExpectedRefusal!.Code).ToHashSet();
        foreach (var refusal in Enum.GetValues<InventoryRefusal>())
            Assert.True(covered.Contains(refusal.ToString()), $"no vector expects {refusal}");

        foreach (var b in built.Where(b => b.Scope == "shared"))
            Assert.Null(b.Why);
        foreach (var b in built.Where(b => b.Scope == "backend"))
            Assert.False(string.IsNullOrWhiteSpace(b.Why), $"{b.Name}: a backend case needs a why");
    }

    // ---- what a shared case may not do (it also runs against Unity's partial, clamping, NotFound-on-unknown-operative rules) ----

    static void AssertSharedStaysInsideUnity(Built b)
    {
        var known = b.Catalogue.Items.ToDictionary(i => i.Id);
        foreach (var i in b.Initial.Instances) Assert.True(known.ContainsKey(i.DefinitionId), $"{b.Name}: shared cases never involve a retired definition ({i.DefinitionId})");
        foreach (var s in b.Initial.Stacks) Assert.True(known.ContainsKey(s.DefinitionId), $"{b.Name}: shared cases never involve a retired definition ({s.DefinitionId})");
        foreach (var o in b.Operations.OfType<MoveStack>()) Assert.True(known.ContainsKey(o.DefinitionId), $"{b.Name}: shared cases never involve a retired definition ({o.DefinitionId})");
        foreach (var operative in b.Initial.Operatives)
            Assert.True(InventoryEditor.BagEntries(b.Initial, b.Catalogue, operative) <= b.Catalogue.Inventory.BagCapacity, $"{b.Name}: shared cases never start over capacity");
        if (b.ExpectedRefusal is { } refusal)
            Assert.False(refusal.Code is "InsufficientQuantity" or "UnknownItem", $"{b.Name}: {refusal.Code} is a backend-only refusal");

        for (var i = 0; i < b.Operations.Count; i++)
        {
            var op = b.Operations[i];
            if (op is MoveInstance or MoveStack)
                Assert.NotEqual(UnknownOperativeId, op.OperativeId);   // Unity reports NotFound there; the backend says UnknownOperative

            if (b.ExpectedRefusal is { } refused && i > refused.Index) continue;   // a refused batch never runs these, and the prefix before them has no state
            if (op is not MoveStack move || move.Quantity < 1) continue;
            var before = i == 0 ? b.Initial : InventoryEditor.Apply(b.Initial, b.Catalogue, b.Operations.Take(i).ToList()).After!;
            var source = before.Stacks.FirstOrDefault(s => s.OperativeId == (move.To == Destination.Stash ? move.OperativeId : null) && s.DefinitionId == move.DefinitionId);
            if (source is not null) Assert.True(move.Quantity <= source.Quantity, $"{b.Name}: operation {i} asks for more than is held");
            if (move.To != Destination.Bag) continue;

            var maxStack = known[move.DefinitionId].MaxStack;
            var held = before.Stacks.FirstOrDefault(s => s.OperativeId == move.OperativeId && s.DefinitionId == move.DefinitionId)?.Quantity ?? 0;
            var free = b.Catalogue.Inventory.BagCapacity - InventoryEditor.BagEntries(before, b.Catalogue, move.OperativeId);
            var room = (long)Math.Max(0, free) * maxStack + (held % maxStack == 0 ? 0 : maxStack - held % maxStack);
            if (b.ExpectedRefusal is { Index: var at } && at == i && source is not null)
                Assert.True(room == 0, $"{b.Name}: operation {i} would be a partial move in Unity ({room} units fit)");
            else if (source is not null)
                Assert.True(move.Quantity <= room, $"{b.Name}: operation {i} does not fit entirely");
        }
    }

    // ---- reading a case ----

    sealed record Refusal(int Index, string Code);

    sealed record Built(string Name, string Scope, string? Why, CatalogueDocument Catalogue, InventoryModel Initial,
        IReadOnlyList<InventoryOperation> Operations, InventoryModel? ExpectedState, Refusal? ExpectedRefusal,
        IReadOnlyDictionary<string, long> Items, IReadOnlyDictionary<string, long> Operatives);

    static Built Build(JsonElement c)
    {
        var name = c.GetProperty("name").GetString()!;
        var scope = c.TryGetProperty("scope", out var s) ? s.GetString() : null;
        if (scope is not ("shared" or "backend")) throw new InvalidOperationException($"{name}: scope must be shared or backend, not {scope ?? "missing"}");
        var why = c.TryGetProperty("why", out var w) ? w.GetString() : null;

        var items = c.GetProperty("items").EnumerateArray().Select(i => new CatalogueItem(
            i.GetProperty("id").GetString()!, Enum.Parse<ItemCategory>(i.GetProperty("category").GetString()!, true),
            Enum.Parse<ItemSlot>(i.GetProperty("slot").GetString()!, true), i.GetProperty("stackable").GetBoolean(),
            i.GetProperty("maxStack").GetInt32(), i.GetProperty("healAmount").GetInt32())).ToList();
        foreach (var item in items)
        {
            Assert.True(item.Stackable == item.MaxStack > 1, $"{name}: {item.Id} stackable must mean maxStack above 1");
            var slot = item.Category switch { ItemCategory.Weapon => ItemSlot.Weapon, ItemCategory.Armor => ItemSlot.Armor, ItemCategory.Utility => ItemSlot.Utility, _ => ItemSlot.None };
            Assert.True(item.Slot == slot, $"{name}: {item.Id} slot does not match its category");
        }
        var bagCapacity = c.GetProperty("bagCapacity").GetInt32();
        Assert.True(bagCapacity >= 1, $"{name}: bagCapacity must be at least 1 (Unity reads 0 as uncapped, the backend as zero)");
        var catalogue = new CatalogueDocument(CatalogueDocument.SupportedFormatVersion, new InventorySettings(bagCapacity), items,
            new LootTable(1, 1, 1, 1, []), new Blackglass.Accounts.Domain.Catalogue.Progression([0, 100], 10, []), [], new Starter([], []));

        var operativeLabels = c.GetProperty("operatives").EnumerateArray().Select(o => o.GetString()!).ToList();
        Assert.Equal(operativeLabels.Count, operativeLabels.Distinct().Count());
        foreach (var label in operativeLabels) Assert.True(label is not ("ghost" or "opX"), $"{name}: operative label {label} is reserved");
        var operatives = new Dictionary<string, long>();
        foreach (var label in operativeLabels) operatives[label] = operatives.Count + 1;

        // Item ids are numbered 1, 2, ... in the order the labels first appear: the stash, then each bag in operative order.
        var initialState = c.GetProperty("initial");
        var labels = new Dictionary<string, long>();
        var bagKeys = initialState.GetProperty("bags").EnumerateObject().Select(p => p.Name).Order().ToList();
        Assert.True(bagKeys.SequenceEqual(operativeLabels.Order()), $"{name}: the bags must be exactly the operatives");
        foreach (var e in initialState.GetProperty("stash").GetProperty("instances").EnumerateArray()) AddLabel(name, labels, e[0].GetString()!);
        foreach (var label in operativeLabels)
            foreach (var e in initialState.GetProperty("bags").GetProperty(label).GetProperty("instances").EnumerateArray()) AddLabel(name, labels, e[0].GetString()!);

        var initial = BuildState(name, initialState, operatives, labels, operativeLabels);
        var operations = c.GetProperty("operations").EnumerateArray().Select(o => BuildOperation(name, o, operatives, labels)).ToList();

        var expected = c.GetProperty("expected");
        InventoryModel? expectedState = null;
        Refusal? expectedRefusal = null;
        if (expected.TryGetProperty("refused", out var refused))
        {
            expectedRefusal = new Refusal(refused.GetProperty("index").GetInt32(), refused.GetProperty("code").GetString()!);
            Assert.True(Enum.TryParse<InventoryRefusal>(expectedRefusal.Code, out _), $"{name}: {expectedRefusal.Code} is not an InventoryRefusal");
        }
        else expectedState = BuildState(name, expected.GetProperty("state"), operatives, labels, operativeLabels);

        return new Built(name, scope, why, catalogue, initial, operations, expectedState, expectedRefusal, labels, operatives);
    }

    static void AddLabel(string name, Dictionary<string, long> labels, string label)
    {
        Assert.True(label is not ("ghost" or "opX"), $"{name}: {label} is reserved");
        Assert.True(labels.TryAdd(label, labels.Count + 1), $"{name}: label {label} appears twice in the initial state");
    }

    static InventoryModel BuildState(string name, JsonElement state, Dictionary<string, long> operatives, Dictionary<string, long> labels, List<string> operativeOrder)
    {
        var instances = new List<HeldInstance>();
        var stacks = new List<HeldStack>();
        foreach (var e in state.GetProperty("stash").GetProperty("instances").EnumerateArray())
            instances.Add(new HeldInstance(Label(name, labels, e[0].GetString()!), e[1].GetString()!, null, null));
        foreach (var e in state.GetProperty("stash").GetProperty("stacks").EnumerateArray())
            stacks.Add(new HeldStack(null, e[0].GetString()!, e[1].GetInt32()));

        foreach (var label in operativeOrder)
        {
            var bag = state.GetProperty("bags").GetProperty(label);
            var holder = operatives[label];
            var equipped = new Dictionary<string, ItemSlot>();
            var equippedProperty = bag.GetProperty("equipped");
            foreach (var slotName in new[] { "weapon", "armor", "utility" })
                if (equippedProperty.GetProperty(slotName).GetString() is { } equippedLabel) equipped[equippedLabel] = ParseSlot(slotName);
            var bagLabels = new HashSet<string>();
            foreach (var e in bag.GetProperty("instances").EnumerateArray())
            {
                var itemLabel = e[0].GetString()!;
                bagLabels.Add(itemLabel);
                instances.Add(new HeldInstance(Label(name, labels, itemLabel), e[1].GetString()!, holder, equipped.TryGetValue(itemLabel, out var slot) ? slot : null));
            }
            Assert.True(equipped.Keys.All(bagLabels.Contains), $"{name}: {label} equips an item that is not in its bag");
            foreach (var e in bag.GetProperty("stacks").EnumerateArray())
                stacks.Add(new HeldStack(holder, e[0].GetString()!, e[1].GetInt32()));
        }
        return new InventoryModel(operativeOrder.Select(l => operatives[l]).ToList(), instances, stacks);
    }

    static long Label(string name, Dictionary<string, long> labels, string label)
    {
        Assert.True(labels.TryGetValue(label, out var id), $"{name}: label {label} is not in the initial state");
        return id;
    }

    static ItemSlot ParseSlot(string slot)
    {
        var parsed = Enum.Parse<ItemSlot>(slot, true);
        Assert.NotEqual(ItemSlot.None, parsed);
        return parsed;
    }

    static InventoryOperation BuildOperation(string name, JsonElement o, Dictionary<string, long> operatives, Dictionary<string, long> labels)
    {
        var operativeLabel = o.GetProperty("operative").GetString()!;
        var operative = operativeLabel == "opX" ? UnknownOperativeId
            : operatives.TryGetValue(operativeLabel, out var id) ? id : throw new InvalidOperationException($"{name}: operative {operativeLabel} is not in the case");
        long Item()
        {
            var label = o.GetProperty("item").GetString()!;
            return label == "ghost" ? GhostItem : Label(name, labels, label);
        }

        var kind = o.GetProperty("op").GetString();
        switch (kind)
        {
            case "equip": return new Equip(operative, Item());
            case "unequip": return new Unequip(operative, ParseSlot(o.GetProperty("slot").GetString()!));
            case "equipFromStash": return new EquipFromStash(operative, Item());
            case "toStash" or "toBag":
                var to = kind == "toStash" ? Destination.Stash : Destination.Bag;
                return o.TryGetProperty("item", out _)
                    ? new MoveInstance(operative, Item(), to)
                    : new MoveStack(operative, o.GetProperty("definition").GetString()!, o.GetProperty("quantity").GetInt32(), to);
            default: throw new InvalidOperationException($"{name}: unknown operation {kind}");
        }
    }

    /// <summary>A model as readable lines with the case's labels, in a canonical order, so a failure shows what differs.</summary>
    static string Render(InventoryModel model, Built built)
    {
        string ItemName(long id) => built.Items.FirstOrDefault(p => p.Value == id).Key ?? $"#{id}";
        string Where(long? holder) => holder is { } h ? built.Operatives.First(p => p.Value == h).Key : "stash";
        var lines = new List<string>();
        lines.AddRange(model.Instances.OrderBy(i => i.ItemId).Select(i => $"{ItemName(i.ItemId)} {i.DefinitionId} @{Where(i.OperativeId)} {(i.Equipped is { } e ? "equipped:" + e : "-")}"));
        lines.AddRange(model.Stacks.OrderBy(s => s.OperativeId ?? 0).ThenBy(s => s.DefinitionId, StringComparer.Ordinal).Select(s => $"stack {s.DefinitionId} x{s.Quantity} @{Where(s.OperativeId)}"));
        lines.Add("operatives " + string.Join(",", model.Operatives.Order()));
        return string.Join("\n", lines);
    }
}
