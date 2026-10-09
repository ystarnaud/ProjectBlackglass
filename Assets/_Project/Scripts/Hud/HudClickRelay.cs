using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Blackglass
{
    /// <summary>
    /// Passes a primary-button pointer click on its object to a plain HUD view, with the click count (a Button's onClick
    /// has none, and a squad card tells a double click from a single one). Pointer clicks only: it has no submit handler.
    /// </summary>
    [DisallowMultipleComponent]
    internal sealed class HudClickRelay : MonoBehaviour, IPointerClickHandler
    {
        internal event Action<int> Clicked;

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData == null || eventData.button != PointerEventData.InputButton.Left)
                return;
            Clicked?.Invoke(eventData.clickCount);
        }
    }
}
