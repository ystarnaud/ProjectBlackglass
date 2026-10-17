using System.Security.Cryptography;
using System.Text.Json;
using Blackglass.Accounts.Domain.Ids;

namespace Blackglass.Accounts.Domain.Attempts;

/// <summary>Why a deploy body was refused. The API maps each to its ProblemDetails code.</summary>
public enum DeployRequestError { InvalidBody, DuplicateOperative, InvalidOperativeId }

/// <summary>A refused deploy body. The message is the reason's name only: no part of the request is echoed.</summary>
public sealed class DeployRequestException(DeployRequestError error) : Exception(error.ToString())
{
    public DeployRequestError Error { get; } = error;
}

/// <summary>
/// A deploy request body (owner rulings S7-D5, S7-D9). Parse reads strictly: valid UTF-8 JSON (no comments, no trailing
/// commas or content), an object with exactly "missionTemplate" (a string in the template id pattern) and "operativeIds"
/// (an array of 1 to 16 strings, each the canonical Base58 spelling of an owned operative id), no member unknown, missing
/// or repeated, no id given twice. Any other shape is InvalidBody; a malformed id InvalidOperativeId; a repeated id
/// DuplicateOperative. The operative order is meaningful and kept. CanonicalJson() is the normalized form the idempotency
/// hash covers: {"missionTemplate":"…","operativeIds":["…",…]} in UTF-8 with no whitespace and the ids re-encoded in the
/// order given, so property order, whitespace and escaping don't change the hash, but the operative order does.
/// </summary>
public sealed record DeployRequest(string MissionTemplate, IReadOnlyList<long> OperativeIds)
{
    public const int MaxOperativeIds = Attempts.MissionTemplate.MaxOperativesLimit;

    static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 4,
    };

    public static DeployRequest Parse(ReadOnlySpan<byte> utf8Json)
    {
        if (!System.Text.Unicode.Utf8.IsValid(utf8Json)) throw Refuse(DeployRequestError.InvalidBody);
        JsonDocument document;
        try { document = JsonDocument.Parse(utf8Json.ToArray(), Options); }
        catch (JsonException) { throw Refuse(DeployRequestError.InvalidBody); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Refuse(DeployRequestError.InvalidBody);
            JsonElement? template = null, ids = null;
            foreach (var member in root.EnumerateObject())
            {
                if (member.NameEquals("missionTemplate") && template is null) template = member.Value;
                else if (member.NameEquals("operativeIds") && ids is null) ids = member.Value;
                else throw Refuse(DeployRequestError.InvalidBody); // unknown, or named twice
            }
            if (template is not { } t || ids is not { } list) throw Refuse(DeployRequestError.InvalidBody);

            var templateId = String(t);
            if (templateId is null || !Attempts.MissionTemplate.IsWellFormedId(templateId)) throw Refuse(DeployRequestError.InvalidBody);
            if (list.ValueKind != JsonValueKind.Array) throw Refuse(DeployRequestError.InvalidBody);
            var count = list.GetArrayLength();
            if (count < 1 || count > MaxOperativeIds) throw Refuse(DeployRequestError.InvalidBody);

            var operativeIds = new List<long>(count);
            var seen = new HashSet<long>();
            foreach (var element in list.EnumerateArray())
            {
                var text = String(element) ?? throw Refuse(DeployRequestError.InvalidBody);
                if (!Base58Id.TryParse(text, out var id)) throw Refuse(DeployRequestError.InvalidOperativeId);
                if (!seen.Add(id)) throw Refuse(DeployRequestError.DuplicateOperative);
                operativeIds.Add(id);
            }
            return new DeployRequest(templateId, operativeIds);
        }
    }

    /// <summary>The normalized request in UTF-8 (see the type summary). ArgumentException if this request could not have been parsed.</summary>
    public byte[] CanonicalJson()
    {
        if (!Attempts.MissionTemplate.IsWellFormedId(MissionTemplate)) throw new ArgumentException("The template id is malformed.", nameof(MissionTemplate));
        if (OperativeIds is null || OperativeIds.Count < 1 || OperativeIds.Count > MaxOperativeIds || OperativeIds.Distinct().Count() != OperativeIds.Count)
            throw new ArgumentException($"A request names 1 to {MaxOperativeIds} distinct operatives.", nameof(OperativeIds));
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("missionTemplate", MissionTemplate);
            w.WriteStartArray("operativeIds");
            foreach (var id in OperativeIds) w.WriteStringValue(Base58Id.Format(id));
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return buffer.ToArray();
    }

    /// <summary>SHA-256 of CanonicalJson(): 32 bytes.</summary>
    public byte[] RequestHash() => SHA256.HashData(CanonicalJson());

    static string? String(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.String) return null;
        try { return e.GetString(); }
        catch (InvalidOperationException) { throw Refuse(DeployRequestError.InvalidBody); } // an escape that is not valid UTF-16
    }

    static DeployRequestException Refuse(DeployRequestError error) => new(error);
}
