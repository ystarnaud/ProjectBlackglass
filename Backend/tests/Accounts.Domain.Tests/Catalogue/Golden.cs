using Blackglass.Accounts.Domain.Catalogue;

namespace Blackglass.Accounts.Domain.Tests.Catalogue;

/// <summary>The shared golden vector (Backend/contracts/catalogue), which Unity's CatalogueExportTests checks too.</summary>
internal static class Golden
{
    public const string One = "00000000-0000-4000-8000-000000000001";
    public const string Two = "00000000-0000-4000-8000-000000000002";

    static string Folder => Path.Combine(AppContext.BaseDirectory, "contracts", "catalogue");
    public static byte[] Bytes() => File.ReadAllBytes(Path.Combine(Folder, "golden-catalogue.json"));
    public static string Version() => File.ReadAllText(Path.Combine(Folder, "golden-catalogue.version")).Trim();

    public static CatalogueDocument Document() => new(
        FormatVersion: 2,
        Inventory: new(6),
        Items:
        [
            new("item.alpha-rifle", ItemCategory.Weapon, ItemSlot.Weapon, false, 1, 0),
            new("item.beta-vest", ItemCategory.Armor, ItemSlot.Armor, false, 1, 0),
            new("item.delta-boots", ItemCategory.Utility, ItemSlot.Utility, false, 1, 0),
            new("item.gamma-kit", ItemCategory.Consumable, ItemSlot.None, true, 3, 10),
        ],
        LootTable: new(7, 2, 1, 3, [new("item.gamma-kit", 1, 2, 5), new("item.alpha-rifle", 1, 1, 1)]),
        Progression: new([0, 10, 30], 20, ["choice.one", "choice.two"]),
        Operatives: [new(One, "role.alpha", ["ability.two", "ability.one"]), new(Two, "role.beta", [])],
        Starter: new(
            [new(One, [new("item.alpha-rifle", 1, true), new("item.gamma-kit", 2, false)])],
            [new("item.beta-vest", 1), new("item.gamma-kit", 4)]));
}
