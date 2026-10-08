using UnityEngine;
using UnityEngine.InputSystem;

namespace Blackglass
{
    /// <summary>
    /// Developer keys for the intelligence layer as input actions (F12 truth view, F5 next preset + regenerate the same
    /// seed, M map overlay). They ask the service, the director and the overlay; the rules live there. Keyboard only, like
    /// the other developer keys, and never part of normal play.
    /// </summary>
    public sealed class IntelligenceDeveloperInput : MonoBehaviour
    {
        [SerializeField] IntelligenceService intelligence;
        [SerializeField] MissionDirector director;
        [SerializeField] IntelMapView map;
        [SerializeField] InputActionReference truthViewAction;
        [SerializeField] InputActionReference cyclePresetAction;
        [SerializeField] InputActionReference toggleMapAction;

        int presetIndex = -1;

        internal void Initialize(IntelligenceService service, MissionDirector missionDirector, IntelMapView mapView,
            InputActionReference truthView, InputActionReference cyclePreset, InputActionReference toggleMap)
        {
            intelligence = service;
            director = missionDirector;
            map = mapView;
            truthViewAction = truthView;
            cyclePresetAction = cyclePreset;
            toggleMapAction = toggleMap;
        }

        void OnEnable()
        {
            InputActionUtility.SetEnabled(true, truthViewAction, cyclePresetAction, toggleMapAction);
            if (truthViewAction != null)
                truthViewAction.action.performed += OnTruthView;
            if (cyclePresetAction != null)
                cyclePresetAction.action.performed += OnCyclePreset;
            if (toggleMapAction != null)
                toggleMapAction.action.performed += OnToggleMap;
        }

        void OnDisable()
        {
            if (truthViewAction != null)
                truthViewAction.action.performed -= OnTruthView;
            if (cyclePresetAction != null)
                cyclePresetAction.action.performed -= OnCyclePreset;
            if (toggleMapAction != null)
                toggleMapAction.action.performed -= OnToggleMap;
            InputActionUtility.SetEnabled(false, truthViewAction, cyclePresetAction, toggleMapAction);
        }

        void OnTruthView(InputAction.CallbackContext context)
        {
            if (intelligence != null)
                intelligence.TruthView = !intelligence.TruthView;
        }

        void OnToggleMap(InputAction.CallbackContext context)
        {
            if (map != null)
                map.Toggle();
        }

        // The next preset replaces the director's intelligence settings and the same seed is generated again.
        void OnCyclePreset(InputAction.CallbackContext context)
        {
            if (director == null || director.State == MissionState.Generating)
                return;
            if (presetIndex < 0)
                presetIndex = CurrentPresetIndex();
            presetIndex = (presetIndex + 1) % IntelligenceSettings.PresetCount;
            director.Settings.intelligence = IntelligenceSettings.Preset(presetIndex);
            director.RegenerateSame();
        }

        // The preset the director is on now (0, Full, when it matches none).
        int CurrentPresetIndex()
        {
            var current = director.Settings.intelligence != null ? director.Settings.intelligence.Describe() : string.Empty;
            for (var i = 0; i < IntelligenceSettings.PresetCount; i++)
            {
                if (IntelligenceSettings.Preset(i).Describe() == current)
                    return i;
            }
            return 0;
        }
    }
}
