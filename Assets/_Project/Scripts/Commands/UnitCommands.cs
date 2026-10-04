using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// An order for a unit. Commands are plain data: whoever creates one (player input today;
    /// AI, queues or selection later) hands it to CommandableUnit.Issue, and the unit decides
    /// how to carry it out. Future commands (Stop, Interact) are new subclasses.
    /// </summary>
    public abstract class UnitCommand { }

    public sealed class MoveCommand : UnitCommand
    {
        public MoveCommand(Vector3 destination) => Destination = destination;

        public Vector3 Destination { get; }
    }

    public sealed class AttackCommand : UnitCommand
    {
        public AttackCommand(Health target)
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));
            Target = target;
        }

        public Health Target { get; }
    }
}
