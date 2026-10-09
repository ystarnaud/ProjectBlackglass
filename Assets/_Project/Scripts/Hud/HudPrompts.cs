using System.Collections.Generic;
using System.Globalization;
using UnityEngine.InputSystem;

namespace Blackglass
{
    public struct HudPromptContext
    {
        public bool Paused, AbilityArmed;
        public string TerminalName;   // non-empty while a usable terminal is in reach
    }

    /// <summary>
    /// Decides which control hints the HUD shows for the current situation and what they read for the active input family.
    /// The text always comes from PromptResolver, so a rebind or a different controller changes it and no key or button
    /// name is written here.
    /// </summary>
    public static class HudPrompts
    {
        const int MaxEntries = 6;
        const string None = "-";
        const string Separator = " / ";

        struct Spec
        {
            public string LabelKbm, LabelPad;
            public string[] KbmPaths, PadPaths;
        }

        static Spec Same(string label, params string[] paths) =>
            new Spec { LabelKbm = label, LabelPad = label, KbmPaths = paths, PadPaths = paths };

        static Spec Split(string label, string kbmPath, string padPath) =>
            new Spec { LabelKbm = label, LabelPad = label, KbmPaths = new[] { kbmPath }, PadPaths = new[] { padPath } };

        static readonly Spec Switch = new Spec
        {
            LabelKbm = "Switch", LabelPad = "Switch",
            KbmPaths = new[] { "Character/NextCharacter" },
            PadPaths = new[] { "Character/PreviousCharacter", "Character/NextCharacter" },
        };

        static readonly Spec[] RealTime =
        {
            new Spec { LabelKbm = "Order", LabelPad = "Attack", KbmPaths = new[] { "Commands/Command" }, PadPaths = new[] { "Commands/Attack" } },
            Same("Pause", "Commands/ToggleTacticalPause"),
            Switch,
            Same("Follow", "Character/ToggleFollow"),
            Same("Stop", "Commands/Stop"),
        };

        static readonly Spec[] Paused =
        {
            Same("Resume", "Commands/ToggleTacticalPause"),
            Split("Order", "Commands/Command", "Commands/Confirm"),
            Same("Queue", "Commands/QueueModifier"),
            Same("Cancel", "Commands/Cancel"),
            Switch,
        };

        static readonly Spec[] Armed =
        {
            Split("Cast", "Commands/Command", "Commands/Confirm"),
            Same("Cancel", "Commands/Cancel"),
            Same("Queue", "Commands/QueueModifier"),
        };

        /// <summary>Replaces the contents of `into` with the hints for `context` in `family`. Null controls give an empty list.</summary>
        public static void Build(HudPromptContext context, InputFamily family, InputActionAsset controls, List<HudPromptEntry> into)
        {
            into.Clear();
            if (controls == null)
                return;

            if (!string.IsNullOrEmpty(context.TerminalName))
            {
                Add(into, new Spec
                {
                    LabelKbm = "Interact " + context.TerminalName, LabelPad = "Interact " + context.TerminalName,
                    KbmPaths = new[] { "Commands/Interact" }, PadPaths = new[] { "Commands/Confirm" },
                }, family, controls);
            }

            var specs = context.AbilityArmed ? Armed : context.Paused ? Paused : RealTime;
            foreach (var spec in specs)
            {
                if (into.Count >= MaxEntries)
                    break;
                Add(into, spec, family, controls);
            }
        }

        /// <summary>The hint for ability slot `slot` (0-based). Falls back to the slot number when nothing is bound.</summary>
        public static string AbilityPrompt(int slot, InputFamily family, InputActionAsset controls)
        {
            var number = (slot + 1).ToString(CultureInfo.InvariantCulture);
            if (controls == null)
                return number;
            var prompt = PromptResolver.GetPrompt(controls.FindAction("Commands/Ability" + number), family);
            return string.IsNullOrEmpty(prompt) || prompt == None ? number : prompt;
        }

        static void Add(List<HudPromptEntry> into, Spec spec, InputFamily family, InputActionAsset controls)
        {
            var keyboard = family == InputFamily.KeyboardMouse;
            var prompt = Resolve(keyboard ? spec.KbmPaths : spec.PadPaths, family, controls);
            if (string.IsNullOrEmpty(prompt))
                return;
            into.Add(new HudPromptEntry { Label = keyboard ? spec.LabelKbm : spec.LabelPad, Prompt = prompt });
        }

        static string Resolve(string[] paths, InputFamily family, InputActionAsset controls)
        {
            var parts = new List<string>();
            foreach (var path in paths)
            {
                var text = PromptResolver.GetPrompt(controls.FindAction(path), family);
                if (!string.IsNullOrEmpty(text) && text != None)
                    parts.Add(text);
            }
            return string.Join(Separator, parts);
        }
    }
}
