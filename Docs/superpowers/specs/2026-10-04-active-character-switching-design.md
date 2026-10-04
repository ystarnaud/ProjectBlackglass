# Active Character Switching (Phase 3 refinement) — Design

Date: 2026-10-04 · Branch: `prototype/active-character-switching` · Status: design approved by the owner in conversation 2026-10-04; written spec awaiting review

Builds on Phase 3 (`2026-10-04-primary-character-takeover-design.md`) and decisions 006–011 in `Docs/Decisions.md`. A small refinement before Phase 4 (combat). It does not redesign the control architecture.

## 1. Goal

Let the player change which friendly character they directly control:

- **Tab** makes the next eligible friendly the active character; **Shift+Tab** the previous one. Both wrap around.
- The old character stops receiving direct input at once; the new one obeys the Phase 3 takeover rules.
- The camera focuses the new character, the marker moves to it, and it becomes the selection.
- Works while tactically paused without advancing simulation or touching any queue.

The fixed "primary character" becomes the **active character**: whichever eligible squad member is currently controlled.

```
V / WASD / Tab ─► DirectControlInput ─► ActiveCharacter (state: Unit, takeover; Cycle over the roster)
                       │ SetMoveIntent        ▲ read by camera, marker, HUD, click input
                       │ Select (on Tab)      │ roster
                       ▼                      │
                 UnitSelection ───────────────┘
Clicks ─► PlayerCommandInput ─► Issue(command) ─► CommandableUnit (unchanged)
```

## 2. Scope

**In scope:** renaming `PrimaryCharacter` to `ActiveCharacter`; Tab/Shift+Tab cycling over eligible roster units; direct-control handover; selection follows the switch; camera focus on switch; a marker that follows the active character; HUD text; scene update; automated tests; decision records.

**Out of scope:** new combat, companion AI, inventory, equipment, abilities, stats, dialogue, quests, save/load, production UI, animation, final art, audio, multiplayer, ECS/DOTS, new packages, right-click context actions, automatic switching when the active character dies, a "can be directly controlled" flag separate from roster membership, cinematic camera transitions.

## 3. Decisions made during brainstorming

| Topic | Decision |
|---|---|
| Naming | Rename the class `PrimaryCharacter` → **`ActiveCharacter`** (file renamed with its `.meta`, so the script GUID and scene wiring survive). Serialized fields named `primary` become `activeCharacter` with `[FormerlySerializedAs("primary")]`. |
| Eligible characters | Units in `UnitSelection.Roster` (the friendlies) whose `SelectableUnit` and `CommandableUnit` are enabled, whose GameObject is active in the hierarchy, and which have no `Health` or a living one. No separate "controllable" flag yet (YAGNI). |
| Shift+Tab | One new action **`Character/CycleCharacter`** bound to Tab. Direction comes from the existing **`Commands/Modifier`** action (Shift): held → previous, otherwise next. Same pattern as Shift-to-queue (decision 008). Rejected: separate Next/Previous actions with a Shift+Tab composite, which needs the project-wide "shortcut keys consume input" setting so Shift+Tab does not also fire Tab. |
| Held keys on switch | **Hybrid.** A held move key carries over to the new character only if it has no orders; if it has orders, the release gate re-arms so a held key cannot wipe them. |
| Selection | Tab/Shift+Tab **selects the new active character, replacing the selection**, so paused Tab-then-click orders the character just tabbed to. Queued orders are untouched. |
| Camera | On a switch the camera **glides once** to the new character, also while paused or in free mode. Any pan input cancels the glide. While driving it follows as in Phase 3. |
| When Tab works | Always: free mode, takeover mode, paused or not. Real-time clicks already order the active character in both modes, so switching matters in both. |
| Marker | One marker that follows the active character (above the head), instead of a cube parented to `FriendlyUnit_1`. Stays distinct from the selection ring at the feet. |

## 4. Architecture

All runtime code stays in the `Blackglass` assembly and namespace. No new packages or assembly references.

### 4.1 `ControlCycle` — `Controls/ControlCycle.cs` (new)

Pure static helper, no Unity dependencies:

