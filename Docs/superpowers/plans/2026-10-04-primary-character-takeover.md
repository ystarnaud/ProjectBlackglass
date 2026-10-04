# Primary Character and Takeover (Phase 3) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the player a primary character. Real-time clicks order it, and in takeover mode (V) WASD drives it directly with the camera following. Tactical pause, selection and queued orders keep working for the whole squad, primary character included.

**Architecture:**
- `CommandableUnit` gains a held **move intent**. A non-zero intent clears the unit's orders and steers it through a new `UnitMover.Steer`, so `CommandableUnit` stays the only thing that drives `UnitMover`.
- `PrimaryCharacter` (state only) says which unit is the primary character and whether takeover is on.
- `DirectControlInput` turns V and WASD into requests. `PlayerCommandInput` routes real-time clicks to the primary character. `TacticalCameraController` follows it while driving.

**Tech Stack:** Unity 6000.3.25f1 (Unity 6.3 LTS), URP, Input System 1.20.0, AI Navigation 2.0.14, Unity Test Framework (NUnit), `InputTestFixture`.

**Spec:** `Docs/superpowers/specs/2026-10-04-primary-character-takeover-design.md`

## Global Constraints

- **Unity Editor:** exactly 6000.3.25f1 at `C:\Program Files\Unity\Hub\Editor\6000.3.25f1\Editor\Unity.exe`.
  - **The Editor must be closed during every batch-mode run** (only one instance can open the project).
  - If a run prints "The Unity Editor has this project open", stop and ask the owner to close it.
- **Code location:**
  - Runtime code goes in `Assets/_Project/Scripts/` (assembly `Blackglass`, namespace `Blackglass`).
  - Tests go in `Assets/_Project/Tests/EditMode/` and `Assets/_Project/Tests/PlayMode/` (namespace `Blackglass.Tests`).
  - No new assemblies, assembly references or packages.
- **Commands are data** (decision 006). Orders go through `CommandableUnit.Issue` or `GroupOrders.Issue` only. Direct control is a held move intent (`CommandableUnit.SetMoveIntent`), never a stream of commands.
- **Only `CommandableUnit` drives `UnitMover`.** Input components never touch transforms, NavMeshAgents, `Health` or movement and combat components.
- **Pause ownership:** only `TacticalPause` writes `Time.timeScale` in game code. Tests may reset it to 1 in `TearDown`.
- **Time:** unit orders, steering and combat use scaled time. Input, camera, HUD and debug views use unscaled time or input callbacks.
- **Wiring:**
  - Use serialized Inspector references. No singletons, no `Find*` or `Camera.main` in game code, no static mutable state.
  - Tests wire components through `internal Initialize(...)`, called while the host GameObject is inactive.
- **Input:**
  - Input System only, through `InputActionReference` fields.
  - New `Character` map: `Move` (WASD 2D vector), `Takeover` (V). `Camera/Pan` keeps WASD.
- **Visuals:** debug only. IMGUI `OnGUI` for text. Primitives with placeholder URP Unlit materials. Debug objects have **no colliders**.
- **Scope:** no weapons, abilities, stats, companion AI, formations, first-person camera, camera collision, production UI or packages.
- **Git:**
  - Work on local branch `prototype/primary-takeover`, which already exists with the spec committed. Never push or merge.
  - Commit after each task. Messages are sentence case and imperative, and end with the line `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
  - Commit Unity's `.meta` files with their assets.
- **Test runs:** always use `Tools/run-tests.sh`, which restricts runs to our assemblies. Use a 10-minute timeout for PlayMode runs.

### Running tests

```bash
Tools/run-tests.sh EditMode                                             # all EditMode tests
Tools/run-tests.sh PlayMode                                             # all PlayMode tests
Tools/run-tests.sh PlayMode Blackglass.Tests.UnitMoverSteerPlayModeTests  # one fixture (any -testFilter expression)
```

**Output:** the script prints any `error CS` lines, a summary (`result= total= passed= failed=`), the names and messages of failed tests, and `EXIT=<code>`.

**Exit codes:**
- **0:** all tests passed.
- **2:** some tests failed.
- **3:** the tests could not run, usually because of a compile error. No results file is written, and the `error CS` lines say why.

A "red" step usually means **EXIT=3 with `error CS0246`/`CS1061`/`CS1501`**, because the new types or members don't exist yet.

**Baseline before this plan:** EditMode 94/94, PlayMode 66/66.

**Expected totals after each task:**

| After task | EditMode | PlayMode |
|---|---|---|
| 1 | 94 | 70 |
| 2 | 97 | 77 |
| 3 | 108 | 77 |
| 4 | 115 | 90 |
| 5 | 115 | 99 |
| 6 | 115 | 104 |
| 7 | 123 | 104 |
| 8 | 123 | 107 |

## Deviations from the spec (decided while planning; the spec is already updated to match)

1. `PlayerCommandInput` reads "paused" from its own `TacticalPause` reference, which is the same component the primary character uses in the scene. It does not read `PrimaryCharacter.IsPaused`. This keeps fixtures that have no primary character working.
2. The HUD hint text is shortened so it fits. The content is the same.
3. `PlayerCommandInputTests` and `TacticalCameraControllerTests` always wire a `PrimaryCharacter` whose unit is **null** (`HasUnit` is false, so Phase 2 behaviour applies). The new tests give it a unit. This avoids duplicating each fixture and also exercises the fallback path.

## Review Focus

Five inputs the spec implies but that are easy to break. Each one has a test in its owning task.

1. **The whole squad, primary character included, gets an order in pause, then you take over after resume.** Only the primary character may drop its orders; the companions keep moving. Pinned in Task 8 by `PausedSquadOrder_ThenTakeover_OnlyThePrimaryDropsItsOrders`.
2. **Pressing V while already holding W to pan the camera.** The character must not start running at once. Pinned in Task 4 by `V_WhileWIsHeld_DoesNotSteerUntilWIsPressedAgain`.
3. **A rotated camera.** W must move the character toward the top of the screen with the real component wiring, not just in the pure helper. Pinned in Task 4 by `W_IsRelativeToTheCameraYaw`.
4. **Clicking the primary character itself in real time.** This must select it, not order it to move onto itself. Pinned in Task 5 by `RealTimeClickOnThePrimary_SelectsIt`.
5. **The primary character is deactivated while being driven.** Driving must stop, with no exceptions and no stuck intent. Pinned in Task 4 by `PrimaryUnitDeactivated_WhileDriving_StopsDrivingWithoutErrors`.

---

## File Structure

```
Assets/_Project/
├── Scripts/
│   ├── Units/UnitMover.cs                    + Steer(direction) (Task 1)
│   ├── Units/CommandableUnit.cs              + SetMoveIntent / MoveIntent; intent beats orders (Task 2)
│   ├── Controls/PrimaryCharacter.cs          NEW: primary unit + takeover state (Task 3)
│   ├── Controls/DirectControlInput.cs        NEW: V toggles takeover; WASD -> move intent (Task 4)
│   ├── Controls/PlayerCommandInput.cs        real-time clicks order the primary character (Task 5)
│   ├── CameraControl/TacticalCameraController.cs   follow while driving (Task 6)
│   └── DebugUI/PrototypeHud.cs               hints, primary status line, layout (Task 7)
├── Input/BlackglassControls.inputactions     + Character map: Move, Takeover (Task 4)
├── Materials/PrimaryMarker.mat               NEW, generated (Task 8)
├── Scenes/Prototype.unity                    PrimaryCharacter, DirectControlInput, marker, wiring (Task 8)
├── Tests/EditMode/
│   ├── CommandableUnitTests.cs               + move-intent tests (Task 2)
│   ├── PrimaryCharacterTests.cs              NEW (Task 3)
│   ├── DirectControlDirectionTests.cs        NEW (Task 4)
│   └── PrototypeHudTests.cs                  + DescribePrimary (Task 7)
└── Tests/PlayMode/
    ├── UnitMoverSteerPlayModeTests.cs        NEW (Task 1)
    ├── CommandableUnitMoveIntentPlayModeTests.cs   NEW (Task 2)
    ├── DirectControlInputTests.cs            NEW (Task 4)
    ├── PlayerCommandInputTests.cs            + primary-character tests (Task 5)
    ├── TacticalCameraControllerTests.cs      + follow tests (Task 6)
    └── PrototypeSceneTests.cs                3 tests pause first; 3 new tests; wiring asserts (Task 8)
Docs/Decisions.md                             006 and 008 updated, 011 added (Task 9)
```

`Assets/_Project/Editor/TakeoverSceneBuilder.cs` is **temporary**: it is created and deleted within Task 8 and never committed.

---

### Task 1: `UnitMover.Steer`

**Files:**
- Modify: `Assets/_Project/Scripts/Units/UnitMover.cs` (add a method after `MoveTo`)
- Test: `Assets/_Project/Tests/PlayMode/UnitMoverSteerPlayModeTests.cs` (new)

**Interfaces:**
- Consumes: `UnitMover.Stop()`, `UnitMover.MoveTo(Vector3)`, `TestWorld` (`CreateEnvironment`, `CreateUnit`, `Track`, `HorizontalDistance`).
- Produces: `public void UnitMover.Steer(Vector3 direction)`.
  - It moves the unit this frame by `direction × agent speed × Time.deltaTime` (direction flattened and clamped to length 1) and turns it toward the direction at the agent's angular speed.
  - It drops any path and leftover velocity first.
  - It does nothing when `Time.deltaTime <= 0`, when the direction is zero, or when the agent is off the NavMesh.

- [ ] **Step 1: Write the failing tests**

Create `Assets/_Project/Tests/PlayMode/UnitMoverSteerPlayModeTests.cs`:

```csharp
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class UnitMoverSteerPlayModeTests
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
        public IEnumerator Steer_MovesAtUnitSpeed_AndTurnsToFaceTheDirection()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(-6f, 0f, 0f));
            var mover = unit.GetComponent<UnitMover>();
            var speed = unit.GetComponent<NavMeshAgent>().speed;
            yield return null;
            var start = unit.transform.position;

            var steeredTime = 0f;
            while (steeredTime < 0.5f)
            {
                mover.Steer(Vector3.right);
                steeredTime += Time.deltaTime;
                yield return null;
            }
            yield return null;

            var travelled = unit.transform.position - start;
            var expected = speed * steeredTime;
            Assert.That(travelled.x, Is.EqualTo(expected).Within(expected * 0.15f));
            Assert.That(Mathf.Abs(travelled.z), Is.LessThan(0.05f));
            Assert.That(Vector3.Dot(unit.transform.forward, Vector3.right), Is.GreaterThan(0.99f), "Did not turn to face the direction");
        }

        [UnityTest]
        public IEnumerator Steer_IntoAWall_StopsAtTheWallAndStaysOnTheNavMesh()
        {
            // A wall from x = 2.5 to 3.5 across the unit's path.
            world.CreateEnvironment((new Vector3(3f, 1f, 0f), new Vector3(1f, 2f, 10f)));
            var unit = world.CreateUnit(new Vector3(0f, 0f, 0f));
            var mover = unit.GetComponent<UnitMover>();
            var agent = unit.GetComponent<NavMeshAgent>();
            yield return null;

            var steeredTime = 0f;
            while (steeredTime < 1.5f)
            {
                mover.Steer(Vector3.right);
                steeredTime += Time.deltaTime;
                yield return null;
            }

            Assert.That(unit.transform.position.x, Is.GreaterThan(1f), "Did not move toward the wall");
            Assert.That(unit.transform.position.x, Is.LessThan(2.5f), "Walked into the wall");
            Assert.That(agent.isOnNavMesh, Is.True);
        }

        [UnityTest]
        public IEnumerator Steer_WhilePaused_DoesNothing()
        {
            world.CreateEnvironment();
            var pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            var unit = world.CreateUnit(new Vector3(0f, 0f, 0f));
            var mover = unit.GetComponent<UnitMover>();
            yield return null;
            var start = unit.transform.position;
            var startRotation = unit.transform.rotation;

            pause.Pause();
            for (var i = 0; i < 10; i++)
            {
                mover.Steer(Vector3.right);
                yield return null;
            }

            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, start), Is.LessThan(0.001f));
            Assert.That(Quaternion.Angle(unit.transform.rotation, startRotation), Is.LessThan(0.01f));
        }

        [UnityTest]
        public IEnumerator Steer_DuringAMove_DropsThePathAndLeftoverVelocity()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(0f, 0f, -6f));
            var mover = unit.GetComponent<UnitMover>();
            var agent = unit.GetComponent<NavMeshAgent>();
            yield return null;
            Assert.That(mover.MoveTo(new Vector3(0f, 0f, 8f)), Is.True);
            yield return new WaitForSeconds(0.4f);
            Assert.That(agent.velocity.magnitude, Is.GreaterThan(1f), "Precondition: the unit should be walking");
            var start = unit.transform.position;

            var steeredTime = 0f;
            while (steeredTime < 0.4f)
            {
                mover.Steer(Vector3.right);
                steeredTime += Time.deltaTime;
                yield return null;
            }

            Assert.That(agent.hasPath, Is.False, "The old path is still active");
            Assert.That(agent.velocity.magnitude, Is.LessThan(0.01f), "Leftover velocity is still applied");
            var travelled = unit.transform.position - start;
            Assert.That(travelled.x, Is.GreaterThan(1f));
            Assert.That(Mathf.Abs(travelled.z), Is.LessThan(0.3f), "Kept drifting along the old path");
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `Tools/run-tests.sh PlayMode Blackglass.Tests.UnitMoverSteerPlayModeTests`
Expected: `EXIT=3` with `error CS1061: 'UnitMover' does not contain a definition for 'Steer'`.

