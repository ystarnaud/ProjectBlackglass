using Blackglass.Accounts.Domain.Ids;

namespace Blackglass.Accounts.Domain.Accounts;

/// <summary>
/// The public form of an account id (decision 048 amendment, spec §4.1): the canonical Bitcoin Base58 encoding of the
/// positive bigint PostgreSQL generated. It is the number itself, not a hash, and not a secret. Parsing accepts only the
/// exact canonical spelling: 1 to 11 alphabet characters (ordinal, so case-sensitive), no leading '1' (the zero digit),
/// a value from 1 to long.MaxValue. The codec is Base58Id (shared by every owned id); this keeps the account-specific
/// name. Code works with long; this and AccountIdJsonConverter are the only account conversions.
/// </summary>
public static class AccountId
{
    public const string Alphabet = Base58Id.Alphabet;

    /// <summary>long.MaxValue is "NQm6nKp8qFC": 11 characters.</summary>
    public const int MaxLength = Base58Id.MaxLength;

    public static string Format(long id) => Base58Id.Format(id);

    public static bool TryParse(string? text, out long id) => Base58Id.TryParse(text, out id);
}
