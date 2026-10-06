namespace Blackglass
{
    /// <summary>
    /// Who owns the right stick. Driving the active character, it looks around (camera); in tactical mode it moves the
    /// cursor. Holding the camera modifier swaps the two, so a controller can rotate the camera while planning and
    /// point while driving. The same "the mode decides" rule WASD follows (decision 008).
    /// </summary>
    public static class StickRole
    {
        public static bool CameraOwnsRightStick(bool isDriving, bool modifierHeld) => isDriving != modifierHeld;
    }
}
