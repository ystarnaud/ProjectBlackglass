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
    /// Prototype attack: a combat role (melee or ranged), fixed range, damage and cooldown. A ranged hit also needs
    /// line of sight, and a ranged shot at a target occupying cover that protects it from here lands only with that
    /// cover's hit chance; melee ignores cover. Cooldown uses scaled time, so it freezes while paused. Reports each
    /// hit through Attacked and each miss through Missed, and tells the target which Health hit it. Owns the unit's
    /// sight test (LineOfSight from this unit's eye), which EnemyAI uses for acquisition. An assigned CombatArchetype
    /// overrides the inline values.
    /// </summary>
    public sealed class UnitAttacker : MonoBehaviour
    {
        [SerializeField] CombatRole role = CombatRole.Melee;
        [SerializeField, Min(0.1f)] float range = 2f;
        [SerializeField, Min(0)] int damage = 25;
        [SerializeField, Min(0f)] float cooldown = 1f;
        // Everything blocks sight except units (see LineOfSight). Serialized so a layer scheme can narrow it later.
        [SerializeField] LayerMask sightBlockers = ~0;
        // Optional preset. When assigned it overrides the inline role, range, damage and cooldown at Awake.
        [SerializeField] CombatArchetype archetype;

        readonly RaycastHit[] sightHits = new RaycastHit[LineOfSight.HitBufferSize];
        float nextAttackTime;
        // The roll is replaceable so tests can force a hit or a miss; the default is Unity's random stream.
        static readonly Func<float> defaultRoll = () => UnityEngine.Random.value;
        Func<float> hitRoll;
        Health ownHealth;

        public float Range => range;
        public int Damage => damage;
        public float Cooldown => cooldown;
        public CombatRole Role => role;

        /// <summary>The assigned combat preset, or null when the unit is configured inline.</summary>
        public CombatArchetype Archetype => archetype;

        /// <summary>Ranged units need a clear line to hit; melee units do not.</summary>
        public bool NeedsLineOfSight => role == CombatRole.Ranged;

        /// <summary>Seconds of scaled time until the next hit may land; 0 when ready.</summary>
        public float CooldownRemaining => Mathf.Max(0f, nextAttackTime - Time.time);

        /// <summary>Raised with the target after every hit.</summary>
        public event Action<Health> Attacked;

        /// <summary>Raised with the target after every shot that cover turned away.</summary>
        public event Action<Health> Missed;

        /// <summary>Shots fired since creation (debug counter).</summary>
        public int ShotsFired { get; private set; }

        /// <summary>Shots that landed (debug counter).</summary>
        public int Hits { get; private set; }

        /// <summary>The 0..1 roll compared with a covered target's hit chance. A plain delegate, so ?? is fine.</summary>
        internal Func<float> HitRoll
        {
            get => hitRoll ?? defaultRoll;
            set => hitRoll = value;
        }

        // This unit's own Health, if it has one. Looked up lazily so a Health added after this component is found.
        Health OwnHealth => ownHealth != null ? ownHealth : ownHealth = GetComponent<Health>();

        internal void Initialize(float attackRange, int attackDamage, float attackCooldown, CombatRole combatRole = CombatRole.Melee)
        {
            range = attackRange;
            damage = attackDamage;
            cooldown = attackCooldown;
            role = combatRole;
        }

        void Awake()
        {
            if (archetype != null)
                ApplyArchetype(archetype);
        }

        /// <summary>Copies a preset's role, range, damage and attack interval onto this attacker.</summary>
        public void ApplyArchetype(CombatArchetype preset)
        {
            if (preset == null)
                throw new ArgumentNullException(nameof(preset));
            archetype = preset;
            role = preset.Role;
            range = preset.Range;
            damage = preset.Damage;
            cooldown = preset.AttackInterval;
        }

        /// <summary>The 0..1 roll the unit uses against a target's cover chance; abilities share it so tests can force it.</summary>
        internal float RollHit() => HitRoll();

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

        /// <summary>True when the line from this unit's eye to the point crosses no world geometry (units never block).</summary>
        public bool HasLineOfSightToPoint(Vector3 point) => HasLineOfSightToPointFrom(transform.position, point);

        /// <summary>The same test from the eye a unit would have standing at `pivot`.</summary>
        public bool HasLineOfSightToPointFrom(Vector3 pivot, Vector3 point) =>
            LineOfSight.IsClear(pivot + Vector3.up * LineOfSight.EyeHeight, point, sightBlockers, sightHits);

        /// <summary>The one "could I hit it from here" test: in range, and for a ranged unit in sight.</summary>
        public bool CanAttack(Health target) => CanAttackFrom(transform.position, target);

        /// <summary>The CanAttack test from another pivot, for choosing a firing position.</summary>
        public bool CanAttackFrom(Vector3 pivot, Health target) =>
            IsInRangeFrom(pivot, target) && (!NeedsLineOfSight || HasLineOfSightFrom(pivot, target));

        /// <summary>
        /// True when the target is in cover against a shot from this unit's position: ranged role, a target with a
        /// UnitCover that occupies a point, and that point protecting it from here. Melee: always false. hitChance
        /// is that point's chance when in cover, else 1. The HUD uses the same overload.
        /// </summary>
        public bool IsTargetInCover(Health target, out float hitChance)
        {
            hitChance = 1f;
            if (!NeedsLineOfSight || target == null || !target.TryGetComponent<UnitCover>(out var cover)
                || !cover.IsProtectedFrom(transform.position))
                return false;
            hitChance = cover.HitChance;
            return true;
        }

        public bool IsTargetInCover(Health target) => IsTargetInCover(target, out _);

        /// <summary>
        /// Fires at the target if it is alive, attackable from here (range, and sight for ranged units) and the
        /// cooldown has elapsed. A target in cover against this unit is hit with its cover's chance; otherwise every
        /// shot lands. Returns whether a shot was fired (hit or miss); Attacked and Missed say which.
        /// </summary>
        public bool TryAttack(Health target)
        {
            if (target == null || !target.IsAlive || !CanAttack(target) || Time.time < nextAttackTime)
                return false;
            nextAttackTime = Time.time + cooldown;
            ShotsFired++;
            var inCover = IsTargetInCover(target, out var hitChance);
            if (CoverRules.ResolveHit(inCover, hitChance, HitRoll()))
            {
                Hits++;
                target.TakeDamage(damage, OwnHealth);
                Attacked?.Invoke(target);
            }
            else
            {
                Missed?.Invoke(target);
            }
            return true;
        }
    }
}
