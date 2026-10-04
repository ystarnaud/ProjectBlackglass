# Tactical Squad Control (Phase 2) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extend the Phase 1 prototype so the player can select several friendly units, give them move/attack/stop orders, queue orders per unit (also while tactically paused) and watch them run in order after resuming.

**Architecture:**
- Commands stay immutable data. `CommandableUnit` stays the single entry point for orders and now owns a plain C# `CommandQueue` (current + pending); `Issue(command, IssueMode.Replace|Append)`.
- `GroupOrders` (static) spreads one command over several units (hex-lattice move offsets). `UnitSelection` (component) holds the selection and the roster; `SelectableUnit` marks friendly units.
- `PlayerCommandInput` decides what the left button means (select, command, or box-select) and never touches movement or combat. Debug views only read state.

**Tech Stack:** Unity 6000.3.25f1 (Unity 6.3 LTS), URP 17.3.0, Input System 1.20.0, AI Navigation 2.0.14, Unity Test Framework 1.6.0 (NUnit), `InputTestFixture`.

**Spec:** `Docs/superpowers/specs/2026-10-04-tactical-squad-control-design.md`

## Global Constraints

- **Unity Editor:** exactly 6000.3.25f1 at `C:\Program Files\Unity\Hub\Editor\6000.3.25f1\Editor\Unity.exe`. **The Editor must be closed during every batch-mode run** (one instance per project). If a run prints "The Unity Editor has this project open", stop and ask the owner to close it.
- **Code location:** runtime code in `Assets/_Project/Scripts/` (assembly `Blackglass`, namespace `Blackglass`); tests in `Assets/_Project/Tests/EditMode/` and `Assets/_Project/Tests/PlayMode/` (namespace `Blackglass.Tests`). No new assemblies, no new assembly references.
- **Commands are data** (decision 006): no `Begin/Tick/Cancel` methods on command classes. Orders go through `CommandableUnit.Issue` or `GroupOrders.Issue` only.
- **Pause ownership:** only `TacticalPause` writes `Time.timeScale` in game code. Tests may reset it in `TearDown`. Pause only freezes simulation; issuing rules are identical paused or not (plain = Replace, Shift = Append).
- **Time:** unit orders, movement and combat on scaled time (`CommandableUnit.Update` returns early while `Time.deltaTime <= 0`). Input, selection, camera, HUD and debug views never read `Time.deltaTime`.
- **Wiring:** serialized Inspector references; no singletons, no `Find*`/`Camera.main` in game code, no static mutable state. Tests wire components through `internal Initialize(...)`.
- **Input:** Input System only, through `InputActionReference` fields. Left button = select/command/box-select; right button stays camera-only; Shift = `Commands/Modifier`; X = `Commands/Stop`; Esc = `Commands/ClearSelection`; Space = `Commands/TogglePause`.
- **Visuals:** debug-only. IMGUI `OnGUI` for text; primitives with placeholder URP Unlit materials. Debug objects (selection ring, markers, line) have **no colliders**.
- **Scope:** no weapons, formations, control groups, factions, AI, Interact, production UI, packages. Weapons are deferred to their own phase (owner decision).
- **Git:** local branch `prototype/tactical-squad` (already created; the spec is committed on it). Never push or merge. Commit after each task; messages are sentence-case, imperative, and end with the line `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Commit Unity's `.meta` files with their assets.
- **Test runs:** always via `Tools/run-tests.sh` (it restricts runs to our assemblies). Use a 10-minute timeout for PlayMode runs.

### Running tests

```bash
Tools/run-tests.sh EditMode                                   # all EditMode tests
Tools/run-tests.sh PlayMode                                   # all PlayMode tests
Tools/run-tests.sh EditMode Blackglass.Tests.CommandQueueTests   # one fixture (any -testFilter expression)
```

The script prints any `error CS` lines, the summary (`result= total= passed= failed=`), failed test names with messages, and `EXIT=<code>`. Exit codes: **0** all passed; **2** some failed; **3** could not run (usually a compile error — no results file; the `error CS` lines say why). A "red" step usually means **EXIT=3 with `error CS0246`/`CS1061`** because the new types don't exist yet.

Baseline before this plan: EditMode 36/36, PlayMode 36/36.

## Deviations from the spec (decided while planning; the spec is updated to match)

1. **No `CommandsChanged` event.** `CommandQueueView` and the HUD read `CurrentCommand`/`PendingCommands` every frame; the line must be redrawn every frame anyway as the unit walks, so an event would have no consumer.
2. **No `UnitDebugLabel` component.** `PrototypeHud` draws the order label above every roster unit. It already holds the camera and selection; a per-unit component would need a camera reference per instance or a `Camera.main` lookup.
3. **`UnitMover.HasArrived`** uses the distance to `NavMeshAgent.pathEndPosition` plus a "no move requested this frame" guard, not `remainingDistance` (Phase 1 deviation 3 found `remainingDistance` unreliable on the frame a destination is set).
4. **`GroupMoveOffsets`** is a hexagonal lattice: ring corners at `r·spacing`, edge points between them, so neighbours are exactly `spacing` apart.
5. **`GroupOrders.Issue`** returns the number of units that accepted the order and takes an optional `spacing` (default `GroupOrders.DefaultSpacing = 1.5f`).
6. **`PlayerCommandInput.DragRect`** is in screen pixels (origin bottom-left); `ScreenBox.ToGuiRect` converts it for IMGUI.
7. **Selection details:** `SelectableUnit` raises an internal `Disabled` event from `OnDisable` so `UnitSelection` can drop it; units are selectable when `enabled && gameObject.activeInHierarchy`; `Select(null)`, `Toggle(null)`, `AddToRoster(null)` and null collections throw.
8. A rejected **appended** Move logs a warning (`cannot queue a move`), matching the warning a rejected Replace move already logs.

## Review Focus

Five inputs the spec implies but that are easy to break. Each has a test in its owning task.

1. **A selected unit is disabled or destroyed** — it must drop out of the selection (ring off, count updated) and later orders must go to the remaining units without exceptions. Pinned in Task 4: `DisabledUnit_IsDroppedFromTheSelection`, `DestroyedUnit_IsDroppedFromTheSelection`.
2. **Clicking on a selection ring, move marker or queue line** must click "through" to the ground or unit beneath. Pinned in Task 5 (`ShowsALineThroughEveryOrder_AndMarkersAtMoveDestinations` asserts markers have no collider) and Task 6 (scene test asserts each friendly has exactly one collider).
3. **A group move next to the map edge or a wall** where an offset slot is off the NavMesh — that unit must fall back to the clicked point, not silently stay put. Pinned in Task 3: `OffsetPointOffTheNavMesh_FallsBackToTheClickedPoint`.
4. **Re-ordering an attack on the current target while orders are queued** (plain click) — keeps chasing without a restart and drops the queued orders. Pinned in Task 2: `Replace_AttackOnCurrentTarget_KeepsItAndDropsPending`.
5. **Shift-clicking a friendly while units are selected and moving** — must only toggle selection, never issue or alter orders. Pinned in Task 6: `ShiftClickFriendly_WithUnitsSelected_NeverIssuesOrders`.

---

## File Structure

```
Assets/_Project/
├── Scripts/
│   ├── Commands/UnitCommands.cs           + StopCommand, IssueMode (Task 1)
│   ├── Commands/CommandQueue.cs           current + pending orders, pure C# (Task 1)
│   ├── Units/UnitMover.cs                 + CanMoveTo, HasArrived fix (Task 2)
│   ├── Units/CommandableUnit.cs           owns a CommandQueue; Issue(command, mode) (Task 2)
│   ├── Commands/GroupMoveOffsets.cs       hex-lattice offsets, pure (Task 3)
│   ├── Commands/GroupOrders.cs            one command -> per-unit orders (Task 3)
│   ├── Selection/SelectableUnit.cs        marks a friendly unit, IsSelected (Task 4)
│   ├── Selection/UnitSelection.cs         roster + selection state (Task 4)
│   ├── Selection/ScreenBox.cs             box maths, pure (Task 4)
│   ├── Selection/SelectionIndicator.cs    shows the ring (Task 4)
│   ├── DebugUI/CommandQueueView.cs        order line + move markers (Task 5)
│   ├── DebugUI/PrototypeHud.cs            hints, labels, drag box (Tasks 5 and 6)
│   ├── Controls/InputActionUtility.cs     + IsPressed (Task 6)
│   └── Controls/PlayerCommandInput.cs     select / command / box / stop / clear (Task 6)
├── Tests/EditMode/  CommandQueueTests, CommandableUnitTests (extended), GroupMoveOffsetsTests,
│                    GroupOrdersTests, UnitSelectionTests, ScreenBoxTests, PrototypeHudTests
├── Tests/PlayMode/  CommandableUnitQueuePlayModeTests, GroupOrdersPlayModeTests, UnitSelectionPlayModeTests,
│                    CommandQueueViewTests, PlayerCommandInputTests (rewritten), PrototypeSceneTests (updated),
│                    TestSupport/TestWorld.cs (+ CreateFriendly)
├── Input/BlackglassControls.inputactions  + Modifier, Stop, ClearSelection (Task 6)
├── Materials/{SelectionRing,QueueLine,MoveMarker}.mat   (Task 6, generated)
├── Prefabs/FriendlyUnit.prefab            (Task 6, generated)
└── Scenes/Prototype.unity                 three friendly units, UnitSelection (Task 6, generated)
Docs/Decisions.md                          006/008 updated, 010 added, 003 note (Task 7)
```

`Assets/_Project/Editor/SquadSceneBuilder.cs` is **temporary**: created and deleted within Task 6, never committed.

---

### Task 1: `StopCommand`, `IssueMode` and `CommandQueue`

**Files:**
- Modify: `Assets/_Project/Scripts/Commands/UnitCommands.cs`
- Create: `Assets/_Project/Scripts/Commands/CommandQueue.cs`
- Test: `Assets/_Project/Tests/EditMode/CommandQueueTests.cs`

**Interfaces:**
- Consumes: `UnitCommand`, `MoveCommand`, `AttackCommand` (Phase 1).
- Produces:
  - `public sealed class StopCommand : UnitCommand` (no members).
  - `public enum IssueMode { Replace, Append }`.
  - `public sealed class CommandQueue` with `UnitCommand Current { get; }`, `IReadOnlyList<UnitCommand> Pending { get; }`, `void Replace(UnitCommand)`, `bool Append(UnitCommand)` (true when it became current), `void Clear()`, `UnitCommand Advance()` (returns the new current or null). Null commands throw `ArgumentNullException`.

- [ ] **Step 1: Write the failing tests**

`Assets/_Project/Tests/EditMode/CommandQueueTests.cs`:

```csharp
using System;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class CommandQueueTests
    {
        CommandQueue queue;
        MoveCommand first;
        MoveCommand second;
        MoveCommand third;

        [SetUp]
        public void SetUp()
        {
            queue = new CommandQueue();
            first = new MoveCommand(new Vector3(1f, 0f, 0f));
            second = new MoveCommand(new Vector3(2f, 0f, 0f));
            third = new MoveCommand(new Vector3(3f, 0f, 0f));
        }

        [Test]
        public void StartsIdle()
        {
            Assert.That(queue.Current, Is.Null);
            Assert.That(queue.Pending, Is.Empty);
        }

        [Test]
        public void Append_WhileIdle_BecomesCurrent()
        {
            Assert.That(queue.Append(first), Is.True);
            Assert.That(queue.Current, Is.SameAs(first));
            Assert.That(queue.Pending, Is.Empty);
        }

        [Test]
        public void Append_WhileBusy_WaitsInOrder()
        {
            queue.Append(first);
            Assert.That(queue.Append(second), Is.False);
            Assert.That(queue.Append(third), Is.False);
            Assert.That(queue.Current, Is.SameAs(first));
            Assert.That(queue.Pending, Is.EqualTo(new UnitCommand[] { second, third }));
        }

        [Test]
        public void Replace_DropsCurrentAndPending()
        {
            queue.Append(first);
            queue.Append(second);
            queue.Replace(third);
            Assert.That(queue.Current, Is.SameAs(third));
            Assert.That(queue.Pending, Is.Empty);
        }

        [Test]
        public void Advance_PromotesPendingInOrder_ThenGoesIdle()
        {
            queue.Append(first);
            queue.Append(second);
            queue.Append(third);

            Assert.That(queue.Advance(), Is.SameAs(second));
            Assert.That(queue.Pending, Is.EqualTo(new UnitCommand[] { third }));
            Assert.That(queue.Advance(), Is.SameAs(third));
            Assert.That(queue.Pending, Is.Empty);
            Assert.That(queue.Advance(), Is.Null);
            Assert.That(queue.Current, Is.Null);
        }

        [Test]
        public void Advance_WhenIdle_StaysIdle()
        {
            Assert.That(queue.Advance(), Is.Null);
            Assert.That(queue.Current, Is.Null);
        }

        [Test]
        public void Clear_EmptiesEverything()
        {
            queue.Append(first);
            queue.Append(second);
            queue.Clear();
            Assert.That(queue.Current, Is.Null);
            Assert.That(queue.Pending, Is.Empty);
        }

        [Test]
        public void NullCommands_AreRejected()
        {
            Assert.Throws<ArgumentNullException>(() => queue.Replace(null));
            Assert.Throws<ArgumentNullException>(() => queue.Append(null));
            Assert.That(queue.Current, Is.Null);
        }

        [Test]
        public void StopCommand_IsAUnitCommand()
        {
            Assert.That(new StopCommand(), Is.InstanceOf<UnitCommand>());
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `Tools/run-tests.sh EditMode Blackglass.Tests.CommandQueueTests`
Expected: `error CS0246: The type or namespace name 'CommandQueue' could not be found` (and `StopCommand`), `EXIT=3`.

- [ ] **Step 3: Add `StopCommand` and `IssueMode`**

Replace the whole of `Assets/_Project/Scripts/Commands/UnitCommands.cs` with:

```csharp
using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// An order for a unit. Commands are plain data: whoever creates one (player input, groups, AI or scripts)
    /// hands it to CommandableUnit.Issue, and the unit decides how to carry it out. Future commands (Interact)
    /// are new subclasses.
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

    /// <summary>Halts the unit and clears all of its orders. Never queued.</summary>
    public sealed class StopCommand : UnitCommand { }

    /// <summary>How a new order combines with the orders a unit already has.</summary>
    public enum IssueMode
    {
        /// <summary>Drop the current and pending orders and start this one now.</summary>
        Replace,
        /// <summary>Run this order after the pending ones (immediately if the unit is idle).</summary>
        Append,
    }
}
```

- [ ] **Step 4: Write `CommandQueue`**

`Assets/_Project/Scripts/Commands/CommandQueue.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace Blackglass
{
    /// <summary>
    /// A unit's orders: the one being carried out plus the ones waiting behind it, in order.
    /// Only records order; CommandableUnit decides when commands start and finish.
    /// </summary>
    public sealed class CommandQueue
    {
        readonly List<UnitCommand> pending = new List<UnitCommand>();

        /// <summary>The order being carried out, or null when idle. Never null while orders are pending.</summary>
        public UnitCommand Current { get; private set; }

        /// <summary>Orders waiting behind the current one, in the order they will run.</summary>
        public IReadOnlyList<UnitCommand> Pending => pending;

        /// <summary>Drops all orders; the command becomes current.</summary>
        public void Replace(UnitCommand command)
        {
            Current = command ?? throw new ArgumentNullException(nameof(command));
            pending.Clear();
        }

        /// <summary>Queues the command last. Returns true if the queue was idle, so it became current.</summary>
        public bool Append(UnitCommand command)
        {
            if (command == null)
                throw new ArgumentNullException(nameof(command));
            if (Current == null)
            {
                Current = command;
                return true;
            }
            pending.Add(command);
            return false;
        }

        public void Clear()
        {
            Current = null;
            pending.Clear();
        }

        /// <summary>Drops the current order and promotes the first pending one. Returns the new current order, or null.</summary>
        public UnitCommand Advance()
        {
            if (pending.Count == 0)
            {
                Current = null;
                return null;
            }
            Current = pending[0];
            pending.RemoveAt(0);
            return Current;
        }
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `Tools/run-tests.sh EditMode`
Expected: `result="Passed" total="45" passed="45"`, `EXIT=0` (36 existing + 9 new).

- [ ] **Step 6: Commit**

```bash
git add Assets/_Project/Scripts/Commands Assets/_Project/Tests/EditMode
git status --short   # expect the new .cs files plus the .meta files Unity generated during the test run
git commit -m "Add Stop command, issue modes and a per-unit command queue

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Queued orders in `CommandableUnit`, and the `HasArrived` fix

**Files:**
- Modify: `Assets/_Project/Scripts/Units/UnitMover.cs`
- Modify: `Assets/_Project/Scripts/Units/CommandableUnit.cs` (full rewrite shown below; Phase 1 behaviour preserved)
- Modify: `Assets/_Project/Tests/EditMode/CommandableUnitTests.cs` (add tests)
- Create: `Assets/_Project/Tests/PlayMode/CommandableUnitQueuePlayModeTests.cs`

**Interfaces:**
- Consumes: `CommandQueue`, `StopCommand`, `IssueMode` (Task 1).
- Produces:
  - `CommandableUnit.Issue(UnitCommand command, IssueMode mode = IssueMode.Replace) : bool`.
  - `CommandableUnit.CurrentCommand : UnitCommand` (unchanged name), `CommandableUnit.PendingCommands : IReadOnlyList<UnitCommand>`.
  - `UnitMover.CanMoveTo(Vector3 point) : bool` (silent, no side effects).

- [ ] **Step 1: Add the failing EditMode tests**

In `Assets/_Project/Tests/EditMode/CommandableUnitTests.cs`, add a second target to the fixture and the tests below. Change the fields and `SetUp`/`TearDown` to:

```csharp
        GameObject unitHost;
        GameObject targetHost;
        GameObject otherTargetHost;
        CommandableUnit unit;
        Health target;
        Health otherTarget;

        [SetUp]
        public void SetUp()
        {
            unitHost = new GameObject("Unit");
            unit = unitHost.AddComponent<CommandableUnit>();
            targetHost = new GameObject("Target");
            target = targetHost.AddComponent<Health>();
            otherTargetHost = new GameObject("OtherTarget");
            otherTarget = otherTargetHost.AddComponent<Health>();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(unitHost);
            Object.DestroyImmediate(targetHost);
            Object.DestroyImmediate(otherTargetHost);
        }
```

Then add these tests inside the class (attacks are used because no NavMesh exists in EditMode):

```csharp
        [Test]
        public void Issue_UnknownMode_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => unit.Issue(new AttackCommand(target), (IssueMode)99));
            Assert.That(unit.CurrentCommand, Is.Null);
        }

        [Test]
        public void Append_WhileIdle_BecomesCurrent()
        {
            var attack = new AttackCommand(target);
            Assert.That(unit.Issue(attack, IssueMode.Append), Is.True);
            Assert.That(unit.CurrentCommand, Is.SameAs(attack));
            Assert.That(unit.PendingCommands, Is.Empty);
        }

        [Test]
        public void Append_WhileBusy_QueuesBehindCurrent()
        {
            var first = new AttackCommand(target);
            var second = new AttackCommand(otherTarget);
            unit.Issue(first);
            Assert.That(unit.Issue(second, IssueMode.Append), Is.True);
            Assert.That(unit.CurrentCommand, Is.SameAs(first));
            Assert.That(unit.PendingCommands, Is.EqualTo(new UnitCommand[] { second }));
        }

        [Test]
        public void Append_DeadTarget_IsRejectedAndQueueUnchanged()
        {
            var first = new AttackCommand(target);
            unit.Issue(first);
            otherTarget.TakeDamage(otherTarget.Max);
            Assert.That(unit.Issue(new AttackCommand(otherTarget), IssueMode.Append), Is.False);
            Assert.That(unit.CurrentCommand, Is.SameAs(first));
            Assert.That(unit.PendingCommands, Is.Empty);
        }

        [Test]
        public void Replace_DropsPendingOrders()
        {
            unit.Issue(new AttackCommand(target));
            unit.Issue(new AttackCommand(otherTarget), IssueMode.Append);
            var replacement = new AttackCommand(otherTarget);
            Assert.That(unit.Issue(replacement), Is.True);
            Assert.That(unit.CurrentCommand, Is.SameAs(replacement));
            Assert.That(unit.PendingCommands, Is.Empty);
        }

        [Test]
        public void Replace_AttackOnCurrentTarget_KeepsItAndDropsPending()
        {
            unit.Issue(new AttackCommand(target));
            unit.Issue(new AttackCommand(otherTarget), IssueMode.Append);
            var again = new AttackCommand(target);
            Assert.That(unit.Issue(again), Is.True);
            Assert.That(unit.CurrentCommand, Is.SameAs(again));
            Assert.That(unit.PendingCommands, Is.Empty);
        }

        [Test]
        public void Replace_RejectedCommand_KeepsCurrentAndPending()
        {
            var first = new AttackCommand(target);
            var second = new AttackCommand(otherTarget);
            unit.Issue(first);
            unit.Issue(second, IssueMode.Append);
            var deadHost = new GameObject("Dead");
            var dead = deadHost.AddComponent<Health>();
            dead.TakeDamage(dead.Max);

            Assert.That(unit.Issue(new AttackCommand(dead)), Is.False);
            Assert.That(unit.CurrentCommand, Is.SameAs(first));
            Assert.That(unit.PendingCommands, Is.EqualTo(new UnitCommand[] { second }));
            Object.DestroyImmediate(deadHost);
        }

        [TestCase(IssueMode.Replace)]
        [TestCase(IssueMode.Append)]
        public void Stop_ClearsCurrentAndPending(IssueMode mode)
        {
            unit.Issue(new AttackCommand(target));
            unit.Issue(new AttackCommand(otherTarget), IssueMode.Append);
            Assert.That(unit.Issue(new StopCommand(), mode), Is.True);
            Assert.That(unit.CurrentCommand, Is.Null);
            Assert.That(unit.PendingCommands, Is.Empty);
        }

        [Test]
        public void Stop_WhileIdle_IsAccepted()
        {
            Assert.That(unit.Issue(new StopCommand()), Is.True);
            Assert.That(unit.CurrentCommand, Is.Null);
        }
```

- [ ] **Step 2: Write the failing PlayMode tests**

`Assets/_Project/Tests/PlayMode/CommandableUnitQueuePlayModeTests.cs`:

```csharp
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CommandableUnitQueuePlayModeTests
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
        public IEnumerator AppendedMoves_RunInOrder()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(-6f, 0f, -6f));
            yield return null;
            var a = new Vector3(-6f, 0f, 6f);
            var b = new Vector3(6f, 0f, 6f);

            Assert.That(unit.Issue(new MoveCommand(a)), Is.True);
            Assert.That(unit.Issue(new MoveCommand(b), IssueMode.Append), Is.True);
            Assert.That(unit.PendingCommands.Count, Is.EqualTo(1));

            yield return TestWorld.WaitUntil(() => unit.PendingCommands.Count == 0, 10f);
            Assert.That(unit.PendingCommands, Is.Empty, "First move never finished");
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, a), Is.LessThan(0.5f), "Second move started before the unit reached the first destination");

            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 10f);
            Assert.That(unit.CurrentCommand, Is.Null, "Second move never finished");
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, b), Is.LessThan(0.3f));
        }

        [UnityTest]
        public IEnumerator OrdersQueuedWhilePaused_WaitThenRunInOrder()
        {
            world.CreateEnvironment();
            var pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            var unit = world.CreateUnit(new Vector3(-6f, 0f, -6f));
            yield return null;
            var start = unit.transform.position;
            var a = new MoveCommand(new Vector3(-6f, 0f, 4f));
            var b = new MoveCommand(new Vector3(4f, 0f, 4f));

            pause.Pause();
            Assert.That(unit.Issue(a), Is.True);
            Assert.That(unit.Issue(b, IssueMode.Append), Is.True);
            yield return new WaitForSecondsRealtime(1f);

            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, start), Is.LessThan(0.01f), "Unit moved while paused");
            Assert.That(unit.CurrentCommand, Is.SameAs(a));
            Assert.That(unit.PendingCommands, Is.EqualTo(new UnitCommand[] { b }));

            pause.Resume();
            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == b, 10f);
            Assert.That(unit.CurrentCommand, Is.SameAs(b), "First move never finished");
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, a.Destination), Is.LessThan(0.5f));

            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 10f);
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, b.Destination), Is.LessThan(0.3f));
        }

        [UnityTest]
        public IEnumerator Stop_ClearsOrdersAndHaltsTheUnit()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(-6f, 0f, -6f));
            yield return null;
            unit.Issue(new MoveCommand(new Vector3(6f, 0f, 6f)));
            unit.Issue(new MoveCommand(new Vector3(-6f, 0f, 6f)), IssueMode.Append);
            yield return new WaitForSeconds(0.5f);

            Assert.That(unit.Issue(new StopCommand()), Is.True);
            Assert.That(unit.CurrentCommand, Is.Null);
            Assert.That(unit.PendingCommands, Is.Empty);

            var stoppedAt = unit.transform.position;
            yield return new WaitForSeconds(0.5f);
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, stoppedAt), Is.LessThan(0.5f));
        }

        [UnityTest]
        public IEnumerator QueuedAttackThenMove_KillsTargetThenWalks()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(0f, 0f, -4f));
            var dummy = world.CreateDummy(new Vector3(0f, 0f, 4f));
            yield return null;
            var destination = new Vector3(-5f, 0f, -5f);

            Assert.That(unit.Issue(new AttackCommand(dummy)), Is.True);
            Assert.That(unit.Issue(new MoveCommand(destination), IssueMode.Append), Is.True);

            yield return TestWorld.WaitUntil(() => !dummy.IsAlive && unit.CurrentCommand == null, 20f);

            Assert.That(dummy.IsAlive, Is.False);
            Assert.That(unit.CurrentCommand, Is.Null, "Queued move never finished");
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, destination), Is.LessThan(0.3f));
        }

        [UnityTest]
        public IEnumerator QueuedAttack_OnTargetThatDiedBeforeItsTurn_IsSkipped()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(-6f, 0f, -6f));
            var dummy = world.CreateDummy(new Vector3(6f, 0f, 6f));
            yield return null;
            var last = new Vector3(6f, 0f, -6f);

            unit.Issue(new MoveCommand(new Vector3(-6f, 0f, 0f)));
            Assert.That(unit.Issue(new AttackCommand(dummy), IssueMode.Append), Is.True);
            unit.Issue(new MoveCommand(last), IssueMode.Append);
            dummy.TakeDamage(1000);

            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 20f);

            Assert.That(unit.CurrentCommand, Is.Null, "Queue stalled on the dead target");
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, last), Is.LessThan(0.3f));
        }

        [UnityTest]
        public IEnumerator MoveToWalledOffPoint_Completes_AndTheNextOrderRuns()
        {
            // A closed box of walls around (6, 0, 6): the floor inside is NavMesh, but unreachable from outside.
            world.CreateEnvironment(
                (new Vector3(6f, 1f, 8.5f), new Vector3(6f, 2f, 1f)),
                (new Vector3(6f, 1f, 3.5f), new Vector3(6f, 2f, 1f)),
                (new Vector3(3.5f, 1f, 6f), new Vector3(1f, 2f, 6f)),
                (new Vector3(8.5f, 1f, 6f), new Vector3(1f, 2f, 6f)));
            var unit = world.CreateUnit(new Vector3(-6f, 0f, -6f));
            yield return null;
            var next = new Vector3(-6f, 0f, 0f);

            Assert.That(unit.Issue(new MoveCommand(new Vector3(6f, 0f, 6f))), Is.True);
            Assert.That(unit.Issue(new MoveCommand(next), IssueMode.Append), Is.True);

            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 25f);

            Assert.That(unit.CurrentCommand, Is.Null, "The walled-off move never completed, so the queue stalled");
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, next), Is.LessThan(0.3f));
        }

        [UnityTest]
        public IEnumerator Append_MoveOffNavMesh_IsRejectedWithWarning()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(-6f, 0f, -6f));
            yield return null;
            var first = new MoveCommand(new Vector3(6f, 0f, 6f));
            unit.Issue(first);

            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("cannot queue a move"));
            Assert.That(unit.Issue(new MoveCommand(new Vector3(100f, 0f, 100f)), IssueMode.Append), Is.False);

            Assert.That(unit.CurrentCommand, Is.SameAs(first));
            Assert.That(unit.PendingCommands, Is.Empty);
        }
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `Tools/run-tests.sh EditMode Blackglass.Tests.CommandableUnitTests`
Expected: `error CS1501`/`CS1061` (`Issue` takes 1 argument; `PendingCommands` not found), `EXIT=3`.

- [ ] **Step 4: Add `CanMoveTo` and fix `HasArrived` in `UnitMover`**

In `Assets/_Project/Scripts/Units/UnitMover.cs`:

Add a field after `NavMeshAgent agent;`:

```csharp
        int moveRequestFrame = -1;
```

Replace the `HasArrived` property with:

```csharp
        /// <summary>
        /// True when the unit is at the end of its path, or has no path left to walk. The path ends at the closest
        /// reachable point, so a destination the NavMesh only partly reaches still counts as arrived there.
        /// Never true on the frame a move was requested, while the agent's path data is still the old one.
        /// </summary>
        public bool HasArrived
        {
            get
            {
                if (!Agent.isOnNavMesh || Agent.pathPending || Time.frameCount == moveRequestFrame)
                    return false;
                if (!Agent.hasPath)
                    return true;
                var offset = Agent.pathEndPosition - transform.position;
                offset.y = 0f;
                return offset.magnitude <= Agent.stoppingDistance + ArrivalTolerance;
            }
        }
```

Add after `HasArrived`:

```csharp
        /// <summary>True if MoveTo(point) would be accepted: on a NavMesh, with a walkable point within 2 m. No side effects.</summary>
        public bool CanMoveTo(Vector3 point) =>
            Agent.isOnNavMesh && NavMesh.SamplePosition(point, out _, SnapRadius, NavMesh.AllAreas);
```

In `MoveTo`, record the frame just before setting the destination:

```csharp
            Agent.isStopped = false;
            moveRequestFrame = Time.frameCount;
            return Agent.SetDestination(hit.position);
```

- [ ] **Step 5: Rewrite `CommandableUnit` with the queue**

Replace the whole of `Assets/_Project/Scripts/Units/CommandableUnit.cs` with:

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The single entry point for gameplay orders. Keeps the unit's orders in a CommandQueue (the current order
    /// plus pending ones) and carries out the current order each simulation frame using UnitMover and UnitAttacker.
    /// </summary>
    [RequireComponent(typeof(UnitMover), typeof(UnitAttacker))]
    public sealed class CommandableUnit : MonoBehaviour
    {
        const float ChaseRepathDistance = 0.5f;

        readonly CommandQueue queue = new CommandQueue();
        UnitMover mover;
        UnitAttacker attacker;
        bool chasing;
        Vector3 lastChaseTarget;

        /// <summary>The order being carried out, or null when idle.</summary>
        public UnitCommand CurrentCommand => queue.Current;

        /// <summary>Orders waiting behind the current one, in the order they will run.</summary>
        public IReadOnlyList<UnitCommand> PendingCommands => queue.Pending;

        UnitMover Mover => mover != null ? mover : mover = GetComponent<UnitMover>();
        UnitAttacker Attacker => attacker != null ? attacker : attacker = GetComponent<UnitAttacker>();

        /// <summary>
        /// Gives the unit an order. Replace drops the current and pending orders and starts this one now; Append runs
        /// it after the pending ones (now, if the unit is idle). A Stop always halts the unit and clears every order.
        /// Returns false if the order cannot be carried out (no walkable point within 2 m of the destination, dead or
        /// inactive target); the unit's orders are then unchanged.
        /// Re-issuing an attack on the current target keeps the unit moving instead of restarting its chase.
        /// </summary>
        public bool Issue(UnitCommand command, IssueMode mode = IssueMode.Replace)
        {
            if (command == null)
                throw new ArgumentNullException(nameof(command));
            if (mode != IssueMode.Replace && mode != IssueMode.Append)
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown issue mode.");

            switch (command)
            {
                case StopCommand _:
                    StopAll();
                    return true;
                case MoveCommand _:
                case AttackCommand _:
                    break;
                default:
                    throw new ArgumentException($"Unsupported command type {command.GetType().Name}.", nameof(command));
            }

            if (mode == IssueMode.Append && queue.Current != null)
            {
                if (!CanStart(command))
                    return false;
                queue.Append(command);
                return true;
            }

            if (command is AttackCommand attack && queue.Current is AttackCommand current
                && current.Target == attack.Target && IsAttackable(attack.Target))
            {
                queue.Replace(attack);
                return true;
            }

            if (!TryStart(command))
                return false;
            queue.Replace(command);
            return true;
        }

        void Update()
        {
            // Orders only advance while simulation time advances (tactical pause sets timeScale to 0).
            if (Time.deltaTime <= 0f)
                return;

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

        // Whether a queued order could start later, without starting it.
        bool CanStart(UnitCommand command)
        {
            switch (command)
            {
                case MoveCommand move:
                    if (Mover.CanMoveTo(move.Destination))
                        return true;
                    Debug.LogWarning($"{name} cannot queue a move: no walkable NavMesh point within 2 m of {move.Destination}.", this);
                    return false;
                case AttackCommand attack:
                    return IsAttackable(attack.Target);
                default:
                    return false;
            }
        }

        // Starts carrying out an order. Returns false, without side effects, if it cannot be carried out.
        bool TryStart(UnitCommand command)
        {
            switch (command)
            {
                case MoveCommand move:
                    if (!Mover.MoveTo(move.Destination))
                        return false;
                    chasing = false;
                    return true;
                case AttackCommand attack:
                    if (!IsAttackable(attack.Target))
                        return false;
                    Mover.Stop();
                    chasing = false;
                    return true;
                default:
                    return false;
            }
        }

        // The current order is finished: start the next pending order that can still be carried out.
        void StartNext()
        {
            while (queue.Advance() != null)
            {
                if (TryStart(queue.Current))
                    return;
            }
        }

        void StopAll()
        {
            Mover.Stop();
            chasing = false;
            queue.Clear();
        }

        void UpdateAttack(Health target)
        {
            if (!IsAttackable(target))
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

        // A target that is missing, dead, or deactivated while still alive can no longer be attacked.
        static bool IsAttackable(Health target) => target != null && target.IsAlive && target.gameObject.activeInHierarchy;

        void FinishAttack()
        {
            Mover.Stop();
            chasing = false;
            StartNext();
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

- [ ] **Step 6: Run the tests to verify they pass**

Run: `Tools/run-tests.sh EditMode`
Expected: `total="55" passed="55"`, `EXIT=0` (45 + 10 new; the `Stop_ClearsCurrentAndPending` case runs twice).

Run: `Tools/run-tests.sh PlayMode`
Expected: `total="43" passed="43"`, `EXIT=0` (36 + 7 new). All Phase 1 PlayMode tests must still pass — they exercise `Issue` with the default Replace mode.

If `MoveToWalledOffPoint_Completes_AndTheNextOrderRuns` fails, do not loosen the test: use superpowers:systematic-debugging on `UnitMover.HasArrived` (log `pathStatus`, `hasPath`, `pathEndPosition` each frame) and fix the cause.

- [ ] **Step 7: Commit**

```bash
git add Assets/_Project/Scripts/Units Assets/_Project/Tests/EditMode/CommandableUnitTests.cs Assets/_Project/Tests/PlayMode/CommandableUnitQueuePlayModeTests.cs*
git status --short
git commit -m "Queue unit orders and finish moves at the end of partial paths

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Group orders with move offsets

**Files:**
- Create: `Assets/_Project/Scripts/Commands/GroupMoveOffsets.cs`
- Create: `Assets/_Project/Scripts/Commands/GroupOrders.cs`
- Create: `Assets/_Project/Tests/EditMode/GroupMoveOffsetsTests.cs`
- Create: `Assets/_Project/Tests/EditMode/GroupOrdersTests.cs`
- Create: `Assets/_Project/Tests/PlayMode/GroupOrdersPlayModeTests.cs`

**Interfaces:**
- Consumes: `CommandableUnit.Issue(UnitCommand, IssueMode)` (Task 2), `StopCommand`, `IssueMode` (Task 1).
- Produces:
  - `public static class GroupMoveOffsets { public static Vector3[] Compute(int count, float spacing); }`
  - `public static class GroupOrders { public const float DefaultSpacing = 1.5f; public static int Issue(IReadOnlyList<CommandableUnit> units, UnitCommand command, IssueMode mode, float spacing = DefaultSpacing); }`

- [ ] **Step 1: Write the failing EditMode tests**

`Assets/_Project/Tests/EditMode/GroupMoveOffsetsTests.cs`:

```csharp
using System;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class GroupMoveOffsetsTests
    {
        const float Spacing = 1.5f;
        const float Tolerance = 1e-4f;

        [Test]
        public void ZeroUnits_GetNoOffsets()
        {
            Assert.That(GroupMoveOffsets.Compute(0, Spacing), Is.Empty);
        }

        [Test]
        public void OneUnit_GoesToTheClickedPoint()
        {
            Assert.That(GroupMoveOffsets.Compute(1, Spacing), Is.EqualTo(new[] { Vector3.zero }));
        }

        [Test]
        public void ThreeUnits_CentreThenTwoNeighboursAtSpacing()
        {
            var offsets = GroupMoveOffsets.Compute(3, Spacing);
            Assert.That(offsets[0], Is.EqualTo(Vector3.zero));
            Assert.That(offsets[1].magnitude, Is.EqualTo(Spacing).Within(Tolerance));
            Assert.That(offsets[2].magnitude, Is.EqualTo(Spacing).Within(Tolerance));
            Assert.That(Vector3.Distance(offsets[1], offsets[2]), Is.EqualTo(Spacing).Within(Tolerance));
        }

        [Test]
        public void SevenUnits_FillTheFirstRing()
        {
            var offsets = GroupMoveOffsets.Compute(7, Spacing);
            for (var i = 1; i < 7; i++)
                Assert.That(offsets[i].magnitude, Is.EqualTo(Spacing).Within(Tolerance), $"offset {i}");
        }

        [Test]
        public void EighthUnit_StartsTheSecondRing()
        {
            var offsets = GroupMoveOffsets.Compute(8, Spacing);
            Assert.That(offsets[7].magnitude, Is.EqualTo(2f * Spacing).Within(Tolerance));
        }

        [Test]
        public void Offsets_StayOnTheGroundPlane()
        {
            foreach (var offset in GroupMoveOffsets.Compute(19, Spacing))
                Assert.That(offset.y, Is.EqualTo(0f));
        }

        [TestCase(19)]
        [TestCase(37)]
        public void NoTwoOffsetsAreCloserThanTheSpacing(int count)
        {
            var offsets = GroupMoveOffsets.Compute(count, Spacing);
            Assert.That(offsets, Has.Length.EqualTo(count));
            for (var i = 0; i < count; i++)
            for (var j = i + 1; j < count; j++)
                Assert.That(Vector3.Distance(offsets[i], offsets[j]), Is.GreaterThanOrEqualTo(Spacing - Tolerance), $"offsets {i} and {j}");
        }

        [Test]
        public void NegativeCount_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => GroupMoveOffsets.Compute(-1, Spacing));
        }

        [TestCase(0f)]
        [TestCase(-1f)]
        public void NonPositiveSpacing_Throws(float spacing)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => GroupMoveOffsets.Compute(3, spacing));
        }
    }
}
```

`Assets/_Project/Tests/EditMode/GroupOrdersTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    // Attack and Stop only: Move needs a NavMesh and is covered by GroupOrdersPlayModeTests.
    public class GroupOrdersTests
    {
        readonly List<GameObject> hosts = new List<GameObject>();
        Health target;

        [SetUp]
        public void SetUp()
        {
            var targetHost = new GameObject("Target");
            hosts.Add(targetHost);
            target = targetHost.AddComponent<Health>();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var host in hosts)
                Object.DestroyImmediate(host);
            hosts.Clear();
        }

        CommandableUnit CreateUnit()
        {
            var host = new GameObject("Unit");
            hosts.Add(host);
            return host.AddComponent<CommandableUnit>();
        }

        [Test]
        public void Attack_GivesEveryUnitTheSameOrder()
        {
            var units = new[] { CreateUnit(), CreateUnit(), CreateUnit() };
            var attack = new AttackCommand(target);

            Assert.That(GroupOrders.Issue(units, attack, IssueMode.Replace), Is.EqualTo(3));
            foreach (var unit in units)
                Assert.That(unit.CurrentCommand, Is.SameAs(attack));
        }

        [Test]
        public void Attack_Append_QueuesBehindExistingOrders()
        {
            var units = new[] { CreateUnit(), CreateUnit() };
            var first = new AttackCommand(target);
            GroupOrders.Issue(units, first, IssueMode.Replace);
            var otherHost = new GameObject("Other");
            hosts.Add(otherHost);
            var second = new AttackCommand(otherHost.AddComponent<Health>());

            Assert.That(GroupOrders.Issue(units, second, IssueMode.Append), Is.EqualTo(2));
            foreach (var unit in units)
            {
                Assert.That(unit.CurrentCommand, Is.SameAs(first));
                Assert.That(unit.PendingCommands, Is.EqualTo(new UnitCommand[] { second }));
            }
        }

        [Test]
        public void Stop_ClearsEveryUnit()
        {
            var units = new[] { CreateUnit(), CreateUnit() };
            GroupOrders.Issue(units, new AttackCommand(target), IssueMode.Replace);

            Assert.That(GroupOrders.Issue(units, new StopCommand(), IssueMode.Replace), Is.EqualTo(2));
            foreach (var unit in units)
                Assert.That(unit.CurrentCommand, Is.Null);
        }

        [Test]
        public void NullUnits_AreSkipped()
        {
            var first = CreateUnit();
            var second = CreateUnit();
            var attack = new AttackCommand(target);

            Assert.That(GroupOrders.Issue(new[] { first, null, second }, attack, IssueMode.Replace), Is.EqualTo(2));
            Assert.That(first.CurrentCommand, Is.SameAs(attack));
            Assert.That(second.CurrentCommand, Is.SameAs(attack));
        }

        [Test]
        public void EmptyGroup_DoesNothing()
        {
            Assert.That(GroupOrders.Issue(new CommandableUnit[0], new AttackCommand(target), IssueMode.Replace), Is.EqualTo(0));
        }

        [Test]
        public void NullArguments_Throw()
        {
            Assert.Throws<ArgumentNullException>(() => GroupOrders.Issue(null, new StopCommand(), IssueMode.Replace));
            Assert.Throws<ArgumentNullException>(() => GroupOrders.Issue(new[] { CreateUnit() }, null, IssueMode.Replace));
        }
    }
}
```

- [ ] **Step 2: Write the failing PlayMode tests**

`Assets/_Project/Tests/PlayMode/GroupOrdersPlayModeTests.cs`:

```csharp
using System.Collections;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class GroupOrdersPlayModeTests
    {
        TestWorld world;

        [SetUp]
        public void SetUp() => world = new TestWorld();

        [TearDown]
        public void TearDown() => world.Dispose();

        [UnityTest]
        public IEnumerator Move_ThreeUnitsArriveAtDistinctPointsAroundTheDestination_OthersGetNothing()
        {
            world.CreateEnvironment();
            var units = new[]
            {
                world.CreateUnit(new Vector3(-6f, 0f, -6f)),
                world.CreateUnit(new Vector3(-4f, 0f, -6f)),
                world.CreateUnit(new Vector3(-2f, 0f, -6f)),
            };
            var outsider = world.CreateUnit(new Vector3(6f, 0f, -6f));
            yield return null;
            var destination = new Vector3(4f, 0f, 4f);

            Assert.That(GroupOrders.Issue(units, new MoveCommand(destination), IssueMode.Replace), Is.EqualTo(3));
            Assert.That(outsider.CurrentCommand, Is.Null);

            yield return TestWorld.WaitUntil(() => units.All(u => u.CurrentCommand == null), 15f);

            Assert.That(units.All(u => u.CurrentCommand == null), Is.True, "Group move did not finish in time");
            for (var i = 0; i < units.Length; i++)
            {
                Assert.That(TestWorld.HorizontalDistance(units[i].transform.position, destination), Is.LessThan(2f), $"unit {i}");
                for (var j = i + 1; j < units.Length; j++)
                    Assert.That(TestWorld.HorizontalDistance(units[i].transform.position, units[j].transform.position), Is.GreaterThan(1f), $"units {i} and {j} overlap");
            }
        }

        [UnityTest]
        public IEnumerator OffsetPointOffTheNavMesh_FallsBackToTheClickedPoint()
        {
            world.CreateEnvironment();   // ground spans -20..20; the NavMesh edge is about 0.5 m inside it
            var first = world.CreateUnit(new Vector3(14f, 0f, 0f));
            var second = world.CreateUnit(new Vector3(14f, 0f, 3f));
            yield return null;
            var destination = new Vector3(19f, 0f, 0f);

            // With 3 m spacing the second slot is (22, 0, 0): more than 2 m from any walkable point.
            LogAssert.Expect(LogType.Warning, new Regex("no walkable NavMesh point"));
            Assert.That(GroupOrders.Issue(new[] { first, second }, new MoveCommand(destination), IssueMode.Replace, 3f), Is.EqualTo(2));

            Assert.That(second.CurrentCommand, Is.TypeOf<MoveCommand>());
            Assert.That(((MoveCommand)second.CurrentCommand).Destination, Is.EqualTo(destination));
        }
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `Tools/run-tests.sh EditMode Blackglass.Tests.GroupMoveOffsetsTests`
Expected: `error CS0103: The name 'GroupMoveOffsets' does not exist`, `EXIT=3`.

- [ ] **Step 4: Write `GroupMoveOffsets`**

`Assets/_Project/Scripts/Commands/GroupMoveOffsets.cs`:

```csharp
using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Destination offsets for a group move, so units don't all aim for the same point. Index 0 is the clicked point;
    /// the rest fill rings of a hexagonal lattice around it, so no two offsets are closer than the spacing.
    /// Not a formation: no facing, no slot matching.
    /// </summary>
    public static class GroupMoveOffsets
    {
        public static Vector3[] Compute(int count, float spacing)
        {
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count), count, "Count cannot be negative.");
            if (spacing <= 0f)
                throw new ArgumentOutOfRangeException(nameof(spacing), spacing, "Spacing must be positive.");

            var offsets = new Vector3[count];
            var index = 1;
            for (var ring = 1; index < count; ring++)
            {
                // Ring r has six corners at distance r * spacing and r - 1 lattice points along each edge between them.
                for (var side = 0; side < 6 && index < count; side++)
                {
                    var corner = Corner(side, ring, spacing);
                    var nextCorner = Corner(side + 1, ring, spacing);
                    for (var step = 0; step < ring && index < count; step++)
                        offsets[index++] = Vector3.Lerp(corner, nextCorner, step / (float)ring);
                }
            }
            return offsets;
        }

        static Vector3 Corner(int side, int ring, float spacing)
        {
            var angle = side * 60f * Mathf.Deg2Rad;
            return new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * (ring * spacing);
        }
    }
}
```

- [ ] **Step 5: Write `GroupOrders`**

`Assets/_Project/Scripts/Commands/GroupOrders.cs`:

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Gives one order to a group of units. A move is spread over GroupMoveOffsets so units don't pile up; other
    /// orders go to every unit unchanged. Usable by player input, AI and scripts alike.
    /// </summary>
    public static class GroupOrders
    {
        /// <summary>Distance between neighbouring move destinations (NavMesh agents are 0.5 m in radius).</summary>
        public const float DefaultSpacing = 1.5f;

        /// <summary>Issues the order to every non-null unit. Returns how many units accepted it.</summary>
        public static int Issue(IReadOnlyList<CommandableUnit> units, UnitCommand command, IssueMode mode, float spacing = DefaultSpacing)
        {
            if (units == null)
                throw new ArgumentNullException(nameof(units));
            if (command == null)
                throw new ArgumentNullException(nameof(command));

            var accepted = 0;
            if (command is MoveCommand move)
            {
                var offsets = GroupMoveOffsets.Compute(units.Count, spacing);
                for (var i = 0; i < units.Count; i++)
                {
                    var unit = units[i];
                    if (unit == null)
                        continue;
                    // A slot off the NavMesh (past a map edge, inside a wall) falls back to the clicked point itself.
                    var unitAccepted = unit.Issue(new MoveCommand(move.Destination + offsets[i]), mode)
                        || (offsets[i] != Vector3.zero && unit.Issue(move, mode));
                    if (unitAccepted)
                        accepted++;
                }
                return accepted;
            }

            foreach (var unit in units)
            {
                if (unit != null && unit.Issue(command, mode))
                    accepted++;
            }
            return accepted;
        }
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `Tools/run-tests.sh EditMode`
Expected: `total="72" passed="72"`, `EXIT=0` (55 + 11 offset cases — `NoTwoOffsetsAreCloserThanTheSpacing` and `NonPositiveSpacing_Throws` run twice each — + 6 group tests).

Run: `Tools/run-tests.sh PlayMode`
Expected: `total="45" passed="45"`, `EXIT=0`.

- [ ] **Step 7: Commit**

```bash
git add Assets/_Project/Scripts/Commands Assets/_Project/Tests/EditMode Assets/_Project/Tests/PlayMode/GroupOrdersPlayModeTests.cs*
git status --short
git commit -m "Add group orders that spread moves over hex offsets

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Selection — `SelectableUnit`, `UnitSelection`, `ScreenBox`, `SelectionIndicator`

