using System.Text.Json;
using System.Text.RegularExpressions;
using Blackglass.Accounts.Domain.Ids;

namespace Blackglass.Accounts.Domain.Attempts;

/// <summary>A refused agent or process body. The message is "InvalidBody" only: no part of the request is echoed.</summary>
public sealed class RequestFormatException() : Exception("InvalidBody");

/// <summary>
/// The strict reader shared by the agent and process bodies (spec §16.2, §16.3): valid UTF-8 JSON with no comments,
/// trailing commas or trailing content; objects with exactly the named members, none unknown, missing or repeated (names
/// match case-sensitively); integers without fractions or exponents; UUIDs in canonical lowercase 8-4-4-4-12 form; account
/// ids in canonical Base58. Anything else throws <see cref="RequestFormatException"/>.
/// </summary>
internal static partial class StrictJson
{
    static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 8,
    };

    [GeneratedRegex(@"^[a-z0-9]+(?:[._-][a-z0-9]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex WordPattern();

    public static T Read<T>(ReadOnlySpan<byte> utf8Json, Func<JsonElement, T> read)
    {
        if (!System.Text.Unicode.Utf8.IsValid(utf8Json)) throw Refuse();
        JsonDocument document;
        try { document = JsonDocument.Parse(utf8Json.ToArray(), Options); }
        catch (JsonException) { throw Refuse(); }
        using (document)
        {
            try { return read(document.RootElement); }
            catch (InvalidOperationException) { throw Refuse(); } // e.g. an escape that is not valid UTF-16
        }
    }

    /// <summary>The object's members, which must be exactly <paramref name="names"/>.</summary>
    public static Dictionary<string, JsonElement> Members(JsonElement e, params string[] names)
    {
        if (e.ValueKind != JsonValueKind.Object) throw Refuse();
        var found = new Dictionary<string, JsonElement>(names.Length, StringComparer.Ordinal);
        foreach (var member in e.EnumerateObject())
        {
            if (System.Array.IndexOf(names, member.Name) < 0 || !found.TryAdd(member.Name, member.Value)) throw Refuse();
        }
        if (found.Count != names.Length) throw Refuse();
        return found;
    }

    public static string String(JsonElement e, int maxLength)
    {
        if (e.ValueKind != JsonValueKind.String) throw Refuse();
        var s = e.GetString()!;
        return s.Length <= maxLength ? s : throw Refuse();
    }

    public static string? NullableString(JsonElement e, int maxLength) => e.ValueKind == JsonValueKind.Null ? null : String(e, maxLength);

    /// <summary>A lowercase word: letters and digits, separated by single '.', '_' or '-' (1 to maxLength characters).</summary>
    public static string Word(JsonElement e, int maxLength)
    {
        var s = String(e, maxLength);
        return IsWord(s, maxLength) ? s : throw Refuse();
    }

    public static bool IsWord(string? s, int maxLength) => s is { Length: > 0 } && s.Length <= maxLength && WordPattern().IsMatch(s);

    public static int Int(JsonElement e, int min, int max)
    {
        if (e.ValueKind != JsonValueKind.Number || !e.TryGetInt32(out var v) || v < min || v > max) throw Refuse();
        return v;
    }

    public static int? NullableInt(JsonElement e, int min, int max) => e.ValueKind == JsonValueKind.Null ? null : Int(e, min, max);

    public static bool Bool(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw Refuse(),
    };

    public static Guid Uuid(JsonElement e)
    {
        var s = String(e, 36);
        return TryCanonicalUuid(s, out var id) ? id : throw Refuse();
    }

    public static bool TryCanonicalUuid(string? text, out Guid id)
    {
        id = Guid.Empty;
        return text is { Length: 36 } && Guid.TryParseExact(text, "D", out id) && id.ToString("D") == text;
    }

    public static long AccountId(JsonElement e)
    {
        var s = String(e, 16);
        return Base58Id.TryParse(s, out var id) ? id : throw Refuse();
    }

    public static IReadOnlyList<T> Array<T>(JsonElement e, int min, int max, Func<JsonElement, T> read)
    {
        if (e.ValueKind != JsonValueKind.Array) throw Refuse();
        var count = e.GetArrayLength();
        if (count < min || count > max) throw Refuse();
        var list = new List<T>(count);
        foreach (var item in e.EnumerateArray()) list.Add(read(item));
        return list;
    }

    /// <summary>Refuses a list in which two items share a key.</summary>
    public static IReadOnlyList<T> Distinct<T, TKey>(IReadOnlyList<T> list, Func<T, TKey> key)
    {
        var seen = new HashSet<TKey>();
        foreach (var item in list)
            if (!seen.Add(key(item))) throw Refuse();
        return list;
    }

    public static RequestFormatException Refuse() => new();
}
