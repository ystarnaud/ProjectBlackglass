using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Blackglass.Backend.Core
{
    /// <summary>The bounded refusal codes of a body that is not a format-v1 manifest (Backend ManifestCodes, the 422 codes).</summary>
    public static class ManifestCodes
    {
        public const string TooLarge = "TooLarge";
        public const string NotJson = "NotJson";
        public const string TooDeep = "TooDeep";
        public const string WrongType = "WrongType";
        public const string DuplicateKey = "DuplicateKey";
        public const string UnknownMember = "UnknownMember";
        public const string MissingMember = "MissingMember";
        public const string UnsupportedFormat = "UnsupportedFormat";
        public const string NotInteger = "NotInteger";
        public const string OutOfBounds = "OutOfBounds";
        public const string NonCanonicalId = "NonCanonicalId";
        public const string BadValue = "BadValue";
        public const string BadEnum = "BadEnum";
        public const string BackendOnlyOutcome = "BackendOnlyOutcome";
        public const string DuplicateEntry = "DuplicateEntry";

        public static readonly IReadOnlyList<string> All = new ReadOnlyCollection<string>(new[]
        {
            TooLarge, NotJson, TooDeep, WrongType, DuplicateKey, UnknownMember, MissingMember, UnsupportedFormat, NotInteger, OutOfBounds,
            NonCanonicalId, BadValue, BadEnum, BackendOnlyOutcome, DuplicateEntry,
        });
    }

    /// <summary>The five outcomes a mission process may report. infra_void is backend-only and has no member here.</summary>
    public enum ManifestOutcome { Success, Defeat, Abandoned, Disconnected, Expired }

    public enum ManifestIntegrity { Ok, LedgerMismatch }

    public enum ObjectiveStateKind { Active, Completed, Failed }

    /// <summary>Declared in the ordinal order of their words, which is the canonical sort order of equipment lists.</summary>
    public enum EquipSlot { Armor, Utility, Weapon }

    /// <summary>The format's words for the enumerations.</summary>
    public static class ManifestWords
    {
        static readonly string[] Outcomes = { "success", "defeat", "abandoned", "disconnected", "expired" };
        static readonly string[] Integrities = { "ok", "ledgerMismatch" };
        static readonly string[] States = { "active", "completed", "failed" };
        static readonly string[] Slots = { "armor", "utility", "weapon" };

        public const string InfraVoid = "infra_void";

        public static string Of(ManifestOutcome value) => Outcomes[(int)value];
        public static string Of(ManifestIntegrity value) => Integrities[(int)value];
        public static string Of(ObjectiveStateKind value) => States[(int)value];
        public static string Of(EquipSlot value) => Slots[(int)value];

        public static bool TryOutcome(string word, out ManifestOutcome value) => TryFind(Outcomes, word, out value);
        public static bool TryIntegrity(string word, out ManifestIntegrity value) => TryFind(Integrities, word, out value);
        public static bool TryState(string word, out ObjectiveStateKind value) => TryFind(States, word, out value);
        public static bool TrySlot(string word, out EquipSlot value) => TryFind(Slots, word, out value);

        static bool TryFind<T>(string[] words, string word, out T value) where T : struct
        {
            var index = Array.IndexOf(words, word);
            value = index >= 0 ? (T)(object)index : default;
            return index >= 0;
        }
    }

    public sealed class ObjectiveFact
    {
        public ObjectiveFact(string objectiveId, ObjectiveStateKind state)
        {
            ObjectiveId = objectiveId ?? throw new ArgumentNullException(nameof(objectiveId));
            State = state;
        }

        public string ObjectiveId { get; }
        public ObjectiveStateKind State { get; }
    }

    /// <summary>Owned units of one consumable definition an operative used.</summary>
    public sealed class OwnedUse
    {
        public OwnedUse(string definitionId, int quantity)
        {
            DefinitionId = definitionId ?? throw new ArgumentNullException(nameof(definitionId));
            Quantity = quantity;
        }

        public string DefinitionId { get; }
        public int Quantity { get; }
    }

    /// <summary>Units an operative used from a loot line it collected.</summary>
    public sealed class LootUse
    {
        public LootUse(int containerIndex, int line, string definitionId, int quantity)
        {
            ContainerIndex = containerIndex;
            Line = line;
            DefinitionId = definitionId ?? throw new ArgumentNullException(nameof(definitionId));
            Quantity = quantity;
        }

        public int ContainerIndex { get; }
        public int Line { get; }
        public string DefinitionId { get; }
        public int Quantity { get; }
    }

    /// <summary>An owned stack held at the freeze.</summary>
    public sealed class OwnedStackFact
    {
        public OwnedStackFact(string definitionId, int quantity)
        {
            DefinitionId = definitionId ?? throw new ArgumentNullException(nameof(definitionId));
            Quantity = quantity;
        }

        public string DefinitionId { get; }
        public int Quantity { get; }
    }

    /// <summary>Units of a loot line held at the freeze.</summary>
    public sealed class LootLotFact
    {
        public LootLotFact(int containerIndex, int line, int quantity)
        {
            ContainerIndex = containerIndex;
            Line = line;
            Quantity = quantity;
        }

        public int ContainerIndex { get; }
        public int Line { get; }
        public int Quantity { get; }
    }

    /// <summary>Units taken out of a pool line (collected).</summary>
    public sealed class LineCount
    {
        public LineCount(int containerIndex, int line, int quantity)
        {
            ContainerIndex = containerIndex;
            Line = line;
            Quantity = quantity;
        }

        public int ContainerIndex { get; }
        public int Line { get; }
        public int Quantity { get; }
    }

    /// <summary>A slot and what fills it: an owned item id, or a held loot line (exactly one of the two).</summary>
    public sealed class EquipmentFact
    {
        EquipmentFact(EquipSlot slot, long? itemId, int? containerIndex, int? line)
        {
            Slot = slot;
            ItemId = itemId;
            ContainerIndex = containerIndex;
            Line = line;
        }

        public static EquipmentFact Item(EquipSlot slot, long itemId) => new EquipmentFact(slot, itemId, null, null);

        public static EquipmentFact Loot(EquipSlot slot, int containerIndex, int line) => new EquipmentFact(slot, null, containerIndex, line);

        public EquipSlot Slot { get; }
        public long? ItemId { get; }
        public int? ContainerIndex { get; }
        public int? Line { get; }
    }

    public sealed class OperativeFact
    {
        public OperativeFact(long operativeId, int slot, bool alive, IEnumerable<OwnedUse> consumedOwned, IEnumerable<LootUse> consumedLoot,
            IEnumerable<long> heldItems, IEnumerable<OwnedStackFact> heldStacks, IEnumerable<LootLotFact> heldLoot,
            IEnumerable<LineCount> collected, IEnumerable<EquipmentFact> equipment)
        {
            OperativeId = operativeId;
            Slot = slot;
            Alive = alive;
            ConsumedOwned = Copy(consumedOwned);
            ConsumedLoot = Copy(consumedLoot);
            HeldItems = Copy(heldItems);
            HeldStacks = Copy(heldStacks);
            HeldLoot = Copy(heldLoot);
            Collected = Copy(collected);
            Equipment = Copy(equipment);
        }

        public long OperativeId { get; }
        public int Slot { get; }
        public bool Alive { get; }
        public IReadOnlyList<OwnedUse> ConsumedOwned { get; }
        public IReadOnlyList<LootUse> ConsumedLoot { get; }
        public IReadOnlyList<long> HeldItems { get; }
        public IReadOnlyList<OwnedStackFact> HeldStacks { get; }
        public IReadOnlyList<LootLotFact> HeldLoot { get; }
        public IReadOnlyList<LineCount> Collected { get; }
        public IReadOnlyList<EquipmentFact> Equipment { get; }

        internal static IReadOnlyList<T> Copy<T>(IEnumerable<T> items) =>
            new ReadOnlyCollection<T>(new List<T>(items ?? throw new ArgumentNullException(nameof(items))));
    }

    /// <summary>
    /// One participant's result manifest, format v1 (S9-B4, Stage 9 §G; Backend/contracts/manifest-v1 is the authority): the
    /// mission's facts only, with no XP, rewards, energy, insurance, retention choice, seed or salt. Immutable. Its only byte
    /// form is ManifestCodec.ToCanonicalBytes, and its only reader ManifestCodec.Parse.
    /// </summary>
    public sealed class ResultManifestV1
    {
        public const int FormatVersion = 1;
        public const int MaxBytes = 65536;
        public const int MaxElapsedMs = 86400000;
        public const int MaxDepthBelowRoot = 4;
        public const int MaxOperatives = 8;
        public const int MaxObjectives = 64;
        public const int MaxEntries = 256;
        public const int MaxStringBytes = 128;
        public const int MaxContainerOrLine = 65535;
        public const int MaxObjectiveIdLength = 64;

        public ResultManifestV1(string attemptId, long accountId, string processSessionId, string buildRevision, string catalogueVersion,
            ManifestIntegrity integrity, ManifestOutcome outcome, int outcomeSequence, int serverElapsedMs, string layoutHash, int lootTableVersion,
            string lootHash, IEnumerable<ObjectiveFact> objectives, IEnumerable<OperativeFact> operatives)
        {
            AttemptId = attemptId ?? throw new ArgumentNullException(nameof(attemptId));
            AccountId = accountId;
            ProcessSessionId = processSessionId ?? throw new ArgumentNullException(nameof(processSessionId));
            BuildRevision = buildRevision ?? throw new ArgumentNullException(nameof(buildRevision));
            CatalogueVersion = catalogueVersion ?? throw new ArgumentNullException(nameof(catalogueVersion));
            Integrity = integrity;
            Outcome = outcome;
            OutcomeSequence = outcomeSequence;
            ServerElapsedMs = serverElapsedMs;
            LayoutHash = layoutHash ?? throw new ArgumentNullException(nameof(layoutHash));
            LootTableVersion = lootTableVersion;
            LootHash = lootHash ?? throw new ArgumentNullException(nameof(lootHash));
            Objectives = OperativeFact.Copy(objectives);
            Operatives = OperativeFact.Copy(operatives);
        }

        public string AttemptId { get; }
        public long AccountId { get; }
        public string ProcessSessionId { get; }
        public string BuildRevision { get; }
        public string CatalogueVersion { get; }
        public ManifestIntegrity Integrity { get; }
        public ManifestOutcome Outcome { get; }
        public int OutcomeSequence { get; }
        public int ServerElapsedMs { get; }
        public string LayoutHash { get; }
        public int LootTableVersion { get; }
        public string LootHash { get; }
        public IReadOnlyList<ObjectiveFact> Objectives { get; }
        public IReadOnlyList<OperativeFact> Operatives { get; }
    }
}