**Files:**
- Create: `Assets/_Project/Scripts/Selection/SelectableUnit.cs`
- Create: `Assets/_Project/Scripts/Selection/UnitSelection.cs`
- Create: `Assets/_Project/Scripts/Selection/ScreenBox.cs`
- Create: `Assets/_Project/Scripts/Selection/SelectionIndicator.cs`
- Modify: `Assets/_Project/Tests/PlayMode/TestSupport/TestWorld.cs` (add `CreateFriendly`)
- Create: `Assets/_Project/Tests/EditMode/UnitSelectionTests.cs`
- Create: `Assets/_Project/Tests/EditMode/ScreenBoxTests.cs`
- Create: `Assets/_Project/Tests/PlayMode/UnitSelectionPlayModeTests.cs`

**Interfaces:**
- Consumes: `CommandableUnit` (Phase 1).
- Produces:
  - `SelectableUnit : MonoBehaviour` (requires `CommandableUnit`): `CommandableUnit Unit { get; }`, `bool IsSelected { get; internal set; }`, `event Action<bool> SelectionChanged`, `internal event Action<SelectableUnit> Disabled`.
  - `UnitSelection : MonoBehaviour`: serialized `List<SelectableUnit> roster`; `IReadOnlyList<SelectableUnit> Roster`, `IReadOnlyList<SelectableUnit> Selected`, `event Action Changed`, `AddToRoster(SelectableUnit)`, `Select(SelectableUnit)`, `Toggle(SelectableUnit)`, `Add(IEnumerable<SelectableUnit>)`, `SetSelection(IEnumerable<SelectableUnit>)`, `Clear()`, `internal void Initialize(params SelectableUnit[] rosterUnits)`.
  - `ScreenBox` (static): `Rect FromCorners(Vector2, Vector2)`, `bool Contains(Rect box, Vector3 screenPoint)`, `Rect ToGuiRect(Rect screenRect, float screenHeight)`.
  - `SelectionIndicator : MonoBehaviour` (requires `SelectableUnit`): serialized `GameObject ring`; `internal void Initialize(GameObject ringObject)`.
  - `TestWorld.CreateFriendly(Vector3 groundPosition) : SelectableUnit`.

