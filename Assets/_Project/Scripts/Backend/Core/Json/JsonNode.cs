using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;

namespace Blackglass.Backend.Core
{
    public enum JsonKind { Object, Array, String, Number, Bool, Null }

    /// <summary>
    /// A parsed or hand-built JSON value. The strict reader keeps what the canonical forms and the backend's refusal order
    /// need: object members in their order, duplicates included; a number's raw text; whether a string came from an escape
    /// sequence that is not valid UTF-16 (a lone surrogate), which the codecs refuse only where the backend would decode it.
    /// </summary>
    public abstract class JsonNode
    {
        public abstract JsonKind Kind { get; }
    }

    public sealed class JsonMember
    {
        public JsonMember(string name, JsonNode value, bool nameIsInvalidUtf16 = false)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Value = value ?? throw new ArgumentNullException(nameof(value));
            NameIsInvalidUtf16 = nameIsInvalidUtf16;
        }

        public string Name { get; }
        public JsonNode Value { get; }
        public bool NameIsInvalidUtf16 { get; }
    }

    public sealed class JsonObject : JsonNode
    {
        public JsonObject(IEnumerable<JsonMember> members) => Members = new ReadOnlyCollection<JsonMember>(new List<JsonMember>(members));

        public static JsonObject Of(params (string Name, JsonNode Value)[] members)
        {
            var list = new List<JsonMember>(members.Length);
            foreach (var m in members) list.Add(new JsonMember(m.Name, m.Value));
            return new JsonObject(list);
        }

        public override JsonKind Kind => JsonKind.Object;
        public IReadOnlyList<JsonMember> Members { get; }
    }

    public sealed class JsonArray : JsonNode
    {
        public JsonArray(IEnumerable<JsonNode> items) => Items = new ReadOnlyCollection<JsonNode>(new List<JsonNode>(items));

        public override JsonKind Kind => JsonKind.Array;
        public IReadOnlyList<JsonNode> Items { get; }
    }

    public sealed class JsonString : JsonNode
    {
        public JsonString(string value, bool isInvalidUtf16 = false)
        {
            Value = value ?? throw new ArgumentNullException(nameof(value));
            IsInvalidUtf16 = isInvalidUtf16;
        }

        public override JsonKind Kind => JsonKind.String;
        public string Value { get; }
        public bool IsInvalidUtf16 { get; }
    }

    public sealed class JsonNumber : JsonNode
    {
        JsonNumber(string rawText) => RawText = rawText;

        /// <summary>A number exactly as it was written (the reader has checked its JSON syntax).</summary>
        internal static JsonNumber FromRaw(string rawText) => new JsonNumber(rawText);

        /// <summary>An integer in invariant decimal, the only number the canonical forms write.</summary>
        public static JsonNumber FromInt(long value) => new JsonNumber(value.ToString(CultureInfo.InvariantCulture));

        public override JsonKind Kind => JsonKind.Number;
        public string RawText { get; }
    }

    public sealed class JsonBool : JsonNode
    {
        public static readonly JsonBool True = new JsonBool(true), False = new JsonBool(false);

        JsonBool(bool value) => Value = value;

        public static JsonBool Of(bool value) => value ? True : False;

        public override JsonKind Kind => JsonKind.Bool;
        public bool Value { get; }
    }

    public sealed class JsonNull : JsonNode
    {
        public static readonly JsonNull Instance = new JsonNull();

        JsonNull() { }

        public override JsonKind Kind => JsonKind.Null;
    }
}
