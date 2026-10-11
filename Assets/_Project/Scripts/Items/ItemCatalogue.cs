using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Every item definition the game knows, looked up by stable id. Saved state stores only definition ids, so this is the
    /// one place an id becomes data. Holds no state.
    /// </summary>
    [CreateAssetMenu(menuName = "Blackglass/Item Catalogue", fileName = "ItemCatalogue")]
    public sealed class ItemCatalogue : ScriptableObject
    {
        [SerializeField] ItemDefinition[] items = new ItemDefinition[0];

        public IReadOnlyList<ItemDefinition> Items => items;

        /// <summary>The definition with this id, or null (blank id, unknown id).</summary>
        public ItemDefinition Find(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || items == null)
                return null;
            foreach (var item in items)
            {
                if (item != null && item.Id == id)
                    return item;
            }
            return null;
        }

        /// <summary>
        /// Checks every entry: an empty slot, an invalid definition or a repeated id each add an actionable message that
        /// names the asset. True when there are no errors.
        /// </summary>
        public bool Validate(List<string> errors)
        {
            var start = errors.Count;
            var seen = new Dictionary<string, ItemDefinition>();
            for (var i = 0; i < items.Length; i++)
            {
                var item = items[i];
                if (item == null)
                {
                    errors.Add($"{name}: entry {i} is empty.");
                    continue;
                }
                if (!item.IsValid(out var problem))
                {
                    errors.Add($"{name}: item '{item.DisplayName}' (id '{item.Id}', asset {item.name}) is invalid: {problem}.");
                    continue;
                }
                if (seen.TryGetValue(item.Id, out var other))
                    errors.Add($"{name}: duplicate item id '{item.Id}' on '{other.name}' and '{item.name}'.");
                else
                    seen.Add(item.Id, item);
            }
            return errors.Count == start;
        }

        internal static ItemCatalogue Create(params ItemDefinition[] definitions)
        {
            var catalogue = CreateInstance<ItemCatalogue>();
            catalogue.items = definitions ?? new ItemDefinition[0];
            return catalogue;
        }
    }
}
