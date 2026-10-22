using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The client's side of the projection (decision 052): a MissionLayout rebuilt from a validated NetMap, and the world bounds
    /// of each cube MissionBuilder and MissionContent build for it. Seed 0 and attempt 0, because the real ones are never known;
    /// rooms, connections, spawns and regions stay empty.
    /// </summary>
    public static class NetMapGeometry
    {
        /// <summary>The map must already pass NetMapValidation.</summary>
        public static MissionLayout ToLayout(NetMap map)
        {
            if (map == null)
                throw new ArgumentNullException(nameof(map));
            var floor = new bool[map.Width * map.Height];
            foreach (var rect in map.Floors)
                for (var y = rect.yMin; y < rect.yMax; y++)
                    for (var x = rect.xMin; x < rect.xMax; x++)
                        floor[y * map.Width + x] = true;
            var boxes = new List<MissionBox>(map.Boxes.Count);
            for (var i = 0; i < map.Boxes.Count; i++)
            {
                var box = map.Boxes[i];
                boxes.Add(new MissionBox(box.Kind + "_" + (i + 1), box.Kind, box.Footprint, box.Rise));
            }
            return new MissionLayout(0, 0, map.Width, map.Height, floor)
            {
                FloorRects = new List<RectInt>(map.Floors).AsReadOnly(),
                Boxes = boxes.AsReadOnly(),
                UsedTiles = UsedRect(floor, map.Width, map.Height),
            };
        }

        /// <summary>As MissionBuilder places a floor cube.</summary>
        public static Bounds FloorBounds(MissionLayout layout, RectInt rect) =>
            new Bounds(layout.RectCenter(rect) + Vector3.down * (MissionConstants.FloorThickness * 0.5f),
                new Vector3(rect.width, MissionConstants.FloorThickness, rect.height));

        /// <summary>As MissionBuilder places an obstacle cube.</summary>
        public static Bounds BoxBounds(MissionLayout layout, MissionBox box) =>
            new Bounds(layout.RectCenter(box.Footprint) + Vector3.up * (box.Height * 0.5f),
                new Vector3(box.Footprint.width, box.Height, box.Footprint.height));

        /// <summary>As MissionContent places a loot container's collider.</summary>
        public static Bounds ContainerBounds(MissionLayout layout, Vector2Int tile) =>
            new Bounds(layout.TileCenter(tile) + Vector3.up * (MissionContent.LootHeight * 0.5f),
                new Vector3(MissionContent.LootSize, MissionContent.LootHeight, MissionContent.LootSize));

        /// <summary>The generator's used rectangle (MissionGenerator.UsedRect): the floor's bounds plus one tile all round.</summary>
        internal static RectInt UsedRect(bool[] floor, int width, int height)
        {
            int minX = width, minY = height, maxX = -1, maxY = -1;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    if (!floor[y * width + x])
                        continue;
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                }
            }
            return new RectInt(minX - 1, minY - 1, maxX - minX + 3, maxY - minY + 3);
        }
    }
}
