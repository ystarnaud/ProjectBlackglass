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
        /// <summary>
        /// Free tiles kept between an obstacle and any wall or other obstacle. The one exception: a low wall may touch a
        /// room wall with its short end (gap 0, a peninsula whose long faces keep this clearance to the other walls). Never
        /// one tile: a one-tile gap is too narrow for the NavMesh agent and only formed dead pockets (decision 030).
        /// </summary>
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
        /// <summary>Extra hostiles placed around the terminal (an upper bound: fewer when they do not fit).</summary>
        public int guardCount = 2;
        /// <summary>How long a unit works on the terminal.</summary>
        public float interactionSeconds = 2f;
        /// <summary>Living squad members that must be inside the extraction zone.</summary>
        public int extractionUnits = 1;
        /// <summary>The hostile group must be eliminated before extraction opens.</summary>
        public bool eliminateHostiles = true;
        /// <summary>The terminal must be hacked before extraction opens.</summary>
        public bool hackTerminal = true;

        /// <summary>Battlefield uncertainty and security cameras (decision 037). The default hides nothing.</summary>
        public IntelligenceSettings intelligence = new IntelligenceSettings();

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
            copy.guardCount = Mathf.Clamp(guardCount, 0, 4);
            copy.interactionSeconds = Mathf.Clamp(interactionSeconds, 0f, 30f);
            copy.extractionUnits = Mathf.Clamp(extractionUnits, 1, 6);
            // A mission always has a required objective; with both off the terminal stays.
            if (!copy.eliminateHostiles && !copy.hackTerminal)
                copy.hackTerminal = true;
            // MemberwiseClone shares nested objects: clone the intelligence settings so a validated copy is independent.
            copy.intelligence = (intelligence ?? new IntelligenceSettings()).Validated();
            return copy;
        }

        /// <summary>One line with every setting, so a failure message plus its seed reproduces the layout.</summary>
        public string Describe()
        {
            var text =
                FormattableString.Invariant($"seed={seed} grid={gridColumns}x{gridRows} cell={cellSize} rooms={roomCount} corridor={corridorWidth} ") +
                FormattableString.Invariant($"loops={extraLoops} baffles={bafflesPerRoom} lowCover={lowCoverDensity:0.##} friendlies={friendlyCount} ") +
                FormattableString.Invariant($"hostiles={hostileCount} separation={minTeamSeparation:0.##} attempts={maxAttempts} ") +
                FormattableString.Invariant($"guards={guardCount} interact={interactionSeconds:0.##} extractionUnits={extractionUnits} eliminate={eliminateHostiles} hack={hackTerminal}");
            if (intelligence != null && (intelligence.fogEnabled || intelligence.cameraCount > 0))
                text += " intel=[" + intelligence.Describe() + "]";
            return text;
        }
    }
}
