using System;
using System.Collections.Generic;
using System.Linq;
using Blackglass.Backend.Core;

namespace Blackglass
{
    /// <summary>
    /// One online attempt on the server (Stage 12 Task 6): owns the attempt's MissionItemLedger, registers the generated
    /// containers (one line per roll, gate B25), turns the friendly units' UnitItems detail events into ledger events, and at
    /// the first terminal event freezes the participant and builds its format-v1 manifest (ManifestBuilder.BuildForPublication,
    /// with the truthful fallback). After the freeze nothing changes the outcome: later events reach the ledger, which keeps the
    /// frozen facts, and the manifest is never rebuilt. Nothing is written or sent here: Stage 13 writes Outcome's bytes through
    /// the outbox and posts ReadyReport. Not thread-safe: the simulation thread is its only user.
    /// </summary>
    public sealed class AttemptSession
    {
        readonly AttemptStart attempt;
        readonly LedgerCatalogue catalogue;
        readonly string buildRevision;
        readonly IReadOnlyList<(int Index, IReadOnlyList<ItemEntry> EntriesInRollOrder)> containers;
        readonly Func<string, OperativeRuntimeState> operativeState;
        readonly Func<IReadOnlyList<ContainerRuntimeState>> containerStates;
        readonly Func<int> elapsedMs;
        ulong layoutHash;
        int lootTableVersion;
        MissionDirector director;
        readonly List<UnitItems> bound = new List<UnitItems>();
        // How to unsubscribe every gameplay-event handler Bind added (Unbind runs them).
        readonly List<Action> unbinders = new List<Action>();
        readonly OutcomeSequence sequence;

        /// <summary>The attempt's ledger (null before a successful start).</summary>
        public MissionItemLedger Ledger { get; private set; }
        public AttemptStart Attempt => attempt;
        /// <summary>True once the participant is frozen: the outcome no longer changes.</summary>
        public bool IsFrozen { get; private set; }
        /// <summary>The build's result at the freeze (null before it): Normal, the truthful fallback, or Failed with no bytes.</summary>
        public ManifestBuildOutcome Outcome { get; private set; }
        /// <summary>The caller error a Failed outcome came from (Stage 11 M2), else null. Never printed with a credential.</summary>
        public string FailureMessage { get; private set; }
        public ParticipantFacts Facts { get; private set; }
        public ParticipantResult Result { get; private set; }
        /// <summary>
        /// The attempt's outcomeSequence counter (owner ruling, Track L final rulings §1): one per committed authoritative state
        /// change of the mission, each counted once:
        /// - a unit's health changes: damage taken, healing received, death (Health.Damaged, Healed, Died; any unit);
        /// - a resolved basic attack that missed (UnitAttacker.Missed; a hit is counted as the damage it dealt);
        /// - an accepted ability use (UnitAbilities.Used);
        /// - an item event the ledger applied: a collect (one per container entry moved), a use, an equip;
        /// - an interaction's first completion: a container's search, a terminal (MissionInteractable.Completed; a re-open of a
        ///   searched container changes nothing and is not counted);
        /// - an objective's state change (MissionRuntime.ObjectiveChanged).
        /// One action can cause several distinct transitions, each counted (a killing blow: damage and death; a medkit: the use and
        /// the healing; a hack: the interaction and the objective). A collect counts one event per container entry moved.
        /// Orders and movement are intents, counted through what they cause. Refused, malformed, unauthorized and no-effect requests
        /// raise none of these (every source raises only on success, Health only on a real change), and a ledger-refused item event
        /// is not counted. The first terminal event (MissionFinished) takes the next number. Read-only outside the assembly.
        /// </summary>
        internal OutcomeSequence Sequence => sequence;
        /// <summary>Committed gameplay events so far (to the freeze).</summary>
        public int CommittedEvents => sequence.Committed;