```csharp
/// Index of the next eligible entry after `current` in `direction` (+1 or -1), wrapping around. Returns `current`
/// when it is the only eligible entry, and -1 when nothing is eligible. A `current` outside 0..count-1 (the active
/// unit is not in the roster) starts the search before the first entry (forward) or after the last (backward).
public static int NextIndex(int count, int current, int direction, Func<int, bool> isEligible)
```

- `direction` other than +1/−1 throws `ArgumentOutOfRangeException`; `count < 0` throws; `isEligible == null` throws.
- Checks at most `count` candidates, so it terminates for any input.
- An ineligible `current` is never returned (with no other eligible entry the result is -1).

### 4.2 `ActiveCharacter` — `Controls/ActiveCharacter.cs` (renamed from `PrimaryCharacter.cs`)

Still holds state only. Unchanged members: `Unit`, `HasUnit`, `IsTakeoverOn`, `IsPaused`, `IsDriving`, `SetTakeover`, `ToggleTakeover`.

New:

- Serialized field `selection` (`UnitSelection`): the roster to cycle over.
- `internal void Initialize(CommandableUnit activeUnit, TacticalPause pause, UnitSelection unitSelection = null)`.
- `public void SetUnit(CommandableUnit newUnit)`: makes `newUnit` the active character (null allowed: no active character). No validation; cycling validates.
- `public bool Cycle(int direction)`: finds the current unit's roster index (−1 if absent), asks `ControlCycle.NextIndex` with `IsEligible`, and calls `SetUnit` on the result. Returns true if the active unit changed; false when there is no selection/roster, nothing is eligible, or the only eligible unit is already active. Never throws for an empty roster or null entries.
- `public static bool IsEligible(SelectableUnit candidate)`: the rule in §3. Uses `enabled` and `activeInHierarchy` (not `isActiveAndEnabled`), like `UnitSelection`, so EditMode gives the same answer.

No change event: consumers that care (input, camera) compare `Unit` with the unit they last saw each frame (decision 006: no change events; views read state each frame). This also catches changes from any source (Inspector, `SetUnit`, future scripts).

### 4.3 `DirectControlInput` (changed)

New serialized fields: `selection` (`UnitSelection`), `cycleAction` (`Character/CycleCharacter`), `modifierAction` (`Commands/Modifier`). `Initialize` gains matching optional parameters.

- **Tab:** on `cycleAction.performed`: `direction = modifier held ? -1 : +1`; `activeCharacter.Cycle(direction)`; then, if there is an active unit with a `SelectableUnit` and a selection, `selection.Select(it)`. Selecting happens even when the active unit did not change (one eligible unit), so after Tab the selection is always the active character.
- **Handover**, in `Update` (still `[DefaultExecutionOrder(-100)]`, so before units update):
  1. Keep `steeredUnit`, the unit this component last wrote an intent to.
  2. If `activeCharacter.Unit != steeredUnit`: zero `steeredUnit`'s intent (if it still exists), then apply the hybrid rule — if Move reads non-zero and the new unit has orders (`CurrentCommand != null`), set `waitingForRelease = true`; otherwise leave the gate as it was (an idle new unit takes over a held key; a gated key stays gated). Set `steeredUnit` to the new unit.
  3. The existing logic follows unchanged: re-arm the gate when driving starts, clear it when Move reads zero, steer `steeredUnit` or zero it.
- `OnDisable` zeroes `steeredUnit`'s intent.
- Switching never calls `Issue` or touches a queue. Whatever the old character was doing (its orders, or nothing) continues; Phase 3's "keys win while held" already cleared its orders if it was being driven.
- While paused, `IsDriving` is false, so no intent is applied; when simulation resumes, the existing gate applies to held keys.

### 4.4 `TacticalCameraController` (changed)

