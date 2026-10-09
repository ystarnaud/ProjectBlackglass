using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Serialization;

namespace Blackglass
{
    /// <summary>
    /// Direct-control input for the active character. V (or the controller's control toggle) toggles takeover. Tab / RB
    /// makes the next eligible friendly the active character and selects it; Shift+Tab / LB the previous one. While the
    /// active character is being driven (ActiveCharacter.IsDriving), WASD or the left stick (analog magnitude kept)
    /// becomes a camera-relative move intent on its CommandableUnit; otherwise the intent is zero. F / D-pad up toggles
    /// the follow flag on ActiveCharacter. Contains no movement or follow rules: the unit
    /// decides what the intent means, and CompanionAI what the flag means.
    /// Release gate: whenever driving starts (resume, or takeover turned on), keys already held are ignored until Move
    /// reads zero, so a key held from panning the camera cannot wipe orders just queued.
    /// Handover: when the active character changes, the old one's intent is zeroed in the same frame. A held key
    /// carries over to the new one only if it has no orders; otherwise the gate re-arms. Switching never touches orders.
    /// </summary>
    [DefaultExecutionOrder(-100)] // set the intent before units update in the same frame
    public sealed class DirectControlInput : MonoBehaviour
    {
        [SerializeField, FormerlySerializedAs("primary")] ActiveCharacter activeCharacter;
        [SerializeField] Camera viewCamera;
        // Tab selects the new active character here.
        [SerializeField] UnitSelection selection;

        [Header("Input")]
        [SerializeField] InputActionReference moveAction;
        [SerializeField] InputActionReference takeoverAction;
        [SerializeField] InputActionReference cycleAction;
        // Held while cycling to go backward (Shift).
        [SerializeField] InputActionReference reverseAction;
        [SerializeField] InputActionReference toggleFollowAction;
        // Plain press: the previous character (the gamepad's counterpart of Shift+Tab).
        [SerializeField] InputActionReference previousAction;

        CommandableUnit steeredUnit;
        bool wasDriving;
        bool waitingForRelease;

        /// <summary>True when the scene wired the follow-toggle action.</summary>
        internal bool IsFollowWired => toggleFollowAction != null;

        internal void Initialize(ActiveCharacter active, Camera camera, InputActionReference move,
            InputActionReference takeover, UnitSelection unitSelection = null, InputActionReference cycle = null,
            InputActionReference reverse = null, InputActionReference toggleFollow = null,
            InputActionReference previous = null)
        {
            activeCharacter = active;
            viewCamera = camera;
            moveAction = move;
            takeoverAction = takeover;
            selection = unitSelection;
            cycleAction = cycle;
            reverseAction = reverse;
            toggleFollowAction = toggleFollow;
            previousAction = previous;
        }

        /// <summary>Turns movement input into a ground direction relative to the camera's yaw, at most length 1.</summary>
        public static Vector3 ToWorldDirection(Vector2 input, float cameraYawDegrees) =>
            Vector3.ClampMagnitude(Quaternion.Euler(0f, cameraYawDegrees, 0f) * new Vector3(input.x, 0f, input.y), 1f);

        void OnEnable()
        {
            InputActionUtility.SetEnabled(true, moveAction, takeoverAction, cycleAction, reverseAction, toggleFollowAction,
                previousAction);
            if (takeoverAction != null)
                takeoverAction.action.performed += OnTakeover;
            if (cycleAction != null)
                cycleAction.action.performed += OnCycle;
            if (previousAction != null)
                previousAction.action.performed += OnPrevious;
            if (toggleFollowAction != null)
                toggleFollowAction.action.performed += OnToggleFollow;
        }

        void OnDisable()
        {
            if (takeoverAction != null)
                takeoverAction.action.performed -= OnTakeover;
            if (cycleAction != null)
                cycleAction.action.performed -= OnCycle;
            if (previousAction != null)
                previousAction.action.performed -= OnPrevious;
            if (toggleFollowAction != null)
                toggleFollowAction.action.performed -= OnToggleFollow;
            InputActionUtility.SetEnabled(false, moveAction, takeoverAction, cycleAction, reverseAction, toggleFollowAction,
                previousAction);
            wasDriving = false;
            SetIntent(Vector3.zero);
        }

        void Update()
        {
            if (activeCharacter == null)
                return;

            var input = InputActionUtility.Read<Vector2>(moveAction);
            var unit = activeCharacter.Unit;
            if (unit != steeredUnit)
                HandOver(unit, input);

            var driving = activeCharacter.IsDriving;
            if (driving && !wasDriving)
                waitingForRelease = true;
            wasDriving = driving;
            if (waitingForRelease && input == Vector2.zero)
                waitingForRelease = false;

            var steer = driving && !waitingForRelease && viewCamera != null;
            SetIntent(steer ? ToWorldDirection(input, viewCamera.transform.eulerAngles.y) : Vector3.zero);
        }

        // The active character changed: release the old one now, and let a held key carry over only to an idle unit.
        void HandOver(CommandableUnit unit, Vector2 input)
        {
            if (steeredUnit != null)
                steeredUnit.SetMoveIntent(Vector3.zero);
            steeredUnit = unit;
            if (input != Vector2.zero && unit != null && unit.CurrentCommand != null)
                waitingForRelease = true;
        }

        void SetIntent(Vector3 direction)
        {
            if (steeredUnit != null)
                steeredUnit.SetMoveIntent(direction);
        }

        void OnTakeover(InputAction.CallbackContext context)
        {
            if (activeCharacter != null)
                activeCharacter.ToggleTakeover();
        }

        void OnToggleFollow(InputAction.CallbackContext context)
        {
            if (activeCharacter != null)
                activeCharacter.ToggleFollow();
        }

        void OnCycle(InputAction.CallbackContext context) =>
            CycleCharacter(InputActionUtility.IsPressed(reverseAction) ? -1 : 1);

        void OnPrevious(InputAction.CallbackContext context) => CycleCharacter(-1);

        void CycleCharacter(int direction)
        {
            if (activeCharacter == null)
                return;
            activeCharacter.Cycle(direction);
            activeCharacter.SelectActiveIn(selection);
        }
    }
}
