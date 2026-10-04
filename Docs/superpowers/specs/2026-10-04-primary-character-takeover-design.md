# Primary Character and Takeover (Phase 3) — Design

Date: 2026-10-04 · Branch: `prototype/primary-takeover` · Status: design approved by the owner in conversation 2026-10-04; written spec awaiting review

Builds on Phase 1 (`2026-10-04-prototype-control-loop-design.md`), Phase 2 (`2026-10-04-tactical-squad-control-design.md`) and decisions 006–010 in `Docs/Decisions.md`.

## 1. Goal

Prototype the game's defining control model and find out whether it feels good:

- **Real time:** the player plays a **primary character**. Clicks make it move or attack; in **takeover mode** WASD drives it directly and the camera follows it.
- **Tactical pause:** simulation freezes, the camera is free, friendly units (the primary character included) can be selected and given queued orders.
- **Resume:** orders run. Manual input can take the primary character back at any time without touching companions' orders.

Two control sources, one set of unit capabilities:

```
WASD/V ─► DirectControlInput ─► PrimaryCharacter (state)
                 │ SetMoveIntent            ▲ read by camera, HUD, click input
Clicks ─► PlayerCommandInput ─► Issue(command)
                 ▼                          ▼
              CommandableUnit (queue + move intent; decides precedence)
                 ├─► UnitMover  (MoveTo for orders, Steer for keys)
                 └─► UnitAttacker
```

## 2. Scope

**In scope:** the primary character, takeover mode, direct WASD steering, real-time click orders to the primary character, camera follow, debug feedback, scene update, automated tests, decision records.

**Out of scope:** inventory, loot, equipment, weapons, stats, skill trees, abilities, quests, dialogue, save/load, procedural generation, companion AI (follow, formations, tactics, threat, autonomous targeting, cover), stealth, perception, production UI, animation, final art, audio, multiplayer, ECS/DOTS, new packages, first-person or over-the-shoulder cameras, camera collision, switching which unit is the primary character at runtime.

## 3. Decisions made during brainstorming

| Topic | Decision |
|---|---|
| Real-time modes | Two: **free mode** (default; WASD pans the camera, as now) and **takeover mode** (WASD steers the primary character, the camera follows it). |
| Takeover key | **V**, toggles takeover on and off. Common "switch view" key. The camera stays the existing orbit camera; takeover is not a first-person view. |
| Real-time clicks | Left-click on the ground or an enemy orders the **primary character** (move / attack), in both real-time modes. Shift queues. Left-click on a friendly still selects it. Box selection still works. |
| Paused clicks | Unchanged from Phase 2: ground and enemy clicks order the **selection**. |
| Direct attack | Clicking an enemy issues the existing `AttackCommand` to the primary character (it runs over and hits). No new combat code. |
| Takeover implementation | **Approach A:** a held move intent on `CommandableUnit`, carried out by a new `UnitMover.Steer`. `CommandableUnit` stays the only driver of `UnitMover`. Rejected: a separate `ManualControl` component that also drives the mover (two writers to the agent, precedence split across files); throttled `MoveCommand`s ahead of the character (pathfinding lag, against the brief). |
| Takeover cancels | **All** of the primary character's orders, current and pending, move and attack (same as Stop). Cancelling only the current order would start the next queued move and fight the keys. |
| Keys versus clicks | Keys win while held. Kept simple; to be revisited after playtesting (§9). |
| Camera in takeover | Follows the primary character in real time. While paused it is free (WASD pans), and eases back on resume. |

## 4. Architecture

All runtime code stays in the `Blackglass` assembly and namespace. No new assembly references, no new packages.

### 4.1 Unit — `Units/`

**`UnitMover`** (changed) — new method:

```csharp
/// Moves the unit this frame in a horizontal direction at its normal speed, turning toward it.
public void Steer(Vector3 direction)
```

- Uses scaled time (`Time.deltaTime`), so it does nothing while paused.
- Flattens `direction` to the ground plane and clamps its length to 1. A zero direction does nothing.
- Drops any active path and leftover velocity first (`ResetPath` plus `velocity = Vector3.zero`, as `Stop` does), so it never fights a path or coasts. A unit whose last move just finished can still have both.
- Moves with `NavMeshAgent.Move(direction * speed * Time.deltaTime)`, which keeps the unit on the NavMesh and slides it along walls.
- Turns the transform toward the direction at the agent's angular speed (`Quaternion.RotateTowards`).
- Does nothing if the agent is not on a NavMesh.
- No acceleration or deceleration: movement starts and stops at once.

