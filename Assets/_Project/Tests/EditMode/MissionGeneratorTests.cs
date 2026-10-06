using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class MissionGeneratorTests
    {
        static MissionLayout Make(int seed, Action<MissionSettings> tweak = null)
        {
            var settings = new MissionSettings { seed = seed };
            tweak?.Invoke(settings);
            var result = MissionGenerator.Generate(settings);
            Assert.That(result.Succeeded, Is.True, result.Describe());
            return result.Layout;
        }

        [Test]
        public void SameSeedTwice_GivesTheSameLayout() =>
            Assert.That(Make(12345).Hash, Is.EqualTo(Make(12345).Hash));

        [Test]
        public void DifferentSeeds_GiveDifferentLayouts()
        {
            var hashes = Enumerable.Range(1, 30).Select(s => Make(s).Hash).Distinct().Count();
            Assert.That(hashes, Is.GreaterThanOrEqualTo(28));
        }

        [Test]
        public void Rooms_AreTheRequestedCount_InsideTheirCells_AndNeverOverlap()
        {
            for (var seed = 1; seed <= 20; seed++)
            {
                var layout = Make(seed);
                Assert.That(layout.Rooms, Has.Count.EqualTo(6));
                foreach (var room in layout.Rooms)
                {
                    Assert.That(room.Rect.width, Is.InRange(MissionConstants.RoomMin, MissionConstants.RoomMax));
                    Assert.That(room.Rect.height, Is.InRange(MissionConstants.RoomMin, MissionConstants.RoomMax));
                    var cell = new RectInt(room.Cell.x * 14, room.Cell.y * 14, 14, 14);
                    Assert.That(room.Rect.xMin, Is.GreaterThanOrEqualTo(cell.xMin + MissionConstants.RoomInset));
                    Assert.That(room.Rect.xMax, Is.LessThanOrEqualTo(cell.xMax - MissionConstants.RoomInset));
                    Assert.That(room.Rect.yMin, Is.GreaterThanOrEqualTo(cell.yMin + MissionConstants.RoomInset));
                    Assert.That(room.Rect.yMax, Is.LessThanOrEqualTo(cell.yMax - MissionConstants.RoomInset));
                }
                for (var i = 0; i < layout.Rooms.Count; i++)
                    for (var j = i + 1; j < layout.Rooms.Count; j++)
                        Assert.That(layout.Rooms[i].Rect.Overlaps(layout.Rooms[j].Rect), Is.False);
            }
        }

        [Test]
        public void Connections_FormASpanningTreePlusLoops_WithCorridorsAsWideAsConfigured()
        {
            for (var seed = 1; seed <= 20; seed++)
            {
                var layout = Make(seed);
                Assert.That(layout.Connections.Count, Is.InRange(5, 6), "5 tree edges + at most 1 loop");
                foreach (var c in layout.Connections)
                {
                    var narrow = Mathf.Min(c.Strip.width, c.Strip.height);
                    Assert.That(narrow, Is.EqualTo(3));
                    Assert.That(Mathf.Max(c.Strip.width, c.Strip.height), Is.GreaterThanOrEqualTo(4), "crosses the gutter");
                }
            }
        }

        [Test]
        public void Floor_IsOneConnectedRegion_TheUnionOfRoomsAndCorridors()
        {
            for (var seed = 1; seed <= 20; seed++)
            {
                var layout = Make(seed);
                var floor = new HashSet<Vector2Int>();
                for (var x = 0; x < layout.Width; x++)
                    for (var y = 0; y < layout.Height; y++)
                        if (layout.IsFloor(x, y))
                            floor.Add(new Vector2Int(x, y));
                Assert.That(floor, Has.Count.EqualTo(layout.FloorTileCount));
                Assert.That(Flood(floor, floor.First()), Is.EqualTo(floor.Count), $"seed {seed}");
                var rects = layout.FloorRects.SelectMany(Tiles).ToList();
                Assert.That(rects, Has.Count.EqualTo(floor.Count), "floor rectangles cover the floor exactly once");
                Assert.That(rects.ToHashSet(), Is.EquivalentTo(floor));
            }
        }

        [Test]
        public void Walls_RingTheFloor_NeverOverlapIt_AndLeaveDoorGaps()
        {
            for (var seed = 1; seed <= 20; seed++)
            {
                var layout = Make(seed);
                var wallTiles = layout.Boxes.Where(b => b.Kind == MissionBoxKind.Wall).SelectMany(b => Tiles(b.Footprint)).ToList();
                Assert.That(wallTiles.Distinct().Count(), Is.EqualTo(wallTiles.Count), "wall boxes do not overlap");
                var walls = wallTiles.ToHashSet();
                foreach (var tile in walls)
                    Assert.That(layout.IsFloor(tile.x, tile.y), Is.False, $"wall tile {tile} is on the floor");
                for (var x = 1; x < layout.Width - 1; x++)
                    for (var y = 1; y < layout.Height - 1; y++)
                        if (layout.IsFloor(x, y))
                            foreach (var n in Neighbours8(x, y))
                                Assert.That(layout.IsFloor(n.x, n.y) || walls.Contains(n), Is.True, $"seed {seed}: gap at {n}");
                foreach (var c in layout.Connections)
                    foreach (var t in Tiles(c.Strip))
                        Assert.That(walls.Contains(t), Is.False, "a corridor tile is never a wall");
                Assert.That(layout.Boxes.Where(b => b.Kind == MissionBoxKind.Wall).All(b => b.Height == MissionConstants.WallHeight), Is.True);
            }
        }

        static IEnumerable<Vector2Int> Tiles(RectInt r)
        {
            for (var x = r.xMin; x < r.xMax; x++)
                for (var y = r.yMin; y < r.yMax; y++)
                    yield return new Vector2Int(x, y);
        }

        static IEnumerable<Vector2Int> Neighbours8(int x, int y)
        {
            for (var dx = -1; dx <= 1; dx++)
                for (var dy = -1; dy <= 1; dy++)
                    if (dx != 0 || dy != 0)
                        yield return new Vector2Int(x + dx, y + dy);
        }

        static int Flood(HashSet<Vector2Int> open, Vector2Int start)
        {
            var seen = new HashSet<Vector2Int> { start };
            var queue = new Queue<Vector2Int>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var t = queue.Dequeue();
                foreach (var d in new[] { Vector2Int.up, Vector2Int.down, Vector2Int.left, Vector2Int.right })
                    if (open.Contains(t + d) && seen.Add(t + d))
                        queue.Enqueue(t + d);
            }
            return seen.Count;
        }
    }
}