        /// <summary>The plain core, for tests and the fixture export: the runtime is read through the three sources.</summary>
        internal AttemptSession(AttemptStart attempt, LedgerCatalogue catalogue, string buildRevision, ulong layoutHash, int lootTableVersion,
            IReadOnlyList<(int Index, IReadOnlyList<ItemEntry> EntriesInRollOrder)> containers, Func<string, OperativeRuntimeState> operativeState,
            Func<IReadOnlyList<ContainerRuntimeState>> containerStates, Func<int> elapsedMs, OutcomeSequence sequence = null)
        {
            this.sequence = sequence ?? new OutcomeSequence();
            this.attempt = attempt ?? throw new ArgumentNullException(nameof(attempt));
            this.catalogue = catalogue ?? throw new ArgumentNullException(nameof(catalogue));
            this.buildRevision = buildRevision ?? throw new ArgumentNullException(nameof(buildRevision));
            this.layoutHash = layoutHash;
            this.lootTableVersion = lootTableVersion;
            this.containers = containers ?? throw new ArgumentNullException(nameof(containers));
            this.operativeState = operativeState ?? throw new ArgumentNullException(nameof(operativeState));
            this.containerStates = containerStates ?? throw new ArgumentNullException(nameof(containerStates));
            this.elapsedMs = elapsedMs ?? throw new ArgumentNullException(nameof(elapsedMs));
        }

        /// <summary>
        /// Starts the session on the attempt's ready mission: the participant and its snapshot, every container's registration,
        /// a start check of the runtime against the ledger, then the friendly units' item events and the director's end. Null on
        /// success, else one code (not_ready, bad_snapshot, bad_pool) and nothing is bound.
        /// </summary>
        public static AttemptSession Begin(AttemptStart attempt, MissionDirector director, SquadInventory inventory, LedgerCatalogue catalogue,
            ServerClock clock, string buildRevision, out string refusal)
        {
            if (director == null || inventory == null || clock == null)
                throw new ArgumentNullException(director == null ? nameof(director) : inventory == null ? nameof(inventory) : nameof(clock));
            if (attempt == null || director.State != MissionState.Ready || !ReferenceEquals(director.Attempt, attempt) || director.Current == null)
            {
                refusal = "not_ready";
                return null;
            }
            // Every snapshot operative is exactly one spawned friendly with its item component, or the manifest would report an
            // operative that never entered the mission as alive with its whole loadout.
            foreach (var operative in attempt.Snapshot)
            {
                var units = 0;
                foreach (var unit in director.Friendlies)
                    if (unit != null && unit.TryGetComponent<UnitItems>(out var items) && items.OperativeId == operative.OperativeId)
                        units++;
                if (units != 1)
                {
                    refusal = "squad_not_spawned";
                    return null;
                }
            }
            var session = new AttemptSession(attempt, catalogue, buildRevision, director.Report.LayoutHash, director.AttemptLootTableVersion,
                director.AttemptContainers, id => AttemptRuntime.OperativeState(inventory, id),
                () => director.Current != null
                    ? director.Current.LootContainers.Select((c, i) => AttemptRuntime.ContainerState(i, c)).ToList()
                    : new List<ContainerRuntimeState>(),
                () => clock.ElapsedMs);
            refusal = session.Start();
            if (refusal != null)
                return null;
            clock.Restart();
            session.Bind(director);
            return session;
        }

        /// <summary>The ledger's participant and containers, then the start check. Null, or the refusal code.</summary>
        internal string Start()
        {
            var ledger = new MissionItemLedger(catalogue);
            try { ledger.AddParticipant(attempt.AccountId, attempt.Snapshot); }
            catch (ArgumentException) { return "bad_snapshot"; }
            try
            {
                foreach (var (index, entries) in containers)
                    AttemptLoot.Register(ledger, index, entries.Select(e => new ContainerEntry(e.InstanceId, e.DefinitionId, e.Quantity)).ToList());
            }
            catch (ArgumentException) { return "bad_pool"; }
            Ledger = ledger;
            // A runtime that already disagrees with the ledger is an integrity failure from the first frame, never "ok" later.
            foreach (var o in attempt.Snapshot)
            {
                var state = operativeState(o.OperativeId);
                if (state == null) ledger.ReportIntegrityFailure(attempt.AccountId, "operative_state_missing");
                else ledger.VerifyOperative(o.OperativeId, state.Bag, state.Equipment);
            }
            foreach (var state in containerStates())
                ledger.VerifyContainer(state.ContainerIndex, state.Entries);
            return null;
        }

