using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Turns a screen point into a PointerTarget: a friendly unit, a living hostile, an available interactable (a
    /// terminal), a cover location within coverRadius of the hit point, or plain ground. The one place a pointer is classified, so the mouse click and the
    /// controller cursor agree. Raycasts the physics scene; while paused no physics step runs, so moved transforms are
    /// pushed to physics first.
    /// </summary>
    public static class PointerTargetResolver
    {
        static readonly Func<CoverLocation, bool> acceptAny = _ => true;

        public static PointerTarget Resolve(Camera camera, Vector2 screenPoint, float maxDistance, LayerMask layers,
            CoverRegistry registry, float coverRadius)
        {
            if (camera == null)
                return PointerTarget.None;

            Physics.SyncTransforms();
            var ray = camera.ScreenPointToRay(screenPoint);
            if (!Physics.Raycast(ray, out var hit, maxDistance, layers, QueryTriggerInteraction.Ignore))
                return PointerTarget.None;

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
                && CoverRules.TryChooseNearest(registry.Points, hit.point, coverRadius, acceptAny, out var cover))
                return PointerTarget.OnCover(cover, hit.point);

            return PointerTarget.OnGround(hit.point);
        }
    }
}