**`CommandableUnit`** (changed):

```csharp
/// Sets the direction the unit is being steered in by direct control. Zero means none.
public void SetMoveIntent(Vector3 direction)
public Vector3 MoveIntent { get; }
```

- The intent is held state, not a command: it stays until it is set again. It is flattened and clamped to length 1.
- In `Update`, only while `Time.deltaTime > 0` (unchanged guard):
  - **Intent non-zero:** if the unit has any order, clear them all exactly as Stop does (`StopAll`). Then call `UnitMover.Steer(intent)`. Orders are not processed this frame.
  - **Intent zero:** process orders exactly as in Phase 2.
- `Issue` is unchanged. An order issued while the intent is non-zero is accepted and then cleared on the next simulation frame (keys win, rule 3 in §5).
- Any unit can take an intent. Only `DirectControlInput` sets one, and only on the primary character.

`UnitAttacker` is unchanged.

### 4.2 Player control — `Controls/`

**`PrimaryCharacter`** (new component on `Systems`, wired through the Inspector like `UnitSelection`). Holds state only: it reads no input and moves nothing.

```csharp
public CommandableUnit Unit { get; }        // the primary character; set in the Inspector
public bool IsTakeoverOn { get; }           // false at start
public void SetTakeover(bool on)
public void ToggleTakeover()
public bool HasUnit { get; }                // Unit != null && Unit.enabled && Unit.gameObject.activeInHierarchy
public bool IsPaused { get; }               // from its TacticalPause reference; false if none
public bool IsDriving { get; }              // IsTakeoverOn && !IsPaused && HasUnit
internal void Initialize(CommandableUnit unit, TacticalPause pause)
```

- Takeover can be toggled while paused. It takes effect on resume.
- Takeover starts **off**: the game starts in free mode.
- `HasUnit` is the single meaning of "there is a primary character", used by `IsDriving` and by click routing. It uses `enabled` + `activeInHierarchy` rather than `isActiveAndEnabled` (the same check as `UnitSelection.CanSelect`), so it gives the same answer in EditMode tests.

**`DirectControlInput`** (new component on `Systems`). Turns keys into requests. It contains no movement rules.

- References: `PrimaryCharacter`, view `Camera`, and actions `Character/Move` and `Character/Takeover`. Test wiring: `internal void Initialize(PrimaryCharacter primary, Camera camera, InputActionReference move, InputActionReference takeover)`, called before the component is enabled (as for `PlayerCommandInput`).
- **`OnDisable`:** sets the primary character's intent to zero if it has a unit, so a disabled input can never leave the character steering by itself.
- **V** (`Takeover` performed): `primary.ToggleTakeover()`.
- **Each frame** (`[DefaultExecutionOrder(-100)]`, so the intent is set before units update in the same frame):
  - If `primary.IsDriving` and the release gate is open: read `Move`, convert it to a world direction relative to the camera, and pass it to `primary.Unit.SetMoveIntent`.
  - Otherwise pass zero. It only ever sets the intent on the primary character.
- **Camera-relative direction** (pure static, tested in EditMode): `ToWorldDirection(Vector2 input, float cameraYawDegrees)` rotates `(x, 0, y)` by the camera's yaw and clamps the length to 1. W moves "into the screen" whatever the camera pitch.
- **Release gate:** whenever `IsDriving` turns from false to true (resume, or V turning takeover on), the gate closes. It opens on the first frame `Move` reads zero. So keys still held from panning the camera during pause cannot wipe orders just queued. (Holding two opposite keys, such as W+S, also reads zero and opens the gate; accepted.)

**`PlayerCommandInput`** (changed):

