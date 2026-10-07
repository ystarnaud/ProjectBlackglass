using System;
using System.Collections.Generic;

namespace Blackglass
{
    /// <summary>Where a generated mission is in play. Generation has its own state (MissionState on the director).</summary>
    public enum MissionPhase
    {
        /// <summary>Not started (or no mission).</summary>
        Inactive,
        /// <summary>Running; required objectives are open.</summary>
        Active,
        /// <summary>Every required objective is Completed and the extraction objective is open.</summary>
        ExtractionOpen,
        /// <summary>The extraction objective is Completed. Terminal.</summary>
        Success,
        /// <summary>The squad is dead or a required objective failed. Terminal.</summary>
        Failure,
    }

    /// <summary>
    /// The state of one mission: a list of objectives and the phase they imply. Plain C#, ticked by MissionDirector. The
    /// extraction objective is not a goal: it starts Inactive and the runtime opens it when every required goal is Completed;
    /// completing it is success. Success and Failure are terminal, and failure is judged first, so everyone dead in the same
    /// tick as an extraction is a failure. Pull-based: each Tick re-derives objective states from the world, so the result
    /// cannot drift from the scene. State is plain fields; nothing is kept in views.
    /// </summary>
    public sealed class MissionRuntime
    {
        readonly List<MissionObjective> objectives = new List<MissionObjective>();
        readonly MissionObjective extraction;
        readonly IReadOnlyList<Health> squad;

        public MissionRuntime(IEnumerable<MissionObjective> goals, MissionObjective extraction, IReadOnlyList<Health> squad)
        {
            if (goals == null)
                throw new ArgumentNullException(nameof(goals));
            if (extraction == null)
                throw new ArgumentNullException(nameof(extraction));
            if (extraction.IsRequired)
                throw new ArgumentException("The extraction objective opens when the required goals are done, so it cannot be required itself.", nameof(extraction));
            if (squad == null)
                throw new ArgumentNullException(nameof(squad));
            objectives.AddRange(goals);
            objectives.Add(extraction);
            this.extraction = extraction;
            this.squad = squad;
            foreach (var objective in objectives)
                objective.StateChanged += OnObjectiveChanged;
        }

        public MissionPhase Phase { get; private set; }
        public bool IsOver => Phase == MissionPhase.Success || Phase == MissionPhase.Failure;
        /// <summary>True after Detach: the runtime no longer ticks or forwards anything.</summary>
        public bool IsDetached { get; private set; }
        /// <summary>The goals in order, then the extraction.</summary>
        public IReadOnlyList<MissionObjective> Objectives => objectives;
        public MissionObjective Extraction => extraction;

        public int LivingSquad
        {
            get
            {
                var living = 0;
                foreach (var unit in squad)
                {
                    if (unit != null && unit.IsAlive)
                        living++;
                }
                return living;
            }
        }

        public event Action<MissionPhase> PhaseChanged;
        public event Action<MissionObjective> ObjectiveChanged;

        /// <summary>Activates every goal (not the extraction) and begins the mission.</summary>
        public void Start()
        {
            if (Phase != MissionPhase.Inactive || IsDetached)
                return;
            foreach (var objective in objectives)
            {
                if (objective != extraction)
                    objective.Activate();
            }
            SetPhase(MissionPhase.Active);
        }

        public void Tick()
        {
            if (IsDetached || Phase == MissionPhase.Inactive || IsOver)
                return;
            if (squad.Count > 0 && LivingSquad == 0)
            {
                Finish(MissionPhase.Failure);
                return;
            }
            foreach (var objective in objectives)
                objective.Evaluate();
            foreach (var objective in objectives)
            {
                if (objective.IsRequired && objective.State == ObjectiveState.Failed)
                {
                    Finish(MissionPhase.Failure);
                    return;
                }
            }
            if (Phase == MissionPhase.Active && AllRequiredComplete())
            {
                extraction.Activate();
                SetPhase(MissionPhase.ExtractionOpen);
                extraction.Evaluate();
            }
            if (Phase == MissionPhase.ExtractionOpen && extraction.State == ObjectiveState.Completed)
                Finish(MissionPhase.Success);
        }

        /// <summary>Stops the runtime for good (the mission is being torn down): nothing it holds can change it any more.</summary>
        public void Detach()
        {
            if (IsDetached)
                return;
            IsDetached = true;
            foreach (var objective in objectives)
                objective.StateChanged -= OnObjectiveChanged;
        }

        bool AllRequiredComplete()
        {
            foreach (var objective in objectives)
            {
                if (objective.IsRequired && objective.State != ObjectiveState.Completed)
                    return false;
            }
            return true;
        }

        void Finish(MissionPhase result)
        {
            SetPhase(result);
            foreach (var objective in objectives)
                objective.EndMission();
        }

        void SetPhase(MissionPhase next)
        {
            if (Phase == next)
                return;
            Phase = next;
            PhaseChanged?.Invoke(next);
        }

        void OnObjectiveChanged(MissionObjective objective) => ObjectiveChanged?.Invoke(objective);
    }
}
