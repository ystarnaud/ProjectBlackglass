using System.Globalization;
using System.Text;

namespace Blackglass.Accounts.Domain.Settlement;

/// <summary>
/// The compact canonical JSON of manifests and receipts (S9-B4, plan §G): UTF-8 without a BOM, no whitespace, object members
/// in ordinal key order, arrays in the order given (callers sort them by their stated key first), integers in invariant
/// decimal with no sign, fraction or exponent beyond a leading '-' for negatives (none occur in format v1), and minimal string
/// escapes: only '"', '\' and the control characters below U+0020 (as \b, \t, \n, \f, \r or \u00xx in lowercase hex); every
/// other character, including non-ASCII, is written as its raw UTF-8. (The catalogue's pretty-printed form,
/// CanonicalCatalogueJson, is a different format and stays separate.)
/// </summary>
public static class CanonicalJson
{
    public abstract record Node;

    public sealed record Obj(IReadOnlyList<KeyValuePair<string, Node>> Members) : Node;

    public sealed record Arr(IReadOnlyList<Node> Items) : Node;

    public sealed record Str(string Value) : Node;

    public sealed record Num(long Value) : Node;

    public sealed record Bool(bool Value) : Node;

    public sealed record Null : Node;

    public static Obj JObj(params (string Key, Node Value)[] members) => new([.. members.Select(m => new KeyValuePair<string, Node>(m.Key, m.Value))]);

    public static Arr JArr(IEnumerable<Node> items) => new([.. items]);

    public static Node JStr(string value) => new Str(value);

    public static Node JNum(long value) => new Num(value);

    public static Node JBool(bool value) => new Bool(value);

    public static Node JStrOrNull(string? value) => value is null ? new Null() : new Str(value);

    public static byte[] ToBytes(Node node)
    {
        var text = new StringBuilder();
        Write(text, node);
        return new UTF8Encoding(false).GetBytes(text.ToString());
    }

    static void Write(StringBuilder text, Node node)
    {
        switch (node)
        {
            case Obj o:
                text.Append('{');
                var first = true;
                string? previous = null;
                foreach (var m in o.Members.OrderBy(m => m.Key, StringComparer.Ordinal))
                {
                    if (previous is not null && string.Equals(previous, m.Key, StringComparison.Ordinal))
                        throw new ArgumentException($"Duplicate member '{m.Key}'.", nameof(node));
                    previous = m.Key;
                    if (!first) text.Append(',');
                    first = false;
                    WriteString(text, m.Key);
                    text.Append(':');
                    Write(text, m.Value);
                }
                text.Append('}');
                break;
            case Arr a:
                text.Append('[');
                for (var i = 0; i < a.Items.Count; i++)
                {
                    if (i > 0) text.Append(',');
                    Write(text, a.Items[i]);
                }
                text.Append(']');
                break;
            case Str s:
                WriteString(text, s.Value);
                break;
            case Num n:
                text.Append(n.Value.ToString(CultureInfo.InvariantCulture));
                break;
            case Bool b:
                text.Append(b.Value ? "true" : "false");
                break;
            case Null:
                text.Append("null");
                break;
            default:
                throw new ArgumentException("Unknown node.", nameof(node));
        }
    }

    static void WriteString(StringBuilder text, string value)
    {
        text.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': text.Append("\\\""); break;
                case '\\': text.Append("\\\\"); break;
                case '\b': text.Append("\\b"); break;
                case '\t': text.Append("\\t"); break;
                case '\n': text.Append("\\n"); break;
                case '\f': text.Append("\\f"); break;
                case '\r': text.Append("\\r"); break;
                default:
                    if (c < 0x20) text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else text.Append(c);
                    break;
            }
        }
        text.Append('"');
    }
}
