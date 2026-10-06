using System;
using UnityEngine;
using UnityEngine.AI;

namespace Blackglass
{
    public readonly struct MissionNavigationReport
    {
        public MissionNavigationReport(float navArea, float floorArea, int pathsChecked)
        {
            NavArea = navArea;
            FloorArea = floorArea;
            PathsChecked = pathsChecked;
        }

        public float NavArea { get; }
        public float FloorArea { get; }
        public int PathsChecked { get; }
    }

    /// <summary>Checks a freshly built NavMesh against the layout it was built from.</summary>
    public static class MissionNavigation
    {
        const float SnapRadius = 2f;

        /// <summary>
        /// Valid when the NavMesh is not empty and covers a sane share of the floor, the first friendly spawn is on it,
        /// and a complete path leads from there to every room centre and every spawn point.
        /// </summary>
        public static bool Validate(MissionLayout layout, out string reason, out MissionNavigationReport report)
        {
            var navArea = NavMeshArea();
            report = new MissionNavigationReport(navArea, layout.FloorTileCount, 0);
            if (navArea <= 0f)
            {
                reason = "the NavMesh is empty";
                return false;
            }
            if (navArea < layout.FloorTileCount * MissionConstants.MinNavAreaRatio)
            {
                reason = $"the NavMesh covers only {navArea:0} of {layout.FloorTileCount} m2 of floor";
                return false;
            }
            if (!NavMesh.SamplePosition(layout.TileCenter(layout.FriendlySpawns[0]), out var start, 1f, NavMesh.AllAreas))
            {
                reason = "the first friendly spawn is not on the NavMesh";
                return false;
            }

            var path = new NavMeshPath();
            var checkedPaths = 0;
            foreach (var room in layout.Rooms)
            {
                if (!PathExists(start.position, layout.RectCenter(room.Rect), path, out reason, $"room {room.Index}"))
                    return false;
                checkedPaths++;
            }
            foreach (var tile in layout.FriendlySpawns)
            {
                if (!PathExists(start.position, layout.TileCenter(tile), path, out reason, "a friendly spawn"))
                    return false;
                checkedPaths++;
            }
            foreach (var tile in layout.HostileSpawns)
            {
                if (!PathExists(start.position, layout.TileCenter(tile), path, out reason, "a hostile spawn"))
                    return false;
                checkedPaths++;
            }
            report = new MissionNavigationReport(navArea, layout.FloorTileCount, checkedPaths);
            reason = null;
            return true;
        }

        static bool PathExists(Vector3 from, Vector3 to, NavMeshPath path, out string reason, string label)
        {
            reason = null;
            if (!NavMesh.SamplePosition(to, out var target, SnapRadius, NavMesh.AllAreas))
            {
                reason = $"{label} is not on the NavMesh";
                return false;
            }
            if (!NavMesh.CalculatePath(from, target.position, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
            {
                reason = $"no complete path from the friendly spawn to {label}";
                return false;
            }
            return true;
        }

        /// <summary>A predicate for cover discovery: true when a complete path leads from the origin to a walkable point within 0.5 m.</summary>
        public static Func<Vector3, bool> ReachableFrom(Vector3 origin)
        {
            var path = new NavMeshPath();
            return point =>
            {
                if (!NavMesh.SamplePosition(point, out var hit, 0.5f, NavMesh.AllAreas))
                    return false;
                return NavMesh.CalculatePath(origin, hit.position, NavMesh.AllAreas, path) && path.status == NavMeshPathStatus.PathComplete;
            };
        }

        /// <summary>Total area, in square metres, of the triangles of every active NavMesh.</summary>
        public static float NavMeshArea()
        {
            var triangulation = NavMesh.CalculateTriangulation();
            var vertices = triangulation.vertices;
            var indices = triangulation.indices;
            var area = 0f;
            for (var i = 0; i + 2 < indices.Length; i += 3)
            {
                var a = vertices[indices[i]];
                var b = vertices[indices[i + 1]];
                var c = vertices[indices[i + 2]];
                area += 0.5f * Mathf.Abs((b.x - a.x) * (c.z - a.z) - (c.x - a.x) * (b.z - a.z));
            }
            return area;
        }
    }
}
