using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class ObjectivePlacerTests
    {
        const ulong Golden12345 = 18368419039347052969UL;
        const ulong Golden1 = 1731673444590866448UL;
        const ulong Golden2 = 705921155356568738UL;

        // The pipeline's loop in miniature: the first attempt whose layout also takes a placement.
        static (MissionLayout layout, ObjectivePlan plan, MissionSettings settings, int attempt) Build(int seed, Action<MissionSettings> tweak = null)
        {
            var settings = new MissionSettings { seed = seed };
            tweak?.Invoke(settings);
            settings = settings.Validated();
            for (var attempt = 1; attempt <= settings.maxAttempts; attempt++)
            {
                if (MissionGenerator.TryAttempt(settings, attempt, out var layout, out _)
                    && ObjectivePlacer.TryPlace(layout, settings, out var plan, out _))
                    return (layout, plan, settings, attempt);
            }
            Assert.Fail($"seed {seed}: no attempt produced a layout with objectives");
            return default;
        }

        static int[] Distances(MissionLayout layout, int from)
        {
            var n = layout.Rooms.Count;
            var d = Enumerable.Repeat(-1, n).ToArray();
            d[from] = 0;
            var queue = new Queue<int>();
            queue.Enqueue(from);
            while (queue.Count > 0)
            {
                var room = queue.Dequeue();
                foreach (var c in layout.Connections)
                {
                    var next = c.RoomA == room ? c.RoomB : c.RoomB == room ? c.RoomA : -1;
                    if (next < 0 || d[next] >= 0)
                        continue;
                    d[next] = d[room] + 1;
                    queue.Enqueue(next);
                }
            }
            return d;
        }

        static bool Blocked(MissionLayout layout, int x, int y) =>
            layout.Boxes.Any(b => b.Kind != MissionBoxKind.Wall && b.Footprint.Contains(new Vector2Int(x, y)));

        static bool FreeBlock(MissionLayout layout, Vector2Int tile, int radius)
        {
            for (var dy = -radius; dy <= radius; dy++)
                for (var dx = -radius; dx <= radius; dx++)
                    if (!layout.IsFloor(tile.x + dx, tile.y + dy) || Blocked(layout, tile.x + dx, tile.y + dy))
                        return false;
            return true;
        }

        static int Chebyshev(Vector2Int a, Vector2Int b) => Mathf.Max(Mathf.Abs(a.x - b.x), Mathf.Abs(a.y - b.y));

        static MissionLayout Tiny()
        {
            const int width = 20;
            const int height = 10;
            var floor = new bool[width * height];
            void Fill(RectInt r)
            {
                for (var y = r.yMin; y < r.yMax; y++)
                    for (var x = r.xMin; x < r.xMax; x++)
                        floor[y * width + x] = true;
            }
            var a = new RectInt(1, 1, 2, 2);
            var b = new RectInt(12, 1, 2, 2);
            var strip = new RectInt(3, 1, 9, 1);
            Fill(a);
            Fill(b);
            Fill(strip);
            return new MissionLayout(1, 1, width, height, floor)
            {
                Rooms = new[] { new MissionRoom(0, new Vector2Int(0, 0), a), new MissionRoom(1, new Vector2Int(1, 0), b) },
                Connections = new[] { new MissionConnection(0, 1, strip) },
                FriendlyRoom = 0,
            };
        }

        [Test]
        public void SameSeedTwice_GivesTheSamePlan()
        {
            Assert.That(Build(12345).plan.Hash, Is.EqualTo(Build(12345).plan.Hash));
        }

        [Test]
        public void DifferentSeeds_GiveDifferentPlans()
        {
            var distinct = Enumerable.Range(1, 30).Select(s => Build(s).plan.Hash).Distinct().Count();
            Assert.That(distinct, Is.GreaterThanOrEqualTo(25));
        }

        [Test]
        public void GoldenSeeds_ArePinned()
        {
            // Re-pin deliberately when the placement algorithm changes (record 033).
            Assert.That(Build(12345).plan.Hash, Is.EqualTo(Golden12345));
            Assert.That(Build(1).plan.Hash, Is.EqualTo(Golden1));
            Assert.That(Build(2).plan.Hash, Is.EqualTo(Golden2));
        }

        [Test]
        public void Placement_UsesItsOwnStream_AndNeverTouchesTheGlobalRandom()
        {
            var (layout, first, settings, _) = Build(7);

            UnityEngine.Random.InitState(99);
            var expected = UnityEngine.Random.value;
            UnityEngine.Random.InitState(99);
            Assert.That(ObjectivePlacer.TryPlace(layout, settings, out var second, out _), Is.True);
            var after = UnityEngine.Random.value;

            Assert.That(after, Is.EqualTo(expected), "placement must not consume UnityEngine.Random");
            Assert.That(second.Hash, Is.EqualTo(first.Hash));
        }

        [Test]
        public void ObjectiveToggles_DoNotChangeWhereThingsAreFound()
        {
            var on = Build(5).plan.Hash;
            var off = Build(5, s => { s.hackTerminal = false; s.eliminateHostiles = true; }).plan.Hash;
            Assert.That(off, Is.EqualTo(on));
        }

        [Test]
        public void ThePlacement_DoesNotChangeTheLayout()
        {
            var (layout, _, settings, _) = Build(3);
            var before = layout.Hash;
            ObjectivePlacer.TryPlace(layout, settings, out _, out _);
            Assert.That(layout.Hash, Is.EqualTo(before));
        }

        [Test]
        public void EveryPlan_ObeysThePlacementRules()
        {
            var inFriendlyRoom = 0;
            var withGuards = 0;
            for (var seed = 1; seed <= 60; seed++)
            {
                var (layout, plan, settings, _) = Build(seed);
                var rooms = layout.Rooms;
                var fromFriendly = Distances(layout, layout.FriendlyRoom);
                var spawns = layout.FriendlySpawns.Concat(layout.HostileSpawns).ToList();

                Assert.That(plan.TerminalRoom, Is.Not.EqualTo(layout.FriendlyRoom), $"seed {seed}");
                Assert.That(fromFriendly[plan.TerminalRoom], Is.GreaterThanOrEqualTo((fromFriendly.Max() + 1) / 2), $"seed {seed}: deeper half");
                Assert.That(rooms[plan.TerminalRoom].Rect.Contains(plan.TerminalTile), Is.True, $"seed {seed}");
                Assert.That(FreeBlock(layout, plan.TerminalTile, ObjectivePlacer.FreeRadius), Is.True, $"seed {seed}: 3x3 free around the terminal");
                foreach (var spawn in spawns)
                    Assert.That(Vector2.Distance(plan.TerminalTile, spawn), Is.GreaterThanOrEqualTo(MissionConstants.SpawnSpacing), $"seed {seed}");

                // The whole 3x3 block lies inside the room, so a terminal never stands in a doorway mouth (decision 030).
                var interior = rooms[plan.TerminalRoom].Rect;
                interior = new RectInt(interior.x + ObjectivePlacer.FreeRadius, interior.y + ObjectivePlacer.FreeRadius,
                    interior.width - 2 * ObjectivePlacer.FreeRadius, interior.height - 2 * ObjectivePlacer.FreeRadius);
                Assert.That(interior.Contains(plan.TerminalTile), Is.True, $"seed {seed}: terminal inside the room inset by the free radius");
                for (var dy = -ObjectivePlacer.FreeRadius; dy <= ObjectivePlacer.FreeRadius; dy++)
                    for (var dx = -ObjectivePlacer.FreeRadius; dx <= ObjectivePlacer.FreeRadius; dx++)
                        foreach (var connection in layout.Connections)
                            Assert.That(connection.Strip.Contains(new Vector2Int(plan.TerminalTile.x + dx, plan.TerminalTile.y + dy)), Is.False,
                                $"seed {seed}: no corridor tile in the terminal's block");

                Assert.That(plan.GuardTiles.Count, Is.LessThanOrEqualTo(settings.guardCount));
                if (plan.GuardTiles.Count > 0)
                    withGuards++;
                foreach (var guard in plan.GuardTiles)
                {
                    Assert.That(rooms[plan.TerminalRoom].Rect.Contains(guard), Is.True, $"seed {seed}: guard in the terminal room");
                    Assert.That(Chebyshev(guard, plan.TerminalTile), Is.InRange(ObjectivePlacer.GuardMinReach, ObjectivePlacer.GuardMaxReach), $"seed {seed}");
                    Assert.That(FreeBlock(layout, guard, 0), Is.True, $"seed {seed}: guard on free floor");
                    foreach (var box in layout.Boxes.Where(b => b.Kind != MissionBoxKind.Wall))
                        for (var dy = -1; dy <= 1; dy++)
                            for (var dx = -1; dx <= 1; dx++)
                                Assert.That(box.Footprint.Contains(new Vector2Int(guard.x + dx, guard.y + dy)), Is.False, $"seed {seed}: guard keeps one tile from every obstacle");
                    foreach (var friendly in layout.FriendlySpawns)
                        Assert.That(Vector2.Distance(guard, friendly), Is.GreaterThanOrEqualTo(settings.minTeamSeparation), $"seed {seed}");
                    foreach (var other in spawns.Concat(plan.GuardTiles.Where(g => g != guard)))
                        Assert.That(Vector2.Distance(guard, other), Is.GreaterThanOrEqualTo(MissionConstants.SpawnSpacing), $"seed {seed}");
                }

                Assert.That(plan.ExtractionRoom, Is.Not.EqualTo(plan.TerminalRoom), $"seed {seed}");
                Assert.That(rooms[plan.ExtractionRoom].Rect.Contains(plan.ExtractionTile), Is.True, $"seed {seed}");
                Assert.That(FreeBlock(layout, plan.ExtractionTile, ObjectivePlacer.FreeRadius), Is.True, $"seed {seed}");
                foreach (var other in spawns.Concat(plan.GuardTiles))
                    Assert.That(Vector2.Distance(plan.ExtractionTile, other), Is.GreaterThanOrEqualTo(ObjectivePlacer.ExtractionClearance), $"seed {seed}");
                if (plan.ExtractionRoom == layout.FriendlyRoom)
                    inFriendlyRoom++;
            }
            Assert.That(inFriendlyRoom, Is.LessThanOrEqualTo(2), "the friendly room is only the last-resort extraction room");
            Assert.That(withGuards, Is.GreaterThanOrEqualTo(30), "guards are really placed with the default guardCount");
        }

        [Test]
        public void GuardCount_IsHonoured_AsAnUpperBound()
        {
            Assert.That(Build(11, s => s.guardCount = 0).plan.GuardTiles, Is.Empty);
            Assert.That(Build(11, s => s.guardCount = 4).plan.GuardTiles.Count, Is.InRange(0, 4));
            var most = Enumerable.Range(1, 20).Max(seed => Build(seed, s => s.guardCount = 4).plan.GuardTiles.Count);
            Assert.That(most, Is.InRange(3, 4), "guardCount 4 really yields guards (not vacuous)");
        }

        [Test]
        public void EverySeedYieldsAPlanWithinTheAttemptBudget()
        {
            var attempts = new List<int>();
            for (var seed = 1; seed <= 200; seed++)
                attempts.Add(Build(seed).attempt);
            TestContext.WriteLine($"attempt 1: {attempts.Count(a => a == 1)}/200, mean {attempts.Average():0.00}, max {attempts.Max()}");
            Assert.That(attempts.Max(), Is.LessThanOrEqualTo(20));
        }

        [Test]
        public void ALayoutWithNoRoomForATerminal_FailsWithAReason_InsteadOfThrowing()
        {
            var layout = Tiny();
            var settings = new MissionSettings { seed = 1 }.Validated();

            var placed = ObjectivePlacer.TryPlace(layout, settings, out var plan, out var reason);

            Assert.That(placed, Is.False);
            Assert.That(plan, Is.Null);
            Assert.That(reason, Does.Contain("terminal"));
        }
    }
}
