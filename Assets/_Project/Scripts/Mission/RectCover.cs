using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Covers the true tiles of a mask with rectangles, deterministically. <see cref="Decompose"/> gives non-overlapping
    /// rectangles (the floor); <see cref="MaximalRuns"/> gives straight runs that overlap where they cross (the walls).
    /// </summary>
    internal static class RectCover
    {
        /// <summary>
        /// Covers the true tiles with every maximal straight run of length 2 or more, horizontal and vertical, so a
        /// straight edge of the mask is always the side of one rectangle that ends where the mask ends (decision 032). A
        /// tile where two runs cross (an L or T junction) is covered by both. A true tile in no run (no true neighbour
        /// along x or y) is a 1 x 1 rectangle. Order: horizontal runs (rows from y = 0, then x), vertical runs (by start
        /// tile, rows from y = 0, then x), then single tiles. Meant for masks one tile thick, like the wall ring; a
        /// thicker block is still covered exactly, but by parallel runs stacked over each other.
        /// </summary>
        public static List<RectInt> MaximalRuns(bool[] mask, int width, int height)
        {
            var inRun = new bool[mask.Length];
            var result = new List<RectInt>();
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    if (!mask[y * width + x] || (x > 0 && mask[y * width + x - 1]))
                        continue;
                    var run = 1;
                    while (x + run < width && mask[y * width + x + run])
                        run++;
                    if (run < 2)
                        continue;
                    for (var dx = 0; dx < run; dx++)
                        inRun[y * width + x + dx] = true;
                    result.Add(new RectInt(x, y, run, 1));
                }
            }
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    if (!mask[y * width + x] || (y > 0 && mask[(y - 1) * width + x]))
                        continue;
                    var run = 1;
                    while (y + run < height && mask[(y + run) * width + x])
                        run++;
                    if (run < 2)
                        continue;
                    for (var dy = 0; dy < run; dy++)
                        inRun[(y + dy) * width + x] = true;
                    result.Add(new RectInt(x, y, 1, run));
                }
            }
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    if (mask[y * width + x] && !inRun[y * width + x])
                        result.Add(new RectInt(x, y, 1, 1));
            return result;
        }

        /// <summary>
        /// Covers the true tiles with non-overlapping rectangles: scan rows from y = 0 and columns from x = 0; each
        /// uncovered tile starts a rectangle that grows right as far as it can, then up while the whole row segment is
        /// still free.
        /// </summary>
        public static List<RectInt> Decompose(bool[] mask, int width, int height)
        {
            var used = new bool[mask.Length];
            var result = new List<RectInt>();
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var index = y * width + x;
                    if (!mask[index] || used[index])
                        continue;
                    var run = 1;
                    while (x + run < width && mask[index + run] && !used[index + run])
                        run++;
                    var rows = 1;
                    while (y + rows < height && RowFree(mask, used, width, x, y + rows, run))
                        rows++;
                    for (var dy = 0; dy < rows; dy++)
                        for (var dx = 0; dx < run; dx++)
                            used[(y + dy) * width + x + dx] = true;
                    result.Add(new RectInt(x, y, run, rows));
                }
            }
            return result;
        }

        static bool RowFree(bool[] mask, bool[] used, int width, int x, int y, int run)
        {
            for (var dx = 0; dx < run; dx++)
            {
                var index = y * width + x + dx;
                if (!mask[index] || used[index])
                    return false;
            }
            return true;
        }
    }
}
