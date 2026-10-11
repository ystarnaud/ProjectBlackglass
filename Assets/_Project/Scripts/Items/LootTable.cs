using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>One line of a loot table: an item, a quantity range and a weight (higher is more likely).</summary>
    [Serializable]
    public struct LootEntry
    {
        public ItemDefinition item;
        [Min(1)] public int minQuantity;
        [Min(1)] public int maxQuantity;
        [Min(1)] public int weight;
    }

    /// <summary>
    /// What generated containers hold: how many containers a mission asks for, how many items each rolls, and the weighted
    /// entries they roll from. Authored data. `Version` is part of the loot plan's hash, so a table change is visible in a
    /// reproduced seed; it never affects the mission layout or where containers stand.
    /// </summary>
    [CreateAssetMenu(menuName = "Blackglass/Loot Table", fileName = "LootTable")]
    public sealed class LootTable : ScriptableObject
    {
        public const int MaxContainers = 6;

        [SerializeField, Min(1)] int version = 1;
        [SerializeField, Range(0, MaxContainers)] int containerCount = 3;
        [SerializeField, Min(1)] int minItems = 1;
        [SerializeField, Min(1)] int maxItems = 2;
        [SerializeField] LootEntry[] entries = new LootEntry[0];

        public int Version => version;
        public int ContainerCount => containerCount;
        public int MinItems => minItems;
        public int MaxItems => maxItems;
        public LootEntry[] Entries => entries;

        public bool IsValid(out string problem)
        {
            if (containerCount < 0 || containerCount > MaxContainers)
                problem = $"the container count must be 0 to {MaxContainers}";
            else if (minItems < 1 || maxItems < minItems)
                problem = "the items per container range is empty";
            else if (entries == null || entries.Length == 0)
                problem = "there are no entries";
            else
            {
                problem = null;
                for (var i = 0; i < entries.Length && problem == null; i++)
                {
                    var entry = entries[i];
                    if (entry.item == null)
                        problem = $"entry {i} has no item";
                    else if (entry.weight < 1)
                        problem = $"entry {i} ({entry.item.DisplayName}) has a weight below 1";
                    else if (entry.minQuantity < 1 || entry.maxQuantity < entry.minQuantity)
                        problem = $"entry {i} ({entry.item.DisplayName}) has an empty quantity range";
                }
            }
            return problem == null;
        }

        internal static LootTable Create(int version, int containerCount, int minItems, int maxItems, LootEntry[] entries)
        {
            var table = CreateInstance<LootTable>();
            table.version = version;
            table.containerCount = containerCount;
            table.minItems = minItems;
            table.maxItems = maxItems;
            table.entries = entries ?? new LootEntry[0];
            return table;
        }
    }
}
