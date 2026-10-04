using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Whether gameplay simulation advances this frame. Time.deltaTime still holds the previous value on the frame
    /// TacticalPause sets the time scale to 0, so both are checked; this is the one test every simulation Update
    /// makes. Camera, input and debug UI do not use it: they run on unscaled time.
    /// </summary>
    public static class SimulationTime
    {
        public static bool IsRunning => Time.deltaTime > 0f && Time.timeScale > 0f;
    }
}
