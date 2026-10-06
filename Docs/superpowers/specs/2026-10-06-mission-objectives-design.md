# Phase 9 — Mission objectives and extraction: design

Date: 2026-10-06. Status: approved in brainstorming (parts 1 and 2), spec awaiting owner review. Phase 10 is not started.

## 1. Goal and scope

A generated mission stops being "kill all enemies" and becomes a short, explicit flow:

Generate → place objectives → spawn squad and hostiles → begin → complete all required objectives → extraction opens → a living squad member reaches it → success. All controllable friendlies dead → failure.

This phase builds a small reusable mission/objective foundation and proves it with three objective types (eliminate hostiles, interact with a terminal, reach a zone = extraction). Out of scope (owner list): campaign, progression, XP, loot, inventory, equipment, rewards, economy, dialogue, narrative, hacking minigame, fog of war, reconnaissance, stealth, optional-objective UI, minimap, production waypoints, procedural story, save/load, multiplayer, final UI/art/audio, new packages.

Owner decisions taken in brainstorming (2026-10-06):

- **Controller Interact = context Confirm.** With the tactical cursor hidden, Confirm interacts when the active character stands next to an available terminal, otherwise it attacks the best hostile as today. No new pad button. Keyboard: new `Interact` action, key R.
- **Approach A:** plain C# runtime (`MissionRuntime`, objectives, placer) plus thin scene components; not MonoBehaviour objectives, not ScriptableObject definitions.
- **Lifecycle:** `MissionPhase` is new and separate from the director's generation `MissionState`; extraction opens in the same instant the last required objective completes, so "ObjectivesComplete" and "ExtractionAvailable" are one phase.
- **Placement** is a pure function of the layout with its own derived seed stream; the terminal is baked into the NavMesh geometry (so the objective-content step runs before the bake, a deliberate deviation from the owner's listed pipeline order).

Assumptions made (say so if wrong):

- The hand-built `Prototype` scene has no director and keeps the kill-all banner; only generated missions use the new result.
- Interaction progress is lost on cancel (no partial progress kept).
- One unit interacts at a time; a second unit arriving finds the terminal claimed and fails cleanly.
- The guards are 2 extra hostiles in the terminal room (fewer if fewer fit); they count as part of the "security team" the eliminate objective tracks.
- Prototype numbers: interaction range 1.8 m, duration 2 s, extraction radius 2 m, context-Confirm range 3 m.

## 2. Architecture

```
MissionDirector (existing, scene)        generation coroutine; owns Runtime; ticks it
  ├─ MissionGenerator (existing, pure)   seed → MissionLayout            (unchanged, golden hashes keep)
  ├─ ObjectivePlacer  (new, pure)        layout + settings + seed → ObjectivePlan (integer tiles)
  ├─ MissionBuilder   (existing)         layout → geometry + bake; takes an optional pre-bake content callback
  ├─ MissionContent   (new, scene)       terminal prop (pre-bake), extraction zone, markers, builds MissionRuntime
  ├─ MissionSpawner   (existing)         + extra hostile tiles (guards), + UnitInteractor on friendlies
  └─ MissionRuntime   (new, plain C#)    phase machine + objective list; Tick() from the director

Objectives (plain C#): MissionObjective ← EliminateHostilesObjective, InteractObjective, ReachZoneObjective
Scene components:      MissionInteractable (terminal), InteractableRegistry (Systems), ObjectiveMarker (visuals)
Unit side:             InteractCommand (data) → CommandableUnit → UnitInteractor (rules + progress)
Input side:            Interact action / context Confirm / click / cursor → PlayerCommandInput.Act → GroupOrders → Issue
UI (debug):            MissionHud (panel, banner, prompt, marker labels)
```

The mission layer consumes the generated layout and never builds rooms or walls. The terminal is a prop added to `Geometry` through a callback so `MissionBuilder` stays ignorant of objectives. Persistent systems are only reset and refilled; everything per mission lives under `GeneratedMissionRoot`.

## 3. Lifecycle

`MissionState` (Idle, Generating, Ready, Failed) is unchanged: it describes generation. New `MissionPhase`:

| Phase | Meaning |
|---|---|
| `Inactive` | no mission built (Idle, Generating, generation Failed) |
| `Active` | mission running, required objectives open |
| `ExtractionOpen` | every required objective Completed; the extraction zone is Active |
| `Success` | the extraction objective Completed (terminal) |
| `Failure` | no living friendly (terminal) |

The mission is `Active` the moment generation reaches Ready (no briefing). Success and Failure are terminal: later deaths, kills or zone entries change nothing, and all interactables are disabled. Failure is checked before success in the same tick (everyone dead is a failure). `MissionRuntime` exposes `Phase`, `Objectives`, `PhaseChanged` and `ObjectiveChanged`; all state is plain fields (state, flags, counters), nothing is kept in views, so a later save/load can serialise it.

## 4. Objectives

`MissionObjective` (abstract): `Id`, `Type` (`EliminateHostiles`, `Interact`, `ReachZone`), `Title`, `State` (`Inactive`, `Active`, `Completed`, `Failed`), `IsRequired`, `IsKnown`, `HasTarget` / `TargetPosition`, `Describe()`, `Activate()`, `Evaluate()`.

- **Exists vs known:** an objective in the list exists; `IsKnown` says whether the player is told (HUD line, marker, label). It defaults to true; nothing sets it false yet. Hidden, discovered, optional, prerequisite and alternate objectives later become flags and an `Inactive` start, not a rewrite. Nothing assumes every objective is visible and active from the start.
- **`EliminateHostilesObjective`:** tied to a list of `Health` (its group), not to `Encounter`. Completes when every member is dead; counts living members for the description (`Eliminate security team (2/5)`). A mission may therefore contain hostiles outside the group.
- **`InteractObjective`:** owns a `MissionInteractable`; `Activate` makes it available; its `Completed` event completes the objective.
- **`ReachZoneObjective`:** a centre, a radius and a required unit count. While Active it completes when at least that many living squad members are inside (flat distance). Entering while Inactive does nothing. Extraction is a `ReachZoneObjective` with `IsRequired = false` that starts Inactive (HUD: `[LOCKED]`).
- **`MissionRuntime`:** `Start()` activates every objective except those marked locked at start. `Tick()` evaluates in order, fails on a dead squad, opens extraction when all required objectives are Completed, succeeds when extraction Completed. Active objectives are re-derived from world state each frame, so the result cannot drift from the scene.
- **Victory:** `Encounter.Outcome` keeps working (the `Prototype` scene and its tests) but no generated-mission behaviour reads `Victory`; the HUD banner in generated missions comes from `MissionPhase`.

## 5. Procedural placement

`ObjectivePlacer.TryPlace(layout, settings, seed, attempt, out plan, out reason)` is pure: integer tiles, no scene access, EditMode-testable over hundreds of seeds. It runs after a successful `MissionGenerator.TryAttempt` and before `MissionBuilder.Build`. A placement failure fails the attempt, so retries cover it like any other failure.

- **Stream:** `SeededRandom` gets a second factory that mixes a fixed salt; `ForAttempt`'s output is unchanged (golden layout hashes stay valid). Placement uses only this stream, so combat randomness and layout draws cannot affect it.
- **Distances:** BFS over `layout.Connections` from `FriendlyRoom` (room-graph distance, as the spawn rule does).
- **Terminal room:** not the friendly room, graph distance ≥ ⌈max / 2⌉ ("deeper"), preferring rooms that contain at least one obstacle box (usable combat geometry). Rooms are tried in a seeded shuffled order.
- **Terminal tile:** a floor tile whose whole 5×5 neighbourhood is floor and covered by no box (two free tiles on every side, so a solid terminal never narrows a path to a gap too small for the NavMesh agent), and at least `SpawnSpacing` from every spawn tile. If a room has none, the next room is tried; if none has one the attempt fails.
- **Guards:** `guardCount` (default 2) extra hostile tiles in the terminal room: floor tiles more than 1 tile from every box, at least 2 tiles from the terminal, within 6 tiles of it, `SpawnSpacing` from every placed spawn, and `minTeamSeparation` from the friendlies. Fewer than requested is allowed (lightweight, not an encounter director).
- **Extraction:** a room other than the terminal room and the friendly room (with only two rooms, or as the last fallback, the friendly room is allowed); preferred at graph distance 1 to 2 from the terminal room so there is a walk but not a tedious one; a tile with a free 5×5 neighbourhood at least 4 m from every spawn tile and guard.
- **Result:** `ObjectivePlan` = terminal tile and room, guard tiles, extraction tile and room, plus its own FNV-1a `Hash` (tests: same seed twice equal, different seeds differ, golden seeds pinned, `UnityEngine.Random` untouched).
- **Reachability after the bake:** `MissionNavigation` additionally checks a complete NavMesh path from the first friendly spawn to the terminal's stand point and to the extraction centre; failure fails the attempt. The pure rules guarantee grid reachability; the NavMesh check guards the real mesh.

## 6. Interaction

- **`InteractCommand(MissionInteractable target)`:** plain data, a new `UnitCommand`. Real-time, tactical, queued (Append) and group use are the one `CommandableUnit.Issue` path; no player-only interaction logic exists. Input code never touches objective state.
- **`MissionInteractable`** (MonoBehaviour on the terminal): `IsAvailable`, `Range` (1.8 m), `Duration` (2 s, data), `Progress`, `User`, `StandPoint`, `Completed` event. `TryBegin(unit)`, `Advance(unit, dt)`, `Release(unit)` (resets progress). Available only while its objective is Active and it is not completed.
- **`UnitInteractor`** (component added to friendlies at spawn, like `UnitAbilities`): the capability rules, `InteractionFailure` reasons and `LastFailure`. A unit without it refuses Interact.
- **Order life in `CommandableUnit`:** `Issue` accepts when the unit is alive, has an interactor, the target exists and is available, and its stand point is reachable (`Mover.CanMoveTo`); appended orders get the static checks only. The order walks to the stand point, stops within range, faces the terminal, claims it and advances progress with `Time.deltaTime`; completion finishes the order and starts the next. Every frame it re-checks that the unit is alive, the target exists and is available and the mission is live; any failure releases the claim and ends the order quietly so the queue moves on and the unit never sticks. A walk that makes no progress for 3 s, or arrives out of range, gives up (the ability-walk rule).
- **Time:** `CommandableUnit.Update` already returns while simulation time is stopped, so progress freezes during tactical pause and resumes where it was.
- **Cancel:** Stop, a replacing order, held-key steering and death all call release: the claim is dropped and progress resets to zero.

## 7. Input

- New semantic action `Interact` (keyboard R) in the `Commands` map. It has no pad binding of its own: the pad's route is context Confirm. The pad-binding asset tests use an explicit action list, so no asset test changes are expected (to be verified in the plan).
- `InteractableRegistry` (on Systems, like `CoverRegistry`): the mission's interactables, emptied in `ResetSystems`, refilled per mission. It answers "nearest available interactable within r of a point".
- `PointerTargetKind.Interactable` + `PointerTarget.OnInteractable`. `PointerTargetResolver` classifies a collider under an available `MissionInteractable` as Interactable; `TacticalCursor` snaps to the nearest available one within about 1.5 m of the ground point, after friendlies and hostiles and before cover. `CommandResolver.Resolve` gains an interactable argument and returns `InteractCommand`. `PlayerCommandInput.Act` is otherwise unchanged, so a click or cursor Confirm works paused or running, with the queue modifier to append, for one unit or a selected group.
- Keyboard R and context Confirm run the same code: the nearest available interactable within 3 m of the active character, ordered to the units `OrderedUnits()` chooses (the queue modifier appends). An armed ability still takes precedence. No gameplay code names a device or a physical button; the prompt text comes from the bindings via `PromptResolver`.
- Selecting any friendly and clicking the terminal while paused queues Interact with Shift, e.g. MoveToCover → Attack → Interact → Move; the queue view and HUD unit label describe `InteractCommand`.

## 8. HUD and markers (debug quality)

- `MissionHud` (IMGUI): a `MISSION` panel (phase, then one line per known objective: `[x]`, `[ ]`, `[LOCKED]`, `[FAILED]`), a SUCCESS / FAILURE banner, the context prompt (`Cross: Interact` / `R: Interact`) when it applies, and a label at each known objective marker. It reads only `director.Runtime`. Plain `[x]` instead of a tick glyph because the IMGUI font may lack it.
- `PrototypeHud` suppresses its kill-all banner when a director is wired.
- World markers (under the mission root, so teardown is free): the terminal is a cube with a marker top that changes colour with its state (available, working, done, disabled); extraction is a flat disc plus a thin beacon, grey while locked and green when open. Marker materials are instances destroyed with their marker.

## 9. Extraction, victory and failure

Rule (prototype, configurable): at least `extractionUnits` (default 1) living squad members inside the active extraction zone completes the mission; units outside are ignored and nothing happens to abandoned units. Killing every hostile no longer ends anything unless the eliminate objective is the last required one, and even then extraction must still be reached. Failure is the squad dead, from any live phase; the runtime's failure check is the extension point for later failure conditions.

## 10. Regeneration

Every successful attempt builds a fresh `MissionRuntime`, interactable registry contents and root. `Teardown` first detaches and drops the old runtime (it unsubscribes, so nothing can fire into it), clears the registry in `ResetSystems`, then destroys the root as today (terminal, zone, markers, units). A failed attempt builds no runtime. Tests pin: the old runtime is not the new one and its objectives never change again; the registry holds exactly the new terminal; no objective, marker or interactable of the previous mission survives repeated F6/F7, including during combat, pause and an interaction in progress.

## 11. Files

New (`Scripts/Mission/`): `MissionPhase.cs`, `MissionObjective.cs` (+ the three types), `MissionRuntime.cs`, `ObjectivePlacer.cs`, `ObjectivePlan.cs`, `MissionContent.cs`, `MissionInteractable.cs`, `InteractableRegistry.cs`, `ObjectiveMarker.cs`, `MissionHud.cs`; `Scripts/Units/UnitInteractor.cs`; `Scripts/Commands/` gains `InteractCommand` in `UnitCommands.cs`; `Scripts/Input/PointerTarget.cs` and `PointerTargetResolver.cs` changes.

Changed: `MissionDirector` (runtime, tick, placement call, teardown), `MissionBuilder` (pre-bake callback), `MissionSpawner` (guards, `UnitInteractor`), `MissionNavigation` (objective path checks), `MissionSettings` (`guardCount`, `interactionSeconds`, `extractionUnits`, `eliminateHostiles`, `hackTerminal`; at least one required objective), `SeededRandom` (salted factory), `CommandableUnit`, `CommandResolver`, `PlayerCommandInput`, `TacticalCursor`, `PrototypeHud`, `CommandQueueView`, `BlackglassControls.inputactions`, the `ProceduralMission` scene (a temporary editor builder, deleted afterwards, as in Phases 7 and 8), `Docs/Decisions.md` (record 033).

## 12. Tests

EditMode: phase and objective state machines with fake world state; `ObjectivePlacer` determinism, hash, validity over hundreds of seeds (terminal on a 5×5 free floor, room rule, spacing, extraction room and clearance, guard rules), different seeds give different placements; `CommandResolver`, `PointerTarget`, `InteractCommand`, input asset (Interact binding, no device names in gameplay code), settings clamping.

PlayMode (Unity runs one instance, so these serialise): interaction (walk, range, 2 s duration, pause freezes it and resume continues, cancel by Stop / replace / steering / death, claimed or completed target, target destroyed), tactical queue ordering with move and attack around Interact, input from keyboard, context Confirm and the cursor (snap and Act), and a full scene flow on seed 12345: terminal before kills, kills before terminal, extraction refused while locked, extraction after unlock completes the mission, defeat, repeated regeneration cleanup. `Prototype` tests untouched.

## 13. Validation list mapping (owner items 1–35)

Items 1–5, 8–9, 17–18, 21–22 map to the placer and director tests; 6–7 and 28–32 to eliminate-objective tests plus the unchanged existing suites; 10–16 to interaction and input tests; 19–20, 23–24 to the scene flow tests; 25–27 to regeneration tests; 33–35 to the existing input suites, the full-suite run and a console check with no unexpected errors.

## 14. Known limitations and later work

- One interactable at a time per terminal and no partial progress; enemies are not objective-aware (no guarding behaviour beyond their spawn place); objective visibility is data only (`IsKnown`), nothing reveals or hides yet.
- Generation can now fail one more way (no valid terminal tile); the attempt-1 success rate will be re-measured in the plan.
- The terminal is solid geometry, so it exists in `Geometry` but is not a `CoverSurface` and offers no cover.
- Architecture concerns for Phase 10 are listed in the completion report (placement stays room-grid specific; the plan is not part of `MissionLayout`).
