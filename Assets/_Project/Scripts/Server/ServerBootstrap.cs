using System;
using System.Collections.Generic;
using System.Diagnostics;
using Blackglass.Backend.Core;
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
        /// <summary>-outboxSelftest: a check failed (Stage 12 plan, Task 1).</summary>
        public const int SelftestFailed = 3;
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
        OutboxSelftest selftest;
        double heartbeatStart;
        double nextHeartbeat;
        int heartbeatFrames;
        TimeSpan heartbeatCpu;

        /// <summary>How the process ends. Application.Quit does nothing in the Editor; tests replace this.</summary>
        internal Action<int> Exit = code => Application.Quit(code);
        /// <summary>Opens the self-test's outbox directory from $STATE_DIRECTORY; tests replace it with a portable directory.</summary>
        internal Func<ServerOptions, IOutboxDirectory> OpenOutbox = o => OpenSelftestOutbox(o, Environment.GetEnvironmentVariable("STATE_DIRECTORY"));
        /// <summary>The effective uid the self-test checks (it refuses root); tests replace it.</summary>
        internal Func<long> EffectiveUserId = OutboxDirectories.EffectiveUserId;
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

        void Start() => Begin(Environment.GetCommandLineArgs());

        /// <summary>Start's work, from the command line (tests pass their own arguments).</summary>
        internal void Begin(IReadOnlyList<string> args)
        {
#if !UNITY_EDITOR
            // One line per log call in the player's stdout; warnings and errors keep their stack traces.
            Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
#endif
            options = ServerOptions.Parse(args, out var warnings);
            foreach (var warning in warnings)
                Debug.LogWarning(Prefix + "option ignored: " + warning);
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = options.FrameRate;
            Debug.Log(Prefix + IdentityLine());
            if (options.OutboxSelftest != null)
            {
                // The self-test needs no mission and no network: nothing below runs, -net is never read.
                Debug.Log(Prefix + "outbox self-test phase=" + options.OutboxSelftest);
                selftest = new OutboxSelftest(options.OutboxSelftest, () => OpenOutbox(options), EffectiveUserId, line => Debug.Log(line));
                return;
            }
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
            StartConfiguredMode(options);
        }

        /// <summary>Starts the diagnostic on the ready mission (from the Ready handler with -diagnostic; tests call it directly).</summary>
        internal void BeginDiagnostic(ServerOptions diagnosticOptions)
        {
            diagnostic = new ServerDiagnostic(director, diagnosticOptions.DiagnosticTimeoutSeconds, line => Debug.Log(line));
            diagnostic.Begin(Now);
        }

        /// <summary>
        /// What the ready server does next (from the Ready handler; tests call it directly): a diagnostic run wins over -net, so
        /// the diagnostic stays bounded; otherwise -net starts the networking adapter; otherwise the server just runs.
        /// </summary>
        internal void StartConfiguredMode(ServerOptions modeOptions)
        {
            if (modeOptions.Diagnostic)
            {
                if (modeOptions.Net)
                    Debug.LogWarning(Prefix + "-net is ignored with -diagnostic");
                BeginDiagnostic(modeOptions);
            }
            else if (modeOptions.Net)
            {
                BeginNet(modeOptions);
            }
        }

        /// <summary>Starts the networking adapter on the ready mission; a failure ends the process with the startup exit code.</summary>
        internal void BeginNet(ServerOptions netOptions)
        {
            var net = GetComponent<NetServer>();
            if (net == null)
            {
                Finish(ServerExitCodes.StartupFailed, "net: -net was given but the scene has no NetServer");
                return;
            }
            var failure = net.Begin(netOptions, director);
            if (failure != null)
                Finish(ServerExitCodes.StartupFailed, "net: " + failure);
        }

        void Update() => Tick(Now);

        /// <summary>One frame at <paramref name="now"/> (unscaled seconds); Update passes the real clock, tests their own.</summary>
        internal void Tick(double now)
        {
            if (HasExited)
                return;
            if (selftest != null)
            {
                selftest.Step(now);
                if (selftest.IsFinished)
                    Finish(selftest.Passed ? ServerExitCodes.Success : ServerExitCodes.SelftestFailed, selftest.Summary);
                return;
            }
            if (!started)
                return;
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

        /// <summary>
        /// The self-test's directory: never under the real outbox's parent (the agent relays from there), then exactly
        /// <c>parent/id</c> as OutboxLocation checks, opened and checked as the server's outbox is.
        /// A symbolic-link parent pointing at the real outbox isn't caught by the name check; OutboxLocation's exact-path rule
        /// and the directory's owner check (the unit's dynamic user, never the agent's) still refuse it.
        /// </summary>
        internal static IOutboxDirectory OpenSelftestOutbox(ServerOptions o, string stateDirectory)
        {
            const string production = "/var/lib/blackglass-outbox";
            var parent = (o.SelftestParent ?? string.Empty).TrimEnd('/');
            if (parent == production || parent.StartsWith(production + "/", StringComparison.Ordinal) || parent.Contains("blackglass-outbox"))
                throw new OutboxIOException("production_outbox", 0);
            return OutboxDirectories.OpenForServer(stateDirectory, o.SelftestParent, o.SelftestId);
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
                $"heartbeat uptimeS={now:0} fps={fps:0.0} cpuPct={cpu} rssMB={rss} managedMB={GC.GetTotalMemory(false) / 1048576d:0.0} state={director.State} phase={director.Phase}") + NetSummary());
            heartbeatStart = now;
            nextHeartbeat = now + options.HeartbeatSeconds;
            heartbeatFrames = 0;
        }

        string NetSummary()
        {
            var net = GetComponent<NetServer>();
            if (net == null || !net.Running || net.Host == null)
                return string.Empty;
            return FormattableString.Invariant(
                $" net={net.ConnectionCount} accepted={net.Host.Accepted} rejected={net.Host.Rejected} dropped={net.Host.Dropped}");
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
