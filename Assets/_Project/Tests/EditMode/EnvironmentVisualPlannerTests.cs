using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class EnvironmentVisualPlannerTests
    {
        static MissionBox Box(MissionBoxKind kind, int x, int y, int w, int h, float height = 3f) =>
            new MissionBox($"{kind}_{x}_{y}", kind, new RectInt(x, y, w, h), height);

        // A width x height grid; floorMask decides which tiles are floor (default: all).
        static MissionLayout Layout(int width, int height, System.Func<int, int, bool> floorMask, params MissionBox[] boxes)
        {
            var floor = new bool[width * height];
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    floor[y * width + x] = floorMask == null || floorMask(x, y);
            return new MissionLayout(1, 0, width, height, floor) { Boxes = boxes };
        }

        static VisualPlacement At(MissionLayout layout, EnvironmentElement element, int x, int y) =>
            EnvironmentVisualPlanner.Plan(layout).Single(p => p.Element == element && p.Tile == new Vector2Int(x, y));

        [Test]
        public void StraightRun_EastWest_HasEndsFacingTheNeighbour_AndAStraightMiddle()
        {
            var layout = Layout(7, 7, null, Box(MissionBoxKind.Wall, 2, 3, 3, 1));

            Assert.That(At(layout, EnvironmentElement.WallEnd, 2, 3).YawDegrees, Is.EqualTo(90f), "neighbour to the east");
            Assert.That(At(layout, EnvironmentElement.WallEnd, 4, 3).YawDegrees, Is.EqualTo(270f), "neighbour to the west");
            Assert.That(At(layout, EnvironmentElement.WallStraight, 3, 3).YawDegrees, Is.EqualTo(0f));
            Assert.That(At(layout, EnvironmentElement.WallStraight, 3, 3).Position, Is.EqualTo(layout.TileCenter(new Vector2Int(3, 3))));
        }

        [Test]
        public void StraightRun_NorthSouth_IsRotatedNinetyDegrees()
        {
            var layout = Layout(7, 7, null, Box(MissionBoxKind.Wall, 3, 2, 1, 3));

            Assert.That(At(layout, EnvironmentElement.WallStraight, 3, 3).YawDegrees, Is.EqualTo(90f));
            Assert.That(At(layout, EnvironmentElement.WallEnd, 3, 2).YawDegrees, Is.EqualTo(0f), "neighbour to the north");
            Assert.That(At(layout, EnvironmentElement.WallEnd, 3, 4).YawDegrees, Is.EqualTo(180f), "neighbour to the south");
        }

        [TestCase(1, 1, 0f)]     // L opening to north + east
        [TestCase(1, 3, 90f)]    // east + south
        [TestCase(3, 3, 180f)]   // south + west
        [TestCase(3, 1, 270f)]   // west + north
        public void Corners_TakeTheYawOfTheirTwoNeighbours(int cornerX, int cornerY, float yaw)
        {
            var layout = Layout(7, 7, null,
                Box(MissionBoxKind.Wall, 1, cornerY, 3, 1),
                Box(MissionBoxKind.Wall, cornerX, 1, 1, 3));

            Assert.That(At(layout, EnvironmentElement.WallCorner, cornerX, cornerY).YawDegrees, Is.EqualTo(yaw));
        }

        [Test]
        public void TJunction_PointsItsMissingSide_AndACrossIsAJunctionToo()
        {
            var tee = Layout(7, 7, null, Box(MissionBoxKind.Wall, 1, 3, 5, 1), Box(MissionBoxKind.Wall, 3, 1, 1, 3));
            Assert.That(At(tee, EnvironmentElement.WallJunction, 3, 3).YawDegrees, Is.EqualTo(180f), "missing north");

            var cross = Layout(7, 7, null, Box(MissionBoxKind.Wall, 1, 3, 5, 1), Box(MissionBoxKind.Wall, 3, 1, 1, 5));
            Assert.That(At(cross, EnvironmentElement.WallJunction, 3, 3).YawDegrees, Is.EqualTo(0f));
        }

        [Test]
        public void AnIsolatedWallTile_AndAPillarBox_AreBothPillars()
        {
            var layout = Layout(7, 7, null, Box(MissionBoxKind.Wall, 1, 1, 1, 1), Box(MissionBoxKind.Pillar, 4, 4, 1, 1));

            Assert.That(At(layout, EnvironmentElement.Pillar, 1, 1), Is.Not.Null);
            Assert.That(At(layout, EnvironmentElement.Pillar, 4, 4).Position, Is.EqualTo(layout.TileCenter(new Vector2Int(4, 4))));
        }

        [Test]
        public void DoorFrames_AppearOnlyOnRingWallEnds_NotBaffleEnds()
        {
            var ring = Layout(9, 9, null, Box(MissionBoxKind.Wall, 1, 1, 3, 1));
            var baffle = Layout(9, 9, null, Box(MissionBoxKind.Baffle, 1, 1, 3, 1));

            Assert.That(EnvironmentVisualPlanner.Plan(ring).Count(p => p.Element == EnvironmentElement.DoorFrame), Is.EqualTo(2));
            Assert.That(EnvironmentVisualPlanner.Plan(baffle).Count(p => p.Element == EnvironmentElement.DoorFrame), Is.EqualTo(0));
            Assert.That(EnvironmentVisualPlanner.Plan(baffle).Count(p => p.Element == EnvironmentElement.WallEnd), Is.EqualTo(2));
        }

        [Test]
        public void ALowWallOfThree_IsOneLongModuleAndOneShort_AlongItsAxis()
        {
            var east = Layout(9, 9, null, Box(MissionBoxKind.LowWall, 2, 2, 3, 1, 1f));
            var placements = EnvironmentVisualPlanner.Plan(east);
            var longModule = placements.Single(p => p.Element == EnvironmentElement.LowCoverLong);
            var shortModule = placements.Single(p => p.Element == EnvironmentElement.LowCover);
            Assert.That(longModule.Position, Is.EqualTo(east.ToWorld(3f, 2.5f)), "centred between tiles 2 and 3");
            Assert.That(longModule.YawDegrees, Is.EqualTo(0f));
            Assert.That(shortModule.Tile, Is.EqualTo(new Vector2Int(4, 2)));

            var north = Layout(9, 9, null, Box(MissionBoxKind.LowWall, 2, 2, 1, 3, 1f));
            var vertical = EnvironmentVisualPlanner.Plan(north).Single(p => p.Element == EnvironmentElement.LowCoverLong);
            Assert.That(vertical.YawDegrees, Is.EqualTo(90f));
            Assert.That(vertical.Position, Is.EqualTo(north.ToWorld(2.5f, 3f)));
        }

        [Test]
        public void ACrate_IsOneModulePerTile()
        {
            var layout = Layout(9, 9, null, Box(MissionBoxKind.Crate, 4, 4, 1, 1, 1f));

            Assert.That(At(layout, EnvironmentElement.Crate, 4, 4).Position, Is.EqualTo(layout.TileCenter(new Vector2Int(4, 4))));
        }

        [Test]
        public void Floor_HasOneModulePerFloorTile_AtGroundLevel()
        {
            var layout = Layout(6, 6, (x, y) => x < 4);
            var floors = EnvironmentVisualPlanner.Plan(layout).Where(p => p.Element == EnvironmentElement.Floor).ToList();

            Assert.That(floors, Has.Count.EqualTo(layout.FloorTileCount));
            Assert.That(floors.All(p => p.Position.y == 0f), Is.True);
        }

        [Test]
        public void LightFixtures_SitOnStraightWallsAndFaceTheFloorSide()
        {
            // Floor only at y >= 4, a straight wall along y = 3 below it: the floor side is north.
            var layout = Layout(24, 8, (x, y) => y >= 4, Box(MissionBoxKind.Wall, 1, 3, 22, 1));
            var lights = EnvironmentVisualPlanner.Plan(layout).Where(p => p.Element == EnvironmentElement.LightFixture).ToList();

            Assert.That(lights, Is.Not.Empty);
            Assert.That(lights.All(p => p.Tile.y == 3 && p.YawDegrees == 0f), Is.True);
        }

        [Test]
        public void WallsWithFloorOnBothSides_GetNoLightFixture()
        {
            var layout = Layout(24, 8, null, Box(MissionBoxKind.Wall, 1, 3, 22, 1));

            Assert.That(EnvironmentVisualPlanner.Plan(layout).Count(p => p.Element == EnvironmentElement.LightFixture), Is.Zero);
        }

        [Test]
        public void Planning_IsDeterministic_AndDoesNotTouchTheLayout()
        {
            var result = MissionGenerator.Generate(new MissionSettings { seed = 12345 });
            var layout = result.Layout;
            var hash = layout.Hash;

            var first = EnvironmentVisualPlanner.Plan(layout);
            var second = EnvironmentVisualPlanner.Plan(layout);

            Assert.That(second, Is.EqualTo(first));
            Assert.That(layout.Hash, Is.EqualTo(hash));
            Assert.That(MissionGenerator.Generate(new MissionSettings { seed = 12345 }).Layout.Hash, Is.EqualTo(hash));
        }

        [Test]
        public void GeneratedMissions_GetExactlyOneWallModulePerWallTile_AndNoneElsewhere()
        {
            for (var seed = 1; seed <= 20; seed++)
            {
                var layout = MissionGenerator.Generate(new MissionSettings { seed = seed }).Layout;
                var tiles = new System.Collections.Generic.HashSet<Vector2Int>();
                foreach (var box in layout.Boxes.Where(b => b.Kind == MissionBoxKind.Wall || b.Kind == MissionBoxKind.Baffle))
                    for (var y = box.Footprint.yMin; y < box.Footprint.yMax; y++)
                        for (var x = box.Footprint.xMin; x < box.Footprint.xMax; x++)
                            tiles.Add(new Vector2Int(x, y));
                var pillarBoxTiles = layout.Boxes.Count(b => b.Kind == MissionBoxKind.Pillar);

                var wallModules = EnvironmentVisualPlanner.Plan(layout).Where(p =>
                    p.Element == EnvironmentElement.WallStraight || p.Element == EnvironmentElement.WallCorner ||
                    p.Element == EnvironmentElement.WallEnd || p.Element == EnvironmentElement.WallJunction ||
                    p.Element == EnvironmentElement.Pillar).ToList();

                Assert.That(wallModules, Has.Count.EqualTo(tiles.Count + pillarBoxTiles), $"seed {seed}");
                Assert.That(wallModules.Select(p => p.Tile).Distinct().Count(), Is.EqualTo(wallModules.Count), $"seed {seed}: no tile twice");
            }
        }
    }
}
