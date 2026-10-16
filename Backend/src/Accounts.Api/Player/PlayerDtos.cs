using System.Text.Json.Serialization;
using Blackglass.Accounts.Domain.Ids;
using Blackglass.Accounts.Persistence.Player;

namespace Blackglass.Accounts.Api.Player;

// Wire shapes of the player routes. Operative and item ids are PostgreSQL bigints inside the backend and canonical Base58
// strings here (never JSON numbers). No type carries an account: the session is the only source of one.

/// <summary>The body of POST /v1/operatives/{operativeId}/choices. Both members are required; unknown members are refused.</summary>
public sealed record PickChoiceRequest(string? ChoiceId, long? ExpectedVersion);

public sealed record OperativeDto(
    [property: JsonConverter(typeof(Base58IdJsonConverter))] long OperativeId,
    string DefinitionId, bool DefinitionKnown, int Experience, int Rank, int PendingPicks, IReadOnlyList<string> Choices)
{
    public static OperativeDto From(OperativeView v) =>
        new(v.OperativeId, v.DefinitionId, v.DefinitionKnown, v.Experience, v.Rank, v.PendingPicks, v.Choices);
}

public sealed record RosterResponse(long Version, IReadOnlyList<OperativeDto> Operatives)
{
    public static RosterResponse From(RosterView v) => new(v.Version, v.Operatives.Select(OperativeDto.From).ToList());
}

public sealed record PickChoiceResponse(long Version, OperativeDto Operative);

public sealed record InstanceDto([property: JsonConverter(typeof(Base58IdJsonConverter))] long ItemId, string DefinitionId, bool DefinitionKnown);

public sealed record StackDto(string DefinitionId, bool DefinitionKnown, int Quantity);

public sealed record LocationDto(IReadOnlyList<InstanceDto> Instances, IReadOnlyList<StackDto> Stacks)
{
    public static LocationDto From(LocationView v) => new(
        v.Instances.Select(i => new InstanceDto(i.ItemId, i.DefinitionId, i.DefinitionKnown)).ToList(),
        v.Stacks.Select(s => new StackDto(s.DefinitionId, s.DefinitionKnown, s.Quantity)).ToList());
}

public sealed record EquipmentDto(
    [property: JsonConverter(typeof(NullableBase58IdJsonConverter))] long? Weapon,
    [property: JsonConverter(typeof(NullableBase58IdJsonConverter))] long? Armor,
    [property: JsonConverter(typeof(NullableBase58IdJsonConverter))] long? Utility);

public sealed record BagDto([property: JsonConverter(typeof(Base58IdJsonConverter))] long OperativeId, LocationDto Bag, EquipmentDto Equipment);

public sealed record InventoryResponse(long Version, bool Locked, LocationDto Stash, IReadOnlyList<BagDto> Operatives)
{
    public static InventoryResponse From(InventoryView v) => new(v.Version, v.Locked, LocationDto.From(v.Stash),
        v.Operatives.Select(o => new BagDto(o.OperativeId, LocationDto.From(o.Bag), new EquipmentDto(o.Equipment.Weapon, o.Equipment.Armor, o.Equipment.Utility))).ToList());
}

/// <summary>
/// The body of POST /v1/inventory/operations. Both members are required; unknown members are refused. Ids stay strings
/// here (canonical Base58) so a bad one can be refused with the index of its operation.
/// </summary>
public sealed record InventoryOperationsRequest(long? ExpectedVersion, IReadOnlyList<OperationDto?>? Operations);

/// <summary>
/// One operation of a batch. Each <c>op</c> takes exactly its members: equip, equipFromStash (operativeId, itemId); unequip
/// (operativeId, slot: weapon, armor or utility); toStash, toBag (operativeId and either itemId, or definitionId and quantity).
/// </summary>
public sealed record OperationDto(string? Op, string? OperativeId, string? ItemId, string? Slot, string? DefinitionId, int? Quantity);

public sealed record InventoryOperationsResponse(long Version, InventoryResponse Inventory);
