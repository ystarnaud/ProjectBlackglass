using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The scene's current cover locations. Holds state only, like Encounter: clicks, hostile searches, automatic
    /// occupancy and the cover view read it. CoverDiscovery fills it (Rebuild); nothing registers itself.
    /// </summary>
    public sealed class CoverRegistry : MonoBehaviour
    {
        readonly List<CoverLocation> locations = new List<CoverLocation>();

        public IReadOnlyList<CoverLocation> Points => locations;

        /// <summary>Bumped by every Rebuild, so views can tell when to redraw.</summary>
        public int Version { get; private set; }

        /// <summary>Raised after every Rebuild.</summary>
        public event Action Changed;

        /// <summary>
        /// Replaces the current locations. Every old location that is not in `replacement` is retired: its claim is
        /// dropped and it reads as invalid, so units holding it let go on their next update and orders towards it end.
        /// </summary>
        public void Rebuild(IEnumerable<CoverLocation> replacement)
        {
            if (replacement == null)
                throw new ArgumentNullException(nameof(replacement));
            var next = new List<CoverLocation>(replacement);
            var keep = new HashSet<CoverLocation>(next);
            foreach (var old in locations)
            {
                if (!keep.Contains(old))
                    old.Retire();
            }
            locations.Clear();
            locations.AddRange(next);
            Version++;
            Changed?.Invoke();
        }

        internal void Initialize(params CoverLocation[] scenePoints) => Rebuild(scenePoints);
    }
}
