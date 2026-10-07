using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The part of an operative that outlives a mission: its stable id, XP and the advancement choices it has made.
    /// Plain serializable data with no Unity object references (a future save file can store a list of these, keyed by
    /// id). Rank and the number of pending picks are derived from XP and a ProgressionTrack, never stored, so they cannot
    /// disagree with it. Transient mission state (health, cooldowns, orders) is deliberately not here.
    /// </summary>
    [Serializable]
    public sealed class PersistentOperativeState
    {
        [SerializeField] string operativeId;
        [SerializeField] int experience;
        [SerializeField] List<string> choiceIds = new List<string>();

        public PersistentOperativeState(string operativeId)
        {
            if (string.IsNullOrWhiteSpace(operativeId))
                throw new ArgumentException("An operative needs a stable id.", nameof(operativeId));
            this.operativeId = operativeId;
        }

        // For the serializer only (JsonUtility, Unity serialization); it leaves the id to the data it reads.
        PersistentOperativeState() { }

        public string OperativeId => operativeId;
        public int Experience => experience;
        public IReadOnlyList<string> ChoiceIds => choiceIds;

        public int Rank(ProgressionTrack track) => track.RankFor(experience);

        /// <summary>Choices the operative has earned but not yet made: one per rank after the first, minus the picks made. Never negative.</summary>
        public int PendingPicks(ProgressionTrack track) => Math.Max(0, Rank(track) - 1 - choiceIds.Count);

        /// <summary>Adds XP (zero is fine). Clamps at int.MaxValue instead of wrapping.</summary>
        public void AddExperience(int amount)
        {
            if (amount < 0)
                throw new ArgumentOutOfRangeException(nameof(amount), amount, "Experience cannot be negative.");
            experience = (int)Math.Min((long)int.MaxValue, (long)experience + amount);
        }

        /// <summary>Makes a pick: needs a pending pick and a choice the track knows. The same choice may be picked again.</summary>
        public bool TryPickChoice(ProgressionTrack track, string choiceId)
        {
            if (track.FindChoice(choiceId) == null || PendingPicks(track) <= 0)
                return false;
            choiceIds.Add(choiceId);
            return true;
        }

        /// <summary>Back to rank 1: no XP, no picks. The id stays.</summary>
        public void ResetProgression()
        {
            experience = 0;
            choiceIds.Clear();
        }
    }
}
