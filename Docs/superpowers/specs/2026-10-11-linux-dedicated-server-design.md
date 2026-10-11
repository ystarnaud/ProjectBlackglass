# Checkpoint A: Linux dedicated-server startup (design)

Date: 2026-10-11. Branch: `checkpoint-a-linux-server` (off `main` @ `5e837cd`). Owner approved the design in conversation on 2026-10-11.

## 1. Goal and scope

Produce a reproducible Linux dedicated-server build that automatically starts one small mission and shows that the existing navigation, movement and combat simulation work in the built server.

**In scope:**
- a server-only scene and bootstrap;
- a fixture mission;
- a frame cap;
- identity and readiness logging;
- a bounded diagnostic with PASS/FAIL and an exit code;
- a build entry point and packaging;
- VM deploy, diagnostic and smoke scripts;
- Editor tests;
- the Linux build;
- a run on `bg-dev-mission01`;
- narrow `CLAUDE.md` and audit-document updates;
- decision 045 and a run guide.

**Out of scope:**
- FishNet or any networking;
- accounts, APIs, PostgreSQL;
- inventory settlement redesign, reconnect;
- Docker, orchestration;
- Asset Studio;
- assembly restructuring;
- new packages;
- any change to client scenes or gameplay behaviour.

## 2. Starting point (observed 2026-10-11)

- **Editor and modules.**
  - Unity 6000.3.25f1.
  - The Linux Build Support (Mono) and Linux Dedicated Server Build Support modules were missing. With the owner's approval they were installed on 2026-10-11 through `Unity Hub.exe -- --headless install-modules --version 6000.3.25f1 -m linux-mono -m linux-server` (exit 0).
  - `PlaybackEngines/LinuxStandaloneSupport/Variations` now contains `linux64_server_nondevelopment_mono` and `linux64_server_development_mono`.
- **Player settings.**
  - `dedicatedServerOptimizations: 1` is already set.
  - `scriptingBackend` lists only Android, so the server target uses the default, Mono.
  - `bundleVersion: 0.1.0`.
- **The simulation already runs without a camera, HUD, input or presenter** (audit F3).
  - `MissionDirector` null-checks `camera`, `intelligence`, `presentation`, `inventory` and `roster`.
  - `MissionSpawner` adds `HostilePresenter` / `UnitPresentation` only when those systems are wired.
  - It always wires `selection`, `activeCharacter`, `encounter` and `coverRegistry`, and `CompanionAI` reads the active character.
- **The NavMesh bakes from physics colliders** (`MissionBuilder.cs:103`, `NavMeshCollectGeometry.PhysicsColliders`), so the server's stripping of render data cannot remove the bake's input.
- **There is a test seam on the hit roll.** `UnitAttacker.HitRoll` (internal, per instance) is the existing seam the tests use to force a hit or a miss.
- **The unit prefabs carry views** besides the simulation components:
  - `FriendlyUnit` has `SelectionIndicator`, `CommandQueueView`, `AttackLineView` and `HitFlash`;
  - `HostileUnit` has `AttackLineView` and `HitFlash`.
  - Neither prefab has an Animator or a model child.
- **Build output is ignored by git.** `.gitignore` already ignores `/[Bb]uilds/`.
- **The VM.**
  - `bg-dev-mission01` runs Ubuntu 24.04.5 LTS with kernel 6.8 on x86_64, with 4 vCPU, 7.9 GiB RAM and 77 GB of disk. `timeout`, `ps`, `awk`, `tar` and `sha256sum` are present.
  - It is reached only through the SSH alias. The repo never contains its address or any key file name.
  - The owner set the layout: `/opt/blackglass/server/` holds the complete compiled build and `/opt/blackglass/logs/` holds diagnostic logs and measurement output.

## 3. Design

### 3.1 Server scene and fixture

