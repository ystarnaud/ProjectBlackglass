using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class SecurityPlacerTests
    {
        // The pipeline's loop in miniature: the first attempt whose layout takes objectives and (when cameras are on) security.
        static (MissionLayout layout, ObjectivePlan objectives, SecurityPlan security, MissionSettings settings) Build(int seed, int cameras = 3)
        {
            var settings = new MissionSettings { seed = seed, intelligence = new IntelligenceSettings { cameraCount = cameras } }.Validated();
            for (var attempt = 1; attempt <= settings.maxAttempts; attempt++)
            {
                if (MissionGenerator.TryAttempt(settings, attempt, out var layout, out _)
                    && ObjectivePlacer.TryPlace(layout, settings, out var objectives, out _)
                    && SecurityPlacer.TryPlace(layout, objectives, settings, out var security, out _))
                    return (layout, objectives, security, settings);
            }
            Assert.Fail($"seed {seed}: no attempt produced layout + objectives + security");
            return default;
        }

        [Test]
        public void WithNoCameras_ThePlanIsEmpty()
        {
            var built = Build(12345, cameras: 0);
            Assert.That(built.security, Is.SameAs(SecurityPlan.Empty));
            Assert.That(built.security.HasTerminal, Is.False);
            Assert.That(built.security.Cameras, Is.Empty);
            Assert.That(built.security.DeviceCount, Is.EqualTo(0));
        }

        [Test]
        public void TheSameSeed_GivesTheSamePlan_AndDifferentSeedsMostlyDiffer()
        {
            Assert.That(Build(12345).security.Hash, Is.EqualTo(Build(12345).security.Hash));
            var hashes = new HashSet<ulong>();
            for (var seed = 1; seed <= 12; seed++)
                hashes.Add(Build(seed).security.Hash);
            Assert.That(hashes.Count, Is.GreaterThan(6), "twelve seeds should give mostly different plans");
        }

        [Test]
        public void ThePlacementNeverMovesTheLayoutOrTheObjectivePlan()
        {
            foreach (var seed in new[] { 12345, 1, 2 })
            {
                var without = Build(seed, cameras: 0);
                var with = Build(seed, cameras: 3);
                Assert.That(with.layout.Hash, Is.EqualTo(without.layout.Hash), $"seed {seed} layout");
                Assert.That(with.objectives.Hash, Is.EqualTo(without.objectives.Hash), $"seed {seed} objectives");
            }
        }

        [Test]
        public void TheSecurityStream_IsIndependentOfTheLayoutAndObjectiveStreams()
        {
            ulong First(SeededRandom r) => r.NextULong();
            var security = First(SeededRandom.ForSecurity(12345, 1));
            Assert.That(security, Is.Not.EqualTo(First(SeededRandom.ForObjectives(12345, 1))));
            Assert.That(security, Is.Not.EqualTo(First(SeededRandom.ForAttempt(12345, 1))));
            Assert.That(First(SeededRandom.ForSecurity(12345, 1)), Is.EqualTo(security));
        }

        [TestCase(12345)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(31)]
        public void TheTerminal_IsNearTheStart_OnAFreeTile_AwayFromSpawnsAndObjectives(int seed)
        {
            var (layout, objectives, security, _) = Build(seed);
            Assert.That(security.HasTerminal, Is.True);

            var allowed = new List<int> { layout.FriendlyRoom };
            foreach (var connection in layout.Connections)
            {
                if (connection.RoomA == layout.FriendlyRoom) allowed.Add(connection.RoomB);
                if (connection.RoomB == layout.FriendlyRoom) allowed.Add(connection.RoomA);
            }
            Assert.That(allowed, Has.Member(security.TerminalRoom), "the friendly room or one joined to it");

            var tile = security.TerminalTile;
            Assert.That(layout.Rooms[security.TerminalRoom].Rect.Contains(tile), Is.True);
            for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    Assert.That(layout.IsFloor(tile.x + dx, tile.y + dy), Is.True, "a free 3x3 block");
                    Assert.That(layout.Boxes.Any(b => b.Kind != MissionBoxKind.Wall && b.Footprint.Contains(new Vector2Int(tile.x + dx, tile.y + dy))), Is.False);
                }
            foreach (var spawn in layout.FriendlySpawns.Concat(layout.HostileSpawns).Concat(objectives.GuardTiles))
                Assert.That(Vector2.Distance(tile, spawn), Is.GreaterThanOrEqualTo(MissionConstants.SpawnSpacing));
            Assert.That(tile, Is.Not.EqualTo(objectives.TerminalTile));
            Assert.That(Vector2.Distance(tile, objectives.ExtractionTile), Is.GreaterThanOrEqualTo(ObjectivePlacer.ExtractionClearance));
        }

        [TestCase(12345)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(31)]
        public void TheCameras_HangOnAWall_FaceIntoADifferentNonStartRoomEach(int seed)
        {
            var (layout, _, security, settings) = Build(seed);
            Assert.That(security.Cameras.Count, Is.InRange(1, settings.intelligence.cameraCount));
            var rooms = new HashSet<int>();
            foreach (var mount in security.Cameras)
            {
                Assert.That(mount.Room, Is.Not.EqualTo(layout.FriendlyRoom));
                Assert.That(rooms.Add(mount.Room), Is.True, "one camera per room");
                Assert.That(layout.Rooms[mount.Room].Rect.Contains(mount.Tile), Is.True);
                Assert.That(layout.IsFloor(mount.Tile.x, mount.Tile.y), Is.True);
                var wall = mount.Tile + mount.WallSide;
                Assert.That(layout.IsFloor(wall.x, wall.y), Is.False, "the wall side is not floor");
                Assert.That(Mathf.Abs(mount.WallSide.x) + Mathf.Abs(mount.WallSide.y), Is.EqualTo(1), "a cardinal step");
                var front = mount.Tile - mount.WallSide;
                Assert.That(layout.IsFloor(front.x, front.y), Is.True, "it faces into the room");
            }
        }

        [Test]
        public void DeviceIds_AreTheTerminalThenTheCamerasInOrder()
        {
            var (layout, _, security, _) = Build(12345);
            Assert.That(SecurityPlan.TerminalDeviceId, Is.EqualTo(0));
            Assert.That(security.DeviceCount, Is.EqualTo(1 + security.Cameras.Count));
            for (var i = 0; i < security.Cameras.Count; i++)
                Assert.That(security.CameraDeviceId(i), Is.EqualTo(1 + i));
            var position = security.CameraPosition(layout, 0);
            Assert.That(position.y, Is.EqualTo(SecurityPlan.CameraHeight));
            var tileCentre = layout.TileCenter(security.Cameras[0].Tile);
            Assert.That(Vector3.Distance(new Vector3(position.x, 0f, position.z), tileCentre), Is.EqualTo(0.4f).Within(1e-4f));
            Assert.That(security.CameraForward(0), Is.EqualTo(new Vector3(-security.Cameras[0].WallSide.x, 0f, -security.Cameras[0].WallSide.y)));
        }

        [Test]
        public void ThePinnedHash_ForSeed12345_DoesNotDrift()
        {
            // Pinned the first time this test ran (see the plan, Task 4 Step 4). A change here means the placement changed.
            Assert.That(Build(12345).security.Hash, Is.EqualTo(PinnedHash12345));
        }

        const ulong PinnedHash12345 = 1091115860883586314UL;

        [Test]
        public void NotEnoughRooms_StillPlacesTheCamerasThereAre()
        {
            var built = Build(12345, cameras: 6);
            Assert.That(built.security.Cameras.Count, Is.LessThanOrEqualTo(built.layout.Rooms.Count - 1));
            Assert.That(built.security.Cameras.Count, Is.GreaterThan(0));
        }

        [Test]
        public void ADifferentCameraCount_ChangesNothingAboutTheTerminal()
        {
            var few = Build(12345, cameras: 1).security;
            var many = Build(12345, cameras: 3).security;
            Assert.That(few.TerminalTile, Is.EqualTo(many.TerminalTile), "the terminal is drawn before the cameras");
            Assert.That(few.Cameras.Count, Is.LessThanOrEqualTo(many.Cameras.Count));
        }

        [Test]
        public void ARoomListWithNoWallToHangOn_FailsTheAttemptWithAReason()
        {
            // A layout whose only non-start room is a single floor tile with floor all around cannot take a camera.
            var settings = new MissionSettings { intelligence = new IntelligenceSettings { cameraCount = 1 } }.Validated();
            var floor = new bool[9 * 9];
            for (var i = 0; i < floor.Length; i++) floor[i] = true;
            var layout = new MissionLayout(1, 1, 9, 9, floor)
            {
                Rooms = new[] { new MissionRoom(0, new Vector2Int(0, 0), new RectInt(0, 0, 4, 9)), new MissionRoom(1, new Vector2Int(1, 0), new RectInt(4, 3, 3, 3)) },
                FriendlyRoom = 0,
                FriendlySpawns = new[] { new Vector2Int(1, 1) },
                HostileSpawns = new[] { new Vector2Int(8, 8) },
            };
            var ok = SecurityPlacer.TryPlace(layout, null, settings, out var plan, out var reason);
            Assert.That(ok, Is.False);
            Assert.That(reason, Is.Not.Empty);
            Assert.That(plan, Is.SameAs(SecurityPlan.Empty));
        }
    }
}