- New serialized reference: `PrimaryCharacter primary`. `Initialize` gains a matching trailing parameter.
- **Who a ground or enemy click orders:**
  - Not paused (read from `PlayerCommandInput`'s own `TacticalPause` reference, the same component as the primary character's) and `primary.HasUnit`: the primary character only. `GroupOrders.Issue` with a one-unit list (offset zero), Shift → Append, otherwise Replace.
  - Paused, or no primary character wired, or its unit missing, disabled or inactive (`HasUnit` false): the selection, exactly as in Phase 2. Existing Phase 2 tests and scenes without a primary character keep their behaviour.
- Unchanged: clicking a friendly selects it, box selection, X stops the **selection**, Esc clears the selection, Space toggles pause.

### 4.3 Camera — `CameraControl/`

**`TacticalCameraController`** (changed):

- New serialized reference: `PrimaryCharacter primary`, and a tuning value `followSharpness` (default 10). `Initialize` gains a matching trailing parameter.
- **While `primary.IsDriving`:** the pivot's x and z ease toward the primary character's position by `1 − exp(−followSharpness × unscaledDeltaTime)` each frame. Pan input is ignored. Rotate, drag-rotate, tilt and zoom work as now, so they orbit around the character. The bounds clamp still applies.
- **Otherwise** (free mode, or paused): unchanged, and WASD pans.
- Returning to the character on resume needs no extra code: the follow always eases.
- `Camera/Pan` keeps its WASD binding. While driving, the camera ignores Pan. While not driving, `DirectControlInput` ignores Move. So only one of them acts at a time.

### 4.4 Debug feedback — `DebugUI/`

**`PrototypeHud`** (changed):

- New serialized reference: `PrimaryCharacter primary`.
- New control hints:
  ```
  WASD: pan camera   Q/E: rotate   Right-drag: rotate/tilt   Wheel: zoom   V: takeover (WASD drives primary)
  Left-click unit: select (Shift: add/remove)   Left-drag: box select   Esc: clear selection
  Left-click ground/dummy: primary moves/attacks (paused: selected units)   Shift: queue   X: stop selected
  Space: tactical pause
  ```
- New status line, for example `Primary: FriendlyUnit_1 | Takeover ON (V) | Following orders`.
  - Takeover part: `Takeover OFF (V)` or `Takeover ON (V)`; while paused with takeover on, `Takeover ON (after pause)`.
  - Activity part: `Following orders` while the primary character has a current order; otherwise `Manual control` while driving; otherwise `Idle`.
  - The text comes from a pure static `DescribePrimary(string name, bool takeoverOn, bool isPaused, bool hasOrders)`, tested in EditMode.
- **Layout:** the hint label is widened and made taller so the longer lines neither wrap nor clip. The rows below it (dummy HP, Selected, the new Primary line, the pause banner) each keep their own row and move down as needed.
- Existing order labels, queue lines, selection rings and the pause banner are unchanged. The primary character's queued orders show like any other unit's.

## 5. Precedence rules (primary character only)

1. Orders from real-time clicks or from tactical pause go through the unit's queue exactly as in Phase 2 (Replace, or Append with Shift).
2. **Takeover:** in takeover mode with simulation running, any WASD input clears **all** of the primary character's orders (current and pending, move or attack, same as Stop). Then the keys steer it.
3. **Keys win while held:** an order given to the primary character while a movement key is held is cleared on the next simulation frame. Releasing the keys lets clicks act.
4. Releasing the keys leaves the character idle. Cancelled orders do not come back.
5. Takeover never touches companions' orders.
6. **Pause and resume:** steering stops while paused, and the input sets the intent to zero. V works while paused and takes effect on resume. After resume, keys already held are ignored until `Move` reads zero (release gate, §4.2).
7. **Direct attack:** clicking an enemy issues `AttackCommand` to the primary character, which chases and hits until the target dies, cannot be reached, or is cancelled by rule 2.
8. **Free mode:** WASD never moves the character. The takeover is any real-time click, which replaces the current order unless Shift is held.

## 6. Input actions

`BlackglassControls.inputactions` gains a third map:

| Map | Action | Type | Binding |
|---|---|---|---|
| Character | Move | Value Vector2 | 2D vector composite: W / S / A / D |
| Character | Takeover | Button | `<Keyboard>/v` |

The Camera and Commands maps are unchanged.

## 7. Scene and assets

