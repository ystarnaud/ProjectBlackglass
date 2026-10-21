using System;
using System.Collections.Generic;

namespace Blackglass.Backend.Core
{
    /// <summary>One successful roll of a loot-table entry: a definition and a quantity, before it becomes a container entry.</summary>
    public sealed class LootRoll
    {
        public LootRoll(string definitionId, int quantity)
        {
            DefinitionId = definitionId;
            Quantity = quantity;
        }

        public string DefinitionId { get; }
        public int Quantity { get; }
    }

    /// <summary>
    /// The online container fill's rules (Stage 12 gate B25, Stage 11 final review I1): one successful roll is exactly one
    /// container entry and one registered pool line, never merged, split, truncated or dropped. A roll that can't be one
    /// entry fails the generation with one of these codes, before any ready report exists.
    /// </summary>
    public static class AttemptLoot
    {
        public const string UnknownItem = "loot_roll_unknown_item";
        public const string QuantityInvalid = "loot_roll_quantity_invalid";
        public const string ExceedsStack = "loot_roll_exceeds_stack";
        public const string NotSingle = "loot_roll_not_single";
        /// <summary>The container refused the entry: impossible in an uncapped container with a fresh id, but never silent.</summary>
        public const string NotAdded = "loot_roll_not_added";

        /// <summary>Null when the roll fits one entry: a stackable 1..maxStack, a non-stackable exactly 1. Else its code.</summary>
        public static string CheckRoll(LootRoll roll, LedgerItemDefinition definition, int maxStack)
        {
            if (roll == null) throw new ArgumentNullException(nameof(roll));
            if (definition == null || !string.Equals(definition.Id, roll.DefinitionId, StringComparison.Ordinal)) return UnknownItem;
            if (roll.Quantity < 1) return QuantityInvalid;
            if (definition.Stackable) return roll.Quantity > maxStack ? ExceedsStack : null;
            return roll.Quantity != 1 ? NotSingle : null;
        }

        /// <summary>
        /// Registers a filled container: its entries in roll order, so line i is roll i. Called once per container, after the
        /// fill and before gameplay; the ledger keeps each entry id's line from then on, whatever happens to other entries.
        /// </summary>
        public static void Register(MissionItemLedger ledger, int containerIndex, IReadOnlyList<ContainerEntry> entriesInRollOrder)
        {
            if (ledger == null) throw new ArgumentNullException(nameof(ledger));
            ledger.RegisterContainer(containerIndex, entriesInRollOrder);
        }
    }
}
