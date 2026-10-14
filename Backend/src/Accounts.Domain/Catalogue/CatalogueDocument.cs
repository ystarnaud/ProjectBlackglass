namespace Blackglass.Accounts.Domain.Catalogue;

public enum ItemCategory { Weapon, Armor, Utility, Consumable }

public enum ItemSlot { None, Weapon, Armor, Utility }

public sealed record CatalogueItem(string Id, ItemCategory Category, ItemSlot Slot, bool Stackable, int MaxStack, int HealAmount);

public sealed record LootEntry(string ItemId, int MinQuantity, int MaxQuantity, int Weight);

public sealed record LootTable(int Version, int ContainerCount, int MinItems, int MaxItems, IReadOnlyList<LootEntry> Entries);

public sealed record Progression(IReadOnlyList<int> XpThresholds, int MissionCompletionXp, IReadOnlyList<string> ChoiceIds);

public sealed record OperativeDefinition(string Id, string RoleId, IReadOnlyList<string> AbilityIds);

public sealed record StarterGrant(string ItemId, int Quantity, bool Equip);

public sealed record StashGrant(string ItemId, int Quantity);

public sealed record StarterOperative(string OperativeId, IReadOnlyList<StarterGrant> Grants);

public sealed record Starter(IReadOnlyList<StarterOperative> Operatives, IReadOnlyList<StashGrant> Stash);

/// <summary>
/// The catalogue exported from Unity (Backend/catalogue/catalogue.json), in the shape and order of the file. Ids and
/// rules only; display text stays in Unity. Lists keep the file's order.
/// </summary>
public sealed record CatalogueDocument(int FormatVersion, IReadOnlyList<CatalogueItem> Items, LootTable LootTable,
    Progression Progression, IReadOnlyList<OperativeDefinition> Operatives, Starter Starter)
{
    public const int SupportedFormatVersion = 1;
}
