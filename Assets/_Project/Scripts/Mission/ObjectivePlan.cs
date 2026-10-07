using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Where the mission's objective content goes, as integer tile coordinates (pure data, like the layout). The terminal and
    /// the extraction zone are placed on tiles; the guards are extra hostile spawn tiles in the terminal room. Hash is FNV-1a
    /// over everything, so tests can pin and compare placements.
    /// </summary>
    public sealed class ObjectivePlan
    {
        internal ObjectivePlan(int terminalRoom, Vector2Int terminalTile, IReadOnlyList<Vector2Int> guardTiles,
            int extractionRoom, Vector2Int extractionTile)
        {
            TerminalRoom = terminalRoom;
            TerminalTile = terminalTile;
            GuardTiles = new List<Vector2Int>(guardTiles).AsReadOnly();
            ExtractionRoom = extractionRoom;
            ExtractionTile = extractionTile;
            Hash = ComputeHash();
        }

        public int TerminalRoom { get; }
        public Vector2Int TerminalTile { get; }
        public IReadOnlyList<Vector2Int> GuardTiles { get; }
        public int ExtractionRoom { get; }
        public Vector2Int ExtractionTile { get; }
        public ulong Hash { get; }

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
            Add(TerminalRoom);
            Add(TerminalTile.x);
            Add(TerminalTile.y);
            Add(GuardTiles.Count);
            foreach (var tile in GuardTiles)
            {
                Add(tile.x);
                Add(tile.y);
            }
            Add(ExtractionRoom);
            Add(ExtractionTile.x);
            Add(ExtractionTile.y);
            return h;
        }
    }
}
