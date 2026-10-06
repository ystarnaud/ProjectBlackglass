using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    public enum MissionBoxKind
    {
        Wall,
        Baffle,
        Pillar,
        LowWall,
        Crate,
    }

    /// <summary>An upright axis-aligned box, in tile units, standing on y = 0.</summary>
    public readonly struct MissionBox
    {
        public MissionBox(string name, MissionBoxKind kind, RectInt footprint, float height)
        {
            Name = name;
            Kind = kind;
            Footprint = footprint;
            Height = height;
        }

        public string Name { get; }
        public MissionBoxKind Kind { get; }
        public RectInt Footprint { get; }
        public float Height { get; }
        public bool IsTall => Kind == MissionBoxKind.Wall || Kind == MissionBoxKind.Baffle || Kind == MissionBoxKind.Pillar;
    }

    public readonly struct MissionRoom
    {
        public MissionRoom(int index, Vector2Int cell, RectInt rect)
        {
            Index = index;
            Cell = cell;
            Rect = rect;
        }

        public int Index { get; }
        public Vector2Int Cell { get; }
        public RectInt Rect { get; }
    }

    public readonly struct MissionConnection
    {
        public MissionConnection(int roomA, int roomB, RectInt strip)
        {
            RoomA = roomA;
            RoomB = roomB;
            Strip = strip;
        }

        public int RoomA { get; }
        public int RoomB { get; }
        /// <summary>The corridor floor between the two rooms (the gutter), as tiles.</summary>
        public RectInt Strip { get; }
    }

    /// <summary>
    /// The pure result of mission generation: integer tile data only. The grid is Width x Height tiles of 1 m; tile
    /// (x, y) is world (x, z) after ToWorld centres the grid on the origin.
    /// </summary>
    public sealed class MissionLayout
    {
        readonly bool[] floor;

        internal MissionLayout(int seed, int attempt, int width, int height, bool[] floorMask)
        {
            Seed = seed;
            Attempt = attempt;
            Width = width;
            Height = height;
            floor = floorMask;
            for (var i = 0; i < floor.Length; i++)
                if (floor[i])
                    FloorTileCount++;
        }

        public int Seed { get; }
        public int Attempt { get; }
        public int Width { get; }
        public int Height { get; }
        public int FloorTileCount { get; }

        public IReadOnlyList<MissionRoom> Rooms { get; internal set; } = Array.Empty<MissionRoom>();
        public IReadOnlyList<MissionConnection> Connections { get; internal set; } = Array.Empty<MissionConnection>();
        public IReadOnlyList<RectInt> FloorRects { get; internal set; } = Array.Empty<RectInt>();
        /// <summary>Walls first, then obstacles.</summary>
        public IReadOnlyList<MissionBox> Boxes { get; internal set; } = Array.Empty<MissionBox>();
        public int FriendlyRoom { get; internal set; } = -1;
        public IReadOnlyList<int> HostileRooms { get; internal set; } = Array.Empty<int>();
        public RectInt FriendlyRegion { get; internal set; }
        public IReadOnlyList<RectInt> HostileRegions { get; internal set; } = Array.Empty<RectInt>();
        public IReadOnlyList<Vector2Int> FriendlySpawns { get; internal set; } = Array.Empty<Vector2Int>();
        public IReadOnlyList<Vector2Int> HostileSpawns { get; internal set; } = Array.Empty<Vector2Int>();
        /// <summary>Tight tile rectangle around the floor and its wall ring.</summary>
        public RectInt UsedTiles { get; internal set; }
        public ulong Hash { get; internal set; }

        public bool IsFloor(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height && floor[y * Width + x];

        public Vector3 ToWorld(float tileX, float tileY) => new Vector3(tileX - Width * 0.5f, 0f, tileY - Height * 0.5f);

        public Vector3 TileCenter(Vector2Int tile) => ToWorld(tile.x + 0.5f, tile.y + 0.5f);

        public Vector3 RectCenter(RectInt rect) => ToWorld(rect.xMin + rect.width * 0.5f, rect.yMin + rect.height * 0.5f);

        public Bounds WorldBounds
        {
            get
            {
                var min = ToWorld(UsedTiles.xMin, UsedTiles.yMin);
                var max = ToWorld(UsedTiles.xMax, UsedTiles.yMax);
                var bounds = new Bounds();
                bounds.SetMinMax(min, new Vector3(max.x, MissionConstants.WallHeight, max.z));
                return bounds;
            }
        }
    }
}
