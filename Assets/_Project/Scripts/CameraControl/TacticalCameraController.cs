using UnityEngine;
using UnityEngine.InputSystem;

namespace Blackglass
{
    /// <summary>
    /// Simple strategy camera orbiting a pivot on the ground. Runs entirely on unscaled time
    /// so it keeps working during tactical pause. Lives on the pivot; the camera is a child.
    /// </summary>
    public sealed class TacticalCameraController : MonoBehaviour
    {
        [SerializeField] Camera viewCamera;

        [Header("Input")]
        [SerializeField] InputActionReference panAction;
        [SerializeField] InputActionReference rotateAction;
        [SerializeField] InputActionReference rotateDragAction;
        [SerializeField] InputActionReference pointerPositionAction;
        [SerializeField] InputActionReference zoomAction;

        [Header("Tuning")]
        [SerializeField, Min(0f)] float panSpeed = 12f;
        [SerializeField, Min(1f)] float panReferenceDistance = 20f;
        [SerializeField, Min(0f)] float keyRotateSpeed = 90f;
        [SerializeField] float dragRotateDegreesPerPixel = 0.25f;
        // Must equal the command input's threshold, so one right-button press never both rotates and commands.
        [SerializeField, Min(0f)] float dragThresholdPixels = ClickDragDetector.DefaultThresholdPixels;
        [SerializeField, Min(0f)] float zoomStep = 2f;
        [SerializeField, Min(1f)] float minDistance = 5f;
        [SerializeField, Min(1f)] float maxDistance = 40f;
        [SerializeField, Min(1f)] float distance = 20f;
        [SerializeField, Range(10f, 89f)] float pitch = 55f;
        [SerializeField, Min(0f)] float boundsHalfSize = 25f;

        ClickDragDetector dragDetector;
        Vector2 lastPointerPosition;
        int pendingZoomSteps;

        public float Distance => distance;
        public float Yaw => transform.eulerAngles.y;

        internal void Initialize(Camera camera, InputActionReference pan, InputActionReference rotate,
            InputActionReference rotateDrag, InputActionReference pointerPosition, InputActionReference zoom)
        {
            viewCamera = camera;
            panAction = pan;
            rotateAction = rotate;
            rotateDragAction = rotateDrag;
            pointerPositionAction = pointerPosition;
            zoomAction = zoom;
        }

        void Awake() => dragDetector = new ClickDragDetector(dragThresholdPixels);

        void OnEnable()
        {
            InputActionUtility.SetEnabled(true, panAction, rotateAction, rotateDragAction, pointerPositionAction, zoomAction);
            if (zoomAction != null)
                zoomAction.action.performed += OnZoom;
            if (rotateDragAction != null)
            {
                rotateDragAction.action.started += OnRotateDragStarted;
                rotateDragAction.action.canceled += OnRotateDragEnded;
            }
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
            InputActionUtility.SetEnabled(false, panAction, rotateAction, rotateDragAction, pointerPositionAction, zoomAction);
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
            if (dragDetector.IsPressed)
            {
                var pointer = InputActionUtility.Read<Vector2>(pointerPositionAction);
                var wasDragging = dragDetector.IsDragging;
                dragDetector.Track(pointer);
                if (wasDragging)
                    yaw += (pointer.x - lastPointerPosition.x) * dragRotateDegreesPerPixel;
                lastPointerPosition = pointer;
            }

            var rotation = Quaternion.Euler(0f, yaw, 0f);
            var pan = InputActionUtility.Read<Vector2>(panAction);
            var speed = panSpeed * (distance / panReferenceDistance);
            var position = transform.position + rotation * new Vector3(pan.x, 0f, pan.y) * (speed * deltaTime);
            position.x = Mathf.Clamp(position.x, -boundsHalfSize, boundsHalfSize);
            position.z = Mathf.Clamp(position.z, -boundsHalfSize, boundsHalfSize);

            transform.SetPositionAndRotation(position, rotation);
            ApplyCameraPose();
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