- Field `primary` → `activeCharacter` (`[FormerlySerializedAs("primary")]`).
- Keeps `lastSeenUnit`, set in `OnEnable` and `Initialize` to the current active unit so loading a scene does not trigger a glide.
- Each `Update`: if the active unit (null when `!HasUnit`) differs from `lastSeenUnit`, record it and, if non-null, start a **focus glide**.
- Driving: follow exactly as in Phase 3 (ends any glide).
- Not driving and gliding: if pan input is non-zero, end the glide and pan as usual; otherwise ease toward the unit with the same `followSharpness` easing on unscaled time. The glide ends once the rig is within 0.05 m of the unit's position after the bounds clamp.
- Zoom, Q/E and right-drag rotate/tilt are unchanged and keep working during a glide and while paused.

### 4.5 `PlayerCommandInput` (changed — rename only)

Field `primary` → `activeCharacter` (`[FormerlySerializedAs("primary")]`). Behaviour unchanged: real-time ground/enemy clicks order `activeCharacter.Unit`, so after a switch they come from the new character.

### 4.6 `ActiveCharacterMarker` — `DebugUI/ActiveCharacterMarker.cs` (new)

Debug feedback on a scene-level `ActiveMarker` object.

- Serialized: `activeCharacter`, `visual` (child GameObject), `height` (default 1.6 m above the unit's pivot, matching the old marker).
- `LateUpdate`: if `activeCharacter.HasUnit`, place itself at the unit's position plus `height` and show `visual`; otherwise hide `visual`. Runs while paused.
- `internal Initialize(ActiveCharacter, GameObject visual)` for tests.

### 4.7 `PrototypeHud` (changed)

- `DescribePrimary` → `DescribeActive`; line reads `Controlled: <name> | Takeover ON (V) | Manual control` (same mode/activity words as now).
- Field `primary` → `activeCharacter` (`[FormerlySerializedAs("primary")]`).
- Control hints gain `Tab / Shift+Tab: switch character`, and "primary" wording becomes "controlled character".

## 5. Rules summary

1. Tab → next eligible roster unit; Shift+Tab → previous; both wrap; ineligible units are skipped.
2. One eligible unit: stays active (Tab still selects it). None: nothing changes, no exception.
3. On a switch the old character's move intent is zeroed in the same frame; its orders are untouched.
4. A held move key drives the new character at once only if it is idle; otherwise it waits for release and a fresh press.
5. The new character becomes the only selected unit.
6. The camera glides to the new character once (pan cancels); while driving it follows.
7. Switching while paused changes state only: no movement, no queue changes, no simulation steps.
8. Takeover precedence (decision 011) is unchanged and now applies to whichever character is active.

## 6. Input actions

`Character` map gains:

| Action | Type | Binding |
|---|---|---|
| `CycleCharacter` | Button | `<Keyboard>/tab` |

Shift is the existing `Commands/Modifier` (left/right Shift). No other bindings change. Right mouse button remains camera-only (`Camera/RotateDrag`); the camera's `ClickDragDetector` already distinguishes a right-drag from a right-click, which keeps a future right-click context action possible without changing this design.

## 7. Scene and assets

Generated by a temporary editor script (created and deleted within the task, never committed), as in Phase 3:

- `Systems`: `ActiveCharacter` (was `PrimaryCharacter`, same component) gets `selection` wired; `DirectControlInput` gets `selection`, `cycleAction`, `modifierAction`.
- `PrimaryMarker` (child of `FriendlyUnit_1`) is replaced by a root `ActiveMarker` with `ActiveCharacterMarker` and a collider-free child `Visual` using the existing marker material.
- `Materials/PrimaryMarker.mat` is renamed `ActiveMarker.mat` with its `.meta` (GUID kept).
- Every `primary` reference is re-saved under its new field name.

## 8. Testing and validation

**EditMode**

- `ControlCycleTests`: forward and backward wrap over 3 and 4 entries; skips ineligible entries both ways; single eligible entry returns itself; none returns -1; current outside the range (−1, count) starts at the right end; ineligible current with others eligible; argument checks.
- `ActiveCharacterTests` (renamed from `PrimaryCharacterTests`, existing tests kept): `Cycle` forward/backward with wrap; skips inactive, disabled (`SelectableUnit` or `CommandableUnit`) and dead units (a `Health` with `disableOnDeath` off, set through `SerializedObject`); one eligible unit returns false and keeps it; empty roster, null entries and no selection return false without exceptions; active unit missing from the roster.
- `PrototypeHudTests`: `DescribeActive` text.

**PlayMode** (with the real input actions)

- `DirectControlInputTests`: Tab cycles forward and selects the new unit; Shift+Tab cycles backward; wrap; switching while W is held zeroes the old intent and the old unit stops; held W carries to an idle new unit; held W does not wipe a new unit's orders until re-pressed; the old unit's queued orders survive a switch; Tab while paused moves nothing and keeps every queue, and the new unit drives after resume; rapid alternating Tab/Shift+Tab (50 presses) logs no errors and ends in a consistent state; a deactivated unit is skipped.
- `TacticalCameraControllerTests`: switching while paused glides to the new unit; pan input cancels the glide; no glide on enable; follow while driving still works.
- `ActiveCharacterMarkerTests` (new): marker sits above the active unit, moves after a switch, hides with no unit.
- `PlayerCommandInputTests`: after `SetUnit`, a real-time click on the dummy orders the new active character.
- `PrototypeSceneTests`: wiring asserts (`ActiveCharacter`, `ActiveMarker`, roster wired); in the scene, Tab makes `FriendlyUnit_2` active and selected and a left-click on the dummy makes it attack; Shift+Tab from `FriendlyUnit_1` reaches `FriendlyUnit_3`.
- Full EditMode and PlayMode suites pass via `Tools/run-tests.sh`; scene tests fail on any logged error.

**Manual checks** (owner, in the Editor): the 16 validation items of the brief, especially feel of the hybrid key rule and the camera glide.

## 9. Known limitations, accepted for the prototype

- No automatic switch when the active character dies or is disabled: it stays active (and invalid) until Tab is pressed. Friendly units have no `Health` yet.
- Cycling order is roster order (the order in `UnitSelection`), not screen position or a portrait bar.
- The camera glide uses the follow easing; there is no separate transition tuning.
- Every roster unit is controllable; a per-unit "cannot be directly controlled" flag waits until a design needs it.
- Tab replaces a group selection; rebuilding it is a box-select away.

## 10. Files

```
Assets/_Project/
├── Input/BlackglassControls.inputactions          Character/CycleCharacter
├── Materials/ActiveMarker.mat                     renamed from PrimaryMarker.mat
├── Scenes/Prototype.unity                         rewired (generated)
├── Scripts/Controls/ActiveCharacter.cs            renamed from PrimaryCharacter.cs; SetUnit, Cycle, IsEligible
├── Scripts/Controls/ControlCycle.cs               NEW
├── Scripts/Controls/DirectControlInput.cs         Tab, handover, hybrid gate
├── Scripts/Controls/PlayerCommandInput.cs         rename only
├── Scripts/CameraControl/TacticalCameraController.cs  focus glide, rename
├── Scripts/DebugUI/ActiveCharacterMarker.cs       NEW
├── Scripts/DebugUI/PrototypeHud.cs                text, rename
└── Tests/
    ├── EditMode/ControlCycleTests.cs              NEW
    ├── EditMode/ActiveCharacterTests.cs           renamed from PrimaryCharacterTests.cs
    ├── EditMode/PrototypeHudTests.cs
    ├── PlayMode/ActiveCharacterMarkerTests.cs     NEW
    ├── PlayMode/DirectControlInputTests.cs
    ├── PlayMode/PlayerCommandInputTests.cs
    ├── PlayMode/TacticalCameraControllerTests.cs
    └── PlayMode/PrototypeSceneTests.cs
Docs/Decisions.md                                  012 added; 008 and 011 amended
```

## 11. Decision records (`Docs/Decisions.md`)

- **012 — Active character switching** (new): the rename and why (no fixed protagonist in the control code), eligibility, Tab + Modifier binding and the rejected composite/consumption alternative, the hybrid held-key rule, selection follows the switch, camera glide, polling instead of a change event.
- **011** amended: "primary character" now means the active character; "switching at runtime is not designed yet" points to 012.
- **008** amended: `Character` map lists `CycleCharacter` (Tab; Shift via `Modifier` reverses).
