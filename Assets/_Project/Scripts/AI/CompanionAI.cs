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
    /// any pending order, is left alone. A companion holding cover it was ordered into does not follow, and assists
    /// only against a target it can attack from where it stands; cover it merely stopped on changes nothing. Both
    /// checks are made when its own order is issued (acquisition only, like the rest). Following has two more
    /// conditions (decision 022): the follow flag on ActiveCharacter is on, and the companion is attached rather than
    /// parked. It is parked by a switch of the controlled character that leaves it farther than the follow start
    /// distance from the new leader, by an explicit order (not a retaliation) and by a Stop, and it is attached again
    /// when the controlled character walks up to it (the magnet). A freshly spawned mission squad is held (decision
    /// 028): a held companion does not start a follow move until the controlled character first moves, so units
    /// spawned a little apart do not squad up on their own. Decides only what to do; CommandableUnit does it.
    /// Runs on simulation time.
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
        // Optional: without it every hostile is a valid assist target (decision 037).
        [SerializeField] IntelligenceService intelligence;
        [Header("Follow")]
        // Where a follow move aims: this far from the leader, on the companion's side.
        [SerializeField, Min(0f)] float followDistance = 3.5f;
        // A companion farther than this starts following; the gap to followDistance stops it from pacing.
        [SerializeField, Min(0f)] float followStartDistance = 6f;
        // A running follow move is re-aimed once the leader has moved this far from where it was aimed.
        [SerializeField, Min(0f)] float followRepathDistance = 2f;
        // A parked companion is attached again when the controlled character comes this close (flat distance).
        [SerializeField, Min(0f)] float magnetRadius = 4.5f;
        [Header("Assist")]
        [SerializeField, Min(0f)] float assistRange = 10f;
        [SerializeField, Min(0f)] float thinkInterval = 0.25f;

        CommandableUnit unit;
        UnitMover mover;
        UnitAttacker attacker;
        UnitCover coverComponent;
        AutoRetaliate retaliate;
        bool retaliateLookedUp;
        float nextThinkTime;
        // The order this component issued, while it is still the unit's current order.
        UnitCommand ownCommand;
        Vector3 leaderPositionAtIssue;
        // Attached companions follow; parked ones stay where they are until the magnet attaches them (decision 022).
        bool isParked;
        // Who the controlled character was last frame, to see a switch; leaderSeen tells "no frame yet" from "nobody".
        CommandableUnit lastLeader;
        bool leaderSeen;
        // The unit's StopCount as of the last frame, to see a Stop; our own Stops are folded in so they never park us.
        int seenStopCount;
        // Whether the controlled character was inside the magnet radius last frame: the magnet fires on entering.
        bool inMagnet;
        // Held at spawn: no follow move until the controlled character first moves (decision 028).
        bool isHeld;
        // Where the controlled character was last frame, for the hold's release test; re-seeded on a leader change.
        Vector3 lastLeaderPosition;
        bool hasLeaderPosition;
        // The controlled character counts as moving above this flat speed (the same figure as UnitCover's still speed).
        const float LeaderMovingSpeed = 0.5f;
        Func<Vector3, bool> canReach;
        // Engaged and known to the player, built once so ticks allocate nothing.
        Func<Health, bool> engagedAndKnown;

        /// <summary>How far from the controlled character a follow move aims.</summary>
        public float FollowDistance => followDistance;
        /// <summary>Beyond this distance an idle companion starts following.</summary>
        public float FollowStartDistance => followStartDistance;
        /// <summary>Hostiles farther than this are never assisted against.</summary>
        public float AssistRange => assistRange;
        /// <summary>A parked companion is attached again when the controlled character comes within this distance.</summary>
        public float MagnetRadius => magnetRadius;

        /// <summary>
        /// True while this companion is parked: it does not follow, whatever the follow flag says, until the controlled
        /// character walks up to it. The controlled character itself is never parked.
        /// </summary>
        public bool IsParked => isParked;

        /// <summary>
        /// True while this companion is held at spawn: it starts no follow move until the controlled character first
        /// moves. Assist, retaliation and explicit orders are unaffected.
        /// </summary>
        public bool IsHeld => isHeld;

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
        UnitAttacker Attacker => attacker != null ? attacker : attacker = GetComponent<UnitAttacker>();
        UnitCover Cover => coverComponent != null ? coverComponent : coverComponent = Unit.Cover;

        bool IsControlled => activeCharacter != null && activeCharacter.Unit == Unit;

        // Our order only counts while it is still current: Replace by anyone else, Stop, death or direct control all
        // make a different (or no) order current, which reads as "not ours".
        UnitCommand OwnCommand => ownCommand != null && Unit.CurrentCommand == ownCommand ? ownCommand : null;

        internal void Initialize(ActiveCharacter active, Encounter encounterToAssist, float follow = 3.5f, float followStart = 6f,
            float followRepath = 2f, float assist = 10f, float interval = 0.25f, float magnet = 4.5f)
        {
            activeCharacter = active;
            encounter = encounterToAssist;
            followDistance = follow;
            followStartDistance = followStart;
            followRepathDistance = followRepath;
            assistRange = assist;
            thinkInterval = interval;
            magnetRadius = magnet;
        }

        internal void SetIntelligence(IntelligenceService service) => intelligence = service;

        internal void Wire(ActiveCharacter active, Encounter encounterToAssist)
        {
            activeCharacter = active;
            encounter = encounterToAssist;
        }

        /// <summary>
        /// Holds this companion where it stands until the controlled character first moves (flat speed above 0.5 m/s,
        /// or a non-zero move intent). A switch of the controlled character does not release it; the new one has to move.
        /// </summary>
        internal void HoldUntilLeaderMoves()
        {
            isHeld = true;
            hasLeaderPosition = false;
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
            // Every frame, not every tick: a switch, a Stop or a walk past the magnet radius can last a single frame.
            TrackAttachment();
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
                seenStopCount = Unit.StopCount;   // our own Stop is not the player's: it must not park us
            }
            return true;
        }

        /// <summary>
        /// Keeps the parked/attached state (decision 022), every simulation frame. The controlled character is
        /// attached by definition. Otherwise a unit is parked by a switch of the controlled character that leaves it
        /// beyond the follow start distance from the new leader, by any explicit order (a current order that is neither
        /// ours nor a retaliation, or anything pending) and by a Stop; parked is sticky, so finishing the orders leaves
        /// it where it ended. It is attached again only by the controlled character ENTERING the magnet radius, never
        /// on a switch frame: a level would undo a Stop or a finished order next to the leader on the next frame.
        /// Finally a follow move that is running while the unit is parked or follow is off is stopped.
        /// </summary>
        internal void TrackAttachment()
        {
            var leaderUnit = activeCharacter != null && activeCharacter.HasUnit ? activeCharacter.Unit : null;
            var leaderChanged = leaderSeen && leaderUnit != lastLeader;
            lastLeader = leaderUnit;
            leaderSeen = true;
            var stopped = Unit.StopCount != seenStopCount;
            seenStopCount = Unit.StopCount;

            if (IsControlled)
            {
                isParked = false;
                inMagnet = false;
                isHeld = false;   // the player's unit now
                return;
            }

            if (isHeld)
                TrackLeaderMotion(leaderUnit, leaderChanged);

            var distance = leaderUnit != null ? FlatDistance(leaderUnit.transform.position, transform.position) : float.PositiveInfinity;
            var inMagnetNow = distance <= magnetRadius;
            var current = Unit.CurrentCommand;
            var hasExplicit = (current != null && current != ownCommand && !IsRetaliation(current)) || Unit.PendingCommands.Count > 0;

            if (leaderChanged && distance > followStartDistance)
                isParked = true;
            if (stopped || hasExplicit)
                isParked = true;
            else if (isParked && inMagnetNow && !inMagnet && !leaderChanged)
                isParked = false;
            inMagnet = inMagnetNow;

            // Never wipe the player's queue: StopCommand clears all of it (the same guard as YieldToControl). The check
            // reads the unit's current order, because ownCommand may be stale here.
            if (OwnCommand is MoveCommand && (isParked || (activeCharacter != null && !activeCharacter.IsFollowOn)) && Unit.PendingCommands.Count == 0)
            {
                Unit.Issue(new StopCommand());
                ownCommand = null;
                seenStopCount = Unit.StopCount;   // our own Stop must not park us
            }
        }

        // Releases the spawn hold when the controlled character moves. Its speed is measured from its position change
        // between simulation frames (it counts a held move key, a path and a push alike); the frame of a leader change
        // only seeds the new leader's position.
        void TrackLeaderMotion(CommandableUnit leaderUnit, bool leaderChanged)
        {
            if (leaderUnit == null)
            {
                hasLeaderPosition = false;
                return;
            }
            var position = leaderUnit.transform.position;
            if (LeaderMoved(hasLeaderPosition && !leaderChanged, lastLeaderPosition, position, Time.deltaTime, leaderUnit.MoveIntent, LeaderMovingSpeed))
                isHeld = false;
            lastLeaderPosition = position;
            hasLeaderPosition = true;
        }

        /// <summary>
        /// Whether the controlled character moved since the last frame: its flat speed over `deltaTime` is above
        /// `speedThreshold`, or it has a non-zero move intent. With no previous position (the first frame, or the frame
        /// of a leader change) or no elapsed time, it is not moving.
        /// </summary>
        internal static bool LeaderMoved(bool hasLastPosition, Vector3 lastPosition, Vector3 position, float deltaTime,
            Vector3 moveIntent, float speedThreshold)
        {
            if (!hasLastPosition || deltaTime <= 0f)
                return false;
            return moveIntent != Vector3.zero || FlatDistance(lastPosition, position) / deltaTime > speedThreshold;
        }

        // AutoRetaliate is optional (a plain companion may not carry one); looked up once, found or not.
        bool IsRetaliation(UnitCommand command)
        {
            if (!retaliateLookedUp)
            {
                TryGetComponent(out retaliate);
                retaliateLookedUp = true;
            }
            return retaliate != null && retaliate.IsRetaliating(command);
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
                // Ordered cover is held: a companion that cannot hit the target from where it stands does not charge.
                if (Cover.OccupiedByOrder && !Attacker.CanAttack(target))
                    return;
                IssueOwn(new AttackCommand(target));
                return;
            }
            // Ordered cover is held instead of following; a point the companion merely stopped on is not.
            if (Cover.OccupiedByOrder)
                return;

            // Following needs the flag on, an attached unit and no spawn hold; assist and cover above never look at any.
            if (activeCharacter == null || !activeCharacter.IsFollowOn || isParked || isHeld || !activeCharacter.HasUnit || !activeCharacter.Unit.IsAlive)
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

        internal Health ChooseAssistTarget()
        {
            if (encounter == null)
                return null;
            Health leaderTarget = null;
            if (activeCharacter != null && activeCharacter.HasUnit)
                leaderTarget = activeCharacter.Unit.AttackTarget;   // also set while the leader walks to cover with an attack queued
            canReach ??= Mover.CanReach;   // plain delegates (this one and the engagement test), cached so ticks allocate nothing
            engagedAndKnown ??= hostile => Knowledge.CanTarget(intelligence, hostile) && IsEngaged(hostile);
            return ChooseAssistTarget(transform.position, assistRange, leaderTarget, encounter.Hostiles, engagedAndKnown, canReach);
        }

        // A hostile that is attacking anyone, or walking to cover with its attack queued, is engaged; read from the
        // shared unit, never from EnemyAI.
        static bool IsEngaged(Health hostile) =>
            hostile.TryGetComponent<CommandableUnit>(out var hostileUnit) && hostileUnit.AttackTarget != null;

        // The raw follow point can sit inside a wall or past the ground edge. Snap it to the mesh (the snap radius is
        // 2 m); a point farther than that from the mesh falls back to the leader's own position, which is always on
        // the mesh. A rejected order is forgotten.
        void IssueFollow(Transform leader)
        {
            var point = FollowPoint(leader.position, transform.position, followDistance, leader.forward);
            if (!Mover.TrySnap(point, out var onMesh))
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

            if (Contains(hostiles, leaderTarget) && IsValidTarget(from, range, leaderTarget, canReach))
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
