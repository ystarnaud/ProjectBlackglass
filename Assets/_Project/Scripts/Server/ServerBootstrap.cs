using System;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.EventSystems;
using Debug = UnityEngine.Debug;

namespace Blackglass
{
    /// <summary>Exit codes of the dedicated server (decision 045).</summary>
    public static class ServerExitCodes
    {
        /// <summary>The diagnostic passed (a normal stop also exits with Unity's own 0).</summary>
        public const int Success = 0;
        public const int DiagnosticFailed = 1;
        /// <summary>The mission never became ready: generation failed, the startup deadline passed, or the scene is unwired.</summary>
        public const int StartupFailed = 2;
    }

    /// <summary>
    /// The dedicated server's entry point (the ServerMission scene, Checkpoint A, decision 045). Reads the command line, caps
    /// the frame rate, logs which build is running, and generates the scene's fixture mission without a local player. When
    /// the mission is ready it disables the units' view components (the server shows nothing), logs readiness and a
    /// self-check, then either runs the bounded diagnostic (-diagnostic) or logs a heartbeat until the process is stopped.
    /// Holds no game rule: the mission comes from MissionDirector and every order from CommandableUnit.Issue.
    /// </summary>
    public sealed class ServerBootstrap : MonoBehaviour
    {
        public const string Prefix = "[Server] ";

#if UNITY_SERVER
        const bool UnityServerDefine = true;
#else
        const bool UnityServerDefine = false;
#endif

        [SerializeField] MissionDirector director;
        [SerializeField] TacticalPause pause;

        ServerOptions options = ServerOptions.Default;
        Stopwatch generation;
        double startupDeadline;
        bool started;
        bool ready;
        ServerDiagnostic diagnostic;
        double heartbeatStart;
        double nextHeartbeat;
        int heartbeatFrames;
        TimeSpan heartbeatCpu;

        /// <summary>How the process ends. Application.Quit does nothing in the Editor; tests replace this.</summary>
        internal Action<int> Exit = code => Application.Quit(code);
        internal ServerOptions Options => options;
        internal bool HasExited { get; private set; }
        internal int ExitCode { get; private set; } = -1;
        internal string ExitReason { get; private set; }
        internal int UnitViewsDisabled { get; private set; }
        internal int UnitViewsActive { get; private set; }

        internal void Initialize(MissionDirector missionDirector, TacticalPause tacticalPause)
        {
            director = missionDirector;
            pause = tacticalPause;
        }

        static double Now => Time.realtimeSinceStartupAsDouble;

        void Start()
        {
#if !UNITY_EDITOR
            // One line per log call in the player's stdout; warnings and errors keep their stack traces.
            Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
#endif
            options = ServerOptions.Parse(Environment.GetCommandLineArgs(), out var warnings);
            foreach (var warning in warnings)
                Debug.LogWarning(Prefix + "option ignored: " + warning);
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = options.FrameRate;
            Debug.Log(Prefix + IdentityLine());
            if (director == null || pause == null)
            {
                Finish(ServerExitCodes.StartupFailed, "the scene's ServerBootstrap is not wired to a MissionDirector and a TacticalPause");
                return;
            }
            Debug.Log(Prefix + "options " + options.Describe(director.Settings.seed));
            director.StateChanged += OnStateChanged;
            started = true;
            startupDeadline = Now + options.StartupTimeoutSeconds;
            generation = Stopwatch.StartNew();
            var seed = options.Seed ?? director.Settings.seed;
            if (!director.Generate(seed))
            {
                Finish(ServerExitCodes.StartupFailed, "the director refused to generate");
                return;
            }
            Debug.Log(Prefix + "generating " + director.Settings.Describe());
        }

        void OnDestroy()
        {
            if (director != null)
                director.StateChanged -= OnStateChanged;
        }

        void OnApplicationQuit() => Debug.Log(Prefix + "quitting");

        internal void OnStateChanged(MissionState state)
        {
            if (state == MissionState.Failed)
            {
                Finish(ServerExitCodes.StartupFailed, "mission generation failed: " + (director != null ? director.Report.Failure : "no director"));
                return;
            }
            if (state != MissionState.Ready)
                return;
            ready = true;
            generation?.Stop();
            DisableUnitViews();
            LogReady();
            LogSelfCheck();
            StartHeartbeat(Now);
            if (options.Diagnostic)
                BeginDiagnostic(options);
        }

        /// <summary>Starts the diagnostic on the ready mission (from the Ready handler with -diagnostic; tests call it directly).</summary>
        internal void BeginDiagnostic(ServerOptions diagnosticOptions)
        {
            diagnostic = new ServerDiagnostic(director, diagnosticOptions.DiagnosticTimeoutSeconds, line => Debug.Log(line));
            diagnostic.Begin(Now);
        }

        void Update()
        {
            if (HasExited || !started)
                return;
            var now = Now;
            if (!ready)
            {
                if (now > startupDeadline)
                    Finish(ServerExitCodes.StartupFailed,
                        FormattableString.Invariant($"the mission was not ready within {options.StartupTimeoutSeconds} s (state {director.State})"));
                return;
            }
            if (diagnostic != null)
            {
                diagnostic.Tick(now);
                if (!diagnostic.IsFinished)
                    return;
                if (diagnostic.Passed)
                    Debug.Log(diagnostic.Summary);
                else
                    Debug.LogWarning(diagnostic.Summary);
                Finish(diagnostic.Passed ? ServerExitCodes.Success : ServerExitCodes.DiagnosticFailed, diagnostic.Summary);
                return;
            }
            Heartbeat(now);
        }

