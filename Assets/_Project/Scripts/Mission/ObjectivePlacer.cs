using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Chooses where the mission's objective content goes, from the generated layout alone: pure integer tile work with its
    /// own seed stream (SeededRandom.ForObjectives), so it is deterministic per seed and attempt and independent of combat
    /// randomness. Rules (decision 033): the terminal goes in a room other than the friendly one and at least half the
    /// farthest room-graph distance from it, preferring rooms with obstacles (usable combat geometry), on a tile with one
    /// free tile on every side (a 3x3 block; the post-bake NavMesh checks are the authority on connectivity); guards are
    /// extra hostile tiles in that room; the extraction zone goes in a different room (the friendly room only as a last
    /// resort), preferably one or two rooms from the terminal's, clear of every spawn and guard. A failure (no tile) fails the
    /// attempt and the pipeline retries.
    /// </summary>
    public static class ObjectivePlacer
    {
        /// <summary>Free tiles required on every side (1 = a 3x3 block, measured: 2 fits on only 9% of layouts) of a terminal or extraction centre.</summary>
        public const int FreeRadius = 1;
        public const float ExtractionClearance = 4f;
        public const int GuardMinReach = 2;
        public const int GuardMaxReach = 6;

        public static bool TryPlace(MissionLayout layout, MissionSettings settings, out ObjectivePlan plan, out string reason)
        {
            plan = null;
            reason = null;
            var rng = SeededRandom.ForObjectives(layout.Seed, layout.Attempt);
            var rooms = layout.Rooms;
            var blocked = ObstacleMask(layout);
            var spawns = new List<Vector2Int>(layout.FriendlySpawns);
            spawns.AddRange(layout.HostileSpawns);

            // Terminal room: not the friendly room, in the deeper half of the room graph, rooms with obstacles first.
            // Terminal tiles come from the room inset by FreeRadius, so its whole block lies inside the room and it never stands in a doorway.
            var fromFriendly = RoomDistances(layout, layout.FriendlyRoom);
            var farthest = 0;
            foreach (var distance in fromFriendly)
                farthest = Mathf.Max(farthest, distance);
            var minimum = Mathf.Max(1, (farthest + 1) / 2);
            var withObstacles = new List<int>();
            var bare = new List<int>();
            for (var r = 0; r < rooms.Count; r++)
            {
                if (r == layout.FriendlyRoom || fromFriendly[r] < minimum)
                    continue;
                (HasObstacle(layout, blocked, rooms[r].Rect) ? withObstacles : bare).Add(r);
            }
            rng.Shuffle(withObstacles);
            rng.Shuffle(bare);
            withObstacles.AddRange(bare);

            var terminalRoom = -1;
            var terminalTile = default(Vector2Int);
            foreach (var r in withObstacles)
            {
                var tiles = FreeTiles(layout, blocked, Shrink(rooms[r].Rect, FreeRadius), spawns, MissionConstants.SpawnSpacing);
                if (tiles.Count == 0)
                    continue;
                terminalRoom = r;
                terminalTile = tiles[rng.NextInt(tiles.Count)];
                break;
            }
            if (terminalRoom < 0)
            {
                reason = "no room in the deeper half has a free terminal tile";
                return false;
            }

            // Guards: extra hostile tiles near the terminal, in its room.
            var guards = new List<Vector2Int>();
            if (settings.guardCount > 0)
            {
                var region = Shrink(rooms[terminalRoom].Rect, 1);
                var candidates = new List<Vector2Int>();
                for (var y = region.yMin; y < region.yMax; y++)
                {
                    for (var x = region.xMin; x < region.xMax; x++)
                    {
                        var tile = new Vector2Int(x, y);
                        var reach = Chebyshev(tile, terminalTile);
                        if (reach < GuardMinReach || reach > GuardMaxReach || NearBlocked(layout, blocked, tile, 1))
                            continue;
                        candidates.Add(tile);
                    }
                }
                rng.Shuffle(candidates);
                var placed = new List<Vector2Int>(spawns);
                foreach (var tile in candidates)
                {
                    if (guards.Count >= settings.guardCount)
                        break;
                    if (TooClose(tile, placed, MissionConstants.SpawnSpacing) || TooClose(tile, layout.FriendlySpawns, settings.minTeamSeparation))
                        continue;
                    guards.Add(tile);
                    placed.Add(tile);
                }
            }

            // Extraction: another room, one or two rooms from the terminal's if possible, the friendly room only as a last resort.
            var fromTerminal = RoomDistances(layout, terminalRoom);
            var near = new List<int>();
            var elsewhere = new List<int>();
            for (var r = 0; r < rooms.Count; r++)
            {
                if (r == terminalRoom || r == layout.FriendlyRoom)
                    continue;
                (fromTerminal[r] >= 1 && fromTerminal[r] <= 2 ? near : elsewhere).Add(r);
            }
            rng.Shuffle(near);
            rng.Shuffle(elsewhere);
            near.AddRange(elsewhere);
            near.Add(layout.FriendlyRoom);

            var avoid = new List<Vector2Int>(spawns);
            avoid.AddRange(guards);
            var extractionRoom = -1;
            var extractionTile = default(Vector2Int);
            foreach (var r in near)
            {
                if (r == terminalRoom)
                    continue;
                var tiles = FreeTiles(layout, blocked, rooms[r].Rect, avoid, ExtractionClearance);
                if (tiles.Count == 0)
                    continue;
                extractionRoom = r;
                extractionTile = tiles[rng.NextInt(tiles.Count)];
                break;
            }
            if (extractionRoom < 0)
            {
                reason = "no room has a free extraction tile";
                return false;
            }

            plan = new ObjectivePlan(terminalRoom, terminalTile, guards, extractionRoom, extractionTile);
            return true;
        }

        // Breadth-first room-graph distance from one room (-1 for a room not reachable through the connections).
        static int[] RoomDistances(MissionLayout layout, int from)
        {
            var n = layout.Rooms.Count;
            var distance = new int[n];
            for (var i = 0; i < n; i++)
                distance[i] = -1;
            distance[from] = 0;
            var queue = new Queue<int>();
            queue.Enqueue(from);
            while (queue.Count > 0)
            {
                var room = queue.Dequeue();
                foreach (var connection in layout.Connections)
                {
                    var next = connection.RoomA == room ? connection.RoomB : connection.RoomB == room ? connection.RoomA : -1;
                    if (next < 0 || distance[next] >= 0)
                        continue;
                    distance[next] = distance[room] + 1;
                    queue.Enqueue(next);
                }
            }
            return distance;
        }

        // Tiles covered by an obstacle (walls stand on void tiles and are excluded; the floor mask covers them).
        internal static bool[] ObstacleMask(MissionLayout layout)
        {
            var mask = new bool[layout.Width * layout.Height];
            foreach (var box in layout.Boxes)
            {
                if (box.Kind == MissionBoxKind.Wall)
                    continue;
                var f = box.Footprint;
                for (var y = f.yMin; y < f.yMax; y++)
                    for (var x = f.xMin; x < f.xMax; x++)
                        mask[y * layout.Width + x] = true;
            }
            return mask;
        }

        static bool IsBlocked(MissionLayout layout, bool[] blocked, int x, int y) =>
            x >= 0 && y >= 0 && x < layout.Width && y < layout.Height && blocked[y * layout.Width + x];

        static bool HasObstacle(MissionLayout layout, bool[] blocked, RectInt rect)
        {
            for (var y = rect.yMin; y < rect.yMax; y++)
                for (var x = rect.xMin; x < rect.xMax; x++)
                    if (IsBlocked(layout, blocked, x, y))
                        return true;
            return false;
        }

        static bool NearBlocked(MissionLayout layout, bool[] blocked, Vector2Int tile, int radius)
        {
            for (var dy = -radius; dy <= radius; dy++)
                for (var dx = -radius; dx <= radius; dx++)
                    if (IsBlocked(layout, blocked, tile.x + dx, tile.y + dy))
                        return true;
            return false;
        }

        // Every tile within FreeRadius is floor and carries no obstacle.
        static bool FreeNeighbourhood(MissionLayout layout, bool[] blocked, Vector2Int tile)
        {
            for (var dy = -FreeRadius; dy <= FreeRadius; dy++)
            {
                for (var dx = -FreeRadius; dx <= FreeRadius; dx++)
                {
                    var x = tile.x + dx;
                    var y = tile.y + dy;
                    if (!layout.IsFloor(x, y) || IsBlocked(layout, blocked, x, y))
                        return false;
                }
            }
            return true;
        }

        internal static List<Vector2Int> FreeTiles(MissionLayout layout, bool[] blocked, RectInt rect, List<Vector2Int> avoid, float minimumDistance)
        {
            var tiles = new List<Vector2Int>();
            for (var y = rect.yMin; y < rect.yMax; y++)
            {
                for (var x = rect.xMin; x < rect.xMax; x++)
                {
                    var tile = new Vector2Int(x, y);
                    if (FreeNeighbourhood(layout, blocked, tile) && !TooClose(tile, avoid, minimumDistance))
                        tiles.Add(tile);
                }
            }
            return tiles;
        }

        static bool TooClose(Vector2Int tile, IReadOnlyList<Vector2Int> others, float distance)
        {
            foreach (var other in others)
            {
                if (Vector2.Distance(tile, other) < distance)
                    return true;
            }
            return false;
        }

        static int Chebyshev(Vector2Int a, Vector2Int b) => Mathf.Max(Mathf.Abs(a.x - b.x), Mathf.Abs(a.y - b.y));

        internal static RectInt Shrink(RectInt rect, int by) => new RectInt(rect.x + by, rect.y + by, rect.width - 2 * by, rect.height - 2 * by);
    }
}
