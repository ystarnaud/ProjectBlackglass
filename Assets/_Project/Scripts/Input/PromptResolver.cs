using System.Collections.Generic;
using UnityEngine.InputSystem;

namespace Blackglass
{
    /// <summary>
    /// Answers "what should be shown for this action on this input family?" from the action's current bindings, so a
    /// rebind changes the prompt and no code hard-codes "Press A". Keyboard/mouse text is the Input System's readable
    /// path; gamepad text comes from GamepadLabels. Placeholder text only.
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
                prompts.Add(Describe(binding, family));
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

        static string Describe(InputBinding binding, InputFamily family)
        {
            if (binding.isComposite)
                return binding.name;
            var path = binding.effectivePath;
            if (family == InputFamily.KeyboardMouse)
                return InputControlPath.ToHumanReadableString(path, InputControlPath.HumanReadableStringOptions.OmitDevice);
            var slash = path.IndexOf('/');
            return GamepadLabels.Label(slash < 0 ? path : path.Substring(slash + 1), family);
        }
    }
}
