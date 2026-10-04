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
- **Implications:** New top-level categories (for example `Scripts/`, `Prefabs/`, `Materials/`) are added under `Assets/_Project/` when the first real asset needs them. `Prefabs/` was added on 2026-10-04 for the first prefab (`FriendlyUnit`).

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

- **Decided:** Gameplay orders are small immutable objects (`MoveCommand`, `AttackCommand`, `StopCommand`, base `UnitCommand`) passed to `CommandableUnit.Issue(command, mode)`. The unit and its components (`UnitMover`, `UnitAttacker`) decide how to carry them out.
- **Queue (Phase 2, 2026-10-04):** each unit keeps a `CommandQueue`: the current order plus pending ones.
  - `IssueMode.Replace` (the default) drops all orders and starts the new one now. `IssueMode.Append` runs it after the pending ones, or now if the unit is idle.
  - `StopCommand` halts the unit and clears every order, whatever the mode.
  - `Issue` checks first and returns false without changing anything when an order can't be carried out: no walkable point within 2 m of a move destination, or a dead or inactive attack target.
  - When the current order finishes, the next pending one starts in the same frame. A pending order that can no longer start (its target already died) is skipped.
  - Tactical pause needs no special handling: orders issued while paused are queued normally and run once simulation time moves.
- **Groups:** `GroupOrders.Issue(units, command, mode)` gives one order to several units. Moves are spread over a hexagonal lattice 1.5 m apart (`GroupMoveOffsets`); a slot off the NavMesh falls back to the clicked point.
- **Direct control (Phase 3, 2026-10-04):** `CommandableUnit` also takes a held **move intent** (`SetMoveIntent(direction)`), which is not a command. While it is non-zero and simulation time runs, the unit clears all of its orders (current and pending, as Stop does) and steers with `UnitMover.Steer`. Orders and direct control share `UnitMover` and `UnitAttacker`; nothing else drives them. An order issued while the intent is held is accepted, then dropped on the next simulation frame (keys win while held).
- **Why:** Player input, groups, AI and scripts all produce the same objects without knowing movement or combat rules. Commands stay testable and loggable. Keeping the queue inside `CommandableUnit` keeps one entry point for orders.
- **Rejected:** Classic `Execute(unit)` / `Begin/Tick/Cancel` commands, which would put long-running chase/attack logic into command classes. Direct methods on the unit (`MoveTo`, `Attack`), which leave no command value to queue or send from AI. A separate queue component feeding `Issue`, which would mean two entry points coordinating by polling.
- **Implications:** Interact will be a new `UnitCommand` subclass with cases in `CommandableUnit`. Weapon choice (a later phase) would be data on `AttackCommand`. There is no change event; debug views read `CurrentCommand`/`PendingCommands` each frame.

## 007 — Tactical pause owns time scale

- **Decided:** `TacticalPause` is the only code that writes `Time.timeScale` (0 when paused, the previous value on resume). Simulation code (unit orders, NavMesh movement, attack cooldowns, hit flash) uses scaled time and stops. Camera, input and debug UI use unscaled time or input callbacks and keep working. Unit orders don't advance while simulation time is frozen, but commands are accepted while paused.
- **Why:** A single owner keeps pause behaviour predictable and lets it evolve, for example into slow motion or per-system clocks, without hunting down scattered `timeScale` calls.
- **Implications:** Any new system must choose scaled or unscaled time deliberately. Systems reference `TacticalPause` through the Inspector; it's not a singleton.
- **Pause frame (Phase 3, 2026-10-04):** `Time.deltaTime` still holds the previous frame's value on the frame `Pause()` is called, so a scaled-time check alone lets one more frame of simulation through. `UnitMover.Steer` therefore also returns while `Time.timeScale <= 0` (it only reads the time scale). `CommandableUnit.Update`'s order processing still checks only `deltaTime`, so orders can advance one frame on the pause frame; a shared "is simulation running" check is the fix if that ever matters.

## 008 — Input mapping and click vs drag

- **Decided:** Project-wide actions live in `Assets/_Project/Input/BlackglassControls.inputactions`, with three maps:
  - **Camera:** Pan (WASD), Rotate (Q/E), RotateDrag (right button), PointerPosition, Zoom (wheel).
  - **Commands:** Command (left button), PointerPosition, TogglePause (Space), Modifier (Shift), Stop (X), ClearSelection (Esc).
  - **Character** (Phase 3, 2026-10-04): Move (WASD), Takeover (V), CycleCharacter (Tab) and CycleReverse (Shift; held with Tab it cycles backward, see 012).
