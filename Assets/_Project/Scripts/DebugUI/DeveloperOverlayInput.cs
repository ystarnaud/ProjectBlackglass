using UnityEngine;
using UnityEngine.InputSystem;

namespace Blackglass
{
    /// <summary>The developer-overlay key (F1) as an input action; toggles the overlay flag and nothing else.</summary>
    public sealed class DeveloperOverlayInput : MonoBehaviour
    {
        [SerializeField] DeveloperOverlay overlay;
        [SerializeField] InputActionReference toggleAction;

        internal void Initialize(DeveloperOverlay developerOverlay, InputActionReference toggle)
        {
            overlay = developerOverlay;
            toggleAction = toggle;
        }

        void OnEnable()
        {
            InputActionUtility.SetEnabled(true, toggleAction);
            if (toggleAction != null)
                toggleAction.action.performed += OnToggle;
        }

        void OnDisable()
        {
            if (toggleAction != null)
                toggleAction.action.performed -= OnToggle;
            InputActionUtility.SetEnabled(false, toggleAction);
        }

        void OnToggle(InputAction.CallbackContext context)
        {
            if (overlay != null)
                overlay.Toggle();
        }
    }
}
