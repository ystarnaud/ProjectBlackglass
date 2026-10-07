using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;
using Unity.AI.Navigation;

namespace Blackglass.Tests
{
    public class MissionBuilderPlayModeTests
    {
        TestWorld world;
        GeneratedMission mission;

        [SetUp]
        public void SetUp() => world = new TestWorld();

        [TearDown]
        public void TearDown()
        {
            DestroyMission();
            world.Dispose();
        }

        void DestroyMission()
        {
            if (mission != null && mission.Root != null)
            {
                var data = mission.Surface != null ? mission.Surface.navMeshData : null;
                Object.DestroyImmediate(mission.Root);   // immediate: the surface removes its NavMesh on disable
                if (data != null)
                    Object.DestroyImmediate(data);   // the data is a separate object that removing the NavMesh leaves behind
            }
            mission = null;
        }

        MissionLayout Layout(int seed)
        {
            var result = MissionGenerator.Generate(new MissionSettings { seed = seed });
            Assert.That(result.Succeeded, Is.True, result.Describe());
            return result.Layout;
        }

        MissionLayout LayoutWith(System.Func<MissionLayout, bool> predicate)
        {
            for (var seed = 1; seed <= 60; seed++)
            {
                var layout = Layout(seed);
                if (predicate(layout))
                    return layout;
            }
            Assert.Fail("no seed in 1..60 gives such a layout");
            return null;
        }

        CoverRegistry Discover(GeneratedMission built)
        {
            var registry = world.CreateRegistry();
            var discovery = world.Track(new GameObject("Discovery")).AddComponent<CoverDiscovery>();
            discovery.Initialize(registry, discoverAtStart: false);
            var origin = built.Layout.TileCenter(built.Layout.FriendlySpawns[0]);
            discovery.Discover(built.Geometry, MissionNavigation.ReachableFrom(origin));
            return registry;
        }

        [UnityTest]
        public IEnumerator Build_CreatesTheOwnedRoot_WithOneCubePerFloorRectangleAndBox()
        {
            var layout = Layout(12345);
            mission = MissionBuilder.Build(layout, null, null);
            yield return null;

            Assert.That(mission.Root.name, Is.EqualTo(GeneratedMission.RootName));
            Assert.That(mission.Geometry.parent, Is.EqualTo(mission.Root.transform));
            Assert.That(mission.Actors.parent, Is.EqualTo(mission.Root.transform));
            Assert.That(mission.Geometry.childCount, Is.EqualTo(layout.FloorRects.Count + layout.Boxes.Count));
            foreach (var box in layout.Boxes)
            {
                var cube = mission.Geometry.Find(box.Name);
                Assert.That(cube, Is.Not.Null, box.Name);
                Assert.That(cube.GetComponent<CoverSurface>(), Is.Not.Null, box.Name);
                var modifier = cube.GetComponent<NavMeshModifier>();
                Assert.That(modifier != null && modifier.overrideArea && modifier.area == 1, Is.True, box.Name + " is not carved");
                Assert.That(cube.GetComponent<BoxCollider>().bounds.size.y, Is.EqualTo(box.Height).Within(0.01f));
            }
            Assert.That(mission.Geometry.GetComponentsInChildren<CoverSurface>(), Has.Length.EqualTo(layout.Boxes.Count));
        }

        [UnityTest]
        public IEnumerator Build_WhenThePreBakeContentThrows_RethrowsAndLeavesNoMissionRoot()
        {
            var layout = Layout(12345);

            Assert.That(() => MissionBuilder.Build(layout, null, null, g => throw new System.InvalidOperationException("boom")),
                Throws.TypeOf<System.InvalidOperationException>().With.Message.EqualTo("boom"));
            yield return null;

            Assert.That(GameObject.Find(GeneratedMission.RootName), Is.Null, "the half-built root was destroyed");
        }

