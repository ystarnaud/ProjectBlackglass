using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    public enum RegionKind
    {
        Room,
        Corridor,
    }

    /// <summary>One discovery unit: a room, or the corridor strip joining two rooms.</summary>
    public readonly struct MapRegion
    {
        public MapRegion(int index, RegionKind kind, RectInt rect)
        {
            Index = index;
            Kind = kind;
            Rect = rect;
        }

        public int Index { get; }
        public RegionKind Kind { get; }
        public RectInt Rect { get; }
    }

    /// <summary>
    /// The regions of a generated mission, derived from its MissionLayout (rooms first, ids equal to the layout's room
    /// indices, then one corridor per connection): the unit of discovery. Pure and immutable; it answers "which region is this
    /// tile or point in", "which regions touch this circle or wall footprint", "which regions border this one" and "where do I
    /// aim sight rays to see this region" (a coarse grid of floor sample points). It is not a second level representation: it
    /// only indexes the rectangles the layout already has.
    /// </summary>
    public sealed class RegionMap
    {
        /// <summary>Sample points float this high above the floor, so a ray to one ends at about a prone unit's height.</summary>
        public const float SampleHeight = 0.5f;
        const int SampleStep = 3;

        readonly MissionLayout layout;
        readonly MapRegion[] regions;
        readonly int[] tileRegion;
        readonly List<int>[] neighbours;
        readonly Vector3[][] samples;

        public RegionMap(MissionLayout layout)
        {
            this.layout = layout ?? throw new ArgumentNullException(nameof(layout));
            var rooms = layout.Rooms;
            var connections = layout.Connections;
            RoomCount = rooms.Count;
            regions = new MapRegion[rooms.Count + connections.Count];
            for (var i = 0; i < rooms.Count; i++)
                regions[i] = new MapRegion(i, RegionKind.Room, rooms[i].Rect);
            for (var j = 0; j < connections.Count; j++)
                regions[rooms.Count + j] = new MapRegion(rooms.Count + j, RegionKind.Corridor, connections[j].Strip);

            tileRegion = new int[layout.Width * layout.Height];
            for (var i = 0; i < tileRegion.Length; i++)
                tileRegion[i] = -1;
            // Corridors first, rooms second: a tile in both belongs to the room.
            for (var i = regions.Length - 1; i >= 0; i--)
                Paint(regions[i]);

            neighbours = new List<int>[regions.Length];
            for (var i = 0; i < neighbours.Length; i++)
                neighbours[i] = new List<int>();
            for (var j = 0; j < connections.Count; j++)
            {
                var corridor = rooms.Count + j;
                Link(corridor, connections[j].RoomA);
                Link(corridor, connections[j].RoomB);
            }

            samples = new Vector3[regions.Length][];
            for (var i = 0; i < regions.Length; i++)
                samples[i] = BuildSamples(regions[i].Rect);
        }

        public int Count => regions.Length;
        public int RoomCount { get; }
        public MapRegion this[int index] => regions[index];

        public int RegionOfTile(int x, int y) =>
            x < 0 || y < 0 || x >= layout.Width || y >= layout.Height ? -1 : tileRegion[y * layout.Width + x];

        public int RegionAt(Vector3 world) =>
            RegionOfTile(Mathf.FloorToInt(world.x + layout.Width * 0.5f), Mathf.FloorToInt(world.z + layout.Height * 0.5f));

        public IReadOnlyList<int> Neighbours(int region) => neighbours[region];

        public IReadOnlyList<Vector3> SamplePoints(int region) => samples[region];

        /// <summary>The region's rectangle in world space (x and y of the Rect are world x and z).</summary>
        public Rect WorldRect(int region)
        {
            var rect = regions[region].Rect;
            var min = layout.ToWorld(rect.xMin, rect.yMin);
            var max = layout.ToWorld(rect.xMax, rect.yMax);
            return Rect.MinMaxRect(min.x, min.z, max.x, max.z);
        }

        /// <summary>Clears `into` and fills it with every region whose rectangle touches the flat circle.</summary>
        public void RegionsInCircle(Vector3 centre, float radius, List<int> into)
        {
            into.Clear();
            for (var i = 0; i < regions.Length; i++)
            {
                var rect = WorldRect(i);
                var dx = centre.x - Mathf.Clamp(centre.x, rect.xMin, rect.xMax);
                var dz = centre.z - Mathf.Clamp(centre.z, rect.yMin, rect.yMax);
                if (dx * dx + dz * dz <= radius * radius)
                    into.Add(i);
            }
        }

        /// <summary>Clears `into` and fills it with every region whose rectangle, grown by one tile, overlaps the footprint (a wall box).</summary>
        public void RegionsAdjacentTo(RectInt footprint, List<int> into)
        {
            into.Clear();
            for (var i = 0; i < regions.Length; i++)
            {
                var r = regions[i].Rect;
                var grown = new RectInt(r.x - 1, r.y - 1, r.width + 2, r.height + 2);
                if (grown.Overlaps(footprint))
                    into.Add(i);
            }
        }

        void Paint(MapRegion region)
        {
            var rect = region.Rect;
            for (var y = Mathf.Max(0, rect.yMin); y < Mathf.Min(layout.Height, rect.yMax); y++)
                for (var x = Mathf.Max(0, rect.xMin); x < Mathf.Min(layout.Width, rect.xMax); x++)
                    tileRegion[y * layout.Width + x] = region.Index;
        }

        void Link(int a, int b)
        {
            if (b < 0 || b >= neighbours.Length)
                return;
            if (!neighbours[a].Contains(b))
                neighbours[a].Add(b);
            if (!neighbours[b].Contains(a))
                neighbours[b].Add(a);
        }

        // Floor tile centres on a three-tile grid inside the rect (offset one tile from the edge), or the centre tile for a
        // rect too small for the grid.
        Vector3[] BuildSamples(RectInt rect)
        {
            var list = new List<Vector3>();
            for (var y = rect.yMin + SampleStep / 2; y < rect.yMax; y += SampleStep)
                for (var x = rect.xMin + SampleStep / 2; x < rect.xMax; x += SampleStep)
                    AddSample(list, x, y);
            if (list.Count == 0)
                AddSample(list, rect.xMin + rect.width / 2, rect.yMin + rect.height / 2);
            return list.ToArray();
        }

        void AddSample(List<Vector3> list, int x, int y)
        {
            if (layout.IsFloor(x, y))
                list.Add(layout.TileCenter(new Vector2Int(x, y)) + Vector3.up * SampleHeight);
        }
    }
}
