using System.Text.Json;
using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.Domain.Inventory;

namespace Blackglass.Accounts.Domain.Tests.Inventory;

/// <summary>
/// The shared starter vector (Backend/contracts/starter), which Unity's EditMode tests run against InventorySession.SeedStarter.
/// The vector is written by hand from Unity's rules; a test-side model of ItemInventory.Add checks the hand-written bags too.
/// </summary>
public class StarterVectorTests
{
    static JsonElement Load() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "starter", "vectors.json"))).RootElement;

    static readonly string[] RefusalClasses = ["second-equip-in-slot", "equip-without-slot", "unknown-item", "bag-capacity"];

    [Fact]
    public void Every_case_matches()
    {
        var cases = Load().GetProperty("cases").EnumerateArray().ToList();
        Assert.True(cases.Count >= 10, "the vector file lost cases");
        var names = new HashSet<string>();
        var refusals = new HashSet<string>();
        foreach (var c in cases)
        {
            var name = c.GetProperty("name").GetString()!;
            Assert.True(names.Add(name), $"duplicate case name {name}");
            var document = BuildDocument(c);
            var expected = c.GetProperty("expected");

            var errors = new List<string>();
            CatalogueValidator.Validate(document, errors);

            if (expected.TryGetProperty("refused", out var refused))
            {
                var expectedClass = refused.GetString()!;
                refusals.Add(expectedClass);
                var classes = errors.Select(e => RefusalClass(name, e)).Distinct().ToList();
                Assert.True(classes.Count == 1 && classes[0] == expectedClass,
                    $"{name}: expected refusal {expectedClass} but the validator said [{string.Join("; ", errors)}]");
                Assert.Throws<InvalidOperationException>(() => StarterPlanner.For(1, document));
                continue;
            }

            Assert.True(errors.Count == 0, $"{name}: the validator refused a case that should be fine: {string.Join("; ", errors)}");
            AssertPlanMatches(name, document, expected, StarterPlanner.For(1, document));
        }
        Assert.True(RefusalClasses.All(refusals.Contains), "the vector must cover every refusal class");
    }

    [Fact]
    public void The_hand_written_bags_follow_the_Unity_add_rules()
    {
        foreach (var c in Load().GetProperty("cases").EnumerateArray())
        {
            var expected = c.GetProperty("expected");
            if (expected.TryGetProperty("refused", out _)) continue;
            var name = c.GetProperty("name").GetString()!;
            var items = c.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("id").GetString()!, i => i.GetProperty("maxStack").GetInt32());
            var capacity = c.GetProperty("bagCapacity").GetInt32();

            foreach (var op in c.GetProperty("operatives").EnumerateArray())
            {
                var bag = new List<(string Id, int Quantity)>();
                foreach (var g in op.GetProperty("grants").EnumerateArray())
                    Add(bag, g.GetProperty("itemId").GetString()!, g.GetProperty("quantity").GetInt32(), items);
                var id = op.GetProperty("id").GetString()!;
                Assert.True(bag.Count <= capacity, $"{name}: {id} bag over capacity");
                Assert.Equal(Entries(expected.GetProperty("operatives").GetProperty(id).GetProperty("bag")), bag);
            }

            var stash = new List<(string Id, int Quantity)>();
            foreach (var g in c.GetProperty("stash").EnumerateArray())
                Add(stash, g.GetProperty("itemId").GetString()!, g.GetProperty("quantity").GetInt32(), items);
            Assert.Equal(Entries(expected.GetProperty("stash")), stash);
        }
    }

    /// <summary>Unity's ItemInventory.Add on a bag with room: open stacks of the definition first, then new entries of min(left, maxStack).</summary>
    static void Add(List<(string Id, int Quantity)> entries, string id, int quantity, Dictionary<string, int> maxStacks)
    {
        var maxStack = maxStacks[id];
        var left = quantity;
        if (maxStack > 1)
        {
            for (var i = 0; i < entries.Count && left > 0; i++)
            {
                if (entries[i].Id != id || entries[i].Quantity >= maxStack) continue;
                var take = Math.Min(left, maxStack - entries[i].Quantity);
                entries[i] = (id, entries[i].Quantity + take);
                left -= take;
            }
        }
        while (left > 0)
        {
            var take = Math.Min(left, maxStack);
            entries.Add((id, take));
            left -= take;
        }
    }

    static List<(string Id, int Quantity)> Entries(JsonElement list) =>
        list.EnumerateArray().Select(e => (e[0].GetString()!, e[1].GetInt32())).ToList();

    /// <summary>The one place validator wording maps to the vector's refusal classes.</summary>
    static string RefusalClass(string caseName, string error)
    {
        if (error.Contains("unknown item", StringComparison.Ordinal)) return "unknown-item";
        if (error.Contains("a second item to equip", StringComparison.Ordinal)) return "second-equip-in-slot";
        if (error.Contains("cannot be equipped", StringComparison.Ordinal)) return "equip-without-slot";
        if (error.Contains("exceed the bag capacity", StringComparison.Ordinal)) return "bag-capacity";
        throw new InvalidOperationException($"{caseName}: the validator error maps to no refusal class: {error}");
    }

    static void AssertPlanMatches(string name, CatalogueDocument document, JsonElement expected, StarterPlan plan)
    {
        var expectedOperatives = expected.GetProperty("operatives").EnumerateObject().ToList();
        Assert.True(expectedOperatives.Count == plan.Operatives.Count, $"{name}: operative count");
        foreach (var op in expectedOperatives)
        {
            Assert.True(plan.Operatives.Any(p => p.DefinitionId == op.Name), $"{name}: operative {op.Name} missing");
            var instances = plan.Instances.Where(i => i.OperativeDefinitionId == op.Name).ToList();
            var stacks = plan.Stacks.Where(s => s.OperativeDefinitionId == op.Name).ToList();
            AssertContents(name + " bag " + op.Name, document, op.Value.GetProperty("bag"), instances, stacks);

            var equipped = op.Value.GetProperty("equipped");
            foreach (var slot in new[] { "weapon", "armor", "utility" })
            {
                var want = equipped.GetProperty(slot).GetString();
                var got = instances.Where(i => i.EquippedSlot == slot).Select(i => i.DefinitionId).ToList();
                Assert.True(want is null ? got.Count == 0 : got.Count == 1 && got[0] == want,
                    $"{name}: {op.Name} {slot} expected {want ?? "nothing"} but got [{string.Join(", ", got)}]");
            }
        }
        // The plan keeps stacked items as one total, so it can't show the split into entries or their order; Unity checks those.
        AssertContents(name + " stash", document, expected.GetProperty("stash"),
            plan.Instances.Where(i => i.OperativeDefinitionId is null).ToList(), plan.Stacks.Where(s => s.OperativeDefinitionId is null).ToList());
        Assert.DoesNotContain(plan.Instances, i => i.OperativeDefinitionId is null && i.EquippedSlot is not null);
    }

    static void AssertContents(string where, CatalogueDocument document, JsonElement entries,
        List<PlannedInstance> instances, List<PlannedStack> stacks)
    {
        var wanted = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (id, quantity) in Entries(entries)) wanted[id] = wanted.GetValueOrDefault(id) + quantity;
        var got = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var i in instances) got[i.DefinitionId] = got.GetValueOrDefault(i.DefinitionId) + 1;
        foreach (var s in stacks) got[s.DefinitionId] = got.GetValueOrDefault(s.DefinitionId) + s.Quantity;
        Assert.True(wanted.OrderBy(p => p.Key, StringComparer.Ordinal).SequenceEqual(got.OrderBy(p => p.Key, StringComparer.Ordinal)),
            $"{where}: expected [{string.Join(", ", wanted)}] but the plan has [{string.Join(", ", got)}]");
        foreach (var s in stacks) Assert.True(document.Items.Single(i => i.Id == s.DefinitionId).Stackable, $"{where}: a stack of a non-stackable item");
        Assert.Equal(stacks.Count, stacks.Select(s => s.DefinitionId).Distinct().Count());
    }

    static CatalogueDocument BuildDocument(JsonElement c)
    {
        var items = c.GetProperty("items").EnumerateArray().Select(i => new CatalogueItem(
            i.GetProperty("id").GetString()!, Enum.Parse<ItemCategory>(i.GetProperty("category").GetString()!, true),
            Enum.Parse<ItemSlot>(i.GetProperty("slot").GetString()!, true), i.GetProperty("stackable").GetBoolean(),
            i.GetProperty("maxStack").GetInt32(), i.GetProperty("healAmount").GetInt32())).ToList();
        var starterOperatives = c.GetProperty("operatives").EnumerateArray().Select(o => new StarterOperative(
            o.GetProperty("id").GetString()!,
            o.GetProperty("grants").EnumerateArray().Select(g => new StarterGrant(
                g.GetProperty("itemId").GetString()!, g.GetProperty("quantity").GetInt32(), g.GetProperty("equip").GetBoolean())).ToList())).ToList();
        var stash = c.GetProperty("stash").EnumerateArray().Select(g => new StashGrant(g.GetProperty("itemId").GetString()!, g.GetProperty("quantity").GetInt32())).ToList();
        return new CatalogueDocument(
            FormatVersion: CatalogueDocument.SupportedFormatVersion,
            Inventory: new InventorySettings(c.GetProperty("bagCapacity").GetInt32()),
            Items: items,
            LootTable: new LootTable(1, 1, 1, 1, [new LootEntry(items[0].Id, 1, 1, 1)]),
            Progression: new Blackglass.Accounts.Domain.Catalogue.Progression([0, 100], 10, ["choice.one"]),
            Operatives: starterOperatives.Select(o => new OperativeDefinition(o.OperativeId, "role.alpha", [])).ToList(),
            Starter: new Starter(starterOperatives, stash));
    }
}