- [ ] **Step 3: Implement `Steer`**

In `Assets/_Project/Scripts/Units/UnitMover.cs`, add after `MoveTo` (before `Stop`):

```csharp
        /// <summary>
        /// Moves the unit this frame in a horizontal direction at its normal speed, turning toward it. Call once per
        /// simulation frame while steering. Drops any path and leftover velocity first, so it never fights a path or
        /// coasts. Uses scaled time: does nothing while paused. A direction longer than 1 is clamped; zero does nothing.
        /// </summary>
        public void Steer(Vector3 direction)
        {
            var deltaTime = Time.deltaTime;
            if (deltaTime <= 0f || !Agent.isOnNavMesh)
                return;
            direction.y = 0f;
            direction = Vector3.ClampMagnitude(direction, 1f);
            if (direction == Vector3.zero)
                return;

            if (Agent.hasPath || Agent.pathPending || Agent.velocity != Vector3.zero)
                Stop();
            // Move keeps the unit on the NavMesh and slides it along walls.
            Agent.Move(direction * (Agent.speed * deltaTime));
            transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(direction),
                Agent.angularSpeed * deltaTime);
        }
```

Also update the class summary line to say that the class moves the unit along paths and by direct steering:

```csharp
    /// <summary>Moves the unit over the NavMesh, along paths or by direct steering. The only component that talks to the NavMeshAgent.</summary>
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `Tools/run-tests.sh PlayMode Blackglass.Tests.UnitMoverSteerPlayModeTests` → expected `total="4" passed="4"`, `EXIT=0`.

**If `Steer_MovesAtUnitSpeed_AndTurnsToFaceTheDirection` fails only on its facing assertion,** the agent is overwriting the transform's rotation. In that case, set `Agent.updateRotation = false` at the top of `Steer` (after the early returns), and set `Agent.updateRotation = true` at the start of `MoveTo`. Re-run the tests.

Then run the full suite: `Tools/run-tests.sh PlayMode` → expected `total="70" passed="70"`, `EXIT=0`.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/Units/UnitMover.cs Assets/_Project/Tests/PlayMode/UnitMoverSteerPlayModeTests.cs Assets/_Project/Tests/PlayMode/UnitMoverSteerPlayModeTests.cs.meta
git commit -m "Add direct steering to UnitMover

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

(Unity writes the `.meta` file for a new script during the test run. If `git status` shows it missing, run the PlayMode tests once more, then add it.)

---

### Task 2: Move intent on `CommandableUnit`

**Files:**
- Modify: `Assets/_Project/Scripts/Units/CommandableUnit.cs`
- Modify: `Assets/_Project/Tests/EditMode/CommandableUnitTests.cs` (append tests)
- Test: `Assets/_Project/Tests/PlayMode/CommandableUnitMoveIntentPlayModeTests.cs` (new)

**Interfaces:**
- Consumes: `UnitMover.Steer(Vector3)` (Task 1), `CommandableUnit.StopAll()` (existing, private).
- Produces:
  - `public Vector3 CommandableUnit.MoveIntent { get; }`, zero by default.
  - `public void CommandableUnit.SetMoveIntent(Vector3 direction)`: flattens (y = 0) and clamps the length to 1. The value is held until it is set again.
  - **Rule:** each frame with `Time.deltaTime > 0` and a non-zero intent, the unit clears all of its orders (as Stop does) and calls `UnitMover.Steer(intent)` instead of processing orders.

- [ ] **Step 1: Write the failing EditMode tests**

Append inside the class in `Assets/_Project/Tests/EditMode/CommandableUnitTests.cs`, after the last test:

```csharp
        [Test]
        public void StartsWithNoMoveIntent()
        {
            Assert.That(unit.MoveIntent, Is.EqualTo(Vector3.zero));
        }

        [Test]
        public void SetMoveIntent_FlattensAndClampsToLengthOne()
        {
            unit.SetMoveIntent(new Vector3(3f, 5f, 4f));
            Assert.That(unit.MoveIntent.x, Is.EqualTo(0.6f).Within(1e-5f));
            Assert.That(unit.MoveIntent.y, Is.EqualTo(0f));
            Assert.That(unit.MoveIntent.z, Is.EqualTo(0.8f).Within(1e-5f));
        }

        [Test]
        public void SetMoveIntent_KeepsShortDirections_AndZeroClearsIt()
        {
            unit.SetMoveIntent(new Vector3(0.3f, 0f, 0.4f));
            Assert.That(unit.MoveIntent.magnitude, Is.EqualTo(0.5f).Within(1e-5f));
            unit.SetMoveIntent(Vector3.zero);
            Assert.That(unit.MoveIntent, Is.EqualTo(Vector3.zero));
        }
```

- [ ] **Step 2: Write the failing PlayMode tests**

Create `Assets/_Project/Tests/PlayMode/CommandableUnitMoveIntentPlayModeTests.cs`:

```csharp
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CommandableUnitMoveIntentPlayModeTests
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
        public IEnumerator MoveIntent_ClearsTheCurrentMoveAndPendingOrders_ThenSteers()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(0f, 0f, -6f));
            var dummy = world.CreateDummy(new Vector3(-6f, 0f, 6f));
            yield return null;
            Assert.That(unit.Issue(new MoveCommand(new Vector3(0f, 0f, 8f))), Is.True);
            Assert.That(unit.Issue(new AttackCommand(dummy), IssueMode.Append), Is.True);
            var start = unit.transform.position;

            unit.SetMoveIntent(Vector3.right);
            yield return null;
            Assert.That(unit.CurrentCommand, Is.Null, "The current order was not cleared");
            Assert.That(unit.PendingCommands, Is.Empty, "Pending orders were not cleared");

            yield return new WaitForSeconds(0.4f);
            var travelled = unit.transform.position - start;
            Assert.That(travelled.x, Is.GreaterThan(1f), "The unit is not steering");
            Assert.That(Mathf.Abs(travelled.z), Is.LessThan(0.3f), "The unit kept following the cancelled move");
        }

        [UnityTest]
        public IEnumerator MoveIntent_ClearsAnAttackInProgress()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(0f, 0f, -6f));
            var dummy = world.CreateDummy(new Vector3(0f, 0f, 6f));
            yield return null;
            Assert.That(unit.Issue(new AttackCommand(dummy)), Is.True);
            yield return new WaitForSeconds(0.3f);
            var startingHealth = dummy.Current;

            unit.SetMoveIntent(Vector3.left);
            yield return new WaitForSeconds(0.5f);

            Assert.That(unit.CurrentCommand, Is.Null);
            Assert.That(unit.transform.position.x, Is.LessThan(-1f), "The unit is not steering");
            Assert.That(dummy.Current, Is.EqualTo(startingHealth), "The cancelled attack still hit");
        }

        [UnityTest]
        public IEnumerator ZeroMoveIntent_LeavesOrdersAlone()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(0f, 0f, -6f));
            yield return null;
            var move = new MoveCommand(new Vector3(0f, 0f, 8f));
            var next = new MoveCommand(new Vector3(6f, 0f, 8f));
            unit.Issue(move);
            unit.Issue(next, IssueMode.Append);

            unit.SetMoveIntent(Vector3.zero);
            yield return new WaitForSeconds(0.3f);

            Assert.That(unit.CurrentCommand, Is.SameAs(move));
            Assert.That(unit.PendingCommands, Is.EqualTo(new UnitCommand[] { next }));
            Assert.That(unit.transform.position.z, Is.GreaterThan(-5f), "The move stopped running");
        }

        [UnityTest]
        public IEnumerator MoveIntentOnOneUnit_LeavesAnotherUnitsOrdersAlone()
        {
            world.CreateEnvironment();
            var primary = world.CreateUnit(new Vector3(-6f, 0f, -6f));
            var companion = world.CreateUnit(new Vector3(6f, 0f, -6f));
            yield return null;
            var first = new MoveCommand(new Vector3(6f, 0f, 8f));
            var second = new MoveCommand(new Vector3(0f, 0f, 8f));
            companion.Issue(first);
            companion.Issue(second, IssueMode.Append);
            primary.Issue(new MoveCommand(new Vector3(-6f, 0f, 8f)));

            primary.SetMoveIntent(Vector3.left);
            yield return new WaitForSeconds(0.5f);

            Assert.That(primary.CurrentCommand, Is.Null);
            Assert.That(companion.CurrentCommand, Is.SameAs(first), "The companion's order was cancelled");
            Assert.That(companion.PendingCommands, Is.EqualTo(new UnitCommand[] { second }));
            Assert.That(companion.transform.position.z, Is.GreaterThan(-4f), "The companion stopped walking");
        }

        [UnityTest]
        public IEnumerator OrderIssuedWhileTheIntentIsHeld_IsClearedOnTheNextSimulationFrame()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(0f, 0f, -6f));
            yield return null;
            unit.SetMoveIntent(Vector3.right);
            yield return null;

            Assert.That(unit.Issue(new MoveCommand(new Vector3(0f, 0f, 8f))), Is.True, "Orders are still accepted");
            Assert.That(unit.CurrentCommand, Is.Not.Null);
            yield return null;

            Assert.That(unit.CurrentCommand, Is.Null, "Keys must win while held");
        }

        [UnityTest]
        public IEnumerator ReleasingTheIntent_LeavesTheUnitIdleWhereItIs()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(0f, 0f, -6f));
            yield return null;
            unit.Issue(new MoveCommand(new Vector3(0f, 0f, 8f)));
            unit.SetMoveIntent(Vector3.right);
            yield return new WaitForSeconds(0.3f);

            unit.SetMoveIntent(Vector3.zero);
            yield return null;
            var stoppedAt = unit.transform.position;
            yield return new WaitForSeconds(0.3f);

            Assert.That(unit.CurrentCommand, Is.Null, "The cancelled order came back");
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, stoppedAt), Is.LessThan(0.05f), "The unit kept moving");
        }

        [UnityTest]
        public IEnumerator MoveIntentWhilePaused_NeitherMovesTheUnitNorClearsItsOrders()
        {
            world.CreateEnvironment();
            var pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            var unit = world.CreateUnit(new Vector3(0f, 0f, -6f));
            yield return null;
            pause.Pause();
            var move = new MoveCommand(new Vector3(0f, 0f, 8f));
            unit.Issue(move);
            var start = unit.transform.position;

            unit.SetMoveIntent(Vector3.right);
            yield return new WaitForSecondsRealtime(0.3f);

            Assert.That(unit.CurrentCommand, Is.SameAs(move));
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, start), Is.LessThan(0.001f));
        }
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `Tools/run-tests.sh EditMode Blackglass.Tests.CommandableUnitTests`
Expected: `EXIT=3` with `error CS1061: 'CommandableUnit' does not contain a definition for 'MoveIntent'` (or for `SetMoveIntent`).

- [ ] **Step 4: Implement the move intent**

In `Assets/_Project/Scripts/Units/CommandableUnit.cs`:

(a) Replace the class summary with:

```csharp
    /// <summary>
    /// The single entry point for gameplay orders and direct control. Keeps the unit's orders in a CommandQueue (the
    /// current order plus pending ones) and carries out the current order each simulation frame using UnitMover and
    /// UnitAttacker. A held move intent (direct control) takes precedence: while it is non-zero the unit drops its
    /// orders and steers instead.
    /// </summary>
```

(b) Add a field after `Vector3 lastChaseTarget;`:

```csharp
        Vector3 moveIntent;
```

(c) Add after the `PendingCommands` property:

