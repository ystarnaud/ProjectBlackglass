using System.Collections.Generic;
using System.Linq;
using Blackglass.Backend.Core;
using NUnit.Framework;

namespace Blackglass.Tests
{
    /// <summary>
    /// A fast inner check mirroring Backend ManifestValidator.ValidateItems' V8, V9, V10 and V12 for one participant (not V11,
    /// which needs the bag capacity). It is not the authority: the ledger-v1 scenarios run by the real validator are (Task 7),
    /// and any disagreement is settled there.
    /// </summary>
    internal static class Conservation
    {
        public static void Check(IReadOnlyList<OperativeSnapshot> snapshot, ParticipantFacts facts, IReadOnlyList<PoolLine> pool, LedgerCatalogue catalogue)
        {
            var errors = Errors(snapshot, facts.Operatives.Select(o => new Operative
            {
                Id = o.OperativeId, Slot = o.Slot,
                ConsumedOwned = o.ConsumedOwned.ToList(), ConsumedLoot = o.ConsumedLoot.ToList(), HeldItems = o.HeldItems.ToList(),
                HeldStacks = o.HeldStacks.ToList(), HeldLoot = o.HeldLoot.ToList(), Collected = o.Collected.ToList(),
                Equipment = o.Equipment.Select(e => (e.Slot, e.ItemId, e.ContainerIndex, e.Line)).ToList(),
            }).ToList(), pool, catalogue);
            Assert.That(errors, Is.Empty, "conservation");
        }

        public static void Check(IReadOnlyList<OperativeSnapshot> snapshot, ResultManifestV1 manifest, IReadOnlyList<PoolLine> pool, LedgerCatalogue catalogue)
        {
            var errors = Errors(snapshot, manifest.Operatives.Select(o => new Operative
            {
                Id = Base58Id.Format(o.OperativeId), Slot = o.Slot,
                ConsumedOwned = o.ConsumedOwned.ToList(), ConsumedLoot = o.ConsumedLoot.ToList(), HeldItems = o.HeldItems.Select(Base58Id.Format).ToList(),
                HeldStacks = o.HeldStacks.ToList(), HeldLoot = o.HeldLoot.ToList(), Collected = o.Collected.ToList(),
                Equipment = o.Equipment.Select(e => (e.Slot, e.ItemId.HasValue ? Base58Id.Format(e.ItemId.Value) : null, e.ContainerIndex, e.Line)).ToList(),
            }).ToList(), pool, catalogue);
            Assert.That(errors, Is.Empty, "conservation");
        }

        sealed class Operative
        {
            public string Id;
            public int Slot;
            public List<OwnedUse> ConsumedOwned;
            public List<LootUse> ConsumedLoot;
            public List<string> HeldItems;
            public List<OwnedStackFact> HeldStacks;
            public List<LootLotFact> HeldLoot;
            public List<LineCount> Collected;
            public List<(EquipSlot Slot, string ItemId, int? ContainerIndex, int? Line)> Equipment;
        }

