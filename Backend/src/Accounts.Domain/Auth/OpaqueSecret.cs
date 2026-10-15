using System.Buffers.Text;
using System.Security.Cryptography;

namespace Blackglass.Accounts.Domain.Auth;

/// <summary>
/// A 256-bit random secret shown once: a development account key ("dk1") or an access token ("at1"). Its text form is
/// "&lt;prefix&gt;_&lt;43 base64url characters&gt;". Only SHA-256 of the 32 raw bytes is ever stored or compared (spec §4.2:
/// high-entropy random secrets need no slow hash). TryParse is strict: the exact prefix and exactly 32 canonically
/// encoded bytes. ToString never reveals the secret.
/// </summary>
public sealed class OpaqueSecret
{
    public const int Bytes = 32;
    public const string AccountKeyPrefix = "dk1";
    public const string AccessTokenPrefix = "at1";
    const int EncodedLength = 43;

    readonly byte[] raw;

    OpaqueSecret(string prefix, byte[] raw) => (Prefix, this.raw) = (prefix, raw);

    public string Prefix { get; }
    public string Text => Prefix + "_" + Base64Url.EncodeToString(raw);

    public static OpaqueSecret New(string prefix) => new(prefix, RandomNumberGenerator.GetBytes(Bytes));

    public byte[] Hash() => SHA256.HashData(raw);

    public static bool TryParse(string? text, string prefix, out OpaqueSecret? secret)
    {
        secret = null;
        if (text is null || text.Length != prefix.Length + 1 + EncodedLength || !text.StartsWith(prefix + "_", StringComparison.Ordinal))
            return false;
        var encoded = text.AsSpan(prefix.Length + 1);
        // The decoder throws FormatException (despite its name) on characters outside base64url, so check them first.
        foreach (var c in encoded)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return false;
        Span<byte> buffer = stackalloc byte[Bytes + 2];
        int written;
        try { if (!Base64Url.TryDecodeFromChars(encoded, buffer, out written) || written != Bytes) return false; }
        catch (FormatException) { return false; }
        var bytes = buffer[..Bytes].ToArray();
        if (!encoded.SequenceEqual(Base64Url.EncodeToString(bytes)))
            return false; // non-canonical trailing bits
        secret = new OpaqueSecret(prefix, bytes);
        return true;
    }

    /// <summary>The 8-hex prefix of a hash: the most of a secret's hash any log may show (spec §4.4).</summary>
    public static string HashPrefix(ReadOnlySpan<byte> hash) => Convert.ToHexStringLower(hash[..4]);

    public override string ToString() => Prefix + "_<redacted>";
}
