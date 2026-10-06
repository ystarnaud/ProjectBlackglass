using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>What an ability is aimed at: a unit, or a point on the ground (an area).</summary>
    public enum AbilityTargetMode
    {
        Unit,
        Ground,
    }

    /// <summary>For a unit-targeted ability, whose side the target must be on, relative to the caster. Derived from the effect.</summary>
    public enum AbilityTargetSide
    {
        Hostile,
        Friendly,
    }

    public enum AbilityEffect
    {
        Damage,
        Heal,
    }

    /// <summary>Whether a covered target's hit chance applies to the ability (explicit per ability, never accidental).</summary>
    public enum AbilityCoverRule
    {
        /// <summary>A target that holds cover protecting it from the caster (or the blast point) is hit with its cover's chance.</summary>
        Applies,
        /// <summary>Cover does not matter: the effect always lands.</summary>
        Ignored,
    }

    /// <summary>
    /// One ability's rules as shared, immutable data: how it is aimed, how far, whether it needs line of sight, how it
    /// treats cover, its cooldown and its effect. Never holds runtime state: cooldowns and the last failure live on the
    /// unit (UnitAbilities), so two units sharing one definition never share a cooldown. The target side follows the
    /// effect: damage always targets hostiles (no friendly fire, decision 025) and healing always targets friendlies,
    /// whatever a definition is authored with. A ground ability is always area damage.
    /// </summary>
    [CreateAssetMenu(menuName = "Blackglass/Ability", fileName = "Ability")]
    public sealed class AbilityDefinition : ScriptableObject
    {
        [SerializeField] string displayName = "Ability";
        [SerializeField] AbilityTargetMode targetMode = AbilityTargetMode.Unit;
        // Derived from the effect (Damage = Hostile, Heal = Friendly); Create and OnValidate force it.
        [SerializeField] AbilityTargetSide targetSide =AbilityTargetSide.Hostile;
        [SerializeField, Min(0.5f)] float range = 10f;
        [SerializeField] bool requiresLineOfSight = true;
        [SerializeField] AbilityCoverRule coverRule = AbilityCoverRule.Applies;
        [SerializeField, Min(0f)] float cooldown = 5f;
        [SerializeField] AbilityEffect effect = AbilityEffect.Damage;
        [SerializeField, Min(0)] int amount = 25;
        // Ground only: the blast radius in metres.
        [SerializeField, Min(0f)] float radius;

        public string DisplayName => displayName;
        public AbilityTargetMode TargetMode => targetMode;
        public AbilityTargetSide TargetSide => targetSide;
        /// <summary>Flat distance in metres from the caster to the target or point; inclusive.</summary>
        public float Range => range;
        public bool RequiresLineOfSight => requiresLineOfSight;
        public AbilityCoverRule CoverRule => coverRule;
        /// <summary>Seconds of scaled time before the same unit can use it again.</summary>
        public float Cooldown => cooldown;
        public AbilityEffect Effect => effect;
        /// <summary>Damage dealt or hit points restored.</summary>
        public int Amount => amount;
        /// <summary>Ground abilities only.</summary>
        public float Radius => radius;

        internal static AbilityDefinition Create(string displayName, AbilityTargetMode mode, AbilityTargetSide side,
            float range, bool requiresLineOfSight, AbilityCoverRule coverRule, float cooldown, AbilityEffect effect,
            int amount, float radius = 0f)
        {
            if (range <= 0f)
                throw new ArgumentOutOfRangeException(nameof(range), range, "An ability needs a positive range.");
            if (mode == AbilityTargetMode.Ground)
            {
                if (effect != AbilityEffect.Damage)
                    throw new ArgumentException("A ground ability is area damage; it cannot heal.", nameof(effect));
                if (radius <= 0f)
                    throw new ArgumentException("A ground ability needs a positive radius.", nameof(radius));
            }

            var definition = CreateInstance<AbilityDefinition>();
            definition.displayName = displayName;
            definition.targetMode = mode;
            definition.targetSide = SideFor(effect);
            definition.range = range;
            definition.requiresLineOfSight = requiresLineOfSight;
            definition.coverRule = coverRule;
            definition.cooldown = cooldown;
            definition.effect = effect;
            definition.amount = amount;
            definition.radius = radius;
            return definition;
        }

        // The side is never a free choice: harmful effects reach only hostiles, helpful ones only friendlies.
        static AbilityTargetSide SideFor(AbilityEffect effect) =>
            effect == AbilityEffect.Damage ? AbilityTargetSide.Hostile : AbilityTargetSide.Friendly;

        // Keeps an asset edited in the Inspector inside the rules Create enforces.
        void OnValidate()
        {
            if (targetMode == AbilityTargetMode.Ground)
            {
                effect = AbilityEffect.Damage;
                if (radius <= 0f)
                    radius = 1f;
            }
            targetSide = SideFor(effect);
        }
    }
}
