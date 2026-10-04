using UnityEngine;
using UnityEngine.InputSystem;

namespace Blackglass
{
    /// <summary>
    /// Translates the player's input into requests: a quick left-click becomes a unit command
    /// (attack the clicked target, or move to the clicked point) and Space toggles tactical pause.
    /// Contains no movement or combat rules; the unit decides how to carry out its orders.
    /// </summary>
    public sealed class PlayerCommandInput : MonoBehaviour
    {
        [SerializeField] Camera viewCamera;
        [SerializeField] CommandableUnit controlledUnit;
        [SerializeField] TacticalPause tacticalPause;

        [Header("Input")]
        [SerializeField] InputActionReference commandAction;
        [SerializeField] InputActionReference pointerPositionAction;
        [SerializeField] InputActionReference togglePauseAction;

        [Header("Tuning")]
        // A press that moves further than this is a drag (reserved for selection later), not a command.
        [SerializeField, Min(0f)] float dragThresholdPixels = ClickDragDetector.DefaultThresholdPixels;
        [SerializeField, Min(1f)] float maxClickDistance = 500f;
        [SerializeField] LayerMask clickableLayers = ~0;

        ClickDragDetector clickDetector;

        internal void Initialize(Camera camera, CommandableUnit unit, TacticalPause pause,
            InputActionReference command, InputActionReference pointerPosition, InputActionReference togglePause)
        {
            viewCamera = camera;
            controlledUnit = unit;
            tacticalPause = pause;
            commandAction = command;
            pointerPositionAction = pointerPosition;
            togglePauseAction = togglePause;
        }

        void Awake() => clickDetector = new ClickDragDetector(dragThresholdPixels);

        void OnEnable()
        {
            InputActionUtility.SetEnabled(true, commandAction, pointerPositionAction, togglePauseAction);
            if (commandAction != null)
            {
                commandAction.action.started += OnCommandPressed;
                commandAction.action.canceled += OnCommandReleased;
            }
            if (togglePauseAction != null)
                togglePauseAction.action.performed += OnTogglePause;
        }

        void OnDisable()
        {
            if (commandAction != null)
            {
                commandAction.action.started -= OnCommandPressed;
                commandAction.action.canceled -= OnCommandReleased;
            }
            if (togglePauseAction != null)
                togglePauseAction.action.performed -= OnTogglePause;
            InputActionUtility.SetEnabled(false, commandAction, pointerPositionAction, togglePauseAction);
        }

        void Update()
        {
            if (clickDetector.IsPressed)
                clickDetector.Track(PointerPosition);
        }

        Vector2 PointerPosition => InputActionUtility.Read<Vector2>(pointerPositionAction);

        void OnCommandPressed(InputAction.CallbackContext context) => clickDetector.Press(PointerPosition);

        void OnCommandReleased(InputAction.CallbackContext context)
        {
            var pointer = PointerPosition;
            if (clickDetector.Release(pointer))
                IssueCommandAt(pointer);
        }

        void OnTogglePause(InputAction.CallbackContext context)
        {
            if (tacticalPause != null)
                tacticalPause.Toggle();
        }

        void IssueCommandAt(Vector2 screenPoint)
        {
            if (viewCamera == null || controlledUnit == null)
                return;

            // While paused no physics steps run, so push moved transforms to physics before raycasting.
            Physics.SyncTransforms();
            var ray = viewCamera.ScreenPointToRay(screenPoint);
            if (!Physics.Raycast(ray, out var hit, maxClickDistance, clickableLayers, QueryTriggerInteraction.Ignore))
                return;

            var clickedHealth = hit.collider.GetComponentInParent<Health>();
            controlledUnit.Issue(CommandResolver.Resolve(clickedHealth, hit.point));
        }
    }
}
