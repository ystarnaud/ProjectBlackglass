using System.Text.Json;
using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.Domain.Inventory;

namespace Blackglass.Accounts.IntegrationTests.Support;

/// <summary>One non-stackable in a vector state, by its label; Holder null is the stash.</summary>
public sealed record VectorInstance(string Label, string DefinitionId, string? Holder, ItemSlot? Equipped);

/// <summary>The total of one definition at one location (Holder null is the stash).</summary>
public sealed record VectorStack(string? Holder, string DefinitionId, int Quantity);

public sealed record VectorState(IReadOnlyList<VectorInstance> Instances, IReadOnlyList<VectorStack> Stacks);

/// <summary>One operation as the vector file writes it, still by labels ("ghost" and "opX" included).</summary>
public sealed record VectorOperation(string Op, string Operative, string? Item, ItemSlot? Slot, string? Definition, int? Quantity);

public sealed record VectorCase(string Name, string Scope, int BagCapacity, IReadOnlyList<CatalogueItem> Items, IReadOnlyList<string> Operatives,
    VectorState Initial, IReadOnlyList<VectorOperation> Operations, VectorState? Expected, int? RefusedIndex, string? RefusedCode)
{
    /// <summary>The operations with real ids: <paramref name="operative"/> and <paramref name="item"/> map every label, the
    /// reserved ones too (the caller gives them ids that exist nowhere).</summary>
    public IReadOnlyList<InventoryOperation> BuildOperations(Func<string, long> operative, Func<string, long> item) =>
        Operations.Select(o => (InventoryOperation)(o.Op switch
        {
            "equip" => new Equip(operative(o.Operative), item(o.Item!)),
            "unequip" => new Unequip(operative(o.Operative), o.Slot!.Value),
            "equipFromStash" => new EquipFromStash(operative(o.Operative), item(o.Item!)),
            "toStash" or "toBag" when o.Item is not null =>
                new MoveInstance(operative(o.Operative), item(o.Item), o.Op == "toStash" ? Destination.Stash : Destination.Bag),
            "toStash" or "toBag" =>
                new MoveStack(operative(o.Operative), o.Definition!, o.Quantity!.Value, o.Op == "toStash" ? Destination.Stash : Destination.Bag),
            _ => throw new InvalidOperationException($"{Name}: unknown operation {o.Op}"),
        })).ToList();

    /// <summary>
    /// A catalogue the backend accepts (it must pass GameCatalogue.Load) whose items and bag capacity are exactly the case's;
    /// a definition the case leaves out is retired. The loot table, progression and operatives only satisfy validation.
    /// </summary>
    public GameCatalogue Catalogue()
    {
        var embedded = GameCatalogue.Embedded.Document;
        var items = Items.OrderBy(i => i.Id, StringComparer.Ordinal).ToList();
        var document = embedded with
        {
            Inventory = new InventorySettings(BagCapacity),
            Items = items,
            LootTable = new LootTable(1, 1, 1, 1, [new LootEntry(items[0].Id, 1, 1, 1)]),
            Starter = new Starter([], []),
        };
        return GameCatalogue.Load(CanonicalCatalogueJson.ToBytes(document));
    }
}

/// <summary>
/// Reads Backend/contracts/inventory/vectors.json into label-level cases, so a runner can give the labels its own ids (the
/// domain runner numbers them; the store tests mint real database rows). The format is documented in the file's note and
/// validated by the domain runner (Accounts.Domain.Tests InventoryVectorTests); this reader only parses it.
/// </summary>
public static class InventoryVectors
{
    public static IReadOnlyList<VectorCase> Load()
    {
        using var file = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "inventory", "vectors.json")));
        return file.RootElement.GetProperty("cases").EnumerateArray().Select(Parse).ToList();
    }

    static VectorCase Parse(JsonElement c)
    {
        var name = c.GetProperty("name").GetString()!;
        var items = c.GetProperty("items").EnumerateArray().Select(i => new CatalogueItem(
            i.GetProperty("id").GetString()!, Enum.Parse<ItemCategory>(i.GetProperty("category").GetString()!, true),
            Enum.Parse<ItemSlot>(i.GetProperty("slot").GetString()!, true), i.GetProperty("stackable").GetBoolean(),
            i.GetProperty("maxStack").GetInt32(), i.GetProperty("healAmount").GetInt32())).ToList();
        var operatives = c.GetProperty("operatives").EnumerateArray().Select(o => o.GetString()!).ToList();
        var expected = c.GetProperty("expected");
        var refused = expected.TryGetProperty("refused", out var r) ? r : (JsonElement?)null;
        return new VectorCase(name, c.GetProperty("scope").GetString()!, c.GetProperty("bagCapacity").GetInt32(), items, operatives,
            State(c.GetProperty("initial"), operatives),
            c.GetProperty("operations").EnumerateArray().Select(Operation).ToList(),
            refused is null ? State(expected.GetProperty("state"), operatives) : null,
            refused?.GetProperty("index").GetInt32(), refused?.GetProperty("code").GetString());
    }

    static VectorState State(JsonElement state, IReadOnlyList<string> operatives)
    {
        var instances = new List<VectorInstance>();
        var stacks = new List<VectorStack>();
        var stash = state.GetProperty("stash");
        foreach (var e in stash.GetProperty("instances").EnumerateArray()) instances.Add(new VectorInstance(e[0].GetString()!, e[1].GetString()!, null, null));
        foreach (var e in stash.GetProperty("stacks").EnumerateArray()) stacks.Add(new VectorStack(null, e[0].GetString()!, e[1].GetInt32()));
        foreach (var operative in operatives)
        {
            var bag = state.GetProperty("bags").GetProperty(operative);
            var equipped = new Dictionary<string, ItemSlot>();
            foreach (var slot in new[] { ItemSlot.Weapon, ItemSlot.Armor, ItemSlot.Utility })
                if (bag.GetProperty("equipped").GetProperty(CanonicalCatalogueJson.Word(slot)).GetString() is { } label) equipped[label] = slot;
            foreach (var e in bag.GetProperty("instances").EnumerateArray())
            {
                var label = e[0].GetString()!;
                instances.Add(new VectorInstance(label, e[1].GetString()!, operative, equipped.TryGetValue(label, out var slot) ? slot : null));
            }
            foreach (var e in bag.GetProperty("stacks").EnumerateArray()) stacks.Add(new VectorStack(operative, e[0].GetString()!, e[1].GetInt32()));
        }
        return new VectorState(instances, stacks);
    }

    static VectorOperation Operation(JsonElement o) => new(
        o.GetProperty("op").GetString()!,
        o.GetProperty("operative").GetString()!,
        o.TryGetProperty("item", out var item) ? item.GetString() : null,
        o.TryGetProperty("slot", out var slot) ? Enum.Parse<ItemSlot>(slot.GetString()!, true) : null,
        o.TryGetProperty("definition", out var definition) ? definition.GetString() : null,
        o.TryGetProperty("quantity", out var quantity) ? quantity.GetInt32() : null);

    /// <summary>A state as sorted, readable lines, so a failure shows what differs.</summary>
    public static string Render(VectorState state) => string.Join("\n",
        state.Instances.Select(i => $"{i.Label} {i.DefinitionId} @{i.Holder ?? "stash"} {(i.Equipped is { } e ? "equipped:" + e : "-")}")
            .Concat(state.Stacks.Select(s => $"stack {s.DefinitionId} x{s.Quantity} @{s.Holder ?? "stash"}"))
            .Order(StringComparer.Ordinal));
}
