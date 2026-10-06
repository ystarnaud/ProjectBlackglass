using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// How much of the validation to run. Full is everything. Static is only the checks that do not depend on where
    /// the caster will be or when: used when an ability is queued behind other orders (the caster will have moved on
    /// by the time it runs) and re-run in full when it starts.
    /// </summary>
    public enum AbilityCheckScope
    {
        Full,
        Static,
    }

    /// <summary>The facts about one attempt to use an ability, gathered by UnitAbilities so the rules stay pure.</summary>
    public readonly struct AbilityFacts
    {
        public AbilityFacts(bool casterAlive, bool hasAbility, bool hasTarget, bool targetAlive, bool targetOnRequiredSide,
            bool hasPosition, float cooldownRemaining, float flatDistance)
        {
            CasterAlive = casterAlive;
            HasAbility = hasAbility;
            HasTarget = hasTarget;
            TargetAlive = targetAlive;
            TargetOnRequiredSide = targetOnRequiredSide;
            HasPosition = hasPosition;
            CooldownRemaining = cooldownRemaining;
            FlatDistance = flatDistance;
        }

        public bool CasterAlive { get; }
        public bool HasAbility { get; }
        public bool HasTarget { get; }
        /// <summary>The target is alive and active in the hierarchy.</summary>
        public bool TargetAlive { get; }
        public bool TargetOnRequiredSide { get; }
        public bool HasPosition { get; }
        public float CooldownRemaining { get; }
        /// <summary>Flat distance from the caster to the target or point.</summary>
        public float FlatDistance { get; }
    }

    /// <summary>
    /// The one ordered validation of an ability use: caster alive, ability known, target (or position) valid, cooldown,
    /// range, then line of sight. The preview, Issue and execution all use it, so what the player sees is what runs.
    /// Pure and static; sight is a separate call because it costs a raycast.
    /// </summary>
    public static class AbilityRules
    {
        public static AbilityFailure CheckBasics(AbilityDefinition ability, in AbilityFacts facts, AbilityCheckScope scope)
        {
            if (!facts.CasterAlive)
                return AbilityFailure.CasterDead;
            if (!facts.HasAbility || ability == null)
                return AbilityFailure.UnknownAbility;

            if (ability.TargetMode == AbilityTargetMode.Unit)
            {
                if (!facts.HasTarget)
                    return AbilityFailure.NoTarget;
                if (!facts.TargetAlive)
                    return AbilityFailure.TargetDead;
                if (!facts.TargetOnRequiredSide)
                    return AbilityFailure.WrongSide;
            }
            else if (!facts.HasPosition)
            {
                return AbilityFailure.NoPosition;
            }

            if (scope == AbilityCheckScope.Static)
                return AbilityFailure.None;
            if (facts.CooldownRemaining > 0f)
                return AbilityFailure.OnCooldown;
            if (!IsInRange(facts.FlatDistance, ability.Range))
                return AbilityFailure.OutOfRange;
            return AbilityFailure.None;
        }

        /// <summary>NoLineOfSight when the ability needs a clear line and it is blocked.</summary>
        public static AbilityFailure CheckSight(AbilityDefinition ability, bool lineOfSightClear) =>
            ability != null && ability.RequiresLineOfSight && !lineOfSightClear
                ? AbilityFailure.NoLineOfSight
                : AbilityFailure.None;

        /// <summary>
        /// Whether a failure only means the caster is not in position yet (decision 029): out of range or out of sight.
        /// An order that fails only on these is accepted and the unit walks into position first; every other failure
        /// refuses it. Cooldown is checked before range, so a unit never walks anywhere for an ability that is cooling down.
        /// </summary>
        public static bool IsApproachable(AbilityFailure failure) =>
            failure == AbilityFailure.OutOfRange || failure == AbilityFailure.NoLineOfSight;

        /// <summary>
        /// How the unit walks into position for an approachable failure, with the attack's steps: Approach (walk toward
        /// the aim) while out of range, Reposition (look for a firing position) while in range but blind; None otherwise.
        /// </summary>
        public static AttackPhase ApproachPhase(AbilityFailure failure)
        {
            switch (failure)
            {
                case AbilityFailure.OutOfRange:
                    return AttackPhase.Approach;
                case AbilityFailure.NoLineOfSight:
                    return AttackPhase.Reposition;
                default:
                    return AttackPhase.None;
            }
        }

        /// <summary>Inclusive: a target exactly at range is in range.</summary>
        public static bool IsInRange(float flatDistance, float range) => flatDistance <= range;

        /// <summary>Flat and inclusive: units stand at pivot height, blast points on the ground.</summary>
        public static bool IsInArea(Vector3 center, Vector3 position, float radius) =>
            CoverRules.FlatDistance(center, position) <= radius;
    }
}
