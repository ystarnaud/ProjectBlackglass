using System.Text;
using System.Text.Json;
using Blackglass.Accounts.Domain.Advancement;
using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.Domain.Ids;
using Blackglass.Accounts.Domain.Inventory;

namespace Blackglass.Accounts.Domain.Attempts;

/// <summary>One owned operative as the roster holds it: its bigint id, its authored catalogue definition id, XP and the picks made, in order.</summary>
public sealed record SnapshotOperativeSource(long OperativeId, string DefinitionId, int Experience, IReadOnlyList<string> Choices);

/// <summary>A non-stackable item in a deployed operative's bag.</summary>
public sealed record SnapshotInstance(long ItemId, string DefinitionId);

/// <summary>The fungible units of one definition in a deployed operative's bag.</summary>
public sealed record SnapshotStack(string DefinitionId, int Quantity);

/// <summary>The item id equipped in each slot, or null.</summary>
public sealed record SnapshotEquipment(long? Weapon, long? Armor, long? Utility);

/// <summary>One deployed operative at squad slot Slot (its index in the requested order).</summary>
public sealed record SnapshotOperative(int Slot, long OperativeId, string DefinitionId, int Experience, int Rank, IReadOnlyList<string> Choices,
    IReadOnlyList<SnapshotInstance> Instances, IReadOnlyList<SnapshotStack> Stacks, SnapshotEquipment Equipment);

/// <summary>An item in the bag of the operative at OperativeIndex whose definition the catalogue does not know.</summary>
public sealed record UnknownItemDefinition(int OperativeIndex, string DefinitionId);

/// <summary>A built snapshot and the bag items whose definitions the catalogue does not know (admission refuses those: 422 UnknownItem).</summary>
public sealed record LoadoutBuild(LoadoutSnapshot Snapshot, IReadOnlyList<UnknownItemDefinition> UnknownItems);

/// <summary>
/// The deployment snapshot taken at admission (owner rulings S7-D8, S7-D9): the account id, its inventory version and the
/// catalogue version, then the chosen operatives in the requested order (slot 0..n−1), each with both identities (the
/// owned bigint id and the authored definition id), XP, rank, picks, bag contents and equipment. The stash and every other
/// operative are left out: the persistent inventory stays locked while the attempt is active. ToJson() is deterministic:
/// members in a fixed order, owned ids in Base58, instances sorted by item id and stacks by definition id (ordinal), no
/// whitespace. The snapshot is immutable once stored.
/// </summary>
public sealed record LoadoutSnapshot(long AccountId, long InventoryVersion, string CatalogueVersion, IReadOnlyList<SnapshotOperative> Operatives)
{
    /// <summary>
    /// Builds the snapshot of `requestedOrder` (1 or more distinct ids, each listed once in `operatives`) from the account's
    /// roster rows and inventory. Admission validates ownership, team size and duplicates before calling; anything
    /// malformed here (a non-positive account id or inventory version, an unknown or repeated id, two items in one slot, an
    /// equipped item outside a bag, a repeated or non-positive stack) is an ArgumentException. Rank comes from the
    /// catalogue's progression track.
    /// </summary>
    public static LoadoutBuild Build(long accountId, long inventoryVersion, string catalogueVersion, IReadOnlyList<long> requestedOrder,
        IReadOnlyList<SnapshotOperativeSource> operatives, InventoryModel inventory, CatalogueDocument catalogue)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(accountId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inventoryVersion); // inventories.version starts at 1 (0007 checks >= 1)
        ArgumentException.ThrowIfNullOrEmpty(catalogueVersion);
        ArgumentNullException.ThrowIfNull(requestedOrder);
        ArgumentNullException.ThrowIfNull(operatives);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(catalogue);
        if (requestedOrder.Count == 0) throw new ArgumentException("At least one operative is deployed.", nameof(requestedOrder));
        if (requestedOrder.Distinct().Count() != requestedOrder.Count) throw new ArgumentException("An operative is requested twice.", nameof(requestedOrder));

