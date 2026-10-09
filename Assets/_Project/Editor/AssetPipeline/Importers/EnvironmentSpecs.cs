using System.Collections.Generic;
using UnityEngine;

namespace Blackglass.AssetPipeline
{
    public enum PivotKind { BottomCenter, TopCenter }

    public sealed class ModuleSpec
    {
        public readonly Vector3 size;
        public readonly PivotKind pivot;
        /// <summary>True for props that may be smaller than the cell (crates): only the upper limit applies.</summary>
        public readonly bool maxOnly;

        public ModuleSpec(Vector3 size, PivotKind pivot, bool maxOnly = false)
        {
            this.size = size;
            this.pivot = pivot;
            this.maxOnly = maxOnly;
        }
    }

    /// <summary>The footprints and pivots of docs/EnvironmentAssetGuide.md section 2, as data. A test pins every element to a spec.</summary>
    public static class EnvironmentSpecs
    {
        public const float SizeTolerance = 0.05f;
        /// <summary>The guide: decoration may stick out at most about 0.12 m beyond the tile footprint.</summary>
        public const float OverhangAllowance = 0.12f;
        public const float PivotTolerance = 0.07f;

        public static ModuleSpec For(EnvironmentElement element)
        {
            switch (element)
            {
                case EnvironmentElement.Floor: return new ModuleSpec(new Vector3(1f, 0.2f, 1f), PivotKind.TopCenter);
                case EnvironmentElement.WallStraight:
                case EnvironmentElement.WallCorner:
                case EnvironmentElement.WallEnd:
                case EnvironmentElement.WallJunction:
                case EnvironmentElement.Pillar: return new ModuleSpec(new Vector3(1f, 3f, 1f), PivotKind.BottomCenter);
                case EnvironmentElement.DoorFrame: return new ModuleSpec(new Vector3(1f, 2.4f, 1f), PivotKind.BottomCenter);
                case EnvironmentElement.LowCover: return new ModuleSpec(new Vector3(1f, 1f, 1f), PivotKind.BottomCenter);
                case EnvironmentElement.LowCoverLong: return new ModuleSpec(new Vector3(2f, 1f, 1f), PivotKind.BottomCenter);
                case EnvironmentElement.Crate: return new ModuleSpec(new Vector3(1f, 1f, 1f), PivotKind.BottomCenter, true);
                case EnvironmentElement.Terminal: return new ModuleSpec(new Vector3(0.8f, 1.2f, 0.8f), PivotKind.BottomCenter);
                default: return null; // LightFixture: a thin prop placed on a wall face; nothing to compare
            }
        }

        /// <summary>Warnings (never errors, never a rescale) for a measured module. size/min/max are in the prefab root's space.</summary>
        public static List<string> Check(EnvironmentElement element, Vector3 size, Vector3 min, Vector3 max)
        {
            var warnings = new List<string>();
            var spec = For(element);
            if (spec == null) return warnings;

            var axes = new[] { "X", "Y", "Z" };
            for (var i = 0; i < 3; i++)
            {
                var expected = spec.size[i];
                var upper = expected + (i == 1 ? SizeTolerance : OverhangAllowance);
                var lower = spec.maxOnly ? 0f : expected - SizeTolerance;
                if (size[i] < lower || size[i] > upper)
                    warnings.Add(axes[i] + " is " + size[i].ToString("0.###") + " m but a " + element + " module should be " + expected.ToString("0.###") +
                                 " m (allowed " + lower.ToString("0.###") + " to " + upper.ToString("0.###") + " m).");
            }

            if (spec.pivot == PivotKind.BottomCenter && Mathf.Abs(min.y) > PivotTolerance)
                warnings.Add("The pivot is not at the bottom: the lowest point is at y = " + min.y.ToString("0.###") + " m (it should be 0).");
            if (spec.pivot == PivotKind.TopCenter && Mathf.Abs(max.y) > PivotTolerance)
                warnings.Add("The pivot is not at the top face: the highest point is at y = " + max.y.ToString("0.###") + " m (it should be 0).");
            var centreX = (min.x + max.x) * 0.5f;
            var centreZ = (min.z + max.z) * 0.5f;
            if (Mathf.Abs(centreX) > PivotTolerance || Mathf.Abs(centreZ) > PivotTolerance)
                warnings.Add("The pivot is not centred on the footprint (centre offset x " + centreX.ToString("0.###") + ", z " + centreZ.ToString("0.###") + " m).");
            return warnings;
        }
    }
}
