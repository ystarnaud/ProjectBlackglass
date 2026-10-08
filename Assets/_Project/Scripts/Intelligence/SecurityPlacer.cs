using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Chooses where the security devices go, from the generated layout and the objective plan alone: pure integer tile
    /// work on its own seed stream (SeededRandom.ForSecurity), after the objectives, so it never moves a wall or an
    /// objective. With no cameras asked for it places nothing. Rules (decision 037): the camera-control terminal goes in the
    /// friendly room, else in a room joined to it, on a tile with a free 3x3 block, clear of every spawn, guard and the
    /// objective terminal and at least ExtractionClearance from the extraction tile; each camera hangs on a wall of its own
    /// non-friendly room, facing into it. A layout that cannot take the terminal or any camera fails the attempt.
    /// </summary>
    public static class SecurityPlacer
    {
        static readonly Vector2Int[] Directions =
        {
            new Vector2Int(0, 1), new Vector2Int(1, 0), new Vector2Int(0, -1), new Vector2Int(-1, 0),
        };

        public static bool TryPlace(MissionLayout layout, ObjectivePlan objectives, MissionSettings settings,
            out SecurityPlan plan, out string reason)
        {
            plan = SecurityPlan.Empty;
            reason = null;
            var wanted = settings.intelligence != null ? settings.intelligence.cameraCount : 0;
            if (wanted <= 0)
                return true;

            var rng = SeededRandom.ForSecurity(layout.Seed, layout.Attempt);
            var blocked = ObjectivePlacer.ObstacleMask(layout);
            var avoid = new List<Vector2Int>(layout.FriendlySpawns);
            avoid.AddRange(layout.HostileSpawns);
            if (objectives != null)
            {
                avoid.AddRange(objectives.GuardTiles);
                avoid.Add(objectives.TerminalTile);
            }

            // The terminal: the friendly room first, then the rooms joined to it.
            var terminalRoom = -1;
            var terminalTile = default(Vector2Int);
            foreach (var room in TerminalRooms(layout))
            {
                var area = ObjectivePlacer.Shrink(layout.Rooms[room].Rect, ObjectivePlacer.FreeRadius);
                var tiles = ObjectivePlacer.FreeTiles(layout, blocked, area, avoid, MissionConstants.SpawnSpacing);
                if (objectives != null)
                    tiles.RemoveAll(t => Vector2.Distance(t, objectives.ExtractionTile) < ObjectivePlacer.ExtractionClearance);
                if (tiles.Count == 0)
                    continue;
                terminalRoom = room;
                terminalTile = tiles[rng.NextInt(tiles.Count)];
                break;
            }
            if (terminalRoom < 0)
            {
                reason = "no room near the start has a free camera-terminal tile";
                return false;
            }

            // The cameras: one per room, in a shuffled order of the rooms other than the friendly one.
            var rooms = new List<int>();
            for (var r = 0; r < layout.Rooms.Count; r++)
            {
                if (r != layout.FriendlyRoom)
                    rooms.Add(r);
            }
            rng.Shuffle(rooms);
            var cameras = new List<CameraMount>();
            foreach (var room in rooms)
            {
                if (cameras.Count >= wanted)
                    break;
                var mounts = MountsIn(layout, blocked, room);
                if (mounts.Count > 0)
                    cameras.Add(mounts[rng.NextInt(mounts.Count)]);
            }
            if (cameras.Count == 0)
            {
                reason = "no room has a wall to hang a camera on";
                return false;
            }

            plan = new SecurityPlan(true, terminalRoom, terminalTile, cameras);
            return true;
        }

        static List<int> TerminalRooms(MissionLayout layout)
        {
            var rooms = new List<int>();
            if (layout.FriendlyRoom >= 0 && layout.FriendlyRoom < layout.Rooms.Count)
                rooms.Add(layout.FriendlyRoom);
            foreach (var connection in layout.Connections)
            {
                if (connection.RoomA == layout.FriendlyRoom && !rooms.Contains(connection.RoomB))
                    rooms.Add(connection.RoomB);
                else if (connection.RoomB == layout.FriendlyRoom && !rooms.Contains(connection.RoomA))
                    rooms.Add(connection.RoomA);
            }
            return rooms;
        }

        // Every floor tile of the room, free of obstacles, with a wall (a non-floor tile) on one cardinal side.
        static List<CameraMount> MountsIn(MissionLayout layout, bool[] blocked, int room)
        {
            var mounts = new List<CameraMount>();
            var rect = layout.Rooms[room].Rect;
            for (var y = rect.yMin; y < rect.yMax; y++)
            {
                for (var x = rect.xMin; x < rect.xMax; x++)
                {
                    if (!layout.IsFloor(x, y) || blocked[y * layout.Width + x])
                        continue;
                    foreach (var side in Directions)
                    {
                        if (!layout.IsFloor(x + side.x, y + side.y) && layout.IsFloor(x - side.x, y - side.y))
                            mounts.Add(new CameraMount(new Vector2Int(x, y), side, room));
                    }
                }
            }
            return mounts;
        }
    }
}
