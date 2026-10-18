using System.Globalization;
using System.Text.RegularExpressions;
using Blackglass.Accounts.Domain.Ids;

namespace Blackglass.MissionHost.Agent;

/// <summary>
/// Attempt ids (spec §7.4, A10-D4). An attempt id is accepted only as the canonical lowercase UUID text, exactly as the backend
/// writes it, never the nil UUID; everything built from it (a unit name, an outbox or relay path) is rendered from the parsed
/// value, so no other text can reach systemctl or the filesystem.
/// </summary>
public static partial class AttemptIds
{
    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Canonical();

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
}

/// <summary>
/// A mission unit (security correction C1): blackglass-mission-NN@&lt;attempt&gt;.service, where NN (00..63) is the host slot the
/// attempt's port gives (port - portBase) and the instance is the full canonical attempt id. Each slot's template runs its unit as
/// its own systemd dynamic user (blackglass-mNN), so two live units never share a uid; the agent starts a slot's unit only when no
/// other unit of that slot is live. Built only from a validated slot and attempt; parsed strictly.
/// </summary>
public readonly partial record struct MissionUnit
{
    public const int MaxSlots = 64;
    public const string ListPattern = "blackglass-mission-*@*.service";

    public int Slot { get; }
    public Guid Attempt { get; }

    public MissionUnit(int slot, Guid attempt)
    {
        if (slot is < 0 or >= MaxSlots) throw new ArgumentOutOfRangeException(nameof(slot), "A slot is 0 to 63.");
        AttemptIds.Text(attempt); // never the nil UUID
        (Slot, Attempt) = (slot, attempt);
    }

    public string Name => $"blackglass-mission-{Slot.ToString("D2", CultureInfo.InvariantCulture)}@{AttemptIds.Text(Attempt)}.service";

    public override string ToString() => Name;

    [GeneratedRegex(@"^blackglass-mission-([0-5][0-9]|6[0-3])@([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\.service\z", RegexOptions.CultureInvariant)]
    private static partial Regex UnitName();

    /// <summary>The slot and attempt of a mission unit name, or false for any other name.</summary>
    public static bool TryParse(string? name, out MissionUnit unit)
    {
        unit = default;
        if (name is null) return false;
        var match = UnitName().Match(name);
        if (!match.Success || !AttemptIds.TryParse(match.Groups[2].Value, out var attempt)) return false;
        unit = new MissionUnit(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), attempt);
        return true;
    }
}

/// <summary>The host's slots: ports [PortBase, PortBase + Count) are slots 0..Count-1 (the backend allocates exactly these ports,
/// Stage 8's port_base and capacity). A port outside them is never launched.</summary>
public sealed record MissionSlots(int PortBase, int Count)
{
    public int? SlotOf(int port) => port >= PortBase && port < PortBase + Count && port - PortBase < MissionUnit.MaxSlots ? port - PortBase : null;

    /// <summary>The unit of a claimed attempt, or null when its port is outside the slots or it has no attempt.</summary>
    public MissionUnit? UnitOf(ClaimRecord record) =>
        record.Attempt is { } a && AttemptIds.TryParse(a, out var attempt) && SlotOf(record.MissionPort) is { } slot ? new MissionUnit(slot, attempt) : null;
}

/// <summary>Account ids as the backend shows them: canonical Base58 (decision 048 amendment).</summary>
public static class AccountIds
{
    /// <summary>True only for canonical Base58 (it formats back to the same text).</summary>
    public static bool TryParse(string? text, out long id) => Base58Id.TryParse(text, out id) && id > 0 && Base58Id.Format(id) == text;

    public static string Text(long id) => Base58Id.Format(id);
}
