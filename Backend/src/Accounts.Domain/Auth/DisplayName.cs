namespace Blackglass.Accounts.Domain.Auth;

/// <summary>An optional, operator-chosen display name for a development account: 1 to 64 characters, no control
/// character, no leading or trailing whitespace. It is a label, never an identifier or a key.</summary>
public static class DisplayName
{
    public const int MaxLength = 64;

    public static bool IsValid(string? name) =>
        name is { Length: > 0 and <= MaxLength }
        && !char.IsWhiteSpace(name[0]) && !char.IsWhiteSpace(name[^1])
        && !name.Any(char.IsControl);
}
