using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>Fixed numbers of the mission generator that are deliberately not exposed as settings.</summary>
    public static class MissionConstants
    {
        public const float WallHeight = 3f;
        public const float LowHeight = 1f;
        public const float FloorThickness = 0.2f;
        public const int RoomMin = 7;
        public const int RoomMax = 10;
        /// <summary>Tiles between a cell edge and its room, so the gutter between rooms is at least twice this.</summary>
        public const int RoomInset = 2;
        /// <summary>Free tiles kept between an obstacle and any wall or other obstacle.</summary>
        public const int Clearance = 2;
        public const float SpawnSpacing = 2.5f;
        /// <summary>The eroded NavMesh must cover at least this share of the floor: a sanity check, not a quality bar.</summary>
        public const float MinNavAreaRatio = 0.3f;
    }

    /// <summary>Plain tuning data for the mission generator. Same seed + same settings = same layout.</summary>
    [Serializable]
    public sealed class MissionSettings
    {
        public int seed = 12345;
        public int gridColumns = 4;
        public int gridRows = 3;
        /// <summary>Metres (= tiles) per grid cell.</summary>
        public int cellSize = 14;
        public int roomCount = 6;
        public int corridorWidth = 3;
        /// <summary>Extra connections beyond the spanning tree (loops).</summary>
        public int extraLoops = 1;
        /// <summary>Most free-standing tall wall segments per room.</summary>
        public int bafflesPerRoom = 1;
        /// <summary>Low-cover objects per 100 square metres of floor.</summary>
        public float lowCoverDensity = 3f;
        public int friendlyCount = 3;
        public int hostileCount = 3;
        /// <summary>Straight-line metres between any friendly and any hostile spawn.</summary>
        public float minTeamSeparation = 16f;
        public int maxAttempts = 20;

        /// <summary>A clamped copy; the original is untouched. Everything downstream works on the copy.</summary>
        public MissionSettings Validated()
        {
            var copy = (MissionSettings)MemberwiseClone();
            copy.gridColumns = Mathf.Clamp(gridColumns, 2, 6);
            copy.gridRows = Mathf.Clamp(gridRows, 2, 6);
            copy.cellSize = Mathf.Clamp(cellSize, 12, 20);
            copy.roomCount = Mathf.Clamp(roomCount, 2, copy.gridColumns * copy.gridRows);
            copy.corridorWidth = Mathf.Clamp(corridorWidth, 3, 5);
            copy.extraLoops = Mathf.Clamp(extraLoops, 0, 4);
            copy.bafflesPerRoom = Mathf.Clamp(bafflesPerRoom, 0, 3);
            copy.lowCoverDensity = Mathf.Clamp(lowCoverDensity, 0f, 10f);
            copy.friendlyCount = Mathf.Clamp(friendlyCount, 1, 6);
            copy.hostileCount = Mathf.Clamp(hostileCount, 1, 8);
            copy.minTeamSeparation = Mathf.Clamp(minTeamSeparation, 0f, 60f);
            copy.maxAttempts = Mathf.Clamp(maxAttempts, 1, 100);
            return copy;
        }

        /// <summary>One line with every setting, so a failure message plus its seed reproduces the layout.</summary>
        public string Describe() =>
            FormattableString.Invariant($"seed={seed} grid={gridColumns}x{gridRows} cell={cellSize} rooms={roomCount} corridor={corridorWidth} ") +
            FormattableString.Invariant($"loops={extraLoops} baffles={bafflesPerRoom} lowCover={lowCoverDensity:0.##} friendlies={friendlyCount} ") +
            FormattableString.Invariant($"hostiles={hostileCount} separation={minTeamSeparation:0.##} attempts={maxAttempts}");
    }
}