        var roster = new Dictionary<long, SnapshotOperativeSource>();
        foreach (var source in operatives)
        {
            if (!roster.TryAdd(source.OperativeId, source)) throw new ArgumentException("The roster lists an operative twice.", nameof(operatives));
        }
        var known = catalogue.Items.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);

        var slots = new List<SnapshotOperative>(requestedOrder.Count);
        var unknown = new List<UnknownItemDefinition>();
        foreach (var operativeId in requestedOrder)
        {
            if (!roster.TryGetValue(operativeId, out var source)) throw new ArgumentException("A requested operative is not in the roster.", nameof(requestedOrder));
            var slot = slots.Count;

            var instances = new List<SnapshotInstance>();
            long? weapon = null, armor = null, utility = null;
            foreach (var held in inventory.Instances)
            {
                if (held.OperativeId is null && held.Equipped is not null) throw new ArgumentException("An item in the stash is marked equipped.", nameof(inventory));
                if (held.OperativeId != operativeId) continue;
                instances.Add(new SnapshotInstance(held.ItemId, held.DefinitionId));
                switch (held.Equipped)
                {
                    case null: break;
                    case ItemSlot.Weapon: weapon = Fill(weapon, held.ItemId); break;
                    case ItemSlot.Armor: armor = Fill(armor, held.ItemId); break;
                    case ItemSlot.Utility: utility = Fill(utility, held.ItemId); break;
                    default: throw new ArgumentException("An equipped item names no slot.", nameof(inventory));
                }
            }
            var stacks = new List<SnapshotStack>();
            foreach (var held in inventory.Stacks.Where(s => s.OperativeId == operativeId))
            {
                if (held.Quantity < 1) throw new ArgumentException("A stack holds at least one unit.", nameof(inventory));
                if (stacks.Any(s => s.DefinitionId == held.DefinitionId)) throw new ArgumentException("A bag holds one stack row per definition.", nameof(inventory));
                stacks.Add(new SnapshotStack(held.DefinitionId, held.Quantity));
            }
            instances.Sort((a, b) => a.ItemId.CompareTo(b.ItemId));
            stacks.Sort((a, b) => string.CompareOrdinal(a.DefinitionId, b.DefinitionId));

            unknown.AddRange(instances.Select(i => i.DefinitionId).Concat(stacks.Select(s => s.DefinitionId))
                .Where(d => !known.Contains(d)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                .Select(d => new UnknownItemDefinition(slot, d)));

            slots.Add(new SnapshotOperative(slot, operativeId, source.DefinitionId, source.Experience,
                ProgressionRules.RankFor(catalogue.Progression, source.Experience), [.. source.Choices], instances, stacks,
                new SnapshotEquipment(weapon, armor, utility)));
        }
        return new LoadoutBuild(new LoadoutSnapshot(accountId, inventoryVersion, catalogueVersion, slots), unknown);
    }

    static long Fill(long? current, long itemId) =>
        current is null ? itemId : throw new ArgumentException("Two items are equipped in one slot.", "inventory");

    public string ToJson()
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("accountId", Base58Id.Format(AccountId));
            w.WriteNumber("inventoryVersion", InventoryVersion);
            w.WriteString("catalogueVersion", CatalogueVersion);
            w.WriteStartArray("operatives");
            foreach (var o in Operatives)
            {
                w.WriteStartObject();
                w.WriteNumber("slot", o.Slot);
                w.WriteString("operativeId", Base58Id.Format(o.OperativeId));
                w.WriteString("definitionId", o.DefinitionId);
                w.WriteNumber("experience", o.Experience);
                w.WriteNumber("rank", o.Rank);
                w.WriteStartArray("choices");
                foreach (var choice in o.Choices) w.WriteStringValue(choice);
                w.WriteEndArray();
                w.WriteStartObject("bag");
                w.WriteStartArray("instances");
                foreach (var i in o.Instances.OrderBy(i => i.ItemId))
                {
                    w.WriteStartObject();
                    w.WriteString("itemId", Base58Id.Format(i.ItemId));
                    w.WriteString("definitionId", i.DefinitionId);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartArray("stacks");
                foreach (var s in o.Stacks.OrderBy(s => s.DefinitionId, StringComparer.Ordinal))
                {
                    w.WriteStartObject();
                    w.WriteString("definitionId", s.DefinitionId);
                    w.WriteNumber("quantity", s.Quantity);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
                w.WriteStartObject("equipment");
                WriteSlot(w, "weapon", o.Equipment.Weapon);
                WriteSlot(w, "armor", o.Equipment.Armor);
                WriteSlot(w, "utility", o.Equipment.Utility);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Reads a stored snapshot (the <see cref="ToJson"/> shape; jsonb may have reordered its members). Anything else is a
    /// FormatException: a stored snapshot is backend-written, so a malformed one is an invariant failure, not bad input.</summary>
    public static LoadoutSnapshot Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        static long Id(JsonElement e) => Base58Id.TryParse(e.GetString(), out var id) ? id : throw new FormatException("A snapshot id is not canonical Base58.");
        static long? OptionalId(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : Id(e);
        var operatives = root.GetProperty("operatives").EnumerateArray().Select(o =>
        {
            var bag = o.GetProperty("bag");
            var equipment = o.GetProperty("equipment");
            return new SnapshotOperative(o.GetProperty("slot").GetInt32(), Id(o.GetProperty("operativeId")), o.GetProperty("definitionId").GetString()!,
                o.GetProperty("experience").GetInt32(), o.GetProperty("rank").GetInt32(), [.. o.GetProperty("choices").EnumerateArray().Select(c => c.GetString()!)],
                [.. bag.GetProperty("instances").EnumerateArray().Select(i => new SnapshotInstance(Id(i.GetProperty("itemId")), i.GetProperty("definitionId").GetString()!))],
                [.. bag.GetProperty("stacks").EnumerateArray().Select(s => new SnapshotStack(s.GetProperty("definitionId").GetString()!, s.GetProperty("quantity").GetInt32()))],
                new SnapshotEquipment(OptionalId(equipment.GetProperty("weapon")), OptionalId(equipment.GetProperty("armor")), OptionalId(equipment.GetProperty("utility"))));
        }).OrderBy(o => o.Slot).ToList();
        return new LoadoutSnapshot(Id(root.GetProperty("accountId")), root.GetProperty("inventoryVersion").GetInt64(), root.GetProperty("catalogueVersion").GetString()!, operatives);
    }

    static void WriteSlot(Utf8JsonWriter w, string name, long? itemId)
    {
        if (itemId is { } id) w.WriteString(name, Base58Id.Format(id));
        else w.WriteNull(name);
    }
}
