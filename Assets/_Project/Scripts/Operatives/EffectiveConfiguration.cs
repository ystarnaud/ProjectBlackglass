using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// What an operative is worth right now: its definition, plus its role's bonus, plus the modifiers of every choice it
    /// has picked, plus (when the unit has an equipment system) the passive modifiers of its equipped items and its
    /// equipped weapon in place of the definition's archetype. A plain value computed on demand from authoritative inputs
    /// and never stored back into any asset, so repeating the calculation can never accumulate a bonus. Combat stays in
    /// the combat components; they receive these numbers and do the rest.
    /// </summary>
    public readonly struct EffectiveConfiguration
    {
        // A cooldown can be cut by at most this much, so abilities never become free.
        const float MaxCooldownReduction = 0.75f;
        const float MinMoveSpeed = 0.5f;

        EffectiveConfiguration(int rank, int maxHealth, float moveSpeed, CombatRole attackRole, float attackRange,
            int attackDamage, float attackInterval, float abilityPower, float abilityCooldownMultiplier, bool hasWeapon,
            string weaponName)
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
            HasWeapon = hasWeapon;
            WeaponName = weaponName ?? string.Empty;
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
        /// <summary>False only for a unit with an equipment system and an empty Weapon slot: its ordinary attack is disabled.</summary>
        public bool HasWeapon { get; }
        /// <summary>The weapon's display name (the archetype's without equipment), empty with no weapon.</summary>
        public string WeaponName { get; }

        /// <summary>The definition and its archetype alone: no role bonus, no picks, rank 1.</summary>
        public static EffectiveConfiguration Base(OperativeDefinition definition) => Build(definition, 1, default, default);

        /// <summary>
        /// Base + role bonus + the modifiers of the state's picks. A null state or track means no picks (rank 1); a pick
        /// whose id the track no longer knows is skipped; a null role gives no bonus.
        /// </summary>
        public static EffectiveConfiguration Evaluate(OperativeDefinition definition, PersistentOperativeState state, ProgressionTrack track) =>
            Evaluate(definition, state, track, default);

        /// <summary>
        /// As above, plus equipment. `equipped.IsSet` false (the default) means no equipment system and gives the result
        /// above unchanged; true replaces the definition's archetype with the equipped weapon (none = no weapon) and adds
        /// the passive modifiers of the weapon, armor and utility items.
        /// </summary>
        public static EffectiveConfiguration Evaluate(OperativeDefinition definition, PersistentOperativeState state,
            ProgressionTrack track, EquippedItems equipped)
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
            return Build(definition, rank, total, equipped);
        }

        static EffectiveConfiguration Build(OperativeDefinition definition, int rank, StatModifiers total, EquippedItems equipped)
        {
            if (definition == null)
                throw new ArgumentNullException(nameof(definition));

            CombatArchetype weapon;
            string weaponName;
            if (equipped.IsSet)
            {
                total = StatModifiers.Combine(total, equipped.Modifiers);
                weapon = equipped.Weapon != null ? equipped.Weapon.Weapon : null;
                weaponName = equipped.Weapon != null ? equipped.Weapon.DisplayName : string.Empty;
            }
            else
            {
                weapon = definition.Archetype;
                if (weapon == null)
                    throw new ArgumentException($"{definition.name} has no combat archetype.", nameof(definition));
                weaponName = weapon.DisplayName;
            }
            var armed = weapon != null;

            return new EffectiveConfiguration(
                rank,
                Math.Max(1, definition.BaseMaxHealth + total.maxHealth),
                Math.Max(MinMoveSpeed, definition.BaseMoveSpeed + total.moveSpeed),
                armed ? weapon.Role : CombatRole.Melee,
                armed ? weapon.Range : 0f,
                armed ? ScaleRounded(weapon.Damage, total.attackDamage) : 0,
                armed ? weapon.AttackInterval : 0f,
                Math.Max(0f, 1f + total.abilityPower),
                1f - Mathf.Clamp(total.abilityCooldownReduction, 0f, MaxCooldownReduction),
                armed,
                armed ? weaponName : string.Empty);
        }

        // Rounded away from zero in double precision so a .5 result never depends on float noise; never below 0.
        static int ScaleRounded(int value, float fraction) =>
            Math.Max(0, (int)Math.Round(value * (1.0 + fraction), MidpointRounding.AwayFromZero));
    }
}
