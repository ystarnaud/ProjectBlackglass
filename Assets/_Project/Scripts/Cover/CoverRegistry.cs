using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The scene's cover points, as a serialized list. Holds state only, like Encounter: clicks, hostile searches,
    /// automatic occupancy and the cover view read it. Points do not register themselves.
    /// </summary>
    public sealed class CoverRegistry : MonoBehaviour
    {
        [SerializeField] List<CoverPoint> points = new List<CoverPoint>();

        public IReadOnlyList<CoverPoint> Points => points;

        internal void Initialize(params CoverPoint[] scenePoints)
        {
            points.Clear();
            points.AddRange(scenePoints);
        }
    }
}
