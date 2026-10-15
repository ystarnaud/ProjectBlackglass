using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>
    /// Reads the shared contract vectors under Backend/contracts (the same files the backend's xUnit tests run) by a path
    /// relative to the project root. The vectors use mixed-type arrays and nulls, which JsonUtility cannot read, and the
    /// EditMode test assembly references no JSON library, so this is a small strict reader: objects become
    /// Dictionary&lt;string, object&gt;, arrays List&lt;object&gt;, numbers long or double, plus string, bool and null.
    /// </summary>
    internal static class ContractVectors
    {
        public static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;

        public static Dictionary<string, object> Load(params string[] pathUnderBackendContracts)
        {
            var parts = new List<string> { ProjectRoot, "Backend", "contracts" };
            parts.AddRange(pathUnderBackendContracts);
            var path = Path.Combine(parts.ToArray());
            var root = Parse(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
            if (root == null)
                throw new InvalidDataException(path + " is not a JSON object.");
            return root;
        }

        public static object Parse(string json)
        {
            var reader = new Reader(json);
            var value = reader.ReadValue();
            reader.SkipWhitespace();
            if (!reader.AtEnd)
                throw reader.Fail("unexpected text after the value");
            return value;
        }

        public static Dictionary<string, object> Obj(object value) => (Dictionary<string, object>)value;
        public static List<object> Arr(object value) => (List<object>)value;
        public static string Str(object value) => (string)value;
        public static int Int(object value) => checked((int)(long)value);
        public static bool Bool(object value) => (bool)value;

        sealed class Reader
        {
            readonly string text;
            int position;

            public Reader(string text) => this.text = text;

            public bool AtEnd => position >= text.Length;

            public InvalidDataException Fail(string why) => new InvalidDataException($"JSON error at offset {position}: {why}.");

            public void SkipWhitespace()
            {
                while (position < text.Length && char.IsWhiteSpace(text[position]))
                    position++;
            }

            char Peek()
            {
                SkipWhitespace();
                if (AtEnd)
                    throw Fail("unexpected end");
                return text[position];
            }

            void Expect(char c)
            {
                if (Peek() != c)
                    throw Fail($"expected '{c}' but found '{text[position]}'");
                position++;
            }

            public object ReadValue()
            {
                var c = Peek();
                switch (c)
                {
                    case '{': return ReadObject();
                    case '[': return ReadArray();
                    case '"': return ReadString();
                    case 't': ReadWord("true"); return true;
                    case 'f': ReadWord("false"); return false;
                    case 'n': ReadWord("null"); return null;
                    default: return ReadNumber();
                }
            }

            void ReadWord(string word)
            {
                if (string.CompareOrdinal(text, position, word, 0, word.Length) != 0)
                    throw Fail("expected " + word);
                position += word.Length;
            }

            Dictionary<string, object> ReadObject()
            {
                var result = new Dictionary<string, object>(StringComparer.Ordinal);
                Expect('{');
                if (Peek() == '}')
                {
                    position++;
                    return result;
                }
                while (true)
                {
                    var key = ReadString();
                    Expect(':');
                    if (result.ContainsKey(key))
                        throw Fail("duplicate key " + key);
                    result[key] = ReadValue();
                    var next = Peek();
                    position++;
                    if (next == '}')
                        return result;
                    if (next != ',')
                        throw Fail("expected ',' or '}'");
                }
            }

            List<object> ReadArray()
            {
                var result = new List<object>();
                Expect('[');
                if (Peek() == ']')
                {
                    position++;
                    return result;
                }
                while (true)
                {
                    result.Add(ReadValue());
                    var next = Peek();
                    position++;
                    if (next == ']')
                        return result;
                    if (next != ',')
                        throw Fail("expected ',' or ']'");
                }
            }

            string ReadString()
            {
                Expect('"');
                var builder = new StringBuilder();
                while (true)
                {
                    if (AtEnd)
                        throw Fail("unterminated string");
                    var c = text[position++];
                    if (c == '"')
                        return builder.ToString();
                    if (c != '\\')
                    {
                        builder.Append(c);
                        continue;
                    }
                    if (AtEnd)
                        throw Fail("unterminated escape");
                    var escape = text[position++];
                    switch (escape)
                    {
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case '/': builder.Append('/'); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case 'u':
                            if (position + 4 > text.Length)
                                throw Fail("short \\u escape");
                            builder.Append((char)int.Parse(text.Substring(position, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            position += 4;
                            break;
                        default: throw Fail("unknown escape \\" + escape);
                    }
                }
            }

            object ReadNumber()
            {
                var start = position;
                while (position < text.Length && "+-0123456789.eE".IndexOf(text[position]) >= 0)
                    position++;
                var token = text.Substring(start, position - start);
                if (token.Length == 0)
                    throw Fail("unexpected character '" + text[position] + "'");
                if (token.IndexOfAny(new[] { '.', 'e', 'E' }) < 0 && long.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole))
                    return whole;
                if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var real))
                    return real;
                throw Fail("bad number " + token);
            }
        }
    }
}
