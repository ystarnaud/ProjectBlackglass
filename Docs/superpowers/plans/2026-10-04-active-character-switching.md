# Active Character Switching Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Tab / Shift+Tab move direct control to the next / previous eligible friendly. Control hands over cleanly, the camera focuses the new character, the selection and marker follow it, and it all works while paused.

**Architecture:**
- `PrimaryCharacter` is renamed `ActiveCharacter` (still state only) and gains `SetUnit`, `Cycle` and `IsEligible`. It cycles over `UnitSelection.Roster` using a pure helper, `ControlCycle.NextIndex`.
- `DirectControlInput` reads Tab (`Character/CycleCharacter`) and Shift (`Character/CycleReverse`). It asks `ActiveCharacter` to cycle, selects the result, and hands the move intent over in its own `Update`.
- `TacticalCameraController` glides once to a new active unit. A new `ActiveCharacterMarker` floats above whoever is active. No change events: consumers compare `ActiveCharacter.Unit` with the unit they last saw.

**Tech Stack:** Unity 6000.3.25f1 (Unity 6.3 LTS), URP, Input System 1.20.0, AI Navigation 2.0.14, Unity Test Framework (NUnit), `InputTestFixture`.

**Spec:** `Docs/superpowers/specs/2026-10-04-active-character-switching-design.md`

## Global Constraints

- **Unity Editor:** exactly 6000.3.25f1 at `C:\Program Files\Unity\Hub\Editor\6000.3.25f1\Editor\Unity.exe`.
  - **The Editor must be closed during every batch-mode run** (only one instance can open the project).
  - If a run prints "The Unity Editor has this project open", stop and ask the owner to close it.
- **Code location:**
  - Runtime code goes in `Assets/_Project/Scripts/` (assembly `Blackglass`, namespace `Blackglass`).
  - Tests go in `Assets/_Project/Tests/EditMode/` and `Assets/_Project/Tests/PlayMode/` (namespace `Blackglass.Tests`).
  - No new assemblies, assembly references or packages.
