using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass.BackendTools
{
    /// <summary>
    /// Turns the authored assets into a CatalogueDocument. It checks everything the backend's validator checks, plus what
    /// only the assets show: which assets share an id, and references to an asset other than the exported one. Every
    /// problem is collected; the result is null when there is any. Each message starts with its kind: "missing:",
    /// "duplicate:", "malformed:" or "incompatible:". Sets (items, choices, operatives, starter operatives) are sorted by
    /// id, so asset order never changes the export; lists whose order means something (loot entries, an operative's
    /// abilities, grants, the stash) keep the authored order. Display names are never read.
    /// </summary>
    public static class CatalogueBuilder
    {
        const string IdRule = "is not a well-formed id (lowercase letters and digits separated by single '.' or '-', at most 64 characters)";

        public static CatalogueDocument Build(CatalogueSources sources, List<string> errors)
        {
            var start = errors.Count;
            if (sources.Items == null) errors.Add($"missing: the item catalogue ({CatalogueSources.ItemCataloguePath})");
            if (sources.LootTable == null) errors.Add($"missing: the loot table ({CatalogueSources.LootTablePath})");
            if (sources.Progression == null) errors.Add($"missing: the progression track ({CatalogueSources.ProgressionPath})");
            if (sources.Starter == null) errors.Add($"missing: the starter loadout ({CatalogueSources.StarterPath})");
            if (sources.Operatives.Count == 0) errors.Add($"missing: no operative definitions in {CatalogueSources.OperativeFolder}");
            if (errors.Count > start)
                return null;

            var document = new CatalogueDocument();
            var items = AddItems(sources.Items, document, errors);
            AddLootTable(sources.LootTable, items, document, errors);
            AddProgression(sources.Progression, document, errors);
            var operatives = AddOperatives(sources.Operatives, document, errors);
            AddStarter(sources.Starter, items, operatives, document, errors);
            return errors.Count > start ? null : document;
        }

        static Dictionary<string, ItemDefinition> AddItems(ItemCatalogue catalogue, CatalogueDocument document, List<string> errors)
        {
            var byId = new Dictionary<string, ItemDefinition>(StringComparer.Ordinal);
            var items = catalogue.Items;
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null) { errors.Add($"missing: item catalogue {catalogue.name} entry {i} is empty"); continue; }
                if (!item.IsValid(out var problem)) { errors.Add($"malformed: item asset {item.name}: {problem}"); continue; }
                if (!CanonicalCatalogueWriter.IsWellFormedId(item.Id)) { errors.Add($"malformed: item asset {item.name}: id '{item.Id}' {IdRule}"); continue; }
                if (byId.TryGetValue(item.Id, out var other))
                {
                    errors.Add(ReferenceEquals(other, item)
                        ? $"duplicate: item asset {item.name} is listed more than once in {catalogue.name}"
                        : $"duplicate: item id '{item.Id}' is on both {other.name} and {item.name}");
                    continue;
                }
                var category = CategoryWord(item.Category);
                var slot = SlotWord(item.Slot);
                if (category == null || slot == null)
                {
                    errors.Add($"incompatible: item asset {item.name}: category {(int)item.Category} or slot {(int)item.Slot} has no catalogue word");
                    continue;
                }
                byId.Add(item.Id, item);
                document.Items.Add(new CatalogueItem
                {
                    Id = item.Id, Category = category, Slot = slot, Stackable = item.IsStackable, MaxStack = item.MaxStack, HealAmount = item.HealAmount,
                });
            }
            document.Items.Sort((x, y) => string.CompareOrdinal(x.Id, y.Id));
            return byId;
        }

        static void AddLootTable(LootTable table, Dictionary<string, ItemDefinition> items, CatalogueDocument document, List<string> errors)
        {
            if (!table.IsValid(out var problem)) { errors.Add($"malformed: loot table {table.name}: {problem}"); return; }
            if (table.Version < 1) { errors.Add($"malformed: loot table {table.name}: the version must be at least 1"); return; }
            var row = document.LootTable;
            row.Version = table.Version;
            row.ContainerCount = table.ContainerCount;
            row.MinItems = table.MinItems;
            row.MaxItems = table.MaxItems;
            for (var i = 0; i < table.Entries.Length; i++)
            {
                var entry = table.Entries[i];
                if (!InCatalogue(entry.item, items))
                {
                    errors.Add($"incompatible: loot table {table.name} entry {i} uses item asset {entry.item.name}, which is not in the item catalogue");
                    continue;
                }
                row.Entries.Add(new CatalogueLootEntry { ItemId = entry.item.Id, MinQuantity = entry.minQuantity, MaxQuantity = entry.maxQuantity, Weight = entry.weight });
            }
        }

        static void AddProgression(ProgressionTrack track, CatalogueDocument document, List<string> errors)
        {
            var row = document.Progression;
            for (var rank = 1; rank <= track.MaxRank; rank++)
                row.XpThresholds.Add(track.XpForRank(rank));
            // The asset's OnValidate and Create already enforce these; the export checks again because the backend will.
            if (row.XpThresholds[0] != 0)
                errors.Add($"malformed: progression track {track.name}: rank 1 must need 0 XP");
            for (var i = 1; i < row.XpThresholds.Count; i++)
            {
                if (row.XpThresholds[i] > row.XpThresholds[i - 1])
                    continue;
                errors.Add($"malformed: progression track {track.name}: XP thresholds must strictly increase (rank {i + 1})");
                break;
            }
            if (track.MissionCompletionXp < 0)
                errors.Add($"malformed: progression track {track.name}: mission completion XP is negative");
            row.MissionCompletionXp = track.MissionCompletionXp;

            var byId = new Dictionary<string, AdvancementChoice>(StringComparer.Ordinal);
            for (var i = 0; i < track.Choices.Count; i++)
            {
                var choice = track.Choices[i];
                if (choice == null) { errors.Add($"missing: progression track {track.name} choice {i} is empty"); continue; }
                if (!CanonicalCatalogueWriter.IsWellFormedId(choice.Id)) { errors.Add($"malformed: choice asset {choice.name}: id '{choice.Id}' {IdRule}"); continue; }
                if (byId.TryGetValue(choice.Id, out var other)) { errors.Add($"duplicate: choice id '{choice.Id}' is on both {other.name} and {choice.name}"); continue; }
                byId.Add(choice.Id, choice);
                row.ChoiceIds.Add(choice.Id);
            }
            row.ChoiceIds.Sort(string.CompareOrdinal);
        }

        static Dictionary<string, OperativeDefinition> AddOperatives(List<OperativeDefinition> definitions, CatalogueDocument document, List<string> errors)
        {
            var byId = new Dictionary<string, OperativeDefinition>(StringComparer.Ordinal);
            var roles = new Dictionary<string, OperativeRole>(StringComparer.Ordinal);
            var abilities = new Dictionary<string, AbilityDefinition>(StringComparer.Ordinal);
            foreach (var definition in definitions)
            {
                if (definition == null) { errors.Add($"missing: an operative definition in {CatalogueSources.OperativeFolder} could not be loaded"); continue; }
                if (!definition.IsValid(out var problem)) { errors.Add($"malformed: operative asset {definition.name}: {problem}"); continue; }
                if (!CanonicalCatalogueWriter.IsWellFormedId(definition.Id)) { errors.Add($"malformed: operative asset {definition.name}: id '{definition.Id}' {IdRule}"); continue; }
                if (byId.TryGetValue(definition.Id, out var twin))
                {
                    errors.Add($"duplicate: operative id '{definition.Id}' is on both {twin.name} and {definition.name} (use \"Generate new id\" on a copied asset)");
                    continue;
                }
                var row = new CatalogueOperative { Id = definition.Id, RoleId = definition.Role.Id };
                var ok = Keyed(definition.Role, definition.Role.Id, "role", definition, roles, errors);
                var seen = new HashSet<AbilityDefinition>();
                foreach (var ability in definition.Abilities)
                {
                    if (!Keyed(ability, ability.Id, "ability", definition, abilities, errors)) { ok = false; continue; }
                    if (!seen.Add(ability)) { errors.Add($"duplicate: operative asset {definition.name} lists ability {ability.name} more than once"); ok = false; continue; }
                    row.AbilityIds.Add(ability.Id);
                }
                if (!ok)
                    continue;
                byId.Add(definition.Id, definition);
                document.Operatives.Add(row);
            }
            document.Operatives.Sort((x, y) => string.CompareOrdinal(x.Id, y.Id));
            return byId;
        }

        // A role or ability, which the catalogue names by its own id: the id must be authored, well-formed and not shared
        // with a different asset.
        static bool Keyed<T>(T asset, string id, string kind, OperativeDefinition user, Dictionary<string, T> seen, List<string> errors) where T : UnityEngine.Object
        {
            if (string.IsNullOrWhiteSpace(id)) { errors.Add($"missing: {kind} asset {asset.name} (used by operative {user.name}) has no id"); return false; }
            if (!CanonicalCatalogueWriter.IsWellFormedId(id)) { errors.Add($"malformed: {kind} asset {asset.name}: id '{id}' {IdRule}"); return false; }
            if (seen.TryGetValue(id, out var other) && !ReferenceEquals(other, asset)) { errors.Add($"duplicate: {kind} id '{id}' is on both {other.name} and {asset.name}"); return false; }
            seen[id] = asset;
            return true;
        }

        static void AddStarter(StarterLoadout starter, Dictionary<string, ItemDefinition> items, Dictionary<string, OperativeDefinition> operatives,
            CatalogueDocument document, List<string> errors)
        {
            var listed = new HashSet<string>(StringComparer.Ordinal);
            var entries = starter.Operatives;
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry == null || entry.operative == null) { errors.Add($"missing: starter loadout {starter.name} entry {i} has no operative"); continue; }
                var operative = entry.operative;
                if (operative.Id == null || !operatives.TryGetValue(operative.Id, out var known) || !ReferenceEquals(known, operative))
                {
                    errors.Add($"incompatible: starter loadout {starter.name} entry {i} names operative asset {operative.name}, which is not an exported operative definition");
                    continue;
                }
                if (!listed.Add(operative.Id)) { errors.Add($"duplicate: starter loadout {starter.name} lists operative {operative.name} more than once"); continue; }
                var row = new CatalogueStarterOperative { OperativeId = operative.Id };
                var equipped = new HashSet<ItemSlot>();
                var grants = entry.items ?? new StarterGrant[0];
                for (var j = 0; j < grants.Length; j++)
                {
                    var grant = grants[j];
                    var where = $"starter loadout {starter.name}, operative {operative.name}, grant {j}";
                    if (!GrantIsUsable(grant.item, grant.quantity, where, items, errors))
                        continue;
                    if (grant.equip && grant.item.Slot == ItemSlot.None) { errors.Add($"incompatible: {where}: item {grant.item.Id} has no equipment slot, so it cannot be equipped"); continue; }
                    if (grant.equip && !equipped.Add(grant.item.Slot)) { errors.Add($"incompatible: {where}: a second item to equip in the {grant.item.Slot} slot"); continue; }
                    row.Grants.Add(new CatalogueGrant { ItemId = grant.item.Id, Quantity = grant.quantity, Equip = grant.equip });
                }
                document.Starter.Operatives.Add(row);
            }
            document.Starter.Operatives.Sort((x, y) => string.CompareOrdinal(x.OperativeId, y.OperativeId));

            var stash = starter.Stash;
            for (var k = 0; k < stash.Count; k++)
            {
                var grant = stash[k];
                var where = $"starter loadout {starter.name}, stash grant {k}";
                if (!GrantIsUsable(grant.item, grant.quantity, where, items, errors))
                    continue;
                if (grant.equip) { errors.Add($"incompatible: {where}: a stash item cannot be equipped"); continue; }
                document.Starter.Stash.Add(new CatalogueStashGrant { ItemId = grant.item.Id, Quantity = grant.quantity });
            }
        }

        static bool GrantIsUsable(ItemDefinition item, int quantity, string where, Dictionary<string, ItemDefinition> items, List<string> errors)
        {
            if (item == null) { errors.Add($"missing: {where} has no item"); return false; }
            if (!InCatalogue(item, items)) { errors.Add($"incompatible: {where}: item asset {item.name} is not in the item catalogue"); return false; }
            if (quantity < 1) { errors.Add($"malformed: {where}: quantity {quantity} is below 1"); return false; }
            return true;
        }

        // The exported definition with this id, and the very same asset (Unity's == for a missing reference).
        static bool InCatalogue(ItemDefinition item, Dictionary<string, ItemDefinition> items) =>
            item != null && item.Id != null && items.TryGetValue(item.Id, out var known) && ReferenceEquals(known, item);

        static string CategoryWord(ItemCategory category)
        {
            switch (category)
            {
                case ItemCategory.Weapon: return "weapon";
                case ItemCategory.Armor: return "armor";
                case ItemCategory.Utility: return "utility";
                case ItemCategory.Consumable: return "consumable";
                default: return null;
            }
        }

        static string SlotWord(ItemSlot slot)
        {
            switch (slot)
            {
                case ItemSlot.None: return "none";
                case ItemSlot.Weapon: return "weapon";
                case ItemSlot.Armor: return "armor";
                case ItemSlot.Utility: return "utility";
                default: return null;
            }
        }
    }
}
