using UnityEngine.InputSystem;

namespace Blackglass
{
    /// <summary>Small helpers for components that read input through InputActionReference fields.</summary>
    internal static class InputActionUtility
    {
        public static void SetEnabled(bool enable, params InputActionReference[] references)
        {
            foreach (var reference in references)
            {
                if (reference == null || reference.action == null)
                    continue;
                if (enable)
                    reference.action.Enable();
                else
                    reference.action.Disable();
            }
        }

        public static T Read<T>(InputActionReference reference) where T : struct =>
            reference != null && reference.action != null ? reference.action.ReadValue<T>() : default;

        public static bool IsPressed(InputActionReference reference) =>
            reference != null && reference.action != null && reference.action.IsPressed();
    }
}
