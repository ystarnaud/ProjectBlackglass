using UnityEngine.InputSystem;

namespace Blackglass
{
    /// <summary>
    /// The one play-client binding the project's input asset has no action for: select every operative (Ctrl+A). Built in code
    /// here, beside NetTestControls, so device names stay in Scripts/Input; adding it to BlackglassControls would change the
    /// offline input asset, which needs the owner's word.
    /// </summary>
    internal static class NetPlayKeys
    {
        public static InputAction SelectAll()
        {
            var action = new InputAction("SelectAllOperatives", InputActionType.Button);
            action.AddCompositeBinding("ButtonWithOneModifier").With("Modifier", "<Keyboard>/ctrl").With("Button", "<Keyboard>/a");
            return action;
        }
    }
}
