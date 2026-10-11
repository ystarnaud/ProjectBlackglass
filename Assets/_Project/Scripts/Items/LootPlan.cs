using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    public readonly struct LootItemPlan
    {
        public LootItemPlan(string definitionId, int quantity)
        {
            DefinitionId = definitionId;
            Quantity = quantity;
        }

        public string DefinitionId { get; }
        public int Quantity { get; }
    }

    /// <summary>One container to build: its tile, its room and what it holds (definition ids and quantities, no instance ids).</summary>
    public sealed class LootContainerPlan
    {
        public LootContainerPlan(Vector2Int tile, int room, IReadOnlyList<LootItemPlan> items)
        {
            Tile = tile;
            Room = room;
            Items = items;
        }

        public Vector2Int Tile { get; }
        public int Room { get; }
        public IReadOnlyList<LootItemPlan> Items { get; }
    }

    /// <summary>
    /// Where the mission's loot containers go and what they hold, as pure data. Hash is FNV-1a over the table version, the
    /// request and every container, so tests can pin and compare placements. Instance ids are not part of it: they are
    /// created when the containers are built and differ between deployments.
    /// </summary>
    public sealed class LootPlan
    {
        public static readonly LootPlan Empty = new LootPlan(0, 0, new List<LootContainerPlan>(), string.Empty);

        public LootPlan(int tableVersion, int requested, List<LootContainerPlan> containers, string shortfall)
        {
            TableVersion = tableVersion;
            Requested = requested;
            Containers = containers.AsReadOnly();
            Shortfall = shortfall ?? string.Empty;
            Hash = ComputeHash();
        }

        public int TableVersion { get; }
        public int Requested { get; }
        public IReadOnlyList<LootContainerPlan> Containers { get; }
        public int Placed => Containers.Count;
        /// <summary>Empty when every requested container was placed, else why some were not.</summary>
        public string Shortfall { get; }
        public ulong Hash { get; }

        ulong ComputeHash()
        {
            var h = 14695981039346656037UL;
            void Add(int value)
            {
                unchecked
                {
                    h ^= (uint)value;
                    h *= 1099511628211UL;
                }
            }
            Add(TableVersion);
            Add(Requested);
            Add(Containers.Count);
            foreach (var container in Containers)
            {
                Add(container.Room);
                Add(container.Tile.x);
                Add(container.Tile.y);
                Add(container.Items.Count);
                foreach (var item in container.Items)
                {
                    Add(item.DefinitionId.Length);
                    foreach (var c in item.DefinitionId)
                        Add(c);
                    Add(item.Quantity);
                }
            }
            return h;
        }
    }
}
