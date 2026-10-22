using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>The client's seedless layout rebuilt from the projection equals the server's geometry (decision 052).</summary>
    public class NetMapGeometryTests
    {
        static IEnumerable<TestCaseData> Layouts()
        {
            for (var seed = 1; seed <= 12; seed++)
            {
                var s = ServerFixture.Settings();
                s.seed = seed;
                yield return new TestCaseData(s).SetName("Fixture settings, seed " + seed);
            }
            for (var seed = 1; seed <= 3; seed++)
            {
                var big = ServerFixture.Settings();
                big.seed = seed;
                big.gridColumns = 6;
                big.gridRows = 6;
                big.cellSize = 20;
                big.roomCount = 20;
                big.extraLoops = 4;
                big.bafflesPerRoom = 3;
                big.lowCoverDensity = 10f;
                yield return new TestCaseData(big).SetName("Largest settings, seed " + seed);
            }
        }

        static MissionLayout Generate(MissionSettings settings)
        {
            var request = settings.Validated();
            for (var attempt = 1; attempt <= request.maxAttempts; attempt++)
                if (MissionGenerator.TryAttempt(request, attempt, out var layout, out _))
                    return layout;
            Assert.Fail("no attempt generated");
            return null;
        }

        static MissionLayout RoundTrip(MissionLayout server)
        {
            var map = NetMapProjection.Map(server, new Vector2Int[0], ServerFixture.Settings().intelligence, false, false, out var refusal);
            Assert.That(refusal, Is.Null, "the projection fits the limits");
            return NetMapGeometry.ToLayout(map);
        }

        [TestCaseSource(nameof(Layouts))]
        public void TheClientLayout_HasTheServersFloorBoxesUsedTilesAndBounds(MissionSettings settings)
        {
            var server = Generate(settings);
            var client = RoundTrip(server);
            Assert.That(client.Width, Is.EqualTo(server.Width));
            Assert.That(client.Height, Is.EqualTo(server.Height));
            for (var y = -1; y <= server.Height; y++)
                for (var x = -1; x <= server.Width; x++)
                    Assert.That(client.IsFloor(x, y), Is.EqualTo(server.IsFloor(x, y)), "tile " + x + "," + y);
            Assert.That(client.FloorTileCount, Is.EqualTo(server.FloorTileCount));
            Assert.That(client.FloorRects, Is.EqualTo(server.FloorRects));
            Assert.That(client.Boxes.Select(b => (b.Kind, b.Footprint, b.Height)), Is.EqualTo(server.Boxes.Select(b => (b.Kind, b.Footprint, b.Height))));
            Assert.That(client.UsedTiles, Is.EqualTo(server.UsedTiles));
            Assert.That(client.WorldBounds, Is.EqualTo(server.WorldBounds));
        }

        [TestCaseSource(nameof(Layouts))]
        public void TheClientLayout_KnowsNoSeedAttemptRoomSpawnOrRegion(MissionSettings settings)
        {
            var client = RoundTrip(Generate(settings));
            Assert.That(client.Seed, Is.Zero);
            Assert.That(client.Attempt, Is.Zero);
            Assert.That(client.Hash, Is.Zero);
            Assert.That(client.Rooms, Is.Empty);
            Assert.That(client.Connections, Is.Empty);
            Assert.That(client.FriendlySpawns, Is.Empty);
            Assert.That(client.HostileSpawns, Is.Empty);
            Assert.That(client.HostileRooms, Is.Empty);
            Assert.That(client.HostileRegions, Is.Empty);
            Assert.That(client.FriendlyRoom, Is.EqualTo(-1));
        }

        [TestCaseSource(nameof(Layouts))]
        public void TheThemedVisualPlan_IsTheServersExceptTheSeedPickedLights(MissionSettings settings)
        {
            var server = Generate(settings);
            var client = RoundTrip(server);
            string Key(VisualPlacement p) => p.Element + "@" + p.Tile + ":" + p.Position.ToString("F3") + "/" + p.YawDegrees;
            var expected = EnvironmentVisualPlanner.Plan(server).Where(p => p.Element != EnvironmentElement.LightFixture).Select(Key);
            var actual = EnvironmentVisualPlanner.Plan(client).Where(p => p.Element != EnvironmentElement.LightFixture).Select(Key);
            Assert.That(actual, Is.EqualTo(expected));
        }

        [Test]
        public void CubeBounds_AreMissionBuildersFormulas()
        {
            var layout = RoundTrip(Generate(ServerFixture.Settings()));
            var rect = layout.FloorRects[0];
            var floor = NetMapGeometry.FloorBounds(layout, rect);
            Assert.That(floor.center, Is.EqualTo(layout.RectCenter(rect) + Vector3.down * (MissionConstants.FloorThickness * 0.5f)));
            Assert.That(floor.size, Is.EqualTo(new Vector3(rect.width, MissionConstants.FloorThickness, rect.height)));
            var box = layout.Boxes[0];
            var b = NetMapGeometry.BoxBounds(layout, box);
            Assert.That(b.center, Is.EqualTo(layout.RectCenter(box.Footprint) + Vector3.up * (box.Height * 0.5f)));
            Assert.That(b.size, Is.EqualTo(new Vector3(box.Footprint.width, box.Height, box.Footprint.height)));
            var tile = new Vector2Int(rect.xMin, rect.yMin);
            var crate = NetMapGeometry.ContainerBounds(layout, tile);
            Assert.That(crate.center, Is.EqualTo(layout.TileCenter(tile) + Vector3.up * (MissionContent.LootHeight * 0.5f)));
            Assert.That(crate.size, Is.EqualTo(new Vector3(MissionContent.LootSize, MissionContent.LootHeight, MissionContent.LootSize)));
        }
    }
}
