using System.Collections.Generic;
using System.Text;

namespace Blackglass.Backend.Core
{
    /// <summary>
    /// RFC 8259 JSON over strictly validated UTF-8, accepting exactly what the backend's System.Text.Json reader accepts
    /// (JsonDocument with no comments, no trailing commas and MaxDepth 64): no byte-order mark, no comment, no trailing comma
    /// or content, no leading zero, no raw control character in a string, at most 64 open containers. Duplicate member names
    /// are kept in order (the codecs refuse them in their own order). A string or member name whose escapes form a lone
    /// surrogate is kept and flagged: System.Text.Json accepts it too and fails only when the value is decoded. Byte-level and
    /// hand-written, so its answers don't depend on the runtime's encoder (Mono or CoreCLR).
    /// </summary>
    public static class StrictJsonReader
    {
        public const int MaxDepth = 64;

        public static bool TryRead(byte[] utf8, out JsonNode root)
        {
            root = null;
            if (utf8 == null || !IsValidUtf8(utf8)) return false;
            var parser = new Parser(utf8);
            if (!parser.TryValue(0, out var value)) return false;
            parser.SkipWhitespace();
            if (!parser.AtEnd) return false;
            root = value;
            return true;
        }

        /// <summary>RFC 3629: no overlong form, no encoded surrogate, nothing above U+10FFFF.</summary>
        public static bool IsValidUtf8(byte[] bytes)
        {
            var i = 0;
            while (i < bytes.Length)
            {
                var b = bytes[i];
                if (b < 0x80) { i++; continue; }
                int length, min, cp;
                if ((b & 0xE0) == 0xC0) { length = 2; min = 0x80; cp = b & 0x1F; }
                else if ((b & 0xF0) == 0xE0) { length = 3; min = 0x800; cp = b & 0x0F; }
                else if ((b & 0xF8) == 0xF0) { length = 4; min = 0x10000; cp = b & 0x07; }
                else return false;
                if (i + length > bytes.Length) return false;
                for (var k = 1; k < length; k++)
                {
                    var c = bytes[i + k];
                    if ((c & 0xC0) != 0x80) return false;
                    cp = (cp << 6) | (c & 0x3F);
                }
                if (cp < min || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF)) return false;
                i += length;
            }
            return true;
        }

        sealed class Parser
        {
            readonly byte[] bytes;
            int position;

            public Parser(byte[] bytes) => this.bytes = bytes;

            public bool AtEnd => position >= bytes.Length;

            public void SkipWhitespace()
            {
                while (position < bytes.Length)
                {
                    var b = bytes[position];
                    if (b != ' ' && b != '\t' && b != '\n' && b != '\r') return;
                    position++;
                }
            }

            /// <param name="open">Containers already open around this value.</param>
            public bool TryValue(int open, out JsonNode value)
            {
                value = null;
                SkipWhitespace();
                if (AtEnd) return false;
                switch (bytes[position])
                {
                    case (byte)'{': return TryObject(open, out value);
                    case (byte)'[': return TryArray(open, out value);
                    case (byte)'"':
                        if (!TryString(out var text, out var invalid)) return false;
                        value = new JsonString(text, invalid);
                        return true;
                    case (byte)'t': return TryLiteral("true", JsonBool.True, out value);
                    case (byte)'f': return TryLiteral("false", JsonBool.False, out value);
                    case (byte)'n': return TryLiteral("null", JsonNull.Instance, out value);
                    default: return TryNumber(out value);
                }
            }

            bool TryLiteral(string word, JsonNode node, out JsonNode value)
            {
                value = null;
                if (position + word.Length > bytes.Length) return false;
                for (var i = 0; i < word.Length; i++)
                    if (bytes[position + i] != word[i]) return false;
                position += word.Length;
                value = node;
                return true;
            }

            bool TryObject(int open, out JsonNode value)
            {
                value = null;
                if (open + 1 > MaxDepth) return false;
                position++; // '{'
                var members = new List<JsonMember>();
                SkipWhitespace();
                if (!AtEnd && bytes[position] == '}')
                {
                    position++;
                    value = new JsonObject(members);
                    return true;
                }
                while (true)
                {
                    SkipWhitespace();
                    if (AtEnd || bytes[position] != '"' || !TryString(out var name, out var invalid)) return false;
                    SkipWhitespace();
                    if (AtEnd || bytes[position] != ':') return false;
                    position++;
                    if (!TryValue(open + 1, out var member)) return false;
                    members.Add(new JsonMember(name, member, invalid));
                    SkipWhitespace();
                    if (AtEnd) return false;
                    var next = bytes[position++];
                    if (next == '}') break;
                    if (next != ',') return false;
                }
                value = new JsonObject(members);
                return true;
            }

