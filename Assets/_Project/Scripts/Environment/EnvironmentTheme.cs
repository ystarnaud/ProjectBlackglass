using System;
using System.Linq;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// One visual theme: which prefab variants represent each semantic element. A prefab here is a pure visual (the builder
    /// removes any collider on it). `salt` makes two themes choose differently for the same seed. A second theme is a new
    /// asset; the generator never changes.
    /// </summary>
    [CreateAssetMenu(menuName = "Blackglass/Environment Theme", fileName = "EnvironmentTheme")]
    public sealed class EnvironmentTheme : ScriptableObject
    {
        [Serializable]
        public struct Entry
        {
            public EnvironmentElement element;
            public GameObject[] variants;
        }

        [SerializeField] int salt;
        [SerializeField] Entry[] entries = new Entry[0];

        public int Salt => salt;

        /// <summary>Builds a theme in memory (editor tooling and tests).</summary>
        public static EnvironmentTheme Create(int salt, Entry[] entries)
        {
            var theme = CreateInstance<EnvironmentTheme>();
            theme.salt = salt;
            theme.entries = entries;
            return theme;
        }

        public bool Has(EnvironmentElement element) => Variants(element).Length > 0;

        /// <summary>The variant for this element at this tile of this seed, or null when the theme has none.</summary>
        public GameObject Resolve(EnvironmentElement element, Vector2Int tile, int seed)
        {
            var variants = Variants(element);
            var index = VisualVariants.Pick(seed, salt, element, tile, variants.Length);
            return index < 0 ? null : variants[index];
        }

        GameObject[] Variants(EnvironmentElement element)
        {
            foreach (var entry in entries)
            {
                if (entry.element != element || entry.variants == null)
                    continue;
                return entry.variants.Where(v => v != null).ToArray();
            }
            return Array.Empty<GameObject>();
        }
    }
}
