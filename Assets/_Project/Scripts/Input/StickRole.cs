namespace Blackglass
{
    /// <summary>
    /// Who owns the right stick. While the game is running (takeover on or off) it looks around (camera); while it is
    /// paused it moves the tactical cursor. Holding the camera modifier swaps the two, so a controller can point while
    /// the game runs and rotate the camera while planning.
    /// </summary>
    public static class StickRole
    {
        public static bool CameraOwnsRightStick(bool isRunning, bool modifierHeld) => isRunning != modifierHeld;

        /// <summary>The same rule for a game with this active character; with none there is nothing to pause, so it runs.</summary>
        public static bool CameraOwnsRightStick(ActiveCharacter activeCharacter, bool modifierHeld) =>
            CameraOwnsRightStick(activeCharacter == null || !activeCharacter.IsPaused, modifierHeld);
    }
}
