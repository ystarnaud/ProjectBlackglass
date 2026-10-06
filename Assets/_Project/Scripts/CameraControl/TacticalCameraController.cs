using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Serialization;

namespace Blackglass
{
    /// <summary>
    /// Simple strategy camera orbiting a pivot on the ground. Runs entirely on unscaled time so it keeps working during
    /// tactical pause. Lives on the pivot; the camera is a child. While the active character is being driven
    /// (takeover, not paused) the pivot follows it and pan input is ignored; otherwise WASD pans freely. When the active
    /// character changes, the pivot glides once to where the new one stands; any pan input ends the glide.
    /// A controller's right stick looks around while driving (and, with the camera modifier held, while planning); the
    /// left stick pans; the stick clicks zoom.
    /// </summary>
    public sealed class TacticalCameraController : MonoBehaviour
    {
        // A focus glide ends once the pivot is this close to its target.
        const float FocusArrivalDistance = 0.05f;

        [SerializeField] Camera viewCamera;
        [SerializeField, FormerlySerializedAs("primary")] ActiveCharacter activeCharacter;

        [Header("Input")]
        [SerializeField] InputActionReference panAction;
        [SerializeField] InputActionReference rotateAction;
        [SerializeField] InputActionReference rotateDragAction;
        [SerializeField] InputActionReference pointerPositionAction;
        [SerializeField] InputActionReference zoomAction;
        [SerializeField] InputActionReference lookAction;
        [SerializeField] InputActionReference cameraModifierAction;

        [Header("Tuning")]
        [SerializeField, Min(0f)] float panSpeed = 12f;
        [SerializeField, Min(1f)] float panReferenceDistance = 20f;
        [SerializeField, Min(0f)] float keyRotateSpeed = 90f;
        [SerializeField] float dragRotateDegreesPerPixel = 0.25f;
        [SerializeField] float dragTiltDegreesPerPixel = 0.25f;
        // Right-stick look (controller): degrees per second at full deflection, after the response curve.
        [SerializeField, Min(0f)] float lookYawDegreesPerSecond = 120f;
        [SerializeField, Min(0f)] float lookTiltDegreesPerSecond = 60f;
        [SerializeField, Min(1f)] float lookResponseExponent = 1.5f;
        // Small accidental movements during a right-click don't move the camera.
        [SerializeField, Min(0f)] float dragThresholdPixels = ClickDragDetector.DefaultThresholdPixels;
        [SerializeField, Min(0f)] float zoomStep = 2f;
        [SerializeField, Min(1f)] float minDistance = 5f;
        [SerializeField, Min(1f)] float maxDistance = 40f;
        [SerializeField, Min(1f)] float distance = 20f;
        [SerializeField, Range(10f, 89f)] float pitch = 55f;
        [SerializeField, Range(10f, 89f)] float minPitch = 25f;
        [SerializeField, Range(10f, 89f)] float maxPitch = 85f;
        [SerializeField, Min(0f)] float boundsHalfSize = 25f;
        // How quickly the pivot catches up with the active character while following or focusing (higher is tighter).
        [SerializeField, Min(0f)] float followSharpness = 10f;

        ClickDragDetector dragDetector;
        Vector2 lastPointerPosition;
        int pendingZoomSteps;
        CommandableUnit lastSeenUnit;
        bool isFocusing;
        // Where the active character stood when it became active. The glide eases here and never reads the unit again,
        // so it ends even if the unit walks on (no follow in free mode) or is destroyed.
        Vector3 focusPoint;

        public float Distance => distance;
        public float Yaw => transform.eulerAngles.y;
        public float Pitch => pitch;

        internal void Initialize(Camera camera, InputActionReference pan, InputActionReference rotate,
            InputActionReference rotateDrag, InputActionReference pointerPosition, InputActionReference zoom,
            ActiveCharacter active = null, InputActionReference look = null, InputActionReference cameraModifier = null)
        {
            viewCamera = camera;
            panAction = pan;
            rotateAction = rotate;
            rotateDragAction = rotateDrag;
            pointerPositionAction = pointerPosition;
            zoomAction = zoom;
            activeCharacter = active;
            lookAction = look;
            cameraModifierAction = cameraModifier;
            lastSeenUnit = CurrentUnit;
        }

        // The active character's unit, or null when there is none that can act.
        CommandableUnit CurrentUnit => activeCharacter != null && activeCharacter.HasUnit ? activeCharacter.Unit : null;

        void Awake() => dragDetector = new ClickDragDetector(dragThresholdPixels);

        void OnEnable()
        {
            InputActionUtility.SetEnabled(true, panAction, rotateAction, rotateDragAction, pointerPositionAction, zoomAction,
                lookAction, cameraModifierAction);
            if (zoomAction != null)
                zoomAction.action.performed += OnZoom;
            if (rotateDragAction != null)
            {
                rotateDragAction.action.started += OnRotateDragStarted;
                rotateDragAction.action.canceled += OnRotateDragEnded;
            }
            // The character already active when the camera starts is not a switch: no glide.
            lastSeenUnit = CurrentUnit;
            isFocusing = false;
            ApplyCameraPose();
        }

