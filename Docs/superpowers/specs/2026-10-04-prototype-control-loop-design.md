# Prototype Control Loop — Design

Date: 2026-10-04 · Branch: `prototype/control-loop` · Status: approved; implemented on branch `prototype/control-loop`

## 1. Goal

Prove the core control architecture in one small playable scene:

**Input → Command → Unit → Movement / Combat**, plus **Tactical Pause** as an explicit system.

Success means all of the following work in `Prototype.unity`:

1. The camera pans, rotates and zooms, including while tactically paused.
2. One player-controlled unit moves to a right-clicked point.
3. Right-clicking the dummy target makes the unit approach it and attack until its HP reaches 0, then the dummy is disabled.
4. Space toggles tactical pause. While paused, unit movement and combat stop. The camera, input and HUD keep working.
5. A command issued while paused is accepted, then carried out after resume.
6. Input code never contains movement or combat rules. It only creates commands and hands them to the unit.

## 2. Scope

**In scope:** everything in section 1, the debug HUD, placeholder materials, automated tests, and decision records.

**Out of scope:**
- **Not implemented yet:** selection, command queues, Stop/Interact commands, AI, factions or teams.
- **Never in this phase:** inventory, equipment, progression, dialogue, quests, procedural generation, save/load, networking, production UI, animation or art, audio, DI or ECS frameworks, third-party packages.

## 3. Decisions made during brainstorming

| Topic | Decision |
|---|---|
| Movement | NavMesh: re-add `com.unity.ai.navigation` 2.0.14. Unit uses `NavMeshAgent`. `NavMeshSurface` is baked once in the scene. |
| Controls | RTS-style. A **quick right-click** is the context command: Move on ground, Attack on a target with `Health`. A **right-drag** past about 6 px rotates the camera and issues no command. Q/E rotate, WASD pan, wheel zoom, Space pause. Left-click is unused, kept free for selection later. |
| Commands | Plain immutable data (`MoveCommand`, `AttackCommand`). The unit decides how to execute them. A new command replaces the current one; there's no queue. |
| Selection | None. `PlayerCommandInput` holds a serialized reference to the single controlled unit. |
| Debug UI | IMGUI `OnGUI` overlay only. No uGUI canvas or EventSystem. |
| Wiring | Serialized Inspector references. No singletons, `Find*` lookups or global state. |

## 4. Architecture

All runtime code lives in `Assets/_Project/Scripts/` (assembly `Blackglass`, namespace `Blackglass`). The assembly references only `Unity.InputSystem`; `NavMeshAgent` is part of the engine's AI module. `Unity.AI.Navigation` (`NavMeshSurface`) is referenced by the PlayMode test assembly.

### 4.1 Commands — `Commands/UnitCommands.cs`

```csharp
public abstract class UnitCommand { }                       // marker base, no behaviour
public sealed class MoveCommand : UnitCommand   { public Vector3 Destination { get; } }
public sealed class AttackCommand : UnitCommand { public Health Target { get; } }  // ctor rejects null
```

Future `StopCommand` and `InteractCommand` will be added as new subclasses. AI, queues and selection will create the same objects.

### 4.2 Unit — `Units/`

- **`CommandableUnit`** is the only entry point for gameplay commands.
  - `bool Issue(UnitCommand command)`: throws `ArgumentNullException` for null, `ArgumentException` for unknown types. If accepted, it replaces the current order. If the command can't be carried out, it returns false and **the current order continues**. That happens when a move destination has no walkable NavMesh point within 2 m, or an attack target is dead or inactive. A destination that is only partially reachable is accepted, and the unit moves as close as it can.
  - Orders only advance while simulation time advances (`Time.deltaTime > 0`). Commands are accepted while paused.
  - `UnitCommand CurrentCommand { get; }`: null when idle.
  - **Move order:** calls `mover.MoveTo(destination)` once. The order completes when `mover.HasArrived`.
  - **Attack order**, evaluated each `Update`:
    1. If the target is null, dead or inactive, stop and complete the order.
    2. Otherwise, if it's out of range, call `mover.MoveTo(target.position)`. The path is only refreshed when the target has moved more than 0.5 m since the last refresh.
    3. Otherwise, stop, turn toward the target, and call `attacker.TryAttack(target)`.
  - All per-frame logic runs in `Update` on scaled time, so it naturally halts while paused.
- **`UnitMover`** (requires `NavMeshAgent`):
  - `MoveTo(Vector3)` snaps the point onto the NavMesh with `NavMesh.SamplePosition`, within 2 m. If no NavMesh point is found, the order is ignored and a warning is logged.
  - `Stop()` clears the path and zeroes velocity, so the unit stops on the spot.
  - `bool HasArrived` is true when the path isn't pending and either the horizontal distance to the destination is at most the stopping distance + 0.1, or the agent has no path left.
  - Agent settings: speed 5, angular speed 720, acceleration 20, stopping distance 0.1.
