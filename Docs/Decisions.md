# Architecture Decisions

Short record of decisions that are likely to matter later. Newest last.

## 001 — Engine version: Unity 6.3 LTS (6000.3.25f1)

- **Decided:** Create the project with Unity 6.3 LTS, 6000.3.25f1 (installed via Unity Hub, 2026-10-03).
- **Why:** Current LTS stream, supported until December 2027. Input System and URP development is focused on Unity 6.
- **Rejected:** 2022.3.46f1 (already installed, but its free LTS support has ended; we would have to upgrade soon). Unity 6.0 LTS (support ends October 2026). 6000.6 (newer, but not an LTS release).
- **Implications:** Everyone working on the project needs exactly this Editor version (see `ProjectSettings/ProjectVersion.txt`). Upgrade only deliberately, as its own change.
- **Known issue:** Unity 6 requires Windows 10 21H1 (build 19043) or newer. The current development machine runs Windows 10 2004 (build 19041). The Editor works but prints an "unsupported OS" warning at every launch. Built Windows players ran here without any warning. Updating to Windows 10 22H2 meets the minimum. Windows 10 itself has been out of mainstream support since October 2025, so Windows 11 is the longer-term option.

## 002 — Render pipeline: URP

- **Decided:** Universal Render Pipeline, set up from Unity's "Universal 3D" project template.
- **Why:** Unity's default for new 3D projects. The Built-in pipeline is being deprecated, so starting there would mean migrating materials and lighting later.
- **Rejected:** Built-in (deprecated); HDRP (heavier and aimed at high-end visuals we don't need).
- **Implications:** Use URP shaders (`Universal Render Pipeline/Lit`) for placeholder materials. Pipeline assets live in `Assets/Settings/`, where the template put them.

## 003 — Repository and folder layout

- **Decided:** The Unity project sits at the repository root. All of our own assets go under `Assets/_Project/`. Folders are created only when something goes in them.
- **Why:** The root layout is the Unity convention and matches the standard Unity `.gitignore`. One project folder keeps our content separate from Unity-generated and third-party folders (`Settings/`, `Plugins/`, imported packages). Empty folders cause churn: Git doesn't track them, and Unity deletes their orphaned `.meta` files.
- **Rejected:** A `UnityProject/` subfolder (only useful if the repo will hold non-Unity code). A large pre-made folder tree (speculative).
- **Implications:** New top-level categories (for example `Scripts/`, `Prefabs/`, `Materials/`) are added under `Assets/_Project/` when the first real asset needs them.

## 004 — Line endings

- **Decided:** `* text=auto` for everything. On top of that, the formats Unity writes as text (`.unity`, `.prefab`, `.asset`, `.meta`, `.inputactions`, `.asmdef`, `.json`, …) get `eol=lf`, so they stay LF in the working tree too. Those rules set only `eol` and never force `text`.
- **Why:** This machine's Git has `core.autocrlf=true`, and Unity writes LF. Without the rules, these files check out as CRLF, and once Unity re-saves them they show as modified even though nothing changed. `text` is not forced because some `.asset` files are always binary (LightingData, NavMesh, TerrainData). Forcing `text` would make Git rewrite CRLF bytes inside them and corrupt them on commit. That was reproduced in a test repo.
- **Implications:** C# scripts and other hand-edited files follow each machine's `autocrlf` setting (CRLF on Windows). When a new Unity text format appears, add an `eol=lf` line for it. Git LFS is not used yet. Add it deliberately when large binary art arrives.

## 005 — Package baseline

- **Decided:** Keep the URP template's packages that serve the prototype: URP, Input System, Test Framework, uGUI, and the Rider/Visual Studio integrations. Remove the template extras: Timeline, Visual Scripting, Unity Version Control (`collab-proxy`), Multiplayer Center. Built-in engine modules stay at Unity's defaults. Versions match what Unity 6.3 recommends.
- **Why:** `CLAUDE.md` asks for no unnecessary packages. Each removed package can be added back with one line in `Packages/manifest.json`. Pruning built-in modules would save little and cause confusing missing-type errors later.
- **Implications:** `com.unity.ai.navigation` 2.0.14 was re-added on 2026-10-04 for prototype unit movement (NavMeshAgent + a baked NavMeshSurface). `Packages/manifest.json` also lists `com.unity.inputsystem` under `testables`, which is test-only and exposes `InputTestFixture`. Test runs must therefore filter to our assemblies (`Tools/run-tests.sh` does this).
- **Input:** The project uses the Input System package only. The legacy Input Manager is disabled (Player Settings → Active Input Handling). The project-wide actions asset is `Assets/_Project/Input/BlackglassControls.inputactions`, which replaced Unity's template asset and kept its GUID (see 008).

## 006 — Unit commands are data; units execute them

- **Decided:** Gameplay orders are small immutable objects (`MoveCommand`, `AttackCommand`, base `UnitCommand`) passed to `CommandableUnit.Issue`. The unit and its components (`UnitMover`, `UnitAttacker`) decide how to carry them out. A new command replaces the current one. `Issue` returns false and keeps the current order when a command can't be carried out: there is no walkable point within 2 m of a move destination, or an attack target is dead or inactive.
- **Why:** Player input, a future command queue, RTS selection and AI can all produce the same objects without knowing movement or combat rules. Commands stay testable and loggable.
- **Rejected:** Classic `Execute(unit)` commands, which would put long-running chase/attack logic into command classes. Direct methods on the unit (`MoveTo`, `Attack`), which leave no command value to queue or send from AI.
- **Implications:** Stop and Interact will be new `UnitCommand` subclasses with a case in `CommandableUnit.Issue`. A queue would store `UnitCommand`s and feed them to `Issue`.

## 007 — Tactical pause owns time scale

- **Decided:** `TacticalPause` is the only code that writes `Time.timeScale` (0 when paused, the previous value on resume). Simulation code (unit orders, NavMesh movement, attack cooldowns, hit flash) uses scaled time and stops. Camera, input and debug UI use unscaled time or input callbacks and keep working. Unit orders don't advance while simulation time is frozen, but commands are accepted while paused.
- **Why:** A single owner keeps pause behaviour predictable and lets it evolve, for example into slow motion or per-system clocks, without hunting down scattered `timeScale` calls.
- **Implications:** Any new system must choose scaled or unscaled time deliberately. Systems reference `TacticalPause` through the Inspector; it's not a singleton.

## 008 — Input mapping and click vs drag

- **Decided:** Project-wide actions live in `Assets/_Project/Input/BlackglassControls.inputactions`, with two maps: **Camera** (Pan WASD, Rotate Q/E, RotateDrag right button, PointerPosition, Zoom wheel) and **Commands** (Command right button, PointerPosition, TogglePause Space). A quick right-click (≤ 6 px of movement) issues the context command on release: Attack on a living `Health`, otherwise Move. A right-drag rotates the camera instead. `ClickDragDetector` holds that rule. Left-click is unused, reserved for selection.
- **Why:** RTS convention, and one shared rule keeps the camera and command input consistent without coupling them.
- **Implications:** Input components only create commands or call `TacticalPause`. They never touch movement or combat. Rebinding means editing the actions asset.

## 009 — Code assemblies and tests

- **Decided:** Runtime code lives in the `Blackglass` assembly (`Assets/_Project/Scripts`). Tests are in `Blackglass.Tests.EditMode` (pure logic) and `Blackglass.Tests.PlayMode` (runtime behaviour, plus simulated input via `InputTestFixture`). Components expose `internal Initialize(...)` for test wiring. Scenes wire them through serialized fields. Tests run in batch mode via `Tools/run-tests.sh`, with the Unity Editor closed.
- **Why:** Separate assemblies keep compile times short and dependencies explicit. Automated tests cover the control loop so later changes can't silently break it.
- **Implications:** New gameplay code gets EditMode tests for pure rules and PlayMode tests for runtime behaviour.
- **Gotcha:** PlayMode tests that load a scene containing input components must derive from `InputTestFixture`, load the scene inside the test (not in `[UnitySetUp]`), and destroy the scene's objects before `base.TearDown()`. Otherwise their input-action state leaks into later fixtures (see `PrototypeSceneTests`).
