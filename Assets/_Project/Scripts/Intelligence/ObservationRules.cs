using UnityEngine;

namespace Blackglass
{
    /// <summary>Pure geometry for who can see what: all tests are flat (XZ), like the rest of the game's range rules.</summary>
    public static class ObservationRules
    {
        /// <summary>Inclusive.</summary>
        public static bool InCircle(Vector3 centre, float radius, Vector3 point) =>
            CoverRules.FlatDistance(centre, point) <= radius;

        /// <summary>Within `range` of the origin and within `halfAngleDegrees` of `forward` (both flattened). The origin itself is in.</summary>
        public static bool InCone(Vector3 origin, Vector3 forward, float halfAngleDegrees, float range, Vector3 point)
        {
            var offset = point - origin;
            offset.y = 0f;
            if (offset.sqrMagnitude <= 1e-6f)
                return true;
            if (offset.magnitude > range)
                return false;
            forward.y = 0f;
            if (forward.sqrMagnitude <= 1e-6f)
                return false;
            return Vector3.Angle(forward, offset) <= halfAngleDegrees;
        }
    }
}
