using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>Where a shot is drawn to and what it hits visually. Cosmetic only.</summary>
    internal readonly struct ShotEndpoint
    {
        public ShotEndpoint(bool hasTracer, Vector3 end, bool hasImpact, Vector3 impactPoint, Vector3 impactNormal, ImpactSurfaceKind surface)
        {
            HasTracer = hasTracer;
            End = end;
            HasImpact = hasImpact;
            ImpactPoint = impactPoint;
            ImpactNormal = impactNormal;
            Surface = surface;
        }

        public bool HasTracer { get; }
        public Vector3 End { get; }
        public bool HasImpact { get; }
        public Vector3 ImpactPoint { get; }
        public Vector3 ImpactNormal { get; }
        public ImpactSurfaceKind Surface { get; }
    }

    /// <summary>
    /// Works out, from a shot gameplay already resolved, where its streak ends and what the impact looks like. It only reads
    /// colliders (units never count as blockers, the LineOfSight convention) and writes nothing; it never decides a hit. A hit is drawn to
    /// where the line first enters the target's own colliders (the recorded aim point when the target is gone or has none), unless a
    /// world blocker is in the way (then the streak stops there and the body impact is
    /// omitted). A cover miss lands on the cover that turned it away, else passes beside the target and carries on to the
    /// first world surface or a cap. Anything that cannot be resolved safely (zero length, NaN) shows nothing.
    /// </summary>
    internal static class ShotGeometry
    {
        public const float MissCap = 30f;
        const float Epsilon = 0.05f;
        // A unit's feet, measured down from the recorded aim point (chest = pivot + BodyHeight, the pivot is 1 m up): 0.1 m above the floor.
        const float AimToFeet = ShotRecord.BodyHeight + 0.9f;

        public static ShotEndpoint Resolve(in ShotRecord record, Vector3 muzzle, CosmeticRandom random)
        {
            var toAim = record.AimPoint - muzzle;
            var distance = toAim.magnitude;
            if (!(distance > 0.1f) || float.IsInfinity(distance))
                return default;
            var direction = toAim / distance;

            if (record.Outcome == ShotOutcome.Hit)
            {
                // The recorded aim point is on the unit's axis, inside its body: the visible hit is where the line first enters
                // the target's own colliders, facing the shooter. A target that is gone or has no collider keeps the aim point.
                var impactPoint = record.AimPoint;
                var impactNormal = -direction;
                var impactDistance = distance;
                if (TryTargetSurface(record.Target, muzzle, direction, distance + Epsilon, out var surface))
                {
                    impactPoint = surface.point;
                    impactNormal = surface.normal;
                    impactDistance = surface.distance;
                }
                else if (TryCapsuleSurface(record.Target, record.AimPoint, direction, out var analytic))
                {
                    impactPoint = analytic;
                    impactDistance = Mathf.Max(0f, (analytic - muzzle).magnitude);
                }
                if (record.IsMelee)
                    return new ShotEndpoint(false, default, true, impactPoint, impactNormal, ImpactSurfaceKind.Character);
                if (TryFirstWorldHit(muzzle, direction, impactDistance, out var blocker) && blocker.distance < impactDistance - Epsilon)
                    return new ShotEndpoint(true, blocker.point, false, default, default, ImpactSurfaceKind.Default);
                return new ShotEndpoint(true, impactPoint, true, impactPoint, impactNormal, ImpactSurfaceKind.Character);
            }

            if (record.IsMelee)
                return default;

            // Cover miss: the cover rule is judged on the line to the target's feet, so look for what that line crosses.
            var toFeet = record.AimPoint - Vector3.up * AimToFeet - muzzle;
            var feetDistance = toFeet.magnitude;
            if (feetDistance > 0.1f && TryFirstWorldHit(muzzle, toFeet / feetDistance, feetDistance, out var cover))
                return new ShotEndpoint(true, cover.point, true, cover.point, cover.normal, ImpactSurfaces.Classify(cover.collider));

            // Open ground: pass beside the target and carry on.
            var side = Vector3.Cross(Vector3.up, direction);
            if (side.sqrMagnitude < 1e-4f)
                side = Vector3.right;
            side.Normalize();
            var deflected = (record.AimPoint + side * (random.Sign() * random.Range(0.6f, 1.2f)) - muzzle).normalized;
            if (TryFirstWorldHit(muzzle, deflected, MissCap, out var beyond))
                return new ShotEndpoint(true, beyond.point, true, beyond.point, beyond.normal, ImpactSurfaces.Classify(beyond.collider));
            return new ShotEndpoint(true, muzzle + deflected * MissCap, false, default, default, ImpactSurfaceKind.Default);
        }

        static readonly List<Collider> TargetColliders = new List<Collider>(8);

        // Where the ray first enters the target's own colliders (a Collider.Raycast on each, nearest wins). False when the target
        // is destroyed or inactive (a killing blow deactivates the unit before the shot is announced), has no enabled solid
        // collider, the ray misses, or the origin is already inside the body.
        static bool TryTargetSurface(Health target, Vector3 origin, Vector3 direction, float maxDistance, out RaycastHit result)
        {
            result = default;
            if (target == null || !target.gameObject.activeInHierarchy)
                return false;
            var ray = new Ray(origin, direction);
            var found = false;
            target.GetComponentsInChildren(false, TargetColliders);
            foreach (var collider in TargetColliders)
            {
                if (collider == null || !collider.enabled || collider.isTrigger)
                    continue;
                if (collider.Raycast(ray, out var hit, maxDistance) && (!found || hit.distance < result.distance))
                {
                    result = hit;
                    found = true;
                }
            }
            TargetColliders.Clear();
            return found;
        }

        // A killing blow deactivates the unit before the shot is announced, so nothing can be raycast; its capsule component still
        // reports radius and transform, though. Steps from the aim point (on the axis) back towards the shooter by the capsule's
        // horizontal radius. Only when raycasting was unavailable (inactive unit or disabled capsule): a live capsule that the ray
        // simply missed keeps the aim point. No capsule component at all, or a destroyed target, also keeps the aim point.
        static bool TryCapsuleSurface(Health target, Vector3 aimPoint, Vector3 direction, out Vector3 point)
        {
            point = default;
            if (target == null)
                return false;
            var capsule = target.GetComponentInChildren<CapsuleCollider>(true);
            if (capsule == null || (target.gameObject.activeInHierarchy && capsule.enabled))
                return false;
            var flat = new Vector3(direction.x, 0f, direction.z);
            if (flat.sqrMagnitude < 1e-4f)
                return false;
            var scale = capsule.transform.lossyScale;
            var radius = capsule.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
            point = aimPoint - flat.normalized * radius;
            return true;
        }

        // The nearest collider that is not part of a unit, skipping units in the way (a few at most).
        static bool TryFirstWorldHit(Vector3 origin, Vector3 direction, float maxDistance, out RaycastHit result)
        {
            var travelled = 0f;
            var from = origin;
            for (var i = 0; i < 4 && travelled < maxDistance; i++)
            {
                if (!Physics.Raycast(from, direction, out var hit, maxDistance - travelled, ~0, QueryTriggerInteraction.Ignore))
                    break;
                if (hit.collider.GetComponentInParent<Health>() == null)
                {
                    hit.distance += travelled;
                    result = hit;
                    return true;
                }
                var step = hit.distance + 0.02f;
                travelled += step;
                from += direction * step;
            }
            result = default;
            return false;
        }
    }
}
