using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The friendly character the player currently controls directly, and whether takeover mode is on. Holds state
    /// only: DirectControlInput, click input, the camera, the marker and the HUD read it. Any eligible roster unit can be
    /// the active character; Cycle moves to the next or previous one. Takeover starts off (free mode) and can be toggled
    /// while paused; it takes effect when simulation runs again.
    /// </summary>
    public sealed class ActiveCharacter : MonoBehaviour
    {
        [SerializeField] CommandableUnit unit;
        [SerializeField] TacticalPause tacticalPause;
        // The roster to cycle through.
        [SerializeField] UnitSelection selection;

        public CommandableUnit Unit => unit;

        public bool IsTakeoverOn { get; private set; }

        /// <summary>
        /// True when there is an active character that can act: assigned, enabled and active. Uses enabled and
        /// activeInHierarchy (not isActiveAndEnabled), like UnitSelection, so it gives the same answer in EditMode.
        /// </summary>
        public bool HasUnit => unit != null && unit.enabled && unit.gameObject.activeInHierarchy;

        public bool IsPaused => tacticalPause != null && tacticalPause.IsPaused;

        /// <summary>True while WASD drives the active character: takeover on, simulation running, unit present.</summary>
        public bool IsDriving => IsTakeoverOn && !IsPaused && HasUnit;

        internal void Initialize(CommandableUnit activeUnit, TacticalPause pause, UnitSelection unitSelection = null)
        {
            unit = activeUnit;
            tacticalPause = pause;
            selection = unitSelection;
        }

        /// <summary>
        /// Whether a roster unit can become the active character: present, its SelectableUnit and CommandableUnit
        /// enabled, active in the hierarchy, and either without Health or alive.
        /// </summary>
        public static bool IsEligible(SelectableUnit candidate)
        {
            if (candidate == null || !candidate.enabled || !candidate.gameObject.activeInHierarchy)
                return false;
            var commandable = candidate.Unit;
            if (commandable == null || !commandable.enabled)
                return false;
            var health = candidate.GetComponent<Health>();
            return health == null || health.IsAlive;
        }

        /// <summary>Makes this unit the active character; null means none. Does not check eligibility.</summary>
        public void SetUnit(CommandableUnit newUnit) => unit = newUnit;

        /// <summary>
        /// Makes the next (+1) or previous (-1) eligible roster unit the active character, wrapping around. Returns true
        /// if the active character changed; false when there is no roster, nothing is eligible, or the only eligible
        /// unit is already active.
        /// </summary>
        public bool Cycle(int direction)
        {
            if (selection == null)
                return false;
            var roster = selection.Roster;
            var current = -1;
            for (var i = 0; i < roster.Count; i++)
            {
                if (roster[i] != null && roster[i].Unit == unit)
                {
                    current = i;
                    break;
                }
            }

            var next = ControlCycle.NextIndex(roster.Count, current, direction, i => IsEligible(roster[i]));
            if (next < 0 || roster[next].Unit == unit)
                return false;
            SetUnit(roster[next].Unit);
            return true;
        }

        public void SetTakeover(bool on) => IsTakeoverOn = on;

        public void ToggleTakeover() => IsTakeoverOn = !IsTakeoverOn;
    }
}