- **WASD is shared** by `Camera/Pan` and `Character/Move`. The mode decides which one acts: while the active character is being driven (takeover on, not paused) the camera ignores Pan, and otherwise `DirectControlInput` ignores Move. **Shift is shared** by `Commands/Modifier` and `Character/CycleReverse`: each map owns its own action so no component enables or disables another's.
- **Left button** (Phase 2, 2026-10-04): a quick click (≤ 6 px of movement) acts on what is under the cursor:
  - a friendly unit (`SelectableUnit`) is selected; with Shift it is added or removed;
  - anything else is an order: Attack on a living `Health`, otherwise Move. Shift queues it instead of replacing. **In real time the order goes to the active character; while paused (or with no active character that can act) it goes to the selected units** (Phase 3, 2026-10-04).
  - A left-drag box-selects the roster units inside the box (Shift adds; an empty box clears).
- **Right button** is camera-only: right-drag rotates (horizontal) and tilts (vertical, 25°–85°). `ClickDragDetector` holds the click-versus-drag rule for both buttons.
- **Why:** The owner chose left-click commands (2026-10-04), then chose to keep them when selection arrived: one button for "act on what I click", the other for the camera. This is also the convention of party-based tactical-pause games.
- **Consequences:** Clicking the ground is Move, so it can't clear the selection; Esc does. Clicking a friendly always selects it, so a move can't be ordered onto a spot where a friendly stands.
- **Implications:** Input components only create commands, change the selection, call `TacticalPause`, set the active character's move intent through `CommandableUnit.SetMoveIntent`, toggle `ActiveCharacter` takeover, or ask `ActiveCharacter` to cycle. They never touch `UnitMover`, `UnitAttacker`, the NavMeshAgent or `Health`. Rebinding means editing the actions asset. The keys 1–9 are still free; they may be wanted for both weapon choice and control groups later.

## 009 — Code assemblies and tests

- **Decided:** Runtime code lives in the `Blackglass` assembly (`Assets/_Project/Scripts`). Tests are in `Blackglass.Tests.EditMode` (pure logic) and `Blackglass.Tests.PlayMode` (runtime behaviour, plus simulated input via `InputTestFixture`). Components expose `internal Initialize(...)` for test wiring. Scenes wire them through serialized fields. Tests run in batch mode via `Tools/run-tests.sh`, with the Unity Editor closed.
- **Why:** Separate assemblies keep compile times short and dependencies explicit. Automated tests cover the control loop so later changes can't silently break it.
- **Implications:** New gameplay code gets EditMode tests for pure rules and PlayMode tests for runtime behaviour.
- **Gotcha:** PlayMode tests that load a scene containing input components must derive from `InputTestFixture`, load the scene inside the test (not in `[UnitySetUp]`), and destroy the scene's objects before `base.TearDown()`. Otherwise their input-action state leaks into later fixtures (see `PrototypeSceneTests`).

## 010 — Unit selection

- **Decided:** `UnitSelection` (a component on `Systems`, wired through the Inspector) holds the selected units and the **roster** of units the player controls. `SelectableUnit` marks a unit the player may select and carries its `IsSelected` state; `SelectionIndicator` shows a ring. Selection holds state only: `PlayerCommandInput` decides what to select, and while paused, orders go to the selected units through `GroupOrders` (in real time, ground and enemy clicks order the primary character; see 011). Disabled or destroyed units drop out of the selection.
- **Why:** The roster gives box selection its candidate list without `Find*` lookups, singletons or a static registry. Keeping selection separate from commands lets AI and scripts order units without any selection.
- **Rejected:** Finding units with `FindObjectsByType` at drag time (global lookup, against 007/009 wiring rules). A static registry of selectable units (global mutable state). Storing selection on the input component (the HUD and later systems need to read it).
- **Implications:** Units spawned at runtime call `UnitSelection.AddToRoster`. Enemies and AI units are `CommandableUnit`s without `SelectableUnit`. Control groups, if wanted, would be saved lists of roster units.

## 011 — Primary character and takeover mode

- **Renamed (2026-10-04, see 012):** `PrimaryCharacter` is now `ActiveCharacter`, and Tab/Shift+Tab can change which friendly it holds. Below, "primary character" means the active character.
- **Decided (Phase 3, 2026-10-04):** One friendly unit is the **primary character** (`FriendlyUnit_1` in `Prototype.unity`). `PrimaryCharacter` (on `Systems`) holds which unit it is and whether **takeover mode** is on. Like `UnitSelection`, it holds state only.
  - **Free mode** (default): WASD pans the camera.
  - **Takeover mode** (V toggles): WASD drives the primary character (camera-relative, through `DirectControlInput` → `CommandableUnit.SetMoveIntent`), and the camera follows it on unscaled time. V works while paused and takes effect on resume.
  - **Real-time clicks** (both modes): ground and enemy clicks order the primary character; Shift queues.
  - **Tactical pause:** the camera is free (WASD pans) and clicks order the selection, which may include the primary character (unchanged from Phase 2).