- [ ] **Step 1: Write the failing EditMode tests**

`Assets/_Project/Tests/EditMode/UnitSelectionTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class UnitSelectionTests
    {
        readonly List<GameObject> hosts = new List<GameObject>();
        UnitSelection selection;
        SelectableUnit a;
        SelectableUnit b;
        SelectableUnit c;
        int changes;

        [SetUp]
        public void SetUp()
        {
            a = CreateSelectable("A");
            b = CreateSelectable("B");
            c = CreateSelectable("C");
            var host = new GameObject("Selection");
            hosts.Add(host);
            selection = host.AddComponent<UnitSelection>();
            selection.Initialize(a, b, c);
            changes = 0;
            selection.Changed += () => changes++;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var host in hosts)
                Object.DestroyImmediate(host);
            hosts.Clear();
        }

        SelectableUnit CreateSelectable(string name)
        {
            var host = new GameObject(name);
            hosts.Add(host);
            return host.AddComponent<SelectableUnit>();
        }

        [Test]
        public void StartsEmpty_WithTheRoster()
        {
            Assert.That(selection.Selected, Is.Empty);
            Assert.That(selection.Roster, Is.EqualTo(new[] { a, b, c }));
        }

        [Test]
        public void SelectableUnit_ExposesItsUnit()
        {
            Assert.That(a.Unit, Is.SameAs(a.GetComponent<CommandableUnit>()));
        }

        [Test]
        public void Select_SelectsOnlyThatUnit()
        {
            selection.Select(a);
            selection.Select(b);
            Assert.That(selection.Selected, Is.EqualTo(new[] { b }));
            Assert.That(a.IsSelected, Is.False);
            Assert.That(b.IsSelected, Is.True);
            Assert.That(changes, Is.EqualTo(2));
        }

        [Test]
        public void Select_SameUnitTwice_RaisesChangedOnce()
        {
            selection.Select(a);
            selection.Select(a);
            Assert.That(changes, Is.EqualTo(1));
        }

        [Test]
        public void Toggle_AddsThenRemoves()
        {
            selection.Select(a);
            selection.Toggle(b);
            Assert.That(selection.Selected, Is.EqualTo(new[] { a, b }));
            selection.Toggle(a);
            Assert.That(selection.Selected, Is.EqualTo(new[] { b }));
            Assert.That(a.IsSelected, Is.False);
            Assert.That(changes, Is.EqualTo(3));
        }

        [Test]
        public void Add_KeepsExistingAndSkipsDuplicates()
        {
            selection.Select(b);
            selection.Add(new[] { a, b, c });
            Assert.That(selection.Selected, Is.EqualTo(new[] { b, a, c }));
            Assert.That(changes, Is.EqualTo(2));
            selection.Add(new[] { a });
            Assert.That(changes, Is.EqualTo(2), "Adding only already-selected units is not a change");
        }

        [Test]
        public void SetSelection_ReplacesInTheGivenOrder()
        {
            selection.Select(a);
            selection.SetSelection(new[] { c, b });
            Assert.That(selection.Selected, Is.EqualTo(new[] { c, b }));
            Assert.That(a.IsSelected, Is.False);
            Assert.That(b.IsSelected && c.IsSelected, Is.True);
        }

        [Test]
        public void SetSelection_SkipsNullEntries()
        {
            selection.SetSelection(new[] { a, null, b });
            Assert.That(selection.Selected, Is.EqualTo(new[] { a, b }));
        }

        [Test]
        public void Clear_DeselectsEverything()
        {
            selection.SetSelection(new[] { a, b });
            selection.Clear();
            Assert.That(selection.Selected, Is.Empty);
            Assert.That(a.IsSelected || b.IsSelected, Is.False);
            selection.Clear();
            Assert.That(changes, Is.EqualTo(2), "Clearing an empty selection is not a change");
        }

        [Test]
        public void InactiveUnit_CannotBeSelected()
        {
            c.gameObject.SetActive(false);
            selection.Select(c);
            selection.Toggle(c);
            selection.Add(new[] { c });
            Assert.That(selection.Selected, Is.Empty);
            Assert.That(changes, Is.EqualTo(0));
        }

        [Test]
        public void IsSelected_RaisesSelectionChangedOnlyOnChange()
        {
            var events = new List<bool>();
            a.SelectionChanged += events.Add;
            selection.Select(a);
            selection.Select(a);
            selection.Clear();
            Assert.That(events, Is.EqualTo(new[] { true, false }));
        }

        [Test]
        public void AddToRoster_AddsOnce()
        {
            var d = CreateSelectable("D");
            selection.AddToRoster(d);
            selection.AddToRoster(d);
            Assert.That(selection.Roster, Is.EqualTo(new[] { a, b, c, d }));
        }

        [Test]
        public void NullArguments_Throw()
        {
            Assert.Throws<ArgumentNullException>(() => selection.Select(null));
            Assert.Throws<ArgumentNullException>(() => selection.Toggle(null));
            Assert.Throws<ArgumentNullException>(() => selection.Add(null));
            Assert.Throws<ArgumentNullException>(() => selection.SetSelection(null));
            Assert.Throws<ArgumentNullException>(() => selection.AddToRoster(null));
        }
    }
}
```

