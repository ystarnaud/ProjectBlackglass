using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Blackglass
{
    /// <summary>
    /// The controller's tactical cursor: a screen-space pointer moved by the right stick (on unscaled time, so it works
    /// while paused). Each frame it resolves what it is on into a PointerTarget, snapping a little: to a friendly, then a
    /// hostile, then a cover location near the ground point under the cursor. Confirm and Attack read that target in
    /// PlayerCommandInput; the cursor itself issues no orders. It is active whenever a controller is in use and the
    /// camera does not own the right stick (see StickRole). Also holds the "soft target" that NextTarget and
    /// PreviousTarget cycle through living hostiles (only they set it: hovering a hostile or an Attack's fallback does
    /// not), and picks the hostile an Attack should use. While an ability is armed (`Aiming`) it owns the stick whatever
    /// the game is doing, and `SnapTo` narrows what it snaps to. Lives on Systems.
    /// </summary>
    public sealed class TacticalCursor : MonoBehaviour
    {
        static readonly Func<CoverLocation, bool> acceptAny = _ => true;

        [SerializeField] Camera viewCamera;
        [SerializeField] ActiveCharacter activeCharacter;
        [SerializeField] UnitSelection selection;
        [SerializeField] Encounter encounter;
        [SerializeField] CoverRegistry coverRegistry;
        // Optional: without it the cursor behaves as if a controller were in use (tests, scenes without detection).
        [SerializeField] ActiveInputDevice inputDevice;

        [Header("Input")]
        [SerializeField] InputActionReference cursorMoveAction;
        // Owned by TacticalCameraController (which disables it); this component only enables and reads it.
        [SerializeField] InputActionReference cameraModifierAction;
        [SerializeField] InputActionReference nextTargetAction;
        [SerializeField] InputActionReference previousTargetAction;

        [Header("Tuning")]
        [SerializeField, Min(0f)] float speedPixelsPerSecond = 900f;
        [SerializeField, Min(1f)] float responseExponent = 1.5f;
        // Within this distance of the ground point under the cursor, a unit is snapped to.
        [SerializeField, Min(0f)] float unitSnapRadius = 1.2f;
        // Smaller than the unit radius: cover points are 1 m apart, so a larger one would make every ground order
        // beside a wall a cover order.
        [SerializeField, Min(0f)] float coverSnapRadius = 0.75f;
        [SerializeField, Min(1f)] float maxDistance = 500f;
        [SerializeField] LayerMask clickableLayers = ~0;

        Vector2 screenPosition;
        bool hasPosition;
        PointerTarget target;
        Health softTarget;

        public Vector2 ScreenPosition => screenPosition;

        /// <summary>True while an ability is armed: the stick is the cursor's whatever else would own it (see StickRole).</summary>
        public bool Aiming { get; set; }

        /// <summary>
        /// What the cursor snaps to. None: the default (a friendly, then a hostile, then a cover location). Friendly or
        /// Hostile: only that side. Ground: nothing, the exact point under the cursor (an area is aimed freely).
        /// </summary>
        public PointerTargetKind SnapTo { get; set; }

        /// <summary>What the cursor is on as of the last update; None while the cursor is inactive.</summary>
        public PointerTarget Target => target;

        /// <summary>The hostile NextTarget / PreviousTarget last chose, while it is still a valid target.</summary>
        public Health SoftTarget => HostileTargets.IsValid(softTarget) ? softTarget : null;

        public bool IsActive =>
            (inputDevice == null || inputDevice.Family.IsController())
            && !StickRole.CameraOwnsRightStick(activeCharacter, InputActionUtility.IsPressed(cameraModifierAction), Aiming);

        internal void Initialize(Camera camera, ActiveCharacter active, UnitSelection unitSelection,
            Encounter currentEncounter, CoverRegistry registry, ActiveInputDevice device,
            InputActionReference cursorMove, InputActionReference cameraModifier,
            InputActionReference nextTarget, InputActionReference previousTarget)
        {
            viewCamera = camera;
            activeCharacter = active;
            selection = unitSelection;
            encounter = currentEncounter;
            coverRegistry = registry;
            inputDevice = device;
            cursorMoveAction = cursorMove;
            cameraModifierAction = cameraModifier;
            nextTargetAction = nextTarget;
            previousTargetAction = previousTarget;
        }

        void OnEnable()
        {
            InputActionUtility.SetEnabled(true, cursorMoveAction, cameraModifierAction, nextTargetAction, previousTargetAction);
            if (nextTargetAction != null)
                nextTargetAction.action.performed += OnNextTarget;
            if (previousTargetAction != null)
                previousTargetAction.action.performed += OnPreviousTarget;
        }

        void OnDisable()
        {
            if (nextTargetAction != null)
                nextTargetAction.action.performed -= OnNextTarget;
            if (previousTargetAction != null)
                previousTargetAction.action.performed -= OnPreviousTarget;
            InputActionUtility.SetEnabled(false, cursorMoveAction, nextTargetAction, previousTargetAction);
        }

        void Update()
        {
            EnsurePosition();
            if (!IsActive)
            {
                target = PointerTarget.None;
                return;
            }
            var move = StickResponse.Curve(InputActionUtility.Read<Vector2>(cursorMoveAction), responseExponent);
            if (move != Vector2.zero)
                screenPosition = ClampToScreen(screenPosition + move * (speedPixelsPerSecond * Time.unscaledDeltaTime));
            Refresh();
        }

        /// <summary>Re-resolves what the cursor is on right now (None while inactive) and returns it.</summary>
        internal PointerTarget Refresh()
        {
            EnsurePosition();
            target = IsActive ? Resolve(screenPosition) : PointerTarget.None;
            return target;
        }

        internal void SetScreenPosition(Vector2 position)
        {
            screenPosition = ClampToScreen(position);
            hasPosition = true;
            Refresh();
        }

        /// <summary>
        /// The hostile an Attack should use: the one under the cursor (cursor active), else the soft target chosen with
        /// NextTarget / PreviousTarget, else the best living hostile for someone at `origin` facing `facing`. Neither the
        /// hovered hostile nor that fallback is remembered, so an Attack never locks on. Null when no living hostile exists.
        /// </summary>
        public Health PickAttackTarget(Vector3 origin, Vector3 facing)
        {
            if (IsActive && target.Kind == PointerTargetKind.Hostile && HostileTargets.IsValid(target.Hostile))
                return target.Hostile;
            var soft = SoftTarget;
            if (soft != null)
                return soft;
            if (encounter == null)
                return null;
            return HostileTargets.Best(encounter.Hostiles, origin, facing);
        }

        void OnNextTarget(InputAction.CallbackContext context) => CycleTarget(1);

        void OnPreviousTarget(InputAction.CallbackContext context) => CycleTarget(-1);

        internal void CycleTarget(int direction)
        {
            if (encounter == null)
                return;
            if (SnapTo == PointerTargetKind.Friendly)
            {
                CycleFriendly(direction);
                return;
            }
            var next = HostileTargets.Cycle(encounter.Hostiles, CycleOrigin(), SoftTarget, direction);
            if (next == null)
                return;
            softTarget = next;
            MoveOnto(next);
        }

        // Aiming at a friend (a heal): walk the living friendlies by distance and put the cursor on the next one. The
        // soft target stays hostile-only, so an Attack can never pick a friendly.
        void CycleFriendly(int direction)
        {
            var current = target.Kind == PointerTargetKind.Friendly && target.Friendly != null
                ? target.Friendly.GetComponent<Health>()
                : null;
            var next = HostileTargets.Cycle(encounter.Friendlies, CycleOrigin(), current, direction);
            if (next != null)
                MoveOnto(next);
        }

        void MoveOnto(Health unit)
        {
            if (!IsActive || viewCamera == null)
                return;
            var screen = viewCamera.WorldToScreenPoint(unit.transform.position);
            if (screen.z > 0f)
                SetScreenPosition(new Vector2(screen.x, screen.y));
        }

        // Targets are ordered by distance from the character being played, or from the camera when there is none.
        Vector3 CycleOrigin()
        {
            if (activeCharacter != null && activeCharacter.HasUnit)
                return activeCharacter.Unit.transform.position;
            return viewCamera != null ? viewCamera.transform.position : Vector3.zero;
        }

        PointerTarget Resolve(Vector2 point)
        {
            // Raw: units and ground exactly under the cursor. Snapping is added below, around the ground point.
            var raw = PointerTargetResolver.Resolve(viewCamera, point, maxDistance, clickableLayers, null, 0f);
            if (raw.Kind != PointerTargetKind.Ground)
                return raw;

            var ground = raw.Point;
            switch (SnapTo)
            {
                case PointerTargetKind.Ground:
                    return raw;
                case PointerTargetKind.Friendly:
                {
                    var onlyFriendly = NearestFriendly(ground);
                    return onlyFriendly != null ? PointerTarget.OnFriendly(onlyFriendly, ground) : raw;
                }
                case PointerTargetKind.Hostile:
                {
                    var onlyHostile = NearestHostile(ground);
                    return onlyHostile != null ? PointerTarget.OnHostile(onlyHostile, ground) : raw;
                }
            }

            var friendly = NearestFriendly(ground);
            if (friendly != null)
                return PointerTarget.OnFriendly(friendly, ground);
            var hostile = NearestHostile(ground);
            if (hostile != null)
                return PointerTarget.OnHostile(hostile, ground);
            if (coverRegistry != null && CoverRules.TryChooseNearest(coverRegistry.Points, ground, coverSnapRadius, acceptAny, out var cover))
                return PointerTarget.OnCover(cover, ground);
            return raw;
        }

        SelectableUnit NearestFriendly(Vector3 point)
        {
            if (selection == null)
                return null;
            SelectableUnit best = null;
            var bestDistance = unitSnapRadius;
            foreach (var unit in selection.Roster)
            {
                if (unit == null || !unit.isActiveAndEnabled)
                    continue;
                var distance = CoverRules.FlatDistance(point, unit.transform.position);
                if (distance > bestDistance)
                    continue;
                best = unit;
                bestDistance = distance;
            }
            return best;
        }

        Health NearestHostile(Vector3 point)
        {
            if (encounter == null)
                return null;
            Health best = null;
            var bestDistance = unitSnapRadius;
            foreach (var hostile in encounter.Hostiles)
            {
                if (!HostileTargets.IsValid(hostile))
                    continue;
                var distance = CoverRules.FlatDistance(point, hostile.transform.position);
                if (distance > bestDistance)
                    continue;
                best = hostile;
                bestDistance = distance;
            }
            return best;
        }

        void EnsurePosition()
        {
            if (hasPosition || viewCamera == null)
                return;
            screenPosition = new Vector2(viewCamera.pixelWidth * 0.5f, viewCamera.pixelHeight * 0.5f);
            hasPosition = true;
        }

        Vector2 ClampToScreen(Vector2 position)
        {
            if (viewCamera == null)
                return position;
            return new Vector2(Mathf.Clamp(position.x, 0f, viewCamera.pixelWidth), Mathf.Clamp(position.y, 0f, viewCamera.pixelHeight));
        }
    }
}