- **Commands are data** (decision 006). Switching never calls `Issue`, `GroupOrders.Issue` or touches a queue. Direct control is a held move intent (`CommandableUnit.SetMoveIntent`).
- **Input components** only create commands, change the selection, call `TacticalPause`, set move intents, toggle takeover, or ask `ActiveCharacter` to cycle. They never touch `UnitMover`, `UnitAttacker`, NavMeshAgents or `Health`.
- **Pause ownership:** only `TacticalPause` writes `Time.timeScale` in game code. Tests may reset it to 1 in `TearDown`.
- **Time:** simulation uses scaled time; input, camera, marker and HUD use unscaled time or input callbacks.
- **Wiring:**
  - Serialized Inspector references only. No singletons, no `Find*` or `Camera.main` in game code, no static mutable state.
  - Tests wire components through `internal Initialize(...)`, called while the host GameObject is inactive (or before the component's first `Update`).
- **Renamed fields** keep their serialized data with `[FormerlySerializedAs("primary")]` (`using UnityEngine.Serialization;`).
- **Input:** Input System only, through `InputActionReference` fields. Each component enables and disables only its own map's actions.
- **Visuals:** debug only. IMGUI for text. Primitives with the existing placeholder material. Debug objects have **no colliders**.
- **Scope:** no combat, companion AI, inventory, abilities, stats, production UI, packages, right-click context actions, or auto-switch on death.
- **Git:**
  - Work on local branch `prototype/active-character-switching` (exists; spec committed). Never push or merge.
  - Commit after each task. Messages are sentence case and imperative, and end with the line `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
  - Rename files with `git mv` **together with their `.meta`** so GUIDs survive. Commit `.meta` files with their assets.
- **Test runs:** always `Tools/run-tests.sh`, with a 10-minute timeout for PlayMode runs.

### Running tests

```bash
Tools/run-tests.sh EditMode                                          # all EditMode tests
Tools/run-tests.sh PlayMode                                          # all PlayMode tests
Tools/run-tests.sh PlayMode Blackglass.Tests.DirectControlInputTests # one fixture (any -testFilter expression)
```

The script prints any `error CS` lines, a summary (`result= total= passed= failed=`), failed test names and messages, and `EXIT=<code>`: **0** all passed, **2** some failed, **3** could not run (usually a compile error; no results file). A "red" step is usually **EXIT=3 with `error CS0246`/`CS1061`/`CS1501`/`CS1739`** because new members don't exist yet.

**Baseline before this plan (measured 2026-10-04):** EditMode 123/123, PlayMode 108/108.

**Expected totals after each task:**

| After task | EditMode | PlayMode |
|---|---|---|
| 1 | 123 | 108 |
| 2 | 162 | 109 |
| 3 | 162 | 119 |
| 4 | 162 | 125 |
| 5 | 162 | 130 |
| 6 | 162 | 132 |
| 7 | 162 | 132 |

## Deviations from the spec (decided while planning; the spec is already updated to match)

- Shift for Shift+Tab is a new `Character/CycleReverse` action instead of the shared `Commands/Modifier`, so `DirectControlInput` and `PlayerCommandInput` never enable/disable the same action (spec §3, §4.3, §6).
- Review-focus tests below are added beyond the spec's §8 list.

## Review Focus

1. **The active unit is destroyed (not just deactivated) while driving, then Tab is pressed** → the next friendly becomes active and selected; no `MissingReferenceException`. Pinned in Task 3 (`ActiveUnitDestroyed_WhileDriving_ThenTab_MovesOnWithoutErrors`) and Task 5 (`DestroyedUnit_HidesTheVisual`).
2. **A group selection exists when Tab is pressed** → it is replaced by only the new active character; nobody's orders change. Pinned in Task 3 (`Tab_MakesTheNextFriendlyActive_SelectsIt_AndWraps` starts from a 3-unit selection).
3. **Tab in free mode (takeover off)** → the active character, selection and marker change, but WASD still only pans: no unit moves. Pinned in Task 3 (`TabInFreeMode_SwitchesTheActiveCharacter_ButWMovesNobody`).
4. **Right-drag rotation while a focus glide runs** → both apply: the camera rotates by the dragged amount and still arrives. Pinned in Task 4 (`RightDragDuringAGlide_StillRotates`).
5. **Focusing a unit outside the camera bounds** → the glide stops at the bounds edge without errors. Pinned in Task 4 (`GlideToAUnitOutsideTheBounds_StopsAtTheEdge`).

---

### Task 1: Rename `PrimaryCharacter` to `ActiveCharacter`

Pure rename; no behaviour change. The scene keeps working because the script `.meta` GUID is kept and renamed fields carry `[FormerlySerializedAs("primary")]`.

**Files:**
- Rename: `Assets/_Project/Scripts/Controls/PrimaryCharacter.cs` (+`.meta`) → `ActiveCharacter.cs` (+`.meta`)
- Rename: `Assets/_Project/Tests/EditMode/PrimaryCharacterTests.cs` (+`.meta`) → `ActiveCharacterTests.cs` (+`.meta`)
- Modify: `Assets/_Project/Scripts/Controls/DirectControlInput.cs`, `Assets/_Project/Scripts/Controls/PlayerCommandInput.cs`, `Assets/_Project/Scripts/CameraControl/TacticalCameraController.cs`, `Assets/_Project/Scripts/DebugUI/PrototypeHud.cs`
- Test: `Assets/_Project/Tests/EditMode/ActiveCharacterTests.cs`, `Assets/_Project/Tests/EditMode/PrototypeHudTests.cs`, `Assets/_Project/Tests/PlayMode/DirectControlInputTests.cs`, `Assets/_Project/Tests/PlayMode/PlayerCommandInputTests.cs`, `Assets/_Project/Tests/PlayMode/TacticalCameraControllerTests.cs`, `Assets/_Project/Tests/PlayMode/PrototypeSceneTests.cs`

**Interfaces:**
- Produces: `public sealed class ActiveCharacter : MonoBehaviour` with `Unit`, `IsTakeoverOn`, `HasUnit`, `IsPaused`, `IsDriving`, `SetTakeover(bool)`, `ToggleTakeover()`, `internal Initialize(CommandableUnit activeUnit, TacticalPause pause)`. `PrototypeHud.DescribeActive(string unitName, bool takeoverOn, bool isPaused, bool hasOrders)` returning `"Controlled: <name> | <mode> | <activity>"`. Fields named `activeCharacter` in `DirectControlInput`, `PlayerCommandInput`, `TacticalCameraController`, `PrototypeHud`; `Initialize` parameters renamed to `active`.

- [ ] **Step 1: Rename the test file and update the tests**

```bash
git mv Assets/_Project/Tests/EditMode/PrimaryCharacterTests.cs Assets/_Project/Tests/EditMode/ActiveCharacterTests.cs
git mv Assets/_Project/Tests/EditMode/PrimaryCharacterTests.cs.meta Assets/_Project/Tests/EditMode/ActiveCharacterTests.cs.meta
sed -i -E 's/PrimaryCharacter/ActiveCharacter/g; s/\bprimary\b/active/g' \
  Assets/_Project/Tests/EditMode/ActiveCharacterTests.cs \
  Assets/_Project/Tests/PlayMode/DirectControlInputTests.cs
sed -i -E 's/PrimaryCharacter/ActiveCharacter/g; s/\bprimaryCharacter\b/activeCharacter/g; s/MakePrimary/MakeActive/g' \
  Assets/_Project/Tests/PlayMode/PlayerCommandInputTests.cs \
  Assets/_Project/Tests/PlayMode/TacticalCameraControllerTests.cs \
  Assets/_Project/Tests/PlayMode/PrototypeSceneTests.cs
grep -n "PrimaryCharacter\|primaryCharacter\|\bprimary\b" Assets/_Project/Tests -r   # expect only plain-English words in strings/comments, e.g. "the primary character"
```

`\bprimary\b` does not touch `primaryUnit` (word boundary). The string `"PrimaryMarker"` in `PrototypeSceneTests.cs` stays: the marker still exists until Task 6.

In `Assets/_Project/Tests/EditMode/PrototypeHudTests.cs`, replace the whole `DescribePrimary_ShowsModeAndActivity` test (its 8 `TestCase` lines and the method) with:

```csharp
        [TestCase(false, false, false, "Controlled: Hero | Takeover OFF (V) | Idle")]
        [TestCase(false, false, true, "Controlled: Hero | Takeover OFF (V) | Following orders")]
        [TestCase(false, true, false, "Controlled: Hero | Takeover OFF (V) | Idle")]
        [TestCase(false, true, true, "Controlled: Hero | Takeover OFF (V) | Following orders")]
        [TestCase(true, false, false, "Controlled: Hero | Takeover ON (V) | Manual control")]
        [TestCase(true, false, true, "Controlled: Hero | Takeover ON (V) | Following orders")]
        [TestCase(true, true, false, "Controlled: Hero | Takeover ON (after pause) | Idle")]
        [TestCase(true, true, true, "Controlled: Hero | Takeover ON (after pause) | Following orders")]
        public void DescribeActive_ShowsModeAndActivity(bool takeoverOn, bool isPaused, bool hasOrders, string expected)
        {
            Assert.That(PrototypeHud.DescribeActive("Hero", takeoverOn, isPaused, hasOrders), Is.EqualTo(expected));
        }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `Tools/run-tests.sh EditMode` → expected `EXIT=3` with `error CS0246: The type or namespace name 'ActiveCharacter' could not be found` and `CS0117 ... 'DescribeActive'`.

- [ ] **Step 3: Rename the runtime class**

```bash
git mv Assets/_Project/Scripts/Controls/PrimaryCharacter.cs Assets/_Project/Scripts/Controls/ActiveCharacter.cs
git mv Assets/_Project/Scripts/Controls/PrimaryCharacter.cs.meta Assets/_Project/Scripts/Controls/ActiveCharacter.cs.meta
```

Replace the whole content of `Assets/_Project/Scripts/Controls/ActiveCharacter.cs` with:

```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The friendly character the player currently controls directly, and whether takeover mode is on. Holds state
    /// only: DirectControlInput, click input, the camera and the HUD read it. Takeover starts off (free mode) and can be
    /// toggled while paused; it takes effect when simulation runs again.
    /// </summary>
    public sealed class ActiveCharacter : MonoBehaviour
    {
        [SerializeField] CommandableUnit unit;
        [SerializeField] TacticalPause tacticalPause;

        public CommandableUnit Unit => unit;

        public bool IsTakeoverOn { get; private set; }

        /// <summary>
        /// True when there is an active character that can act: assigned, enabled and active. Uses enabled and
        /// activeInHierarchy (not isActiveAndEnabled), like UnitSelection, so it gives the same answer in EditMode.
        /// </summary>
        public bool HasUnit => unit != null && unit.enabled && unit.gameObject.activeInHierarchy;

        public bool IsPaused => tacticalPause != null && tacticalPause.IsPaused;

        /// <summary>True while WASD drives the active character: takeover on, simulation running, unit present.</summary>
        public bool IsDriving => IsTakeoverOn && !IsPaused && HasUnit;

        internal void Initialize(CommandableUnit activeUnit, TacticalPause pause)
        {
            unit = activeUnit;
            tacticalPause = pause;
        }

        public void SetTakeover(bool on) => IsTakeoverOn = on;

        public void ToggleTakeover() => IsTakeoverOn = !IsTakeoverOn;
    }
}
```

- [ ] **Step 4: Rename the references in `DirectControlInput`**

Replace the whole content of `Assets/_Project/Scripts/Controls/DirectControlInput.cs` with:

```csharp
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Serialization;

namespace Blackglass
{
    /// <summary>
    /// Takeover-mode input for the active character. V toggles takeover. While the active character is being driven
    /// (ActiveCharacter.IsDriving), WASD becomes a camera-relative move intent on its CommandableUnit; otherwise the
    /// intent is zero. Contains no movement rules: the unit decides what the intent means.
    /// Release gate: whenever driving starts (resume, or takeover turned on), keys already held are ignored until Move
    /// reads zero, so a key held from panning the camera cannot wipe orders just queued.
    /// </summary>
    [DefaultExecutionOrder(-100)] // set the intent before units update in the same frame
    public sealed class DirectControlInput : MonoBehaviour
    {
        [SerializeField, FormerlySerializedAs("primary")] ActiveCharacter activeCharacter;
        [SerializeField] Camera viewCamera;

        [Header("Input")]
        [SerializeField] InputActionReference moveAction;
        [SerializeField] InputActionReference takeoverAction;

        bool wasDriving;
        bool waitingForRelease;

        internal void Initialize(ActiveCharacter active, Camera camera, InputActionReference move,
            InputActionReference takeover)
        {
            activeCharacter = active;
            viewCamera = camera;
            moveAction = move;
            takeoverAction = takeover;
        }

        /// <summary>Turns movement input into a ground direction relative to the camera's yaw, at most length 1.</summary>
        public static Vector3 ToWorldDirection(Vector2 input, float cameraYawDegrees) =>
            Vector3.ClampMagnitude(Quaternion.Euler(0f, cameraYawDegrees, 0f) * new Vector3(input.x, 0f, input.y), 1f);

        void OnEnable()
        {
            InputActionUtility.SetEnabled(true, moveAction, takeoverAction);
            if (takeoverAction != null)
                takeoverAction.action.performed += OnTakeover;
        }

        void OnDisable()
        {
            if (takeoverAction != null)
                takeoverAction.action.performed -= OnTakeover;
            InputActionUtility.SetEnabled(false, moveAction, takeoverAction);
            wasDriving = false;
            SetIntent(Vector3.zero);
        }

        void Update()
        {
            if (activeCharacter == null)
                return;

            var driving = activeCharacter.IsDriving;
            var input = InputActionUtility.Read<Vector2>(moveAction);
            if (driving && !wasDriving)
                waitingForRelease = true;
            wasDriving = driving;
            if (waitingForRelease && input == Vector2.zero)
                waitingForRelease = false;

            var steer = driving && !waitingForRelease && viewCamera != null;
            SetIntent(steer ? ToWorldDirection(input, viewCamera.transform.eulerAngles.y) : Vector3.zero);
        }

        void SetIntent(Vector3 direction)
        {
            if (activeCharacter != null && activeCharacter.Unit != null)
                activeCharacter.Unit.SetMoveIntent(direction);
        }

        void OnTakeover(InputAction.CallbackContext context)
        {
            if (activeCharacter != null)
                activeCharacter.ToggleTakeover();
        }
    }
}
```

- [ ] **Step 5: Rename the references in `PlayerCommandInput`**

In `Assets/_Project/Scripts/Controls/PlayerCommandInput.cs`:

(a) Replace `using UnityEngine.InputSystem;` with:

```csharp
using UnityEngine.InputSystem;
using UnityEngine.Serialization;
```

(b) In the class summary, replace `/// In real time the order goes to the primary character; while paused (or without a primary character) it goes to` with `/// In real time the order goes to the active character; while paused (or without an active character) it goes to`.

(c) Replace `        [SerializeField] PrimaryCharacter primary;` with `        [SerializeField, FormerlySerializedAs("primary")] ActiveCharacter activeCharacter;`.

(d) Replace `            PrimaryCharacter primaryCharacter = null)` with `            ActiveCharacter active = null)`, and `            primary = primaryCharacter;` with `            activeCharacter = active;`.

(e) Replace the `OrderedUnits` comment and method with:

```csharp
        // Who a ground or enemy click orders: the active character in real time; the selection while paused or
        // when there is no active character that can act.
        List<CommandableUnit> OrderedUnits()
        {
            var paused = tacticalPause != null && tacticalPause.IsPaused;
            if (paused || activeCharacter == null || !activeCharacter.HasUnit)
                return SelectedUnits();
            orderedUnits.Clear();
            orderedUnits.Add(activeCharacter.Unit);
            return orderedUnits;
        }
```

- [ ] **Step 6: Rename the references in `TacticalCameraController`**

In `Assets/_Project/Scripts/CameraControl/TacticalCameraController.cs`:

(a) Replace `using UnityEngine.InputSystem;` with:

```csharp
using UnityEngine.InputSystem;
using UnityEngine.Serialization;
```

(b) In the summary replace `/// tactical pause. Lives on the pivot; the camera is a child. While the primary character is being driven` with `/// tactical pause. Lives on the pivot; the camera is a child. While the active character is being driven`.

(c) Replace `        [SerializeField] PrimaryCharacter primary;` with `        [SerializeField, FormerlySerializedAs("primary")] ActiveCharacter activeCharacter;`.

(d) Replace `        // How quickly the pivot catches up with the primary character while following (higher is tighter).` with `        // How quickly the pivot catches up with the active character while following (higher is tighter).`.

(e) Replace `            PrimaryCharacter primaryCharacter = null)` with `            ActiveCharacter active = null)`, and `            primary = primaryCharacter;` with `            activeCharacter = active;`.

(f) Replace:

```csharp
            if (primary != null && primary.IsDriving)
            {
                // Takeover: follow the primary character and ignore pan. Always eased, so after a pause the camera
                // glides back from wherever it was panned.
                var target = primary.Unit.transform.position;
```

with:

```csharp
            if (activeCharacter != null && activeCharacter.IsDriving)
            {
                // Takeover: follow the active character and ignore pan. Always eased, so after a pause the camera
                // glides back from wherever it was panned.
                var target = activeCharacter.Unit.transform.position;
```

- [ ] **Step 7: Rename the references in `PrototypeHud`**

In `Assets/_Project/Scripts/DebugUI/PrototypeHud.cs`:

(a) Replace `using UnityEngine;` with:

```csharp
using UnityEngine;
using UnityEngine.Serialization;
```

(b) Replace the `ControlHints` constant with:

```csharp
        const string ControlHints =
            "WASD: pan camera   Q/E: rotate   Right-drag: rotate/tilt   Wheel: zoom   V: takeover (WASD moves character)\n" +
            "Left-click unit: select (Shift: add/remove)   Left-drag: box select   Esc: clear selection\n" +
            "Left-click ground/dummy: controlled character moves/attacks (paused: selected units)   Shift: queue   X: stop selected\n" +
            "Space: tactical pause";
```

(c) Replace `        [SerializeField] PrimaryCharacter primary;` with `        [SerializeField, FormerlySerializedAs("primary")] ActiveCharacter activeCharacter;`.

(d) Replace the `DescribePrimary` summary and method with:

```csharp
        /// <summary>One status line for the active character, such as "Controlled: Ana | Takeover ON (V) | Manual control".</summary>
        internal static string DescribeActive(string unitName, bool takeoverOn, bool isPaused, bool hasOrders)
        {
            var mode = !takeoverOn ? "Takeover OFF (V)" : isPaused ? "Takeover ON (after pause)" : "Takeover ON (V)";
            var activity = hasOrders ? "Following orders" : takeoverOn && !isPaused ? "Manual control" : "Idle";
            return $"Controlled: {unitName} | {mode} | {activity}";
        }
```

(e) In `OnGUI`, replace:

```csharp
            if (primary != null && primary.HasUnit)
            {
                var unit = primary.Unit;
                GUI.Label(new Rect(10f, 135f, 640f, 22f),
                    DescribePrimary(unit.name, primary.IsTakeoverOn, primary.IsPaused, unit.CurrentCommand != null));
            }
```

with:

```csharp
            if (activeCharacter != null && activeCharacter.HasUnit)
            {
                var unit = activeCharacter.Unit;
                GUI.Label(new Rect(10f, 135f, 640f, 22f),
                    DescribeActive(unit.name, activeCharacter.IsTakeoverOn, activeCharacter.IsPaused, unit.CurrentCommand != null));
            }
```

- [ ] **Step 8: Check nothing still names the old class**

Run: `grep -rn "PrimaryCharacter\|DescribePrimary\|\bprimary\b *[=;.)]" Assets/_Project/Scripts` → expected: no output.

- [ ] **Step 9: Run all tests to verify they pass**

Run: `Tools/run-tests.sh EditMode` → expected `total="123" passed="123"`, `EXIT=0`.
Run: `Tools/run-tests.sh PlayMode` → expected `total="108" passed="108"`, `EXIT=0`. The scene tests prove the scene still loads its wiring through the kept GUID and `FormerlySerializedAs`.

- [ ] **Step 10: Commit**

```bash
git add -A Assets/_Project/Scripts Assets/_Project/Tests
git status --short   # expect renames (R) for both files and their .meta, plus the modified files; nothing else
git commit -m "Rename PrimaryCharacter to ActiveCharacter

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Cycling over eligible friendlies (`ControlCycle`, `ActiveCharacter.Cycle`)

**Files:**
- Create: `Assets/_Project/Scripts/Controls/ControlCycle.cs`
- Modify: `Assets/_Project/Scripts/Controls/ActiveCharacter.cs`
- Test: `Assets/_Project/Tests/EditMode/ControlCycleTests.cs` (new), `Assets/_Project/Tests/EditMode/ActiveCharacterTests.cs`, `Assets/_Project/Tests/PlayMode/PlayerCommandInputTests.cs`

**Interfaces:**
- Consumes: `UnitSelection.Roster` (`IReadOnlyList<SelectableUnit>`), `SelectableUnit.Unit`, `Health.IsAlive`.
- Produces:
  - `public static class ControlCycle { public static int NextIndex(int count, int current, int direction, Func<int, bool> isEligible); }`
  - `ActiveCharacter`: `internal void Initialize(CommandableUnit activeUnit, TacticalPause pause, UnitSelection unitSelection = null)`, `public void SetUnit(CommandableUnit newUnit)`, `public bool Cycle(int direction)` (+1 next, −1 previous; true if the unit changed), `public static bool IsEligible(SelectableUnit candidate)`. Serialized field `selection`.

- [ ] **Step 1: Write the failing `ControlCycle` tests**

Create `Assets/_Project/Tests/EditMode/ControlCycleTests.cs`:

```csharp
using System;
using NUnit.Framework;

namespace Blackglass.Tests
{
    public class ControlCycleTests
    {
        static bool All(int index) => true;

        [TestCase(3, 0, 1, 1)]
        [TestCase(3, 2, 1, 0)]
        [TestCase(3, 0, -1, 2)]
        [TestCase(3, 1, -1, 0)]
        [TestCase(4, 3, 1, 0)]
        [TestCase(4, 0, -1, 3)]
        public void NextIndex_AllEligible_StepsAndWraps(int count, int current, int direction, int expected)
        {
            Assert.That(ControlCycle.NextIndex(count, current, direction, All), Is.EqualTo(expected));
        }

        // Entries 0 and 2 of 4 are eligible.
        [TestCase(0, 1, 2)]
        [TestCase(2, 1, 0)]
        [TestCase(0, -1, 2)]
        [TestCase(2, -1, 0)]
        public void NextIndex_SkipsIneligibleEntries(int current, int direction, int expected)
        {
            Assert.That(ControlCycle.NextIndex(4, current, direction, i => i == 0 || i == 2), Is.EqualTo(expected));
        }

        [Test]
        public void NextIndex_OnlyCurrentEligible_ReturnsCurrent()
        {
            Assert.That(ControlCycle.NextIndex(3, 1, 1, i => i == 1), Is.EqualTo(1));
            Assert.That(ControlCycle.NextIndex(3, 1, -1, i => i == 1), Is.EqualTo(1));
        }

        [Test]
        public void NextIndex_NothingEligible_ReturnsMinusOne()
        {
            Assert.That(ControlCycle.NextIndex(3, 0, 1, i => false), Is.EqualTo(-1));
        }

        [Test]
        public void NextIndex_EmptyList_ReturnsMinusOne()
        {
            Assert.That(ControlCycle.NextIndex(0, -1, 1, All), Is.EqualTo(-1));
            Assert.That(ControlCycle.NextIndex(0, -1, -1, All), Is.EqualTo(-1));
        }

        [TestCase(-1)]
        [TestCase(3)]
        public void NextIndex_CurrentOutsideTheList_Forward_StartsAtTheFirstEligible(int current)
        {
            Assert.That(ControlCycle.NextIndex(3, current, 1, All), Is.EqualTo(0));
        }

        [TestCase(-1)]
        [TestCase(3)]
        public void NextIndex_CurrentOutsideTheList_Backward_StartsAtTheLastEligible(int current)
        {
            Assert.That(ControlCycle.NextIndex(3, current, -1, All), Is.EqualTo(2));
        }

        [Test]
        public void NextIndex_IneligibleCurrent_MovesToAnotherEligibleEntry()
        {
            Assert.That(ControlCycle.NextIndex(3, 0, 1, i => i == 2), Is.EqualTo(2));
            Assert.That(ControlCycle.NextIndex(3, 0, -1, i => i == 2), Is.EqualTo(2));
        }

        [Test]
        public void NextIndex_IneligibleCurrent_AndNothingElse_ReturnsMinusOne()
        {
            Assert.That(ControlCycle.NextIndex(1, 0, 1, i => false), Is.EqualTo(-1));
        }

        [TestCase(0)]
        [TestCase(2)]
        public void NextIndex_DirectionOtherThanPlusOrMinusOne_Throws(int direction)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => ControlCycle.NextIndex(3, 0, direction, All));
        }

        [Test]
        public void NextIndex_NegativeCount_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => ControlCycle.NextIndex(-1, 0, 1, All));
        }

        [Test]
        public void NextIndex_NullPredicate_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => ControlCycle.NextIndex(3, 0, 1, null));
        }
    }
}
```

(23 test cases.)

- [ ] **Step 2: Write the failing `ActiveCharacter` cycling tests**

In `Assets/_Project/Tests/EditMode/ActiveCharacterTests.cs`:

(a) Replace the `using` block with:

```csharp
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
```

(b) Add these fields after `CommandableUnit unit;`:

```csharp
        readonly List<GameObject> squadHosts = new List<GameObject>();
        UnitSelection selection;
        SelectableUnit[] squad;
