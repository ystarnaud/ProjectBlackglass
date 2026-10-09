using UnityEngine.InputSystem.UI;

namespace Blackglass
{
    /// <summary>
    /// The HUD's UI input module: pointer actions only. The base module assigns its default actions when it is enabled
    /// with none (and drops them again when disabled), so the navigation actions (move, submit, cancel) are cleared after
    /// every enable: a stick or button on a pad can never move UI focus or press a HUD element.
    /// </summary>
    public sealed class PointerOnlyInputModule : InputSystemUIInputModule
    {
        protected override void OnEnable()
        {
            base.OnEnable();
            move = null;
            submit = null;
            cancel = null;
        }
    }
}