        void Bind(MissionDirector missionDirector)
        {
            director = missionDirector;
            foreach (var unit in director.Friendlies)
            {
                if (unit == null || !unit.TryGetComponent<UnitItems>(out var items))
                    continue;
                items.CollectedDetail += OnCollectedDetail;
                items.UsedDetail += HandleUsed;
                items.EquippedDetail += HandleEquipped;
                bound.Add(items);
            }
            // The other committed gameplay events of the outcomeSequence (see Sequence).
            foreach (var unit in director.Friendlies.Concat(director.Hostiles))
            {
                if (unit == null)
                    continue;
                if (unit.TryGetComponent<UnitAbilities>(out var abilities))
                {
                    Action<AbilityDefinition> used = _ => CommitGameplayEvent();
                    abilities.Used += used;
                    unbinders.Add(() => { if (abilities != null) abilities.Used -= used; });
                }
                if (unit.TryGetComponent<UnitAttacker>(out var attacker))
                {
                    Action<Health> missed = _ => CommitGameplayEvent();
                    attacker.Missed += missed;
                    unbinders.Add(() => { if (attacker != null) attacker.Missed -= missed; });
                }
                if (unit.TryGetComponent<Health>(out var health))
                {
                    Action<int> changed = _ => CommitGameplayEvent();
                    Action died = CommitGameplayEvent;
                    health.Damaged += changed;
                    health.Healed += changed;
                    health.Died += died;
                    unbinders.Add(() => { if (health != null) { health.Damaged -= changed; health.Healed -= changed; health.Died -= died; } });
                }
            }
            var mission = director.Current;
            var interactables = mission.LootContainers.Select(c => c != null ? c.Interactable : null).Concat(new[] { mission.Terminal, mission.CameraTerminal });
            foreach (var interactable in interactables)
            {
                if (interactable == null)
                    continue;
                var item = interactable;
                // Only the first completion changes the mission (a container's search, a terminal): a re-open of a searched container
                // only shows its panel, so it is not an event.
                Action<MissionInteractable> completed = done => { if (done.CompletionCount == 1) CommitGameplayEvent(); };
                item.Completed += completed;
                unbinders.Add(() => { if (item != null) item.Completed -= completed; });
            }
            var runtime = director.Runtime;
            if (runtime != null)
            {
                runtime.ObjectiveChanged += OnObjectiveChanged;
                unbinders.Add(() => runtime.ObjectiveChanged -= OnObjectiveChanged);
            }
            director.MissionFinished += OnMissionFinished;
        }

        /// <summary>Stops listening (the mission is being torn down). The outcome, if any, is kept.</summary>
        public void Unbind()
        {
            foreach (var items in bound)
                if (items != null)
                {
                    items.CollectedDetail -= OnCollectedDetail;
                    items.UsedDetail -= HandleUsed;
                    items.EquippedDetail -= HandleEquipped;
                }
            bound.Clear();
            foreach (var unbind in unbinders)
                unbind();
            unbinders.Clear();
            if (director != null)
                director.MissionFinished -= OnMissionFinished;
        }

        /// <summary>The ready report over the registered pool (null before a successful start or while the mission isn't Ready).</summary>
        public ReadyRequest ReadyReport => Ledger == null || director == null ? null : AttemptReady.Build(attempt, director, Ledger.PoolLines());

        void OnCollectedDetail(string operativeId, LootContainer container, string entryId, int taken)
        {
            var index = -1;
            if (director != null && director.Current != null)
                for (var i = 0; i < director.Current.LootContainers.Count; i++)
                    if (director.Current.LootContainers[i] == container)
                        index = i;
            OnCollected(operativeId, index, entryId, taken);
        }

