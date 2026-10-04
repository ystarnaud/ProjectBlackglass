using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Destination offsets for a group move, so units don't all aim for the same point. Index 0 is the clicked point;
    /// the rest fill rings of a hexagonal lattice around it, so no two offsets are closer than the spacing.
    /// Not a formation: no facing, no slot matching.
    /// </summary>
    public static class GroupMoveOffsets
    {
        public static Vector3[] Compute(int count, float spacing)
        {
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count), count, "Count cannot be negative.");
            if (spacing <= 0f)
                throw new ArgumentOutOfRangeException(nameof(spacing), spacing, "Spacing must be positive.");

            var offsets = new Vector3[count];
            var index = 1;
            for (var ring = 1; index < count; ring++)
            {
                // Ring r has six corners at distance r * spacing and r - 1 lattice points along each edge between them.
                for (var side = 0; side < 6 && index < count; side++)
                {
                    var corner = Corner(side, ring, spacing);
                    var nextCorner = Corner(side + 1, ring, spacing);
                    for (var step = 0; step < ring && index < count; step++)
                        offsets[index++] = Vector3.Lerp(corner, nextCorner, step / (float)ring);
                }
            }
            return offsets;
        }

        static Vector3 Corner(int side, int ring, float spacing)
        {
            var angle = side * 60f * Mathf.Deg2Rad;
            return new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * (ring * spacing);
        }
    }
}