```csharp
        /// <summary>The direction direct control is steering the unit in, or zero. See SetMoveIntent.</summary>
        public Vector3 MoveIntent => moveIntent;
```

(d) Add after the `Issue` method:

```csharp
        /// <summary>
        /// Sets the direction direct control steers the unit in: flattened, length clamped to 1, zero for none. Held
        /// until set again. While it is non-zero and simulation time runs, the unit drops all of its orders (as Stop
        /// does) and steers instead, so manual control always wins over queued orders. Orders issued meanwhile are
        /// accepted and then dropped on the next simulation frame.
        /// </summary>
        public void SetMoveIntent(Vector3 direction)
        {
            direction.y = 0f;
            moveIntent = Vector3.ClampMagnitude(direction, 1f);
        }
```

(e) Replace `Update` with:

```csharp
        void Update()
        {
            // Orders and steering only advance while simulation time advances (tactical pause sets timeScale to 0).
            if (Time.deltaTime <= 0f)
                return;

            if (moveIntent != Vector3.zero)
            {
                if (queue.Current != null)
                    StopAll();
                Mover.Steer(moveIntent);
                return;
            }

            switch (queue.Current)
            {
                case MoveCommand _:
                    if (Mover.HasArrived)
                        StartNext();
                    break;
                case AttackCommand attack:
                    UpdateAttack(attack.Target);
                    break;
            }
        }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `Tools/run-tests.sh EditMode` → expected `total="97" passed="97"`, `EXIT=0`.
Run: `Tools/run-tests.sh PlayMode` → expected `total="77" passed="77"`, `EXIT=0`.

- [ ] **Step 6: Commit**

```bash
git add Assets/_Project/Scripts/Units/CommandableUnit.cs Assets/_Project/Tests/EditMode/CommandableUnitTests.cs Assets/_Project/Tests/PlayMode/CommandableUnitMoveIntentPlayModeTests.cs Assets/_Project/Tests/PlayMode/CommandableUnitMoveIntentPlayModeTests.cs.meta
git commit -m "Let a held move intent take a unit off its orders and steer it

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: `PrimaryCharacter`

**Files:**
- Create: `Assets/_Project/Scripts/Controls/PrimaryCharacter.cs`
- Test: `Assets/_Project/Tests/EditMode/PrimaryCharacterTests.cs` (new)

**Interfaces:**
- Consumes: `CommandableUnit`, `TacticalPause.IsPaused`.
- Produces: `public sealed class PrimaryCharacter : MonoBehaviour` with:
  - serialized fields `unit` (`CommandableUnit`) and `tacticalPause` (`TacticalPause`). These exact field names are used by the scene builder in Task 8.
  - `CommandableUnit Unit { get; }`
  - `bool IsTakeoverOn { get; }`, false at start
  - `void SetTakeover(bool on)` and `void ToggleTakeover()`
  - `bool HasUnit { get; }`: `unit != null && unit.enabled && unit.gameObject.activeInHierarchy`
  - `bool IsPaused { get; }`: false without a `TacticalPause`
  - `bool IsDriving { get; }`: `IsTakeoverOn && !IsPaused && HasUnit`
  - `internal void Initialize(CommandableUnit primaryUnit, TacticalPause pause)`, which may be called again to change the unit

- [ ] **Step 1: Write the failing tests**

Create `Assets/_Project/Tests/EditMode/PrimaryCharacterTests.cs`:

```csharp
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class PrimaryCharacterTests
    {
        GameObject systems;
        GameObject unitHost;
        TacticalPause pause;
        PrimaryCharacter primary;
        CommandableUnit unit;

        [SetUp]
        public void SetUp()
        {
            Time.timeScale = 1f;
            systems = new GameObject("Systems");
            pause = systems.AddComponent<TacticalPause>();
            primary = systems.AddComponent<PrimaryCharacter>();
            unitHost = new GameObject("Primary");
            unit = unitHost.AddComponent<CommandableUnit>();
            primary.Initialize(unit, pause);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(systems);
            Object.DestroyImmediate(unitHost);
            Time.timeScale = 1f;
        }

        [Test]
        public void StartsInFreeMode()
        {
            Assert.That(primary.IsTakeoverOn, Is.False);
            Assert.That(primary.IsDriving, Is.False);
        }

        [Test]
        public void ExposesItsUnit()
        {
            Assert.That(primary.Unit, Is.SameAs(unit));
            Assert.That(primary.HasUnit, Is.True);
        }

        [Test]
        public void ToggleTakeover_TurnsItOnAndOff()
        {
            primary.ToggleTakeover();
            Assert.That(primary.IsTakeoverOn, Is.True);
            primary.ToggleTakeover();
            Assert.That(primary.IsTakeoverOn, Is.False);
        }

        [Test]
        public void SetTakeover_SetsTheMode()
        {
            primary.SetTakeover(true);
            primary.SetTakeover(true);
            Assert.That(primary.IsTakeoverOn, Is.True);
            primary.SetTakeover(false);
            Assert.That(primary.IsTakeoverOn, Is.False);
        }

        [Test]
        public void IsDriving_WithTakeoverOn_Unpaused_AndAnActiveUnit()
        {
            primary.SetTakeover(true);
            Assert.That(primary.IsDriving, Is.True);
        }

        [Test]
        public void IsDriving_FalseWhilePaused_AndBackOnResume()
        {
            primary.SetTakeover(true);
            pause.Pause();
            Assert.That(primary.IsPaused, Is.True);
            Assert.That(primary.IsDriving, Is.False);
            pause.Resume();
            Assert.That(primary.IsDriving, Is.True);
        }

        [Test]
        public void ToggleTakeover_WhilePaused_TakesEffectOnResume()
        {
            pause.Pause();
            primary.ToggleTakeover();
            Assert.That(primary.IsTakeoverOn, Is.True);
            Assert.That(primary.IsDriving, Is.False);
            pause.Resume();
            Assert.That(primary.IsDriving, Is.True);
        }

        [Test]
        public void HasUnit_FalseWithoutAUnit()
        {
            primary.Initialize(null, pause);
            primary.SetTakeover(true);
            Assert.That(primary.HasUnit, Is.False);
            Assert.That(primary.IsDriving, Is.False);
        }

        [Test]
        public void HasUnit_FalseWhenTheUnitIsDisabled()
        {
            primary.SetTakeover(true);
            unit.enabled = false;
            Assert.That(primary.HasUnit, Is.False);
            Assert.That(primary.IsDriving, Is.False);
        }

        [Test]
        public void HasUnit_FalseWhenTheUnitIsInactive()
        {
            primary.SetTakeover(true);
            unitHost.SetActive(false);
            Assert.That(primary.HasUnit, Is.False);
            Assert.That(primary.IsDriving, Is.False);
        }

        [Test]
        public void IsPaused_FalseWithoutAPauseService()
        {
            primary.Initialize(unit, null);
            primary.SetTakeover(true);
            Assert.That(primary.IsPaused, Is.False);
            Assert.That(primary.IsDriving, Is.True);
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `Tools/run-tests.sh EditMode Blackglass.Tests.PrimaryCharacterTests`
Expected: `EXIT=3` with `error CS0246: The type or namespace name 'PrimaryCharacter' could not be found`.

- [ ] **Step 3: Implement `PrimaryCharacter`**

Create `Assets/_Project/Scripts/Controls/PrimaryCharacter.cs`:

```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The player's primary character and whether takeover mode is on. Holds state only: DirectControlInput, click
    /// input, the camera and the HUD read it. Takeover starts off (free mode) and can be toggled while paused; it
    /// takes effect when simulation runs again.
    /// </summary>
    public sealed class PrimaryCharacter : MonoBehaviour
    {
        [SerializeField] CommandableUnit unit;
        [SerializeField] TacticalPause tacticalPause;

        public CommandableUnit Unit => unit;

        public bool IsTakeoverOn { get; private set; }

        /// <summary>
        /// True when there is a primary character that can act: assigned, enabled and active. Uses enabled and
        /// activeInHierarchy (not isActiveAndEnabled), like UnitSelection, so it gives the same answer in EditMode.
        /// </summary>
        public bool HasUnit => unit != null && unit.enabled && unit.gameObject.activeInHierarchy;

        public bool IsPaused => tacticalPause != null && tacticalPause.IsPaused;

        /// <summary>True while WASD drives the primary character: takeover on, simulation running, unit present.</summary>
        public bool IsDriving => IsTakeoverOn && !IsPaused && HasUnit;

        internal void Initialize(CommandableUnit primaryUnit, TacticalPause pause)
        {
            unit = primaryUnit;
            tacticalPause = pause;
        }

        public void SetTakeover(bool on) => IsTakeoverOn = on;

        public void ToggleTakeover() => IsTakeoverOn = !IsTakeoverOn;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `Tools/run-tests.sh EditMode` → expected `total="108" passed="108"`, `EXIT=0`.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/Controls/PrimaryCharacter.cs Assets/_Project/Scripts/Controls/PrimaryCharacter.cs.meta Assets/_Project/Tests/EditMode/PrimaryCharacterTests.cs Assets/_Project/Tests/EditMode/PrimaryCharacterTests.cs.meta
git commit -m "Add PrimaryCharacter to hold the primary unit and takeover mode

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Character input actions and `DirectControlInput`

**Files:**
- Modify: `Assets/_Project/Input/BlackglassControls.inputactions` (add a third map)
- Create: `Assets/_Project/Scripts/Controls/DirectControlInput.cs`
- Test: `Assets/_Project/Tests/EditMode/DirectControlDirectionTests.cs` (new)
- Test: `Assets/_Project/Tests/PlayMode/DirectControlInputTests.cs` (new)

**Interfaces:**
- Consumes:
  - `PrimaryCharacter` (`Unit`, `IsDriving`, `ToggleTakeover`, `SetTakeover`, `Initialize`), from Task 3.
  - `CommandableUnit.SetMoveIntent` and `MoveIntent`, from Task 2.
  - `InputActionUtility.SetEnabled` and `Read<T>`.
- Produces:
  - Input actions `Character/Move` (Value, Vector2, WASD composite) and `Character/Takeover` (Button, `<Keyboard>/v`).
  - `public sealed class DirectControlInput : MonoBehaviour`, marked `[DefaultExecutionOrder(-100)]`, with:
    - serialized fields `primary`, `viewCamera`, `moveAction`, `takeoverAction` (names used by the scene builder in Task 8);
    - `internal void Initialize(PrimaryCharacter primaryCharacter, Camera camera, InputActionReference move, InputActionReference takeover)`;
    - `public static Vector3 ToWorldDirection(Vector2 input, float cameraYawDegrees)`.

- [ ] **Step 1: Add the `Character` action map**

In `Assets/_Project/Input/BlackglassControls.inputactions`, find the end of the `Commands` map:

```json
                { "name": "", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9e17", "path": "<Keyboard>/escape", "interactions": "", "processors": "", "groups": "", "action": "ClearSelection", "isComposite": false, "isPartOfComposite": false }
            ]
        }
    ],
```

Replace it with this, which adds a third map after `Commands`:

```json
                { "name": "", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9e17", "path": "<Keyboard>/escape", "interactions": "", "processors": "", "groups": "", "action": "ClearSelection", "isComposite": false, "isPartOfComposite": false }
            ]
        },
        {
            "name": "Character",
            "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9f01",
            "actions": [
                { "name": "Move", "type": "Value", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9f02", "expectedControlType": "Vector2", "processors": "", "interactions": "", "initialStateCheck": true },
                { "name": "Takeover", "type": "Button", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9f03", "expectedControlType": "Button", "processors": "", "interactions": "", "initialStateCheck": false }
            ],
            "bindings": [
                { "name": "WASD", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9f11", "path": "2DVector", "interactions": "", "processors": "", "groups": "", "action": "Move", "isComposite": true, "isPartOfComposite": false },
                { "name": "up", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9f12", "path": "<Keyboard>/w", "interactions": "", "processors": "", "groups": "", "action": "Move", "isComposite": false, "isPartOfComposite": true },
                { "name": "down", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9f13", "path": "<Keyboard>/s", "interactions": "", "processors": "", "groups": "", "action": "Move", "isComposite": false, "isPartOfComposite": true },
                { "name": "left", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9f14", "path": "<Keyboard>/a", "interactions": "", "processors": "", "groups": "", "action": "Move", "isComposite": false, "isPartOfComposite": true },
                { "name": "right", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9f15", "path": "<Keyboard>/d", "interactions": "", "processors": "", "groups": "", "action": "Move", "isComposite": false, "isPartOfComposite": true },
                { "name": "", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9f16", "path": "<Keyboard>/v", "interactions": "", "processors": "", "groups": "", "action": "Takeover", "isComposite": false, "isPartOfComposite": false }
            ]
        }
    ],
```

Verify that the file is valid JSON and that the map exists:

```bash
python -c "import json; d=json.load(open('Assets/_Project/Input/BlackglassControls.inputactions')); print([m['name'] for m in d['maps']])"
```

Expected: `['Camera', 'Commands', 'Character']`.

- [ ] **Step 2: Write the failing EditMode tests**

Create `Assets/_Project/Tests/EditMode/DirectControlDirectionTests.cs`:

```csharp
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class DirectControlDirectionTests
    {
        static void AssertDirection(Vector3 actual, float x, float z)
        {
            Assert.That(actual.x, Is.EqualTo(x).Within(1e-4f), "x");
            Assert.That(actual.y, Is.EqualTo(0f), "y");
            Assert.That(actual.z, Is.EqualTo(z).Within(1e-4f), "z");
        }

        // W ("into the screen") follows the camera's yaw.
        [TestCase(0f, 0f, 1f)]
        [TestCase(90f, 1f, 0f)]
        [TestCase(180f, 0f, -1f)]
        [TestCase(-90f, -1f, 0f)]
        public void ToWorldDirection_ForwardFollowsTheCameraYaw(float yaw, float x, float z)
        {
            AssertDirection(DirectControlInput.ToWorldDirection(Vector2.up, yaw), x, z);
        }

        [Test]
        public void ToWorldDirection_RightIsTheCamerasRight()
        {
            AssertDirection(DirectControlInput.ToWorldDirection(Vector2.right, 0f), 1f, 0f);
            AssertDirection(DirectControlInput.ToWorldDirection(Vector2.right, 90f), 0f, -1f);
        }

        [Test]
        public void ToWorldDirection_ClampsDiagonalsToLengthOne()
        {
            var direction = DirectControlInput.ToWorldDirection(new Vector2(1f, 1f), 0f);
            Assert.That(direction.magnitude, Is.EqualTo(1f).Within(1e-4f));
            AssertDirection(direction, 0.70711f, 0.70711f);
        }

        [Test]
        public void ToWorldDirection_ZeroIsZero()
        {
            Assert.That(DirectControlInput.ToWorldDirection(Vector2.zero, 37f), Is.EqualTo(Vector3.zero));
        }
    }
}
```

- [ ] **Step 3: Write the failing PlayMode tests**

Create `Assets/_Project/Tests/PlayMode/DirectControlInputTests.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class DirectControlInputTests : InputTestFixture
    {
        Keyboard keyboard;
        TestWorld world;
        Camera viewCamera;
        CommandableUnit primaryUnit;
        CommandableUnit companion;
        TacticalPause pause;
        PrimaryCharacter primary;
        DirectControlInput input;

        public override void Setup()
        {
            base.Setup();
            keyboard = InputSystem.AddDevice<Keyboard>();
            world = new TestWorld();

            world.CreateEnvironment();
            primaryUnit = world.CreateUnit(new Vector3(-6f, 0f, -6f));
            companion = world.CreateUnit(new Vector3(6f, 0f, -6f));

            var cameraObject = world.Track(new GameObject("Camera"));
            cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 25f, -20f), Quaternion.Euler(50f, 0f, 0f));
            viewCamera = cameraObject.AddComponent<Camera>();

            var systems = world.Track(new GameObject("Systems"));
            systems.SetActive(false);
            pause = systems.AddComponent<TacticalPause>();
            primary = systems.AddComponent<PrimaryCharacter>();
            primary.Initialize(primaryUnit, pause);
            var actions = TestControls.Load();
            input = systems.AddComponent<DirectControlInput>();
            input.Initialize(primary, viewCamera,
                TestControls.Ref(actions, "Character/Move"),
                TestControls.Ref(actions, "Character/Takeover"));
            systems.SetActive(true);
        }

        public override void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
            base.TearDown();
        }

        IEnumerator Tap(KeyControl key)
        {
            Press(key);
            yield return null;
            Release(key);
            yield return null;
        }

        IEnumerator Hold(KeyControl key, float seconds)
        {
            Press(key);
            yield return new WaitForSecondsRealtime(seconds);
            Release(key);
            yield return null;
        }

        // Two orders each for the primary character and the companion, on paths that never cross.
        (MoveCommand primaryFirst, MoveCommand primarySecond, MoveCommand companionFirst, MoveCommand companionSecond) QueueOrdersForBoth()
        {
            var orders = (new MoveCommand(new Vector3(-6f, 0f, 2f)), new MoveCommand(new Vector3(-12f, 0f, 2f)),
                new MoveCommand(new Vector3(6f, 0f, 2f)), new MoveCommand(new Vector3(12f, 0f, 2f)));
            Assert.That(primaryUnit.Issue(orders.Item1), Is.True);
            Assert.That(primaryUnit.Issue(orders.Item2, IssueMode.Append), Is.True);
            Assert.That(companion.Issue(orders.Item3), Is.True);
            Assert.That(companion.Issue(orders.Item4, IssueMode.Append), Is.True);
            return orders;
        }

        // Waits until both units are idle, recording where each was when its first order finished.
        IEnumerator RunBothQueuesToTheEnd(Vector3[] positionsWhenFirstFinished)
        {
            var units = new[] { primaryUnit, companion };
            var recorded = new bool[units.Length];
            var deadline = Time.realtimeSinceStartup + 20f;
            while ((primaryUnit.CurrentCommand != null || companion.CurrentCommand != null) && Time.realtimeSinceStartup < deadline)
            {
                for (var i = 0; i < units.Length; i++)
                {
                    if (!recorded[i] && units[i].PendingCommands.Count == 0)
                    {
                        positionsWhenFirstFinished[i] = units[i].transform.position;
                        recorded[i] = true;
                    }
                }
                yield return null;
            }
        }

        void AssertRanInOrder(Vector3[] positionsWhenFirstFinished, MoveCommand primaryFirst, MoveCommand primarySecond,
            MoveCommand companionFirst, MoveCommand companionSecond)
        {
            Assert.That(primaryUnit.CurrentCommand, Is.Null, "The primary character's orders never finished");
            Assert.That(companion.CurrentCommand, Is.Null, "The companion's orders never finished");
            Assert.That(TestWorld.HorizontalDistance(positionsWhenFirstFinished[0], primaryFirst.Destination), Is.LessThan(0.6f),
                "The primary character's second order started before it reached the first destination");
            Assert.That(TestWorld.HorizontalDistance(positionsWhenFirstFinished[1], companionFirst.Destination), Is.LessThan(0.6f),
                "The companion's second order started before it reached the first destination");
            Assert.That(TestWorld.HorizontalDistance(primaryUnit.transform.position, primarySecond.Destination), Is.LessThan(0.3f));
            Assert.That(TestWorld.HorizontalDistance(companion.transform.position, companionSecond.Destination), Is.LessThan(0.3f));
        }

        [UnityTest]
        public IEnumerator V_TogglesTakeover()
        {
            yield return null;
            Assert.That(primary.IsTakeoverOn, Is.False);
            yield return Tap(keyboard.vKey);
            Assert.That(primary.IsTakeoverOn, Is.True);
            yield return Tap(keyboard.vKey);
            Assert.That(primary.IsTakeoverOn, Is.False);
        }

        [UnityTest]
        public IEnumerator V_WhilePaused_TakesEffectOnResume()
        {
            yield return null;
            pause.Pause();
            yield return Tap(keyboard.vKey);
            Assert.That(primary.IsTakeoverOn, Is.True);
            Assert.That(primary.IsDriving, Is.False);

            pause.Resume();
            Assert.That(primary.IsDriving, Is.True);
        }

        [UnityTest]
        public IEnumerator TakeoverW_MovesThePrimaryForward_AndTheCompanionKeepsItsOrders()
        {
            yield return null;
            var first = new MoveCommand(new Vector3(6f, 0f, 8f));
            var second = new MoveCommand(new Vector3(0f, 0f, 8f));
            companion.Issue(first);
            companion.Issue(second, IssueMode.Append);
            primary.SetTakeover(true);
            yield return null;
            var start = primaryUnit.transform.position;

            yield return Hold(keyboard.wKey, 0.5f);

            var travelled = primaryUnit.transform.position - start;
            Assert.That(travelled.z, Is.GreaterThan(1f), "W did not move the primary character forward");
            Assert.That(Mathf.Abs(travelled.x), Is.LessThan(0.3f));
            Assert.That(companion.CurrentCommand, Is.SameAs(first), "Takeover cancelled the companion's order");
            Assert.That(companion.PendingCommands, Is.EqualTo(new UnitCommand[] { second }));
        }

        [UnityTest]
        public IEnumerator TakeoverW_ClearsThePrimarysOrders()
        {
            yield return null;
            primaryUnit.Issue(new MoveCommand(new Vector3(-14f, 0f, -6f)));
            primaryUnit.Issue(new MoveCommand(new Vector3(-14f, 0f, 6f)), IssueMode.Append);
            primary.SetTakeover(true);
            yield return null;

            yield return Hold(keyboard.wKey, 0.2f);

            Assert.That(primaryUnit.CurrentCommand, Is.Null);
            Assert.That(primaryUnit.PendingCommands, Is.Empty);
        }

        [UnityTest]
        public IEnumerator FreeMode_W_DoesNotMoveThePrimary()
        {
            yield return null;
            var start = primaryUnit.transform.position;
            Press(keyboard.wKey);
            yield return new WaitForSecondsRealtime(0.4f);

            Assert.That(primaryUnit.MoveIntent, Is.EqualTo(Vector3.zero));
            Assert.That(TestWorld.HorizontalDistance(primaryUnit.transform.position, start), Is.LessThan(0.01f));
            Release(keyboard.wKey);
            yield return null;
        }

        [UnityTest]
        public IEnumerator W_IsRelativeToTheCameraYaw()
        {
            yield return null;
            viewCamera.transform.rotation = Quaternion.Euler(50f, 90f, 0f);
            primary.SetTakeover(true);
            yield return null;
            var start = primaryUnit.transform.position;

            yield return Hold(keyboard.wKey, 0.4f);

            var travelled = primaryUnit.transform.position - start;
            Assert.That(travelled.x, Is.GreaterThan(1f), "With the camera facing +x, W must move the character toward +x");
            Assert.That(Mathf.Abs(travelled.z), Is.LessThan(0.3f));
        }

        [UnityTest]
        public IEnumerator KeysHeldAcrossResume_DoNotClearOrdersUntilPressedAgain()
        {
            yield return null;
            primary.SetTakeover(true);
            pause.Pause();
            var first = new MoveCommand(new Vector3(-6f, 0f, 8f));
            var second = new MoveCommand(new Vector3(6f, 0f, 8f));
            primaryUnit.Issue(first);
            primaryUnit.Issue(second, IssueMode.Append);
            Press(keyboard.wKey);   // for example still held from panning the camera during pause
            yield return null;
            yield return null;

            pause.Resume();
            yield return new WaitForSecondsRealtime(0.3f);
            Assert.That(primaryUnit.CurrentCommand, Is.SameAs(first), "A key held across resume wiped the plan");
            Assert.That(primaryUnit.PendingCommands, Is.EqualTo(new UnitCommand[] { second }));

            Release(keyboard.wKey);
            yield return null;
            yield return Hold(keyboard.wKey, 0.2f);
            Assert.That(primaryUnit.CurrentCommand, Is.Null, "A fresh press must take over");
            Assert.That(primaryUnit.PendingCommands, Is.Empty);
        }

        [UnityTest]
        public IEnumerator V_WhileWIsHeld_DoesNotSteerUntilWIsPressedAgain()
        {
            yield return null;
            Press(keyboard.wKey);   // panning the camera in free mode
            yield return null;
            yield return Tap(keyboard.vKey);
            Assert.That(primary.IsDriving, Is.True);
            var start = primaryUnit.transform.position;
            yield return new WaitForSecondsRealtime(0.3f);
            Assert.That(TestWorld.HorizontalDistance(primaryUnit.transform.position, start), Is.LessThan(0.01f),
                "Turning takeover on while W was held made the character run");

            Release(keyboard.wKey);
            yield return null;
            yield return Hold(keyboard.wKey, 0.3f);
            Assert.That(TestWorld.HorizontalDistance(primaryUnit.transform.position, start), Is.GreaterThan(0.5f));
        }

        [UnityTest]
        public IEnumerator Pausing_ZeroesTheIntent()
        {
            yield return null;
            primary.SetTakeover(true);
            yield return null;
            Press(keyboard.wKey);
            yield return new WaitForSecondsRealtime(0.1f);
            Assert.That(primaryUnit.MoveIntent, Is.Not.EqualTo(Vector3.zero));

            pause.Pause();
            yield return null;
            Assert.That(primaryUnit.MoveIntent, Is.EqualTo(Vector3.zero));
            Release(keyboard.wKey);
            yield return null;
        }

        [UnityTest]
        public IEnumerator DisablingTheInput_WhileWIsHeld_ZeroesTheIntent()
        {
            yield return null;
            primary.SetTakeover(true);
            yield return null;
            Press(keyboard.wKey);
            yield return new WaitForSecondsRealtime(0.1f);
            Assert.That(primaryUnit.MoveIntent, Is.Not.EqualTo(Vector3.zero));

            input.enabled = false;
            Assert.That(primaryUnit.MoveIntent, Is.EqualTo(Vector3.zero), "A disabled input left the character steering");
            yield return null;
            var stoppedAt = primaryUnit.transform.position;
            yield return new WaitForSecondsRealtime(0.2f);
            Assert.That(TestWorld.HorizontalDistance(primaryUnit.transform.position, stoppedAt), Is.LessThan(0.05f));
            Release(keyboard.wKey);
            yield return null;
        }

        [UnityTest]
        public IEnumerator PrimaryUnitDeactivated_WhileDriving_StopsDrivingWithoutErrors()
        {
            yield return null;
            primary.SetTakeover(true);
            yield return null;
            Press(keyboard.wKey);
            yield return new WaitForSecondsRealtime(0.1f);

            primaryUnit.gameObject.SetActive(false);
            yield return new WaitForSecondsRealtime(0.2f);

            Assert.That(primary.IsDriving, Is.False);
            Assert.That(primaryUnit.MoveIntent, Is.EqualTo(Vector3.zero));
            Release(keyboard.wKey);
            yield return null;
            // Any error or exception logged meanwhile fails the test.
        }

        [UnityTest]
        public IEnumerator PauseCycling_KeepsBothQueues_AndRunsThemInOrder()
        {
            yield return null;
            primary.SetTakeover(true);   // takeover on, but no keys: resuming must never clear orders
            pause.Pause();
            var (primaryFirst, primarySecond, companionFirst, companionSecond) = QueueOrdersForBoth();

            for (var i = 0; i < 10; i++)
            {
                pause.Resume();
                yield return null;
                pause.Pause();
                yield return new WaitForSecondsRealtime(0.02f);
            }
            Assert.That(primaryUnit.CurrentCommand, Is.Not.Null, "Pause cycling dropped the primary character's orders");
            Assert.That(companion.CurrentCommand, Is.Not.Null, "Pause cycling dropped the companion's orders");
            pause.Resume();

            var positions = new Vector3[2];
            yield return RunBothQueuesToTheEnd(positions);
            AssertRanInOrder(positions, primaryFirst, primarySecond, companionFirst, companionSecond);
        }

        [UnityTest]
        public IEnumerator TakeoverCycling_KeepsBothQueues_AndRunsThemInOrder()
        {
            yield return null;
            pause.Pause();
            var (primaryFirst, primarySecond, companionFirst, companionSecond) = QueueOrdersForBoth();

            for (var i = 0; i < 6; i++)
            {
                yield return Tap(keyboard.vKey);
                if (i % 2 == 0)
                {
                    pause.Resume();
                    yield return null;
                    pause.Pause();
                }
            }

            Assert.That(primaryUnit.CurrentCommand, Is.SameAs(primaryFirst));
            Assert.That(primaryUnit.PendingCommands, Is.EqualTo(new UnitCommand[] { primarySecond }));
            Assert.That(companion.CurrentCommand, Is.SameAs(companionFirst));
            Assert.That(companion.PendingCommands, Is.EqualTo(new UnitCommand[] { companionSecond }));
            pause.Resume();

            var positions = new Vector3[2];
            yield return RunBothQueuesToTheEnd(positions);
            AssertRanInOrder(positions, primaryFirst, primarySecond, companionFirst, companionSecond);
        }
    }
}
#endif
```

- [ ] **Step 4: Run the tests to verify they fail**

Run: `Tools/run-tests.sh EditMode Blackglass.Tests.DirectControlDirectionTests`
Expected: `EXIT=3` with `error CS0103: The name 'DirectControlInput' does not exist` (or `CS0246`).

- [ ] **Step 5: Implement `DirectControlInput`**

Create `Assets/_Project/Scripts/Controls/DirectControlInput.cs`:

```csharp
using UnityEngine;
using UnityEngine.InputSystem;

namespace Blackglass
{
    /// <summary>
    /// Takeover-mode input for the primary character. V toggles takeover. While the primary character is being driven
    /// (PrimaryCharacter.IsDriving), WASD becomes a camera-relative move intent on its CommandableUnit; otherwise the
    /// intent is zero. Contains no movement rules: the unit decides what the intent means.
    /// Release gate: whenever driving starts (resume, or takeover turned on), keys already held are ignored until Move
    /// reads zero, so a key held from panning the camera cannot wipe orders just queued.
    /// </summary>
    [DefaultExecutionOrder(-100)] // set the intent before units update in the same frame
    public sealed class DirectControlInput : MonoBehaviour
    {
        [SerializeField] PrimaryCharacter primary;
        [SerializeField] Camera viewCamera;

        [Header("Input")]
        [SerializeField] InputActionReference moveAction;
        [SerializeField] InputActionReference takeoverAction;

        bool wasDriving;
        bool waitingForRelease;

        internal void Initialize(PrimaryCharacter primaryCharacter, Camera camera, InputActionReference move,
            InputActionReference takeover)
        {
            primary = primaryCharacter;
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
            if (primary == null)
                return;

            var driving = primary.IsDriving;
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
            if (primary != null && primary.Unit != null)
                primary.Unit.SetMoveIntent(direction);
        }

        void OnTakeover(InputAction.CallbackContext context)
        {
            if (primary != null)
                primary.ToggleTakeover();
        }
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `Tools/run-tests.sh EditMode` → expected `total="115" passed="115"`, `EXIT=0`.
Run: `Tools/run-tests.sh PlayMode` → expected `total="90" passed="90"`, `EXIT=0`.

- [ ] **Step 7: Commit**

```bash
git add Assets/_Project/Input/BlackglassControls.inputactions Assets/_Project/Scripts/Controls/DirectControlInput.cs Assets/_Project/Scripts/Controls/DirectControlInput.cs.meta Assets/_Project/Tests/EditMode/DirectControlDirectionTests.cs Assets/_Project/Tests/EditMode/DirectControlDirectionTests.cs.meta Assets/_Project/Tests/PlayMode/DirectControlInputTests.cs Assets/_Project/Tests/PlayMode/DirectControlInputTests.cs.meta
git commit -m "Drive the primary character with WASD in takeover mode

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Real-time clicks order the primary character

**Files:**
- Modify: `Assets/_Project/Scripts/Controls/PlayerCommandInput.cs`
- Modify: `Assets/_Project/Tests/PlayMode/PlayerCommandInputTests.cs`

**Interfaces:**
- Consumes: `PrimaryCharacter` (`Initialize`, `HasUnit`, `Unit`), from Task 3.
- Produces:
  - `PlayerCommandInput` has a new serialized field `primary` (`PrimaryCharacter`).
  - `Initialize` gains a trailing optional parameter: `internal void Initialize(Camera camera, UnitSelection unitSelection, TacticalPause pause, InputActionReference command, InputActionReference pointerPosition, InputActionReference togglePause, InputActionReference modifier, InputActionReference stop, InputActionReference clearSelection, PrimaryCharacter primaryCharacter = null)`.
  - **Rule:** a ground or enemy click orders `primary.Unit` alone when the game isn't paused and `primary.HasUnit` is true. Otherwise it orders the selection, exactly as in Phase 2.

- [ ] **Step 1: Wire a unit-less `PrimaryCharacter` into the fixture**

In `Assets/_Project/Tests/PlayMode/PlayerCommandInputTests.cs`:

(a) Add a field after `PlayerCommandInput input;`:

```csharp
        PrimaryCharacter primaryCharacter;
```

(b) In `Setup`, replace the `input = ...` / `input.Initialize(...)` block with the following. A `PrimaryCharacter` without a unit means Phase 2 behaviour, so all existing tests keep testing selection ordering.

```csharp
            // No unit yet: Phase 2 behaviour (orders go to the selection) until a test picks a primary character.
            primaryCharacter = systems.AddComponent<PrimaryCharacter>();
            primaryCharacter.Initialize(null, pause);
            var actions = TestControls.Load();
            input = systems.AddComponent<PlayerCommandInput>();
            input.Initialize(viewCamera, selection, pause,
                TestControls.Ref(actions, "Commands/Command"),
                TestControls.Ref(actions, "Commands/PointerPosition"),
                TestControls.Ref(actions, "Commands/TogglePause"),
                TestControls.Ref(actions, "Commands/Modifier"),
                TestControls.Ref(actions, "Commands/Stop"),
                TestControls.Ref(actions, "Commands/ClearSelection"),
                primaryCharacter);
```

(The old `var actions = TestControls.Load();` line above `input = ...` is replaced by the one in this block; make sure it isn't declared twice.)

- [ ] **Step 2: Write the failing tests**

Append inside the class, after `OrdersClickedWhilePaused_WaitUntilResume`:

```csharp
        // --- With a primary character (Phase 3). These tests give the fixture's PrimaryCharacter a unit.

        void MakePrimary(SelectableUnit unit) => primaryCharacter.Initialize(unit.Unit, pause);

        [UnityTest]
        public IEnumerator RealTimeClickGround_WithAPrimary_OrdersOnlyThePrimary()
        {
            yield return null;
            MakePrimary(unitA);
            yield return LeftClickAt(ScreenPointOf(unitB));
            yield return LeftClickAt(ScreenPointOf(GroundPoint));

            Assert.That(unitA.Unit.CurrentCommand, Is.TypeOf<MoveCommand>());
            var destination = ((MoveCommand)unitA.Unit.CurrentCommand).Destination;
            Assert.That(TestWorld.HorizontalDistance(destination, GroundPoint), Is.LessThan(0.1f));
            Assert.That(unitB.Unit.CurrentCommand, Is.Null, "The selection must not be ordered in real time");
            Assert.That(selection.Selected, Is.EqualTo(new[] { unitB }), "Ordering the primary must not change the selection");
        }

        [UnityTest]
        public IEnumerator RealTimeClickDummy_WithAPrimary_ThePrimaryAttacks()
        {
            yield return null;
            MakePrimary(unitA);
            yield return LeftClickAt(ScreenPointOf(unitB));
            yield return LeftClickAt(ScreenPointOf(dummy));

            Assert.That(unitA.Unit.CurrentCommand, Is.TypeOf<AttackCommand>());
            Assert.That(((AttackCommand)unitA.Unit.CurrentCommand).Target, Is.SameAs(dummy));
            Assert.That(unitB.Unit.CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator RealTimeShiftClick_WithAPrimary_QueuesForThePrimary()
        {
            yield return null;
            MakePrimary(unitA);
            yield return LeftClickAt(ScreenPointOf(GroundPoint));
            yield return ShiftLeftClickAt(ScreenPointOf(OtherGroundPoint));

            Assert.That(unitA.Unit.CurrentCommand, Is.TypeOf<MoveCommand>());
            Assert.That(unitA.Unit.PendingCommands, Has.Count.EqualTo(1));
            var queued = (MoveCommand)unitA.Unit.PendingCommands[0];
            Assert.That(TestWorld.HorizontalDistance(queued.Destination, OtherGroundPoint), Is.LessThan(0.1f));
        }

        [UnityTest]
        public IEnumerator PausedClickGround_WithAPrimary_OrdersTheSelection()
        {
            yield return null;
            MakePrimary(unitA);
            pause.Pause();
            yield return LeftClickAt(ScreenPointOf(unitB));
            yield return LeftClickAt(ScreenPointOf(GroundPoint));

            Assert.That(unitB.Unit.CurrentCommand, Is.TypeOf<MoveCommand>());
            Assert.That(unitA.Unit.CurrentCommand, Is.Null, "Paused clicks must order the selection, not the primary");
        }

        [UnityTest]
        public IEnumerator RealTimeClickFriendly_WithAPrimary_SelectsItAndOrdersNobody()
        {
            yield return null;
            MakePrimary(unitA);
            yield return LeftClickAt(ScreenPointOf(unitB));

            Assert.That(selection.Selected, Is.EqualTo(new[] { unitB }));
            Assert.That(unitA.Unit.CurrentCommand, Is.Null);
            Assert.That(unitB.Unit.CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator RealTimeClickOnThePrimary_SelectsIt()
        {
            yield return null;
            MakePrimary(unitA);
            yield return LeftClickAt(ScreenPointOf(unitA));

            Assert.That(selection.Selected, Is.EqualTo(new[] { unitA }));
            Assert.That(unitA.Unit.CurrentCommand, Is.Null, "Clicking the primary character must not order it onto itself");
        }

        [UnityTest]
        public IEnumerator RealTimeBoxDrag_WithAPrimary_StillSelects()
        {
            yield return null;
            MakePrimary(unitA);
            var (from, to) = BoxAround(unitA, unitB);
            yield return LeftDrag(from, to);

            Assert.That(selection.Selected, Is.EquivalentTo(new[] { unitA, unitB }));
            Assert.That(unitA.Unit.CurrentCommand, Is.Null);
            Assert.That(unitB.Unit.CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator RealTimeX_WithAPrimary_StopsTheSelectionNotThePrimary()
        {
            yield return null;
            MakePrimary(unitA);
            yield return LeftClickAt(ScreenPointOf(GroundPoint));          // the primary character walks
            pause.Pause();
            yield return LeftClickAt(ScreenPointOf(unitC));
            yield return LeftClickAt(ScreenPointOf(OtherGroundPoint));     // the selected companion walks
            pause.Resume();
            yield return null;
            Assert.That(unitC.Unit.CurrentCommand, Is.TypeOf<MoveCommand>(), "Precondition: the companion has an order");

            yield return Tap(keyboard.xKey);

            Assert.That(unitC.Unit.CurrentCommand, Is.Null, "X did not stop the selected companion");
            Assert.That(unitA.Unit.CurrentCommand, Is.TypeOf<MoveCommand>(), "X stopped the unselected primary character");
        }

        [UnityTest]
        public IEnumerator RealTimeClick_WithThePrimaryUnitDisabled_OrdersTheSelection()
        {
            yield return null;
            MakePrimary(unitA);
            unitA.Unit.enabled = false;
            yield return LeftClickAt(ScreenPointOf(unitB));
            yield return LeftClickAt(ScreenPointOf(GroundPoint));

            Assert.That(unitB.Unit.CurrentCommand, Is.TypeOf<MoveCommand>(), "Without an active primary, clicks must order the selection");
            Assert.That(unitA.Unit.CurrentCommand, Is.Null);
        }
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `Tools/run-tests.sh PlayMode Blackglass.Tests.PlayerCommandInputTests`
Expected: `EXIT=3` with `error CS1501: No overload for method 'Initialize' takes 10 arguments`.

- [ ] **Step 4: Route real-time clicks to the primary character**

In `Assets/_Project/Scripts/Controls/PlayerCommandInput.cs`:

(a) Replace the class summary with:

```csharp
    /// <summary>
    /// Translates the player's input into requests. Left button: a click on a friendly unit selects it, a click
    /// anywhere else gives an order (attack the clicked target, or move to the clicked point), and a drag box-selects.
    /// In real time the order goes to the primary character; while paused (or without a primary character) it goes to
    /// the selected units. Shift adds to the selection or queues the order. X stops the selected units, Esc clears the
    /// selection, Space toggles tactical pause. Contains no movement or combat rules.
    /// </summary>
```

(b) Add a field after `[SerializeField] TacticalPause tacticalPause;`:

```csharp
        [SerializeField] PrimaryCharacter primary;
```

(c) Rename the list field `selectedUnits` to `orderedUnits` (it is declared once and used only in `SelectedUnits()`):

```csharp
        readonly List<CommandableUnit> orderedUnits = new List<CommandableUnit>();
```

(d) Replace the `Initialize` method with:

```csharp
        internal void Initialize(Camera camera, UnitSelection unitSelection, TacticalPause pause,
            InputActionReference command, InputActionReference pointerPosition, InputActionReference togglePause,
            InputActionReference modifier, InputActionReference stop, InputActionReference clearSelection,
            PrimaryCharacter primaryCharacter = null)
        {
            viewCamera = camera;
            selection = unitSelection;
            tacticalPause = pause;
            commandAction = command;
            pointerPositionAction = pointerPosition;
            togglePauseAction = togglePause;
            modifierAction = modifier;
            stopAction = stop;
            clearSelectionAction = clearSelection;
            primary = primaryCharacter;
        }
```

(e) In `HandleClick`, replace the last line:

```csharp
            GroupOrders.Issue(SelectedUnits(), command, ModifierHeld ? IssueMode.Append : IssueMode.Replace, groupSpacing);
```

with:

```csharp
            GroupOrders.Issue(OrderedUnits(), command, ModifierHeld ? IssueMode.Append : IssueMode.Replace, groupSpacing);
```

(f) Replace the `SelectedUnits()` method with these two methods:

```csharp
        // Who a ground or enemy click orders: the primary character in real time; the selection while paused or
        // when there is no active primary character.
        List<CommandableUnit> OrderedUnits()
        {
            var paused = tacticalPause != null && tacticalPause.IsPaused;
            if (paused || primary == null || !primary.HasUnit)
                return SelectedUnits();
            orderedUnits.Clear();
            orderedUnits.Add(primary.Unit);
            return orderedUnits;
        }

        List<CommandableUnit> SelectedUnits()
        {
            orderedUnits.Clear();
            if (selection != null)
            {
                foreach (var selectable in selection.Selected)
                    orderedUnits.Add(selectable.Unit);
            }
            return orderedUnits;
        }
```

`OnStop` keeps calling `SelectedUnits()`, so X always stops the selection.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `Tools/run-tests.sh PlayMode Blackglass.Tests.PlayerCommandInputTests` → expected `total="26" passed="26"`, `EXIT=0` (17 existing + 9 new).
Run: `Tools/run-tests.sh PlayMode` → expected `total="99" passed="99"`, `EXIT=0`. The scene has no `primary` wired yet, so the scene tests keep their Phase 2 behaviour.

- [ ] **Step 6: Commit**

```bash
git add Assets/_Project/Scripts/Controls/PlayerCommandInput.cs Assets/_Project/Tests/PlayMode/PlayerCommandInputTests.cs
git commit -m "Send real-time ground and enemy clicks to the primary character

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Camera follows the primary character while driving

**Files:**
- Modify: `Assets/_Project/Scripts/CameraControl/TacticalCameraController.cs`
- Modify: `Assets/_Project/Tests/PlayMode/TacticalCameraControllerTests.cs`

**Interfaces:**
- Consumes: `PrimaryCharacter` (`IsDriving`, `Unit`, `Initialize`, `SetTakeover`), from Task 3.
- Produces:
  - `TacticalCameraController` has new serialized fields `primary` (`PrimaryCharacter`) and `followSharpness` (float, default 10).
  - `Initialize` gains a trailing optional parameter: `internal void Initialize(Camera camera, InputActionReference pan, InputActionReference rotate, InputActionReference rotateDrag, InputActionReference pointerPosition, InputActionReference zoom, PrimaryCharacter primaryCharacter = null)`.
  - **Rule:** while `primary.IsDriving` is true, the pivot's x and z ease toward the unit by `1 − exp(−followSharpness × unscaledDeltaTime)` each frame, and pan input is ignored. Otherwise the camera behaves as before.

- [ ] **Step 1: Wire a unit-less `PrimaryCharacter` into the fixture**

In `Assets/_Project/Tests/PlayMode/TacticalCameraControllerTests.cs`:

(a) Add a field after `TacticalPause pause;`:

```csharp
        PrimaryCharacter primaryCharacter;
```

(b) In `Setup`, after `var actions = TestControls.Load();`, add:

```csharp
            // No unit yet: the camera behaves as before until a test gives the primary character a unit.
            primaryCharacter = world.Track(new GameObject("Player")).AddComponent<PrimaryCharacter>();
```

(c) Change the `controller.Initialize(...)` call so it passes the primary character as the last argument:

```csharp
            controller.Initialize(viewCamera,
                TestControls.Ref(actions, "Camera/Pan"),
                TestControls.Ref(actions, "Camera/Rotate"),
                TestControls.Ref(actions, "Camera/RotateDrag"),
                TestControls.Ref(actions, "Camera/PointerPosition"),
                TestControls.Ref(actions, "Camera/Zoom"),
                primaryCharacter);
```

- [ ] **Step 2: Write the failing tests**

Append inside the class, after `SmallRightClick_DoesNotRotate`:

```csharp
        // --- Following the primary character (Phase 3).

        // A unit standing at (8, 0, 6), made the primary character with takeover on.
        CommandableUnit CreateDrivenUnit()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(8f, 0f, 6f));
            primaryCharacter.Initialize(unit, pause);
            primaryCharacter.SetTakeover(true);
            return unit;
        }

        float DistanceToRig(Component unit) =>
            TestWorld.HorizontalDistance(controller.transform.position, unit.transform.position);

        [UnityTest]
        public IEnumerator Driving_FollowsThePrimaryCharacter()
        {
            var unit = CreateDrivenUnit();
            yield return new WaitForSecondsRealtime(0.6f);

            Assert.That(DistanceToRig(unit), Is.LessThan(0.2f), "The camera did not follow the primary character");
            Assert.That(controller.transform.position.y, Is.EqualTo(0f).Within(0.001f), "Following must not lift the pivot");
        }

        [UnityTest]
        public IEnumerator Driving_IgnoresPan()
        {
            var unit = CreateDrivenUnit();
            yield return new WaitForSecondsRealtime(0.6f);

            Press(keyboard.wKey);
            yield return new WaitForSecondsRealtime(0.3f);
            Release(keyboard.wKey);
            yield return null;

            Assert.That(DistanceToRig(unit), Is.LessThan(0.2f), "W panned the camera away while driving");
        }

        [UnityTest]
        public IEnumerator PausedWithTakeoverOn_PansFreely()
        {
            CreateDrivenUnit();
            yield return new WaitForSecondsRealtime(0.6f);
            pause.Pause();
            var start = controller.transform.position;

            Press(keyboard.wKey);
            yield return new WaitForSecondsRealtime(0.2f);
            Release(keyboard.wKey);
            yield return null;

            Assert.That(controller.transform.position.z, Is.GreaterThan(start.z + 0.5f));
        }

        [UnityTest]
        public IEnumerator TakeoverOff_PansFreely_AndDoesNotFollow()
        {
            CreateDrivenUnit();
            primaryCharacter.SetTakeover(false);
            var start = controller.transform.position;

            Press(keyboard.wKey);
            yield return new WaitForSecondsRealtime(0.2f);
            Release(keyboard.wKey);
            yield return null;

            Assert.That(controller.transform.position.z, Is.GreaterThan(start.z + 0.5f));
            Assert.That(controller.transform.position.x, Is.EqualTo(start.x).Within(0.01f), "The camera drifted toward the unit");
        }

        [UnityTest]
        public IEnumerator Resume_EasesBackToThePrimaryCharacter()
        {
            var unit = CreateDrivenUnit();
            yield return new WaitForSecondsRealtime(0.6f);
            pause.Pause();
            Press(keyboard.sKey);
            yield return new WaitForSecondsRealtime(0.3f);
            Release(keyboard.sKey);
            yield return null;
            Assert.That(DistanceToRig(unit), Is.GreaterThan(1f), "Precondition: the camera panned away while paused");

            pause.Resume();
            yield return new WaitForSecondsRealtime(0.6f);

            Assert.That(DistanceToRig(unit), Is.LessThan(0.2f), "The camera did not return to the primary character");
        }
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `Tools/run-tests.sh PlayMode Blackglass.Tests.TacticalCameraControllerTests`
Expected: `EXIT=3` with `error CS1501: No overload for method 'Initialize' takes 7 arguments`.

- [ ] **Step 4: Implement following**

In `Assets/_Project/Scripts/CameraControl/TacticalCameraController.cs`:

(a) Replace the class summary with:

```csharp
    /// <summary>
    /// Simple strategy camera orbiting a pivot on the ground. Runs entirely on unscaled time so it keeps working during
    /// tactical pause. Lives on the pivot; the camera is a child. While the primary character is being driven
    /// (takeover, not paused) the pivot follows it and pan input is ignored; otherwise WASD pans freely.
    /// </summary>
```

(b) Add after `[SerializeField] Camera viewCamera;`:

```csharp
        [SerializeField] PrimaryCharacter primary;
```

(c) Add to the `[Header("Tuning")]` fields, after `boundsHalfSize`:

```csharp
        // How quickly the pivot catches up with the primary character while following (higher is tighter).
        [SerializeField, Min(0f)] float followSharpness = 10f;
```

(d) Replace `Initialize` with:

```csharp
        internal void Initialize(Camera camera, InputActionReference pan, InputActionReference rotate,
            InputActionReference rotateDrag, InputActionReference pointerPosition, InputActionReference zoom,
            PrimaryCharacter primaryCharacter = null)
        {
            viewCamera = camera;
            panAction = pan;
            rotateAction = rotate;
            rotateDragAction = rotateDrag;
            pointerPositionAction = pointerPosition;
            zoomAction = zoom;
            primary = primaryCharacter;
        }
```

(e) In `Update`, replace these lines:

```csharp
            var rotation = Quaternion.Euler(0f, yaw, 0f);
            var pan = InputActionUtility.Read<Vector2>(panAction);
            var speed = panSpeed * (distance / panReferenceDistance);
            var position = transform.position + rotation * new Vector3(pan.x, 0f, pan.y) * (speed * deltaTime);
```

with:

```csharp
            var rotation = Quaternion.Euler(0f, yaw, 0f);
            Vector3 position;
            if (primary != null && primary.IsDriving)
            {
                // Takeover: follow the primary character and ignore pan. Always eased, so after a pause the camera
                // glides back from wherever it was panned.
                var target = primary.Unit.transform.position;
                target.y = transform.position.y;
                position = Vector3.Lerp(transform.position, target, 1f - Mathf.Exp(-followSharpness * deltaTime));
            }
            else
            {
                var pan = InputActionUtility.Read<Vector2>(panAction);
                var speed = panSpeed * (distance / panReferenceDistance);
                position = transform.position + rotation * new Vector3(pan.x, 0f, pan.y) * (speed * deltaTime);
            }
```

The bounds clamp and `SetPositionAndRotation` that follow are unchanged.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `Tools/run-tests.sh PlayMode Blackglass.Tests.TacticalCameraControllerTests` → expected `total="15" passed="15"`, `EXIT=0` (10 existing + 5 new).
Run: `Tools/run-tests.sh PlayMode` → expected `total="104" passed="104"`, `EXIT=0`.

- [ ] **Step 6: Commit**

```bash
git add Assets/_Project/Scripts/CameraControl/TacticalCameraController.cs Assets/_Project/Tests/PlayMode/TacticalCameraControllerTests.cs
git commit -m "Follow the primary character with the camera in takeover mode

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: HUD status line, hints and layout

**Files:**
- Modify: `Assets/_Project/Scripts/DebugUI/PrototypeHud.cs`
- Modify: `Assets/_Project/Tests/EditMode/PrototypeHudTests.cs`

**Interfaces:**
- Consumes: `PrimaryCharacter` (`Unit`, `IsTakeoverOn`, `IsPaused`), from Task 3.
- Produces:
  - `PrototypeHud` has a new serialized field `primary` (`PrimaryCharacter`).
  - `internal static string DescribePrimary(string unitName, bool takeoverOn, bool isPaused, bool hasOrders)`.

- [ ] **Step 1: Write the failing tests**

Append inside the class in `Assets/_Project/Tests/EditMode/PrototypeHudTests.cs`:

```csharp
        [TestCase(false, false, false, "Primary: Hero | Takeover OFF (V) | Idle")]
        [TestCase(false, false, true, "Primary: Hero | Takeover OFF (V) | Following orders")]
        [TestCase(false, true, false, "Primary: Hero | Takeover OFF (V) | Idle")]
        [TestCase(false, true, true, "Primary: Hero | Takeover OFF (V) | Following orders")]
        [TestCase(true, false, false, "Primary: Hero | Takeover ON (V) | Manual control")]
        [TestCase(true, false, true, "Primary: Hero | Takeover ON (V) | Following orders")]
        [TestCase(true, true, false, "Primary: Hero | Takeover ON (after pause) | Idle")]
        [TestCase(true, true, true, "Primary: Hero | Takeover ON (after pause) | Following orders")]
        public void DescribePrimary_ShowsModeAndActivity(bool takeoverOn, bool isPaused, bool hasOrders, string expected)
        {
            Assert.That(PrototypeHud.DescribePrimary("Hero", takeoverOn, isPaused, hasOrders), Is.EqualTo(expected));
        }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `Tools/run-tests.sh EditMode Blackglass.Tests.PrototypeHudTests`
Expected: `EXIT=3` with `error CS0117: 'PrototypeHud' does not contain a definition for 'DescribePrimary'`.

- [ ] **Step 3: Implement the HUD changes**

In `Assets/_Project/Scripts/DebugUI/PrototypeHud.cs`:

(a) Replace `ControlHints` with:

```csharp
        const string ControlHints =
            "WASD: pan camera   Q/E: rotate   Right-drag: rotate/tilt   Wheel: zoom   V: takeover (WASD drives primary)\n" +
            "Left-click unit: select (Shift: add/remove)   Left-drag: box select   Esc: clear selection\n" +
            "Left-click ground/dummy: primary moves/attacks (paused: selected units)   Shift: queue   X: stop selected\n" +
            "Space: tactical pause";
```

(b) Add after `[SerializeField] PlayerCommandInput commandInput;`:

```csharp
        [SerializeField] PrimaryCharacter primary;
```

(c) Add after `DescribeOrders`:

```csharp
        /// <summary>One status line for the primary character, such as "Primary: Ana | Takeover ON (V) | Manual control".</summary>
        internal static string DescribePrimary(string unitName, bool takeoverOn, bool isPaused, bool hasOrders)
        {
            var mode = !takeoverOn ? "Takeover OFF (V)" : isPaused ? "Takeover ON (after pause)" : "Takeover ON (V)";
            var activity = hasOrders ? "Following orders" : takeoverOn && !isPaused ? "Manual control" : "Idle";
            return $"Primary: {unitName} | {mode} | {activity}";
        }
```

(d) Replace `OnGUI` with the following. The hints get a wider label, the primary line gets its own row, and the pause banner moves down:

```csharp
        void OnGUI()
        {
            GUI.Label(new Rect(10f, 10f, 820f, 80f), ControlHints);

            if (target != null)
            {
                var status = target.IsAlive ? $"{target.Current} / {target.Max}" : "destroyed";
                GUI.Label(new Rect(10f, 95f, 320f, 22f), $"{targetLabel}: {status}");
            }

            if (selection != null)
                GUI.Label(new Rect(10f, 115f, 320f, 22f), $"Selected: {selection.Selected.Count}");

            if (primary != null && primary.Unit != null)
            {
                var unit = primary.Unit;
                GUI.Label(new Rect(10f, 135f, 640f, 22f),
                    DescribePrimary(unit.name, primary.IsTakeoverOn, primary.IsPaused, unit.CurrentCommand != null));
            }

            DrawUnitLabels();

            if (commandInput != null && commandInput.IsDragging)
                GUI.Box(ScreenBox.ToGuiRect(commandInput.DragRect, Screen.height), GUIContent.none);

            if (tacticalPause != null && tacticalPause.IsPaused)
            {
                pausedStyle ??= new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.UpperCenter,
                    fontSize = 22,
                    fontStyle = FontStyle.Bold,
                };
                GUI.Label(new Rect(0f, 165f, Screen.width, 40f), "TACTICAL PAUSE - Space to resume", pausedStyle);
            }
        }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `Tools/run-tests.sh EditMode` → expected `total="123" passed="123"`, `EXIT=0`.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/DebugUI/PrototypeHud.cs Assets/_Project/Tests/EditMode/PrototypeHudTests.cs
git commit -m "Show the primary character's mode and activity in the HUD

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 8: Wire the Prototype scene and update the scene tests

**Files:**
- Modify: `Assets/_Project/Tests/PlayMode/PrototypeSceneTests.cs`
- Create (temporary, never committed): `Assets/_Project/Editor/TakeoverSceneBuilder.cs`
- Modify (generated): `Assets/_Project/Scenes/Prototype.unity`
- Create (generated): `Assets/_Project/Materials/PrimaryMarker.mat`

**Interfaces:**
- Consumes: the serialized field names from Tasks 3–7:
  - `PrimaryCharacter.unit`, `PrimaryCharacter.tacticalPause`;
  - `DirectControlInput.primary`, `.viewCamera`, `.moveAction`, `.takeoverAction`;
  - `PlayerCommandInput.primary`, `TacticalCameraController.primary`, `PrototypeHud.primary`;
  - `PlayerCommandInput.viewCamera`, an existing field.
- Produces: `Prototype.unity` with `PrimaryCharacter` and `DirectControlInput` on `Systems`, `FriendlyUnit_1` as the primary character with a `PrimaryMarker` child that has no collider, and every `primary` reference wired.

- [ ] **Step 1: Update the scene tests**

In `Assets/_Project/Tests/PlayMode/PrototypeSceneTests.cs`:

(a) Add `using UnityEngine.InputSystem.Controls;` after `using UnityEngine.InputSystem;`.

(b) In `Scene_ContainsWiredSquad_AndRunsWithoutErrors`, insert before `Assert.That(Camera.main, Is.Not.Null);`:

```csharp
            var primary = Object.FindFirstObjectByType<PrimaryCharacter>();
            Assert.That(primary, Is.Not.Null, "PrimaryCharacter missing");
            Assert.That(primary.Unit, Is.Not.Null, "PrimaryCharacter has no unit");
            Assert.That(primary.Unit.name, Is.EqualTo(FriendlyNames[0]));
            Assert.That(primary.HasUnit, Is.True);
            Assert.That(primary.IsTakeoverOn, Is.False, "The game starts in free mode");
            Assert.That(primary.Unit.transform.Find("PrimaryMarker"), Is.Not.Null, "Primary marker missing");
            Assert.That(Object.FindFirstObjectByType<DirectControlInput>(), Is.Not.Null, "DirectControlInput missing");
```

The existing per-friendly assertion that a unit has exactly one collider also covers the marker: it must have no collider.

(c) In `PrototypeSceneInputTests`, replace `TearDown` and add a `Tap` helper after `LeftClickAt`:

```csharp
        public override void TearDown()
        {
            Time.timeScale = 1f;
            PrototypeSceneTests.DestroySceneObjects();
            base.TearDown();
        }
```

```csharp
        IEnumerator Tap(KeyControl key)
        {
            Press(key);
            yield return null;
            Release(key);
            yield return null;
        }
```

(d) Replace `ClickUnitThenDummy_InScene_OnlyThatUnitAttacks` with this version. It pauses first and selects a companion, so it still tests select-then-order:

```csharp
        [UnityTest]
        public IEnumerator PausedClickCompanionThenDummy_InScene_OnlyThatUnitAttacks()
        {
            var mouse = InputSystem.AddDevice<Mouse>();
            var keyboard = InputSystem.AddDevice<Keyboard>();
            yield return LoadScene();
            var squad = PrototypeSceneTests.FindSquad();
            var dummy = Object.FindFirstObjectByType<Health>();

            yield return Tap(keyboard.spaceKey);
            yield return LeftClickAt(mouse, Camera.main.WorldToScreenPoint(squad[1].transform.position));
            yield return LeftClickAt(mouse, Camera.main.WorldToScreenPoint(dummy.transform.position));

            Assert.That(squad[1].CurrentCommand, Is.TypeOf<AttackCommand>());
            Assert.That(squad[0].CurrentCommand, Is.Null);
            Assert.That(squad[2].CurrentCommand, Is.Null);
        }
```

(e) Replace `BoxSelectSquadThenClickGround_InScene_AllThreeMove` with:

```csharp
        [UnityTest]
        public IEnumerator PausedBoxSelectSquadThenClickGround_InScene_AllThreeGetMoves()
        {
            var mouse = InputSystem.AddDevice<Mouse>();
            var keyboard = InputSystem.AddDevice<Keyboard>();
            yield return LoadScene();
            var squad = PrototypeSceneTests.FindSquad();

            yield return Tap(keyboard.spaceKey);
            yield return BoxSelect(mouse, squad);
            Assert.That(Object.FindFirstObjectByType<UnitSelection>().Selected, Has.Count.EqualTo(3));

            yield return LeftClickAt(mouse, Camera.main.WorldToScreenPoint(new Vector3(-6f, 0f, 0f)));

            foreach (var member in squad)
                Assert.That(member.CurrentCommand, Is.TypeOf<MoveCommand>(), member.name);
        }
```

(f) In `StopAndClearSelection_InScene_UseTheSceneBindings`, insert this line directly after `var selection = Object.FindFirstObjectByType<UnitSelection>();`, so its clicks order the selection:

```csharp
            yield return Tap(keyboard.spaceKey);   // paused: clicks order the selection
```

(g) Append three new tests at the end of `PrototypeSceneInputTests`:

```csharp
        [UnityTest]
        public IEnumerator RealTimeClickOnDummy_InScene_ThePrimaryAttacks()
        {
            var mouse = InputSystem.AddDevice<Mouse>();
            InputSystem.AddDevice<Keyboard>();
            yield return LoadScene();
            var squad = PrototypeSceneTests.FindSquad();
            var dummy = Object.FindFirstObjectByType<Health>();

            yield return LeftClickAt(mouse, Camera.main.WorldToScreenPoint(dummy.transform.position));

            Assert.That(squad[0].CurrentCommand, Is.TypeOf<AttackCommand>(), "The primary character did not attack");
            Assert.That(((AttackCommand)squad[0].CurrentCommand).Target, Is.SameAs(dummy));
            Assert.That(squad[1].CurrentCommand, Is.Null);
            Assert.That(squad[2].CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator VThenW_InScene_DrivesThePrimary_AndTheCameraFollows()
        {
            var keyboard = InputSystem.AddDevice<Keyboard>();
            yield return LoadScene();
            var primary = PrototypeSceneTests.FindSquad()[0];
            var rig = Object.FindFirstObjectByType<TacticalCameraController>();
            var start = primary.transform.position;

            yield return Tap(keyboard.vKey);
            Press(keyboard.wKey);
            yield return TestWorld.WaitUntil(() => TestWorld.HorizontalDistance(primary.transform.position, start) > 1.5f, 3f);
            Release(keyboard.wKey);
            yield return new WaitForSecondsRealtime(0.6f);

            Assert.That(TestWorld.HorizontalDistance(primary.transform.position, start), Is.GreaterThan(1.5f),
                "V then W did not drive the primary character");
            Assert.That(TestWorld.HorizontalDistance(rig.transform.position, primary.transform.position), Is.LessThan(0.5f),
                "The camera did not follow the primary character");
        }

        [UnityTest]
        public IEnumerator PausedSquadOrder_ThenTakeover_OnlyThePrimaryDropsItsOrders()
        {
            var mouse = InputSystem.AddDevice<Mouse>();
            var keyboard = InputSystem.AddDevice<Keyboard>();
            yield return LoadScene();
            var squad = PrototypeSceneTests.FindSquad();

            yield return Tap(keyboard.spaceKey);
            yield return BoxSelect(mouse, squad);
            yield return LeftClickAt(mouse, Camera.main.WorldToScreenPoint(new Vector3(-6f, 0f, 0f)));
            foreach (var member in squad)
                Assert.That(member.CurrentCommand, Is.TypeOf<MoveCommand>(), $"{member.name}: precondition");
            yield return Tap(keyboard.vKey);
            yield return Tap(keyboard.spaceKey);   // resume: the plan starts running

            Press(keyboard.wKey);
            yield return new WaitForSecondsRealtime(0.3f);
            Release(keyboard.wKey);
            yield return null;

            Assert.That(squad[0].CurrentCommand, Is.Null, "Manual input must take the primary character back");
            Assert.That(squad[1].CurrentCommand, Is.TypeOf<MoveCommand>(), "Takeover cancelled a companion's order");
            Assert.That(squad[2].CurrentCommand, Is.TypeOf<MoveCommand>(), "Takeover cancelled a companion's order");
        }
```

- [ ] **Step 2: Run the scene tests to verify the new ones fail**

Run: `Tools/run-tests.sh PlayMode Blackglass.Tests.PrototypeSceneTests` → expected `EXIT=2`. `Scene_ContainsWiredSquad_AndRunsWithoutErrors` fails with "PrimaryCharacter missing".
Run: `Tools/run-tests.sh PlayMode Blackglass.Tests.PrototypeSceneInputTests` → expected `EXIT=2`. `RealTimeClickOnDummy_InScene_ThePrimaryAttacks`, `VThenW_InScene_DrivesThePrimary_AndTheCameraFollows` and `PausedSquadOrder_ThenTakeover_OnlyThePrimaryDropsItsOrders` fail; the three paused tests pass.

- [ ] **Step 3: Create the temporary scene builder**

Create `Assets/_Project/Editor/TakeoverSceneBuilder.cs`:

```csharp
// TEMPORARY: wires the primary character and takeover into Prototype.unity, then this file is deleted (never committed).
using System;
using System.Linq;
using Blackglass;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

public static class TakeoverSceneBuilder
{
    const string ScenePath = "Assets/_Project/Scenes/Prototype.unity";
    const string ControlsPath = "Assets/_Project/Input/BlackglassControls.inputactions";

    public static void Build()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        var systems = Require(GameObject.Find("Systems"), "Systems");
        var primaryObject = Require(GameObject.Find("FriendlyUnit_1"), "FriendlyUnit_1");
        var primaryUnit = Require(primaryObject.GetComponent<CommandableUnit>(), "FriendlyUnit_1's CommandableUnit");
        var pause = Require(systems.GetComponent<TacticalPause>(), "TacticalPause on Systems");
        var commandInput = Require(systems.GetComponent<PlayerCommandInput>(), "PlayerCommandInput on Systems");
        var hud = Require(systems.GetComponent<PrototypeHud>(), "PrototypeHud on Systems");
        var cameraController = Require(Object.FindFirstObjectByType<TacticalCameraController>(), "TacticalCameraController");
        var viewCamera = Require((Camera)new SerializedObject(commandInput).FindProperty("viewCamera").objectReferenceValue,
            "PlayerCommandInput.viewCamera");
        if (systems.GetComponent<PrimaryCharacter>() != null)
            throw new Exception("Systems already has a PrimaryCharacter; the builder has already run.");

        var primary = systems.AddComponent<PrimaryCharacter>();
        SetReference(primary, "unit", primaryUnit);
        SetReference(primary, "tacticalPause", pause);

        var direct = systems.AddComponent<DirectControlInput>();
        SetReference(direct, "primary", primary);
        SetReference(direct, "viewCamera", viewCamera);
        SetReference(direct, "moveAction", ActionReference("Character/Move"));
        SetReference(direct, "takeoverAction", ActionReference("Character/Takeover"));

        SetReference(commandInput, "primary", primary);
        SetReference(cameraController, "primary", primary);
        SetReference(hud, "primary", primary);

        CreateMarker(primaryObject.transform);

        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene))
            throw new Exception("Failed to save " + ScenePath);
        AssetDatabase.SaveAssets();
        Debug.Log($"[TakeoverSceneBuilder] Wired the primary character into {ScenePath}");
    }

    // A small orange diamond floating above the primary character's head. No collider, so it never blocks clicks.
    static void CreateMarker(Transform unit)
    {
        var material = CreateUnlitMaterial("PrimaryMarker", new Color(1f, 0.5f, 0.1f));
        var marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
        marker.name = "PrimaryMarker";
        Object.DestroyImmediate(marker.GetComponent<Collider>());
        marker.transform.SetParent(unit, false);
        marker.transform.localPosition = new Vector3(0f, 1.6f, 0f);
        marker.transform.localRotation = Quaternion.Euler(45f, 0f, 45f);
        marker.transform.localScale = new Vector3(0.35f, 0.35f, 0.35f);
        var renderer = marker.GetComponent<Renderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
    }

    static Material CreateUnlitMaterial(string name, Color color)
    {
        var path = $"Assets/_Project/Materials/{name}.mat";
        AssetDatabase.DeleteAsset(path);
        var shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null)
            throw new Exception("URP Unlit shader not found");
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
"/c/Program Files/Unity/Hub/Editor/6000.3.25f1/Editor/Unity.exe" -batchmode -quit -projectPath "$(pwd -W)" -executeMethod TakeoverSceneBuilder.Build -logFile "$(pwd -W)/Logs/BuildTakeoverScene.log"; echo "EXIT=$?"
grep -E '\[TakeoverSceneBuilder\]|error CS|Exception' Logs/BuildTakeoverScene.log | head -20
grep -c "PrimaryMarker" Assets/_Project/Scenes/Prototype.unity
```

**Expected:**
- `EXIT=0`.
- The log contains `[TakeoverSceneBuilder] Wired the primary character into Assets/_Project/Scenes/Prototype.unity` and no exceptions.
- The scene mentions `PrimaryMarker` at least once.

Then delete the builder:

```bash
rm -r Assets/_Project/Editor Assets/_Project/Editor.meta
```

- [ ] **Step 5: Run all tests to verify they pass**

Run: `Tools/run-tests.sh EditMode` → expected `total="123" passed="123"`, `EXIT=0`.
Run: `Tools/run-tests.sh PlayMode` → expected `total="107" passed="107"`, `EXIT=0`.

This run also recompiles without the deleted builder, which confirms that nothing depends on it.

- [ ] **Step 6: Commit**

```bash
git add Assets/_Project/Scenes/Prototype.unity Assets/_Project/Materials/PrimaryMarker.mat Assets/_Project/Materials/PrimaryMarker.mat.meta Assets/_Project/Tests/PlayMode/PrototypeSceneTests.cs
git status --short   # must NOT list Assets/_Project/Editor
git commit -m "Make FriendlyUnit_1 the primary character in the prototype scene

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 9: Decision records and final verification

**Files:**
- Modify: `Docs/Decisions.md`

- [ ] **Step 1: Update decision 006**

In `Docs/Decisions.md`, in section `## 006 — Unit commands are data; units execute them`, add this bullet after the `- **Groups:** ...` bullet:

```markdown
- **Direct control (Phase 3, 2026-10-04):** `CommandableUnit` also takes a held **move intent** (`SetMoveIntent(direction)`), which is not a command. While it is non-zero and simulation time runs, the unit clears all of its orders (current and pending, as Stop does) and steers with `UnitMover.Steer`. Orders and direct control share `UnitMover` and `UnitAttacker`; nothing else drives them. An order issued while the intent is held is accepted, then dropped on the next simulation frame (keys win while held).
```

- [ ] **Step 2: Update decision 008**

In section `## 008 — Input mapping and click vs drag`:

(a) Replace the `- **Decided:** ...` bullet and its two sub-bullets (Camera, Commands) with:

```markdown
- **Decided:** Project-wide actions live in `Assets/_Project/Input/BlackglassControls.inputactions`, with three maps:
  - **Camera:** Pan (WASD), Rotate (Q/E), RotateDrag (right button), PointerPosition, Zoom (wheel).
  - **Commands:** Command (left button), PointerPosition, TogglePause (Space), Modifier (Shift), Stop (X), ClearSelection (Esc).
  - **Character** (Phase 3, 2026-10-04): Move (WASD), Takeover (V).
- **WASD is shared** by `Camera/Pan` and `Character/Move`. The mode decides which one acts: while the primary character is being driven (takeover on, not paused) the camera ignores Pan, and otherwise `DirectControlInput` ignores Move.
```

(b) Replace the line `  - anything else orders the selected units: Attack on a living `Health`, otherwise Move. Shift queues the order instead of replacing.` with:

```markdown
  - anything else is an order: Attack on a living `Health`, otherwise Move. Shift queues it instead of replacing. **In real time the order goes to the primary character; while paused (or with no active primary character) it goes to the selected units** (Phase 3, 2026-10-04).
```

- [ ] **Step 3: Add decision 011**

Append to the end of `Docs/Decisions.md`:

```markdown

## 011 — Primary character and takeover mode

- **Decided (Phase 3, 2026-10-04):** One friendly unit is the **primary character** (`FriendlyUnit_1` in `Prototype.unity`). `PrimaryCharacter` (on `Systems`) holds which unit it is and whether **takeover mode** is on. Like `UnitSelection`, it holds state only.
  - **Free mode** (default): WASD pans the camera. Real-time ground and enemy clicks order the primary character; Shift queues.
  - **Takeover mode** (V toggles): WASD drives the primary character (camera-relative, through `DirectControlInput` → `CommandableUnit.SetMoveIntent`), and the camera follows it on unscaled time. V works while paused and takes effect on resume.
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
  - Switching the primary character at runtime, and what happens when it dies, are not designed yet; friendly units have no `Health`.
  - If "keys win while held" feels wrong in play, the alternative is that a click suspends the keys until they are released.
```

- [ ] **Step 4: Final verification**

Run both suites and confirm the totals:

```bash
Tools/run-tests.sh EditMode   # expected total="123" passed="123" EXIT=0
Tools/run-tests.sh PlayMode   # expected total="107" passed="107" EXIT=0
grep -c "error\|Exception" Logs/TestRun-PlayMode.log   # review any hits; expected only test-framework noise, no runtime exceptions
git status --short            # only Docs/Decisions.md modified
```

- [ ] **Step 5: Commit**

```bash
git add Docs/Decisions.md
git commit -m "Record primary character, takeover and input sharing decisions

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```