```

(c) In `TearDown`, before `Time.timeScale = 1f;`, add:

```csharp
            foreach (var host in squadHosts)
            {
                if (host != null)
                    Object.DestroyImmediate(host);
            }
            squadHosts.Clear();
```

(d) Append these members at the end of the class (after `IsPaused_FalseWithoutAPauseService`):

```csharp
        // --- Cycling. Three friendlies A, B, C in roster order; A is active.

        void CreateSquad()
        {
            squad = new[] { CreateFriendly("A"), CreateFriendly("B"), CreateFriendly("C") };
            selection = systems.AddComponent<UnitSelection>();
            selection.Initialize(squad);
            active.Initialize(squad[0].Unit, pause, selection);
        }

        SelectableUnit CreateFriendly(string name)
        {
            var host = new GameObject(name);
            squadHosts.Add(host);
            return host.AddComponent<SelectableUnit>();
        }

        // A dead unit that stays active, so the test checks Health and not just activeInHierarchy.
        static void KillWithoutDeactivating(GameObject host)
        {
            var health = host.AddComponent<Health>();
            var serialized = new SerializedObject(health);
            serialized.FindProperty("disableOnDeath").boolValue = false;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            health.TakeDamage(health.Max);
            Assert.That(health.IsAlive, Is.False);
            Assert.That(host.activeInHierarchy, Is.True);
        }

        [Test]
        public void Cycle_Forward_VisitsEachFriendly_AndWraps()
        {
            CreateSquad();
            Assert.That(active.Cycle(1), Is.True);
            Assert.That(active.Unit, Is.SameAs(squad[1].Unit));
            Assert.That(active.Cycle(1), Is.True);
            Assert.That(active.Unit, Is.SameAs(squad[2].Unit));
            Assert.That(active.Cycle(1), Is.True);
            Assert.That(active.Unit, Is.SameAs(squad[0].Unit));
        }

        [Test]
        public void Cycle_Backward_VisitsEachFriendly_AndWraps()
        {
            CreateSquad();
            Assert.That(active.Cycle(-1), Is.True);
            Assert.That(active.Unit, Is.SameAs(squad[2].Unit));
            Assert.That(active.Cycle(-1), Is.True);
            Assert.That(active.Unit, Is.SameAs(squad[1].Unit));
            Assert.That(active.Cycle(-1), Is.True);
            Assert.That(active.Unit, Is.SameAs(squad[0].Unit));
        }

        [Test]
        public void Cycle_SkipsAnInactiveFriendly()
        {
            CreateSquad();
            squad[1].gameObject.SetActive(false);
            Assert.That(active.Cycle(1), Is.True);
            Assert.That(active.Unit, Is.SameAs(squad[2].Unit));
        }

        [Test]
        public void Cycle_SkipsAFriendlyWithItsSelectableUnitDisabled()
        {
            CreateSquad();
            squad[1].enabled = false;
            active.Cycle(1);
            Assert.That(active.Unit, Is.SameAs(squad[2].Unit));
        }

        [Test]
        public void Cycle_SkipsAFriendlyWithItsCommandableUnitDisabled()
        {
            CreateSquad();
            squad[1].Unit.enabled = false;
            active.Cycle(1);
            Assert.That(active.Unit, Is.SameAs(squad[2].Unit));
        }

        [Test]
        public void Cycle_SkipsADeadFriendly()
        {
            CreateSquad();
            KillWithoutDeactivating(squad[1].gameObject);
            active.Cycle(1);
            Assert.That(active.Unit, Is.SameAs(squad[2].Unit));
        }

        [Test]
        public void Cycle_AFriendlyWithLivingHealth_IsEligible()
        {
            CreateSquad();
            squad[1].gameObject.AddComponent<Health>();
            active.Cycle(1);
            Assert.That(active.Unit, Is.SameAs(squad[1].Unit));
        }

        [Test]
        public void Cycle_OnlyOneEligible_KeepsItAndReturnsFalse()
        {
            CreateSquad();
            squad[1].gameObject.SetActive(false);
            squad[2].gameObject.SetActive(false);
            Assert.That(active.Cycle(1), Is.False);
            Assert.That(active.Unit, Is.SameAs(squad[0].Unit));
            Assert.That(active.Cycle(-1), Is.False);
            Assert.That(active.Unit, Is.SameAs(squad[0].Unit));
        }

        [Test]
        public void Cycle_NothingEligible_ReturnsFalse_AndKeepsTheUnit()
        {
            CreateSquad();
            foreach (var friendly in squad)
                friendly.gameObject.SetActive(false);
            Assert.That(active.Cycle(1), Is.False);
            Assert.That(active.Unit, Is.SameAs(squad[0].Unit));
        }

        [Test]
        public void Cycle_WithoutASelection_ReturnsFalse()
        {
            Assert.That(active.Cycle(1), Is.False);
            Assert.That(active.Unit, Is.SameAs(unit));
        }

        [Test]
        public void Cycle_EmptyRoster_ReturnsFalse()
        {
            selection = systems.AddComponent<UnitSelection>();
            active.Initialize(unit, pause, selection);
            Assert.That(active.Cycle(1), Is.False);
            Assert.That(active.Unit, Is.SameAs(unit));
        }

        [Test]
        public void Cycle_SkipsNullAndDestroyedRosterEntries()
        {
            var a = CreateFriendly("A");
            var b = CreateFriendly("B");
            var c = CreateFriendly("C");
            selection = systems.AddComponent<UnitSelection>();
            selection.Initialize(a, null, b, c);
            active.Initialize(a.Unit, pause, selection);
            Object.DestroyImmediate(b.gameObject);

            Assert.That(active.Cycle(1), Is.True);
            Assert.That(active.Unit, Is.SameAs(c.Unit));
        }

        [Test]
        public void Cycle_ActiveUnitNotInTheRoster_StartsFromTheEnds()
        {
            CreateSquad();
            active.SetUnit(unit);
            Assert.That(active.Cycle(1), Is.True);
            Assert.That(active.Unit, Is.SameAs(squad[0].Unit));
            active.SetUnit(unit);
            Assert.That(active.Cycle(-1), Is.True);
            Assert.That(active.Unit, Is.SameAs(squad[2].Unit));
        }

        [Test]
        public void Cycle_FromAnIneligibleActiveUnit_MovesOn()
        {
            CreateSquad();
            squad[0].gameObject.SetActive(false);
            Assert.That(active.Cycle(1), Is.True);
            Assert.That(active.Unit, Is.SameAs(squad[1].Unit));
        }

        [Test]
        public void SetUnit_ChangesTheUnit_AndNullClearsIt()
        {
            CreateSquad();
            active.SetUnit(squad[2].Unit);
            Assert.That(active.Unit, Is.SameAs(squad[2].Unit));
            active.SetUnit(null);
            Assert.That(active.Unit, Is.Null);
            Assert.That(active.HasUnit, Is.False);
        }

        [Test]
        public void IsEligible_NullIsFalse()
        {
            Assert.That(ActiveCharacter.IsEligible(null), Is.False);
        }
```

(16 new tests; `ActiveCharacterTests` now has 27.)

- [ ] **Step 3: Write the click-after-switch test**

In `Assets/_Project/Tests/PlayMode/PlayerCommandInputTests.cs`, append after `RealTimeClick_WithThePrimaryUnitDisabled_OrdersTheSelection`:

```csharp
        [UnityTest]
        public IEnumerator RealTimeClickDummy_AfterSwitchingTheActiveCharacter_TheNewOneAttacks()
        {
            yield return null;
            MakeActive(unitA);
            activeCharacter.SetUnit(unitC.Unit);
            yield return LeftClickAt(ScreenPointOf(dummy));

            Assert.That(unitC.Unit.CurrentCommand, Is.TypeOf<AttackCommand>(), "The attack must come from the new active character");
            Assert.That(((AttackCommand)unitC.Unit.CurrentCommand).Target, Is.SameAs(dummy));
            Assert.That(unitA.Unit.CurrentCommand, Is.Null, "The previous active character must not attack");
        }
```

- [ ] **Step 4: Run the tests to verify they fail**

Run: `Tools/run-tests.sh EditMode` → expected `EXIT=3` with `error CS0103: The name 'ControlCycle' does not exist`, `CS1061 ... 'Cycle'`, `CS1501`/`CS1739` for the 3-argument `Initialize`.

- [ ] **Step 5: Implement `ControlCycle`**

Create `Assets/_Project/Scripts/Controls/ControlCycle.cs`:

```csharp
using System;

namespace Blackglass
{
    /// <summary>Picks the next entry when cycling through a list, skipping entries that are not eligible.</summary>
    public static class ControlCycle
    {
        /// <summary>
        /// Index of the next eligible entry after <paramref name="current"/> in <paramref name="direction"/> (+1 forward,
        /// -1 backward), wrapping around. Returns current when it is the only eligible entry, and -1 when no entry is
        /// eligible. A current outside 0..count-1 (for example -1: not in the list) starts before the first entry going
        /// forward and after the last going backward. Checks each entry at most once.
        /// </summary>
        public static int NextIndex(int count, int current, int direction, Func<int, bool> isEligible)
        {
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count), count, "Count cannot be negative.");
            if (direction != 1 && direction != -1)
                throw new ArgumentOutOfRangeException(nameof(direction), direction, "Direction must be +1 or -1.");
            if (isEligible == null)
                throw new ArgumentNullException(nameof(isEligible));

