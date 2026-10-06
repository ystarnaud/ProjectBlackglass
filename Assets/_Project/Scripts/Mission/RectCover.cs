using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Covers the true tiles of a mask with non-overlapping rectangles, deterministically: scan rows from y = 0 and
    /// columns from x = 0; each uncovered tile starts a rectangle that grows right as far as it can, then up while
    /// the whole row segment is still free.
    /// </summary>
    internal static class RectCover
    {
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
