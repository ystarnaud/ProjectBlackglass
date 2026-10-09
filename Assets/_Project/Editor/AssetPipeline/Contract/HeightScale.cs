#nullable disable
using System;

namespace Blackglass.AssetPipeline
{
    public struct ScaleResult
    {
        public float scale;
        public string source;
        public string error;
        public bool Ok { get { return string.IsNullOrEmpty(error); } }
    }

    /// <summary>The one rule for the visual scale of a character. Applied to the visual hierarchy only, never to a gameplay root.</summary>
    public static class HeightScale
    {
        /// <summary>Below this a measurement is treated as "no geometry" and cannot drive a scale.</summary>
        public const float MinMeasuredHeight = 0.01f;

        public static ScaleResult Compute(float measuredHeight, float targetHeight, bool normalize, float manualOverride)
        {
            if (float.IsNaN(manualOverride) || float.IsInfinity(manualOverride) || manualOverride < 0f)
                return Fail("The manual scale must be a positive number (or 0 for none).");
            if (manualOverride > 0f)
                return new ScaleResult { scale = manualOverride, source = ScaleSources.Manual };
            if (!normalize)
                return new ScaleResult { scale = 1f, source = ScaleSources.None };
            if (float.IsNaN(targetHeight) || float.IsInfinity(targetHeight) || targetHeight <= 0f)
                return Fail("The target height must be greater than 0.");
            if (float.IsNaN(measuredHeight) || float.IsInfinity(measuredHeight) || measuredHeight < MinMeasuredHeight)
                return Fail("The model's measured height is unusable (" + measuredHeight + " m): it may have no meshes. Set a manual scale instead.");
            return new ScaleResult { scale = targetHeight / measuredHeight, source = ScaleSources.Auto };
        }

        static ScaleResult Fail(string message)
        {
            return new ScaleResult { scale = 1f, source = ScaleSources.None, error = message };
        }
    }
}
