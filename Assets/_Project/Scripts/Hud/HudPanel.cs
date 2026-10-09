using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// One part of the tactical HUD: it builds its own uGUI under a root rect once, keeps its Text/Image references, and
    /// on Apply writes only what changed since the last Apply (HudFactory.SetText / SetActive / SetColor / SetFill).
    /// A plain class, not a component: TacticalHud owns the panels and calls Apply once per frame.
    /// </summary>
    internal abstract class HudPanel
    {
        protected HudPanel(RectTransform root) => Root = root;

        internal RectTransform Root { get; }

        public abstract void Apply(HudSnapshot s);
    }
}
