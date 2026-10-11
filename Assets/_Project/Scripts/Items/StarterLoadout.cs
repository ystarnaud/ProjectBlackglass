using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>One starter grant: an item, how many, and whether to equip it (the first matching entry of the bag).</summary>
    [Serializable]
    public struct StarterGrant
    {
        public ItemDefinition item;
        [Min(1)] public int quantity;
        public bool equip;
    }

    /// <summary>One operative's starter grants, matched by the operative definition's stable id.</summary>
    [Serializable]
    public sealed class StarterOperative
    {
        public OperativeDefinition operative;
        public StarterGrant[] items = new StarterGrant[0];
    }

    /// <summary>
    /// The items a fresh prototype session starts with: per-operative grants and the shared stash. Authored data; seeding
    /// is done once by InventorySession.SeedStarter and never repeated for an operative that already received it.
    /// </summary>
    [CreateAssetMenu(menuName = "Blackglass/Starter Loadout", fileName = "StarterLoadout")]
    public sealed class StarterLoadout : ScriptableObject
    {
        [SerializeField] StarterOperative[] operatives = new StarterOperative[0];
        [SerializeField] StarterGrant[] stash = new StarterGrant[0];

        // Test-only source: grants keyed by operative id, used when the asset has no operative definitions.
        Dictionary<string, StarterGrant[]> byId;

        public IReadOnlyList<StarterGrant> Stash => stash;

        public IReadOnlyList<StarterGrant> For(string operativeId)
        {
            if (byId != null && byId.TryGetValue(operativeId, out var direct))
                return direct;
            if (operatives != null)
            {
                foreach (var entry in operatives)
                {
                    if (entry != null && entry.operative != null && entry.operative.Id == operativeId)
                        return entry.items ?? new StarterGrant[0];
                }
            }
            return Array.Empty<StarterGrant>();
        }

        internal static StarterLoadout Create(Dictionary<string, StarterGrant[]> grantsById, StarterGrant[] stash)
        {
            var asset = CreateInstance<StarterLoadout>();
            asset.byId = grantsById;
            asset.stash = stash ?? new StarterGrant[0];
            return asset;
        }

        internal static StarterLoadout Create(StarterOperative[] operatives, StarterGrant[] stash)
        {
            var asset = CreateInstance<StarterLoadout>();
            asset.operatives = operatives ?? new StarterOperative[0];
            asset.stash = stash ?? new StarterGrant[0];
            return asset;
        }
    }
}
