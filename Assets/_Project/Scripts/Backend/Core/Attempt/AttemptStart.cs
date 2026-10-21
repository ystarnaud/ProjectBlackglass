using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Blackglass.Backend.Core
{
    /// <summary>
    /// The validated attempt (Stage 12 Task 2): what the mission process may start from, built from the backend's register
    /// answer and checked before any gameplay exists. Refused, with one code, when the answer is malformed, incomplete or
    /// inconsistent with this build. The first failure in this order wins:
    /// bad_attempt_id (not a canonical UUIDv7), bad_response (no answer, or a negative seed), bad_session_id, bad_template, catalogue_mismatch
    /// (the answer's or the snapshot's catalogue is not the build's), unsupported_participants (not exactly one),
    /// bad_snapshot (the DTO boundary refuses it, another account, negative experience, a rank below 1, a blank choice),
    /// unknown_operative_definition, bad_slots (not 0..n-1 distinct with n 1..8), then bad_snapshot again when the item
    /// ledger refuses the snapshot's items (ids, definitions, equipment). Immutable copies only: later changes to the DTO
    /// change nothing here. The seed is never printed (no ToString).
    /// </summary>
    public sealed class AttemptStart
    {
        public const int MaxOperatives = 8;

        AttemptStart() { }

        public string AttemptId { get; private set; }
        public string ProcessSessionId { get; private set; }
        public int Seed { get; private set; }
        public string TemplateId { get; private set; }
        public int TemplateVersion { get; private set; }
        public string CatalogueVersion { get; private set; }
        public string AccountId { get; private set; }
        /// <summary>The participant's admission snapshot, in slot order (slot i at index i).</summary>
        public IReadOnlyList<OperativeSnapshot> Snapshot { get; private set; }
        /// <summary>Each operative's catalogue definition id, in slot order.</summary>
        public IReadOnlyList<string> OperativeDefinitionIds { get; private set; }
        public IReadOnlyList<int> Experience { get; private set; }
        /// <summary>The rank the backend stated; the roster checks it against the track (bad_progression).</summary>
        public IReadOnlyList<int> Ranks { get; private set; }
        public IReadOnlyList<IReadOnlyList<string>> Choices { get; private set; }

        public static bool TryCreate(string attemptId, SessionRegisterResponse response, string buildCatalogueVersion,
            Func<string, bool> knownOperativeDefinition, LedgerCatalogue catalogue, out AttemptStart start, out string refusal)
        {
            if (knownOperativeDefinition == null) throw new ArgumentNullException(nameof(knownOperativeDefinition));
            if (catalogue == null) throw new ArgumentNullException(nameof(catalogue));
            start = null;
            refusal = Check(attemptId, response, buildCatalogueVersion, knownOperativeDefinition, catalogue, out var made);
            if (refusal != null) return false;
            start = made;
            return true;
        }

        static string Check(string attemptId, SessionRegisterResponse response, string buildCatalogueVersion,
            Func<string, bool> knownOperativeDefinition, LedgerCatalogue catalogue, out AttemptStart start)
        {
            start = null;
            if (!CanonicalIds.IsAttemptId(attemptId)) return "bad_attempt_id";
            if (response == null || response.seed < 0) return "bad_response"; // the backend stores check (seed >= 0)
            if (!CanonicalIds.IsUuid(response.processSessionId)) return "bad_session_id";
            var template = response.missionTemplate;
            if (template == null || !CanonicalIds.IsDefinitionId(template.id) || template.version < 1) return "bad_template";
            if (!CanonicalIds.IsSha256(buildCatalogueVersion) || !string.Equals(response.catalogueVersion, buildCatalogueVersion, StringComparison.Ordinal))
                return "catalogue_mismatch";
            foreach (var p in response.participants ?? new RegisterParticipantDto[0])
                if (p?.loadout != null && !string.Equals(p.loadout.catalogueVersion, buildCatalogueVersion, StringComparison.Ordinal))
                    return "catalogue_mismatch";
            if (response.participants == null || response.participants.Length != 1) return "unsupported_participants";

            var participant = response.participants[0];
            var loadout = participant?.loadout;
            if (loadout == null || !Base58Id.TryParse(participant.accountId, out _)
                || !string.Equals(loadout.accountId, participant.accountId, StringComparison.Ordinal))
                return "bad_snapshot";
            var operatives = loadout.operatives ?? new SnapshotOperativeDto[0];
            if (operatives.Any(o => o == null || o.experience < 0 || o.rank < 1 || (o.choices ?? new string[0]).Any(string.IsNullOrEmpty)))
                return "bad_snapshot";
            IReadOnlyList<OperativeSnapshot> snapshot;
            try { snapshot = OperativeSnapshot.FromDto(loadout); }
            catch (ArgumentException) { return "bad_snapshot"; } // ArgumentNullException included: a missing id or definition

            if (operatives.Any(o => o.definitionId == null || !knownOperativeDefinition(o.definitionId))) return "unknown_operative_definition";
            if (operatives.Length < 1 || operatives.Length > MaxOperatives
                || !operatives.Select(o => o.slot).OrderBy(s => s).SequenceEqual(Enumerable.Range(0, operatives.Length)))
                return "bad_slots";

            // The item ledger's own snapshot rules (canonical ids, no repeats, known definitions, equipment in its slot), run
            // on a throwaway ledger now so a malformed snapshot is refused before the mission is generated.
            try { new MissionItemLedger(catalogue).AddParticipant(participant.accountId, snapshot); }
            catch (ArgumentException) { return "bad_snapshot"; }

            var order = Enumerable.Range(0, operatives.Length).OrderBy(i => operatives[i].slot).ToList();
            start = new AttemptStart
            {
                AttemptId = attemptId,
                ProcessSessionId = response.processSessionId,
                Seed = response.seed,
                TemplateId = template.id,
                TemplateVersion = template.version,
                CatalogueVersion = buildCatalogueVersion,
                AccountId = participant.accountId,
                Snapshot = ReadOnly(order.Select(i => snapshot[i])),
                OperativeDefinitionIds = ReadOnly(order.Select(i => operatives[i].definitionId)),
                Experience = ReadOnly(order.Select(i => operatives[i].experience)),
                Ranks = ReadOnly(order.Select(i => operatives[i].rank)),
                Choices = ReadOnly(order.Select(i => (IReadOnlyList<string>)ReadOnly(operatives[i].choices ?? new string[0]))),
            };
            return null;
        }

        static ReadOnlyCollection<T> ReadOnly<T>(IEnumerable<T> items) => new ReadOnlyCollection<T>(items.ToList());
    }
}
