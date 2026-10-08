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
        Reveal,
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
    /// whatever a definition is authored with. A ground ability is area damage or a reveal (Recon Scan, decision 037).
    /// </summary>
    [CreateAssetMenu(menuName = "Blackglass/Ability", fileName = "Ability")]
    public sealed class AbilityDefinition : ScriptableObject
    {
        [SerializeField] string displayName = "Ability";
        [SerializeField] AbilityTargetMode targetMode = AbilityTargetMode.Unit;
        // Derived from the effect (Damage = Hostile, Heal = Friendly): Create and OnValidate normalise the stored value, and
        // TargetSide derives it again on every read, so a hand-edited asset cannot change it in a build either.
        [SerializeField] AbilityTargetSide targetSide = AbilityTargetSide.Hostile;
        [SerializeField, Min(0.5f)] float range = 10f;
        [SerializeField] bool requiresLineOfSight = true;
        [SerializeField] AbilityCoverRule coverRule = AbilityCoverRule.Applies;
        [SerializeField, Min(0f)] float cooldown = 5f;
        [SerializeField] AbilityEffect effect = AbilityEffect.Damage;
        [SerializeField, Min(0)] int amount = 25;
        // Ground only: the blast radius in metres.
        [SerializeField, Min(0f)] float radius;
        // Reveal only: how long the enemies inside the circle stay observed.
        [SerializeField, Min(0f)] float revealSeconds = 6f;

        public string DisplayName => displayName;
        public AbilityTargetMode TargetMode => targetMode;
        /// <summary>Always derived from <see cref="Effect"/>: damage targets hostiles, healing targets friendlies.</summary>
        public AbilityTargetSide TargetSide => SideFor(Effect);
        /// <summary>Flat distance in metres from the caster to the target or point; inclusive.</summary>
        public float Range => range;
        public bool RequiresLineOfSight => requiresLineOfSight;
        public AbilityCoverRule CoverRule => coverRule;
        /// <summary>Seconds of scaled time before the same unit can use it again.</summary>
        public float Cooldown => cooldown;
        /// <summary>
        /// A ground ability is area damage or a reveal (a stored Heal reads as Damage); a unit ability is damage or healing
        /// (a stored Reveal reads as Damage).
        /// </summary>
        public AbilityEffect Effect =>
            targetMode == AbilityTargetMode.Ground
                ? (effect == AbilityEffect.Reveal ? AbilityEffect.Reveal : AbilityEffect.Damage)
                : (effect == AbilityEffect.Reveal ? AbilityEffect.Damage : effect);
        /// <summary>Damage dealt or hit points restored.</summary>
        public int Amount => amount;
        /// <summary>Ground abilities only.</summary>
        public float Radius => radius;
        /// <summary>Reveal only: seconds the enemies inside the circle stay observed; 0 for every other effect.</summary>
        public float RevealSeconds => Effect == AbilityEffect.Reveal ? revealSeconds : 0f;

        internal static AbilityDefinition Create(string displayName, AbilityTargetMode mode,
            float range, bool requiresLineOfSight, AbilityCoverRule coverRule, float cooldown, AbilityEffect effect,
            int amount, float radius = 0f, float revealSeconds = 0f)
        {
            if (range <= 0f)
                throw new ArgumentOutOfRangeException(nameof(range), range, "An ability needs a positive range.");
            if (mode == AbilityTargetMode.Ground)
            {
                if (effect == AbilityEffect.Heal)
                    throw new ArgumentException("A ground ability is area damage or a reveal; it cannot heal.", nameof(effect));
                if (radius <= 0f)
                    throw new ArgumentException("A ground ability needs a positive radius.", nameof(radius));
            }
            else if (effect == AbilityEffect.Reveal)
            {
                throw new ArgumentException("A reveal is aimed at the ground; a unit ability cannot reveal.", nameof(effect));
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
            definition.revealSeconds = revealSeconds;
            return definition;
        }

        // The side is never a free choice: harmful effects reach only hostiles, helpful ones only friendlies.
        static AbilityTargetSide SideFor(AbilityEffect effect) =>
            effect == AbilityEffect.Heal ? AbilityTargetSide.Friendly : AbilityTargetSide.Hostile;

        // Keeps an asset edited in the Inspector inside the rules Create enforces.
        void OnValidate()
        {
            if (targetMode == AbilityTargetMode.Ground)
            {
                if (effect != AbilityEffect.Reveal)
                    effect = AbilityEffect.Damage;
                if (radius <= 0f)
                    radius = 1f;
            }
            else if (effect == AbilityEffect.Reveal)
            {
                effect = AbilityEffect.Damage;
            }
            targetSide = SideFor(effect);
        }
    }
}
