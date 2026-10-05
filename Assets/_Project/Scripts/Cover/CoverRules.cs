using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Pure cover rules shared by UnitAttacker (hit resolution), PlayerCommandInput (cover clicks), EnemyAI (cover
    /// search) and UnitCover (automatic occupancy). Static, stateless, EditMode-tested.
    /// </summary>
    public static class CoverRules
    {
        /// <summary>A shot lands unless the target is protected and the roll (0..1) is at or above the hit chance.</summary>
        public static bool ResolveHit(bool isProtected, float hitChance, float roll) => !isProtected || roll < hitChance;

        /// <summary>
        /// The nearest point (flat distance from `from`, within `maxDistance`) that `accept` admits, or false. A
        /// candidate that is null or invalid (retired, or its obstacle gone) is skipped before its position is read, and
        /// one out of range or not nearer than the best accepted so far is skipped before `accept` is called (as
        /// EnemyAI.FindTarget does), so the predicate runs only on valid points that could win; callers still order
        /// it cheap to dear.
        /// </summary>
        public static bool TryChooseNearest(IReadOnlyList<CoverLocation> points, Vector3 from, float maxDistance,
            Func<CoverLocation, bool> accept, out CoverLocation chosen)
        {
            if (points == null)
                throw new ArgumentNullException(nameof(points));
            if (accept == null)
                throw new ArgumentNullException(nameof(accept));

            chosen = null;
            var bestDistance = float.PositiveInfinity;
            for (var i = 0; i < points.Count; i++)
            {
                var point = points[i];
                if (point == null || !point.IsValid)
                    continue;
                var distance = FlatDistance(from, point.Position);
                if (distance > maxDistance || distance >= bestDistance || !accept(point))
                    continue;
                chosen = point;
                bestDistance = distance;
            }
            return chosen != null;
        }

        /// <summary>Distance ignoring height: units stand at pivot height, points at ground level.</summary>
        public static float FlatDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }
    }
}
