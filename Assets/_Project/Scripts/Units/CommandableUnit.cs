using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>Where an attack order is in its life. Derived by CommandableUnit; debug views and AI state read it.</summary>
    public enum AttackPhase
    {
        /// <summary>No attack order.</summary>
        None,
        /// <summary>Out of range: walking toward the target.</summary>
        Approach,
        /// <summary>In range but no line of sight (ranged only): walking to a firing position, or at the target.</summary>
        Reposition,
        /// <summary>In range (and in sight): standing, facing and hitting on cooldown.</summary>
        Attack,
    }

    /// <summary>
    /// The single entry point for gameplay orders and direct control. Keeps the unit's orders in a CommandQueue (the
    /// current order plus pending ones) and carries out the current order each simulation frame using UnitMover and
    /// UnitAttacker. A held move intent (direct control) takes precedence: while it is non-zero the unit drops its
    /// orders and steers instead. An attack order runs in phases: approach until in range, reposition while a ranged
    /// unit has no line of sight, attack otherwise. A MoveToCover order reserves its point when it starts, occupies it
    /// on arrival, and gives up after CoverWalkTimeout without progress.
    /// </summary>
    [RequireComponent(typeof(UnitMover), typeof(UnitAttacker), typeof(UnitCover))]
    public sealed class CommandableUnit : MonoBehaviour
    {
        const float ChaseRepathDistance = 0.5f;
        // Firing-position searches cost 16 snaps, sight rays and paths, so they are rate limited.
        const float RepositionInterval = 0.5f;
        // A reposition walk that has not arrived after this long (a spot taken by another unit) is searched again.
        const float RepositionWalkTimeout = 3f;
        // After this many repositions without landing a hit the unit walks at the target instead.
        const int MaxRepositionsWithoutShot = 3;
        // A cover walk that has not come closer to its point for this long is held off (another unit stands there).
        const float CoverWalkTimeout = 3f;
        // The least the flat distance to the point must fall for the walk to count as progressing.
        const float CoverProgressStep = 0.05f;

        readonly CommandQueue queue = new CommandQueue();
        readonly Vector3[] firingCandidates = new Vector3[FiringPositionFinder.CandidateCount];
        UnitMover mover;
        UnitAttacker attacker;
        AttackPhase attackPhase;
        // Where the target stood when the current approach or reposition path was requested.
        Vector3 lastTargetPosition;
        float nextRepositionTime;
        float searchTime;
        int repositionsWithoutShot;
        // True while the reposition fallback is walking at the target itself rather than to a firing position.
        bool walkingAtTarget;
        Vector3 moveIntent;
        Health ownHealth;
        UnitCover cover;
        float coverBestDistance;
        float coverLastProgressTime;

        /// <summary>The order being carried out, or null when idle.</summary>
        public UnitCommand CurrentCommand => queue.Current;

        /// <summary>Orders waiting behind the current one, in the order they will run.</summary>
        public IReadOnlyList<UnitCommand> PendingCommands => queue.Pending;

        /// <summary>The direction direct control is steering the unit in, or zero. See SetMoveIntent.</summary>
        public Vector3 MoveIntent => moveIntent;

        /// <summary>False once this unit's Health (if it has one) has died. A dead unit takes and runs no orders.</summary>
        public bool IsAlive => OwnHealth == null || OwnHealth.IsAlive;

        /// <summary>
        /// The phase of the current attack order; None without one. An attack order that has not ticked yet reads as
        /// Approach, so the HUD never shows an attacking unit as doing nothing.
        /// </summary>
        public AttackPhase AttackPhase
        {
            get
            {
                if (!(queue.Current is AttackCommand))
                    return AttackPhase.None;
                return attackPhase == AttackPhase.None ? AttackPhase.Approach : attackPhase;
            }
        }

        /// <summary>The unit's cover state; AI and views read cover through the unit.</summary>
        public UnitCover Cover => cover != null ? cover : cover = GetComponent<UnitCover>();

        /// <summary>
        /// Whom the unit is attacking: the current attack's target, else the first pending attack's target while a
        /// cover order is current (a unit walking to cover with its attack queued is already engaged), else null.
        /// </summary>
        public Health AttackTarget
        {
            get
            {
                switch (queue.Current)
                {
                    case AttackCommand attack:
                        return attack.Target;
                    case MoveToCoverCommand _ when queue.Pending.Count > 0 && queue.Pending[0] is AttackCommand pending:
                        return pending.Target;
                    default:
                        return null;
                }
            }
        }

        UnitMover Mover => mover != null ? mover : mover = GetComponent<UnitMover>();
        UnitAttacker Attacker => attacker != null ? attacker : attacker = GetComponent<UnitAttacker>();

        // Looked up lazily so a Health added after this component is still found.
        Health OwnHealth => ownHealth != null ? ownHealth : ownHealth = GetComponent<Health>();

        /// <summary>
        /// Gives the unit an order. Replace drops the current and pending orders and starts this one now; Append runs
        /// it after the pending ones (now, if the unit is idle). A Stop always halts the unit and clears every order.
        /// Returns false if the order cannot be carried out (this unit is dead, no walkable point within 2 m of the
        /// destination, dead or inactive target, a cover point another unit holds); the unit's orders are then unchanged.
        /// Re-issuing an attack on the current target keeps the unit moving instead of restarting its chase.
        /// </summary>
        public bool Issue(UnitCommand command, IssueMode mode = IssueMode.Replace)
        {
            if (command == null)
                throw new ArgumentNullException(nameof(command));
            if (mode != IssueMode.Replace && mode != IssueMode.Append)
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown issue mode.");
            if (!IsAlive)
                return false;

            switch (command)
            {
                case StopCommand _:
                    StopAll();
                    return true;
                case MoveCommand _:
                case AttackCommand _:
                case MoveToCoverCommand _:
                    break;
                default:
                    throw new ArgumentException($"Unsupported command type {command.GetType().Name}.", nameof(command));
            }

            if (mode == IssueMode.Append && queue.Current != null)
            {
                if (!CanStart(command))
                    return false;
                queue.Append(command);
                return true;
            }

            if (command is AttackCommand attack && queue.Current is AttackCommand current
                && current.Target == attack.Target && IsAttackable(attack.Target))
            {
                queue.Replace(attack);
                return true;
            }

            if (!TryStart(command))
                return false;
            queue.Replace(command);
            return true;
        }

        /// <summary>
        /// Sets the direction direct control steers the unit in: flattened, length clamped to 1, zero for none. Held
        /// until set again. While it is non-zero and simulation time runs, the unit drops all of its orders (as Stop
        /// does) and steers instead, so manual control always wins over queued orders. Orders issued meanwhile are
        /// accepted and then dropped on the next simulation frame.
        /// </summary>
        public void SetMoveIntent(Vector3 direction)
        {
            direction.y = 0f;
            moveIntent = Vector3.ClampMagnitude(direction, 1f);
        }

        void OnEnable()
        {
            if (OwnHealth != null)
                OwnHealth.Died += OnDied;
        }

        void OnDisable()
        {
            if (OwnHealth != null)
                OwnHealth.Died -= OnDied;
        }

        // A corpse keeps no plan: whatever it was doing ends here, before Health deactivates the GameObject.
        void OnDied() => StopAll();

        void Update()
        {
            // Orders and steering only advance while simulation time advances (tactical pause sets timeScale to 0).
            if (!SimulationTime.IsRunning)
                return;

            if (moveIntent != Vector3.zero)
            {
                if (queue.Current != null)
                    StopAll();
                Mover.Steer(moveIntent);
                return;
            }

            switch (queue.Current)
            {
                case MoveCommand _:
                    if (Mover.HasArrived)
                        StartNext();
                    break;
                case AttackCommand attack:
                    UpdateAttack(attack.Target);
                    break;
                case MoveToCoverCommand toCover:
                    UpdateCover(toCover);
                    break;
            }
        }

        // Whether a queued order could start later, without starting it.
        bool CanStart(UnitCommand command)
        {
            switch (command)
            {
                case MoveCommand move:
                    if (Mover.CanMoveTo(move.Destination))
                        return true;
                    Debug.LogWarning($"{name} cannot queue a move: no walkable NavMesh point within 2 m of {move.Destination}.", this);
                    return false;
                case AttackCommand attack:
                    return IsAttackable(attack.Target);
                case MoveToCoverCommand toCover:
                    return CanTakeCover(toCover.Point);
                default:
                    return false;
            }
        }

        // Starts carrying out an order. Returns false, without side effects, if it cannot be carried out.
        bool TryStart(UnitCommand command)
        {
            switch (command)
            {
                case MoveCommand move:
                    if (!Mover.MoveTo(move.Destination))
                        return false;
                    Cover.ReleaseReservation();
                    ResetAttack();
                    return true;
                case AttackCommand attack:
                    if (!IsAttackable(attack.Target))
                        return false;
                    Mover.Stop();
                    Cover.ReleaseReservation();
                    ResetAttack();
                    return true;
                case MoveToCoverCommand toCover:
                    // MoveTo returns SetDestination's result, which can be false even after CanMoveTo passed, so it
                    // runs before the claim; TryReserve cannot fail once CanTakeCover has passed.
                    if (!CanTakeCover(toCover.Point) || !Mover.MoveTo(toCover.Point.Position))
                        return false;
                    Cover.TryReserve(toCover.Point);
                    ResetAttack();
                    coverBestDistance = float.PositiveInfinity;
                    coverLastProgressTime = Time.time;
                    return true;
                default:
                    return false;
            }
        }

        // The current order is finished: start the next pending order that can still be carried out.
        void StartNext()
        {
            while (queue.Advance() != null)
            {
                if (TryStart(queue.Current))
                    return;
            }
        }

        void StopAll()
        {
            Cover.ReleaseReservation();
            Mover.Stop();
            ResetAttack();
            queue.Clear();
        }

        // A point that exists, is active, is unclaimed or ours, and has walkable mesh within the usual 2 m.
        bool CanTakeCover(CoverPoint point) =>
            point != null && point.gameObject.activeInHierarchy && (!point.IsClaimed || point.IsClaimedBy(Cover))
            && Mover.CanMoveTo(point.Position);

        // Arrival occupies the point. A vanished point ends the order silently, as a vanished attack target does. A
        // walk that stops making progress (another unit stands on the spot, so HasArrived never turns true) gives up
        // after CoverWalkTimeout without a log: an expected situation in play. Scaled time never advances while paused,
        // so a pause is not a stall.
        void UpdateCover(MoveToCoverCommand order)
        {
            var point = order.Point;
            if (point == null || !point.gameObject.activeInHierarchy || Cover.Status == CoverStatus.None)
            {
                Cover.ReleaseReservation();
                Mover.Stop();
                StartNext();
                return;
            }
            if (Mover.HasArrived)
            {
                if (!Cover.TryOccupy())
                {
                    Debug.LogWarning($"{name} could not occupy {point.name}: the path ended {CoverRules.FlatDistance(transform.position, point.Position):0.0} m from it.", this);
                    Cover.ReleaseReservation();
                }
                StartNext();
                return;
            }
            var distance = CoverRules.FlatDistance(transform.position, point.Position);
            if (distance < coverBestDistance - CoverProgressStep)
            {
                coverBestDistance = distance;
                coverLastProgressTime = Time.time;
            }
            else if (Time.time >= coverLastProgressTime + CoverWalkTimeout)
            {
                Cover.ReleaseReservation();
                Mover.Stop();
                StartNext();
            }
        }

        void ResetAttack()
        {
            attackPhase = AttackPhase.None;
            repositionsWithoutShot = 0;
            walkingAtTarget = false;
            nextRepositionTime = 0f;
            searchTime = 0f;
        }

        void UpdateAttack(Health target)
        {
            if (!IsAttackable(target))
            {
                FinishAttack();
                return;
            }

            if (!Attacker.IsInRange(target))
            {
                Approach(target);
                return;
            }

            // Committed to a validated firing position: finish the walk even if the line clears early, so the unit
            // ends clear of the corner rather than on its edge (where a small target move would blind it again).
            if (attackPhase == AttackPhase.Reposition && !walkingAtTarget && StillWalking(target))
                return;

            if (Attacker.NeedsLineOfSight && !Attacker.HasLineOfSight(target))
            {
                Reposition(target);
                return;
            }

            if (attackPhase != AttackPhase.Attack)
            {
                Mover.Stop();
                attackPhase = AttackPhase.Attack;
                // The fallback walk (if any) is over; a stale flag would end the order the next time sight is lost.
                walkingAtTarget = false;
            }
            FaceTowards(target.transform.position);
            if (Attacker.TryAttack(target))
                repositionsWithoutShot = 0;
            if (!target.IsAlive)
                FinishAttack();
        }

        // Out of range: walk toward the target, re-pathing when it has moved. Arriving while still out of range
        // means the path was partial (a complete path ends at the target, inside range): the target cannot be reached.
        void Approach(Health target)
        {
            var targetPosition = target.transform.position;
            if (attackPhase != AttackPhase.Approach || TargetMoved(targetPosition))
            {
                if (!Mover.MoveTo(targetPosition))
                {
                    FinishAttack();
                    return;
                }
                attackPhase = AttackPhase.Approach;
                lastTargetPosition = targetPosition;
            }
            else if (Mover.HasArrived)
            {
                FinishAttack();
            }
        }

        // In range but blind (ranged only): stand, then at most twice a second look for a nearby firing position
        // and walk there. UpdateAttack keeps the unit on a walk to a validated spot until it arrives; only the
        // fallback walk at the target ends the moment the line clears, since it has no validated endpoint.
        // Arriving at the end of a fallback walk still blind means the path was partial: the target cannot be
        // reached from anywhere in sight, so the order ends as Approach's does for an unreachable target.
        void Reposition(Health target)
        {
            if (attackPhase != AttackPhase.Reposition)
            {
                Mover.Stop();
                attackPhase = AttackPhase.Reposition;
                lastTargetPosition = target.transform.position;
                // Stopped, not arrived: until a search starts a new fallback walk, "arrived" must not end the order.
                walkingAtTarget = false;
            }
            else if (walkingAtTarget && Mover.HasArrived)
            {
                FinishAttack();
                return;
            }
            else if (StillWalking(target))
            {
                return;   // still on the fallback walk; UpdateAttack ends it the moment the line clears
            }
            if (Time.time >= nextRepositionTime)
                SearchFiringPosition(target);
        }

        void SearchFiringPosition(Health target)
        {
            searchTime = Time.time;
            nextRepositionTime = Time.time + RepositionInterval;
            lastTargetPosition = target.transform.position;
            if (repositionsWithoutShot < MaxRepositionsWithoutShot)
            {
                // Candidates start on the ground, so the 2 m snap only has to absorb the erosion band beside walls.
                var count = FiringPositionFinder.Candidates(transform.position - Vector3.up * Mover.PivotHeight, firingCandidates);
                // The closure allocates once per search (at most twice a second per blind unit); acceptable.
                if (FiringPositionFinder.TryChoose(firingCandidates, count,
                        (Vector3 candidate, out Vector3 accepted) => IsFiringPosition(candidate, target, out accepted), out var spot)
                    && Mover.MoveTo(spot))
                {
                    repositionsWithoutShot++;
                    walkingAtTarget = false;
                    return;
                }
            }
            // Fallback: walk at the target until the line clears. No walkable point near it means it is unreachable.
            if (Mover.MoveTo(lastTargetPosition))
                walkingAtTarget = true;
            else
                FinishAttack();
        }

        // On the NavMesh, in range and in sight from the eye a unit would have there (snapped point + pivot height,
        // then LineOfSight adds the eye height), and reachable. Returns the snapped point as the place to walk to.
        bool IsFiringPosition(Vector3 candidate, Health target, out Vector3 point) =>
            Mover.TrySnap(candidate, out point)
            && Attacker.CanAttackFrom(point + Vector3.up * Mover.PivotHeight, target)
            && Mover.CanReach(point);

        // A reposition walk goes on until it arrives, the target moves away from where it was planned, or it times out.
        bool StillWalking(Health target) =>
            !Mover.HasArrived && !TargetMoved(target.transform.position) && Time.time < searchTime + RepositionWalkTimeout;

        bool TargetMoved(Vector3 targetPosition) =>
            (targetPosition - lastTargetPosition).sqrMagnitude > ChaseRepathDistance * ChaseRepathDistance;

        // A target that is missing, dead, or deactivated while still alive can no longer be attacked.
        static bool IsAttackable(Health target) => target != null && target.IsAlive && target.gameObject.activeInHierarchy;

        void FinishAttack()
        {
            Mover.Stop();
            ResetAttack();
            StartNext();
        }

        void FaceTowards(Vector3 point)
        {
            var direction = point - transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude > 0.0001f)
                transform.rotation = Quaternion.LookRotation(direction);
        }
    }
}
