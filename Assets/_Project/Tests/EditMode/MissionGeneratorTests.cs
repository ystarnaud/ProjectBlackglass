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
        public void Walls_RingTheFloor_NeverOverlapIt_OverlapEachOtherOnlyAtJunctions_AndLeaveDoorGaps()
        {
            for (var seed = 1; seed <= 20; seed++)
            {
                var layout = Make(seed);
                var wallBoxes = layout.Boxes.Where(b => b.Kind == MissionBoxKind.Wall).ToList();
                var wallTiles = wallBoxes.SelectMany(b => Tiles(b.Footprint)).ToList();
                var walls = wallTiles.ToHashSet();
                // The union of the wall boxes is exactly the wall mask: every void tile next to a floor tile, diagonals included.
                var expected = new HashSet<Vector2Int>();
                for (var x = 0; x < layout.Width; x++)
                    for (var y = 0; y < layout.Height; y++)
                        if (!layout.IsFloor(x, y) && Neighbours8(x, y).Any(n => layout.IsFloor(n.x, n.y)))
                            expected.Add(new Vector2Int(x, y));
                Assert.That(walls, Is.EquivalentTo(expected), $"seed {seed}: the wall boxes cover exactly the wall tiles");
                // Decision 032: wall boxes are straight runs that overlap only at a junction tile, covered by exactly one
                // horizontal run and one vertical run.
                foreach (var group in wallTiles.GroupBy(t => t).Where(g => g.Count() > 1))
                {
                    var covering = wallBoxes.Where(b => b.Footprint.Contains(group.Key)).ToList();
                    Assert.That(covering, Has.Count.EqualTo(2), $"seed {seed}: junction tile {group.Key} is covered at most twice");
                    Assert.That(covering.Count(b => b.Footprint.height == 1 && b.Footprint.width >= 2), Is.EqualTo(1),
                        $"seed {seed}: junction tile {group.Key} is covered by one horizontal run");
                    Assert.That(covering.Count(b => b.Footprint.width == 1 && b.Footprint.height >= 2), Is.EqualTo(1),
                        $"seed {seed}: junction tile {group.Key} is covered by one vertical run");
                }
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

        [Test]
        public void MaximalRuns_CoverEveryStraightRunWithOneRectangle_OverlappingAtJunctions_InScanOrder()
        {
            // Rows listed from y = 4 down to y = 0: a T (row 1 with a column rising from x = 1), an L (row 3 at x 3..4
            // with a column rising from x = 4) and one isolated tile.
            var rows = new[]
            {
                "....X.",
                ".X.XX.",
                ".X...X",
                "XXXX..",
                "......",
            };
            const int width = 6, height = 5;
            var mask = new bool[width * height];
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    mask[y * width + x] = rows[height - 1 - y][x] == 'X';

            var runs = RectCover.MaximalRuns(mask, width, height);

            Assert.That(runs, Is.EqualTo(new[]
            {
                new RectInt(0, 1, 4, 1), new RectInt(3, 3, 2, 1),   // horizontal runs, row by row
                new RectInt(1, 1, 1, 3), new RectInt(4, 3, 1, 2),   // vertical runs, by start tile
                new RectInt(5, 2, 1, 1),                            // a tile in no run
            }));
        }

        [Test]
        public void Attempt_IsAPureFunctionOfSeedAndAttemptNumber()
        {
            var settings = new MissionSettings { seed = 99 }.Validated();
            Assert.That(MissionGenerator.TryAttempt(settings, 3, out var a, out var ra), Is.EqualTo(MissionGenerator.TryAttempt(settings, 3, out var b, out var rb)));
            Assert.That(ra, Is.EqualTo(rb));
            if (a != null)
                Assert.That(a.Hash, Is.EqualTo(b.Hash));
            // The final layout does not depend on how often the generator was called before.
            MissionGenerator.Generate(new MissionSettings { seed = 5 });
            Assert.That(Make(99).Hash, Is.EqualTo(Make(99).Hash));
        }

        [Test]
        public void Generate_DoesNotTouchUnityEngineRandom()
        {
            UnityEngine.Random.InitState(2024);
            var expected = new[] { UnityEngine.Random.value, UnityEngine.Random.value };
            UnityEngine.Random.InitState(2024);
            MissionGenerator.Generate(new MissionSettings { seed = 321 });
            var actual = new[] { UnityEngine.Random.value, UnityEngine.Random.value };
            Assert.That(actual, Is.EqualTo(expected));
        }

        [Test]
        public void Generate_FailsCleanlyWhenTheTeamsCannotBeSeparated()
        {
            // A 2x2 grid of 12 m cells is 24 m across, so 60 m between the teams is impossible by construction.
            var result = MissionGenerator.Generate(new MissionSettings { seed = 1, gridColumns = 2, gridRows = 2, cellSize = 12, roomCount = 4, minTeamSeparation = 60f, maxAttempts = 5 });
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Failures, Has.Count.EqualTo(5), "exactly maxAttempts attempts, then it stops");
            Assert.That(result.Describe(), Does.Contain("seed=1").And.Contain("attempts=5").And.Contain("attempt 5:"));
        }

        [Test]
        public void Generate_ClampsDegenerateSettings_InsteadOfThrowingOrHanging()
        {
            var result = MissionGenerator.Generate(new MissionSettings
            {
                gridColumns = 1, gridRows = 1, cellSize = 1, roomCount = 99, corridorWidth = 0, maxAttempts = 1000,
                hostileCount = 0, friendlyCount = 0,
            });
            Assert.That(result.Settings.maxAttempts, Is.EqualTo(100));
            Assert.That(result.Settings.roomCount, Is.EqualTo(4));
            Assert.That(result.Succeeded || result.Failures.Count == 100, Is.True);
        }

        // Golden layouts: the hashes pin determinism across code changes. Change the generator on purpose and these
        // move: update them in the same commit and say so in the message.
        static readonly Dictionary<int, ulong> Golden = new Dictionary<int, ulong>
        {
            { 12345, 3187845850788023860UL },
            { 1, 17747232627558074983UL },
            { 2, 10482210376931648275UL },
        };

        [TestCase(12345)]
        [TestCase(1)]
        [TestCase(2)]
        public void GoldenSeeds_KeepTheirLayoutHash(int seed) =>
            Assert.That(Make(seed).Hash, Is.EqualTo(Golden[seed]), $"seed {seed}");

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
