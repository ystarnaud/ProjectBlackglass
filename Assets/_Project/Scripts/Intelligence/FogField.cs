using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The fog of war as a grid of brightness over the mission floor plan: 0 is hidden, 1 is clear, in between is dim. The
    /// presenter paints what the knowledge model allows, feathers it so the edge of the dark is a smooth ramp, and eases the
    /// displayed values toward that target so a reveal sweeps open instead of popping.
    ///
    /// Feathering can only darken: the result at a cell is the lowest of its own level and every other cell's level plus a
    /// slope times the distance to it. Hidden ground therefore stays exactly hidden and the ramp lies entirely on the known
    /// side (decision 037: presentation never shows more than the knowledge model allows). The dark runs across walls like
    /// any other ground, so the top of a wall between a known and a hidden room fades into the black instead of stopping at
    /// a hard edge.
    ///
    /// Pure data with no Unity scene dependency: x and z are world metres.
    /// </summary>
    public sealed class FogField
    {
        const float Diagonal = 1.4142135f;

        readonly float originX;
        readonly float originZ;
        readonly float cellsPerMetre;
        readonly float[] raw;
        readonly float[] target;
        readonly float[] current;
        bool settled = true;

        public int Width { get; }
        public int Height { get; }

        /// <summary>True when the displayed brightness equals the target everywhere.</summary>
        public bool IsSettled => settled;

        /// <param name="world">The area covered; x and y of the Rect are world x and z.</param>
        public FogField(Rect world, float cellsPerMetre)
        {
            this.cellsPerMetre = Mathf.Max(0.01f, cellsPerMetre);
            originX = world.xMin;
            originZ = world.yMin;
            Width = Mathf.Max(1, Mathf.CeilToInt(world.width * this.cellsPerMetre));
            Height = Mathf.Max(1, Mathf.CeilToInt(world.height * this.cellsPerMetre));
            raw = new float[Width * Height];
            target = new float[Width * Height];
            current = new float[Width * Height];
            Clear();
            SnapToTarget();
        }

        /// <summary>Every cell clear. The target is not recomputed until Feather.</summary>
        public void Clear()
        {
            for (var i = 0; i < raw.Length; i++)
            {
                raw[i] = 1f;
            }
        }

        /// <summary>Sets every cell whose centre is inside the rect (clipped to the field) to the level.</summary>
        public void Paint(Rect rect, float level)
        {
            var x0 = Mathf.Max(0, Mathf.CeilToInt((rect.xMin - originX) * cellsPerMetre - 0.5f));
            var x1 = Mathf.Min(Width - 1, Mathf.CeilToInt((rect.xMax - originX) * cellsPerMetre - 0.5f) - 1);
            var z0 = Mathf.Max(0, Mathf.CeilToInt((rect.yMin - originZ) * cellsPerMetre - 0.5f));
            var z1 = Mathf.Min(Height - 1, Mathf.CeilToInt((rect.yMax - originZ) * cellsPerMetre - 0.5f) - 1);
            for (var z = z0; z <= z1; z++)
            {
                for (var x = x0; x <= x1; x++)
                    raw[z * Width + x] = level;
            }
        }

        /// <summary>
        /// Computes the target from the painted levels: brightness rises by 1 per `metres` of distance from darker ground.
        /// With 0 the target is the painted levels.
        /// </summary>
        public void Feather(float metres)
        {
            System.Array.Copy(raw, target, raw.Length);
            if (metres > 0f)
            {
                var step = 1f / cellsPerMetre / metres;
                // Two sweeps each way cover the usual shapes, including a bend through a doorway; the chamfer distance is
                // within a few percent of true distance, which only matters to the look of the ramp.
                for (var iteration = 0; iteration < 2; iteration++)
                {
                    for (var z = 0; z < Height; z++)
                        for (var x = 0; x < Width; x++)
                            Relax(x, z, step, -1);
                    for (var z = Height - 1; z >= 0; z--)
                        for (var x = Width - 1; x >= 0; x--)
                            Relax(x, z, step, 1);
                }
            }
            settled = false;
        }

        // Pulls the cell down toward its four neighbours already visited by this sweep (s is -1 going forward, 1 going back).
        void Relax(int x, int z, float step, int s)
        {
            var index = z * Width + x;
            var best = target[index];
            best = Pull(best, x + s, z, step);
            best = Pull(best, x, z + s, step);
            best = Pull(best, x + s, z + s, step * Diagonal);
            best = Pull(best, x - s, z + s, step * Diagonal);
            target[index] = best;
        }

        float Pull(float best, int x, int z, float cost)
        {
            if (x < 0 || z < 0 || x >= Width || z >= Height)
                return best;
            var candidate = target[z * Width + x] + cost;
            return candidate < best ? candidate : best;
        }

        /// <summary>Makes the displayed brightness equal the target at once.</summary>
        public void SnapToTarget()
        {
            System.Array.Copy(target, current, target.Length);
            settled = true;
        }

        /// <summary>Eases the displayed brightness toward the target; a full 0 to 1 change takes `fadeSeconds`. True if anything changed.</summary>
        public bool Step(float seconds, float fadeSeconds)
        {
            if (settled)
                return false;
            if (fadeSeconds <= 0f)
            {
                SnapToTarget();
                return true;
            }
            var delta = seconds / fadeSeconds;
            var changed = false;
            var done = true;
            for (var i = 0; i < current.Length; i++)
            {
                var difference = target[i] - current[i];
                if (difference == 0f)
                    continue;
                changed = true;
                current[i] = Mathf.Abs(difference) <= delta ? target[i] : current[i] + Mathf.Sign(difference) * delta;
                if (current[i] != target[i])
                    done = false;
            }
            settled = done;
            return changed;
        }

        public float RawAt(float x, float z) => raw[CellAt(x, z)];
        public float TargetAt(float x, float z) => target[CellAt(x, z)];
        public float CurrentAt(float x, float z) => current[CellAt(x, z)];

        /// <summary>Writes the displayed brightness as one byte per cell (row by row from the field's minimum z), for a texture.</summary>
        public void CopyTo(byte[] bytes)
        {
            for (var i = 0; i < current.Length && i < bytes.Length; i++)
                bytes[i] = (byte)(current[i] * 255f + 0.5f);
        }

        int CellAt(float x, float z)
        {
            var cx = Mathf.Clamp(Mathf.FloorToInt((x - originX) * cellsPerMetre), 0, Width - 1);
            var cz = Mathf.Clamp(Mathf.FloorToInt((z - originZ) * cellsPerMetre), 0, Height - 1);
            return cz * Width + cx;
        }
    }
}
