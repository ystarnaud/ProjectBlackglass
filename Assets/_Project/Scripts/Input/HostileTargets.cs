using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Pure target rules for the controller: which living hostile an Attack should pick (nearest, favouring the one the
    /// player is facing) and how NextTarget / PreviousTarget walk the living hostiles. Not a lock-on system: it keeps
    /// no state, and a hostile that dies or is destroyed simply stops being a candidate. An optional filter keeps targets
    /// the player cannot see out of every choice (decision 037).
    /// </summary>
    public static class HostileTargets
    {
        // How strongly "behind me" counts against a candidate: a target directly behind scores (1 + this) times its distance.
        const float BehindPenalty = 2f;

        public static bool IsValid(Health hostile) =>
            hostile != null && hostile.IsAlive && hostile.gameObject.activeInHierarchy;

        public static Health Best(IReadOnlyList<Health> hostiles, Vector3 origin, Vector3 facing, Func<Health, bool> accept = null)
        {
            facing.y = 0f;
            var hasFacing = facing.sqrMagnitude > 1e-6f;
            Health best = null;
            var bestScore = float.PositiveInfinity;
            for (var i = 0; i < hostiles.Count; i++)
            {
                var candidate = hostiles[i];
                if (!IsValid(candidate) || (accept != null && !accept(candidate)))
                    continue;
                var offset = candidate.transform.position - origin;
                offset.y = 0f;
                var distance = offset.magnitude;
                var turn = hasFacing && distance > 1e-4f ? Vector3.Angle(facing, offset) / 180f : 0f;
                var score = distance * (1f + BehindPenalty * turn);
                if (score < bestScore)
                {
                    best = candidate;
                    bestScore = score;
                }
            }
            return best;
        }

        public static Health Cycle(IReadOnlyList<Health> hostiles, Vector3 origin, Health current, int direction, Func<Health, bool> accept = null)
        {
            var sorted = new List<(float distance, int order, Health health)>();
            for (var i = 0; i < hostiles.Count; i++)
            {
                if (IsValid(hostiles[i]) && (accept == null || accept(hostiles[i])))
                    sorted.Add((CoverRules.FlatDistance(origin, hostiles[i].transform.position), i, hostiles[i]));
            }
            if (sorted.Count == 0)
                return null;
            sorted.Sort((a, b) => a.distance != b.distance ? a.distance.CompareTo(b.distance) : a.order.CompareTo(b.order));

            var currentIndex = -1;
            for (var i = 0; i < sorted.Count; i++)
            {
                if (sorted[i].health == current)
                {
                    currentIndex = i;
                    break;
                }
            }
            var next = ControlCycle.NextIndex(sorted.Count, currentIndex, direction, _ => true);
            return next < 0 ? null : sorted[next].health;
        }
    }
}
