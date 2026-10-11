using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>What the player's knowledge says about a world point: not in any region, in a region that is not observed, or observed.</summary>
    public enum PointVisibility
    {
        /// <summary>Not inside any region (a wall, the void): neutral for path tests, never enough to show an impact.</summary>
        Outside,
        Hidden,
        Visible,
    }

    /// <summary>Which parts of a shot's presentation the player may see (sounds follow the same flags as their visuals).</summary>
    public readonly struct ShotPermissions
    {
        public ShotPermissions(bool muzzle, bool tracer, bool impact)
        {
            Muzzle = muzzle;
            Tracer = tracer;
            Impact = impact;
        }

        /// <summary>Muzzle flash, smoke, light, firing sound and recoil.</summary>
        public bool Muzzle { get; }
        public bool Tracer { get; }
        /// <summary>The impact effect and its sound.</summary>
        public bool Impact { get; }
    }

    /// <summary>
    /// Pure rules for what combat presentation may show under the knowledge model (decision 037). The caller supplies
    /// the point query (the intelligence service, or "everything visible" without one). A missing answer means do not show.
    /// </summary>
    public static class PresentationVisibility
    {
        /// <summary>A tracer is sampled this often along its path.</summary>
        public const float PathSampleSpacing = 3f;

        public static bool AllowsPoint(Func<Vector3, PointVisibility> query, Vector3 point) =>
            query != null && query(point) == PointVisibility.Visible;

        /// <summary>True when no sample from `from` to `to` (inclusive, every PathSampleSpacing metres) is hidden ground.</summary>
        public static bool AllowsPath(Func<Vector3, PointVisibility> query, Vector3 from, Vector3 to)
        {
            if (query == null)
                return false;
            var steps = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(from, to) / PathSampleSpacing));
            for (var i = 0; i <= steps; i++)
            {
                if (query(Vector3.Lerp(from, to, i / (float)steps)) == PointVisibility.Hidden)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Muzzle effects need an observed shooter. A tracer needs that plus a path with no hidden ground. An impact on a
        /// character needs the victim observed; an impact on the world needs a visible point (the caller offsets it off the surface).
        /// </summary>
        public static ShotPermissions EvaluateShot(bool shooterObserved, bool victimObserved, Func<Vector3, PointVisibility> query,
            Vector3 muzzle, bool hasTracer, Vector3 tracerEnd, bool hasImpact, Vector3 impactPoint, bool impactOnCharacter)
        {
            var tracer = shooterObserved && hasTracer && AllowsPath(query, muzzle, tracerEnd);
            var impact = hasImpact && (impactOnCharacter ? victimObserved : AllowsPoint(query, impactPoint));
            return new ShotPermissions(shooterObserved, tracer, impact);
        }
    }
}
