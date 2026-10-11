using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Something a unit can work on until it completes: the mission terminal. Holds plain state (available flag, progress, who
    /// is working on it); the rules about who may use it live in UnitInteractor and the order in CommandableUnit. Its
    /// objective makes it available when the objective activates and closes it when the mission ends. One unit works on it at
    /// a time; the claim of a dead or destroyed unit is stale and ignored. Progress is lost on Release (a cancelled
    /// interaction keeps nothing). Advance is called by the working unit with scaled delta time, so a pause freezes it.
    /// </summary>
    public sealed class MissionInteractable : MonoBehaviour
    {
        [SerializeField] string displayName = "Terminal";
        // The flat distance from the unit to the object within which it may work.
        [SerializeField, Min(0.1f)] float range = 1.8f;
        [SerializeField, Min(0f)] float duration = 2f;

        bool available;
        bool completed;
        float progress;
        CommandableUnit user;
        // After a completion the interactable becomes available again with this duration (a negative value: stay completed).
        float repeatDuration = -1f;
        int completionCount;
        CommandableUnit lastUser;

        /// <summary>How many times the work has finished (a repeatable interactable counts each one).</summary>
        public int CompletionCount => completionCount;
        /// <summary>The unit that finished the work most recently.</summary>
        public CommandableUnit LastUser => lastUser;

        public string DisplayName => displayName;
        public float Range => range;
        public float Duration => duration;
        public float Progress => progress;
        /// <summary>0 to 1.</summary>
        public float Fraction => completed ? 1f : (duration <= 0f ? 0f : Mathf.Clamp01(progress / duration));
        public bool IsCompleted => completed;
        /// <summary>True once the objective has opened it and until the mission ends.</summary>
        public bool IsEnabled => available;
        public bool IsAvailable => available && !completed && gameObject.activeInHierarchy;
        /// <summary>The unit working on it, or null.</summary>
        public CommandableUnit User => user;
        public Vector3 Position => transform.position;

        /// <summary>
        /// Raised when the work is finished (not when it is cancelled): once for an ordinary interactable, again after every
        /// completion for a repeatable one (SetRepeatable).
        /// </summary>
        public event Action<MissionInteractable> Completed;

        internal void Initialize(float interactionRange, float interactionSeconds, string label = "Terminal")
        {
            range = Mathf.Max(0.1f, interactionRange);
            duration = Mathf.Max(0f, interactionSeconds);
            displayName = label;
        }

        public void SetAvailable(bool value) => available = value;

        public void SetLabel(string label) => displayName = label;

        /// <summary>
        /// After each completion re-arm with this duration instead of staying completed (0 = instant next time). A loot
        /// container searches once and then opens at once; the terminal never calls this.
        /// </summary>
        public void SetRepeatable(float nextDuration) => repeatDuration = Mathf.Max(0f, nextDuration);

        /// <summary>True when a living unit other than `unit` holds the claim.</summary>
        public bool IsInUseByOther(CommandableUnit unit) => user != null && user != unit && user.IsAlive;

        /// <summary>Claims the terminal for `unit`. False when it is unavailable or another living unit works on it.</summary>
        public bool TryBegin(CommandableUnit unit)
        {
            if (unit == null || !IsAvailable || IsInUseByOther(unit))
                return false;
            // A takeover of a dead or destroyed holder's claim starts fresh; the holder beginning again keeps its progress.
            if (user != unit)
                progress = 0f;
            user = unit;
            return true;
        }

        /// <summary>
        /// Adds `deltaTime` of work for the holder. False (and no change) when `unit` is not the holder or the terminal is no
        /// longer available. Completing it releases the claim.
        /// </summary>
        public bool Advance(CommandableUnit unit, float deltaTime)
        {
            if (unit == null || user != unit || !IsAvailable)
                return false;
            progress += Mathf.Max(0f, deltaTime);
            if (progress >= duration)
            {
                progress = duration;
                completed = true;
                lastUser = user;
                user = null;
                completionCount++;
                Completed?.Invoke(this);
                if (repeatDuration >= 0f)
                {
                    completed = false;
                    progress = 0f;
                    duration = repeatDuration;
                }
            }
            return true;
        }

        /// <summary>Drops the claim if `unit` holds it; an unfinished interaction loses its progress.</summary>
        public void Release(CommandableUnit unit)
        {
            if (user != unit)
                return;
            user = null;
            if (!completed)
                progress = 0f;
        }
    }
}
