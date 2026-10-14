namespace Blackglass.Accounts.Domain.Catalogue;

/// <summary>
/// The semantic rules of a catalogue (the plan's rule table), mirrored by Unity's CatalogueBuilder so an export the
/// backend would refuse is never written. Every problem is added to errors; nothing stops at the first.
/// </summary>
public static class CatalogueValidator
{
    /// <summary>Unity's OperativeDefinition.MaxAbilities.</summary>
    public const int MaxAbilities = 4;

    public static void Validate(CatalogueDocument d, List<string> errors)
    {
        if (d.FormatVersion != CatalogueDocument.SupportedFormatVersion)
        {
            errors.Add($"incompatible: format version {d.FormatVersion} is not supported (this backend reads {CatalogueDocument.SupportedFormatVersion})");
            return; // nothing else can be judged against an unknown format
        }
        var items = CheckItems(d.Items, errors);
        CheckLoot(d.LootTable, items, errors);
        CheckProgression(d.Progression, errors);
        var operatives = CheckOperatives(d.Operatives, errors);
        CheckStarter(d.Starter, items, operatives, errors);
    }

    static Dictionary<string, CatalogueItem> CheckItems(IReadOnlyList<CatalogueItem> items, List<string> errors)
    {
        if (items.Count == 0) errors.Add("missing: items is empty");
        var ids = items.Select(i => i.Id).ToList();
        Unique(ids, "items", errors);
        Ascending(ids, "items", errors);
        var byId = new Dictionary<string, CatalogueItem>(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var path = $"items[{i}] '{item.Id}'";
            Id(item.Id, $"items[{i}].id", errors);
            if (item.MaxStack < 1) errors.Add($"malformed: {path}: the maximum stack is below 1");
            if (item.HealAmount < 0) errors.Add($"malformed: {path}: the heal amount is negative");
            if (item.Stackable != item.MaxStack > 1) errors.Add($"incompatible: {path}: stackable must be true exactly when the maximum stack is above 1");
            var slotProblem = item.Category switch
            {
                ItemCategory.Weapon when item.Slot != ItemSlot.Weapon => "a weapon must use the weapon slot",
                ItemCategory.Armor when item.Slot != ItemSlot.Armor => "armor must use the armor slot",
                ItemCategory.Utility when item.Slot != ItemSlot.Utility => "a utility item must use the utility slot",
                ItemCategory.Consumable when item.Slot != ItemSlot.None => "a consumable has no equipment slot",
                _ => null,
            };
            if (slotProblem is not null) errors.Add($"incompatible: {path}: {slotProblem}");
            if (item.Category == ItemCategory.Consumable && item.HealAmount < 1) errors.Add($"incompatible: {path}: a consumable needs a heal amount of at least 1");
            if (item.Category != ItemCategory.Consumable && item.MaxStack != 1) errors.Add($"incompatible: {path}: equipment cannot stack");
            byId.TryAdd(item.Id, item);
        }
        return byId;
    }

    static void CheckLoot(LootTable table, Dictionary<string, CatalogueItem> items, List<string> errors)
    {
        if (table.Version < 1) errors.Add("malformed: lootTable: the version must be at least 1");
        if (table.ContainerCount < 0) errors.Add("malformed: lootTable: the container count is negative");
        if (table.MinItems < 1 || table.MaxItems < table.MinItems) errors.Add("malformed: lootTable: the items per container range is empty");
        if (table.Entries.Count == 0) errors.Add("missing: lootTable.entries is empty");
        for (var i = 0; i < table.Entries.Count; i++)
        {
            var entry = table.Entries[i];
            var path = $"lootTable.entries[{i}]";
            Id(entry.ItemId, path + ".itemId", errors);
            if (!items.ContainsKey(entry.ItemId)) errors.Add($"incompatible: {path}: unknown item '{entry.ItemId}'");
            if (entry.Weight < 1) errors.Add($"malformed: {path}: the weight is below 1");
            if (entry.MinQuantity < 1 || entry.MaxQuantity < entry.MinQuantity) errors.Add($"malformed: {path}: the quantity range is empty");
        }
    }

