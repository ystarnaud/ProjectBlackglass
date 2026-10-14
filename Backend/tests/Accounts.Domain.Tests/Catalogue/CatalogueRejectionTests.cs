using System.Text;
using Blackglass.Accounts.Domain.Catalogue;

namespace Blackglass.Accounts.Domain.Tests.Catalogue;

/// <summary>Every way a catalogue file can be wrong is refused with a message of the right kind.</summary>
public class CatalogueRejectionTests
{
    // Assert.Contains with a predicate (not Assert.True(…Any…)): xUnit's analyzers flag the latter, and warnings are errors.
    static void AssertRejected(byte[] bytes, string expected)
    {
        var e = Assert.Throws<CatalogueException>(() => GameCatalogue.Load(bytes));
        Assert.Contains(e.Errors, x => x.Contains(expected, StringComparison.Ordinal));
    }

    // The golden vector with one exact edit; the edit must match exactly one place.
    static byte[] Edited(string find, string replace)
    {
        var text = Encoding.UTF8.GetString(Golden.Bytes());
        var first = text.IndexOf(find, StringComparison.Ordinal);
        Assert.True(first >= 0 && text.IndexOf(find, first + 1, StringComparison.Ordinal) < 0, $"'{find}' must occur exactly once");
        return Encoding.UTF8.GetBytes(text.Replace(find, replace, StringComparison.Ordinal));
    }

    [Fact]
    public void A_byte_order_mark_is_rejected()
        => AssertRejected([0xEF, 0xBB, 0xBF, .. Golden.Bytes()], "malformed: the file starts with a byte-order mark");

