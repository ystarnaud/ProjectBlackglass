using UnityEngine;
using UnityEngine.InputSystem;

namespace Blackglass
{
    /// <summary>
    /// Takeover-mode input for the primary character. V toggles takeover. While the primary character is being driven
    /// (PrimaryCharacter.IsDriving), WASD becomes a camera-relative move intent on its CommandableUnit; otherwise the
    /// intent is zero. Contains no movement rules: the unit decides what the intent means.
    /// Release gate: whenever driving starts (resume, or takeover turned on), keys already held are ignored until Move
    /// reads zero, so a key held from panning the camera cannot wipe orders just queued.
    /// </summary>
    [DefaultExecutionOrder(-100)] // set the intent before units update in the same frame
    public sealed class DirectControlInput : MonoBehaviour
    {
        [SerializeField] PrimaryCharacter primary;
        [SerializeField] Camera viewCamera;

        [Header("Input")]
        [SerializeField] InputActionReference moveAction;
        [SerializeField] InputActionReference takeoverAction;

        bool wasDriving;
        bool waitingForRelease;

        internal void Initialize(PrimaryCharacter primaryCharacter, Camera camera, InputActionReference move,
            InputActionReference takeover)
        {
            primary = primaryCharacter;
            viewCamera = camera;
            moveAction = move;
            takeoverAction = takeover;
        }

        /// <summary>Turns movement input into a ground direction relative to the camera's yaw, at most length 1.</summary>
        public static Vector3 ToWorldDirection(Vector2 input, float cameraYawDegrees) =>
            Vector3.ClampMagnitude(Quaternion.Euler(0f, cameraYawDegrees, 0f) * new Vector3(input.x, 0f, input.y), 1f);

        void OnEnable()
        {
            InputActionUtility.SetEnabled(true, moveAction, takeoverAction);
            if (takeoverAction != null)
                takeoverAction.action.performed += OnTakeover;
        }

        void OnDisable()
        {
            if (takeoverAction != null)
                takeoverAction.action.performed -= OnTakeover;
            InputActionUtility.SetEnabled(false, moveAction, takeoverAction);
            wasDriving = false;
            SetIntent(Vector3.zero);
        }

        void Update()
        {
            if (primary == null)
                return;

            var driving = primary.IsDriving;
            var input = InputActionUtility.Read<Vector2>(moveAction);
            if (driving && !wasDriving)
                waitingForRelease = true;
            wasDriving = driving;
            if (waitingForRelease && input == Vector2.zero)
                waitingForRelease = false;

            var steer = driving && !waitingForRelease && viewCamera != null;
            SetIntent(steer ? ToWorldDirection(input, viewCamera.transform.eulerAngles.y) : Vector3.zero);
        }

        void SetIntent(Vector3 direction)
        {
            if (primary != null && primary.Unit != null)
                primary.Unit.SetMoveIntent(direction);
        }

        void OnTakeover(InputAction.CallbackContext context)
        {
            if (primary != null)
                primary.ToggleTakeover();
        }
    }
}
