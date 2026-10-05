using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// An order for a unit. Commands are plain data: whoever creates one (player input, groups, AI or scripts)
    /// hands it to CommandableUnit.Issue, and the unit decides how to carry it out. The commands are Move, Attack,
    /// MoveToCover and Stop. Future commands (Interact) are new subclasses.
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

    /// <summary>
    /// Walk to a cover point and occupy it. The point is reserved when the order starts and refused when another unit
    /// holds it; the reservation lives exactly as long as this order is current.
    /// </summary>
    public sealed class MoveToCoverCommand : UnitCommand
    {
        public MoveToCoverCommand(CoverLocation point)
        {
            if (point == null)
                throw new ArgumentNullException(nameof(point));
            Point = point;
        }

        public CoverLocation Point { get; }
    }

    /// <summary>Halts the unit and clears all of its orders. Never queued.</summary>
    public sealed class StopCommand : UnitCommand { }

    /// <summary>How a new order combines with the orders a unit already has.</summary>
    public enum IssueMode
    {
        /// <summary>Drop the current and pending orders and start this one now.</summary>
        Replace,
        /// <summary>Run this order after the pending ones (immediately if the unit is idle).</summary>
        Append,
    }
}
