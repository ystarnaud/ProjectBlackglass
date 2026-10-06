namespace Blackglass
{
    /// <summary>
    /// Which kind of input device the player is using right now. Prompts and the active binding group follow it;
    /// gameplay never reads it.
    /// </summary>
    public enum InputFamily
    {
        KeyboardMouse,
        Xbox,
        PlayStation,
        Nintendo,
        GenericGamepad,
    }

    /// <summary>
    /// A more precise controller identity, kept so a prompt family can be split later (a separate Switch 2 glyph set,
    /// say). Gameplay never reads it.
    /// </summary>
    public enum ControllerModel
    {
        None,
        Unknown,
        Xbox,
        DualShock,
        DualSense,
        SwitchPro,
        Switch2,
    }

    public static class InputFamilyExtensions
    {
        public static bool IsController(this InputFamily family) => family != InputFamily.KeyboardMouse;

        /// <summary>The binding group in BlackglassControls.inputactions that holds this family's bindings.</summary>
        public static string BindingGroup(this InputFamily family)
        {
            switch (family)
            {
                case InputFamily.Xbox:
                    return "Xbox";
                case InputFamily.PlayStation:
                    return "PlayStation";
                case InputFamily.Nintendo:
                    return "Nintendo";
                case InputFamily.GenericGamepad:
                    return "Gamepad";
                default:
                    return "KeyboardMouse";
            }
        }

        public static string DisplayName(this InputFamily family)
        {
            switch (family)
            {
                case InputFamily.Xbox:
                    return "Xbox";
                case InputFamily.PlayStation:
                    return "PlayStation";
                case InputFamily.Nintendo:
                    return "Nintendo";
                case InputFamily.GenericGamepad:
                    return "Generic Gamepad";
                default:
                    return "Keyboard/Mouse";
            }
        }
    }
}
