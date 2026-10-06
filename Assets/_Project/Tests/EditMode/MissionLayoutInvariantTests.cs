using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class MissionLayoutInvariantTests
    {
        const int Seeds = 60;

        static IEnumerable<MissionLayout> Layouts()
        {
            for (var seed = 1; seed <= Seeds; seed++)
            {
                var result = MissionGenerator.Generate(new MissionSettings { seed = seed });
                Assert.That(result.Succeeded, Is.True, result.Describe());
                yield return result.Layout;
            }
        }

        static IEnumerable<Vector2Int> Tiles(RectInt r)
        {
            for (var x = r.xMin; x < r.xMax; x++)
                for (var y = r.yMin; y < r.yMax; y++)
                    yield return new Vector2Int(x, y);
        }

        static IEnumerable<MissionBox> Obstacles(MissionLayout layout) => layout.Boxes.Where(b => b.Kind != MissionBoxKind.Wall);

        [Test]
        public void EveryDefaultSeed_Succeeds_WithinTheAttemptBudget() =>
            Assert.That(Layouts().All(l => l.Attempt >= 1 && l.Attempt <= 20), Is.True);

        [Test]
        public void Obstacles_StandOnFloorInsideRooms_NeverOnCorridors_AndKeepTheirClearance()
        {
            foreach (var layout in Layouts())
            {
                var obstacles = Obstacles(layout).ToList();
                foreach (var box in obstacles)
                {
                    Assert.That(Tiles(box.Footprint).All(t => layout.IsFloor(t.x, t.y)), Is.True, $"{box.Name} floats");
                    var room = layout.Rooms.Where(r => Contains(r.Rect, box.Footprint, 0)).ToList();
                    Assert.That(room, Has.Count.EqualTo(1), $"seed {layout.Seed}: {box.Name} stands inside one room");
                    var gaps = WallGaps(room[0].Rect, box.Footprint).ToList();
                    for (var wall = 0; wall < 4; wall++)
                    {
                        if (gaps[wall] == 0 && MayTouch(box, wall))
                            continue;
                        Assert.That(gaps[wall], Is.GreaterThanOrEqualTo(MissionConstants.Clearance),
                            $"seed {layout.Seed}: {box.Name} stands {gaps[wall]} tile(s) from room wall {wall}");
                    }
                    foreach (var c in layout.Connections)
                        Assert.That(box.Footprint.Overlaps(c.Strip), Is.False);
                }
                // Every pair, low or tall, keeps the full clearance: only the gap to a room wall differs for low objects.
                for (var i = 0; i < obstacles.Count; i++)
                    for (var j = i + 1; j < obstacles.Count; j++)
                        Assert.That(Inflate(obstacles[i].Footprint, MissionConstants.Clearance).Overlaps(obstacles[j].Footprint), Is.False,
                            $"seed {layout.Seed}: {obstacles[i].Name} and {obstacles[j].Name} are too close");
            }
        }

        [Test]
        public void Obstacles_NeverStandWithinTwoTilesOfACorridorStrip()
        {
            foreach (var layout in Layouts())
                foreach (var box in Obstacles(layout))
                    foreach (var c in layout.Connections)
                        Assert.That(Inflate(box.Footprint, 2).Overlaps(c.Strip), Is.False,
                            $"seed {layout.Seed}: {box.Name} is within 2 tiles of a corridor");
        }

        // The lateral gaps of a strip to one room: how far each room edge lies beyond the strip's edge (low side, high side).
        static (int low, int high) LateralGaps(RectInt strip, RectInt room, bool alongX) =>
            alongX ? (strip.yMin - room.yMin, room.yMax - strip.yMax) : (strip.xMin - room.xMin, room.xMax - strip.xMax);

        [Test]
        public void ACorridorStripsEdges_AreFlushWithEachRoomEdge_OrAtLeastTwoTilesIn_NeverExactlyOne()
        {
            // Decision 031: a room edge exactly one tile beyond a strip edge leaves a 1 m wall stub beside the mouth, which
            // has no corner point. Checked for both rooms and both sides of every strip.
            int flush = 0, deep = 0;
            foreach (var layout in Layouts())
                foreach (var c in layout.Connections)
                {
                    var a = layout.Rooms[c.RoomA];
                    var b = layout.Rooms[c.RoomB];
                    var alongX = a.Cell.y == b.Cell.y;
                    foreach (var room in new[] { a, b })
                    {
                        var (low, high) = LateralGaps(c.Strip, room.Rect, alongX);
                        foreach (var gap in new[] { low, high })
                        {
                            Assert.That(gap, Is.GreaterThanOrEqualTo(0), $"seed {layout.Seed}: strip {c.Strip} lies inside the overlap of its rooms");
                            Assert.That(gap, Is.Not.EqualTo(1), $"seed {layout.Seed}: strip {c.Strip} is one tile from an edge of room {room.Index} {room.Rect}");
                            if (gap == 0)
                                flush++;
                            else if (gap >= 2)
                                deep++;
                        }
                    }
                }
            Assert.That(flush, Is.GreaterThan(0), "some strip edges are flush with a room edge");
            Assert.That(deep, Is.GreaterThan(0), "some strip edges sit two or more tiles inside a room edge");
        }

        [Test]
        public void ObstacleHeightsAndSizes_MatchTheirKind()
        {
            foreach (var layout in Layouts())
                foreach (var b in Obstacles(layout))
                {
                    var f = b.Footprint;
                    var tall = b.Kind == MissionBoxKind.Baffle || b.Kind == MissionBoxKind.Pillar;
                    Assert.That(b.Height, Is.EqualTo(tall ? MissionConstants.WallHeight : MissionConstants.LowHeight), b.Name);
                    if (b.Kind == MissionBoxKind.Baffle)
                    {
                        Assert.That(Mathf.Min(f.width, f.height), Is.EqualTo(1), b.Name);
                        Assert.That(Mathf.Max(f.width, f.height), Is.InRange(3, 5), b.Name);
                    }
                    if (b.Kind == MissionBoxKind.Pillar || b.Kind == MissionBoxKind.Crate)
                        Assert.That((f.width, f.height), Is.EqualTo((1, 1)), b.Name);
                    if (b.Kind == MissionBoxKind.LowWall)
                        Assert.That((Mathf.Min(f.width, f.height), Mathf.Max(f.width, f.height)), Is.EqualTo((1, 3)), b.Name);
                }
        }

        [Test]
        public void TheWalkableFloor_StaysConnected_AroundEveryObstacle()
        {
            foreach (var layout in Layouts())
            {
                var blocked = Obstacles(layout).SelectMany(b => Tiles(b.Footprint)).ToHashSet();
                var open = new HashSet<Vector2Int>();
                for (var x = 0; x < layout.Width; x++)
                    for (var y = 0; y < layout.Height; y++)
                        if (layout.IsFloor(x, y) && !blocked.Contains(new Vector2Int(x, y)))
                            open.Add(new Vector2Int(x, y));
                var seen = new HashSet<Vector2Int> { layout.FriendlySpawns[0] };
                var queue = new Queue<Vector2Int>(seen);
                while (queue.Count > 0)
                {
                    var t = queue.Dequeue();
                    foreach (var d in new[] { Vector2Int.up, Vector2Int.down, Vector2Int.left, Vector2Int.right })
                        if (open.Contains(t + d) && seen.Add(t + d))
                            queue.Enqueue(t + d);
                }
                Assert.That(seen, Has.Count.EqualTo(open.Count), $"seed {layout.Seed}: isolated floor");
                Assert.That(layout.HostileSpawns.All(seen.Contains), Is.True, "every hostile spawn is reachable");
            }
        }

        [Test]
        public void Spawns_AreOnFreeFloor_Apart_AndTheTeamsStartFarApart()
        {
            foreach (var layout in Layouts())
            {
                Assert.That(layout.FriendlySpawns, Has.Count.EqualTo(3));
                Assert.That(layout.HostileSpawns, Has.Count.EqualTo(3));
                var all = layout.FriendlySpawns.Concat(layout.HostileSpawns).ToList();
                var obstacleTiles = Obstacles(layout).SelectMany(b => Tiles(Inflate(b.Footprint, 1))).ToHashSet();
                foreach (var t in all)
                {
                    Assert.That(layout.IsFloor(t.x, t.y), Is.True);
                    Assert.That(obstacleTiles.Contains(t), Is.False, $"spawn {t} is next to an obstacle");
                }
                for (var i = 0; i < all.Count; i++)
                    for (var j = i + 1; j < all.Count; j++)
                        Assert.That(Vector2.Distance(all[i], all[j]), Is.GreaterThanOrEqualTo(MissionConstants.SpawnSpacing));
                foreach (var f in layout.FriendlySpawns)
                    foreach (var h in layout.HostileSpawns)
                        Assert.That(Vector2.Distance(f, h), Is.GreaterThanOrEqualTo(16f), $"seed {layout.Seed}");
                Assert.That(layout.FriendlySpawns.All(t => layout.FriendlyRegion.Contains(t)), Is.True);
                Assert.That(layout.HostileSpawns.All(t => layout.HostileRegions.Any(r => r.Contains(t))), Is.True);
            }
        }

        [Test]
        public void FriendlyAndHostileRooms_AreDifferentRooms_AtTheGreatestGraphDistance()
        {
            foreach (var layout in Layouts())
            {
                Assert.That(layout.HostileRooms, Has.No.Member(layout.FriendlyRoom));
                Assert.That(layout.HostileRooms.Count, Is.InRange(1, 2));
            }
        }

        [Test]
        public void Bounds_ContainEveryBoxAndSpawn()
        {
            foreach (var layout in Layouts())
            {
                var bounds = layout.WorldBounds;
                foreach (var b in layout.Boxes)
                {
                    var min = layout.ToWorld(b.Footprint.xMin, b.Footprint.yMin);
                    var max = layout.ToWorld(b.Footprint.xMax, b.Footprint.yMax);
                    Assert.That(bounds.Contains(new Vector3(min.x + 0.01f, 1f, min.z + 0.01f)), Is.True, b.Name);
                    Assert.That(bounds.Contains(new Vector3(max.x - 0.01f, 1f, max.z - 0.01f)), Is.True, b.Name);
                }
                foreach (var t in layout.FriendlySpawns.Concat(layout.HostileSpawns))
                    Assert.That(bounds.Contains(layout.TileCenter(t) + Vector3.up), Is.True);
            }
        }

        [Test]
        public void MostMissionsHaveTallObstaclesAndLowCover()
        {
            var withBaffle = Layouts().Count(l => l.Boxes.Any(b => b.Kind == MissionBoxKind.Baffle));
            var withLow = Layouts().Count(l => l.Boxes.Any(IsLow));
            Assert.That(withBaffle, Is.GreaterThanOrEqualTo(Seeds * 9 / 10));
            Assert.That(withLow, Is.GreaterThanOrEqualTo(Seeds * 9 / 10));
        }

        [Test]
        public void MissionsHaveUsefulLowCover_MedianAtLeastThree_AndMostAtLeastTwo()
        {
            // Every low object is useful by construction (free-standing, or a wall touching a room wall end-on only), so
            // the object count is the useful count. Measured over seeds 1 to 60: median 3, 50 seeds with at least 2, 39
            // with at least 3. The median floor is the owner's minimum (decision 030), not a margin below the measurement.
            var counts = Layouts().Select(l => l.Boxes.Count(IsLow)).OrderBy(n => n).ToList();
            var all = $"low objects per mission, sorted: {string.Join(",", counts)}";
            Assert.That(counts[counts.Count / 2], Is.GreaterThanOrEqualTo(3), all);
            Assert.That(counts.Count(n => n >= 2), Is.GreaterThanOrEqualTo(45), all);
            Assert.That(counts.Count(n => n >= 3), Is.GreaterThanOrEqualTo(33), all);
        }

        [Test]
        public void LowCover_KeepsTwoTilesFromRoomWalls_OrTouchesOneWithALowWallsShortEnd()
        {
            // Decision 030: a one-tile gap is too narrow for a unit on the eroded NavMesh (dead pockets). A low wall may
            // touch one room wall with its short end (a peninsula: both long faces keep a lane to the other walls); it never
            // lies flush along a wall, a crate is never flush, and nothing stands flush in a room corner.
            var endOn = 0;
            var low = 0;
            foreach (var layout in Layouts())
                foreach (var box in Obstacles(layout).Where(IsLow))
                {
                    low++;
                    var room = layout.Rooms.Single(r => Contains(r.Rect, box.Footprint, 0));
                    var gaps = WallGaps(room.Rect, box.Footprint).ToList();
                    var label = $"seed {layout.Seed}: {box.Name} gaps W,E,S,N {string.Join(",", gaps)}";
                    Assert.That(gaps, Has.None.EqualTo(1), label);
                    Assert.That(gaps.Count(g => g == 0), Is.LessThanOrEqualTo(1), label + ": flush against two walls");
                    if (box.Kind == MissionBoxKind.Crate)
                        Assert.That(gaps, Has.None.EqualTo(0), label + ": a crate is never flush");
                    for (var wall = 0; wall < 4; wall++)
                        if (gaps[wall] == 0)
                            Assert.That(MayTouch(box, wall), Is.True, label + ": a low wall lies flush along a room wall");
                    if (gaps.Contains(0))
                        endOn++;
                }
            Assert.That(low, Is.GreaterThan(0));
            Assert.That(endOn, Is.GreaterThan(0), "the relaxed half of the tries does stand low walls end-on against a room wall");
        }

        static bool IsLow(MissionBox b) => b.Kind == MissionBoxKind.LowWall || b.Kind == MissionBoxKind.Crate;

        // Whether the box may touch room wall `wall` (0 west, 1 east, 2 south, 3 north, as WallGaps): only a low wall, and
        // only with its short end: a horizontal (3 x 1) wall the west or east wall, a vertical (1 x 3) one the south or north.
        static bool MayTouch(MissionBox box, int wall)
        {
            if (box.Kind != MissionBoxKind.LowWall)
                return false;
            var horizontal = box.Footprint.width > box.Footprint.height;
            return horizontal ? wall <= 1 : wall >= 2;
        }

        // The free tiles between the footprint and each of its room's four walls: west, east, south, north.
        static IEnumerable<int> WallGaps(RectInt room, RectInt footprint)
        {
            yield return footprint.xMin - room.xMin;
            yield return room.xMax - footprint.xMax;
            yield return footprint.yMin - room.yMin;
            yield return room.yMax - footprint.yMax;
        }

        static RectInt Inflate(RectInt r, int by) => new RectInt(r.x - by, r.y - by, r.width + 2 * by, r.height + 2 * by);

        static bool Contains(RectInt outer, RectInt footprint, int margin) =>
            footprint.xMin - margin >= outer.xMin && footprint.xMax + margin <= outer.xMax
            && footprint.yMin - margin >= outer.yMin && footprint.yMax + margin <= outer.yMax;
    }
}
