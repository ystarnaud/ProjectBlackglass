using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Chooses the command for a context click: attack a living target, otherwise work an available interactable, otherwise move into the cover point near the
    /// click, otherwise move to the clicked point.
    /// </summary>
    public static class CommandResolver
    {
        public static UnitCommand Resolve(Health clicked, Vector3 point, CoverLocation cover = null, MissionInteractable interactable = null)
        {
            if (clicked != null && clicked.IsAlive)
                return new AttackCommand(clicked);
            if (interactable != null && interactable.IsAvailable)
                return new InteractCommand(interactable);
            if (cover != null)
                return new MoveToCoverCommand(cover);
            return new MoveCommand(point);
        }
    }
}
