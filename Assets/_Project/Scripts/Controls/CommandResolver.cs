using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Chooses the command for a context click: attack a living target, otherwise move into the cover point near the
    /// click, otherwise move to the clicked point.
    /// </summary>
    public static class CommandResolver
    {
        public static UnitCommand Resolve(Health clicked, Vector3 point, CoverLocation cover = null)
        {
            if (clicked != null && clicked.IsAlive)
                return new AttackCommand(clicked);
            if (cover != null)
                return new MoveToCoverCommand(cover);
            return new MoveCommand(point);
        }
    }
}
