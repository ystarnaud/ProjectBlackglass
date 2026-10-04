# Tactical Squad Control (Phase 2) — Design

Date: 2026-10-04 · Branch: `prototype/tactical-squad` · Status: draft, awaiting owner review

Builds on the Phase 1 control loop (`2026-10-04-prototype-control-loop-design.md`) and decisions 006–009 in `Docs/Decisions.md`.

## 1. Goal

Prove the tactical-control model with several controllable units:

**Input → Selection → Command → Selected Unit(s) → Command Queue → Movement / Combat**, with tactical pause freezing execution while planning continues.

At the end of the phase, in `Prototype.unity`, the player can:

1. See three friendly units.
2. Select one unit, or several (Shift + click, box drag).
3. See which units are selected.
4. Order the selected units to move (spread over small offsets) or to attack the dummy.
5. Pause, issue one or more orders per unit, and watch them run in order after resuming.
6. Stop units, which clears their orders.

## 2. Scope

**In scope:** selection, a per-unit command queue, Stop, group move offsets, debug visuals, the scene update, automated tests, decision records.

**Out of scope:**
- **Deferred to their own phase (owner decision, 2026-10-04):** weapons and weapon choice (for example 1/2 for primary/secondary), and any "press a key, then click a target" targeting mode.
- **Not in this phase:** formations, smarter slot assignment, control groups, factions or teams, AI, Interact, inventory, equipment, progression, quests, dialogue, procedural generation, production UI, save/load, multiplayer, cover, line of sight, animation, final art or audio, ECS/DOTS, third-party packages.

## 3. Decisions made during brainstorming

| Topic | Decision |
|---|---|
| Mouse mapping | Left-click does select **and** command, depending on what is under the cursor. The right button stays camera-only (decision 008 is kept). |
| Clearing selection | **Esc**. An empty box drag also clears it. Clicking the ground is Move, so it cannot clear selection. |
| Pause semantics | Pause only freezes simulation time. Issuing rules are identical paused or not: a plain click replaces a unit's orders, Shift appends. |
| Queue location | Inside `CommandableUnit`, backed by a plain C# `CommandQueue`. `CommandableUnit` stays the single entry point for orders. |
| Command lifecycle | Commands stay plain data (decision 006). No `Begin/Tick/Cancel` methods on command classes; the unit runs them. |
| Stop | A `StopCommand` that clears the current and pending orders and halts the unit. It is never queued. Key **X** (S is camera pan). |
| Modifier | One `Modifier` action (Shift) meaning "add to selection" on a friendly unit and "queue" on a command. |
| Box selection | Included. Left-drag was already reserved for it (decision 008). |
| Friendly units | A unit is player-selectable when it has a `SelectableUnit` component. |
| Unit asset | A `FriendlyUnit` prefab, the project's first prefab. |

## 4. Architecture

All runtime code stays in the `Blackglass` assembly and namespace. No new assembly references.

### 4.1 Commands — `Commands/`

`UnitCommands.cs` gains:

```csharp
public sealed class StopCommand : UnitCommand { }
public enum IssueMode { Replace, Append }
```

`MoveCommand` and `AttackCommand` are unchanged.

