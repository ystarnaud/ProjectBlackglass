using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// What an operative is worth right now: its definition, plus its role's bonus, plus the modifiers of every choice it
    /// has picked. A plain value computed on demand and never stored back into any asset. Combat stays in the combat
    /// components; they receive these numbers and do the rest.
    /// </summary>
    public readonly struct EffectiveConfiguration
    {
        // A cooldown can be cut by at most this much, so abilities never become free.
        const float MaxCooldownReduction = 0.75f;
        const float MinMoveSpeed = 0.5f;

        EffectiveConfiguration(int rank, int maxHealth, float moveSpeed, CombatRole attackRole, float attackRange,
            int attackDamage, float attackInterval, float abilityPower, float abilityCooldownMultiplier)
        {
            Rank = rank;
            MaxHealth = maxHealth;
            MoveSpeed = moveSpeed;
            AttackRole = attackRole;
            AttackRange = attackRange;
            AttackDamage = attackDamage;
            AttackInterval = attackInterval;
            AbilityPower = abilityPower;
            AbilityCooldownMultiplier = abilityCooldownMultiplier;
        }

        public int Rank { get; }
        public int MaxHealth { get; }
        public float MoveSpeed { get; }
        public CombatRole AttackRole { get; }
        public float AttackRange { get; }
        public int AttackDamage { get; }
        public float AttackInterval { get; }
        /// <summary>Multiplier on an ability's damage or healing; 1 leaves it as authored.</summary>
        public float AbilityPower { get; }
        /// <summary>Multiplier on an ability's cooldown; 1 leaves it as authored, never below 0.25.</summary>
        public float AbilityCooldownMultiplier { get; }

        /// <summary>The definition and its archetype alone: no role bonus, no picks, rank 1.</summary>
        public static EffectiveConfiguration Base(OperativeDefinition definition) => Build(definition, 1, default);

        /// <summary>
        /// Base + role bonus + the modifiers of the state's picks. A null state or track means no picks (rank 1); a pick
        /// whose id the track no longer knows is skipped; a null role gives no bonus.
        /// </summary>
        public static EffectiveConfiguration Evaluate(OperativeDefinition definition, PersistentOperativeState state, ProgressionTrack track)
        {
            if (definition == null)
                throw new ArgumentNullException(nameof(definition));
            var total = definition.Role != null ? definition.Role.Bonus : default;
            var rank = 1;
            if (state != null && track != null)
            {
                rank = state.Rank(track);
                foreach (var choiceId in state.ChoiceIds)
                {
                    var choice = track.FindChoice(choiceId);
                    if (choice != null)
                        total = StatModifiers.Combine(total, choice.Modifiers);
                }
            }
            return Build(definition, rank, total);
        }

        static EffectiveConfiguration Build(OperativeDefinition definition, int rank, StatModifiers total)
        {
            if (definition == null)
                throw new ArgumentNullException(nameof(definition));
            var archetype = definition.Archetype;
            if (archetype == null)
                throw new ArgumentException($"{definition.name} has no combat archetype.", nameof(definition));

            return new EffectiveConfiguration(
                rank,
                Math.Max(1, definition.BaseMaxHealth + total.maxHealth),
                Math.Max(MinMoveSpeed, definition.BaseMoveSpeed + total.moveSpeed),
                archetype.Role,
                archetype.Range,
                ScaleRounded(archetype.Damage, total.attackDamage),
                archetype.AttackInterval,
                Math.Max(0f, 1f + total.abilityPower),
                1f - Mathf.Clamp(total.abilityCooldownReduction, 0f, MaxCooldownReduction));
        }

        // Rounded away from zero in double precision so a .5 result never depends on float noise; never below 0.
        static int ScaleRounded(int value, float fraction) =>
            Math.Max(0, (int)Math.Round(value * (1.0 + fraction), MidpointRounding.AwayFromZero));
    }
}
