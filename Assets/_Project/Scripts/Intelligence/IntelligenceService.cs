using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>Everything the service needs to know about one built mission. Only Layout is required.</summary>
    public sealed class IntelligenceMission
    {
        public MissionLayout Layout;
        public ObjectivePlan Objectives;
        public SecurityPlan Security;
        public CameraNetwork Network;
        public MissionInteractable CameraTerminal;
        public IReadOnlyList<MissionObjective> Goals;
        /// <summary>Where presenters parent what they build (the mission root, so regeneration destroys it).</summary>
        public Transform Root;
    }

    /// <summary>A security device the model knows by id: the camera-control terminal (id 0) or a camera, at a world position.</summary>
    public readonly struct IntelDevice
    {
        public IntelDevice(int id, Vector3 position)
        {
            Id = id;
            Position = position;
        }

        public int Id { get; }
        public Vector3 Position { get; }
    }

    /// <summary>A running scan: a flat circle observed until a scaled time.</summary>
    public readonly struct IntelPulse
    {
        public IntelPulse(Vector3 centre, float radius, float until)
        {
            Centre = centre;
            Radius = radius;
            Until = until;
        }

        public Vector3 Centre { get; }
        public float Radius { get; }
        public float Until { get; }
    }

    /// <summary>
    /// The player's knowledge of the running mission. It owns the MissionIntelligence model and feeds it, every 0.2 s of
    /// simulation time, from the sources: friendly sight (range + line of sight, all squad members share it), the hacked
    /// cameras, scan pulses, hostiles that have just fired and (once, at Begin) the briefing. Everything else only asks.
    /// A service without a mission, and a mission whose fog is off, answer "known" to everything. Lives on Systems; the
    /// director calls Begin when a mission is ready and Clear when it is torn down. Passes run on scaled time, so a tactical
    /// pause freezes sight, exposure and scans together; queries keep working.
    /// </summary>
    public sealed class IntelligenceService : MonoBehaviour
    {
        [SerializeField] LayerMask sightBlockers = ~0;
        [SerializeField, Min(0.05f)] float passInterval = 0.2f;
        // Used by presenters for last-known markers.
        [SerializeField] Material markerMaterial;

        readonly RaycastHit[] hits = new RaycastHit[LineOfSight.HitBufferSize];
        readonly List<(UnitAttacker attacker, Action<Health> handler)> watchers = new List<(UnitAttacker, Action<Health>)>();
        readonly Dictionary<Health, float> exposures = new Dictionary<Health, float>();
        readonly List<Health> expired = new List<Health>();
        readonly List<IntelPulse> pulses = new List<IntelPulse>();
        readonly List<IntelDevice> devices = new List<IntelDevice>();
        readonly List<int> regionBuffer = new List<int>();

        IntelligenceSettings settings = new IntelligenceSettings();
        MissionIntelligence model;
        RegionMap map;
        Encounter encounter;
        CameraNetwork network;
        MissionInteractable cameraTerminal;
        float nextPass;
        bool truthView;

        public MissionIntelligence Model => model;
        public IntelligenceSettings Settings => settings;
        public RegionMap Map => map;
        public Material MarkerMaterial => markerMaterial;
        public bool HasMission => model != null;
        /// <summary>A mission is running and its fog is on.</summary>
        public bool IsFogActive => model != null && !model.IsOpen;
        public bool ListsUnknownObjectives => IsFogActive && settings.showUnknownObjectives;

        /// <summary>Developer view: show everything as it is. Display only; targeting is never affected.</summary>
        public bool TruthView
        {
            get => truthView;
            set
            {
                if (truthView == value)
                    return;
                truthView = value;
                Changed?.Invoke();
            }
        }

        /// <summary>Raised when anything the player knows (or the truth view) changes.</summary>
        public event Action Changed;

        /// <summary>Raised when a mission begins, so presenters can build their world objects under its root.</summary>
        public event Action<MissionLayout, RegionMap, Transform> MissionBegun;

        // ---- lifecycle ----

        public void Begin(IntelligenceMission mission, IntelligenceSettings intelligenceSettings, Encounter currentEncounter)
        {
            if (mission == null || mission.Layout == null)
                throw new ArgumentNullException(nameof(mission));
            Clear();
            settings = (intelligenceSettings ?? new IntelligenceSettings()).Validated();
            encounter = currentEncounter;
            network = mission.Network;
            cameraTerminal = mission.CameraTerminal;
            map = new RegionMap(mission.Layout);
            var security = mission.Security ?? SecurityPlan.Empty;
            model = new MissionIntelligence(map, security.DeviceCount, open: !settings.fogEnabled);
            model.Changed += RaiseChanged;
            BuildDevices(mission.Layout, security);
            if (encounter != null)
            {
                foreach (var hostile in encounter.Hostiles)
                {
                    if (hostile == null)
                        continue;
                    model.TrackEnemy(hostile);
                    WatchHostile(hostile);
                }
            }
            ApplyBriefing(mission);
            if (mission.Goals != null)
                model.BindObjectives(mission.Goals, settings.objectives);
            if (cameraTerminal != null)
                cameraTerminal.Completed += OnCameraTerminalCompleted;
            MissionBegun?.Invoke(mission.Layout, map, mission.Root);
            RunPass();
            Changed?.Invoke();
        }

        /// <summary>Forgets the mission: no knowledge, no pulses, no subscriptions survive.</summary>
        public void Clear()
        {
            foreach (var (attacker, handler) in watchers)
            {
                if (attacker == null)
                    continue;
                attacker.Attacked -= handler;
                attacker.Missed -= handler;
            }
            watchers.Clear();
            if (cameraTerminal != null)
                cameraTerminal.Completed -= OnCameraTerminalCompleted;
            cameraTerminal = null;
            network = null;
            exposures.Clear();
            pulses.Clear();
            devices.Clear();
            var had = model != null;
            if (model != null)
                model.Changed -= RaiseChanged;
            model = null;
            map = null;
            encounter = null;
            if (had)
                Changed?.Invoke();
        }

        void OnDisable() => Clear();

        void Update()
        {
            if (model == null || model.IsOpen || !SimulationTime.IsRunning)
                return;
            if (Time.time >= nextPass)
                RunPass();
        }

        void RaiseChanged() => Changed?.Invoke();

        // ---- queries (a missing mission or an open model answers "known") ----

        public bool CanTarget(Health unit) => model == null || model.IsObserved(unit);

        public bool IsUnitShown(Health unit) => model == null || truthView || model.IsObserved(unit);

        public KnowledgeState StateOfEnemy(Health enemy) => model == null ? KnowledgeState.Observed : model.StateOfEnemy(enemy);

        public bool TryLastKnown(Health enemy, out Vector3 position)
        {
            position = default;
            return model != null && model.TryGetLastKnown(enemy, out position);
        }

        public KnowledgeState StateOfRegion(int region) => model == null ? KnowledgeState.Observed : model.StateOfRegion(region);

        /// <summary>The mission's security devices, for the overlay and debug views.</summary>
        public IReadOnlyList<IntelDevice> Devices => devices;

        /// <summary>The scans still running, for the debug views.</summary>
        public IReadOnlyList<IntelPulse> Pulses => pulses;

        public CameraNetwork Network => network;

        public bool CanInteract(MissionInteractable item)
        {
            if (model == null || model.IsOpen || item == null)
                return true;
            if (item == cameraTerminal)
                return model.StateOfDevice(SecurityPlan.TerminalDeviceId) != KnowledgeState.Unknown;
            return model.StateOfPoint(item.Position) != KnowledgeState.Unknown;
        }

        public bool CanSeeCover(CoverLocation cover) =>
            model == null || model.IsOpen || cover == null || model.StateOfPoint(cover.Position) != KnowledgeState.Unknown;

        /// <summary>Whether a cover marker may be drawn: known ground, or the developer truth view.</summary>
        public bool IsCoverShown(CoverLocation cover) => truthView || CanSeeCover(cover);

        public bool IsDeviceShown(int deviceId) =>
            model == null || truthView || model.IsOpen || model.StateOfDevice(deviceId) != KnowledgeState.Unknown;

        // ---- passes ----

        /// <summary>One sampling of every source. Called every passInterval of simulation time, and at once when something changes.</summary>
        public void RunPass()
        {
            if (model == null || model.IsOpen)
                return;
            nextPass = Time.time + passInterval;
            model.BeginPass();
            ObserveFromFriendlies();
            if (network != null && network.Compromised && settings.cameraStaysLive)
            {
                foreach (var camera in network.Cameras)
                    SweepCamera(camera, live: true);
            }
            ObservePulsesAndExposures();
            model.EndPass();
        }

        /// <summary>
        /// A scan pulse (Recon Scan): discovers every region the circle touches for good, discovers the devices inside it,
        /// and observes every hostile inside it for `seconds`, walls notwithstanding (a scan is not sight). Does nothing when
        /// the fog is off.
        /// </summary>
        public void Scan(Vector3 centre, float radius, float seconds)
        {
            if (model == null || model.IsOpen)
                return;
            model.RevealArea(centre, radius);
            foreach (var device in devices)
            {
                if (ObservationRules.InCircle(centre, radius, device.Position))
                    model.RevealDevice(device.Id);
            }
            pulses.Add(new IntelPulse(centre, radius, Time.time + Mathf.Max(0f, seconds)));
            RunPass();
        }

        void ObserveFromFriendlies()
        {
            if (encounter == null)
                return;
            var range = settings.observationRange;
            foreach (var friendly in encounter.Friendlies)
            {
                if (!IsLiving(friendly))
                    continue;
                var pivot = friendly.transform.position;
                var eye = pivot + Vector3.up * LineOfSight.EyeHeight;
                var own = map.RegionAt(pivot);
                if (own >= 0)
                    model.MarkRegionObserved(own);
                map.RegionsInCircle(pivot, range, regionBuffer);
                foreach (var region in regionBuffer)
                {
                    if (region != own && AnySampleVisible(eye, pivot, region, range))
                        model.MarkRegionObserved(region);
                }
                foreach (var hostile in encounter.Hostiles)
                {
                    if (IsLiving(hostile) && ObservationRules.InCircle(pivot, range, hostile.transform.position)
                        && LineOfSight.IsClear(eye, hostile.transform.position, sightBlockers, hits))
                        model.ObserveEnemy(hostile, hostile.transform.position);
                }
                foreach (var device in devices)
                {
                    if (ObservationRules.InCircle(pivot, range, device.Position) && LineOfSight.IsClear(eye, device.Position, sightBlockers, hits))
                        model.RevealDevice(device.Id);
                }
            }
        }

        bool AnySampleVisible(Vector3 eye, Vector3 origin, int region, float range)
        {
            foreach (var sample in map.SamplePoints(region))
            {
                if (ObservationRules.InCircle(origin, range, sample) && LineOfSight.IsClear(eye, sample, sightBlockers, hits))
                    return true;
            }
            return false;
        }

        // A hacked camera: the regions it covers (live: observed this pass; snapshot: discovered for good), the devices it
        // sees, and when live the hostiles it sees.
        void SweepCamera(CameraSpec camera, bool live)
        {
            map.RegionsInCircle(camera.Position, camera.Range, regionBuffer);
            foreach (var region in regionBuffer)
            {
                if (!AnyCameraSampleVisible(camera, region))
                    continue;
                if (live)
                    model.MarkRegionObserved(region);
                else
                    model.RevealRegion(region);
            }
            foreach (var device in devices)
            {
                if (device.Id != camera.DeviceId && CameraSees(camera, device.Position))
                    model.RevealDevice(device.Id);
            }
            if (!live || encounter == null)
                return;
            foreach (var hostile in encounter.Hostiles)
            {
                if (IsLiving(hostile) && CameraSees(camera, hostile.transform.position))
                    model.ObserveEnemy(hostile, hostile.transform.position);
            }
        }

        bool AnyCameraSampleVisible(CameraSpec camera, int region)
        {
            foreach (var sample in map.SamplePoints(region))
            {
                if (CameraSees(camera, sample))
                    return true;
            }
            return false;
        }

        bool CameraSees(CameraSpec camera, Vector3 point) =>
            camera.Covers(point) && LineOfSight.IsClear(camera.Position, point, sightBlockers, hits);

        void ObservePulsesAndExposures()
        {
            for (var i = pulses.Count - 1; i >= 0; i--)
            {
                if (Time.time >= pulses[i].Until)
                    pulses.RemoveAt(i);
            }
            foreach (var pulse in pulses)
            {
                map.RegionsInCircle(pulse.Centre, pulse.Radius, regionBuffer);
                foreach (var region in regionBuffer)
                    model.MarkRegionObserved(region);
            }
            if (encounter != null && pulses.Count > 0)
            {
                foreach (var hostile in encounter.Hostiles)
                {
                    if (!IsLiving(hostile))
                        continue;
                    foreach (var pulse in pulses)
                    {
                        if (!ObservationRules.InCircle(pulse.Centre, pulse.Radius, hostile.transform.position))
                            continue;
                        model.ObserveEnemy(hostile, hostile.transform.position);
                        break;
                    }
                }
            }
            expired.Clear();
            foreach (var pair in exposures)
            {
                if (Time.time >= pair.Value || !IsLiving(pair.Key))
                {
                    expired.Add(pair.Key);
                    continue;
                }
                model.ObserveEnemy(pair.Key, pair.Key.transform.position);
            }
            foreach (var key in expired)
                exposures.Remove(key);
        }

        static bool IsLiving(Health unit) => unit != null && unit.IsAlive && unit.gameObject.activeInHierarchy;

        // ---- sources that arrive as events ----

        // A hostile that fires (hit or miss) is exposed for exposureSeconds wherever it is.
        void WatchHostile(Health hostile)
        {
            if (!hostile.TryGetComponent<UnitAttacker>(out var attacker))
                return;
            Action<Health> handler = _ => Expose(hostile);
            attacker.Attacked += handler;
            attacker.Missed += handler;
            watchers.Add((attacker, handler));
        }

        void Expose(Health hostile)
        {
            if (model == null || model.IsOpen || !IsLiving(hostile))
                return;
            exposures[hostile] = Time.time + settings.exposureSeconds;
            RunPass();
        }

        void OnCameraTerminalCompleted(MissionInteractable item)
        {
            if (network == null || network.Compromised)
                return;
            network.Compromise();
            if (model != null && !model.IsOpen && !settings.cameraStaysLive)
            {
                foreach (var camera in network.Cameras)
                    SweepCamera(camera, live: false);
            }
            RunPass();
            Changed?.Invoke();
        }

        // ---- briefing ----

        void BuildDevices(MissionLayout layout, SecurityPlan security)
        {
            devices.Clear();
            if (security.HasTerminal)
                devices.Add(new IntelDevice(SecurityPlan.TerminalDeviceId, security.TerminalPosition(layout)));
            for (var i = 0; i < security.Cameras.Count; i++)
                devices.Add(new IntelDevice(security.CameraDeviceId(i), security.CameraPosition(layout, i)));
        }

        void ApplyBriefing(IntelligenceMission mission)
        {
            if (model.IsOpen)
                return;
            switch (settings.map)
            {
                case MapKnowledge.Full:
                    for (var region = 0; region < map.Count; region++)
                        model.RevealRegion(region);
                    break;
                case MapKnowledge.Partial:
                    var start = mission.Layout.FriendlyRoom;
                    if (start >= 0 && start < map.RoomCount)
                    {
                        model.RevealRegion(start);
                        foreach (var corridor in map.Neighbours(start))
                            model.RevealRegion(corridor);
                    }
                    if (mission.Objectives != null)
                        model.RevealRegion(mission.Objectives.ExtractionRoom);
                    break;
            }
            if (encounter == null)
                return;
            var markers = Mathf.Min(settings.enemyMarkersAtStart, encounter.Hostiles.Count);
            for (var i = 0; i < markers; i++)
            {
                var hostile = encounter.Hostiles[i];
                if (hostile != null)
                    model.MarkLastKnown(hostile, hostile.transform.position);
            }
        }
    }
}