`Assets/_Project/Tests/EditMode/ScreenBoxTests.cs`:

```csharp
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class ScreenBoxTests
    {
        [Test]
        public void FromCorners_AcceptsCornersInAnyOrder()
        {
            var box = ScreenBox.FromCorners(new Vector2(100f, 20f), new Vector2(10f, 80f));
            Assert.That(box, Is.EqualTo(Rect.MinMaxRect(10f, 20f, 100f, 80f)));
        }

        [Test]
        public void Contains_PointInside()
        {
            var box = ScreenBox.FromCorners(new Vector2(10f, 10f), new Vector2(100f, 100f));
            Assert.That(ScreenBox.Contains(box, new Vector3(50f, 50f, 5f)), Is.True);
        }

        [Test]
        public void Contains_PointOutside()
        {
            var box = ScreenBox.FromCorners(new Vector2(10f, 10f), new Vector2(100f, 100f));
            Assert.That(ScreenBox.Contains(box, new Vector3(150f, 50f, 5f)), Is.False);
            Assert.That(ScreenBox.Contains(box, new Vector3(50f, 5f, 5f)), Is.False);
        }

        [Test]
        public void Contains_EdgesAreInside()
        {
            var box = ScreenBox.FromCorners(new Vector2(10f, 10f), new Vector2(100f, 100f));
            Assert.That(ScreenBox.Contains(box, new Vector3(10f, 10f, 5f)), Is.True);
            Assert.That(ScreenBox.Contains(box, new Vector3(100f, 100f, 5f)), Is.True);
        }

        [Test]
        public void Contains_PointBehindTheCamera_IsOutside()
        {
            var box = ScreenBox.FromCorners(new Vector2(10f, 10f), new Vector2(100f, 100f));
            Assert.That(ScreenBox.Contains(box, new Vector3(50f, 50f, -5f)), Is.False);
        }

        [Test]
        public void ToGuiRect_FlipsTheVerticalAxis()
        {
            var screen = Rect.MinMaxRect(10f, 20f, 110f, 70f);
            var gui = ScreenBox.ToGuiRect(screen, 480f);
            Assert.That(gui, Is.EqualTo(new Rect(10f, 410f, 100f, 50f)));
        }
    }
}
```

- [ ] **Step 2: Write the failing PlayMode tests and the `TestWorld` helper**

In `Assets/_Project/Tests/PlayMode/TestSupport/TestWorld.cs`, add after `CreateUnit`:

```csharp
        /// <summary>A unit the player can select (a CreateUnit unit plus SelectableUnit).</summary>
        public SelectableUnit CreateFriendly(Vector3 groundPosition)
        {
            var unit = CreateUnit(groundPosition);
            unit.name = "TestFriendly";
            return unit.gameObject.AddComponent<SelectableUnit>();
        }
```

`Assets/_Project/Tests/PlayMode/UnitSelectionPlayModeTests.cs`:

```csharp
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class UnitSelectionPlayModeTests
    {
        TestWorld world;
        UnitSelection selection;
        SelectableUnit a;
        SelectableUnit b;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            world.CreateEnvironment();
            a = world.CreateFriendly(new Vector3(-2f, 0f, 0f));
            b = world.CreateFriendly(new Vector3(2f, 0f, 0f));
            selection = world.Track(new GameObject("Selection")).AddComponent<UnitSelection>();
            selection.Initialize(a, b);
        }

        [TearDown]
        public void TearDown() => world.Dispose();

        [UnityTest]
        public IEnumerator DisabledUnit_IsDroppedFromTheSelection()
        {
            yield return null;
            selection.SetSelection(new[] { a, b });
            var changes = 0;
            selection.Changed += () => changes++;

            a.gameObject.SetActive(false);

            Assert.That(selection.Selected, Is.EqualTo(new[] { b }));
            Assert.That(a.IsSelected, Is.False);
            Assert.That(changes, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator DestroyedUnit_IsDroppedFromTheSelection()
        {
            yield return null;
            selection.SetSelection(new[] { a, b });

            Object.Destroy(a.gameObject);
            yield return null;

            Assert.That(selection.Selected, Is.EqualTo(new[] { b }));
            Assert.That(GroupOrders.Issue(new[] { selection.Selected[0].Unit }, new StopCommand(), IssueMode.Replace), Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator Indicator_ShowsTheRingOnlyWhileSelected()
        {
            var ring = new GameObject("Ring");
            ring.transform.SetParent(a.transform, false);
            var indicator = a.gameObject.AddComponent<SelectionIndicator>();
            indicator.Initialize(ring);
            yield return null;
            Assert.That(ring.activeSelf, Is.False);

            selection.Select(a);
            Assert.That(ring.activeSelf, Is.True);

            selection.Select(b);
            Assert.That(ring.activeSelf, Is.False);
        }
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `Tools/run-tests.sh EditMode Blackglass.Tests.UnitSelectionTests`
Expected: `error CS0246: The type or namespace name 'SelectableUnit' could not be found`, `EXIT=3`.

- [ ] **Step 4: Write `SelectableUnit`**

`Assets/_Project/Scripts/Selection/SelectableUnit.cs`:

```csharp
using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Marks a unit the player can select. Its selected state is owned by UnitSelection; enemies and AI units are
    /// CommandableUnits without this component.
    /// </summary>
    [RequireComponent(typeof(CommandableUnit))]
    public sealed class SelectableUnit : MonoBehaviour
    {
        CommandableUnit unit;
        bool isSelected;

        public CommandableUnit Unit => unit != null ? unit : unit = GetComponent<CommandableUnit>();

        /// <summary>Set only by UnitSelection.</summary>
        public bool IsSelected
        {
            get => isSelected;
            internal set
            {
                if (isSelected == value)
                    return;
                isSelected = value;
                SelectionChanged?.Invoke(value);
            }
        }

        /// <summary>Raised with the new state, only when it actually changes.</summary>
        public event Action<bool> SelectionChanged;

        /// <summary>Lets UnitSelection drop units that are disabled or destroyed.</summary>
        internal event Action<SelectableUnit> Disabled;

        void OnDisable() => Disabled?.Invoke(this);
    }
}
```

- [ ] **Step 5: Write `UnitSelection`**

`Assets/_Project/Scripts/Selection/UnitSelection.cs`:

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The player's selected units, plus the roster of units the player controls. Holds state only: input decides
    /// what to select, and orders reach the selected units through GroupOrders.
    /// </summary>
    public sealed class UnitSelection : MonoBehaviour
    {
        [SerializeField] List<SelectableUnit> roster = new List<SelectableUnit>();

        readonly List<SelectableUnit> selected = new List<SelectableUnit>();

        /// <summary>Every unit the player controls. Box selection tests against this list.</summary>
        public IReadOnlyList<SelectableUnit> Roster => roster;

        /// <summary>Selected units, in the order they were selected.</summary>
        public IReadOnlyList<SelectableUnit> Selected => selected;

        /// <summary>Raised after the selection actually changes.</summary>
        public event Action Changed;

        internal void Initialize(params SelectableUnit[] rosterUnits)
        {
            roster.Clear();
            roster.AddRange(rosterUnits);
        }

        public void AddToRoster(SelectableUnit unit)
        {
            if (unit == null)
                throw new ArgumentNullException(nameof(unit));
            if (!roster.Contains(unit))
                roster.Add(unit);
        }

        /// <summary>Selects only this unit.</summary>
        public void Select(SelectableUnit unit)
        {
            if (unit == null)
                throw new ArgumentNullException(nameof(unit));
            SetSelection(new[] { unit });
        }

        /// <summary>Removes the unit if it is selected, otherwise adds it.</summary>
        public void Toggle(SelectableUnit unit)
        {
            if (unit == null)
                throw new ArgumentNullException(nameof(unit));
            if (selected.Remove(unit))
                Release(unit);
            else if (CanSelect(unit))
                Claim(unit);
            else
                return;
            Changed?.Invoke();
        }

        /// <summary>Adds units to the selection, keeping the ones already selected.</summary>
        public void Add(IEnumerable<SelectableUnit> units)
        {
            if (units == null)
                throw new ArgumentNullException(nameof(units));
            var changed = false;
            foreach (var unit in units)
            {
                if (!CanSelect(unit) || selected.Contains(unit))
                    continue;
                Claim(unit);
                changed = true;
            }
            if (changed)
                Changed?.Invoke();
        }

        /// <summary>Replaces the selection with these units, in this order.</summary>
        public void SetSelection(IEnumerable<SelectableUnit> units)
        {
            if (units == null)
                throw new ArgumentNullException(nameof(units));
            var wanted = new List<SelectableUnit>();
            foreach (var unit in units)
            {
                if (CanSelect(unit) && !wanted.Contains(unit))
                    wanted.Add(unit);
            }
            if (IsSelectedExactly(wanted))
                return;

            var previous = new List<SelectableUnit>(selected);
            selected.Clear();
            foreach (var unit in previous)
            {
                if (!wanted.Contains(unit))
                    Release(unit);
            }
            foreach (var unit in wanted)
            {
                if (previous.Contains(unit))
                    selected.Add(unit);
                else
                    Claim(unit);
            }
            Changed?.Invoke();
        }

        public void Clear() => SetSelection(Array.Empty<SelectableUnit>());

        static bool CanSelect(SelectableUnit unit) => unit != null && unit.enabled && unit.gameObject.activeInHierarchy;

        bool IsSelectedExactly(List<SelectableUnit> units)
        {
            if (units.Count != selected.Count)
                return false;
            for (var i = 0; i < units.Count; i++)
            {
                if (units[i] != selected[i])
                    return false;
            }
            return true;
        }

        // Invariant kept by Claim, Release and OnUnitDisabled: a unit is subscribed to, and has IsSelected true,
        // exactly while it is in `selected`.

        // Adds a unit to the end of the selection.
        void Claim(SelectableUnit unit)
        {
            selected.Add(unit);
            unit.Disabled += OnUnitDisabled;
            unit.IsSelected = true;
        }

        // Finishes removing a unit that is no longer in the selected list.
        void Release(SelectableUnit unit)
        {
            Unsubscribe(unit);
            if (unit != null)
                unit.IsSelected = false;
        }

        void Unsubscribe(SelectableUnit unit) => unit.Disabled -= OnUnitDisabled;

        void OnUnitDisabled(SelectableUnit unit)
        {
            if (!selected.Remove(unit))
                return;
            Release(unit);
            Changed?.Invoke();
        }

        void OnDestroy()
        {
            foreach (var unit in selected)
                Unsubscribe(unit);
        }
    }
}
```