        void OnDisable()
        {
            if (zoomAction != null)
                zoomAction.action.performed -= OnZoom;
            if (rotateDragAction != null)
            {
                rotateDragAction.action.started -= OnRotateDragStarted;
                rotateDragAction.action.canceled -= OnRotateDragEnded;
            }
            InputActionUtility.SetEnabled(false, panAction, rotateAction, rotateDragAction, pointerPositionAction, zoomAction,
                lookAction, cameraModifierAction);
        }

        void Update()
        {
            var deltaTime = Time.unscaledDeltaTime;

            if (pendingZoomSteps != 0)
            {
                distance = Mathf.Clamp(distance - pendingZoomSteps * zoomStep, minDistance, maxDistance);
                pendingZoomSteps = 0;
            }

            var yaw = transform.eulerAngles.y + InputActionUtility.Read<float>(rotateAction) * keyRotateSpeed * deltaTime;
            // Right stick: look around while the game runs, or while the camera modifier hands it over during a pause.
            var cameraOwnsStick = StickRole.CameraOwnsRightStick(activeCharacter,
                InputActionUtility.IsPressed(cameraModifierAction));
            if (cameraOwnsStick)
            {
                var look = StickResponse.Curve(InputActionUtility.Read<Vector2>(lookAction), lookResponseExponent);
                yaw += look.x * lookYawDegreesPerSecond * deltaTime;
                pitch = Mathf.Clamp(pitch - look.y * lookTiltDegreesPerSecond * deltaTime, minPitch, maxPitch);
            }
            if (dragDetector.IsPressed)
            {
                var pointer = InputActionUtility.Read<Vector2>(pointerPositionAction);
                var wasDragging = dragDetector.IsDragging;
                dragDetector.Track(pointer);
                if (wasDragging)
                {
                    var delta = pointer - lastPointerPosition;
                    yaw += delta.x * dragRotateDegreesPerPixel;
                    // Dragging up tilts toward the horizon; dragging down looks more straight down.
                    pitch = Mathf.Clamp(pitch - delta.y * dragTiltDegreesPerPixel, minPitch, maxPitch);
                }
                lastPointerPosition = pointer;
            }

            var unit = CurrentUnit;
            if (unit != lastSeenUnit)
            {
                lastSeenUnit = unit;
                isFocusing = unit != null;
                if (isFocusing)
                    focusPoint = ClampToBounds(GroundTarget(unit));
            }

            var rotation = Quaternion.Euler(0f, yaw, 0f);
            Vector3 position;
            if (activeCharacter != null && activeCharacter.IsDriving)
            {
                // Takeover: follow the active character and ignore pan. Always eased, so after a pause or a switch the
                // camera glides over from wherever it was.
                isFocusing = false;
                position = EaseTowards(GroundTarget(activeCharacter.Unit), deltaTime);
            }
            else
            {
                var pan = InputActionUtility.Read<Vector2>(panAction);
                if (pan != Vector2.zero)
                    isFocusing = false;
                if (isFocusing)
                {
                    position = EaseTowards(focusPoint, deltaTime);
                    if ((position - focusPoint).sqrMagnitude < FocusArrivalDistance * FocusArrivalDistance)
                        isFocusing = false;
                }
                else
                {
                    var speed = panSpeed * (distance / panReferenceDistance);
                    position = transform.position + rotation * new Vector3(pan.x, 0f, pan.y) * (speed * deltaTime);
                }
            }
            position = ClampToBounds(position);

            transform.SetPositionAndRotation(position, rotation);
            ApplyCameraPose();
        }

        // A unit's position at the pivot's height.
        Vector3 GroundTarget(Component unit)
        {
            var target = unit.transform.position;
            target.y = transform.position.y;
            return target;
        }

        Vector3 EaseTowards(Vector3 target, float deltaTime) =>
            Vector3.Lerp(transform.position, target, 1f - Mathf.Exp(-followSharpness * deltaTime));

        Vector3 ClampToBounds(Vector3 position)
        {
            position.x = Mathf.Clamp(position.x, -boundsHalfSize, boundsHalfSize);
            position.z = Mathf.Clamp(position.z, -boundsHalfSize, boundsHalfSize);
            return position;
        }

        void OnZoom(InputAction.CallbackContext context)
        {
            var scroll = context.ReadValue<float>();
            if (scroll > 0f)
                pendingZoomSteps++;
            else if (scroll < 0f)
                pendingZoomSteps--;
        }

        void OnRotateDragStarted(InputAction.CallbackContext context)
        {
            lastPointerPosition = InputActionUtility.Read<Vector2>(pointerPositionAction);
            dragDetector.Press(lastPointerPosition);
        }

        void OnRotateDragEnded(InputAction.CallbackContext context) =>
            dragDetector.Release(InputActionUtility.Read<Vector2>(pointerPositionAction));

        void ApplyCameraPose()
        {
            if (viewCamera == null)
                return;
            var localRotation = Quaternion.Euler(pitch, 0f, 0f);
            viewCamera.transform.SetLocalPositionAndRotation(localRotation * new Vector3(0f, 0f, -distance), localRotation);
        }
    }
}
