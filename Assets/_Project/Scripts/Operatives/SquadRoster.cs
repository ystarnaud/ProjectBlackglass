using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>One operative in the roster: the immutable definition and its own mutable persistent state.</summary>
    public sealed class RosterMember
    {
        internal RosterMember(OperativeDefinition definition, PersistentOperativeState state)
        {
            Definition = definition;
            State = state;
        }

        public OperativeDefinition Definition { get; }
        public PersistentOperativeState State { get; }
        public string Id => State.OperativeId;
    }

    /// <summary>
    /// The current squad of persistent operatives, living in the scene so it outlasts every generated mission (the
    /// director only destroys what it generated). It builds one PersistentOperativeState per starting definition (the id
    /// comes from the definition) and is the only place states are changed: XP awards, advancement picks and resets all
    /// raise Changed(operativeId) so a unit in a running mission can re-apply its configuration. Persistence is in memory
    /// only (decision 036); States() is the plain data a future save will store.
    /// </summary>
    public sealed class SquadRoster : MonoBehaviour
    {
        [SerializeField] OperativeDefinition[] startingSquad = new OperativeDefinition[0];
        [SerializeField] ProgressionTrack track;

        readonly List<RosterMember> members = new List<RosterMember>();

        public IReadOnlyList<RosterMember> Members => members;
        public int Count => members.Count;
        public ProgressionTrack Track => track;

        /// <summary>Raised with the operative's id after its XP, rank, picks or progression changed.</summary>
        public event Action<string> Changed;

        void Awake() => Build();

        internal void Initialize(OperativeDefinition[] squad, ProgressionTrack progression)
        {
            startingSquad = squad;
            track = progression;
            Build();
        }

        void Build()
        {
            members.Clear();
            if (startingSquad == null || startingSquad.Length == 0)
                return;
            if (track == null)
            {
                Debug.LogError($"{name}: the squad roster has no progression track.", this);
                return;
            }
            var ids = new HashSet<string>();
            foreach (var definition in startingSquad)
            {
                if (definition == null)
                {
                    Debug.LogError($"{name}: a starting-squad entry is missing.", this);
                    continue;
                }
                if (string.IsNullOrWhiteSpace(definition.Id))
                {
                    Debug.LogError($"{name}: {definition.DisplayName} has a blank operative id and was skipped.", this);
                    continue;
                }
                if (!ids.Add(definition.Id))
                {
                    Debug.LogError($"{name}: {definition.DisplayName} repeats the operative id {definition.Id} and was skipped.", this);
                    continue;
                }
                members.Add(new RosterMember(definition, new PersistentOperativeState(definition.Id)));
            }
        }

        /// <summary>The member with this id, or null.</summary>
        public RosterMember Find(string id)
        {
            if (string.IsNullOrEmpty(id))
                return null;
            foreach (var member in members)
            {
                if (member.Id == id)
                    return member;
            }
            return null;
        }

        public EffectiveConfiguration Evaluate(RosterMember member) =>
            EffectiveConfiguration.Evaluate(member.Definition, member.State, track);

        public int Rank(RosterMember member) => member.State.Rank(track);

        public int PendingPicks(RosterMember member) => member.State.PendingPicks(track);

        /// <summary>The persistent states in squad order: the data a save would store.</summary>
        public IReadOnlyList<PersistentOperativeState> States()
        {
            var states = new List<PersistentOperativeState>(members.Count);
            foreach (var member in members)
                states.Add(member.State);
            return states;
        }

        /// <summary>Adds XP to one operative. False (and nothing raised) for an unknown id or an amount of 0 or less.</summary>
        public bool AwardExperience(string id, int amount)
        {
            var member = Find(id);
            if (member == null || amount <= 0)
                return false;
            member.State.AddExperience(amount);
            Changed?.Invoke(member.Id);
            return true;
        }

        /// <summary>The track's mission-completion XP to every member, dead or alive (assumption recorded in decision 036).</summary>
        public void AwardMissionCompletion()
        {
            foreach (var member in members)
            {
                member.State.AddExperience(track.MissionCompletionXp);
                Changed?.Invoke(member.Id);
            }
        }

        public bool TryPickChoice(string id, string choiceId)
        {
            var member = Find(id);
            if (member == null || !member.State.TryPickChoice(track, choiceId))
                return false;
            Changed?.Invoke(member.Id);
            return true;
        }

        public bool ResetProgression(string id)
        {
            var member = Find(id);
            if (member == null)
                return false;
            member.State.ResetProgression();
            Changed?.Invoke(member.Id);
            return true;
        }

        internal void HandleMissionFinished(MissionPhase phase)
        {
            if (phase == MissionPhase.Success)
                AwardMissionCompletion();
        }
    }
}
