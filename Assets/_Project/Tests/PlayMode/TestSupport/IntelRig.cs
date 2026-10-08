#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>
    /// A small world for the intelligence tests: the IntelLayouts rooms with real wall colliders (a closed 6 m wall between
    /// them, or two wall pieces leaving the corridor open), a NavMesh, a friendly in room A at world (-6.5, -1.5), an
    /// Encounter and an IntelligenceService. Hostiles are added before Begin, which hands everything to the service.
    /// </summary>
    internal sealed class IntelRig : IDisposable
    {
        public static readonly Vector3 FriendlyGround = new Vector3(-6.5f, 0f, -1.5f);
        /// <summary>In room B, on the corridor's axis, 13 m from the friendly.</summary>
        public static readonly Vector3 InLineGround = new Vector3(6.5f, 0f, -1.5f);
        /// <summary>In room B, off the corridor's axis: the corridor walls hide it from the friendly.</summary>
        public static readonly Vector3 OffAxisGround = new Vector3(6.5f, 0f, 2.0f);

        public readonly TestWorld World = new TestWorld();
        public readonly MissionLayout Layout;
        public readonly Encounter Encounter;
        public readonly IntelligenceService Service;
        public readonly SelectableUnit Friendly;
        public readonly Health FriendlyHealth;
        public readonly List<Health> Hostiles = new List<Health>();

        public IntelRig(bool corridor)
        {
            var walls = corridor
                ? new[]
                {
                    (new Vector3(0f, 1.5f, -4f), new Vector3(6f, 3f, 2f)),     // below the corridor, z -5..-3
                    (new Vector3(0f, 1.5f, 2.5f), new Vector3(6f, 3f, 5f)),    // above the corridor, z 0..5
                }
                : new[] { (new Vector3(0f, 1.5f, 0f), new Vector3(6f, 3f, 10f)) };   // closed: the whole gap
            World.CreateEnvironment(walls);
            Layout = IntelLayouts.TwoRooms(corridor);
            Encounter = World.CreateEncounter();
            Friendly = World.CreateFriendlyFighter(FriendlyGround);
            FriendlyHealth = Friendly.GetComponent<Health>();
            Service = World.Track(new GameObject("Intelligence")).AddComponent<IntelligenceService>();
        }

        /// <summary>A hostile with a collider and no weapon (a cylinder dummy).</summary>
        public Health AddHostile(Vector3 ground)
        {
            var health = World.CreateDummy(ground);
            Hostiles.Add(health);
            return health;
        }

        /// <summary>A hostile that can shoot: a ranged fighter. It stands at `ground` and never moves on its own.</summary>
        public (Health health, UnitAttacker attacker) AddShooter(Vector3 ground, float range = 40f)
        {
            // The friendly would otherwise charge the shooter that hit it (AutoRetaliate) and walk into sight within a second.
            Friendly.GetComponent<AutoRetaliate>().enabled = false;
            var unit = World.CreateFighter(ground, 60, 10, 1f, CombatRole.Ranged, range);
            unit.name = "TestShooter";
            Hostiles.Add(unit.GetComponent<Health>());
            return (unit.GetComponent<Health>(), unit.GetComponent<UnitAttacker>());
        }

        /// <summary>
        /// A 0.9 m wall at world x -5 (z -3..0) and the friendly occupying a cover point behind it facing east: a shooter on
        /// the corridor's axis sees over the wall (its eye line stays above 0.9 m) but its eye-to-feet ray crosses it, so the
        /// friendly is protected from it (hit chance 0.5).
        /// </summary>
        public CoverLocation PutFriendlyInCover()
        {
            var wall = World.CreateObstacle(new Vector3(-5f, 0.45f, -1.5f), new Vector3(0.5f, 0.9f, 3f));
            var point = World.CreateCoverPoint(FriendlyGround, Vector3.right, wall.GetComponent<Collider>());
            var registry = World.CreateRegistry(point);
            var cover = Friendly.GetComponent<UnitCover>();
            cover.Initialize(registry);
            cover.TryReserve(point);
            cover.TryOccupy();
            return point;
        }

        public void Begin(IntelligenceSettings settings, IntelligenceMission extra = null)
        {
            Encounter.Initialize(new[] { FriendlyHealth }, Hostiles);
            var mission = extra ?? new IntelligenceMission();
            mission.Layout = Layout;
            Service.Begin(mission, settings, Encounter);
        }

        public static IntelligenceSettings Fog(Action<IntelligenceSettings> tweak = null)
        {
            var settings = new IntelligenceSettings { fogEnabled = true, map = MapKnowledge.None, objectives = ObjectiveKnowledge.None };
            tweak?.Invoke(settings);
            return settings;
        }

        public void Dispose()
        {
            Service.Clear();
            World.Dispose();
            Time.timeScale = 1f;
        }
    }
}
#endif
