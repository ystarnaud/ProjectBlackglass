using System.Text.RegularExpressions;
using Blackglass.Accounts.Domain.Ids;

namespace Blackglass.MissionHost.Agent;

/// <summary>
/// Attempt ids and the unit names and paths built from them (spec §7.4, A10-D4). An attempt id is accepted only as the
/// canonical lowercase UUID text, exactly as the backend writes it, never the nil UUID; everything built from it (a unit
/// name, an outbox or relay path) is rendered from the parsed value, so no other text can reach systemctl or the filesystem.
/// </summary>
public static partial class AttemptIds
{
    public const string UnitPrefix = "blackglass-mission@";
    public const string UnitSuffix = ".service";

    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Canonical();

    [GeneratedRegex(@"^blackglass-mission@([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\.service\z", RegexOptions.CultureInvariant)]
    private static partial Regex UnitName();

    /// <summary>True for exactly a canonical lowercase UUID other than the nil UUID; <paramref name="id"/> is its value.</summary>
    public static bool TryParse(string? text, out Guid id)
    {
        id = Guid.Empty;
        if (text is null || !Canonical().IsMatch(text) || !Guid.TryParseExact(text, "D", out var parsed) || parsed == Guid.Empty) return false;
        if (parsed.ToString("D") != text) return false;
        id = parsed;
        return true;
    }

    public static string Text(Guid id) => id == Guid.Empty ? throw new ArgumentException("The nil UUID is never an attempt.", nameof(id)) : id.ToString("D");

    /// <summary>blackglass-mission@&lt;uuid&gt;.service, built only from a parsed attempt id.</summary>
    public static string Unit(Guid id) => UnitPrefix + Text(id) + UnitSuffix;

    /// <summary>The attempt of a mission unit name, or false for any other name.</summary>
    public static bool TryParseUnit(string? unit, out Guid id)
    {
        id = Guid.Empty;
        if (unit is null) return false;
        var match = UnitName().Match(unit);
        return match.Success && TryParse(match.Groups[1].Value, out id);
    }
}

/// <summary>Account ids as the backend shows them: canonical Base58 (decision 048 amendment).</summary>
public static class AccountIds
{
    /// <summary>True only for canonical Base58 (it formats back to the same text).</summary>
    public static bool TryParse(string? text, out long id) => Base58Id.TryParse(text, out id) && id > 0 && Base58Id.Format(id) == text;

    public static string Text(long id) => Base58Id.Format(id);
}
