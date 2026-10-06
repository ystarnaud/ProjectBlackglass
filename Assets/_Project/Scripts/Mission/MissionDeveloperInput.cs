using UnityEngine;
using UnityEngine.InputSystem;

namespace Blackglass
{
    /// <summary>Developer keys (F6 same seed, F7 new seed) as input actions; calls the director and nothing else.</summary>
    public sealed class MissionDeveloperInput : MonoBehaviour
    {
        [SerializeField] MissionDirector director;
        [SerializeField] InputActionReference regenerateSameAction;
        [SerializeField] InputActionReference regenerateNewAction;

        internal void Initialize(MissionDirector missionDirector, InputActionReference same, InputActionReference fresh)
        {
            director = missionDirector;
            regenerateSameAction = same;
            regenerateNewAction = fresh;
        }

        void OnEnable()
        {
            InputActionUtility.SetEnabled(true, regenerateSameAction, regenerateNewAction);
            if (regenerateSameAction != null)
                regenerateSameAction.action.performed += OnSame;
            if (regenerateNewAction != null)
                regenerateNewAction.action.performed += OnNew;
        }

        void OnDisable()
        {
            if (regenerateSameAction != null)
                regenerateSameAction.action.performed -= OnSame;
            if (regenerateNewAction != null)
                regenerateNewAction.action.performed -= OnNew;
            InputActionUtility.SetEnabled(false, regenerateSameAction, regenerateNewAction);
        }

        void OnSame(InputAction.CallbackContext context) => director.RegenerateSame();

        void OnNew(InputAction.CallbackContext context) => director.GenerateNew();
    }
}