- **`UnitAttacker`**:
  - `float Range = 2`, `int Damage = 25`, `float Cooldown = 1` (seconds of scaled `Time.time`).
  - `bool IsInRange(Health target)` uses horizontal centre-to-centre distance.
  - `bool TryAttack(Health target)` applies damage if the cooldown has elapsed, and returns whether a hit happened.

### 4.3 Combat — `Combat/`

- **`Health`**:
  - `int Max = 100`, `int Current`, `bool IsAlive`.
  - `void TakeDamage(int amount)` ignores the call when dead, throws if `amount < 0`, and clamps at 0.
  - Events: `Damaged(int amount)` and `Died()`. `Died` fires exactly once.
  - `bool disableOnDeath = true` deactivates the GameObject on death.
- **`HitFlash`**: on `Damaged`, tints the renderer white for 0.15 s of **scaled** time using a `MaterialPropertyBlock` (`_BaseColor`). It is gameplay feedback, so it freezes with the simulation.

### 4.4 Tactical pause — `GameTime/TacticalPause.cs`

- This is the **only** code in the project that writes `Time.timeScale`.
- `bool IsPaused`, `Pause()`, `Resume()`, `Toggle()`, `event Action<bool> Changed`. `Changed` fires only on an actual change.
- `Pause()` remembers the current time scale and sets it to 0. `Resume()` restores the remembered value.
- `OnDisable` resumes if the game is still paused, so time scale is never left at 0.

### 4.5 Input — `Controls/`

