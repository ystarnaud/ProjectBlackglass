using System;
using System.Globalization;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Complete when its terminal has been worked to completion. Activating it opens the terminal; ending the mission closes
    /// it. A terminal that vanishes while the objective is Active fails the objective.
    /// </summary>
    public sealed class InteractObjective : MissionObjective
    {
        readonly MissionInteractable interactable;

        public InteractObjective(string id, string title, MissionInteractable interactable, bool isRequired = true)
            : base(id, ObjectiveType.Interact, title, isRequired)
        {
            if (interactable == null)
                throw new ArgumentNullException(nameof(interactable));
            this.interactable = interactable;
        }

        public MissionInteractable Interactable => interactable;

        public override bool HasTarget => interactable != null;
        public override Vector3 TargetPosition => interactable != null ? interactable.Position : Vector3.zero;

        public override string Describe()
        {
            if (State != ObjectiveState.Active || interactable == null || interactable.Progress <= 0f)
                return Title;
            var percent = Mathf.RoundToInt(interactable.Fraction * 100f);
            return string.Format(CultureInfo.InvariantCulture, "{0} ({1}%)", Title, percent);
        }

        protected override void OnActivated()
        {
            if (interactable != null)
                interactable.SetAvailable(true);
        }

        protected override void OnEvaluate()
        {
            if (interactable == null)
                Fail();
            else if (interactable.IsCompleted)
                Complete();
        }

        protected override void OnMissionEnded()
        {
            if (interactable != null)
                interactable.SetAvailable(false);
        }
    }
}
