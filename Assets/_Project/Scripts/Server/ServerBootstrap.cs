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
        // The attempt's data (Stage 12): set by ServerSceneBuilder. The loot table is given to the director only for an attempt,
        // so the fixture mission (-diagnostic, -net) is generated exactly as before.
        [SerializeField] ItemCatalogue itemCatalogue;
        [SerializeField] LootTable lootTable;
        [SerializeField] SquadRoster roster;
        [SerializeField] SquadInventory inventory;
        [SerializeField] ProgressionTrack progression;
        [SerializeField] OperativeDefinition[] operatives = new OperativeDefinition[0];
        [SerializeField] string catalogueVersion = string.Empty;

#if UNITY_EDITOR || BLACKGLASS_NET_FIXTURE
        const bool FixtureBuild = true;
#else
        const bool FixtureBuild = false;
#endif
        AttemptStart attempt;

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
        internal Func<long> EffectiveUserId = OutboxSelftest.EffectiveUserId;
        internal ServerOptions Options => options;
        internal bool HasExited { get; private set; }
        internal int ExitCode { get; private set; } = -1;
        internal string ExitReason { get; private set; }
        internal int UnitViewsDisabled { get; private set; }
        internal int UnitViewsActive { get; private set; }
        /// <summary>Whether -attemptFixture may run: Editor and fixture builds only (S12-D4). Tests replace it, as an Editor
        /// compile can't observe a release build's define.</summary>
        internal Func<bool> AttemptFixtureAllowed = () => FixtureBuild;
        /// <summary>Where -attemptFixture's attempt comes from (the fixture source); tests replace it to vary the register answer.</summary>
        internal Func<string, IAttemptSource> AttemptSourceFactory = CreateFixtureSource;
        /// <summary>The running attempt's session (Stage 12), once its mission is ready; null otherwise.</summary>
        public AttemptSession Session { get; private set; }
        internal SquadInventory Inventory => inventory;

        internal void Initialize(MissionDirector missionDirector, TacticalPause tacticalPause)
        {
            director = missionDirector;
            pause = tacticalPause;
        }

        /// <summary>The attempt's data (ServerSceneBuilder): catalogue, loot table, the snapshot-mode roster and inventory, this
        /// build's operative definitions and progression, and the build's catalogue version.</summary>
        internal void InitializeAttempt(ItemCatalogue items, LootTable loot, SquadRoster squad, SquadInventory squadInventory, ProgressionTrack track,
            OperativeDefinition[] definitions, string buildCatalogueVersion)
        {
            itemCatalogue = items;
            lootTable = loot;
            roster = squad;
            inventory = squadInventory;
            progression = track;
            operatives = definitions ?? new OperativeDefinition[0];
            catalogueVersion = buildCatalogueVersion ?? string.Empty;
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
            director.StateChanged -= OnStateChanged;
            director.StateChanged += OnStateChanged;
            started = true;
            startupDeadline = Now + options.StartupTimeoutSeconds;
            generation = Stopwatch.StartNew();
            if (options.AttemptFixture != null)
            {
                BeginAttempt(options.AttemptFixture);
                return;
            }
            var seed = options.Seed ?? director.Settings.seed;
            if (!director.Generate(seed))
            {
                Finish(ServerExitCodes.StartupFailed, "the director refused to generate");
                return;
            }
            Debug.Log(Prefix + "generating " + director.Settings.Describe());
        }

        /// <summary>
        /// The fixture attempt (Stage 12 Task 8): refused outside Editor and fixture builds; then the source, AttemptStart, the
        /// snapshot squad (roster and inventory), the loot table, and GenerateForAttempt. Any refusal ends startup (exit 2)
        /// with its code; the seed is never logged.
        /// </summary>
        void BeginAttempt(string fixture)
        {
            Session?.Unbind();
            Session = null;
            attempt = null;
            ready = false;
            if (!AttemptFixtureAllowed())
            {
                Finish(ServerExitCodes.StartupFailed, "-attemptFixture is not available in a release build");
                return;
            }
            if (fixture.Length == 0)
            {
                Finish(ServerExitCodes.StartupFailed, "-attemptFixture needs the path of an attempt fixture");
                return;
            }
            var source = AttemptSourceFactory(fixture);
            if (source == null)
            {
                Finish(ServerExitCodes.StartupFailed, "attempt: no fixture source in this build");
                return;
            }
            if (!source.TryLoad(out var attemptId, out var response, out var problem))
            {
                Finish(ServerExitCodes.StartupFailed, "attempt: " + problem);
                return;
            }
            if (itemCatalogue == null || lootTable == null || roster == null || inventory == null || progression == null)
            {
                Finish(ServerExitCodes.StartupFailed, "attempt: the scene's ServerBootstrap is not wired for attempts (rebuild the server scene)");
                return;
            }
            if (!AttemptStart.TryCreate(attemptId, response, catalogueVersion, id => AttemptSquad.Find(operatives, id) != null,
                    ItemLedgerMapping.Catalogue(itemCatalogue), out var start, out var refusal))
            {
                Finish(ServerExitCodes.StartupFailed, "attempt: " + refusal);
                return;
            }
            if (!AttemptSquad.TryMembers(start, operatives, progression, out var members, out refusal))
            {
                Finish(ServerExitCodes.StartupFailed, "attempt: " + refusal);
                return;
            }
            roster.InitializeFromSnapshot(members, progression);
            if (!inventory.InitializeFromSnapshot(itemCatalogue, roster, inventory.Core.BagCapacity, start.Snapshot, out refusal))
            {
                Finish(ServerExitCodes.StartupFailed, "attempt: " + refusal);
                return;
            }
            director.SetLootTable(lootTable);
            attempt = start;
            if (!director.GenerateForAttempt(start))
            {
                Finish(ServerExitCodes.StartupFailed, "the director refused to generate");
                return;
            }
            Debug.Log(Prefix + $"generating attempt {start.AttemptId} operatives={start.Snapshot.Count} template={start.TemplateId}/{start.TemplateVersion}");
        }

        static IAttemptSource CreateFixtureSource(string path)
        {
#if UNITY_EDITOR || BLACKGLASS_NET_FIXTURE
            return new FixtureAttemptSource(path);
#else
            return null;
#endif
        }

        /// <summary>The build's revision for the manifest: the stamped revision when it is well formed, else "editor".</summary>
        static string BuildRevision()
        {
#if UNITY_EDITOR
            return "editor";
#else
            var info = ServerBuildInfo.ReadBesideExecutable();
            return info != null && info.TryGetValue("revision", out var revision) && CanonicalIds.IsBuildRevision(revision) ? revision : "unknown";
#endif
        }

        // The attempt's session starts on the ready mission; a refusal ends startup.
        bool BeginSession()
        {
            Session = AttemptSession.Begin(attempt, director, inventory, ItemLedgerMapping.Catalogue(itemCatalogue), new ServerClock(), BuildRevision(), out var refusal);
            if (Session == null)
            {
                Finish(ServerExitCodes.StartupFailed, "attempt: " + refusal);
                return false;
            }
            var report = Session.ReadyReport;
            Debug.Log(Prefix + $"ATTEMPT READY attempt={attempt.AttemptId} lines={report.lootPool.Length} lootHash={report.lootHash} layoutHash={report.layoutHash}");
            Debug.Log(Prefix + "attempt ready report " + JsonUtility.ToJson(report));
#if UNITY_EDITOR || BLACKGLASS_NET_FIXTURE
            LogFixtureOperatorAid();
#endif
            director.MissionFinished += OnAttemptFinished;
            return true;
        }

