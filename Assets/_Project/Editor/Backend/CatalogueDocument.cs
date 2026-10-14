using System.Collections.Generic;

namespace Blackglass.BackendTools
{
    /// <summary>
    /// The backend catalogue (Backend/catalogue/catalogue.json, format 1) as plain data, in the shape and order it is
    /// written. Ids and rules only: no display text, presentation or stat values (spec §4.6). Strings are ids or enum
    /// words; CanonicalCatalogueWriter refuses anything else.
    /// </summary>
    public sealed class CatalogueDocument
    {
        public const int FormatVersion = 1;
        public readonly List<CatalogueItem> Items = new List<CatalogueItem>();
        public readonly CatalogueLootTable LootTable = new CatalogueLootTable();
        public readonly CatalogueProgression Progression = new CatalogueProgression();
        public readonly List<CatalogueOperative> Operatives = new List<CatalogueOperative>();
        public readonly CatalogueStarter Starter = new CatalogueStarter();
    }

    public sealed class CatalogueItem
    {
        public string Id;
        public string Category;   // weapon, armor, utility, consumable
        public string Slot;       // none, weapon, armor, utility
        public bool Stackable;
        public int MaxStack;
        public int HealAmount;
    }

    public sealed class CatalogueLootTable
    {
        public int Version;
        public int ContainerCount;
        public int MinItems;
        public int MaxItems;
        public readonly List<CatalogueLootEntry> Entries = new List<CatalogueLootEntry>();
    }

    public sealed class CatalogueLootEntry
    {
        public string ItemId;
        public int MinQuantity;
        public int MaxQuantity;
        public int Weight;
    }

    public sealed class CatalogueProgression
    {
        public readonly List<int> XpThresholds = new List<int>();
        public int MissionCompletionXp;
        public readonly List<string> ChoiceIds = new List<string>();
    }

    public sealed class CatalogueOperative
    {
        public string Id;
        public string RoleId;
        public readonly List<string> AbilityIds = new List<string>();
    }

    public sealed class CatalogueStarter
    {
        public readonly List<CatalogueStarterOperative> Operatives = new List<CatalogueStarterOperative>();
        public readonly List<CatalogueStashGrant> Stash = new List<CatalogueStashGrant>();
    }

    public sealed class CatalogueStarterOperative
    {
        public string OperativeId;
        public readonly List<CatalogueGrant> Grants = new List<CatalogueGrant>();
    }

    public sealed class CatalogueGrant
    {
        public string ItemId;
        public int Quantity;
        public bool Equip;
    }

    public sealed class CatalogueStashGrant
    {
        public string ItemId;
        public int Quantity;
    }
}
