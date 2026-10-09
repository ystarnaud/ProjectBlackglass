using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The friendly character the player currently controls directly, and whether takeover mode is on. Holds state
    /// only: DirectControlInput, click input, the camera, the marker and the HUD read it. When the active character
    /// stops being eligible (it dies), control moves to the next eligible roster unit, or to nobody. Any eligible roster
    /// unit can be the active character; Cycle moves to the next or previous one. Takeover starts off (free mode) and
    /// can be toggled while paused; it takes effect when simulation runs again.
    /// </summary>
    // Hands over before DirectControlInput (-100) reads Unit, so a death never shows as a one-frame HasUnit dip that
    // would re-arm its release gate.
    [DefaultExecutionOrder(-200)]
    public sealed class ActiveCharacter : MonoBehaviour
    {
        [SerializeField] CommandableUnit unit;
        [SerializeField] TacticalPause tacticalPause;
        // The roster to cycle through.
        [SerializeField] UnitSelection selection;

        public CommandableUnit Unit => unit;

        public bool IsTakeoverOn { get; private set; }

        /// <summary>
        /// Whether companions follow the controlled character. It gates formation following only: assist, orders and
        /// the parked/attached state (see CompanionAI) are not affected by it. Starts on.
        /// </summary>
        public bool IsFollowOn { get; private set; } = true;

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
        /// Whether a unit can be the active character: present, enabled, active in the hierarchy, alive, and if it
        /// has a SelectableUnit, that one enabled too.
        /// </summary>
        public static bool IsEligible(CommandableUnit candidate)
        {
            if (candidate == null || !candidate.enabled || !candidate.gameObject.activeInHierarchy || !candidate.IsAlive)
                return false;
            return !candidate.TryGetComponent<SelectableUnit>(out var selectable) || selectable.enabled;
        }

        /// <summary>Whether a roster unit can become the active character (see the CommandableUnit overload).</summary>
        public static bool IsEligible(SelectableUnit candidate) =>
            candidate != null && candidate.enabled && IsEligible(candidate.Unit);

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

        /// <summary>
        /// Makes a specific roster unit the active character and selects it, like Tab does for the next one. Returns false and
        /// changes nothing when the unit is not an eligible roster unit or is already in control.
        /// </summary>
        public bool TakeControl(CommandableUnit candidate)
        {
            if (selection == null || candidate == null || candidate == unit || !IsEligible(candidate) || !IsOnRoster(candidate))
                return false;
            SetUnit(candidate);
            SelectActiveIn(selection);
            return true;
        }

        bool IsOnRoster(CommandableUnit candidate)
        {
            foreach (var member in selection.Roster)
            {
                if (member != null && member.Unit == candidate)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// The hand-over selection step shared by Tab and TakeControl: the active character, when it is an eligible roster
        /// unit, becomes the only selected unit in the given selection.
        /// </summary>
        public void SelectActiveIn(UnitSelection target)
        {
            if (target != null && unit != null && unit.TryGetComponent<SelectableUnit>(out var selectable) && IsEligible(selectable))
                target.Select(selectable);
        }

        public void SetTakeover(bool on) => IsTakeoverOn = on;

        public void ToggleTakeover() => IsTakeoverOn = !IsTakeoverOn;

        /// <summary>Turns formation following on or off (see IsFollowOn). Works while paused; it takes effect when simulation runs.</summary>
        public void SetFollow(bool on) => IsFollowOn = on;

        /// <summary>Flips the follow flag.</summary>
        public void ToggleFollow() => IsFollowOn = !IsFollowOn;

        void Update() => RefreshEligibility();

        /// <summary>
        /// Keeps the active character valid: when it dies, is disabled, deactivated or destroyed, control passes to the
        /// next eligible roster unit (roster order, wrapping), or to nobody when none is left. Takeover mode and the
        /// selection are left alone. Runs every frame, paused or not, so the HUD and camera never see a dead unit as
        /// controlled for more than a frame.
        /// </summary>
        internal void RefreshEligibility()
        {
            if (ReferenceEquals(unit, null) || IsEligible(unit))
                return;
            if (!Cycle(1))
                SetUnit(null);
        }
    }
}