#if UNITY_EDITOR || BLACKGLASS_NET_FIXTURE
        // The loopback fixture checkpoint's operator aid (Editor and fixture builds only): where each hostile starts and where
        // extraction is, as NetGround points, in the server's log only. Nothing here reaches a client; an operator reads it to
        // steer an assisted run. The seed is still never logged.
        void LogFixtureOperatorAid()
        {
            var line = new System.Text.StringBuilder("FIXTURE OPERATOR AID (server log only; never sent to a client) extraction=")
                .Append(NetGround.Format(director.Current.ExtractionZone.position))
                .Append(FormattableString.Invariant($" radius={MissionContent.ZoneRadius:0.0}"));
            for (var i = 0; i < director.Hostiles.Count; i++)
                line.Append(" hostile").Append(i + 1).Append('=').Append(NetGround.Format(director.Hostiles[i].transform.position));
            Debug.Log(Prefix + line);
        }

        // The frozen manifest's canonical bytes, once, so an operator can check them against the logged hash (Editor and fixture builds only).
        void LogFixtureManifest(BuiltManifest manifest) =>
            Debug.Log(Prefix + "attempt manifest " + System.Text.Encoding.UTF8.GetString(manifest.CanonicalBytes));
#endif

        void OnAttemptFinished(MissionPhase phase)
        {
            director.MissionFinished -= OnAttemptFinished;
            if (Session == null || Session.Outcome == null)
                return;
            var outcome = Session.Outcome;
            Debug.Log(Prefix + $"ATTEMPT FROZEN outcome={(Session.Result != null ? Session.Result.Outcome.ToString() : "none")} build={outcome.Kind} integrity={Session.Facts.Integrity} " +
                $"outcomeSequence={(outcome.Manifest != null ? outcome.Manifest.Manifest.OutcomeSequence.ToString(System.Globalization.CultureInfo.InvariantCulture) : "none")} " +
                $"manifestHash={(outcome.Manifest != null ? outcome.Manifest.ManifestHash : "none")} diagnostics={string.Join(",", outcome.Diagnostics)}");
#if UNITY_EDITOR || BLACKGLASS_NET_FIXTURE
            if (outcome.Manifest != null)
                LogFixtureManifest(outcome.Manifest);
#endif
        }

        void OnDestroy()
        {
            if (director != null)
            {
                director.StateChanged -= OnStateChanged;
                director.MissionFinished -= OnAttemptFinished;
            }
            Session?.Unbind();
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
            if (attempt != null && !BeginSession())
                return;
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
                FormattableString.Invariant($"MISSION READY instance={director.InstanceId} seed={(director.Attempt != null ? "attempt" : r.Seed.ToString(System.Globalization.CultureInfo.InvariantCulture))} attempt={r.Attempt}/{r.MaxAttempts} rooms={r.Rooms} navArea={r.NavArea:0} ") +
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
