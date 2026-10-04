using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Tactical pause: the only code in the project that writes Time.timeScale.
    /// Simulation (units, combat) runs on scaled time and stops while paused;
    /// camera, input and debug UI use unscaled time and keep working.
    /// </summary>
    public sealed class TacticalPause : MonoBehaviour
    {
        float timeScaleBeforePause = 1f;

        public bool IsPaused { get; private set; }

        /// <summary>Raised with the new paused state, only when the state actually changes.</summary>
        public event Action<bool> Changed;

        public void Pause()
        {
            if (IsPaused)
                return;
            timeScaleBeforePause = Time.timeScale;
            Time.timeScale = 0f;
            IsPaused = true;
            Changed?.Invoke(true);
        }

        public void Resume()
        {
            if (!IsPaused)
                return;
            Time.timeScale = timeScaleBeforePause;
            IsPaused = false;
            Changed?.Invoke(false);
        }

        public void Toggle()
        {
            if (IsPaused)
                Resume();
            else
                Pause();
        }

        // Never leave the game frozen when this service goes away (scene unload, exiting Play mode).
        void OnDisable() => Resume();
    }
}
