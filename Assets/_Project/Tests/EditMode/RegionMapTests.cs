using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class RegionMapTests
    {
        // Two 6x6 rooms and a 6x3 corridor between them on a 20x10 grid. Tile (x, y) is world (x - 10, y - 5).
        static readonly RectInt RoomA = new RectInt(1, 1, 6, 6);
        static readonly RectInt RoomB = new RectInt(13, 1, 6, 6);
        static readonly RectInt Strip = new RectInt(7, 2, 6, 3);

        static MissionLayout TwoRooms()
        {
            const int width = 20;
            const int height = 10;
            var floor = new bool[width * height];
            foreach (var rect in new[] { RoomA, RoomB, Strip })
                for (var y = rect.yMin; y < rect.yMax; y++)
                    for (var x = rect.xMin; x < rect.xMax; x++)
                        floor[y * width + x] = true;
            return new MissionLayout(1, 1, width, height, floor)
            {
                Rooms = new[] { new MissionRoom(0, new Vector2Int(0, 0), RoomA), new MissionRoom(1, new Vector2Int(1, 0), RoomB) },
                Connections = new[] { new MissionConnection(0, 1, Strip) },
                FloorRects = new[] { RoomA, RoomB, Strip },
                FriendlyRoom = 0,
            };
        }

        [Test]
        public void Regions_AreRoomsThenCorridors_WithRoomIdsEqualToLayoutIndices()
        {
            var map = new RegionMap(TwoRooms());
            Assert.That(map.Count, Is.EqualTo(3));
            Assert.That(map.RoomCount, Is.EqualTo(2));
            Assert.That(map[0].Kind, Is.EqualTo(RegionKind.Room));
            Assert.That(map[1].Rect, Is.EqualTo(RoomB));
            Assert.That(map[2].Kind, Is.EqualTo(RegionKind.Corridor));
            Assert.That(map[2].Rect, Is.EqualTo(Strip));
        }

        [Test]
        public void RegionOfTile_FindsTheRegion_AndMinusOneForVoidAndOutOfBounds()
        {
            var map = new RegionMap(TwoRooms());
            Assert.That(map.RegionOfTile(3, 3), Is.EqualTo(0));
            Assert.That(map.RegionOfTile(15, 3), Is.EqualTo(1));
            Assert.That(map.RegionOfTile(9, 3), Is.EqualTo(2));
            Assert.That(map.RegionOfTile(9, 8), Is.EqualTo(-1), "void tile");
            Assert.That(map.RegionOfTile(-1, 0), Is.EqualTo(-1));
            Assert.That(map.RegionOfTile(0, 10), Is.EqualTo(-1));
        }

        [Test]
        public void RegionAt_ConvertsWorldToTiles()
        {
            var layout = TwoRooms();
            var map = new RegionMap(layout);
            Assert.That(map.RegionAt(layout.TileCenter(new Vector2Int(3, 3))), Is.EqualTo(0));
            Assert.That(map.RegionAt(layout.TileCenter(new Vector2Int(16, 4))), Is.EqualTo(1));
            Assert.That(map.RegionAt(new Vector3(0.2f, 1f, -1.5f)), Is.EqualTo(2), "world (0.2, -1.5) is tile (10, 3)");
            Assert.That(map.RegionAt(new Vector3(-50f, 0f, 0f)), Is.EqualTo(-1));
        }

        [Test]
        public void Neighbours_JoinARoomToItsCorridors_AndACorridorToItsTwoRooms()
        {
            var map = new RegionMap(TwoRooms());
            Assert.That(map.Neighbours(0), Is.EquivalentTo(new[] { 2 }));
            Assert.That(map.Neighbours(1), Is.EquivalentTo(new[] { 2 }));
            Assert.That(map.Neighbours(2), Is.EquivalentTo(new[] { 0, 1 }));
        }

        [Test]
        public void SamplePoints_AreFloorTilesAtSampleHeight_AndEveryRegionHasSome()
        {
            var layout = TwoRooms();
            var map = new RegionMap(layout);
            for (var region = 0; region < map.Count; region++)
            {
                var samples = map.SamplePoints(region);
                Assert.That(samples.Count, Is.InRange(1, 16), $"region {region}");
                foreach (var point in samples)
                {
                    Assert.That(point.y, Is.EqualTo(RegionMap.SampleHeight));
                    Assert.That(map.RegionAt(point), Is.EqualTo(region));
                }
            }
        }

        [Test]
        public void SamplePoints_OfA6x6Room_AreFourOnAThreeTileGrid()
        {
            var map = new RegionMap(TwoRooms());
            Assert.That(map.SamplePoints(0).Count, Is.EqualTo(4));
        }

        [Test]
        public void RegionsInCircle_ReturnsRegionsWhoseRectTouchesTheCircle()
        {
            var layout = TwoRooms();
            var map = new RegionMap(layout);
            var found = new List<int>();
            map.RegionsInCircle(layout.TileCenter(new Vector2Int(3, 3)), 2f, found);
            Assert.That(found, Is.EquivalentTo(new[] { 0 }));
            map.RegionsInCircle(layout.TileCenter(new Vector2Int(3, 3)), 8f, found);
            Assert.That(found, Is.EquivalentTo(new[] { 0, 2 }), "the corridor starts 4 m east of the room's far wall; room B is 10 m away");
            map.RegionsInCircle(layout.TileCenter(new Vector2Int(3, 3)), 30f, found);
            Assert.That(found, Is.EquivalentTo(new[] { 0, 1, 2 }));
        }

        [Test]
        public void RegionsAdjacentTo_FindsTheRegionsAWallFootprintStandsBeside()
        {
            var map = new RegionMap(TwoRooms());
            var found = new List<int>();
            map.RegionsAdjacentTo(new RectInt(7, 1, 6, 1), found);   // the wall row above... below the corridor, between the rooms
            Assert.That(found, Is.EquivalentTo(new[] { 0, 1, 2 }));
            map.RegionsAdjacentTo(new RectInt(0, 0, 20, 1), found);   // the grid's bottom row: only room tiles in row 1 touch it
            Assert.That(found, Is.EquivalentTo(new[] { 0, 1 }));
            map.RegionsAdjacentTo(new RectInt(9, 8, 2, 1), found);
            Assert.That(found, Is.Empty);
        }

        [Test]
        public void WorldRect_IsTheRegionRectInWorldXz()
        {
            var layout = TwoRooms();
            var map = new RegionMap(layout);
            var rect = map.WorldRect(0);
            Assert.That(rect.xMin, Is.EqualTo(-9f));
            Assert.That(rect.xMax, Is.EqualTo(-3f));
            Assert.That(rect.yMin, Is.EqualTo(-4f));
            Assert.That(rect.yMax, Is.EqualTo(2f));
        }

        [TestCase(1)]
        [TestCase(7)]
        [TestCase(12345)]
        [TestCase(31)]
        public void OnGeneratedLayouts_EveryFloorTileBelongsToExactlyOneRegion_AndEveryRegionHasSamples(int seed)
        {
            var settings = new MissionSettings { seed = seed }.Validated();
            MissionLayout layout = null;
            for (var attempt = 1; attempt <= settings.maxAttempts && layout == null; attempt++)
                if (MissionGenerator.TryAttempt(settings, attempt, out var candidate, out _))
                    layout = candidate;
            Assert.That(layout, Is.Not.Null, $"seed {seed} produced no layout");

            var map = new RegionMap(layout);
            for (var y = 0; y < layout.Height; y++)
                for (var x = 0; x < layout.Width; x++)
                    if (layout.IsFloor(x, y))
                        Assert.That(map.RegionOfTile(x, y), Is.GreaterThanOrEqualTo(0), $"floor tile ({x},{y}) has no region");
            for (var region = 0; region < map.Count; region++)
                Assert.That(map.SamplePoints(region), Is.Not.Empty, $"region {region}");
            for (var i = 0; i < layout.Rooms.Count; i++)
                Assert.That(layout.Rooms[i].Index, Is.EqualTo(i), "region ids rely on Rooms[i].Index == i");
        }
    }
}