        [UnityTest]
        public IEnumerator Build_GivesANavMeshThatValidates_ForSeveralSeeds()
        {
            for (var seed = 1; seed <= 8; seed++)
            {
                mission = MissionBuilder.Build(Layout(seed), null, null);
                yield return null;
                Assert.That(MissionNavigation.Validate(mission.Layout, out var reason, out var report), Is.True, $"seed {seed}: {reason}");
                Assert.That(report.NavArea, Is.GreaterThan(report.FloorArea * MissionConstants.MinNavAreaRatio));
                Assert.That(report.PathsChecked, Is.GreaterThanOrEqualTo(mission.Layout.Rooms.Count));
                DestroyMission();
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator Validate_FailsWhenThereIsNoNavMesh()
        {
            yield return null;
            Assert.That(MissionNavigation.Validate(Layout(12345), out var reason, out _), Is.False);
            Assert.That(reason, Does.Contain("NavMesh"));
        }

        [UnityTest]
        public IEnumerator TallWallEnds_ProduceCornerLocations_AndEveryBaffleGetsFour()
        {
            var layout = LayoutWith(l => l.Boxes.Any(b => b.Kind == MissionBoxKind.Baffle));
            mission = MissionBuilder.Build(layout, null, null);
            yield return null;
            var registry = Discover(mission);

            var corners = registry.Points.Where(p => p.Placement == CoverPlacement.Corner && p.Height == CoverHeight.Tall).ToList();
            Assert.That(corners.Count, Is.GreaterThanOrEqualTo(4));
            foreach (var baffle in layout.Boxes.Where(b => b.Kind == MissionBoxKind.Baffle))
                Assert.That(corners.Count(p => p.Obstacle.gameObject.name == baffle.Name), Is.EqualTo(4), baffle.Name);
            Assert.That(corners.Any(p => p.Obstacle.gameObject.name.StartsWith("Wall_")), Is.True, "room walls end at door gaps too");
        }

        [UnityTest]
        public IEnumerator LowObstacles_ProduceLowFaceLocationsOnly()
        {
            var layout = LayoutWith(l => l.Boxes.Any(b => b.Kind == MissionBoxKind.LowWall));
            mission = MissionBuilder.Build(layout, null, null);
            yield return null;
            var registry = Discover(mission);

            var low = layout.Boxes.Where(b => b.Kind == MissionBoxKind.LowWall || b.Kind == MissionBoxKind.Crate).Select(b => b.Name).ToHashSet();
            var own = registry.Points.Where(p => low.Contains(p.Obstacle.gameObject.name)).ToList();
            Assert.That(own, Is.Not.Empty);
            Assert.That(own.All(p => p.Height == CoverHeight.Low && p.Placement == CoverPlacement.Face), Is.True);
        }

        [UnityTest]
        public IEnumerator Cover_OnlyWhereItCovers_TallLocationsAreOutwardCornersOrColumns_AndLowCoverStillExists()
        {
            foreach (var seed in new[] { 1, 2, 3, 4, 5, 6, 7, 8, 12345 })
            {
                var layout = Layout(seed);
                mission = MissionBuilder.Build(layout, null, null);
                yield return null;
                var registry = Discover(mission);

                var tall = registry.Points.Where(p => p.Height == CoverHeight.Tall).ToList();
                var low = registry.Points.Where(p => p.Height == CoverHeight.Low).ToList();
                var corners = tall.Where(p => p.Placement == CoverPlacement.Corner).ToList();
                var columns = tall.Where(p => p.Placement == CoverPlacement.Column).ToList();
                Assert.That(tall.Any(p => p.Placement == CoverPlacement.Face), Is.False, $"seed {seed}: no cover along a tall wall");
                Assert.That(corners.All(p => p.HasPeek), Is.True, $"seed {seed}: every corner opens outward");
                Assert.That(columns.All(p => !p.HasPeek), Is.True, $"seed {seed}: a column has no peek data");
                Assert.That(corners.Count + columns.Count, Is.EqualTo(tall.Count), $"seed {seed}: every tall location is a corner or a column");
                Assert.That(corners, Is.Not.Empty, $"seed {seed}: wall ends give corners");
                Assert.That(low, Is.Not.Empty, $"seed {seed}: low cover still exists");
                Assert.That(low.All(p => p.Placement == CoverPlacement.Face), Is.True, $"seed {seed}");
                // Every corner's peek point is open floor: the whole point of the rule.
                foreach (var corner in corners)
                    Assert.That(NavMesh.SamplePosition(corner.PeekPoint, out _, 0.3f, NavMesh.AllAreas), Is.True, $"seed {seed}: {corner.Name} peek point is walkable");

                // Decision 030: cover belongs to its object. A baffle keeps its four corners and gets a column beyond each
                // end; a pillar (its sides are always free: obstacles keep 2 tiles of clearance) gets one on every face.
                foreach (var baffle in layout.Boxes.Where(b => b.Kind == MissionBoxKind.Baffle))
                {
                    Assert.That(corners.Count(p => p.Obstacle.gameObject.name == baffle.Name), Is.EqualTo(4), $"seed {seed}: {baffle.Name} corners");
                    var ends = columns.Where(p => p.Obstacle.gameObject.name == baffle.Name).ToList();
                    Assert.That(ends, Has.Count.EqualTo(2), $"seed {seed}: {baffle.Name} end-cap columns");
                    foreach (var end in ends)
                        Assert.That(NavMesh.SamplePosition(end.Position, out _, 0.25f, NavMesh.AllAreas), Is.True, $"seed {seed}: {end.Name} is on the NavMesh");
                }
                foreach (var pillar in layout.Boxes.Where(b => b.Kind == MissionBoxKind.Pillar))
                    Assert.That(columns.Count(p => p.Obstacle.gameObject.name == pillar.Name), Is.EqualTo(4), $"seed {seed}: {pillar.Name} columns");

                // Decision 031: an end cap exists only where a unit can step round the end on both sides. A room-frame wall
                // has floor on one side only, so it has corner points but no column; every column belongs to a baffle or
                // a pillar, and every corner's peek point is walkable.
                var freeStanding = layout.Boxes.Where(b => b.Kind == MissionBoxKind.Baffle || b.Kind == MissionBoxKind.Pillar).Select(b => b.Name).ToHashSet();
                Assert.That(columns.Where(p => p.Obstacle.gameObject.name.StartsWith("Wall_")), Is.Empty, $"seed {seed}: no column has a room-frame wall as its obstacle");
                Assert.That(columns.All(p => freeStanding.Contains(p.Obstacle.gameObject.name)), Is.True, $"seed {seed}: every column belongs to a baffle or a pillar");
                Assert.That(tall.All(p => p.Placement == CoverPlacement.Corner
                    ? NavMesh.SamplePosition(p.PeekPoint, out _, 0.3f, NavMesh.AllAreas)
                    : freeStanding.Contains(p.Obstacle.gameObject.name)), Is.True, $"seed {seed}: every tall location is a corner with a walkable peek or a column of a baffle or pillar");

                // Every location is reachable from the friendly spawn.
                NavMesh.SamplePosition(layout.TileCenter(layout.FriendlySpawns[0]), out var start, 1f, NavMesh.AllAreas);
                var path = new NavMeshPath();
                foreach (var point in registry.Points)
                {
                    Assert.That(NavMesh.SamplePosition(point.Position, out var hit, 0.5f, NavMesh.AllAreas), Is.True, $"seed {seed}: {point.Name}");
                    Assert.That(NavMesh.CalculatePath(start.position, hit.position, NavMesh.AllAreas, path) && path.status == NavMeshPathStatus.PathComplete,
                        Is.True, $"seed {seed}: {point.Name} is reachable");
                }
                DestroyMission();
                yield return null;
            }
        }

        // Decision 031: at every opening whose jamb is not flush with the room edge, a unit has cover from both directions: a
        // corner point of a room-frame wall standing on the room side of the mouth and one on the hallway side, each within
        // 2.5 m of the jamb corner. No room-frame wall has an end-cap column.
        [UnityTest]
        public IEnumerator EveryOpeningJamb_HasACornerOnTheRoomSide_AndOneOnTheHallwaySide_AndNoWallHasAColumn()
        {
            var jambsChecked = 0;
            foreach (var seed in new[] { 1, 2, 3, 4, 5, 6, 12345 })
            {
                var layout = Layout(seed);
                mission = MissionBuilder.Build(layout, null, null);
                yield return null;
                var points = Discover(mission).Points;

                Assert.That(points.Where(p => p.Placement == CoverPlacement.Column && p.Obstacle.gameObject.name.StartsWith("Wall_")),
                    Is.Empty, $"seed {seed}: a room-frame wall has no end cap");
                var wallCorners = points.Where(p => p.Placement == CoverPlacement.Corner && p.Obstacle.gameObject.name.StartsWith("Wall_")).ToList();

                foreach (var jamb in NonFlushJambs(layout))
                {
                    bool roomCorner = false, hallwayCorner = false;
                    foreach (var corner in wallCorners)
                    {
                        if (CoverRules.FlatDistance(corner.Position, jamb.Corner) > 2.5f)
                            continue;
                        var side = jamb.RoomSideOffset(corner.Position);
                        roomCorner |= side > 0.1f;
                        hallwayCorner |= side < -0.1f;
                    }
                    Assert.That(roomCorner, Is.True, $"seed {seed}: {jamb.Label} has a room-side corner");
                    Assert.That(hallwayCorner, Is.True, $"seed {seed}: {jamb.Label} has a hallway-side corner");
                    jambsChecked++;
                }
                DestroyMission();
                yield return null;
            }
            Assert.That(jambsChecked, Is.GreaterThan(40), "the test is not vacuous: many non-flush jambs were checked");
        }

        // Decision 032 (the owner: corner points "should be equal distance from the wall corner on both sides"): at every
        // non-flush jamb the room-side and the hallway-side corner points stand cornerInset (0.35 m) from the visible
        // corner, measured along the wall, so the two are equally far from it. Every corner point of a room-frame wall
        // stands cornerInset from a visible outer corner of the wall mass along its peek direction.
        [UnityTest]
        public IEnumerator EveryOpeningJamb_HasItsTwoCornerPoints_EquallyFarFromTheVisibleCorner()
        {
            const float inset = 0.35f, tolerance = 0.05f;
            var jambsChecked = 0;
            foreach (var seed in new[] { 1, 2, 3, 4, 5, 6, 12345 })
            {
                var layout = Layout(seed);
                mission = MissionBuilder.Build(layout, null, null);
                yield return null;
                var wallCorners = Discover(mission).Points
                    .Where(p => p.Placement == CoverPlacement.Corner && p.Obstacle.gameObject.name.StartsWith("Wall_")).ToList();

                foreach (var jamb in NonFlushJambs(layout))
                {
                    float room = float.NaN, hallway = float.NaN;
                    foreach (var corner in wallCorners)
                    {
                        // A point of this corner: the jamb corner lies on the point's wall face line, ahead along its peek.
                        var toCorner = jamb.Corner - corner.Position;
                        var along = Vector3.Dot(toCorner, corner.PeekDirection);
                        if (Mathf.Abs(Vector3.Dot(toCorner, corner.Facing) - 0.75f) > tolerance || along < -tolerance || along > 2f)
                            continue;
                        if (jamb.RoomSideOffset(corner.Position) > 0.1f)
                            room = along;
                        else
                            hallway = along;
                    }
                    Assert.That(room, Is.EqualTo(inset).Within(tolerance), $"seed {seed}: {jamb.Label} room-side corner, along the wall from the visible corner");
                    Assert.That(hallway, Is.EqualTo(inset).Within(tolerance), $"seed {seed}: {jamb.Label} hallway-side corner, along the wall from the visible corner");
                    jambsChecked++;
                }

                var outerCorners = VisibleOuterCorners(layout);
                foreach (var corner in wallCorners)
                {
                    var nearest = float.MaxValue;
                    foreach (var outer in outerCorners)
                    {
                        var toCorner = outer - corner.Position;
                        var along = Vector3.Dot(toCorner, corner.PeekDirection);
                        if (Mathf.Abs(Vector3.Dot(toCorner, corner.Facing) - 0.75f) <= tolerance && along >= -tolerance)
                            nearest = Mathf.Min(nearest, along);
                    }
                    Assert.That(nearest, Is.EqualTo(inset).Within(tolerance), $"seed {seed}: {corner.Name} stands cornerInset from a visible corner");
                }
                DestroyMission();
                yield return null;
            }
            Assert.That(jambsChecked, Is.GreaterThan(40), "the test is not vacuous: many non-flush jambs were checked");
        }

        // A visible outer corner of the wall mass: a tile-grid point where exactly one of the four tiles around it is a
        // wall tile (a tile covered by any Wall_n box).
        static List<Vector3> VisibleOuterCorners(MissionLayout layout)
        {
            var walls = new HashSet<Vector2Int>();
            foreach (var box in layout.Boxes.Where(b => b.Kind == MissionBoxKind.Wall))
                for (var x = box.Footprint.xMin; x < box.Footprint.xMax; x++)
                    for (var y = box.Footprint.yMin; y < box.Footprint.yMax; y++)
                        walls.Add(new Vector2Int(x, y));
            var corners = new List<Vector3>();
            for (var x = 0; x <= layout.Width; x++)
                for (var y = 0; y <= layout.Height; y++)
                {
                    var around = 0;
                    for (var dx = -1; dx <= 0; dx++)
                        for (var dy = -1; dy <= 0; dy++)
                            if (walls.Contains(new Vector2Int(x + dx, y + dy)))
                                around++;
                    if (around == 1)
                        corners.Add(layout.ToWorld(x, y));
                }
            return corners;
        }

        // A jamb of a corridor mouth that is not flush with the room edge (a flush edge has no jamb corner: the wall just
        // continues; decision 031 forbids a gap of exactly 1).
        readonly struct Jamb
        {
            public Jamb(string label, Vector3 corner, bool alongX, float roomSide)
            {
                Label = label;
                Corner = corner;
                AlongX = alongX;
                RoomSide = roomSide;
            }

            public string Label { get; }
            /// <summary>The visible corner of the wall end at the mouth, at ground level.</summary>
            public Vector3 Corner { get; }
            public bool AlongX { get; }
            public float RoomSide { get; }

            /// <summary>Positive: the position lies on the room's side of the mouth plane; negative: on the hallway's side.</summary>
            public float RoomSideOffset(Vector3 position)
            {
                var offset = position - Corner;
                return (AlongX ? offset.x : offset.z) * RoomSide;
            }
        }

        static IEnumerable<Jamb> NonFlushJambs(MissionLayout layout)
        {
            foreach (var connection in layout.Connections)
            {
                var strip = connection.Strip;
                var a = layout.Rooms[connection.RoomA];
                var b = layout.Rooms[connection.RoomB];
                var alongX = a.Cell.y == b.Cell.y;   // the strip runs along x between rooms side by side, else along z
                var firstRect = (alongX ? a.Cell.x < b.Cell.x : a.Cell.y < b.Cell.y) ? a.Rect : b.Rect;
                var secondRect = firstRect.Equals(a.Rect) ? b.Rect : a.Rect;
                for (var mouth = 0; mouth < 2; mouth++)
                {
                    var room = mouth == 0 ? firstRect : secondRect;
                    // The mouth plane is the room's inner face of the wall the strip passes through: the strip's near
                    // edge for the first room, its far edge for the second. The room lies on the -1 or +1 side of it.
                    var plane = alongX ? (mouth == 0 ? strip.xMin : strip.xMax) : (mouth == 0 ? strip.yMin : strip.yMax);
                    var roomSide = mouth == 0 ? -1f : 1f;
                    for (var jamb = 0; jamb < 2; jamb++)
                    {
                        var lateral = alongX ? (jamb == 0 ? strip.yMin : strip.yMax) : (jamb == 0 ? strip.xMin : strip.xMax);
                        var gap = alongX ? (jamb == 0 ? strip.yMin - room.yMin : room.yMax - strip.yMax)
                            : (jamb == 0 ? strip.xMin - room.xMin : room.xMax - strip.xMax);
                        if (gap == 0)
                            continue;   // flush with the room edge: the wall just continues, there is no jamb corner (a gap of 1 must fail the callers)
                        var corner = alongX ? layout.ToWorld(plane, lateral) : layout.ToWorld(lateral, plane);
                        yield return new Jamb($"strip {strip} mouth {mouth} jamb {jamb}", corner, alongX, roomSide);
                    }
                }
            }
        }

        [UnityTest]
        public IEnumerator EveryLocation_IsReachableFromTheFriendlySpawn()
        {
            mission = MissionBuilder.Build(Layout(12345), null, null);
            yield return null;
            var registry = Discover(mission);
            Assert.That(registry.Points, Is.Not.Empty);

            NavMesh.SamplePosition(mission.Layout.TileCenter(mission.Layout.FriendlySpawns[0]), out var start, 1f, NavMesh.AllAreas);
            var path = new NavMeshPath();
            foreach (var point in registry.Points)
            {
                Assert.That(NavMesh.SamplePosition(point.Position, out var hit, 0.5f, NavMesh.AllAreas), Is.True, point.Name);
                Assert.That(NavMesh.CalculatePath(start.position, hit.position, NavMesh.AllAreas, path) && path.status == NavMeshPathStatus.PathComplete,
                    Is.True, point.Name);
            }
        }

        [UnityTest]
        public IEnumerator SameSeedTwice_GivesTheSameCoverCount()
        {
            mission = MissionBuilder.Build(Layout(777), null, null);
            yield return null;
            var first = Discover(mission).Points.Count;
            DestroyMission();
            yield return null;
            mission = MissionBuilder.Build(Layout(777), null, null);
            yield return null;
            var second = Discover(mission).Points.Count;

            Assert.That(first, Is.GreaterThan(0));
            Assert.That(second, Is.EqualTo(first));
        }
    }
}