**`Assets/_Project/Scenes/ServerMission.unity`.** It is created and rewired idempotently by `Assets/_Project/Editor/ServerSceneBuilder.cs`, which follows the existing builder pattern:
- `Blackglass/Server/Build Server Scene` (menu);
- `BuildFromCommandLine` for `Tools/run-method.sh`.

The scene has three root objects and nothing else: no camera, light, EventSystem, audio, Canvas, HUD, input reader or debug view.

| Object | Components | Wiring |
|---|---|---|
| `Server` | `ServerBootstrap` | `director` → the Mission object's `MissionDirector`; `pause` → `Systems`' `TacticalPause` |
| `Systems` | `TacticalPause`, `Encounter`, `UnitSelection`, `ActiveCharacter`, `CoverRegistry`, `CoverDiscovery`, `InteractableRegistry` | `ActiveCharacter.Initialize(null, pause, selection)`; `CoverDiscovery.Initialize(registry, discoverAtStart: false)` |
| `Mission` | `MissionDirector` | `Initialize(fixture settings, friendly slots, hostile slots, systems, Ground.mat, Obstacle.mat, generateAtStart: false, randomSeedAtStart: false, theme: null)`; no loot table |

**`MissionSystems`:**
- **Wired:** `encounter`, `selection`, `activeCharacter`, `coverRegistry`, `coverDiscovery`, `pause` and `interactables`.
- **Left null:** `camera`, `abilityTargeting`, `roster`, `intelligence`, `inventory` and `presentation`.

**The fixture** (serialized in the scene, with its values held as constants in the builder):

| Setting | Value | Why |
|---|---|---|
| Grid | 2 × 2, cell 14, 3 rooms, corridor 3, loops 0 | small |
| Baffles, low cover | 1 per room, density 3 (defaults) | exercises cover generation and discovery |
| Friendly slots | 2 × `FriendlyUnit.prefab`: slot 0 `Ranged.asset`, slot 1 `Melee.asset`, no abilities | 2 friendlies exercise companion wiring; slot 0 is the diagnostic's ranged attacker (line of sight is checked) |
| Hostile slots | 1 × `HostileUnit.prefab`, no archetype; `hostileCount` 1, `guardCount` 0 | one target |
| Objectives | `eliminateHostiles` on, `hackTerminal` off, `extractionUnits` 1 | runtime has a required objective and no terminal |
| Separation, attempts | 16 m, 20 (defaults) | as shipped |
| Intelligence | default (fog off, no cameras) | the knowledge layer is out of scope |
| Seed | `ServerSceneBuilder.FixtureSeed` | see below |

**Choosing the fixture seed.** The seed is the lowest value in 1..1000 that meets both conditions:
1. the fixture generates on attempt 1;
2. the diagnostic passes in the Editor.

An Editor PlayMode probe finds it during implementation. The value is recorded as `FixtureSeed` and in decision 045.

### 3.2 `ServerOptions` (`Scripts/Server/ServerOptions.cs`, pure C#)

`ServerOptions.Parse(IReadOnlyList<string> args, out List<string> warnings)`.

