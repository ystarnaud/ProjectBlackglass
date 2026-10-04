# Prototype Control Loop Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build one playable prototype scene that proves Input → Command → Unit → Movement/Combat, with tactical pause as an explicit system.

**Architecture:**
- Commands are immutable data objects handed to `CommandableUnit.Issue`. The unit and its `UnitMover`/`UnitAttacker` components decide how to carry them out.
- Input components only translate device input into commands, camera motion or pause requests.
- `TacticalPause` is the single owner of `Time.timeScale`. Simulation runs on scaled time; camera, input and HUD use unscaled time.

**Tech Stack:** Unity 6000.3.25f1 (Unity 6.3 LTS), URP 17.3.0, Input System 1.20.0, AI Navigation 2.0.14 (`NavMeshAgent`, `NavMeshSurface`), Unity Test Framework 1.6.0 (NUnit), `InputTestFixture` from the Input System test framework.

**Spec:** `Docs/superpowers/specs/2026-10-04-prototype-control-loop-design.md`

## Global Constraints

- **Unity Editor:** exactly 6000.3.25f1 at `C:\Program Files\Unity\Hub\Editor\6000.3.25f1\Editor\Unity.exe`. **The Editor must be closed during every batch-mode run.** Unity allows only one instance per project.
- **Code location:**
  - Runtime code goes in `Assets/_Project/Scripts/` (assembly `Blackglass`, namespace `Blackglass`).
  - Tests go in `Assets/_Project/Tests/EditMode/` and `Assets/_Project/Tests/PlayMode/` (namespace `Blackglass.Tests`).
- **Pause ownership:** only `TacticalPause` may write `Time.timeScale` in game code. Tests may reset it in `TearDown`.
- **Time:** camera, input and HUD use `Time.unscaledDeltaTime` or event callbacks. Unit orders, movement, attack cooldowns and hit flash use scaled time.
- **Wiring:**
  - Serialized Inspector references; no singletons, no `FindObjectOfType` in game code, no static mutable state.
  - Tests wire components through `internal void Initialize(...)` methods, which `InternalsVisibleTo` exposes to the test assemblies.
