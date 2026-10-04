using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The friendly character the player currently controls directly, and whether takeover mode is on. Holds state
    /// only: DirectControlInput, click input, the camera and the HUD read it. Takeover starts off (free mode) and can be
    /// toggled while paused; it takes effect when simulation runs again.
    /// </summary>
    public sealed class ActiveCharacter : MonoBehaviour
    {
        [SerializeField] CommandableUnit unit;
        [SerializeField] TacticalPause tacticalPause;

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

        internal void Initialize(CommandableUnit activeUnit, TacticalPause pause)
        {
            unit = activeUnit;
            tacticalPause = pause;
        }

        public void SetTakeover(bool on) => IsTakeoverOn = on;

        public void ToggleTakeover() => IsTakeoverOn = !IsTakeoverOn;
    }
}