            var start = current >= 0 && current < count ? current : direction > 0 ? -1 : count;
            for (var step = 1; step <= count; step++)
            {
                var index = ((start + step * direction) % count + count) % count;
                if (isEligible(index))
                    return index;
            }
            return -1;
        }
    }
}
```

- [ ] **Step 6: Add cycling to `ActiveCharacter`**

Replace the whole content of `Assets/_Project/Scripts/Controls/ActiveCharacter.cs` with:

```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The friendly character the player currently controls directly, and whether takeover mode is on. Holds state
    /// only: DirectControlInput, click input, the camera, the marker and the HUD read it. Any eligible roster unit can be
    /// the active character; Cycle moves to the next or previous one. Takeover starts off (free mode) and can be toggled
    /// while paused; it takes effect when simulation runs again.
    /// </summary>
    public sealed class ActiveCharacter : MonoBehaviour
    {
        [SerializeField] CommandableUnit unit;
        [SerializeField] TacticalPause tacticalPause;
        // The roster to cycle through.
        [SerializeField] UnitSelection selection;

        public CommandableUnit Unit => unit;

        public bool IsTakeoverOn { get; private set; }

        /// <summary>
        /// True when there is an active character that can act: assigned, enabled and active. Uses enabled and
        /// activeInHierarchy (not isActiveAndEnabled), like UnitSelection, so it gives the same answer in EditMode.
        /// </summary>
        public bool HasUnit => unit != null && unit.enabled && unit.gameObject.activeInHierarchy;

        public bool IsPaused => tacticalPause != null && tacticalPause.IsPaused;

        /// <summary>True while WASD drives the active character: takeover on, simulation running, unit present.</summary>
        public bool IsDriving => IsTakeoverOn && !IsPaused && HasUnit;

        internal void Initialize(CommandableUnit activeUnit, TacticalPause pause, UnitSelection unitSelection = null)
        {
            unit = activeUnit;
            tacticalPause = pause;
            selection = unitSelection;
        }

        /// <summary>
        /// Whether a roster unit can become the active character: present, its SelectableUnit and CommandableUnit
        /// enabled, active in the hierarchy, and either without Health or alive.
        /// </summary>
        public static bool IsEligible(SelectableUnit candidate)
        {
            if (candidate == null || !candidate.enabled || !candidate.gameObject.activeInHierarchy)
                return false;
            var commandable = candidate.Unit;
            if (commandable == null || !commandable.enabled)
                return false;
            var health = candidate.GetComponent<Health>();
            return health == null || health.IsAlive;
        }

        /// <summary>Makes this unit the active character; null means none. Does not check eligibility.</summary>
        public void SetUnit(CommandableUnit newUnit) => unit = newUnit;

        /// <summary>
        /// Makes the next (+1) or previous (-1) eligible roster unit the active character, wrapping around. Returns true
        /// if the active character changed; false when there is no roster, nothing is eligible, or the only eligible
        /// unit is already active.
        /// </summary>
        public bool Cycle(int direction)
        {
            if (selection == null)
                return false;
            var roster = selection.Roster;
            var current = -1;
            for (var i = 0; i < roster.Count; i++)
            {
                if (roster[i] != null && roster[i].Unit == unit)
                {
                    current = i;
                    break;
                }
            }

            var next = ControlCycle.NextIndex(roster.Count, current, direction, i => IsEligible(roster[i]));
            if (next < 0 || roster[next].Unit == unit)
                return false;
            SetUnit(roster[next].Unit);
            return true;
        }

        public void SetTakeover(bool on) => IsTakeoverOn = on;

        public void ToggleTakeover() => IsTakeoverOn = !IsTakeoverOn;
    }
}
```

- [ ] **Step 7: Run all tests to verify they pass**

Run: `Tools/run-tests.sh EditMode` → expected `total="162" passed="162"`, `EXIT=0`.
Run: `Tools/run-tests.sh PlayMode` → expected `total="109" passed="109"`, `EXIT=0`.

- [ ] **Step 8: Commit**

```bash
git add Assets/_Project/Scripts/Controls/ControlCycle.cs Assets/_Project/Scripts/Controls/ControlCycle.cs.meta \
  Assets/_Project/Scripts/Controls/ActiveCharacter.cs \
  Assets/_Project/Tests/EditMode/ControlCycleTests.cs Assets/_Project/Tests/EditMode/ControlCycleTests.cs.meta \
  Assets/_Project/Tests/EditMode/ActiveCharacterTests.cs Assets/_Project/Tests/PlayMode/PlayerCommandInputTests.cs
git status --short   # nothing else staged; untracked .meta files for the two new .cs files must be included above
git commit -m "Let the active character cycle through eligible friendlies

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

If Unity has not generated the `.meta` files yet (they appear after the first test run), the test run in Step 7 creates them; add them before committing.

---

### Task 3: Tab / Shift+Tab input and direct-control handover

**Files:**
- Modify: `Assets/_Project/Input/BlackglassControls.inputactions`
- Modify: `Assets/_Project/Scripts/Controls/DirectControlInput.cs`
- Modify: `Assets/_Project/Scripts/DebugUI/PrototypeHud.cs` (hint line)
- Test: `Assets/_Project/Tests/PlayMode/DirectControlInputTests.cs`

**Interfaces:**
- Consumes: `ActiveCharacter.Cycle(int)`, `ActiveCharacter.IsEligible(SelectableUnit)`, `ActiveCharacter.Initialize(unit, pause, selection)`, `UnitSelection.Select(SelectableUnit)`.
- Produces: actions `Character/CycleCharacter` (Tab) and `Character/CycleReverse` (Shift). `DirectControlInput.Initialize(ActiveCharacter active, Camera camera, InputActionReference move, InputActionReference takeover, UnitSelection unitSelection = null, InputActionReference cycle = null, InputActionReference reverse = null)`. Serialized fields `selection`, `cycleAction`, `reverseAction` (names used by the scene builder in Task 6).

- [ ] **Step 1: Add the input actions**

In `Assets/_Project/Input/BlackglassControls.inputactions`, in the `Character` map:

(a) Replace the `Takeover` action line:

```json
                { "name": "Takeover", "type": "Button", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9f03", "expectedControlType": "Button", "processors": "", "interactions": "", "initialStateCheck": false }
```

with:

```json
                { "name": "Takeover", "type": "Button", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9f03", "expectedControlType": "Button", "processors": "", "interactions": "", "initialStateCheck": false },
                { "name": "CycleCharacter", "type": "Button", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9f04", "expectedControlType": "Button", "processors": "", "interactions": "", "initialStateCheck": false },
                { "name": "CycleReverse", "type": "Button", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9f05", "expectedControlType": "Button", "processors": "", "interactions": "", "initialStateCheck": false }
```

(b) Replace the V binding line:

```json
                { "name": "", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9f16", "path": "<Keyboard>/v", "interactions": "", "processors": "", "groups": "", "action": "Takeover", "isComposite": false, "isPartOfComposite": false }
```

with:

```json
                { "name": "", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9f16", "path": "<Keyboard>/v", "interactions": "", "processors": "", "groups": "", "action": "Takeover", "isComposite": false, "isPartOfComposite": false },
                { "name": "", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9f17", "path": "<Keyboard>/tab", "interactions": "", "processors": "", "groups": "", "action": "CycleCharacter", "isComposite": false, "isPartOfComposite": false },
                { "name": "", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9f18", "path": "<Keyboard>/leftShift", "interactions": "", "processors": "", "groups": "", "action": "CycleReverse", "isComposite": false, "isPartOfComposite": false },
                { "name": "", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9f19", "path": "<Keyboard>/rightShift", "interactions": "", "processors": "", "groups": "", "action": "CycleReverse", "isComposite": false, "isPartOfComposite": false }
```

Check the file is still valid JSON: `python -m json.tool Assets/_Project/Input/BlackglassControls.inputactions > /dev/null && echo OK` → `OK`.

- [ ] **Step 2: Rewire the test fixture with a three-friendly roster**

In `Assets/_Project/Tests/PlayMode/DirectControlInputTests.cs`:

(a) Add fields after `CommandableUnit companion;`:

```csharp
        CommandableUnit third;
        UnitSelection selection;
```

(b) Replace the body of `Setup()` from `world.CreateEnvironment();` to the end of the method with:

```csharp
            world.CreateEnvironment();
            primaryUnit = world.CreateFriendly(new Vector3(-6f, 0f, -6f)).Unit;
            companion = world.CreateFriendly(new Vector3(6f, 0f, -6f)).Unit;
            third = world.CreateFriendly(new Vector3(0f, 0f, -12f)).Unit;

            var cameraObject = world.Track(new GameObject("Camera"));
            cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 25f, -20f), Quaternion.Euler(50f, 0f, 0f));
            viewCamera = cameraObject.AddComponent<Camera>();

            var systems = world.Track(new GameObject("Systems"));
            systems.SetActive(false);
            pause = systems.AddComponent<TacticalPause>();
            selection = systems.AddComponent<UnitSelection>();
            selection.Initialize(SelectableOf(primaryUnit), SelectableOf(companion), SelectableOf(third));
            active = systems.AddComponent<ActiveCharacter>();
            active.Initialize(primaryUnit, pause, selection);
            var actions = TestControls.Load();
            input = systems.AddComponent<DirectControlInput>();
            input.Initialize(active, viewCamera,
                TestControls.Ref(actions, "Character/Move"),
                TestControls.Ref(actions, "Character/Takeover"),
                selection,
                TestControls.Ref(actions, "Character/CycleCharacter"),
                TestControls.Ref(actions, "Character/CycleReverse"));
            systems.SetActive(true);
        }

        static SelectableUnit SelectableOf(CommandableUnit unit) => unit.GetComponent<SelectableUnit>();
```

(c) Add a helper after `Hold`:

```csharp
        IEnumerator ShiftTab()
        {
            Press(keyboard.leftShiftKey);
            yield return null;
            yield return Tap(keyboard.tabKey);
            Release(keyboard.leftShiftKey);
            yield return null;
        }

        static float Moved(CommandableUnit unit, Vector3 from) => TestWorld.HorizontalDistance(unit.transform.position, from);
```

- [ ] **Step 3: Write the failing switching tests**

Append at the end of `DirectControlInputTests` (after `TakeoverCycling_KeepsBothQueues_AndRunsThemInOrder`):

