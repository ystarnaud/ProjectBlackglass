using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>What a hostile is doing, as EnemyAI.DeriveState reads it from its unit.</summary>
    public enum EnemyState
    {
        Idle,
        Chase,
        Reposition,
        Attack,
        /// <summary>Walking to a cover point before attacking.</summary>
        Cover,
        Dead,
    }

    /// <summary>
    /// The smallest hostile brain: while idle, every think tick it looks for the nearest living friendly inside its
    /// detection radius that it can see and reach, and attacks it through the normal order path. A ranged hostile
    /// first looks for useful cover nearby (unclaimed or its own, protecting from the target, letting it shoot the
    /// target, reachable) and, when there is some, walks there before attacking; a melee hostile never looks.
    /// CommandableUnit then chases, stops and hits; when the target dies or cannot be reached that order ends on its
    /// own and the brain looks again. Line of sight (UnitAttacker.HasLineOfSight) gates acquisition only: a target
    /// once taken is followed around corners. Cover is chosen at acquisition only. Runs on simulation time, so it
    /// freezes while paused.
    /// </summary>
    [RequireComponent(typeof(CommandableUnit), typeof(Health), typeof(UnitAttacker))]
    public sealed class EnemyAI : MonoBehaviour
    {
        [SerializeField] Encounter encounter;
        [SerializeField, Min(0f)] float detectionRange = 12f;
        [SerializeField, Min(0f)] float thinkInterval = 0.25f;
        [Header("Cover")]
        // Optional: without a registry the hostile never seeks cover.
        [SerializeField] CoverRegistry coverRegistry;
        [SerializeField, Min(0f)] float coverSearchRange = 8f;

        CommandableUnit unit;
        UnitAttacker attacker;
        UnitMover mover;
        float nextThinkTime;
        // The target of the search in progress, read by the predicate (a cached delegate, so ticks allocate nothing).
        Health coverTarget;
        Func<CoverLocation, bool> isUsefulCover;

        public float DetectionRange => detectionRange;
        public float CoverSearchRange => coverSearchRange;

        /// <summary>The friendly this hostile is after (attacking, or about to once it reaches cover), or null while idle.</summary>
        public Health Target => Unit.AttackTarget;

        /// <summary>Derived each read; nothing is stored. Debug views show it.</summary>
        public EnemyState State => DeriveState(Unit.IsAlive, Unit.CurrentCommand, Unit.AttackPhase);

        internal bool IsCoverWired => coverRegistry != null;

        CommandableUnit Unit => unit != null ? unit : unit = GetComponent<CommandableUnit>();
        UnitAttacker Attacker => attacker != null ? attacker : attacker = GetComponent<UnitAttacker>();
        UnitMover Mover => mover != null ? mover : mover = GetComponent<UnitMover>();

        internal void Initialize(Encounter encounterToFight, float range = 12f, float interval = 0.25f, CoverRegistry registry = null)
        {
            encounter = encounterToFight;
            detectionRange = range;
            thinkInterval = interval;
            coverRegistry = registry;
        }

        /// <summary>
        /// Dead beats everything; a cover order is Cover; an attack order maps its phase to Chase, Reposition or
        /// Attack; otherwise Idle.
        /// </summary>
        public static EnemyState DeriveState(bool alive, UnitCommand current, AttackPhase phase)
        {
            if (!alive)
                return EnemyState.Dead;
            if (current is MoveToCoverCommand)
                return EnemyState.Cover;
            if (!(current is AttackCommand))
                return EnemyState.Idle;
            switch (phase)
            {
                case AttackPhase.Reposition:
                    return EnemyState.Reposition;
                case AttackPhase.Attack:
                    return EnemyState.Attack;
                default:
                    return EnemyState.Chase;
            }
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

            // Busy units are left alone: CommandableUnit runs the walk, the chase and the attack.
            if (Unit.CurrentCommand != null || encounter == null)
                return;
            var target = FindTarget();
            if (target == null)
                return;
            // The cover order can still be refused (someone claimed the point this frame); then attack without cover.
            if (Attacker.NeedsLineOfSight && TryFindCover(target, out var point) && Unit.Issue(new MoveToCoverCommand(point)))
            {
                Unit.Issue(new AttackCommand(target), IssueMode.Append);
                return;
            }
            Unit.Issue(new AttackCommand(target));
        }

        // Nearest friendly that is alive, active, inside the detection radius, in sight and reachable. Distance and
        // sight are tested first, so the path (the dearest check) is computed only for candidates that could win.
        Health FindTarget()
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
                if (!Attacker.HasLineOfSight(candidate) || !Mover.CanReach(candidate.transform.position))
                    continue;
                best = candidate;
                bestDistance = distance;
            }
            return best;
        }

        // The nearest registry point within coverSearchRange that IsUsefulCover admits against this target.
        bool TryFindCover(Health target, out CoverLocation point)
        {
            point = null;
            if (coverRegistry == null)
                return false;
            coverTarget = target;
            isUsefulCover ??= IsUsefulCover;
            var found = CoverRules.TryChooseNearest(coverRegistry.Points, transform.position, coverSearchRange, isUsefulCover, out point);
            coverTarget = null;
            return found;
        }

        // Unclaimed or ours; protects the point from the target (one obstacle ray); the target can be attacked from
        // the eye a unit would have there (range and sight); reachable (a path, so last).
        bool IsUsefulCover(CoverLocation point)
        {
            if (point.IsClaimed && !point.IsClaimedBy(Unit.Cover))
                return false;
            var pivot = point.Position + Vector3.up * Mover.PivotHeight;
            return point.ProtectsFrom(coverTarget.transform.position, pivot)
                && Attacker.CanAttackFrom(pivot, coverTarget)
                && Mover.CanReach(point.Position);
        }
    }
}
