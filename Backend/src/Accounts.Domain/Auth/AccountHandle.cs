using System.Text.RegularExpressions;

namespace Blackglass.Accounts.Domain.Auth;

/// <summary>
/// An operator-chosen development handle, the dev identity's subject (account_identities.subject): lowercase letters and
/// digits separated by single '.' or '-', at most 64 characters. It never becomes the account id.
/// </summary>
public static partial class AccountHandle
{
    public const int MaxLength = 64;

    // \z, not $: .NET's $ also matches before a final newline.
    [GeneratedRegex(@"^[a-z0-9]+(?:[.-][a-z0-9]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static bool IsValid(string? handle) => handle is { Length: > 0 and <= MaxLength } && Pattern().IsMatch(handle);
}
