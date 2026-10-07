using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Pure: turns a layout into the visual modules that represent it. Knows gameplay concepts only (floor, wall tiles by how
    /// they join, low cover, crates); knows nothing about prefabs, themes or randomness. Order is fixed (floor, walls by row,
    /// props), so the result is reproducible. See Docs/EnvironmentAssetGuide.md for the yaw convention.
    /// </summary>
    public static class EnvironmentVisualPlanner
    {
        const int LightOneIn = 4;
        const int North = 1, East = 2, South = 4, West = 8;
        // Direction index 0..3 = N, E, S, W; yaw = index * 90.
        static readonly Vector2Int[] Directions = { new Vector2Int(0, 1), new Vector2Int(1, 0), new Vector2Int(0, -1), new Vector2Int(-1, 0) };

        public static List<VisualPlacement> Plan(MissionLayout layout)
        {
            var result = new List<VisualPlacement>();
            PlanFloor(layout, result);
            PlanWalls(layout, result);
            PlanProps(layout, result);
            return result;
        }

        static void PlanFloor(MissionLayout layout, List<VisualPlacement> result)
        {
            for (var y = 0; y < layout.Height; y++)
                for (var x = 0; x < layout.Width; x++)
                    if (layout.IsFloor(x, y))
                        result.Add(new VisualPlacement(EnvironmentElement.Floor, layout.TileCenter(new Vector2Int(x, y)), 0f, new Vector2Int(x, y)));
        }

        static void PlanWalls(MissionLayout layout, List<VisualPlacement> result)
        {
            var width = layout.Width;
            var height = layout.Height;
            var wall = new bool[width * height];
            var ring = new bool[width * height];
            foreach (var box in layout.Boxes)
            {
                if (box.Kind != MissionBoxKind.Wall && box.Kind != MissionBoxKind.Baffle)
                    continue;
                for (var y = box.Footprint.yMin; y < box.Footprint.yMax; y++)
                {
                    for (var x = box.Footprint.xMin; x < box.Footprint.xMax; x++)
                    {
                        wall[y * width + x] = true;
                        if (box.Kind == MissionBoxKind.Wall)
                            ring[y * width + x] = true;
                    }
                }
            }

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    if (!wall[y * width + x])
                        continue;
                    var mask = 0;
                    for (var d = 0; d < 4; d++)
                    {
                        var nx = x + Directions[d].x;
                        var ny = y + Directions[d].y;
                        if (nx >= 0 && ny >= 0 && nx < width && ny < height && wall[ny * width + nx])
                            mask |= 1 << d;
                    }
                    Classify(mask, out var element, out var yaw);
                    var tile = new Vector2Int(x, y);
                    var position = layout.TileCenter(tile);
                    result.Add(new VisualPlacement(element, position, yaw, tile));
                    if (element == EnvironmentElement.WallEnd && ring[y * width + x])
                        result.Add(new VisualPlacement(EnvironmentElement.DoorFrame, position, yaw, tile));
                    if (element == EnvironmentElement.WallStraight && TryLightFacing(layout, tile, yaw, out var lightYaw))
                        result.Add(new VisualPlacement(EnvironmentElement.LightFixture, position, lightYaw, tile));
                }
            }
        }

        static void Classify(int mask, out EnvironmentElement element, out float yaw)
        {
            var count = 0;
            for (var d = 0; d < 4; d++)
                if ((mask & (1 << d)) != 0)
                    count++;
            switch (count)
            {
                case 0:
                    element = EnvironmentElement.Pillar;
                    yaw = 0f;
                    return;
                case 1:
                    element = EnvironmentElement.WallEnd;
                    yaw = IndexOf(mask) * 90f;
                    return;
                case 2:
                    if (mask == (North | South)) { element = EnvironmentElement.WallStraight; yaw = 90f; return; }
                    if (mask == (East | West)) { element = EnvironmentElement.WallStraight; yaw = 0f; return; }
                    element = EnvironmentElement.WallCorner;
                    yaw = mask == (North | East) ? 0f : mask == (East | South) ? 90f : mask == (South | West) ? 180f : 270f;
                    return;
                case 3:
                    element = EnvironmentElement.WallJunction;
                    var missing = IndexOf(~mask & 15);
                    yaw = (missing * 90f + 180f) % 360f;
                    return;
                default:
                    element = EnvironmentElement.WallJunction;
                    yaw = 0f;
                    return;
            }
        }

        static int IndexOf(int singleBit)
        {
            for (var d = 0; d < 4; d++)
                if (singleBit == 1 << d)
                    return d;
            return 0;
        }

        // A straight wall along X (yaw 0) has its sides north and south; along Z (yaw 90) east and west.
        static bool TryLightFacing(MissionLayout layout, Vector2Int tile, float straightYaw, out float lightYaw)
        {
            lightYaw = 0f;
            var sideA = straightYaw == 0f ? 0 : 1;   // N or E
            var sideB = straightYaw == 0f ? 2 : 3;   // S or W
            var floorA = layout.IsFloor(tile.x + Directions[sideA].x, tile.y + Directions[sideA].y);
            var floorB = layout.IsFloor(tile.x + Directions[sideB].x, tile.y + Directions[sideB].y);
            if (floorA == floorB)
                return false;
            if (VisualVariants.Hash(layout.Seed, 0, EnvironmentElement.LightFixture, tile.x, tile.y) % LightOneIn != 0)
                return false;
            lightYaw = (floorA ? sideA : sideB) * 90f;
            return true;
        }

        static void PlanProps(MissionLayout layout, List<VisualPlacement> result)
        {
            foreach (var box in layout.Boxes)
            {
                var rect = box.Footprint;
                switch (box.Kind)
                {
                    case MissionBoxKind.Pillar:
                    case MissionBoxKind.Crate:
                        var element = box.Kind == MissionBoxKind.Pillar ? EnvironmentElement.Pillar : EnvironmentElement.Crate;
                        for (var y = rect.yMin; y < rect.yMax; y++)
                            for (var x = rect.xMin; x < rect.xMax; x++)
                                result.Add(new VisualPlacement(element, layout.TileCenter(new Vector2Int(x, y)), 0f, new Vector2Int(x, y)));
                        break;
                    case MissionBoxKind.LowWall:
                        PlanLowWall(layout, rect, result);
                        break;
                }
            }
        }

        static void PlanLowWall(MissionLayout layout, RectInt rect, List<VisualPlacement> result)
        {
            if (rect.width > 1 && rect.height > 1)
            {
                for (var y = rect.yMin; y < rect.yMax; y++)
                    for (var x = rect.xMin; x < rect.xMax; x++)
                        result.Add(new VisualPlacement(EnvironmentElement.LowCover, layout.TileCenter(new Vector2Int(x, y)), 0f, new Vector2Int(x, y)));
                return;
            }
            var alongX = rect.width >= rect.height;
            var length = alongX ? rect.width : rect.height;
            var yaw = alongX ? 0f : 90f;
            var i = 0;
            for (; i + 2 <= length; i += 2)
            {
                var centre = alongX ? layout.ToWorld(rect.xMin + i + 1f, rect.yMin + 0.5f) : layout.ToWorld(rect.xMin + 0.5f, rect.yMin + i + 1f);
                var tile = alongX ? new Vector2Int(rect.xMin + i, rect.yMin) : new Vector2Int(rect.xMin, rect.yMin + i);
                result.Add(new VisualPlacement(EnvironmentElement.LowCoverLong, centre, yaw, tile));
            }
            for (; i < length; i++)
            {
                var tile = alongX ? new Vector2Int(rect.xMin + i, rect.yMin) : new Vector2Int(rect.xMin, rect.yMin + i);
                result.Add(new VisualPlacement(EnvironmentElement.LowCover, layout.TileCenter(tile), yaw, tile));
            }
        }
    }
}
