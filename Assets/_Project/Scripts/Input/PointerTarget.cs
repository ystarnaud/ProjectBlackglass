using UnityEngine;

namespace Blackglass
{
    public enum PointerTargetKind
    {
        None,
        Friendly,
        Hostile,
        Cover,
        Ground,
    }

    /// <summary>
    /// What a pointer (the mouse, or the controller's tactical cursor) is on: nothing, a friendly unit, a living
    /// hostile, a cover location or plain ground, plus the world point. Plain data; input turns it into a selection
    /// change or an order and never decides anything else.
    /// </summary>
    public readonly struct PointerTarget
    {
        PointerTarget(PointerTargetKind kind, Vector3 point, SelectableUnit friendly, Health hostile, CoverLocation cover)
        {
            Kind = kind;
            Point = point;
            Friendly = friendly;
            Hostile = hostile;
            Cover = cover;
        }

        public PointerTargetKind Kind { get; }
        public Vector3 Point { get; }
        public SelectableUnit Friendly { get; }
        public Health Hostile { get; }
        public CoverLocation Cover { get; }

        public static PointerTarget None => default;

        public static PointerTarget OnFriendly(SelectableUnit unit, Vector3 point) =>
            new PointerTarget(PointerTargetKind.Friendly, point, unit, null, null);

        public static PointerTarget OnHostile(Health hostile, Vector3 point) =>
            new PointerTarget(PointerTargetKind.Hostile, point, null, hostile, null);

        public static PointerTarget OnCover(CoverLocation cover, Vector3 point) =>
            new PointerTarget(PointerTargetKind.Cover, point, null, null, cover);

        public static PointerTarget OnGround(Vector3 point) =>
            new PointerTarget(PointerTargetKind.Ground, point, null, null, null);
    }
}