            bool TryArray(int open, out JsonNode value)
            {
                value = null;
                if (open + 1 > MaxDepth) return false;
                position++; // '['
                var items = new List<JsonNode>();
                SkipWhitespace();
                if (!AtEnd && bytes[position] == ']')
                {
                    position++;
                    value = new JsonArray(items);
                    return true;
                }
                while (true)
                {
                    if (!TryValue(open + 1, out var item)) return false;
                    items.Add(item);
                    SkipWhitespace();
                    if (AtEnd) return false;
                    var next = bytes[position++];
                    if (next == ']') break;
                    if (next != ',') return false;
                }
                value = new JsonArray(items);
                return true;
            }

            bool TryString(out string text, out bool invalidUtf16)
            {
                text = null;
                invalidUtf16 = false;
                position++; // the opening quote
                var builder = new StringBuilder();
                while (true)
                {
                    if (AtEnd) return false;
                    var b = bytes[position];
                    if (b == '"') { position++; break; }
                    if (b < 0x20) return false;
                    if (b == '\\')
                    {
                        if (position + 1 >= bytes.Length) return false;
                        var e = bytes[position + 1];
                        position += 2;
                        switch (e)
                        {
                            case (byte)'"': builder.Append('"'); break;
                            case (byte)'\\': builder.Append('\\'); break;
                            case (byte)'/': builder.Append('/'); break;
                            case (byte)'b': builder.Append('\b'); break;
                            case (byte)'f': builder.Append('\f'); break;
                            case (byte)'n': builder.Append('\n'); break;
                            case (byte)'r': builder.Append('\r'); break;
                            case (byte)'t': builder.Append('\t'); break;
                            case (byte)'u':
                                if (position + 4 > bytes.Length) return false;
                                var unit = 0;
                                for (var i = 0; i < 4; i++)
                                {
                                    var h = Hex(bytes[position + i]);
                                    if (h < 0) return false;
                                    unit = (unit << 4) | h;
                                }
                                position += 4;
                                builder.Append((char)unit);
                                break;
                            default: return false;
                        }
                        continue;
                    }
                    if (b < 0x80) { builder.Append((char)b); position++; continue; }
                    // Multi-byte UTF-8, already validated: decode one code point.
                    int length = (b & 0xE0) == 0xC0 ? 2 : (b & 0xF0) == 0xE0 ? 3 : 4;
                    int cp = length == 2 ? b & 0x1F : length == 3 ? b & 0x0F : b & 0x07;
                    for (var k = 1; k < length; k++) cp = (cp << 6) | (bytes[position + k] & 0x3F);
                    position += length;
                    if (cp < 0x10000) builder.Append((char)cp);
                    else
                    {
                        cp -= 0x10000;
                        builder.Append((char)(0xD800 + (cp >> 10)));
                        builder.Append((char)(0xDC00 + (cp & 0x3FF)));
                    }
                }
                text = builder.ToString();
                invalidUtf16 = HasLoneSurrogate(text);
                return true;
            }

            bool TryNumber(out JsonNode value)
            {
                value = null;
                var start = position;
                if (Peek() == '-') position++;
                if (Peek() == '0') position++;
                else if (IsDigit1To9(Peek())) { while (IsDigit(Peek())) position++; }
                else return false;
                if (Peek() == '.')
                {
                    position++;
                    if (!IsDigit(Peek())) return false;
                    while (IsDigit(Peek())) position++;
                }
                if (Peek() == 'e' || Peek() == 'E')
                {
                    position++;
                    if (Peek() == '+' || Peek() == '-') position++;
                    if (!IsDigit(Peek())) return false;
                    while (IsDigit(Peek())) position++;
                }
                value = JsonNumber.FromRaw(Encoding.ASCII.GetString(bytes, start, position - start));
                return true;
            }

            int Peek() => position < bytes.Length ? bytes[position] : -1;

            static bool IsDigit(int b) => b >= '0' && b <= '9';

            static bool IsDigit1To9(int b) => b >= '1' && b <= '9';

            static int Hex(byte b) =>
                b >= '0' && b <= '9' ? b - '0' : b >= 'a' && b <= 'f' ? b - 'a' + 10 : b >= 'A' && b <= 'F' ? b - 'A' + 10 : -1;
        }

        public static bool HasLoneSurrogate(string text)
        {
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { i++; continue; }
                    return true;
                }
                if (char.IsLowSurrogate(c)) return true;
            }
            return false;
        }

        /// <summary>The UTF-8 byte count of a string without lone surrogates (Encoding.UTF8.GetByteCount).</summary>
        public static int Utf8ByteCount(string text)
        {
            var count = 0;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c < 0x80) count += 1;
                else if (c < 0x800) count += 2;
                else if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { count += 4; i++; }
                else count += 3;
            }
            return count;
        }
    }
}
