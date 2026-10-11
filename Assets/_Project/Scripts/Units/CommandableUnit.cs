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

    /// <summary>A reason an order was refused that the HUD explains in words.</summary>
    public enum CommandRefusal
    {
        None,
        /// <summary>The unit has no weapon equipped, so it cannot make the ordinary attack.</summary>
        NoWeapon,
    }

    /// <summary>
    /// The single entry point for gameplay orders and direct control. Keeps the unit's orders in a CommandQueue (the
    /// current order plus pending ones) and carries out the current order each simulation frame using UnitMover and
    /// UnitAttacker. A held move intent (direct control) takes precedence: while it is non-zero the unit drops its
    /// orders and steers instead. An attack order runs in phases: approach until in range, reposition while a ranged
    /// unit has no line of sight, attack otherwise. A MoveToCover order reserves its point when it starts, occupies it
    /// on arrival, and gives up after CoverWalkTimeout without progress. An AbilityCommand becomes current without doing
    /// anything and runs (UnitAbilities.TryUse, which validates again) on the first running frame it is usable, before
    /// steering is considered; whether it then succeeds or fails the queue moves on. While its only problem is range or
    /// line of sight it walks into position first with the attack's approach and reposition steps (decision 029), and
    /// gives up when that walk makes no progress for AbilityStallTimeout or the place cannot be reached. An InteractCommand walks into the target's range, then works on it for its duration (UnitInteractor), re-validating every running frame; any failure, a stop, a replacing order, steering or death ends it and drops the claim and its progress.
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
        // An ability's walk into position gives up after the same time without progress as a cover walk.
        const float AbilityStallTimeout = CoverWalkTimeout;

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
        UnitAbilities abilities;
        UnitInteractor interactor;
        UnitItems items;
        float coverBestDistance;
        float coverLastProgressTime;
        // Where the current approach or reposition walk leads, and the ability's progress toward it.
        float walkBestDistance;
        float walkProgressTime;
        float swapElapsed;

        /// <summary>The order being carried out, or null when idle.</summary>
        public UnitCommand CurrentCommand => queue.Current;

        /// <summary>How far through a gear swap the unit is (0 to 1), or 0 when it is not swapping.</summary>
        public float SwapProgress => queue.Current is EquipItemCommand ? Mathf.Clamp01(swapElapsed / UnitItems.SwapSeconds) : 0f;

        /// <summary>Orders waiting behind the current one, in the order they will run.</summary>
        public IReadOnlyList<UnitCommand> PendingCommands => queue.Pending;

        /// <summary>
        /// How many Stop commands this unit has accepted (also when it was already idle). A Stop leaves no order
        /// behind, so this lets autonomy see that one happened.
        /// </summary>
        public int StopCount { get; private set; }

        /// <summary>Why the last order was refused for a reason the player should be told (None before any).</summary>
        public CommandRefusal LastRefusal { get; private set; }

        /// <summary>Unscaled time of the last refusal, so the HUD can show it for a few seconds even while paused.</summary>
        public float LastRefusalTime { get; private set; }

        /// <summary>The direction direct control is steering the unit in, or zero. See SetMoveIntent.</summary>
        public Vector3 MoveIntent => moveIntent;

        /// <summary>
        /// False while direct control steers the unit: an ability order that would first have to walk into range or sight
        /// is then refused with its reason (ruling R10), since steering would drop it on the next frame. Issue and the
        /// targeting preview both use this test.
        /// </summary>
        public bool CanWalkToCast => moveIntent == Vector3.zero;

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

        /// <summary>
        /// Approach or Reposition while the current ability order walks into range or to a firing position (decision
        /// 029); None for any other order, and for an ability that has not needed to walk (yet).
        /// </summary>
        public AttackPhase AbilityPhase => queue.Current is AbilityCommand ? attackPhase : AttackPhase.None;

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

        UnitAbilities Abilities => abilities != null ? abilities : abilities = GetComponent<UnitAbilities>();
        UnitInteractor Interactor => interactor != null ? interactor : interactor = GetComponent<UnitInteractor>();
        UnitItems Items => items != null ? items : items = GetComponent<UnitItems>();
        UnitMover Mover => mover != null ? mover : mover = GetComponent<UnitMover>();
        UnitAttacker Attacker => attacker != null ? attacker : attacker = GetComponent<UnitAttacker>();

        // Looked up lazily so a Health added after this component is still found.
        Health OwnHealth => ownHealth != null ? ownHealth : ownHealth = GetComponent<Health>();

        /// <summary>
        /// Gives the unit an order. Replace drops the current and pending orders and starts this one now; Append runs
        /// it after the pending ones (now, if the unit is idle). A Stop always halts the unit and clears every order.
        /// Returns false if the order cannot be carried out (this unit is dead, no walkable point within 2 m of the
        /// destination, dead or inactive target, a cover point another unit holds, an ability the unit has no UnitAbilities
        /// for or does not own, or whose target is dead or on the wrong side, and, when the order would start now, one that
        /// is on cooldown, or out of range or out of sight while direct control steers the unit); the unit's orders are then
        /// unchanged. Otherwise an ability out of range or out of sight is accepted: the unit walks into position first
        /// (decision 029).
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
            if (command is AttackCommand && !Attacker.HasWeapon)
            {
                Refuse(CommandRefusal.NoWeapon);
                return false;
            }

            switch (command)
            {
                case StopCommand _:
                    StopCount++;
                    StopAll();
                    return true;
                case MoveCommand _:
                case AttackCommand _:
                case MoveToCoverCommand _:
                case AbilityCommand _:
                case InteractCommand _:
                case UseItemCommand _:
                case EquipItemCommand _:
                case CollectCommand _:
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

            if (command is InteractCommand again && queue.Current is InteractCommand running && running.Target == again.Target
                && Interactor != null && Interactor.Check(again.Target) == InteractionFailure.None)
            {
                queue.Replace(again);
                return true;
            }

            // An ability that would start now is checked in full now, but range and sight only decide whether it walks
            // first (decision 029), unless the unit is being steered and could not walk (ruling R10); behind other orders
            // only its target is, because the caster will have moved on by the time it runs.
            if (command is AbilityCommand startingAbility && !CanStartAbility(startingAbility, AbilityCheckScope.Full))
                return false;

            if (command is InteractCommand startingInteract && !CanOrderInteract(startingInteract))
                return false;

            if (command is UseItemCommand startingUse && !CanOrderUse(startingUse))
                return false;

            if (command is CollectCommand startingCollect && !CanOrderCollect(startingCollect))
                return false;

            if (command is EquipItemCommand startingEquip && !CheckEquip(startingEquip))
                return false;

            if (!TryStart(command))
                return false;
            queue.Replace(command);
            return true;
        }

        /// <summary>
        /// Sets the direction direct control steers the unit in: flattened, length clamped to 1, zero for none. Held
        /// until set again. While it is non-zero and simulation time runs, the unit drops all of its orders (as Stop
        /// does) and steers instead, so manual control always wins over queued orders. A usable ability issued meanwhile
        /// runs on the next simulation frame instead of being dropped; one that would first have to walk into range or
        /// sight is refused at once with its reason (CanWalkToCast); other orders are accepted and then dropped on that
        /// frame. An ability walk already under way is dropped like any other order.
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

            var abilityBlocker = RunAbilities();

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
                case AbilityCommand ability:
                    UpdateAbility(ability, abilityBlocker);
                    break;
                case InteractCommand interact:
                    UpdateInteract(interact);
                    break;
                case CollectCommand collect:
                    UpdateCollect(collect);
                    break;
                case EquipItemCommand equip:
                    UpdateEquip(equip);
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
                    if (!Attacker.HasWeapon)
                    {
                        Refuse(CommandRefusal.NoWeapon);
                        return false;
                    }
                    return IsAttackable(attack.Target);
                case MoveToCoverCommand toCover:
                    return CanTakeCover(toCover.Point);
                case AbilityCommand ability:
                    return CanStartAbility(ability, AbilityCheckScope.Static);
                case InteractCommand interact:
                    // Queued: only what cannot change by the time it runs. Another unit's claim and the walk are checked then.
                    return Interactor != null && Interactor.Check(interact.Target, allowOtherUser: true) == InteractionFailure.None;
                case UseItemCommand use:
                    return Items != null && CheckUse(use, full: false);
                case EquipItemCommand equip:
                    return Items != null && CheckEquip(equip);
                case CollectCommand collect:
                    // Queued: only what cannot change by the time it runs. The walk and the bag are checked then.
                {
                    if (Items == null)
                        return false;
                    var queuedFailure = Items.CheckCollect(collect.Container, requireRange: false);
                    if (queuedFailure != CollectFailure.None)
                        Items.RecordCollect(queuedFailure);
                    return queuedFailure == CollectFailure.None;
                }
                default:
                    return false;
            }
        }

        // Starts carrying out an order. Returns false, without side effects, if it cannot be carried out. A new current order
        // ends an interaction in progress (the claim and its progress), unless it is the interact order taking over.
        bool TryStart(UnitCommand command)
        {
            if (!TryStartCommand(command))
                return false;
            if (!(command is InteractCommand) && Interactor != null)
                Interactor.Release();
            return true;
        }

        bool TryStartCommand(UnitCommand command)
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
                case AbilityCommand _:
                    Mover.Stop();
                    Cover.ReleaseReservation();
                    ResetAttack();
                    return true;
                case UseItemCommand _:
                    Mover.Stop();
                    Cover.ReleaseReservation();
                    ResetAttack();
                    return true;
                case EquipItemCommand equip:
                    if (Items == null || !CheckEquip(equip))
                        return false;
                    Mover.Stop();
                    Cover.ReleaseReservation();
                    ResetAttack();
                    swapElapsed = 0f;
                    return true;
                case InteractCommand interact:
                {
                    var worker = Interactor;
                    var target = interact.Target;
                    if (worker == null || worker.Check(target) != InteractionFailure.None)
                        return false;
                    var inRange = worker.InRange(target, transform.position);
                    if (!inRange && !Mover.CanMoveTo(target.Position))
                        return false;
                    worker.Release();
                    if (inRange)
                        Mover.Stop();
                    else if (!WalkToInteractable(target.Position, target.Range))
                        return false;
                    walkProgressTime = Time.time;
                    Cover.ReleaseReservation();
                    ResetAttack();
                    return true;
                }
                case CollectCommand collect:
                {
                    var holder = Items;
                    var container = collect.Container;
                    if (holder == null)
                        return false;
                    var startFailure = holder.CheckCollect(container, requireRange: false);
                    if (startFailure != CollectFailure.None)
                    {
                        holder.RecordCollect(startFailure);
                        return false;
                    }
                    var inRange = holder.InRange(container, transform.position);
                    if (!inRange && !Mover.CanMoveTo(container.Position))
                        return false;
                    if (inRange)
                        Mover.Stop();
                    else if (!WalkToInteractable(container.Position, container.Interactable.Range))
                        return false;
                    walkProgressTime = Time.time;
                    Cover.ReleaseReservation();
                    ResetAttack();
                    return true;
                }
                default:
                    return false;
            }
        }

        // Whether an ability order passes its checks: Full (everything) when it would start now, Static (who and what) when queued.
        bool CanStartAbility(AbilityCommand ability, AbilityCheckScope scope)
        {
            var owned = Abilities;
            if (owned == null)
                return false;
            return scope == AbilityCheckScope.Full ? owned.CanOrderNow(ability, CanWalkToCast) : owned.CanQueue(ability);
        }

        // Abilities are instant once usable: each one succeeds or fails on the first running frame it is usable or refused,
        // then the queue moves on, so a refused ability never leaves the unit stuck. It runs before steering is considered,
        // so an ability that direct control just issued is not dropped by a held move key. One that is only out of range
        // or out of sight stops the loop and its failure is returned: Update then walks the unit into position
        // (UpdateAbility), unless a held move key drops the order. The guard bounds the loop by the queue length.
        AbilityFailure RunAbilities()
        {
            var guard = queue.Pending.Count + 1;
            while (guard-- > 0)
            {
                if (queue.Current is UseItemCommand use)
                {
                    if (Items != null)
                        Items.TryUse(use.InstanceId);
                    Finish();
                    continue;
                }
                if (!(queue.Current is AbilityCommand ability))
                    break;
                var owned = Abilities;
                if (owned != null)
                {
                    var failure = owned.CheckOrder(ability).Failure;
                    if (AbilityRules.IsApproachable(failure))
                        return failure;
                    owned.TryUse(ability);
                }
                Finish();
            }
            return AbilityFailure.None;
        }

        // Out of range or out of sight (RunAbilities has just checked): walk into position with the attack's approach and
        // reposition steps, judged by the ability's own range and sight rule. The ability fires from RunAbilities on the
        // first frame it is usable, so the unit stops at the edge of range or as soon as the line clears. A walk that has
        // not come closer to where it leads for AbilityStallTimeout gives up (scaled time, so a pause is not a stall).
        void UpdateAbility(AbilityCommand order, AbilityFailure blocker)
        {
            if (!AbilityRules.IsApproachable(blocker))
                return;   // defensive: RunAbilities hands over only an ability it could not use for range or sight
            var aim = Aim.Ability(order, blocker);
            if (attackPhase == AttackPhase.None)
            {
                walkProgressTime = Time.time;
            }
            else if (WalkStalled())
            {
                GiveUp(aim);
                return;
            }

            if (AbilityRules.ApproachPhase(blocker) == AttackPhase.Approach)
                Approach(aim);
            else
                Reposition(aim);
        }

        // Whether an interact order passes its checks to start now: a usable, unclaimed target the unit is in range of or can reach.
        bool CanOrderInteract(InteractCommand order)
        {
            var worker = Interactor;
            if (worker == null || worker.Check(order.Target) != InteractionFailure.None)
                return false;
            return worker.InRange(order.Target, transform.position) || Mover.CanReach(order.Target.Position);
        }

        // An item order that would start now: the unit has the item and it would do something.
        bool CanOrderUse(UseItemCommand order) => Items != null && CheckUse(order, full: true);

        bool CheckEquip(EquipItemCommand order)
        {
            if (Items == null)
                return false;
            var failure = Items.CheckEquip(order.InstanceId);
            if (failure == ItemUseFailure.None)
                return true;
            Items.RecordEquip(failure);
            return false;
        }

        // A gear swap takes UnitItems.SwapSeconds of simulation time (a pause freezes it, as Update does not run) in which the
        // unit does nothing else. Any new order replaces this one and cancels the swap with the old gear kept. When the time is
        // up the item is checked again and equipped; a failure then is recorded and ends the order.
        void UpdateEquip(EquipItemCommand order)
        {
            swapElapsed += Time.deltaTime;
            if (swapElapsed < UnitItems.SwapSeconds)
                return;
            if (Items != null)
                Items.TryEquip(order.InstanceId);
            Finish();
        }

        bool CheckUse(UseItemCommand order, bool full)
        {
            var failure = Items.Check(order.InstanceId, full);
            if (failure == ItemUseFailure.None)
                return true;
            Items.Record(failure);
            return false;
        }

        // A collect order that would start now: a searched container the unit can take from, in reach or reachable.
        bool CanOrderCollect(CollectCommand order)
        {
            var holder = Items;
            if (holder == null)
                return false;
            var failure = holder.CheckCollect(order.Container, requireRange: false);
            if (failure != CollectFailure.None)
            {
                holder.RecordCollect(failure);
                return false;
            }
            return holder.InRange(order.Container, transform.position) || Mover.CanReach(order.Container.Position);
        }

        // Re-validates every running frame. In reach the unit stops, faces the container and takes (the transfer re-reads the
        // source); out of reach it walks; a vanished container, a full stop or a walk that makes no progress ends the order
        // so the queue moves on. Only simulation time reaches this, so a pause freezes it.
        void UpdateCollect(CollectCommand order)
        {
            var holder = Items;
            if (holder == null)
            {
                Finish();
                return;
            }
            var failure = holder.CheckCollect(order.Container, requireRange: false);
            if (failure != CollectFailure.None)
            {
                holder.RecordCollect(failure);
                Finish();
                return;
            }
            if (holder.InRange(order.Container, transform.position))
            {
                Mover.Stop();
                FaceTowards(order.Container.Position);
                holder.TryCollect(order.Container, order.InstanceId);
                Finish();
                return;
            }
            if (Mover.HasArrived || WalkStalled())
            {
                holder.RecordCollect(CollectFailure.OutOfRange);
                Finish();
            }
        }

        // Re-validates every running frame: a vanished, completed or taken terminal ends the order (the queue moves on). In
        // range the unit stops, faces the terminal, claims it and adds its scaled frame time (frozen by pause, which stops
        // this Update). Out of range it walks; arriving or stalling out of range, or being pushed out of range while working,
        // ends the order.
        void UpdateInteract(InteractCommand order)
        {
            var worker = Interactor;
            var target = order.Target;
            var failure = worker != null ? worker.Check(target) : InteractionFailure.NoTarget;
            if (failure != InteractionFailure.None)
            {
                if (worker != null)
                    worker.Record(failure);
                Finish();
                return;
            }
            if (worker.InRange(target, transform.position))
            {
                if (!worker.IsWorking)
                {
                    if (!worker.TryStart(target))
                    {
                        Finish();
                        return;
                    }
                    Mover.Stop();
                }
                FaceTowards(target.Position);
                if (worker.Advance(Time.deltaTime) != InteractionStep.Working)
                    Finish();
                return;
            }
            if (worker.IsWorking || Mover.HasArrived || WalkStalled())
                Finish();
        }

        // Starts a walk for an attack or ability order and remembers where it leads, for the ability's progress check. The
        // best distance starts at the current one, so a new walk alone is never counted as progress.
        bool WalkTo(Vector3 point)
        {
            if (!Mover.MoveTo(point))
                return false;
            walkBestDistance = float.PositiveInfinity;   // the first settled path counts as the starting point, never as progress
            return true;
        }

        // Walks to the cheapest point from which a solid interactable (a crate, a terminal) is in reach, not to its centre:
        // the centre is not walkable, and snapping it can pick a point on the far side of a wall. Falls back to the centre.
        bool WalkToInteractable(Vector3 position, float range) =>
            WalkTo(Mover.TryApproachPoint(position, range, out var stand) ? stand : position);

        // True once the walk has not come closer to where it leads (by path length) for AbilityStallTimeout.
        bool WalkStalled()
        {
            // Progress is the path length still to walk, not the straight-line distance: a detour round a wall or a crate
            // moves the unit away from the goal on purpose. While the path is still being computed there is nothing to judge.
            if (!Mover.TryGetRemainingDistance(out var distance))
                return false;
            if (distance < walkBestDistance - CoverProgressStep)
            {
                walkBestDistance = distance;
                walkProgressTime = Time.time;
                return false;
            }
            return Time.time >= walkProgressTime + AbilityStallTimeout;
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
            if (Interactor != null)
                Interactor.Release();
            Mover.Stop();
            ResetAttack();
            queue.Clear();
        }

        // A point that exists, is active, is unclaimed or ours, and has walkable mesh within the usual 2 m.
        bool CanTakeCover(CoverLocation point) =>
            point != null && point.IsValid && (!point.IsClaimed || point.IsClaimedBy(Cover))
            && Mover.CanMoveTo(point.Position);

        // Arrival occupies the point. A vanished point ends the order silently, as a vanished attack target does. A
        // walk that stops making progress (another unit stands on the spot, so HasArrived never turns true) gives up
        // after CoverWalkTimeout without a log: an expected situation in play. Scaled time never advances while paused,
        // so a pause is not a stall.
        void UpdateCover(MoveToCoverCommand order)
        {
            var point = order.Point;
            if (point == null || !point.IsValid || Cover.Status == CoverStatus.None)
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
                    Debug.LogWarning($"{name} could not occupy {point.Name}: the path ended {CoverRules.FlatDistance(transform.position, point.Position):0.0} m from it.", this);
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
            if (!Attacker.HasWeapon || !IsAttackable(target))
            {
                Finish();
                return;
            }

            var aim = Aim.Attack(target);
            if (!Attacker.IsInRange(target))
            {
                Approach(aim);
                return;
            }

            // Committed to a validated firing position: finish the walk even if the line clears early, so the unit
            // ends clear of the corner rather than on its edge (where a small target move would blind it again).
            if (attackPhase == AttackPhase.Reposition && !walkingAtTarget && StillWalking(aim))
                return;

            if (Attacker.NeedsLineOfSight && !Attacker.HasLineOfSight(target))
            {
                Reposition(aim);
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
            // A fired shot (hit or miss) proves the firing position; whether it lands is cover, not positioning.
            if (Attacker.TryAttack(target))
                repositionsWithoutShot = 0;
            if (!target.IsAlive)
                Finish();
        }

        // Out of range: walk toward the target, re-pathing when it has moved. Arriving while still out of range
        // means the path was partial (a complete path ends at the target, inside range): the target cannot be reached.
        void Approach(Aim aim)
        {
            var targetPosition = aim.Position;
            if (attackPhase != AttackPhase.Approach || TargetMoved(targetPosition))
            {
                if (!WalkTo(targetPosition))
                {
                    GiveUp(aim);
                    return;
                }
                attackPhase = AttackPhase.Approach;
                lastTargetPosition = targetPosition;
            }
            else if (Mover.HasArrived)
            {
                GiveUp(aim);
            }
        }

        // In range but blind (a ranged attack, or an ability that needs sight): stand, then at most twice a second look
        // for a nearby firing position and walk there. UpdateAttack keeps the unit on a walk to a validated spot until it
        // arrives; only the fallback walk at the target ends the moment the line clears, since it has no validated
        // endpoint. An ability casts the moment its line clears (RunAbilities), on either walk.
        // Arriving at the end of a fallback walk still blind means the path was partial: the target cannot be
        // reached from anywhere in sight, so the order ends as Approach's does for an unreachable target.
        void Reposition(Aim aim)
        {
            if (attackPhase != AttackPhase.Reposition)
            {
                Mover.Stop();
                attackPhase = AttackPhase.Reposition;
                lastTargetPosition = aim.Position;
                // Stopped, not arrived: until a search starts a new fallback walk, "arrived" must not end the order.
                walkingAtTarget = false;
            }
            else if (walkingAtTarget && Mover.HasArrived)
            {
                GiveUp(aim);
                return;
            }
            else if (StillWalking(aim))
            {
                return;   // still walking; the line clearing ends a fallback walk (UpdateAttack) or casts (RunAbilities)
            }
            if (Time.time >= nextRepositionTime)
                SearchFiringPosition(aim);
        }

        void SearchFiringPosition(Aim aim)
        {
            searchTime = Time.time;
            nextRepositionTime = Time.time + RepositionInterval;
            lastTargetPosition = aim.Position;
            if (repositionsWithoutShot < MaxRepositionsWithoutShot)
            {
                // Candidates start on the ground, so the 2 m snap only has to absorb the erosion band beside walls.
                var count = FiringPositionFinder.Candidates(transform.position - Vector3.up * Mover.PivotHeight, firingCandidates);
                // The closure allocates once per search (at most twice a second per blind unit); acceptable.
                if (FiringPositionFinder.TryChoose(firingCandidates, count,
                        (Vector3 candidate, out Vector3 accepted) => IsFiringPosition(candidate, aim, out accepted), out var spot)
                    && WalkTo(spot))
                {
                    repositionsWithoutShot++;
                    walkingAtTarget = false;
                    return;
                }
            }
            // Fallback: walk at the target until the line clears. No walkable point near it means it is unreachable.
            if (WalkTo(lastTargetPosition))
                walkingAtTarget = true;
            else
                GiveUp(aim);
        }

        // On the NavMesh, in range and in sight from the eye a unit would have there (snapped point + pivot height,
        // then LineOfSight adds the eye height), and reachable. Returns the snapped point as the place to walk to.
        bool IsFiringPosition(Vector3 candidate, Aim aim, out Vector3 point) =>
            Mover.TrySnap(candidate, out point)
            && CanActFrom(aim, point + Vector3.up * Mover.PivotHeight)
            && Mover.CanReach(point);

        // The attack's range and sight test, or the ability's (its own range, and sight only when it needs it).
        bool CanActFrom(Aim aim, Vector3 pivot) =>
            aim.Order != null ? Abilities.CanUseFrom(aim.Order, pivot) : Attacker.CanAttackFrom(pivot, aim.Target);

        // A reposition walk goes on until it arrives, the target moves away from where it was planned, or it times out.
        bool StillWalking(Aim aim) =>
            !Mover.HasArrived && !TargetMoved(aim.Position) && Time.time < searchTime + RepositionWalkTimeout;

        bool TargetMoved(Vector3 targetPosition) =>
            (targetPosition - lastTargetPosition).sqrMagnitude > ChaseRepathDistance * ChaseRepathDistance;

        // A target that is missing, dead, or deactivated while still alive can no longer be attacked.
        static bool IsAttackable(Health target) => target != null && target.IsAlive && target.gameObject.activeInHierarchy;

        // The attack or ability order is over: stop, forget its phases, start the next order.
        void Finish()
        {
            if (Interactor != null)
                Interactor.Release();
            Mover.Stop();
            ResetAttack();
            StartNext();
        }

        // The order cannot be carried out from anywhere reachable, so it ends as an unreachable attack does. An ability
        // records why (still out of range or out of sight) on its unit, so it shows like any refusal; no cooldown is spent.
        void GiveUp(Aim aim)
        {
            if (aim.Order != null && Abilities != null)
                Abilities.ReportFailure(aim.Order.Definition, aim.Blocker);
            Finish();
        }

        void Refuse(CommandRefusal reason)
        {
            LastRefusal = reason;
            LastRefusalTime = Time.unscaledTime;
        }

        void FaceTowards(Vector3 point)
        {
            var direction = point - transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude > 0.0001f)
                transform.rotation = Quaternion.LookRotation(direction);
        }

        // What the shared approach and reposition steps aim at: an attack's target, or an ability order (a unit or a
        // ground point) together with the reason it cannot be used yet, which is reported if the order gives up.
        readonly struct Aim
        {
            Aim(Health target, AbilityCommand order, AbilityFailure blocker)
            {
                Target = target;
                Order = order;
                Blocker = blocker;
            }

            public static Aim Attack(Health target) => new Aim(target, null, AbilityFailure.None);
            public static Aim Ability(AbilityCommand order, AbilityFailure blocker) => new Aim(null, order, blocker);

            public Health Target { get; }
            public AbilityCommand Order { get; }
            public AbilityFailure Blocker { get; }
            public Vector3 Position => Order != null ? Order.AimPoint : Target.transform.position;
        }
    }
}