- **`Prototype.unity`:**
  - `FriendlyUnit_1` is the primary character.
  - `Systems` gains `PrimaryCharacter` (unit `FriendlyUnit_1`, `TacticalPause`) and `DirectControlInput` (wired to `PrimaryCharacter`, `Main Camera`, the new actions).
  - `PlayerCommandInput`, `TacticalCameraController` and `PrototypeHud` get their `primary` reference.
  - `FriendlyUnit_1` gets a child `PrimaryMarker`: a small cube rotated 45° to look like a diamond, about 1.6 m above the unit's centre. It uses a new orange URP Unlit material, `Materials/PrimaryMarker.mat`. Its collider is removed, so it never blocks click raycasts.
  - The `FriendlyUnit` prefab is unchanged. The marker is a scene override on one instance only.
  - The NavMesh is not re-baked.
- The scene is edited by a **temporary editor script** run in batch mode, deleted afterwards and not committed (as in Phases 1 and 2).

## 8. Testing and validation

Tests are written first (decision 009) and run in batch mode with `Tools/run-tests.sh`, with the Editor closed.

**EditMode:**
- `PrimaryCharacter`:
  - `IsDriving` is true only with takeover on, not paused and an active unit;
  - toggling works while paused;
  - takeover starts off;
  - `HasUnit` is false for a missing, disabled or inactive unit, which also makes `IsDriving` false.
- `DirectControlInput.ToWorldDirection`: yaw 0 / 90 / 180, diagonal clamped to length 1, zero gives zero.
- `PrototypeHud.DescribePrimary`: every combination of its inputs.

**PlayMode (test world, no input):**
- `Steer` moves the unit about speed × time in the direction and turns it to face that way. It stays on the NavMesh when pushed into a wall and does nothing while paused. It leaves no path or velocity behind, including when it starts right after a finished move.
- Move intent with a current Move plus a pending Attack clears both, then steers. The same happens with a current Attack.
- Zero intent leaves orders alone. A companion's queue is unchanged while the primary character is steered.
- An order issued while the intent is held is cleared on the next simulation frame (rule 3).
- Releasing the intent leaves the unit idle where it is (rule 4).

**PlayMode (simulated input, `InputTestFixture`):**
- Takeover on, real time: W moves the primary character, and a companion with queued orders keeps them.
- Free mode: W does not move the primary character. V toggles takeover; V while paused takes effect on resume.
- Release gate: queue orders for the primary character while paused, resume with W still held, and the orders survive. Release and press W again, and they are cleared.
- Real-time click on the ground or the dummy orders only the primary character; the selected companions get nothing. Paused click orders the selection. Shift in real time appends to the primary character's queue.
- With a primary character wired, in real time:
  - clicking a friendly selects it and gives the primary character no order;
  - box selection still works;
  - X stops the selected companions and leaves the primary character's orders alone.
- With the primary character's unit disabled, real-time clicks order the selection (fallback).
- Disabling `DirectControlInput` while W is held sets the intent to zero.
- Camera: while driving, the pivot converges on the primary character and WASD does not pan. While paused, or in free mode, WASD pans.
- Pause cycling: ten pause/resume cycles with orders queued for the primary character and a companion leave both queues intact and running in order (validation item 13).
- Takeover cycling: with the same orders queued, toggle V several times, mixed with pause/resume and with no movement keys. Both queues stay intact and run to completion after the final resume.

**Scene (`Prototype.unity`):**
- The wiring is complete and `FriendlyUnit_1` is the primary character.
- V then W moves the primary character, and the camera follows.
- A real-time click on the dummy makes the primary character attack it.
- The scene runs for 1 s without logged errors.

**Existing tests:**
- `Initialize` signatures gain a trailing `primary` parameter that defaults to none, so fixtures without a primary character need no edits.
- `PrototypeSceneTests` changes for behaviour. The scene now wires a primary character, so real-time ground clicks order only `FriendlyUnit_1`.
  - `BoxSelectSquadThenClickGround_InScene_AllThreeMove` and `StopAndClearSelection_InScene_UseTheSceneBindings` pause (Space) before clicking, so they still test that paused clicks order the selection.
  - `ClickUnitThenDummy_InScene_OnlyThatUnitAttacks` also pauses first and selects a companion. It then still tests select-then-order, instead of passing only because the clicked unit is the primary character.

