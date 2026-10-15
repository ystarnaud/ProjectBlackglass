using System.Text.Json;

namespace Blackglass.Accounts.Domain.Catalogue;

/// <summary>
/// Reads catalogue JSON strictly into a CatalogueDocument. Every object must hold exactly its known members, once each,
/// with the right JSON kind; integers must fit 32 bits; enum words must be known. Each problem is added to errors with its
/// JSON path, and the result is null when there was any. Semantic rules are CatalogueValidator's.
/// </summary>
public static class CatalogueReader
{
    static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 16,
    };

    public static CatalogueDocument? Read(ReadOnlyMemory<byte> bytes, List<string> errors)
    {
        JsonDocument json;
        try { json = JsonDocument.Parse(bytes, Options); }
        catch (JsonException e) { errors.Add($"malformed: not valid JSON ({e.Message})"); return null; }
        using (json)
        {
            var start = errors.Count;
            var ok = new Reader(errors).TryDocument(json.RootElement, out var document);
            return ok && errors.Count == start ? document : null;
        }
    }

    delegate bool ElementReader<T>(JsonElement element, string path, out T value);

    sealed class Reader(List<string> errors)
    {
        public bool TryDocument(JsonElement e, out CatalogueDocument document)
        {
            document = null!;
            // Another format has another shape: say so (and how to fix it) instead of listing every member that differs.
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("formatVersion", out var declared)
                && declared.ValueKind == JsonValueKind.Number && declared.TryGetInt32(out var declaredFormat)
                && declaredFormat != CatalogueDocument.SupportedFormatVersion)
            {
                errors.Add(CatalogueDocument.UnsupportedFormat(declaredFormat));
                return false;
            }
            if (!TryMembers(e, "$", out var m, "formatVersion", "inventory", "items", "lootTable", "progression", "operatives", "starter"))
                return false;
            var ok = TryInt(m["formatVersion"], "$.formatVersion", out var format);
            ok &= TryInventory(m["inventory"], "$.inventory", out var inventory);
            ok &= TryList<CatalogueItem>(m["items"], "$.items", TryItem, out var items);
            ok &= TryLootTable(m["lootTable"], "$.lootTable", out var loot);
            ok &= TryProgression(m["progression"], "$.progression", out var progression);
            ok &= TryList<OperativeDefinition>(m["operatives"], "$.operatives", TryOperative, out var operatives);
            ok &= TryStarter(m["starter"], "$.starter", out var starter);
            if (ok)
                document = new CatalogueDocument(format, inventory, items, loot, progression, operatives, starter);
            return ok;
        }

        bool TryInventory(JsonElement e, string path, out InventorySettings inventory)
        {
            inventory = null!;
            if (!TryMembers(e, path, out var m, "bagCapacity"))
                return false;
            var ok = TryInt(m["bagCapacity"], path + ".bagCapacity", out var capacity);
            if (ok)
                inventory = new InventorySettings(capacity);
            return ok;
        }

        bool TryItem(JsonElement e, string path, out CatalogueItem item)
        {
            item = null!;
            if (!TryMembers(e, path, out var m, "id", "category", "slot", "stackable", "maxStack", "healAmount"))
                return false;
            var ok = TryString(m["id"], path + ".id", out var id);
            ok &= TryWord(m["category"], path + ".category", out ItemCategory category);
            ok &= TryWord(m["slot"], path + ".slot", out ItemSlot slot);
            ok &= TryBool(m["stackable"], path + ".stackable", out var stackable);
            ok &= TryInt(m["maxStack"], path + ".maxStack", out var maxStack);
            ok &= TryInt(m["healAmount"], path + ".healAmount", out var heal);
            if (ok)
                item = new CatalogueItem(id, category, slot, stackable, maxStack, heal);
            return ok;
        }

        bool TryLootTable(JsonElement e, string path, out LootTable table)
        {
            table = null!;
            if (!TryMembers(e, path, out var m, "version", "containerCount", "minItems", "maxItems", "entries"))
                return false;
            var ok = TryInt(m["version"], path + ".version", out var version);
            ok &= TryInt(m["containerCount"], path + ".containerCount", out var containers);
            ok &= TryInt(m["minItems"], path + ".minItems", out var min);
            ok &= TryInt(m["maxItems"], path + ".maxItems", out var max);
            ok &= TryList<LootEntry>(m["entries"], path + ".entries", TryLootEntry, out var entries);
            if (ok)
                table = new LootTable(version, containers, min, max, entries);
            return ok;
        }

        bool TryLootEntry(JsonElement e, string path, out LootEntry entry)
        {
            entry = null!;
            if (!TryMembers(e, path, out var m, "itemId", "minQuantity", "maxQuantity", "weight"))
                return false;
            var ok = TryString(m["itemId"], path + ".itemId", out var itemId);
            ok &= TryInt(m["minQuantity"], path + ".minQuantity", out var min);
            ok &= TryInt(m["maxQuantity"], path + ".maxQuantity", out var max);
            ok &= TryInt(m["weight"], path + ".weight", out var weight);
            if (ok)
                entry = new LootEntry(itemId, min, max, weight);
            return ok;
        }

        bool TryProgression(JsonElement e, string path, out Progression progression)
        {
            progression = null!;
            if (!TryMembers(e, path, out var m, "xpThresholds", "missionCompletionXp", "choiceIds"))
                return false;
            var ok = TryList<int>(m["xpThresholds"], path + ".xpThresholds", TryInt, out var thresholds);
            ok &= TryInt(m["missionCompletionXp"], path + ".missionCompletionXp", out var xp);
            ok &= TryList<string>(m["choiceIds"], path + ".choiceIds", TryString, out var choices);
            if (ok)
                progression = new Progression(thresholds, xp, choices);
            return ok;
        }

        bool TryOperative(JsonElement e, string path, out OperativeDefinition operative)
        {
            operative = null!;
            if (!TryMembers(e, path, out var m, "id", "roleId", "abilityIds"))
                return false;
            var ok = TryString(m["id"], path + ".id", out var id);
            ok &= TryString(m["roleId"], path + ".roleId", out var roleId);
            ok &= TryList<string>(m["abilityIds"], path + ".abilityIds", TryString, out var abilities);
            if (ok)
                operative = new OperativeDefinition(id, roleId, abilities);
            return ok;
        }

        bool TryStarter(JsonElement e, string path, out Starter starter)
        {
            starter = null!;
            if (!TryMembers(e, path, out var m, "operatives", "stash"))
                return false;
            var ok = TryList<StarterOperative>(m["operatives"], path + ".operatives", TryStarterOperative, out var operatives);
            ok &= TryList<StashGrant>(m["stash"], path + ".stash", TryStashGrant, out var stash);
            if (ok)
                starter = new Starter(operatives, stash);
            return ok;
        }

        bool TryStarterOperative(JsonElement e, string path, out StarterOperative operative)
        {
            operative = null!;
            if (!TryMembers(e, path, out var m, "operativeId", "grants"))
                return false;
            var ok = TryString(m["operativeId"], path + ".operativeId", out var id);
            ok &= TryList<StarterGrant>(m["grants"], path + ".grants", TryGrant, out var grants);
            if (ok)
                operative = new StarterOperative(id, grants);
            return ok;
        }

        bool TryGrant(JsonElement e, string path, out StarterGrant grant)
        {
            grant = null!;
            if (!TryMembers(e, path, out var m, "itemId", "quantity", "equip"))
                return false;
            var ok = TryString(m["itemId"], path + ".itemId", out var itemId);
            ok &= TryInt(m["quantity"], path + ".quantity", out var quantity);
            ok &= TryBool(m["equip"], path + ".equip", out var equip);
            if (ok)
                grant = new StarterGrant(itemId, quantity, equip);
            return ok;
        }

        bool TryStashGrant(JsonElement e, string path, out StashGrant grant)
        {
            grant = null!;
            if (!TryMembers(e, path, out var m, "itemId", "quantity"))
                return false;
            var ok = TryString(m["itemId"], path + ".itemId", out var itemId);
            ok &= TryInt(m["quantity"], path + ".quantity", out var quantity);
            if (ok)
                grant = new StashGrant(itemId, quantity);
            return ok;
        }

        // The members of an object, by name, when it has exactly the expected names, once each.
        bool TryMembers(JsonElement e, string path, out Dictionary<string, JsonElement> members, params string[] names)
        {
            members = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (e.ValueKind != JsonValueKind.Object) { errors.Add($"malformed: {path} must be an object"); return false; }
            var ok = true;
            foreach (var p in e.EnumerateObject())
            {
                if (Array.IndexOf(names, p.Name) < 0) { errors.Add($"malformed: {path}.{p.Name} is not a known member"); ok = false; }
                else if (!members.TryAdd(p.Name, p.Value)) { errors.Add($"duplicate: {path}.{p.Name} appears more than once"); ok = false; }
            }
            foreach (var name in names)
            {
                if (!members.ContainsKey(name)) { errors.Add($"missing: {path}.{name}"); ok = false; }
            }
            return ok;
        }

        bool TryList<T>(JsonElement e, string path, ElementReader<T> read, out List<T> list)
        {
            list = [];
            if (e.ValueKind != JsonValueKind.Array) { errors.Add($"malformed: {path} must be an array"); return false; }
            var ok = true;
            var i = 0;
            foreach (var element in e.EnumerateArray())
            {
                if (read(element, $"{path}[{i}]", out var value)) list.Add(value);
                else ok = false;
                i++;
            }
            return ok;
        }

        bool TryInt(JsonElement e, string path, out int value)
        {
            value = 0;
            if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out value))
                return true;
            errors.Add($"malformed: {path} must be a 32-bit integer");
            return false;
        }

        bool TryString(JsonElement e, string path, out string value)
        {
            value = "";
            if (e.ValueKind != JsonValueKind.String) { errors.Add($"malformed: {path} must be a string"); return false; }
            // An escaped lone surrogate (\ud800) parses but can't be decoded; it must surface as a catalogue error.
            try { value = e.GetString()!; return true; }
            catch (InvalidOperationException) { errors.Add($"malformed: {path} is not a valid string"); return false; }
        }

        bool TryBool(JsonElement e, string path, out bool value)
        {
            value = e.ValueKind == JsonValueKind.True;
            if (e.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return true;
            errors.Add($"malformed: {path} must be true or false");
            return false;
        }

        bool TryWord(JsonElement e, string path, out ItemCategory value) =>
            TryEnumWord<ItemCategory>(e, path, CanonicalCatalogueJson.Word, out value);

        bool TryWord(JsonElement e, string path, out ItemSlot value) =>
            TryEnumWord<ItemSlot>(e, path, CanonicalCatalogueJson.Word, out value);

        bool TryEnumWord<TEnum>(JsonElement e, string path, Func<TEnum, string> word, out TEnum value) where TEnum : struct, Enum
        {
            value = default;
            if (!TryString(e, path, out var text))
                return false;
            foreach (var candidate in Enum.GetValues<TEnum>())
            {
                if (word(candidate) == text) { value = candidate; return true; }
            }
            errors.Add($"malformed: {path} must be one of {string.Join(", ", Enum.GetValues<TEnum>().Select(word))}");
            return false;
        }
    }
}
