using UnityEngine;

namespace Blackglass
{
    /// <summary>Chooses the command for a context click: attack a living target, otherwise move to the clicked point.</summary>
    public static class CommandResolver
    {
        public static UnitCommand Resolve(Health clicked, Vector3 point)
        {
            if (clicked != null && clicked.IsAlive)
                return new AttackCommand(clicked);
            return new MoveCommand(point);
        }
    }
}
