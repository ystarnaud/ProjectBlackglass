using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>A camera's place: the floor tile it hangs over and the cardinal step from that tile to the wall it hangs on.</summary>
    public readonly struct CameraMount
    {
        public CameraMount(Vector2Int tile, Vector2Int wallSide, int room)
        {
            Tile = tile;
            WallSide = wallSide;
            Room = room;
        }

        public Vector2Int Tile { get; }
        public Vector2Int WallSide { get; }
        public int Room { get; }
    }

    /// <summary>
    /// Where the mission's security devices go, as integer tiles (pure data, like the layout and the objective plan): one
    /// camera-control terminal and the camera mounts. Device ids are the terminal (0) then the cameras in order, the key the
    /// knowledge model uses. Hash is FNV-1a over everything, so tests can pin and compare placements.
    /// </summary>
    public sealed class SecurityPlan
    {
        public const int TerminalDeviceId = 0;
        public const float CameraHeight = 2.6f;
        // How far from the tile centre toward the wall the camera hangs: the wall face is 0.5 away.
        const float WallInset = 0.4f;
        const float TerminalAimHeight = 0.6f;

        public static readonly SecurityPlan Empty = new SecurityPlan(false, -1, default, Array.Empty<CameraMount>());

        internal SecurityPlan(bool hasTerminal, int terminalRoom, Vector2Int terminalTile, IReadOnlyList<CameraMount> cameras)
        {
            HasTerminal = hasTerminal;
            TerminalRoom = terminalRoom;
            TerminalTile = terminalTile;
            Cameras = new List<CameraMount>(cameras).AsReadOnly();
            Hash = ComputeHash();
        }

        public bool HasTerminal { get; }
        public int TerminalRoom { get; }
        public Vector2Int TerminalTile { get; }
        public IReadOnlyList<CameraMount> Cameras { get; }
        public ulong Hash { get; }
        public int DeviceCount => HasTerminal ? 1 + Cameras.Count : 0;

        public int CameraDeviceId(int index) => 1 + index;

        public Vector3 TerminalPosition(MissionLayout layout) =>
            layout.TileCenter(TerminalTile) + Vector3.up * TerminalAimHeight;

        public Vector3 CameraPosition(MissionLayout layout, int index)
        {
            var side = Cameras[index].WallSide;
            return layout.TileCenter(Cameras[index].Tile) + new Vector3(side.x, 0f, side.y) * WallInset + Vector3.up * CameraHeight;
        }

        public Vector3 CameraForward(int index)
        {
            var side = Cameras[index].WallSide;
            return new Vector3(-side.x, 0f, -side.y);
        }

        ulong ComputeHash()
        {
            var h = 14695981039346656037UL;
            void Add(int value)
            {
                unchecked
                {
                    h ^= (uint)value;
                    h *= 1099511628211UL;
                }
            }
            Add(HasTerminal ? 1 : 0);
            Add(TerminalRoom);
            Add(TerminalTile.x);
            Add(TerminalTile.y);
            Add(Cameras.Count);
            foreach (var mount in Cameras)
            {
                Add(mount.Tile.x);
                Add(mount.Tile.y);
                Add(mount.WallSide.x);
                Add(mount.WallSide.y);
                Add(mount.Room);
            }
            return h;
        }
    }
}