```csharp
        // --- Switching the active character (Tab / Shift+Tab).

        [UnityTest]
        public IEnumerator Tab_MakesTheNextFriendlyActive_SelectsIt_AndWraps()
        {
            yield return null;
            selection.SetSelection(new[] { SelectableOf(primaryUnit), SelectableOf(companion), SelectableOf(third) });

            yield return Tap(keyboard.tabKey);
            Assert.That(active.Unit, Is.SameAs(companion));
            Assert.That(selection.Selected, Is.EqualTo(new[] { SelectableOf(companion) }), "Tab must select only the new active character");
            yield return Tap(keyboard.tabKey);
            Assert.That(active.Unit, Is.SameAs(third));
            yield return Tap(keyboard.tabKey);
            Assert.That(active.Unit, Is.SameAs(primaryUnit), "Tab did not wrap to the first friendly");
            Assert.That(selection.Selected, Is.EqualTo(new[] { SelectableOf(primaryUnit) }));
        }

        [UnityTest]
        public IEnumerator ShiftTab_GoesBackward_AndWraps()
        {
            yield return null;
            yield return ShiftTab();
            Assert.That(active.Unit, Is.SameAs(third), "Shift+Tab did not wrap to the last friendly");
            yield return ShiftTab();
            Assert.That(active.Unit, Is.SameAs(companion));
            yield return ShiftTab();
            Assert.That(active.Unit, Is.SameAs(primaryUnit));
            Assert.That(selection.Selected, Is.EqualTo(new[] { SelectableOf(primaryUnit) }));
        }

        [UnityTest]
        public IEnumerator Tab_SkipsInactiveAndDisabledFriendlies_AndKeepsTheLastEligibleOne()
        {
            yield return null;
            companion.gameObject.SetActive(false);
            yield return Tap(keyboard.tabKey);
            Assert.That(active.Unit, Is.SameAs(third), "Tab did not skip the inactive friendly");

            third.enabled = false;
            yield return Tap(keyboard.tabKey);
            Assert.That(active.Unit, Is.SameAs(primaryUnit), "Tab did not skip the disabled friendly");
            yield return Tap(keyboard.tabKey);
            Assert.That(active.Unit, Is.SameAs(primaryUnit), "With one eligible friendly, Tab must keep it");
            Assert.That(selection.Selected, Is.EqualTo(new[] { SelectableOf(primaryUnit) }));
        }

        [UnityTest]
        public IEnumerator TabWhileDriving_StopsTheOldCharacter_AndTheHeldKeyDrivesAnIdleNewOne()
        {
            yield return null;
            active.SetTakeover(true);
            yield return null;
            Press(keyboard.wKey);
            yield return new WaitForSecondsRealtime(0.2f);
            Assert.That(primaryUnit.MoveIntent, Is.Not.EqualTo(Vector3.zero), "Precondition: W drives the first friendly");

            yield return Tap(keyboard.tabKey);   // W stays held
            Assert.That(active.Unit, Is.SameAs(companion));
            Assert.That(primaryUnit.MoveIntent, Is.EqualTo(Vector3.zero), "The old character kept a stale move intent");
            var oldStoppedAt = primaryUnit.transform.position;
            var newStart = companion.transform.position;
            yield return new WaitForSecondsRealtime(0.4f);

            Assert.That(Moved(primaryUnit, oldStoppedAt), Is.LessThan(0.05f), "The old character kept moving");
            Assert.That(Moved(companion, newStart), Is.GreaterThan(0.5f), "The held key did not carry over to the idle new character");
            Release(keyboard.wKey);
            yield return null;
        }

        [UnityTest]
        public IEnumerator TabWhileWHeld_DoesNotWipeTheNewCharactersOrders_UntilPressedAgain()
        {
            yield return null;
            var first = new MoveCommand(new Vector3(6f, 0f, 8f));
            var second = new MoveCommand(new Vector3(0f, 0f, 8f));
            Assert.That(companion.Issue(first), Is.True);
            Assert.That(companion.Issue(second, IssueMode.Append), Is.True);
            active.SetTakeover(true);
            yield return null;
            Press(keyboard.wKey);
            yield return new WaitForSecondsRealtime(0.2f);

            yield return Tap(keyboard.tabKey);   // W stays held
            yield return new WaitForSecondsRealtime(0.3f);
            Assert.That(active.Unit, Is.SameAs(companion));
            Assert.That(companion.MoveIntent, Is.EqualTo(Vector3.zero), "A held key steered a character that had orders");
            Assert.That(companion.CurrentCommand, Is.SameAs(first), "A held key wiped the new character's orders");
            Assert.That(companion.PendingCommands, Is.EqualTo(new UnitCommand[] { second }));

            Release(keyboard.wKey);
            yield return null;
            yield return Hold(keyboard.wKey, 0.2f);
            Assert.That(companion.CurrentCommand, Is.Null, "A fresh press must take over the new character");
            Assert.That(companion.PendingCommands, Is.Empty);
        }

        [UnityTest]
        public IEnumerator Switching_KeepsTheOldCharactersOrders()
        {
            yield return null;
            var first = new MoveCommand(new Vector3(-6f, 0f, 8f));
            var second = new MoveCommand(new Vector3(-12f, 0f, 8f));
            Assert.That(primaryUnit.Issue(first), Is.True);
            Assert.That(primaryUnit.Issue(second, IssueMode.Append), Is.True);
            active.SetTakeover(true);
            yield return null;
            var start = primaryUnit.transform.position;

            yield return Tap(keyboard.tabKey);
            yield return new WaitForSecondsRealtime(0.3f);

            Assert.That(active.Unit, Is.SameAs(companion));
            Assert.That(primaryUnit.CurrentCommand, Is.SameAs(first), "Switching away cleared the old character's order");
            Assert.That(primaryUnit.PendingCommands, Is.EqualTo(new UnitCommand[] { second }));
            Assert.That(Moved(primaryUnit, start), Is.GreaterThan(0.5f), "The old character stopped following its orders");
        }

        [UnityTest]
        public IEnumerator TabWhilePaused_MovesNothing_KeepsEveryQueue_AndTheNewCharacterDrivesAfterResume()
        {
            yield return null;
            active.SetTakeover(true);
            pause.Pause();
            var (primaryFirst, primarySecond, companionFirst, companionSecond) = QueueOrdersForBoth();
            var units = new[] { primaryUnit, companion, third };
            var starts = new[] { primaryUnit.transform.position, companion.transform.position, third.transform.position };

            yield return Tap(keyboard.tabKey);
            yield return Tap(keyboard.tabKey);
            yield return new WaitForSecondsRealtime(0.3f);

            Assert.That(active.Unit, Is.SameAs(third));
            Assert.That(selection.Selected, Is.EqualTo(new[] { SelectableOf(third) }));
            for (var i = 0; i < units.Length; i++)
                Assert.That(Moved(units[i], starts[i]), Is.LessThan(0.01f), $"Friendly {i} moved while paused");
            Assert.That(primaryUnit.CurrentCommand, Is.SameAs(primaryFirst));
            Assert.That(primaryUnit.PendingCommands, Is.EqualTo(new UnitCommand[] { primarySecond }));
            Assert.That(companion.CurrentCommand, Is.SameAs(companionFirst));
            Assert.That(companion.PendingCommands, Is.EqualTo(new UnitCommand[] { companionSecond }));

            pause.Resume();
            yield return null;
            var thirdStart = third.transform.position;
            yield return Hold(keyboard.wKey, 0.3f);

            Assert.That(Moved(third, thirdStart), Is.GreaterThan(0.5f), "After resume, W must drive the character chosen while paused");
            Assert.That(primaryUnit.CurrentCommand, Is.Not.Null, "Driving the new character cancelled the first friendly's orders");
            Assert.That(companion.CurrentCommand, Is.Not.Null, "Driving the new character cancelled the companion's orders");
        }

        [UnityTest]
        public IEnumerator TabInFreeMode_SwitchesTheActiveCharacter_ButWMovesNobody()
        {
            yield return null;
            yield return Tap(keyboard.tabKey);
            Assert.That(active.Unit, Is.SameAs(companion));
            var units = new[] { primaryUnit, companion, third };
            var starts = new[] { primaryUnit.transform.position, companion.transform.position, third.transform.position };

            Press(keyboard.wKey);
            yield return new WaitForSecondsRealtime(0.3f);
            for (var i = 0; i < units.Length; i++)
            {
                Assert.That(units[i].MoveIntent, Is.EqualTo(Vector3.zero), $"Friendly {i} got a move intent in free mode");
                Assert.That(Moved(units[i], starts[i]), Is.LessThan(0.01f), $"Friendly {i} moved in free mode");
            }
            Release(keyboard.wKey);
            yield return null;
        }

        [UnityTest]
        public IEnumerator RapidTabbing_EndsInAConsistentState_WithoutErrors()
        {
            yield return null;
            active.SetTakeover(true);
            yield return null;
            Press(keyboard.wKey);
            yield return null;

            for (var i = 0; i < 50; i++)
            {
                var reverse = i % 3 == 0;
                if (reverse)
                {
                    Press(keyboard.leftShiftKey);
                    yield return null;
                }
                yield return Tap(keyboard.tabKey);
                if (reverse)
                {
                    Release(keyboard.leftShiftKey);
                    yield return null;
                }
            }

            // 33 steps forward and 17 back: net +16 over three friendlies, one step forward from the first.
            Assert.That(active.Unit, Is.SameAs(companion));
            Assert.That(selection.Selected, Is.EqualTo(new[] { SelectableOf(companion) }));
            Assert.That(primaryUnit.MoveIntent, Is.EqualTo(Vector3.zero), "A character switched away from kept its intent");
            Assert.That(third.MoveIntent, Is.EqualTo(Vector3.zero), "A character switched away from kept its intent");
            Release(keyboard.wKey);
            yield return null;
            Assert.That(companion.MoveIntent, Is.EqualTo(Vector3.zero));
            // Any error or exception logged meanwhile fails the test.
        }

        [UnityTest]
        public IEnumerator ActiveUnitDestroyed_WhileDriving_ThenTab_MovesOnWithoutErrors()
        {
            yield return null;
            active.SetTakeover(true);
            yield return null;
            Press(keyboard.wKey);
            yield return new WaitForSecondsRealtime(0.1f);

            Object.Destroy(primaryUnit.gameObject);
            yield return new WaitForSecondsRealtime(0.1f);
            Release(keyboard.wKey);
            yield return null;
            yield return Tap(keyboard.tabKey);

            Assert.That(active.Unit, Is.SameAs(companion));
            Assert.That(selection.Selected, Is.EqualTo(new[] { SelectableOf(companion) }));
            // Any error or exception logged meanwhile fails the test.
        }
```

(10 new tests.)

- [ ] **Step 4: Run the tests to verify they fail**

Run: `Tools/run-tests.sh PlayMode Blackglass.Tests.DirectControlInputTests` → expected `EXIT=3` with `error CS1501: No overload for method 'Initialize' takes 7 arguments`.

- [ ] **Step 5: Implement Tab and the handover in `DirectControlInput`**

Replace the whole content of `Assets/_Project/Scripts/Controls/DirectControlInput.cs` with:

```csharp
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Serialization;

namespace Blackglass
{
    /// <summary>
    /// Direct-control input for the active character. V toggles takeover. Tab makes the next eligible friendly the
    /// active character (Shift+Tab: the previous one) and selects it. While the active character is being driven
    /// (ActiveCharacter.IsDriving), WASD becomes a camera-relative move intent on its CommandableUnit; otherwise the
    /// intent is zero. Contains no movement rules: the unit decides what the intent means.
    /// Release gate: whenever driving starts (resume, or takeover turned on), keys already held are ignored until Move
    /// reads zero, so a key held from panning the camera cannot wipe orders just queued.
    /// Handover: when the active character changes, the old one's intent is zeroed in the same frame. A held key
    /// carries over to the new one only if it has no orders; otherwise the gate re-arms. Switching never touches orders.
    /// </summary>
    [DefaultExecutionOrder(-100)] // set the intent before units update in the same frame
    public sealed class DirectControlInput : MonoBehaviour
    {
        [SerializeField, FormerlySerializedAs("primary")] ActiveCharacter activeCharacter;
        [SerializeField] Camera viewCamera;
        // Tab selects the new active character here.
        [SerializeField] UnitSelection selection;

        [Header("Input")]
        [SerializeField] InputActionReference moveAction;
        [SerializeField] InputActionReference takeoverAction;
        [SerializeField] InputActionReference cycleAction;
        // Held while cycling to go backward (Shift).
        [SerializeField] InputActionReference reverseAction;

        CommandableUnit steeredUnit;
        bool wasDriving;
        bool waitingForRelease;

        internal void Initialize(ActiveCharacter active, Camera camera, InputActionReference move,
            InputActionReference takeover, UnitSelection unitSelection = null, InputActionReference cycle = null,
            InputActionReference reverse = null)
        {
            activeCharacter = active;
            viewCamera = camera;
            moveAction = move;
            takeoverAction = takeover;
            selection = unitSelection;
            cycleAction = cycle;
            reverseAction = reverse;
        }

        /// <summary>Turns movement input into a ground direction relative to the camera's yaw, at most length 1.</summary>
        public static Vector3 ToWorldDirection(Vector2 input, float cameraYawDegrees) =>
            Vector3.ClampMagnitude(Quaternion.Euler(0f, cameraYawDegrees, 0f) * new Vector3(input.x, 0f, input.y), 1f);

        void OnEnable()
        {
            InputActionUtility.SetEnabled(true, moveAction, takeoverAction, cycleAction, reverseAction);
            if (takeoverAction != null)
                takeoverAction.action.performed += OnTakeover;
            if (cycleAction != null)
                cycleAction.action.performed += OnCycle;
        }

        void OnDisable()
        {
            if (takeoverAction != null)
                takeoverAction.action.performed -= OnTakeover;
            if (cycleAction != null)
                cycleAction.action.performed -= OnCycle;
            InputActionUtility.SetEnabled(false, moveAction, takeoverAction, cycleAction, reverseAction);
            wasDriving = false;
            SetIntent(Vector3.zero);
        }

        void Update()
        {
            if (activeCharacter == null)
                return;

            var input = InputActionUtility.Read<Vector2>(moveAction);
            var unit = activeCharacter.Unit;
            if (unit != steeredUnit)
                HandOver(unit, input);

            var driving = activeCharacter.IsDriving;
            if (driving && !wasDriving)
                waitingForRelease = true;
            wasDriving = driving;
            if (waitingForRelease && input == Vector2.zero)
                waitingForRelease = false;

            var steer = driving && !waitingForRelease && viewCamera != null;
            SetIntent(steer ? ToWorldDirection(input, viewCamera.transform.eulerAngles.y) : Vector3.zero);
        }

        // The active character changed: release the old one now, and let a held key carry over only to an idle unit.
        void HandOver(CommandableUnit unit, Vector2 input)
        {
            if (steeredUnit != null)
                steeredUnit.SetMoveIntent(Vector3.zero);
            steeredUnit = unit;
            if (input != Vector2.zero && unit != null && unit.CurrentCommand != null)
                waitingForRelease = true;
        }

        void SetIntent(Vector3 direction)
        {
            if (steeredUnit != null)
                steeredUnit.SetMoveIntent(direction);
        }

        void OnTakeover(InputAction.CallbackContext context)
        {
            if (activeCharacter != null)
                activeCharacter.ToggleTakeover();
        }

        void OnCycle(InputAction.CallbackContext context)
        {
            if (activeCharacter == null)
                return;
            activeCharacter.Cycle(InputActionUtility.IsPressed(reverseAction) ? -1 : 1);
            var unit = activeCharacter.Unit;
            if (selection != null && unit != null && unit.TryGetComponent<SelectableUnit>(out var selectable)
                && ActiveCharacter.IsEligible(selectable))
                selection.Select(selectable);
        }
    }
}
```

- [ ] **Step 6: Add the Tab hint to the HUD**

In `Assets/_Project/Scripts/DebugUI/PrototypeHud.cs`, replace `            "Space: tactical pause";` with `            "Space: tactical pause   Tab / Shift+Tab: switch controlled character";`.

- [ ] **Step 7: Run all tests to verify they pass**

Run: `Tools/run-tests.sh PlayMode Blackglass.Tests.DirectControlInputTests` → expected 25 passed, `EXIT=0`.
Run: `Tools/run-tests.sh EditMode` → expected `total="162" passed="162"`, `EXIT=0`.
Run: `Tools/run-tests.sh PlayMode` → expected `total="119" passed="119"`, `EXIT=0`.

- [ ] **Step 8: Commit**

