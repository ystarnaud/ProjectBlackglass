using System;
using UnityEngine;

namespace Blackglass
{
    public enum ObjectiveType
    {
        EliminateHostiles,
        Interact,
        ReachZone,
    }

    public enum ObjectiveState
    {
        Inactive,
        Active,
        Completed,
        Failed,
    }

    /// <summary>
    /// One goal of a mission. Plain C# with plain-field state, so it can be tested without a scene and serialised later. An
    /// objective exists from the moment it is in a MissionRuntime, whether or not the player is told about it (IsKnown, true
    /// by default) and whether or not it is open yet (Inactive until Activate). Active objectives are re-derived from world
    /// state by Evaluate each frame; Complete and Fail act only on an Active objective.
    /// </summary>
    public abstract class MissionObjective
    {
        ObjectiveState state;

        protected MissionObjective(string id, ObjectiveType type, string title, bool isRequired)
        {
            if (string.IsNullOrEmpty(id))
                throw new ArgumentException("An objective needs an id.", nameof(id));
            Id = id;
            Type = type;
            Title = string.IsNullOrEmpty(title) ? id : title;
            IsRequired = isRequired;
        }

        public string Id { get; }
        public ObjectiveType Type { get; }
        public string Title { get; }
        /// <summary>A required objective must be Completed before extraction opens; one that Fails ends the mission.</summary>
        public bool IsRequired { get; }
        public ObjectiveState State => state;
        /// <summary>Whether the player has been told about this objective (HUD line, marker). Defaults to true.</summary>
        public bool IsKnown { get; private set; } = true;
        public virtual bool HasTarget => false;
        public virtual Vector3 TargetPosition => Vector3.zero;

        /// <summary>Raised after every state change.</summary>
        public event Action<MissionObjective> StateChanged;

        public void SetKnown(bool known) => IsKnown = known;

        /// <summary>Inactive to Active; ignored in any other state.</summary>
        public void Activate()
        {
            if (state != ObjectiveState.Inactive)
                return;
            SetState(ObjectiveState.Active);
            OnActivated();
        }

        /// <summary>Re-derives the state from the world; does nothing unless Active.</summary>
        public void Evaluate()
        {
            if (state == ObjectiveState.Active)
                OnEvaluate();
        }

        /// <summary>The mission is over: stop offering anything (an interactable becomes unavailable). State is kept.</summary>
        public void EndMission() => OnMissionEnded();

        /// <summary>A short line for the HUD, without any status prefix.</summary>
        public abstract string Describe();

        protected virtual void OnActivated() { }
        protected virtual void OnEvaluate() { }
        protected virtual void OnMissionEnded() { }

        protected void Complete()
        {
            if (state == ObjectiveState.Active)
                SetState(ObjectiveState.Completed);
        }

        protected void Fail()
        {
            if (state == ObjectiveState.Active)
                SetState(ObjectiveState.Failed);
        }

        void SetState(ObjectiveState next)
        {
            if (state == next)
                return;
            state = next;
            StateChanged?.Invoke(this);
        }
    }
}
