# Phase 6.5 — Native controller input: design

Date: 2026-10-05. Status: awaiting owner review. Next: implementation plan (`writing-plans`).

## 1. Goal and scope

Controller support is a first-class requirement. Xbox, PlayStation (DualSense), Nintendo (Switch, Switch 2) and unknown gamepads must drive every existing control state through the Unity Input System, with no Steam Input dependency. Keyboard/mouse (KB/M) is untouched; the player moves freely between the two.

Gameplay systems consume **semantic actions** only. Physical bindings live in the Input Actions asset, per device family. Controller type never reaches movement, combat, cover, command queues or AI.

Out of scope (owner list, unchanged): settings UI, glyph artwork, Steam Input, console SDKs, haptics/adaptive triggers, gyro, touchpad, local co-op, multiplayer, inventory, abilities, production HUD. Phase 7 is not started.

Owner decisions taken during brainstorming:
- Per-family bindings use **binding groups plus an asset-level `bindingMask`** (not `PlayerInput`, not one positional gamepad group).
- The controller cursor is a **screen-space virtual cursor with world-radius snapping**, feeding the existing click path (not mouse emulation, not a ground-plane cursor).
- Controller `Attack` means **"attack best target"**: an `AttackCommand` on the soft/cursor target, else the best hostile by distance and facing, through the existing command path.
- The provisional layout below may feel awkward in play; remapping options come later. Nothing here is final.

## 2. Architecture

```
KB/M · Xbox · DualSense · Switch · Switch 2 · Generic gamepad
        ↓ (binding groups, one family active at a time)
Semantic Input Actions  (BlackglassControls.inputactions)
        ↓
Input components (PlayerCommandInput, DirectControlInput, TacticalCameraController, TacticalCursor)
        ↓
Existing systems: CommandResolver → GroupOrders → CommandableUnit; ActiveCharacter; UnitSelection; TacticalPause
```

New code lives in `Assets/_Project/Scripts/Input/` (runtime assembly `Blackglass`):

| Type | Kind | Responsibility |
|---|---|---|
| `InputFamily` | enum | `KeyboardMouse`, `Xbox`, `PlayStation`, `Nintendo`, `GenericGamepad`. `ControllerModel` (`Unknown`, `Switch`, `Switch2`, …) is kept alongside so Switch 2 prompts can split later; gameplay never reads it. |
| `InputFamilyClassifier` | pure static | Device layout/description → family + model. EditMode-tested. |
| `InputActivityFilter` | pure | Decides whether a device event is *meaningful* (thresholds, hysteresis). EditMode-tested. |
| `ActiveInputDevice` | MonoBehaviour (`Systems`) | Listens to `InputSystem.onEvent` on unscaled time; owns the current `Family` and `Device`; raises `Changed`; applies the binding mask and device restriction. |
| `PromptResolver` | plain class | "What is shown for action X on the active family?" |
| `TacticalCursor` | MonoBehaviour | Virtual cursor position, snapping, typed target. |
| `PointerTargetResolver` | pure-ish static | Screen point → `PointerTarget` (`Friendly`, `Hostile`, `Cover`, `Ground`). Extracted from `PlayerCommandInput.HandleClick`; used by the mouse path **and** the cursor. |
| `SoftTarget` | small state holder | The cycled/highlighted hostile; target-cycling order. |

Rules kept: no singletons, no static state, wiring through serialized fields and `internal Initialize(...)`, new simulation-adjacent code uses unscaled time (decision 007).

## 3. Input Actions asset

`BlackglassControls.inputactions` is extended, not rebuilt.

