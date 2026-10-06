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

            var boxes = new List<MissionBox>();
            AddWalls(floor, width, height, boxes);

            layout = new MissionLayout(settings.seed, attempt, width, height, floor)
            {
                Rooms = rooms,
                Connections = connections,
                FloorRects = RectCover.Decompose(floor, width, height),
                Boxes = boxes,
                UsedTiles = UsedRect(floor, width, height),
            };
            layout.Hash = ComputeHash(layout);
            return true;
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
                reason = "the rooms cannot all be connected (corridors need an overlap of at least the corridor width)";
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
                var y0 = rng.NextInt(lo, hi - width + 1);
                strip = new RectInt(first.xMax, y0, second.xMin - first.xMax, width);
            }
            else
            {
                var lo = Math.Max(first.xMin, second.xMin);
                var hi = Math.Min(first.xMax, second.xMax);
                if (hi - lo < width)
                    return false;
                var x0 = rng.NextInt(lo, hi - width + 1);
                strip = new RectInt(x0, first.yMax, width, second.yMin - first.yMax);
            }
            return true;
        }

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

        // Wall tiles are the void tiles next to a floor tile, diagonals included so corners close.
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
            foreach (var rect in RectCover.Decompose(wall, width, height))
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
            Add(layout.Width);
            Add(layout.Height);
            for (var y = 0; y < layout.Height; y++)
                for (var x = 0; x < layout.Width; x++)
                    Add(layout.IsFloor(x, y) ? 1 : 0);
            foreach (var room in layout.Rooms)
            {
                Add(room.Rect.x); Add(room.Rect.y); Add(room.Rect.width); Add(room.Rect.height);
            }
            foreach (var c in layout.Connections)
            {
                Add(c.RoomA); Add(c.RoomB); Add(c.Strip.x); Add(c.Strip.y); Add(c.Strip.width); Add(c.Strip.height);
            }
            foreach (var box in layout.Boxes)
            {
                Add((int)box.Kind); Add(box.Footprint.x); Add(box.Footprint.y); Add(box.Footprint.width); Add(box.Footprint.height);
                Add(Mathf.RoundToInt(box.Height * 10f));
            }
            foreach (var t in layout.FriendlySpawns) { Add(t.x); Add(t.y); }
            Add(-1);
            foreach (var t in layout.HostileSpawns) { Add(t.x); Add(t.y); }
            return h;
        }
    }
}
