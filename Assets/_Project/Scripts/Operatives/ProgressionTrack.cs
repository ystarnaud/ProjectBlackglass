using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// How operatives advance, as shared, immutable data: the XP needed for each rank (rank 1 needs 0), the choices on
    /// offer (one pick per rank gained after the first), and the two XP amounts the prototype awards. Holds no state: a
    /// persistent state's rank is derived from its XP and this track.
    /// </summary>
    [CreateAssetMenu(menuName = "Blackglass/Progression Track", fileName = "Progression")]
    public sealed class ProgressionTrack : ScriptableObject
    {
        [SerializeField] int[] xpThresholds = { 0, 100, 250, 450, 700 };
        [SerializeField] AdvancementChoice[] choices = new AdvancementChoice[0];
        [SerializeField, Min(0)] int missionCompletionXp = 150;
        [SerializeField, Min(1)] int debugXpStep = 50;

        // Cached read-only view over the serialized array; rebuilt if Unity swaps the array instance.
        AdvancementChoice[] choicesViewSource;
        IReadOnlyList<AdvancementChoice> choicesView;

        public int MaxRank => Mathf.Max(1, xpThresholds.Length);
        /// <summary>The choices on offer. A wrapper (not the raw array) so callers see a real <c>Count</c> and cannot write to it.</summary>
        public IReadOnlyList<AdvancementChoice> Choices
        {
            get
            {
                if (choicesView == null || !ReferenceEquals(choicesViewSource, choices))
                {
                    choicesViewSource = choices;
                    choicesView = Array.AsReadOnly(choices);
                }
                return choicesView;
            }
        }
        /// <summary>XP every operative gets when a mission succeeds.</summary>
        public int MissionCompletionXp => missionCompletionXp;
        /// <summary>XP the developer key adds.</summary>
        public int DebugXpStep => debugXpStep;

        /// <summary>The rank an operative with this much XP holds: 1 up to MaxRank. Negative XP is rank 1.</summary>
        public int RankFor(int xp)
        {
            var rank = 1;
            for (var i = 1; i < xpThresholds.Length; i++)
            {
                if (xp >= xpThresholds[i])
                    rank = i + 1;
            }
            return rank;
        }

        /// <summary>XP at which a rank is reached; ranks outside 1..MaxRank are clamped.</summary>
        public int XpForRank(int rank)
        {
            if (xpThresholds.Length == 0)
                return 0;
            return xpThresholds[Mathf.Clamp(rank, 1, xpThresholds.Length) - 1];
        }

        /// <summary>The XP of the next rank, or false at the maximum rank.</summary>
        public bool TryGetNextThreshold(int xp, out int threshold)
        {
            var rank = RankFor(xp);
            if (rank >= xpThresholds.Length)
            {
                threshold = xpThresholds.Length > 0 ? xpThresholds[xpThresholds.Length - 1] : 0;
                return false;
            }
            threshold = xpThresholds[rank];
            return true;
        }

        /// <summary>The choice with this stable id, or null (unknown, removed from the asset, blank).</summary>
        public AdvancementChoice FindChoice(string id)
        {
            if (string.IsNullOrEmpty(id))
                return null;
            foreach (var choice in choices)
            {
                if (choice != null && choice.Id == id)
                    return choice;
            }
            return null;
        }

        internal static ProgressionTrack Create(int[] thresholds, AdvancementChoice[] choices, int missionCompletionXp, int debugXpStep)
        {
            ValidateThresholds(thresholds);
            var seen = new HashSet<string>();
            foreach (var choice in choices)
            {
                if (choice == null)
                    throw new ArgumentException("A track cannot list a missing choice.", nameof(choices));
                if (!seen.Add(choice.Id))
                    throw new ArgumentException($"Two choices share the id '{choice.Id}'.", nameof(choices));
            }

            var track = CreateInstance<ProgressionTrack>();
            track.xpThresholds = (int[])thresholds.Clone();
            track.choices = (AdvancementChoice[])choices.Clone();
            track.missionCompletionXp = Mathf.Max(0, missionCompletionXp);
            track.debugXpStep = Mathf.Max(1, debugXpStep);
            return track;
        }

        static void ValidateThresholds(int[] thresholds)
        {
            if (thresholds == null || thresholds.Length == 0)
                throw new ArgumentException("A track needs at least one rank.", nameof(thresholds));
            if (thresholds[0] != 0)
                throw new ArgumentException("Rank 1 must need 0 XP.", nameof(thresholds));
            for (var i = 1; i < thresholds.Length; i++)
            {
                if (thresholds[i] <= thresholds[i - 1])
                    throw new ArgumentException("Thresholds must strictly increase.", nameof(thresholds));
            }
        }

        // Keeps a hand-edited asset valid: at least rank 1 at 0 XP, strictly increasing thresholds.
        void OnValidate()
        {
            if (xpThresholds == null || xpThresholds.Length == 0)
                xpThresholds = new[] { 0 };
            xpThresholds[0] = 0;
            for (var i = 1; i < xpThresholds.Length; i++)
                xpThresholds[i] = Mathf.Max(xpThresholds[i], xpThresholds[i - 1] + 1);
        }
    }
}
