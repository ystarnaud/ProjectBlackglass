using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Blackglass.Backend.Core
{
    /// <summary>
    /// One participant's item facts at its outcome freeze, the only input of ManifestBuilder.Build. Only
    /// MissionItemLedger.Freeze creates one, so an unfrozen participant can't be built. Lists hold positive quantities only and
    /// no duplicates, even after an integrity failure, so the manifest still parses and the backend quarantines it (V14).
    /// </summary>
    public sealed class ParticipantFacts
    {
        internal ParticipantFacts(string accountId, ManifestIntegrity integrity, IList<string> failureCodes, IList<OperativeFacts> operatives)
        {
            AccountId = accountId;
            Integrity = integrity;
            FailureCodes = new ReadOnlyCollection<string>(new List<string>(failureCodes));
            Operatives = new ReadOnlyCollection<OperativeFacts>(new List<OperativeFacts>(operatives));
        }

        public string AccountId { get; }
        public ManifestIntegrity Integrity { get; }
        /// <summary>Bounded words ([a-z_]{1,40}, at most 32), for diagnostics; empty when Integrity is Ok.</summary>
        public IReadOnlyList<string> FailureCodes { get; }
        /// <summary>In slot order.</summary>
        public IReadOnlyList<OperativeFacts> Operatives { get; }
    }

    public sealed class OperativeFacts
    {
        internal OperativeFacts(string operativeId, int slot, IList<OwnedUse> consumedOwned, IList<LootUse> consumedLoot, IList<string> heldItems,
            IList<OwnedStackFact> heldStacks, IList<LootLotFact> heldLoot, IList<LineCount> collected, IList<LedgerEquipment> equipment)
        {
            OperativeId = operativeId;
            Slot = slot;
            ConsumedOwned = new ReadOnlyCollection<OwnedUse>(consumedOwned);
            ConsumedLoot = new ReadOnlyCollection<LootUse>(consumedLoot);
            HeldItems = new ReadOnlyCollection<string>(heldItems);
            HeldStacks = new ReadOnlyCollection<OwnedStackFact>(heldStacks);
            HeldLoot = new ReadOnlyCollection<LootLotFact>(heldLoot);
            Collected = new ReadOnlyCollection<LineCount>(collected);
            Equipment = new ReadOnlyCollection<LedgerEquipment>(equipment);
        }

        public string OperativeId { get; }
        public int Slot { get; }
        public IReadOnlyList<OwnedUse> ConsumedOwned { get; }
        public IReadOnlyList<LootUse> ConsumedLoot { get; }
        public IReadOnlyList<string> HeldItems { get; }
        public IReadOnlyList<OwnedStackFact> HeldStacks { get; }
        public IReadOnlyList<LootLotFact> HeldLoot { get; }
        public IReadOnlyList<LineCount> Collected { get; }
        public IReadOnlyList<LedgerEquipment> Equipment { get; }
    }

    /// <summary>An equipped slot: an owned item (Base58 id) or a held loot line.</summary>
    public sealed class LedgerEquipment
    {
        internal LedgerEquipment(EquipSlot slot, string itemId, int? containerIndex, int? line)
        {
            Slot = slot;
            ItemId = itemId;
            ContainerIndex = containerIndex;
            Line = line;
        }

        public EquipSlot Slot { get; }
        public string ItemId { get; }
        public int? ContainerIndex { get; }
        public int? Line { get; }
    }
}
