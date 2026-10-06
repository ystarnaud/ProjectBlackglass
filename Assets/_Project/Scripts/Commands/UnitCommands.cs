using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// An order for a unit. Commands are plain data: whoever creates one (player input, groups, AI or scripts)
    /// hands it to CommandableUnit.Issue, and the unit decides how to carry it out. The commands are Move, Attack,
    /// MoveToCover, Ability and Stop. Future commands (Interact) are new subclasses.
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

    /// <summary>
    /// Use an ability on a unit or at a ground point. Plain data like every order: the unit validates it when it starts
    /// and again when it runs (UnitAbilities), and a failure ends the order without a cooldown. Instant: there is no
    /// cast time.
    /// </summary>
    public sealed class AbilityCommand : UnitCommand
    {
        AbilityCommand(AbilityDefinition definition, Health target, Vector3 point)
        {
            Definition = definition;
            Target = target;
            Point = point;
        }

        /// <summary>An ability aimed at a unit (Unit mode).</summary>
        public static AbilityCommand OnUnit(AbilityDefinition definition, Health target)
        {
            if (definition == null)
                throw new ArgumentNullException(nameof(definition));
            if (target == null)
                throw new ArgumentNullException(nameof(target));
            if (definition.TargetMode != AbilityTargetMode.Unit)
                throw new ArgumentException($"{definition.DisplayName} is aimed at the ground, not at a unit.", nameof(definition));
            return new AbilityCommand(definition, target, target.transform.position);
        }

        /// <summary>An ability aimed at a point on the ground (Ground mode).</summary>
        public static AbilityCommand AtGround(AbilityDefinition definition, Vector3 point)
        {
            if (definition == null)
                throw new ArgumentNullException(nameof(definition));
            if (definition.TargetMode != AbilityTargetMode.Ground)
                throw new ArgumentException($"{definition.DisplayName} is aimed at a unit, not at the ground.", nameof(definition));
            return new AbilityCommand(definition, null, point);
        }

        public AbilityDefinition Definition { get; }

        /// <summary>The unit aimed at (Unit mode), else null.</summary>
        public Health Target { get; }

        /// <summary>The ground point (Ground mode); for a unit ability, where the target stood when ordered.</summary>
        public Vector3 Point { get; }

        /// <summary>Where the ability is aimed now: the target's position while it exists, else the stored point.</summary>
        public Vector3 AimPoint => Target != null ? Target.transform.position : Point;
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
