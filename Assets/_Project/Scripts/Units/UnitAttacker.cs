using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>How a unit fights. Plain data on UnitAttacker; there is no class system.</summary>
    public enum CombatRole
    {
        /// <summary>Hits anything within range; walls do not matter at 2 m.</summary>
        Melee,
        /// <summary>Hits from its range, but only with a clear line of sight to the target.</summary>
        Ranged,
    }

    /// <summary>
    /// Prototype attack: a combat role (melee or ranged), fixed range, damage and cooldown. A ranged hit also needs line of sight. Cooldown uses scaled time, so it freezes while paused.
    /// Reports each hit through Attacked and tells the target which Health hit it. Owns the unit's sight test
    /// (LineOfSight from this unit's eye), which EnemyAI uses for acquisition.
    /// </summary>
    public sealed class UnitAttacker : MonoBehaviour
    {
        [SerializeField] CombatRole role = CombatRole.Melee;
        [SerializeField, Min(0.1f)] float range = 2f;
        [SerializeField, Min(0)] int damage = 25;
        [SerializeField, Min(0f)] float cooldown = 1f;
        // Everything blocks sight except units (see LineOfSight). Serialized so a layer scheme can narrow it later.
        [SerializeField] LayerMask sightBlockers = ~0;

        readonly RaycastHit[] sightHits = new RaycastHit[LineOfSight.HitBufferSize];
        float nextAttackTime;
        Health ownHealth;

        public float Range => range;
        public int Damage => damage;
        public float Cooldown => cooldown;
        public CombatRole Role => role;

        /// <summary>Ranged units need a clear line to hit; melee units do not.</summary>
        public bool NeedsLineOfSight => role == CombatRole.Ranged;

        /// <summary>Seconds of scaled time until the next hit may land; 0 when ready.</summary>
        public float CooldownRemaining => Mathf.Max(0f, nextAttackTime - Time.time);

        /// <summary>Raised with the target after every hit.</summary>
        public event Action<Health> Attacked;

        // This unit's own Health, if it has one. Looked up lazily so a Health added after this component is found.
        Health OwnHealth => ownHealth != null ? ownHealth : ownHealth = GetComponent<Health>();

        internal void Initialize(float attackRange, int attackDamage, float attackCooldown, CombatRole combatRole = CombatRole.Melee)
        {
            range = attackRange;
            damage = attackDamage;
            cooldown = attackCooldown;
            role = combatRole;
        }

        /// <summary>Horizontal centre-to-centre distance check from this unit's position.</summary>
        public bool IsInRange(Health target) => IsInRangeFrom(transform.position, target);

        /// <summary>The same check from another pivot, for choosing a firing position.</summary>
        public bool IsInRangeFrom(Vector3 pivot, Health target)
        {
            if (target == null)
                return false;
            var offset = target.transform.position - pivot;
            offset.y = 0f;
            return offset.sqrMagnitude <= range * range;
        }

        /// <summary>True when the line from this unit's eye to the target's pivot crosses no world geometry.</summary>
        public bool HasLineOfSight(Health target) => HasLineOfSightFrom(transform.position, target);

        /// <summary>The same test from the eye a unit would have standing at `pivot`.</summary>
        public bool HasLineOfSightFrom(Vector3 pivot, Health target) =>
            target != null && LineOfSight.IsClear(pivot, target, sightBlockers, sightHits);

        /// <summary>The one "could I hit it from here" test: in range, and for a ranged unit in sight.</summary>
        public bool CanAttack(Health target) => CanAttackFrom(transform.position, target);

        /// <summary>The CanAttack test from another pivot, for choosing a firing position.</summary>
        public bool CanAttackFrom(Vector3 pivot, Health target) =>
            IsInRangeFrom(pivot, target) && (!NeedsLineOfSight || HasLineOfSightFrom(pivot, target));

        /// <summary>Hits the target if it is alive, attackable from here (range, and sight for ranged units) and the cooldown has elapsed. Returns whether it hit.</summary>
        public bool TryAttack(Health target)
        {
            if (target == null || !target.IsAlive || !CanAttack(target) || Time.time < nextAttackTime)
                return false;
            nextAttackTime = Time.time + cooldown;
            target.TakeDamage(damage, OwnHealth);
            Attacked?.Invoke(target);
            return true;
        }
    }
}
