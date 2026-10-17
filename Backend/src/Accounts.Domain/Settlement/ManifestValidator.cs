using Blackglass.Accounts.Domain.Attempts;
using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.Domain.Inventory;

namespace Blackglass.Accounts.Domain.Settlement;

/// <summary>What the backend stored about the attempt and participant, read under the settlement's locks.</summary>
/// <param name="Now">The transaction's clock (database time, read after the locks).</param>
public sealed record AttemptFacts(Guid AttemptId, long AccountId, Guid ProcessSessionId, string BuildRevision, string CatalogueVersion,
    string LayoutHash, int LootTableVersion, string LootHash, DateTime AttemptStartedAt, DateTime ParticipantStartedAt, DateTime Now);

/// <summary>V13's provisional plausibility limits (spec §19): the hard cap plus slack, and the clock tolerance.</summary>
public sealed record SettlementLimits(int MaxElapsedMs, int ElapsedToleranceMs)
{
    public static readonly SettlementLimits Default = new(7_500_000, 60_000);
}

/// <summary>
/// Everything validation and planning read besides the manifest: the participant's admission snapshot, the attempt's stored
/// facts, its registered pool and objectives, the per-line collections of the attempt's already settled participants, the
/// admission catalogue (never the current one, S9-B20.3) and the process's recorded hash, if any.
/// </summary>
public sealed record SettlementContext(LoadoutSnapshot Snapshot, AttemptFacts Attempt, IReadOnlyList<PoolLine> Pool, IReadOnlyList<Objective> Objectives,
    IReadOnlyDictionary<(int ContainerIndex, int Line), int> SettledCollections, GameCatalogue AdmissionCatalogue, string? RecordedHash, SettlementLimits Limits);

/// <summary>
/// V4-V15 (plan §F) over a parsed manifest. Pure: it returns every failure as a bounded code ("V8.owned_exceeds"), sorted
/// ordinally and distinct; an empty list means valid. V16 (the inventory base) is the store's, under the inventory lock; V17
/// (a backend-only outcome) is refused by the parser. When V6 fails, the per-operative rules V8-V12 are skipped (they would
/// compare against the wrong operatives).
/// </summary>
public static class ManifestValidator
{
    public static IReadOnlyList<string> Validate(ResultManifest m, string manifestHash, SettlementContext c)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(c);
        var errors = new SortedSet<string>(StringComparer.Ordinal);
        var a = c.Attempt;
        if (m.BuildRevision != a.BuildRevision) errors.Add("V4.build");
        if (m.CatalogueVersion != a.CatalogueVersion || m.CatalogueVersion != c.AdmissionCatalogue.Version) errors.Add("V4.catalogue");
        if (m.LayoutHash != a.LayoutHash) errors.Add("V5.layout");
        if (m.LootTableVersion != a.LootTableVersion || m.LootHash != a.LootHash) errors.Add("V5b.loot");

        var snapshot = c.Snapshot.Operatives.OrderBy(o => o.Slot).ToList();
        var operativesMatch = m.Operatives.Count == snapshot.Count
            && m.Operatives.OrderBy(o => o.Slot).Zip(snapshot).All(p => p.First.Slot == p.Second.Slot && p.First.OperativeId == p.Second.OperativeId);
        if (!operativesMatch) errors.Add("V6.operatives");

        var registered = c.Objectives.Select(o => o.ObjectiveId).ToHashSet(StringComparer.Ordinal);
        if (!registered.SetEquals(m.Objectives.Select(o => o.ObjectiveId)) || m.Objectives.Count != registered.Count) errors.Add("V7.objectives");
        else if (m.Outcome == "success" && c.Objectives.Where(o => o.Required).Any(o => m.Objectives.Single(s => s.ObjectiveId == o.ObjectiveId).State != "completed"))
            errors.Add("V7.success_incomplete");

        if (operativesMatch) ValidateItems(m, c, snapshot, errors);

