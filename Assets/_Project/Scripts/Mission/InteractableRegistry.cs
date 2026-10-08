using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The mission's current interactables (the terminal). Holds state only, like CoverRegistry: MissionDirector refills it
    /// per mission and empties it on teardown; input and the controller cursor read it. Nothing registers itself, so a
    /// destroyed mission leaves nothing behind here.
    /// </summary>
    public sealed class InteractableRegistry : MonoBehaviour
    {
        readonly List<MissionInteractable> items = new List<MissionInteractable>();

        public IReadOnlyList<MissionInteractable> Items => items;

        /// <summary>Bumped by every Rebuild.</summary>
        public int Version { get; private set; }

        public void Rebuild(IEnumerable<MissionInteractable> replacement)
        {
            if (replacement == null)
                throw new ArgumentNullException(nameof(replacement));
            items.Clear();
            items.AddRange(replacement);
            Version++;
        }

        /// <summary>The closest available interactable within `radius` (flat distance) of `point`, or null.</summary>
        public MissionInteractable NearestAvailable(Vector3 point, float radius) => NearestAvailable(point, radius, null);

        /// <summary>As above, skipping any item the filter rejects (the player does not know about it yet).</summary>
        public MissionInteractable NearestAvailable(Vector3 point, float radius, Func<MissionInteractable, bool> accept)
        {
            MissionInteractable best = null;
            var bestDistance = radius;
            foreach (var item in items)
            {
                if (item == null || !item.IsAvailable || (accept != null && !accept(item)))
                    continue;
                var distance = CoverRules.FlatDistance(point, item.Position);
                if (distance > bestDistance)
                    continue;
                best = item;
                bestDistance = distance;
            }
            return best;
        }

        internal void Initialize(params MissionInteractable[] scenePoints) => Rebuild(scenePoints);
    }
}
