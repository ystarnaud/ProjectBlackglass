using UnityEngine;

namespace Blackglass
{
    /// <summary>Marks a collider (or everything below it) as a kind of surface for impact effects. Presentation data only; nothing in gameplay reads it.</summary>
    [DisallowMultipleComponent]
    public sealed class ImpactSurface : MonoBehaviour
    {
        [SerializeField] ImpactSurfaceKind kind = ImpactSurfaceKind.Default;

        public ImpactSurfaceKind Kind => kind;

        public void Set(ImpactSurfaceKind surface) => kind = surface;
    }

    public static class ImpactSurfaces
    {
        /// <summary>The surface of a hit collider: its nearest ImpactSurface, else Character when it belongs to a unit (Health), else Default.</summary>
        public static ImpactSurfaceKind Classify(Collider collider)
        {
            if (collider == null)
                return ImpactSurfaceKind.Default;
            var surface = collider.GetComponentInParent<ImpactSurface>();
            if (surface != null)
                return surface.Kind;
            return collider.GetComponentInParent<Health>() != null ? ImpactSurfaceKind.Character : ImpactSurfaceKind.Default;
        }

        /// <summary>Generated structure is concrete; low walls and crates are metal.</summary>
        public static ImpactSurfaceKind ForBox(MissionBoxKind kind)
        {
            switch (kind)
            {
                case MissionBoxKind.Wall:
                case MissionBoxKind.Baffle:
                case MissionBoxKind.Pillar:
                    return ImpactSurfaceKind.Concrete;
                case MissionBoxKind.LowWall:
                case MissionBoxKind.Crate:
                    return ImpactSurfaceKind.Metal;
                default:
                    return ImpactSurfaceKind.Default;
            }
        }
    }
}
