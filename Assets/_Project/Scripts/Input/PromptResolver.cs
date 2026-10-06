using System.Collections.Generic;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Utilities;

namespace Blackglass
{
    /// <summary>
    /// Answers "what should be shown for this action on this input family?" from the action's current bindings, so a
    /// rebind changes the prompt and no code hard-codes "Press A". Keyboard/mouse text is the Input System's readable
    /// path; gamepad text comes from GamepadLabels. A chord (modifier plus button) reads "RT + D-pad Up". Placeholder text only.
    /// </summary>
    public static class PromptResolver
    {
        const string None = "-";

        public static string GetPrompt(InputAction action, InputFamily family)
        {
            if (action == null)
                return None;
            var group = family.BindingGroup();
            var prompts = new List<string>();
            var bindings = action.bindings;
            for (var i = 0; i < bindings.Count; i++)
            {
                var binding = bindings[i];
                if (binding.isPartOfComposite || !InGroup(binding, group))
                    continue;
                prompts.Add(binding.isComposite ? DescribeComposite(bindings, i, family) : DescribePath(binding.effectivePath, family));
            }
            return prompts.Count == 0 ? None : string.Join(" / ", prompts);
        }

        static bool InGroup(InputBinding binding, string group)
        {
            if (string.IsNullOrEmpty(binding.groups))
                return false;
            foreach (var candidate in binding.groups.Split(InputBinding.Separator))
            {
                if (candidate == group)
                    return true;
            }
            return false;
        }

        // A chord reads "modifier + button" ("RT + D-pad Up"); any other composite (WASD) keeps its own name.
        static string DescribeComposite(ReadOnlyArray<InputBinding> bindings, int index, InputFamily family)
        {
            var header = bindings[index];
            if (header.path != "ButtonWithOneModifier")
                return header.name;
            string modifier = null;
            string button = null;
            for (var i = index + 1; i < bindings.Count && bindings[i].isPartOfComposite; i++)
            {
                if (bindings[i].name == "modifier")
                    modifier = DescribePath(bindings[i].effectivePath, family);
                else if (bindings[i].name == "button")
                    button = DescribePath(bindings[i].effectivePath, family);
            }
            return modifier != null && button != null ? $"{modifier} + {button}" : header.name;
        }

        static string DescribePath(string path, InputFamily family)
        {
            if (family == InputFamily.KeyboardMouse)
                return InputControlPath.ToHumanReadableString(path, InputControlPath.HumanReadableStringOptions.OmitDevice);
            var slash = path.IndexOf('/');
            return GamepadLabels.Label(slash < 0 ? path : path.Substring(slash + 1), family);
        }
    }
}
