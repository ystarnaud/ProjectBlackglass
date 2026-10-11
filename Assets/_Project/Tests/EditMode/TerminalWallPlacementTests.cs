using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>
    /// The data terminal and the camera-control terminal follow the loot rule (decision 043, playtest amendment 4): against a wall
    /// or in a corner of their room, beside no doorway, with a clear lane in front; a room with no such tile falls back to a free
    /// block FallbackInset tiles in. Plus the direction the terminal's visual faces (into the room, away from the wall).
    /// </summary>
    public class TerminalWallPlacementTests
    {
        const int Seeds = 60;

        static bool Build(int seed, out MissionLayout layout, out ObjectivePlan objectives, out SecurityPlan security)
        {
            var settings = new MissionSettings { seed = seed, intelligence = new IntelligenceSettings { cameraCount = 3 } }.Validated();
            for (var attempt = 1; attempt <= settings.maxAttempts; attempt++)
            {
                if (MissionGenerator.TryAttempt(settings, attempt, out layout, out _)
                    && ObjectivePlacer.TryPlace(layout, settings, out objectives, out _)
                    && SecurityPlacer.TryPlace(layout, objectives, settings, out security, out _))
                    return true;
            }
            layout = null;
            objectives = null;
            security = null;
            return false;
        }

        // Asserts the placement rule for one terminal tile and returns true when it stands on the room's outer ring.
        static bool AssertWallOrInset(MissionLayout layout, int roomIndex, Vector2Int tile, string label, int seed)
        {
            var room = layout.Rooms[roomIndex].Rect;
            var blocked = ObjectivePlacer.ObstacleMask(layout);
            Assert.That(room.Contains(tile), Is.True, $"seed {seed}: {label} inside its room");
            var ring = tile.x == room.xMin || tile.x == room.xMax - 1 || tile.y == room.yMin || tile.y == room.yMax - 1;
            if (!ring)
            {
                Assert.That(Mathf.Min(tile.x - room.xMin, room.xMax - 1 - tile.x, tile.y - room.yMin, room.yMax - 1 - tile.y),
                    Is.GreaterThanOrEqualTo(LootPlacer.FallbackInset), $"seed {seed}: a fallback {label} keeps a lane of two tiles or more to the wall");
            }
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    var x = tile.x + dx;
                    var y = tile.y + dy;
                    var inside = x >= room.xMin && x < room.xMax && y >= room.yMin && y < room.yMax;
                    if (inside)
                    {
                        Assert.That(layout.IsFloor(x, y), Is.True, $"seed {seed}: floor around the {label} at {tile}");
                        Assert.That(blocked[y * layout.Width + x], Is.False, $"seed {seed}: free around the {label} at {tile}");
                    }
                    else if (ring)
                        Assert.That(layout.IsFloor(x, y), Is.False, $"seed {seed}: the {label} at {tile} is beside a doorway or corridor at {x},{y}");
                }
            }
            if (ring)
            {
                // The lane two tiles in front of each wall it touches is clear, so no one-tile strip is left beside an obstacle.
                var inward = new Vector2Int(tile.x == room.xMin ? 1 : tile.x == room.xMax - 1 ? -1 : 0,
                    tile.y == room.yMin ? 1 : tile.y == room.yMax - 1 ? -1 : 0);
                foreach (var direction in new[] { new Vector2Int(inward.x, 0), new Vector2Int(0, inward.y) })
                {
                    if (direction == Vector2Int.zero)
                        continue;
                    for (var side = -1; side <= 1; side++)
                    {
                        var lane = tile + direction * 2 + new Vector2Int(direction.y != 0 ? side : 0, direction.x != 0 ? side : 0);
                        if (lane.x >= room.xMin && lane.x < room.xMax && lane.y >= room.yMin && lane.y < room.yMax)
                            Assert.That(blocked[lane.y * layout.Width + lane.x], Is.False, $"seed {seed}: the {label} at {tile} has an obstacle in its lane at {lane}");
                    }
                }
            }
            return ring;
        }

        [Test]
        public void TheDataTerminal_StandsAgainstAWall_BesideNoDoorway_Across60Seeds()
        {
            var built = 0;
            var onWall = 0;
            for (var seed = 1; seed <= Seeds; seed++)
            {
                if (!Build(seed, out var layout, out var objectives, out _))
                    continue;
                built++;
                if (AssertWallOrInset(layout, objectives.TerminalRoom, objectives.TerminalTile, "data terminal", seed))
                    onWall++;
            }
            Assert.That(built, Is.GreaterThan(50));
            Assert.That(onWall / (float)built, Is.GreaterThanOrEqualTo(0.95f), $"{onWall} of {built} data terminals stand against a wall (the rest used the inset fallback)");
        }

        [Test]
        public void TheCameraTerminal_StandsAgainstAWall_BesideNoDoorway_Across60Seeds()
        {
            var built = 0;
            var onWall = 0;
            foreach (var seed in Enumerable.Range(1, Seeds))
            {
                if (!Build(seed, out var layout, out _, out var security))
                    continue;
                built++;
                if (AssertWallOrInset(layout, security.TerminalRoom, security.TerminalTile, "camera terminal", seed))
                    onWall++;
            }
            Assert.That(built, Is.GreaterThan(50));
            Assert.That(onWall / (float)built, Is.GreaterThanOrEqualTo(0.95f), $"{onWall} of {built} camera terminals stand against a wall (the rest used the inset fallback)");
        }

        [Test]
        public void TheTerminals_StillKeepTheirSpacing_FromSpawnsGuardsAndEachOther()
        {
            for (var seed = 1; seed <= Seeds; seed++)
            {
                if (!Build(seed, out var layout, out var objectives, out var security))
                    continue;
                // The data terminal is placed before the guards (they keep their own distance from it), the camera terminal after.
                var spawns = layout.FriendlySpawns.Concat(layout.HostileSpawns).ToList();
                foreach (var other in spawns)
                    Assert.That(Vector2.Distance(objectives.TerminalTile, other), Is.GreaterThanOrEqualTo(MissionConstants.SpawnSpacing), $"seed {seed}: data terminal");
                foreach (var other in spawns.Concat(objectives.GuardTiles))
                    Assert.That(Vector2.Distance(security.TerminalTile, other), Is.GreaterThanOrEqualTo(MissionConstants.SpawnSpacing), $"seed {seed}: camera terminal");
                Assert.That(security.TerminalTile, Is.Not.EqualTo(objectives.TerminalTile), $"seed {seed}");
                Assert.That(Vector2.Distance(security.TerminalTile, objectives.ExtractionTile), Is.GreaterThanOrEqualTo(ObjectivePlacer.ExtractionClearance), $"seed {seed}");
            }
        }

        // A 7x7 grid of floor with a one-tile wall border and no obstacles.
        static MissionLayout Room()
        {
            var floor = new bool[7 * 7];
            for (var y = 1; y < 6; y++)
                for (var x = 1; x < 6; x++)
                    floor[y * 7 + x] = true;
            return new MissionLayout(1, 1, 7, 7, floor);
        }

        [TestCase(1, 3, 1f, 0f)]
        [TestCase(5, 3, -1f, 0f)]
        [TestCase(3, 1, 0f, 1f)]
        [TestCase(3, 5, 0f, -1f)]
        public void ATerminalOnAWall_FacesIntoTheRoom(int x, int y, float expectedX, float expectedZ)
        {
            var facing = MissionContent.FacingInto(Room(), new Vector2Int(x, y));
            Assert.That(facing.x, Is.EqualTo(expectedX).Within(1e-4f));
            Assert.That(facing.y, Is.EqualTo(0f));
            Assert.That(facing.z, Is.EqualTo(expectedZ).Within(1e-4f));
        }

        [Test]
        public void ATerminalInACorner_FacesAwayFromOneOfItsWalls_AlongAnAxis()
        {
            var facing = MissionContent.FacingInto(Room(), new Vector2Int(1, 1));
            Assert.That(facing == Vector3.right || facing == Vector3.forward, Is.True, $"facing {facing}: into the room along x or z");
        }

        [Test]
        public void ATerminalWithNoWallBesideIt_HasNoPreferredFacing()
        {
            Assert.That(MissionContent.FacingInto(Room(), new Vector2Int(3, 3)), Is.EqualTo(Vector3.zero));
        }
    }
}
