using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Blackglass.Backend.Core
{
    /// <summary>Per attempt and participant: what the backend stored about the attempt. No seed, salt or secret.</summary>
    public sealed class ManifestHeader
    {
        public ManifestHeader(string attemptId, string accountId, string processSessionId, string buildRevision, string catalogueVersion,
            string layoutHash, int lootTableVersion, string lootHash)
        {
            AttemptId = attemptId ?? throw new ArgumentNullException(nameof(attemptId));
            AccountId = accountId ?? throw new ArgumentNullException(nameof(accountId));
            ProcessSessionId = processSessionId ?? throw new ArgumentNullException(nameof(processSessionId));
            BuildRevision = buildRevision ?? throw new ArgumentNullException(nameof(buildRevision));
            CatalogueVersion = catalogueVersion ?? throw new ArgumentNullException(nameof(catalogueVersion));
            LayoutHash = layoutHash ?? throw new ArgumentNullException(nameof(layoutHash));
            LootTableVersion = lootTableVersion;
            LootHash = lootHash ?? throw new ArgumentNullException(nameof(lootHash));
        }

        public string AttemptId { get; }
        public string AccountId { get; }
        public string ProcessSessionId { get; }
        public string BuildRevision { get; }
        public string CatalogueVersion { get; }
        public string LayoutHash { get; }
        public int LootTableVersion { get; }
        public string LootHash { get; }
    }

    public sealed class ObjectiveResult
    {
        public ObjectiveResult(string objectiveId, ObjectiveStateKind state)
        {
            ObjectiveId = objectiveId ?? throw new ArgumentNullException(nameof(objectiveId));
            State = state;
        }

        public string ObjectiveId { get; }
        public ObjectiveStateKind State { get; }
    }

    /// <summary>The participant's authoritative outcome at its freeze, known to the server independently of the item ledger.</summary>
    public sealed class ParticipantResult
    {
        public ParticipantResult(ManifestOutcome outcome, int outcomeSequence, int serverElapsedMs, IReadOnlyList<ObjectiveResult> objectives,
            IReadOnlyCollection<string> deadOperativeIds)
        {
            Outcome = outcome;
            OutcomeSequence = outcomeSequence;
            ServerElapsedMs = serverElapsedMs;
            Objectives = new ReadOnlyCollection<ObjectiveResult>(new List<ObjectiveResult>(objectives ?? throw new ArgumentNullException(nameof(objectives))));
            DeadOperativeIds = new ReadOnlyCollection<string>(new List<string>(deadOperativeIds ?? throw new ArgumentNullException(nameof(deadOperativeIds))));
        }

        public ManifestOutcome Outcome { get; }
        public int OutcomeSequence { get; }
        public int ServerElapsedMs { get; }
        public IReadOnlyList<ObjectiveResult> Objectives { get; }
        /// <summary>Alive = not in this set.</summary>
        public IReadOnlyList<string> DeadOperativeIds { get; }
    }

    /// <summary>A manifest that passed the format self-check: its canonical bytes, their hash and the model.</summary>
    public sealed class BuiltManifest
    {
        readonly byte[] bytes;

        internal BuiltManifest(byte[] bytes, string hash, ResultManifestV1 manifest)
        {
            this.bytes = bytes;
            ManifestHash = hash;
            Manifest = manifest;
        }

        /// <summary>A copy of the canonical bytes, so nothing can change the built result.</summary>
        public byte[] CanonicalBytes => (byte[])bytes.Clone();
        public string ManifestHash { get; }
        public ResultManifestV1 Manifest { get; }
    }

    /// <summary>The builder refused: the value would not survive the format (the code is the format's). Never carries a secret.</summary>
    public sealed class ManifestFormatException : Exception
    {
        public ManifestFormatException(string code) : base("The manifest would be refused with " + code + ".") => Code = code;

        public string Code { get; }
    }

    public enum ManifestBuildKind { Normal, LedgerMismatchFallback, Failed }

    public sealed class ManifestBuildOutcome
    {
        internal ManifestBuildOutcome(ManifestBuildKind kind, BuiltManifest manifest, IList<string> diagnostics)
        {
            Kind = kind;
            Manifest = manifest;
            Diagnostics = new ReadOnlyCollection<string>(new List<string>(diagnostics));
        }

        /// <summary>
        /// Failed with no bytes because the caller passed inconsistent inputs (an ArgumentException from BuildForPublication: a
        /// header/facts account mismatch, a dead id that isn't the participant's; Stage 11 M2). Diagnostics: "caller_error".
        /// </summary>
        public static ManifestBuildOutcome CallerError() => new ManifestBuildOutcome(ManifestBuildKind.Failed, null, new[] { "caller_error" });

        /// <summary>Failed with no bytes because the terminal event's outcomeSequence could not be numbered within the bound
        /// (OutcomeSequence overflow: fail closed, never wrap or clamp). Diagnostics: "sequence_overflow".</summary>
        public static ManifestBuildOutcome SequenceOverflow() => new ManifestBuildOutcome(ManifestBuildKind.Failed, null, new[] { "sequence_overflow" });

        public ManifestBuildKind Kind { get; }
        /// <summary>Null when Failed.</summary>
        public BuiltManifest Manifest { get; }
        /// <summary>Bounded words: "normal:&lt;code&gt;", "fallback:&lt;code&gt;", then the ledger's failure codes.</summary>
        public IReadOnlyList<string> Diagnostics { get; }
    }

    /// <summary>
    /// Builds a participant's format-v1 manifest from facts only: never XP, rewards, energy, insurance, refunds, the seed or a
    /// salt (it is never given them). Deterministic. Fail closed: every result passes a format self-check (it parses, is a
    /// canonical fixed point, names the header's attempt and account, and keeps the facts' integrity, so a known mismatch can
    /// never come out as "ok"). The self-check is format-only; only the backend judges settlement (V4-V15).
    /// </summary>
    public static class ManifestBuilder
    {
        public static BuiltManifest Build(ManifestHeader header, ParticipantResult result, ParticipantFacts facts)
        {
            if (header == null) throw new ArgumentNullException(nameof(header));
            if (result == null) throw new ArgumentNullException(nameof(result));
            if (facts == null) throw new ArgumentNullException(nameof(facts));
            if (header.AccountId != facts.AccountId) throw new ArgumentException("The header and the facts name different participants.", nameof(facts));
            CheckDead(result, facts.Operatives.Select(o => o.OperativeId));
            var operatives = facts.Operatives.Select(o => new OperativeFact(Id(o.OperativeId), o.Slot, !result.DeadOperativeIds.Contains(o.OperativeId),
                o.ConsumedOwned, o.ConsumedLoot, o.HeldItems.Select(Id).ToList(), o.HeldStacks, o.HeldLoot, o.Collected,
                o.Equipment.Select(e => e.ItemId != null ? EquipmentFact.Item(e.Slot, Id(e.ItemId)) : EquipmentFact.Loot(e.Slot, e.ContainerIndex.Value, e.Line.Value)).ToList()))
                .ToList();
            return Assemble(header, result, operatives, facts.Integrity);
        }

        /// <summary>
        /// The truthful fallback (owner ruling D2): the verified header, the authoritative result and the preserved admission
        /// snapshot (owned items and stacks as held, the snapshot's equipment), with nothing collected or consumed and
        /// integrity "ledgerMismatch": "the mission's item facts are unknown; here is the admission state". It claims no item
        /// change, and the backend quarantines it for an operator (V14). It never reads the ledger, so it survives a ledger defect.
        /// </summary>
        public static BuiltManifest BuildLedgerMismatchFallback(ManifestHeader header, ParticipantResult result, string accountId,
            IReadOnlyList<OperativeSnapshot> admissionSnapshot)
        {
            if (header == null) throw new ArgumentNullException(nameof(header));
            if (result == null) throw new ArgumentNullException(nameof(result));
            if (admissionSnapshot == null) throw new ArgumentNullException(nameof(admissionSnapshot));
            if (header.AccountId != accountId) throw new ArgumentException("The header and the snapshot name different participants.", nameof(accountId));
            CheckDead(result, admissionSnapshot.Select(o => o.OperativeId));
            var operatives = admissionSnapshot.Select(s => new OperativeFact(Id(s.OperativeId), s.Slot, !result.DeadOperativeIds.Contains(s.OperativeId),
                new OwnedUse[0], new LootUse[0], s.Items.Select(i => Id(i.ItemId)).ToList(),
                s.Stacks.Select(x => new OwnedStackFact(x.DefinitionId, x.Quantity)).ToList(), new LootLotFact[0], new LineCount[0],
                ((EquipSlot[])Enum.GetValues(typeof(EquipSlot))).Where(slot => s.EquippedIn(slot) != null)
                    .Select(slot => EquipmentFact.Item(slot, Id(s.EquippedIn(slot)))).ToList()))
                .ToList();
            return Assemble(header, result, operatives, ManifestIntegrity.LedgerMismatch);
        }

        /// <summary>The normal build; on a format failure the fallback; on a second failure Failed with no bytes. Never throws ManifestFormatException.</summary>
        public static ManifestBuildOutcome BuildForPublication(ManifestHeader header, ParticipantResult result, ParticipantFacts facts,
            IReadOnlyList<OperativeSnapshot> admissionSnapshot)
        {
            if (facts == null) throw new ArgumentNullException(nameof(facts));
            var diagnostics = new List<string>();
            try
            {
                return new ManifestBuildOutcome(ManifestBuildKind.Normal, Build(header, result, facts), facts.FailureCodes.ToList());
            }
            catch (ManifestFormatException e) { diagnostics.Add("normal:" + e.Code); }
            try
            {
                var fallback = BuildLedgerMismatchFallback(header, result, facts.AccountId, admissionSnapshot);
                diagnostics.AddRange(facts.FailureCodes);
                return new ManifestBuildOutcome(ManifestBuildKind.LedgerMismatchFallback, fallback, diagnostics);
            }
            catch (ManifestFormatException e) { diagnostics.Add("fallback:" + e.Code); }
            diagnostics.AddRange(facts.FailureCodes);
            return new ManifestBuildOutcome(ManifestBuildKind.Failed, null, diagnostics);
        }

        static void CheckDead(ParticipantResult result, IEnumerable<string> operativeIds)
        {
            var known = new HashSet<string>(operativeIds, StringComparer.Ordinal);
            if (result.DeadOperativeIds.Any(id => !known.Contains(id))) throw new ArgumentException("A dead operative is not one of the participant's.", nameof(result));
        }

        static long Id(string base58) => Base58Id.TryParse(base58, out var id) ? id : throw new ManifestFormatException(ManifestCodes.NonCanonicalId);

        static BuiltManifest Assemble(ManifestHeader h, ParticipantResult r, IReadOnlyList<OperativeFact> operatives, ManifestIntegrity integrity)
        {
            // The header and result, in the codec's member order, each with the code the codec would give for the written value.
            var account = Id(h.AccountId);
            Text(h.AttemptId, CanonicalIds.IsAttemptId, ManifestCodes.NonCanonicalId);
            Text(h.BuildRevision, CanonicalIds.IsBuildRevision, ManifestCodes.BadValue);
            Text(h.CatalogueVersion, CanonicalIds.IsSha256, ManifestCodes.NonCanonicalId);
            Text(h.LayoutHash, CanonicalIds.IsLayoutHash, ManifestCodes.NonCanonicalId);
            Text(h.LootHash, CanonicalIds.IsSha256, ManifestCodes.NonCanonicalId);
            Number(h.LootTableVersion, 1, int.MaxValue);
            if (r.Objectives.Count > ResultManifestV1.MaxObjectives) throw new ManifestFormatException(ManifestCodes.OutOfBounds);
            foreach (var o in r.Objectives) Text(o.ObjectiveId, id => CanonicalIds.IsWord(id, ResultManifestV1.MaxObjectiveIdLength), ManifestCodes.BadValue);
            if (r.Objectives.Select(o => o.ObjectiveId).Distinct(StringComparer.Ordinal).Count() != r.Objectives.Count) throw new ManifestFormatException(ManifestCodes.DuplicateEntry);
            Number(r.OutcomeSequence, 0, int.MaxValue);
            Text(h.ProcessSessionId, CanonicalIds.IsUuid, ManifestCodes.NonCanonicalId);
            Number(r.ServerElapsedMs, 0, ResultManifestV1.MaxElapsedMs);

            var model = new ResultManifestV1(h.AttemptId, account, h.ProcessSessionId, h.BuildRevision, h.CatalogueVersion, integrity, r.Outcome,
                r.OutcomeSequence, r.ServerElapsedMs, h.LayoutHash, h.LootTableVersion, h.LootHash,
                r.Objectives.Select(o => new ObjectiveFact(o.ObjectiveId, o.State)), operatives);
            byte[] bytes;
            try { bytes = ManifestCodec.ToCanonicalBytes(model); }
            catch (ArgumentException) { throw new ManifestFormatException(ManifestCodes.NotJson); } // a lone surrogate has no UTF-8 form (EncoderFallbackException included)

            // The format self-check: what the backend's parser will do with these bytes, run here first.
            var parsed = ManifestCodec.Parse(bytes);
            if (!parsed.Ok) throw new ManifestFormatException(parsed.Code);
            if (!parsed.CanonicalBytes.SequenceEqual(bytes) || parsed.Manifest.AttemptId != h.AttemptId || parsed.Manifest.AccountId != account
                || parsed.Manifest.Integrity != integrity)
                throw new ManifestFormatException(ManifestCodes.BadValue);
            return new BuiltManifest(bytes, parsed.ManifestHash, parsed.Manifest);
        }

        static void Text(string value, Func<string, bool> wellFormed, string code)
        {
            if (StrictJsonReader.HasLoneSurrogate(value)) throw new ManifestFormatException(ManifestCodes.NotJson);
            if (StrictJsonReader.Utf8ByteCount(value) > ResultManifestV1.MaxStringBytes) throw new ManifestFormatException(ManifestCodes.OutOfBounds);
            if (!wellFormed(value)) throw new ManifestFormatException(code);
        }

        static void Number(int value, int min, int max)
        {
            if (value < 0) throw new ManifestFormatException(ManifestCodes.NotInteger); // it would be written with a sign
            if (value < min || value > max) throw new ManifestFormatException(ManifestCodes.OutOfBounds);
        }
    }
}
