using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>What a companion is doing, as CompanionAI.DeriveState reads it. Debug views show it.</summary>
    public enum CompanionState
    {
        Dead,
        /// <summary>This unit is the directly controlled character: no autonomy.</summary>
        Controlled,
        /// <summary>Running an order it did not give itself (the player's, or a retaliation), or has orders queued.</summary>
        Orders,
        /// <summary>Running its own attack on a hostile.</summary>
        Assist,
        /// <summary>Running its own move toward the controlled character.</summary>
        Follow,
        Idle,
    }

    /// <summary>
    /// A friendly unit's autonomy while the player is not telling it what to do. Priority: dead, controlled, explicit
    /// orders, assist (attack a hostile that is fighting the squad), follow the controlled character, idle. It tells
    /// its own orders from everyone else's by remembering the command object it issued: any other current order, or
    /// any pending order, is left alone. Decides only what to do; CommandableUnit does it. Runs on simulation time.
    /// </summary>
    // After ActiveCharacter (-200) refreshes who is controlled and before DirectControlInput (-100) hands over, so a
    // companion that just became the controlled character has already dropped its own follow move when the hand-over
    // looks at its orders, and a held move key carries over as decision 012 intends.
    [DefaultExecutionOrder(-150)]
    [RequireComponent(typeof(CommandableUnit), typeof(Health), typeof(UnitAttacker))]
    public sealed class CompanionAI : MonoBehaviour
    {
        [SerializeField] ActiveCharacter activeCharacter;
        [SerializeField] Encounter encounter;
        [Header("Follow")]
        // Where a follow move aims: this far from the leader, on the companion's side.
        [SerializeField, Min(0f)] float followDistance = 3.5f;
        // A companion farther than this starts following; the gap to followDistance stops it from pacing.
        [SerializeField, Min(0f)] float followStartDistance = 6f;
        // A running follow move is re-aimed once the leader has moved this far from where it was aimed.
        [SerializeField, Min(0f)] float followRepathDistance = 2f;
        [Header("Assist")]
        [SerializeField, Min(0f)] float assistRange = 10f;
        [SerializeField, Min(0f)] float thinkInterval = 0.25f;

        CommandableUnit unit;
        UnitMover mover;
        float nextThinkTime;
        // The order this component issued, while it is still the unit's current order.
        UnitCommand ownCommand;
        Vector3 leaderPositionAtIssue;
        Func<Vector3, bool> canReach;
        // The method group converted once; passing IsEngaged directly would allocate a new delegate on every tick.
        static readonly Func<Health, bool> isEngaged = IsEngaged;

        public float FollowDistance => followDistance;
        public float FollowStartDistance => followStartDistance;
        public float AssistRange => assistRange;

        /// <summary>True when the scene wired both references this component needs.</summary>
        public bool IsWired => activeCharacter != null && encounter != null;

        /// <summary>Derived each read; nothing is stored except our own command.</summary>
        public CompanionState State =>
            DeriveState(Unit.IsAlive, IsControlled, Unit.CurrentCommand, Unit.PendingCommands.Count, OwnCommand);

        /// <summary>The hostile our own assist order targets, or null.</summary>
        public Health AssistTarget => OwnCommand is AttackCommand attack ? attack.Target : null;

        /// <summary>True while our own follow move is the unit's current order.</summary>
        public bool IsFollowing => OwnCommand is MoveCommand;

        CommandableUnit Unit => unit != null ? unit : unit = GetComponent<CommandableUnit>();
        UnitMover Mover => mover != null ? mover : mover = GetComponent<UnitMover>();

        bool IsControlled => activeCharacter != null && activeCharacter.Unit == Unit;

        // Our order only counts while it is still current: Replace by anyone else, Stop, death or direct control all
        // make a different (or no) order current, which reads as "not ours".
        UnitCommand OwnCommand => ownCommand != null && Unit.CurrentCommand == ownCommand ? ownCommand : null;

        internal void Initialize(ActiveCharacter active, Encounter encounterToAssist, float follow = 3.5f, float followStart = 6f,
            float followRepath = 2f, float assist = 10f, float interval = 0.25f)
        {
            activeCharacter = active;
            encounter = encounterToAssist;
            followDistance = follow;
            followStartDistance = followStart;
            followRepathDistance = followRepath;
            assistRange = assist;
            thinkInterval = interval;
        }

        void OnEnable()
        {
            if (!IsWired)
                Debug.LogWarning($"{name} has no ActiveCharacter or Encounter wired, so it will neither follow nor assist.", this);
        }

        void Update()
        {
            if (!SimulationTime.IsRunning || !Unit.IsAlive)
                return;
            // "Am I controlled now?" is answered every frame so the stop lands on the Tab frame; the rest is a tick.
            if (YieldToControl())
                return;
            if (Time.time < nextThinkTime)
                return;
            nextThinkTime = Time.time + thinkInterval;
            Think();
        }

        /// <summary>
        /// Steps 0-2 of the priority: forget a finished or replaced own order, then, if this unit is the controlled
        /// character (or is being driven), drop our own order once and report that autonomy must stay out.
        /// </summary>
        internal bool YieldToControl()
        {
            ownCommand = OwnCommand;   // forget an order that finished or was replaced
            if (!IsControlled && Unit.MoveIntent == Vector3.zero)
                return false;
            // No autonomy for the controlled character: drop our own order once, never anyone else's.
            if (ownCommand != null && Unit.PendingCommands.Count == 0)
            {
                Unit.Issue(new StopCommand());
                ownCommand = null;
            }
            return true;
        }

        /// <summary>Steps 3-6 of the priority: orders win, then assist, then follow. Internal so tests can drive it.</summary>
        internal void Think()
        {
            ownCommand = OwnCommand;

            // Explicit orders (or a retaliation) win: a current order that is not ours, or anything queued.
            if (Unit.CurrentCommand != null && (ownCommand == null || Unit.PendingCommands.Count > 0))
                return;

            // Acquisition only: a running assist is never retargeted; it ends with its target, then we look again.
            if (ownCommand is AttackCommand)
                return;
            var target = ChooseAssistTarget();
            if (target != null)
            {
                IssueOwn(new AttackCommand(target));
                return;
            }

            if (activeCharacter == null || !activeCharacter.HasUnit || !activeCharacter.Unit.IsAlive)
                return;
            var leader = activeCharacter.Unit.transform;
            var distanceToLeader = FlatDistance(leader.position, transform.position);
            if (ownCommand is MoveCommand)
            {
                // Re-aim only while still beyond the follow distance: FollowPoint lies on the far side of a companion
                // the leader has walked into, so re-aiming then would send it away from the leader.
                if (distanceToLeader > followDistance && FlatDistance(leader.position, leaderPositionAtIssue) >= followRepathDistance)
                    IssueFollow(leader);
                return;
            }
            if (distanceToLeader > followStartDistance)
                IssueFollow(leader);
        }

        Health ChooseAssistTarget()
        {
            if (encounter == null)
                return null;
            Health leaderTarget = null;
            if (activeCharacter != null && activeCharacter.HasUnit && activeCharacter.Unit.CurrentCommand is AttackCommand leaderAttack)
                leaderTarget = leaderAttack.Target;
            canReach ??= Mover.CanReach;   // plain delegates (this one and isEngaged), cached so ticks allocate nothing
            return ChooseAssistTarget(transform.position, assistRange, leaderTarget, encounter.Hostiles, isEngaged, canReach);
        }

        // A hostile that is attacking anyone is engaged; its order is read from the shared unit, not from EnemyAI.
        static bool IsEngaged(Health hostile) =>
            hostile.TryGetComponent<CommandableUnit>(out var hostileUnit) && hostileUnit.CurrentCommand is AttackCommand;

        // The raw follow point can sit inside a wall or past the ground edge, farther than MoveTo's 2 m snap; snap it
        // first and otherwise aim at the leader itself, which is always on the mesh. A rejected order is forgotten.
        void IssueFollow(Transform leader)
        {
            var point = FollowPoint(leader.position, transform.position, followDistance, leader.forward);
            if (!Mover.TrySnap(point, out var onMesh) || FlatDistance(onMesh, point) > followDistance)
                onMesh = leader.position;
            if (IssueOwn(new MoveCommand(onMesh)))
                leaderPositionAtIssue = leader.position;
        }

        // Replace is safe here: Think only reaches an issue when nothing but our own order is current.
        bool IssueOwn(UnitCommand command)
        {
            if (Unit.Issue(command))
            {
                ownCommand = command;
                return true;
            }
            ownCommand = null;
            return false;
        }

        /// <summary>
        /// Dead beats everything, then controlled. With no order the companion is idle. A current order that is not
        /// ours, or anything pending behind ours, means orders (explicit, or a retaliation). Otherwise our own attack
        /// is assist and our own move is follow.
        /// </summary>
        public static CompanionState DeriveState(bool alive, bool isControlled, UnitCommand current, int pendingCount, UnitCommand ownCommand)
        {
            if (!alive)
                return CompanionState.Dead;
            if (isControlled)
                return CompanionState.Controlled;
            if (current == null)
                return CompanionState.Idle;
            if (current != ownCommand || pendingCount > 0)
                return CompanionState.Orders;
            return ownCommand is AttackCommand ? CompanionState.Assist : CompanionState.Follow;
        }

        /// <summary>
        /// Who to assist against, when the companion has no assist order yet (a running one is never retargeted). The
        /// leader's own target first, when it is a living hostile in range and reachable; otherwise the nearest hostile
        /// that is engaged (already attacking someone), alive, active, in range and reachable. Unaware hostiles are
        /// never chosen: companions do not start fights.
        /// </summary>
        public static Health ChooseAssistTarget(Vector3 from, float range, Health leaderTarget,
            IReadOnlyList<Health> hostiles, Func<Health, bool> isEngaged, Func<Vector3, bool> canReach)
        {
            if (hostiles == null)
                throw new ArgumentNullException(nameof(hostiles));
            if (isEngaged == null)
                throw new ArgumentNullException(nameof(isEngaged));
            if (canReach == null)
                throw new ArgumentNullException(nameof(canReach));

            if (IsValidTarget(from, range, leaderTarget, canReach) && Contains(hostiles, leaderTarget))
                return leaderTarget;

            Health best = null;
            var bestDistance = float.PositiveInfinity;
            foreach (var hostile in hostiles)
            {
                if (!IsLiving(hostile))
                    continue;
                var distance = FlatDistance(from, hostile.transform.position);
                if (distance > range || distance >= bestDistance || !isEngaged(hostile) || !canReach(hostile.transform.position))
                    continue;
                best = hostile;
                bestDistance = distance;
            }
            return best;
        }

        /// <summary>
        /// The spot a follow move aims at: `followDistance` from the leader on the companion's side, at the leader's
        /// height. A companion standing on the leader aims behind it (opposite its forward), so the two part.
        /// </summary>
        public static Vector3 FollowPoint(Vector3 leader, Vector3 companion, float followDistance, Vector3 leaderForward)
        {
            var direction = companion - leader;
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.01f)
            {
                direction = -leaderForward;
                direction.y = 0f;
            }
            if (direction.sqrMagnitude < 0.0001f)
                direction = Vector3.back;
            return leader + direction.normalized * followDistance;
        }

        static bool IsLiving(Health health) => health != null && health.IsAlive && health.gameObject.activeInHierarchy;

        static bool IsValidTarget(Vector3 from, float range, Health target, Func<Vector3, bool> canReach) =>
            IsLiving(target) && FlatDistance(from, target.transform.position) <= range && canReach(target.transform.position);

        static bool Contains(IReadOnlyList<Health> list, Health health)
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i] == health)
                    return true;
            }
            return false;
        }

        static float FlatDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }
    }
}