- **Precedence (primary character only):**
  1. Orders run through the queue as in Phase 2.
  2. Any WASD input while driving clears all of its orders, moves and attacks alike.
  3. Keys win while held.
  4. Releasing them leaves the character idle.
  5. Companions are never affected.
  6. After resume, or after takeover is turned on, keys already held are ignored until Move reads zero (the release gate), so a key held from panning cannot wipe a plan.
- **Why:** One owner (`CommandableUnit`) decides who moves a unit, so direct control and queued orders cannot fight over the NavMeshAgent. The two modes keep WASD for the camera during planning without a separate RTS mode.
- **Rejected:**
  - A separate `ManualControl` component that also drives the mover: two writers to the agent, and precedence split across files.
  - Repeatedly issuing short `MoveCommand`s while keys are held: pathfinding lag, and a command stream the brief ruled out.
  - Cancelling only the current order on takeover: the next queued move would start and fight the keys.
- **Implications:**
  - AI or scripts can use the same move intent later.
  - Switching the active character at runtime is designed in 012. What happens when it dies is not designed yet; friendly units have no `Health`.
  - If "keys win while held" feels wrong in play, the alternative is that a click suspends the keys until they are released.

## 012 — Active character switching

- **Decided (2026-10-04):** The directly controlled character is no longer fixed. `PrimaryCharacter` was renamed `ActiveCharacter` (file renamed with its `.meta`, so the script GUID and scene wiring survived; renamed fields kept their data through `[FormerlySerializedAs("primary")]`). Any **eligible** roster unit can be the active character:
  - eligible = in `UnitSelection.Roster`, `SelectableUnit` and `CommandableUnit` enabled, GameObject active in the hierarchy, and no `Health` or a living one (`ActiveCharacter.IsEligible`);
  - **Tab** makes the next eligible unit active and **Shift+Tab** the previous one, in roster order, wrapping (`ActiveCharacter.Cycle` → `ControlCycle.NextIndex`). With one eligible unit it stays active; with none nothing changes;
  - Tab works in every mode, paused or not. It changes state only: no orders are issued or cleared and no simulation runs.
- **Handover:** `DirectControlInput` zeroes the old unit's move intent in the same frame. A held move key carries over to the new unit only if it has no orders; otherwise the release gate re-arms, so tabbing onto a unit cannot wipe its plan. The old unit keeps whatever orders it had.
- **Selection follows:** Tab replaces the selection with the new active character, so paused Tab-then-click orders the character just chosen.
- **Camera:** when the active unit changes, the camera glides once to where that unit stands at the switch (same easing as follow, unscaled time); any pan input ends the glide. The point is captured, not re-read, so a glide never turns into a follow in free mode and survives the unit being destroyed. While driving it follows as before.
- **Feedback:** one `ActiveMarker` (`ActiveCharacterMarker`) floats above whoever is active, distinct from the selection ring at the feet. The HUD line names the controlled character.
- **Input:** `Character/CycleCharacter` (Tab) and `Character/CycleReverse` (Shift), both read only by `DirectControlInput`.
- **Why:** the control code should not assume a fixed protagonist; squad members are interchangeable for control. Consumers (input, camera, marker) compare `ActiveCharacter.Unit` with the unit they last saw each frame instead of subscribing to a change event, which catches every source of change and keeps `ActiveCharacter` state-only (as in 006).
- **Rejected:**
  - Separate `NextCharacter`/`PreviousCharacter` actions with a Shift+Tab composite: needs the project-wide "shortcut keys consume input" setting so Shift+Tab does not also fire Tab.
  - Reading `Commands/Modifier` from `DirectControlInput`: two components enabling and disabling one shared action, so disabling either would switch the other's Shift off.
  - Carrying a held key over unconditionally (wipes the new unit's orders) or never (a hitch on every mid-run switch).
  - Leaving the selection unchanged on Tab: paused clicks would order someone other than the character just tabbed to.
- **Implications:** No automatic switch when the active character dies. Cycling order is roster order. A per-unit "cannot be directly controlled" flag waits for a design need. Right-click stays camera-only; `ClickDragDetector` already separates a right-click from a right-drag, so a future context action can use the click without changing camera control.