- **Binding groups / control schemes:** `KeyboardMouse`, `Xbox`, `PlayStation`, `Nintendo`, `Gamepad` (generic). **Every existing binding (including composite parts) is assigned to `KeyboardMouse`**; a binding with no group is not matched by a group mask and would silently stop working once the mask is applied. A test enumerates every binding and fails on a gamepad or keyboard binding without a group.
- **Family bindings** use layout-specific paths (`<XInputController>`, `<DualShockGamepad>`, `<SwitchProControllerHID>`) where a family needs deliberate differences, and `<Gamepad>` in the generic group. Because the mask selects one group at a time, a pad matching both a specific and the generic path cannot double-fire.
- **Renames (by name only; action IDs unchanged, so scene `InputActionReference`s survive):** `Character/Takeover` → `ToggleCharacterControl`; `Character/CycleCharacter` → `NextCharacter`; `Commands/TogglePause` → `ToggleTacticalPause`. `Character/CycleReverse` stays the KB Shift modifier. Existing Tab / Shift+Tab behaviour is unchanged: `NextCharacter` (Tab, RB) is read with `CycleReverse` held for backward; `PreviousCharacter` (LB) is a plain press that cycles backward. (Decision 012's rejection of a Shift+Tab composite still stands.)
- **New actions:** `Character/PreviousCharacter`; `Camera/Look` (Vector2), `Camera/CameraModifier` (button), zoom step buttons for L3/R3; a new **`Tactical`** map with `CursorMove` (Vector2), `Select`, `Cancel`, `ContextAction`, `QueueModifier`, `Attack`, `NextTarget`, `PreviousTarget`; a small **`UI`** map with `Navigate`, `Submit`, `Cancel` (no UI is built on it; one stub reader proves it is reachable). Where an action already exists with the right meaning (`Command`, `Modifier`, `Stop`, `ClearSelection`, `Move`, `Pan`) it gains gamepad bindings rather than a duplicate. Final names and map placement are settled in the plan.
- **Dead zones:** radial `StickDeadzone` processors on every stick binding (min about 0.2, max about 0.925), editable in the asset.

## 4. Active device and family detection

- `ActiveInputDevice` subscribes to `InputSystem.onEvent` (state events only) and asks `InputActivityFilter`.
- **Meaningful input:** any key press; mouse movement above a few pixels or any button/scroll; any gamepad button press; a trigger above 0.5; a stick **above an engage threshold of about 0.55** (well above the dead zone) with hysteresis so a stick hovering near the threshold does not toggle. Stick drift, tiny analog values and idle-pad noise never qualify.
- A family change happens only on meaningful input from the other family, starting as `KeyboardMouse` at launch. No restart or setting is needed in either direction.
- **Several controllers:** the one with the most recent meaningful input is the active device; on a gamepad family `asset.devices` restricts bindings to it, so a second pad cannot also control the game. In KB/M it is restricted to the keyboard and mouse.
- **Applying a family:** `asset.bindingMask = InputBinding.MaskByGroup(<family group>)` plus `asset.devices`. Switching cancels in-progress actions; the existing release gates in `DirectControlInput` (decision 011) and the click/drag detectors already tolerate an action cancelling mid-press. Repeated switching is covered by a test (no stuck input, no exceptions).
- **Classification (`InputFamilyClassifier`):** `XInputController`/`XboxOneGamepad` → Xbox; `DualShockGamepad` (including DualSense HID) → PlayStation; `SwitchProControllerHID` (and Joy-Con HID) or vendor ID 0x057E → Nintendo; any other `Gamepad` → GenericGamepad; a non-gamepad joystick is ignored. **Switch 2:** Input System 1.20.0 has no Switch 2 layout, so it is only recognised by Nintendo vendor ID and (if known) product ID as `ControllerModel.Switch2`, falling back to Nintendo, and it is **not claimed to work until tested on hardware**. Gameplay does not depend on the distinction.
- Runs on unscaled time and input events, so detection works while tactically paused.

## 5. Prompts / glyph architecture

`PromptResolver.GetPrompt(InputActionReference action, InputFamily family)` returns the binding text for that action under that family's group, from `InputAction.bindings` and `InputControlPath.ToHumanReadableString`, with a small label table for face buttons (placeholder text such as "A / B / X / Y" for Xbox, "Cross / Circle / Square / Triangle" for PlayStation, the printed Nintendo labels, and "South/East/West/North" for generic). Prompts always derive from the action's current binding, so a rebind changes them. There is no glyph artwork and no hard-coded "Press A" in gameplay or UI code. The HUD uses it for the debug binding lines.

## 6. Provisional controller layout

Modes follow the mouse's rule (decision 008/011): *driving* = takeover on and not paused; *tactical* = paused or takeover off.

| Semantic action | Driving | Tactical / free |
|---|---|---|
| `Move` / `CameraPan` | Left stick: analog, camera-relative, magnitude preserved | Left stick: pan |
| `Look` / `CursorMove` | Right stick: camera yaw and tilt | Right stick: tactical cursor |
| `CameraModifier` (hold RT) | Right stick drives the cursor | Right stick rotates and tilts the camera |
| `Select` / `Confirm` / `ContextAction` | South (Xbox A, PS ×) | Friendly selects, hostile attacks, cover moves into cover, ground moves |
| `Cancel` | East | Clears selection (as Esc) |
| `Attack` | West | West |
| `ToggleCharacterControl` | North | North |
| `PreviousCharacter` / `NextCharacter` | LB / RB | LB / RB |
| `QueueModifier` (hold LT) | Queue; add to selection | Same as Shift |
| `ToggleTacticalPause` | View/Back | View/Back |
| `ToggleFollow` / `Stop` | D-pad up / down | Same |
| `PreviousTarget` / `NextTarget` | D-pad left / right | Same |
| Zoom | L3 out, R3 in | Same |
| Menu/Start | Reserved, unbound | Reserved, unbound |

**Nintendo:** `Confirm` is the east button (printed A) and `Cancel` the south button (printed B); the other positions are unchanged and prompts show the printed label. This is expressed as bindings in the `Nintendo` group, never in code. PlayStation and Xbox share positions. **Generic** uses the Xbox positions via `<Gamepad>` and fails gracefully (no exceptions, usable layout) for unknown pads. This layout is explicitly provisional.

## 7. Tactical cursor and targeting

- **Position:** screen space, moved by `CursorMove` on `Time.unscaledDeltaTime` with a response curve (about 900 px/s at full tilt, finer near the dead zone), clamped to the screen, kept across mode changes. Visible only when the active family is a gamepad and the cursor role is active; drawn as debug-level placeholder geometry/IMGUI.
- **Resolution (`PointerTargetResolver`):** `Physics.SyncTransforms()` then a camera ray like `HandleClick` today. A friendly `SelectableUnit` yields `Friendly`; a living `Health` yields `Hostile`; a cover location within the click radius yields `Cover`; else `Ground`.
- **Snapping:** within a snap radius of about 1.2 m (mouse cover radius stays 0.5 m): friendly, then hostile, then cover; never through walls; releases when the stick pushes past a threshold. Modest, no aim-assist framework.
- **Acting:** Confirm calls the same `PlayerCommandInput` entry point the mouse click uses: friendly selects (or toggles with `QueueModifier`), anything else `CommandResolver.Resolve` → `GroupOrders.Issue` (Replace, or Append with `QueueModifier`). The mouse handler and the cursor handler both delegate to one method taking a resolved `PointerTarget`. No parallel command code.
- **`Attack`:** tactical (or cursor role active while driving): `AttackCommand` on the hostile under the cursor, else the soft target. Driving: the soft target, else the best living hostile scored by distance and angle from the left-stick direction (camera forward if the stick is idle). Recipients follow the existing `OrderedUnits` rule. It issues through `CommandResolver`/`GroupOrders`; no controller-specific combat.
- **`NextTarget` / `PreviousTarget`:** cycle living, active hostiles in distance order from the active character; dead or inactive units are skipped; with the cursor visible they also snap it to the target. Not a lock-on system.
- **Cover:** while paused the existing `CoverView` shows markers; the cursor snaps to them and highlights the snapped one; Confirm issues `MoveToCoverCommand` through `CommandResolver`; `QueueModifier` + Confirm queues the next command. No separate controller cover system.
- **Cancel:** predictable and identical to Esc: it clears the selection. `Stop` stays on its own action.

## 8. Camera and time

`TacticalCameraController` gains `Look` (driving: yaw rate about 120°/s, tilt about 60°/s, clamped like the mouse drag), `CameraModifier`, and zoom-step reads; pan already reads `Pan`, which gets the left stick. Pan versus `Move` sharing the left stick follows the existing driving rule. All of it, the cursor and family detection run on unscaled time or input events. Simulation (movement, combat, cooldowns) is unchanged and stays paused; `TacticalPause` remains the only writer of `Time.timeScale`.

## 9. Selection and multi-select

`QueueModifier` + Confirm on a friendly calls `UnitSelection.Toggle` (the same path as Shift+click). `NextCharacter`/`PreviousCharacter` select the new active character as today. `Cancel` clears. No box selection and no select-all action in this phase.

## 10. Rebinding readiness

Every action is a Unity `InputAction` in the asset with group-scoped bindings, so `PerformInteractiveRebinding`, `ApplyBindingOverride` and `SaveBindingOverridesAsJson` work without changes to gameplay code. Gameplay never reads `Gamepad.current` or a named button (a grep check is part of the plan). A PlayMode test overrides one gamepad binding, checks the action fires from the new button and that `PromptResolver` follows. No runtime rebinding UI.

## 11. Debug HUD

`PrototypeHud` shows `Input: Keyboard/Mouse | Xbox | PlayStation | Nintendo | Generic Gamepad` and, via `PromptResolver`, the active bindings for a few main actions. The static hints text for KB/M is kept; a controller hint line is added. Not production UI.

## 12. Scene wiring and docs

`Prototype.unity` gets `ActiveInputDevice`, `TacticalCursor`, `SoftTarget` and their references via a **temporary editor builder** (created, run and deleted in one task, never committed — the pattern of earlier phases). `Docs/Decisions.md` gets decision **024** (what, why, rejected, implications), with the final mapping table; decisions 008 (input mapping) and 012 (rejected separate Next/Previous actions, now partly changed) get amendment notes.

## 13. Testing and validation

- **Baseline first:** measure the EditMode and PlayMode counts with a real run before writing the plan's totals (lesson recorded in `unity-test-run-facts`). Tests run through `Tools/run-tests.sh`, one Unity instance at a time.
- **EditMode:** `InputFamilyClassifier`, `InputActivityFilter` (drift, hysteresis, thresholds), cursor response/clamp math, target cycling order and skipping, `PromptResolver` label tables, a binding-group completeness test over the asset.
- **PlayMode (`InputTestFixture`, simulated devices: Xbox, DualShock/DualSense, Switch Pro, a generic gamepad):** hot-swap KB/M ↔ controller, drift not flipping family, analog move magnitude, camera look, next/previous character (wrap, skip dead, queue preserved, follow/park respected), takeover and follow toggles, direct attack, tactical pause with the controller still working, cursor snapping to friendly/hostile/ground/cover, select, toggle multi-select, attack, Move/MoveToCover, queueing, cancel, repeated device switching without exceptions, Console free of unexpected errors.
- **KB/M regression:** the existing suites must pass unchanged, including WASD, RMB camera, left-click, V, Tab, Shift+Tab, F, Space, selection, queues, generated cover and MoveToCover.
- **Hardware honesty:** everything above is *simulated* Input System behaviour. Claude has no physical device and batch test runs cannot read one. The owner has offered a **PS5 DualSense**, so the plan ends with a short **manual DualSense checklist** for the owner to run in the Editor (USB or Bluetooth): the HUD shows `Input: PlayStation`; KB/M ↔ pad hot-switching both ways; resting the pad shows no drift or family flicker; left stick moves analog; right stick looks; RB/LB cycle; north toggles control; D-pad up toggles follow; View/Create pauses; the cursor moves and snaps; Cross selects, orders and moves into cover; LT queues; Circle cancels; Square attacks. The completion report will list only what the owner actually reports as tested; every other family (Xbox, Nintendo Switch, Switch 2, generic) stays "simulated only, needs physical testing". Switch 2 has no native Input System layout in 1.20.0, and Steam running on the owner's machine may present pads as XInput, which would show as Xbox.

## 14. Risks and open points

- **Mask switching cancels in-progress input** (accepted; the release gates cover it; tested).
- **Binding-group completeness** — an ungrouped binding silently dies under a mask (guarded by the test above).
- **Cursor in real time while driving** is only reachable with RT held; if that is awkward in play it is a mapping change, not an architecture change.
- **`PlayerCommandInput` grows** a second entry point (a resolved target); the extraction keeps one implementation of the order logic.
- **Switch 2** may need a vendor/product-ID table once real hardware identifiers are known.
- Phase 7 concerns (inventory, abilities) will need action-map additions; the family/mask mechanism is designed to absorb them.
