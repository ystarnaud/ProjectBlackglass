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
- **Pause frame (Phase 3, 2026-10-04; closed in Phase 4):** `Time.deltaTime` still holds the previous frame's value on the frame `Pause()` is called, so a scaled-time check alone lets one more frame of simulation through. Since Phase 4 every simulation `Update` (`CommandableUnit`, `UnitMover.Steer`, `EnemyAI`, `HitFlash`, `AttackLineView`) checks `SimulationTime.IsRunning` (scaled delta time **and** time scale above zero), so no order, hit or AI step runs on the pause frame (see 013).

## 008 — Input mapping and click vs drag

- **Decided:** Project-wide actions live in `Assets/_Project/Input/BlackglassControls.inputactions`, with three maps:
  - **Camera:** Pan (WASD), Rotate (Q/E), RotateDrag (right button), PointerPosition, Zoom (wheel).
  - **Commands:** Command (left button), PointerPosition, TogglePause (Space), Modifier (Shift), Stop (X), ClearSelection (Esc).
  - **Character** (Phase 3, 2026-10-04): Move (WASD), Takeover (V), CycleCharacter (Tab) and CycleReverse (Shift; held with Tab it cycles backward, see 012).
- **WASD is shared** by `Camera/Pan` and `Character/Move`. The mode decides which one acts: while the active character is being driven (takeover on, not paused) the camera ignores Pan, and otherwise `DirectControlInput` ignores Move. **Shift is shared** by `Commands/Modifier` and `Character/CycleReverse`: each map owns its own action so no component enables or disables another's.
- **Left button** (Phase 2, 2026-10-04): a quick click (≤ 6 px of movement) acts on what is under the cursor:
  - a friendly unit (`SelectableUnit`) is selected; with Shift it is added or removed;
  - anything else is an order: Attack on a living `Health`, otherwise Move. Shift queues it instead of replacing. **The order goes to the selected units whenever any are selected or the game is paused; with nothing selected in real time it goes to the active character** (Phase 3 sent every real-time click to the active character; changed 2026-10-04 after Phase 4 because the owner wants real-time squad orders back, see 011).
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
  - **Real-time clicks** (both modes): ground and enemy clicks order the selection when anything is selected, otherwise the active character; Shift queues. (Until 2026-10-04 they always ordered the active character; the owner found that losing real-time group orders felt like a regression, and one rule for paused and real-time clicks is simpler. The empty-selection fallback keeps "click moves my character" in solo play; Tab selects the character it switches to, so right after a switch the two agree; clicking another friendly selects it without changing who is driven, and Esc restores click-moves-my-character. Rejected: a modifier key for group orders (one more chord to learn) and ordering the selection only when it contains the active character (a selected companion alone would then be unorderable in real time).)
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
  - Switching the active character at runtime is designed in 012; what happens when it dies, in 013 (control passes to the next eligible friendly, or to nobody).
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
- **Implications:** Since Phase 4 the active character's death hands control to the next eligible roster unit (013). Cycling order is roster order. A per-unit "cannot be directly controlled" flag waits for a design need. Right-click stays camera-only; `ClickDragDetector` already separates a right-click from a right-drag, so a future context action can use the click without changing camera control.

## 013 — Health, death and the simulation clock

- **Decided (Phase 4, 2026-10-04):** `Health` is the one hit-point implementation for every unit, friendly or hostile (max, current, `TakeDamage(amount, attacker)`, `Damaged`, `AttackedBy`, `Died`). At zero it raises `Died` once and deactivates its GameObject; a `DeathMarker` leaves a flat grey disc where the unit fell. `CommandableUnit` listens to its own `Health`: on death it clears every order and stops, and `Issue` returns false while dead (`IsAlive`). Every existing check (`IsAttackable`, `UnitSelection.CanSelect`, `ActiveCharacter.IsEligible`, `HasUnit`, the HUD) already treats an inactive unit as gone, so death needs no further plumbing.
- **Simulation clock:** `SimulationTime.IsRunning` is the single "may simulation advance this frame" test, true when scaled delta time and time scale are both above zero. It closes the pause-frame leak noted in 007.
- **Controlled character dies:** `ActiveCharacter.RefreshEligibility` (run every frame) moves control to the next eligible roster unit in roster order, wrapping, or to nobody. Takeover mode and the selection are left alone; the dead unit drops out of the selection on its own. With no unit, `HasUnit` is false: WASD pans, no intent is set, clicks order the selection, the HUD says "Controlled: none". `ActiveCharacter` runs at `DefaultExecutionOrder(-200)`, before `DirectControlInput` (-100), so a death never shows as a one-frame `HasUnit` dip that would re-arm the direct-control release gate; a held key carries over to an idle survivor (012 rules unchanged).
- **Why:** one hit-point type keeps the two sides symmetric; deactivating on death reuses the checks the rest of the code already makes; a single clock check is cheaper to reason about than per-component time rules.
- **Rejected:** keeping the corpse as the unit with components disabled (more code and edge cases: clicks on corpses, agents blocking paths); a separate `Damaged` event carrying the attacker (two events is simpler than breaking `HitFlash` and tests); switching selection on death (would wipe a group selection mid-fight).
- **Implications:** a future revive would need the corpse approach instead of deactivation. Corpse markers accumulate until the scene reloads. New simulation components must check `SimulationTime.IsRunning`, not `Time.deltaTime`.

