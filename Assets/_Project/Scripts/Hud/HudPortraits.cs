using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The one place a squad card asks for a unit's portrait. There is no portrait art yet, so it always answers null and
    /// the card shows the unit's initials; a later art pass changes only this method.
    /// </summary>
    public static class HudPortraits
    {
        public static Sprite Resolve(CommandableUnit unit) => null;
    }
}
