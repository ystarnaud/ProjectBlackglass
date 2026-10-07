using System;

namespace Blackglass
{
    /// <summary>
    /// The few numbers a role or an advancement choice can change, as plain data. Max health is a flat amount and move
    /// speed is flat metres per second; the other three are fractions of the base (0.15 = +15%; the cooldown field is a
    /// reduction, 0.15 = 15% shorter). Modifiers only ever add up; they never touch a shared base asset.
    /// </summary>
    [Serializable]
    public struct StatModifiers
    {
        public int maxHealth;
        public float moveSpeed;
        public float attackDamage;
        public float abilityPower;
        public float abilityCooldownReduction;

        public static StatModifiers Combine(StatModifiers a, StatModifiers b) => new StatModifiers
        {
            maxHealth = a.maxHealth + b.maxHealth,
            moveSpeed = a.moveSpeed + b.moveSpeed,
            attackDamage = a.attackDamage + b.attackDamage,
            abilityPower = a.abilityPower + b.abilityPower,
            abilityCooldownReduction = a.abilityCooldownReduction + b.abilityCooldownReduction,
        };
    }
}
