namespace Blackglass
{
    /// <summary>
    /// Placeholder prompt text for a gamepad control, per input family. Nintendo's face buttons are labelled by their
    /// printed letters, which are not in Xbox's positions (south is B, east is A, north is X, west is Y). Text only: no
    /// glyph artwork. Unknown control names come back unchanged, so a new binding never throws.
    /// </summary>
    public static class GamepadLabels
    {
        public static string Label(string controlName, InputFamily family)
        {
            switch (controlName)
            {
                case "buttonSouth": return Pick(family, "A", "Cross", "B", "South");
                case "buttonEast": return Pick(family, "B", "Circle", "A", "East");
                case "buttonWest": return Pick(family, "X", "Square", "Y", "West");
                case "buttonNorth": return Pick(family, "Y", "Triangle", "X", "North");
                case "leftShoulder": return Pick(family, "LB", "L1", "L", "Left Shoulder");
                case "rightShoulder": return Pick(family, "RB", "R1", "R", "Right Shoulder");
                case "leftTrigger": return Pick(family, "LT", "L2", "ZL", "Left Trigger");
                case "rightTrigger": return Pick(family, "RT", "R2", "ZR", "Right Trigger");
                case "select": return Pick(family, "View", "Create", "-", "Select");
                case "start": return Pick(family, "Menu", "Options", "+", "Start");
                case "leftStickPress": return Pick(family, "LS Click", "L3", "L Stick Click", "Left Stick Press");
                case "rightStickPress": return Pick(family, "RS Click", "R3", "R Stick Click", "Right Stick Press");
                case "leftStick": return "Left Stick";
                case "rightStick": return "Right Stick";
                case "dpad": return "D-pad";
                case "dpad/up": return "D-pad Up";
                case "dpad/down": return "D-pad Down";
                case "dpad/left": return "D-pad Left";
                case "dpad/right": return "D-pad Right";
                default: return controlName;
            }
        }

        static string Pick(InputFamily family, string xbox, string playStation, string nintendo, string generic)
        {
            switch (family)
            {
                case InputFamily.Xbox: return xbox;
                case InputFamily.PlayStation: return playStation;
                case InputFamily.Nintendo: return nintendo;
                default: return generic;
            }
        }
    }
}