        void Finish(int code, string reason)
        {
            if (HasExited)
                return;
            HasExited = true;
            ExitCode = code;
            ExitReason = reason;
            var line = Prefix + $"exit code={code} reason={reason}";
            if (code == ServerExitCodes.Success)
                Debug.Log(line);
            else
                Debug.LogWarning(line);
            Exit(code);
        }

        /// <summary>Per-unit components that only show something (decision 014 debug views, the selection ring, the hit flash).</summary>
        internal static bool IsView(Behaviour behaviour) =>
            behaviour is AttackLineView || behaviour is CommandQueueView || behaviour is SelectionIndicator || behaviour is HitFlash;

        // Instances only: GameObjects, renderers and colliders stay; nothing in a prefab changes.
        void DisableUnitViews()
        {
            var disabled = 0;
            var active = 0;
            foreach (var unit in director.Friendlies)
                Disable(unit, ref disabled);
            foreach (var unit in director.Hostiles)
                Disable(unit, ref disabled);
            foreach (var unit in director.Friendlies)
                active += CountActive(unit);
            foreach (var unit in director.Hostiles)
                active += CountActive(unit);
            UnitViewsDisabled = disabled;
            UnitViewsActive = active;
        }

        static void Disable(Component unit, ref int disabled)
        {
            if (unit == null)
                return;
            foreach (var behaviour in unit.GetComponentsInChildren<Behaviour>(true))
            {
                if (!IsView(behaviour) || !behaviour.enabled)
                    continue;
                behaviour.enabled = false;
                disabled++;
            }
        }

        static int CountActive(Component unit)
        {
            if (unit == null)
                return 0;
            var count = 0;
            foreach (var behaviour in unit.GetComponentsInChildren<Behaviour>(true))
            {
                if (IsView(behaviour) && behaviour.enabled)
                    count++;
            }
            return count;
        }

        void LogReady()
        {
            var r = director.Report;
            var generationMs = generation != null ? generation.Elapsed.TotalMilliseconds : 0d;
            Debug.Log(Prefix +
                FormattableString.Invariant($"MISSION READY instance={director.InstanceId} seed={r.Seed} attempt={r.Attempt}/{r.MaxAttempts} rooms={r.Rooms} navArea={r.NavArea:0} ") +
                FormattableString.Invariant($"paths={r.PathsChecked} cover={r.CoverTotal}(low {r.CoverLow}, tall {r.CoverTall}) friendlies={director.Friendlies.Count} ") +
                FormattableString.Invariant($"hostiles={director.Hostiles.Count} layoutHash={r.LayoutHash:x16} generationMs={generationMs:0} ") +
                FormattableString.Invariant($"navMeshBakeMs={r.NavMeshBakeMs:0.0} sinceStartupS={Now:0.0}"));
        }

        void LogSelfCheck()
        {
            var cameras = FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
            var listeners = FindObjectsByType<AudioListener>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
            var canvases = FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
            var eventSystems = FindObjectsByType<EventSystem>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
            Debug.Log(Prefix + $"self-check cameras={cameras} audioListeners={listeners} canvases={canvases} eventSystems={eventSystems} " +
                $"unitViewsDisabled={UnitViewsDisabled} unitViewsActive={UnitViewsActive}");
        }

        string IdentityLine() =>
            $"identity product={Application.productName} version={Application.version} {ServerBuildInfo.Describe(ServerBuildInfo.ReadBesideExecutable())} " +
            $"unity={Application.unityVersion} platform={Application.platform} UNITY_SERVER={(UnityServerDefine ? "yes" : "no")} " +
            $"batchmode={(Application.isBatchMode ? "yes" : "no")} graphics={SystemInfo.graphicsDeviceType} buildGUID={Application.buildGUID}";

        void StartHeartbeat(double now)
        {
            heartbeatStart = now;
            nextHeartbeat = now + options.HeartbeatSeconds;
            heartbeatFrames = 0;
            TrySample(out heartbeatCpu, out _);
        }

        void Heartbeat(double now)
        {
            if (options.HeartbeatSeconds <= 0)
                return;
            heartbeatFrames++;
            if (now < nextHeartbeat)
                return;
            var elapsed = now - heartbeatStart;
            var fps = elapsed > 0d ? heartbeatFrames / elapsed : 0d;
            string cpu = "n/a", rss = "n/a";
            if (TrySample(out var processor, out var workingSet))
            {
                cpu = FormattableString.Invariant($"{100d * (processor - heartbeatCpu).TotalSeconds / Math.Max(elapsed, 0.001d):0.0}");
                rss = FormattableString.Invariant($"{workingSet / 1048576d:0.0}");
                heartbeatCpu = processor;
            }
            Debug.Log(Prefix + FormattableString.Invariant(
                $"heartbeat uptimeS={now:0} fps={fps:0.0} cpuPct={cpu} rssMB={rss} managedMB={GC.GetTotalMemory(false) / 1048576d:0.0} state={director.State} phase={director.Phase}"));
            heartbeatStart = now;
            nextHeartbeat = now + options.HeartbeatSeconds;
            heartbeatFrames = 0;
        }

        static bool TrySample(out TimeSpan processor, out long workingSet)
        {
            try
            {
                using (var process = Process.GetCurrentProcess())
                {
                    processor = process.TotalProcessorTime;
                    workingSet = process.WorkingSet64;
                    return true;
                }
            }
            catch (Exception)
            {
                processor = TimeSpan.Zero;
                workingSet = 0;
                return false;
            }
        }
    }
}
