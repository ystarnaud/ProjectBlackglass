using UnityEngine;

namespace Blackglass
{
    public enum InteractionFailure
    {
        None,
        NoTarget,
        NotAvailable,
        InUse,
    }

    public enum InteractionStep
    {
        Working,
        Completed,
        Lost,
    }

    /// <summary>
    /// The unit's interaction capability: the rules for working on a MissionInteractable and the claim it holds while it does.
    /// CommandableUnit runs the order (walking, facing, when to start, when to give up); this component only answers "may I",
    /// "am I close enough" and advances or releases the work. Added to the friendly units at spawn, like UnitAbilities; a
    /// unit without it refuses Interact orders. The reason for the last refusal is kept for debug text.
    /// </summary>
    public sealed class UnitInteractor : MonoBehaviour
    {
        CommandableUnit unit;
        MissionInteractable current;

        public InteractionFailure LastFailure { get; private set; }

        /// <summary>The terminal this unit holds a claim on, or null.</summary>
        public MissionInteractable Current => current;

        public bool IsWorking => current != null;

        CommandableUnit Unit => unit != null ? unit : unit = GetComponent<CommandableUnit>();

        /// <summary>
        /// Whether the target can be worked on: it exists, is available, and no other living unit holds it. With
        /// `allowOtherUser` the claim is ignored (a queued order is only checked for what cannot change by then).
        /// </summary>
        public InteractionFailure Check(MissionInteractable target, bool allowOtherUser = false)
        {
            if (target == null)
                return InteractionFailure.NoTarget;
            if (!target.IsAvailable)
                return InteractionFailure.NotAvailable;
            if (!allowOtherUser && target.IsInUseByOther(Unit))
                return InteractionFailure.InUse;
            return InteractionFailure.None;
        }

        public bool InRange(MissionInteractable target, Vector3 from) =>
            target != null && CoverRules.FlatDistance(from, target.Position) <= target.Range;

        /// <summary>Claims the target and starts working. False, with the reason in LastFailure, when it cannot.</summary>
        public bool TryStart(MissionInteractable target)
        {
            Release();
            var failure = Check(target);
            if (failure == InteractionFailure.None && !target.TryBegin(Unit))
                failure = InteractionFailure.InUse;
            LastFailure = failure;
            if (failure != InteractionFailure.None)
                return false;
            current = target;
            return true;
        }

        /// <summary>One step of work. Lost when the claim or the terminal is gone; Completed when it just finished.</summary>
        public InteractionStep Advance(float deltaTime)
        {
            if (current == null)
                return InteractionStep.Lost;
            var target = current;
            if (!target.Advance(Unit, deltaTime))
            {
                target.Release(Unit);
                current = null;
                return InteractionStep.Lost;
            }
            if (target.IsCompleted)
            {
                current = null;
                return InteractionStep.Completed;
            }
            return InteractionStep.Working;
        }

        /// <summary>Gives the claim up (a cancelled interaction keeps no progress). Safe to call when nothing is held.</summary>
        public void Release()
        {
            if (current == null)
                return;
            current.Release(Unit);
            current = null;
        }

        internal void Record(InteractionFailure failure) => LastFailure = failure;
    }
}
