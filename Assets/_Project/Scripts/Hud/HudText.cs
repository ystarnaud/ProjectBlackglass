using System;
using System.Globalization;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The HUD's wording rules as pure functions: number formats, names, command and cover text, the fog rules for objective
    /// rows and the extraction strip. No scene access, so every rule is a plain test.
    /// </summary>
    public static class HudText
    {
        static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        public static string Health(int current, int max) => current.ToString(Invariant) + "/" + max.ToString(Invariant);

        /// <summary>
        /// Seconds rounded up to the tenth ("4.3"); from ten seconds on, whole seconds ("25"). The rounded value decides, so
        /// 9.94 reads "10", never "10.0".
        /// </summary>
        public static string Seconds(float seconds)
        {
            var tenths = Mathf.Ceil(Mathf.Max(0f, seconds) * 10f) / 10f;
            if (tenths >= 10f) return Mathf.CeilToInt(seconds).ToString(Invariant);
            return tenths.ToString("0.0", Invariant);
        }

        public static string CleanName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            const string clone = "(Clone)";
            return raw.EndsWith(clone, StringComparison.Ordinal) ? raw.Substring(0, raw.Length - clone.Length).Trim() : raw.Trim();
        }

        public static string Initials(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "?";
            var parts = name.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var text = parts.Length == 1 ? parts[0].Substring(0, 1) : parts[0].Substring(0, 1) + parts[1].Substring(0, 1);
            return text.ToUpperInvariant();
        }

        /// <summary>
        /// One queued order as a short phrase. An attack names its target only while `targetShown` says the player may see
        /// it (null means shown), so the queue never leaks a hostile the knowledge filter hides.
        /// </summary>
        public static string Step(UnitCommand command, Func<Health, bool> targetShown)
        {
            switch (command)
            {
                case MoveCommand _: return "Move";
                case MoveToCoverCommand _: return "Take cover";
                case AttackCommand attack:
                    return attack.Target != null && (targetShown == null || targetShown(attack.Target))
                        ? "Attack " + CleanName(attack.Target.name)
                        : "Attack (target lost)";
                case InteractCommand interact:
                    return interact.Target != null ? "Interact " + interact.Target.DisplayName : "Interact";
                case AbilityCommand ability:
                    return ability.Definition != null ? ability.Definition.DisplayName : "Ability";
                case StopCommand _: return "Stop";
                default: return command == null ? string.Empty : command.GetType().Name;
            }
        }

        public static string Cover(CoverStatus status, CoverHeight height, CoverPlacement placement)
        {
            if (status == CoverStatus.Reserved) return "Moving to cover";
            if (status != CoverStatus.Occupied) return "Exposed";
            if (placement == CoverPlacement.Corner) return "Corner cover";
            return height == CoverHeight.Low ? "Low cover" : "Tall cover";
        }

        public static string CompanionTag(bool parked, bool held, bool followOn) =>
            held ? "HOLDING" : parked ? "PARKED" : followOn ? "FOLLOWING" : "ATTACHED";

        /// <summary>
        /// The objective list's fog rules. A known objective reads as its own description; an unknown one shows only its
        /// vague title and only when `listUnknown`; otherwise it is omitted (false).
        /// </summary>
        public static bool TryObjectiveRow(MissionObjective objective, bool listUnknown, out HudObjectiveKind kind, out string text)
        {
            kind = HudObjectiveKind.Active; text = string.Empty;
            if (objective == null) return false;
            if (!objective.IsKnown)
            {
                if (!listUnknown || string.IsNullOrEmpty(objective.VagueTitle)) return false;
                kind = HudObjectiveKind.Unknown; text = objective.VagueTitle; return true;
            }
            text = objective.Describe();
            switch (objective.State)
            {
                case ObjectiveState.Completed: kind = HudObjectiveKind.Completed; break;
                case ObjectiveState.Failed: kind = HudObjectiveKind.Failed; break;
                case ObjectiveState.Inactive: kind = HudObjectiveKind.Locked; break;
                default: kind = HudObjectiveKind.Active; break;
            }
            return true;
        }

        public static HudExtractionState ExtractionOf(MissionObjective extraction, MissionPhase phase, int inside)
        {
            if (extraction == null) return HudExtractionState.Hidden;
            if (phase == MissionPhase.Success || extraction.State == ObjectiveState.Completed) return HudExtractionState.Extracted;
            if (!extraction.IsKnown) return HudExtractionState.Unknown;
            if (extraction.State == ObjectiveState.Inactive) return HudExtractionState.Locked;
            if (extraction.State == ObjectiveState.Active) return inside > 0 ? HudExtractionState.Active : HudExtractionState.Available;
            return HudExtractionState.Hidden;
        }

        public static string ExtractionLabel(HudExtractionState state, int inside, int required)
        {
            switch (state)
            {
                case HudExtractionState.Unknown: return "EXTRACTION UNKNOWN";
                case HudExtractionState.Locked: return "EXTRACTION LOCKED";
                case HudExtractionState.Available: return "EXTRACTION AVAILABLE";
                case HudExtractionState.Active: return "EXTRACTION ACTIVE " + inside.ToString(Invariant) + "/" + required.ToString(Invariant) + " IN ZONE";
                case HudExtractionState.Extracted: return "EXTRACTED";
                default: return string.Empty;
            }
        }

        public static string Pause(string resumePrompt) => "TACTICAL PAUSE - " + resumePrompt + " to resume";
    }
}
