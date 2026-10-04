using UnityEngine;

namespace Blackglass
{
    /// <summary>Prototype melee attack: fixed range, damage and cooldown. Cooldown uses scaled time.</summary>
    public sealed class UnitAttacker : MonoBehaviour
    {
        [SerializeField, Min(0.1f)] float range = 2f;
        [SerializeField, Min(0)] int damage = 25;
        [SerializeField, Min(0f)] float cooldown = 1f;

        float nextAttackTime;

        public float Range => range;
        public int Damage => damage;
        public float Cooldown => cooldown;

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
            target.TakeDamage(damage);
            return true;
        }
    }
}