```bash
git add Assets/_Project/Input/BlackglassControls.inputactions Assets/_Project/Scripts/Controls/DirectControlInput.cs \
  Assets/_Project/Scripts/DebugUI/PrototypeHud.cs Assets/_Project/Tests/PlayMode/DirectControlInputTests.cs
git commit -m "Switch the active character with Tab and Shift+Tab

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Camera focus glide on switch

**Files:**
- Modify: `Assets/_Project/Scripts/CameraControl/TacticalCameraController.cs`
- Test: `Assets/_Project/Tests/PlayMode/TacticalCameraControllerTests.cs`

**Interfaces:**
- Consumes: `ActiveCharacter.Unit`, `HasUnit`, `IsDriving`, `SetUnit`.
- Produces: no new public API. Behaviour: one eased glide to a newly active unit when not driving; pan cancels it.

- [ ] **Step 1: Write the failing camera tests**

In `Assets/_Project/Tests/PlayMode/TacticalCameraControllerTests.cs`, append at the end of the class (after `Resume_EasesBackToTheActiveCharacter`):

```csharp
        // --- Focusing a newly active character.

        [UnityTest]
        public IEnumerator SwitchingWhilePaused_GlidesToTheNewCharacter()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(8f, 0f, 6f));
            pause.Pause();
            activeCharacter.Initialize(unit, pause);
            yield return new WaitForSecondsRealtime(1f);

            Assert.That(DistanceToRig(unit), Is.LessThan(0.1f), "The camera did not focus the new active character");
        }

        [UnityTest]
        public IEnumerator PanInput_CancelsTheGlide()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(8f, 0f, 6f));
            pause.Pause();
            activeCharacter.Initialize(unit, pause);
            yield return null;   // the glide starts
            Press(keyboard.sKey);
            yield return new WaitForSecondsRealtime(0.2f);
            Release(keyboard.sKey);
            yield return null;
            var afterPan = controller.transform.position;

            yield return new WaitForSecondsRealtime(0.6f);
            Assert.That(TestWorld.HorizontalDistance(controller.transform.position, afterPan), Is.LessThan(0.01f),
                "The glide carried on after the player panned");
            Assert.That(DistanceToRig(unit), Is.GreaterThan(1f));
        }

        [UnityTest]
        public IEnumerator EnablingTheCamera_DoesNotGlideToTheCurrentCharacter()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(8f, 0f, 6f));
            controller.gameObject.SetActive(false);
            activeCharacter.Initialize(unit, pause);
            controller.gameObject.SetActive(true);
            var start = controller.transform.position;

            yield return new WaitForSecondsRealtime(0.5f);
            Assert.That(TestWorld.HorizontalDistance(controller.transform.position, start), Is.LessThan(0.01f),
                "The camera moved to the character that was already active when it started");
        }

        [UnityTest]
        public IEnumerator SwitchWhileDriving_FollowsTheNewCharacter()
        {
            CreateDrivenUnit();
            var second = world.CreateUnit(new Vector3(-8f, 0f, -6f));
            yield return new WaitForSecondsRealtime(0.6f);

            activeCharacter.SetUnit(second);
            yield return new WaitForSecondsRealtime(0.8f);
            Assert.That(DistanceToRig(second), Is.LessThan(0.2f), "The camera did not follow the new active character");
        }

        [UnityTest]
        public IEnumerator RightDragDuringAGlide_StillRotates()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(8f, 0f, 6f));
            pause.Pause();
            Set(mouse.position, new Vector2(100f, 100f));
            yield return null;
            var startYaw = controller.Yaw;

            activeCharacter.Initialize(unit, pause);
            Press(mouse.rightButton);
            yield return null;
            Set(mouse.position, new Vector2(110f, 100f));   // crosses the 6 px threshold, no rotation yet
            yield return null;
            Set(mouse.position, new Vector2(210f, 100f));   // +100 px of drag
            yield return null;
            Release(mouse.rightButton);
            yield return new WaitForSecondsRealtime(1f);

            Assert.That(Mathf.DeltaAngle(startYaw, controller.Yaw), Is.EqualTo(25f).Within(0.5f), "Right-drag did not rotate during the glide");
            Assert.That(DistanceToRig(unit), Is.LessThan(0.1f), "Rotating stopped the glide");
        }

        [UnityTest]
        public IEnumerator GlideToAUnitOutsideTheBounds_StopsAtTheEdge()
        {
            var settings = new UnityEditor.SerializedObject(controller);
            settings.FindProperty("boundsHalfSize").floatValue = 5f;
            settings.ApplyModifiedPropertiesWithoutUndo();
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(8f, 0f, 6f));
            activeCharacter.Initialize(unit, pause);

            yield return new WaitForSecondsRealtime(1f);
            var position = controller.transform.position;
            Assert.That(position.x, Is.EqualTo(5f).Within(0.05f));
            Assert.That(position.z, Is.EqualTo(5f).Within(0.05f));
        }
```

(6 new tests. The file is already wrapped in `#if UNITY_EDITOR`, so `UnityEditor.SerializedObject` compiles.)

- [ ] **Step 2: Run the tests to verify they fail**

Run: `Tools/run-tests.sh PlayMode Blackglass.Tests.TacticalCameraControllerTests` → expected `EXIT=2`. `SwitchingWhilePaused_GlidesToTheNewCharacter`, `RightDragDuringAGlide_StillRotates` and `GlideToAUnitOutsideTheBounds_StopsAtTheEdge` fail (the camera never moves to the unit); the others pass.

- [ ] **Step 3: Implement the glide**

Replace the whole content of `Assets/_Project/Scripts/CameraControl/TacticalCameraController.cs` with:

```csharp
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Serialization;

namespace Blackglass
{
    /// <summary>
    /// Simple strategy camera orbiting a pivot on the ground. Runs entirely on unscaled time so it keeps working during
    /// tactical pause. Lives on the pivot; the camera is a child. While the active character is being driven
    /// (takeover, not paused) the pivot follows it and pan input is ignored; otherwise WASD pans freely. When the active
    /// character changes, the pivot glides to the new one once; any pan input ends the glide.
    /// </summary>
    public sealed class TacticalCameraController : MonoBehaviour
    {
        // A focus glide ends once the pivot is this close to its target.
        const float FocusArrivalDistance = 0.05f;

        [SerializeField] Camera viewCamera;
        [SerializeField, FormerlySerializedAs("primary")] ActiveCharacter activeCharacter;

        [Header("Input")]
        [SerializeField] InputActionReference panAction;
        [SerializeField] InputActionReference rotateAction;
        [SerializeField] InputActionReference rotateDragAction;
        [SerializeField] InputActionReference pointerPositionAction;
        [SerializeField] InputActionReference zoomAction;

        [Header("Tuning")]
        [SerializeField, Min(0f)] float panSpeed = 12f;
        [SerializeField, Min(1f)] float panReferenceDistance = 20f;
        [SerializeField, Min(0f)] float keyRotateSpeed = 90f;
        [SerializeField] float dragRotateDegreesPerPixel = 0.25f;
        [SerializeField] float dragTiltDegreesPerPixel = 0.25f;
        // Small accidental movements during a right-click don't move the camera.
        [SerializeField, Min(0f)] float dragThresholdPixels = ClickDragDetector.DefaultThresholdPixels;
        [SerializeField, Min(0f)] float zoomStep = 2f;
        [SerializeField, Min(1f)] float minDistance = 5f;
        [SerializeField, Min(1f)] float maxDistance = 40f;
        [SerializeField, Min(1f)] float distance = 20f;
        [SerializeField, Range(10f, 89f)] float pitch = 55f;
        [SerializeField, Range(10f, 89f)] float minPitch = 25f;
        [SerializeField, Range(10f, 89f)] float maxPitch = 85f;
        [SerializeField, Min(0f)] float boundsHalfSize = 25f;
        // How quickly the pivot catches up with the active character while following or focusing (higher is tighter).
        [SerializeField, Min(0f)] float followSharpness = 10f;

        ClickDragDetector dragDetector;
        Vector2 lastPointerPosition;
        int pendingZoomSteps;
        CommandableUnit lastSeenUnit;
        bool isFocusing;

        public float Distance => distance;
        public float Yaw => transform.eulerAngles.y;
        public float Pitch => pitch;

        internal void Initialize(Camera camera, InputActionReference pan, InputActionReference rotate,
            InputActionReference rotateDrag, InputActionReference pointerPosition, InputActionReference zoom,
            ActiveCharacter active = null)
        {
            viewCamera = camera;
            panAction = pan;
            rotateAction = rotate;
            rotateDragAction = rotateDrag;
            pointerPositionAction = pointerPosition;
            zoomAction = zoom;
            activeCharacter = active;
            lastSeenUnit = CurrentUnit;
        }

        // The active character's unit, or null when there is none that can act.
        CommandableUnit CurrentUnit => activeCharacter != null && activeCharacter.HasUnit ? activeCharacter.Unit : null;

        void Awake() => dragDetector = new ClickDragDetector(dragThresholdPixels);

        void OnEnable()
        {
            InputActionUtility.SetEnabled(true, panAction, rotateAction, rotateDragAction, pointerPositionAction, zoomAction);
            if (zoomAction != null)
                zoomAction.action.performed += OnZoom;
            if (rotateDragAction != null)
            {
                rotateDragAction.action.started += OnRotateDragStarted;
                rotateDragAction.action.canceled += OnRotateDragEnded;
            }
            // The character already active when the camera starts is not a switch: no glide.
            lastSeenUnit = CurrentUnit;
            isFocusing = false;
            ApplyCameraPose();
        }

        void OnDisable()
        {
            if (zoomAction != null)
                zoomAction.action.performed -= OnZoom;
            if (rotateDragAction != null)
            {
                rotateDragAction.action.started -= OnRotateDragStarted;
                rotateDragAction.action.canceled -= OnRotateDragEnded;
            }
            InputActionUtility.SetEnabled(false, panAction, rotateAction, rotateDragAction, pointerPositionAction, zoomAction);
        }

        void Update()
        {
            var deltaTime = Time.unscaledDeltaTime;

            if (pendingZoomSteps != 0)
            {
                distance = Mathf.Clamp(distance - pendingZoomSteps * zoomStep, minDistance, maxDistance);
                pendingZoomSteps = 0;
            }

            var yaw = transform.eulerAngles.y + InputActionUtility.Read<float>(rotateAction) * keyRotateSpeed * deltaTime;
            if (dragDetector.IsPressed)
            {
                var pointer = InputActionUtility.Read<Vector2>(pointerPositionAction);
                var wasDragging = dragDetector.IsDragging;
                dragDetector.Track(pointer);
                if (wasDragging)
                {
                    var delta = pointer - lastPointerPosition;
                    yaw += delta.x * dragRotateDegreesPerPixel;
                    // Dragging up tilts toward the horizon; dragging down looks more straight down.
                    pitch = Mathf.Clamp(pitch - delta.y * dragTiltDegreesPerPixel, minPitch, maxPitch);
                }
                lastPointerPosition = pointer;
            }

            var unit = CurrentUnit;
            if (unit != lastSeenUnit)
            {
                lastSeenUnit = unit;
                isFocusing = unit != null;
            }

            var rotation = Quaternion.Euler(0f, yaw, 0f);
            Vector3 position;
            if (activeCharacter != null && activeCharacter.IsDriving)
            {
                // Takeover: follow the active character and ignore pan. Always eased, so after a pause or a switch the
                // camera glides over from wherever it was.
                isFocusing = false;
                position = EaseTowards(GroundTarget(activeCharacter.Unit), deltaTime);
            }
            else
            {
                var pan = InputActionUtility.Read<Vector2>(panAction);
                if (pan != Vector2.zero)
                    isFocusing = false;
                if (isFocusing)
                {
                    var target = ClampToBounds(GroundTarget(unit));
                    position = EaseTowards(target, deltaTime);
                    if ((position - target).sqrMagnitude < FocusArrivalDistance * FocusArrivalDistance)
                        isFocusing = false;
                }
                else
                {
                    var speed = panSpeed * (distance / panReferenceDistance);
                    position = transform.position + rotation * new Vector3(pan.x, 0f, pan.y) * (speed * deltaTime);
                }
            }
            position = ClampToBounds(position);

            transform.SetPositionAndRotation(position, rotation);
            ApplyCameraPose();
        }

        // A unit's position at the pivot's height.
        Vector3 GroundTarget(Component unit)
        {
            var target = unit.transform.position;
            target.y = transform.position.y;
            return target;
        }

        Vector3 EaseTowards(Vector3 target, float deltaTime) =>
            Vector3.Lerp(transform.position, target, 1f - Mathf.Exp(-followSharpness * deltaTime));

        Vector3 ClampToBounds(Vector3 position)
        {
            position.x = Mathf.Clamp(position.x, -boundsHalfSize, boundsHalfSize);
            position.z = Mathf.Clamp(position.z, -boundsHalfSize, boundsHalfSize);
            return position;
        }

        void OnZoom(InputAction.CallbackContext context)
        {
            var scroll = context.ReadValue<float>();
            if (scroll > 0f)
                pendingZoomSteps++;
            else if (scroll < 0f)
                pendingZoomSteps--;
        }

        void OnRotateDragStarted(InputAction.CallbackContext context)
        {
            lastPointerPosition = InputActionUtility.Read<Vector2>(pointerPositionAction);
            dragDetector.Press(lastPointerPosition);
        }

        void OnRotateDragEnded(InputAction.CallbackContext context) =>
            dragDetector.Release(InputActionUtility.Read<Vector2>(pointerPositionAction));

        void ApplyCameraPose()
        {
            if (viewCamera == null)
                return;
            var localRotation = Quaternion.Euler(pitch, 0f, 0f);
            viewCamera.transform.SetLocalPositionAndRotation(localRotation * new Vector3(0f, 0f, -distance), localRotation);
        }
    }
}
```

