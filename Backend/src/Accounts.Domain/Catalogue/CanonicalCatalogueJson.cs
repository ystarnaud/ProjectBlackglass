using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Blackglass.Accounts.Domain.Catalogue;

/// <summary>
/// The one canonical text form of a catalogue, identical to Unity's CanonicalCatalogueWriter (Assets/_Project/Editor/
/// Backend): UTF-8 without a BOM, LF line ends, two-space indentation, one member or element per line, members in a fixed
/// order, "[]" for an empty array, invariant integers and one final newline. Strings are ids or enum words, so nothing is
/// escaped; anything else is refused. The document is written in the order it holds.
/// </summary>
public static partial class CanonicalCatalogueJson
{
    public const int MaxIdLength = 64;

    // \z, not $: .NET's $ also matches before a final newline.
    [GeneratedRegex(@"^[a-z0-9]+(?:[.-][a-z0-9]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    public static bool IsWellFormedId(string? value) => value is { Length: > 0 and <= MaxIdLength } && IdPattern().IsMatch(value);

    public static string Word(ItemCategory category) => category switch
    {
        ItemCategory.Weapon => "weapon",
        ItemCategory.Armor => "armor",
        ItemCategory.Utility => "utility",
        ItemCategory.Consumable => "consumable",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "No catalogue word for this category."),
    };

    public static string Word(ItemSlot slot) => slot switch
    {
        ItemSlot.None => "none",
        ItemSlot.Weapon => "weapon",
        ItemSlot.Armor => "armor",
        ItemSlot.Utility => "utility",
        _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, "No catalogue word for this slot."),
    };

    public static byte[] ToBytes(CatalogueDocument document) => new UTF8Encoding(false).GetBytes(Write(document));

    public static string Write(CatalogueDocument d)
    {
        var root = new Obj()
            .Add("formatVersion", Int(d.FormatVersion))
            .Add("items", Elements(d.Items, item => new Obj()
                .Add("id", Str(item.Id))
                .Add("category", Str(Word(item.Category)))
                .Add("slot", Str(Word(item.Slot)))
                .Add("stackable", Bool(item.Stackable))
                .Add("maxStack", Int(item.MaxStack))
                .Add("healAmount", Int(item.HealAmount))))
            .Add("lootTable", new Obj()
                .Add("version", Int(d.LootTable.Version))
                .Add("containerCount", Int(d.LootTable.ContainerCount))
                .Add("minItems", Int(d.LootTable.MinItems))
                .Add("maxItems", Int(d.LootTable.MaxItems))
                .Add("entries", Elements(d.LootTable.Entries, entry => new Obj()
                    .Add("itemId", Str(entry.ItemId))
                    .Add("minQuantity", Int(entry.MinQuantity))
                    .Add("maxQuantity", Int(entry.MaxQuantity))
                    .Add("weight", Int(entry.Weight)))))
            .Add("progression", new Obj()
                .Add("xpThresholds", Elements(d.Progression.XpThresholds, Int))
                .Add("missionCompletionXp", Int(d.Progression.MissionCompletionXp))
                .Add("choiceIds", Elements(d.Progression.ChoiceIds, Str)))
            .Add("operatives", Elements(d.Operatives, operative => new Obj()
                .Add("id", Str(operative.Id))
                .Add("roleId", Str(operative.RoleId))
                .Add("abilityIds", Elements(operative.AbilityIds, Str))))
            .Add("starter", new Obj()
                .Add("operatives", Elements(d.Starter.Operatives, starter => new Obj()
                    .Add("operativeId", Str(starter.OperativeId))
                    .Add("grants", Elements(starter.Grants, grant => new Obj()
                        .Add("itemId", Str(grant.ItemId))
                        .Add("quantity", Int(grant.Quantity))
                        .Add("equip", Bool(grant.Equip))))))
                .Add("stash", Elements(d.Starter.Stash, grant => new Obj()
                    .Add("itemId", Str(grant.ItemId))
                    .Add("quantity", Int(grant.Quantity)))));
        var text = new StringBuilder();
        Render(text, root, 0);
        return text.Append('\n').ToString();
    }

    abstract class Node { }

    sealed class Obj : Node
    {
        public List<KeyValuePair<string, Node>> Members { get; } = [];

        public Obj Add(string key, Node value)
        {
            Members.Add(new(key, value));
            return this;
        }
    }

    sealed class Arr(List<Node> items) : Node
    {
        public List<Node> Items { get; } = items;
    }

    sealed class Literal(string text) : Node
    {
        public string Text { get; } = text;
    }

    static Node Int(int value) => new Literal(value.ToString(CultureInfo.InvariantCulture));

    static Node Bool(bool value) => new Literal(value ? "true" : "false");

    static Node Str(string value) => IsWellFormedId(value)
        ? new Literal("\"" + value + "\"")
        : throw new ArgumentException($"'{value}' is not a well-formed catalogue id or word.", nameof(value));

    static Node Elements<T>(IEnumerable<T> values, Func<T, Node> element) => new Arr([.. values.Select(element)]);

    static void Render(StringBuilder text, Node node, int depth)
    {
        switch (node)
        {
            case Obj obj:
                text.Append("{\n");
                for (var i = 0; i < obj.Members.Count; i++)
                {
                    text.Append(' ', (depth + 1) * 2).Append('"').Append(obj.Members[i].Key).Append("\": ");
                    Render(text, obj.Members[i].Value, depth + 1);
                    text.Append(i + 1 < obj.Members.Count ? ",\n" : "\n");
                }
                text.Append(' ', depth * 2).Append('}');
                break;
            case Arr { Items.Count: 0 }:
                text.Append("[]");
                break;
            case Arr arr:
                text.Append("[\n");
                for (var i = 0; i < arr.Items.Count; i++)
                {
                    text.Append(' ', (depth + 1) * 2);
                    Render(text, arr.Items[i], depth + 1);
                    text.Append(i + 1 < arr.Items.Count ? ",\n" : "\n");
                }
                text.Append(' ', depth * 2).Append(']');
                break;
            case Literal literal:
                text.Append(literal.Text);
                break;
        }
    }
}
