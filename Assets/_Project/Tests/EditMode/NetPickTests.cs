using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>Picking against the drawn map with no physics: open floor, an obstacle in the way, off the map, and units.</summary>
    public class NetPickTests
    {
        static MissionLayout Layout(out List<Bounds> obstacles)
        {
            var map = new NetMap(10, 10, new List<RectInt> { new RectInt(1, 1, 8, 8) },
                new List<NetMapBox> { new NetMapBox(MissionBoxKind.Pillar, new RectInt(4, 4, 2, 2), 3f) }, new List<Vector2Int>());
            var layout = NetMapGeometry.ToLayout(map);
            obstacles = new List<Bounds> { NetMapGeometry.BoxBounds(layout, layout.Boxes[0]) };
            return layout;
        }

        static Ray Down(Vector3 at) => new Ray(at + Vector3.up * 20f, Vector3.down);

        [Test]
        public void ARayOntoOpenFloor_GivesThatGroundPoint()
        {
            var layout = Layout(out var obstacles);
            var target = layout.TileCenter(new Vector2Int(2, 2));
            Assert.That(NetPick.Ground(Down(target), layout, obstacles, out var point), Is.EqualTo(NetPick.GroundHit.Floor));
            Assert.That(Vector3.Distance(point, target), Is.LessThan(1e-4f));
        }

        [Test]
        public void ARayThatMeetsAnObstacleFirst_IsBlocked()
        {
            var layout = Layout(out var obstacles);
            Assert.That(NetPick.Ground(Down(layout.TileCenter(new Vector2Int(4, 4))), layout, obstacles, out _), Is.EqualTo(NetPick.GroundHit.Blocked));
        }

        [Test]
        public void AnObliqueRayPastAWall_StopsAtTheWallNotTheFloorBehindIt()
        {
            var layout = Layout(out var obstacles);
            // Aimed at a point inside the pillar from the west and above: the ray meets the pillar's west face before it could
            // reach the open floor beyond.
            var inside = layout.ToWorld(4.5f, 5f) + Vector3.up * 1.5f;
            var origin = inside + new Vector3(-10f, 5f, 0f);
            Assert.That(NetPick.Ground(new Ray(origin, (inside - origin).normalized), layout, obstacles, out _), Is.EqualTo(NetPick.GroundHit.Blocked));
        }

        [Test]
        public void ARayOffTheFloor_IsNothing()
        {
            var layout = Layout(out var obstacles);
            Assert.That(NetPick.Ground(Down(layout.TileCenter(new Vector2Int(0, 0))), layout, obstacles, out _), Is.EqualTo(NetPick.GroundHit.None));
            Assert.That(NetPick.Ground(new Ray(Vector3.up, Vector3.up), layout, obstacles, out _), Is.EqualTo(NetPick.GroundHit.None));
        }

        [Test]
        public void Unit_PicksTheNearestUnitAlongTheRay_OrNone()
        {
            var units = new List<(int, Vector3)> { (1, new Vector3(0f, 0f, 0f)), (2, new Vector3(0f, 0f, 5f)) };
            var ray = new Ray(new Vector3(0f, 1f, -10f), Vector3.forward);
            Assert.That(NetPick.Unit(ray, units), Is.EqualTo(1));
            Assert.That(NetPick.Unit(new Ray(new Vector3(3f, 1f, -10f), Vector3.forward), units), Is.Zero);
        }
    }
}