- **`ClickDragDetector`** (pure C#):
  - `Press(Vector2)`, `Track(Vector2)`, `Release(Vector2) → bool wasClick` (the release position counts too), `bool IsPressed`, `bool IsDragging`, threshold in pixels (default 6).
  - Pointer movement since the press, measured as the furthest distance seen, decides click or drag.
- **`CommandResolver`** (pure, static): `UnitCommand Resolve(Health clicked, Vector3 point)`. Returns an `AttackCommand` if `clicked` is non-null and alive, otherwise a `MoveCommand(point)`.
- **`PlayerCommandInput`** holds serialized references to the `Camera`, the `CommandableUnit`, `TacticalPause`, and the `InputActionReference`s `Command`, `PointerPosition` and `TogglePause`.
  - Pressing the right button starts the detector, and moving the pointer updates it.
  - On release, if it was a click, it raycasts from the camera through the pointer. It looks for `Health` in the hit collider's parents, then calls `controlledUnit.Issue(CommandResolver.Resolve(...))`.
  - `TogglePause` calls `pause.Toggle()`.
  - It never touches the mover, attacker or health directly.

### 4.6 Camera — `CameraControl/TacticalCameraController.cs`

- Sits on `CameraRig`, a pivot on the ground. The Main Camera is a child, positioned from yaw, a fixed pitch of 55° and a zoom distance.
- **Pan:** WASD relative to camera yaw, 12 m/s at 20 m zoom, scaled by `distance / 20`.
- **Rotate:**
  - Q/E rotate at 90 °/s.
  - Right-drag rotates by 0.25 °/px. Its own `ClickDragDetector` ensures rotation only starts after the drag threshold, so the same press never both rotates and commands.
- **Zoom:** the wheel changes the distance by 2 m per notch, clamped to 5–40 m.
- Pivot clamped to ±25 m on X and Z.
- Uses `Time.unscaledDeltaTime` everywhere, so it works while paused. All speeds are adjustable in the Inspector.

### 4.7 Debug HUD — `DebugUI/PrototypeHud.cs`

`OnGUI` shows three things:
- control hints, top-left;
- "TACTICAL PAUSE — Space to resume", top-centre, while paused;
- "Training Dummy: 75 / 100", or "destroyed".

It references `TacticalPause` and the dummy's `Health`.

## 5. Input actions

`Assets/InputSystem_Actions.inputactions` is moved and renamed to `Assets/_Project/Input/BlackglassControls.inputactions`. Its GUID is kept, so it stays the project-wide actions asset. Its content is replaced, and the template's Player and UI maps are removed.

| Map | Action | Type | Binding |
|---|---|---|---|
| Camera | Pan | Value Vector2 | WASD composite |
| Camera | Rotate | Value Axis | Q (−) / E (+) |
| Camera | RotateDrag | Button | `<Mouse>/rightButton` |
| Camera | PointerPosition | Value Vector2 | `<Pointer>/position` (drag rotation uses the change in position) |
| Camera | Zoom | Value Axis | `<Mouse>/scroll/y` |
| Commands | Command | Button | `<Mouse>/rightButton` |
| Commands | PointerPosition | Value Vector2 | `<Pointer>/position` |
| Commands | TogglePause | Button | `<Keyboard>/space` |

Components enable their referenced actions in `OnEnable` and disable them in `OnDisable`. The Input System processes events every frame regardless of `Time.timeScale`.

## 6. Scene — `Assets/_Project/Scenes/Prototype.unity`

| Object | Contents |
|---|---|
| Directional Light | default |
| `Environment` | `NavMeshSurface` (collect: current hierarchy). Ground plane scaled 4×1×4 (40×40 m). Five grey cubes of various sizes as obstacles, including one between the unit's spawn and the dummy. |
| `PlayerUnit` | Blue capsule at (−10, 1, −10). `NavMeshAgent`, `UnitMover`, `UnitAttacker`, `CommandableUnit`. |
| `TrainingDummy` | Red cylinder at (10, 1, 10). `CapsuleCollider`, `Health`, `HitFlash`. |
| `CameraRig` | `TacticalCameraController`, child Main Camera (`AudioListener`, tag MainCamera). |
| `Systems` | `TacticalPause`, `PlayerCommandInput`, `PrototypeHud`. |

- **Materials** (`Assets/_Project/Materials/`, URP Lit): `Ground`, `Obstacle`, `Unit` (blue), `Dummy` (red).
- **NavMesh:** baked data is saved as a binary asset next to the scene (`Assets/_Project/Scenes/Prototype/`). To re-bake by hand, select `Environment` and click **Bake** on `NavMeshSurface`. The unit and dummy are outside `Environment`, so they're not baked into the NavMesh.
- **Build:** `Prototype.unity` becomes the only scene in the build list. `TestScene.unity` remains as a file.
- **How it's built:** the scene, materials and NavMesh are created by a **temporary editor script**. The script is deleted afterwards and not committed.

## 7. Testing and validation

**Assemblies:**
- `Blackglass.Tests.EditMode`: Editor-only. References `Blackglass` and NUnit.
- `Blackglass.Tests.PlayMode`: references `Blackglass`, `Unity.InputSystem`, `Unity.InputSystem.TestFramework` and `Unity.AI.Navigation`.

**Manifest:** add `"testables": ["com.unity.inputsystem"]` to `Packages/manifest.json`. This is test-only and exposes `InputTestFixture`.

**EditMode tests (written test-first):**
- `ClickDragDetector`: click versus drag at the threshold; reset on a new press.
- `TacticalPause`: pause, resume and toggle; time scale restored; repeated calls safe; `Changed` fires only on change; resumes on disable.
- `Health`: damage, clamping, `Died` fires exactly once, no damage after death, negative damage rejected, disable on death.
- `CommandResolver`: alive `Health` → Attack; null or dead → Move.
- `CommandableUnit.Issue`: rejects null.

**PlayMode tests (load `Prototype.unity`):**
1. The scene loads and runs for 1 s. No errors are logged; unexpected errors fail tests automatically.
2. A `MoveCommand` to the far side of an obstacle: the unit arrives within 0.3 m in under 10 s, and the command clears.
3. An `AttackCommand`: the dummy's HP decreases, reaches 0, and the dummy is inactive within 15 s.
4. Pause: a Move issued while paused leaves the unit in place for 1 s of real time. After resume, it arrives.
5. Camera while paused, using `InputTestFixture`: holding W moves the rig, a wheel step changes the zoom, and a right-drag changes the yaw.
6. Input end to end, using `InputTestFixture`:
   - a right-click on the dummy's screen point leads to an attack;
   - a right-click on the ground leads to a move;
   - a right-drag rotates the camera and issues no command.
7. Simulated Space toggles pause.

**Run:** batch mode, with the Editor closed (`-runTests -testPlatform EditMode|PlayMode`). Afterwards, check the result XML, the compile log (0 `error CS`) and the log for exceptions.

**Manual checks for the owner:** camera feel, visual readability, hit flash, HUD legibility.

## 8. Files

- **Created:**
  - `Assets/_Project/Scripts/**` (asmdef + 14 scripts)
  - `Assets/_Project/Tests/EditMode/**`
  - `Assets/_Project/Tests/PlayMode/**`
  - `Assets/_Project/Input/BlackglassControls.inputactions` (moved)
  - `Assets/_Project/Materials/*.mat`
  - `Assets/_Project/Scenes/Prototype.unity` and its NavMesh asset
- **Changed:** `Packages/manifest.json` (navigation + testables), `packages-lock.json`, `ProjectSettings/EditorBuildSettings.asset`, `Docs/Decisions.md`.

## 9. Decision records to add (`Docs/Decisions.md`)

- **005 update:** NavMesh re-added, and why.
- **006** Command abstraction.
- **007** Tactical pause service.
- **008** Input mapping and click-vs-drag.
- **009** Code assemblies and test strategy.

## 10. Known limitations, accepted for the prototype

- One unit, no selection.
- A new command replaces the old one; there's no queue.
- No factions. A unit with `Health` could be told to attack itself or a friendly unit later.
- The dummy is excluded from the NavMesh bake, so paths may route through its position. The unit stops at attack range before reaching it.
- The IMGUI HUD is debug-only.
- A move to a point the NavMesh reaches only partially is accepted and the unit stops as close as it can; whether the order then clears depends on the agent abandoning its path. This must be settled before command queues are built.