    [Fact]
    public void Crlf_line_ends_are_rejected()
        => AssertRejected(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Golden.Bytes()).Replace("\n", "\r\n")), "malformed: the file contains a carriage return");

    [Fact]
    public void A_truncated_file_is_rejected()
    {
        var bytes = Golden.Bytes();
        AssertRejected(bytes[..(bytes.Length / 2)], "malformed: not valid JSON");
    }

    [Fact]
    public void A_root_that_is_not_an_object_is_rejected()
        => AssertRejected("[]\n"u8.ToArray(), "malformed: $ must be an object");

    [Theory]
    [InlineData("\"healAmount\": 10\n", "\"healAmount\": 10,\n      \"price\": 5\n", "malformed: $.items[3].price is not a known member")]
    [InlineData("\"maxStack\": 3,", "\"maxStack\": 3,\n      \"maxStack\": 3,", "duplicate: $.items[3].maxStack appears more than once")]
    [InlineData("\"maxQuantity\": 2,\n        \"weight\": 5", "\"maxQuantity\": 2", "missing: $.lootTable.entries[0].weight")]
    [InlineData("\"quantity\": 4", "\"quantity\": \"4\"", "malformed: $.starter.stash[1].quantity must be a 32-bit integer")]
    [InlineData("\"version\": 7,", "\"version\": 7.0,", "malformed: $.lootTable.version must be a 32-bit integer")]
    [InlineData("\"category\": \"armor\"", "\"category\": \"shield\"", "malformed: $.items[1].category must be one of")]
    [InlineData("\"equip\": true", "\"equip\": 1", "malformed: $.starter.operatives[0].grants[0].equip must be true or false")]
    [InlineData("\"weight\": 1\n", "\"weight\": 1,\n", "malformed: not valid JSON")]
    [InlineData("\"formatVersion\": 1,", "\"formatVersion\":  1,", "malformed: the file is not in canonical form")]
    [InlineData("\"stackable\": true,\n      \"maxStack\": 3,", "\"maxStack\": 3,\n      \"stackable\": true,", "malformed: the file is not in canonical form")]
    [InlineData("\"id\": \"item.beta-vest\"", "\"id\": \"item.Beta-vest\"", "malformed: items[1].id 'item.Beta-vest' is not a well-formed id")]
    [InlineData("\"formatVersion\": 1,", "\"formatVersion\": 2,", "incompatible: format version 2 is not supported (this backend reads 1)")]
    public void An_edited_golden_vector_is_rejected(string find, string replace, string expected)
        => AssertRejected(Edited(find, replace), expected);

    static CatalogueDocument Item(CatalogueDocument d, int index, Func<CatalogueItem, CatalogueItem> change) =>
        d with { Items = [.. d.Items.Select((item, i) => i == index ? change(item) : item)] };

    static CatalogueDocument Loot(CatalogueDocument d, Func<LootTable, LootTable> change) => d with { LootTable = change(d.LootTable) };

    static CatalogueDocument Track(CatalogueDocument d, Func<Progression, Progression> change) => d with { Progression = change(d.Progression) };

    static CatalogueDocument Op(CatalogueDocument d, int index, Func<OperativeDefinition, OperativeDefinition> change) =>
        d with { Operatives = [.. d.Operatives.Select((o, i) => i == index ? change(o) : o)] };

    static CatalogueDocument Start(CatalogueDocument d, Func<Starter, Starter> change) => d with { Starter = change(d.Starter) };

    static CatalogueDocument Grants(CatalogueDocument d, Func<IReadOnlyList<StarterGrant>, IReadOnlyList<StarterGrant>> change) =>
        Start(d, s => s with { Operatives = [s.Operatives[0] with { Grants = change(s.Operatives[0].Grants) }] });

    const string Unknown = "00000000-0000-4000-8000-000000000009";

    // Faults the writer can express (canonical text), so only the validator can catch them.
    static readonly Dictionary<string, (Func<CatalogueDocument, CatalogueDocument> Mutate, string Expected)> Mutations = new()
    {
        ["duplicate item"] = (d => d with { Items = [.. d.Items, d.Items[0]] }, "duplicate: items lists 'item.alpha-rifle' more than once"),
        ["unsorted items"] = (d => d with { Items = [d.Items[1], d.Items[0], d.Items[2], d.Items[3]] }, "malformed: items must be in ascending id order"),
        ["no items"] = (d => d with { Items = [] }, "missing: items is empty"),
        ["weapon in the armor slot"] = (d => Item(d, 0, i => i with { Slot = ItemSlot.Armor }), "incompatible: items[0] 'item.alpha-rifle': a weapon must use the weapon slot"),
        ["stackable flag disagrees"] = (d => Item(d, 3, i => i with { Stackable = false }), "incompatible: items[3] 'item.gamma-kit': stackable must be true exactly when the maximum stack is above 1"),
        ["consumable without healing"] = (d => Item(d, 3, i => i with { HealAmount = 0 }), "incompatible: items[3] 'item.gamma-kit': a consumable needs a heal amount of at least 1"),
        ["stacking equipment"] = (d => Item(d, 1, i => i with { Stackable = true, MaxStack = 2 }), "incompatible: items[1] 'item.beta-vest': equipment cannot stack"),
        ["maximum stack below 1"] = (d => Item(d, 1, i => i with { MaxStack = 0 }), "malformed: items[1] 'item.beta-vest': the maximum stack is below 1"),
        ["loot from an unknown item"] = (d => Loot(d, t => t with { Entries = [t.Entries[0] with { ItemId = "item.unknown" }, t.Entries[1]] }), "incompatible: lootTable.entries[0]: unknown item 'item.unknown'"),
        ["loot weight 0"] = (d => Loot(d, t => t with { Entries = [t.Entries[0] with { Weight = 0 }, t.Entries[1]] }), "malformed: lootTable.entries[0]: the weight is below 1"),
        ["loot quantity range empty"] = (d => Loot(d, t => t with { Entries = [t.Entries[0] with { MinQuantity = 3 }, t.Entries[1]] }), "malformed: lootTable.entries[0]: the quantity range is empty"),
        ["loot items range empty"] = (d => Loot(d, t => t with { MinItems = 4 }), "malformed: lootTable: the items per container range is empty"),
        ["loot without entries"] = (d => Loot(d, t => t with { Entries = [] }), "missing: lootTable.entries is empty"),
        ["negative container count"] = (d => Loot(d, t => t with { ContainerCount = -1 }), "malformed: lootTable: the container count is negative"),
        ["loot version 0"] = (d => Loot(d, t => t with { Version = 0 }), "malformed: lootTable: the version must be at least 1"),
        ["thresholds not from 0"] = (d => Track(d, p => p with { XpThresholds = [5, 10, 30] }), "malformed: progression.xpThresholds: rank 1 must need 0 XP"),
        ["thresholds not increasing"] = (d => Track(d, p => p with { XpThresholds = [0, 30, 30] }), "malformed: progression.xpThresholds: thresholds must strictly increase (rank 3)"),
        ["no thresholds"] = (d => Track(d, p => p with { XpThresholds = [] }), "missing: progression.xpThresholds is empty"),
        ["negative completion XP"] = (d => Track(d, p => p with { MissionCompletionXp = -1 }), "malformed: progression.missionCompletionXp is negative"),
        ["duplicate choice"] = (d => Track(d, p => p with { ChoiceIds = ["choice.one", "choice.one"] }), "duplicate: progression.choiceIds lists 'choice.one' more than once"),
        ["unsorted choices"] = (d => Track(d, p => p with { ChoiceIds = ["choice.two", "choice.one"] }), "malformed: progression.choiceIds must be in ascending id order"),
        ["no operatives"] = (d => d with { Operatives = [] }, "missing: operatives is empty"),
        ["duplicate operative"] = (d => d with { Operatives = [d.Operatives[0], d.Operatives[0], d.Operatives[1]] }, $"duplicate: operatives lists '{Golden.One}' more than once"),
        ["unsorted operatives"] = (d => d with { Operatives = [d.Operatives[1], d.Operatives[0]] }, "malformed: operatives must be in ascending id order"),
        ["five abilities"] = (d => Op(d, 1, o => o with { AbilityIds = ["ability.a", "ability.b", "ability.c", "ability.d", "ability.e"] }), $"incompatible: operatives[1] '{Golden.Two}': more than 4 abilities"),
        ["same ability twice"] = (d => Op(d, 1, o => o with { AbilityIds = ["ability.a", "ability.a"] }), "duplicate: operatives[1].abilityIds lists 'ability.a' more than once"),
        ["starter for an unknown operative"] = (d => Start(d, s => s with { Operatives = [s.Operatives[0] with { OperativeId = Unknown }] }), $"incompatible: starter.operatives[0]: unknown operative '{Unknown}'"),
        ["starter operative twice"] = (d => Start(d, s => s with { Operatives = [s.Operatives[0], s.Operatives[0]] }), $"duplicate: starter.operatives lists '{Golden.One}' more than once"),
        ["grant of an unknown item"] = (d => Grants(d, g => [g[0] with { ItemId = "item.unknown" }, g[1]]), "incompatible: starter.operatives[0].grants[0]: unknown item 'item.unknown'"),
        ["grant quantity 0"] = (d => Grants(d, g => [g[0], g[1] with { Quantity = 0 }]), "malformed: starter.operatives[0].grants[1]: the quantity is below 1"),
        ["equipping a consumable"] = (d => Grants(d, g => [g[0], g[1] with { Equip = true }]), "incompatible: starter.operatives[0].grants[1]: item 'item.gamma-kit' has no equipment slot, so it cannot be equipped"),
        ["two items for one slot"] = (d => Grants(d, g => [g[0], g[0], g[1]]), "incompatible: starter.operatives[0].grants[1]: a second item to equip in the Weapon slot"),
        ["stash of an unknown item"] = (d => Start(d, s => s with { Stash = [s.Stash[0] with { ItemId = "item.unknown" }, s.Stash[1]] }), "incompatible: starter.stash[0]: unknown item 'item.unknown'"),
        ["stash quantity 0"] = (d => Start(d, s => s with { Stash = [s.Stash[0], s.Stash[1] with { Quantity = 0 }] }), "malformed: starter.stash[1]: the quantity is below 1"),
    };

    public static TheoryData<string> MutationNames()
    {
        var names = new TheoryData<string>();
        foreach (var name in Mutations.Keys)
            names.Add(name);
        return names;
    }

    [Theory]
    [MemberData(nameof(MutationNames))]
    public void A_semantic_fault_is_rejected(string name)
    {
        var (mutate, expected) = Mutations[name];
        AssertRejected(CanonicalCatalogueJson.ToBytes(mutate(Golden.Document())), expected);
    }

    [Fact]
    public void Every_problem_is_reported_at_once()
    {
        var d = Track(Loot(Golden.Document(), t => t with { Version = 0 }), p => p with { MissionCompletionXp = -1 });
        var e = Assert.Throws<CatalogueException>(() => GameCatalogue.Load(CanonicalCatalogueJson.ToBytes(d)));
        Assert.Contains(e.Errors, x => x.Contains("the version must be at least 1", StringComparison.Ordinal));
        Assert.Contains(e.Errors, x => x.Contains("missionCompletionXp is negative", StringComparison.Ordinal));
    }
}