- **Commands:** a new command replaces the current one. No queue, no Stop/Interact, no selection, no factions.
- **Packages:** only add `com.unity.ai.navigation` 2.0.14 and `"testables": ["com.unity.inputsystem"]`. Nothing else.
- **Debug UI:** IMGUI `OnGUI` only. No uGUI canvas or EventSystem.
- **Git:**
  - Work on local branch `prototype/control-loop`. Never push or merge.
  - Commit after each task. Messages are sentence-case, imperative, and end with the line `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
  - Unity generates `.meta` files for every new file and folder. Always commit them alongside the asset.
- **Test runs:** always go through `Tools/run-tests.sh` (Task 1). It restricts runs to our assemblies with `-assemblyNames`, because `testables` also exposes the Input System's own test suite, which we must not run.

### Running tests

```bash
Tools/run-tests.sh EditMode                       # all EditMode tests
Tools/run-tests.sh PlayMode                       # all PlayMode tests
Tools/run-tests.sh EditMode Blackglass.Tests.HealthTests   # one fixture (any -testFilter expression)
```

The script prints any `error CS` lines, the test-run summary (`result= total= passed= failed=`), the names of failed tests with their messages, and `EXIT=<code>`.

Unity exit codes:
- **0:** all tests passed.
- **2:** some tests failed.
- **3:** the run couldn't execute, typically a compile error. No results file is written, and the `error CS` lines explain why.

A "red" step in this plan usually means **EXIT=3 with `error CS0246`** (type not found), because Unity can't run any tests while the code doesn't compile.

## Deviations from the spec (decided while planning; the spec is updated to match)

1. The Camera map uses `PointerPosition` instead of `PointerDelta`. Drag rotation is computed from the change in pointer position, which the click-vs-drag detector needs anyway, and it's deterministic under `InputTestFixture`.
2. `CommandableUnit.Issue` returns `bool` (accepted or not). A rejected command, such as an unreachable destination or a dead target, **leaves the current order running**.
3. `UnitMover.HasArrived` means: the path isn't pending, and either the horizontal distance to the agent's destination is at most `stoppingDistance + 0.1`, or the agent no longer has a path. `remainingDistance` is not used because it's unreliable on the frame a destination is set.
4. `CommandableUnit.Update` does nothing when `Time.deltaTime <= 0`, which is the simulation frozen by tactical pause. Without this, an attack issued while paused with the target already in range would land its first hit before resume.
5. `ClickDragDetector.Release(Vector2)` takes the release position and counts it, so a fast flick released far away in one frame counts as a drag.
6. `PlayerCommandInput` calls `Physics.SyncTransforms()` before raycasting, so clicks are accurate while paused. Transforms aren't synced to physics without simulation steps.
7. `UnitMover.Stop()` also zeroes the agent's velocity, so the unit stops on the spot instead of coasting about 0.6 m.

## Review Focus

These five inputs are implied by the spec but easy to break. Each has a test in its owning task.

1. **An attack issued while paused, with the target already in range** must deal no damage until resume. Pinned in Task 5: `Paused_AttackInRange_DealsNoDamageUntilResume`.
2. **A right-click on the sky, or on a point with no NavMesh nearby,** does nothing: no exception, and the previous order keeps running. Pinned in Task 5 (`Issue_MoveOffNavMesh_IsRejectedAndPreviousOrderKept`) and Task 7 (`RightClickOnSky_IssuesNothing`).
3. **A fast right-button flick released far from its press point within one frame** counts as a drag: no command, no stray rotation. Pinned in Task 1: `FastFlick_ReleasedFarAway_IsDrag`.
4. **A target that dies or is disabled mid-chase, or an attack command on an already-dead target,** leaves the unit stopped cleanly and the command rejected or cleared, with no exceptions. Pinned in Task 5 (`Attack_TargetKilledElsewhere_UnitStopsAndClearsOrder`) and Task 4 (`Issue_AttackOnDeadTarget_IsRejected`).
5. **Huge or repeated damage** clamps HP at 0 without integer overflow, and `Died` fires exactly once. Pinned in Task 3: `TakeDamage_HugeValue_DoesNotOverflow` and `TakeDamage_PastZero_RaisesDiedOnce`.

---

## File Structure

```
Tools/run-tests.sh                                  test runner wrapper (Task 1)
Assets/_Project/
├── Scripts/
│   ├── Blackglass.asmdef                           runtime assembly (Task 1)
│   ├── AssemblyInfo.cs                             InternalsVisibleTo test assemblies (Task 1)
│   ├── Controls/ClickDragDetector.cs               click vs drag rule (Task 1)
│   ├── GameTime/TacticalPause.cs                   sole owner of Time.timeScale (Task 2)
│   ├── Combat/Health.cs                            HP, damage, death (Task 3)
│   ├── Combat/HitFlash.cs                          debug hit feedback (Task 3)
│   ├── Commands/UnitCommands.cs                    UnitCommand, MoveCommand, AttackCommand (Task 4)
│   ├── Controls/CommandResolver.cs                 click -> command rule (Task 4)
│   ├── Units/UnitMover.cs                          NavMeshAgent wrapper (Task 4, runtime-tested in Task 5)
│   ├── Units/UnitAttacker.cs                       range, damage, cooldown (Task 4)
│   ├── Units/CommandableUnit.cs                    executes orders (Task 4, runtime-tested in Task 5)
│   ├── CameraControl/TacticalCameraController.cs   strategy camera (Task 6)
│   ├── Controls/PlayerCommandInput.cs              input -> commands / pause (Task 7)
│   └── DebugUI/PrototypeHud.cs                     OnGUI overlay (Task 8)
├── Tests/
│   ├── EditMode/Blackglass.Tests.EditMode.asmdef   (Task 1)
│   ├── EditMode/*Tests.cs
│   ├── PlayMode/Blackglass.Tests.PlayMode.asmdef   (Task 2, extended in Tasks 5 and 6)
│   ├── PlayMode/TestSupport/TestWorld.cs           runtime test scene builder (Task 5)
│   ├── PlayMode/TestSupport/TestControls.cs        loads our input asset (Task 6)
│   └── PlayMode/*Tests.cs
├── Input/BlackglassControls.inputactions           moved from Assets/InputSystem_Actions.inputactions (Task 6)
├── Materials/{Ground,Obstacle,Unit,Dummy}.mat      (Task 8)
└── Scenes/Prototype.unity + Prototype/NavMesh-Environment.asset (Task 8)
Docs/Decisions.md                                   decision records (Task 9)
```

`Assets/_Project/Editor/PrototypeSceneBuilder.cs` is **temporary**. It's created and deleted within Task 8 and never committed.

---

### Task 1: Runtime assembly, test runner, and `ClickDragDetector`

**Files:**
- Create: `Tools/run-tests.sh`
- Create: `Assets/_Project/Scripts/Blackglass.asmdef`
- Create: `Assets/_Project/Scripts/AssemblyInfo.cs`
- Create: `Assets/_Project/Scripts/Controls/ClickDragDetector.cs`
- Create: `Assets/_Project/Tests/EditMode/Blackglass.Tests.EditMode.asmdef`
- Test: `Assets/_Project/Tests/EditMode/ClickDragDetectorTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `Blackglass.ClickDragDetector`:
    - `ClickDragDetector(float thresholdPixels = 6f)`, which throws `ArgumentOutOfRangeException` if the threshold is < 0.
    - `bool IsPressed { get; }`, `bool IsDragging { get; }`.
    - `void Press(Vector2 position)`, `void Track(Vector2 position)`.
    - `bool Release(Vector2 position)`: returns true when the press was a click.
  - `Tools/run-tests.sh <EditMode|PlayMode> [filter]`.
  - Assembly `Blackglass`, with internals visible to `Blackglass.Tests.EditMode` and `Blackglass.Tests.PlayMode`.

- [ ] **Step 1: Create the test runner script**

`Tools/run-tests.sh`:

```bash
#!/usr/bin/env bash
# Runs this project's Unity tests in batch mode. The Unity Editor must be closed.
# Usage: Tools/run-tests.sh EditMode|PlayMode [test filter]
set -u
platform="${1:?usage: Tools/run-tests.sh EditMode|PlayMode [test filter]}"
filter="${2:-}"
root="$(cd "$(dirname "$0")/.." && (pwd -W 2>/dev/null || pwd))"
unity="${UNITY_EXE:-/c/Program Files/Unity/Hub/Editor/6000.3.25f1/Editor/Unity.exe}"
results="$root/Logs/TestResults-$platform.xml"
log="$root/Logs/TestRun-$platform.log"
mkdir -p "$root/Logs"
rm -f "$results"

args=(-batchmode -projectPath "$root" -runTests -testPlatform "$platform"
      -assemblyNames "Blackglass.Tests.$platform"
      -testResults "$results" -logFile "$log")
if [ -n "$filter" ]; then args+=(-testFilter "$filter"); fi

"$unity" "${args[@]}"
code=$?

if grep -q "another Unity instance is running" "$log" 2>/dev/null; then
  echo "The Unity Editor has this project open. Close it and run again."
fi
grep -h "error CS" "$log" 2>/dev/null | sort -u
if [ -f "$results" ]; then
  grep -o '<test-run [^>]*>' "$results" | grep -oE '(result|total|passed|failed|skipped)="[^"]*"' | tr '\n' ' '
  echo
  grep -oE '<test-case [^>]*result="Failed"[^>]*>' "$results" | grep -oE 'fullname="[^"]*"'
  grep -A6 '<failure>' "$results" | grep -vE '^\s*$' | head -60
else
  echo "No results file was written. See $log"
fi
echo "EXIT=$code"
exit $code
```

Run: `chmod +x Tools/run-tests.sh`

- [ ] **Step 2: Create the runtime assembly definition and InternalsVisibleTo**

`Assets/_Project/Scripts/Blackglass.asmdef`:

```json
{
    "name": "Blackglass",
    "rootNamespace": "Blackglass",
    "references": [
        "Unity.InputSystem"
    ],
    "includePlatforms": [],
    "excludePlatforms": [],
    "allowUnsafeCode": false,
    "overrideReferences": false,
    "precompiledReferences": [],
    "autoReferenced": true,
    "defineConstraints": [],
    "versionDefines": [],
    "noEngineReferences": false
}
```

`Assets/_Project/Scripts/AssemblyInfo.cs`:

```csharp
using System.Runtime.CompilerServices;

// Tests wire components through internal Initialize methods instead of reflection.
[assembly: InternalsVisibleTo("Blackglass.Tests.EditMode")]
[assembly: InternalsVisibleTo("Blackglass.Tests.PlayMode")]
```

- [ ] **Step 3: Create the EditMode test assembly definition**

`Assets/_Project/Tests/EditMode/Blackglass.Tests.EditMode.asmdef`:

```json
{
    "name": "Blackglass.Tests.EditMode",
    "rootNamespace": "Blackglass.Tests",
    "references": [
        "Blackglass",
        "UnityEngine.TestRunner",
        "UnityEditor.TestRunner"
    ],
    "includePlatforms": [
        "Editor"
    ],
    "excludePlatforms": [],
    "allowUnsafeCode": false,
    "overrideReferences": true,
    "precompiledReferences": [
        "nunit.framework.dll"
    ],
    "autoReferenced": false,
    "defineConstraints": [
        "UNITY_INCLUDE_TESTS"
    ],
    "versionDefines": [],
    "noEngineReferences": false
}
```

- [ ] **Step 4: Write the failing tests**

`Assets/_Project/Tests/EditMode/ClickDragDetectorTests.cs`:

```csharp
using System;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class ClickDragDetectorTests
    {
        static readonly Vector2 Origin = new Vector2(100f, 100f);

        ClickDragDetector detector;

        [SetUp]
        public void SetUp() => detector = new ClickDragDetector(6f);

        [Test]
        public void PressAndReleaseInPlace_IsClick()
        {
            detector.Press(Origin);
            Assert.That(detector.Release(Origin), Is.True);
        }

        [Test]
        public void MovementUpToThreshold_IsStillClick()
        {
            var edge = Origin + new Vector2(6f, 0f);
            detector.Press(Origin);
            detector.Track(edge);
            Assert.That(detector.IsDragging, Is.False);
            Assert.That(detector.Release(edge), Is.True);
        }

        [Test]
        public void MovementPastThreshold_IsDrag()
        {
            var beyond = Origin + new Vector2(6.5f, 0f);
            detector.Press(Origin);
            detector.Track(beyond);
            Assert.That(detector.IsDragging, Is.True);
            Assert.That(detector.Release(beyond), Is.False);
        }

        [Test]
        public void ReturningToPressPoint_AfterDrag_IsStillDrag()
        {
            detector.Press(Origin);
            detector.Track(Origin + new Vector2(0f, 20f));
            detector.Track(Origin);
            Assert.That(detector.Release(Origin), Is.False);
        }

        [Test]
        public void FastFlick_ReleasedFarAway_IsDrag()
        {
            detector.Press(Origin);
            Assert.That(detector.Release(Origin + new Vector2(0f, 50f)), Is.False);
        }

        [Test]
        public void ReleaseWithoutPress_IsNotClick()
        {
            Assert.That(detector.Release(Origin), Is.False);
        }

        [Test]
        public void NewPress_ClearsPreviousDrag()
        {
            detector.Press(Origin);
            detector.Track(Origin + new Vector2(30f, 0f));
            detector.Release(Origin + new Vector2(30f, 0f));

            detector.Press(Origin);
            Assert.That(detector.IsDragging, Is.False);
            Assert.That(detector.Release(Origin), Is.True);
        }

        [Test]
        public void IsPressed_FollowsPressAndRelease()
        {
            Assert.That(detector.IsPressed, Is.False);
            detector.Press(Origin);
            Assert.That(detector.IsPressed, Is.True);
            detector.Release(Origin);
            Assert.That(detector.IsPressed, Is.False);
        }

        [Test]
        public void TrackWithoutPress_DoesNothing()
        {
            detector.Track(Origin + new Vector2(100f, 0f));
            Assert.That(detector.IsDragging, Is.False);
        }

        [Test]
        public void NegativeThreshold_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new ClickDragDetector(-1f));
        }
    }
}
```

- [ ] **Step 5: Run the tests to verify they fail**

Run: `Tools/run-tests.sh EditMode`
Expected: `error CS0246: The type or namespace name 'ClickDragDetector' could not be found`, `EXIT=3`.

- [ ] **Step 6: Write the implementation**

`Assets/_Project/Scripts/Controls/ClickDragDetector.cs`:

```csharp
using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Decides whether a pointer press was a click or a drag, from how far the pointer
    /// moved away from the press point. Once a press becomes a drag it stays a drag.
    /// </summary>
    public sealed class ClickDragDetector
    {
        readonly float thresholdPixels;
        Vector2 pressPosition;

        public ClickDragDetector(float thresholdPixels = 6f)
        {
            if (thresholdPixels < 0f)
                throw new ArgumentOutOfRangeException(nameof(thresholdPixels), thresholdPixels, "Threshold cannot be negative.");
            this.thresholdPixels = thresholdPixels;
        }

        public bool IsPressed { get; private set; }
        public bool IsDragging { get; private set; }

        public void Press(Vector2 position)
        {
            IsPressed = true;
            IsDragging = false;
            pressPosition = position;
        }

        public void Track(Vector2 position)
        {
            if (!IsPressed || IsDragging)
                return;
            if ((position - pressPosition).sqrMagnitude > thresholdPixels * thresholdPixels)
                IsDragging = true;
        }

        /// <summary>Ends the press. Returns true when it was a click, false for a drag or when nothing was pressed.</summary>
        public bool Release(Vector2 position)
        {
            if (!IsPressed)
                return false;
            Track(position);
            var wasClick = !IsDragging;
            IsPressed = false;
            IsDragging = false;
            return wasClick;
        }
    }
}
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `Tools/run-tests.sh EditMode`
Expected: `result="Passed" total="10" passed="10" failed="0"`, `EXIT=0`.

- [ ] **Step 8: Commit**

```bash
git add Tools/run-tests.sh Assets/_Project/Scripts Assets/_Project/Scripts.meta Assets/_Project/Tests Assets/_Project/Tests.meta
git status --short   # expect only the files above plus their .meta files
git commit -m "Add runtime assembly, test runner and click/drag detector

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: `TacticalPause` service

**Files:**
- Create: `Assets/_Project/Scripts/GameTime/TacticalPause.cs`
- Create: `Assets/_Project/Tests/PlayMode/Blackglass.Tests.PlayMode.asmdef`
- Test: `Assets/_Project/Tests/EditMode/TacticalPauseTests.cs`
- Test: `Assets/_Project/Tests/PlayMode/TacticalPausePlayModeTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `Blackglass.TacticalPause : MonoBehaviour`, with:
  - `bool IsPaused { get; }`
  - `event Action<bool> Changed` (argument = new paused state)
  - `void Pause()`, `void Resume()`, `void Toggle()`

- [ ] **Step 1: Write the failing EditMode tests**

`Assets/_Project/Tests/EditMode/TacticalPauseTests.cs`:

```csharp
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class TacticalPauseTests
    {
        GameObject host;
        TacticalPause pause;
        List<bool> changes;

        [SetUp]
        public void SetUp()
        {
            Time.timeScale = 1f;
            host = new GameObject("TacticalPauseTest");
            pause = host.AddComponent<TacticalPause>();
            changes = new List<bool>();
            pause.Changed += changes.Add;
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(host);
            Time.timeScale = 1f;
        }

        [Test]
        public void StartsUnpaused()
        {
            Assert.That(pause.IsPaused, Is.False);
        }

        [Test]
        public void Pause_StopsTimeAndReportsPaused()
        {
            pause.Pause();
            Assert.That(pause.IsPaused, Is.True);
            Assert.That(Time.timeScale, Is.EqualTo(0f));
            Assert.That(changes, Is.EqualTo(new[] { true }));
        }

        [Test]
        public void Resume_RestoresTheTimeScaleFromBeforePause()
        {
            Time.timeScale = 0.5f;
            pause.Pause();
            pause.Resume();
            Assert.That(pause.IsPaused, Is.False);
            Assert.That(Time.timeScale, Is.EqualTo(0.5f));
            Assert.That(changes, Is.EqualTo(new[] { true, false }));
        }

        [Test]
        public void Toggle_AlternatesBetweenPausedAndRunning()
        {
            pause.Toggle();
            Assert.That(pause.IsPaused, Is.True);
            pause.Toggle();
            Assert.That(pause.IsPaused, Is.False);
            Assert.That(Time.timeScale, Is.EqualTo(1f));
        }

        [Test]
        public void PauseTwice_RaisesChangedOnce()
        {
            pause.Pause();
            pause.Pause();
            Assert.That(changes, Is.EqualTo(new[] { true }));
            Assert.That(Time.timeScale, Is.EqualTo(0f));
        }

        [Test]
        public void ResumeWhileRunning_DoesNothing()
        {
            Time.timeScale = 0.75f;
            pause.Resume();
            Assert.That(changes, Is.Empty);
            Assert.That(Time.timeScale, Is.EqualTo(0.75f));
        }
    }
}
```

- [ ] **Step 2: Create the PlayMode test assembly and the failing PlayMode test**

`Assets/_Project/Tests/PlayMode/Blackglass.Tests.PlayMode.asmdef`:

```json
{
    "name": "Blackglass.Tests.PlayMode",
    "rootNamespace": "Blackglass.Tests",
    "references": [
        "Blackglass",
        "UnityEngine.TestRunner",
        "UnityEditor.TestRunner"
    ],
    "includePlatforms": [],
    "excludePlatforms": [],
    "allowUnsafeCode": false,
    "overrideReferences": true,
    "precompiledReferences": [
        "nunit.framework.dll"
    ],
    "autoReferenced": false,
    "defineConstraints": [
        "UNITY_INCLUDE_TESTS"
    ],
    "versionDefines": [],
    "noEngineReferences": false
}
```

`Assets/_Project/Tests/PlayMode/TacticalPausePlayModeTests.cs`:

```csharp
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class TacticalPausePlayModeTests
    {
        [TearDown]
        public void TearDown() => Time.timeScale = 1f;

        [UnityTest]
        public IEnumerator DisablingWhilePaused_RestoresTime()
        {
            var host = new GameObject("TacticalPause");
            var pause = host.AddComponent<TacticalPause>();
            pause.Pause();
            yield return null;

            host.SetActive(false);

            Assert.That(Time.timeScale, Is.EqualTo(1f));
            Assert.That(pause.IsPaused, Is.False);
            Object.Destroy(host);
        }

        [UnityTest]
        public IEnumerator WhilePaused_ScaledTimeStopsButRealTimeContinues()
        {
            var host = new GameObject("TacticalPause");
            var pause = host.AddComponent<TacticalPause>();
            pause.Pause();
            var scaledStart = Time.time;

            yield return new WaitForSecondsRealtime(0.2f);

            Assert.That(Time.time, Is.EqualTo(scaledStart));
            pause.Resume();
            Object.Destroy(host);
        }
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `Tools/run-tests.sh EditMode`
Expected: `error CS0246: ... 'TacticalPause' could not be found`, `EXIT=3`.

- [ ] **Step 4: Write the implementation**

`Assets/_Project/Scripts/GameTime/TacticalPause.cs`:

```csharp
using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Tactical pause: the only code in the project that writes Time.timeScale.
    /// Simulation (units, combat) runs on scaled time and stops while paused;
    /// camera, input and debug UI use unscaled time and keep working.
    /// </summary>
    public sealed class TacticalPause : MonoBehaviour
    {
        float timeScaleBeforePause = 1f;

        public bool IsPaused { get; private set; }

        /// <summary>Raised with the new paused state, only when the state actually changes.</summary>
        public event Action<bool> Changed;

        public void Pause()
        {
            if (IsPaused)
                return;
            timeScaleBeforePause = Time.timeScale;
            Time.timeScale = 0f;
            IsPaused = true;
            Changed?.Invoke(true);
        }

        public void Resume()
        {
            if (!IsPaused)
                return;
            Time.timeScale = timeScaleBeforePause;
            IsPaused = false;
            Changed?.Invoke(false);
        }

        public void Toggle()
        {
            if (IsPaused)
                Resume();
            else
                Pause();
        }

        // Never leave the game frozen when this service goes away (scene unload, exiting Play mode).
        void OnDisable() => Resume();
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `Tools/run-tests.sh EditMode` → expected `total="16" passed="16"`, `EXIT=0`.
Run: `Tools/run-tests.sh PlayMode` → expected `total="2" passed="2"`, `EXIT=0`.

- [ ] **Step 6: Commit**

```bash
git add Assets/_Project/Scripts Assets/_Project/Tests
git status --short
git commit -m "Add tactical pause service

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: `Health` and `HitFlash`

**Files:**
- Create: `Assets/_Project/Scripts/Combat/Health.cs`
- Create: `Assets/_Project/Scripts/Combat/HitFlash.cs`
- Test: `Assets/_Project/Tests/EditMode/HealthTests.cs`
- Test: `Assets/_Project/Tests/PlayMode/HitFlashTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `Blackglass.Health : MonoBehaviour`, with:
    - `int Max`, `int Current`, `bool IsAlive`
    - `event Action<int> Damaged`, `event Action Died`
    - `void TakeDamage(int amount)`: throws `ArgumentOutOfRangeException` if `amount < 0`.
    - The default `Max` is 100. The serialized field is `max`; `disableOnDeath` defaults to true.
  - `Blackglass.HitFlash : MonoBehaviour`, with `[RequireComponent(typeof(Health))]`. It finds its renderer via `GetComponentInChildren<Renderer>()` unless `targetRenderer` is assigned.

- [ ] **Step 1: Write the failing EditMode tests**

`Assets/_Project/Tests/EditMode/HealthTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class HealthTests
    {
        GameObject host;
        Health health;
        List<int> damaged;
        int deaths;

        [SetUp]
        public void SetUp()
        {
            host = new GameObject("HealthTest");
            health = host.AddComponent<Health>();
            damaged = new List<int>();
            deaths = 0;
            health.Damaged += damaged.Add;
            health.Died += () => deaths++;
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(host);

        [Test]
        public void NewHealth_IsFullAndAlive()
        {
            Assert.That(health.Max, Is.EqualTo(100));
            Assert.That(health.Current, Is.EqualTo(100));
            Assert.That(health.IsAlive, Is.True);
        }

        [Test]
        public void TakeDamage_ReducesCurrentAndReportsAmount()
        {
            health.TakeDamage(25);
            Assert.That(health.Current, Is.EqualTo(75));
            Assert.That(damaged, Is.EqualTo(new[] { 25 }));
            Assert.That(deaths, Is.EqualTo(0));
        }

        [Test]
        public void TakeDamage_PastZero_RaisesDiedOnce()
        {
            health.TakeDamage(150);
            health.TakeDamage(10);
            Assert.That(health.Current, Is.EqualTo(0));
            Assert.That(health.IsAlive, Is.False);
            Assert.That(deaths, Is.EqualTo(1));
            Assert.That(damaged, Is.EqualTo(new[] { 150 }));
        }

        [Test]
        public void TakeDamage_HugeValue_DoesNotOverflow()
        {
            health.TakeDamage(60);
            health.TakeDamage(int.MaxValue);
            Assert.That(health.Current, Is.EqualTo(0));
            Assert.That(deaths, Is.EqualTo(1));
        }

        [Test]
        public void TakeDamage_Zero_ChangesNothing()
        {
            health.TakeDamage(0);
            Assert.That(health.Current, Is.EqualTo(100));
            Assert.That(damaged, Is.Empty);
        }

        [Test]
        public void TakeDamage_Negative_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => health.TakeDamage(-5));
            Assert.That(health.Current, Is.EqualTo(100));
        }

        [Test]
        public void Death_DeactivatesTheGameObject()
        {
            health.TakeDamage(100);
            Assert.That(host.activeSelf, Is.False);
        }
    }
}
```

- [ ] **Step 2: Write the failing PlayMode test**

`Assets/_Project/Tests/PlayMode/HitFlashTests.cs`:

```csharp
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class HitFlashTests
    {
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        GameObject target;

        [TearDown]
        public void TearDown()
        {
            if (target != null)
                Object.Destroy(target);
        }

        [UnityTest]
        public IEnumerator Damage_TintsRendererWhite_ThenClears()
        {
            target = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            var health = target.AddComponent<Health>();
            target.AddComponent<HitFlash>();
            var renderer = target.GetComponent<Renderer>();
            yield return null;

            health.TakeDamage(10);

            Assert.That(renderer.HasPropertyBlock(), Is.True);
            var block = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(block);
            Assert.That(block.GetColor(BaseColorId), Is.EqualTo(Color.white));

            yield return new WaitForSeconds(0.3f);

            Assert.That(renderer.HasPropertyBlock(), Is.False);
        }
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `Tools/run-tests.sh EditMode`
Expected: `error CS0246: ... 'Health' could not be found`, `EXIT=3`.

- [ ] **Step 4: Write the implementation**

`Assets/_Project/Scripts/Combat/Health.cs`:

```csharp
using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>Hit points for anything that can be attacked. Dies once, at zero.</summary>
    public sealed class Health : MonoBehaviour
    {
        [SerializeField, Min(1)] int max = 100;
        [SerializeField] bool disableOnDeath = true;

        int damageTaken;
        bool hasDied;

        public int Max => max;
        public int Current => Mathf.Max(0, max - damageTaken);
        public bool IsAlive => !hasDied;

        /// <summary>Raised with the damage amount whenever a living target takes damage.</summary>
        public event Action<int> Damaged;
        /// <summary>Raised exactly once, when hit points reach zero.</summary>
        public event Action Died;

        public void TakeDamage(int amount)
        {
            if (amount < 0)
                throw new ArgumentOutOfRangeException(nameof(amount), amount, "Damage cannot be negative.");
            if (hasDied || amount == 0)
                return;

            damageTaken = (int)Math.Min((long)max, (long)damageTaken + amount);
            Damaged?.Invoke(amount);

            if (Current > 0)
                return;
            hasDied = true;
            Died?.Invoke();
            if (disableOnDeath)
                gameObject.SetActive(false);
        }
    }
}
```

`Assets/_Project/Scripts/Combat/HitFlash.cs`:

```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>Debug feedback: briefly tints the renderer when its Health takes damage. Runs on scaled time.</summary>
    [RequireComponent(typeof(Health))]
    public sealed class HitFlash : MonoBehaviour
    {
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        [SerializeField] Renderer targetRenderer;
        [SerializeField] Color flashColor = Color.white;
        [SerializeField, Min(0f)] float duration = 0.15f;

        Health health;
        MaterialPropertyBlock block;
        float remaining;

        void Awake()
        {
            health = GetComponent<Health>();
            if (targetRenderer == null)
                targetRenderer = GetComponentInChildren<Renderer>();
            block = new MaterialPropertyBlock();
        }

        void OnEnable() => health.Damaged += OnDamaged;

        void OnDisable()
        {
            health.Damaged -= OnDamaged;
            remaining = 0f;
            ClearFlash();
        }

        void Update()
        {
            if (remaining <= 0f)
                return;
            remaining -= Time.deltaTime;
            if (remaining <= 0f)
                ClearFlash();
        }

        void OnDamaged(int amount)
        {
            if (targetRenderer == null)
                return;
            remaining = duration;
            targetRenderer.GetPropertyBlock(block);
            block.SetColor(BaseColorId, flashColor);
            targetRenderer.SetPropertyBlock(block);
        }

        void ClearFlash()
        {
            if (targetRenderer != null)
                targetRenderer.SetPropertyBlock(null);
        }
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `Tools/run-tests.sh EditMode` → expected `total="23" passed="23"`, `EXIT=0`.
Run: `Tools/run-tests.sh PlayMode` → expected `total="3" passed="3"`, `EXIT=0`.

- [ ] **Step 6: Commit**

```bash
git add Assets/_Project/Scripts Assets/_Project/Tests
git status --short
git commit -m "Add health and hit flash feedback

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Commands, `CommandResolver`, and unit components

**Files:**
- Create: `Assets/_Project/Scripts/Commands/UnitCommands.cs`
- Create: `Assets/_Project/Scripts/Controls/CommandResolver.cs`
- Create: `Assets/_Project/Scripts/Units/UnitMover.cs`
- Create: `Assets/_Project/Scripts/Units/UnitAttacker.cs`
- Create: `Assets/_Project/Scripts/Units/CommandableUnit.cs`
- Test: `Assets/_Project/Tests/EditMode/CommandResolverTests.cs`
- Test: `Assets/_Project/Tests/EditMode/CommandableUnitTests.cs`

**Interfaces:**
- Consumes: `Health` (Task 3): `IsAlive`, `TakeDamage(int)`, `transform`.
- Produces:
  - `abstract class UnitCommand`.
  - `sealed class MoveCommand : UnitCommand { MoveCommand(Vector3 destination); Vector3 Destination { get; } }`.
  - `sealed class AttackCommand : UnitCommand { AttackCommand(Health target); Health Target { get; } }`. The constructor throws `ArgumentNullException` on a null target.
  - `static class CommandResolver { static UnitCommand Resolve(Health clicked, Vector3 point); }`.
  - `UnitMover : MonoBehaviour` (requires `NavMeshAgent`): `bool MoveTo(Vector3 point)`, `void Stop()`, `bool HasArrived { get; }`. Agent tuning is serialized: `speed` = 5, `angularSpeed` = 720, `acceleration` = 20, `stoppingDistance` = 0.1. It's applied in `Awake`.
  - `UnitAttacker : MonoBehaviour`: `float Range` (2), `int Damage` (25), `float Cooldown` (1), `bool IsInRange(Health target)`, `bool TryAttack(Health target)`.
  - `CommandableUnit : MonoBehaviour` (requires `UnitMover` and `UnitAttacker`): `UnitCommand CurrentCommand { get; }`, `bool Issue(UnitCommand command)`. `Issue` throws `ArgumentNullException` on null and `ArgumentException` on an unsupported type.

- [ ] **Step 1: Write the failing EditMode tests**

`Assets/_Project/Tests/EditMode/CommandResolverTests.cs`:

```csharp
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class CommandResolverTests
    {
        GameObject host;
        Health health;

        [SetUp]
        public void SetUp()
        {
            host = new GameObject("Target");
            health = host.AddComponent<Health>();
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(host);

        [Test]
        public void ClickOnLivingTarget_ResolvesToAttack()
        {
            var command = CommandResolver.Resolve(health, new Vector3(1f, 0f, 2f));
            Assert.That(command, Is.TypeOf<AttackCommand>());
            Assert.That(((AttackCommand)command).Target, Is.SameAs(health));
        }

        [Test]
        public void ClickOnGround_ResolvesToMoveToClickedPoint()
        {
            var point = new Vector3(3f, 0f, -4f);
            var command = CommandResolver.Resolve(null, point);
            Assert.That(command, Is.TypeOf<MoveCommand>());
            Assert.That(((MoveCommand)command).Destination, Is.EqualTo(point));
        }

        [Test]
        public void ClickOnDeadTarget_ResolvesToMove()
        {
            health.TakeDamage(health.Max);
            var point = new Vector3(5f, 0f, 5f);
            var command = CommandResolver.Resolve(health, point);
            Assert.That(command, Is.TypeOf<MoveCommand>());
        }

        [Test]
        public void AttackCommand_RejectsNullTarget()
        {
            Assert.Throws<System.ArgumentNullException>(() => new AttackCommand(null));
        }
    }
}
```

`Assets/_Project/Tests/EditMode/CommandableUnitTests.cs`:

```csharp
using System;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class CommandableUnitTests
    {
        sealed class UnsupportedCommand : UnitCommand { }

        GameObject unitHost;
        GameObject targetHost;
        CommandableUnit unit;
        Health target;

        [SetUp]
        public void SetUp()
        {
            unitHost = new GameObject("Unit");
            unit = unitHost.AddComponent<CommandableUnit>();
            targetHost = new GameObject("Target");
            target = targetHost.AddComponent<Health>();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(unitHost);
            Object.DestroyImmediate(targetHost);
        }

        [Test]
        public void RequiredComponentsAreAdded()
        {
            Assert.That(unitHost.GetComponent<UnitMover>(), Is.Not.Null);
            Assert.That(unitHost.GetComponent<UnitAttacker>(), Is.Not.Null);
            Assert.That(unitHost.GetComponent<UnityEngine.AI.NavMeshAgent>(), Is.Not.Null);
        }

        [Test]
        public void Issue_Null_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => unit.Issue(null));
        }

        [Test]
        public void Issue_UnsupportedCommand_Throws()
        {
            Assert.Throws<ArgumentException>(() => unit.Issue(new UnsupportedCommand()));
            Assert.That(unit.CurrentCommand, Is.Null);
        }

        [Test]
        public void Issue_AttackOnLivingTarget_BecomesCurrentCommand()
        {
            var attack = new AttackCommand(target);
            Assert.That(unit.Issue(attack), Is.True);
            Assert.That(unit.CurrentCommand, Is.SameAs(attack));
        }

        [Test]
        public void Issue_AttackOnDeadTarget_IsRejected()
        {
            var attack = new AttackCommand(target);
            target.TakeDamage(target.Max);
            Assert.That(unit.Issue(attack), Is.False);
            Assert.That(unit.CurrentCommand, Is.Null);
        }

        [Test]
        public void Issue_MoveWithoutNavMesh_IsRejectedAndKeepsCurrentOrder()
        {
            var attack = new AttackCommand(target);
            unit.Issue(attack);
            UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("not on a NavMesh"));
            Assert.That(unit.Issue(new MoveCommand(Vector3.one)), Is.False);
            Assert.That(unit.CurrentCommand, Is.SameAs(attack));
        }

        [Test]
        public void Attacker_HasPrototypeDefaults()
        {
            var attacker = unitHost.GetComponent<UnitAttacker>();
            Assert.That(attacker.Range, Is.EqualTo(2f));
            Assert.That(attacker.Damage, Is.EqualTo(25));
            Assert.That(attacker.Cooldown, Is.EqualTo(1f));
        }

        [Test]
        public void Attacker_IsInRange_UsesHorizontalDistance()
        {
            var attacker = unitHost.GetComponent<UnitAttacker>();
            targetHost.transform.position = new Vector3(0f, 10f, 1.9f);
            Assert.That(attacker.IsInRange(target), Is.True);
            targetHost.transform.position = new Vector3(0f, 0f, 2.1f);
            Assert.That(attacker.IsInRange(target), Is.False);
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `Tools/run-tests.sh EditMode`
Expected: `error CS0246` for `CommandResolver`, `AttackCommand`, `CommandableUnit` and the other new types, `EXIT=3`.

- [ ] **Step 3: Write the implementation**

`Assets/_Project/Scripts/Commands/UnitCommands.cs`:

```csharp
using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// An order for a unit. Commands are plain data: whoever creates one (player input today;
    /// AI, queues or selection later) hands it to CommandableUnit.Issue, and the unit decides
    /// how to carry it out. Future commands (Stop, Interact) are new subclasses.
    /// </summary>
    public abstract class UnitCommand { }

    public sealed class MoveCommand : UnitCommand
    {
        public MoveCommand(Vector3 destination) => Destination = destination;

        public Vector3 Destination { get; }
    }

    public sealed class AttackCommand : UnitCommand
    {
        public AttackCommand(Health target)
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));
            Target = target;
        }

        public Health Target { get; }
    }
}
```

`Assets/_Project/Scripts/Controls/CommandResolver.cs`:

```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>Chooses the command for a context click: attack a living target, otherwise move to the clicked point.</summary>
    public static class CommandResolver
    {
        public static UnitCommand Resolve(Health clicked, Vector3 point)
        {
            if (clicked != null && clicked.IsAlive)
                return new AttackCommand(clicked);
            return new MoveCommand(point);
        }
    }
}
```

`Assets/_Project/Scripts/Units/UnitMover.cs`:

```csharp
using UnityEngine;
using UnityEngine.AI;

namespace Blackglass
{
    /// <summary>Moves the unit over the NavMesh. The only component that talks to the NavMeshAgent.</summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public sealed class UnitMover : MonoBehaviour
    {
        const float SnapRadius = 2f;
        const float ArrivalTolerance = 0.1f;

        [SerializeField, Min(0f)] float speed = 5f;
        [SerializeField, Min(0f)] float angularSpeed = 720f;
        [SerializeField, Min(0f)] float acceleration = 20f;
        [SerializeField, Min(0f)] float stoppingDistance = 0.1f;

        NavMeshAgent agent;

        NavMeshAgent Agent => agent != null ? agent : agent = GetComponent<NavMeshAgent>();

        /// <summary>True when the unit is at its destination, or its path has ended (nothing left to walk).</summary>
        public bool HasArrived
        {
            get
            {
                if (!Agent.isOnNavMesh || Agent.pathPending)
                    return false;
                var offset = Agent.destination - transform.position;
                offset.y = 0f;
                return offset.magnitude <= Agent.stoppingDistance + ArrivalTolerance || !Agent.hasPath;
            }
        }

        void Awake()
        {
            Agent.speed = speed;
            Agent.angularSpeed = angularSpeed;
            Agent.acceleration = acceleration;
            Agent.stoppingDistance = stoppingDistance;
        }

        /// <summary>Starts moving to the nearest walkable point. Returns false (and logs a warning) if there is none.</summary>
        public bool MoveTo(Vector3 point)
        {
            if (!Agent.isOnNavMesh)
            {
                Debug.LogWarning($"{name} cannot move: it is not on a NavMesh.", this);
                return false;
            }
            if (!NavMesh.SamplePosition(point, out var hit, SnapRadius, NavMesh.AllAreas))
            {
                Debug.LogWarning($"{name} cannot move: no walkable NavMesh point within {SnapRadius} m of {point}.", this);
                return false;
            }
            Agent.isStopped = false;
            return Agent.SetDestination(hit.position);
        }

        /// <summary>Stops immediately (no coasting) and clears the path.</summary>
        public void Stop()
        {
            if (!Agent.isOnNavMesh)
                return;
            Agent.ResetPath();
            Agent.velocity = Vector3.zero;
        }
    }
}
```

`Assets/_Project/Scripts/Units/UnitAttacker.cs`:

```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>Prototype melee attack: fixed range, damage and cooldown. Cooldown uses scaled time.</summary>
    public sealed class UnitAttacker : MonoBehaviour
    {
        [SerializeField, Min(0.1f)] float range = 2f;
        [SerializeField, Min(0)] int damage = 25;
        [SerializeField, Min(0f)] float cooldown = 1f;

        float nextAttackTime;

        public float Range => range;
        public int Damage => damage;
        public float Cooldown => cooldown;

        /// <summary>Horizontal centre-to-centre distance check.</summary>
        public bool IsInRange(Health target)
        {
            if (target == null)
                return false;
            var offset = target.transform.position - transform.position;
            offset.y = 0f;
            return offset.sqrMagnitude <= range * range;
        }

        /// <summary>Hits the target if it is alive, in range and the cooldown has elapsed. Returns whether it hit.</summary>
        public bool TryAttack(Health target)
        {
            if (target == null || !target.IsAlive || !IsInRange(target) || Time.time < nextAttackTime)
                return false;
            nextAttackTime = Time.time + cooldown;
            target.TakeDamage(damage);
            return true;
        }
    }
}
```

`Assets/_Project/Scripts/Units/CommandableUnit.cs`:

```csharp
using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The single entry point for gameplay commands. Holds the current order and executes it
    /// each simulation frame using UnitMover and UnitAttacker. A new command replaces the old one.
    /// </summary>
    [RequireComponent(typeof(UnitMover), typeof(UnitAttacker))]
    public sealed class CommandableUnit : MonoBehaviour
    {
        const float ChaseRepathDistance = 0.5f;

        UnitMover mover;
        UnitAttacker attacker;
        bool chasing;
        Vector3 lastChaseTarget;

        public UnitCommand CurrentCommand { get; private set; }

        UnitMover Mover => mover != null ? mover : mover = GetComponent<UnitMover>();
        UnitAttacker Attacker => attacker != null ? attacker : attacker = GetComponent<UnitAttacker>();

        /// <summary>
        /// Gives the unit a new order. Returns false if the order cannot be carried out
        /// (unreachable destination, dead target); the current order then continues.
        /// </summary>
        public bool Issue(UnitCommand command)
        {
            switch (command)
            {
                case null:
                    throw new ArgumentNullException(nameof(command));

                case MoveCommand move:
                    if (!Mover.MoveTo(move.Destination))
                        return false;
                    chasing = false;
                    CurrentCommand = move;
                    return true;

                case AttackCommand attack:
                    if (!attack.Target.IsAlive)
                        return false;
                    Mover.Stop();
                    chasing = false;
                    CurrentCommand = attack;
                    return true;

                default:
                    throw new ArgumentException($"Unsupported command type {command.GetType().Name}.", nameof(command));
            }
        }

        void Update()
        {
            // Orders only advance while simulation time advances (tactical pause sets timeScale to 0).
            if (Time.deltaTime <= 0f)
                return;

            switch (CurrentCommand)
            {
                case MoveCommand _:
                    if (Mover.HasArrived)
                        CurrentCommand = null;
                    break;
                case AttackCommand attack:
                    UpdateAttack(attack.Target);
                    break;
            }
        }

        void UpdateAttack(Health target)
        {
            if (target == null || !target.IsAlive)
            {
                FinishAttack();
                return;
            }

            if (!Attacker.IsInRange(target))
            {
                var targetPosition = target.transform.position;
                if (!chasing || (targetPosition - lastChaseTarget).sqrMagnitude > ChaseRepathDistance * ChaseRepathDistance)
                {
                    if (!Mover.MoveTo(targetPosition))
                    {
                        FinishAttack();
                        return;
                    }
                    chasing = true;
                    lastChaseTarget = targetPosition;
                }
                return;
            }

            if (chasing)
            {
                Mover.Stop();
                chasing = false;
            }
            FaceTowards(target.transform.position);
            Attacker.TryAttack(target);
            if (!target.IsAlive)
                FinishAttack();
        }

        void FinishAttack()
        {
            Mover.Stop();
            chasing = false;
            CurrentCommand = null;
        }

        void FaceTowards(Vector3 point)
        {
            var direction = point - transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude > 0.0001f)
                transform.rotation = Quaternion.LookRotation(direction);
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `Tools/run-tests.sh EditMode`
Expected: `total="35" passed="35"`, `EXIT=0`.

If `Issue_MoveWithoutNavMesh_IsRejectedAndKeepsCurrentOrder` fails because Unity also logs a NavMeshAgent error in Edit mode, read the log message and add a matching `LogAssert.Expect` for that exact message. Don't remove the assertion on `CurrentCommand`.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts Assets/_Project/Tests
git status --short
git commit -m "Add unit commands, command resolver and unit components

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: NavMesh package and runtime unit behaviour (PlayMode)

**Files:**
- Modify: `Packages/manifest.json` (add `"com.unity.ai.navigation": "2.0.14"`)
- Modify: `Assets/_Project/Tests/PlayMode/Blackglass.Tests.PlayMode.asmdef` (add `Unity.AI.Navigation`)
- Create: `Assets/_Project/Tests/PlayMode/TestSupport/TestWorld.cs`
- Test: `Assets/_Project/Tests/PlayMode/CommandableUnitPlayModeTests.cs`

**Interfaces:**
- Consumes: `CommandableUnit.Issue/CurrentCommand`, `UnitMover`, `UnitAttacker`, `Health`, `TacticalPause` (Tasks 2–4).
- Produces (test support, used by Tasks 6–7), `Blackglass.Tests.TestWorld : IDisposable`:
  - `GameObject CreateEnvironment(params (Vector3 position, Vector3 scale)[] obstacles)`: 40×40 m ground plus obstacles, with a NavMesh built at runtime.
  - `CommandableUnit CreateUnit(Vector3 groundPosition)`.
  - `Health CreateDummy(Vector3 groundPosition)`.
  - `T Track<T>(T obj) where T : Object`.
  - `static IEnumerator WaitUntil(Func<bool> condition, float timeoutSeconds)`: uses real time, so it also works while paused.
  - `static float HorizontalDistance(Vector3 a, Vector3 b)`.
  - `void Dispose()`.

- [ ] **Step 1: Add the package**

In `Packages/manifest.json`, add this line as the first entry inside `"dependencies"` (keep alphabetical order):

```json
    "com.unity.ai.navigation": "2.0.14",
```

In `Assets/_Project/Tests/PlayMode/Blackglass.Tests.PlayMode.asmdef`, replace the `references` array with:

```json
    "references": [
        "Blackglass",
        "UnityEngine.TestRunner",
        "UnityEditor.TestRunner",
        "Unity.AI.Navigation"
    ],
```

- [ ] **Step 2: Write the test support class**

`Assets/_Project/Tests/PlayMode/TestSupport/TestWorld.cs`:

```csharp
using System;
using System.Collections;
using System.Collections.Generic;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    /// <summary>Builds small throwaway worlds for PlayMode tests and destroys them afterwards.</summary>
    internal sealed class TestWorld : IDisposable
    {
        readonly List<Object> created = new List<Object>();

        public T Track<T>(T obj) where T : Object
        {
            created.Add(obj);
            return obj;
        }

        /// <summary>40 x 40 m ground at the origin plus box obstacles, with a NavMesh built at runtime.</summary>
        public GameObject CreateEnvironment(params (Vector3 position, Vector3 scale)[] obstacles)
        {
            var root = Track(new GameObject("TestEnvironment"));
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.SetParent(root.transform, false);
            ground.transform.localScale = new Vector3(4f, 1f, 4f);

            foreach (var (position, scale) in obstacles)
            {
                var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
                box.name = "Obstacle";
                box.transform.SetParent(root.transform, false);
                box.transform.position = position;
                box.transform.localScale = scale;
            }

            var surface = root.AddComponent<NavMeshSurface>();
            surface.collectObjects = CollectObjects.Children;
            surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            surface.BuildNavMesh();
            return root;
        }

        public CommandableUnit CreateUnit(Vector3 groundPosition)
        {
            var unit = Track(GameObject.CreatePrimitive(PrimitiveType.Capsule));
            unit.name = "TestUnit";
            unit.transform.position = groundPosition + Vector3.up;
            unit.AddComponent<UnitMover>();
            unit.GetComponent<NavMeshAgent>().baseOffset = 1f;
            unit.AddComponent<UnitAttacker>();
            return unit.AddComponent<CommandableUnit>();
        }

        public Health CreateDummy(Vector3 groundPosition)
        {
            var dummy = Track(GameObject.CreatePrimitive(PrimitiveType.Cylinder));
            dummy.name = "TestDummy";
            dummy.transform.position = groundPosition + Vector3.up;
            var health = dummy.AddComponent<Health>();
            dummy.AddComponent<HitFlash>();
            return health;
        }

        /// <summary>Waits (in real time, so it also works while paused) until the condition holds or the timeout passes.</summary>
        public static IEnumerator WaitUntil(Func<bool> condition, float timeoutSeconds)
        {
            var deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (!condition() && Time.realtimeSinceStartup < deadline)
                yield return null;
        }

        public static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }

        public void Dispose()
        {
            for (var i = created.Count - 1; i >= 0; i--)
            {
                if (created[i] != null)
                    Object.Destroy(created[i]);
            }
            created.Clear();
        }
    }
}
```

- [ ] **Step 3: Write the PlayMode tests**

`Assets/_Project/Tests/PlayMode/CommandableUnitPlayModeTests.cs`:

```csharp
using System.Collections;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CommandableUnitPlayModeTests
    {
        TestWorld world;

        [SetUp]
        public void SetUp() => world = new TestWorld();

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        [UnityTest]
        public IEnumerator Move_ToFarSideOfWall_ArrivesAndClearsOrder()
        {
            world.CreateEnvironment((new Vector3(0f, 1f, 0f), new Vector3(8f, 2f, 1f)));
            var unit = world.CreateUnit(new Vector3(0f, 0f, -6f));
            yield return null;
            var destination = new Vector3(0f, 0f, 6f);

            Assert.That(unit.Issue(new MoveCommand(destination)), Is.True);
            Assert.That(unit.CurrentCommand, Is.TypeOf<MoveCommand>());

            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 15f);

            Assert.That(unit.CurrentCommand, Is.Null, "Move order did not complete in time");
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, destination), Is.LessThan(0.3f));
        }

        [UnityTest]
        public IEnumerator Issue_MoveOffNavMesh_IsRejectedAndPreviousOrderKept()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(-5f, 0f, -5f));
            yield return null;
            var first = new MoveCommand(new Vector3(5f, 0f, 5f));
            unit.Issue(first);

            LogAssert.Expect(LogType.Warning, new Regex("no walkable NavMesh point"));
            Assert.That(unit.Issue(new MoveCommand(new Vector3(100f, 0f, 100f))), Is.False);

            Assert.That(unit.CurrentCommand, Is.SameAs(first));
        }

        [UnityTest]
        public IEnumerator Attack_OutOfRange_ChasesAndDestroysTarget()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(0f, 0f, -8f));
            var dummy = world.CreateDummy(new Vector3(0f, 0f, 8f));
            yield return null;

            Assert.That(unit.Issue(new AttackCommand(dummy)), Is.True);
            yield return TestWorld.WaitUntil(() => dummy.Current < dummy.Max, 10f);
            Assert.That(dummy.Current, Is.LessThan(dummy.Max), "Unit never reached and hit the dummy");

            yield return TestWorld.WaitUntil(() => !dummy.IsAlive, 10f);

            Assert.That(dummy.IsAlive, Is.False);
            Assert.That(dummy.gameObject.activeSelf, Is.False);
            Assert.That(unit.CurrentCommand, Is.Null);
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, dummy.transform.position), Is.LessThanOrEqualTo(2.5f));
        }

        [UnityTest]
        public IEnumerator Attack_RespectsCooldown()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(0f, 0f, 0f));
            var dummy = world.CreateDummy(new Vector3(0f, 0f, 1.5f));
            yield return null;

            unit.Issue(new AttackCommand(dummy));
            yield return TestWorld.WaitUntil(() => dummy.Current < dummy.Max, 2f);
            Assert.That(dummy.Current, Is.EqualTo(75));

            yield return new WaitForSeconds(0.5f);
            Assert.That(dummy.Current, Is.EqualTo(75), "Second hit landed before the cooldown");

            yield return new WaitForSeconds(0.7f);
            Assert.That(dummy.Current, Is.EqualTo(50));
        }

        [UnityTest]
        public IEnumerator Attack_TargetKilledElsewhere_UnitStopsAndClearsOrder()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(0f, 0f, -10f));
            var dummy = world.CreateDummy(new Vector3(0f, 0f, 10f));
            yield return null;
            unit.Issue(new AttackCommand(dummy));
            yield return new WaitForSeconds(0.3f);

            dummy.TakeDamage(1000);
            yield return null;
            yield return null;

            Assert.That(unit.CurrentCommand, Is.Null);
            var stoppedAt = unit.transform.position;
            yield return new WaitForSeconds(0.5f);
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, stoppedAt), Is.LessThan(0.5f));
        }

        [UnityTest]
        public IEnumerator Paused_MoveIsAcceptedButWaitsUntilResume()
        {
            world.CreateEnvironment();
            var pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            var unit = world.CreateUnit(new Vector3(-5f, 0f, 0f));
            yield return null;
            var start = unit.transform.position;
            var destination = new Vector3(5f, 0f, 0f);

            pause.Pause();
            Assert.That(unit.Issue(new MoveCommand(destination)), Is.True);
            yield return new WaitForSecondsRealtime(1f);

            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, start), Is.LessThan(0.01f), "Unit moved while paused");
            Assert.That(unit.CurrentCommand, Is.TypeOf<MoveCommand>());

            pause.Resume();
            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 10f);
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, destination), Is.LessThan(0.3f));
        }

        [UnityTest]
        public IEnumerator Paused_AttackInRange_DealsNoDamageUntilResume()
        {
            world.CreateEnvironment();
            var pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            var unit = world.CreateUnit(new Vector3(0f, 0f, 0f));
            var dummy = world.CreateDummy(new Vector3(0f, 0f, 1.5f));
            yield return null;

            pause.Pause();
            unit.Issue(new AttackCommand(dummy));
            yield return new WaitForSecondsRealtime(1f);
            Assert.That(dummy.Current, Is.EqualTo(dummy.Max), "Attack landed while paused");

            pause.Resume();
            yield return TestWorld.WaitUntil(() => dummy.Current < dummy.Max, 2f);
            Assert.That(dummy.Current, Is.EqualTo(75));
        }
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `Tools/run-tests.sh PlayMode`
Expected: `total="10" passed="10"`, `EXIT=0`. The first run also resolves the new package, which takes longer.

These tests exercise Task 4's code at runtime, so they may expose a real defect. If one fails, use `superpowers:systematic-debugging`. Fix the root cause in `UnitMover` or `CommandableUnit`; don't weaken the assertion. Then re-run.

- [ ] **Step 5: Run EditMode again to confirm nothing regressed**

Run: `Tools/run-tests.sh EditMode` → expected `total="35" passed="35"`, `EXIT=0`.

- [ ] **Step 6: Commit**

```bash
git add Packages/manifest.json Packages/packages-lock.json Assets/_Project/Tests
git status --short
git commit -m "Add AI Navigation and runtime tests for unit orders

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Input actions asset and `TacticalCameraController`

**Files:**
- Move: `Assets/InputSystem_Actions.inputactions` (+ `.meta`) → `Assets/_Project/Input/BlackglassControls.inputactions` (+ `.meta`)
- Rewrite content: `Assets/_Project/Input/BlackglassControls.inputactions`
- Modify: `Packages/manifest.json` (add `testables`)
- Modify: `Assets/_Project/Tests/PlayMode/Blackglass.Tests.PlayMode.asmdef` (add Input System references)
- Create: `Assets/_Project/Scripts/CameraControl/TacticalCameraController.cs`
- Create: `Assets/_Project/Tests/PlayMode/TestSupport/TestControls.cs`
- Test: `Assets/_Project/Tests/PlayMode/TacticalCameraControllerTests.cs`

**Interfaces:**
- Consumes: `ClickDragDetector` (Task 1), `TacticalPause` (Task 2).
- Produces:
  - **Input action paths:** `Camera/Pan`, `Camera/Rotate`, `Camera/RotateDrag`, `Camera/PointerPosition`, `Camera/Zoom`, `Commands/Command`, `Commands/PointerPosition`, `Commands/TogglePause`.
  - **`TacticalCameraController : MonoBehaviour`:**
    - `float Distance { get; }`, `float Yaw { get; }`.
    - `internal void Initialize(Camera viewCamera, InputActionReference pan, InputActionReference rotate, InputActionReference rotateDrag, InputActionReference pointerPosition, InputActionReference zoom)`.
    - Serialized field names, used by Task 8's builder: `viewCamera`, `panAction`, `rotateAction`, `rotateDragAction`, `pointerPositionAction`, `zoomAction`, `distance`.
  - **`TestControls`** (test support):
    - `const string AssetPath`.
    - `static InputActionAsset Load()`.
    - `static InputActionReference Ref(InputActionAsset asset, string actionPath)`.

- [ ] **Step 1: Move the actions asset, keeping its GUID**

```bash
mkdir -p Assets/_Project/Input
git mv Assets/InputSystem_Actions.inputactions Assets/_Project/Input/BlackglassControls.inputactions
git mv Assets/InputSystem_Actions.inputactions.meta Assets/_Project/Input/BlackglassControls.inputactions.meta
grep -n 'guid' Assets/_Project/Input/BlackglassControls.inputactions.meta   # must still be 052faaac586de48259a63d0c4782560b
```

- [ ] **Step 2: Replace the asset content**

Overwrite `Assets/_Project/Input/BlackglassControls.inputactions` with:

```json
{
    "version": 1,
    "name": "BlackglassControls",
    "maps": [
        {
            "name": "Camera",
            "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9d01",
            "actions": [
                { "name": "Pan", "type": "Value", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9d02", "expectedControlType": "Vector2", "processors": "", "interactions": "", "initialStateCheck": true },
                { "name": "Rotate", "type": "Value", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9d03", "expectedControlType": "Axis", "processors": "", "interactions": "", "initialStateCheck": true },
                { "name": "RotateDrag", "type": "Button", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9d04", "expectedControlType": "Button", "processors": "", "interactions": "", "initialStateCheck": false },
                { "name": "PointerPosition", "type": "Value", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9d05", "expectedControlType": "Vector2", "processors": "", "interactions": "", "initialStateCheck": true },
                { "name": "Zoom", "type": "Value", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9d06", "expectedControlType": "Axis", "processors": "", "interactions": "", "initialStateCheck": false }
            ],
            "bindings": [
                { "name": "WASD", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9d11", "path": "2DVector", "interactions": "", "processors": "", "groups": "", "action": "Pan", "isComposite": true, "isPartOfComposite": false },
                { "name": "up", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9d12", "path": "<Keyboard>/w", "interactions": "", "processors": "", "groups": "", "action": "Pan", "isComposite": false, "isPartOfComposite": true },
                { "name": "down", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9d13", "path": "<Keyboard>/s", "interactions": "", "processors": "", "groups": "", "action": "Pan", "isComposite": false, "isPartOfComposite": true },
                { "name": "left", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9d14", "path": "<Keyboard>/a", "interactions": "", "processors": "", "groups": "", "action": "Pan", "isComposite": false, "isPartOfComposite": true },
                { "name": "right", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9d15", "path": "<Keyboard>/d", "interactions": "", "processors": "", "groups": "", "action": "Pan", "isComposite": false, "isPartOfComposite": true },
                { "name": "QE", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9d16", "path": "1DAxis", "interactions": "", "processors": "", "groups": "", "action": "Rotate", "isComposite": true, "isPartOfComposite": false },
                { "name": "negative", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9d17", "path": "<Keyboard>/q", "interactions": "", "processors": "", "groups": "", "action": "Rotate", "isComposite": false, "isPartOfComposite": true },
                { "name": "positive", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9d18", "path": "<Keyboard>/e", "interactions": "", "processors": "", "groups": "", "action": "Rotate", "isComposite": false, "isPartOfComposite": true },
                { "name": "", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9d19", "path": "<Mouse>/rightButton", "interactions": "", "processors": "", "groups": "", "action": "RotateDrag", "isComposite": false, "isPartOfComposite": false },
                { "name": "", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9d1a", "path": "<Pointer>/position", "interactions": "", "processors": "", "groups": "", "action": "PointerPosition", "isComposite": false, "isPartOfComposite": false },
                { "name": "", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9d1b", "path": "<Mouse>/scroll/y", "interactions": "", "processors": "", "groups": "", "action": "Zoom", "isComposite": false, "isPartOfComposite": false }
            ]
        },
        {
            "name": "Commands",
            "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9e01",
            "actions": [
                { "name": "Command", "type": "Button", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9e02", "expectedControlType": "Button", "processors": "", "interactions": "", "initialStateCheck": false },
                { "name": "PointerPosition", "type": "Value", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9e03", "expectedControlType": "Vector2", "processors": "", "interactions": "", "initialStateCheck": true },
                { "name": "TogglePause", "type": "Button", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9e04", "expectedControlType": "Button", "processors": "", "interactions": "", "initialStateCheck": false }
            ],
            "bindings": [
                { "name": "", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9e11", "path": "<Mouse>/rightButton", "interactions": "", "processors": "", "groups": "", "action": "Command", "isComposite": false, "isPartOfComposite": false },
                { "name": "", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9e12", "path": "<Pointer>/position", "interactions": "", "processors": "", "groups": "", "action": "PointerPosition", "isComposite": false, "isPartOfComposite": false },
                { "name": "", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9e13", "path": "<Keyboard>/space", "interactions": "", "processors": "", "groups": "", "action": "TogglePause", "isComposite": false, "isPartOfComposite": false }
            ]
        }
    ],
    "controlSchemes": []
}
```

- [ ] **Step 3: Enable the Input System test fixture**

In `Packages/manifest.json`, add a top-level `testables` array after the closing `}` of `"dependencies"`. The file must end like this:

```json
    "com.unity.modules.xr": "1.0.0"
  },
  "testables": [
    "com.unity.inputsystem"
  ]
}
```

In `Assets/_Project/Tests/PlayMode/Blackglass.Tests.PlayMode.asmdef`, replace the `references` array with:

```json
    "references": [
        "Blackglass",
        "UnityEngine.TestRunner",
        "UnityEditor.TestRunner",
        "Unity.AI.Navigation",
        "Unity.InputSystem",
        "Unity.InputSystem.TestFramework"
    ],
```

- [ ] **Step 4: Write the test support class and the failing tests**

`Assets/_Project/Tests/PlayMode/TestSupport/TestControls.cs`:

```csharp
#if UNITY_EDITOR
using System;
using UnityEngine.InputSystem;

namespace Blackglass.Tests
{
    /// <summary>Gives tests the project's real input actions so bindings are tested too.</summary>
    internal static class TestControls
    {
        public const string AssetPath = "Assets/_Project/Input/BlackglassControls.inputactions";

        public static InputActionAsset Load()
        {
            var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<InputActionAsset>(AssetPath);
            if (asset == null)
                throw new InvalidOperationException($"Input actions not found at {AssetPath}");
            return asset;
        }

        public static InputActionReference Ref(InputActionAsset asset, string actionPath) =>
            InputActionReference.Create(asset.FindAction(actionPath, throwIfNotFound: true));
    }
}
#endif
```

`Assets/_Project/Tests/PlayMode/TacticalCameraControllerTests.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class TacticalCameraControllerTests : InputTestFixture
    {
        Keyboard keyboard;
        Mouse mouse;
        TestWorld world;
        TacticalCameraController controller;
        TacticalPause pause;

        public override void Setup()
        {
            base.Setup();
            keyboard = InputSystem.AddDevice<Keyboard>();
            mouse = InputSystem.AddDevice<Mouse>();
            world = new TestWorld();

            var actions = TestControls.Load();
            var rig = world.Track(new GameObject("CameraRig"));
            rig.SetActive(false);
            var cameraObject = new GameObject("Camera");
            cameraObject.transform.SetParent(rig.transform, false);
            var viewCamera = cameraObject.AddComponent<Camera>();
            controller = rig.AddComponent<TacticalCameraController>();
            controller.Initialize(viewCamera,
                TestControls.Ref(actions, "Camera/Pan"),
                TestControls.Ref(actions, "Camera/Rotate"),
                TestControls.Ref(actions, "Camera/RotateDrag"),
                TestControls.Ref(actions, "Camera/PointerPosition"),
                TestControls.Ref(actions, "Camera/Zoom"));
            rig.SetActive(true);

            pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
        }

        public override void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
            base.TearDown();
        }

        [UnityTest]
        public IEnumerator HoldingW_PansForward()
        {
            var start = controller.transform.position;
            Press(keyboard.wKey);
            yield return new WaitForSecondsRealtime(0.2f);
            Release(keyboard.wKey);
            yield return null;

            Assert.That(controller.transform.position.z, Is.GreaterThan(start.z + 0.5f));
            Assert.That(controller.transform.position.x, Is.EqualTo(start.x).Within(0.01f));
        }

        [UnityTest]
        public IEnumerator HoldingW_PansWhilePaused()
        {
            pause.Pause();
            var start = controller.transform.position;
            Press(keyboard.wKey);
            yield return new WaitForSecondsRealtime(0.2f);
            Release(keyboard.wKey);
            yield return null;

            Assert.That(controller.transform.position.z, Is.GreaterThan(start.z + 0.5f));
        }

        [UnityTest]
        public IEnumerator ScrollUp_ZoomsInOneStepWhilePaused()
        {
            pause.Pause();
            yield return null;
            var start = controller.Distance;

            Set(mouse.scroll, new Vector2(0f, 120f));
            yield return null;
            yield return null;

            Assert.That(controller.Distance, Is.EqualTo(start - 2f).Within(0.001f));
        }

        [UnityTest]
        public IEnumerator Zoom_IsClampedToRange()
        {
            for (var i = 0; i < 30; i++)
            {
                Set(mouse.scroll, new Vector2(0f, 120f));
                yield return null;
                Set(mouse.scroll, Vector2.zero);
                yield return null;
            }
            Assert.That(controller.Distance, Is.EqualTo(5f).Within(0.001f));
        }

        [UnityTest]
        public IEnumerator HoldingE_RotatesWhilePaused()
        {
            pause.Pause();
            var startYaw = controller.Yaw;
            Press(keyboard.eKey);
            yield return new WaitForSecondsRealtime(0.2f);
            Release(keyboard.eKey);
            yield return null;

            Assert.That(Mathf.DeltaAngle(startYaw, controller.Yaw), Is.GreaterThan(5f));
        }

        [UnityTest]
        public IEnumerator RightDrag_RotatesByPixelsTravelledAfterThreshold_WhilePaused()
        {
            pause.Pause();
            Set(mouse.position, new Vector2(100f, 100f));
            yield return null;
            var startYaw = controller.Yaw;

            Press(mouse.rightButton);
            yield return null;
            Set(mouse.position, new Vector2(110f, 100f));   // crosses the 6 px threshold, no rotation yet
            yield return null;
            Set(mouse.position, new Vector2(210f, 100f));   // +100 px of drag
            yield return null;
            Release(mouse.rightButton);
            yield return null;

            Assert.That(Mathf.DeltaAngle(startYaw, controller.Yaw), Is.EqualTo(25f).Within(0.5f));
        }

        [UnityTest]
        public IEnumerator SmallRightClick_DoesNotRotate()
        {
            Set(mouse.position, new Vector2(100f, 100f));
            yield return null;
            var startYaw = controller.Yaw;

            Press(mouse.rightButton);
            yield return null;
            Set(mouse.position, new Vector2(104f, 100f));
            yield return null;
            Release(mouse.rightButton);
            yield return null;

            Assert.That(controller.Yaw, Is.EqualTo(startYaw).Within(0.001f));
        }
    }
}
#endif
```

- [ ] **Step 5: Run the tests to verify they fail**

Run: `Tools/run-tests.sh PlayMode`
Expected: `error CS0246: ... 'TacticalCameraController' could not be found`, `EXIT=3`.

- [ ] **Step 6: Write the implementation**

`Assets/_Project/Scripts/CameraControl/TacticalCameraController.cs`:

```csharp
using UnityEngine;
using UnityEngine.InputSystem;

namespace Blackglass
{
    /// <summary>
    /// Simple strategy camera orbiting a pivot on the ground. Runs entirely on unscaled time
    /// so it keeps working during tactical pause. Lives on the pivot; the camera is a child.
    /// </summary>
    public sealed class TacticalCameraController : MonoBehaviour
    {
        [SerializeField] Camera viewCamera;

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
        [SerializeField, Min(0f)] float dragThresholdPixels = 6f;
        [SerializeField, Min(0f)] float zoomStep = 2f;
        [SerializeField, Min(1f)] float minDistance = 5f;
        [SerializeField, Min(1f)] float maxDistance = 40f;
        [SerializeField, Min(1f)] float distance = 20f;
        [SerializeField, Range(10f, 89f)] float pitch = 55f;
        [SerializeField, Min(0f)] float boundsHalfSize = 25f;

        ClickDragDetector dragDetector;
        Vector2 lastPointerPosition;
        int pendingZoomSteps;

        public float Distance => distance;
        public float Yaw => transform.eulerAngles.y;

        internal void Initialize(Camera camera, InputActionReference pan, InputActionReference rotate,
            InputActionReference rotateDrag, InputActionReference pointerPosition, InputActionReference zoom)
        {
            viewCamera = camera;
            panAction = pan;
            rotateAction = rotate;
            rotateDragAction = rotateDrag;
            pointerPositionAction = pointerPosition;
            zoomAction = zoom;
        }

        void Awake() => dragDetector = new ClickDragDetector(dragThresholdPixels);

        void OnEnable()
        {
            SetEnabled(true);
            if (zoomAction != null)
                zoomAction.action.performed += OnZoom;
            if (rotateDragAction != null)
            {
                rotateDragAction.action.started += OnRotateDragStarted;
                rotateDragAction.action.canceled += OnRotateDragEnded;
            }
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
            SetEnabled(false);
        }

        void Update()
        {
            var deltaTime = Time.unscaledDeltaTime;

            if (pendingZoomSteps != 0)
            {
                distance = Mathf.Clamp(distance - pendingZoomSteps * zoomStep, minDistance, maxDistance);
                pendingZoomSteps = 0;
            }

            var yaw = transform.eulerAngles.y + Read<float>(rotateAction) * keyRotateSpeed * deltaTime;
            if (dragDetector.IsPressed)
            {
                var pointer = Read<Vector2>(pointerPositionAction);
                var wasDragging = dragDetector.IsDragging;
                dragDetector.Track(pointer);
                if (wasDragging)
                    yaw += (pointer.x - lastPointerPosition.x) * dragRotateDegreesPerPixel;
                lastPointerPosition = pointer;
            }

            var rotation = Quaternion.Euler(0f, yaw, 0f);
            var pan = Read<Vector2>(panAction);
            var speed = panSpeed * (distance / panReferenceDistance);
            var position = transform.position + rotation * new Vector3(pan.x, 0f, pan.y) * (speed * deltaTime);
            position.x = Mathf.Clamp(position.x, -boundsHalfSize, boundsHalfSize);
            position.z = Mathf.Clamp(position.z, -boundsHalfSize, boundsHalfSize);

            transform.SetPositionAndRotation(position, rotation);
            ApplyCameraPose();
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
            lastPointerPosition = Read<Vector2>(pointerPositionAction);
            dragDetector.Press(lastPointerPosition);
        }

        void OnRotateDragEnded(InputAction.CallbackContext context) =>
            dragDetector.Release(Read<Vector2>(pointerPositionAction));

        void ApplyCameraPose()
        {
            if (viewCamera == null)
                return;
            var localRotation = Quaternion.Euler(pitch, 0f, 0f);
            viewCamera.transform.SetLocalPositionAndRotation(localRotation * new Vector3(0f, 0f, -distance), localRotation);
        }

        void SetEnabled(bool enable)
        {
            foreach (var reference in new[] { panAction, rotateAction, rotateDragAction, pointerPositionAction, zoomAction })
            {
                if (reference == null || reference.action == null)
                    continue;
                if (enable)
                    reference.action.Enable();
                else
                    reference.action.Disable();
            }
        }

        static T Read<T>(InputActionReference reference) where T : struct =>
            reference != null && reference.action != null ? reference.action.ReadValue<T>() : default;
    }
}
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `Tools/run-tests.sh PlayMode` → expected `total="17" passed="17"`, `EXIT=0`.
Run: `Tools/run-tests.sh EditMode` → expected `total="35" passed="35"`, `EXIT=0`.

If the scroll test fails because the Input System reports scroll in a different scale, nothing needs to change: zoom only uses the sign of the scroll value. Debug with `superpowers:systematic-debugging`.

If the drag-rotation test fails because the pointer value read in `Update` lags by one event, look at what `Read<Vector2>` returns in each frame before changing anything.

- [ ] **Step 8: Commit**

```bash
git add Assets/_Project Packages/manifest.json Packages/packages-lock.json   # the move itself was staged by git mv in Step 1
git status --short   # the asset should show as renamed (R)
git commit -m "Add project input actions and tactical camera

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: `PlayerCommandInput`

**Files:**
- Create: `Assets/_Project/Scripts/Controls/PlayerCommandInput.cs`
- Test: `Assets/_Project/Tests/PlayMode/PlayerCommandInputTests.cs`

**Interfaces:**
- Consumes:
  - `CommandableUnit.Issue/CurrentCommand` and `CommandResolver.Resolve` (Task 4);
  - `TacticalPause.Toggle/IsPaused` (Task 2);
  - `ClickDragDetector` (Task 1);
  - `TestWorld` (Task 5) and `TestControls` (Task 6).
- Produces `PlayerCommandInput : MonoBehaviour`:
  - `internal void Initialize(Camera viewCamera, CommandableUnit unit, TacticalPause pause, InputActionReference command, InputActionReference pointerPosition, InputActionReference togglePause)`.
  - Serialized field names, used by Task 8's builder: `viewCamera`, `controlledUnit`, `tacticalPause`, `commandAction`, `pointerPositionAction`, `togglePauseAction`.

- [ ] **Step 1: Write the failing tests**

`Assets/_Project/Tests/PlayMode/PlayerCommandInputTests.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class PlayerCommandInputTests : InputTestFixture
    {
        Keyboard keyboard;
        Mouse mouse;
        TestWorld world;
        Camera viewCamera;
        CommandableUnit unit;
        Health dummy;
        TacticalPause pause;

        public override void Setup()
        {
            base.Setup();
            keyboard = InputSystem.AddDevice<Keyboard>();
            mouse = InputSystem.AddDevice<Mouse>();
            world = new TestWorld();

            world.CreateEnvironment();
            unit = world.CreateUnit(new Vector3(-6f, 0f, -6f));
            dummy = world.CreateDummy(new Vector3(6f, 0f, 6f));

            var cameraObject = world.Track(new GameObject("Camera"));
            cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 25f, -20f), Quaternion.Euler(50f, 0f, 0f));
            viewCamera = cameraObject.AddComponent<Camera>();

            var systems = world.Track(new GameObject("Systems"));
            systems.SetActive(false);
            pause = systems.AddComponent<TacticalPause>();
            var actions = TestControls.Load();
            systems.AddComponent<PlayerCommandInput>().Initialize(viewCamera, unit, pause,
                TestControls.Ref(actions, "Commands/Command"),
                TestControls.Ref(actions, "Commands/PointerPosition"),
                TestControls.Ref(actions, "Commands/TogglePause"));
            systems.SetActive(true);
        }

        public override void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
            base.TearDown();
        }

        IEnumerator RightClickAt(Vector2 screenPoint)
        {
            Set(mouse.position, screenPoint);
            yield return null;
            Press(mouse.rightButton);
            yield return null;
            Release(mouse.rightButton);
            yield return null;
        }

        Vector2 ScreenPointOf(Vector3 world) => viewCamera.WorldToScreenPoint(world);

        [UnityTest]
        public IEnumerator RightClickOnDummy_IssuesAttackOnIt()
        {
            yield return null;
            yield return RightClickAt(ScreenPointOf(dummy.transform.position));

            Assert.That(unit.CurrentCommand, Is.TypeOf<AttackCommand>());
            Assert.That(((AttackCommand)unit.CurrentCommand).Target, Is.SameAs(dummy));
        }

        [UnityTest]
        public IEnumerator RightClickOnGround_IssuesMoveToClickedPoint()
        {
            yield return null;
            var groundPoint = new Vector3(4f, 0f, -4f);
            yield return RightClickAt(ScreenPointOf(groundPoint));

            Assert.That(unit.CurrentCommand, Is.TypeOf<MoveCommand>());
            var destination = ((MoveCommand)unit.CurrentCommand).Destination;
            Assert.That(TestWorld.HorizontalDistance(destination, groundPoint), Is.LessThan(0.1f));
        }

        [UnityTest]
        public IEnumerator RightDrag_IssuesNoCommand()
        {
            yield return null;
            var start = ScreenPointOf(new Vector3(4f, 0f, -4f));
            Set(mouse.position, start);
            yield return null;
            Press(mouse.rightButton);
            yield return null;
            Set(mouse.position, start + new Vector2(40f, 0f));
            yield return null;
            Release(mouse.rightButton);
            yield return null;

            Assert.That(unit.CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator RightClickOnSky_IssuesNothing()
        {
            yield return null;
            var skyPoint = ScreenPointOf(new Vector3(0f, 500f, 2000f));
            yield return RightClickAt(skyPoint);

            Assert.That(unit.CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator Space_TogglesTacticalPause()
        {
            yield return null;
            Press(keyboard.spaceKey);
            yield return null;
            Release(keyboard.spaceKey);
            yield return null;
            Assert.That(pause.IsPaused, Is.True);

            Press(keyboard.spaceKey);
            yield return null;
            Release(keyboard.spaceKey);
            yield return null;
            Assert.That(pause.IsPaused, Is.False);
        }

        [UnityTest]
        public IEnumerator RightClickWhilePaused_IssuesCommandButUnitWaits()
        {
            yield return null;
            pause.Pause();
            var start = unit.transform.position;

            yield return RightClickAt(ScreenPointOf(new Vector3(4f, 0f, -4f)));
            Assert.That(unit.CurrentCommand, Is.TypeOf<MoveCommand>());

            yield return new WaitForSecondsRealtime(0.5f);
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, start), Is.LessThan(0.01f));
        }
    }
}
#endif
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `Tools/run-tests.sh PlayMode`
Expected: `error CS0246: ... 'PlayerCommandInput' could not be found`, `EXIT=3`.

- [ ] **Step 3: Write the implementation**

`Assets/_Project/Scripts/Controls/PlayerCommandInput.cs`:

```csharp
using UnityEngine;
using UnityEngine.InputSystem;

namespace Blackglass
{
    /// <summary>
    /// Translates the player's input into requests: a quick right-click becomes a unit command
    /// (attack the clicked target, or move to the clicked point) and Space toggles tactical pause.
    /// Contains no movement or combat rules; the unit decides how to carry out its orders.
    /// </summary>
    public sealed class PlayerCommandInput : MonoBehaviour
    {
        [SerializeField] Camera viewCamera;
        [SerializeField] CommandableUnit controlledUnit;
        [SerializeField] TacticalPause tacticalPause;

        [Header("Input")]
        [SerializeField] InputActionReference commandAction;
        [SerializeField] InputActionReference pointerPositionAction;
        [SerializeField] InputActionReference togglePauseAction;

        [Header("Tuning")]
        [SerializeField, Min(0f)] float dragThresholdPixels = 6f;
        [SerializeField, Min(1f)] float maxClickDistance = 500f;
        [SerializeField] LayerMask clickableLayers = ~0;

        ClickDragDetector clickDetector;

        internal void Initialize(Camera camera, CommandableUnit unit, TacticalPause pause,
            InputActionReference command, InputActionReference pointerPosition, InputActionReference togglePause)
        {
            viewCamera = camera;
            controlledUnit = unit;
            tacticalPause = pause;
            commandAction = command;
            pointerPositionAction = pointerPosition;
            togglePauseAction = togglePause;
        }

        void Awake() => clickDetector = new ClickDragDetector(dragThresholdPixels);

        void OnEnable()
        {
            SetEnabled(true);
            if (commandAction != null)
            {
                commandAction.action.started += OnCommandPressed;
                commandAction.action.canceled += OnCommandReleased;
            }
            if (togglePauseAction != null)
                togglePauseAction.action.performed += OnTogglePause;
        }

        void OnDisable()
        {
            if (commandAction != null)
            {
                commandAction.action.started -= OnCommandPressed;
                commandAction.action.canceled -= OnCommandReleased;
            }
            if (togglePauseAction != null)
                togglePauseAction.action.performed -= OnTogglePause;
            SetEnabled(false);
        }

        void Update()
        {
            if (clickDetector.IsPressed)
                clickDetector.Track(PointerPosition);
        }

        Vector2 PointerPosition =>
            pointerPositionAction != null && pointerPositionAction.action != null
                ? pointerPositionAction.action.ReadValue<Vector2>()
                : Vector2.zero;

        void OnCommandPressed(InputAction.CallbackContext context) => clickDetector.Press(PointerPosition);

        void OnCommandReleased(InputAction.CallbackContext context)
        {
            var pointer = PointerPosition;
            if (clickDetector.Release(pointer))
                IssueCommandAt(pointer);
        }

        void OnTogglePause(InputAction.CallbackContext context)
        {
            if (tacticalPause != null)
                tacticalPause.Toggle();
        }

        void IssueCommandAt(Vector2 screenPoint)
        {
            if (viewCamera == null || controlledUnit == null)
                return;

            // While paused no physics steps run, so push moved transforms to physics before raycasting.
            Physics.SyncTransforms();
            var ray = viewCamera.ScreenPointToRay(screenPoint);
            if (!Physics.Raycast(ray, out var hit, maxClickDistance, clickableLayers, QueryTriggerInteraction.Ignore))
                return;

            var clickedHealth = hit.collider.GetComponentInParent<Health>();
            controlledUnit.Issue(CommandResolver.Resolve(clickedHealth, hit.point));
        }

        void SetEnabled(bool enable)
        {
            foreach (var reference in new[] { commandAction, pointerPositionAction, togglePauseAction })
            {
                if (reference == null || reference.action == null)
                    continue;
                if (enable)
                    reference.action.Enable();
                else
                    reference.action.Disable();
            }
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `Tools/run-tests.sh PlayMode` → expected `total="23" passed="23"`, `EXIT=0`.
Run: `Tools/run-tests.sh EditMode` → expected `total="35" passed="35"`, `EXIT=0`.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project
git status --short
git commit -m "Add player command input

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 8: Debug HUD, materials, and the Prototype scene

**Files:**
- Create: `Assets/_Project/Scripts/DebugUI/PrototypeHud.cs`
- Create (temporary, never committed): `Assets/_Project/Editor/PrototypeSceneBuilder.cs`
- Generated by the builder:
  - `Assets/_Project/Scenes/Prototype.unity`
  - `Assets/_Project/Scenes/Prototype/NavMesh-Environment.asset`
  - `Assets/_Project/Materials/{Ground,Obstacle,Unit,Dummy}.mat`
- Modify (builder): `ProjectSettings/EditorBuildSettings.asset`
- Test: `Assets/_Project/Tests/PlayMode/PrototypeSceneTests.cs`

**Interfaces:**
- Consumes: every runtime component from Tasks 2–7, plus the serialized field names listed in Tasks 6 and 7.
- Produces:
  - `PrototypeHud : MonoBehaviour`, with serialized fields `tacticalPause` (TacticalPause) and `target` (Health).
  - Scene objects named `Environment`, `PlayerUnit`, `TrainingDummy`, `CameraRig`, `Systems`.

- [ ] **Step 1: Write the HUD**

`Assets/_Project/Scripts/DebugUI/PrototypeHud.cs`:

```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>Debug-only on-screen text (IMGUI). Not production UI. Works while paused.</summary>
    public sealed class PrototypeHud : MonoBehaviour
    {
        const string ControlHints =
            "WASD: pan   Q/E or right-drag: rotate   Wheel: zoom\n" +
            "Right-click ground: move   Right-click dummy: attack\n" +
            "Space: tactical pause";

        [SerializeField] TacticalPause tacticalPause;
        [SerializeField] Health target;
        [SerializeField] string targetLabel = "Training Dummy";

        GUIStyle pausedStyle;

        void OnGUI()
        {
            GUI.Label(new Rect(10f, 10f, 520f, 60f), ControlHints);

            if (target != null)
            {
                var status = target.IsAlive ? $"{target.Current} / {target.Max}" : "destroyed";
                GUI.Label(new Rect(10f, 75f, 320f, 22f), $"{targetLabel}: {status}");
            }

            if (tacticalPause != null && tacticalPause.IsPaused)
            {
                pausedStyle ??= new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.UpperCenter,
                    fontSize = 22,
                    fontStyle = FontStyle.Bold,
                };
                GUI.Label(new Rect(0f, 20f, Screen.width, 40f), "TACTICAL PAUSE - Space to resume", pausedStyle);
            }
        }
    }
}
```

- [ ] **Step 2: Write the failing scene tests**

`Assets/_Project/Tests/PlayMode/PrototypeSceneTests.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class PrototypeSceneTests
    {
        CommandableUnit unit;
        Health dummy;
        TacticalPause pause;

        [UnitySetUp]
        public IEnumerator LoadScene()
        {
            yield return SceneManager.LoadSceneAsync("Prototype", LoadSceneMode.Single);
            yield return null;
            unit = Object.FindFirstObjectByType<CommandableUnit>();
            dummy = Object.FindFirstObjectByType<Health>();
            pause = Object.FindFirstObjectByType<TacticalPause>();
        }

        [TearDown]
        public void TearDown() => Time.timeScale = 1f;

        [UnityTest]
        public IEnumerator Scene_ContainsWiredPrototypeObjects_AndRunsWithoutErrors()
        {
            Assert.That(unit, Is.Not.Null, "PlayerUnit missing");
            Assert.That(unit.name, Is.EqualTo("PlayerUnit"));
            Assert.That(dummy, Is.Not.Null, "TrainingDummy missing");
            Assert.That(dummy.name, Is.EqualTo("TrainingDummy"));
            Assert.That(pause, Is.Not.Null, "TacticalPause missing");
            Assert.That(Object.FindFirstObjectByType<PlayerCommandInput>(), Is.Not.Null);
            Assert.That(Object.FindFirstObjectByType<TacticalCameraController>(), Is.Not.Null);
            Assert.That(Object.FindFirstObjectByType<PrototypeHud>(), Is.Not.Null);
            Assert.That(Camera.main, Is.Not.Null);

            // Any error or exception logged during this second fails the test automatically.
            yield return new WaitForSeconds(1f);
        }

        [UnityTest]
        public IEnumerator Move_AroundTheCentralWall_Arrives()
        {
            var destination = new Vector3(6f, 0f, 6f);
            Assert.That(unit.Issue(new MoveCommand(destination)), Is.True);

            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 15f);

            Assert.That(unit.CurrentCommand, Is.Null, "Move did not finish in time");
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, destination), Is.LessThan(0.3f));
        }

        [UnityTest]
        public IEnumerator Attack_DestroysTrainingDummy()
        {
            Assert.That(unit.Issue(new AttackCommand(dummy)), Is.True);

            yield return TestWorld.WaitUntil(() => !dummy.IsAlive, 20f);

            Assert.That(dummy.IsAlive, Is.False);
            Assert.That(dummy.gameObject.activeSelf, Is.False);
        }

        [UnityTest]
        public IEnumerator Pause_HoldsTheUnitUntilResume()
        {
            var start = unit.transform.position;
            pause.Pause();
            unit.Issue(new MoveCommand(new Vector3(-10f, 0f, 0f)));

            yield return new WaitForSecondsRealtime(1f);
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, start), Is.LessThan(0.01f));

            pause.Resume();
            yield return TestWorld.WaitUntil(() => TestWorld.HorizontalDistance(unit.transform.position, start) > 1f, 5f);
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, start), Is.GreaterThan(1f));
        }
    }

    public class PrototypeSceneInputTests : InputTestFixture
    {
        [UnityTest]
        public IEnumerator RightClickOnDummy_InScene_IssuesAttack()
        {
            var mouse = InputSystem.AddDevice<Mouse>();
            InputSystem.AddDevice<Keyboard>();
            yield return SceneManager.LoadSceneAsync("Prototype", LoadSceneMode.Single);
            yield return null;
            var unit = Object.FindFirstObjectByType<CommandableUnit>();
            var dummy = Object.FindFirstObjectByType<Health>();

            Set(mouse.position, (Vector2)Camera.main.WorldToScreenPoint(dummy.transform.position));
            yield return null;
            Press(mouse.rightButton);
            yield return null;
            Release(mouse.rightButton);
            yield return null;

            Assert.That(unit.CurrentCommand, Is.TypeOf<AttackCommand>());
        }
    }
}
#endif
```

- [ ] **Step 3: Run the scene tests to verify they fail**

Run: `Tools/run-tests.sh PlayMode Blackglass.Tests.PrototypeScene`
Expected: the tests fail with `Scene 'Prototype' couldn't be loaded because it has not been added to the build settings`, and `EXIT=2`.

- [ ] **Step 4: Create the temporary scene builder**

`Assets/_Project/Editor/PrototypeSceneBuilder.cs`:

```csharp
// TEMPORARY: builds Prototype.unity, its materials and NavMesh once, then this file is deleted (never committed).
using System;
using System.Linq;
using Blackglass;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;
using Object = UnityEngine.Object;

public static class PrototypeSceneBuilder
{
    const string ScenePath = "Assets/_Project/Scenes/Prototype.unity";
    const string NavMeshPath = "Assets/_Project/Scenes/Prototype/NavMesh-Environment.asset";
    const string ControlsPath = "Assets/_Project/Input/BlackglassControls.inputactions";

    public static void Build()
    {
        EnsureFolder("Assets/_Project", "Materials");
        EnsureFolder("Assets/_Project/Scenes", "Prototype");
        var groundMaterial = CreateMaterial("Ground", new Color(0.55f, 0.57f, 0.52f));
        var obstacleMaterial = CreateMaterial("Obstacle", new Color(0.36f, 0.36f, 0.4f));
        var unitMaterial = CreateMaterial("Unit", new Color(0.2f, 0.45f, 0.95f));
        var dummyMaterial = CreateMaterial("Dummy", new Color(0.85f, 0.2f, 0.2f));

        var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        Object.DestroyImmediate(Camera.main.gameObject); // replaced by the camera rig below

        // Environment: ground + obstacles, baked into the NavMesh.
        var environment = new GameObject("Environment");
        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.transform.SetParent(environment.transform, false);
        ground.transform.localScale = new Vector3(4f, 1f, 4f);
        ground.GetComponent<Renderer>().sharedMaterial = groundMaterial;

        CreateObstacle(environment, "Obstacle_CentralWall", new Vector3(0f, 1f, 0f), new Vector3(8f, 2f, 1f), 45f, obstacleMaterial);
        CreateObstacle(environment, "Obstacle_A", new Vector3(-12f, 0.75f, 6f), new Vector3(3f, 1.5f, 3f), 0f, obstacleMaterial);
        CreateObstacle(environment, "Obstacle_B", new Vector3(8f, 1f, -9f), new Vector3(2f, 2f, 4f), 0f, obstacleMaterial);
        CreateObstacle(environment, "Obstacle_C", new Vector3(14f, 0.5f, -2f), new Vector3(4f, 1f, 2f), 0f, obstacleMaterial);
        CreateObstacle(environment, "Obstacle_D", new Vector3(-4f, 1.5f, 14f), new Vector3(2f, 3f, 2f), 0f, obstacleMaterial);

        var surface = environment.AddComponent<NavMeshSurface>();
        surface.collectObjects = CollectObjects.Children;
        surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
        surface.BuildNavMesh();
        AssetDatabase.DeleteAsset(NavMeshPath);
        AssetDatabase.CreateAsset(surface.navMeshData, NavMeshPath);

        // Player unit.
        var unit = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        unit.name = "PlayerUnit";
        unit.transform.position = new Vector3(-10f, 1f, -10f);
        unit.GetComponent<Renderer>().sharedMaterial = unitMaterial;
        unit.AddComponent<UnitMover>();
        unit.GetComponent<NavMeshAgent>().baseOffset = 1f;
        unit.AddComponent<UnitAttacker>();
        var commandableUnit = unit.AddComponent<CommandableUnit>();

        // Training dummy.
        var dummy = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        dummy.name = "TrainingDummy";
        dummy.transform.position = new Vector3(10f, 1f, 10f);
        dummy.GetComponent<Renderer>().sharedMaterial = dummyMaterial;
        var dummyHealth = dummy.AddComponent<Health>();
        dummy.AddComponent<HitFlash>();

        // Camera rig.
        var rig = new GameObject("CameraRig");
        rig.transform.position = new Vector3(0f, 0f, -2f);
        var cameraObject = new GameObject("Main Camera") { tag = "MainCamera" };
        cameraObject.transform.SetParent(rig.transform, false);
        var viewCamera = cameraObject.AddComponent<Camera>();
        cameraObject.AddComponent<AudioListener>();
        var cameraController = rig.AddComponent<TacticalCameraController>();
        SetReference(cameraController, "viewCamera", viewCamera);
        SetReference(cameraController, "panAction", ActionReference("Camera/Pan"));
        SetReference(cameraController, "rotateAction", ActionReference("Camera/Rotate"));
        SetReference(cameraController, "rotateDragAction", ActionReference("Camera/RotateDrag"));
        SetReference(cameraController, "pointerPositionAction", ActionReference("Camera/PointerPosition"));
        SetReference(cameraController, "zoomAction", ActionReference("Camera/Zoom"));
        SetFloat(cameraController, "distance", 28f);
        // Place the camera now so the saved scene matches what Play mode shows.
        cameraObject.transform.SetLocalPositionAndRotation(Quaternion.Euler(55f, 0f, 0f) * new Vector3(0f, 0f, -28f), Quaternion.Euler(55f, 0f, 0f));

        // Systems.
        var systems = new GameObject("Systems");
        var pause = systems.AddComponent<TacticalPause>();
        var input = systems.AddComponent<PlayerCommandInput>();
        SetReference(input, "viewCamera", viewCamera);
        SetReference(input, "controlledUnit", commandableUnit);
        SetReference(input, "tacticalPause", pause);
        SetReference(input, "commandAction", ActionReference("Commands/Command"));
        SetReference(input, "pointerPositionAction", ActionReference("Commands/PointerPosition"));
        SetReference(input, "togglePauseAction", ActionReference("Commands/TogglePause"));
        var hud = systems.AddComponent<PrototypeHud>();
        SetReference(hud, "tacticalPause", pause);
        SetReference(hud, "target", dummyHealth);

        if (!EditorSceneManager.SaveScene(scene, ScenePath))
            throw new Exception("Failed to save " + ScenePath);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        AssetDatabase.SaveAssets();
        Debug.Log("[PrototypeSceneBuilder] Built " + ScenePath);
    }

    static void CreateObstacle(GameObject parent, string name, Vector3 position, Vector3 scale, float yaw, Material material)
    {
        var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        box.name = name;
        box.transform.SetParent(parent.transform, false);
        box.transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yaw, 0f));
        box.transform.localScale = scale;
        box.GetComponent<Renderer>().sharedMaterial = material;
    }

    static Material CreateMaterial(string name, Color color)
    {
        var path = $"Assets/_Project/Materials/{name}.mat";
        AssetDatabase.DeleteAsset(path);
        var shader = Shader.Find("Universal Render Pipeline/Lit") ?? throw new Exception("URP Lit shader not found");
        var material = new Material(shader);
        material.SetColor("_BaseColor", color);
        AssetDatabase.CreateAsset(material, path);
        return material;
    }

    static InputActionReference ActionReference(string actionPath)
    {
        var reference = AssetDatabase.LoadAllAssetsAtPath(ControlsPath)
            .OfType<InputActionReference>()
            .FirstOrDefault(r => r.action != null && $"{r.action.actionMap.name}/{r.action.name}" == actionPath);
        return reference != null ? reference : throw new Exception($"No InputActionReference for {actionPath} in {ControlsPath}");
    }

    static void SetReference(Object target, string field, Object value)
    {
        var serialized = new SerializedObject(target);
        var property = serialized.FindProperty(field) ?? throw new Exception($"{target.GetType().Name}.{field} not found");
        property.objectReferenceValue = value;
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }

    static void SetFloat(Object target, string field, float value)
    {
        var serialized = new SerializedObject(target);
        var property = serialized.FindProperty(field) ?? throw new Exception($"{target.GetType().Name}.{field} not found");
        property.floatValue = value;
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }

    static void EnsureFolder(string parent, string child)
    {
        if (!AssetDatabase.IsValidFolder($"{parent}/{child}"))
            AssetDatabase.CreateFolder(parent, child);
    }
}
```

- [ ] **Step 5: Run the builder**

```bash
"/c/Program Files/Unity/Hub/Editor/6000.3.25f1/Editor/Unity.exe" -batchmode -quit -projectPath "$(pwd -W)" -executeMethod PrototypeSceneBuilder.Build -logFile "$(pwd -W)/Logs/BuildPrototypeScene.log"; echo "EXIT=$?"
grep -E '\[PrototypeSceneBuilder\]|error CS|Exception' Logs/BuildPrototypeScene.log | head -20
grep -n 'path:' ProjectSettings/EditorBuildSettings.asset
```

Expected:
- `EXIT=0`;
- the log contains `[PrototypeSceneBuilder] Built Assets/_Project/Scenes/Prototype.unity` and no exceptions;
- the build settings list only `Assets/_Project/Scenes/Prototype.unity`.

- [ ] **Step 6: Delete the builder**

```bash
rm -r Assets/_Project/Editor Assets/_Project/Editor.meta
```

- [ ] **Step 7: Run all tests to verify they pass**

Run: `Tools/run-tests.sh PlayMode` → expected `total="28" passed="28"`, `EXIT=0`.
Run: `Tools/run-tests.sh EditMode` → expected `total="35" passed="35"`, `EXIT=0`.

The run also recompiles without the deleted builder, which confirms nothing depends on it.

- [ ] **Step 8: Commit**

```bash
git add Assets/_Project ProjectSettings/EditorBuildSettings.asset
git status --short   # must NOT contain Assets/_Project/Editor
git commit -m "Add prototype scene, placeholder materials and debug HUD

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 9: Decision records and final verification

**Files:**
- Modify: `Docs/Decisions.md`

**Interfaces:**
- Consumes: everything above.
- Produces: decision records 005 (updated), 006, 007, 008 and 009.

- [ ] **Step 1: Update decision 005**

In `Docs/Decisions.md` section 005:
- In the **Decided** line, remove `AI Navigation` from the list of removed template extras.
- Replace the **Implications** line with:

```markdown
- **Implications:** `com.unity.ai.navigation` 2.0.14 was re-added on 2026-10-04 for prototype unit movement (NavMeshAgent + a baked NavMeshSurface). `Packages/manifest.json` also lists `com.unity.inputsystem` under `testables`, which is test-only and exposes `InputTestFixture`. Test runs must therefore filter to our assemblies (`Tools/run-tests.sh` does this).
```

- [ ] **Step 2: Append decisions 006–009**

Append to the end of `Docs/Decisions.md`:

```markdown

## 006 — Unit commands are data; units execute them

- **Decided:** Gameplay orders are small immutable objects (`MoveCommand`, `AttackCommand`, base `UnitCommand`) passed to `CommandableUnit.Issue`. The unit and its components (`UnitMover`, `UnitAttacker`) decide how to carry them out. A new command replaces the current one. `Issue` returns false and keeps the current order when a command can't be carried out.
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
```

- [ ] **Step 3: Full verification run**

```bash
Tools/run-tests.sh EditMode     # expected: total="35" passed="35", EXIT=0
Tools/run-tests.sh PlayMode     # expected: total="28" passed="28", EXIT=0
grep -c "error CS" Logs/TestRun-EditMode.log Logs/TestRun-PlayMode.log   # expected: 0 and 0
grep -nE "Exception|Error" Logs/TestRun-PlayMode.log | grep -viE "licens|ErrorReporting|ErrorAnalytics|d3d12|Expected|LogAssert" | head -20
grep -rn "timeScale" Assets/_Project/Scripts   # expected: only GameTime/TacticalPause.cs
git status --short   # expected: only Docs/Decisions.md
```

Investigate anything unexpected from the `Exception|Error` grep before continuing.

- [ ] **Step 4: Commit**

```bash
git add Docs/Decisions.md
git commit -m "Record prototype control loop decisions

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git log --oneline main..HEAD
```

- [ ] **Step 5: Manual checks for the owner (report them; don't perform them)**

Open `Assets/_Project/Scenes/Prototype.unity` in the Editor, press Play, and check:

1. **Camera:** WASD pans, Q/E rotate, right-drag rotates, and the wheel zooms. All of it feels controllable and stays within the play area.
2. **Move:** right-click the ground. The blue capsule walks there, going around the grey walls.
3. **Attack:** right-click the red dummy. The unit walks up, and the dummy flashes white on each hit. The HUD shows HP dropping 100 → 75 → 50 → 25, then "destroyed", and the dummy disappears.
4. **Pause:** press Space. "TACTICAL PAUSE" appears, and the unit freezes mid-move. The camera still works. A right-click issues a new order. Press Space again, and the unit carries out the latest order.
5. The Console shows no errors or exceptions.
```
