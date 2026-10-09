using UnityEngine.InputSystem;

namespace Blackglass
{
    /// <summary>
    /// What a click on the HUD asks of gameplay. Every request is the call the existing input already makes (a click on a
    /// unit, Tab, the ability keys, Stop, Follow, Pause), so the HUD adds no rule of its own, and each has a controller
    /// equivalent (spec section 8). A unit can be destroyed or die between the last snapshot and the click: such a unit, a
    /// missing source or an ineligible unit makes the request a safe no-op (Unity null checks, never ReferenceEquals).
    /// </summary>
    public sealed class HudRequests
    {
        readonly HudSources sources;
        readonly InputActionReference queueModifier;

        public HudRequests(HudSources sources, InputActionReference queueModifier)
        {
            this.sources = sources;
            this.queueModifier = queueModifier;
        }

        /// <summary>True while the queue modifier is held: a card click then adds or removes the unit.</summary>
        public bool AdditiveHeld => InputActionUtility.IsPressed(queueModifier);

        /// <summary>Selects only this unit, or toggles it when additive (UnitSelection.Select / Toggle, as a click on the unit).</summary>
        public void SelectUnit(CommandableUnit unit, bool additive)
        {
            var selection = sources != null ? sources.selection : null;
            if (selection == null || unit == null || !unit.TryGetComponent<SelectableUnit>(out var selectable)
                || !ActiveCharacter.IsEligible(selectable))
                return;
            if (additive)
                selection.Toggle(selectable);
            else
                selection.Select(selectable);
        }

        /// <summary>Takes control of the unit by Tab's rules (ActiveCharacter.TakeControl). False when nothing changed.</summary>
        public bool TakeControl(CommandableUnit unit)
        {
            var active = sources != null ? sources.activeCharacter : null;
            return active != null && unit != null && active.TakeControl(unit);
        }

        /// <summary>Arms the slot, or disarms it when that slot is the armed one (the ability keys' rule).</summary>
        public void ToggleAbility(int slot)
        {
            var targeting = sources != null ? sources.abilityTargeting : null;
            if (targeting != null)
                targeting.Pick(slot);
        }

        /// <summary>Stops the unit and empties its queue: the Stop action's order, for this one unit.</summary>
        public void ClearOrders(CommandableUnit unit)
        {
            if (unit != null)
                GroupOrders.Issue(new[] { unit }, new StopCommand(), IssueMode.Replace);
        }

        public void ToggleFollow()
        {
            var active = sources != null ? sources.activeCharacter : null;
            if (active != null)
                active.ToggleFollow();
        }

        public void TogglePause()
        {
            var pause = sources != null ? sources.tacticalPause : null;
            if (pause != null)
                pause.Toggle();
        }
    }
}
