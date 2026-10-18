using System.Buffers.Text;
using System.Text;
using System.Text.RegularExpressions;

namespace Blackglass.MissionHost.CredentialBroker;

/// <summary>
/// The broker's whole interface (security correction I3), one request per connection, at most <see cref="MaxBytes"/> of
/// printable ASCII and newlines:
///   "prepare &lt;attempt&gt; &lt;generation&gt;\n&lt;credential&gt;\n"   store the process credential for that attempt's unit
///   "discard &lt;attempt&gt;\n"                                  remove it once the unit has it (or will never start)
/// The attempt is a canonical lowercase UUID other than nil, the generation a decimal 1..2147483647 without leading zeros, the
/// credential exactly a pc1 secret (prefix, 43 canonical base64url characters). No path, no other verb, nothing else.
/// </summary>
public abstract partial record BrokerRequest
{
    public const int MaxBytes = 512;

    public sealed record Prepare(string Attempt, int Generation, string Credential) : BrokerRequest
    {
        public override string ToString() => $"Prepare {{ Attempt = {Attempt}, Generation = {Generation} }}"; // never the credential
    }

    public sealed record Discard(string Attempt) : BrokerRequest;

    public sealed record Invalid(string Code) : BrokerRequest;

    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalUuid();

    [GeneratedRegex(@"^[1-9][0-9]{0,9}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Decimal();

    [GeneratedRegex(@"^pc1_[A-Za-z0-9_-]{43}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ProcessCredential();

    public static bool IsAttempt(string? text) =>
        text is not null && CanonicalUuid().IsMatch(text) && Guid.TryParseExact(text, "D", out var id) && id != Guid.Empty && id.ToString("D") == text;

    /// <summary>Exactly a pc1 credential: the prefix and 32 canonically encoded bytes.</summary>
    public static bool IsCredential(string? text)
    {
        if (text is null || !ProcessCredential().IsMatch(text)) return false;
        Span<byte> raw = stackalloc byte[34];
        try { if (!Base64Url.TryDecodeFromChars(text.AsSpan(4), raw, out var written) || written != 32) return false; }
        catch (FormatException) { return false; }
        return Base64Url.EncodeToString(raw[..32]) == text[4..];
    }

    public static BrokerRequest Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxBytes) return new Invalid("too_large");
        foreach (var b in bytes)
            if (b != (byte)'\n' && b is < 0x20 or > 0x7E) return new Invalid("malformed");
        var lines = Encoding.ASCII.GetString(bytes).Split('\n');
        if (lines.Length < 2 || lines[^1].Length != 0) return new Invalid("malformed");
        var head = lines[0].Split(' ');
        switch (head[0])
        {
            case "prepare" when head.Length == 3 && lines.Length == 3:
                if (!IsAttempt(head[1])) return new Invalid("bad_attempt");
                if (!Decimal().IsMatch(head[2]) || !int.TryParse(head[2], out var generation)) return new Invalid("bad_generation");
                if (!IsCredential(lines[1])) return new Invalid("bad_credential");
                return new Prepare(head[1], generation, lines[1]);
            case "discard" when head.Length == 2 && lines.Length == 2:
                return IsAttempt(head[1]) ? new Discard(head[1]) : new Invalid("bad_attempt");
            default:
                return new Invalid("malformed");
        }
    }
}
