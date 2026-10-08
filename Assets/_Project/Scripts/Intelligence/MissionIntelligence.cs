using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// What the player knows about one mission. Plain C#, no randomness, no scene access: sources (friendly sight, cameras,
    /// scans, exposure, briefing) call the reveal API and everything else reads the queries. Permanent knowledge (regions,
    /// devices, objectives) only ever goes up. Live knowledge (an enemy being observed, a region being in sight) is rebuilt
    /// by each pass: BeginPass, then sources mark what they see, then EndPass demotes whatever nobody marked (an enemy to
    /// last known at its last seen position, a region to Discovered). A mission whose fog is off is "open": every query
    /// answers "known" and passes do nothing.
    /// </summary>
    public sealed class MissionIntelligence
    {
        sealed class EnemyIntel
        {
            public KnowledgeState State;
            public Vector3 LastPosition;
            public bool HasPosition;
            public bool SeenThisPass;
            public Vector3 PassPosition;
        }

        readonly RegionMap map;
        readonly KnowledgeState[] regions;
        readonly bool[] observedNow;
        readonly bool[] observedNext;
        readonly KnowledgeState[] devices;
        readonly Dictionary<Health, EnemyIntel> enemies = new Dictionary<Health, EnemyIntel>();
        readonly List<Health> order = new List<Health>();
        readonly List<int> circleBuffer = new List<int>();
        IReadOnlyList<MissionObjective> goals = Array.Empty<MissionObjective>();
        bool anyEnemyEverObserved;

        public MissionIntelligence(RegionMap map, int deviceCount, bool open = false)
        {
            this.map = map ?? throw new ArgumentNullException(nameof(map));
            IsOpen = open;
            regions = new KnowledgeState[map.Count];
            observedNow = new bool[map.Count];
            observedNext = new bool[map.Count];
            devices = new KnowledgeState[Math.Max(0, deviceCount)];
        }

        public bool IsOpen { get; }
        public RegionMap Map => map;
        public int DeviceCount => devices.Length;
        /// <summary>Bumped by every real change.</summary>
        public int Version { get; private set; }
        public bool AnyEnemyEverObserved => anyEnemyEverObserved;

        /// <summary>Raised after any real change to what is known.</summary>
        public event Action Changed;

        // ---- regions ----

        public KnowledgeState StateOfRegion(int region)
        {
            if (IsOpen)
                return KnowledgeState.Observed;
            if (region < 0 || region >= regions.Length)
                return KnowledgeState.Unknown;
            return observedNow[region] ? KnowledgeState.Observed : regions[region];
        }

        /// <summary>The region the point is in; Unknown for a point outside every region (a wall, the void).</summary>
        public KnowledgeState StateOfPoint(Vector3 world) =>
            IsOpen ? KnowledgeState.Observed : StateOfRegion(map.RegionAt(world));

        /// <summary>Discovers the region for good. True when it was unknown.</summary>
        public bool RevealRegion(int region)
        {
            if (IsOpen || region < 0 || region >= regions.Length || regions[region] != KnowledgeState.Unknown)
                return false;
            regions[region] = KnowledgeState.Discovered;
            UpdateObjectives();
            Touch();
            return true;
        }

        /// <summary>Discovers every region the flat circle touches (a scan ignores walls). Returns how many were unknown.</summary>
        public int RevealArea(Vector3 centre, float radius)
        {
            if (IsOpen)
                return 0;
            map.RegionsInCircle(centre, radius, circleBuffer);
            var newly = 0;
            foreach (var region in circleBuffer)
                if (RevealRegion(region))
                    newly++;
            return newly;
        }

        // ---- passes ----

        public void BeginPass()
        {
            if (IsOpen)
                return;
            Array.Clear(observedNext, 0, observedNext.Length);
            foreach (var enemy in enemies.Values)
                enemy.SeenThisPass = false;
        }

        /// <summary>The region is in sight this pass; it is also discovered for good.</summary>
        public void MarkRegionObserved(int region)
        {
            if (IsOpen || region < 0 || region >= regions.Length)
                return;
            observedNext[region] = true;
            if (regions[region] == KnowledgeState.Unknown)
            {
                regions[region] = KnowledgeState.Discovered;
                Touch();
            }
        }

        public void ObserveEnemy(Health enemy, Vector3 position)
        {
            if (IsOpen || enemy == null)
                return;
            var intel = Track(enemy);
            intel.SeenThisPass = true;
            intel.PassPosition = position;
        }

        public void EndPass()
        {
            if (IsOpen)
                return;
            var changed = false;
            for (var r = 0; r < observedNow.Length; r++)
            {
                if (observedNow[r] == observedNext[r])
                    continue;
                observedNow[r] = observedNext[r];
                changed = true;
            }
            foreach (var health in order)
            {
                var enemy = enemies[health];
                // A destroyed or dead enemy takes no part: a dead one reads Unknown and never moves a marker.
                if (health == null || !health.IsAlive)
                    continue;
                if (enemy.SeenThisPass)
                {
                    if (enemy.State != KnowledgeState.Observed || enemy.LastPosition != enemy.PassPosition)
                        changed = true;
                    enemy.State = KnowledgeState.Observed;
                    enemy.LastPosition = enemy.PassPosition;
                    enemy.HasPosition = true;
                    anyEnemyEverObserved = true;
                }
                else if (enemy.State == KnowledgeState.Observed)
                {
                    enemy.State = KnowledgeState.Discovered;   // last known: the position is left where it was
                    changed = true;
                }
            }
            if (UpdateObjectives())
                changed = true;
            if (changed)
                Touch();
        }

        // ---- enemies ----

        /// <summary>Starts keeping intel on a hostile. Every hostile of a mission is tracked at its start.</summary>
        public void TrackEnemy(Health enemy)
        {
            if (enemy != null)
                Track(enemy);
        }

        public KnowledgeState StateOfEnemy(Health enemy)
        {
            if (IsOpen)
                return KnowledgeState.Observed;
            if (enemy == null || !enemy.IsAlive || !enemies.TryGetValue(enemy, out var intel))
                return KnowledgeState.Unknown;
            return intel.State;
        }

        /// <summary>
        /// Whether the unit is in live sight, so it may be targeted and shown. True for every unit in an open model and for
        /// units the model does not track (friendlies); a tracked hostile must be Observed.
        /// </summary>
        public bool IsObserved(Health unit)
        {
            if (IsOpen)
                return true;
            if (unit == null)
                return false;
            if (!enemies.TryGetValue(unit, out var intel))
                return true;
            return unit.IsAlive && intel.State == KnowledgeState.Observed;
        }

        public bool TryGetLastKnown(Health enemy, out Vector3 position)
        {
            position = default;
            if (enemy == null || !enemy.IsAlive || !enemies.TryGetValue(enemy, out var intel) || !intel.HasPosition)
                return false;
            position = intel.LastPosition;
            return true;
        }

        /// <summary>A briefing marker: the enemy is known to have been here, without having been seen.</summary>
        public void MarkLastKnown(Health enemy, Vector3 position)
        {
            if (IsOpen || enemy == null)
                return;
            var intel = Track(enemy);
            if (intel.State == KnowledgeState.Observed)
                return;
            intel.State = KnowledgeState.Discovered;
            intel.LastPosition = position;
            intel.HasPosition = true;
            Touch();
        }

        // ---- devices ----

        public KnowledgeState StateOfDevice(int id)
        {
            if (IsOpen)
                return KnowledgeState.Discovered;
            return id < 0 || id >= devices.Length ? KnowledgeState.Unknown : devices[id];
        }

        public bool RevealDevice(int id)
        {
            if (IsOpen || id < 0 || id >= devices.Length || devices[id] != KnowledgeState.Unknown)
                return false;
            devices[id] = KnowledgeState.Discovered;
            Touch();
            return true;
        }

        // ---- objectives ----

        /// <summary>
        /// Sets each objective's starting knowledge from the settings and keeps it up to date: an objective with a place is
        /// learned when its region is observed (seen, so a mapped layout keeps it unknown), the elimination objective when the first hostile is observed. Hides the
        /// elimination objective's living/total count. An open model changes nothing.
        /// </summary>
        public void BindObjectives(IReadOnlyList<MissionObjective> objectives, ObjectiveKnowledge knowledge)
        {
            goals = objectives ?? Array.Empty<MissionObjective>();
            if (IsOpen)
                return;
            foreach (var goal in goals)
            {
                goal.SetKnown(KnownAtStart(goal.Type, knowledge));
                if (goal is EliminateHostilesObjective eliminate)
                    eliminate.ShowCounts = false;
            }
            UpdateObjectives();
        }

        public void RevealObjective(MissionObjective objective)
        {
            if (objective == null || objective.IsKnown)
                return;
            objective.SetKnown(true);
            Touch();
        }

        static bool KnownAtStart(ObjectiveType type, ObjectiveKnowledge knowledge)
        {
            switch (knowledge)
            {
                case ObjectiveKnowledge.All:
                    return true;
                case ObjectiveKnowledge.ExtractionAndTerminal:
                    return type == ObjectiveType.ReachZone || type == ObjectiveType.Interact;
                case ObjectiveKnowledge.ExtractionOnly:
                    return type == ObjectiveType.ReachZone;
                default:
                    return false;
            }
        }

        bool UpdateObjectives()
        {
            var changed = false;
            foreach (var goal in goals)
            {
                if (goal.IsKnown)
                    continue;
                var learned = goal.HasTarget ? StateOfPoint(goal.TargetPosition) == KnowledgeState.Observed : anyEnemyEverObserved;
                if (!learned)
                    continue;
                goal.SetKnown(true);
                changed = true;
            }
            return changed;
        }

        EnemyIntel Track(Health enemy)
        {
            if (enemies.TryGetValue(enemy, out var intel))
                return intel;
            intel = new EnemyIntel();
            enemies[enemy] = intel;
            order.Add(enemy);
            return intel;
        }

        void Touch()
        {
            Version++;
            Changed?.Invoke();
        }
    }
}
