using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace Blackglass
{
    /// <summary>Pure text for the ability debug views, so the exact wording is pinned by tests. Invariant culture, placeholder style.</summary>
    public static class AbilityDescriptions
    {
        const int MaxNamedVictims = 3;

        /// <summary>One bar line: the armed marker, the slot number, the name, the prompt and "ready" or the cooldown.</summary>
        public static string Slot(int slot, string prompt, string name, float cooldownRemaining, bool armed)
        {
            var state = cooldownRemaining > 0f ? "CD " + Number(cooldownRemaining) : "ready";
            return $"{(armed ? ">" : " ")} [{slot + 1}] {name}  {prompt}  {state}";
        }

        /// <summary>The preview line: what is aimed at, distance/range, sight, the cover rule, who a blast hits, and OK or why not.</summary>
        public static string Preview(AbilityPreview preview, IReadOnlyList<Health> areaHits)
        {
            if (!preview.IsArmed)
                return string.Empty;
            var ability = preview.Ability;
            var name = ability.DisplayName;
            if (!preview.HasAim)
                return ability.TargetMode == AbilityTargetMode.Ground ? $"{name}: choose a position" : $"{name}: choose a target";

            var check = preview.Check;
            var text = new StringBuilder();
            text.Append(preview.Target != null
                ? $"{name} -> {preview.Target.name}"
                : $"{name} @ ({Number(preview.Point.x)}, {Number(preview.Point.z)})");
            text.Append($" | {Number(check.Distance)}/{Number(ability.Range)} m");
            text.Append(ability.RequiresLineOfSight ? (check.SightClear ? " | LOS clear" : " | LOS blocked") : " | no LOS needed");
            if (ability.Effect == AbilityEffect.Damage)
            {
                if (ability.CoverRule == AbilityCoverRule.Ignored)
                    text.Append(" | ignores cover");
                else if (preview.Target != null)
                    text.Append(check.TargetInCover ? $" | cover {Mathf.RoundToInt(check.HitChance * 100f)}%" : " | exposed");
            }
            if (ability.TargetMode == AbilityTargetMode.Ground)
                text.Append($" | hits {areaHits.Count}{VictimNames(areaHits)}");
            text.Append(" | ");
            text.Append(preview.IsValid ? "OK" : preview.Failure.Describe());
            if (preview.Queued)
                text.Append(" (range, sight and cooldown are checked when it runs)");
            return text.ToString();
        }

        public static string Failure(string abilityName, AbilityFailure failure) => $"{abilityName}: {failure.Describe()}";

        /// <summary>An ability order as one phrase: "Aimed Shot -> Bandit" or "Blast @ (5.0, 2.0)".</summary>
        public static string Order(AbilityCommand command) =>
            command.Target != null
                ? $"{command.Definition.DisplayName} -> {command.Target.name}"
                : $"{command.Definition.DisplayName} @ ({Number(command.Point.x)}, {Number(command.Point.z)})";

        static string VictimNames(IReadOnlyList<Health> victims)
        {
            if (victims.Count == 0)
                return string.Empty;
            var names = new List<string>();
            for (var i = 0; i < victims.Count && i < MaxNamedVictims; i++)
                names.Add(victims[i].name);
            var more = victims.Count > MaxNamedVictims ? ", ..." : string.Empty;
            return $" ({string.Join(", ", names)}{more})";
        }

        static string Number(float value) => value.ToString("0.0", CultureInfo.InvariantCulture);
    }
}
