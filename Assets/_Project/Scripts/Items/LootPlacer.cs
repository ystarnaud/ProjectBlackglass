using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Chooses where loot containers go and what they hold, from the generated layout and the objective and security plans
    /// alone: pure integer tile work, deterministic per seed, attempt and table, with its own random streams
    /// (SeededRandom.ForLoot for tiles, ForLootContents per container for items), so loot never changes the layout and a
    /// change to the table's items never moves a container. Rules: against a wall or in a corner of its room with clear
    /// floor in front and no doorway beside it (ObjectivePlacer.WallTiles); a room with no such tile falls back to a free 3x3
    /// block at least FallbackInset tiles inside (a lane of two tiles or more to the wall, never the one-tile lane). Either way AvoidDistance tiles from every spawn, guard, terminal,
    /// camera terminal, extraction tile and earlier container, and outside the squad's starting room when another room
    /// exists. Loot is optional: a container that finds no tile is reported in Shortfall, the attempt does not fail.
    /// </summary>
    public static class LootPlacer
    {
        public const float AvoidDistance = 3f;
        /// <summary>
        /// How far inside its room a container's tile must be when the room has no wall tile (the fallback). At least 2, so no
        /// one-tile lane is left between the crate and the wall (the NavMesh bake closes it). As the only rule, an inset of 2
        /// placed all three containers on just 81.7% of 60 test layouts; as a fallback behind the wall rule it is rarely needed.
        /// </summary>
        public const int FallbackInset = 2;

        public static LootPlan Place(MissionLayout layout, ObjectivePlan objectives, SecurityPlan security, LootTable table,
            float avoidDistance = AvoidDistance)
        {
            if (table == null || table.ContainerCount < 1)
                return table == null ? LootPlan.Empty : new LootPlan(table.Version, 0, new List<LootContainerPlan>(), string.Empty);

            var rng = SeededRandom.ForLoot(layout.Seed, layout.Attempt);
            var blocked = ObjectivePlacer.ObstacleMask(layout);
            var avoid = new List<Vector2Int>(layout.FriendlySpawns);
            avoid.AddRange(layout.HostileSpawns);
            if (objectives != null)
            {
                avoid.AddRange(objectives.GuardTiles);
                avoid.Add(objectives.TerminalTile);
                avoid.Add(objectives.ExtractionTile);
            }
            if (security != null && security.HasTerminal)
                avoid.Add(security.TerminalTile);

            // Rooms in a seeded order, the squad's own room last (only used when it is the only room).
            var order = new List<int>();
            for (var r = 0; r < layout.Rooms.Count; r++)
            {
                if (r != layout.FriendlyRoom || layout.Rooms.Count == 1)
                    order.Add(r);
            }
            rng.Shuffle(order);

            var containers = new List<LootContainerPlan>();
            for (var i = 0; i < table.ContainerCount; i++)
            {
                for (var step = 0; step < order.Count; step++)
                {
                    var room = order[(i + step) % order.Count];
                    // Against a wall or in a corner first (a crate mid-room leaves narrow lanes the NavMesh closes); a room with
                    // no such tile falls back to the inset block.
                    var rect = layout.Rooms[room].Rect;
                    var tiles = ObjectivePlacer.WallTiles(layout, blocked, rect, avoid, avoidDistance);
                    if (tiles.Count == 0)
                        tiles = ObjectivePlacer.FreeTiles(layout, blocked, ObjectivePlacer.Shrink(rect, FallbackInset), avoid, avoidDistance);
                    if (tiles.Count == 0)
                        continue;
                    var tile = tiles[rng.NextInt(tiles.Count)];
                    containers.Add(new LootContainerPlan(tile, room, Roll(table, SeededRandom.ForLootContents(layout.Seed, layout.Attempt, i))));
                    avoid.Add(tile);
                    break;
                }
            }
            var shortfall = containers.Count < table.ContainerCount
                ? $"placed {containers.Count} of {table.ContainerCount} loot containers: no free tile for the rest"
                : string.Empty;
            return new LootPlan(table.Version, table.ContainerCount, containers, shortfall);
        }

        static List<LootItemPlan> Roll(LootTable table, SeededRandom rng)
        {
            var total = 0;
            foreach (var entry in table.Entries)
                total += entry.weight;
            var count = rng.NextInt(table.MinItems, table.MaxItems + 1);
            var items = new List<LootItemPlan>(count);
            for (var n = 0; n < count; n++)
            {
                var pick = rng.NextInt(total);
                foreach (var entry in table.Entries)
                {
                    if (pick < entry.weight)
                    {
                        items.Add(new LootItemPlan(entry.item.Id, rng.NextInt(entry.minQuantity, entry.maxQuantity + 1)));
                        break;
                    }
                    pick -= entry.weight;
                }
            }
            return items;
        }
    }
}