- [ ] **Step 6: Write `ScreenBox` and `SelectionIndicator`**

`Assets/_Project/Scripts/Selection/ScreenBox.cs`:

```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>Screen-space maths for box selection. Screen coordinates are pixels with the origin bottom-left.</summary>
    public static class ScreenBox
    {
        /// <summary>The rectangle spanned by two corners given in any order.</summary>
        public static Rect FromCorners(Vector2 a, Vector2 b) =>
            Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));

        /// <summary>
        /// True when a point from Camera.WorldToScreenPoint lies inside the box (edges included)
        /// and in front of the camera (positive z).
        /// </summary>
        public static bool Contains(Rect box, Vector3 screenPoint) =>
            screenPoint.z > 0f &&
            screenPoint.x >= box.xMin && screenPoint.x <= box.xMax &&
            screenPoint.y >= box.yMin && screenPoint.y <= box.yMax;

        /// <summary>Converts a screen rectangle to IMGUI coordinates (origin top-left).</summary>
        public static Rect ToGuiRect(Rect screenRect, float screenHeight) =>
            new Rect(screenRect.xMin, screenHeight - screenRect.yMax, screenRect.width, screenRect.height);
    }
}
```

`Assets/_Project/Scripts/Selection/SelectionIndicator.cs`:

```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>Debug feedback: shows a ring object while the unit is selected.</summary>
    [RequireComponent(typeof(SelectableUnit))]
    public sealed class SelectionIndicator : MonoBehaviour
    {
        [SerializeField] GameObject ring;

        SelectableUnit selectable;

        internal void Initialize(GameObject ringObject)
        {
            ring = ringObject;
            Show(Selectable.IsSelected);
        }

        SelectableUnit Selectable => selectable != null ? selectable : selectable = GetComponent<SelectableUnit>();

        void OnEnable()
        {
            Selectable.SelectionChanged += Show;
            Show(Selectable.IsSelected);
        }

        void OnDisable() => Selectable.SelectionChanged -= Show;

        void Show(bool selected)
        {
            if (ring != null)
                ring.SetActive(selected);
        }
    }
}
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `Tools/run-tests.sh EditMode`
Expected: `total="91" passed="91"`, `EXIT=0` (72 + 13 selection + 6 screen box).

Run: `Tools/run-tests.sh PlayMode`
Expected: `total="48" passed="48"`, `EXIT=0`.

- [ ] **Step 8: Commit**

```bash
git add Assets/_Project/Scripts Assets/_Project/Tests
git status --short
git commit -m "Add unit selection, selection ring and box-selection maths

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Debug visuals — `CommandQueueView` and order labels in the HUD

**Files:**
- Create: `Assets/_Project/Scripts/DebugUI/CommandQueueView.cs`
- Modify: `Assets/_Project/Scripts/DebugUI/PrototypeHud.cs` (order labels, "Selected: N", new references; control hints and drag box come in Task 6)
- Create: `Assets/_Project/Tests/PlayMode/CommandQueueViewTests.cs`
- Create: `Assets/_Project/Tests/EditMode/PrototypeHudTests.cs`

**Interfaces:**
- Consumes: `CommandableUnit.CurrentCommand`, `PendingCommands` (Task 2); `UnitSelection.Roster`, `Selected`, `SelectableUnit.Unit` (Task 4).
- Produces:
  - `CommandQueueView : MonoBehaviour` (requires `CommandableUnit`, `LineRenderer`): serialized `Material markerMaterial`, `float markerDiameter = 0.6f`, `float groundHeight = 0.05f`; `internal void Initialize(Material marker)`; `internal int LinePointCount`, `internal int ActiveMarkerCount`, `internal IReadOnlyList<GameObject> Markers`.
  - `PrototypeHud`: new serialized fields `viewCamera` (Camera), `selection` (UnitSelection); `internal static string DescribeOrders(UnitCommand current, int pendingCount)`.

- [ ] **Step 1: Write the failing tests**

`Assets/_Project/Tests/EditMode/PrototypeHudTests.cs`:

```csharp
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class PrototypeHudTests
    {
        [Test]
        public void DescribeOrders_Idle_IsEmpty()
        {
            Assert.That(PrototypeHud.DescribeOrders(null, 0), Is.Empty);
        }

        [Test]
        public void DescribeOrders_NamesTheCurrentOrder()
        {
            Assert.That(PrototypeHud.DescribeOrders(new MoveCommand(Vector3.zero), 0), Is.EqualTo("Move"));
        }

        [Test]
        public void DescribeOrders_CountsPendingOrders()
        {
            var host = new GameObject("Target");
            var attack = new AttackCommand(host.AddComponent<Health>());
            Assert.That(PrototypeHud.DescribeOrders(attack, 2), Is.EqualTo("Attack +2"));
            Object.DestroyImmediate(host);
        }
    }
}
```

`Assets/_Project/Tests/PlayMode/CommandQueueViewTests.cs`:

```csharp
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CommandQueueViewTests
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
        public IEnumerator ShowsALineThroughEveryOrder_AndMarkersAtMoveDestinations()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(-6f, 0f, -6f));
            var dummy = world.CreateDummy(new Vector3(6f, 0f, 6f));
            var view = unit.gameObject.AddComponent<CommandQueueView>();
            yield return null;
            Assert.That(view.LinePointCount, Is.EqualTo(0), "Idle unit shows a line");

            unit.Issue(new MoveCommand(new Vector3(-6f, 0f, 4f)));
            unit.Issue(new MoveCommand(new Vector3(4f, 0f, -6f)), IssueMode.Append);
            unit.Issue(new AttackCommand(dummy), IssueMode.Append);
            yield return null;

            Assert.That(view.LinePointCount, Is.EqualTo(4), "unit + two move destinations + attack target");
            Assert.That(view.ActiveMarkerCount, Is.EqualTo(2));
            foreach (var marker in view.Markers)
                Assert.That(marker.GetComponent<Collider>(), Is.Null, "Markers must never block click raycasts");

            unit.Issue(new StopCommand());
            yield return null;

            Assert.That(view.LinePointCount, Is.EqualTo(0));
            Assert.That(view.ActiveMarkerCount, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator WhilePaused_ShowsNewOrdersImmediately()
        {
            world.CreateEnvironment();
            var pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            var unit = world.CreateUnit(new Vector3(-6f, 0f, -6f));
            var view = unit.gameObject.AddComponent<CommandQueueView>();
            yield return null;

            pause.Pause();
            unit.Issue(new MoveCommand(new Vector3(4f, 0f, 4f)));
            unit.Issue(new MoveCommand(new Vector3(-4f, 0f, 4f)), IssueMode.Append);
            yield return null;

            Assert.That(view.LinePointCount, Is.EqualTo(3));
            Assert.That(view.ActiveMarkerCount, Is.EqualTo(2));
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `Tools/run-tests.sh EditMode Blackglass.Tests.PrototypeHudTests`
Expected: `error CS0117: 'PrototypeHud' does not contain a definition for 'DescribeOrders'`, `EXIT=3`.

- [ ] **Step 3: Write `CommandQueueView`**

`Assets/_Project/Scripts/DebugUI/CommandQueueView.cs`:

```csharp
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Blackglass
{
    /// <summary>
    /// Debug view of a unit's orders: a line from the unit through each order in sequence, and a small disc at every
    /// move destination. Reads the unit's orders every frame and never changes them. Works while paused.
    /// Has no colliders, so it never blocks click raycasts.
    /// </summary>
    [RequireComponent(typeof(CommandableUnit), typeof(LineRenderer))]
    public sealed class CommandQueueView : MonoBehaviour
    {
        [SerializeField] Material markerMaterial;
        [SerializeField, Min(0.05f)] float markerDiameter = 0.6f;
        // The prototype ground is flat at y = 0; the line and markers float just above it.
        [SerializeField] float groundHeight = 0.05f;

        readonly List<Vector3> points = new List<Vector3>();
        readonly List<GameObject> markers = new List<GameObject>();
        CommandableUnit unit;
        LineRenderer line;
        int markersInUse;

        internal int LinePointCount => line.positionCount;
        internal IReadOnlyList<GameObject> Markers => markers;

        internal int ActiveMarkerCount
        {
            get
            {
                var count = 0;
                foreach (var marker in markers)
                {
                    if (marker.activeSelf)
                        count++;
                }
                return count;
            }
        }

        internal void Initialize(Material marker) => markerMaterial = marker;

        void Awake()
        {
            unit = GetComponent<CommandableUnit>();
            line = GetComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.positionCount = 0;
        }

        void LateUpdate()
        {
            points.Clear();
            markersInUse = 0;
            points.Add(OnGround(transform.position));
            AddOrder(unit.CurrentCommand);
            foreach (var pending in unit.PendingCommands)
                AddOrder(pending);

            if (points.Count < 2)
            {
                line.positionCount = 0;
            }
            else
            {
                line.positionCount = points.Count;
                for (var i = 0; i < points.Count; i++)
                    line.SetPosition(i, points[i]);
            }

            for (var i = markersInUse; i < markers.Count; i++)
                markers[i].SetActive(false);
        }

        void AddOrder(UnitCommand command)
        {
            switch (command)
            {
                case MoveCommand move:
                    var destination = OnGround(move.Destination);
                    points.Add(destination);
                    ShowMarker(destination);
                    break;
                case AttackCommand attack when attack.Target != null && attack.Target.gameObject.activeInHierarchy:
                    points.Add(OnGround(attack.Target.transform.position));
                    break;
            }
        }

        Vector3 OnGround(Vector3 point) => new Vector3(point.x, groundHeight, point.z);

        void ShowMarker(Vector3 position)
        {
            if (markersInUse == markers.Count)
                markers.Add(CreateMarker());
            var marker = markers[markersInUse++];
            marker.transform.SetPositionAndRotation(position, Quaternion.identity);
            marker.SetActive(true);
        }

        GameObject CreateMarker()
        {
            var marker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            marker.name = "MoveMarker";
            DestroyImmediate(marker.GetComponent<Collider>());
            // Parented to the unit for cleanup only; its world position is set every frame.
            marker.transform.SetParent(transform, false);
            marker.transform.localScale = new Vector3(markerDiameter, 0.01f, markerDiameter);
            var markerRenderer = marker.GetComponent<Renderer>();
            if (markerMaterial != null)
                markerRenderer.sharedMaterial = markerMaterial;
            markerRenderer.shadowCastingMode = ShadowCastingMode.Off;
            markerRenderer.receiveShadows = false;
            return marker;
        }
    }
}
```

- [ ] **Step 4: Add order labels and the selection count to `PrototypeHud`**

Replace the whole of `Assets/_Project/Scripts/DebugUI/PrototypeHud.cs` with:

```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>Debug-only on-screen text (IMGUI). Not production UI. Works while paused.</summary>
    public sealed class PrototypeHud : MonoBehaviour
    {
        const string ControlHints =
            "WASD: pan   Q/E: rotate   Right-drag: rotate/tilt   Wheel: zoom\n" +
            "Left-click ground: move   Left-click dummy: attack\n" +
            "Space: tactical pause";
        // Order labels float this far above a unit's centre (the capsule is 2 m tall).
        const float UnitLabelHeight = 1.5f;

        [SerializeField] TacticalPause tacticalPause;
        [SerializeField] Health target;
        [SerializeField] string targetLabel = "Training Dummy";
        [SerializeField] Camera viewCamera;
        [SerializeField] UnitSelection selection;

        GUIStyle pausedStyle;
        GUIStyle unitLabelStyle;

        /// <summary>Short summary of a unit's orders, such as "Move +2". Empty when idle.</summary>
        internal static string DescribeOrders(UnitCommand current, int pendingCount)
        {
            if (current == null)
                return string.Empty;
            var orderName = current.GetType().Name;
            if (orderName.EndsWith("Command"))
                orderName = orderName.Substring(0, orderName.Length - "Command".Length);
            return pendingCount > 0 ? $"{orderName} +{pendingCount}" : orderName;
        }

        void OnGUI()
        {
            GUI.Label(new Rect(10f, 10f, 640f, 80f), ControlHints);

            if (target != null)
            {
                var status = target.IsAlive ? $"{target.Current} / {target.Max}" : "destroyed";
                GUI.Label(new Rect(10f, 95f, 320f, 22f), $"{targetLabel}: {status}");
            }

            if (selection != null)
                GUI.Label(new Rect(10f, 115f, 320f, 22f), $"Selected: {selection.Selected.Count}");

            DrawUnitLabels();

            if (tacticalPause != null && tacticalPause.IsPaused)
            {
                pausedStyle ??= new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.UpperCenter,
                    fontSize = 22,
                    fontStyle = FontStyle.Bold,
                };
                GUI.Label(new Rect(0f, 140f, Screen.width, 40f), "TACTICAL PAUSE - Space to resume", pausedStyle);
            }
        }

        void DrawUnitLabels()
        {
            if (selection == null || viewCamera == null)
                return;
            unitLabelStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter };
            foreach (var selectable in selection.Roster)
            {
                if (selectable == null || !selectable.isActiveAndEnabled)
                    continue;
                var unit = selectable.Unit;
                var text = DescribeOrders(unit.CurrentCommand, unit.PendingCommands.Count);
                if (text.Length == 0)
                    continue;
                var screen = viewCamera.WorldToScreenPoint(unit.transform.position + Vector3.up * UnitLabelHeight);
                if (screen.z <= 0f)
                    continue;
                GUI.Label(new Rect(screen.x - 60f, Screen.height - screen.y - 11f, 120f, 22f), text, unitLabelStyle);
            }
        }
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `Tools/run-tests.sh EditMode`
Expected: `total="94" passed="94"`, `EXIT=0`.

