using UnityEngine;

namespace Blackglass
{
    /// <summary>Whether the developer (debug) overlay is shown. Hidden by default; a null reference elsewhere means "shown".</summary>
    public sealed class DeveloperOverlay : MonoBehaviour
    {
        [SerializeField] bool visible;

        public bool IsVisible => visible;
        public void SetVisible(bool show) => visible = show;
        public void Toggle() => visible = !visible;
        public static bool Shows(DeveloperOverlay overlay) => overlay == null || overlay.IsVisible;
    }
}
