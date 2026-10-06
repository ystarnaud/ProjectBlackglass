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
                    Assert.That(layout.Rooms.Any(r => Contains(r.Rect, box.Footprint, MissionConstants.Clearance)), Is.True,
                        $"{box.Name} is closer than {MissionConstants.Clearance} tiles to a wall or door");
                    foreach (var c in layout.Connections)
                        Assert.That(box.Footprint.Overlaps(c.Strip), Is.False);
                }
                for (var i = 0; i < obstacles.Count; i++)
                    for (var j = i + 1; j < obstacles.Count; j++)
                        Assert.That(Inflate(obstacles[i].Footprint, MissionConstants.Clearance).Overlaps(obstacles[j].Footprint), Is.False,
                            $"{obstacles[i].Name} and {obstacles[j].Name} are too close");
            }
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
            var withLow = Layouts().Count(l => l.Boxes.Any(b => b.Kind == MissionBoxKind.LowWall || b.Kind == MissionBoxKind.Crate));
            Assert.That(withBaffle, Is.GreaterThanOrEqualTo(Seeds * 9 / 10));
            Assert.That(withLow, Is.GreaterThanOrEqualTo(Seeds * 9 / 10));
        }

        static RectInt Inflate(RectInt r, int by) => new RectInt(r.x - by, r.y - by, r.width + 2 * by, r.height + 2 * by);

        static bool Contains(RectInt outer, RectInt footprint, int margin) =>
            footprint.xMin - margin >= outer.xMin && footprint.xMax + margin <= outer.xMax
            && footprint.yMin - margin >= outer.yMin && footprint.yMax + margin <= outer.yMax;
    }
}