| Argument | Default | Accepted |
|---|---|---|
| `-seed <int>` | none (use the scene's fixture seed) | any int |
| `-fps <int>` | 60 | 1..1000 |
| `-diagnostic` | off | flag |
| `-diagnosticTimeout <s>` | 120 | 5..3600 |
| `-startupTimeout <s>` | 60 | 5..3600 |
| `-heartbeat <s>` | 60 | 0 (off) or 5..3600 |

Parsing rules:
- Matching is exact and case-sensitive. Unknown arguments are ignored; Unity's own `-batchmode`, `-nographics` and `-logFile x` are among them.
- A missing or out-of-range value keeps the default and adds a warning.
- `Describe()` returns one line for the log.

### 3.3 `ServerBootstrap` (`Scripts/Server/ServerBootstrap.cs`, MonoBehaviour)

**Startup, in `Start`:**
1. Parse `Environment.GetCommandLineArgs()`. Log each warning.
2. Set `QualitySettings.vSyncCount = 0` and `Application.targetFrameRate = fps`.
3. Log the identity line, shown below.
4. Subscribe to `director.StateChanged`, then call `director.Generate(seed)`, using `-seed` or else `director.Settings.seed`, which is the fixture value.
5. Start the generation stopwatch.

**The log.** Every line is prefixed, so the run can be grepped:
```
[Server] identity product=Blackglass version=0.1.0 revision=<sha>[+dirty] built=<utc> buildGuid=<guid> unity=6000.3.25f1 platform=LinuxServer UNITY_SERVER=yes batchmode=yes graphics=Null
[Server] options seed=<n>(fixture|override) fps=60 diagnostic=yes|no diagnosticTimeout=120s startupTimeout=60s heartbeat=60s
[Server] generating seed=<n> <MissionSettings.Describe()>
[Server] MISSION READY instance=<id> seed=<n> attempt=<a>/<max> rooms=<r> navArea=<m2> paths=<p> cover=<t>(low <l>, tall <t>) friendlies=<f> hostiles=<h> layoutHash=<x> generationMs=<ms> navMeshBakeMs=<ms> sinceStartupS=<s>
[Server] self-check cameras=0 audioListeners=0 canvases=0 eventSystems=0 unitViewsDisabled=<n> unitViewsActive=0
[Server] heartbeat uptimeS=<s> fps=<f> cpuPct=<c> rssMB=<m> managedMB=<m> state=Ready phase=<p>
```

Where each identity field comes from:
- `revision` and `built` come from `build-info.txt` beside the executable, at `Path.GetDirectoryName(Application.dataPath)`. If the file is missing, as in the Editor, the log says `revision=unknown`.
- `UNITY_SERVER` comes from `#if UNITY_SERVER`.
- `graphics` is `SystemInfo.graphicsDeviceType`.

**On `MissionState.Ready`,** handled in the same `StateChanged` call:
1. Turn off the view components on every spawned unit instance by setting `enabled = false` on `AttackLineView`, `CommandQueueView`, `SelectionIndicator` and `HitFlash`. Their GameObjects, renderers and colliders stay.
2. Log the self-check counts with `FindObjectsByType` once.
3. Log `MISSION READY`.
4. If `-diagnostic` is set, call `BeginDiagnostic`.

**Startup failure** gives exit code 2. It covers three cases:
- `MissionState.Failed`: the log gives `director.Report.Failure`;
- the real-time startup deadline expires before Ready;
- `director` or `pause` is unwired.

**Heartbeat.** It runs only when the diagnostic is off and the interval is above 0. The interval is measured with `Time.realtimeSinceStartupAsDouble`. Each line reports:
- `fps`: frames ÷ elapsed real time;
- `cpuPct`: the change in `Process.TotalProcessorTime` ÷ elapsed time, as a percentage of one core;
- `rssMB`: `Process.WorkingSet64`, after `Refresh`;
- `managedMB`: `GC.GetTotalMemory(false)`.

If the process API is unavailable, the affected values are logged as `n/a`.

**Exit.** Exit goes through `internal Action<int> Exit`, whose default is `Application.Quit(code)`.
- The bootstrap logs `[Server] exit code=<n> reason=<text>` first.
- In the Editor, `Application.Quit` does nothing, and tests replace `Exit`.
- On `Application.quitting` the bootstrap logs `[Server] quitting`.

### 3.4 Diagnostic (`Scripts/Server/ServerDiagnostic.cs`)

The diagnostic is a plain C# step machine. Two methods drive it:
- `ServerBootstrap.BeginDiagnostic(ServerOptions, Action<int>)` creates it;
- `ServerBootstrap.Update` ticks it with `Time.realtimeSinceStartupAsDouble`.

Nothing in it runs unless `-diagnostic` is given. All deadlines use real time, so a paused or stalled simulation fails instead of hanging.

1. **Prepare.** This runs in the frame Ready is raised, before any think tick of the AI.
   - Select the units:
     - the mover is `director.Friendlies[0]`, which is the active character;
     - the target is the living hostile with the shortest complete NavMesh path from the mover;
     - if no hostile has a complete path, the diagnostic fails with `unreachable`.
   - Control the run. These changes apply to the spawned instances only; no asset is touched:
     - turn off `EnemyAI` and `AutoRetaliate` on every hostile;
     - turn off `CompanionAI` and `AutoRetaliate` on every friendly except the mover;
     - set `mover.Attacker.HitRoll = () => 0f`, so cover cannot turn a shot into a miss.
   - Log the target's `IsTargetInCover` state for the record.
2. **Move.**
   - Take the point 6 m along that path, snapped with `UnitMover.TrySnap`, and call `mover.Issue(new MoveCommand(point))`. If `Issue` refuses it, the diagnostic fails with `move refused`.
   - **PASS** needs all three:
     - `CurrentCommand == null`;
     - the flat distance to the point is ≤ 0.75 m;
     - the unit has travelled ≥ 3 m from its start.
   - The deadline is 30 s.
   - The PASS line logs the travelled distance and the time. The FAIL line logs the distance travelled, the distance remaining, `SimulationTime.IsRunning` and the current command.
3. **Attack.**
   - Record `target.Current` and `mover.Attacker.Hits`, then call `mover.Issue(new AttackCommand(target))`.
   - **PASS** needs both: `target.Current` has dropped, and `Hits` has risen, so the damage is attributed to the ordered unit.
   - The deadline is 45 s.
   - The PASS line logs health before and after, the shots fired and the time. The FAIL line logs the attack phase, the distance and line of sight.
4. **Finish.** `mover.Issue(new StopCommand())`. The diagnostic then logs one of these and exits:
   - `BLACKGLASS DIAGNOSTIC PASS moveS=<s> attackS=<s>` and exits 0;
   - `BLACKGLASS DIAGNOSTIC FAIL step=<move|attack|prepare|timeout> reason=<text>` and exits 1.
   - An overall `-diagnosticTimeout` counts from Ready and caps every step.

### 3.5 The one shared-code change

`MissionBuilder.Build` times `surface.BuildNavMesh()` with a `Stopwatch` and stores the result in `GeneratedMission.NavMeshBakeMs`. `MissionDirector.Fill` copies it into `MissionReport.NavMeshBakeMs`. Nothing else in shared gameplay code changes.

### 3.6 Build and packaging

**`Assets/_Project/Editor/ServerBuild.cs`** provides `Blackglass/Server/Build Linux Dedicated Server` and `BuildFromCommandLine`.

1. **Preflight.** If either check below fails, it reports the reason and exits non-zero without changing anything.
   - `BuildPipeline.IsBuildTargetSupported(Standalone, StandaloneLinux64)`, and `<GetPlaybackEngineDirectory>/Variations/linux64_server_nondevelopment_mono` exists.
   - `PlayerSettings.GetScriptingBackend(NamedBuildTarget.Server) == Mono2x`. The script never sets it.
2. **Remember** the active build target and subtarget.
3. **Clean** `Builds/LinuxServer/`.
4. **Build** with `BuildPipeline.BuildPlayer`:
   - scenes `[Assets/_Project/Scenes/ServerMission.unity]`;
   - target `StandaloneLinux64`;
   - subtarget `(int)StandaloneBuildSubtarget.Server`;
   - `BuildOptions.StrictMode`;
   - output `Builds/LinuxServer/BlackglassServer.x86_64`.
5. **On success:**
   - write `Builds/LinuxServer/build-info.txt` (`product`, `version`, `revision`, `dirty`, `builtUtc`, `unity`, `target=StandaloneLinux64`, `subtarget=Server`, `backend=Mono`);
   - `revision` and `dirty` come from `git rev-parse HEAD` and `git status --porcelain`, run as processes, falling back to `unknown`;
   - check that `BlackglassServer.x86_64` and `BlackglassServer_Data/` exist.
6. **Finally, always:** restore the remembered target and subtarget. Otherwise the Editor and `Tools/run-tests.sh` would reopen on the server target with `UNITY_SERVER` defined.
7. In batch mode it ends with `EditorApplication.Exit(0 or 1)`.

**`Tools/build-server.sh`** is the documented entry point. It runs in Git Bash, and the Editor must be closed.
1. Run Unity in batch mode with `-projectPath`, `-executeMethod Blackglass.EditorTools.ServerBuild.BuildFromCommandLine`, `-quit` and a log in `Logs/ServerBuild.log`.
2. Fail on a non-zero exit, a compile error or a missing output.
3. Copy `Tools/LinuxServer/*.sh` into `Builds/LinuxServer/`.
4. Create `Builds/blackglass-server-linux-<short-sha>[-dirty].tar.gz`, whose top-level folder is `LinuxServer/`.
5. Print the archive path, its size and its SHA-256.

### 3.7 VM deployment and scripts

**`Tools/deploy-server.sh [host] [archive]`** runs on Windows. The host defaults to `bg-dev-mission01` and the archive to the newest `Builds/blackglass-server-linux-*.tar.gz`.
1. `scp` the archive to `/tmp/`.
2. On the host:
   1. `mkdir -p /opt/blackglass/logs`;
   2. `rm -rf /opt/blackglass/server.new`;
   3. extract into `server.new` with `--strip-components=1`;
   4. `chmod +x` the executable and the `*.sh` files;
   5. check that the executable and `build-info.txt` exist;
   6. replace `server` with `server.new`, which is never half-replaced;
   7. delete the uploaded archive;
   8. print `build-info.txt`.

**`Tools/LinuxServer/run-diagnostic.sh [timeoutS]`** runs on the VM; the timeout defaults to 180.
- It runs `timeout <t> /opt/blackglass/server/BlackglassServer.x86_64 -batchmode -nographics -logFile - -diagnostic` and copies the output to `/opt/blackglass/logs/<UTC>-<rev>-diagnostic.log`.
- It prints the `[Server]`, `[Diagnostic]` and `BLACKGLASS` lines and exits with the server's exit code. `timeout` returns 124 on a hang.

**`Tools/LinuxServer/smoke-run.sh [seconds]`** runs on the VM; the duration defaults to 600.
1. Launch the server in normal mode with `-logFile <UTC>-<rev>-smoke.log`.
2. Every 5 s, append to `<UTC>-<rev>-smoke.csv`:
   - elapsed time;
   - CPU % for the interval, from the `/proc/<pid>/stat` utime and stime deltas ÷ `CLK_TCK`;
   - `VmRSS` and `VmHWM` from `/proc/<pid>/status`.
3. At the end, send SIGTERM, wait up to 30 s, then SIGKILL if the process is still alive, and record which happened.
4. Write `<UTC>-<rev>-smoke-summary.txt`:
   - launch to `MISSION READY`, in wall-clock seconds and as logged;
   - `navMeshBakeMs` and `generationMs`;
   - CPU % mean and maximum after Ready;
   - peak RSS (`VmHWM`);
   - steady RSS, the mean over the last 5 minutes;
   - RSS drift, the first steady sample against the last;
   - the heartbeat FPS range;
   - the error and exception line count;
   - the exit status.

The scripts use only bash, coreutils, procps and awk. The VM needs no packages, users or services.

### 3.8 Documentation

- **`CLAUDE.md`:** replace only the "Current Development Stage" and "Current Priority" sections with the text in Appendix A. "Project Goal" and every other section are unchanged.
- **`Docs/OnlineReadinessAudit.md`:**
  - §1–§8 stay as written.
  - One sentence under the opening labels list points to §9.
  - Append "§9 Follow-up record":
    - §9.1 sets out the step ladder: Checkpoint A (headless startup, no networking), then the networking experiment (§7's proof, which needs a library decision), then Phase 13 (backend and settlement).
    - §9.2 records the Checkpoint A results: the revision and environment actually tested; Editor tests run; the Linux build; the Linux runtime run with measurements; and which earlier [R] and [O] items it resolves (F3 server build unverified, F16 modules missing) and which remain unverified.
- **`Docs/Decisions.md`:** decision 045, "Dedicated-server startup path (Checkpoint A)". It records:
  - the separate scene;
  - the fixture and seed;
  - bootstrap-only stripping of view components;
  - the diagnostic's controls;
  - the exit codes;
  - restoring the build target;
  - one mission per process;
  - what was rejected: a server mode in `ProceduralMission`, and a code-only bootstrap;
  - the implications for the networking experiment.
- **`Docs/DedicatedServer.md` (new):**
  - prerequisites (modules, a closed Editor);
  - the build command and the artifact locations;
  - deploying;
  - the diagnostic and smoke commands, with the exact `ssh bg-dev-mission01 …` invocations;
  - the options table, log lines and exit codes;
  - how to read the measurements;
  - troubleshooting.

## 4. Verification and acceptance

The report keeps three kinds of evidence separate: **Editor**, **Linux build** and **Linux runtime**. Anything not exercised is labelled as such.

**Editor validation** (Windows host, `Tools/run-tests.sh`):

| # | Check |
|---|---|
| E0 | Fresh baselines are measured before any change, with full EditMode and PlayMode runs. |
| E1 | `ServerOptionsTests` (EditMode) cover the defaults, every argument, out-of-range and missing values (default kept, warning raised), Unity's own arguments being ignored, and `-heartbeat 0`. |
| E2 | `ServerMissionPlayModeTests` loads `ServerMission.unity` additively and checks:<ul><li>`MissionState.Ready` within 60 s real time, with the fixture seed and attempt 1;</li><li>outside the generated mission root, every MonoBehaviour belongs to the allow-list (§3.1);</li><li>the scene has 0 Camera, AudioListener, Canvas and EventSystem;</li><li>every unit view component is disabled;</li><li>`targetFrameRate == 60` (restored in teardown).</li></ul> |
| E3 | The same fixture with `BeginDiagnostic(defaults)`: `Exit(0)` within 120 s, with PASS logged.<br>The test calls `BeginDiagnostic` a frame or more after Ready, because the scene's `Start` has already parsed the Editor's own arguments. In production the Ready handler calls the same method, and that same-frame path is exercised by L2. The hostile's 12 m detection range sits inside the 16 m team separation, so the AI cannot act before the freeze in either case. |
| E4 | With `TacticalPause.Pause()` right after Ready and `-diagnosticTimeout 5`, `Exit(1)` follows within 10 s real time and the reason says timeout. |
| E5 | After generation, `MissionReport.NavMeshBakeMs > 0`. This is added to the existing `MissionDirectorPlayModeTests`. |
| E6 | The focused existing suites pass (`MissionDirectorPlayModeTests`, `ProceduralMissionSceneTests`, `MissionRuntimePlayModeTests`), and full EditMode and PlayMode runs equal the baseline plus the new tests, all passing. |
| E7 | `git diff` shows no change to `EditorBuildSettings.asset`, `ProceduralMission.unity`, `Prototype.unity` or any prefab or data asset. |

**Linux build** (Windows host):

| # | Check |
|---|---|
| B1 | `Tools/build-server.sh` exits 0. `Builds/LinuxServer/` contains `BlackglassServer.x86_64`, `BlackglassServer_Data/`, `build-info.txt` and the VM scripts. The archive and its SHA-256 are printed. |
| B2 | The build log shows the target `StandaloneLinux64` with the Server subtarget and the Mono backend. Afterwards the active build target is Windows Standalone again, checked by a short batch query. |

**Linux runtime** (`bg-dev-mission01`):

| # | Check |
|---|---|
| L1 | The deploy puts the build into `/opt/blackglass/server/`, and `build-info.txt` matches the archive's revision. |
| L2 | The diagnostic exits 0 within 180 s. Its log contains `UNITY_SERVER=yes`, `platform=LinuxServer`, `graphics=Null`, `MISSION READY`, the self-check zeros, Move PASS, Attack PASS and `BLACKGLASS DIAGNOSTIC PASS`. |
| L3 | **Smoke run (600 s, normal mode):**<ul><li>the process stays alive until SIGTERM;</li><li>no exception lines appear in the log;</li><li>every heartbeat shows FPS between 55 and 61.</li></ul>The summary records startup to Ready, the NavMesh bake time, CPU mean and maximum, peak and steady RSS, and drift. This is **initial evidence, not proof of long-term stability.** |

## 5. Risks and open points

- **Build-target switching is untried in this project.** The first switch to the Linux Server target re-imports assets for that target, which is slow once. Restoring the target afterwards is a hard requirement (B2).
- **Server stripping.** If the dedicated-server optimizations strip something the simulation reads, L2 will show it. The NavMesh source (physics colliders) is safe by construction.
- **The frame cap.** It assumes `targetFrameRate` is honoured in a dedicated-server player. L3's heartbeat FPS measures it.
- **Shutdown on SIGTERM** is untested. The smoke script records the exit status, and a SIGKILL fallback keeps it bounded.
- **The VM's idle memory** (about 0.8 GiB in use before any run) is recorded as the baseline, not attributed to the server.
- **Running as root** is accepted for this checkpoint. A dedicated service user belongs with the Docker and orchestration work.

## 6. Source control

- All work is on `checkpoint-a-linux-server`, in logically grouped commits.
- The owner's uncommitted `Assets/Settings/Mobile_RPAsset.asset` and `ProjectSettings/URPProjectSettings.asset` and the untracked `.claude/` are never staged.
- No build output, archive, log or VM measurement file is committed. They live in `Builds/`, `Logs/` (both git-ignored) and `.claude/scratchpad/checkpoint-a/`.
- Pushing and merging wait for the owner.

## Appendix A: replacement text for `CLAUDE.md`

> ## Current Development Stage
>
> Gameplay is complete through Phase 12.5 (combat presentation and VFX). The control loop, procedural missions, objectives, battlefield intelligence, persistent operatives, inventory and loot, the tactical HUD and combat presentation exist. `Docs/Decisions.md` records each decision.
>
> The owner is preparing an online foundation with an authoritative dedicated server; see `Docs/OnlineReadinessAudit.md`. The candidate stack (Unity Dedicated Server for Linux, FishNet, ASP.NET Core on .NET 10, PostgreSQL, Docker on isolated Linux VMs, HAProxy) is **provisional, not decided**.
>
> The step currently authorized is **Checkpoint A: Linux dedicated-server startup**. It is a headless server build that starts one fixture mission and proves navigation, movement and combat in the built server. It contains **no networking**.
>
> Each of these stays out of scope until the owner authorizes it:
> - networking libraries and client/server messaging;
> - accounts, backend APIs and databases;
> - settlement redesign and reconnect;
> - Docker packaging and orchestration;
> - final production content (graphics, animation, audio, story).
>
> Placeholder art remains the default.
>
> ## Current Priority
>
> Checkpoint A: a reproducible Linux Dedicated Server build (Mono) that starts one small fixture mission without a local player. It has a frame cap and identity and readiness logs, and a bounded diagnostic proves movement and combat through the existing command path.
>
> - Keep the server path free of input readers, cameras, HUD, debug views and cosmetic presentation.
> - Keep client scenes and gameplay behaviour unchanged.
>
> Later steps, each authorized separately: the networking experiment (audit §7), then Phase 13 (authenticated between-mission flow and trusted settlement).
>
> Do not begin later-stage systems until specifically directed.
