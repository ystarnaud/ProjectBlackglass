using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Prototype melee attack: fixed range, damage and cooldown. Cooldown uses scaled time, so it freezes while
    /// paused. Reports each hit through Attacked and tells the target which Health hit it.
    /// </summary>
    public sealed class UnitAttacker : MonoBehaviour
    {
        [SerializeField, Min(0.1f)] float range = 2f;
        [SerializeField, Min(0)] int damage = 25;
        [SerializeField, Min(0f)] float cooldown = 1f;

        float nextAttackTime;
        Health ownHealth;

        public float Range => range;
        public int Damage => damage;
        public float Cooldown => cooldown;

        /// <summary>Seconds of scaled time until the next hit may land; 0 when ready.</summary>
        public float CooldownRemaining => Mathf.Max(0f, nextAttackTime - Time.time);

        /// <summary>Raised with the target after every hit.</summary>
        public event Action<Health> Attacked;

        // This unit's own Health, if it has one. Looked up lazily so a Health added after this component is found.
        Health OwnHealth => ownHealth != null ? ownHealth : ownHealth = GetComponent<Health>();

        internal void Initialize(float attackRange, int attackDamage, float attackCooldown)
        {
            range = attackRange;
            damage = attackDamage;
            cooldown = attackCooldown;
        }

        /// <summary>Horizontal centre-to-centre distance check.</summary>
        public bool IsInRange(Health target)
        {
            if (target == null)
                return false;
            var offset = target.transform.position - transform.position;
            offset.y = 0f;
            return offset.sqrMagnitude <= range * range;
        }

        /// <summary>Hits the target if it is alive, in range and the cooldown has elapsed. Returns whether it hit.</summary>
        public bool TryAttack(Health target)
        {
            if (target == null || !target.IsAlive || !IsInRange(target) || Time.time < nextAttackTime)
                return false;
            nextAttackTime = Time.time + cooldown;
            target.TakeDamage(damage, OwnHealth);
            Attacked?.Invoke(target);
            return true;
        }
    }
}
