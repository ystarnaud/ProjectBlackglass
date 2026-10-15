namespace Blackglass.Accounts.Domain.Accounts;

/// <summary>
/// The public form of an account id (decision 048 amendment, spec §4.1): the canonical Bitcoin Base58 encoding of the
/// positive bigint PostgreSQL generated. It is the number itself, not a hash, and not a secret. Parsing accepts only the
/// exact canonical spelling: 1 to 11 alphabet characters (ordinal, so case-sensitive), no leading '1' (the zero digit),
/// a value from 1 to long.MaxValue. Code works with long; this and AccountIdJsonConverter are the only conversions.
/// </summary>
public static class AccountId
{
    public const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    /// <summary>long.MaxValue is "NQm6nKp8qFC": 11 characters.</summary>
    public const int MaxLength = 11;

    static readonly sbyte[] Digits = BuildDigits();

    static sbyte[] BuildDigits()
    {
        var digits = new sbyte[128];
        Array.Fill(digits, (sbyte)-1);
        for (var i = 0; i < Alphabet.Length; i++) digits[Alphabet[i]] = (sbyte)i;
        return digits;
    }

    public static string Format(long id)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        Span<char> buffer = stackalloc char[MaxLength];
        var start = MaxLength;
        for (var rest = id; rest > 0; rest /= 58)
            buffer[--start] = Alphabet[(int)(rest % 58)];
        return new string(buffer[start..]);
    }

    public static bool TryParse(string? text, out long id)
    {
        id = 0;
        if (text is not { Length: > 0 and <= MaxLength } || text[0] == Alphabet[0]) return false;
        long value = 0;
        foreach (var c in text)
        {
            int digit = c < 128 ? Digits[c] : -1;
            if (digit < 0) return false;
            if (value > (long.MaxValue - digit) / 58) return false; // value * 58 + digit would pass long.MaxValue
            value = value * 58 + digit;
        }
        id = value;
        return true;
    }
}
