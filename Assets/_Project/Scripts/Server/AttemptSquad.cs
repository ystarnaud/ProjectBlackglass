using System.Collections.Generic;
using Blackglass.Backend.Core;

namespace Blackglass
{
    /// <summary>
    /// The attempt's squad (Stage 12 Task 5): each snapshot operative, in slot order, paired with this build's definition and
    /// its restored persistent state (the backend's id, XP and picks). Refused with one code: unknown_operative_definition,
    /// bad_operative_definition (the asset is invalid), bad_progression (picks the track doesn't allow, or a stated rank that
    /// isn't the track's rank for the XP).
    /// </summary>
    public static class AttemptSquad
    {
        public static bool TryMembers(AttemptStart start, IReadOnlyList<OperativeDefinition> definitions, ProgressionTrack track,
            out List<(OperativeDefinition Definition, PersistentOperativeState State)> members, out string problem)
        {
            members = new List<(OperativeDefinition, PersistentOperativeState)>();
            problem = null;
            for (var i = 0; i < start.Snapshot.Count; i++)
            {
                var definition = Find(definitions, start.OperativeDefinitionIds[i]);
                if (definition == null)
                    problem = "unknown_operative_definition";
                else if (!definition.IsValid(out _))
                    problem = "bad_operative_definition";
                else if (track == null || start.Ranks[i] != track.RankFor(start.Experience[i]))
                    problem = "bad_progression";
                var state = problem == null
                    ? PersistentOperativeState.Restore(start.Snapshot[i].OperativeId, start.Experience[i], start.Choices[i], track, out problem)
                    : null;
                if (problem != null)
                {
                    members.Clear();
                    return false;
                }
                members.Add((definition, state));
            }
            return true;
        }

        /// <summary>The definition with this id among the scene's, or null. Unity's == on each entry: a missing asset is a fake null.</summary>
        public static OperativeDefinition Find(IReadOnlyList<OperativeDefinition> definitions, string id)
        {
            if (definitions == null || string.IsNullOrEmpty(id))
                return null;
            foreach (var definition in definitions)
                if (definition != null && definition.Id == id)
                    return definition;
            return null;
        }
    }
}
