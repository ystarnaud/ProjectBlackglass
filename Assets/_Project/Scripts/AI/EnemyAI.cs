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
    /// again. Line of sight (UnitAttacker.HasLineOfSight) gates acquisition only: a target once taken is followed
    /// around corners. Runs on simulation time, so it freezes while paused.
    /// </summary>
    [RequireComponent(typeof(CommandableUnit), typeof(Health), typeof(UnitAttacker))]
    public sealed class EnemyAI : MonoBehaviour
    {
        [SerializeField] Encounter encounter;
        [SerializeField, Min(0f)] float detectionRange = 12f;
        [SerializeField, Min(0f)] float thinkInterval = 0.25f;

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
            foreach (var candidate in encounter.Friendlies)
            {
                if (candidate == null || !candidate.IsAlive || !candidate.gameObject.activeInHierarchy)
                    continue;
                var offset = candidate.transform.position - transform.position;
                offset.y = 0f;
                var distance = offset.magnitude;
                if (distance > detectionRange || distance >= bestDistance)
                    continue;
                if (!Attacker.HasLineOfSight(candidate))
                    continue;
                best = candidate;
                bestDistance = distance;
            }
            return best;
        }
    }
}