## 014 — One combat path, four deciders

- **Decided (Phase 4, 2026-10-04):** The only way anything attacks is `CommandableUnit.Issue(new AttackCommand(target))`. `CommandableUnit` approaches, repositions when a ranged unit is blind (017), stops in range and faces; `UnitAttacker` applies range, cooldown and damage on scaled time and raises `Attacked`. Four deciders feed it: player clicks (`PlayerCommandInput`, unchanged), `EnemyAI` (015), `CompanionAI` (018, Phase 5) and `AutoRetaliate`: a unit hit while it has **no orders** and no held move intent attacks its attacker. Any order wins (a Move away is a retreat); held WASD wins. Both prefabs carry `AutoRetaliate`.
- **Sides:** `Encounter` (on `Systems`) holds two serialized lists of `Health`, friendlies and hostiles, and computes `Outcome` on read: all friendlies dead → Defeat (checked first), all hostiles dead → Victory, empty sides never resolve. Enemy AI reads friendlies from it; the HUD reads the outcome. No faction system.
- **Attack timing:** damage only in `UnitAttacker.TryAttack`, never per frame; cooldown uses `Time.time`, so it freezes while paused.
- **Why:** player, AI and retaliation share every rule about range, timing, death and target validation, so a fix lands once. `Encounter` keeps "who is on which side" in one place without a faction system the prototype does not need.
- **Rejected:** friendly-only retaliation (a hostile hit from a blind spot would wait for its think tick); a per-unit "hold fire" flag (a future `UnitCommand` if ever wanted); using `UnitSelection.Roster` as the friendly side (ties enemy AI to selection and rules out allied units the player does not control); events on `Encounter` (views read state each frame, as in 006).
- **Implications:** Interact and ranged weapons go through `Issue` the same way. A team concept later replaces the two lists in `Encounter`. `AttackLineView` and `HitFlash` are the only hit feedback and are debug-only.

## 015 — Enemy awareness

- **Decided (Phase 4, 2026-10-04):** `EnemyAI` idles until a living friendly is within its detection radius (12 m) **and** in line of sight (`UnitAttacker.HasLineOfSight`, built on `LineOfSight`, 016: one ray from the hostile's eye at 1.5 m to the friendly's centre, blocked by any collider without a `Health` in its parents; units never block sight) **and reachable** (`UnitMover.CanReach`, a complete NavMesh path; added in Phase 5, 2026-10-04). It then issues an `AttackCommand` and leaves the rest to `CommandableUnit`; when that order ends (target dead, unreachable) it looks again on its next think tick (0.25 s, scaled time). Line of sight gates **acquisition only**: a target once taken is followed around corners until it dies. State (`Idle`, `Chase`, `Attack`, `Dead`) is derived from the unit's order, range and health, never stored.
- **Why:** the radius and sight check make approach, positioning and pause matter without a perception system; chasing without re-checking sight avoids oscillation at corners; deriving state keeps one source of truth.
- **Rejected:** always aggressive (every run opens the same way); losing the target when sight breaks (oscillation); target memory, leash, retargeting to a closer unit, cones, alert states (out of scope).
- **Implications:** hiding works before you are spotted, not after. An unreachable friendly is not a target (until Phase 5 it was re-chased every tick). Perception can grow inside `EnemyAI.FindTarget` without touching combat. Sight checks read the physics scene, which only picks up colliders created or moved at runtime after the next physics step (auto sync is off), so geometry spawned at runtime is invisible to hostiles for a frame; the scene's walls are static and unaffected. The sight ray keeps at most 8 hits, so a line crossing more than 8 colliders could miss a wall.

## 016 — Combat roles and line of sight

- **Decided (Phase 5, 2026-10-04):** `UnitAttacker` carries a `CombatRole` (`Melee`, `Ranged`) next to its range, damage and cooldown; scene instances override the prefab values. `LineOfSight` is the one sight test: a ray from an eye (pivot + 0.5 m, so 1.5 m up) to the target's pivot, blocked by any collider without a `Health` in its parents; units never block. `UnitAttacker.HasLineOfSight`/`CanAttack` wrap it for the unit's own attacks, and `TryAttack` refuses a ranged hit without sight, so no caller can shoot through a wall. Melee ignores sight for hitting; both roles use it for acquisition (`EnemyAI`). A ranged unit stops approaching once in range and never backs away.
- **Why:** two roles and three numbers do not justify a data asset or class system; one sight function shared by attacks, firing positions and enemy acquisition means a change lands once; gating the hit itself keeps the rule out of every decider.
- **Rejected:** a `ScriptableObject` per role or a class system (speculative data); sight checks per caller (duplicated, drift); kiting or minimum range (out of scope, keeps melee hostiles dangerous).
- **Implications:** weapons later become data that fills the same three fields plus the role. The 8-hit buffer cap carries over from 015. Sight is one ray to the pivot, so a half-exposed unit is either seen or not.

