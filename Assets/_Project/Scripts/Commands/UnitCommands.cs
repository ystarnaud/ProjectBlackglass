using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// An order for a unit. Commands are plain data: whoever creates one (player input, groups, AI or scripts)
    /// hands it to CommandableUnit.Issue, and the unit decides how to carry it out. The commands are Move, Attack,
    /// MoveToCover, Ability, Interact, UseItem, Collect and Stop.
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

    /// <summary>
    /// Walk to a mission interactable (a terminal) and work on it until it completes. Plain data like every order: the unit
    /// validates it when it starts and every frame it runs (UnitInteractor), and a failure ends the order so the queue
    /// moves on. Progress advances only with simulation time and is lost if the order is cancelled.
    /// </summary>
    public sealed class InteractCommand : UnitCommand
    {
        public InteractCommand(MissionInteractable target)
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));
            Target = target;
        }

        public MissionInteractable Target { get; }
    }

    /// <summary>
    /// Use an owned consumable on the unit itself. Plain data like every order: the unit checks it when it is issued and
    /// again when it runs (UnitItems), and a failure ends the order without consuming anything. Instant; it names the
    /// item by its entry's instance id, never by its name.
    /// </summary>
    public sealed class UseItemCommand : UnitCommand
    {
        public UseItemCommand(string instanceId)
        {
            if (string.IsNullOrWhiteSpace(instanceId))
                throw new ArgumentException("An item order needs the entry's instance id.", nameof(instanceId));
            InstanceId = instanceId;
        }

        public string InstanceId { get; }
    }

    /// <summary>
    /// Swap in an equippable item from the unit's own bag. Takes UnitItems.SwapSeconds, during which the unit does nothing
    /// else (it is the running order) and any new order cancels the swap without changing its gear. Plain data like every
    /// order; it names the entry by instance id, and the unit checks it when issued and again when the time is up.
    /// </summary>
    public sealed class EquipItemCommand : UnitCommand
    {
        public EquipItemCommand(string instanceId)
        {
            if (string.IsNullOrWhiteSpace(instanceId))
                throw new ArgumentException("An item order needs the entry's instance id.", nameof(instanceId));
            InstanceId = instanceId;
        }

        public string InstanceId { get; }
    }

    /// <summary>
    /// Walk to a searched loot container and move items from it into the unit's own bag: one entry, or everything that fits
    /// (no instance id). Plain data like every order: the unit checks it when it is issued and again when it runs
    /// (UnitItems), so another unit taking the entry first, a full bag or a vanished container end the order without losing
    /// anything. Leftovers stay in the container.
    /// </summary>
    public sealed class CollectCommand : UnitCommand
    {
        public CollectCommand(LootContainer container, string instanceId = null)
        {
            if (container == null)
                throw new ArgumentNullException(nameof(container));
            Container = container;
            InstanceId = instanceId;
        }

        public LootContainer Container { get; }

        /// <summary>The container entry to take, or null/empty for everything that fits.</summary>
        public string InstanceId { get; }

        public bool TakeAll => string.IsNullOrEmpty(InstanceId);
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
