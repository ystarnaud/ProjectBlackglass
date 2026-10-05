using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The one line-of-sight test for combat: a ray from an eye to a point, blocked by any collider that is not part of
    /// a unit (anything with a Health in its parents never blocks sight). UnitAttacker uses it for ranged attacks and
    /// firing positions, EnemyAI for acquisition. Static, stateless, allocation-free with a caller-owned buffer.
    /// </summary>
    public static class LineOfSight
    {
        /// <summary>Eye height above a unit's pivot (the capsule centre, 1 m up), so the eye is 1.5 m above the ground.</summary>
        public const float EyeHeight = 0.5f;
        /// <summary>Enough for the few units and walls a sight line can cross in the prototype.</summary>
        public const int HitBufferSize = 8;

        /// <summary>True when nothing but units lies between the eye and the point. Triggers are ignored.</summary>
        public static bool IsClear(Vector3 eye, Vector3 point, LayerMask blockers, RaycastHit[] buffer)
        {
            var toPoint = point - eye;
            var distance = toPoint.magnitude;
            if (distance <= 0.001f)
                return true;
            var count = Physics.RaycastNonAlloc(eye, toPoint / distance, buffer, distance, blockers, QueryTriggerInteraction.Ignore);
            for (var i = 0; i < count; i++)
            {
                if (buffer[i].collider.GetComponentInParent<Health>() == null)
                    return false;
            }
            return true;
        }

        /// <summary>IsClear from the eye above a unit pivot to the target's pivot.</summary>
        public static bool IsClear(Vector3 pivot, Health target, LayerMask blockers, RaycastHit[] buffer) =>
            IsClear(pivot + Vector3.up * EyeHeight, target.transform.position, blockers, buffer);
    }
}
