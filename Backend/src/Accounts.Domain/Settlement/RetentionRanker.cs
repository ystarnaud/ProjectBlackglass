using System.Buffers.Binary;
using System.Security.Cryptography;
using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.Domain.Insurance;

namespace Blackglass.Accounts.Domain.Settlement;

/// <summary>A loot line the participant's operatives held at the freeze (units summed over its operatives), with its catalogue category.</summary>
public sealed record RetentionCandidate(int ContainerIndex, int Line, string DefinitionId, ItemCategory Category, int Quantity);

/// <summary>
/// The insured loot retention (S9-B12.3, S9-B12.4). Eligible candidates are those whose category the terms list (the policy's
/// words Weapon, Armor, Utility and Consumable are the catalogue categories of the same name: a fixed table, nothing else is
/// eligible). They are ranked by the terms' category priority, then by SHA-256(salt ‖ account id as 8 bytes big-endian ‖
/// containerIndex as 4 bytes ‖ line as 4 bytes) compared bytewise, then by (containerIndex, line); the first maxEntries are
/// kept, each with at most maxUnitsPerEntry units. The selection is stored with the settlement and never recomputed.
/// </summary>
public static class RetentionRanker
{
    public static IReadOnlyList<RetainedLot> Select(byte[] salt, long accountId, IReadOnlyList<RetentionCandidate> candidates, LootRetention terms)
    {
        ArgumentNullException.ThrowIfNull(salt);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(terms);
        return candidates
            .Where(c => terms.EligibleCategories.Contains(Word(c.Category)))
            .Select(c => (Candidate: c, Priority: IndexOf(terms.CategoryPriority, Word(c.Category)), Hash: SaltedHash(salt, accountId, c.ContainerIndex, c.Line)))
            .OrderBy(x => x.Priority).ThenBy(x => x.Hash, ByteOrder.Instance).ThenBy(x => x.Candidate.ContainerIndex).ThenBy(x => x.Candidate.Line)
            .Take(terms.MaxEntries)
            .Select(x => new RetainedLot(x.Candidate.ContainerIndex, x.Candidate.Line, x.Candidate.DefinitionId, Math.Min(x.Candidate.Quantity, terms.MaxUnitsPerEntry)))
            .ToList();
    }

    /// <summary>The fixed category table: the policy word for each catalogue category.</summary>
    public static string Word(ItemCategory category) => category switch
    {
        ItemCategory.Weapon => "Weapon",
        ItemCategory.Armor => "Armor",
        ItemCategory.Utility => "Utility",
        ItemCategory.Consumable => "Consumable",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "No policy word for this category."),
    };

    static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
            if (list[i] == value) return i;
        return int.MaxValue;
    }

    public static byte[] SaltedHash(byte[] salt, long accountId, int containerIndex, int line)
    {
        var input = new byte[salt.Length + 16];
        salt.CopyTo(input, 0);
        BinaryPrimitives.WriteInt64BigEndian(input.AsSpan(salt.Length), accountId);
        BinaryPrimitives.WriteInt32BigEndian(input.AsSpan(salt.Length + 8), containerIndex);
        BinaryPrimitives.WriteInt32BigEndian(input.AsSpan(salt.Length + 12), line);
        return SHA256.HashData(input);
    }

    sealed class ByteOrder : IComparer<byte[]>
    {
        public static readonly ByteOrder Instance = new();

        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
    }
}
