using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace Blackglass
{
    /// <summary>
    /// Finds the scene's CoverSurfaces, runs the generator over them and hands the result to the registry. Assumes the
    /// NavMesh already covers the geometry: a procedural mission builds its geometry, then its NavMesh, then calls
    /// Discover(). Runs once at start by default; can be run again at any time (a rebuild retires the old locations).
    /// </summary>
    public sealed class CoverDiscovery : MonoBehaviour
    {
        [SerializeField] CoverRegistry registry;
        [SerializeField] CoverGenerationSettings settings = new CoverGenerationSettings();
        [SerializeField] bool discoverOnStart = true;

        public CoverGenerationSettings Settings => settings;

        internal void Initialize(CoverRegistry coverRegistry, bool discoverAtStart = true, CoverGenerationSettings generationSettings = null)
        {
            registry = coverRegistry;
            discoverOnStart = discoverAtStart;
            if (generationSettings != null)
                settings = generationSettings;
        }

        void Start()
        {
            if (discoverOnStart)
                Discover();
        }

        /// <summary>Scans, generates and rebuilds the registry. Returns the number of locations now listed.</summary>
        public int Discover()
        {
            if (registry == null)
            {
                Debug.LogWarning($"{name}: CoverDiscovery has no registry wired, so nothing was discovered.", this);
                return 0;
            }
            var surfaces = new List<CoverSurface>(FindObjectsByType<CoverSurface>(FindObjectsSortMode.None));
            // A stable order makes names and tests repeatable whatever order Unity returns the objects in.
            surfaces.Sort(CompareSurfaces);
            var boxes = new List<CoverBox>(surfaces.Count);
            foreach (var surface in surfaces)
                boxes.Add(surface.ToBox());

            var tolerance = settings.walkableTolerance;
            var locations = CoverGenerator.Generate(boxes, settings,
                point => NavMesh.SamplePosition(point, out _, tolerance, NavMesh.AllAreas));
            registry.Rebuild(locations);
            return locations.Count;
        }

        static int CompareSurfaces(CoverSurface a, CoverSurface b)
        {
            var byName = string.CompareOrdinal(a.name, b.name);
            if (byName != 0)
                return byName;
            var byX = a.transform.position.x.CompareTo(b.transform.position.x);
            return byX != 0 ? byX : a.transform.position.z.CompareTo(b.transform.position.z);
        }
    }
}