- [ ] **Step 4: Run all tests to verify they pass**

Run: `Tools/run-tests.sh PlayMode Blackglass.Tests.TacticalCameraControllerTests` → expected all passed, `EXIT=0` (including the Phase 3 follow tests, e.g. `TakeoverOff_PansFreely_AndDoesNotFollow`, where the held W cancels the glide on its first frame).
Run: `Tools/run-tests.sh PlayMode` → expected `total="125" passed="125"`, `EXIT=0`.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/CameraControl/TacticalCameraController.cs Assets/_Project/Tests/PlayMode/TacticalCameraControllerTests.cs
git commit -m "Glide the camera to a newly active character

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Marker that follows the active character

**Files:**
- Create: `Assets/_Project/Scripts/DebugUI/ActiveCharacterMarker.cs`
- Test: `Assets/_Project/Tests/PlayMode/ActiveCharacterMarkerTests.cs` (new)

**Interfaces:**
- Consumes: `ActiveCharacter.HasUnit`, `Unit`, `SetUnit`.
- Produces: `public sealed class ActiveCharacterMarker : MonoBehaviour` with serialized `activeCharacter`, `visual`, `height` (default 1.6) and `internal void Initialize(ActiveCharacter active, GameObject visualObject)` (field names used by the scene builder in Task 6).

- [ ] **Step 1: Write the failing marker tests**

Create `Assets/_Project/Tests/PlayMode/ActiveCharacterMarkerTests.cs`:

```csharp
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class ActiveCharacterMarkerTests
    {
        TestWorld world;
        ActiveCharacter active;
        ActiveCharacterMarker marker;
        GameObject visual;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            world.CreateEnvironment();
            active = world.Track(new GameObject("Systems")).AddComponent<ActiveCharacter>();
            var markerObject = world.Track(new GameObject("ActiveMarker"));
            markerObject.SetActive(false);
            visual = new GameObject("Visual");
            visual.transform.SetParent(markerObject.transform, false);
            marker = markerObject.AddComponent<ActiveCharacterMarker>();
            marker.Initialize(active, visual);
            markerObject.SetActive(true);
        }

        [TearDown]
        public void TearDown() => world.Dispose();

        void AssertAbove(Component unit) =>
            Assert.That(Vector3.Distance(marker.transform.position, unit.transform.position + Vector3.up * 1.6f),
                Is.LessThan(0.01f), "The marker is not above the active character");

        [UnityTest]
        public IEnumerator NoActiveCharacter_HidesTheVisual()
        {
            yield return null;
            Assert.That(visual.activeSelf, Is.False);
        }

        [UnityTest]
        public IEnumerator FloatsAboveTheActiveCharacter()
        {
            var unit = world.CreateUnit(new Vector3(3f, 0f, 4f));
            active.Initialize(unit, null);
            yield return null;

            Assert.That(visual.activeSelf, Is.True);
            AssertAbove(unit);
        }

        [UnityTest]
        public IEnumerator MovesToTheNewCharacterAfterASwitch()
        {
            var first = world.CreateUnit(new Vector3(3f, 0f, 4f));
            var second = world.CreateUnit(new Vector3(-5f, 0f, -2f));
            active.Initialize(first, null);
            yield return null;

            active.SetUnit(second);
            yield return null;
            AssertAbove(second);
        }

        [UnityTest]
        public IEnumerator DeactivatedUnit_HidesTheVisual()
        {
            var unit = world.CreateUnit(new Vector3(3f, 0f, 4f));
            active.Initialize(unit, null);
            yield return null;

            unit.gameObject.SetActive(false);
            yield return null;
            Assert.That(visual.activeSelf, Is.False);
        }

        [UnityTest]
        public IEnumerator DestroyedUnit_HidesTheVisual()
        {
            var unit = world.CreateUnit(new Vector3(3f, 0f, 4f));
            active.Initialize(unit, null);
            yield return null;

            Object.Destroy(unit.gameObject);
            yield return null;
            yield return null;
            Assert.That(visual.activeSelf, Is.False);
            // Any error or exception logged meanwhile fails the test.
        }
    }
}
```

(5 new tests.)

- [ ] **Step 2: Run the tests to verify they fail**

Run: `Tools/run-tests.sh PlayMode Blackglass.Tests.ActiveCharacterMarkerTests` → expected `EXIT=3` with `error CS0246: The type or namespace name 'ActiveCharacterMarker' could not be found`.

- [ ] **Step 3: Implement the marker**

Create `Assets/_Project/Scripts/DebugUI/ActiveCharacterMarker.cs`:

```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Debug feedback: keeps a visual floating above the active character's head, and hides it when there is none.
    /// Lives on its own object (not on a unit), so it follows whoever is active. Works while paused.
    /// </summary>
    public sealed class ActiveCharacterMarker : MonoBehaviour
    {
        [SerializeField] ActiveCharacter activeCharacter;
        [SerializeField] GameObject visual;
        // Height above the unit's pivot (the capsule's centre; it is 2 m tall).
        [SerializeField] float height = 1.6f;

        internal void Initialize(ActiveCharacter active, GameObject visualObject)
        {
            activeCharacter = active;
            visual = visualObject;
        }

        void LateUpdate()
        {
            var show = activeCharacter != null && activeCharacter.HasUnit;
            if (show)
                transform.position = activeCharacter.Unit.transform.position + Vector3.up * height;
            if (visual != null && visual.activeSelf != show)
                visual.SetActive(show);
        }
    }
}
```

- [ ] **Step 4: Run all tests to verify they pass**

Run: `Tools/run-tests.sh PlayMode Blackglass.Tests.ActiveCharacterMarkerTests` → expected 5 passed, `EXIT=0`.
Run: `Tools/run-tests.sh PlayMode` → expected `total="130" passed="130"`, `EXIT=0`.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/DebugUI/ActiveCharacterMarker.cs Assets/_Project/Scripts/DebugUI/ActiveCharacterMarker.cs.meta \
  Assets/_Project/Tests/PlayMode/ActiveCharacterMarkerTests.cs Assets/_Project/Tests/PlayMode/ActiveCharacterMarkerTests.cs.meta
git commit -m "Add a marker that follows the active character

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Wire the Prototype scene and update the scene tests

**Files:**
- Modify: `Assets/_Project/Tests/PlayMode/PrototypeSceneTests.cs`
- Create (temporary, never committed): `Assets/_Project/Editor/SwitchingSceneBuilder.cs`
- Modify (generated): `Assets/_Project/Scenes/Prototype.unity`
- Rename (generated): `Assets/_Project/Materials/PrimaryMarker.mat` (+`.meta`) → `ActiveMarker.mat` (+`.meta`), GUID kept

**Interfaces:**
- Consumes: serialized field names `ActiveCharacter.selection`; `DirectControlInput.selection`, `cycleAction`, `reverseAction`, `activeCharacter`; `PlayerCommandInput.activeCharacter`; `TacticalCameraController.activeCharacter`; `PrototypeHud.activeCharacter`; `ActiveCharacterMarker.activeCharacter`, `visual`.
- Produces: `Prototype.unity` with the roster wired into `ActiveCharacter`, Tab/Shift wired into `DirectControlInput`, `PrimaryMarker` removed from `FriendlyUnit_1`, and a root `ActiveMarker` with a collider-free `Visual` child.

- [ ] **Step 1: Update the scene tests**

In `Assets/_Project/Tests/PlayMode/PrototypeSceneTests.cs`:

(a) In `Scene_ContainsWiredSquad_AndRunsWithoutErrors`, replace the block from `var primary = Object.FindFirstObjectByType<ActiveCharacter>();` through `Assert.That(primary.Unit.transform.Find("PrimaryMarker"), Is.Not.Null, "Primary marker missing");` with:

```csharp
            var active = Object.FindFirstObjectByType<ActiveCharacter>();
            Assert.That(active, Is.Not.Null, "ActiveCharacter missing");
            Assert.That(active.Unit, Is.Not.Null, "ActiveCharacter has no unit");
            Assert.That(active.Unit.name, Is.EqualTo(FriendlyNames[0]), "The game starts controlling FriendlyUnit_1");
            Assert.That(active.HasUnit, Is.True);
            Assert.That(active.IsTakeoverOn, Is.False, "The game starts in free mode");
            Assert.That(active.Unit.transform.Find("PrimaryMarker"), Is.Null, "The old fixed marker is still on FriendlyUnit_1");
            var marker = Object.FindFirstObjectByType<ActiveCharacterMarker>();
            Assert.That(marker, Is.Not.Null, "ActiveMarker missing");
            Assert.That(marker.GetComponentsInChildren<Collider>(true), Is.Empty, "The marker must not block clicks");
            Assert.That(marker.GetComponentsInChildren<Renderer>(true), Is.Not.Empty, "The marker has no visual");
```

(b) Append at the end of `PrototypeSceneInputTests` (after `PausedSquadOrder_ThenTakeover_OnlyThePrimaryDropsItsOrders`):

```csharp
        [UnityTest]
        public IEnumerator Tab_InScene_SwitchesToTheNextFriendly_AndItsClicksAttack()
        {
            var mouse = InputSystem.AddDevice<Mouse>();
            var keyboard = InputSystem.AddDevice<Keyboard>();
            yield return LoadScene();
            var squad = PrototypeSceneTests.FindSquad();
            var dummy = Object.FindFirstObjectByType<Health>();
            var active = Object.FindFirstObjectByType<ActiveCharacter>();
            var selection = Object.FindFirstObjectByType<UnitSelection>();
            var marker = Object.FindFirstObjectByType<ActiveCharacterMarker>();

            yield return Tap(keyboard.tabKey);
            Assert.That(active.Unit, Is.SameAs(squad[1]), "Tab did not switch to FriendlyUnit_2");
            Assert.That(selection.Selected, Is.EqualTo(new[] { squad[1].GetComponent<SelectableUnit>() }));
            yield return new WaitForSecondsRealtime(1f);   // let the camera finish focusing before aiming the click

            Assert.That(TestWorld.HorizontalDistance(marker.transform.position, squad[1].transform.position), Is.LessThan(0.01f),
                "The marker did not move to the new active character");
            var dummyOnScreen = Camera.main.WorldToScreenPoint(dummy.transform.position);
            Assert.That(new Rect(0f, 0f, Screen.width, Screen.height).Contains(dummyOnScreen), Is.True,
                "Precondition: the dummy is on screen after the camera focused FriendlyUnit_2");
            yield return LeftClickAt(mouse, dummyOnScreen);

            Assert.That(squad[1].CurrentCommand, Is.TypeOf<AttackCommand>(), "The attack must come from FriendlyUnit_2");
            Assert.That(((AttackCommand)squad[1].CurrentCommand).Target, Is.SameAs(dummy));
            Assert.That(squad[0].CurrentCommand, Is.Null);
            Assert.That(squad[2].CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator ShiftTab_InScene_SwitchesBackToTheLastFriendly()
        {
            var keyboard = InputSystem.AddDevice<Keyboard>();
            yield return LoadScene();
            var squad = PrototypeSceneTests.FindSquad();
            var active = Object.FindFirstObjectByType<ActiveCharacter>();

            Press(keyboard.leftShiftKey);
            yield return null;
            yield return Tap(keyboard.tabKey);
            Release(keyboard.leftShiftKey);
            yield return null;

            Assert.That(active.Unit, Is.SameAs(squad[2]), "Shift+Tab did not switch to FriendlyUnit_3");
        }
```

(2 new tests.)

- [ ] **Step 2: Run the scene tests to verify they fail**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.PrototypeSceneTests|Blackglass.Tests.PrototypeSceneInputTests"` → expected `EXIT=2`: `Scene_ContainsWiredSquad_AndRunsWithoutErrors` fails ("The old fixed marker is still on FriendlyUnit_1"), and both new Tab tests fail (the scene's `DirectControlInput` has no cycle action yet, so the active unit stays FriendlyUnit_1).

- [ ] **Step 3: Create the temporary scene builder**

Create `Assets/_Project/Editor/SwitchingSceneBuilder.cs`:

```csharp
// TEMPORARY: rewires Prototype.unity for active character switching, then this file is deleted (never committed).
using System;
using System.Linq;
using Blackglass;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

public static class SwitchingSceneBuilder
{
    const string ScenePath = "Assets/_Project/Scenes/Prototype.unity";
    const string ControlsPath = "Assets/_Project/Input/BlackglassControls.inputactions";
    const string OldMaterialPath = "Assets/_Project/Materials/PrimaryMarker.mat";
    const string MaterialPath = "Assets/_Project/Materials/ActiveMarker.mat";

    public static void Build()
    {
        if (AssetDatabase.LoadAssetAtPath<Material>(OldMaterialPath) != null)
        {
            var renameError = AssetDatabase.RenameAsset(OldMaterialPath, "ActiveMarker");
            if (!string.IsNullOrEmpty(renameError))
                throw new Exception("Could not rename the marker material: " + renameError);
        }
        var material = Require(AssetDatabase.LoadAssetAtPath<Material>(MaterialPath), MaterialPath);

        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var systems = Require(GameObject.Find("Systems"), "Systems");
        var firstFriendly = Require(GameObject.Find("FriendlyUnit_1"), "FriendlyUnit_1");
        var active = Require(systems.GetComponent<ActiveCharacter>(), "ActiveCharacter on Systems");
        var selection = Require(systems.GetComponent<UnitSelection>(), "UnitSelection on Systems");
        var direct = Require(systems.GetComponent<DirectControlInput>(), "DirectControlInput on Systems");
        var commandInput = Require(systems.GetComponent<PlayerCommandInput>(), "PlayerCommandInput on Systems");
        var hud = Require(systems.GetComponent<PrototypeHud>(), "PrototypeHud on Systems");
        var cameraController = Require(Object.FindFirstObjectByType<TacticalCameraController>(), "TacticalCameraController");
        var oldMarker = Require(firstFriendly.transform.Find("PrimaryMarker"), "FriendlyUnit_1/PrimaryMarker");
        if (Object.FindFirstObjectByType<ActiveCharacterMarker>() != null)
            throw new Exception("The scene already has an ActiveCharacterMarker; the builder has already run.");

        SetReference(active, "selection", selection);
        SetReference(direct, "selection", selection);
        SetReference(direct, "cycleAction", ActionReference("Character/CycleCharacter"));
        SetReference(direct, "reverseAction", ActionReference("Character/CycleReverse"));
        // Already loaded through [FormerlySerializedAs("primary")]; set again so the scene is saved under the new names.
        SetReference(direct, "activeCharacter", active);
        SetReference(commandInput, "activeCharacter", active);
        SetReference(cameraController, "activeCharacter", active);
        SetReference(hud, "activeCharacter", active);

        Object.DestroyImmediate(oldMarker.gameObject);
        CreateMarker(active, material, firstFriendly.transform.position);

        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene))
            throw new Exception("Failed to save " + ScenePath);
        AssetDatabase.SaveAssets();
        Debug.Log($"[SwitchingSceneBuilder] Wired active character switching into {ScenePath}");
    }

    // A small orange diamond that floats above the active character's head. No collider, so it never blocks clicks.
    static void CreateMarker(ActiveCharacter active, Material material, Vector3 firstUnitPosition)
    {
        var root = new GameObject("ActiveMarker");
        root.transform.position = firstUnitPosition + Vector3.up * 1.6f;
        var visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
        visual.name = "Visual";
        Object.DestroyImmediate(visual.GetComponent<Collider>());
        visual.transform.SetParent(root.transform, false);
        visual.transform.localRotation = Quaternion.Euler(45f, 0f, 45f);
        visual.transform.localScale = new Vector3(0.35f, 0.35f, 0.35f);
        var renderer = visual.GetComponent<Renderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        var marker = root.AddComponent<ActiveCharacterMarker>();
        SetReference(marker, "activeCharacter", active);
        SetReference(marker, "visual", visual);
    }

    static InputActionReference ActionReference(string actionPath)
    {
        var reference = AssetDatabase.LoadAllAssetsAtPath(ControlsPath)
            .OfType<InputActionReference>()
            .FirstOrDefault(r => r.action != null && $"{r.action.actionMap.name}/{r.action.name}" == actionPath);
        return reference != null ? reference : throw new Exception($"No InputActionReference for {actionPath} in {ControlsPath}");
    }

    static T Require<T>(T value, string what) where T : Object =>
        value != null ? value : throw new Exception(what + " not found in " + ScenePath);

    static void SetReference(Object target, string field, Object value)
    {
        var serialized = new SerializedObject(target);
        var property = serialized.FindProperty(field) ?? throw new Exception($"{target.GetType().Name}.{field} not found");
        property.objectReferenceValue = value;
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }
}
```

- [ ] **Step 4: Run the builder, then delete it**

```bash
"/c/Program Files/Unity/Hub/Editor/6000.3.25f1/Editor/Unity.exe" -batchmode -quit -projectPath "$(pwd -W)" -executeMethod SwitchingSceneBuilder.Build -logFile "$(pwd -W)/Logs/BuildSwitchingScene.log"; echo "EXIT=$?"
grep -E '\[SwitchingSceneBuilder\]|error CS|Exception' Logs/BuildSwitchingScene.log | head -20
grep -c "primary:" Assets/_Project/Scenes/Prototype.unity          # expect 0 (all saved as activeCharacter)
grep -c "activeCharacter:" Assets/_Project/Scenes/Prototype.unity  # expect 5
grep -c "PrimaryMarker" Assets/_Project/Scenes/Prototype.unity     # expect 0
grep -c "m_Name: ActiveMarker" Assets/_Project/Scenes/Prototype.unity  # expect 1
git status --short Assets/_Project/Materials                        # expect the material renamed (PrimaryMarker.mat deleted, ActiveMarker.mat new)
```

**Expected:** `EXIT=0`; the log contains `[SwitchingSceneBuilder] Wired active character switching into Assets/_Project/Scenes/Prototype.unity` and no exceptions; the counts above match. Check the GUID was kept: `grep guid Assets/_Project/Materials/ActiveMarker.mat.meta` must show `ad56dd9d0c64bd4408cd11904c6578bf`.

Then delete the builder:

```bash
rm -r Assets/_Project/Editor Assets/_Project/Editor.meta
```

- [ ] **Step 5: Run all tests to verify they pass**

Run: `Tools/run-tests.sh EditMode` → expected `total="162" passed="162"`, `EXIT=0`.
Run: `Tools/run-tests.sh PlayMode` → expected `total="132" passed="132"`, `EXIT=0`. This run also recompiles without the deleted builder.

- [ ] **Step 6: Commit**

```bash
git add -A Assets/_Project/Scenes/Prototype.unity Assets/_Project/Materials Assets/_Project/Tests/PlayMode/PrototypeSceneTests.cs
git status --short   # must NOT list Assets/_Project/Editor; the material shows as a rename
git commit -m "Wire active character switching into the prototype scene

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Decision records and final verification

**Files:**
- Modify: `Docs/Decisions.md`

- [ ] **Step 1: Amend decision 008**

In `Docs/Decisions.md`, section `## 008 — Input mapping and click vs drag`:

(a) Replace `  - **Character** (Phase 3, 2026-10-04): Move (WASD), Takeover (V).` with:

```markdown
  - **Character** (Phase 3, 2026-10-04): Move (WASD), Takeover (V), CycleCharacter (Tab) and CycleReverse (Shift; held with Tab it cycles backward, see 012).
```

(b) Replace `- **WASD is shared** by `Camera/Pan` and `Character/Move`. The mode decides which one acts: while the primary character is being driven (takeover on, not paused) the camera ignores Pan, and otherwise `DirectControlInput` ignores Move.` with:

```markdown
- **WASD is shared** by `Camera/Pan` and `Character/Move`. The mode decides which one acts: while the active character is being driven (takeover on, not paused) the camera ignores Pan, and otherwise `DirectControlInput` ignores Move. **Shift is shared** by `Commands/Modifier` and `Character/CycleReverse`: each map owns its own action so no component enables or disables another's.
```

(c) Replace `**In real time the order goes to the primary character; while paused (or with no active primary character) it goes to the selected units** (Phase 3, 2026-10-04).` with:

```markdown
**In real time the order goes to the active character; while paused (or with no active character that can act) it goes to the selected units** (Phase 3, 2026-10-04).
```

(d) Replace `- **Implications:** Input components only create commands, change the selection, call `TacticalPause`, set the primary character's move intent through `CommandableUnit.SetMoveIntent`, or toggle `PrimaryCharacter` takeover.` with:

```markdown
- **Implications:** Input components only create commands, change the selection, call `TacticalPause`, set the active character's move intent through `CommandableUnit.SetMoveIntent`, toggle `ActiveCharacter` takeover, or ask `ActiveCharacter` to cycle.
```

- [ ] **Step 2: Amend decision 011**

In section `## 011 — Primary character and takeover mode`:

(a) Insert directly under the heading:

```markdown
- **Renamed (2026-10-04, see 012):** `PrimaryCharacter` is now `ActiveCharacter`, and Tab/Shift+Tab can change which friendly it holds. Below, "primary character" means the active character.
```

(b) Replace `  - Switching the primary character at runtime, and what happens when it dies, are not designed yet; friendly units have no `Health`.` with:

```markdown
  - Switching the active character at runtime is designed in 012. What happens when it dies is not designed yet; friendly units have no `Health`.
```

- [ ] **Step 3: Add decision 012**

Append at the end of `Docs/Decisions.md`:

```markdown

## 012 — Active character switching

- **Decided (2026-10-04):** The directly controlled character is no longer fixed. `PrimaryCharacter` was renamed `ActiveCharacter` (file renamed with its `.meta`, so the script GUID and scene wiring survived; renamed fields kept their data through `[FormerlySerializedAs("primary")]`). Any **eligible** roster unit can be the active character:
  - eligible = in `UnitSelection.Roster`, `SelectableUnit` and `CommandableUnit` enabled, GameObject active in the hierarchy, and no `Health` or a living one (`ActiveCharacter.IsEligible`);
  - **Tab** makes the next eligible unit active and **Shift+Tab** the previous one, in roster order, wrapping (`ActiveCharacter.Cycle` → `ControlCycle.NextIndex`). With one eligible unit it stays active; with none nothing changes;
  - Tab works in every mode, paused or not. It changes state only: no orders are issued or cleared and no simulation runs.
- **Handover:** `DirectControlInput` zeroes the old unit's move intent in the same frame. A held move key carries over to the new unit only if it has no orders; otherwise the release gate re-arms, so tabbing onto a unit cannot wipe its plan. The old unit keeps whatever orders it had.
- **Selection follows:** Tab replaces the selection with the new active character, so paused Tab-then-click orders the character just chosen.
- **Camera:** when the active unit changes, the camera glides to it once (same easing as follow, unscaled time); any pan input ends the glide. While driving it follows as before.
- **Feedback:** one `ActiveMarker` (`ActiveCharacterMarker`) floats above whoever is active, distinct from the selection ring at the feet. The HUD line names the controlled character.
- **Input:** `Character/CycleCharacter` (Tab) and `Character/CycleReverse` (Shift), both read only by `DirectControlInput`.
- **Why:** the control code should not assume a fixed protagonist; squad members are interchangeable for control. Consumers (input, camera, marker) compare `ActiveCharacter.Unit` with the unit they last saw each frame instead of subscribing to a change event, which catches every source of change and keeps `ActiveCharacter` state-only (as in 006).
- **Rejected:**
  - Separate `NextCharacter`/`PreviousCharacter` actions with a Shift+Tab composite: needs the project-wide "shortcut keys consume input" setting so Shift+Tab does not also fire Tab.
  - Reading `Commands/Modifier` from `DirectControlInput`: two components enabling and disabling one shared action, so disabling either would switch the other's Shift off.
  - Carrying a held key over unconditionally (wipes the new unit's orders) or never (a hitch on every mid-run switch).
  - Leaving the selection unchanged on Tab: paused clicks would order someone other than the character just tabbed to.
- **Implications:** No automatic switch when the active character dies. Cycling order is roster order. A per-unit "cannot be directly controlled" flag waits for a design need. Right-click stays camera-only; `ClickDragDetector` already separates a right-click from a right-drag, so a future context action can use the click without changing camera control.
```

- [ ] **Step 4: Final verification**

```bash
grep -rn "PrimaryCharacter" Assets/_Project/Scripts Assets/_Project/Tests   # expect no output
git status --short                                                          # expect only Docs/Decisions.md
```

Run: `Tools/run-tests.sh EditMode` → expected `total="162" passed="162"`, `EXIT=0`.
Run: `Tools/run-tests.sh PlayMode` → expected `total="132" passed="132"`, `EXIT=0`.

- [ ] **Step 5: Commit**

```bash
git add Docs/Decisions.md
git commit -m "Record active character switching decisions

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

- [ ] **Step 6: Owner manual checks (hand back to the owner; do not merge)**

In the Editor, open `Prototype.unity`, press Play, and check: V still toggles takeover; Tab/Shift+Tab cycle and wrap with the marker, selection ring and HUD line following; a held W carries to an idle character but not to one with orders; Tab while paused moves nothing and keeps queue lines; the camera glides to the new character and WASD cancels the glide; right-drag still rotates/tilts while paused; rapid Tab presses leave the Console clean.
