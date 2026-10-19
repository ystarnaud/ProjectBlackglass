using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Blackglass.Backend.Core
{
    /// <summary>
    /// The compact canonical JSON of manifests and receipts, a port of Backend/src/Accounts.Domain/Settlement/CanonicalJson.cs:
    /// UTF-8 without a BOM, no whitespace, object members in ordinal key order (a duplicate member is an ArgumentException),
    /// arrays in the order given (callers sort them by their stated key first), numbers as their text (the codecs build them
    /// only with JsonNumber.FromInt), and minimal string escapes: only '"', '\' and U+0000-U+001F (as \b \t \n \f \r, else
    /// \u00xx in lowercase hex); everything else is raw UTF-8.
    /// </summary>
    public static class CanonicalJsonWriter
    {
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        public static byte[] ToBytes(JsonNode node)
        {
            var text = new StringBuilder();
            Write(text, node);
            return Utf8.GetBytes(text.ToString());
        }

        static void Write(StringBuilder text, JsonNode node)
        {
            switch (node)
            {
                case JsonObject o:
                    var members = new List<JsonMember>(o.Members);
                    members.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                    text.Append('{');
                    for (var i = 0; i < members.Count; i++)
                    {
                        if (i > 0)
                        {
                            if (string.Equals(members[i - 1].Name, members[i].Name, StringComparison.Ordinal))
                                throw new ArgumentException("Duplicate member '" + members[i].Name + "'.", nameof(node));
                            text.Append(',');
                        }
                        WriteString(text, members[i].Name, members[i].NameIsInvalidUtf16);
                        text.Append(':');
                        Write(text, members[i].Value);
                    }
                    text.Append('}');
                    break;
                case JsonArray a:
                    text.Append('[');
                    for (var i = 0; i < a.Items.Count; i++)
                    {
                        if (i > 0) text.Append(',');
                        Write(text, a.Items[i]);
                    }
                    text.Append(']');
                    break;
                case JsonString s:
                    WriteString(text, s.Value, s.IsInvalidUtf16);
                    break;
                case JsonNumber n:
                    text.Append(n.RawText);
                    break;
                case JsonBool b:
                    text.Append(b.Value ? "true" : "false");
                    break;
                case JsonNull _:
                    text.Append("null");
                    break;
                default:
                    throw new ArgumentException("Unknown node.", nameof(node));
            }
        }

        static void WriteString(StringBuilder text, string value, bool invalidUtf16)
        {
            if (invalidUtf16 || StrictJsonReader.HasLoneSurrogate(value))
                throw new ArgumentException("A string with a lone surrogate has no UTF-8 form.", nameof(value));
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
}