## 017 — Repositioning

- **Decided (Phase 5, 2026-10-04):** an attack order runs in three phases inside `CommandableUnit` (`AttackPhase`): **Approach** while out of range (unchanged chase: re-path when the target moves 0.5 m, arriving out of range means unreachable), **Reposition** while a ranged unit is in range but blind, **Attack** otherwise. Reposition searches at most twice a second (`RepositionInterval` 0.5 s) among 16 candidates around the unit's ground point (`FiringPositionFinder`: 8 compass points at 2 m and 4 m, nearest ring first), keeping the first that snaps to the NavMesh (the 2 m `SnapRadius`), is in range and in sight from the eye a unit would have there, and is reachable; the unit walks to the snapped point. A unit walking to a validated firing position commits to it and finishes the walk even if the line clears early, so it ends clear of the corner rather than on its edge; only the walk-at-the-target fallback stops the moment sight returns (ruled during implementation when units stopped on a corner's edge). A walk that has not arrived after 3 s is searched again. After three repositions without landing a hit, or when no candidate is valid, the unit **walks at the target** until the line clears; at a reachable target's position sight is trivially clear. If that walk ends with the unit still blind (partial path), or no walkable point lies within 2 m of the target, the order **ends** as Approach's does for an unreachable target, so every attack order terminates. Sight is re-checked every frame, so the fallback walk ends as soon as the line clears (a walk to a validated spot finishes first).
- **Why:** the brief asked for a modest, predictable search; the fallback plus the two unreachable exits bound the loop without a planner; keeping the phases in `CommandableUnit` means every decider (player, enemy, companion, retaliation) gets the behaviour for free.
- **Rejected:** candidates around the target or scored by cover (a cover system is Phase 6 or later); searching every frame (16 snaps, rays and paths per blind unit); giving up when no candidate is valid (a ranged unit would stand uselessly behind a wide wall); a 1 m snap radius (candidates in the 0.5 m erosion band beside walls would be rejected exactly where repositioning is needed).
- **Implications:** the search may pick an exposed spot, or one on the far side of an obstacle (nearest by ring, not by travel); cover value would plug into the candidate predicate. The fallback has no standoff, so a ranged unit may end up firing from melee range. `EnemyState` gained `Reposition`, derived from the phase.

## 018 — Companion autonomy

- **Decided (Phase 5, 2026-10-04):** `CompanionAI` (friendlies only, wired to `ActiveCharacter` and `Encounter`, `DefaultExecutionOrder(-150)`) checks every simulation frame whether it is the controlled character (then it stops its own order once and does nothing else) and otherwise thinks every 0.25 s with this priority: **dead** (nothing), **controlled**, **orders** (a current order it did not issue, or anything pending: left alone), **assist** (attack a hostile within 10 m that is the controlled character's target or is already attacking someone; the leader's target first, then the nearest engaged; acquisition only, a running assist is never retargeted), **follow** (beyond 6 m of the controlled character, move to 3.5 m from it on the companion's own side; a running follow move is re-aimed once the leader has moved 2 m and only while the companion is still farther than 3.5 m, so a leader walking into it never pushes it back; the point is snapped to the NavMesh first, else the leader's position), **idle**. It tells its own orders from everyone else's by **remembering the command object it issued**; `Replace` by anyone, `Append` (pending orders), `Stop`, death and direct control all read as "not ours". It only ever `Replace`s or `Stop`s its own order. `CompanionState` is derived on read.
- **Why:** the brief's priority model, implemented as two small methods; command identity needs no change to command data (006) and treats a retaliation like an explicit order (never interrupted by following); requiring an engaged target means companions never start a fight the player did not; the two follow distances and the no-backstep rule stop pacing; running the controlled check before `DirectControlInput` keeps 012's "a held key carries over to an idle unit" true on Tab.
- **Rejected:** a `Source` tag on `UnitCommand` (project-wide change for one consumer); attacking any visible hostile (wakes sleeping enemies); retargeting a running assist to the leader's new target (flip-flopping between engaged hostiles); formations or slots (out of scope); a leash on assist (the player can always order the companion back); interrupting the current follow move when the player queues behind it (the queue semantics of 006 are kept: the queued order runs after it).
- **Implications:** a Shift-queued order waits for a short follow move. Autonomy never overrides explicit orders, even for self-preservation. The controlled character has no autonomy at all; Tab moves that status with it. Later AI (scripts, allied NPCs) can reuse the same component or the same "own command" trick. A player's Stop (X) on a companion more than 6 m from the controlled character is followed by a follow move on the next tick, because there is no hold-position order; a `HoldCommand` is a Phase 6 candidate if the owner wants companions to stay put. Tab onto a companion that is mid-assist stops its own attack as well as a follow move (only its own orders, never the player's). Because acquisition requires reachability, a ranged hostile or companion will not shoot a visible, in-range target standing where no path leads, although an explicit attack order on it would work; revisit when the arena gains islands.
