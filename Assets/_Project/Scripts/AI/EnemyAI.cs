using UnityEngine;

namespace Blackglass
{
    /// <summary>What a hostile is doing, as EnemyAI.DeriveState reads it from its unit.</summary>
    public enum EnemyState
    {
        Idle,
        Chase,
        Attack,
        Dead,
    }

    /// <summary>
    /// The smallest hostile brain: while idle, every think tick it looks for the nearest living friendly inside its
    /// detection radius that it can see, and attacks it through the normal order path. CommandableUnit then chases,
    /// stops and hits; when the target dies or cannot be reached that order ends on its own and the brain looks
    /// again. Line of sight gates acquisition only: a target once taken is followed around corners. Runs on
    /// simulation time, so it freezes while paused.
    /// </summary>
    [RequireComponent(typeof(CommandableUnit), typeof(Health), typeof(UnitAttacker))]
    public sealed class EnemyAI : MonoBehaviour
    {
        // Enough for the few units and walls a sight line can cross in the prototype.
        const int SightHitBufferSize = 8;

        [SerializeField] Encounter encounter;
        [SerializeField, Min(0f)] float detectionRange = 12f;
        [SerializeField, Min(0f)] float thinkInterval = 0.25f;
        [SerializeField] LayerMask sightBlockers = ~0;
        // Above the unit's pivot (the capsule centre, 1 m up), so the eye is at 1.5 m.
        [SerializeField] float eyeHeight = 0.5f;

        readonly RaycastHit[] sightHits = new RaycastHit[SightHitBufferSize];
        CommandableUnit unit;
        UnitAttacker attacker;
        float nextThinkTime;

        public float DetectionRange => detectionRange;

        /// <summary>The friendly this hostile is after, or null while idle.</summary>
        public Health Target => Unit.CurrentCommand is AttackCommand attack ? attack.Target : null;

        /// <summary>Derived each read; nothing is stored. Debug views show it.</summary>
        public EnemyState State => DeriveState(Unit.IsAlive, Unit.CurrentCommand, Target != null && Attacker.IsInRange(Target));

        CommandableUnit Unit => unit != null ? unit : unit = GetComponent<CommandableUnit>();
        UnitAttacker Attacker => attacker != null ? attacker : attacker = GetComponent<UnitAttacker>();

        internal void Initialize(Encounter encounterToFight, float range = 12f, float interval = 0.25f)
        {
            encounter = encounterToFight;
            detectionRange = range;
            thinkInterval = interval;
        }

        /// <summary>Dead beats everything; an attack order is Chase out of range and Attack in range; otherwise Idle.</summary>
        public static EnemyState DeriveState(bool alive, UnitCommand current, bool inRange)
        {
            if (!alive)
                return EnemyState.Dead;
            if (current is AttackCommand)
                return inRange ? EnemyState.Attack : EnemyState.Chase;
            return EnemyState.Idle;
        }

        /// <summary>
        /// True when nothing but units lies between the eye and the target's centre. Units (anything with a Health in
        /// its parents) never block sight; other colliders do. Uses the given buffer so think ticks allocate nothing.
        /// </summary>
        public static bool HasLineOfSight(Vector3 eye, Health target, LayerMask blockers, RaycastHit[] buffer)
        {
            var toTarget = target.transform.position - eye;
            var distance = toTarget.magnitude;
            if (distance <= 0.001f)
                return true;
            var count = Physics.RaycastNonAlloc(eye, toTarget / distance, buffer, distance, blockers, QueryTriggerInteraction.Ignore);
            for (var i = 0; i < count; i++)
            {
                if (buffer[i].collider.GetComponentInParent<Health>() == null)
                    return false;
            }
            return true;
        }

        void OnEnable()
        {
            if (encounter == null)
                Debug.LogWarning($"{name} has no Encounter wired, so it will never acquire a target.", this);
        }

        void Update()
        {
            if (!SimulationTime.IsRunning || !Unit.IsAlive)
                return;
            if (Time.time < nextThinkTime)
                return;
            nextThinkTime = Time.time + thinkInterval;

            // Busy units are left alone: CommandableUnit runs the chase and the attack.
            if (Unit.CurrentCommand != null || encounter == null)
                return;
            var target = FindNearestVisibleFriendly();
            if (target != null)
                Unit.Issue(new AttackCommand(target));
        }

        Health FindNearestVisibleFriendly()
        {
            Health best = null;
            var bestDistance = float.PositiveInfinity;
            var eye = transform.position + Vector3.up * eyeHeight;
            foreach (var candidate in encounter.Friendlies)
            {
                if (candidate == null || !candidate.IsAlive || !candidate.gameObject.activeInHierarchy)
                    continue;
                var offset = candidate.transform.position - transform.position;
                offset.y = 0f;
                var distance = offset.magnitude;
                if (distance > detectionRange || distance >= bestDistance)
                    continue;
                if (!HasLineOfSight(eye, candidate, sightBlockers, sightHits))
                    continue;
                best = candidate;
                bestDistance = distance;
            }
            return best;
        }
    }
}
