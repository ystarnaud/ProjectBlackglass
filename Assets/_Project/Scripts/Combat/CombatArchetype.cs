using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// A weapon/combat preset as shared, immutable data: role (which decides line of sight and whether cover applies),
    /// range, damage and attack interval. A unit points at one from its UnitAttacker; there is no inventory or
    /// equipment. Holds no runtime state.
    /// </summary>
    [CreateAssetMenu(menuName = "Blackglass/Combat Archetype", fileName = "Archetype")]
    public sealed class CombatArchetype : ScriptableObject
    {
        [SerializeField] string displayName = "Archetype";
        [SerializeField] CombatRole role = CombatRole.Melee;
        [SerializeField, Min(0.1f)] float range = 2f;
        [SerializeField, Min(0)] int damage = 25;
        [SerializeField, Min(0f)] float attackInterval = 1f;

        public string DisplayName => displayName;
        public CombatRole Role => role;
        public float Range => range;
        public int Damage => damage;
        /// <summary>Seconds of scaled time between attacks.</summary>
        public float AttackInterval => attackInterval;

        internal static CombatArchetype Create(string displayName, CombatRole role, float range, int damage, float attackInterval)
        {
            if (range <= 0f)
                throw new ArgumentOutOfRangeException(nameof(range), range, "An archetype needs a positive range.");
            if (damage < 0)
                throw new ArgumentOutOfRangeException(nameof(damage), damage, "Damage cannot be negative.");
            if (attackInterval < 0f)
                throw new ArgumentOutOfRangeException(nameof(attackInterval), attackInterval, "An interval cannot be negative.");

            var archetype = CreateInstance<CombatArchetype>();
            archetype.displayName = displayName;
            archetype.role = role;
            archetype.range = range;
            archetype.damage = damage;
            archetype.attackInterval = attackInterval;
            return archetype;
        }
    }
}