        // A refused event is already an integrity failure inside the ledger (sticky, per participant): nothing to add here.
        internal LedgerEventResult OnCollected(string operativeId, int containerIndex, string entryId, int taken) =>
            Count(Ledger.Collect(operativeId, containerIndex, entryId, taken));

        internal LedgerEventResult OnUsed(string operativeId, string definitionId) => Count(Ledger.Use(operativeId, definitionId));

        internal LedgerEventResult OnEquipped(string operativeId, string entryId) => Count(Ledger.Equip(operativeId, entryId));

        void HandleUsed(string operativeId, string definitionId) => OnUsed(operativeId, definitionId);

        void HandleEquipped(string operativeId, string entryId) => OnEquipped(operativeId, entryId);

        void OnObjectiveChanged(MissionObjective objective) => CommitGameplayEvent();

        // Only an item event the ledger applied is a committed gameplay event; a refused one is an integrity failure, not an event.
        LedgerEventResult Count(LedgerEventResult result)
        {
            if (result == LedgerEventResult.Applied)
                CommitGameplayEvent();
            return result;
        }

        /// <summary>One committed gameplay event (an ability use, a death, an objective transition). Ignored after the freeze.</summary>
        internal void CommitGameplayEvent()
        {
            if (!IsFrozen)
                sequence.Commit();
        }

        void OnMissionFinished(MissionPhase phase)
        {
            if (IsFrozen || director == null || director.Runtime == null)
                return;
            var dead = new List<string>();
            foreach (var unit in director.Friendlies)
            {
                if (unit == null || !unit.TryGetComponent<UnitItems>(out var items) || items.OperativeId == null)
                    continue;
                if (!unit.TryGetComponent<Health>(out var health) || !health.IsAlive)
                    dead.Add(items.OperativeId);
            }
            Finish(phase == MissionPhase.Success ? ManifestOutcome.Success : ManifestOutcome.Defeat, Objectives(director.Runtime), dead);
        }

        static IReadOnlyList<ObjectiveResult> Objectives(MissionRuntime runtime) => runtime.Objectives.Select(o => new ObjectiveResult(o.Id,
            o.State == ObjectiveState.Completed ? ObjectiveStateKind.Completed : o.State == ObjectiveState.Failed ? ObjectiveStateKind.Failed : ObjectiveStateKind.Active))
            .ToList();

        /// <summary>
        /// The freeze at the first terminal event: the participant's runtime against the ledger (fail closed), the authoritative
        /// result (outcomeSequence: the terminal event's number in the committed gameplay sequence), and the manifest. Only the
        /// first call does anything. A sequence overflow fails closed: Failed, no bytes (sequence_overflow).
        /// </summary>
        internal void Finish(ManifestOutcome outcome, IReadOnlyList<ObjectiveResult> objectives, IReadOnlyCollection<string> deadOperativeIds)
        {
            if (IsFrozen || Ledger == null)
                return;
            IsFrozen = true;
            var states = attempt.Snapshot.Select(o => operativeState(o.OperativeId)).Where(s => s != null).ToList();
            Facts = Ledger.Freeze(attempt.AccountId, states, containerStates());
            if (!sequence.TryFreeze(out var terminal))
            {
                Outcome = ManifestBuildOutcome.SequenceOverflow();
                return;
            }
            Result = new ParticipantResult(outcome, terminal, elapsedMs(), objectives, deadOperativeIds);
            var pool = Ledger.PoolLines();
            try
            {
                var header = new ManifestHeader(attempt.AttemptId, attempt.AccountId, attempt.ProcessSessionId, buildRevision, attempt.CatalogueVersion,
                    ReadyReportBuilder.LayoutHashText(layoutHash), lootTableVersion, LootPoolHash.Compute(lootTableVersion, pool));
                Outcome = ManifestBuilder.BuildForPublication(header, Result, Facts, attempt.Snapshot);
            }
            catch (ArgumentException e)
            {
                // Stage 11 M2: a caller error (a header/facts mismatch, a dead id that isn't the participant's) is Failed, never thrown.
                FailureMessage = e.Message;
                Outcome = ManifestBuildOutcome.CallerError();
            }
        }
    }
}