        static List<string> Errors(IReadOnlyList<OperativeSnapshot> snapshot, List<Operative> operatives, IReadOnlyList<PoolLine> pool, LedgerCatalogue catalogue)
        {
            var errors = new List<string>();
            var lines = pool.ToDictionary(l => (l.ContainerIndex, l.Line));
            var collectedByLine = new Dictionary<(int, int), long>();
            var snap = snapshot.OrderBy(s => s.Slot).ToList();
            var ops = operatives.OrderBy(o => o.Slot).ToList();
            if (snap.Count != ops.Count || snap.Zip(ops, (s, o) => s.OperativeId == o.Id && s.Slot == o.Slot).Any(ok => !ok)) errors.Add("V6.operatives");
            foreach (var (s, o) in snap.Zip(ops, (s, o) => (s, o)))
            {
                bool Consumable(string definition) => catalogue.TryGet(definition, out var d) && d.Consumable && d.Stackable;
                var collected = o.Collected.ToDictionary(c => (c.ContainerIndex, c.Line), c => c.Quantity);
                foreach (var used in o.ConsumedOwned)
                {
                    if (!Consumable(used.DefinitionId)) errors.Add("V8.not_consumable");
                    if (used.Quantity > s.Stacks.Where(x => x.DefinitionId == used.DefinitionId).Sum(x => x.Quantity)) errors.Add("V8.owned_exceeds");
                }
                foreach (var used in o.ConsumedLoot)
                {
                    var line = (used.ContainerIndex, used.Line);
                    if (!lines.ContainsKey(line)) { errors.Add("V9.unknown_line"); continue; }
                    if (!collected.ContainsKey(line)) errors.Add("V8.loot_not_collected");
                    if (lines[line].DefinitionId != used.DefinitionId) errors.Add("V8.loot_definition");
                    if (!Consumable(lines[line].DefinitionId)) errors.Add("V8.not_consumable");
                }
                foreach (var definition in s.Stacks.Select(x => x.DefinitionId).Concat(o.ConsumedOwned.Select(x => x.DefinitionId)).Concat(o.HeldStacks.Select(x => x.DefinitionId)).Distinct())
                {
                    long before = s.Stacks.Where(x => x.DefinitionId == definition).Sum(x => (long)x.Quantity);
                    long used = o.ConsumedOwned.Where(x => x.DefinitionId == definition).Sum(x => (long)x.Quantity);
                    long held = o.HeldStacks.Where(x => x.DefinitionId == definition).Sum(x => (long)x.Quantity);
                    if (before - used != held) errors.Add("V10.owned_stack");
                }
                if (!new HashSet<string>(s.Items.Select(i => i.ItemId)).SetEquals(o.HeldItems)) errors.Add("V10.owned_item");
                foreach (var line in o.Collected.Select(x => (x.ContainerIndex, x.Line)).Concat(o.ConsumedLoot.Select(x => (x.ContainerIndex, x.Line)))
                    .Concat(o.HeldLoot.Select(x => (x.ContainerIndex, x.Line))).Distinct())
                {
                    if (!lines.ContainsKey(line)) { errors.Add("V9.unknown_line"); continue; }
                    long took = collected.TryGetValue(line, out var t) ? t : 0;
                    long used = o.ConsumedLoot.Where(x => (x.ContainerIndex, x.Line) == line).Sum(x => (long)x.Quantity);
                    long held = o.HeldLoot.Where(x => (x.ContainerIndex, x.Line) == line).Sum(x => (long)x.Quantity);
                    if (took - used != held) errors.Add("V10.loot");
                }
                foreach (var pair in collected)
                    collectedByLine[pair.Key] = (collectedByLine.TryGetValue(pair.Key, out var c) ? c : 0) + pair.Value;
                var refs = new HashSet<(string, int?, int?)>();
                foreach (var e in o.Equipment)
                {
                    if (!refs.Add((e.ItemId, e.ContainerIndex, e.Line))) errors.Add("V12.duplicate_ref");
                    string definition = null;
                    if (e.ItemId != null)
                    {
                        if (o.HeldItems.Contains(e.ItemId)) definition = s.Items.First(i => i.ItemId == e.ItemId).DefinitionId;
                        else errors.Add("V12.not_held");
                    }
                    else
                    {
                        var line = (e.ContainerIndex.Value, e.Line.Value);
                        if (o.HeldLoot.Any(h => (h.ContainerIndex, h.Line) == line) && lines.ContainsKey(line)) definition = lines[line].DefinitionId;
                        else errors.Add("V12.not_held");
                    }
                    if (definition != null && catalogue.TryGet(definition, out var d) && d.Slot != e.Slot) errors.Add("V12.slot");
                }
            }
            foreach (var pair in collectedByLine)
                if (pair.Value > lines[pair.Key].Quantity) errors.Add("V9.pool_exceeded");
            return errors.Distinct().ToList();
        }
    }
}