**`CommandQueue`** (new, plain C#, no Unity API):

- `UnitCommand Current` (null when idle), `IReadOnlyList<UnitCommand> Pending`.
- `Replace(cmd)`: `Current = cmd`, pending cleared.
- `Append(cmd)`: if idle, `Current = cmd`; otherwise added to the end of `Pending`.
- `Clear()`: current and pending emptied.
- `Advance()`: the first pending command becomes `Current`, or `Current` becomes null.
- Null commands are rejected with `ArgumentNullException`.
- It only records order. It never moves or validates anything.

**`GroupOrders`** (new, static): turns one command into orders for several units. Usable by player input, AI and scripts.

- `Issue(IReadOnlyList<CommandableUnit> units, UnitCommand command, IssueMode mode)`.
- **Move:** unit *i* receives `MoveCommand(destination + offsets[i])`. If that unit rejects the offset point (not walkable, for example inside a wall), it is given `MoveCommand(destination)` instead.
- **Attack, Stop:** every unit receives the same command instance. Commands are immutable, so sharing is safe.
- Null entries in `units` are skipped. An empty list does nothing.

**`GroupMoveOffsets`** (new, static, pure): `Vector3[] Compute(int count, float spacing)`.

- Index 0 is `Vector3.zero` (the clicked point).
- Following indices fill hexagonal rings around it on the XZ plane: ring *r* holds 6·*r* points at distance *r*·`spacing`. Three units use the centre and two points of the first ring.
- Default spacing: 1.5 m (agent radius is 0.5 m).
- Slots are assigned in selection order. There is no nearest-slot matching.

### 4.2 Unit — `Units/`

**`CommandableUnit`** (changed):

```csharp
bool Issue(UnitCommand command, IssueMode mode = IssueMode.Replace)
UnitCommand CurrentCommand { get; }
IReadOnlyList<UnitCommand> PendingCommands { get; }
event Action CommandsChanged;   // raised after any change to current or pending
```

- **Validation at issue time** (as in Phase 1): a Move needs a walkable NavMesh point within 2 m (`UnitMover.CanMoveTo`); an Attack needs a target that is alive and active. A rejected command returns false and leaves current and pending orders untouched.
- `null` throws `ArgumentNullException`; an unknown type throws `ArgumentException`. Unknown `IssueMode` values throw `ArgumentOutOfRangeException`.
- **Stop:** halts the mover, clears current and pending, returns true. The mode is ignored.
- **Replace:** cancels the current order (stops the mover, ends any chase), replaces the queue, and starts the new command immediately. Phase 1's special case stays: replacing with an attack on the *current* attack target keeps the chase going instead of restarting it (pending orders are still cleared).
- **Append:** if the unit is idle, the command starts immediately; otherwise it is added to the end of the pending list.
- **Starting a command:** Move calls `UnitMover.MoveTo`. Attack stops the mover and lets the per-frame logic chase and strike, as in Phase 1.
- **Per frame** (`Update`, only while `Time.deltaTime > 0`):
  - Move completes when `UnitMover.HasArrived`.
  - Attack runs the Phase 1 chase/attack logic and completes when the target is dead or inactive, or when no path to it exists.
  - When the current order completes, the unit advances to the next pending order in the same frame. If that order can no longer start (the target already died, the destination is no longer walkable), it is skipped and the unit tries the next one.
- **Pause:** nothing special. A command issued while paused becomes current or pending at once. Nothing moves until simulation time runs again.

**`UnitMover`** (changed):

- New `bool CanMoveTo(Vector3 point)`: true when the agent is on a NavMesh and a walkable point exists within 2 m. No side effects, no warning.
- **`HasArrived` fix** (Phase 1 spec §10): true when the path is not pending and either the agent has no path, or the **remaining distance along the path** is at most stopping distance + 0.1 m. For a destination the NavMesh only partially reaches, this becomes true at the end of the partial path, so a queued move can never block the queue forever.

### 4.3 Selection — `Selection/` (new folder)

**`SelectableUnit`** (new component, requires `CommandableUnit`):

- `CommandableUnit Unit`, `bool IsSelected { get; internal set; }`, `event Action<bool> SelectionChanged` (raised only on change).
- Marks a unit the player may select. Enemies or AI units are `CommandableUnit`s without it.

**`UnitSelection`** (new component on `Systems`, referenced through the Inspector):

- `[SerializeField] List<SelectableUnit> roster`: the units the player controls. Box selection tests against it. Units spawned later call `AddToRoster(unit)`.
- `IReadOnlyList<SelectableUnit> Selected` (ordered by selection time), `event Action Changed` (raised only on change).
- `Select(unit)` replaces the selection with one unit. `Toggle(unit)` adds or removes one. `Add(units)` adds several. `SetSelection(units)` replaces with several. `Clear()` empties it.
- Every operation keeps each unit's `IsSelected` in sync.
- Units that are destroyed or disabled are dropped from `Selected`.
- Holds state only. It reads no input and issues no commands.

**`ScreenBox`** (new, static, pure): builds a normalized screen rectangle from two corners and tests whether a screen point (in front of the camera) lies inside it.

**`SelectionIndicator`** (new presentation component on the unit): shows or hides a child ring object when its `SelectableUnit` changes.

### 4.4 Input — `Controls/`

**Input actions** (`BlackglassControls.inputactions`, Commands map):

| Action | Type | Binding | Status |
|---|---|---|---|
| Command | Button | `<Mouse>/leftButton` | kept (same action ID) |
| PointerPosition | Value Vector2 | `<Pointer>/position` | kept |
| TogglePause | Button | `<Keyboard>/space` | kept |
| Modifier | Button | `<Keyboard>/leftShift`, `<Keyboard>/rightShift` | new |
| Stop | Button | `<Keyboard>/x` | new |
| ClearSelection | Button | `<Keyboard>/escape` | new |

The Camera map is unchanged: right-drag rotates and tilts, right-click alone does nothing.

**`PlayerCommandInput`** (changed): the serialized `CommandableUnit controlledUnit` becomes `UnitSelection selection`, and the new action references are added.

- **Left click** (≤ 6 px movement, `ClickDragDetector`): raycast from the camera as in Phase 1.
  - Hit a collider whose parents include a `SelectableUnit`: plain → `selection.Select(unit)`; Shift → `selection.Toggle(unit)`.
  - Any other hit: `CommandResolver.Resolve(health, point)` (unchanged), then `GroupOrders.Issue(selected units, command, Shift ? Append : Replace)`. With nothing selected, nothing happens.
  - No hit: nothing.
- **Left drag:** on release, the roster units whose screen positions lie inside the box are selected. Plain → `SetSelection` (an empty box clears). Shift → `Add`. While dragging, `IsDragging` and `DragRect` (screen rectangle in GUI coordinates) are exposed for the HUD.
- **X:** `GroupOrders.Issue(selected units, new StopCommand(), Replace)`.
- **Esc:** `selection.Clear()`.
- **Space:** `tacticalPause.Toggle()`.
- It never touches movement, combat, health or transforms.

`CommandResolver` is unchanged.

### 4.5 Debug visuals — `DebugUI/`

All of these only read state. None reads `Time.deltaTime`, so they behave the same paused or not. None has a collider, so none can block a click raycast.

- **Selection ring:** child of each unit, a flat cylinder (about 1.4 m wide) with `SelectionRing.mat` (green), toggled by `SelectionIndicator`.
- **`CommandQueueView`** (per unit, `LineRenderer`): a line from the unit through each order in sequence, current order first. A Move contributes its destination and a small flat marker disc (`MoveMarker.mat`, yellow). An Attack contributes the target's current position. Rebuilt on `CommandsChanged`; the unit end and attack points are refreshed in `LateUpdate`. Shown for every unit with orders. Markers are pooled children created on demand.
- **`UnitDebugLabel`** (per unit, IMGUI): a label above the unit, such as `Move +2` (current order type plus pending count). Hidden when idle.
- **`PrototypeHud`** (changed): new control hints, a "Selected: N" line, and the drag box while `PlayerCommandInput.IsDragging`. The pause banner and dummy HP line are unchanged.
- **New materials** (URP Unlit): `SelectionRing.mat` (green), `QueueLine.mat` (white), `MoveMarker.mat` (yellow).

## 5. Scene and assets

- **Prefab** `Assets/_Project/Prefabs/FriendlyUnit.prefab`: blue capsule (`Unit.mat`), `NavMeshAgent`, `UnitMover`, `UnitAttacker`, `CommandableUnit`, `SelectableUnit`, `SelectionIndicator` with its ring child, `CommandQueueView` (`LineRenderer`, `QueueLine.mat`), `UnitDebugLabel`.
- **`Prototype.unity`:**
  - `PlayerUnit` is replaced by `FriendlyUnit_1`, `FriendlyUnit_2`, `FriendlyUnit_3` (prefab instances) about 2 m apart around the old spawn point (−10, 1, −10).
  - `Systems` gains `UnitSelection` with the three units in its roster.
  - `PlayerCommandInput` and `PrototypeHud` are re-wired.
  - Nothing is selected at start.
  - The NavMesh is not re-baked: units are outside `Environment`.
- The scene and prefab are built by a **temporary editor script** run in batch mode, deleted afterwards and not committed (as in Phase 1).

## 6. Testing and validation

Tests are written first, following decision 009. Tests are run in batch mode with `Tools/run-tests.sh`.

**EditMode:**
- `CommandQueue`: replace, append when idle and busy, clear, advance, order preserved, null rejected.
- `GroupMoveOffsets`: count 0, 1, 3, 7, 8; index 0 at centre; points distinct; nearest-neighbour distance ≥ spacing; negative inputs rejected.
- `UnitSelection`: select, toggle on and off, add, set, clear; `IsSelected` flags; `Changed` only on change; destroyed or disabled units dropped.
- `ScreenBox`: inside, outside, edges, reversed corners, points behind the camera.
- `CommandableUnit.Issue`: null rejected, unknown command type rejected.

**PlayMode (test world, no input):**
1. Two appended moves run in order: the unit reaches the first destination before the second.
2. Appending while paused: nothing moves for 1 s real time; after resume, the orders run in order.
3. Replace clears pending orders.
4. Stop clears current and pending and the unit stays put.
5. A queued Attack then Move: the dummy dies, then the unit walks to the move destination.
6. A queued Attack on a target that died before its turn is skipped.
7. A move to a walled-off point (on the NavMesh, but unreachable) completes, and the next queued order starts.
8. `GroupOrders` move of three units: all arrive at distinct points near the destination; a unit not in the list receives nothing.

**PlayMode (simulated input, `InputTestFixture`):**
- Click a friendly: it alone is selected. Shift + click another: both are selected. Shift + click again: removed.
- Box drag around two units selects exactly those two. An empty box clears. Esc clears.
- Click the ground: only selected units get a Move. With nothing selected, nothing happens.
- Click the dummy: selected units get an Attack.
- Shift + click: the order is appended.
- X: selected units stop and their orders are cleared.
- Orders clicked while paused wait until resume.
- Right-click issues nothing (kept from Phase 1). Space toggles pause (kept).

**Scene (`Prototype.unity`):** three wired friendly units, `UnitSelection` with all three in the roster; select-then-attack works from start to finish; the scene runs for 1 s without logged errors (any logged error fails a test).

**Phase 1 tests:** updated only where they assume a single controlled unit (`PlayerCommandInput.Initialize` signature, `PlayerUnit` name).

**Validation checklist** (the owner's 13 items): 1–6, 9–12 by automated tests; 7–8 by the pause tests plus manual check; 13 by the scene test plus manual play.

**Manual checks for the owner:** selection feel, ring and line readability, group spread, crowding around the dummy, HUD legibility.

## 7. Files

- **Created:**
  - `Scripts/Commands/CommandQueue.cs`, `GroupOrders.cs`, `GroupMoveOffsets.cs`
  - `Scripts/Selection/SelectableUnit.cs`, `UnitSelection.cs`, `ScreenBox.cs`, `SelectionIndicator.cs`
  - `Scripts/DebugUI/CommandQueueView.cs`, `UnitDebugLabel.cs`
  - `Prefabs/FriendlyUnit.prefab`
  - `Materials/SelectionRing.mat`, `QueueLine.mat`, `MoveMarker.mat`
  - new EditMode and PlayMode test files
- **Changed:**
  - `Scripts/Commands/UnitCommands.cs`, `Units/CommandableUnit.cs`, `Units/UnitMover.cs`, `Controls/PlayerCommandInput.cs`, `DebugUI/PrototypeHud.cs`
  - `Input/BlackglassControls.inputactions`, `Scenes/Prototype.unity`
  - existing tests as noted in section 6
  - `Docs/Decisions.md`

(Paths above are under `Assets/_Project/`.)

## 8. Decision records (`Docs/Decisions.md`)

- **006 update:** per-unit queue inside `CommandableUnit`, `IssueMode`, `StopCommand`, skip-on-start rule, `GroupOrders`.
- **008 update:** left-click selects or commands depending on the target; Shift modifier; X stop; Esc clear selection; right button still camera-only.
- **010 (new) — Selection:** `UnitSelection` component with an explicit roster; `SelectableUnit` marks player units; no singletons or `Find*` lookups; selection holds state only.
- **003 note:** `Prefabs/` added for the first prefab.

## 9. Known limitations, accepted for the prototype

- No formations. Offset slots follow selection order, so paths may cross.
- Units crowding the dummy rely on the NavMesh agents' built-in avoidance.
- A unit pushed away from its exact destination by other agents may take a moment to settle; there is no stall timeout.
- The queue view shows every unit's plan, which may get busy with many units.
- Clicking a friendly always selects it, so a move cannot be ordered onto a spot where a friendly stands.
- No factions: friendly units have no `Health` and cannot be attacked; the dummy is the only target.
- No weapon choice (deferred to its own phase).
