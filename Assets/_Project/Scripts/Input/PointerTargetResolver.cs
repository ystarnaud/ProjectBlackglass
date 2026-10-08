using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Turns a screen point into a PointerTarget: a friendly unit, a living hostile, an available interactable (a
    /// terminal), a cover location within coverRadius of the hit point, or plain ground. The one place a pointer is classified, so the mouse click and the
    /// controller cursor agree. Raycasts the physics scene; while paused no physics step runs, so moved transforms are
    /// pushed to physics first. With an IntelligenceService whose fog is on, the ray passes through what the player cannot
    /// see (an unobserved hostile's collider, an unknown terminal) and cover in unknown regions is not offered, so a click
    /// can never confirm something the player has not found (decision 037).
    /// </summary>
    public static class PointerTargetResolver
    {
        static readonly Func<CoverLocation, bool> acceptAny = _ => true;

        public static PointerTarget Resolve(Camera camera, Vector2 screenPoint, float maxDistance, LayerMask layers,
            CoverRegistry registry, float coverRadius) =>
            Resolve(camera, screenPoint, maxDistance, layers, registry, coverRadius, null);

        public static PointerTarget Resolve(Camera camera, Vector2 screenPoint, float maxDistance, LayerMask layers,
            CoverRegistry registry, float coverRadius, IntelligenceService intelligence)
        {
            if (camera == null)
                return PointerTarget.None;

            Physics.SyncTransforms();
            var ray = camera.ScreenPointToRay(screenPoint);
            var gated = intelligence != null && intelligence.IsFogActive;
            RaycastHit hit;
            if (!gated)
            {
                if (!Physics.Raycast(ray, out hit, maxDistance, layers, QueryTriggerInteraction.Ignore))
                    return PointerTarget.None;
            }
            else if (!TryFirstKnownHit(ray, maxDistance, layers, intelligence, out hit))
            {
                return PointerTarget.None;
            }

            var friendly = hit.collider.GetComponentInParent<SelectableUnit>();
            if (friendly != null)
                return PointerTarget.OnFriendly(friendly, hit.point);

            var health = hit.collider.GetComponentInParent<Health>();
            if (health != null && health.IsAlive)
                return PointerTarget.OnHostile(health, hit.point);

            var interactable = hit.collider.GetComponentInParent<MissionInteractable>();
            if (interactable != null && interactable.IsAvailable)
                return PointerTarget.OnInteractable(interactable, hit.point);

            if (registry != null && coverRadius > 0f
                && CoverRules.TryChooseNearest(registry.Points, hit.point, coverRadius, gated ? intelligence.CanSeeCover : acceptAny, out var cover))
                return PointerTarget.OnCover(cover, hit.point);

            return PointerTarget.OnGround(hit.point);
        }

        // The first collider along the ray that is not hidden from the player. Only used while fog is on, so the per-call
        // allocation of RaycastAll costs nothing in the usual case.
        static bool TryFirstKnownHit(Ray ray, float maxDistance, LayerMask layers, IntelligenceService intelligence, out RaycastHit first)
        {
            var all = Physics.RaycastAll(ray, maxDistance, layers, QueryTriggerInteraction.Ignore);
            Array.Sort(all, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var hit in all)
            {
                if (IsHidden(hit.collider, intelligence))
                    continue;
                first = hit;
                return true;
            }
            first = default;
            return false;
        }

        // A hostile (alive, unit on the other side) the player has not observed, or a terminal the player has not found.
        static bool IsHidden(Collider collider, IntelligenceService intelligence)
        {
            if (collider.GetComponentInParent<SelectableUnit>() != null)
                return false;
            var health = collider.GetComponentInParent<Health>();
            if (health != null && health.IsAlive)
                return !intelligence.CanTarget(health);
            var interactable = collider.GetComponentInParent<MissionInteractable>();
            return interactable != null && interactable.IsAvailable && !intelligence.CanInteract(interactable);
        }
    }
}
