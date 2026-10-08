#if UNITY_EDITOR
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>
    /// A hand-made layout for the intelligence tests: two 6x6 rooms on a 20x10 grid (tile (x, y) is world (x - 10, y - 5)),
    /// room A at world x -9..-3, z -4..2 and room B at x 3..9, z -4..2, with an optional 6x3 corridor between them
    /// (world x -3..3, z -3..0). Region ids: A 0, B 1, corridor 2.
    /// </summary>
    internal static class IntelLayouts
    {
        public static readonly RectInt RoomA = new RectInt(1, 1, 6, 6);
        public static readonly RectInt RoomB = new RectInt(13, 1, 6, 6);
        public static readonly RectInt Strip = new RectInt(7, 2, 6, 3);

        public static MissionLayout TwoRooms(bool corridor)
        {
            const int width = 20;
            const int height = 10;
            var floor = new bool[width * height];
            void Fill(RectInt rect)
            {
                for (var y = rect.yMin; y < rect.yMax; y++)
                    for (var x = rect.xMin; x < rect.xMax; x++)
                        floor[y * width + x] = true;
            }
            Fill(RoomA);
            Fill(RoomB);
            if (corridor)
                Fill(Strip);
            return new MissionLayout(1, 1, width, height, floor)
            {
                Rooms = new[] { new MissionRoom(0, new Vector2Int(0, 0), RoomA), new MissionRoom(1, new Vector2Int(1, 0), RoomB) },
                Connections = corridor ? new[] { new MissionConnection(0, 1, Strip) } : new MissionConnection[0],
                FriendlyRoom = 0,
                FriendlySpawns = new[] { new Vector2Int(3, 3) },
                HostileSpawns = new[] { new Vector2Int(16, 3) },
                Boxes = corridor
                    ? new[]
                    {
                        new MissionBox("Wall_Below", MissionBoxKind.Wall, new RectInt(7, 0, 6, 2), 3f),
                        new MissionBox("Wall_Above", MissionBoxKind.Wall, new RectInt(7, 5, 6, 5), 3f),
                        new MissionBox("Wall_EastOfB", MissionBoxKind.Wall, new RectInt(19, 1, 1, 6), 3f),
                    }
                    : new[]
                    {
                        new MissionBox("Wall_Gap", MissionBoxKind.Wall, new RectInt(7, 0, 6, 10), 3f),
                        new MissionBox("Wall_EastOfB", MissionBoxKind.Wall, new RectInt(19, 1, 1, 6), 3f),
                    },
            };
        }
    }
}
#endif