    static void CheckProgression(Progression progression, List<string> errors)
    {
        var thresholds = progression.XpThresholds;
        if (thresholds.Count == 0) errors.Add("missing: progression.xpThresholds is empty");
        else if (thresholds[0] != 0) errors.Add("malformed: progression.xpThresholds: rank 1 must need 0 XP");
        for (var i = 1; i < thresholds.Count; i++)
        {
            if (thresholds[i] > thresholds[i - 1]) continue;
            errors.Add($"malformed: progression.xpThresholds: thresholds must strictly increase (rank {i + 1})");
            break;
        }
        if (progression.MissionCompletionXp < 0) errors.Add("malformed: progression.missionCompletionXp is negative");
        for (var i = 0; i < progression.ChoiceIds.Count; i++)
            Id(progression.ChoiceIds[i], $"progression.choiceIds[{i}]", errors);
        Unique(progression.ChoiceIds, "progression.choiceIds", errors);
        Ascending(progression.ChoiceIds, "progression.choiceIds", errors);
    }

    static HashSet<string> CheckOperatives(IReadOnlyList<OperativeDefinition> operatives, List<string> errors)
    {
        if (operatives.Count == 0) errors.Add("missing: operatives is empty");
        var ids = operatives.Select(o => o.Id).ToList();
        Unique(ids, "operatives", errors);
        Ascending(ids, "operatives", errors);
        for (var i = 0; i < operatives.Count; i++)
        {
            var operative = operatives[i];
            Id(operative.Id, $"operatives[{i}].id", errors);
            Id(operative.RoleId, $"operatives[{i}].roleId", errors);
            if (operative.AbilityIds.Count > MaxAbilities) errors.Add($"incompatible: operatives[{i}] '{operative.Id}': more than {MaxAbilities} abilities");
            for (var j = 0; j < operative.AbilityIds.Count; j++)
                Id(operative.AbilityIds[j], $"operatives[{i}].abilityIds[{j}]", errors);
            Unique(operative.AbilityIds, $"operatives[{i}].abilityIds", errors);
        }
        return new HashSet<string>(ids, StringComparer.Ordinal);
    }

    static void CheckStarter(Starter starter, Dictionary<string, CatalogueItem> items, HashSet<string> operatives, List<string> errors)
    {
        var ids = starter.Operatives.Select(o => o.OperativeId).ToList();
        Unique(ids, "starter.operatives", errors);
        Ascending(ids, "starter.operatives", errors);
        for (var i = 0; i < starter.Operatives.Count; i++)
        {
            var entry = starter.Operatives[i];
            var path = $"starter.operatives[{i}]";
            if (!operatives.Contains(entry.OperativeId)) errors.Add($"incompatible: {path}: unknown operative '{entry.OperativeId}'");
            var equipped = new HashSet<ItemSlot>();
            for (var j = 0; j < entry.Grants.Count; j++)
            {
                var grant = entry.Grants[j];
                var where = $"{path}.grants[{j}]";
                var item = CheckGrant(grant.ItemId, grant.Quantity, where, items, errors);
                if (!grant.Equip || item is null) continue;
                if (item.Slot == ItemSlot.None) errors.Add($"incompatible: {where}: item '{item.Id}' has no equipment slot, so it cannot be equipped");
                else if (!equipped.Add(item.Slot)) errors.Add($"incompatible: {where}: a second item to equip in the {item.Slot} slot");
            }
        }
        for (var k = 0; k < starter.Stash.Count; k++)
            CheckGrant(starter.Stash[k].ItemId, starter.Stash[k].Quantity, $"starter.stash[{k}]", items, errors);
    }

    static CatalogueItem? CheckGrant(string itemId, int quantity, string path, Dictionary<string, CatalogueItem> items, List<string> errors)
    {
        Id(itemId, path + ".itemId", errors);
        if (quantity < 1) errors.Add($"malformed: {path}: the quantity is below 1");
        if (items.TryGetValue(itemId, out var item)) return item;
        errors.Add($"incompatible: {path}: unknown item '{itemId}'");
        return null;
    }

    static void Id(string value, string path, List<string> errors)
    {
        if (!CanonicalCatalogueJson.IsWellFormedId(value))
            errors.Add($"malformed: {path} '{value}' is not a well-formed id (lowercase letters and digits separated by single '.' or '-', at most {CanonicalCatalogueJson.MaxIdLength} characters)");
    }

    static void Unique(IReadOnlyList<string> ids, string path, List<string> errors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (!seen.Add(id) && reported.Add(id)) errors.Add($"duplicate: {path} lists '{id}' more than once");
        }
    }

    static void Ascending(IReadOnlyList<string> ids, string path, List<string> errors)
    {
        for (var i = 1; i < ids.Count; i++)
        {
            if (string.CompareOrdinal(ids[i - 1], ids[i]) <= 0) continue;
            errors.Add($"malformed: {path} must be in ascending id order ('{ids[i]}' follows '{ids[i - 1]}')");
            return;
        }
    }
}