**Validation checklist** (the owner's 14 items):

| # | Item | Covered by |
|---|---|---|
| 1 | Primary character can be directly controlled in real time | Takeover W test; scene V+W test |
| 2 | Direct movement feels responsive | Manual play |
| 3 | Direct attack works | Real-time dummy click test; scene dummy click test |
| 4 | Companions remain command-controlled | Takeover W test (the companion keeps its orders); real-time click test (companions get nothing) |
| 5 | Tactical pause freezes gameplay simulation | `Steer` paused test; existing Phase 1–2 pause tests |
| 6 | Camera still operates during pause | Camera pans-while-paused test; existing camera tests |
| 7 | Units remain selectable during pause | Existing Phase 2 selection tests; scene tests that now click while paused |
| 8 | Tactical commands can be queued for companions | Paused-click-orders-selection test; scene tests |
| 9 | Tactical commands can be queued for the primary character | Release-gate test (orders queued for the primary while paused) |
| 10 | Queued commands execute after resume | Pause cycling test |
| 11 | Manual movement can retake control of the primary character | Move-intent tests; release-gate test (the second W press clears orders) |
| 12 | Manual takeover does not cancel companion orders | Move-intent companion test; takeover W test |
| 13 | Repeated switching between real time and pause does not break command state | Pause cycling and takeover cycling tests |
| 14 | Console free of unexpected errors | Scene test's logged-error check (an unexpected logged error fails any test) |

**Manual checks for the owner:**
- Steering feel: speed, instant start and stop, turning.
- Camera follow tightness and the ease-back after resume.
- Rule 3 in practice: clicking the dummy while moving.
- Whether free mode or takeover should be the default.
- Readability of the primary character's marker and the HUD status line.

## 9. Known limitations, accepted for the prototype

- **Keys win while held** (rule 3): clicking the dummy while holding WASD does nothing until the keys are released. The alternative (a click suspends the keys until they are released) is held back for playtesting.
- Takeover cancels attacks as well as moves. There is no "attack while moving".
- Steering has no acceleration, and `NavMeshAgent.Move` does not run local avoidance for the steered unit (other agents still avoid it).
- The camera has no collision, and the bounds clamp can stop it following at the map edge.
- Which unit is the primary character is fixed in the scene. There is no switching at runtime, and no handling of the primary character dying (friendly units have no `Health` yet).
- In real time, X stops the selection, not the primary character.
- Clicking a friendly always selects it (unchanged from Phase 2), so neither the primary character nor the selection can be ordered onto a spot where a friendly stands.

## 10. Files

- **Created:**
  - `Scripts/Controls/PrimaryCharacter.cs`, `Scripts/Controls/DirectControlInput.cs`
  - `Materials/PrimaryMarker.mat`
  - new EditMode and PlayMode test files
- **Changed:**
  - `Scripts/Units/UnitMover.cs`, `Scripts/Units/CommandableUnit.cs`
  - `Scripts/Controls/PlayerCommandInput.cs`, `Scripts/CameraControl/TacticalCameraController.cs`, `Scripts/DebugUI/PrototypeHud.cs`
  - `Input/BlackglassControls.inputactions`, `Scenes/Prototype.unity`
  - `Tests/PlayMode/PrototypeSceneTests.cs` (behaviour, see §8), plus any fixture touched only for `Initialize` signatures
  - `Docs/Decisions.md`

(Paths are under `Assets/_Project/` except `Docs/`.)

## 11. Decision records (`Docs/Decisions.md`)

- **006 update:** `CommandableUnit` also takes a held move intent. A non-zero intent clears all orders and steers. Orders and direct control share `UnitMover` and `UnitAttacker`.
- **008 update:**
  - the Character map (Move on WASD, Takeover on V);
  - WASD is shared with camera pan, and the mode decides which acts;
  - real-time ground and enemy clicks order the primary character, paused clicks order the selection.
- **011 (new) — Primary character and takeover:**
  - `PrimaryCharacter` holds the state;
  - the free and takeover modes;
  - the precedence rules (§5);
  - the release gate;
  - camera follow on unscaled time;
  - the rejected alternatives (§3).
