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
        Interactable,
    }

    /// <summary>
    /// What a pointer (the mouse, or the controller's tactical cursor) is on: nothing, a friendly unit, a living
    /// hostile, a cover location, an available interactable (a terminal) or plain ground, plus the world point. Plain data; input turns it into a selection
    /// change or an order and never decides anything else.
    /// </summary>
    public readonly struct PointerTarget
    {
        PointerTarget(PointerTargetKind kind, Vector3 point, SelectableUnit friendly, Health hostile, CoverLocation cover,
            MissionInteractable interactable)
        {
            Kind = kind;
            Point = point;
            Friendly = friendly;
            Hostile = hostile;
            Cover = cover;
            Interactable = interactable;
        }

        public PointerTargetKind Kind { get; }
        public Vector3 Point { get; }
        public SelectableUnit Friendly { get; }
        public Health Hostile { get; }
        public CoverLocation Cover { get; }
        public MissionInteractable Interactable { get; }

        public static PointerTarget None => default;

        public static PointerTarget OnFriendly(SelectableUnit unit, Vector3 point) =>
            new PointerTarget(PointerTargetKind.Friendly, point, unit, null, null, null);

        public static PointerTarget OnHostile(Health hostile, Vector3 point) =>
            new PointerTarget(PointerTargetKind.Hostile, point, null, hostile, null, null);

        public static PointerTarget OnCover(CoverLocation cover, Vector3 point) =>
            new PointerTarget(PointerTargetKind.Cover, point, null, null, cover, null);

        public static PointerTarget OnGround(Vector3 point) =>
            new PointerTarget(PointerTargetKind.Ground, point, null, null, null, null);

        public static PointerTarget OnInteractable(MissionInteractable interactable, Vector3 point) =>
            new PointerTarget(PointerTargetKind.Interactable, point, null, null, null, interactable);
    }
}
