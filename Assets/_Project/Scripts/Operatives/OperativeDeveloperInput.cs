using UnityEngine;
using UnityEngine.InputSystem;

namespace Blackglass
{
    /// <summary>
    /// Developer keys for progression testing (F9 panel, F10 +XP, F11 reset) as input actions. They ask the roster to
    /// change the controlled operative; the rules live in the roster and the track, not here.
    /// </summary>
    public sealed class OperativeDeveloperInput : MonoBehaviour
    {
        [SerializeField] SquadRoster roster;
        [SerializeField] ActiveCharacter activeCharacter;
        [SerializeField] OperativePanelView panel;
        [SerializeField] InputActionReference toggleAction;
        [SerializeField] InputActionReference addExperienceAction;
        [SerializeField] InputActionReference resetAction;

        internal void Initialize(SquadRoster squad, ActiveCharacter active, OperativePanelView view,
            InputActionReference toggle, InputActionReference addExperience, InputActionReference reset)
        {
            roster = squad;
            activeCharacter = active;
            panel = view;
            toggleAction = toggle;
            addExperienceAction = addExperience;
            resetAction = reset;
        }

        void OnEnable()
        {
            InputActionUtility.SetEnabled(true, toggleAction, addExperienceAction, resetAction);
            if (toggleAction != null)
                toggleAction.action.performed += OnToggle;
            if (addExperienceAction != null)
                addExperienceAction.action.performed += OnAddExperience;
            if (resetAction != null)
                resetAction.action.performed += OnReset;
        }

        void OnDisable()
        {
            if (toggleAction != null)
                toggleAction.action.performed -= OnToggle;
            if (addExperienceAction != null)
                addExperienceAction.action.performed -= OnAddExperience;
            if (resetAction != null)
                resetAction.action.performed -= OnReset;
            InputActionUtility.SetEnabled(false, toggleAction, addExperienceAction, resetAction);
        }

        void OnToggle(InputAction.CallbackContext context)
        {
            if (panel != null)
                panel.Toggle();
        }

        void OnAddExperience(InputAction.CallbackContext context)
        {
            if (roster != null && roster.Track != null && TryControlledId(out var id))
                roster.AwardExperience(id, roster.Track.DebugXpStep);
        }

        void OnReset(InputAction.CallbackContext context)
        {
            if (roster != null && TryControlledId(out var id))
                roster.ResetProgression(id);
        }

        bool TryControlledId(out string id)
        {
            id = null;
            if (activeCharacter == null || activeCharacter.Unit == null || !activeCharacter.Unit.TryGetComponent<UnitIdentity>(out var identity))
                return false;
            id = identity.OperativeId;
            return true;
        }
    }
}