        if (a.ParticipantStartedAt > a.Now) errors.Add("V13.started");
        var sinceStart = (long)(a.Now - a.AttemptStartedAt).TotalMilliseconds;
        if (m.ServerElapsedMs > c.Limits.MaxElapsedMs || m.ServerElapsedMs > sinceStart + c.Limits.ElapsedToleranceMs) errors.Add("V13.elapsed");
        if (m.Integrity != "ok") errors.Add("V14.integrity");
        if (c.RecordedHash is { } recorded && recorded != manifestHash) errors.Add("V15.recorded_hash");
        return [.. errors];
    }

    static void ValidateItems(ResultManifest m, SettlementContext c, List<SnapshotOperative> snapshot, SortedSet<string> errors)
    {
        var catalogue = c.AdmissionCatalogue;
        var pool = c.Pool.ToDictionary(l => (l.ContainerIndex, l.Line));
        var collectedByLine = new Dictionary<(int, int), long>();
        foreach (var (operative, snap) in m.Operatives.OrderBy(o => o.Slot).Zip(snapshot))
        {
            var collected = operative.Collected.ToDictionary(x => (x.ContainerIndex, x.Line), x => x.Quantity);
            bool Known((int, int) line)
            {
                if (pool.ContainsKey(line)) return true;
                errors.Add("V9.unknown_line");
                return false;
            }

            // V8: owned consumption is consumables only, within the snapshot; loot consumption names a collected line and its definition.
            foreach (var used in operative.ConsumedOwned)
            {
                if (catalogue.FindItem(used.DefinitionId) is not { Category: ItemCategory.Consumable, Stackable: true }) errors.Add("V8.not_consumable");
                var owned = snap.Stacks.Where(s => s.DefinitionId == used.DefinitionId).Sum(s => (long)s.Quantity);
                if (used.Quantity > owned) errors.Add("V8.owned_exceeds");
            }
            foreach (var used in operative.ConsumedLoot)
            {
                var line = (used.ContainerIndex, used.Line);
                if (!Known(line)) continue;
                if (!collected.ContainsKey(line)) errors.Add("V8.loot_not_collected");
                if (pool[line].DefinitionId != used.DefinitionId) errors.Add("V8.loot_definition");
                if (catalogue.FindItem(pool[line].DefinitionId) is not { Category: ItemCategory.Consumable, Stackable: true }) errors.Add("V8.not_consumable");
            }

            // V10: owned stacks balance; snapshot items are held by their own operative and nothing else is held as owned.
            var definitions = snap.Stacks.Select(s => s.DefinitionId).Concat(operative.ConsumedOwned.Select(u => u.DefinitionId))
                .Concat(operative.HeldStacks.Select(h => h.DefinitionId)).Distinct(StringComparer.Ordinal);
            foreach (var definition in definitions)
            {
                long before = snap.Stacks.Where(s => s.DefinitionId == definition).Sum(s => (long)s.Quantity);
                long used = operative.ConsumedOwned.Where(u => u.DefinitionId == definition).Sum(u => (long)u.Quantity);
                long held = operative.HeldStacks.Where(h => h.DefinitionId == definition).Sum(h => (long)h.Quantity);
                if (before - used != held) errors.Add("V10.owned_stack");
            }
            if (!snap.Instances.Select(i => i.ItemId).ToHashSet().SetEquals(operative.HeldItems)) errors.Add("V10.owned_item");

            // V10 (loot): per line, collected − consumed = held, for this operative.
            var lines = operative.Collected.Select(x => (x.ContainerIndex, x.Line)).Concat(operative.ConsumedLoot.Select(x => (x.ContainerIndex, x.Line)))
                .Concat(operative.HeldLoot.Select(x => (x.ContainerIndex, x.Line))).Distinct();
            foreach (var line in lines)
            {
                if (!Known(line)) continue;
                long took = collected.GetValueOrDefault(line);
                long used = operative.ConsumedLoot.Where(x => (x.ContainerIndex, x.Line) == line).Sum(x => (long)x.Quantity);
                long held = operative.HeldLoot.Where(x => (x.ContainerIndex, x.Line) == line).Sum(x => (long)x.Quantity);
                if (took - used != held) errors.Add("V10.loot");
            }
            foreach (var (line, quantity) in collected)
                if (pool.ContainsKey(line)) collectedByLine[line] = collectedByLine.GetValueOrDefault(line) + quantity;

            // V11: the bag at the freeze, as bag entries under the admission catalogue.
            // (Definitions the admission catalogue lacks are already refused by V8 or V10: admission refused unknown items.)
            var contents = new List<(CatalogueItem, int)>();
            foreach (var id in operative.HeldItems)
            {
                var definition = snap.Instances.FirstOrDefault(i => i.ItemId == id)?.DefinitionId;
                if (definition is not null && catalogue.FindItem(definition) is { } item) contents.Add((item, 1));
            }
            foreach (var h in operative.HeldStacks)
            {
                if (catalogue.FindItem(h.DefinitionId) is { } item) contents.Add((item, h.Quantity));
            }
            foreach (var h in operative.HeldLoot)
            {
                if (pool.TryGetValue((h.ContainerIndex, h.Line), out var line) && catalogue.FindItem(line.DefinitionId) is { } item) contents.Add((item, h.Quantity));
            }
            if (InventoryRules.BagEntries(contents) > catalogue.Document.Inventory.BagCapacity) errors.Add("V11.capacity");

            // V12: each equipped reference is a held item of this operative, of the slot's kind, used once.
            var refs = new HashSet<(long?, int?, int?)>();
            foreach (var e in operative.Equipment)
            {
                if (!refs.Add((e.ItemId, e.ContainerIndex, e.Line))) errors.Add("V12.duplicate_ref");
                string? definition = null;
                if (e.ItemId is { } id)
                {
                    if (operative.HeldItems.Contains(id)) definition = snap.Instances.FirstOrDefault(i => i.ItemId == id)?.DefinitionId;
                    else errors.Add("V12.not_held");
                }
                else
                {
                    var line = (e.ContainerIndex!.Value, e.Line!.Value);
                    if (operative.HeldLoot.Any(h => (h.ContainerIndex, h.Line) == line) && pool.TryGetValue(line, out var pooled)) definition = pooled.DefinitionId;
                    else errors.Add("V12.not_held");
                }
                if (definition is not null && catalogue.FindItem(definition) is { } item && SlotWord(item.Slot) != e.Slot) errors.Add("V12.slot");
            }
        }

        // V9: per line, this manifest plus the attempt's settled participants never exceed the pool.
        foreach (var (line, quantity) in collectedByLine)
            if (quantity + c.SettledCollections.GetValueOrDefault(line) > pool[line].Quantity) errors.Add("V9.pool_exceeded");
    }

    static string SlotWord(ItemSlot slot) => slot switch
    {
        ItemSlot.Weapon => "weapon",
        ItemSlot.Armor => "armor",
        ItemSlot.Utility => "utility",
        _ => "none",
    };
}
