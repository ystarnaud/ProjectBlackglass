using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Blackglass.BackendTools
{
    /// <summary>
    /// Writes a CatalogueDocument in the one canonical text form the backend also writes (CanonicalCatalogueJson in
    /// Backend/src/Accounts.Domain): UTF-8 without a BOM, LF line ends, two-space indentation, one member or element per
    /// line, members in a fixed order, "[]" for an empty array, invariant integers and one final newline. Every string is
    /// an id or an enum word, so nothing is escaped; anything else is refused. The document is written in the order it
    /// holds (CatalogueBuilder sorts). catalogueVersion is "sha256:" and the lowercase hex SHA-256 of these bytes.
    /// </summary>
    public static class CanonicalCatalogueWriter
    {
        public const int MaxIdLength = 64;
        static readonly Regex IdPattern = new Regex("^[a-z0-9]+(?:[.-][a-z0-9]+)*$", RegexOptions.CultureInvariant);
        static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public static bool IsWellFormedId(string value) =>
            value != null && value.Length > 0 && value.Length <= MaxIdLength && IdPattern.IsMatch(value);

        public static byte[] ToBytes(CatalogueDocument document) => Utf8NoBom.GetBytes(Write(document));

        public static string VersionOf(byte[] bytes)
        {
            byte[] hash;
            using (var sha = SHA256.Create())
                hash = sha.ComputeHash(bytes);
            var text = new StringBuilder("sha256:");
            foreach (var b in hash)
                text.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return text.ToString();
        }

        public static string Write(CatalogueDocument d)
        {
            var root = new Obj()
                .Add("formatVersion", Int(CatalogueDocument.FormatVersion))
                .Add("items", Elements(d.Items, item => new Obj()
                    .Add("id", Str(item.Id))
                    .Add("category", Str(item.Category))
                    .Add("slot", Str(item.Slot))
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
            public readonly List<KeyValuePair<string, Node>> Members = new List<KeyValuePair<string, Node>>();

            public Obj Add(string key, Node value)
            {
                Members.Add(new KeyValuePair<string, Node>(key, value));
                return this;
            }
        }

        sealed class Arr : Node
        {
            public readonly List<Node> Items = new List<Node>();
        }

        sealed class Literal : Node
        {
            public readonly string Text;
            public Literal(string text) { Text = text; }
        }

        static Node Int(int value) => new Literal(value.ToString(CultureInfo.InvariantCulture));

        static Node Bool(bool value) => new Literal(value ? "true" : "false");

        static Node Str(string value)
        {
            if (!IsWellFormedId(value))
                throw new ArgumentException($"'{value}' is not a well-formed catalogue id or word.", nameof(value));
            return new Literal("\"" + value + "\"");
        }

        static Node Elements<T>(IEnumerable<T> values, Func<T, Node> element)
        {
            var array = new Arr();
            foreach (var value in values)
                array.Items.Add(element(value));
            return array;
        }

        static void Render(StringBuilder text, Node node, int depth)
        {
            switch (node)
            {
                case Obj obj:
                    text.Append("{\n");
                    for (var i = 0; i < obj.Members.Count; i++)
                    {
                        Indent(text, depth + 1);
                        text.Append('"').Append(obj.Members[i].Key).Append("\": ");
                        Render(text, obj.Members[i].Value, depth + 1);
                        text.Append(i + 1 < obj.Members.Count ? ",\n" : "\n");
                    }
                    Indent(text, depth);
                    text.Append('}');
                    break;
                case Arr arr when arr.Items.Count == 0:
                    text.Append("[]");
                    break;
                case Arr arr:
                    text.Append("[\n");
                    for (var i = 0; i < arr.Items.Count; i++)
                    {
                        Indent(text, depth + 1);
                        Render(text, arr.Items[i], depth + 1);
                        text.Append(i + 1 < arr.Items.Count ? ",\n" : "\n");
                    }
                    Indent(text, depth);
                    text.Append(']');
                    break;
                case Literal literal:
                    text.Append(literal.Text);
                    break;
            }
        }

        static void Indent(StringBuilder text, int depth) => text.Append(' ', depth * 2);
    }
}
