using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    public sealed class MissionGenerationResult
    {
        internal MissionGenerationResult(MissionSettings settings, MissionLayout layout, IReadOnlyList<string> failures)
        {
            Settings = settings;
            Layout = layout;
            Failures = failures;
        }

        public bool Succeeded => Layout != null;
        public MissionLayout Layout { get; }
        /// <summary>The validated settings the attempts ran with.</summary>
        public MissionSettings Settings { get; }
        /// <summary>One line per failed attempt.</summary>
        public IReadOnlyList<string> Failures { get; }

        public string Describe() => Succeeded
            ? $"Mission seed {Settings.seed} ok on attempt {Layout.Attempt}"
            : $"Mission generation failed after {Failures.Count} attempts. {Settings.Describe()}\n  " + string.Join("\n  ", Failures);
    }

    /// <summary>
    /// Pure mission layout generation: a connected set of rooms on a coarse grid, corridors across the gutters between
    /// neighbouring rooms, a wall ring derived from the floor, then obstacles and spawns. Every random choice comes
    /// from SeededRandom.ForAttempt(seed, attempt), so attempt n of seed s is always the same layout.
    /// </summary>
    public static class MissionGenerator
    {
        /// <summary>Tries attempts 1..maxAttempts and returns the first valid layout, or the reason of every failure.</summary>
        public static MissionGenerationResult Generate(MissionSettings requested)
        {
            if (requested == null)
                throw new ArgumentNullException(nameof(requested));
            var settings = requested.Validated();
            var failures = new List<string>();
            for (var attempt = 1; attempt <= settings.maxAttempts; attempt++)
            {
                if (TryAttempt(settings, attempt, out var layout, out var reason))
                    return new MissionGenerationResult(settings, layout, failures);
                failures.Add($"attempt {attempt}: {reason}");
            }
            return new MissionGenerationResult(settings, null, failures);
        }

        /// <summary>One attempt. `settings` must already be validated. On failure `reason` says why and `layout` is null.</summary>
        public static bool TryAttempt(MissionSettings settings, int attempt, out MissionLayout layout, out string reason)
        {
            layout = null;
            reason = null;
            var rng = SeededRandom.ForAttempt(settings.seed, attempt);
            var width = settings.gridColumns * settings.cellSize;
            var height = settings.gridRows * settings.cellSize;

            var rooms = PlaceRooms(settings, rng);
            if (!TryConnect(settings, rng, rooms, out var connections, out reason))
                return false;

            var floor = new bool[width * height];
            foreach (var room in rooms)
                Fill(floor, width, room.Rect);
            foreach (var connection in connections)
                Fill(floor, width, connection.Strip);

            var obstacles = new ObstaclePlacer(floor, width, height, connections);
            PlaceTallObstacles(settings, rng, rooms, obstacles);
            PlaceLowCover(settings, rng, rooms, floor, obstacles);
            if (!IsConnected(floor, obstacles.Blocked, width, height, out reason))
                return false;

            if (!TryPlaceSpawns(settings, rng, rooms, connections, obstacles.Boxes, width, height, out var spawns, out reason))
                return false;

            var boxes = new List<MissionBox>();
            AddWalls(floor, width, height, boxes);
            boxes.AddRange(obstacles.Boxes);

            layout = new MissionLayout(settings.seed, attempt, width, height, floor)
            {
                Rooms = rooms,
                Connections = connections,
                FloorRects = RectCover.Decompose(floor, width, height),
                Boxes = boxes,
                FriendlyRoom = spawns.FriendlyRoom,
                HostileRooms = spawns.HostileRooms,
                FriendlyRegion = spawns.FriendlyRegion,
                HostileRegions = spawns.HostileRegions,
                FriendlySpawns = spawns.FriendlySpawns,
                HostileSpawns = spawns.HostileSpawns,
                UsedTiles = UsedRect(floor, width, height),
            };
            layout.Hash = ComputeHash(layout);
            return true;
        }

        const int PlacementTries = 8;
        // Free spots are scarce once the tall obstacles stand (the clearance leaves a room 3 to 6 tiles of space), so
        // low cover keeps looking across the rooms for longer. The first half of the tries keeps the full wall clearance
        // (free-standing cover you can walk around); in the second half a low wall may also touch a room wall with its
        // short end (a peninsula: both long faces keep a lane of at least Clearance to the other walls). A low wall never
        // lies flush along a wall, a crate is never flush, nothing stands flush against two walls, and a one-tile gap is
        // never used: it is too narrow for a unit on the eroded NavMesh (decision 030).
        const int LowCoverTries = 40;

        static void PlaceTallObstacles(MissionSettings s, SeededRandom rng, List<MissionRoom> rooms, ObstaclePlacer placer)
        {
            foreach (var room in rooms)
            {
                for (var b = 0; b < s.bafflesPerRoom; b++)
                {
                    for (var attempt = 0; attempt < PlacementTries; attempt++)
                    {
                        var horizontal = rng.Chance(0.5f);
                        var length = rng.NextInt(3, 6);
                        if (placer.TryPlaceRandom(rng, room, horizontal ? length : 1, horizontal ? 1 : length,
                                MissionBoxKind.Baffle, MissionConstants.WallHeight, MissionConstants.Clearance))
                            break;
                    }
                }
                if (rng.Chance(0.5f))
                {
                    for (var attempt = 0; attempt < PlacementTries; attempt++)
                    {
                        if (placer.TryPlaceRandom(rng, room, 1, 1, MissionBoxKind.Pillar, MissionConstants.WallHeight, MissionConstants.Clearance))
                            break;
                    }
                }
            }
        }

        static void PlaceLowCover(MissionSettings s, SeededRandom rng, List<MissionRoom> rooms, bool[] floor, ObstaclePlacer placer)
        {
            var floorTiles = 0;
            foreach (var tile in floor)
                if (tile)
                    floorTiles++;
            var target = (int)(s.lowCoverDensity * floorTiles / 100f + 0.5f);
            for (var i = 0; i < target; i++)
            {
                for (var attempt = 0; attempt < LowCoverTries; attempt++)
                {
                    // A new room per try: a room that a baffle already fills must not use up all the tries.
                    var room = rooms[rng.NextInt(rooms.Count)];
                    var wall = rng.Chance(0.6f);
                    var horizontal = rng.Chance(0.5f);
                    var w = wall ? (horizontal ? 3 : 1) : 1;
                    var h = wall ? (horizontal ? 1 : 3) : 1;
                    // End-on only: a horizontal (3 x 1) wall may touch the west or east wall, a vertical (1 x 3) one the
                    // south or north wall; the other axis keeps Clearance on both sides.
                    var endOn = wall && attempt >= LowCoverTries / 2;
                    if (placer.TryPlaceRandom(rng, room, w, h, wall ? MissionBoxKind.LowWall : MissionBoxKind.Crate, MissionConstants.LowHeight,
                            MissionConstants.Clearance, flushX: endOn && horizontal, flushY: endOn && !horizontal))
                        break;
                }
            }
        }

        /// <summary>
        /// True when every floor tile that no obstacle covers is reachable (4-connected) from the first such tile.
        /// </summary>
        internal static bool IsConnected(bool[] floor, bool[] blocked, int width, int height, out string reason)
        {
            reason = null;
            var start = -1;
            var open = 0;
            for (var i = 0; i < floor.Length; i++)
            {
                if (!floor[i] || blocked[i])
                    continue;
                open++;
                if (start < 0)
                    start = i;
            }
            if (start < 0)
            {
                reason = "no walkable floor";
                return false;
            }
            var seen = new bool[floor.Length];
            var queue = new Queue<int>();
            queue.Enqueue(start);
            seen[start] = true;
            var reached = 1;
            while (queue.Count > 0)
            {
                var index = queue.Dequeue();
                var x = index % width;
                var y = index / width;
                for (var d = 0; d < 4; d++)
                {
                    var nx = x + (d == 0 ? 1 : d == 1 ? -1 : 0);
                    var ny = y + (d == 2 ? 1 : d == 3 ? -1 : 0);
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                        continue;
                    var next = ny * width + nx;
                    if (!floor[next] || blocked[next] || seen[next])
                        continue;
                    seen[next] = true;
                    reached++;
                    queue.Enqueue(next);
                }
            }
            if (reached != open)
            {
                reason = $"the walkable floor is split: {reached} of {open} tiles reachable";
                return false;
            }
            return true;
        }

        sealed class ObstaclePlacer
        {
            readonly bool[] floor;
            readonly int width;
            readonly int height;
            readonly Dictionary<MissionBoxKind, int> counts = new Dictionary<MissionBoxKind, int>();

            readonly List<MissionConnection> connections;

            public ObstaclePlacer(bool[] floorMask, int gridWidth, int gridHeight, List<MissionConnection> corridors)
            {
                connections = corridors;
                floor = floorMask;
                width = gridWidth;
                height = gridHeight;
                Blocked = new bool[floorMask.Length];
            }

            public bool[] Blocked { get; }
            public List<MissionBox> Boxes { get; } = new List<MissionBox>();

            // A random footprint inside the room whose gap to each room wall is at least `wallClearance` tiles or, on an
            // axis whose flush flag is set, zero (touching the west or east wall for `flushX`, the south or north wall for
            // `flushY`); any other gap is never drawn. The caller sets at most one flag, so nothing touches two walls.
            public bool TryPlaceRandom(SeededRandom rng, MissionRoom room, int w, int h, MissionBoxKind kind, float boxHeight,
                int wallClearance, bool flushX = false, bool flushY = false)
            {
                var xCount = CountStarts(room.Rect.xMin, room.Rect.xMax - w, wallClearance, flushX);
                var yCount = CountStarts(room.Rect.yMin, room.Rect.yMax - h, wallClearance, flushY);
                if (xCount == 0 || yCount == 0)
                    return false;
                var x = NthStart(room.Rect.xMin, room.Rect.xMax - w, wallClearance, flushX, rng.NextInt(xCount));
                var y = NthStart(room.Rect.yMin, room.Rect.yMax - h, wallClearance, flushY, rng.NextInt(yCount));
                return TryPlace(new RectInt(x, y, w, h), kind, boxHeight);
            }

            // Start coordinates along one axis run from `lo` (touching the low wall) to `hi` (touching the high wall).
            // Without flush placement the allowed starts are lo + clearance .. hi - clearance in order, so drawing the
            // n-th one is the same draw as a plain range.
            static bool AllowedGap(int gap, int clearance, bool mayStandFlush) => gap >= clearance || (mayStandFlush && gap == 0);

            static bool AllowedStart(int start, int lo, int hi, int clearance, bool mayStandFlush) =>
                AllowedGap(start - lo, clearance, mayStandFlush) && AllowedGap(hi - start, clearance, mayStandFlush);

            static int CountStarts(int lo, int hi, int clearance, bool mayStandFlush)
            {
                var count = 0;
                for (var start = lo; start <= hi; start++)
                    if (AllowedStart(start, lo, hi, clearance, mayStandFlush))
                        count++;
                return count;
            }

            static int NthStart(int lo, int hi, int clearance, bool mayStandFlush, int n)
            {
                for (var start = lo; start <= hi; start++)
                    if (AllowedStart(start, lo, hi, clearance, mayStandFlush) && n-- == 0)
                        return start;
                throw new ArgumentOutOfRangeException(nameof(n));
            }

            // Callers keep the footprint inside a room with an allowed wall gap (TryPlaceRandom). A footprint closer than
            // Clearance to a corridor strip is refused here, so no obstacle ever narrows a door; the rest is the gap to
            // earlier obstacles and the connectivity check.
            bool TryPlace(RectInt rect, MissionBoxKind kind, float boxHeight)
            {
                var c = MissionConstants.Clearance;
                var inflated = new RectInt(rect.x - c, rect.y - c, rect.width + 2 * c, rect.height + 2 * c);
                foreach (var connection in connections)
                    if (inflated.Overlaps(connection.Strip))
                        return false;
                foreach (var existing in Boxes)
                    if (inflated.Overlaps(existing.Footprint))
                        return false;
                Mark(rect, true);
                if (!IsConnected(floor, Blocked, width, height, out _))
                {
                    Mark(rect, false);
                    return false;
                }
                counts.TryGetValue(kind, out var n);
                counts[kind] = ++n;
                Boxes.Add(new MissionBox($"{kind}_{n}", kind, rect, boxHeight));
                return true;
            }

            void Mark(RectInt rect, bool value)
            {
                for (var y = rect.yMin; y < rect.yMax; y++)
                    for (var x = rect.xMin; x < rect.xMax; x++)
                        Blocked[y * width + x] = value;
            }
        }

        sealed class SpawnPlan
        {
            public int FriendlyRoom;
            public List<int> HostileRooms = new List<int>();
            public RectInt FriendlyRegion;
            public List<RectInt> HostileRegions = new List<RectInt>();
            public List<Vector2Int> FriendlySpawns = new List<Vector2Int>();
            public List<Vector2Int> HostileSpawns = new List<Vector2Int>();
        }

        static bool TryPlaceSpawns(MissionSettings s, SeededRandom rng, List<MissionRoom> rooms, List<MissionConnection> connections,
            List<MissionBox> obstacles, int width, int height, out SpawnPlan plan, out string reason)
        {
            reason = null;
            plan = new SpawnPlan();
            var n = rooms.Count;

            // Room graph distances (breadth-first from every room).
            var adjacency = new List<int>[n];
            for (var i = 0; i < n; i++)
                adjacency[i] = new List<int>();
            foreach (var c in connections)
            {
                adjacency[c.RoomA].Add(c.RoomB);
                adjacency[c.RoomB].Add(c.RoomA);
            }
            var distance = new int[n][];
            for (var from = 0; from < n; from++)
            {
                var d = new int[n];
                for (var i = 0; i < n; i++)
                    d[i] = -1;
                d[from] = 0;
                var queue = new Queue<int>();
                queue.Enqueue(from);
                while (queue.Count > 0)
                {
                    var room = queue.Dequeue();
                    foreach (var next in adjacency[room])
                    {
                        if (d[next] >= 0)
                            continue;
                        d[next] = d[room] + 1;
                        queue.Enqueue(next);
                    }
                }
                distance[from] = d;
            }

            var best = -1;
            var pairs = new List<(int a, int b)>();
            for (var i = 0; i < n; i++)
            {
                for (var j = i + 1; j < n; j++)
                {
                    if (distance[i][j] > best)
                    {
                        best = distance[i][j];
                        pairs.Clear();
                    }
                    if (distance[i][j] == best)
                        pairs.Add((i, j));
                }
            }
            var pair = pairs[rng.NextInt(pairs.Count)];
            var friendlyRoom = pair.a;
            var mainHostile = pair.b;
            if (rng.Chance(0.5f))
                (friendlyRoom, mainHostile) = (mainHostile, friendlyRoom);

            var secondHostile = -1;
            var secondDistance = -1;
            for (var r = 0; r < n; r++)
            {
                if (r == friendlyRoom || r == mainHostile || distance[friendlyRoom][r] <= secondDistance)
                    continue;
                secondHostile = r;
                secondDistance = distance[friendlyRoom][r];
            }

            plan.FriendlyRoom = friendlyRoom;
            plan.FriendlyRegion = Shrink(rooms[friendlyRoom].Rect, 1);
            plan.HostileRooms.Add(mainHostile);
            plan.HostileRegions.Add(Shrink(rooms[mainHostile].Rect, 1));
            if (secondHostile >= 0)
            {
                plan.HostileRooms.Add(secondHostile);
                plan.HostileRegions.Add(Shrink(rooms[secondHostile].Rect, 1));
            }

            // Tiles within one tile of an obstacle are never spawn points.
            var avoid = new bool[width * height];
            foreach (var box in obstacles)
            {
                var f = box.Footprint;
                for (var y = f.yMin - 1; y < f.yMax + 1; y++)
                    for (var x = f.xMin - 1; x < f.xMax + 1; x++)
                        avoid[y * width + x] = true;
            }

            var placed = new List<Vector2Int>();
            Pick(Candidates(plan.FriendlyRegion, avoid, width), rng, s.friendlyCount, plan.FriendlySpawns, placed, null, 0f);
            if (plan.FriendlySpawns.Count < s.friendlyCount)
            {
                reason = $"the friendly room fits only {plan.FriendlySpawns.Count} of {s.friendlyCount} spawns";
                return false;
            }
            foreach (var region in plan.HostileRegions)
            {
                if (plan.HostileSpawns.Count >= s.hostileCount)
                    break;
                Pick(Candidates(region, avoid, width), rng, s.hostileCount, plan.HostileSpawns, placed, plan.FriendlySpawns, s.minTeamSeparation);
            }
            if (plan.HostileSpawns.Count < s.hostileCount)
            {
                reason = $"only {plan.HostileSpawns.Count} of {s.hostileCount} hostile spawns fit at {s.minTeamSeparation:0.#} m from the friendlies";
                return false;
            }
            return true;
        }

        static RectInt Shrink(RectInt rect, int by) => new RectInt(rect.x + by, rect.y + by, rect.width - 2 * by, rect.height - 2 * by);

        static List<Vector2Int> Candidates(RectInt region, bool[] avoid, int width)
        {
            var list = new List<Vector2Int>();
            for (var y = region.yMin; y < region.yMax; y++)
                for (var x = region.xMin; x < region.xMax; x++)
                    if (!avoid[y * width + x])
                        list.Add(new Vector2Int(x, y));
            return list;
        }

        static void Pick(List<Vector2Int> candidates, SeededRandom rng, int needed, List<Vector2Int> into, List<Vector2Int> placed,
            List<Vector2Int> otherTeam, float teamSeparation)
        {
            rng.Shuffle(candidates);
            foreach (var tile in candidates)
            {
                if (into.Count >= needed)
                    return;
                if (TooClose(tile, placed, MissionConstants.SpawnSpacing) || (otherTeam != null && TooClose(tile, otherTeam, teamSeparation)))
                    continue;
                into.Add(tile);
                placed.Add(tile);
            }
        }

        static bool TooClose(Vector2Int tile, List<Vector2Int> others, float distance)
        {
            foreach (var other in others)
                if (Vector2.Distance(tile, other) < distance)
                    return true;
            return false;
        }

        static List<MissionRoom> PlaceRooms(MissionSettings s, SeededRandom rng)
        {
            var cols = s.gridColumns;
            var cellCount = cols * s.gridRows;
            var chosen = new List<int> { rng.NextInt(cellCount) };
            var isChosen = new bool[cellCount];
            isChosen[chosen[0]] = true;
            var frontier = new List<int>();
            while (chosen.Count < s.roomCount)
            {
                frontier.Clear();
                for (var cell = 0; cell < cellCount; cell++)
                    if (!isChosen[cell] && TouchesChosen(cell, cols, s.gridRows, isChosen))
                        frontier.Add(cell);
                var next = frontier[rng.NextInt(frontier.Count)];
                isChosen[next] = true;
                chosen.Add(next);
            }

            var rooms = new List<MissionRoom>();
            var interior = s.cellSize - 2 * MissionConstants.RoomInset;
            var largest = Math.Min(MissionConstants.RoomMax, interior);
            for (var i = 0; i < chosen.Count; i++)
            {
                var cx = chosen[i] % cols;
                var cy = chosen[i] / cols;
                var w = rng.NextInt(MissionConstants.RoomMin, largest + 1);
                var h = rng.NextInt(MissionConstants.RoomMin, largest + 1);
                var ox = rng.NextInt(0, interior - w + 1);
                var oy = rng.NextInt(0, interior - h + 1);
                var rect = new RectInt(cx * s.cellSize + MissionConstants.RoomInset + ox,
                    cy * s.cellSize + MissionConstants.RoomInset + oy, w, h);
                rooms.Add(new MissionRoom(i, new Vector2Int(cx, cy), rect));
            }
            return rooms;
        }

        static bool TouchesChosen(int cell, int cols, int rows, bool[] isChosen)
        {
            var x = cell % cols;
            var y = cell / cols;
            return (x > 0 && isChosen[cell - 1]) || (x < cols - 1 && isChosen[cell + 1])
                || (y > 0 && isChosen[cell - cols]) || (y < rows - 1 && isChosen[cell + cols]);
        }

        static bool TryConnect(MissionSettings s, SeededRandom rng, List<MissionRoom> rooms,
            out List<MissionConnection> connections, out string reason)
        {
            reason = null;
            connections = new List<MissionConnection>();
            var legal = new List<MissionConnection>();
            for (var i = 0; i < rooms.Count; i++)
            {
                for (var j = i + 1; j < rooms.Count; j++)
                {
                    if (TryStrip(s, rng, rooms[i], rooms[j], out var strip))
                        legal.Add(new MissionConnection(i, j, strip));
                }
            }
            rng.Shuffle(legal);

            var parent = new int[rooms.Count];
            for (var i = 0; i < parent.Length; i++)
                parent[i] = i;
            var extra = new List<MissionConnection>();
            foreach (var candidate in legal)
            {
                var a = Find(parent, candidate.RoomA);
                var b = Find(parent, candidate.RoomB);
                if (a != b)
                {
                    parent[a] = b;
                    connections.Add(candidate);
                }
                else
                {
                    extra.Add(candidate);
                }
            }
            if (connections.Count != rooms.Count - 1)
            {
                reason = "the rooms cannot all be connected (corridors need an overlap of at least the corridor width, or no corridor position that avoids a one-tile step to a room edge)";
                return false;
            }
            for (var i = 0; i < extra.Count && i < s.extraLoops; i++)
                connections.Add(extra[i]);
            return true;
        }

        // A corridor between two rooms in neighbouring cells, across the gutter, inside the overlap of their extents.
        static bool TryStrip(MissionSettings s, SeededRandom rng, MissionRoom a, MissionRoom b, out RectInt strip)
        {
            strip = default;
            var dx = b.Cell.x - a.Cell.x;
            var dy = b.Cell.y - a.Cell.y;
            if (Math.Abs(dx) + Math.Abs(dy) != 1)
                return false;
            var width = s.corridorWidth;
            // Order so that `first` is the room with the smaller cell coordinate along the shared axis.
            var first = dx + dy > 0 ? a.Rect : b.Rect;
            var second = dx + dy > 0 ? b.Rect : a.Rect;
            if (dx != 0)
            {
                var lo = Math.Max(first.yMin, second.yMin);
                var hi = Math.Min(first.yMax, second.yMax);
                if (hi - lo < width)
                    return false;
                if (!TryDrawStart(rng, first.yMin, first.yMax, second.yMin, second.yMax, lo, hi, width, out var y0))
                    return false;
                strip = new RectInt(first.xMax, y0, second.xMin - first.xMax, width);
            }
            else
            {
                var lo = Math.Max(first.xMin, second.xMin);
                var hi = Math.Min(first.xMax, second.xMax);
                if (hi - lo < width)
                    return false;
                if (!TryDrawStart(rng, first.xMin, first.xMax, second.xMin, second.xMax, lo, hi, width, out var x0))
                    return false;
                strip = new RectInt(x0, first.yMax, width, second.yMin - first.yMax);
            }
            return true;
        }

        // Draws a strip's lateral start from [lo, hi - width] (the overlap of the two rooms' extents), uniformly over the
        // valid starts only: no room edge may lie exactly one tile beyond a strip edge, for either room, on either side
        // (decision 031). A step of 1 tile leaves a 1 m wall stub beside the mouth, which gets no corner. A strip edge
        // flush with a room edge (distance 0) or at least 2 tiles in is fine. One draw per pair, as before; a pair with
        // no valid start is not a legal connection.
        static bool TryDrawStart(SeededRandom rng, int minA, int maxA, int minB, int maxB, int lo, int hi, int width, out int start)
        {
            start = 0;
            var count = 0;
            for (var candidate = lo; candidate <= hi - width; candidate++)
                if (IsValidStart(candidate, width, minA, maxA, minB, maxB))
                    count++;
            if (count == 0)
                return false;
            var pick = rng.NextInt(count);
            for (var candidate = lo; candidate <= hi - width; candidate++)
            {
                if (!IsValidStart(candidate, width, minA, maxA, minB, maxB))
                    continue;
                if (pick-- == 0)
                {
                    start = candidate;
                    return true;
                }
            }
            return false;
        }

        static bool IsValidStart(int start, int width, int minA, int maxA, int minB, int maxB) =>
            start - minA != 1 && start - minB != 1 && maxA - (start + width) != 1 && maxB - (start + width) != 1;

        static int Find(int[] parent, int i)
        {
            while (parent[i] != i)
                i = parent[i] = parent[parent[i]];
            return i;
        }

        static void Fill(bool[] mask, int width, RectInt rect)
        {
            for (var y = rect.yMin; y < rect.yMax; y++)
                for (var x = rect.xMin; x < rect.xMax; x++)
                    mask[y * width + x] = true;
        }

        // Wall tiles are the void tiles next to a floor tile, diagonals included so corners close. The ring is one tile
        // thick. Its boxes are the maximal straight runs of wall tiles in both directions, overlapping at junction tiles
        // (RectCover.MaximalRuns), so every straight wall face is the face of one box that ends at the visible corner
        // and a corner point inset from a box end is inset from the visible corner on both sides of a jamb (decision
        // 032). Overlapping boxes have the same height and material.
        static void AddWalls(bool[] floor, int width, int height, List<MissionBox> boxes)
        {
            var wall = new bool[floor.Length];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    if (floor[y * width + x])
                        continue;
                    for (var dy = -1; dy <= 1 && !wall[y * width + x]; dy++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            var nx = x + dx;
                            var ny = y + dy;
                            if (nx >= 0 && ny >= 0 && nx < width && ny < height && floor[ny * width + nx])
                            {
                                wall[y * width + x] = true;
                                break;
                            }
                        }
                    }
                }
            }
            var n = 0;
            foreach (var rect in RectCover.MaximalRuns(wall, width, height))
                boxes.Add(new MissionBox($"Wall_{++n}", MissionBoxKind.Wall, rect, MissionConstants.WallHeight));
        }

        static RectInt UsedRect(bool[] floor, int width, int height)
        {
            int minX = width, minY = height, maxX = -1, maxY = -1;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    if (!floor[y * width + x])
                        continue;
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                }
            }
            return new RectInt(minX - 1, minY - 1, maxX - minX + 3, maxY - minY + 3);
        }

        internal static ulong ComputeHash(MissionLayout layout)
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
            void AddRect(RectInt r)
            {
                Add(r.x); Add(r.y); Add(r.width); Add(r.height);
            }
            Add(layout.Width);
            Add(layout.Height);
            for (var y = 0; y < layout.Height; y++)
                for (var x = 0; x < layout.Width; x++)
                    Add(layout.IsFloor(x, y) ? 1 : 0);
            // Every variable-length list is preceded by its count so that neighbouring lists cannot trade members.
            Add(layout.Rooms.Count);
            foreach (var room in layout.Rooms)
                AddRect(room.Rect);
            Add(layout.Connections.Count);
            foreach (var c in layout.Connections)
            {
                Add(c.RoomA); Add(c.RoomB);
                AddRect(c.Strip);
            }
            Add(layout.Boxes.Count);
            foreach (var box in layout.Boxes)
            {
                Add((int)box.Kind);
                AddRect(box.Footprint);
                Add(Mathf.RoundToInt(box.Height * 10f));
            }
            Add(layout.FriendlyRoom);
            AddRect(layout.FriendlyRegion);
            Add(layout.HostileRooms.Count);
            foreach (var room in layout.HostileRooms)
                Add(room);
            Add(layout.HostileRegions.Count);
            foreach (var region in layout.HostileRegions)
                AddRect(region);
            Add(layout.FriendlySpawns.Count);
            foreach (var t in layout.FriendlySpawns) { Add(t.x); Add(t.y); }
            Add(layout.HostileSpawns.Count);
            foreach (var t in layout.HostileSpawns) { Add(t.x); Add(t.y); }
            return h;
        }
    }
}
