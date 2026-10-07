using UnityEngine;
using UnityEngine.InputSystem;

namespace Blackglass
{
    /// <summary>Developer keys (F6 same seed, F7 new seed, F8 toggle themed visuals) as input actions; calls the director and nothing else.</summary>
    public sealed class MissionDeveloperInput : MonoBehaviour
    {
        [SerializeField] MissionDirector director;
        [SerializeField] InputActionReference regenerateSameAction;
        [SerializeField] InputActionReference regenerateNewAction;
        [SerializeField] InputActionReference toggleVisualsAction;

        internal void Initialize(MissionDirector missionDirector, InputActionReference same, InputActionReference fresh,
            InputActionReference toggleVisuals = null)
        {
            director = missionDirector;
            regenerateSameAction = same;
            regenerateNewAction = fresh;
            toggleVisualsAction = toggleVisuals;
        }

        void OnEnable()
        {
            InputActionUtility.SetEnabled(true, regenerateSameAction, regenerateNewAction, toggleVisualsAction);
            if (regenerateSameAction != null)
                regenerateSameAction.action.performed += OnSame;
            if (regenerateNewAction != null)
                regenerateNewAction.action.performed += OnNew;
            if (toggleVisualsAction != null)
                toggleVisualsAction.action.performed += OnToggleVisuals;
        }

        void OnDisable()
        {
            if (regenerateSameAction != null)
                regenerateSameAction.action.performed -= OnSame;
            if (regenerateNewAction != null)
                regenerateNewAction.action.performed -= OnNew;
            if (toggleVisualsAction != null)
                toggleVisualsAction.action.performed -= OnToggleVisuals;
            InputActionUtility.SetEnabled(false, regenerateSameAction, regenerateNewAction, toggleVisualsAction);
        }

        void OnSame(InputAction.CallbackContext context) => director.RegenerateSame();

        void OnNew(InputAction.CallbackContext context) => director.GenerateNew();

        void OnToggleVisuals(InputAction.CallbackContext context) => director.ToggleVisuals();
    }
}
