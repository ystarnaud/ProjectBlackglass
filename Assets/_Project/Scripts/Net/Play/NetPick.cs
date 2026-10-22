using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// What a click ray meets in the drawn world, by plain geometry (the play client has no colliders): the nearest obstacle box,
    /// or the floor plane on a floor tile, or a unit's upright box. Pure.
    /// </summary>
    public static class NetPick
    {
        public enum GroundHit
        {
            None,
            Floor,
            Blocked,
        }

        const float UnitRadius = 0.5f;
        const float UnitHeight = 1.9f;

        public static GroundHit Ground(Ray ray, MissionLayout layout, IReadOnlyList<Bounds> obstacles, out Vector3 point)
        {
            point = default;
            var nearestObstacle = float.PositiveInfinity;
            foreach (var bounds in obstacles)
                if (bounds.IntersectRay(ray, out var distance) && distance >= 0f && distance < nearestObstacle)
                    nearestObstacle = distance;
            if (new Plane(Vector3.up, Vector3.zero).Raycast(ray, out var floorDistance) && floorDistance <= nearestObstacle)
            {
                var hit = ray.GetPoint(floorDistance);
                var tileX = Mathf.FloorToInt(hit.x + layout.Width * 0.5f);
                var tileY = Mathf.FloorToInt(hit.z + layout.Height * 0.5f);
                if (!layout.IsFloor(tileX, tileY))
                    return float.IsPositiveInfinity(nearestObstacle) ? GroundHit.None : GroundHit.Blocked;
                point = new Vector3(hit.x, 0f, hit.z);
                return GroundHit.Floor;
            }
            return float.IsPositiveInfinity(nearestObstacle) ? GroundHit.None : GroundHit.Blocked;
        }

        /// <summary>The id of the nearest unit whose upright box the ray enters, or 0.</summary>
        public static int Unit(Ray ray, IReadOnlyList<(int Id, Vector3 Position)> units)
        {
            var best = 0;
            var bestDistance = float.PositiveInfinity;
            foreach (var (id, position) in units)
            {
                var box = new Bounds(position + Vector3.up * (UnitHeight * 0.5f), new Vector3(UnitRadius * 2f, UnitHeight, UnitRadius * 2f));
                if (box.IntersectRay(ray, out var distance) && distance < bestDistance)
                {
                    best = id;
                    bestDistance = distance;
                }
            }
            return best;
        }
    }
}
