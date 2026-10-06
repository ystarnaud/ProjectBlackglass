using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    public enum MissionState
    {
        Idle,
        Generating,
        Ready,
        Failed,
    }

    /// <summary>Everything the debug view shows about the last generation. Filled by the director; nothing reads it back.</summary>
    public sealed class MissionReport
    {
        public int Seed;
        public int Attempt;
        public int AttemptsMade;
        public int MaxAttempts;
        public bool Succeeded;
        public string Failure;
        public List<string> Failures = new List<string>();
        public int Rooms;
        public int Connections;
        public int FloorTiles;
        public float NavArea;
        public int PathsChecked;
        public int CoverTotal;
        public int CoverLow;
        public int CoverTall;
        public int CoverCorner;
        public int CoverColumn;
        public int FriendlySpawns;
        public int HostileSpawns;
        public float TeamSeparation;
        public Bounds Bounds;
        public ulong LayoutHash;
        public int Guards;
        public ulong ObjectiveHash;
    }

    /// <summary>
    /// Runs the mission pipeline: teardown, layout, objective placement (before the build; the terminal is part of the
    /// NavMesh bake), geometry, NavMesh, validation, cover, spawn, camera. Persistent
    /// systems are only reset and refilled, never created or destroyed. One generation at a time; every generated object
    /// lives under GeneratedMissionRoot, which is all teardown has to destroy (plus the death markers units leave at
    /// the scene root, and the NavMeshData each bake creates, which removing the NavMesh does not destroy).
    /// </summary>
    public sealed class MissionDirector : MonoBehaviour
    {
        [SerializeField] MissionSettings settings = new MissionSettings();
        [SerializeField] FriendlySlot[] friendlySlots = new FriendlySlot[0];
        [SerializeField] HostileSlot[] hostileSlots = new HostileSlot[0];
        [SerializeField] MissionSystems systems = new MissionSystems();
        [SerializeField] Material groundMaterial;
        [SerializeField] Material obstacleMaterial;
        [SerializeField] bool generateOnStart = true;
        // At Play, draw a fresh seed (the Inspector then shows it, F6 repeats it) instead of reusing the serialized one.
        [SerializeField] bool newSeedAtStart = true;
        [SerializeField, Min(0f)] float cameraMargin = 2f;

        readonly List<CommandableUnit> friendlies = new List<CommandableUnit>();
        readonly List<CommandableUnit> hostiles = new List<CommandableUnit>();
        Coroutine running;

        public MissionState State { get; private set; }
        public MissionReport Report { get; private set; } = new MissionReport();
        public GeneratedMission Current { get; private set; }
        public MissionSettings Settings => settings;
        internal bool NewSeedAtStart => newSeedAtStart;
        /// <summary>Where a new seed comes from; the clock by default, replaceable by tests.</summary>
        internal Func<int> seedSource = ClockSeed;
        public IReadOnlyList<CommandableUnit> Friendlies => friendlies;
        public IReadOnlyList<CommandableUnit> Hostiles => hostiles;

        public event Action<MissionState> StateChanged;

        internal void Initialize(MissionSettings missionSettings, FriendlySlot[] friendly, HostileSlot[] hostile, MissionSystems persistent,
            Material ground, Material obstacle, bool generateAtStart, bool randomSeedAtStart = false)
        {
            settings = missionSettings;
            friendlySlots = friendly;
            hostileSlots = hostile;
            systems = persistent;
            groundMaterial = ground;
            obstacleMaterial = obstacle;
            generateOnStart = generateAtStart;
            newSeedAtStart = randomSeedAtStart;
        }

        void Start()
        {
            if (!generateOnStart)
                return;
            if (newSeedAtStart)
                GenerateNew();
            else
                Generate(settings.seed);
        }

        /// <summary>Starts generating this seed. Refused (false, with a warning) while a generation is running.</summary>
        public bool Generate(int seed)
        {
            if (State == MissionState.Generating)
            {
                Debug.LogWarning($"{name}: a mission is already being generated; the request for seed {seed} was ignored.", this);
                return false;
            }
            settings.seed = seed;
            running = StartCoroutine(Run());
            return true;
        }

        public bool RegenerateSame() => Generate(settings.seed);

        /// <summary>A fresh seed from `seedSource` (the clock; never UnityEngine.Random, so generation cannot disturb combat randomness).</summary>
        public bool GenerateNew() => Generate(seedSource());

        static int ClockSeed() => unchecked(Environment.TickCount * 397) & 0x7FFFFFFF;

        /// <summary>Destroys the current mission and resets the persistent systems. Takes effect at the end of the frame.</summary>
        public void Clear()
        {
            CancelRun();
            Teardown();
            SetState(MissionState.Idle);
        }

        // The only yield is after Teardown and before any build or spawn, so stopping a run never leaves a half-built mission.
        void CancelRun()
        {
            if (running == null)
                return;
            StopCoroutine(running);
            running = null;
            SetState(MissionState.Idle);
        }

        void OnDisable() => CancelRun();

        IEnumerator Run()
        {
            SetState(MissionState.Generating);
            var request = settings.Validated();
            if (friendlySlots.Length > 0)
                request.friendlyCount = Mathf.Clamp(friendlySlots.Length, 1, 6);
            Report = new MissionReport { Seed = request.seed, MaxAttempts = request.maxAttempts };
            Teardown();
            yield return null;   // let Destroy finish so the old NavMesh and colliders are really gone

            for (var attempt = 1; attempt <= request.maxAttempts; attempt++)
            {
                Report.AttemptsMade = attempt;
                if (TryAttemptSafely(request, attempt))
                {
                    running = null;
                    SetState(MissionState.Ready);
                    yield break;
                }
            }

            var failureText = $"Mission generation failed. {request.Describe()}\n  " + string.Join("\n  ", Report.Failures);
            Report.Failure = failureText;
            Debug.LogError(failureText, this);
            running = null;
            SetState(MissionState.Failed);
        }

        // An exception inside an attempt (a prefab missing a component, a generator bug) is one failed attempt: without
        // this the coroutine would die with `running` set and every later request would be refused.
        bool TryAttemptSafely(MissionSettings request, int attempt)
        {
            GeneratedMission mission = null;
            try
            {
                return TryAttempt(request, attempt, ref mission);
            }
            catch (Exception exception)
            {
                Report.Failures.Add($"attempt {attempt}: exception: {exception.GetType().Name}: {exception.Message}");
                friendlies.Clear();
                hostiles.Clear();
                Current = null;
                ResetSystems();
                if (mission != null)
                    DestroyMission(mission, true);
                return false;
            }
        }

        // One attempt: layout, objective placement, geometry (with the terminal), NavMesh, validation, cover, spawn, camera.
        // A failed attempt leaves nothing behind.
        bool TryAttempt(MissionSettings request, int attempt, ref GeneratedMission mission)
        {
            if (!MissionGenerator.TryAttempt(request, attempt, out var layout, out var reason))
            {
                Report.Failures.Add($"attempt {attempt}: {reason}");
                return false;
            }
            if (!ObjectivePlacer.TryPlace(layout, request, out var plan, out reason))
            {
                Report.Failures.Add($"attempt {attempt}: objectives: {reason}");
                return false;
            }
            MissionInteractable terminal = null;
            Action<Transform> addTerminal = request.hackTerminal
                ? geometry => terminal = MissionContent.AddTerminal(geometry, plan, layout, request, obstacleMaterial)
                : (Action<Transform>)null;
            mission = MissionBuilder.Build(layout, groundMaterial, obstacleMaterial, addTerminal);
            if (!MissionNavigation.Validate(layout, out reason, out var navigation))
            {
                Report.Failures.Add($"attempt {attempt}: navigation: {reason}");
                DestroyMission(mission, true);   // immediate: the next attempt must not see this NavMesh
                return false;
            }
            if (!MissionNavigation.ValidateObjectives(layout, plan, terminal != null, out reason))
            {
                Report.Failures.Add($"attempt {attempt}: objectives: {reason}");
                DestroyMission(mission, true);
                return false;
            }

            var origin = layout.TileCenter(layout.FriendlySpawns[0]);
            systems.coverDiscovery.Discover(mission.Geometry, MissionNavigation.ReachableFrom(origin));

            if (!MissionSpawner.TrySpawn(mission, friendlySlots, hostileSlots, systems, plan.GuardTiles, out var spawned, out reason))
            {
                Report.Failures.Add($"attempt {attempt}: spawn: {reason}");
                ResetSystems();
                DestroyMission(mission, true);
                return false;
            }

            mission.Plan = plan;
            mission.Terminal = terminal;
            mission.ExtractionZone = MissionContent.CreateZone(layout, plan, mission.Root.transform);
            Current = mission;
            friendlies.AddRange(spawned.Friendlies);
            hostiles.AddRange(spawned.Hostiles);
            if (systems.interactables != null)
                systems.interactables.Rebuild(terminal != null ? new[] { terminal } : Array.Empty<MissionInteractable>());
            Fill(Report, layout, navigation, plan);
            FrameCamera(layout);
            return true;
        }

        void Teardown()
        {
            ResetSystems();
            foreach (var unit in friendlies)
                DestroyMarker(unit);
            foreach (var unit in hostiles)
                DestroyMarker(unit);
            friendlies.Clear();
            hostiles.Clear();
            if (Current != null)
                DestroyMission(Current, false);
            Current = null;
        }

        // Empties every persistent system the mission fills, so nothing keeps a reference to a unit about to be destroyed.
        void ResetSystems()
        {
            if (systems.coverRegistry != null)
                systems.coverRegistry.Rebuild(Array.Empty<CoverLocation>());
            if (systems.encounter != null)
                systems.encounter.Initialize(Array.Empty<Health>(), Array.Empty<Health>());
            if (systems.selection != null)
            {
                systems.selection.Clear();
                systems.selection.Initialize();
            }
            if (systems.activeCharacter != null)
                systems.activeCharacter.SetUnit(null);
            if (systems.interactables != null)
                systems.interactables.Rebuild(Array.Empty<MissionInteractable>());
            if (systems.abilityTargeting != null)
                systems.abilityTargeting.Disarm();
            if (systems.pause != null)
                systems.pause.Resume();
        }

        // The surface's NavMeshData is a separate object that outlives the surface component: removing the NavMesh
        // (the surface's OnDisable) does not destroy it, so it would leak once per generation.
        static void DestroyMission(GeneratedMission mission, bool immediate)
        {
            var data = mission.Surface != null ? mission.Surface.navMeshData : null;
            if (mission.Root != null)
            {
                if (immediate)
                    DestroyImmediate(mission.Root);
                else
                {
                    // Destroy only takes effect at the end of the frame: until then nothing in the mission may run, react
                    // or leave a death marker, and the surface must already have removed its NavMesh.
                    mission.Root.SetActive(false);
                    Destroy(mission.Root);
                }
            }
            if (data == null)
                return;
            if (immediate)
                DestroyImmediate(data);
            else
                Destroy(data);
        }

        // A death marker is a root object that outlives its unit, so it is not under the mission root.
        static void DestroyMarker(CommandableUnit unit)
        {
            if (unit == null || !unit.TryGetComponent<DeathMarker>(out var marker) || marker.LastMarker == null)
                return;
            Destroy(marker.LastMarker);
        }

        void FrameCamera(MissionLayout layout)
        {
            if (systems.camera == null)
                return;
            var bounds = layout.WorldBounds;
            systems.camera.SetBounds(new Rect(bounds.min.x - cameraMargin, bounds.min.z - cameraMargin,
                bounds.size.x + 2f * cameraMargin, bounds.size.z + 2f * cameraMargin));
            var sum = Vector3.zero;
            foreach (var unit in friendlies)
                sum += unit.transform.position;
            systems.camera.FocusOn(sum / Mathf.Max(1, friendlies.Count));
        }

        void Fill(MissionReport report, MissionLayout layout, MissionNavigationReport navigation, ObjectivePlan plan)
        {
            report.Succeeded = true;
            report.Attempt = layout.Attempt;
            report.Rooms = layout.Rooms.Count;
            report.Connections = layout.Connections.Count;
            report.FloorTiles = layout.FloorTileCount;
            report.NavArea = navigation.NavArea;
            report.PathsChecked = navigation.PathsChecked;
            report.FriendlySpawns = layout.FriendlySpawns.Count;
            report.HostileSpawns = layout.HostileSpawns.Count;
            report.Bounds = layout.WorldBounds;
            report.LayoutHash = layout.Hash;
            report.Guards = plan.GuardTiles.Count;
            report.ObjectiveHash = plan.Hash;
            var separation = float.MaxValue;
            foreach (var f in layout.FriendlySpawns)
                foreach (var h in layout.HostileSpawns)
                    separation = Mathf.Min(separation, Vector2.Distance(f, h));
            report.TeamSeparation = separation;
            foreach (var point in systems.coverRegistry.Points)
            {
                report.CoverTotal++;
                if (point.Height == CoverHeight.Low)
                    report.CoverLow++;
                else
                    report.CoverTall++;
                if (point.Placement == CoverPlacement.Corner)
                    report.CoverCorner++;
                else if (point.Placement == CoverPlacement.Column)
                    report.CoverColumn++;
            }
        }

        void SetState(MissionState state)
        {
            if (State == state)
                return;
            State = state;
            StateChanged?.Invoke(state);
        }
    }
}
