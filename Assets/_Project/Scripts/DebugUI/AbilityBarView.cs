using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Blackglass
{
    /// <summary>
    /// Debug-only on-screen text (IMGUI) for abilities, bottom-left, working while paused: who casts, each slot with its
    /// prompt, name and readiness or cooldown (the armed one marked), the preview of what the pointer is on (target,
    /// range, sight, cover, hits, OK, the walk into position or why not), the ability orders running (with the walk, if
    /// any) or queued, and the last failure for a few seconds. With a controller, holding the menu trigger says so. Not
    /// production UI. The text itself is built by AbilityDescriptions and BuildLines so tests can read it.
    /// </summary>
    public sealed class AbilityBarView : MonoBehaviour
    {
        const float FailureSeconds = 3f;
        const float LineHeight = 20f;

        [SerializeField] AbilityTargeting targeting;
        // Optional: without it the bar shows keyboard prompts.
        [SerializeField] ActiveInputDevice inputDevice;
        [SerializeField] InputActionAsset controls;
        // Read only (the AbilityMenuGate owns it): true while a controller's menu trigger is held.
        [SerializeField] InputActionReference menuAction;

        readonly List<string> lines = new List<string>();

        internal void Initialize(AbilityTargeting abilityTargeting, ActiveInputDevice device, InputActionAsset actions, InputActionReference menu)
        {
            targeting = abilityTargeting;
            inputDevice = device;
            controls = actions;
            menuAction = menu;
        }

        InputFamily Family => inputDevice != null ? inputDevice.Family : InputFamily.KeyboardMouse;

        /// <summary>The lines for the current state. Rebuilt on every call; the list is reused.</summary>
        internal IReadOnlyList<string> BuildLines()
        {
            lines.Clear();
            if (targeting == null)
                return lines;
            var caster = targeting.Caster;
            var abilities = targeting.CasterAbilities;
            if (caster == null || abilities == null)
            {
                lines.Add("Abilities: the unit in control has none");
                return lines;
            }

            var family = Family;
            var header = $"Abilities: {caster.name}";
            if (family.IsController())
            {
                header += InputActionUtility.IsPressed(menuAction)
                    ? "   [ABILITY MENU: press a D-pad direction]"
                    : $"   (hold {Prompt("Commands/AbilityMenu", "-", family)} to pick)";
            }
            lines.Add(header);

            for (var i = 0; i < abilities.Count; i++)
            {
                var ability = abilities.Definition(i);
                if (ability == null)
                    continue;
                lines.Add(AbilityDescriptions.Slot(i, Prompt($"Commands/Ability{i + 1}", (i + 1).ToString(), family),
                    ability.DisplayName, abilities.CooldownRemaining(i), targeting.ArmedSlot == i));
            }

            if (targeting.IsArmed)
                lines.Add(AbilityDescriptions.Preview(targeting.Preview, targeting.AreaHits));

            if (caster.CurrentCommand is AbilityCommand casting)
                lines.Add(AbilityDescriptions.Running(casting, caster.AbilityPhase));
            for (var i = 0; i < caster.PendingCommands.Count; i++)
            {
                if (caster.PendingCommands[i] is AbilityCommand queued)
                    lines.Add("Queued: " + AbilityDescriptions.Order(queued));
            }

            if (abilities.LastFailedAbility != null && Time.unscaledTime - abilities.LastFailureTime < FailureSeconds)
                lines.Add(AbilityDescriptions.Failure(abilities.LastFailedAbility.DisplayName, abilities.LastFailure));
            return lines;
        }

        string Prompt(string actionPath, string fallback, InputFamily family) =>
            controls != null ? PromptResolver.GetPrompt(controls.FindAction(actionPath), family) : fallback;

        void OnGUI()
        {
            var shown = BuildLines();
            for (var i = 0; i < shown.Count; i++)
                GUI.Label(new Rect(10f, Screen.height - 10f - LineHeight * (shown.Count - i), 900f, LineHeight + 2f), shown[i]);
        }
    }
}