Run: `Tools/run-tests.sh PlayMode`
Expected: `total="50" passed="50"`, `EXIT=0`. (The scene's HUD has no `viewCamera`/`selection` yet; both are null-guarded, so `Scene_ContainsWiredPrototypeObjects_AndRunsWithoutErrors` still passes.)

- [ ] **Step 6: Commit**

```bash
git add Assets/_Project/Scripts/DebugUI Assets/_Project/Tests
git status --short
git commit -m "Show queued orders as a line with move markers and HUD labels

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Squad input, the `FriendlyUnit` prefab and the Prototype scene

This task changes `PlayerCommandInput`'s serialized fields, so the scene is re-wired in the same task to keep every test green at the commit.

**Files:**
- Modify: `Assets/_Project/Input/BlackglassControls.inputactions`
- Modify: `Assets/_Project/Scripts/Controls/InputActionUtility.cs`
- Modify: `Assets/_Project/Scripts/Controls/PlayerCommandInput.cs` (full rewrite below)
- Modify: `Assets/_Project/Scripts/DebugUI/PrototypeHud.cs` (control hints, drag box, `commandInput` reference)
- Rewrite: `Assets/_Project/Tests/PlayMode/PlayerCommandInputTests.cs`
- Modify: `Assets/_Project/Tests/PlayMode/PrototypeSceneTests.cs`
- Create (temporary, never committed): `Assets/_Project/Editor/SquadSceneBuilder.cs`
- Generated by the builder: `Assets/_Project/Prefabs/FriendlyUnit.prefab`, `Assets/_Project/Materials/{SelectionRing,QueueLine,MoveMarker}.mat`, changes to `Assets/_Project/Scenes/Prototype.unity`

**Interfaces:**
- Consumes: everything from Tasks 1–5.
- Produces:
  - Input actions `Commands/Modifier` (Shift), `Commands/Stop` (X), `Commands/ClearSelection` (Esc).
  - `InputActionUtility.IsPressed(InputActionReference) : bool`.
  - `PlayerCommandInput`: serialized fields `viewCamera`, `selection`, `tacticalPause`, `commandAction`, `pointerPositionAction`, `togglePauseAction`, `modifierAction`, `stopAction`, `clearSelectionAction`, `dragThresholdPixels`, `maxClickDistance`, `clickableLayers`, `groupSpacing`; `bool IsDragging`; `Rect DragRect`; `internal void Initialize(Camera, UnitSelection, TacticalPause, InputActionReference command, InputActionReference pointerPosition, InputActionReference togglePause, InputActionReference modifier, InputActionReference stop, InputActionReference clearSelection)`.
  - `PrototypeHud`: serialized `commandInput` (PlayerCommandInput).
  - Scene objects `FriendlyUnit_1`, `FriendlyUnit_2`, `FriendlyUnit_3` (prefab instances); `Systems` has `UnitSelection`.

- [ ] **Step 1: Add the input actions**

In `Assets/_Project/Input/BlackglassControls.inputactions`, in the `Commands` map:

Replace the `TogglePause` action line with it plus three new actions:

```json
                { "name": "TogglePause", "type": "Button", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9e04", "expectedControlType": "Button", "processors": "", "interactions": "", "initialStateCheck": false },
                { "name": "Modifier", "type": "Button", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9e05", "expectedControlType": "Button", "processors": "", "interactions": "", "initialStateCheck": false },
                { "name": "Stop", "type": "Button", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9e06", "expectedControlType": "Button", "processors": "", "interactions": "", "initialStateCheck": false },
                { "name": "ClearSelection", "type": "Button", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9e07", "expectedControlType": "Button", "processors": "", "interactions": "", "initialStateCheck": false }
```

Replace the space binding line with it plus four new bindings:

```json
                { "name": "", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9e13", "path": "<Keyboard>/space", "interactions": "", "processors": "", "groups": "", "action": "TogglePause", "isComposite": false, "isPartOfComposite": false },
                { "name": "", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9e14", "path": "<Keyboard>/leftShift", "interactions": "", "processors": "", "groups": "", "action": "Modifier", "isComposite": false, "isPartOfComposite": false },
                { "name": "", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9e15", "path": "<Keyboard>/rightShift", "interactions": "", "processors": "", "groups": "", "action": "Modifier", "isComposite": false, "isPartOfComposite": false },
                { "name": "", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9e16", "path": "<Keyboard>/x", "interactions": "", "processors": "", "groups": "", "action": "Stop", "isComposite": false, "isPartOfComposite": false },
                { "name": "", "id": "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9e17", "path": "<Keyboard>/escape", "interactions": "", "processors": "", "groups": "", "action": "ClearSelection", "isComposite": false, "isPartOfComposite": false }
```

Verify the file is valid JSON: `python -c "import json,sys; json.load(open('Assets/_Project/Input/BlackglassControls.inputactions'))" && echo OK` → `OK`.

- [ ] **Step 2: Add `IsPressed` to `InputActionUtility`**

In `Assets/_Project/Scripts/Controls/InputActionUtility.cs`, add after `Read<T>`:

```csharp

        public static bool IsPressed(InputActionReference reference) =>
            reference != null && reference.action != null && reference.action.IsPressed();
```

- [ ] **Step 3: Rewrite the failing input tests**

Replace the whole of `Assets/_Project/Tests/PlayMode/PlayerCommandInputTests.cs` with:

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
    public class PlayerCommandInputTests : InputTestFixture
    {
        static readonly Vector3 GroundPoint = new Vector3(2f, 0f, 2f);
        static readonly Vector3 OtherGroundPoint = new Vector3(-2f, 0f, 2f);

        Keyboard keyboard;
        Mouse mouse;
        TestWorld world;
        Camera viewCamera;
        SelectableUnit unitA;
        SelectableUnit unitB;
        SelectableUnit unitC;
        Health dummy;
        TacticalPause pause;
        UnitSelection selection;
        PlayerCommandInput input;

        public override void Setup()
        {
            base.Setup();
            keyboard = InputSystem.AddDevice<Keyboard>();
            mouse = InputSystem.AddDevice<Mouse>();
            world = new TestWorld();

            world.CreateEnvironment();
            unitA = world.CreateFriendly(new Vector3(-8f, 0f, -6f));
            unitB = world.CreateFriendly(new Vector3(-4f, 0f, -6f));
            unitC = world.CreateFriendly(new Vector3(4f, 0f, -6f));
            dummy = world.CreateDummy(new Vector3(6f, 0f, 6f));

            var cameraObject = world.Track(new GameObject("Camera"));
            cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 25f, -20f), Quaternion.Euler(50f, 0f, 0f));
            viewCamera = cameraObject.AddComponent<Camera>();

            var systems = world.Track(new GameObject("Systems"));
            systems.SetActive(false);
            pause = systems.AddComponent<TacticalPause>();
            selection = systems.AddComponent<UnitSelection>();
            selection.Initialize(unitA, unitB, unitC);
            var actions = TestControls.Load();
            input = systems.AddComponent<PlayerCommandInput>();
            input.Initialize(viewCamera, selection, pause,
                TestControls.Ref(actions, "Commands/Command"),
                TestControls.Ref(actions, "Commands/PointerPosition"),
                TestControls.Ref(actions, "Commands/TogglePause"),
                TestControls.Ref(actions, "Commands/Modifier"),
                TestControls.Ref(actions, "Commands/Stop"),
                TestControls.Ref(actions, "Commands/ClearSelection"));
            systems.SetActive(true);
        }

        public override void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
            base.TearDown();
        }

        Vector2 ScreenPointOf(Vector3 worldPoint) => viewCamera.WorldToScreenPoint(worldPoint);
        Vector2 ScreenPointOf(Component component) => ScreenPointOf(component.transform.position);

        IEnumerator LeftClickAt(Vector2 screenPoint)
        {
            Set(mouse.position, screenPoint);
            yield return null;
            Press(mouse.leftButton);
            yield return null;
            Release(mouse.leftButton);
            yield return null;
        }

        IEnumerator ShiftLeftClickAt(Vector2 screenPoint)
        {
            Press(keyboard.leftShiftKey);
            yield return null;
            yield return LeftClickAt(screenPoint);
            Release(keyboard.leftShiftKey);
            yield return null;
        }

        IEnumerator LeftDrag(Vector2 from, Vector2 to, bool shift = false)
        {
            if (shift)
                Press(keyboard.leftShiftKey);
            Set(mouse.position, from);
            yield return null;
            Press(mouse.leftButton);
            yield return null;
            Set(mouse.position, to);
            yield return null;
            Release(mouse.leftButton);
            yield return null;
            if (shift)
                Release(keyboard.leftShiftKey);
            yield return null;
        }

        IEnumerator Tap(KeyControl key)
        {
            Press(key);
            yield return null;
            Release(key);
            yield return null;
        }

        // Screen box around two units, with a margin.
        (Vector2 from, Vector2 to) BoxAround(Component first, Component second)
        {
            var a = ScreenPointOf(first);
            var b = ScreenPointOf(second);
            var margin = new Vector2(20f, 20f);
            return (Vector2.Min(a, b) - margin, Vector2.Max(a, b) + margin);
        }

        [UnityTest]
        public IEnumerator ClickFriendly_SelectsOnlyThatUnit()
        {
            yield return null;
            yield return LeftClickAt(ScreenPointOf(unitA));
            Assert.That(selection.Selected, Is.EqualTo(new[] { unitA }));

            yield return LeftClickAt(ScreenPointOf(unitB));
            Assert.That(selection.Selected, Is.EqualTo(new[] { unitB }));
            Assert.That(unitA.IsSelected, Is.False);
            Assert.That(unitA.Unit.CurrentCommand, Is.Null, "Selecting must not issue orders");
        }

        [UnityTest]
        public IEnumerator ShiftClickFriendly_AddsThenRemoves()
        {
            yield return null;
            yield return LeftClickAt(ScreenPointOf(unitA));
            yield return ShiftLeftClickAt(ScreenPointOf(unitB));
            Assert.That(selection.Selected, Is.EqualTo(new[] { unitA, unitB }));

            yield return ShiftLeftClickAt(ScreenPointOf(unitA));
            Assert.That(selection.Selected, Is.EqualTo(new[] { unitB }));
        }

        [UnityTest]
        public IEnumerator BoxDrag_SelectsTheUnitsInsideTheBox()
        {
            yield return null;
            var (from, to) = BoxAround(unitA, unitB);
            Set(mouse.position, from);
            yield return null;
            Press(mouse.leftButton);
            yield return null;
            Set(mouse.position, to);
            yield return null;
            Assert.That(input.IsDragging, Is.True);
            Assert.That(input.DragRect, Is.EqualTo(ScreenBox.FromCorners(from, to)));
            Release(mouse.leftButton);
            yield return null;

            Assert.That(input.IsDragging, Is.False);
            Assert.That(selection.Selected, Is.EquivalentTo(new[] { unitA, unitB }));
        }

        [UnityTest]
        public IEnumerator ShiftBoxDrag_AddsToTheSelection()
        {
            yield return null;
            yield return LeftClickAt(ScreenPointOf(unitC));
            var (from, to) = BoxAround(unitA, unitB);
            yield return LeftDrag(from, to, shift: true);

            Assert.That(selection.Selected, Is.EquivalentTo(new[] { unitA, unitB, unitC }));
        }

        [UnityTest]
        public IEnumerator EmptyBoxDrag_ClearsSelectionAndIssuesNoOrder()
        {
            yield return null;
            yield return LeftClickAt(ScreenPointOf(unitA));
            var start = ScreenPointOf(GroundPoint);
            yield return LeftDrag(start, start + new Vector2(40f, 0f));

            Assert.That(selection.Selected, Is.Empty);
            Assert.That(unitA.Unit.CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator Escape_ClearsSelection()
        {
            yield return null;
            yield return LeftClickAt(ScreenPointOf(unitA));
            yield return Tap(keyboard.escapeKey);
            Assert.That(selection.Selected, Is.Empty);
        }

        [UnityTest]
        public IEnumerator ClickGround_MovesOnlyTheSelectedUnits()
        {
            yield return null;
            yield return LeftClickAt(ScreenPointOf(unitA));
            yield return LeftClickAt(ScreenPointOf(GroundPoint));

            Assert.That(unitA.Unit.CurrentCommand, Is.TypeOf<MoveCommand>());
            var destination = ((MoveCommand)unitA.Unit.CurrentCommand).Destination;
            Assert.That(TestWorld.HorizontalDistance(destination, GroundPoint), Is.LessThan(0.1f));
            Assert.That(unitB.Unit.CurrentCommand, Is.Null);
            Assert.That(unitC.Unit.CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator ClickGround_WithNothingSelected_IssuesNothing()
        {
            yield return null;
            yield return LeftClickAt(ScreenPointOf(GroundPoint));
            Assert.That(unitA.Unit.CurrentCommand, Is.Null);
            Assert.That(unitB.Unit.CurrentCommand, Is.Null);
            Assert.That(unitC.Unit.CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator ClickDummy_SelectedUnitsAttackIt()
        {
            yield return null;
            yield return LeftClickAt(ScreenPointOf(unitA));
            yield return ShiftLeftClickAt(ScreenPointOf(unitB));
            yield return LeftClickAt(ScreenPointOf(dummy));

            foreach (var selected in new[] { unitA, unitB })
            {
                Assert.That(selected.Unit.CurrentCommand, Is.TypeOf<AttackCommand>(), selected.name);
                Assert.That(((AttackCommand)selected.Unit.CurrentCommand).Target, Is.SameAs(dummy));
            }
            Assert.That(unitC.Unit.CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator ShiftClickGround_AppendsTheOrder()
        {
            yield return null;
            yield return LeftClickAt(ScreenPointOf(unitA));
            yield return LeftClickAt(ScreenPointOf(GroundPoint));
            yield return ShiftLeftClickAt(ScreenPointOf(OtherGroundPoint));

            Assert.That(unitA.Unit.CurrentCommand, Is.TypeOf<MoveCommand>());
            Assert.That(unitA.Unit.PendingCommands, Has.Count.EqualTo(1));
            var queued = (MoveCommand)unitA.Unit.PendingCommands[0];
            Assert.That(TestWorld.HorizontalDistance(queued.Destination, OtherGroundPoint), Is.LessThan(0.1f));
        }

        [UnityTest]
        public IEnumerator X_StopsTheSelectedUnitsOnly()
        {
            yield return null;
            yield return LeftClickAt(ScreenPointOf(unitC));
            yield return LeftClickAt(ScreenPointOf(GroundPoint));
            yield return LeftClickAt(ScreenPointOf(unitA));
            yield return LeftClickAt(ScreenPointOf(GroundPoint));
            yield return ShiftLeftClickAt(ScreenPointOf(OtherGroundPoint));

            yield return Tap(keyboard.xKey);

            Assert.That(unitA.Unit.CurrentCommand, Is.Null);
            Assert.That(unitA.Unit.PendingCommands, Is.Empty);
            Assert.That(unitC.Unit.CurrentCommand, Is.TypeOf<MoveCommand>(), "An unselected unit was stopped");
        }

        [UnityTest]
        public IEnumerator X_WithNothingSelected_DoesNothing()
        {
            yield return null;
            yield return Tap(keyboard.xKey);
            Assert.That(selection.Selected, Is.Empty);
        }

        [UnityTest]
        public IEnumerator ShiftClickFriendly_WithUnitsSelected_NeverIssuesOrders()
        {
            yield return null;
            yield return LeftClickAt(ScreenPointOf(unitA));
            yield return LeftClickAt(ScreenPointOf(GroundPoint));
            var orderBefore = unitA.Unit.CurrentCommand;

            yield return ShiftLeftClickAt(ScreenPointOf(unitB));

            Assert.That(selection.Selected, Is.EqualTo(new[] { unitA, unitB }));
            Assert.That(unitA.Unit.CurrentCommand, Is.SameAs(orderBefore));
            Assert.That(unitA.Unit.PendingCommands, Is.Empty);
            Assert.That(unitB.Unit.CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator RightClickOnDummy_IssuesNothing()
        {
            yield return null;
            yield return LeftClickAt(ScreenPointOf(unitA));
            Set(mouse.position, ScreenPointOf(dummy));
            yield return null;
            Press(mouse.rightButton);
            yield return null;
            Release(mouse.rightButton);
            yield return null;

            Assert.That(unitA.Unit.CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator LeftClickOnSky_ChangesNothing()
        {
            yield return null;
            yield return LeftClickAt(ScreenPointOf(unitA));
            yield return LeftClickAt(ScreenPointOf(new Vector3(0f, 500f, 2000f)));

            Assert.That(unitA.Unit.CurrentCommand, Is.Null);
            Assert.That(selection.Selected, Is.EqualTo(new[] { unitA }));
        }

        [UnityTest]
        public IEnumerator Space_TogglesTacticalPause()
        {
            yield return null;
            yield return Tap(keyboard.spaceKey);
            Assert.That(pause.IsPaused, Is.True);
            yield return Tap(keyboard.spaceKey);
            Assert.That(pause.IsPaused, Is.False);
        }

        [UnityTest]
        public IEnumerator OrdersClickedWhilePaused_WaitUntilResume()
        {
            yield return null;
            pause.Pause();
            var start = unitA.transform.position;

            yield return LeftClickAt(ScreenPointOf(unitA));
            yield return LeftClickAt(ScreenPointOf(GroundPoint));
            yield return ShiftLeftClickAt(ScreenPointOf(OtherGroundPoint));
            yield return new WaitForSecondsRealtime(0.5f);

            Assert.That(selection.Selected, Is.EqualTo(new[] { unitA }), "Selection must work while paused");
            Assert.That(unitA.Unit.CurrentCommand, Is.TypeOf<MoveCommand>());
            Assert.That(unitA.Unit.PendingCommands, Has.Count.EqualTo(1));
            Assert.That(TestWorld.HorizontalDistance(unitA.transform.position, start), Is.LessThan(0.01f), "Unit moved while paused");

            pause.Resume();
            yield return TestWorld.WaitUntil(() => TestWorld.HorizontalDistance(unitA.transform.position, start) > 1f, 5f);
            Assert.That(TestWorld.HorizontalDistance(unitA.transform.position, start), Is.GreaterThan(1f));
        }
    }
}
#endif
```

- [ ] **Step 4: Run the input tests to verify they fail**

Run: `Tools/run-tests.sh PlayMode Blackglass.Tests.PlayerCommandInputTests`
Expected: `error CS1501: No overload for method 'Initialize' takes 9 arguments` (and `IsDragging`/`DragRect` missing), `EXIT=3`.

- [ ] **Step 5: Rewrite `PlayerCommandInput`**

Replace the whole of `Assets/_Project/Scripts/Controls/PlayerCommandInput.cs` with:

```csharp
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Blackglass
{
    /// <summary>
    /// Translates the player's input into requests. Left button: a click on a friendly unit selects it, a click
    /// anywhere else orders the selected units (attack the clicked target, or move to the clicked point), and a drag
    /// box-selects. Shift adds to the selection or queues the order. X stops the selected units, Esc clears the
    /// selection, Space toggles tactical pause. Contains no movement or combat rules.
    /// </summary>
    public sealed class PlayerCommandInput : MonoBehaviour
    {
        [SerializeField] Camera viewCamera;
        [SerializeField] UnitSelection selection;
        [SerializeField] TacticalPause tacticalPause;

        [Header("Input")]
        [SerializeField] InputActionReference commandAction;
        [SerializeField] InputActionReference pointerPositionAction;
        [SerializeField] InputActionReference togglePauseAction;
        [SerializeField] InputActionReference modifierAction;
        [SerializeField] InputActionReference stopAction;
        [SerializeField] InputActionReference clearSelectionAction;

        [Header("Tuning")]
        // A press that moves further than this is a box-selection drag, not a click.
        [SerializeField, Min(0f)] float dragThresholdPixels = ClickDragDetector.DefaultThresholdPixels;
        [SerializeField, Min(1f)] float maxClickDistance = 500f;
        [SerializeField] LayerMask clickableLayers = ~0;
        [SerializeField, Min(0.5f)] float groupSpacing = GroupOrders.DefaultSpacing;

        readonly List<CommandableUnit> selectedUnits = new List<CommandableUnit>();
        readonly List<SelectableUnit> boxedUnits = new List<SelectableUnit>();
        ClickDragDetector clickDetector;
        Vector2 pressPosition;

        /// <summary>True while the left button is held and has moved far enough to be a box selection.</summary>
        public bool IsDragging => clickDetector != null && clickDetector.IsDragging;

        /// <summary>The box being dragged, in screen pixels (origin bottom-left). Only meaningful while IsDragging.</summary>
        public Rect DragRect => ScreenBox.FromCorners(pressPosition, PointerPosition);

        internal void Initialize(Camera camera, UnitSelection unitSelection, TacticalPause pause,
            InputActionReference command, InputActionReference pointerPosition, InputActionReference togglePause,
            InputActionReference modifier, InputActionReference stop, InputActionReference clearSelection)
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
        }

        void Awake() => clickDetector = new ClickDragDetector(dragThresholdPixels);

        void OnEnable()
        {
            InputActionUtility.SetEnabled(true, commandAction, pointerPositionAction, togglePauseAction,
                modifierAction, stopAction, clearSelectionAction);
            if (commandAction != null)
            {
                commandAction.action.started += OnCommandPressed;
                commandAction.action.canceled += OnCommandReleased;
            }
            if (togglePauseAction != null)
                togglePauseAction.action.performed += OnTogglePause;
            if (stopAction != null)
                stopAction.action.performed += OnStop;
            if (clearSelectionAction != null)
                clearSelectionAction.action.performed += OnClearSelection;
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
            if (stopAction != null)
                stopAction.action.performed -= OnStop;
            if (clearSelectionAction != null)
                clearSelectionAction.action.performed -= OnClearSelection;
            InputActionUtility.SetEnabled(false, commandAction, pointerPositionAction, togglePauseAction,
                modifierAction, stopAction, clearSelectionAction);
        }

        void Update()
        {
            if (clickDetector.IsPressed)
                clickDetector.Track(PointerPosition);
        }

        Vector2 PointerPosition => InputActionUtility.Read<Vector2>(pointerPositionAction);
        bool ModifierHeld => InputActionUtility.IsPressed(modifierAction);

        void OnCommandPressed(InputAction.CallbackContext context)
        {
            pressPosition = PointerPosition;
            clickDetector.Press(pressPosition);
        }

        void OnCommandReleased(InputAction.CallbackContext context)
        {
            var wasPressed = clickDetector.IsPressed;
            var pointer = PointerPosition;
            if (clickDetector.Release(pointer))
                HandleClick(pointer);
            else if (wasPressed)
                SelectInBox(ScreenBox.FromCorners(pressPosition, pointer));
        }

        void OnTogglePause(InputAction.CallbackContext context)
        {
            if (tacticalPause != null)
                tacticalPause.Toggle();
        }

        void OnStop(InputAction.CallbackContext context) =>
            GroupOrders.Issue(SelectedUnits(), new StopCommand(), IssueMode.Replace);

        void OnClearSelection(InputAction.CallbackContext context)
        {
            if (selection != null)
                selection.Clear();
        }

        void HandleClick(Vector2 screenPoint)
        {
            if (viewCamera == null)
                return;

            // While paused no physics steps run, so push moved transforms to physics before raycasting.
            Physics.SyncTransforms();
            var ray = viewCamera.ScreenPointToRay(screenPoint);
            if (!Physics.Raycast(ray, out var hit, maxClickDistance, clickableLayers, QueryTriggerInteraction.Ignore))
                return;

            var friendly = hit.collider.GetComponentInParent<SelectableUnit>();
            if (friendly != null)
            {
                if (selection == null)
                    return;
                if (ModifierHeld)
                    selection.Toggle(friendly);
                else
                    selection.Select(friendly);
                return;
            }

            var command = CommandResolver.Resolve(hit.collider.GetComponentInParent<Health>(), hit.point);
            GroupOrders.Issue(SelectedUnits(), command, ModifierHeld ? IssueMode.Append : IssueMode.Replace, groupSpacing);
        }

        void SelectInBox(Rect box)
        {
            if (selection == null || viewCamera == null)
                return;
            boxedUnits.Clear();
            foreach (var unit in selection.Roster)
            {
                if (unit != null && unit.isActiveAndEnabled &&
                    ScreenBox.Contains(box, viewCamera.WorldToScreenPoint(unit.transform.position)))
                    boxedUnits.Add(unit);
            }
            if (ModifierHeld)
                selection.Add(boxedUnits);
            else
                selection.SetSelection(boxedUnits);
        }

        List<CommandableUnit> SelectedUnits()
        {
            selectedUnits.Clear();
            if (selection != null)
            {
                foreach (var selectable in selection.Selected)
                    selectedUnits.Add(selectable.Unit);
            }
            return selectedUnits;
        }
    }
}
```

- [ ] **Step 6: Finish the HUD: control hints and drag box**

In `Assets/_Project/Scripts/DebugUI/PrototypeHud.cs`:

Replace the `ControlHints` constant with:

```csharp
        const string ControlHints =
            "WASD: pan   Q/E: rotate   Right-drag: rotate/tilt   Wheel: zoom\n" +
            "Left-click unit: select (Shift: add/remove)   Left-drag: box select   Esc: clear selection\n" +
            "Left-click ground: move   Left-click dummy: attack   Shift: queue the order   X: stop\n" +
            "Space: tactical pause";
```

Add a field after `[SerializeField] UnitSelection selection;`:

```csharp
        [SerializeField] PlayerCommandInput commandInput;
```

In `OnGUI`, after `DrawUnitLabels();` add:

```csharp

            if (commandInput != null && commandInput.IsDragging)
                GUI.Box(ScreenBox.ToGuiRect(commandInput.DragRect, Screen.height), GUIContent.none);
```

- [ ] **Step 7: Run the input tests to verify they pass**

Run: `Tools/run-tests.sh PlayMode Blackglass.Tests.PlayerCommandInputTests`
Expected: `total="17" passed="17"`, `EXIT=0`.

(Do not run the scene tests yet: the scene is re-wired in Steps 8–10.)

- [ ] **Step 8: Update the scene tests**

Replace the whole of `Assets/_Project/Tests/PlayMode/PrototypeSceneTests.cs` with:

```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class PrototypeSceneTests : InputTestFixture
    {
        static readonly string[] FriendlyNames = { "FriendlyUnit_1", "FriendlyUnit_2", "FriendlyUnit_3" };

        CommandableUnit unit;
        Health dummy;
        TacticalPause pause;
        UnitSelection selection;

        // Loaded from each test rather than [UnitySetUp]: the scene must load after InputTestFixture has isolated the
        // input system, otherwise the scene's actions (shared InputActionAsset) leak into later fixtures.
        IEnumerator LoadScene()
        {
            yield return SceneManager.LoadSceneAsync("Prototype", LoadSceneMode.Single);
            yield return null;
            unit = GameObject.Find(FriendlyNames[0]).GetComponent<CommandableUnit>();
            dummy = Object.FindFirstObjectByType<Health>();
            pause = Object.FindFirstObjectByType<TacticalPause>();
            selection = Object.FindFirstObjectByType<UnitSelection>();
        }

        internal static CommandableUnit[] FindSquad() =>
            FriendlyNames.Select(n => GameObject.Find(n).GetComponent<CommandableUnit>()).ToArray();

        public override void TearDown()
        {
            Time.timeScale = 1f;
            DestroySceneObjects();
            base.TearDown();
        }

        // The scene's components share the project's InputActionAsset; destroy them while the input isolation is
        // still active so their actions are disabled and do not leak into later tests.
        internal static void DestroySceneObjects()
        {
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
                Object.DestroyImmediate(root);
        }

        [UnityTest]
        public IEnumerator Scene_ContainsWiredSquad_AndRunsWithoutErrors()
        {
            yield return LoadScene();
            var friendlies = Object.FindObjectsByType<SelectableUnit>(FindObjectsSortMode.None);
            Assert.That(friendlies.Select(f => f.name), Is.EquivalentTo(FriendlyNames));
            Assert.That(selection, Is.Not.Null, "UnitSelection missing");
            Assert.That(selection.Roster, Is.EquivalentTo(friendlies));
            Assert.That(selection.Selected, Is.Empty, "Nothing should be selected at start");
            foreach (var friendly in friendlies)
            {
                Assert.That(friendly.GetComponent<SelectionIndicator>(), Is.Not.Null, friendly.name);
                Assert.That(friendly.GetComponent<CommandQueueView>(), Is.Not.Null, friendly.name);
                Assert.That(friendly.GetComponentsInChildren<Collider>(true), Has.Length.EqualTo(1),
                    $"{friendly.name}: only the capsule may have a collider, so debug visuals never block clicks");
            }
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
            yield return LoadScene();
            var destination = new Vector3(6f, 0f, 6f);
            Assert.That(unit.Issue(new MoveCommand(destination)), Is.True);

            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 15f);

            Assert.That(unit.CurrentCommand, Is.Null, "Move did not finish in time");
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, destination), Is.LessThan(0.3f));
        }

        [UnityTest]
        public IEnumerator Attack_DestroysTrainingDummy()
        {
            yield return LoadScene();
            Assert.That(unit.Issue(new AttackCommand(dummy)), Is.True);

            yield return TestWorld.WaitUntil(() => !dummy.IsAlive, 20f);

            Assert.That(dummy.IsAlive, Is.False);
            Assert.That(dummy.gameObject.activeSelf, Is.False);
        }

        [UnityTest]
        public IEnumerator Pause_HoldsTheUnitUntilResume()
        {
            yield return LoadScene();
            var start = unit.transform.position;
            pause.Pause();
            unit.Issue(new MoveCommand(new Vector3(-10f, 0f, 0f)));

            yield return new WaitForSecondsRealtime(1f);
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, start), Is.LessThan(0.01f));

            pause.Resume();
            yield return TestWorld.WaitUntil(() => TestWorld.HorizontalDistance(unit.transform.position, start) > 1f, 5f);
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, start), Is.GreaterThan(1f));
        }

        [UnityTest]
        public IEnumerator GroupMove_TheSquadArrivesAtDistinctPoints()
        {
            yield return LoadScene();
            var squad = FindSquad();
            var destination = new Vector3(-6f, 0f, 0f);
            Assert.That(GroupOrders.Issue(squad, new MoveCommand(destination), IssueMode.Replace), Is.EqualTo(3));

            yield return TestWorld.WaitUntil(() => squad.All(u => u.CurrentCommand == null), 15f);

            Assert.That(squad.All(u => u.CurrentCommand == null), Is.True, "Group move did not finish in time");
            for (var i = 0; i < squad.Length; i++)
            for (var j = i + 1; j < squad.Length; j++)
                Assert.That(TestWorld.HorizontalDistance(squad[i].transform.position, squad[j].transform.position), Is.GreaterThan(1f));
        }
    }

    public class PrototypeSceneInputTests : InputTestFixture
    {
        public override void TearDown()
        {
            PrototypeSceneTests.DestroySceneObjects();
            base.TearDown();
        }

        static IEnumerator LoadScene()
        {
            yield return SceneManager.LoadSceneAsync("Prototype", LoadSceneMode.Single);
            yield return null;
        }

        IEnumerator LeftClickAt(Mouse mouse, Vector2 screenPoint)
        {
            Set(mouse.position, screenPoint);
            yield return null;
            Press(mouse.leftButton);
            yield return null;
            Release(mouse.leftButton);
            yield return null;
        }

        [UnityTest]
        public IEnumerator ClickUnitThenDummy_InScene_OnlyThatUnitAttacks()
        {
            var mouse = InputSystem.AddDevice<Mouse>();
            InputSystem.AddDevice<Keyboard>();
            yield return LoadScene();
            var squad = PrototypeSceneTests.FindSquad();
            var dummy = Object.FindFirstObjectByType<Health>();

            yield return LeftClickAt(mouse, Camera.main.WorldToScreenPoint(squad[0].transform.position));
            yield return LeftClickAt(mouse, Camera.main.WorldToScreenPoint(dummy.transform.position));

            Assert.That(squad[0].CurrentCommand, Is.TypeOf<AttackCommand>());
            Assert.That(squad[1].CurrentCommand, Is.Null);
            Assert.That(squad[2].CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator BoxSelectSquadThenClickGround_InScene_AllThreeMove()
        {
            var mouse = InputSystem.AddDevice<Mouse>();
            InputSystem.AddDevice<Keyboard>();
            yield return LoadScene();
            var squad = PrototypeSceneTests.FindSquad();
            var screenPoints = squad.Select(u => (Vector2)Camera.main.WorldToScreenPoint(u.transform.position)).ToArray();
            var margin = new Vector2(25f, 25f);
            var from = screenPoints.Aggregate(Vector2.Min) - margin;
            var to = screenPoints.Aggregate(Vector2.Max) + margin;

            Set(mouse.position, from);
            yield return null;
            Press(mouse.leftButton);
            yield return null;
            Set(mouse.position, to);
            yield return null;
            Release(mouse.leftButton);
            yield return null;
            Assert.That(Object.FindFirstObjectByType<UnitSelection>().Selected, Has.Count.EqualTo(3));

            yield return LeftClickAt(mouse, Camera.main.WorldToScreenPoint(new Vector3(-6f, 0f, 0f)));

            foreach (var member in squad)
                Assert.That(member.CurrentCommand, Is.TypeOf<MoveCommand>(), member.name);
        }

        [UnityTest]
        public IEnumerator HoldingW_InScene_PansTheCameraRig()
        {
            var keyboard = InputSystem.AddDevice<Keyboard>();
            yield return LoadScene();
            var rig = Object.FindFirstObjectByType<TacticalCameraController>();
            var start = rig.transform.position;

            Press(keyboard.wKey);
            yield return new WaitForSecondsRealtime(0.2f);
            Release(keyboard.wKey);
            yield return null;

            Assert.That(rig.transform.position.z, Is.GreaterThan(start.z + 0.5f));
        }
    }
}
#endif
```

- [ ] **Step 9: Create the temporary scene builder**

`Assets/_Project/Editor/SquadSceneBuilder.cs`:

```csharp
// TEMPORARY: builds the FriendlyUnit prefab and adds the squad to Prototype.unity, then this file is deleted
// (never committed).
using System;
using System.Linq;
using Blackglass;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

public static class SquadSceneBuilder
{
    const string ScenePath = "Assets/_Project/Scenes/Prototype.unity";
    const string PrefabPath = "Assets/_Project/Prefabs/FriendlyUnit.prefab";
    const string ControlsPath = "Assets/_Project/Input/BlackglassControls.inputactions";

    static readonly Vector3[] SpawnPoints =
    {
        new Vector3(-12f, 1f, -10f),
        new Vector3(-10f, 1f, -10f),
        new Vector3(-8f, 1f, -10f),
    };

    public static void Build()
    {
        // Open the scene first: the prefab's temporary source object is created in it and destroyed before saving.
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        if (!AssetDatabase.IsValidFolder("Assets/_Project/Prefabs"))
            AssetDatabase.CreateFolder("Assets/_Project", "Prefabs");
        var ringMaterial = CreateUnlitMaterial("SelectionRing", new Color(0.2f, 0.9f, 0.3f));
        var lineMaterial = CreateUnlitMaterial("QueueLine", Color.white);
        var markerMaterial = CreateUnlitMaterial("MoveMarker", new Color(1f, 0.85f, 0.1f));
        var unitMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/_Project/Materials/Unit.mat");
        if (unitMaterial == null)
            throw new Exception("Unit.mat not found");
        var prefab = CreateFriendlyUnitPrefab(unitMaterial, ringMaterial, lineMaterial, markerMaterial);

        var oldUnit = GameObject.Find("PlayerUnit");
        if (oldUnit == null)
            throw new Exception("PlayerUnit not found in " + ScenePath);
        Object.DestroyImmediate(oldUnit);

        var squad = new SelectableUnit[SpawnPoints.Length];
        for (var i = 0; i < SpawnPoints.Length; i++)
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            instance.name = $"FriendlyUnit_{i + 1}";
            instance.transform.position = SpawnPoints[i];
            squad[i] = instance.GetComponent<SelectableUnit>();
        }

        var systems = GameObject.Find("Systems");
        if (systems == null)
            throw new Exception("Systems not found in " + ScenePath);
        var selection = systems.AddComponent<UnitSelection>();
        var serializedSelection = new SerializedObject(selection);
        var roster = serializedSelection.FindProperty("roster");
        roster.arraySize = squad.Length;
        for (var i = 0; i < squad.Length; i++)
            roster.GetArrayElementAtIndex(i).objectReferenceValue = squad[i];
        serializedSelection.ApplyModifiedPropertiesWithoutUndo();

        var input = systems.GetComponent<PlayerCommandInput>();
        SetReference(input, "selection", selection);
        SetReference(input, "modifierAction", ActionReference("Commands/Modifier"));
        SetReference(input, "stopAction", ActionReference("Commands/Stop"));
        SetReference(input, "clearSelectionAction", ActionReference("Commands/ClearSelection"));
        var viewCamera = (Camera)new SerializedObject(input).FindProperty("viewCamera").objectReferenceValue;
        if (viewCamera == null)
            throw new Exception("PlayerCommandInput has no viewCamera");

        var hud = systems.GetComponent<PrototypeHud>();
        SetReference(hud, "viewCamera", viewCamera);
        SetReference(hud, "selection", selection);
        SetReference(hud, "commandInput", input);

        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene))
            throw new Exception("Failed to save " + ScenePath);
        AssetDatabase.SaveAssets();
        Debug.Log($"[SquadSceneBuilder] Built {PrefabPath} and updated {ScenePath}");
    }

    static GameObject CreateFriendlyUnitPrefab(Material unitMaterial, Material ringMaterial, Material lineMaterial, Material markerMaterial)
    {
        var unit = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        unit.name = "FriendlyUnit";
        unit.GetComponent<Renderer>().sharedMaterial = unitMaterial;
        unit.AddComponent<UnitMover>();
        unit.GetComponent<NavMeshAgent>().baseOffset = 1f;
        unit.AddComponent<UnitAttacker>();
        unit.AddComponent<CommandableUnit>();
        unit.AddComponent<SelectableUnit>();

        // Selection ring: a flat disc just above the ground under the capsule, hidden until selected. No collider.
        var ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        ring.name = "SelectionRing";
        Object.DestroyImmediate(ring.GetComponent<Collider>());
        ring.transform.SetParent(unit.transform, false);
        ring.transform.localPosition = new Vector3(0f, -0.97f, 0f);
        ring.transform.localScale = new Vector3(1.4f, 0.01f, 1.4f);
        var ringRenderer = ring.GetComponent<Renderer>();
        ringRenderer.sharedMaterial = ringMaterial;
        ringRenderer.shadowCastingMode = ShadowCastingMode.Off;
        ringRenderer.receiveShadows = false;
        ring.SetActive(false);
        var indicator = unit.AddComponent<SelectionIndicator>();
        SetReference(indicator, "ring", ring);

        var line = unit.AddComponent<LineRenderer>();
        line.sharedMaterial = lineMaterial;
        line.widthMultiplier = 0.08f;
        line.positionCount = 0;
        line.useWorldSpace = true;
        line.shadowCastingMode = ShadowCastingMode.Off;
        line.receiveShadows = false;
        var view = unit.AddComponent<CommandQueueView>();
        SetReference(view, "markerMaterial", markerMaterial);

        AssetDatabase.DeleteAsset(PrefabPath);
        var prefab = PrefabUtility.SaveAsPrefabAsset(unit, PrefabPath);
        Object.DestroyImmediate(unit);
        if (prefab == null)
            throw new Exception("Failed to save " + PrefabPath);
        return prefab;
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

    static void SetReference(Object target, string field, Object value)
    {
        var serialized = new SerializedObject(target);
        var property = serialized.FindProperty(field) ?? throw new Exception($"{target.GetType().Name}.{field} not found");
        property.objectReferenceValue = value;
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }
}
```

- [ ] **Step 10: Run the builder, then delete it**

```bash
"/c/Program Files/Unity/Hub/Editor/6000.3.25f1/Editor/Unity.exe" -batchmode -quit -projectPath "$(pwd -W)" -executeMethod SquadSceneBuilder.Build -logFile "$(pwd -W)/Logs/BuildSquadScene.log"; echo "EXIT=$?"
grep -E '\[SquadSceneBuilder\]|error CS|Exception' Logs/BuildSquadScene.log | head -20
grep -c "FriendlyUnit_" Assets/_Project/Scenes/Prototype.unity
grep -c "PlayerUnit" Assets/_Project/Scenes/Prototype.unity
```

Expected: `EXIT=0`; the log contains `[SquadSceneBuilder] Built Assets/_Project/Prefabs/FriendlyUnit.prefab and updated Assets/_Project/Scenes/Prototype.unity` and no exceptions; the scene mentions `FriendlyUnit_` (3 or more lines) and `PlayerUnit` 0 times.

Then delete the builder:

```bash
rm -r Assets/_Project/Editor Assets/_Project/Editor.meta
```

- [ ] **Step 11: Run all tests to verify they pass**

Run: `Tools/run-tests.sh EditMode` → expected `total="94" passed="94"`, `EXIT=0`.
Run: `Tools/run-tests.sh PlayMode` → expected `total="62" passed="62"`, `EXIT=0` (50 − 7 old input tests + 17 new input tests − 6 old scene tests + 8 new scene tests).

The run also recompiles without the deleted builder, which confirms nothing depends on it.

- [ ] **Step 12: Commit**

```bash
git add Assets/_Project
git status --short   # must NOT contain Assets/_Project/Editor
git commit -m "Control a three-unit squad with selection, queued orders and stop

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Decision records and final verification

**Files:**
- Modify: `Docs/Decisions.md`

**Interfaces:**
- Consumes: everything above.
- Produces: decision records 003 (note), 006 and 008 (updated), 010 (new).

- [ ] **Step 1: Update decision 003**

In `Docs/Decisions.md`, at the end of the **Implications** bullet of `## 003 — Repository and folder layout`, append the sentence:

```
`Prefabs/` was added on 2026-10-04 for the first prefab (`FriendlyUnit`).
```

- [ ] **Step 2: Replace decision 006**

Replace the whole `## 006 — Unit commands are data; units execute them` section (heading through its **Implications** bullet) with:

```markdown
## 006 — Unit commands are data; units execute them

- **Decided:** Gameplay orders are small immutable objects (`MoveCommand`, `AttackCommand`, `StopCommand`, base `UnitCommand`) passed to `CommandableUnit.Issue(command, mode)`. The unit and its components (`UnitMover`, `UnitAttacker`) decide how to carry them out.
- **Queue (Phase 2, 2026-10-04):** each unit keeps a `CommandQueue`: the current order plus pending ones.
  - `IssueMode.Replace` (the default) drops all orders and starts the new one now. `IssueMode.Append` runs it after the pending ones, or now if the unit is idle.
  - `StopCommand` halts the unit and clears every order, whatever the mode.
  - `Issue` checks first and returns false without changing anything when an order can't be carried out: no walkable point within 2 m of a move destination, or a dead or inactive attack target.
  - When the current order finishes, the next pending one starts in the same frame. A pending order that can no longer start (its target already died) is skipped.
  - Tactical pause needs no special handling: orders issued while paused are queued normally and run once simulation time moves.
- **Groups:** `GroupOrders.Issue(units, command, mode)` gives one order to several units. Moves are spread over a hexagonal lattice 1.5 m apart (`GroupMoveOffsets`); a slot off the NavMesh falls back to the clicked point.
- **Why:** Player input, groups, AI and scripts all produce the same objects without knowing movement or combat rules. Commands stay testable and loggable. Keeping the queue inside `CommandableUnit` keeps one entry point for orders.
- **Rejected:** Classic `Execute(unit)` / `Begin/Tick/Cancel` commands, which would put long-running chase/attack logic into command classes. Direct methods on the unit (`MoveTo`, `Attack`), which leave no command value to queue or send from AI. A separate queue component feeding `Issue`, which would mean two entry points coordinating by polling.
- **Implications:** Interact will be a new `UnitCommand` subclass with cases in `CommandableUnit`. Weapon choice (a later phase) would be data on `AttackCommand`. There is no change event; debug views read `CurrentCommand`/`PendingCommands` each frame.
```

- [ ] **Step 3: Replace decision 008**

Replace the whole `## 008 — Input mapping and click vs drag` section with:

```markdown
## 008 — Input mapping and click vs drag

- **Decided:** Project-wide actions live in `Assets/_Project/Input/BlackglassControls.inputactions`, with two maps:
  - **Camera:** Pan (WASD), Rotate (Q/E), RotateDrag (right button), PointerPosition, Zoom (wheel).
  - **Commands:** Command (left button), PointerPosition, TogglePause (Space), Modifier (Shift), Stop (X), ClearSelection (Esc).
- **Left button** (Phase 2, 2026-10-04): a quick click (≤ 6 px of movement) acts on what is under the cursor:
  - a friendly unit (`SelectableUnit`) is selected; with Shift it is added or removed;
  - anything else orders the selected units: Attack on a living `Health`, otherwise Move. Shift queues the order instead of replacing.
  - A left-drag box-selects the roster units inside the box (Shift adds; an empty box clears).
- **Right button** is camera-only: right-drag rotates (horizontal) and tilts (vertical, 25°–85°). `ClickDragDetector` holds the click-versus-drag rule for both buttons.
- **Why:** The owner chose left-click commands (2026-10-04), then chose to keep them when selection arrived: one button for "act on what I click", the other for the camera. This is also the convention of party-based tactical-pause games.
- **Consequences:** Clicking the ground is Move, so it can't clear the selection; Esc does. Clicking a friendly always selects it, so a move can't be ordered onto a spot where a friendly stands.
- **Implications:** Input components only create commands, change the selection or call `TacticalPause`. They never touch movement or combat. Rebinding means editing the actions asset. The keys 1–9 are still free; they may be wanted for both weapon choice and control groups later.
```

- [ ] **Step 4: Add decision 010**

Append to the end of `Docs/Decisions.md`:

```markdown

## 010 — Unit selection

- **Decided:** `UnitSelection` (a component on `Systems`, wired through the Inspector) holds the selected units and the **roster** of units the player controls. `SelectableUnit` marks a unit the player may select and carries its `IsSelected` state; `SelectionIndicator` shows a ring. Selection holds state only: `PlayerCommandInput` decides what to select, and orders go to the selected units through `GroupOrders`. Disabled or destroyed units drop out of the selection.
- **Why:** The roster gives box selection its candidate list without `Find*` lookups, singletons or a static registry. Keeping selection separate from commands lets AI and scripts order units without any selection.
- **Rejected:** Finding units with `FindObjectsByType` at drag time (global lookup, against 007/009 wiring rules). A static registry of selectable units (global mutable state). Storing selection on the input component (the HUD and later systems need to read it).
- **Implications:** Units spawned at runtime call `UnitSelection.AddToRoster`. Enemies and AI units are `CommandableUnit`s without `SelectableUnit`. Control groups, if wanted, would be saved lists of roster units.
```

- [ ] **Step 5: Final verification**

Run both suites and check the logs for exceptions:

```bash
Tools/run-tests.sh EditMode
Tools/run-tests.sh PlayMode
grep -cE "Exception|error CS" Logs/TestRun-EditMode.log Logs/TestRun-PlayMode.log
git status --short
```

Expected: EditMode `total="94" passed="94"`, PlayMode `total="62" passed="62"`, both `EXIT=0`. The exception count may be non-zero only for lines from expected test logs; open the log and confirm every match belongs to a passing test's expected log (`LogAssert.Expect`) or to Unity's own startup messages. `git status --short` shows only `Docs/Decisions.md`.

Then walk the owner's 13-item validation checklist (spec §6) against the test names and note which items need the owner's manual play test.

- [ ] **Step 6: Commit**

```bash
git add Docs/Decisions.md
git commit -m "Record queue, selection and input mapping decisions

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```
