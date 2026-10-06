# Phase 9 — Mission objectives and extraction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A generated mission has required objectives (eliminate the hostile group, hack a terminal) that must be completed before an extraction zone opens; a living squad member entering it ends the mission in success, the squad dying in failure.

**Architecture:** A plain-C# `MissionRuntime` (phase machine) owns a list of `MissionObjective`s (eliminate, interact, reach-zone) and is ticked by `MissionDirector`. A pure `ObjectivePlacer` turns the generated `MissionLayout` into an `ObjectivePlan` of tiles (own seed stream); `MissionContent` puts the terminal (before the NavMesh bake), the extraction zone and markers into the mission root and builds the runtime. Interaction is an ordinary `InteractCommand` run by `CommandableUnit` through a small `UnitInteractor` against a `MissionInteractable`; input reaches it through the existing `PlayerCommandInput.Act` path.

**Tech Stack:** Unity 6.3 LTS (6000.3.25f1), C#, Input System 1.20, AI Navigation package (already installed), NUnit via the Unity Test Framework. No new packages.

**Spec:** `Docs/superpowers/specs/2026-10-06-mission-objectives-design.md` (read it first; this plan implements it). Project rules: `CLAUDE.md`. Decision records: `Docs/Decisions.md` (006 commands, 007 pause, 013 health/time, 019 cover, 024 controller input, 026 procedural missions).

## Global Constraints

- Work on a new branch `mission-objectives` created from `main` (f10f267 plus the spec commit 95f3097). Commit after every task. End commit messages with the line `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>` (a subagent on another model may keep its own truthful trailer).
- Unity batch tests: `Tools/run-tests.sh EditMode|PlayMode "<filter>"` (the Editor must be closed; one Unity instance per project, so run tests strictly one at a time and never while another run or an implementer is using Unity). Exit 0 = all passed, 1 = compile error, 2 = test failures. Do not start a test run when the machine is low on memory.
- EditMode tests get no `Awake`/`OnEnable`/`Update`; anything needing them is a PlayMode test. Assemble multi-component test objects on an inactive GameObject when `OnEnable` ordering matters (`TestWorld.CreateFighter`).
- Never use `??` or `?.` on `UnityEngine.Object` references (a destroyed or missing component is not a C# null): use `== null` / `!= null`.
- Colliders created at runtime are invisible to raycasts until `Physics.SyncTransforms()` (auto sync is off); `NavMesh` carving is not used.
- Simulation code advances only while `SimulationTime.IsRunning` and uses scaled `Time.deltaTime`; UI, input, camera and the tactical cursor use unscaled time.
- Layout data is integer tiles (1 tile = 1 m); floats only when converting to world positions. All mission randomness is `SeededRandom`; never `UnityEngine.Random` or `System.Random`.
- The golden layout hashes in `MissionGeneratorTests` must NOT change: nothing in this plan may alter `MissionGenerator`, `SeededRandom.ForAttempt` output or `MissionLayout.Hash`.
- Do not modify the `Prototype` scene or its tests. Existing tests must keep passing unmodified, except where a task names the change.
- No gameplay code may name a device or a physical button (a test scans for it); prompt text comes from `PromptResolver`.
- Placeholder primitives and the existing placeholder materials only; no new packages; no save/load, fog of war, loot, minimap, or any other item in the spec's out-of-scope list. Do not start Phase 10.
- Numbers (prototype data): interaction range 1.8 m, duration 2 s, extraction radius 2 m, context-Confirm radius 3 m, cursor snap to a terminal 1.5 m, terminal 0.8 m wide and 1.2 m high, `guardCount` 2, `extractionUnits` 1.
- Before Task 1 the controller runs the full EditMode and PlayMode suites once and records the baselines (project memory expects about 595 and 588); later tasks report their own pass counts and the final run is compared with these baselines. Do not copy a total from a plan or an old note.

## Review Focus

Failure modes the spec implies but a straight reading of the tasks might not exercise; each has a test in the named task:

1. The terminal disappears (destroyed or disabled) while a unit is walking to it, working on it, or has Interact queued: the order ends, the unit goes idle, the next queued order runs, nothing throws (Task 4).
2. A second unit is ordered to the terminal while the first works: the immediate order is refused, a queued one fails cleanly on arrival, the first unit is undisturbed (Task 4).
3. F6/F7 regeneration while an interaction is in progress and while paused: no stale claim, registry entry, zone, marker or runtime survives (Task 7).
4. A layout with no valid terminal tile fails that attempt and retries; settings with `hackTerminal` or `eliminateHostiles` off, or both off, still produce a playable mission (Tasks 5 and 7).
5. Context Confirm / R with no terminal in reach, or with the terminal already completed, does nothing harmful; Confirm with a hostile around still attacks (Task 8).

---

## File Structure

New, `Assets/_Project/Scripts/Mission/`: `MissionObjective.cs` (enums + abstract base), `MissionRuntime.cs` (+ `MissionPhase`), `EliminateHostilesObjective.cs`, `ReachZoneObjective.cs`, `InteractObjective.cs`, `MissionInteractable.cs`, `InteractableRegistry.cs`, `ObjectivePlan.cs`, `ObjectivePlacer.cs`, `MissionContent.cs`, `ObjectiveMarker.cs`, `MissionHud.cs` (+ `MissionHudText`).
New: `Scripts/Units/UnitInteractor.cs`.
Changed: `Commands/UnitCommands.cs` (+`InteractCommand`), `Units/CommandableUnit.cs`, `Mission/SeededRandom.cs`, `Mission/MissionSettings.cs`, `Mission/MissionBuilder.cs`, `Mission/MissionSpawner.cs`, `Mission/MissionNavigation.cs`, `Mission/MissionSlots.cs`, `Mission/MissionDirector.cs`, `Input/PointerTarget.cs`, `Input/PointerTargetResolver.cs`, `Input/TacticalCursor.cs`, `Controls/CommandResolver.cs`, `Controls/PlayerCommandInput.cs`, `DebugUI/PrototypeHud.cs`, `DebugUI/CommandQueueView.cs`, `DebugUI/TacticalCursorView.cs`, `Input/BlackglassControls.inputactions`, scene `Scenes/ProceduralMission.unity`, `Docs/Decisions.md`.
Tests: `Tests/EditMode/` and `Tests/PlayMode/` (named per task).

Dependency order: 1 (core) → 2 (objectives) → 3 (interaction capability) → 4 (unit order) → 5 (placement) → 6 (content and pipeline) → 7 (runtime in the director) → 8 (input) → 9 (HUD and markers) → 10 (scene wiring and scene tests) → 11 (docs and final verification). Tasks 1–3 and 5 are independent of each other's code except as stated in each task's Interfaces.

---

### Task 1: Mission objective base and MissionRuntime

**Files:**
- Create: `Assets/_Project/Scripts/Mission/MissionObjective.cs`
- Create: `Assets/_Project/Scripts/Mission/MissionRuntime.cs`
- Test: `Assets/_Project/Tests/EditMode/MissionRuntimeTests.cs`

**Interfaces:**
- Produces:
  - `enum ObjectiveType { EliminateHostiles, Interact, ReachZone }`, `enum ObjectiveState { Inactive, Active, Completed, Failed }`.
  - `abstract class MissionObjective`: ctor `(string id, ObjectiveType type, string title, bool isRequired)`; `Id`, `Type`, `Title`, `IsRequired`, `State`, `IsKnown`, `virtual bool HasTarget`, `virtual Vector3 TargetPosition`, `event Action<MissionObjective> StateChanged`; `void SetKnown(bool)`, `void Activate()`, `void Evaluate()`, `void EndMission()`, `abstract string Describe()`; protected `OnActivated()`, `OnEvaluate()`, `OnMissionEnded()`, `Complete()`, `Fail()` (both act only from Active).
  - `enum MissionPhase { Inactive, Active, ExtractionOpen, Success, Failure }`.
  - `sealed class MissionRuntime`: ctor `(IEnumerable<MissionObjective> goals, MissionObjective extraction, IReadOnlyList<Health> squad)` (the extraction must be non-null and not required); `Phase`, `IsOver`, `IsDetached`, `Objectives` (goals then the extraction last), `Extraction`, `LivingSquad`, `event Action<MissionPhase> PhaseChanged`, `event Action<MissionObjective> ObjectiveChanged`, `Start()`, `Tick()`, `Detach()`.

- [ ] **Step 1: Create the branch**

```bash
cd /f/Programs/ProjectBlackglass
git checkout -b mission-objectives
```

- [ ] **Step 2: Write the failing tests**

`Assets/_Project/Tests/EditMode/MissionRuntimeTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class MissionRuntimeTests
    {
        internal sealed class FakeObjective : MissionObjective
        {
            public FakeObjective(string id, bool required) : base(id, ObjectiveType.Interact, id, required) { }

            public bool CompleteNow;
            public bool FailNow;
            public bool Ended;

            public override string Describe() => Id;

            protected override void OnEvaluate()
            {
                if (FailNow)
                    Fail();
                else if (CompleteNow)
                    Complete();
            }

            protected override void OnMissionEnded() => Ended = true;
        }

        readonly List<GameObject> hosts = new List<GameObject>();
        Health[] squad;
        FakeObjective first;
        FakeObjective second;
        FakeObjective extraction;

        [SetUp]
        public void SetUp()
        {
            squad = new[] { NewUnit("A"), NewUnit("B") };
            first = new FakeObjective("first", true);
            second = new FakeObjective("second", true);
            extraction = new FakeObjective("extraction", false);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var host in hosts)
            {
                if (host != null)
                    Object.DestroyImmediate(host);
            }
            hosts.Clear();
        }

        Health NewUnit(string name)
        {
            var host = new GameObject(name);
            hosts.Add(host);
            return host.AddComponent<Health>();
        }

        static void Kill(Health unit) => unit.TakeDamage(unit.Max);

        MissionRuntime Make(params MissionObjective[] goals)
        {
            var runtime = new MissionRuntime(goals, extraction, squad);
            runtime.Start();
            return runtime;
        }

        [Test]
        public void Start_ActivatesTheGoals_ButNotTheExtraction()
        {
            var runtime = Make(first, second);

            Assert.That(runtime.Phase, Is.EqualTo(MissionPhase.Active));
            Assert.That(first.State, Is.EqualTo(ObjectiveState.Active));
            Assert.That(second.State, Is.EqualTo(ObjectiveState.Active));
            Assert.That(extraction.State, Is.EqualTo(ObjectiveState.Inactive));
            Assert.That(runtime.Objectives, Is.EqualTo(new MissionObjective[] { first, second, extraction }));
        }

        [Test]
        public void BeforeStart_ThePhaseIsInactive_AndTickDoesNothing()
        {
            var runtime = new MissionRuntime(new[] { first }, extraction, squad);
            first.CompleteNow = true;

            runtime.Tick();

            Assert.That(runtime.Phase, Is.EqualTo(MissionPhase.Inactive));
            Assert.That(first.State, Is.EqualTo(ObjectiveState.Inactive));
        }

        [Test]
        public void ExtractionStaysLocked_UntilEveryRequiredObjectiveIsComplete()
        {
            var runtime = Make(first, second);
            first.CompleteNow = true;
            extraction.CompleteNow = true;

            runtime.Tick();

            Assert.That(first.State, Is.EqualTo(ObjectiveState.Completed));
            Assert.That(runtime.Phase, Is.EqualTo(MissionPhase.Active));
            Assert.That(extraction.State, Is.EqualTo(ObjectiveState.Inactive), "an unlocked-too-early extraction must stay inactive");
        }

        [Test]
        public void ExtractionOpens_WhenTheLastRequiredObjectiveCompletes()
        {
            var runtime = Make(first, second);
            first.CompleteNow = true;
            second.CompleteNow = true;

            runtime.Tick();

            Assert.That(runtime.Phase, Is.EqualTo(MissionPhase.ExtractionOpen));
            Assert.That(extraction.State, Is.EqualTo(ObjectiveState.Active));
        }

        [Test]
        public void TheMissionSucceeds_WhenTheExtractionCompletes_AndEveryObjectiveIsToldTheMissionEnded()
        {
            var runtime = Make(first);
            first.CompleteNow = true;
            runtime.Tick();
            extraction.CompleteNow = true;

            runtime.Tick();

            Assert.That(runtime.Phase, Is.EqualTo(MissionPhase.Success));
            Assert.That(runtime.IsOver, Is.True);
            Assert.That(first.Ended && extraction.Ended, Is.True);
        }

        [Test]
        public void AnExtractionConditionAlreadySatisfied_OpensAndSucceedsInTheSameTick()
        {
            var runtime = Make(first);
            first.CompleteNow = true;
            extraction.CompleteNow = true;

            runtime.Tick();

            Assert.That(runtime.Phase, Is.EqualTo(MissionPhase.Success));
        }

        [Test]
        public void TheWholeSquadDead_IsFailure_AndTheExtractionNeverOpens()
        {
            var runtime = Make(first);
            Kill(squad[0]);
            runtime.Tick();
            Assert.That(runtime.Phase, Is.EqualTo(MissionPhase.Active), "one survivor keeps the mission going");

            Kill(squad[1]);
            first.CompleteNow = true;
            runtime.Tick();

            Assert.That(runtime.Phase, Is.EqualTo(MissionPhase.Failure));
            Assert.That(extraction.State, Is.EqualTo(ObjectiveState.Inactive));
        }

        [Test]
        public void FailureWinsOverSuccess_WhenBothHappenInOneTick()
        {
            var runtime = Make(first);
            first.CompleteNow = true;
            runtime.Tick();
            extraction.CompleteNow = true;
            Kill(squad[0]);
            Kill(squad[1]);

            runtime.Tick();

            Assert.That(runtime.Phase, Is.EqualTo(MissionPhase.Failure));
        }

        [Test]
        public void ARequiredObjectiveFailing_IsFailure()
        {
            var runtime = Make(first, second);
            second.FailNow = true;

            runtime.Tick();

            Assert.That(second.State, Is.EqualTo(ObjectiveState.Failed));
            Assert.That(runtime.Phase, Is.EqualTo(MissionPhase.Failure));
        }

        [Test]
        public void AnOptionalGoal_NeverBlocksTheExtraction()
        {
            var optional = new FakeObjective("optional", false);
            var runtime = Make(first, optional);
            first.CompleteNow = true;

            runtime.Tick();

            Assert.That(runtime.Phase, Is.EqualTo(MissionPhase.ExtractionOpen));
            Assert.That(optional.State, Is.EqualTo(ObjectiveState.Active));
        }

        [Test]
        public void AnUnknownObjective_StillCountsAsRequired()
        {
            var runtime = Make(first);
            first.SetKnown(false);
            runtime.Tick();
            Assert.That(runtime.Phase, Is.EqualTo(MissionPhase.Active));

            first.CompleteNow = true;
            runtime.Tick();
            Assert.That(runtime.Phase, Is.EqualTo(MissionPhase.ExtractionOpen));
        }

        [Test]
        public void SuccessAndFailureAreTerminal()
        {
            var runtime = Make(first);
            first.CompleteNow = true;
            runtime.Tick();
            extraction.CompleteNow = true;
            runtime.Tick();
            Kill(squad[0]);
            Kill(squad[1]);

            runtime.Tick();

            Assert.That(runtime.Phase, Is.EqualTo(MissionPhase.Success), "later deaths change nothing");
        }

        [Test]
        public void AnEmptySquad_NeverFails()
        {
            var runtime = new MissionRuntime(new[] { first }, extraction, Array.Empty<Health>());
            runtime.Start();

            runtime.Tick();

            Assert.That(runtime.Phase, Is.EqualTo(MissionPhase.Active));
        }

        [Test]
        public void Detach_StopsTickingAndStopsForwardingChanges()
        {
            var runtime = Make(first);
            var changes = 0;
            runtime.ObjectiveChanged += _ => changes++;
            runtime.Detach();
            first.CompleteNow = true;

            runtime.Tick();
            first.Evaluate();

            Assert.That(runtime.IsDetached, Is.True);
            Assert.That(runtime.Phase, Is.EqualTo(MissionPhase.Active));
            Assert.That(changes, Is.Zero, "a detached runtime forwards nothing");
        }

        [Test]
        public void PhaseChanged_ReportsEachTransitionInOrder()
        {
            var phases = new List<MissionPhase>();
            var runtime = new MissionRuntime(new[] { first }, extraction, squad);
            runtime.PhaseChanged += phases.Add;
            runtime.Start();
            first.CompleteNow = true;
            runtime.Tick();
            extraction.CompleteNow = true;
            runtime.Tick();

            Assert.That(phases, Is.EqualTo(new[] { MissionPhase.Active, MissionPhase.ExtractionOpen, MissionPhase.Success }));
        }

        [Test]
        public void ObjectiveChanged_ForwardsObjectiveStateChanges()
        {
            var seen = new List<MissionObjective>();
            var runtime = Make(first);
            runtime.ObjectiveChanged += seen.Add;
            first.CompleteNow = true;

            runtime.Tick();

            Assert.That(seen, Does.Contain(first));
            Assert.That(seen, Does.Contain(extraction), "the extraction activating is a change too");
        }

        [Test]
        public void TheConstructor_RejectsABadExtraction_OrSquad()
        {
            Assert.Throws<ArgumentNullException>(() => new MissionRuntime(new[] { first }, null, squad));
            Assert.Throws<ArgumentException>(() => new MissionRuntime(new[] { first }, new FakeObjective("x", true), squad));
            Assert.Throws<ArgumentNullException>(() => new MissionRuntime(new[] { first }, extraction, null));
            Assert.Throws<ArgumentNullException>(() => new MissionRuntime(null, extraction, squad));
        }

        [Test]
        public void ANullOrDestroyedSquadMember_CountsAsDead()
        {
            var runtime = Make(first);
            Object.DestroyImmediate(squad[0].gameObject);
            runtime.Tick();
            Assert.That(runtime.LivingSquad, Is.EqualTo(1));

            Kill(squad[1]);
            runtime.Tick();
            Assert.That(runtime.Phase, Is.EqualTo(MissionPhase.Failure));
        }
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.MissionRuntimeTests"`
Expected: EXIT=1 with `error CS0246` (`MissionObjective`, `MissionRuntime` not found).

- [ ] **Step 4: Write the objective base**

`Assets/_Project/Scripts/Mission/MissionObjective.cs`:

```csharp
using System;
using UnityEngine;

namespace Blackglass
{
    public enum ObjectiveType
    {
        EliminateHostiles,
        Interact,
        ReachZone,
    }

    public enum ObjectiveState
    {
        Inactive,
        Active,
        Completed,
        Failed,
    }

    /// <summary>
    /// One goal of a mission. Plain C# with plain-field state, so it can be tested without a scene and serialised later. An
    /// objective exists from the moment it is in a MissionRuntime, whether or not the player is told about it (IsKnown, true
    /// by default) and whether or not it is open yet (Inactive until Activate). Active objectives are re-derived from world
    /// state by Evaluate each frame; Complete and Fail act only on an Active objective.
    /// </summary>
    public abstract class MissionObjective
    {
        ObjectiveState state;

        protected MissionObjective(string id, ObjectiveType type, string title, bool isRequired)
        {
            if (string.IsNullOrEmpty(id))
                throw new ArgumentException("An objective needs an id.", nameof(id));
            Id = id;
            Type = type;
            Title = string.IsNullOrEmpty(title) ? id : title;
            IsRequired = isRequired;
        }

        public string Id { get; }
        public ObjectiveType Type { get; }
        public string Title { get; }
        /// <summary>A required objective must be Completed before extraction opens; one that Fails ends the mission.</summary>
        public bool IsRequired { get; }
        public ObjectiveState State => state;
        /// <summary>Whether the player has been told about this objective (HUD line, marker). Defaults to true.</summary>
        public bool IsKnown { get; private set; } = true;
        public virtual bool HasTarget => false;
        public virtual Vector3 TargetPosition => Vector3.zero;

        /// <summary>Raised after every state change.</summary>
        public event Action<MissionObjective> StateChanged;

        public void SetKnown(bool known) => IsKnown = known;

        /// <summary>Inactive to Active; ignored in any other state.</summary>
        public void Activate()
        {
            if (state != ObjectiveState.Inactive)
                return;
            SetState(ObjectiveState.Active);
            OnActivated();
        }

        /// <summary>Re-derives the state from the world; does nothing unless Active.</summary>
        public void Evaluate()
        {
            if (state == ObjectiveState.Active)
                OnEvaluate();
        }

        /// <summary>The mission is over: stop offering anything (an interactable becomes unavailable). State is kept.</summary>
        public void EndMission() => OnMissionEnded();

        /// <summary>A short line for the HUD, without any status prefix.</summary>
        public abstract string Describe();

        protected virtual void OnActivated() { }
        protected virtual void OnEvaluate() { }
        protected virtual void OnMissionEnded() { }

        protected void Complete()
        {
            if (state == ObjectiveState.Active)
                SetState(ObjectiveState.Completed);
        }

        protected void Fail()
        {
            if (state == ObjectiveState.Active)
                SetState(ObjectiveState.Failed);
        }

        void SetState(ObjectiveState next)
        {
            if (state == next)
                return;
            state = next;
            StateChanged?.Invoke(this);
        }
    }
}
```

- [ ] **Step 5: Write the runtime**

`Assets/_Project/Scripts/Mission/MissionRuntime.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace Blackglass
{
    /// <summary>Where a generated mission is in play. Generation has its own state (MissionState on the director).</summary>
    public enum MissionPhase
    {
        /// <summary>Not started (or no mission).</summary>
        Inactive,
        /// <summary>Running; required objectives are open.</summary>
        Active,
        /// <summary>Every required objective is Completed and the extraction objective is open.</summary>
        ExtractionOpen,
        /// <summary>The extraction objective is Completed. Terminal.</summary>
        Success,
        /// <summary>The squad is dead or a required objective failed. Terminal.</summary>
        Failure,
    }

    /// <summary>
    /// The state of one mission: a list of objectives and the phase they imply. Plain C#, ticked by MissionDirector. The
    /// extraction objective is not a goal: it starts Inactive and the runtime opens it when every required goal is Completed;
    /// completing it is success. Success and Failure are terminal, and failure is judged first, so everyone dead in the same
    /// tick as an extraction is a failure. Pull-based: each Tick re-derives objective states from the world, so the result
    /// cannot drift from the scene. State is plain fields; nothing is kept in views.
    /// </summary>
    public sealed class MissionRuntime
    {
        readonly List<MissionObjective> objectives = new List<MissionObjective>();
        readonly MissionObjective extraction;
        readonly IReadOnlyList<Health> squad;

        public MissionRuntime(IEnumerable<MissionObjective> goals, MissionObjective extraction, IReadOnlyList<Health> squad)
        {
            if (goals == null)
                throw new ArgumentNullException(nameof(goals));
            if (extraction == null)
                throw new ArgumentNullException(nameof(extraction));
            if (extraction.IsRequired)
                throw new ArgumentException("The extraction objective opens when the required goals are done, so it cannot be required itself.", nameof(extraction));
            if (squad == null)
                throw new ArgumentNullException(nameof(squad));
            objectives.AddRange(goals);
            objectives.Add(extraction);
            this.extraction = extraction;
            this.squad = squad;
            foreach (var objective in objectives)
                objective.StateChanged += OnObjectiveChanged;
        }

        public MissionPhase Phase { get; private set; }
        public bool IsOver => Phase == MissionPhase.Success || Phase == MissionPhase.Failure;
        /// <summary>True after Detach: the runtime no longer ticks or forwards anything.</summary>
        public bool IsDetached { get; private set; }
        /// <summary>The goals in order, then the extraction.</summary>
        public IReadOnlyList<MissionObjective> Objectives => objectives;
        public MissionObjective Extraction => extraction;

        public int LivingSquad
        {
            get
            {
                var living = 0;
                foreach (var unit in squad)
                {
                    if (unit != null && unit.IsAlive)
                        living++;
                }
                return living;
            }
        }

        public event Action<MissionPhase> PhaseChanged;
        public event Action<MissionObjective> ObjectiveChanged;

        /// <summary>Activates every goal (not the extraction) and begins the mission.</summary>
        public void Start()
        {
            if (Phase != MissionPhase.Inactive || IsDetached)
                return;
            foreach (var objective in objectives)
            {
                if (objective != extraction)
                    objective.Activate();
            }
            SetPhase(MissionPhase.Active);
        }

        public void Tick()
        {
            if (IsDetached || Phase == MissionPhase.Inactive || IsOver)
                return;
            if (squad.Count > 0 && LivingSquad == 0)
            {
                Finish(MissionPhase.Failure);
                return;
            }
            foreach (var objective in objectives)
                objective.Evaluate();
            foreach (var objective in objectives)
            {
                if (objective.IsRequired && objective.State == ObjectiveState.Failed)
                {
                    Finish(MissionPhase.Failure);
                    return;
                }
            }
            if (Phase == MissionPhase.Active && AllRequiredComplete())
            {
                extraction.Activate();
                SetPhase(MissionPhase.ExtractionOpen);
                extraction.Evaluate();
            }
            if (Phase == MissionPhase.ExtractionOpen && extraction.State == ObjectiveState.Completed)
                Finish(MissionPhase.Success);
        }

        /// <summary>Stops the runtime for good (the mission is being torn down): nothing it holds can change it any more.</summary>
        public void Detach()
        {
            if (IsDetached)
                return;
            IsDetached = true;
            foreach (var objective in objectives)
                objective.StateChanged -= OnObjectiveChanged;
        }

        bool AllRequiredComplete()
        {
            foreach (var objective in objectives)
            {
                if (objective.IsRequired && objective.State != ObjectiveState.Completed)
                    return false;
            }
            return true;
        }

        void Finish(MissionPhase result)
        {
            SetPhase(result);
            foreach (var objective in objectives)
                objective.EndMission();
        }

        void SetPhase(MissionPhase next)
        {
            if (Phase == next)
                return;
            Phase = next;
            PhaseChanged?.Invoke(next);
        }

        void OnObjectiveChanged(MissionObjective objective) => ObjectiveChanged?.Invoke(objective);
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.MissionRuntimeTests"`
Expected: EXIT=0, 18 tests passed.

- [ ] **Step 7: Commit**

```bash
git add Assets/_Project/Scripts/Mission/MissionObjective.cs Assets/_Project/Scripts/Mission/MissionObjective.cs.meta Assets/_Project/Scripts/Mission/MissionRuntime.cs Assets/_Project/Scripts/Mission/MissionRuntime.cs.meta Assets/_Project/Tests/EditMode/MissionRuntimeTests.cs Assets/_Project/Tests/EditMode/MissionRuntimeTests.cs.meta
git commit -m "Add MissionObjective base and MissionRuntime phase machine

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 2: EliminateHostiles and ReachZone objectives

**Files:**
- Create: `Assets/_Project/Scripts/Mission/EliminateHostilesObjective.cs`
- Create: `Assets/_Project/Scripts/Mission/ReachZoneObjective.cs`
- Test: `Assets/_Project/Tests/EditMode/MissionObjectivesTests.cs`

**Interfaces:**
- Consumes: `MissionObjective`, `ObjectiveType`, `ObjectiveState` (Task 1); `Health`, `CoverRules.FlatDistance(Vector3, Vector3)`.
- Produces:
  - `EliminateHostilesObjective(string id, string title, IReadOnlyList<Health> group, bool isRequired = true)`; `Group`, `Living`.
  - `ReachZoneObjective(string id, string title, Vector3 center, float radius, int requiredUnits, IReadOnlyList<Health> squad, bool isRequired)`; `Center`, `Radius`, `Inside`; `HasTarget` true, `TargetPosition` = center.

- [ ] **Step 1: Write the failing tests**

`Assets/_Project/Tests/EditMode/MissionObjectivesTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class MissionObjectivesTests
    {
        readonly List<GameObject> hosts = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (var host in hosts)
            {
                if (host != null)
                    Object.DestroyImmediate(host);
            }
            hosts.Clear();
        }

        Health Unit(Vector3 position)
        {
            var host = new GameObject("Unit");
            host.transform.position = position;
            hosts.Add(host);
            return host.AddComponent<Health>();
        }

        static void Kill(Health unit) => unit.TakeDamage(unit.Max);

        // ---- EliminateHostiles

        [Test]
        public void Eliminate_CompletesWhenEveryMemberOfItsGroupIsDead_AndNotBefore()
        {
            var a = Unit(Vector3.zero);
            var b = Unit(Vector3.zero);
            var objective = new EliminateHostilesObjective("kill", "Eliminate security team", new[] { a, b });
            objective.Activate();

            objective.Evaluate();
            Assert.That(objective.State, Is.EqualTo(ObjectiveState.Active));
            Kill(a);
            objective.Evaluate();
            Assert.That(objective.State, Is.EqualTo(ObjectiveState.Active));
            Assert.That(objective.Living, Is.EqualTo(1));
            Kill(b);
            objective.Evaluate();

            Assert.That(objective.State, Is.EqualTo(ObjectiveState.Completed));
        }

        [Test]
        public void Eliminate_IgnoresHostilesOutsideItsGroup()
        {
            var inGroup = Unit(Vector3.zero);
            Unit(Vector3.zero);   // a bystander hostile, not in the group
            var objective = new EliminateHostilesObjective("kill", "Eliminate", new[] { inGroup });
            objective.Activate();

            Kill(inGroup);
            objective.Evaluate();

            Assert.That(objective.State, Is.EqualTo(ObjectiveState.Completed));
        }

        [Test]
        public void Eliminate_CountsDestroyedMembersAsDead_AndAnEmptyGroupAsDone()
        {
            var a = Unit(Vector3.zero);
            var objective = new EliminateHostilesObjective("kill", "Eliminate", new[] { a });
            objective.Activate();
            Object.DestroyImmediate(a.gameObject);
            objective.Evaluate();
            Assert.That(objective.State, Is.EqualTo(ObjectiveState.Completed));

            var empty = new EliminateHostilesObjective("none", "Nothing", Array.Empty<Health>());
            empty.Activate();
            empty.Evaluate();
            Assert.That(empty.State, Is.EqualTo(ObjectiveState.Completed));
        }

        [Test]
        public void Eliminate_DoesNothingWhileInactive_AndDescribesTheCount()
        {
            var a = Unit(Vector3.zero);
            var b = Unit(Vector3.zero);
            var objective = new EliminateHostilesObjective("kill", "Eliminate security team", new[] { a, b });
            Kill(a);
            Kill(b);

            objective.Evaluate();

            Assert.That(objective.State, Is.EqualTo(ObjectiveState.Inactive));
            Assert.That(objective.Describe(), Is.EqualTo("Eliminate security team (0/2)"));
            Assert.That(objective.Type, Is.EqualTo(ObjectiveType.EliminateHostiles));
            Assert.That(objective.HasTarget, Is.False);
        }

        [Test]
        public void Eliminate_DescribeShowsOnlyTheTitleOnceCompleted()
        {
            var a = Unit(Vector3.zero);
            var objective = new EliminateHostilesObjective("kill", "Eliminate security team", new[] { a });
            objective.Activate();
            Kill(a);
            objective.Evaluate();

            Assert.That(objective.Describe(), Is.EqualTo("Eliminate security team"));
        }

        // ---- ReachZone

        [Test]
        public void Reach_CompletesWhenARequiredNumberOfLivingUnitsAreInsideTheRadius()
        {
            var inside = Unit(new Vector3(1f, 0f, 0f));
            var outside = Unit(new Vector3(5f, 0f, 0f));
            var objective = new ReachZoneObjective("extract", "Extraction", Vector3.zero, 2f, 1, new[] { inside, outside }, false);
            objective.Activate();

            objective.Evaluate();

            Assert.That(objective.Inside, Is.EqualTo(1));
            Assert.That(objective.State, Is.EqualTo(ObjectiveState.Completed));
        }

        [Test]
        public void Reach_NeedsTheConfiguredCount_AndIgnoresTheDead()
        {
            var a = Unit(Vector3.zero);
            var b = Unit(new Vector3(0.5f, 0f, 0f));
            var objective = new ReachZoneObjective("extract", "Extraction", Vector3.zero, 2f, 2, new[] { a, b }, false);
            objective.Activate();
            Kill(b);

            objective.Evaluate();
            Assert.That(objective.State, Is.EqualTo(ObjectiveState.Active), "a dead unit in the zone does not count");
        }

        [Test]
        public void Reach_UsesFlatDistance_SoHeightIsIgnored()
        {
            var tall = Unit(new Vector3(1f, 1f, 0f));
            var objective = new ReachZoneObjective("extract", "Extraction", Vector3.zero, 2f, 1, new[] { tall }, false);
            objective.Activate();

            objective.Evaluate();

            Assert.That(objective.State, Is.EqualTo(ObjectiveState.Completed));
        }

        [Test]
        public void Reach_UnitsInsideWhileInactive_DoNotComplete()
        {
            var a = Unit(Vector3.zero);
            var objective = new ReachZoneObjective("extract", "Extraction", Vector3.zero, 2f, 1, new[] { a }, false);

            objective.Evaluate();

            Assert.That(objective.State, Is.EqualTo(ObjectiveState.Inactive));
        }

        [Test]
        public void Reach_ExposesItsTarget_AndItsType()
        {
            var center = new Vector3(3f, 0f, 4f);
            var objective = new ReachZoneObjective("extract", "Extraction", center, 2f, 1, Array.Empty<Health>(), false);

            Assert.That(objective.HasTarget, Is.True);
            Assert.That(objective.TargetPosition, Is.EqualTo(center));
            Assert.That(objective.Type, Is.EqualTo(ObjectiveType.ReachZone));
            Assert.That(objective.IsRequired, Is.False);
        }

        [Test]
        public void Reach_RejectsBadArguments()
        {
            Assert.Throws<ArgumentNullException>(() => new ReachZoneObjective("e", "E", Vector3.zero, 2f, 1, null, false));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ReachZoneObjective("e", "E", Vector3.zero, 0f, 1, Array.Empty<Health>(), false));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ReachZoneObjective("e", "E", Vector3.zero, 2f, 0, Array.Empty<Health>(), false));
            Assert.Throws<ArgumentNullException>(() => new EliminateHostilesObjective("k", "K", null));
        }
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.MissionObjectivesTests"`
Expected: EXIT=1, `error CS0246` for the two new types.

- [ ] **Step 3: Implement**

`Assets/_Project/Scripts/Mission/EliminateHostilesObjective.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace Blackglass
{
    /// <summary>
    /// Complete when every unit of its own group is dead. The group is a list of Health the mission hands it, not the
    /// Encounter's hostile side, so a mission may hold hostiles that are not this objective's concern. A destroyed or null
    /// member counts as dead, and an empty group is already done.
    /// </summary>
    public sealed class EliminateHostilesObjective : MissionObjective
    {
        readonly IReadOnlyList<Health> group;

        public EliminateHostilesObjective(string id, string title, IReadOnlyList<Health> group, bool isRequired = true)
            : base(id, ObjectiveType.EliminateHostiles, title, isRequired)
        {
            this.group = group ?? throw new ArgumentNullException(nameof(group));
        }

        public IReadOnlyList<Health> Group => group;

        public int Living
        {
            get
            {
                var living = 0;
                foreach (var unit in group)
                {
                    if (unit != null && unit.IsAlive)
                        living++;
                }
                return living;
            }
        }

        public override string Describe() =>
            State == ObjectiveState.Completed ? Title : $"{Title} ({Living}/{group.Count})";

        protected override void OnEvaluate()
        {
            if (Living == 0)
                Complete();
        }
    }
}
```

`Assets/_Project/Scripts/Mission/ReachZoneObjective.cs`:

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// A location objective: complete once at least `requiredUnits` living squad members stand within `radius` (flat
    /// distance) of the centre while it is Active. Extraction is one of these, not required and opened by the runtime.
    /// </summary>
    public sealed class ReachZoneObjective : MissionObjective
    {
        readonly IReadOnlyList<Health> squad;

        public ReachZoneObjective(string id, string title, Vector3 center, float radius, int requiredUnits,
            IReadOnlyList<Health> squad, bool isRequired)
            : base(id, ObjectiveType.ReachZone, title, isRequired)
        {
            this.squad = squad ?? throw new ArgumentNullException(nameof(squad));
            if (radius <= 0f)
                throw new ArgumentOutOfRangeException(nameof(radius), radius, "The zone needs a positive radius.");
            if (requiredUnits < 1)
                throw new ArgumentOutOfRangeException(nameof(requiredUnits), requiredUnits, "At least one unit must be required.");
            Center = center;
            Radius = radius;
            RequiredUnits = requiredUnits;
        }

        public Vector3 Center { get; }
        public float Radius { get; }
        public int RequiredUnits { get; }

        public override bool HasTarget => true;
        public override Vector3 TargetPosition => Center;

        /// <summary>Living squad members inside the zone right now.</summary>
        public int Inside
        {
            get
            {
                var inside = 0;
                foreach (var unit in squad)
                {
                    if (unit != null && unit.IsAlive && CoverRules.FlatDistance(unit.transform.position, Center) <= Radius)
                        inside++;
                }
                return inside;
            }
        }

        public override string Describe() => Title;

        protected override void OnEvaluate()
        {
            if (Inside >= RequiredUnits)
                Complete();
        }
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.MissionObjectivesTests"`
Expected: EXIT=0, 11 tests passed.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/Mission/EliminateHostilesObjective.cs* Assets/_Project/Scripts/Mission/ReachZoneObjective.cs* Assets/_Project/Tests/EditMode/MissionObjectivesTests.cs*
git commit -m "Add EliminateHostiles and ReachZone objectives

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Interaction capability (InteractCommand, MissionInteractable, UnitInteractor, InteractObjective)

**Files:**
- Modify: `Assets/_Project/Scripts/Commands/UnitCommands.cs` (add `InteractCommand`; update the doc comment)
- Create: `Assets/_Project/Scripts/Mission/MissionInteractable.cs`
- Create: `Assets/_Project/Scripts/Units/UnitInteractor.cs`
- Create: `Assets/_Project/Scripts/Mission/InteractObjective.cs`
- Test: `Assets/_Project/Tests/EditMode/MissionInteractableTests.cs`

**Interfaces:**
- Consumes: `MissionObjective` (Task 1), `CommandableUnit.IsAlive` (existing), `CoverRules.FlatDistance`.
- Produces:
  - `MissionInteractable : MonoBehaviour`: `DisplayName` (default "Terminal"), `Range` (1.8), `Duration` (2), `Progress`, `Fraction` (0..1), `IsCompleted`, `IsEnabled`, `IsAvailable`, `User`, `Position`; `internal void Initialize(float range, float duration, string displayName = "Terminal")`; `void SetAvailable(bool)`; `bool IsInUseByOther(CommandableUnit)`; `bool TryBegin(CommandableUnit)`; `bool Advance(CommandableUnit, float deltaTime)`; `void Release(CommandableUnit)`.
  - `enum InteractionFailure { None, NoTarget, NotAvailable, InUse }`, `enum InteractionStep { Working, Completed, Lost }`.
  - `UnitInteractor : MonoBehaviour`: `LastFailure`, `IsWorking`, `Current`; `InteractionFailure Check(MissionInteractable target, bool allowOtherUser = false)`; `bool InRange(MissionInteractable target, Vector3 from)`; `bool TryStart(MissionInteractable target)`; `InteractionStep Advance(float deltaTime)`; `void Release()`; `internal void Record(InteractionFailure failure)`.
  - `InteractCommand(MissionInteractable target)` with `Target`.
  - `InteractObjective(string id, string title, MissionInteractable interactable, bool isRequired = true)`; `Interactable`; `HasTarget` true; `TargetPosition` = interactable position.

- [ ] **Step 1: Write the failing tests**

`Assets/_Project/Tests/EditMode/MissionInteractableTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class MissionInteractableTests
    {
        readonly List<GameObject> hosts = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (var host in hosts)
            {
                if (host != null)
                    Object.DestroyImmediate(host);
            }
            hosts.Clear();
        }

        MissionInteractable Terminal(float range = 1.8f, float duration = 2f)
        {
            var host = new GameObject("Terminal");
            hosts.Add(host);
            var terminal = host.AddComponent<MissionInteractable>();
            terminal.Initialize(range, duration);
            return terminal;
        }

        CommandableUnit Unit(string name = "Unit", Vector3 position = default)
        {
            var host = new GameObject(name);
            host.transform.position = position;
            hosts.Add(host);
            host.AddComponent<Health>();
            host.AddComponent<UnitInteractor>();
            return host.AddComponent<CommandableUnit>();
        }

        // ---- MissionInteractable

        [Test]
        public void ANewTerminal_IsUnavailable_UntilItsObjectiveOpensIt()
        {
            var terminal = Terminal();
            Assert.That(terminal.IsAvailable, Is.False);
            terminal.SetAvailable(true);
            Assert.That(terminal.IsAvailable, Is.True);
            Assert.That(terminal.IsCompleted, Is.False);
        }

        [Test]
        public void Advance_AccumulatesProgress_AndCompletesAtTheDuration()
        {
            var terminal = Terminal(duration: 2f);
            terminal.SetAvailable(true);
            var unit = Unit();

            Assert.That(terminal.TryBegin(unit), Is.True);
            Assert.That(terminal.Advance(unit, 0.5f), Is.True);
            Assert.That(terminal.Progress, Is.EqualTo(0.5f).Within(1e-4f));
            Assert.That(terminal.Fraction, Is.EqualTo(0.25f).Within(1e-4f));
            Assert.That(terminal.IsCompleted, Is.False);
            terminal.Advance(unit, 1.5f);

            Assert.That(terminal.IsCompleted, Is.True);
            Assert.That(terminal.IsAvailable, Is.False, "a completed terminal is used up");
            Assert.That(terminal.User == null, Is.True, "the claim is released on completion");
            Assert.That(terminal.TryBegin(unit), Is.False);
        }

        [Test]
        public void Release_ResetsProgress_AndFreesTheTerminalForAnotherUnit()
        {
            var terminal = Terminal();
            terminal.SetAvailable(true);
            var a = Unit("A");
            var b = Unit("B");
            terminal.TryBegin(a);
            terminal.Advance(a, 1f);

            terminal.Release(a);

            Assert.That(terminal.Progress, Is.Zero, "a cancelled interaction keeps no progress");
            Assert.That(terminal.TryBegin(b), Is.True);
        }

        [Test]
        public void AClaimedTerminal_RefusesAnotherUnit_UnlessTheHolderIsDead()
        {
            var terminal = Terminal();
            terminal.SetAvailable(true);
            var a = Unit("A");
            var b = Unit("B");
            Assert.That(terminal.TryBegin(a), Is.True);

            Assert.That(terminal.IsInUseByOther(b), Is.True);
            Assert.That(terminal.TryBegin(b), Is.False);
            Assert.That(terminal.TryBegin(a), Is.True, "the holder may begin again");

            var health = a.GetComponent<Health>();
            health.TakeDamage(health.Max);
            Assert.That(terminal.IsInUseByOther(b), Is.False, "a dead holder's claim is stale");
            Assert.That(terminal.TryBegin(b), Is.True);
        }

        [Test]
        public void Advance_ByAUnitThatIsNotTheHolder_DoesNothing()
        {
            var terminal = Terminal();
            terminal.SetAvailable(true);
            var a = Unit("A");
            var b = Unit("B");
            terminal.TryBegin(a);

            Assert.That(terminal.Advance(b, 5f), Is.False);
            Assert.That(terminal.Progress, Is.Zero);
        }

        [Test]
        public void MakingTheTerminalUnavailableMidWork_StopsTheInteraction()
        {
            var terminal = Terminal();
            terminal.SetAvailable(true);
            var unit = Unit();
            terminal.TryBegin(unit);
            terminal.Advance(unit, 0.5f);

            terminal.SetAvailable(false);

            Assert.That(terminal.Advance(unit, 0.5f), Is.False);
            Assert.That(terminal.IsCompleted, Is.False);
        }

        [Test]
        public void AZeroDuration_CompletesOnTheFirstAdvance()
        {
            var terminal = Terminal(duration: 0f);
            terminal.SetAvailable(true);
            var unit = Unit();
            terminal.TryBegin(unit);

            terminal.Advance(unit, 0f);

            Assert.That(terminal.IsCompleted, Is.True);
            Assert.That(terminal.Fraction, Is.EqualTo(1f));
        }

        // ---- UnitInteractor

        [Test]
        public void Check_ReportsMissingUnavailableAndBusyTargets()
        {
            var interactor = Unit("A").GetComponent<UnitInteractor>();
            var other = Unit("B");
            var terminal = Terminal();

            Assert.That(interactor.Check(null), Is.EqualTo(InteractionFailure.NoTarget));
            Assert.That(interactor.Check(terminal), Is.EqualTo(InteractionFailure.NotAvailable));
            terminal.SetAvailable(true);
            Assert.That(interactor.Check(terminal), Is.EqualTo(InteractionFailure.None));
            terminal.TryBegin(other);
            Assert.That(interactor.Check(terminal), Is.EqualTo(InteractionFailure.InUse));
            Assert.That(interactor.Check(terminal, allowOtherUser: true), Is.EqualTo(InteractionFailure.None));
        }

        [Test]
        public void Check_ASafeNoTarget_ForADestroyedTerminal()
        {
            var interactor = Unit().GetComponent<UnitInteractor>();
            var terminal = Terminal();
            terminal.SetAvailable(true);
            Object.DestroyImmediate(terminal.gameObject);

            Assert.That(interactor.Check(terminal), Is.EqualTo(InteractionFailure.NoTarget));
            Assert.That(interactor.InRange(terminal, Vector3.zero), Is.False);
        }

        [Test]
        public void InRange_IsFlatDistanceWithinTheTerminalsRange()
        {
            var interactor = Unit().GetComponent<UnitInteractor>();
            var terminal = Terminal(range: 1.8f);
            terminal.transform.position = new Vector3(10f, 1.2f, 0f);

            Assert.That(interactor.InRange(terminal, new Vector3(8.5f, 0f, 0f)), Is.True);
            Assert.That(interactor.InRange(terminal, new Vector3(7.5f, 0f, 0f)), Is.False);
        }

        [Test]
        public void TryStart_Advance_ThenComplete_RunsTheInteraction()
        {
            var unit = Unit();
            var interactor = unit.GetComponent<UnitInteractor>();
            var terminal = Terminal(duration: 1f);
            terminal.SetAvailable(true);

            Assert.That(interactor.TryStart(terminal), Is.True);
            Assert.That(interactor.IsWorking, Is.True);
            Assert.That(interactor.Advance(0.4f), Is.EqualTo(InteractionStep.Working));
            Assert.That(interactor.Advance(0.7f), Is.EqualTo(InteractionStep.Completed));

            Assert.That(interactor.IsWorking, Is.False);
            Assert.That(terminal.IsCompleted, Is.True);
        }

        [Test]
        public void TryStart_FailsAndRecordsWhy_ForABusyTerminal()
        {
            var interactor = Unit("A").GetComponent<UnitInteractor>();
            var other = Unit("B");
            var terminal = Terminal();
            terminal.SetAvailable(true);
            terminal.TryBegin(other);

            Assert.That(interactor.TryStart(terminal), Is.False);
            Assert.That(interactor.LastFailure, Is.EqualTo(InteractionFailure.InUse));
            Assert.That(interactor.IsWorking, Is.False);
        }

        [Test]
        public void Advance_ReportsLost_WhenTheTerminalWasTakenAway_AndRelease_IsSafeToRepeat()
        {
            var interactor = Unit().GetComponent<UnitInteractor>();
            var terminal = Terminal();
            terminal.SetAvailable(true);
            interactor.TryStart(terminal);
            terminal.SetAvailable(false);

            Assert.That(interactor.Advance(0.1f), Is.EqualTo(InteractionStep.Lost));
            Assert.That(interactor.IsWorking, Is.False);
            Assert.DoesNotThrow(() => { interactor.Release(); interactor.Release(); });
        }

        [Test]
        public void Release_DropsTheClaim_AndResetsProgress()
        {
            var interactor = Unit().GetComponent<UnitInteractor>();
            var terminal = Terminal();
            terminal.SetAvailable(true);
            interactor.TryStart(terminal);
            interactor.Advance(0.5f);

            interactor.Release();

            Assert.That(terminal.Progress, Is.Zero);
            Assert.That(terminal.User == null, Is.True);
        }

        // ---- InteractCommand and InteractObjective

        [Test]
        public void InteractCommand_RequiresATarget()
        {
            Assert.Throws<ArgumentNullException>(() => new InteractCommand(null));
            var terminal = Terminal();
            Assert.That(new InteractCommand(terminal).Target, Is.EqualTo(terminal));
        }

        [Test]
        public void InteractObjective_OpensTheTerminalOnActivate_AndCompletesWhenItIsUsed()
        {
            var terminal = Terminal(duration: 0f);
            var objective = new InteractObjective("hack", "Access data terminal", terminal);
            Assert.That(terminal.IsAvailable, Is.False, "inactive objectives offer nothing");

            objective.Activate();
            Assert.That(terminal.IsAvailable, Is.True);
            Assert.That(objective.HasTarget, Is.True);
            Assert.That(objective.TargetPosition, Is.EqualTo(terminal.Position));

            var unit = Unit();
            terminal.TryBegin(unit);
            terminal.Advance(unit, 0f);
            objective.Evaluate();

            Assert.That(objective.State, Is.EqualTo(ObjectiveState.Completed));
            Assert.That(objective.Type, Is.EqualTo(ObjectiveType.Interact));
        }

        [Test]
        public void InteractObjective_EndMission_ClosesTheTerminal_AndADestroyedTerminalFailsIt()
        {
            var terminal = Terminal();
            var objective = new InteractObjective("hack", "Access data terminal", terminal);
            objective.Activate();
            objective.EndMission();
            Assert.That(terminal.IsAvailable, Is.False);

            var other = Terminal();
            var broken = new InteractObjective("hack2", "Access", other);
            broken.Activate();
            Object.DestroyImmediate(other.gameObject);
            broken.Evaluate();
            Assert.That(broken.State, Is.EqualTo(ObjectiveState.Failed));
            Assert.DoesNotThrow(() => broken.EndMission());
            Assert.DoesNotThrow(() => broken.Describe());
        }

        [Test]
        public void InteractObjective_DescribeShowsProgressWhileWorking()
        {
            var terminal = Terminal(duration: 2f);
            var objective = new InteractObjective("hack", "Access data terminal", terminal);
            objective.Activate();
            Assert.That(objective.Describe(), Is.EqualTo("Access data terminal"));

            var unit = Unit();
            terminal.TryBegin(unit);
            terminal.Advance(unit, 1f);

            Assert.That(objective.Describe(), Is.EqualTo("Access data terminal (50%)"));
        }
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.MissionInteractableTests"`
Expected: EXIT=1 (`MissionInteractable`, `UnitInteractor`, `InteractCommand`, `InteractObjective` not found).

- [ ] **Step 3: Add `InteractCommand`**

In `Assets/_Project/Scripts/Commands/UnitCommands.cs`: change the doc comment of `UnitCommand` from `...MoveToCover, Ability and Stop. Future commands (Interact) are new subclasses.` to `...MoveToCover, Ability, Interact and Stop.`, and add after `AbilityCommand` (before `StopCommand`):

```csharp
    /// <summary>
    /// Walk to a mission interactable (a terminal) and work on it until it completes. Plain data like every order: the unit
    /// validates it when it starts and every frame it runs (UnitInteractor), and a failure ends the order so the queue
    /// moves on. Progress advances only with simulation time and is lost if the order is cancelled.
    /// </summary>
    public sealed class InteractCommand : UnitCommand
    {
        public InteractCommand(MissionInteractable target)
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));
            Target = target;
        }

        public MissionInteractable Target { get; }
    }
```

- [ ] **Step 4: Add `MissionInteractable`**

`Assets/_Project/Scripts/Mission/MissionInteractable.cs`:

```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Something a unit can work on until it completes: the mission terminal. Holds plain state (available flag, progress, who
    /// is working on it); the rules about who may use it live in UnitInteractor and the order in CommandableUnit. Its
    /// objective makes it available when the objective activates and closes it when the mission ends. One unit works on it at
    /// a time; the claim of a dead or destroyed unit is stale and ignored. Progress is lost on Release (a cancelled
    /// interaction keeps nothing). Advance is called by the working unit with scaled delta time, so a pause freezes it.
    /// </summary>
    public sealed class MissionInteractable : MonoBehaviour
    {
        [SerializeField] string displayName = "Terminal";
        // The flat distance from the unit to the object within which it may work.
        [SerializeField, Min(0.1f)] float range = 1.8f;
        [SerializeField, Min(0f)] float duration = 2f;

        bool available;
        bool completed;
        float progress;
        CommandableUnit user;

        public string DisplayName => displayName;
        public float Range => range;
        public float Duration => duration;
        public float Progress => progress;
        /// <summary>0 to 1.</summary>
        public float Fraction => completed ? 1f : (duration <= 0f ? 0f : Mathf.Clamp01(progress / duration));
        public bool IsCompleted => completed;
        /// <summary>True once the objective has opened it and until the mission ends.</summary>
        public bool IsEnabled => available;
        public bool IsAvailable => available && !completed && gameObject.activeInHierarchy;
        /// <summary>The unit working on it, or null.</summary>
        public CommandableUnit User => user;
        public Vector3 Position => transform.position;

        internal void Initialize(float interactionRange, float interactionSeconds, string label = "Terminal")
        {
            range = Mathf.Max(0.1f, interactionRange);
            duration = Mathf.Max(0f, interactionSeconds);
            displayName = label;
        }

        public void SetAvailable(bool value) => available = value;

        /// <summary>True when a living unit other than `unit` holds the claim.</summary>
        public bool IsInUseByOther(CommandableUnit unit) => user != null && user != unit && user.IsAlive;

        /// <summary>Claims the terminal for `unit`. False when it is unavailable or another living unit works on it.</summary>
        public bool TryBegin(CommandableUnit unit)
        {
            if (unit == null || !IsAvailable || IsInUseByOther(unit))
                return false;
            user = unit;
            return true;
        }

        /// <summary>
        /// Adds `deltaTime` of work for the holder. False (and no change) when `unit` is not the holder or the terminal is no
        /// longer available. Completing it releases the claim.
        /// </summary>
        public bool Advance(CommandableUnit unit, float deltaTime)
        {
            if (unit == null || user != unit || !IsAvailable)
                return false;
            progress += Mathf.Max(0f, deltaTime);
            if (progress >= duration)
            {
                progress = duration;
                completed = true;
                user = null;
            }
            return true;
        }

        /// <summary>Drops the claim if `unit` holds it; an unfinished interaction loses its progress.</summary>
        public void Release(CommandableUnit unit)
        {
            if (user != unit)
                return;
            user = null;
            if (!completed)
                progress = 0f;
        }
    }
}
```

- [ ] **Step 5: Add `UnitInteractor`**

`Assets/_Project/Scripts/Units/UnitInteractor.cs`:

```csharp
using UnityEngine;

namespace Blackglass
{
    public enum InteractionFailure
    {
        None,
        NoTarget,
        NotAvailable,
        InUse,
    }

    public enum InteractionStep
    {
        Working,
        Completed,
        Lost,
    }

    /// <summary>
    /// The unit's interaction capability: the rules for working on a MissionInteractable and the claim it holds while it does.
    /// CommandableUnit runs the order (walking, facing, when to start, when to give up); this component only answers "may I",
    /// "am I close enough" and advances or releases the work. Added to the friendly units at spawn, like UnitAbilities; a
    /// unit without it refuses Interact orders. The reason for the last refusal is kept for debug text.
    /// </summary>
    public sealed class UnitInteractor : MonoBehaviour
    {
        CommandableUnit unit;
        MissionInteractable current;

        public InteractionFailure LastFailure { get; private set; }

        /// <summary>The terminal this unit holds a claim on, or null.</summary>
        public MissionInteractable Current => current;

        public bool IsWorking => current != null;

        CommandableUnit Unit => unit != null ? unit : unit = GetComponent<CommandableUnit>();

        /// <summary>
        /// Whether the target can be worked on: it exists, is available, and no other living unit holds it. With
        /// `allowOtherUser` the claim is ignored (a queued order is only checked for what cannot change by then).
        /// </summary>
        public InteractionFailure Check(MissionInteractable target, bool allowOtherUser = false)
        {
            if (target == null)
                return InteractionFailure.NoTarget;
            if (!target.IsAvailable)
                return InteractionFailure.NotAvailable;
            if (!allowOtherUser && target.IsInUseByOther(Unit))
                return InteractionFailure.InUse;
            return InteractionFailure.None;
        }

        public bool InRange(MissionInteractable target, Vector3 from) =>
            target != null && CoverRules.FlatDistance(from, target.Position) <= target.Range;

        /// <summary>Claims the target and starts working. False, with the reason in LastFailure, when it cannot.</summary>
        public bool TryStart(MissionInteractable target)
        {
            Release();
            var failure = Check(target);
            if (failure == InteractionFailure.None && !target.TryBegin(Unit))
                failure = InteractionFailure.InUse;
            LastFailure = failure;
            if (failure != InteractionFailure.None)
                return false;
            current = target;
            return true;
        }

        /// <summary>One step of work. Lost when the claim or the terminal is gone; Completed when it just finished.</summary>
        public InteractionStep Advance(float deltaTime)
        {
            if (current == null)
                return InteractionStep.Lost;
            var target = current;
            if (!target.Advance(Unit, deltaTime))
            {
                current = null;
                return InteractionStep.Lost;
            }
            if (target.IsCompleted)
            {
                current = null;
                return InteractionStep.Completed;
            }
            return InteractionStep.Working;
        }

        /// <summary>Gives the claim up (a cancelled interaction keeps no progress). Safe to call when nothing is held.</summary>
        public void Release()
        {
            if (current == null)
                return;
            current.Release(Unit);
            current = null;
        }

        internal void Record(InteractionFailure failure) => LastFailure = failure;
    }
}
```

Note: when `current` was destroyed, `current == null` is true (Unity null) and `Release` just returns; the stale claim died with the terminal.

- [ ] **Step 6: Add `InteractObjective`**

`Assets/_Project/Scripts/Mission/InteractObjective.cs`:

```csharp
using System;
using System.Globalization;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Complete when its terminal has been worked to completion. Activating it opens the terminal; ending the mission closes
    /// it. A terminal that vanishes while the objective is Active fails the objective.
    /// </summary>
    public sealed class InteractObjective : MissionObjective
    {
        readonly MissionInteractable interactable;

        public InteractObjective(string id, string title, MissionInteractable interactable, bool isRequired = true)
            : base(id, ObjectiveType.Interact, title, isRequired)
        {
            if (interactable == null)
                throw new ArgumentNullException(nameof(interactable));
            this.interactable = interactable;
        }

        public MissionInteractable Interactable => interactable;

        public override bool HasTarget => interactable != null;
        public override Vector3 TargetPosition => interactable != null ? interactable.Position : Vector3.zero;

        public override string Describe()
        {
            if (State != ObjectiveState.Active || interactable == null || interactable.Progress <= 0f)
                return Title;
            var percent = Mathf.RoundToInt(interactable.Fraction * 100f);
            return string.Format(CultureInfo.InvariantCulture, "{0} ({1}%)", Title, percent);
        }

        protected override void OnActivated()
        {
            if (interactable != null)
                interactable.SetAvailable(true);
        }

        protected override void OnEvaluate()
        {
            if (interactable == null)
                Fail();
            else if (interactable.IsCompleted)
                Complete();
        }

        protected override void OnMissionEnded()
        {
            if (interactable != null)
                interactable.SetAvailable(false);
        }
    }
}
```

- [ ] **Step 7: Run to verify pass**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.MissionInteractableTests"`
Expected: EXIT=0, 18 tests passed. Then run the whole EditMode suite once: `Tools/run-tests.sh EditMode` → EXIT=0 (existing tests unchanged).

- [ ] **Step 8: Commit**

```bash
git add Assets/_Project/Scripts/Commands/UnitCommands.cs Assets/_Project/Scripts/Mission/MissionInteractable.cs* Assets/_Project/Scripts/Mission/InteractObjective.cs* Assets/_Project/Scripts/Units/UnitInteractor.cs* Assets/_Project/Tests/EditMode/MissionInteractableTests.cs*
git commit -m "Add the interaction capability: InteractCommand, MissionInteractable, UnitInteractor, InteractObjective

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```


### Task 4: The Interact order in CommandableUnit

**Files:**
- Modify: `Assets/_Project/Scripts/Units/CommandableUnit.cs`
- Test: `Assets/_Project/Tests/PlayMode/CommandableUnitInteractPlayModeTests.cs`

**Interfaces:**
- Consumes: `InteractCommand`, `MissionInteractable`, `UnitInteractor`, `InteractionFailure`, `InteractionStep` (Task 3); existing `CommandableUnit` internals (`Mover`, `Cover`, `WalkTo`, `WalkStalled`, `walkProgressTime`, `Finish`, `StopAll`, `StartNext`, `FaceTowards`, `ResetAttack`).
- Produces: `CommandableUnit.Issue(new InteractCommand(target), mode)` works as specified in the spec (section 6). No new public members.

- [ ] **Step 1: Write the failing tests**

`Assets/_Project/Tests/PlayMode/CommandableUnitInteractPlayModeTests.cs`:

```csharp
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class CommandableUnitInteractPlayModeTests
    {
        static readonly Vector3 TerminalGround = new Vector3(6f, 0f, 0f);
        static readonly Vector3 MovePoint = new Vector3(0f, 0f, 8f);

        TestWorld world;
        TacticalPause pause;
        CommandableUnit unit;
        UnitInteractor interactor;
        MissionInteractable terminal;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            world.CreateEnvironment();
            pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            unit = Worker(new Vector3(4f, 0f, 0f));
            interactor = unit.GetComponent<UnitInteractor>();
            terminal = Terminal(TerminalGround, 1f);
        }

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        CommandableUnit Worker(Vector3 ground)
        {
            var worker = world.CreateFighter(ground);
            worker.gameObject.AddComponent<UnitInteractor>();
            return worker;
        }

        // A plain marker cube with no collider (so it carves nothing) at ground + 0.6 m, available at once.
        MissionInteractable Terminal(Vector3 ground, float duration)
        {
            var host = world.Track(GameObject.CreatePrimitive(PrimitiveType.Cube));
            host.name = "TestTerminal";
            host.transform.position = ground + Vector3.up * 0.6f;
            Object.DestroyImmediate(host.GetComponent<Collider>());
            var made = host.AddComponent<MissionInteractable>();
            made.Initialize(1.8f, duration);
            made.SetAvailable(true);
            return made;
        }

        // Long enough that a test can act while the unit is still working.
        void Slow() => terminal.Initialize(1.8f, 6f);

        IEnumerator WorkingStarted(MissionInteractable target, float seconds = 10f) =>
            TestWorld.WaitUntil(() => target.Progress > 0.1f, seconds);

        [UnityTest]
        public IEnumerator Interact_WalksIntoRange_WorksForTheDuration_ThenCompletesAndTheUnitIsIdle()
        {
            var far = Worker(new Vector3(-10f, 0f, 0f));
            yield return null;

            Assert.That(far.Issue(new InteractCommand(terminal)), Is.True);
            Assert.That(far.CurrentCommand, Is.TypeOf<InteractCommand>());
            yield return TestWorld.WaitUntil(() => terminal.IsCompleted, 15f);

            Assert.That(terminal.IsCompleted, Is.True);
            yield return null;
            Assert.That(far.CurrentCommand, Is.Null);
            Assert.That(TestWorld.HorizontalDistance(far.transform.position, terminal.Position), Is.LessThanOrEqualTo(terminal.Range + 0.2f));
            Assert.That(far.GetComponent<UnitInteractor>().IsWorking, Is.False);
            Assert.That(terminal.User == null, Is.True);
        }

        [UnityTest]
        public IEnumerator TheInteractionFreezesDuringTacticalPause_AndResumesWhereItWas()
        {
            terminal.Initialize(1.8f, 1.5f);
            yield return null;
            unit.Issue(new InteractCommand(terminal));
            yield return WorkingStarted(terminal);

            pause.Pause();
            var frozen = terminal.Progress;
            yield return new WaitForSecondsRealtime(0.4f);

            Assert.That(terminal.Progress, Is.EqualTo(frozen).Within(1e-4f), "no progress while paused");
            Assert.That(terminal.User, Is.EqualTo(unit), "the claim is kept through a pause");
            Assert.That(terminal.IsCompleted, Is.False);

            pause.Resume();
            yield return TestWorld.WaitUntil(() => terminal.IsCompleted, 10f);
            Assert.That(terminal.IsCompleted, Is.True);
        }

        [UnityTest]
        public IEnumerator AnInteractOrderIssuedWhilePaused_IsAccepted_ButNothingRunsUntilResume()
        {
            yield return null;
            pause.Pause();

            Assert.That(unit.Issue(new InteractCommand(terminal)), Is.True);
            yield return new WaitForSecondsRealtime(0.3f);

            Assert.That(terminal.Progress, Is.Zero);
            Assert.That(unit.CurrentCommand, Is.TypeOf<InteractCommand>());

            pause.Resume();
            yield return TestWorld.WaitUntil(() => terminal.IsCompleted, 10f);
            Assert.That(terminal.IsCompleted, Is.True);
        }

        [UnityTest]
        public IEnumerator Stop_CancelsTheInteraction_ReleasesTheClaim_AndLosesProgress()
        {
            Slow();
            yield return null;
            unit.Issue(new InteractCommand(terminal));
            yield return WorkingStarted(terminal);

            unit.Issue(new StopCommand());
            yield return null;

            Assert.That(unit.CurrentCommand, Is.Null);
            Assert.That(terminal.Progress, Is.Zero);
            Assert.That(terminal.User == null, Is.True);
            Assert.That(interactor.IsWorking, Is.False);
        }

        [UnityTest]
        public IEnumerator AReplacingOrder_CancelsTheInteraction()
        {
            Slow();
            yield return null;
            unit.Issue(new InteractCommand(terminal));
            yield return WorkingStarted(terminal);

            Assert.That(unit.Issue(new MoveCommand(MovePoint)), Is.True);
            yield return null;

            Assert.That(unit.CurrentCommand, Is.TypeOf<MoveCommand>());
            Assert.That(terminal.Progress, Is.Zero);
            Assert.That(terminal.User == null, Is.True);
        }

        [UnityTest]
        public IEnumerator DirectControlSteering_CancelsTheInteraction()
        {
            Slow();
            yield return null;
            unit.Issue(new InteractCommand(terminal));
            yield return WorkingStarted(terminal);

            unit.SetMoveIntent(Vector3.forward);
            yield return null;
            yield return null;
            unit.SetMoveIntent(Vector3.zero);

            Assert.That(unit.CurrentCommand, Is.Null);
            Assert.That(terminal.Progress, Is.Zero);
            Assert.That(terminal.User == null, Is.True);
        }

        [UnityTest]
        public IEnumerator ADeadUnit_ReleasesTheTerminal_AndTakesNoFurtherOrders()
        {
            Slow();
            yield return null;
            unit.Issue(new InteractCommand(terminal));
            yield return WorkingStarted(terminal);

            var health = unit.GetComponent<Health>();
            health.TakeDamage(health.Max);
            yield return null;

            Assert.That(terminal.Progress, Is.Zero);
            Assert.That(terminal.User == null, Is.True);
            Assert.That(unit.Issue(new InteractCommand(terminal)), Is.False, "a dead unit takes no orders");
        }

        [UnityTest]
        public IEnumerator ATerminalDestroyedMidWork_EndsTheOrder_AndTheNextQueuedOrderRuns()
        {
            Slow();
            yield return null;
            Assert.That(unit.Issue(new InteractCommand(terminal)), Is.True);
            Assert.That(unit.Issue(new MoveCommand(MovePoint), IssueMode.Append), Is.True);
            yield return WorkingStarted(terminal);

            Object.Destroy(terminal.gameObject);
            yield return TestWorld.WaitUntil(() => unit.CurrentCommand is MoveCommand, 3f);
            Assert.That(unit.CurrentCommand, Is.TypeOf<MoveCommand>());
            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 15f);

            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, MovePoint), Is.LessThan(1f));
            Assert.That(interactor.IsWorking, Is.False);
        }

        [UnityTest]
        public IEnumerator ATerminalDestroyedWhileTheUnitWalks_LeavesTheUnitIdle()
        {
            var far = Worker(new Vector3(-10f, 0f, 0f));
            yield return null;
            far.Issue(new InteractCommand(terminal));
            yield return new WaitForSeconds(0.3f);

            Object.Destroy(terminal.gameObject);
            yield return TestWorld.WaitUntil(() => far.CurrentCommand == null, 3f);

            Assert.That(far.CurrentCommand, Is.Null, "the unit must not keep walking toward a vanished terminal");
        }

        [UnityTest]
        public IEnumerator ATerminalMadeUnavailableMidWork_EndsTheOrder_AndRecordsWhy()
        {
            Slow();
            yield return null;
            unit.Issue(new InteractCommand(terminal));
            yield return WorkingStarted(terminal);

            terminal.SetAvailable(false);
            yield return null;
            yield return null;

            Assert.That(unit.CurrentCommand, Is.Null);
            Assert.That(interactor.LastFailure, Is.EqualTo(InteractionFailure.NotAvailable));
            Assert.That(terminal.User == null, Is.True);
        }

        [UnityTest]
        public IEnumerator ACompletedTerminal_RefusesFurtherOrders()
        {
            terminal.Initialize(1.8f, 0f);
            yield return null;
            unit.Issue(new InteractCommand(terminal));
            yield return TestWorld.WaitUntil(() => terminal.IsCompleted, 10f);

            Assert.That(unit.Issue(new InteractCommand(terminal)), Is.False);
            Assert.That(unit.Issue(new MoveCommand(MovePoint)), Is.True);
            Assert.That(unit.Issue(new InteractCommand(terminal), IssueMode.Append), Is.False);
        }

        [UnityTest]
        public IEnumerator AnUnreachableTerminal_IsRefused()
        {
            yield return null;
            var offMap = Terminal(new Vector3(100f, 0f, 100f), 1f);

            Assert.That(unit.Issue(new InteractCommand(offMap)), Is.False);
            Assert.That(unit.CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator AnImmediateOrderIsRefused_WhileAnotherUnitWorks_AndTheFirstIsUndisturbed()
        {
            Slow();
            var other = Worker(new Vector3(4f, 0f, -4f));
            yield return null;
            unit.Issue(new InteractCommand(terminal));
            yield return WorkingStarted(terminal);

            Assert.That(other.Issue(new InteractCommand(terminal)), Is.False);
            yield return new WaitForSeconds(0.2f);

            Assert.That(terminal.User, Is.EqualTo(unit));
            Assert.That(unit.CurrentCommand, Is.TypeOf<InteractCommand>());
        }

        [UnityTest]
        public IEnumerator AQueuedInteract_BehindABusyTerminal_FailsCleanlyWhenItsTurnComes()
        {
            Slow();
            var other = Worker(new Vector3(-10f, 0f, 0f));
            yield return null;
            unit.Issue(new InteractCommand(terminal));
            yield return WorkingStarted(terminal);

            Assert.That(other.Issue(new MoveCommand(new Vector3(0f, 0f, 4f))), Is.True);
            Assert.That(other.Issue(new InteractCommand(terminal), IssueMode.Append), Is.True, "queued orders get only the static checks");
            yield return TestWorld.WaitUntil(() => other.CurrentCommand == null, 15f);

            Assert.That(other.CurrentCommand, Is.Null, "the second unit must not get stuck");
            Assert.That(terminal.User, Is.EqualTo(unit));
            Assert.That(unit.CurrentCommand, Is.TypeOf<InteractCommand>());
        }

        [UnityTest]
        public IEnumerator InteractIsQueuedBetweenOtherOrders_MoveThenInteractThenMove()
        {
            terminal.Initialize(1.8f, 0.5f);
            var end = new Vector3(-4f, 0f, 8f);
            yield return null;

            Assert.That(unit.Issue(new MoveCommand(new Vector3(2f, 0f, 2f))), Is.True);
            Assert.That(unit.Issue(new InteractCommand(terminal), IssueMode.Append), Is.True);
            Assert.That(unit.Issue(new MoveCommand(end), IssueMode.Append), Is.True);
            yield return TestWorld.WaitUntil(() => terminal.IsCompleted, 15f);
            Assert.That(terminal.IsCompleted, Is.True);
            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 20f);

            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, end), Is.LessThan(1.2f));
        }

        [UnityTest]
        public IEnumerator AUnitWithoutAnInteractor_RefusesInteract()
        {
            yield return null;
            var plain = world.CreateFighter(new Vector3(4f, 0f, 4f));

            Assert.That(plain.Issue(new InteractCommand(terminal)), Is.False);
            Assert.That(plain.Issue(new InteractCommand(terminal), IssueMode.Append), Is.False);
        }

        [UnityTest]
        public IEnumerator ReissuingInteractOnTheTerminalBeingWorked_KeepsTheProgress()
        {
            Slow();
            yield return null;
            unit.Issue(new InteractCommand(terminal));
            yield return WorkingStarted(terminal);
            var before = terminal.Progress;

            Assert.That(unit.Issue(new InteractCommand(terminal)), Is.True);
            yield return null;

            Assert.That(terminal.Progress, Is.GreaterThanOrEqualTo(before));
            Assert.That(terminal.User, Is.EqualTo(unit));
        }
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.CommandableUnitInteractPlayModeTests"`
Expected: EXIT=2 (every test fails: `Issue` throws `ArgumentException: Unsupported command type InteractCommand`).

- [ ] **Step 3: Implement in `CommandableUnit.cs`**

Make these edits (all in `Assets/_Project/Scripts/Units/CommandableUnit.cs`):

(a) Class summary: append the sentence ` An InteractCommand walks into the target's range, then works on it for its duration (UnitInteractor), re-validating every running frame; any failure, a stop, a replacing order, steering or death ends it and drops the claim and its progress.` before `</summary>` of the class doc.

(b) Add the field and property next to the other lazily found components:

```csharp
        UnitInteractor interactor;
```
after `UnitAbilities abilities;`, and after the `UnitAbilities Abilities => ...` line:

```csharp
        UnitInteractor Interactor => interactor != null ? interactor : interactor = GetComponent<UnitInteractor>();
```

(c) In `Issue`, add `case InteractCommand _:` to the accepted list:

```csharp
                case MoveCommand _:
                case AttackCommand _:
                case MoveToCoverCommand _:
                case AbilityCommand _:
                case InteractCommand _:
                    break;
```

(d) In `Issue`, after the existing "re-issuing an attack on the current target" `if` block, add the same shortcut for interact:

```csharp
            if (command is InteractCommand again && queue.Current is InteractCommand running && running.Target == again.Target
                && Interactor != null && Interactor.Check(again.Target) == InteractionFailure.None)
            {
                queue.Replace(again);
                return true;
            }
```

and directly after the ability check (`if (command is AbilityCommand startingAbility && ...) return false;`), add:

```csharp
            if (command is InteractCommand startingInteract && !CanOrderInteract(startingInteract))
                return false;
```

(e) In `CanStart` add before `default`:

```csharp
                case InteractCommand interact:
                    // Queued: only what cannot change by the time it runs. Another unit's claim and the walk are checked then.
                    return Interactor != null && Interactor.Check(interact.Target, allowOtherUser: true) == InteractionFailure.None;
```

(f) Rename the existing `TryStart` to `TryStartCommand` (private, same body) and add this wrapper above it, plus the new case in its switch (before `default`):

```csharp
        // Starts carrying out an order. Returns false, without side effects, if it cannot be carried out. A new current order
        // ends an interaction in progress (the claim and its progress), unless it is the interact order taking over.
        bool TryStart(UnitCommand command)
        {
            if (!TryStartCommand(command))
                return false;
            if (!(command is InteractCommand) && Interactor != null)
                Interactor.Release();
            return true;
        }
```

```csharp
                case InteractCommand interact:
                {
                    var worker = Interactor;
                    var target = interact.Target;
                    if (worker == null || worker.Check(target) != InteractionFailure.None)
                        return false;
                    var inRange = worker.InRange(target, transform.position);
                    if (!inRange && !Mover.CanMoveTo(target.Position))
                        return false;
                    worker.Release();
                    if (inRange)
                        Mover.Stop();
                    else if (!WalkTo(target.Position))
                        return false;
                    walkProgressTime = Time.time;
                    Cover.ReleaseReservation();
                    ResetAttack();
                    return true;
                }
```

(g) In the `Update` `switch (queue.Current)` add:

```csharp
                case InteractCommand interact:
                    UpdateInteract(interact);
                    break;
```

(h) Add the release to `StopAll` and `Finish`:

```csharp
        void StopAll()
        {
            Cover.ReleaseReservation();
            if (Interactor != null)
                Interactor.Release();
            Mover.Stop();
            ResetAttack();
            queue.Clear();
        }
```

```csharp
        void Finish()
        {
            if (Interactor != null)
                Interactor.Release();
            Mover.Stop();
            ResetAttack();
            StartNext();
        }
```

(i) Add the two new methods next to `UpdateAbility`:

```csharp
        // Whether an interact order passes its checks to start now: a usable, unclaimed target the unit is in range of or can reach.
        bool CanOrderInteract(InteractCommand order)
        {
            var worker = Interactor;
            if (worker == null || worker.Check(order.Target) != InteractionFailure.None)
                return false;
            return worker.InRange(order.Target, transform.position) || Mover.CanReach(order.Target.Position);
        }

        // Re-validates every running frame: a vanished, completed or taken terminal ends the order (the queue moves on). In
        // range the unit stops, faces the terminal, claims it and adds its scaled frame time (frozen by pause, which stops
        // this Update). Out of range it walks; arriving or stalling out of range, or being pushed out of range while working,
        // ends the order.
        void UpdateInteract(InteractCommand order)
        {
            var worker = Interactor;
            var target = order.Target;
            var failure = worker != null ? worker.Check(target) : InteractionFailure.NoTarget;
            if (failure != InteractionFailure.None)
            {
                if (worker != null)
                    worker.Record(failure);
                Finish();
                return;
            }
            if (worker.InRange(target, transform.position))
            {
                if (!worker.IsWorking)
                {
                    if (!worker.TryStart(target))
                    {
                        Finish();
                        return;
                    }
                    Mover.Stop();
                }
                FaceTowards(target.Position);
                if (worker.Advance(Time.deltaTime) != InteractionStep.Working)
                    Finish();
                return;
            }
            if (worker.IsWorking || Mover.HasArrived || WalkStalled())
                Finish();
        }
```

- [ ] **Step 4: Run to verify pass**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.CommandableUnitInteractPlayModeTests"` → EXIT=0, 17 tests passed.
Then regression: `Tools/run-tests.sh PlayMode "Blackglass.Tests.CommandableUnit"` (all existing unit/queue/ability/cover order tests) → EXIT=0, and `Tools/run-tests.sh EditMode "Blackglass.Tests.CommandableUnitTests"` → EXIT=0.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/Units/CommandableUnit.cs Assets/_Project/Tests/PlayMode/CommandableUnitInteractPlayModeTests.cs Assets/_Project/Tests/PlayMode/CommandableUnitInteractPlayModeTests.cs.meta
git commit -m "Run Interact orders in CommandableUnit: walk into range, work for the duration, fail cleanly

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Objective placement (settings, salted seed stream, ObjectivePlan, ObjectivePlacer)

**Files:**
- Modify: `Assets/_Project/Scripts/Mission/SeededRandom.cs`
- Modify: `Assets/_Project/Scripts/Mission/MissionSettings.cs`
- Create: `Assets/_Project/Scripts/Mission/ObjectivePlan.cs`
- Create: `Assets/_Project/Scripts/Mission/ObjectivePlacer.cs`
- Test: `Assets/_Project/Tests/EditMode/ObjectivePlacerTests.cs`; modify `Assets/_Project/Tests/EditMode/MissionSettingsTests.cs`, and add a case to the existing `SeededRandomTests.cs` (read it first; add the test at its end).

**Interfaces:**
- Consumes: `MissionLayout` (`Rooms`, `Connections`, `FriendlyRoom`, `FriendlySpawns`, `HostileSpawns`, `Boxes`, `Seed`, `Attempt`, `IsFloor`, `Width`, `Height`), `MissionBoxKind`, `MissionConstants.SpawnSpacing`, `SeededRandom`.
- Produces:
  - `SeededRandom.ForObjectives(int seed, int attempt)`; `ForAttempt` output unchanged.
  - New `MissionSettings` fields: `int guardCount = 2` (0..4), `float interactionSeconds = 2f` (0..30), `int extractionUnits = 1` (1..6), `bool eliminateHostiles = true`, `bool hackTerminal = true` (`Validated()` forces `hackTerminal` on when both are off); `Describe()` mentions all five.
  - `ObjectivePlan`: `TerminalRoom`, `TerminalTile`, `GuardTiles`, `ExtractionRoom`, `ExtractionTile`, `Hash`.
  - `ObjectivePlacer.TryPlace(MissionLayout layout, MissionSettings settings, out ObjectivePlan plan, out string reason)` and constants `FreeRadius = 2`, `ExtractionClearance = 4f`, `GuardMinReach = 2`, `GuardMaxReach = 6`.

- [ ] **Step 1: Write the failing tests**

`Assets/_Project/Tests/EditMode/ObjectivePlacerTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class ObjectivePlacerTests
    {
        // Replaced in Step 6 with the measured hashes.
        const ulong Golden12345 = 0UL;
        const ulong Golden1 = 0UL;
        const ulong Golden2 = 0UL;

        // The pipeline's loop in miniature: the first attempt whose layout also takes a placement.
        static (MissionLayout layout, ObjectivePlan plan, MissionSettings settings, int attempt) Build(int seed, Action<MissionSettings> tweak = null)
        {
            var settings = new MissionSettings { seed = seed };
            tweak?.Invoke(settings);
            settings = settings.Validated();
            for (var attempt = 1; attempt <= settings.maxAttempts; attempt++)
            {
                if (MissionGenerator.TryAttempt(settings, attempt, out var layout, out _)
                    && ObjectivePlacer.TryPlace(layout, settings, out var plan, out _))
                    return (layout, plan, settings, attempt);
            }
            Assert.Fail($"seed {seed}: no attempt produced a layout with objectives");
            return default;
        }

        static int[] Distances(MissionLayout layout, int from)
        {
            var n = layout.Rooms.Count;
            var d = Enumerable.Repeat(-1, n).ToArray();
            d[from] = 0;
            var queue = new Queue<int>();
            queue.Enqueue(from);
            while (queue.Count > 0)
            {
                var room = queue.Dequeue();
                foreach (var c in layout.Connections)
                {
                    var next = c.RoomA == room ? c.RoomB : c.RoomB == room ? c.RoomA : -1;
                    if (next < 0 || d[next] >= 0)
                        continue;
                    d[next] = d[room] + 1;
                    queue.Enqueue(next);
                }
            }
            return d;
        }

        static bool Blocked(MissionLayout layout, int x, int y) =>
            layout.Boxes.Any(b => b.Kind != MissionBoxKind.Wall && b.Footprint.Contains(new Vector2Int(x, y)));

        static bool FreeBlock(MissionLayout layout, Vector2Int tile, int radius)
        {
            for (var dy = -radius; dy <= radius; dy++)
                for (var dx = -radius; dx <= radius; dx++)
                    if (!layout.IsFloor(tile.x + dx, tile.y + dy) || Blocked(layout, tile.x + dx, tile.y + dy))
                        return false;
            return true;
        }

        static int Chebyshev(Vector2Int a, Vector2Int b) => Mathf.Max(Mathf.Abs(a.x - b.x), Mathf.Abs(a.y - b.y));

        static MissionLayout Tiny()
        {
            const int width = 20;
            const int height = 10;
            var floor = new bool[width * height];
            void Fill(RectInt r)
            {
                for (var y = r.yMin; y < r.yMax; y++)
                    for (var x = r.xMin; x < r.xMax; x++)
                        floor[y * width + x] = true;
            }
            var a = new RectInt(1, 1, 4, 4);
            var b = new RectInt(12, 1, 4, 4);
            var strip = new RectInt(5, 2, 7, 2);
            Fill(a);
            Fill(b);
            Fill(strip);
            return new MissionLayout(1, 1, width, height, floor)
            {
                Rooms = new[] { new MissionRoom(0, new Vector2Int(0, 0), a), new MissionRoom(1, new Vector2Int(1, 0), b) },
                Connections = new[] { new MissionConnection(0, 1, strip) },
                FriendlyRoom = 0,
            };
        }

        [Test]
        public void SameSeedTwice_GivesTheSamePlan()
        {
            Assert.That(Build(12345).plan.Hash, Is.EqualTo(Build(12345).plan.Hash));
        }

        [Test]
        public void DifferentSeeds_GiveDifferentPlans()
        {
            var distinct = Enumerable.Range(1, 30).Select(s => Build(s).plan.Hash).Distinct().Count();
            Assert.That(distinct, Is.GreaterThanOrEqualTo(25));
        }

        [Test]
        public void GoldenSeeds_ArePinned()
        {
            // Re-pin deliberately when the placement algorithm changes (record 033).
            Assert.That(Build(12345).plan.Hash, Is.EqualTo(Golden12345));
            Assert.That(Build(1).plan.Hash, Is.EqualTo(Golden1));
            Assert.That(Build(2).plan.Hash, Is.EqualTo(Golden2));
        }

        [Test]
        public void Placement_UsesItsOwnStream_AndNeverTouchesTheGlobalRandom()
        {
            var (layout, first, settings, _) = Build(7);

            UnityEngine.Random.InitState(99);
            var expected = UnityEngine.Random.value;
            UnityEngine.Random.InitState(99);
            Assert.That(ObjectivePlacer.TryPlace(layout, settings, out var second, out _), Is.True);
            var after = UnityEngine.Random.value;

            Assert.That(after, Is.EqualTo(expected), "placement must not consume UnityEngine.Random");
            Assert.That(second.Hash, Is.EqualTo(first.Hash));
        }

        [Test]
        public void ObjectiveToggles_DoNotChangeWhereThingsAreFound()
        {
            var on = Build(5).plan.Hash;
            var off = Build(5, s => { s.hackTerminal = false; s.eliminateHostiles = true; }).plan.Hash;
            Assert.That(off, Is.EqualTo(on));
        }

        [Test]
        public void ThePlacement_DoesNotChangeTheLayout()
        {
            var (layout, _, settings, _) = Build(3);
            var before = layout.Hash;
            ObjectivePlacer.TryPlace(layout, settings, out _, out _);
            Assert.That(layout.Hash, Is.EqualTo(before));
        }

        [Test]
        public void EveryPlan_ObeysThePlacementRules()
        {
            var inFriendlyRoom = 0;
            for (var seed = 1; seed <= 60; seed++)
            {
                var (layout, plan, settings, _) = Build(seed);
                var rooms = layout.Rooms;
                var fromFriendly = Distances(layout, layout.FriendlyRoom);
                var spawns = layout.FriendlySpawns.Concat(layout.HostileSpawns).ToList();

                Assert.That(plan.TerminalRoom, Is.Not.EqualTo(layout.FriendlyRoom), $"seed {seed}");
                Assert.That(fromFriendly[plan.TerminalRoom], Is.GreaterThanOrEqualTo((fromFriendly.Max() + 1) / 2), $"seed {seed}: deeper half");
                Assert.That(rooms[plan.TerminalRoom].Rect.Contains(plan.TerminalTile), Is.True, $"seed {seed}");
                Assert.That(FreeBlock(layout, plan.TerminalTile, ObjectivePlacer.FreeRadius), Is.True, $"seed {seed}: 5x5 free around the terminal");
                foreach (var spawn in spawns)
                    Assert.That(Vector2.Distance(plan.TerminalTile, spawn), Is.GreaterThanOrEqualTo(MissionConstants.SpawnSpacing), $"seed {seed}");

                Assert.That(plan.GuardTiles.Count, Is.LessThanOrEqualTo(settings.guardCount));
                foreach (var guard in plan.GuardTiles)
                {
                    Assert.That(rooms[plan.TerminalRoom].Rect.Contains(guard), Is.True, $"seed {seed}: guard in the terminal room");
                    Assert.That(Chebyshev(guard, plan.TerminalTile), Is.InRange(ObjectivePlacer.GuardMinReach, ObjectivePlacer.GuardMaxReach), $"seed {seed}");
                    Assert.That(FreeBlock(layout, guard, 0), Is.True, $"seed {seed}: guard on free floor");
                    foreach (var friendly in layout.FriendlySpawns)
                        Assert.That(Vector2.Distance(guard, friendly), Is.GreaterThanOrEqualTo(settings.minTeamSeparation), $"seed {seed}");
                    foreach (var other in spawns.Concat(plan.GuardTiles.Where(g => g != guard)))
                        Assert.That(Vector2.Distance(guard, other), Is.GreaterThanOrEqualTo(MissionConstants.SpawnSpacing), $"seed {seed}");
                }

                Assert.That(plan.ExtractionRoom, Is.Not.EqualTo(plan.TerminalRoom), $"seed {seed}");
                Assert.That(rooms[plan.ExtractionRoom].Rect.Contains(plan.ExtractionTile), Is.True, $"seed {seed}");
                Assert.That(FreeBlock(layout, plan.ExtractionTile, ObjectivePlacer.FreeRadius), Is.True, $"seed {seed}");
                foreach (var other in spawns.Concat(plan.GuardTiles))
                    Assert.That(Vector2.Distance(plan.ExtractionTile, other), Is.GreaterThanOrEqualTo(ObjectivePlacer.ExtractionClearance), $"seed {seed}");
                if (plan.ExtractionRoom == layout.FriendlyRoom)
                    inFriendlyRoom++;
            }
            Assert.That(inFriendlyRoom, Is.LessThanOrEqualTo(2), "the friendly room is only the last-resort extraction room");
        }

        [Test]
        public void GuardCount_IsHonoured_AsAnUpperBound()
        {
            Assert.That(Build(11, s => s.guardCount = 0).plan.GuardTiles, Is.Empty);
            Assert.That(Build(11, s => s.guardCount = 4).plan.GuardTiles.Count, Is.InRange(0, 4));
        }

        [Test]
        public void EverySeedYieldsAPlanWithinTheAttemptBudget()
        {
            var attempts = new List<int>();
            for (var seed = 1; seed <= 200; seed++)
                attempts.Add(Build(seed).attempt);
            TestContext.WriteLine($"attempt 1: {attempts.Count(a => a == 1)}/200, mean {attempts.Average():0.00}, max {attempts.Max()}");
            Assert.That(attempts.Max(), Is.LessThanOrEqualTo(20));
        }

        [Test]
        public void ALayoutWithNoRoomForATerminal_FailsWithAReason_InsteadOfThrowing()
        {
            var layout = Tiny();
            var settings = new MissionSettings { seed = 1 }.Validated();

            var placed = ObjectivePlacer.TryPlace(layout, settings, out var plan, out var reason);

            Assert.That(placed, Is.False);
            Assert.That(plan, Is.Null);
            Assert.That(reason, Does.Contain("terminal"));
        }
    }
}
```

Add to `MissionSettingsTests.cs`:

```csharp
        [Test]
        public void ObjectiveSettings_AreClamped_AndAtLeastOneRequiredObjectiveRemains()
        {
            var v = new MissionSettings { guardCount = 99, interactionSeconds = -3f, extractionUnits = 0 }.Validated();
            Assert.That(v.guardCount, Is.EqualTo(4));
            Assert.That(v.interactionSeconds, Is.EqualTo(0f));
            Assert.That(v.extractionUnits, Is.EqualTo(1));
            Assert.That(new MissionSettings { guardCount = -1, interactionSeconds = 99f, extractionUnits = 99 }.Validated().guardCount, Is.EqualTo(0));

            var none = new MissionSettings { eliminateHostiles = false, hackTerminal = false }.Validated();
            Assert.That(none.hackTerminal, Is.True, "a mission always has a required objective");
            var kept = new MissionSettings { eliminateHostiles = false, hackTerminal = true }.Validated();
            Assert.That(kept.eliminateHostiles, Is.False);
        }

        [Test]
        public void Describe_AlsoNamesTheObjectiveSettings()
        {
            var text = new MissionSettings().Describe();
            foreach (var part in new[] { "guards=2", "interact=2", "extractionUnits=1", "eliminate=True", "hack=True" })
                Assert.That(text, Does.Contain(part));
        }
```

Add to `SeededRandomTests.cs`:

```csharp
        [Test]
        public void ForObjectives_IsDeterministic_AndIndependentOfTheLayoutStream()
        {
            Assert.That(SeededRandom.ForObjectives(5, 1).NextULong(), Is.EqualTo(SeededRandom.ForObjectives(5, 1).NextULong()));
            Assert.That(SeededRandom.ForObjectives(5, 1).NextULong(), Is.Not.EqualTo(SeededRandom.ForAttempt(5, 1).NextULong()));
            Assert.That(SeededRandom.ForObjectives(5, 1).NextULong(), Is.Not.EqualTo(SeededRandom.ForObjectives(5, 2).NextULong()));
            Assert.That(SeededRandom.ForObjectives(5, 1).NextULong(), Is.Not.EqualTo(SeededRandom.ForObjectives(6, 1).NextULong()));
        }
```

- [ ] **Step 2: Run to verify failure**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.ObjectivePlacerTests"` → EXIT=1 (`ObjectivePlacer`, `ObjectivePlan`, `ForObjectives`, the new settings fields do not exist).

- [ ] **Step 3: `SeededRandom.ForObjectives`**

In `SeededRandom.cs` replace `ForAttempt` with a shared private factory so its output is byte-for-byte unchanged:

```csharp
        /// <summary>The generator for one attempt of one seed: attempt n always gets the same stream.</summary>
        public static SeededRandom ForAttempt(int seed, int attempt) => ForStream(seed, attempt, 0x632BE59BD9B4E019UL);

        /// <summary>
        /// The objective placer's stream for one attempt of one seed: independent of the layout stream, so the layout never
        /// depends on where objectives go and the placement never depends on any layout draw count.
        /// </summary>
        public static SeededRandom ForObjectives(int seed, int attempt) => ForStream(seed, attempt, 0x0B1EC71F0C0FFEE1UL);

        static SeededRandom ForStream(int seed, int attempt, ulong salt)
        {
            unchecked
            {
                // The outer Finish hashes the seed too: without it, seed + 1 would start one step into the stream of seed.
                return new SeededRandom(Finish((ulong)(uint)seed * Gamma + Finish((ulong)(uint)attempt + salt)));
            }
        }
```

- [ ] **Step 4: Settings**

In `MissionSettings.cs` add after `maxAttempts`:

```csharp
        /// <summary>Extra hostiles placed around the terminal (an upper bound: fewer when they do not fit).</summary>
        public int guardCount = 2;
        /// <summary>How long a unit works on the terminal.</summary>
        public float interactionSeconds = 2f;
        /// <summary>Living squad members that must be inside the extraction zone.</summary>
        public int extractionUnits = 1;
        /// <summary>The hostile group must be eliminated before extraction opens.</summary>
        public bool eliminateHostiles = true;
        /// <summary>The terminal must be hacked before extraction opens.</summary>
        public bool hackTerminal = true;
```

in `Validated()` before `return copy;`:

```csharp
            copy.guardCount = Mathf.Clamp(guardCount, 0, 4);
            copy.interactionSeconds = Mathf.Clamp(interactionSeconds, 0f, 30f);
            copy.extractionUnits = Mathf.Clamp(extractionUnits, 1, 6);
            // A mission always has a required objective; with both off the terminal stays.
            if (!copy.eliminateHostiles && !copy.hackTerminal)
                copy.hackTerminal = true;
```

and extend `Describe()` so the whole expression stays one valid concatenation, ending with:

```csharp
            FormattableString.Invariant($"hostiles={hostileCount} separation={minTeamSeparation:0.##} attempts={maxAttempts} ") +
            FormattableString.Invariant($"guards={guardCount} interact={interactionSeconds:0.##} extractionUnits={extractionUnits} eliminate={eliminateHostiles} hack={hackTerminal}");
```

- [ ] **Step 5: `ObjectivePlan` and `ObjectivePlacer`**

`Assets/_Project/Scripts/Mission/ObjectivePlan.cs`:

```csharp
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Where the mission's objective content goes, as integer tile coordinates (pure data, like the layout). The terminal and
    /// the extraction zone are placed on tiles; the guards are extra hostile spawn tiles in the terminal room. Hash is FNV-1a
    /// over everything, so tests can pin and compare placements.
    /// </summary>
    public sealed class ObjectivePlan
    {
        internal ObjectivePlan(int terminalRoom, Vector2Int terminalTile, IReadOnlyList<Vector2Int> guardTiles,
            int extractionRoom, Vector2Int extractionTile)
        {
            TerminalRoom = terminalRoom;
            TerminalTile = terminalTile;
            GuardTiles = guardTiles;
            ExtractionRoom = extractionRoom;
            ExtractionTile = extractionTile;
            Hash = ComputeHash();
        }

        public int TerminalRoom { get; }
        public Vector2Int TerminalTile { get; }
        public IReadOnlyList<Vector2Int> GuardTiles { get; }
        public int ExtractionRoom { get; }
        public Vector2Int ExtractionTile { get; }
        public ulong Hash { get; }

        ulong ComputeHash()
        {
            var h = 14695981039346656037UL;
            void Add(int value)
            {
                unchecked
                {
                    h ^= (uint)value;
                    h *= 1099511628211UL;
                }
            }
            Add(TerminalRoom);
            Add(TerminalTile.x);
            Add(TerminalTile.y);
            Add(GuardTiles.Count);
            foreach (var tile in GuardTiles)
            {
                Add(tile.x);
                Add(tile.y);
            }
            Add(ExtractionRoom);
            Add(ExtractionTile.x);
            Add(ExtractionTile.y);
            return h;
        }
    }
}
```

`Assets/_Project/Scripts/Mission/ObjectivePlacer.cs`:

```csharp
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Chooses where the mission's objective content goes, from the generated layout alone: pure integer tile work with its
    /// own seed stream (SeededRandom.ForObjectives), so it is deterministic per seed and attempt and independent of combat
    /// randomness. Rules (decision 033): the terminal goes in a room other than the friendly one and at least half the
    /// farthest room-graph distance from it, preferring rooms with obstacles (usable combat geometry), on a tile with two
    /// free tiles on every side (a solid terminal never narrows a path to a gap too small for the NavMesh agent); guards are
    /// extra hostile tiles in that room; the extraction zone goes in a different room (the friendly room only as a last
    /// resort), preferably one or two rooms from the terminal's, clear of every spawn and guard. A failure (no tile) fails the
    /// attempt and the pipeline retries.
    /// </summary>
    public static class ObjectivePlacer
    {
        /// <summary>Free tiles required on every side of a terminal or extraction centre.</summary>
        public const int FreeRadius = 2;
        public const float ExtractionClearance = 4f;
        public const int GuardMinReach = 2;
        public const int GuardMaxReach = 6;

        public static bool TryPlace(MissionLayout layout, MissionSettings settings, out ObjectivePlan plan, out string reason)
        {
            plan = null;
            reason = null;
            var rng = SeededRandom.ForObjectives(layout.Seed, layout.Attempt);
            var rooms = layout.Rooms;
            var blocked = ObstacleMask(layout);
            var spawns = new List<Vector2Int>(layout.FriendlySpawns);
            spawns.AddRange(layout.HostileSpawns);

            // Terminal room: not the friendly room, in the deeper half of the room graph, rooms with obstacles first.
            var fromFriendly = RoomDistances(layout, layout.FriendlyRoom);
            var farthest = 0;
            foreach (var distance in fromFriendly)
                farthest = Mathf.Max(farthest, distance);
            var minimum = Mathf.Max(1, (farthest + 1) / 2);
            var withObstacles = new List<int>();
            var bare = new List<int>();
            for (var r = 0; r < rooms.Count; r++)
            {
                if (r == layout.FriendlyRoom || fromFriendly[r] < minimum)
                    continue;
                (HasObstacle(layout, blocked, rooms[r].Rect) ? withObstacles : bare).Add(r);
            }
            rng.Shuffle(withObstacles);
            rng.Shuffle(bare);
            withObstacles.AddRange(bare);

            var terminalRoom = -1;
            var terminalTile = default(Vector2Int);
            foreach (var r in withObstacles)
            {
                var tiles = FreeTiles(layout, blocked, rooms[r].Rect, spawns, MissionConstants.SpawnSpacing);
                if (tiles.Count == 0)
                    continue;
                terminalRoom = r;
                terminalTile = tiles[rng.NextInt(tiles.Count)];
                break;
            }
            if (terminalRoom < 0)
            {
                reason = "no room in the deeper half has a free terminal tile";
                return false;
            }

            // Guards: extra hostile tiles near the terminal, in its room.
            var guards = new List<Vector2Int>();
            if (settings.guardCount > 0)
            {
                var region = Shrink(rooms[terminalRoom].Rect, 1);
                var candidates = new List<Vector2Int>();
                for (var y = region.yMin; y < region.yMax; y++)
                {
                    for (var x = region.xMin; x < region.xMax; x++)
                    {
                        var tile = new Vector2Int(x, y);
                        var reach = Chebyshev(tile, terminalTile);
                        if (reach < GuardMinReach || reach > GuardMaxReach || NearBlocked(layout, blocked, tile, 1))
                            continue;
                        candidates.Add(tile);
                    }
                }
                rng.Shuffle(candidates);
                var placed = new List<Vector2Int>(spawns);
                foreach (var tile in candidates)
                {
                    if (guards.Count >= settings.guardCount)
                        break;
                    if (TooClose(tile, placed, MissionConstants.SpawnSpacing) || TooClose(tile, layout.FriendlySpawns, settings.minTeamSeparation))
                        continue;
                    guards.Add(tile);
                    placed.Add(tile);
                }
            }

            // Extraction: another room, one or two rooms from the terminal's if possible, the friendly room only as a last resort.
            var fromTerminal = RoomDistances(layout, terminalRoom);
            var near = new List<int>();
            var elsewhere = new List<int>();
            for (var r = 0; r < rooms.Count; r++)
            {
                if (r == terminalRoom || r == layout.FriendlyRoom)
                    continue;
                (fromTerminal[r] >= 1 && fromTerminal[r] <= 2 ? near : elsewhere).Add(r);
            }
            rng.Shuffle(near);
            rng.Shuffle(elsewhere);
            near.AddRange(elsewhere);
            near.Add(layout.FriendlyRoom);

            var avoid = new List<Vector2Int>(spawns);
            avoid.AddRange(guards);
            var extractionRoom = -1;
            var extractionTile = default(Vector2Int);
            foreach (var r in near)
            {
                if (r == terminalRoom)
                    continue;
                var tiles = FreeTiles(layout, blocked, rooms[r].Rect, avoid, ExtractionClearance);
                if (tiles.Count == 0)
                    continue;
                extractionRoom = r;
                extractionTile = tiles[rng.NextInt(tiles.Count)];
                break;
            }
            if (extractionRoom < 0)
            {
                reason = "no room has a free extraction tile";
                return false;
            }

            plan = new ObjectivePlan(terminalRoom, terminalTile, guards, extractionRoom, extractionTile);
            return true;
        }

        // Breadth-first room-graph distance from one room (-1 for a room not reachable through the connections).
        static int[] RoomDistances(MissionLayout layout, int from)
        {
            var n = layout.Rooms.Count;
            var distance = new int[n];
            for (var i = 0; i < n; i++)
                distance[i] = -1;
            distance[from] = 0;
            var queue = new Queue<int>();
            queue.Enqueue(from);
            while (queue.Count > 0)
            {
                var room = queue.Dequeue();
                foreach (var connection in layout.Connections)
                {
                    var next = connection.RoomA == room ? connection.RoomB : connection.RoomB == room ? connection.RoomA : -1;
                    if (next < 0 || distance[next] >= 0)
                        continue;
                    distance[next] = distance[room] + 1;
                    queue.Enqueue(next);
                }
            }
            return distance;
        }

        // Tiles covered by an obstacle (walls stand on void tiles and are excluded; the floor mask covers them).
        static bool[] ObstacleMask(MissionLayout layout)
        {
            var mask = new bool[layout.Width * layout.Height];
            foreach (var box in layout.Boxes)
            {
                if (box.Kind == MissionBoxKind.Wall)
                    continue;
                var f = box.Footprint;
                for (var y = f.yMin; y < f.yMax; y++)
                    for (var x = f.xMin; x < f.xMax; x++)
                        mask[y * layout.Width + x] = true;
            }
            return mask;
        }

        static bool IsBlocked(MissionLayout layout, bool[] blocked, int x, int y) =>
            x >= 0 && y >= 0 && x < layout.Width && y < layout.Height && blocked[y * layout.Width + x];

        static bool HasObstacle(MissionLayout layout, bool[] blocked, RectInt rect)
        {
            for (var y = rect.yMin; y < rect.yMax; y++)
                for (var x = rect.xMin; x < rect.xMax; x++)
                    if (IsBlocked(layout, blocked, x, y))
                        return true;
            return false;
        }

        static bool NearBlocked(MissionLayout layout, bool[] blocked, Vector2Int tile, int radius)
        {
            for (var dy = -radius; dy <= radius; dy++)
                for (var dx = -radius; dx <= radius; dx++)
                    if (IsBlocked(layout, blocked, tile.x + dx, tile.y + dy))
                        return true;
            return false;
        }

        // Every tile within FreeRadius is floor and carries no obstacle.
        static bool FreeNeighbourhood(MissionLayout layout, bool[] blocked, Vector2Int tile)
        {
            for (var dy = -FreeRadius; dy <= FreeRadius; dy++)
            {
                for (var dx = -FreeRadius; dx <= FreeRadius; dx++)
                {
                    var x = tile.x + dx;
                    var y = tile.y + dy;
                    if (!layout.IsFloor(x, y) || IsBlocked(layout, blocked, x, y))
                        return false;
                }
            }
            return true;
        }

        static List<Vector2Int> FreeTiles(MissionLayout layout, bool[] blocked, RectInt rect, List<Vector2Int> avoid, float minimumDistance)
        {
            var tiles = new List<Vector2Int>();
            for (var y = rect.yMin; y < rect.yMax; y++)
            {
                for (var x = rect.xMin; x < rect.xMax; x++)
                {
                    var tile = new Vector2Int(x, y);
                    if (FreeNeighbourhood(layout, blocked, tile) && !TooClose(tile, avoid, minimumDistance))
                        tiles.Add(tile);
                }
            }
            return tiles;
        }

        static bool TooClose(Vector2Int tile, IReadOnlyList<Vector2Int> others, float distance)
        {
            foreach (var other in others)
            {
                if (Vector2.Distance(tile, other) < distance)
                    return true;
            }
            return false;
        }

        static int Chebyshev(Vector2Int a, Vector2Int b) => Mathf.Max(Mathf.Abs(a.x - b.x), Mathf.Abs(a.y - b.y));

        static RectInt Shrink(RectInt rect, int by) => new RectInt(rect.x + by, rect.y + by, rect.width - 2 * by, rect.height - 2 * by);
    }
}
```

- [ ] **Step 6: Run, pin the golden hashes, re-run**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.ObjectivePlacerTests"`. Expected: everything passes except `GoldenSeeds_ArePinned` (the placeholders are `0UL`). Read the actual values from the failure message, replace `Golden12345`, `Golden1`, `Golden2` with them, delete the "Replaced in Step 6" comment, and re-run the class: EXIT=0. Keep the `attempt 1: N/200 ...` line printed by `EverySeedYieldsAPlanWithinTheAttemptBudget` (in `Logs/TestResults-EditMode.xml` or the run output) for record 033. If a rule test fails on a seed, that is a real placement bug: fix the placer, not the test (the rules are the spec's).

Then run, one at a time: `Tools/run-tests.sh EditMode "Blackglass.Tests.MissionSettingsTests"`, `"Blackglass.Tests.SeededRandomTests"` and `"Blackglass.Tests.MissionGeneratorTests"` (the golden layout hashes must be unchanged) → all EXIT=0.

- [ ] **Step 7: Commit**

```bash
git add Assets/_Project/Scripts/Mission/SeededRandom.cs Assets/_Project/Scripts/Mission/MissionSettings.cs Assets/_Project/Scripts/Mission/ObjectivePlan.cs* Assets/_Project/Scripts/Mission/ObjectivePlacer.cs* Assets/_Project/Tests/EditMode/ObjectivePlacerTests.cs* Assets/_Project/Tests/EditMode/MissionSettingsTests.cs Assets/_Project/Tests/EditMode/SeededRandomTests.cs
git commit -m "Add the pure objective placer, its own seed stream and the objective settings

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 6: World content and pipeline (terminal, zone, guards, registry, navigation check)

**Files:**
- Create: `Assets/_Project/Scripts/Mission/InteractableRegistry.cs`, `Assets/_Project/Scripts/Mission/MissionContent.cs`
- Modify: `Assets/_Project/Scripts/Mission/MissionBuilder.cs`, `MissionSpawner.cs`, `MissionNavigation.cs`, `MissionSlots.cs`, `MissionDirector.cs`
- Modify: `Assets/_Project/Tests/PlayMode/TestSupport/MissionRig.cs`
- Test: `Assets/_Project/Tests/PlayMode/MissionContentPlayModeTests.cs`

**Interfaces:**
- Consumes: `ObjectivePlan`, `ObjectivePlacer` (Task 5), `MissionInteractable`, `UnitInteractor` (Task 3), `MissionSettings` new fields.
- Produces:
  - `InteractableRegistry : MonoBehaviour`: `Items` (`IReadOnlyList<MissionInteractable>`), `Version`, `Rebuild(IEnumerable<MissionInteractable>)`, `NearestAvailable(Vector3 point, float radius)` (flat distance, null when none), `internal Initialize(params MissionInteractable[])`.
  - `MissionSystems.interactables` (`InteractableRegistry`, optional).
  - `MissionContent`: consts `InteractionRange = 1.8f`, `TerminalSize = 0.8f`, `TerminalHeight = 1.2f`, `ZoneRadius = 2f`; `static MissionInteractable AddTerminal(Transform geometry, ObjectivePlan plan, MissionLayout layout, MissionSettings settings, Material material)`; `static Transform CreateZone(MissionLayout layout, ObjectivePlan plan, Transform root)`.
  - `MissionBuilder.Build(layout, groundMaterial, obstacleMaterial, Action<Transform> addContent = null)` (the callback runs on the Geometry node before the NavMesh bake).
  - `GeneratedMission.Plan`, `.Terminal`, `.ExtractionZone`.
  - `MissionSpawner.TrySpawn(mission, friendlySlots, hostileSlots, systems, IReadOnlyList<Vector2Int> guardTiles, out result, out failure)`: guards are extra hostiles, every friendly gets a `UnitInteractor`.
  - `MissionNavigation.ValidateObjectives(MissionLayout layout, ObjectivePlan plan, bool hasTerminal, out string reason)`.
  - `MissionReport.Guards`, `MissionReport.ObjectiveHash`.

- [ ] **Step 1: Update the test rig**

In `Assets/_Project/Tests/PlayMode/TestSupport/MissionRig.cs` add the field `public readonly InteractableRegistry Interactables;`, create it next to the cover registry in the constructor:

```csharp
            Interactables = World.Track(new GameObject("Interactables")).AddComponent<InteractableRegistry>();
```

and add `interactables = Interactables,` to the `new MissionSystems { ... }` initializer.

- [ ] **Step 2: Write the failing tests**

`Assets/_Project/Tests/PlayMode/MissionContentPlayModeTests.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class MissionContentPlayModeTests
    {
        MissionRig rig;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
        }

        static int ZoneCount() => Object.FindObjectsByType<Transform>(FindObjectsSortMode.None).Count(t => t.name == "ExtractionZone");

        [UnityTest]
        public IEnumerator AGeneratedMission_HasASolidTerminal_InTheRegistry_ThatIsNotCover_AndNotWalkable()
        {
            rig = new MissionRig();
            yield return rig.Generate(12345);
            var mission = rig.Director.Current;
            var terminal = mission.Terminal;

            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready), string.Join("\n", rig.Director.Report.Failures));
            Assert.That(terminal, Is.Not.Null);
            Assert.That(terminal.transform.IsChildOf(mission.Geometry), Is.True);
            Assert.That(terminal.GetComponent<Collider>(), Is.Not.Null, "the terminal is solid");
            Assert.That(terminal.GetComponent<CoverSurface>() == null, Is.True, "the terminal offers no cover");
            Assert.That(rig.Interactables.Items, Is.EqualTo(new[] { terminal }));
            Assert.That(NavMesh.SamplePosition(terminal.Position - Vector3.up * 0.6f, out _, 0.3f, NavMesh.AllAreas), Is.False,
                "no NavMesh under the terminal: agents route around it");
            Assert.That(mission.Plan, Is.Not.Null);
            Assert.That(rig.Director.Report.ObjectiveHash, Is.EqualTo(mission.Plan.Hash));
        }

        [UnityTest]
        public IEnumerator TheTerminalAndTheExtractionZone_AreReachableAndAUnitCanStandInRangeOfTheTerminal()
        {
            rig = new MissionRig();
            foreach (var seed in new[] { 12345, 1, 2, 3, 4 })
            {
                yield return rig.Generate(seed);
                Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready), $"seed {seed}");
                var mover = rig.Director.Friendlies[0].GetComponent<UnitMover>();
                var terminal = rig.Director.Current.Terminal;
                var zone = rig.Director.Current.ExtractionZone;

                Assert.That(mover.CanReach(terminal.Position), Is.True, $"seed {seed}: terminal reachable");
                Assert.That(mover.TrySnap(terminal.Position, out var stand), Is.True);
                Assert.That(TestWorld.HorizontalDistance(stand, terminal.Position), Is.LessThanOrEqualTo(terminal.Range - 0.2f),
                    $"seed {seed}: somewhere to stand within reach");
                Assert.That(mover.CanReach(zone.position), Is.True, $"seed {seed}: extraction reachable");
                Assert.That(NavMesh.SamplePosition(zone.position, out _, 0.5f, NavMesh.AllAreas), Is.True, $"seed {seed}: zone centre is on the mesh");
            }
        }

        [UnityTest]
        public IEnumerator Guards_AreExtraHostilesInTheTerminalRoom_AndTheyAreWiredLikeAnyHostile()
        {
            rig = new MissionRig();
            yield return rig.Generate(12345);
            var plan = rig.Director.Current.Plan;
            var layout = rig.Director.Current.Layout;

            Assert.That(rig.Director.Hostiles, Has.Count.EqualTo(layout.HostileSpawns.Count + plan.GuardTiles.Count));
            Assert.That(rig.Encounter.Hostiles, Has.Count.EqualTo(rig.Director.Hostiles.Count));
            Assert.That(rig.Director.Report.Guards, Is.EqualTo(plan.GuardTiles.Count));
            for (var i = 0; i < plan.GuardTiles.Count; i++)
            {
                var guard = rig.Director.Hostiles[layout.HostileSpawns.Count + i];
                var expected = layout.TileCenter(plan.GuardTiles[i]);
                Assert.That(TestWorld.HorizontalDistance(guard.transform.position, expected), Is.LessThan(1.1f));
                Assert.That(guard.GetComponent<EnemyAI>().IsCoverWired, Is.True);
                Assert.That(guard.GetComponent<NavMeshAgent>().isOnNavMesh, Is.True);
            }
            foreach (var friendly in rig.Director.Friendlies)
                Assert.That(friendly.TryGetComponent<UnitInteractor>(out _), Is.True, "every friendly can interact");
            foreach (var hostile in rig.Director.Hostiles)
                Assert.That(hostile.TryGetComponent<UnitInteractor>(out _), Is.False);
        }

        [UnityTest]
        public IEnumerator TheSameSeed_PlacesTheTerminalAtTheSameSpot_AndDifferentSeedsDoNot()
        {
            rig = new MissionRig();
            yield return rig.Generate(12345);
            var first = rig.Director.Current.Terminal.Position;
            var firstZone = rig.Director.Current.ExtractionZone.position;
            yield return rig.Generate(12345);
            Assert.That(rig.Director.Current.Terminal.Position, Is.EqualTo(first));
            Assert.That(rig.Director.Current.ExtractionZone.position, Is.EqualTo(firstZone));

            var spots = new System.Collections.Generic.HashSet<Vector3>();
            foreach (var seed in new[] { 1, 2, 3, 4, 5 })
            {
                yield return rig.Generate(seed);
                spots.Add(rig.Director.Current.Terminal.Position);
            }
            Assert.That(spots.Count, Is.GreaterThanOrEqualTo(4));
        }

        [UnityTest]
        public IEnumerator WithoutTheHackObjective_NoTerminalIsBuilt_ButTheZoneStillIs()
        {
            rig = new MissionRig(new MissionSettings { hackTerminal = false });
            yield return rig.Generate(12345);

            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready));
            Assert.That(rig.Director.Current.Terminal == null, Is.True);
            Assert.That(rig.Interactables.Items, Is.Empty);
            Assert.That(rig.Director.Current.ExtractionZone, Is.Not.Null);
        }

        [UnityTest]
        public IEnumerator Regeneration_ReplacesTheTerminalAndTheZone_AndLeavesNothingBehind()
        {
            rig = new MissionRig();
            yield return rig.Generate(12345);
            var oldTerminal = rig.Director.Current.Terminal;
            yield return rig.Generate(7);
            yield return null;

            Assert.That(oldTerminal == null, Is.True, "the old terminal is destroyed");
            Assert.That(rig.Interactables.Items, Is.EqualTo(new[] { rig.Director.Current.Terminal }));
            Assert.That(ZoneCount(), Is.EqualTo(1));

            rig.Director.Clear();
            yield return null;
            Assert.That(rig.Interactables.Items, Is.Empty);
            Assert.That(ZoneCount(), Is.Zero);
        }

        [UnityTest]
        public IEnumerator AFailedGeneration_LeavesNoObjectiveContent()
        {
            rig = new MissionRig(new MissionSettings { gridColumns = 2, gridRows = 2, cellSize = 12, minTeamSeparation = 60f, maxAttempts = 3 });
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Mission generation failed"));
            yield return rig.Generate(12345);

            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Failed));
            Assert.That(rig.Director.Current, Is.Null);
            Assert.That(rig.Interactables.Items, Is.Empty);
        }
    }
}
#endif
```

- [ ] **Step 3: Run to verify failure**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.MissionContentPlayModeTests"` → EXIT=1 (compile errors: `InteractableRegistry`, `GeneratedMission.Terminal`, `Report.ObjectiveHash`, `MissionSystems.interactables`).

- [ ] **Step 4: Registry and systems**

`Assets/_Project/Scripts/Mission/InteractableRegistry.cs`:

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The mission's current interactables (the terminal). Holds state only, like CoverRegistry: MissionDirector refills it
    /// per mission and empties it on teardown; input and the controller cursor read it. Nothing registers itself, so a
    /// destroyed mission leaves nothing behind here.
    /// </summary>
    public sealed class InteractableRegistry : MonoBehaviour
    {
        readonly List<MissionInteractable> items = new List<MissionInteractable>();

        public IReadOnlyList<MissionInteractable> Items => items;

        /// <summary>Bumped by every Rebuild.</summary>
        public int Version { get; private set; }

        public void Rebuild(IEnumerable<MissionInteractable> replacement)
        {
            if (replacement == null)
                throw new ArgumentNullException(nameof(replacement));
            items.Clear();
            items.AddRange(replacement);
            Version++;
        }

        /// <summary>The closest available interactable within `radius` (flat distance) of `point`, or null.</summary>
        public MissionInteractable NearestAvailable(Vector3 point, float radius)
        {
            MissionInteractable best = null;
            var bestDistance = radius;
            foreach (var item in items)
            {
                if (item == null || !item.IsAvailable)
                    continue;
                var distance = CoverRules.FlatDistance(point, item.Position);
                if (distance > bestDistance)
                    continue;
                best = item;
                bestDistance = distance;
            }
            return best;
        }

        internal void Initialize(params MissionInteractable[] scenePoints) => Rebuild(scenePoints);
    }
}
```

In `MissionSlots.cs` `MissionSystems` add: `public InteractableRegistry interactables;` and update the summary to say `camera`, `abilityTargeting` and `interactables` are optional.

- [ ] **Step 5: `MissionContent` (terminal prop and zone) and the builder hook**

`Assets/_Project/Scripts/Mission/MissionContent.cs`:

```csharp
using Unity.AI.Navigation;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The mission's objective content as scene objects: the terminal prop (a solid cube added to the geometry before the
    /// NavMesh bake, so agents route around it), the extraction zone anchor, and (later) markers and the runtime. It builds no
    /// rooms or walls; the layout and the plan decide where things go.
    /// </summary>
    public static class MissionContent
    {
        public const float InteractionRange = 1.8f;
        public const float TerminalSize = 0.8f;
        public const float TerminalHeight = 1.2f;
        public const float ZoneRadius = 2f;
        const int NotWalkableArea = 1;

        /// <summary>Adds the terminal under `geometry`. Call it from the builder's pre-bake hook.</summary>
        public static MissionInteractable AddTerminal(Transform geometry, ObjectivePlan plan, MissionLayout layout,
            MissionSettings settings, Material material)
        {
            var terminal = GameObject.CreatePrimitive(PrimitiveType.Cube);
            terminal.name = "Terminal";
            terminal.transform.SetParent(geometry, false);
            terminal.transform.position = layout.TileCenter(plan.TerminalTile) + Vector3.up * (TerminalHeight * 0.5f);
            terminal.transform.localScale = new Vector3(TerminalSize, TerminalHeight, TerminalSize);
            if (material != null)
                terminal.GetComponent<Renderer>().sharedMaterial = material;
            // Not walkable, like every obstacle: no island forms on top and the mesh has a hole around it.
            var modifier = terminal.AddComponent<NavMeshModifier>();
            modifier.overrideArea = true;
            modifier.area = NotWalkableArea;
            var interactable = terminal.AddComponent<MissionInteractable>();
            interactable.Initialize(InteractionRange, settings.interactionSeconds);
            return interactable;
        }

        /// <summary>The extraction zone's anchor under the mission root, on the floor at the planned tile.</summary>
        public static Transform CreateZone(MissionLayout layout, ObjectivePlan plan, Transform root)
        {
            var zone = new GameObject("ExtractionZone");
            zone.transform.SetParent(root, false);
            zone.transform.position = layout.TileCenter(plan.ExtractionTile);
            return zone.transform;
        }
    }
}
```

`MissionBuilder.cs`: add `using System;`; add to `GeneratedMission` (after `Layout`):

```csharp
        public ObjectivePlan Plan { get; internal set; }
        /// <summary>The terminal, or null when the mission has no hack objective.</summary>
        public MissionInteractable Terminal { get; internal set; }
        public Transform ExtractionZone { get; internal set; }
```

change the signature to `public static GeneratedMission Build(MissionLayout layout, Material groundMaterial, Material obstacleMaterial, Action<Transform> addContent = null)`, and insert just before the `// Auto sync is off...` comment:

```csharp
            // Objective content that must exist when the NavMesh is baked (the terminal) is added by the caller here.
            addContent?.Invoke(geometry.transform);
```

(`addContent` is a delegate, not a `UnityEngine.Object`, so `?.` is correct.)

- [ ] **Step 6: Spawner**

In `MissionSpawner.cs`: change the signature to

```csharp
        public static bool TrySpawn(GeneratedMission mission, IReadOnlyList<FriendlySlot> friendlySlots,
            IReadOnlyList<HostileSlot> hostileSlots, MissionSystems systems, IReadOnlyList<Vector2Int> guardTiles,
            out MissionSpawnResult result, out string failure)
```

In the friendly loop after `unit.GetComponent<UnitCover>().Wire(systems.coverRegistry);` add `unit.gameObject.AddComponent<UnitInteractor>();`. Replace the hostile loop header and tile lookup:

```csharp
            var hostileTiles = new List<Vector2Int>(layout.HostileSpawns);
            hostileTiles.AddRange(guardTiles);
            for (var i = 0; i < hostileTiles.Count; i++)
            {
                var slot = hostileSlots[i % hostileSlots.Count];
                if (!TryCreate(mission, slot.prefab, $"HostileUnit_{i + 1}", layout.TileCenter(hostileTiles[i]),
                        friendlyCentre - hostileCentre, out var unit, out var ground, out failure))
                    return false;
```

(`hostileCentre` keeps using `layout.HostileSpawns`; the rest of the loop body is unchanged.) Update the class summary: guards are extra hostile tiles from the objective plan, and every friendly gets a `UnitInteractor`.

- [ ] **Step 7: Navigation check**

Add to `MissionNavigation`:

```csharp
        /// <summary>
        /// The objective content must be usable: a complete path from the first friendly spawn to the terminal (with
        /// somewhere to stand within the interaction range), to the extraction centre and to every guard tile.
        /// </summary>
        public static bool ValidateObjectives(MissionLayout layout, ObjectivePlan plan, bool hasTerminal, out string reason)
        {
            if (!NavMesh.SamplePosition(layout.TileCenter(layout.FriendlySpawns[0]), out var start, 1f, NavMesh.AllAreas))
            {
                reason = "the first friendly spawn is not on the NavMesh";
                return false;
            }
            var path = new NavMeshPath();
            if (hasTerminal)
            {
                var terminal = layout.TileCenter(plan.TerminalTile);
                if (!NavMesh.SamplePosition(terminal, out var stand, SnapRadius, NavMesh.AllAreas)
                    || Vector3.Distance(new Vector3(stand.position.x, 0f, stand.position.z), terminal) > MissionContent.InteractionRange - 0.2f)
                {
                    reason = "the terminal has no standing place within reach";
                    return false;
                }
                if (!PathExists(start.position, terminal, path, out reason, "the terminal"))
                    return false;
            }
            if (!PathExists(start.position, layout.TileCenter(plan.ExtractionTile), path, out reason, "the extraction zone"))
                return false;
            foreach (var tile in plan.GuardTiles)
            {
                if (!PathExists(start.position, layout.TileCenter(tile), path, out reason, "a guard spawn"))
                    return false;
            }
            reason = null;
            return true;
        }
```

- [ ] **Step 8: Director**

In `MissionDirector.cs`:

(a) `MissionReport`: add `public int Guards;` and `public ulong ObjectiveHash;`.

(b) Replace `TryAttempt` with:

```csharp
        // One attempt: layout, objective placement, geometry (with the terminal), NavMesh, validation, cover, spawn, camera.
        // A failed attempt leaves nothing behind.
        bool TryAttempt(MissionSettings request, int attempt, ref GeneratedMission mission)
        {
            if (!MissionGenerator.TryAttempt(request, attempt, out var layout, out var reason))
            {
                Report.Failures.Add($"attempt {attempt}: {reason}");
                return false;
            }
            if (!ObjectivePlacer.TryPlace(layout, request, out var plan, out reason))
            {
                Report.Failures.Add($"attempt {attempt}: objectives: {reason}");
                return false;
            }
            MissionInteractable terminal = null;
            Action<Transform> addTerminal = request.hackTerminal
                ? geometry => terminal = MissionContent.AddTerminal(geometry, plan, layout, request, obstacleMaterial)
                : (Action<Transform>)null;
            mission = MissionBuilder.Build(layout, groundMaterial, obstacleMaterial, addTerminal);
            if (!MissionNavigation.Validate(layout, out reason, out var navigation))
            {
                Report.Failures.Add($"attempt {attempt}: navigation: {reason}");
                DestroyMission(mission, true);   // immediate: the next attempt must not see this NavMesh
                return false;
            }
            if (!MissionNavigation.ValidateObjectives(layout, plan, terminal != null, out reason))
            {
                Report.Failures.Add($"attempt {attempt}: objectives: {reason}");
                DestroyMission(mission, true);
                return false;
            }

            var origin = layout.TileCenter(layout.FriendlySpawns[0]);
            systems.coverDiscovery.Discover(mission.Geometry, MissionNavigation.ReachableFrom(origin));

            if (!MissionSpawner.TrySpawn(mission, friendlySlots, hostileSlots, systems, plan.GuardTiles, out var spawned, out reason))
            {
                Report.Failures.Add($"attempt {attempt}: spawn: {reason}");
                ResetSystems();
                DestroyMission(mission, true);
                return false;
            }

            mission.Plan = plan;
            mission.Terminal = terminal;
            mission.ExtractionZone = MissionContent.CreateZone(layout, plan, mission.Root.transform);
            Current = mission;
            friendlies.AddRange(spawned.Friendlies);
            hostiles.AddRange(spawned.Hostiles);
            if (systems.interactables != null)
                systems.interactables.Rebuild(terminal != null ? new[] { terminal } : Array.Empty<MissionInteractable>());
            Fill(Report, layout, navigation, plan);
            FrameCamera(layout);
            return true;
        }
```

(c) `Fill`: add parameter `ObjectivePlan plan` and the two lines `report.Guards = plan.GuardTiles.Count; report.ObjectiveHash = plan.Hash;` (next to `report.LayoutHash`).

(d) `ResetSystems`: add

```csharp
            if (systems.interactables != null)
                systems.interactables.Rebuild(Array.Empty<MissionInteractable>());
```

(e) Update the director's class summary: the pipeline now also places objectives (before the build) and the terminal is part of the bake.

- [ ] **Step 9: Run to verify pass, plus regression**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.MissionContentPlayModeTests"` → EXIT=0.
Regression, one run at a time: `Tools/run-tests.sh PlayMode "Blackglass.Tests.MissionDirectorPlayModeTests"`, `"Blackglass.Tests.MissionBuilderPlayModeTests"`, `"Blackglass.Tests.ProceduralMissionSceneTests"`, `"Blackglass.Tests.MissionDeveloperInputTests"` → all EXIT=0. If a pre-existing test fails because a mission now has more than 3 hostiles (for example one that asserts `Hostiles` has 3 or that the squad's separation covers every hostile), read it: the intent is "the placed hostiles"; fix that test minimally (use `Report.HostileSpawns` or `Layout.HostileSpawns.Count`) and say so in the commit message. Any other failure is a bug in this task.

- [ ] **Step 10: Commit**

```bash
git add Assets/_Project/Scripts/Mission Assets/_Project/Tests/PlayMode/MissionContentPlayModeTests.cs* Assets/_Project/Tests/PlayMode/TestSupport/MissionRig.cs
git commit -m "Build objective content in the mission pipeline: placement, terminal before the bake, zone, guards, registry

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 7: The runtime in the director (lifecycle, victory, failure, regeneration)

**Files:**
- Modify: `Assets/_Project/Scripts/Mission/MissionContent.cs` (add `CreateRuntime`), `Assets/_Project/Scripts/Mission/MissionDirector.cs`
- Test: `Assets/_Project/Tests/PlayMode/MissionRuntimePlayModeTests.cs`

**Interfaces:**
- Consumes: Tasks 1, 2, 3, 6 (`MissionRuntime`, the three objectives, `GeneratedMission.Terminal/ExtractionZone`, `MissionRig.Interactables`).
- Produces:
  - `MissionContent.CreateRuntime(GeneratedMission mission, MissionSettings settings, IReadOnlyList<Health> hostileGroup, IReadOnlyList<Health> squad)` → `MissionRuntime`: goals are `EliminateHostilesObjective("eliminate", "Eliminate security team", hostileGroup)` when `settings.eliminateHostiles`, then `InteractObjective("hack", "Access data terminal", mission.Terminal)` when the mission has a terminal; the extraction is `ReachZoneObjective("extract", "Extraction", zoneCentre, ZoneRadius, settings.extractionUnits, squad, isRequired: false)`.
  - `MissionDirector.Runtime` (`MissionRuntime`, null when there is no mission), `MissionDirector.Phase` (`MissionPhase`, `Inactive` without a runtime). The runtime is created and started when generation reaches Ready, ticked every `Update`, and detached and dropped by every teardown.

- [ ] **Step 1: Write the failing tests**

`Assets/_Project/Tests/PlayMode/MissionRuntimePlayModeTests.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class MissionRuntimePlayModeTests
    {
        MissionRig rig;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
        }

        MissionDirector Director => rig.Director;
        MissionRuntime Runtime => rig.Director.Runtime;

        IEnumerator Start(MissionSettings settings = null, int seed = 12345)
        {
            rig = new MissionRig(settings ?? new MissionSettings { interactionSeconds = 0.3f });
            yield return rig.Generate(seed);
            Assert.That(Director.State, Is.EqualTo(MissionState.Ready), string.Join("\n", Director.Report.Failures));
        }

        static void Kill(Component unit)
        {
            var health = unit.GetComponent<Health>();
            health.TakeDamage(health.Max);
        }

        void DisarmHostiles()
        {
            foreach (var hostile in Director.Hostiles)
                hostile.GetComponent<EnemyAI>().enabled = false;
        }

        void KillAllHostiles()
        {
            foreach (var hostile in Director.Hostiles)
                Kill(hostile);
        }

        void Warp(CommandableUnit unit, Vector3 position) =>
            Assert.That(unit.GetComponent<NavMeshAgent>().Warp(position), Is.True);

        // Hacks the terminal with the first friendly: stands it next to the terminal and orders Interact.
        IEnumerator Hack()
        {
            var unit = Director.Friendlies[0];
            var terminal = Director.Current.Terminal;
            Assert.That(unit.GetComponent<UnitMover>().TrySnap(terminal.Position, out var stand), Is.True);
            Warp(unit, stand);
            Assert.That(unit.Issue(new InteractCommand(terminal)), Is.True);
            yield return TestWorld.WaitUntil(() => terminal.IsCompleted, 10f);
            Assert.That(terminal.IsCompleted, Is.True, "the terminal was hacked");
            yield return null;
        }

        [UnityTest]
        public IEnumerator AFreshMission_IsActive_WithTwoRequiredObjectivesAndALockedExtraction()
        {
            yield return Start();

            Assert.That(Director.Phase, Is.EqualTo(MissionPhase.Active));
            Assert.That(Runtime.Objectives.Select(o => o.Type), Is.EqualTo(new[] { ObjectiveType.EliminateHostiles, ObjectiveType.Interact, ObjectiveType.ReachZone }));
            Assert.That(Runtime.Objectives.Select(o => o.IsRequired), Is.EqualTo(new[] { true, true, false }));
            Assert.That(Runtime.Objectives.Select(o => o.State), Is.EqualTo(new[] { ObjectiveState.Active, ObjectiveState.Active, ObjectiveState.Inactive }));
            Assert.That(Runtime.Objectives.All(o => o.IsKnown), Is.True);
            Assert.That(Director.Current.Terminal.IsAvailable, Is.True, "the active hack objective opens the terminal");
            Assert.That(Runtime.Extraction.TargetPosition, Is.EqualTo(Director.Current.ExtractionZone.position));
        }

        [UnityTest]
        public IEnumerator KillingEveryHostile_CompletesOnlyTheEliminateObjective_NotTheMission()
        {
            yield return Start();
            KillAllHostiles();
            yield return null;
            yield return null;

            Assert.That(rig.Encounter.Outcome, Is.EqualTo(EncounterOutcome.Victory), "the old kill-all rule would have ended it here");
            Assert.That(Runtime.Objectives[0].State, Is.EqualTo(ObjectiveState.Completed));
            Assert.That(Director.Phase, Is.EqualTo(MissionPhase.Active));
            Assert.That(Runtime.Extraction.State, Is.EqualTo(ObjectiveState.Inactive), "extraction stays locked");
            Assert.That(Director.Current.Terminal.IsAvailable, Is.True);
        }

        [UnityTest]
        public IEnumerator HackingFirst_AndThenKilling_OpensExtraction_OnlyWhenBothAreDone()
        {
            yield return Start();
            DisarmHostiles();
            yield return Hack();

            Assert.That(Runtime.Objectives[1].State, Is.EqualTo(ObjectiveState.Completed));
            Assert.That(Director.Phase, Is.EqualTo(MissionPhase.Active), "one of two required objectives is not enough");
            Assert.That(Runtime.Extraction.State, Is.EqualTo(ObjectiveState.Inactive));

            KillAllHostiles();
            yield return null;
            yield return null;

            Assert.That(Director.Phase, Is.EqualTo(MissionPhase.ExtractionOpen));
            Assert.That(Runtime.Extraction.State, Is.EqualTo(ObjectiveState.Active));
        }

        [UnityTest]
        public IEnumerator EnteringTheZoneBeforeTheObjectivesAreDone_DoesNothing()
        {
            yield return Start();
            DisarmHostiles();
            Warp(Director.Friendlies[0], Director.Current.ExtractionZone.position);
            yield return new WaitForSeconds(0.3f);

            Assert.That(Director.Phase, Is.EqualTo(MissionPhase.Active));
            Assert.That(Runtime.Extraction.State, Is.EqualTo(ObjectiveState.Inactive));
        }

        [UnityTest]
        public IEnumerator EnteringTheOpenZone_CompletesTheMission_AndClosesTheTerminal()
        {
            yield return Start();
            DisarmHostiles();
            yield return Hack();
            KillAllHostiles();
            yield return null;
            Assert.That(Director.Phase, Is.EqualTo(MissionPhase.ExtractionOpen));

            Warp(Director.Friendlies[1], Director.Current.ExtractionZone.position);
            yield return null;
            yield return null;

            Assert.That(Director.Phase, Is.EqualTo(MissionPhase.Success));
            Assert.That(Runtime.Extraction.State, Is.EqualTo(ObjectiveState.Completed));
            Assert.That(Director.Current.Terminal.IsAvailable, Is.False);
        }

        [UnityTest]
        public IEnumerator TheWholeSquadDead_IsFailure_EvenIfEveryHostileDiedFirst()
        {
            yield return Start();
            KillAllHostiles();
            yield return null;
            foreach (var friendly in Director.Friendlies)
                Kill(friendly);
            yield return null;
            yield return null;

            Assert.That(Director.Phase, Is.EqualTo(MissionPhase.Failure));
            Assert.That(Runtime.Extraction.State, Is.EqualTo(ObjectiveState.Inactive));
            Assert.That(Director.Current.Terminal.IsAvailable, Is.False);
        }

        [UnityTest]
        public IEnumerator SuccessIsTerminal_LaterDeathsChangeNothing()
        {
            yield return Start();
            DisarmHostiles();
            yield return Hack();
            KillAllHostiles();
            yield return null;
            Warp(Director.Friendlies[0], Director.Current.ExtractionZone.position);
            yield return null;
            yield return null;
            Assert.That(Director.Phase, Is.EqualTo(MissionPhase.Success));

            foreach (var friendly in Director.Friendlies)
                Kill(friendly);
            yield return null;
            yield return null;

            Assert.That(Director.Phase, Is.EqualTo(MissionPhase.Success));
        }

        [UnityTest]
        public IEnumerator WithoutTheHackObjective_KillingTheHostilesAloneOpensExtraction()
        {
            yield return Start(new MissionSettings { hackTerminal = false });
            Assert.That(Runtime.Objectives.Select(o => o.Type), Is.EqualTo(new[] { ObjectiveType.EliminateHostiles, ObjectiveType.ReachZone }));

            KillAllHostiles();
            yield return null;
            yield return null;

            Assert.That(Director.Phase, Is.EqualTo(MissionPhase.ExtractionOpen));
        }

        [UnityTest]
        public IEnumerator WithoutTheEliminateObjective_HackingAloneOpensExtraction_WhileHostilesLive()
        {
            yield return Start(new MissionSettings { eliminateHostiles = false, interactionSeconds = 0.3f });
            Assert.That(Runtime.Objectives.Select(o => o.Type), Is.EqualTo(new[] { ObjectiveType.Interact, ObjectiveType.ReachZone }));
            DisarmHostiles();

            yield return Hack();
            yield return null;

            Assert.That(Director.Phase, Is.EqualTo(MissionPhase.ExtractionOpen));
            Assert.That(rig.Encounter.LivingHostiles, Is.GreaterThan(0));
        }

        [UnityTest]
        public IEnumerator BothObjectivesOff_StillLeavesTheHack_BecauseAMissionNeedsARequiredObjective()
        {
            yield return Start(new MissionSettings { eliminateHostiles = false, hackTerminal = false });

            Assert.That(Runtime.Objectives.Select(o => o.Type), Is.EqualTo(new[] { ObjectiveType.Interact, ObjectiveType.ReachZone }));
        }

        [UnityTest]
        public IEnumerator Regeneration_ReplacesTheRuntime_AndTheOldOneNeverChangesAgain()
        {
            yield return Start();
            var oldRuntime = Runtime;
            var oldTerminal = Director.Current.Terminal;
            DisarmHostiles();

            yield return rig.Generate(7);
            yield return null;

            Assert.That(Runtime, Is.Not.SameAs(oldRuntime));
            Assert.That(oldRuntime.IsDetached, Is.True);
            Assert.That(Director.Phase, Is.EqualTo(MissionPhase.Active));
            Assert.That(Runtime.Objectives.All(o => o.State != ObjectiveState.Completed), Is.True, "a fresh mission has nothing done");
            Assert.That(oldTerminal == null, Is.True);
            Assert.That(rig.Interactables.Items, Is.EqualTo(new[] { Director.Current.Terminal }));
            var phase = oldRuntime.Phase;
            oldRuntime.Tick();
            Assert.That(oldRuntime.Phase, Is.EqualTo(phase));
        }

        [UnityTest]
        public IEnumerator RegeneratingMidInteraction_LeavesNoStaleClaimOrProgress()
        {
            yield return Start(new MissionSettings { interactionSeconds = 5f });
            DisarmHostiles();
            var unit = Director.Friendlies[0];
            var terminal = Director.Current.Terminal;
            unit.GetComponent<UnitMover>().TrySnap(terminal.Position, out var stand);
            Warp(unit, stand);
            unit.Issue(new InteractCommand(terminal));
            yield return TestWorld.WaitUntil(() => terminal.Progress > 0.1f, 5f);
            Assert.That(terminal.Progress, Is.GreaterThan(0.1f));

            yield return rig.Generate(12345);
            yield return null;

            var fresh = Director.Current.Terminal;
            Assert.That(terminal == null, Is.True);
            Assert.That(fresh.Progress, Is.Zero);
            Assert.That(fresh.User == null, Is.True);
            Assert.That(fresh.IsAvailable, Is.True);
            Assert.That(rig.Interactables.Items, Has.Count.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator RegeneratingWhilePaused_ResumesAndStartsAFreshMission()
        {
            yield return Start();
            rig.Pause.Pause();
            Assert.That(rig.Pause.IsPaused, Is.True);

            yield return rig.Generate(3);
            yield return null;

            Assert.That(rig.Pause.IsPaused, Is.False);
            Assert.That(Director.Phase, Is.EqualTo(MissionPhase.Active));
            Assert.That(Runtime.Objectives.All(o => o.State != ObjectiveState.Completed), Is.True);
        }

        [UnityTest]
        public IEnumerator ClearingTheMission_LeavesNoRuntime()
        {
            yield return Start();
            Director.Clear();
            yield return null;

            Assert.That(Director.Runtime, Is.Null);
            Assert.That(Director.Phase, Is.EqualTo(MissionPhase.Inactive));
            Assert.That(rig.Interactables.Items, Is.Empty);
        }

        [UnityTest]
        public IEnumerator AFailedGeneration_HasNoRuntime()
        {
            rig = new MissionRig(new MissionSettings { gridColumns = 2, gridRows = 2, cellSize = 12, minTeamSeparation = 60f, maxAttempts = 3 });
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Mission generation failed"));
            yield return rig.Generate(12345);

            Assert.That(Director.State, Is.EqualTo(MissionState.Failed));
            Assert.That(Director.Runtime, Is.Null);
            Assert.That(Director.Phase, Is.EqualTo(MissionPhase.Inactive));
        }
    }
}
#endif
```

- [ ] **Step 2: Run to verify failure**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.MissionRuntimePlayModeTests"` → EXIT=1 (`MissionDirector.Runtime`, `Phase` do not exist).

- [ ] **Step 3: `MissionContent.CreateRuntime`**

Add `using System.Collections.Generic;` to `MissionContent.cs` and this method:

```csharp
        /// <summary>
        /// The runtime for a built and populated mission: the goals the settings ask for (eliminate the hostile group, hack the
        /// terminal), and the extraction as a locked reach-zone objective at the planned zone. Not started: the caller starts it.
        /// </summary>
        public static MissionRuntime CreateRuntime(GeneratedMission mission, MissionSettings settings,
            IReadOnlyList<Health> hostileGroup, IReadOnlyList<Health> squad)
        {
            var goals = new List<MissionObjective>();
            if (settings.eliminateHostiles)
                goals.Add(new EliminateHostilesObjective("eliminate", "Eliminate security team", hostileGroup));
            if (mission.Terminal != null)
                goals.Add(new InteractObjective("hack", "Access data terminal", mission.Terminal));
            var extraction = new ReachZoneObjective("extract", "Extraction", mission.ExtractionZone.position, ZoneRadius,
                settings.extractionUnits, squad, isRequired: false);
            return new MissionRuntime(goals, extraction, squad);
        }
```

- [ ] **Step 4: Director**

In `MissionDirector.cs`:

(a) Add members next to `Current`:

```csharp
        /// <summary>The running mission's objectives and phase; null while there is no mission (idle, generating, failed).</summary>
        public MissionRuntime Runtime { get; private set; }
        public MissionPhase Phase => Runtime != null ? Runtime.Phase : MissionPhase.Inactive;
```

(b) At the end of `TryAttempt`, right after the `systems.interactables.Rebuild(...)` lines and before `Fill(...)`:

```csharp
            var squad = new List<Health>();
            foreach (var unit in friendlies)
                squad.Add(unit.GetComponent<Health>());
            var group = new List<Health>();
            foreach (var unit in hostiles)
                group.Add(unit.GetComponent<Health>());
            Runtime = MissionContent.CreateRuntime(mission, request, group, squad);
            Runtime.Start();
```

(c) Add:

```csharp
        void Update()
        {
            if (Runtime != null)
                Runtime.Tick();
        }

        // The runtime is dropped before anything it refers to is destroyed, so nothing can fire into a dead mission.
        void DetachRuntime()
        {
            if (Runtime == null)
                return;
            Runtime.Detach();
            Runtime = null;
        }
```

(d) Call `DetachRuntime();` as the first statement of `Teardown()` and as the first statement inside the `catch` of `TryAttemptSafely` (before `friendlies.Clear()`).

- [ ] **Step 5: Run to verify pass, plus regression**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.MissionRuntimePlayModeTests"` → EXIT=0. Then, one at a time: `"Blackglass.Tests.MissionContentPlayModeTests"`, `"Blackglass.Tests.MissionDirectorPlayModeTests"`, `"Blackglass.Tests.ProceduralMissionSceneTests"` → EXIT=0.

- [ ] **Step 6: Commit**

```bash
git add Assets/_Project/Scripts/Mission/MissionContent.cs Assets/_Project/Scripts/Mission/MissionDirector.cs Assets/_Project/Tests/PlayMode/MissionRuntimePlayModeTests.cs*
git commit -m "Run the mission runtime from the director: lifecycle, objectives, success, failure, clean regeneration

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 8: Input — the Interact action, pointer classification, context Confirm, controller cursor

**Files:**
- Modify: `Assets/_Project/Input/BlackglassControls.inputactions` (new action + keyboard binding)
- Modify: `Assets/_Project/Scripts/Input/PointerTarget.cs`, `Input/PointerTargetResolver.cs`, `Input/TacticalCursor.cs`, `Controls/CommandResolver.cs`, `Controls/PlayerCommandInput.cs`, `DebugUI/TacticalCursorView.cs`, `DebugUI/CommandQueueView.cs`
- Test: `Assets/_Project/Tests/EditMode/InteractInputTests.cs`, `Assets/_Project/Tests/PlayMode/InteractInputPlayModeTests.cs`

**Interfaces:**
- Consumes: `MissionInteractable`, `InteractableRegistry` (Task 6), `InteractCommand`, `UnitInteractor` (Tasks 3, 4).
- Produces:
  - `PointerTargetKind.Interactable` (appended after `Ground`), `PointerTarget.Interactable`, `PointerTarget.OnInteractable(MissionInteractable, Vector3 point)`.
  - `CommandResolver.Resolve(Health clicked, Vector3 point, CoverLocation cover = null, MissionInteractable interactable = null)`: a living hostile is attacked, else an available interactable is interacted with, else cover, else move.
  - Input action `Commands/Interact` (keyboard `R`, no pad binding: the pad's route is context Confirm).
  - `PlayerCommandInput.WireInteraction(InteractableRegistry registry, InputActionReference interact)` (internal, call before the component is enabled), `PlayerCommandInput.NearbyInteractable` (public, the available terminal within `contextInteractRadius` (3 m) of the active character, or null).
  - `TacticalCursor.SetInteractables(InteractableRegistry)` (internal); the cursor snaps to an available interactable within 1.5 m of the ground point under it, after friendlies and hostiles and before cover.

- [ ] **Step 1: Write the failing EditMode tests**

`Assets/_Project/Tests/EditMode/InteractInputTests.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class InteractInputTests
    {
        const string AssetPath = "Assets/_Project/Input/BlackglassControls.inputactions";

        readonly List<GameObject> hosts = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (var host in hosts)
            {
                if (host != null)
                    Object.DestroyImmediate(host);
            }
            hosts.Clear();
        }

        MissionInteractable Terminal(bool available = true)
        {
            var host = new GameObject("Terminal");
            hosts.Add(host);
            var terminal = host.AddComponent<MissionInteractable>();
            terminal.SetAvailable(available);
            return terminal;
        }

        Health Hostile()
        {
            var host = new GameObject("Hostile");
            hosts.Add(host);
            return host.AddComponent<Health>();
        }

        [Test]
        public void PointerTarget_OnInteractable_CarriesTheTerminalAndThePoint()
        {
            var terminal = Terminal();
            var target = PointerTarget.OnInteractable(terminal, new Vector3(1f, 2f, 3f));

            Assert.That(target.Kind, Is.EqualTo(PointerTargetKind.Interactable));
            Assert.That(target.Interactable, Is.SameAs(terminal));
            Assert.That(target.Point, Is.EqualTo(new Vector3(1f, 2f, 3f)));
            Assert.That(target.Friendly, Is.Null);
            Assert.That(target.Hostile, Is.Null);
            Assert.That(target.Cover, Is.Null);
        }

        [Test]
        public void CommandResolver_ChoosesInteract_ForAnAvailableTerminal()
        {
            var terminal = Terminal();
            var command = CommandResolver.Resolve(null, Vector3.zero, null, terminal);

            Assert.That(command, Is.TypeOf<InteractCommand>());
            Assert.That(((InteractCommand)command).Target, Is.SameAs(terminal));
        }

        [Test]
        public void CommandResolver_PrefersAnAttackOnALivingHostile_OverATerminal()
        {
            var command = CommandResolver.Resolve(Hostile(), Vector3.zero, null, Terminal());
            Assert.That(command, Is.TypeOf<AttackCommand>());
        }

        [Test]
        public void CommandResolver_IgnoresAnUnavailableTerminal_AndFallsBackToMove()
        {
            var command = CommandResolver.Resolve(null, new Vector3(4f, 0f, 4f), null, Terminal(available: false));
            Assert.That(command, Is.TypeOf<MoveCommand>());
        }

        [Test]
        public void InteractAction_ExistsInTheCommandsMap_WithAKeyboardBindingAndNoPadBinding()
        {
            var asset = AssetDatabase.LoadAssetAtPath<InputActionAsset>(AssetPath);
            var action = asset.FindAction("Commands/Interact", throwIfNotFound: true);

            var keyboard = action.bindings.Where(b => !b.isComposite && b.groups == "KeyboardMouse").Select(b => b.path).ToArray();
            Assert.That(keyboard, Is.EqualTo(new[] { "<Keyboard>/r" }));
            Assert.That(action.bindings.Any(b => b.path.StartsWith("<Gamepad>")), Is.False,
                "the pad interacts through context Confirm, not its own button");
        }

        [Test]
        public void InteractAction_UsesAKeyThatNoOtherActionUses()
        {
            var asset = AssetDatabase.LoadAssetAtPath<InputActionAsset>(AssetPath);
            var users = asset.bindings.Where(b => b.path == "<Keyboard>/r").Select(b => b.action).Distinct().ToArray();
            Assert.That(users, Is.EqualTo(new[] { "Interact" }));
        }
    }
}
```

- [ ] **Step 2: Write the failing PlayMode tests**

`Assets/_Project/Tests/PlayMode/InteractInputPlayModeTests.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.XInput;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class InteractInputPlayModeTests : InputTestFixture
    {
        static readonly Vector3 TerminalGround = new Vector3(2f, 0f, 2f);
        static readonly Vector3 GroundPoint = new Vector3(-2f, 0f, 2f);

        Keyboard keyboard;
        Mouse mouse;
        Gamepad pad;
        InputActionAsset actions;
        TestWorld world;
        Camera viewCamera;
        SelectableUnit far;
        SelectableUnit near;
        MissionInteractable terminal;
        Health hostile;
        TacticalPause pause;
        UnitSelection selection;
        ActiveCharacter active;
        TacticalCursor cursor;
        PlayerCommandInput input;
        InteractableRegistry interactables;

        public override void Setup()
        {
            base.Setup();
            keyboard = InputSystem.AddDevice<Keyboard>();
            mouse = InputSystem.AddDevice<Mouse>();
            pad = InputSystem.AddDevice<XInputController>();
            actions = TestControls.Load();
            TestControls.UseGroup(actions, "Xbox");   // keyboard bindings are masked off by the group in use, so each test picks
            world = new TestWorld();
            world.CreateEnvironment();

            far = world.CreateFriendly(new Vector3(-8f, 0f, -6f));
            far.gameObject.AddComponent<UnitInteractor>();
            near = world.CreateFriendly(new Vector3(0f, 0f, 2f));   // 2 m from the terminal
            near.gameObject.AddComponent<UnitInteractor>();
            hostile = world.CreateDummy(new Vector3(8f, 0f, 8f));

            // A solid marker box outside the baked mesh (it carves nothing); the click ray hits its collider.
            var host = world.CreateObstacle(TerminalGround + Vector3.up * 0.6f, new Vector3(0.8f, 1.2f, 0.8f));
            terminal = host.AddComponent<MissionInteractable>();
            terminal.Initialize(1.8f, 1f);
            terminal.SetAvailable(true);
            interactables = world.Track(new GameObject("Interactables")).AddComponent<InteractableRegistry>();
            interactables.Initialize(terminal);

            var registry = world.CreateRegistry();
            var encounter = world.CreateEncounter();
            encounter.Initialize(new Health[0], new[] { hostile });

            var cameraObject = world.Track(new GameObject("Camera"));
            cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 25f, -20f), Quaternion.Euler(50f, 0f, 0f));
            viewCamera = cameraObject.AddComponent<Camera>();

            var systems = world.Track(new GameObject("Systems"));
            systems.SetActive(false);
            pause = systems.AddComponent<TacticalPause>();
            selection = systems.AddComponent<UnitSelection>();
            selection.Initialize(far, near);
            active = systems.AddComponent<ActiveCharacter>();
            active.Initialize(near.Unit, pause, selection);
            cursor = systems.AddComponent<TacticalCursor>();
            cursor.Initialize(viewCamera, active, selection, encounter, registry, null,
                TestControls.Ref(actions, "Commands/CursorMove"),
                TestControls.Ref(actions, "Camera/CameraModifier"),
                TestControls.Ref(actions, "Commands/NextTarget"),
                TestControls.Ref(actions, "Commands/PreviousTarget"));
            cursor.SetInteractables(interactables);
            input = systems.AddComponent<PlayerCommandInput>();
            input.Initialize(viewCamera, selection, pause,
                TestControls.Ref(actions, "Commands/Command"),
                TestControls.Ref(actions, "Commands/PointerPosition"),
                TestControls.Ref(actions, "Commands/ToggleTacticalPause"),
                TestControls.Ref(actions, "Commands/QueueModifier"),
                TestControls.Ref(actions, "Commands/Stop"),
                TestControls.Ref(actions, "Commands/Cancel"),
                active, registry, cursor,
                TestControls.Ref(actions, "Commands/Confirm"),
                TestControls.Ref(actions, "Commands/Attack"));
            input.WireInteraction(interactables, TestControls.Ref(actions, "Commands/Interact"));
            systems.SetActive(true);
        }

        public override void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
            TestControls.Reset(actions);
            base.TearDown();
        }

        // The keyboard and mouse bindings are masked off while a pad group is in use, and the reverse.
        void UseKeyboard() => TestControls.UseGroup(actions, "KeyboardMouse");

        Vector2 ScreenPointOf(Vector3 worldPoint) => viewCamera.WorldToScreenPoint(worldPoint);

        IEnumerator LeftClickAt(Vector2 screenPoint)
        {
            Set(mouse.position, screenPoint);
            yield return null;
            Press(mouse.leftButton);
            yield return null;
            Release(mouse.leftButton);
            yield return null;
        }

        IEnumerator Tap(ButtonControl button)
        {
            Press(button);
            yield return null;
            Release(button);
            yield return null;
        }

        IEnumerator CursorOn(Vector3 worldPoint)
        {
            cursor.SetScreenPosition(ScreenPointOf(worldPoint));
            yield return null;
        }

        static InteractCommand InteractOf(CommandableUnit unit) => unit.CurrentCommand as InteractCommand;

        // ---- mouse

        [UnityTest]
        public IEnumerator ClickingTheTerminal_OrdersTheSelectedUnitToInteract()
        {
            UseKeyboard();
            selection.Select(far);

            yield return LeftClickAt(ScreenPointOf(terminal.Position));

            Assert.That(InteractOf(far.Unit), Is.Not.Null);
            Assert.That(InteractOf(far.Unit).Target, Is.SameAs(terminal));
            Assert.That(near.Unit.CurrentCommand, Is.Null, "only the selection is ordered");
        }

        [UnityTest]
        public IEnumerator ShiftClickingTheTerminal_QueuesInteractBehindTheCurrentOrder()
        {
            UseKeyboard();
            selection.Select(far);
            yield return LeftClickAt(ScreenPointOf(GroundPoint));
            Assert.That(far.Unit.CurrentCommand, Is.TypeOf<MoveCommand>());

            Press(keyboard.leftShiftKey);
            yield return null;
            yield return LeftClickAt(ScreenPointOf(terminal.Position));
            Release(keyboard.leftShiftKey);
            yield return null;

            Assert.That(far.Unit.CurrentCommand, Is.TypeOf<MoveCommand>());
            Assert.That(far.Unit.PendingCommands, Has.Count.EqualTo(1));
            Assert.That(far.Unit.PendingCommands[0], Is.TypeOf<InteractCommand>());
        }

        [UnityTest]
        public IEnumerator ClickingTheTerminalWhilePaused_QueuesTheOrder_AndNothingProgresses()
        {
            UseKeyboard();
            selection.Select(near);
            pause.Pause();
            var start = near.transform.position;

            yield return LeftClickAt(ScreenPointOf(terminal.Position));
            yield return new WaitForSecondsRealtime(0.3f);

            Assert.That(InteractOf(near.Unit), Is.Not.Null, "planned while paused");
            Assert.That(terminal.Progress, Is.Zero);
            Assert.That(Vector3.Distance(near.transform.position, start), Is.LessThan(0.01f));
            Assert.That(pause.IsPaused, Is.True);
        }

        [UnityTest]
        public IEnumerator ClickingAUsedUpTerminal_IsNotAnInteractOrder()
        {
            UseKeyboard();
            terminal.SetAvailable(false);
            selection.Select(far);

            yield return LeftClickAt(ScreenPointOf(terminal.Position));

            Assert.That(far.Unit.CurrentCommand is InteractCommand, Is.False);
        }

        // ---- keyboard Interact

        [UnityTest]
        public IEnumerator KeyboardInteract_NearATerminal_OrdersTheActiveCharacter()
        {
            UseKeyboard();
            yield return Tap(keyboard.rKey);

            Assert.That(InteractOf(near.Unit), Is.Not.Null);
            Assert.That(InteractOf(near.Unit).Target, Is.SameAs(terminal));
            Assert.That(input.NearbyInteractable, Is.SameAs(terminal));
        }

        [UnityTest]
        public IEnumerator KeyboardInteract_FarFromTheTerminal_DoesNothing()
        {
            UseKeyboard();
            active.Initialize(far.Unit, pause, selection);
            yield return null;

            yield return Tap(keyboard.rKey);

            Assert.That(far.Unit.CurrentCommand, Is.Null);
            Assert.That(input.NearbyInteractable, Is.Null);
        }

        [UnityTest]
        public IEnumerator KeyboardInteract_WithNoAvailableTerminal_DoesNothing()
        {
            UseKeyboard();
            terminal.SetAvailable(false);

            yield return Tap(keyboard.rKey);

            Assert.That(near.Unit.CurrentCommand, Is.Null);
            Assert.That(input.NearbyInteractable, Is.Null);
        }

        // ---- controller

        [UnityTest]
        public IEnumerator PadConfirm_NearATerminal_WithTheCursorHidden_Interacts()
        {
            Assert.That(cursor.IsActive, Is.False, "running with a character: the camera owns the right stick");

            yield return Tap(pad.buttonSouth);

            Assert.That(InteractOf(near.Unit), Is.Not.Null);
            Assert.That(InteractOf(near.Unit).Target, Is.SameAs(terminal));
        }

        [UnityTest]
        public IEnumerator PadConfirm_WithNoTerminalInReach_StillAttacksTheBestHostile()
        {
            active.Initialize(far.Unit, pause, selection);
            yield return null;

            yield return Tap(pad.buttonSouth);

            Assert.That(far.Unit.CurrentCommand, Is.TypeOf<AttackCommand>());
            Assert.That(((AttackCommand)far.Unit.CurrentCommand).Target, Is.SameAs(hostile));
        }

        [UnityTest]
        public IEnumerator PadConfirm_WithTheQueueModifier_AppendsInteract()
        {
            near.Unit.Issue(new MoveCommand(GroundPoint));
            Press(pad.leftTrigger);
            yield return null;

            yield return Tap(pad.buttonSouth);
            Release(pad.leftTrigger);
            yield return null;

            Assert.That(near.Unit.CurrentCommand, Is.TypeOf<MoveCommand>());
            Assert.That(near.Unit.PendingCommands, Has.Count.EqualTo(1));
            Assert.That(near.Unit.PendingCommands[0], Is.TypeOf<InteractCommand>());
        }

        [UnityTest]
        public IEnumerator PadCursor_OnTheTerminal_ConfirmOrdersTheSelectedUnitToInteract()
        {
            selection.Select(far);
            pause.Pause();

            yield return CursorOn(terminal.Position);
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Interactable));
            yield return Tap(pad.buttonSouth);

            Assert.That(InteractOf(far.Unit), Is.Not.Null);
            Assert.That(pause.IsPaused, Is.True, "the order waits for the resume");
        }

        [UnityTest]
        public IEnumerator PadCursor_SnapsToATerminalWithinTheSnapRadius_ButNotBeyondIt()
        {
            pause.Pause();

            yield return CursorOn(TerminalGround + new Vector3(1.0f, 0f, 0f));
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Interactable));

            yield return CursorOn(TerminalGround + new Vector3(3.0f, 0f, 0f));
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Ground));
        }

        [UnityTest]
        public IEnumerator PadCursor_DoesNotSnapToAnUnavailableTerminal()
        {
            pause.Pause();
            terminal.SetAvailable(false);

            yield return CursorOn(TerminalGround + new Vector3(1.0f, 0f, 0f));

            Assert.That(cursor.Target.Kind, Is.Not.EqualTo(PointerTargetKind.Interactable));
        }
    }
}
#endif
```

Note on the binding group: `Setup` activates the `Xbox` group (so the pad tests work); the keyboard and mouse tests call `UseKeyboard()` first. If a test fails only because of the group in use, fix the test helper, not the production code.

- [ ] **Step 3: Run to verify failure**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.InteractInputTests"` → EXIT=1 (compile errors); the PlayMode class also fails to compile.

- [ ] **Step 4: The input action**

In `Assets/_Project/Input/BlackglassControls.inputactions`, in the `Commands` map `actions` array add after `AbilityMenu` (mind the commas):

```json
                { "name": "Interact", "type": "Button", "id": "5b8e0a3c-72d1-4f64-9a3e-0c6d2b8f41a7", "expectedControlType": "Button", "processors": "", "interactions": "", "initialStateCheck": false }
```

and in the `bindings` array of the same map add (next to the other `KeyboardMouse` command bindings):

```json
                { "name": "", "id": "c2f7d914-3e68-4b0a-8d15-7a9e4c3b6f20", "path": "<Keyboard>/r", "interactions": "", "processors": "", "groups": "KeyboardMouse", "action": "Interact", "isComposite": false, "isPartOfComposite": false }
```

(The actions list ends with the comma-less `AbilityMenu` entry today; add a comma after it. Keep the file's line endings and indentation; Unity regenerates the `InputActionReference` sub-assets on import. If the asset is also a generated C# wrapper, there is none in this project; check the import settings in the `.meta` before assuming.)

- [ ] **Step 5: Pointer target, resolver, command resolver**

`PointerTarget.cs`: append `Interactable,` to `PointerTargetKind` (after `Ground`); add the property `public MissionInteractable Interactable { get; }`; extend the private constructor with a trailing `MissionInteractable interactable` parameter assigned to it, pass `null` from the four existing factories, and add:

```csharp
        public static PointerTarget OnInteractable(MissionInteractable interactable, Vector3 point) =>
            new PointerTarget(PointerTargetKind.Interactable, point, null, null, null, interactable);
```

Update the doc comment: `... a living hostile, a cover location, an available interactable (a terminal) or plain ground ...`.

`PointerTargetResolver.cs`: after the hostile block and before the cover lookup add:

```csharp
            var interactable = hit.collider.GetComponentInParent<MissionInteractable>();
            if (interactable != null && interactable.IsAvailable)
                return PointerTarget.OnInteractable(interactable, hit.point);
```

and mention it in the summary.

`CommandResolver.cs`:

```csharp
        public static UnitCommand Resolve(Health clicked, Vector3 point, CoverLocation cover = null, MissionInteractable interactable = null)
        {
            if (clicked != null && clicked.IsAlive)
                return new AttackCommand(clicked);
            if (interactable != null && interactable.IsAvailable)
                return new InteractCommand(interactable);
            if (cover != null)
                return new MoveToCoverCommand(cover);
            return new MoveCommand(point);
        }
```

with the summary updated to `attack a living target, otherwise work an available interactable, otherwise ...`.

- [ ] **Step 6: Cursor, cursor view, queue view**

`TacticalCursor.cs`: add the fields

```csharp
        // Optional: without it the cursor never snaps to a terminal.
        [SerializeField] InteractableRegistry interactables;
```

and in `[Header("Tuning")]` `[SerializeField, Min(0f)] float interactableSnapRadius = 1.5f;`, the method `internal void SetInteractables(InteractableRegistry registry) => interactables = registry;`, and in `Resolve`, after the hostile snap and before the cover lookup:

```csharp
            var interactable = interactables != null ? interactables.NearestAvailable(ground, interactableSnapRadius) : null;
            if (interactable != null)
                return PointerTarget.OnInteractable(interactable, ground);
```

Update the class summary (`friendly, then a hostile, then a terminal, then a cover location`).

`TacticalCursorView.Describe`: add `case PointerTargetKind.Interactable: return $"Interact {target.Interactable.DisplayName}";` and, in whichever other `switch` on the kind draws the crosshair (read the file), treat `Interactable` like `Friendly`'s accent but with its own colour constant (any distinct colour).

`CommandQueueView.AddOrder`: add

```csharp
                case InteractCommand interact when interact.Target != null && interact.Target.gameObject.activeInHierarchy:
                    points.Add(OnGround(interact.Target.Position));
                    break;
```

- [ ] **Step 7: `PlayerCommandInput`**

In `Assets/_Project/Scripts/Controls/PlayerCommandInput.cs`:

(a) Fields (next to `abilityTargeting`):

```csharp
        // Optional. The mission's terminals: Interact and a context Confirm look here for one within reach.
        [SerializeField] InteractableRegistry interactables;
```

in `[Header("Input")]`: `[SerializeField] InputActionReference interactAction;` and in `[Header("Tuning")]`: `// How close the active character must be for Interact or a context Confirm to find a terminal.` `[SerializeField, Min(0.5f)] float contextInteractRadius = 3f;`

(b) Wiring method (next to `Initialize`):

```csharp
        /// <summary>Wires terminal interaction: the registry Interact looks in, and the Interact action. Call before enabling.</summary>
        internal void WireInteraction(InteractableRegistry registry, InputActionReference interact)
        {
            interactables = registry;
            interactAction = interact;
        }

        /// <summary>
        /// The available terminal within reach of the character being played (what Interact and a context Confirm would work
        /// on), or null. The HUD uses it to show the prompt.
        /// </summary>
        public MissionInteractable NearbyInteractable =>
            interactables != null && activeCharacter != null && activeCharacter.HasUnit
                ? interactables.NearestAvailable(activeCharacter.Unit.transform.position, contextInteractRadius)
                : null;
```

(c) `OnEnable`: add `interactAction` to the `InputActionUtility.SetEnabled(true, ...)` list and

```csharp
            if (interactAction != null)
                interactAction.action.performed += OnInteract;
```

`OnDisable`: the matching `-=` and `interactAction` in the `SetEnabled(false, ...)` list.

(d) `OnConfirm`, replace the `else` branch:

```csharp
            if (cursor.IsActive)
                Act(cursor.Refresh());
            else if (!TryInteractNearby())
                AttackBestHostile();
```

(e) New methods (next to `AttackBestHostile`):

```csharp
        // The Interact action (keyboard): the same order a click on the terminal gives, for the terminal in reach. An armed
        // ability owns the next press, so Interact waits.
        void OnInteract(InputAction.CallbackContext context)
        {
            if (abilityTargeting != null && abilityTargeting.IsArmed)
                return;
            TryInteractNearby();
        }

        // Interact with the nearest available terminal within reach of whoever is ordered (the character being played, else the
        // first ordered unit); the queue modifier appends. True when a terminal was found, so a context Confirm that finds one
        // never also attacks, whether or not the unit could take the order.
        bool TryInteractNearby()
        {
            if (interactables == null)
                return false;
            var units = OrderedUnits();
            var anchor = AttackAnchor(units);
            if (anchor == null)
                return false;
            var terminal = interactables.NearestAvailable(anchor.transform.position, contextInteractRadius);
            if (terminal == null)
                return false;
            GroupOrders.Issue(units, new InteractCommand(terminal), ModifierHeld ? IssueMode.Append : IssueMode.Replace, groupSpacing);
            return true;
        }
```

(f) In `Act`, pass the terminal on: change the default branch to `var command = CommandResolver.Resolve(target.Hostile, target.Point, target.Cover, target.Interactable);`.

(g) Update the class summary: ` A terminal under the pointer is an Interact order (CommandResolver); the Interact action (keyboard) and a controller Confirm with the cursor hidden interact with the terminal in reach, else Confirm attacks as before.`

- [ ] **Step 8: Run to verify pass**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.InteractInputTests"` → EXIT=0, then `Tools/run-tests.sh EditMode` (the whole suite, including `InputAssetTests`, `PromptTests` and the device-name scan) → EXIT=0.
Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.InteractInputPlayModeTests"` → EXIT=0. Regression, one at a time: `"Blackglass.Tests.ControllerCommandInputTests"`, `"Blackglass.Tests.PlayerCommandInputTests"`, `"Blackglass.Tests.TacticalCursorTests"`, `"Blackglass.Tests.PointerTargetResolverTests"`, `"Blackglass.Tests.FamilySwitchClickTests"`, `"Blackglass.Tests.AbilityTargetingPadTests"` → EXIT=0.

- [ ] **Step 9: Commit**

```bash
git add Assets/_Project/Input/BlackglassControls.inputactions Assets/_Project/Scripts Assets/_Project/Tests/EditMode/InteractInputTests.cs* Assets/_Project/Tests/PlayMode/InteractInputPlayModeTests.cs*
git commit -m "Add the Interact action, terminal pointer targets, context Confirm and cursor snapping

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 9: HUD, banners and world markers

**Files:**
- Create: `Assets/_Project/Scripts/Mission/ObjectiveMarker.cs`, `Assets/_Project/Scripts/Mission/MissionHud.cs` (contains `MissionHudText` and `MissionHud`)
- Modify: `Assets/_Project/Scripts/Mission/MissionContent.cs` (visuals), `Assets/_Project/Scripts/DebugUI/PrototypeHud.cs`
- Test: `Assets/_Project/Tests/EditMode/MissionHudTextTests.cs`, `Assets/_Project/Tests/PlayMode/ObjectiveMarkerPlayModeTests.cs`

**Interfaces:**
- Consumes: `MissionRuntime`, `MissionObjective`, `MissionPhase`, `MissionInteractable`, `PlayerCommandInput.NearbyInteractable`, `PromptResolver`, `ActiveInputDevice`, `InputFamily.IsController()`.
- Produces:
  - `MissionHudText` (static, pure): `Line(MissionObjective)`, `Panel(MissionRuntime)`, `PhaseLabel(MissionPhase)`, `Banner(MissionPhase)`, `MarkerLabel(MissionObjective)`.
  - `ObjectiveMarker : MonoBehaviour` with `Bind(Func<Color>)`, and static `TerminalColour(MissionInteractable)`, `ZoneColour(ObjectiveState)`.
  - `MissionHud : MonoBehaviour` (IMGUI; serialized `director`, `commandInput`, `viewCamera`, `inputDevice`, `controls`).
  - `PrototypeHud` serialized `missionDirector`; its kill-all banner is skipped when one is set.

- [ ] **Step 1: Write the failing EditMode tests**

`Assets/_Project/Tests/EditMode/MissionHudTextTests.cs`:

```csharp
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class MissionHudTextTests
    {
        sealed class Fake : MissionObjective
        {
            public Fake(string id, string title, bool required) : base(id, ObjectiveType.Interact, title, required) { }
            public bool CompleteNow;
            public bool FailNow;
            public override bool HasTarget => true;
            public override Vector3 TargetPosition => Vector3.zero;
            public override string Describe() => Title;
            protected override void OnEvaluate()
            {
                if (FailNow)
                    Fail();
                else if (CompleteNow)
                    Complete();
            }
        }

        readonly List<GameObject> hosts = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (var host in hosts)
            {
                if (host != null)
                    Object.DestroyImmediate(host);
            }
            hosts.Clear();
        }

        MissionRuntime Runtime(out Fake kill, out Fake hack, out Fake extraction)
        {
            var unit = new GameObject("Unit");
            hosts.Add(unit);
            var squad = new[] { unit.AddComponent<Health>() };
            kill = new Fake("kill", "Eliminate security team", true);
            hack = new Fake("hack", "Access data terminal", true);
            extraction = new Fake("extract", "Extraction", false);
            var runtime = new MissionRuntime(new MissionObjective[] { kill, hack }, extraction, squad);
            runtime.Start();
            return runtime;
        }

        [Test]
        public void Panel_ShowsEachObjectiveWithItsStatus_AndExtractionLocked()
        {
            var runtime = Runtime(out var kill, out _, out _);
            kill.CompleteNow = true;
            runtime.Tick();

            var text = MissionHudText.Panel(runtime);

            Assert.That(text, Does.StartWith("MISSION"));
            Assert.That(text, Does.Contain("[x] Eliminate security team"));
            Assert.That(text, Does.Contain("[ ] Access data terminal"));
            Assert.That(text, Does.Contain("[LOCKED] Extraction"));
        }

        [Test]
        public void Panel_ShowsExtractionOpenAsAnOrdinaryOpenLine_ThenDoneOnSuccess()
        {
            var runtime = Runtime(out var kill, out var hack, out var extraction);
            kill.CompleteNow = true;
            hack.CompleteNow = true;
            runtime.Tick();
            Assert.That(MissionHudText.Panel(runtime), Does.Contain("[ ] Extraction"));
            Assert.That(MissionHudText.Panel(runtime), Does.Contain(MissionHudText.PhaseLabel(MissionPhase.ExtractionOpen)));

            extraction.CompleteNow = true;
            runtime.Tick();
            Assert.That(MissionHudText.Panel(runtime), Does.Contain("[x] Extraction"));
        }

        [Test]
        public void Panel_MarksAFailedObjective_AndSkipsUnknownOnes()
        {
            var runtime = Runtime(out _, out var hack, out _);
            hack.FailNow = true;
            runtime.Tick();
            Assert.That(MissionHudText.Panel(runtime), Does.Contain("[FAILED] Access data terminal"));

            var second = Runtime(out _, out var secret, out _);
            secret.SetKnown(false);
            Assert.That(MissionHudText.Panel(second), Does.Not.Contain("Access data terminal"));
        }

        [Test]
        public void Banner_NamesTheResult_AndIsEmptyOtherwise()
        {
            Assert.That(MissionHudText.Banner(MissionPhase.Success), Does.Contain("SUCCESS"));
            Assert.That(MissionHudText.Banner(MissionPhase.Failure), Does.Contain("FAILED"));
            Assert.That(MissionHudText.Banner(MissionPhase.Active), Is.Empty);
            Assert.That(MissionHudText.Banner(MissionPhase.ExtractionOpen), Is.Empty);
            Assert.That(MissionHudText.Banner(MissionPhase.Inactive), Is.Empty);
        }

        [Test]
        public void MarkerLabel_ShowsLockedExtraction_AndNothingForACompletedOrUnknownObjective()
        {
            var runtime = Runtime(out var kill, out var hack, out var extraction);
            Assert.That(MissionHudText.MarkerLabel(extraction), Is.EqualTo("Extraction (locked)"));
            Assert.That(MissionHudText.MarkerLabel(hack), Is.EqualTo("Access data terminal"));
            hack.CompleteNow = true;
            runtime.Tick();
            Assert.That(MissionHudText.MarkerLabel(hack), Is.Empty);
            kill.SetKnown(false);
            Assert.That(MissionHudText.MarkerLabel(kill), Is.Empty);
        }

        [Test]
        public void PrototypeHud_KeepsItsKillAllBanner_OnlyWhenNoMissionDirectorIsWired()
        {
            Assert.That(PrototypeHud.ShowsEncounterOutcome(hasMissionDirector: false), Is.True);
            Assert.That(PrototypeHud.ShowsEncounterOutcome(hasMissionDirector: true), Is.False);
        }

        [Test]
        public void MarkerColours_DistinguishEveryTerminalAndZoneState()
        {
            var host = new GameObject("Terminal");
            hosts.Add(host);
            var terminal = host.AddComponent<MissionInteractable>();
            var unit = new GameObject("Unit");
            hosts.Add(unit);
            unit.AddComponent<Health>();
            unit.AddComponent<UnitInteractor>();
            var worker = unit.AddComponent<CommandableUnit>();
            terminal.Initialize(1.8f, 2f);

            var idle = ObjectiveMarker.TerminalColour(terminal);
            terminal.SetAvailable(true);
            var available = ObjectiveMarker.TerminalColour(terminal);
            terminal.TryBegin(worker);
            var working = ObjectiveMarker.TerminalColour(terminal);
            terminal.Advance(worker, 5f);
            var done = ObjectiveMarker.TerminalColour(terminal);

            Assert.That(new[] { idle, available, working, done }, Is.Unique);
            Assert.That(ObjectiveMarker.ZoneColour(ObjectiveState.Inactive), Is.Not.EqualTo(ObjectiveMarker.ZoneColour(ObjectiveState.Active)));
            Assert.That(ObjectiveMarker.ZoneColour(ObjectiveState.Active), Is.Not.EqualTo(ObjectiveMarker.ZoneColour(ObjectiveState.Completed)));
        }
    }
}
```

- [ ] **Step 2: Write the failing PlayMode tests**

`Assets/_Project/Tests/PlayMode/ObjectiveMarkerPlayModeTests.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class ObjectiveMarkerPlayModeTests
    {
        MissionRig rig;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
        }

        static Color ColourOf(Renderer renderer)
        {
            var block = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(block);
            return block.GetColor("_BaseColor");
        }

        [UnityTest]
        public IEnumerator TheExtractionZone_IsVisibleButNotSolid_AndChangesColourWhenItOpens()
        {
            rig = new MissionRig(new MissionSettings { interactionSeconds = 0f });
            yield return rig.Generate(12345);
            var zone = rig.Director.Current.ExtractionZone;
            var renderers = zone.GetComponentsInChildren<Renderer>();

            Assert.That(renderers, Is.Not.Empty, "a disc and a beacon");
            Assert.That(zone.GetComponentsInChildren<Collider>(), Is.Empty, "clicks and rays pass through the zone");
            Assert.That(Physics.Raycast(zone.position + Vector3.up * 5f, Vector3.down, out var hit, 10f), Is.True);
            Assert.That(hit.collider.transform.IsChildOf(zone), Is.False, "the floor is under the zone, not the marker");
            yield return null;
            var locked = ColourOf(renderers[0]);
            Assert.That(locked, Is.EqualTo(ObjectiveMarker.ZoneColour(ObjectiveState.Inactive)));

            foreach (var hostile in rig.Director.Hostiles)
            {
                hostile.GetComponent<EnemyAI>().enabled = false;
                hostile.GetComponent<Health>().TakeDamage(1000);
            }
            var unit = rig.Director.Friendlies[0];
            var terminal = rig.Director.Current.Terminal;
            unit.GetComponent<UnitMover>().TrySnap(terminal.Position, out var stand);
            unit.GetComponent<UnityEngine.AI.NavMeshAgent>().Warp(stand);
            unit.Issue(new InteractCommand(terminal));
            yield return TestWorld.WaitUntil(() => rig.Director.Phase == MissionPhase.ExtractionOpen, 10f);
            yield return null;

            Assert.That(rig.Director.Phase, Is.EqualTo(MissionPhase.ExtractionOpen));
            Assert.That(ColourOf(renderers[0]), Is.EqualTo(ObjectiveMarker.ZoneColour(ObjectiveState.Active)));
        }

        [UnityTest]
        public IEnumerator TheTerminal_ShowsItsState_AndLeavesNoMaterialInstancesBehind()
        {
            rig = new MissionRig(new MissionSettings { interactionSeconds = 5f });
            yield return rig.Generate(12345);
            var terminal = rig.Director.Current.Terminal;
            var renderer = terminal.GetComponent<Renderer>();
            yield return null;

            Assert.That(ColourOf(renderer), Is.EqualTo(ObjectiveMarker.TerminalColour(terminal)));
            var available = ColourOf(renderer);

            var unit = rig.Director.Friendlies[0];
            foreach (var hostile in rig.Director.Hostiles)
                hostile.GetComponent<EnemyAI>().enabled = false;
            unit.GetComponent<UnitMover>().TrySnap(terminal.Position, out var stand);
            unit.GetComponent<UnityEngine.AI.NavMeshAgent>().Warp(stand);
            unit.Issue(new InteractCommand(terminal));
            yield return TestWorld.WaitUntil(() => terminal.Progress > 0.1f, 5f);
            yield return null;

            Assert.That(ColourOf(renderer), Is.Not.EqualTo(available), "working looks different from available");
            Assert.That(renderer.sharedMaterial.name, Does.Not.Contain("(Instance)"), "a property block, not a material instance, tints it");
        }
    }
}
#endif
```

- [ ] **Step 3: Run to verify failure**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.MissionHudTextTests"` → EXIT=1 (compile errors).

- [ ] **Step 4: `ObjectiveMarker`**

`Assets/_Project/Scripts/Mission/ObjectiveMarker.cs`:

```csharp
using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Tints the renderers under it from a colour function each frame, through a MaterialPropertyBlock (no material instance
    /// is created, so nothing leaks when the mission is destroyed). Debug-quality feedback: the terminal shows whether it can
    /// be used, is being worked or is done; the extraction zone shows locked, open or done.
    /// </summary>
    public sealed class ObjectiveMarker : MonoBehaviour
    {
        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        static readonly int LegacyColor = Shader.PropertyToID("_Color");

        Func<Color> colour;
        Renderer[] renderers;
        MaterialPropertyBlock block;

        public static Color TerminalColour(MissionInteractable terminal)
        {
            if (terminal == null)
                return Locked;
            if (terminal.IsCompleted)
                return Done;
            if (!terminal.IsAvailable)
                return Locked;
            return terminal.User != null ? Working : Available;
        }

        public static Color ZoneColour(ObjectiveState state)
        {
            switch (state)
            {
                case ObjectiveState.Active:
                    return new Color(0.2f, 0.9f, 0.3f);
                case ObjectiveState.Completed:
                    return Done;
                case ObjectiveState.Failed:
                    return new Color(0.9f, 0.2f, 0.2f);
                default:
                    return Locked;
            }
        }

        static readonly Color Locked = new Color(0.45f, 0.45f, 0.5f);
        static readonly Color Available = new Color(0.2f, 0.75f, 0.95f);
        static readonly Color Working = new Color(0.95f, 0.8f, 0.2f);
        static readonly Color Done = new Color(0.15f, 0.5f, 0.2f);

        public void Bind(Func<Color> source)
        {
            colour = source;
            Apply();
        }

        void Update() => Apply();

        void Apply()
        {
            if (colour == null)
                return;
            renderers ??= GetComponentsInChildren<Renderer>();
            block ??= new MaterialPropertyBlock();
            var tint = colour();
            foreach (var renderer in renderers)
            {
                if (renderer == null)
                    continue;
                renderer.GetPropertyBlock(block);
                block.SetColor(BaseColor, tint);
                block.SetColor(LegacyColor, tint);
                renderer.SetPropertyBlock(block);
            }
        }
    }
}
```

- [ ] **Step 5: Markers in `MissionContent`**

In `AddTerminal`, before `return interactable;` add:

```csharp
            terminal.AddComponent<ObjectiveMarker>().Bind(() => ObjectiveMarker.TerminalColour(interactable));
```

Replace `CreateZone` with a version that also builds the visuals (a flat disc and a thin beacon, both without colliders, so clicks and sight pass through) and a marker the runtime binds later:

```csharp
        /// <summary>
        /// The extraction zone under the mission root, on the floor at the planned tile: a flat disc the size of the zone and
        /// a thin beacon, neither solid. Its marker shows it locked (grey) until CreateRuntime binds it to the objective.
        /// </summary>
        public static Transform CreateZone(MissionLayout layout, ObjectivePlan plan, Transform root)
        {
            var zone = new GameObject("ExtractionZone");
            zone.transform.SetParent(root, false);
            zone.transform.position = layout.TileCenter(plan.ExtractionTile);
            Visual(zone.transform, "Disc", new Vector3(ZoneRadius * 2f, 0.02f, ZoneRadius * 2f), 0.03f);
            Visual(zone.transform, "Beacon", new Vector3(0.25f, 1.5f, 0.25f), 1.5f);
            zone.AddComponent<ObjectiveMarker>().Bind(() => ObjectiveMarker.ZoneColour(ObjectiveState.Inactive));
            return zone.transform;
        }

        static void Visual(Transform parent, string name, Vector3 scale, float height)
        {
            var part = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            part.name = name;
            Object.DestroyImmediate(part.GetComponent<Collider>());
            part.transform.SetParent(parent, false);
            part.transform.localPosition = new Vector3(0f, height, 0f);
            part.transform.localScale = scale;
            var renderer = part.GetComponent<Renderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }
```

(`Object` here is `UnityEngine.Object`: add `using Object = UnityEngine.Object;` is not needed inside a namespace that has no other `Object`; if the compiler reports ambiguity with `System.Object`, write `UnityEngine.Object.DestroyImmediate`.) A cylinder primitive is 2 m tall and 1 m wide at scale 1, so the disc's local Y position `0.03` with scale Y `0.02` sits just above the floor and the beacon's `1.5` with scale Y `1.5` stands 3 m tall.

In `CreateRuntime`, after `var extraction = ...` and before `return`, bind the zone marker to the objective:

```csharp
            if (mission.ExtractionZone.TryGetComponent<ObjectiveMarker>(out var marker))
                marker.Bind(() => ObjectiveMarker.ZoneColour(extraction.State));
```

- [ ] **Step 6: `MissionHudText`, `MissionHud`, `PrototypeHud`**

`Assets/_Project/Scripts/Mission/MissionHud.cs`:

```csharp
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Blackglass
{
    /// <summary>The text of the mission panel, banner and marker labels. Pure, so it is tested without a scene.</summary>
    public static class MissionHudText
    {
        public static string Line(MissionObjective objective) => $"{Prefix(objective)} {objective.Describe()}";

        static string Prefix(MissionObjective objective)
        {
            switch (objective.State)
            {
                case ObjectiveState.Completed:
                    return "[x]";
                case ObjectiveState.Failed:
                    return "[FAILED]";
                case ObjectiveState.Inactive:
                    return "[LOCKED]";
                default:
                    return "[ ]";
            }
        }

        public static string PhaseLabel(MissionPhase phase)
        {
            switch (phase)
            {
                case MissionPhase.Active:
                    return "in progress";
                case MissionPhase.ExtractionOpen:
                    return "extraction open";
                case MissionPhase.Success:
                    return "success";
                case MissionPhase.Failure:
                    return "failed";
                default:
                    return "-";
            }
        }

        /// <summary>The MISSION panel: the phase, then one line per objective the player knows about.</summary>
        public static string Panel(MissionRuntime runtime)
        {
            var text = new StringBuilder("MISSION - ").Append(PhaseLabel(runtime.Phase));
            foreach (var objective in runtime.Objectives)
            {
                if (objective.IsKnown)
                    text.Append('\n').Append(Line(objective));
            }
            return text.ToString();
        }

        public static string Banner(MissionPhase phase)
        {
            switch (phase)
            {
                case MissionPhase.Success:
                    return "MISSION SUCCESS - squad extracted";
                case MissionPhase.Failure:
                    return "MISSION FAILED";
                default:
                    return string.Empty;
            }
        }

        /// <summary>The label drawn at an objective's world marker; empty when it has none to show.</summary>
        public static string MarkerLabel(MissionObjective objective)
        {
            if (!objective.IsKnown || !objective.HasTarget || objective.State == ObjectiveState.Completed)
                return string.Empty;
            return objective.State == ObjectiveState.Inactive ? $"{objective.Title} (locked)" : objective.Title;
        }
    }

    /// <summary>
    /// Debug-only IMGUI for the mission: the objective panel, the success/failure banner, the context prompt when a terminal
    /// is within reach, and a label over each known objective's marker. Reads only director.Runtime (nothing is kept here).
    /// Not production UI. Works while paused.
    /// </summary>
    public sealed class MissionHud : MonoBehaviour
    {
        [SerializeField] MissionDirector director;
        [SerializeField] PlayerCommandInput commandInput;
        [SerializeField] Camera viewCamera;
        [SerializeField] ActiveInputDevice inputDevice;
        [SerializeField] InputActionAsset controls;

        GUIStyle panelStyle;
        GUIStyle bannerStyle;
        GUIStyle labelStyle;

        void OnGUI()
        {
            if (director == null || director.Runtime == null)
                return;
            var runtime = director.Runtime;
            panelStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 14 };
            bannerStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperCenter, fontSize = 30, fontStyle = FontStyle.Bold };
            labelStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperCenter };

            var panel = MissionHudText.Panel(runtime);
            var lines = panel.Split('\n').Length;
            GUI.Label(new Rect(10f, 190f, 420f, 24f * lines), panel, panelStyle);

            var banner = MissionHudText.Banner(runtime.Phase);
            if (banner.Length > 0)
                GUI.Label(new Rect(0f, 255f, Screen.width, 50f), banner, bannerStyle);

            DrawPrompt(runtime);
            DrawMarkerLabels(runtime);
        }

        // "R: Interact with Terminal" or, for a controller, the Confirm button's label.
        void DrawPrompt(MissionRuntime runtime)
        {
            if (commandInput == null || runtime.IsOver)
                return;
            var terminal = commandInput.NearbyInteractable;
            if (terminal == null)
                return;
            var family = inputDevice != null ? inputDevice.Family : InputFamily.KeyboardMouse;
            var path = family.IsController() ? "Commands/Confirm" : "Commands/Interact";
            var key = controls != null ? PromptResolver.GetPrompt(controls.FindAction(path), family) : "-";
            GUI.Label(new Rect(0f, Screen.height - 80f, Screen.width, 30f), $"{key}: Interact with {terminal.DisplayName}", labelStyle);
        }

        void DrawMarkerLabels(MissionRuntime runtime)
        {
            if (viewCamera == null)
                return;
            foreach (var objective in runtime.Objectives)
            {
                var label = MissionHudText.MarkerLabel(objective);
                if (label.Length == 0)
                    continue;
                var screen = viewCamera.WorldToScreenPoint(objective.TargetPosition + Vector3.up * 2f);
                if (screen.z <= 0f)
                    continue;
                GUI.Label(new Rect(screen.x - 110f, Screen.height - screen.y - 22f, 220f, 24f), label, labelStyle);
            }
        }
    }
}
```


`PrototypeHud.cs`: add `[SerializeField] MissionDirector missionDirector;` next to the other serialized fields, the static

```csharp
        /// <summary>The kill-all banner belongs to scenes without a mission director; generated missions show the mission result.</summary>
        internal static bool ShowsEncounterOutcome(bool hasMissionDirector) => !hasMissionDirector;
```

and change the outcome block of `OnGUI` from `if (encounter != null)` to `if (encounter != null && ShowsEncounterOutcome(missionDirector != null))`. (`missionDirector != null` uses Unity's null check; the `Prototype` scene leaves the field empty and is unchanged.)

- [ ] **Step 7: Run to verify pass**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.MissionHudTextTests"` → EXIT=0. Then `Tools/run-tests.sh PlayMode "Blackglass.Tests.ObjectiveMarkerPlayModeTests"` → EXIT=0. Regression (one at a time): `Tools/run-tests.sh EditMode` (whole suite) → EXIT=0; PlayMode `"Blackglass.Tests.MissionContentPlayModeTests"`, `"Blackglass.Tests.MissionRuntimePlayModeTests"` → EXIT=0. If the first marker test fails at `ColourOf(renderers[0])` because `GetPropertyBlock` returns a colour on `_BaseColor` only after the first `Update`, keep the extra `yield return null` before reading; do not weaken the assertion.

- [ ] **Step 8: Commit**

```bash
git add Assets/_Project/Scripts/Mission Assets/_Project/Scripts/DebugUI/PrototypeHud.cs Assets/_Project/Tests/EditMode/MissionHudTextTests.cs* Assets/_Project/Tests/PlayMode/ObjectiveMarkerPlayModeTests.cs*
git commit -m "Add the mission HUD, banners and world markers for the terminal and extraction zone

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 10: Wire the ProceduralMission scene and play the whole flow in it

**Files:**
- Create (temporary, deleted in Step 5, never committed): `Assets/_Project/Editor/MissionObjectivesSceneBuilder.cs` (+ its `.meta`, and the `Editor` folder's `.meta` if the folder did not exist)
- Modify: `Assets/_Project/Scenes/ProceduralMission.unity` (by the builder)
- Test: `Assets/_Project/Tests/PlayMode/MissionObjectivesSceneTests.cs`; and the minimal fix of `ProceduralMissionSceneTests.cs` if it still asserts a hostile count of 3 (Task 6 should already have fixed it)

**Interfaces:**
- Consumes: everything above; the scene's existing objects (`Systems` with `PlayerCommandInput`, `TacticalCursor`, `PrototypeHud`, `ActiveInputDevice`; the camera rig; the `Mission` object with `MissionDirector`).
- Produces: a scene in which `InteractableRegistry` exists on `Systems` and is wired to the director (`systems.interactables`), `PlayerCommandInput` (`interactables`, `interactAction`) and `TacticalCursor` (`interactables`); a `MissionHud` on the `Mission` object wired to the director, the input, the camera, `ActiveInputDevice` and the controls asset; `PrototypeHud.missionDirector` set. The `Prototype` scene is not touched.

- [ ] **Step 1: Write the failing scene tests**

`Assets/_Project/Tests/PlayMode/MissionObjectivesSceneTests.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.XInput;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>
    /// The objective flow in the real ProceduralMission scene: the scene generates its own mission at start, and these tests
    /// regenerate seed 12345 with a short interaction and play it through the real input components. A test that needs a
    /// unit somewhere warps it there; hostiles that must not act have their AI switched off. Only an Xbox-style simulated pad
    /// is used (see ProceduralMissionSceneTests).
    /// </summary>
    public class MissionObjectivesSceneTests : InputTestFixture
    {
        const int DeterministicSeed = 12345;

        Keyboard keyboard;
        Mouse mouse;
        InputActionAsset actions;
        MissionDirector director;
        CommandableUnit[] squad;
        TacticalPause pause;
        UnitSelection selection;
        ActiveCharacter active;
        TacticalCursor cursor;
        PlayerCommandInput commandInput;
        InteractableRegistry registry;
        TacticalCameraController cameraRig;
        Camera viewCamera;

        public override void Setup()
        {
            base.Setup();
            keyboard = InputSystem.AddDevice<Keyboard>();
            mouse = InputSystem.AddDevice<Mouse>();
            actions = TestControls.Load();
        }

        public override void TearDown()
        {
            Time.timeScale = 1f;
            if (SceneManager.GetActiveScene().name == "ProceduralMission")
                PrototypeSceneTests.DestroySceneObjects();
            TestControls.Reset(actions);
            base.TearDown();
        }

        IEnumerator LoadMission(float interactionSeconds = 0.4f)
        {
            var loading = SceneManager.LoadSceneAsync("ProceduralMission", LoadSceneMode.Single);
            Assert.That(loading, Is.Not.Null, "The ProceduralMission scene is not in the build settings");
            yield return loading;
            director = Object.FindFirstObjectByType<MissionDirector>();
            Assert.That(director, Is.Not.Null);
            yield return TestWorld.WaitUntil(() => director.State == MissionState.Ready || director.State == MissionState.Failed, 20f);
            director.Settings.interactionSeconds = interactionSeconds;
            Assert.That(director.Generate(DeterministicSeed), Is.True);
            yield return TestWorld.WaitUntil(() => director.State == MissionState.Ready || director.State == MissionState.Failed, 20f);
            Assert.That(director.State, Is.EqualTo(MissionState.Ready), director.Report.Failure);
            Bind();
            yield return null;
        }

        void Bind()
        {
            squad = director.Friendlies.ToArray();
            pause = Object.FindFirstObjectByType<TacticalPause>();
            selection = Object.FindFirstObjectByType<UnitSelection>();
            active = Object.FindFirstObjectByType<ActiveCharacter>();
            cursor = Object.FindFirstObjectByType<TacticalCursor>();
            commandInput = Object.FindFirstObjectByType<PlayerCommandInput>();
            registry = Object.FindFirstObjectByType<InteractableRegistry>();
            cameraRig = Object.FindFirstObjectByType<TacticalCameraController>();
            viewCamera = Camera.main;
        }

        MissionRuntime Runtime => director.Runtime;
        MissionInteractable Terminal => director.Current.Terminal;
        Vector3 Zone => director.Current.ExtractionZone.position;

        static void Kill(Component unit)
        {
            var health = unit.GetComponent<Health>();
            health.TakeDamage(health.Max);
        }

        void KillAllHostiles()
        {
            foreach (var hostile in director.Hostiles)
            {
                hostile.GetComponent<EnemyAI>().enabled = false;
                Kill(hostile);
            }
        }

        void DisarmHostiles()
        {
            foreach (var hostile in director.Hostiles)
                hostile.GetComponent<EnemyAI>().enabled = false;
        }

        // Stands the unit on the walkable point next to the terminal (inside the interaction range).
        void WarpNextToTerminal(CommandableUnit unit)
        {
            Assert.That(unit.GetComponent<UnitMover>().TrySnap(Terminal.Position, out var stand), Is.True);
            Assert.That(unit.GetComponent<NavMeshAgent>().Warp(stand), Is.True);
        }

        IEnumerator Tap(ButtonControl button)
        {
            Press(button);
            yield return null;
            Release(button);
            yield return null;
        }

        IEnumerator CursorOn(Vector3 worldPoint)
        {
            cursor.SetScreenPosition(viewCamera.WorldToScreenPoint(worldPoint));
            yield return null;
        }

        IEnumerator WakeKeyboard()
        {
            // Any key switches the input family; the input that wakes a family only wakes it.
            yield return Tap(keyboard.leftAltKey);
        }

        [UnityTest]
        public IEnumerator Scene_WiresTheObjectiveSystems()
        {
            yield return LoadMission();

            Assert.That(registry, Is.Not.Null);
            Assert.That(registry.Items, Is.EqualTo(new[] { Terminal }));
            Assert.That(Object.FindFirstObjectByType<MissionHud>(), Is.Not.Null);
            Assert.That(director.Phase, Is.EqualTo(MissionPhase.Active));
            Assert.That(director.Hostiles.Count, Is.EqualTo(director.Current.Layout.HostileSpawns.Count + director.Current.Plan.GuardTiles.Count));
            Assert.That(squad.All(u => u.TryGetComponent<UnitInteractor>(out _)), Is.True);
        }

        [UnityTest]
        public IEnumerator Scene_KeyboardMouse_ClickTheTerminal_TheSquadMemberWalksThereAndHacksIt_ThenExtractionOpensAndCompletes()
        {
            yield return LoadMission();
            KillAllHostiles();
            yield return WakeKeyboard();
            cameraRig.FocusOn(Terminal.Position);
            yield return null;
            var screen = viewCamera.WorldToScreenPoint(Terminal.Position);
            var seen = PointerTargetResolver.Resolve(viewCamera, screen, 500f, ~0, null, 0f);
            Assert.That(seen.Kind, Is.EqualTo(PointerTargetKind.Interactable), "the camera sees the terminal under its own screen point");

            Set(mouse.position, (Vector2)screen);
            yield return null;
            Press(mouse.leftButton);
            yield return null;
            Release(mouse.leftButton);
            yield return null;

            Assert.That(squad[0].CurrentCommand, Is.TypeOf<InteractCommand>(), "no selection: the click orders the character being played");
            yield return TestWorld.WaitUntil(() => Terminal.IsCompleted, 90f);
            Assert.That(Terminal.IsCompleted, Is.True, "the unit walked to the terminal and hacked it");
            yield return null;
            Assert.That(director.Phase, Is.EqualTo(MissionPhase.ExtractionOpen), "the kills and the hack are both done");

            Assert.That(squad[0].Issue(new MoveCommand(Zone)), Is.True);
            yield return TestWorld.WaitUntil(() => director.Phase == MissionPhase.Success, 90f);
            Assert.That(director.Phase, Is.EqualTo(MissionPhase.Success));
            Assert.That(MissionHudText.Banner(director.Phase), Does.Contain("SUCCESS"));
        }

        [UnityTest]
        public IEnumerator Scene_KeyboardInteract_OrdersTheNearbyTerminal_AndThePromptKnowsIt()
        {
            yield return LoadMission();
            DisarmHostiles();
            WarpNextToTerminal(squad[0]);
            yield return WakeKeyboard();
            yield return null;
            Assert.That(commandInput.NearbyInteractable, Is.SameAs(Terminal), "the HUD prompt condition");

            yield return Tap(keyboard.rKey);

            Assert.That(squad[0].CurrentCommand, Is.TypeOf<InteractCommand>());
            yield return TestWorld.WaitUntil(() => Terminal.IsCompleted, 10f);
            Assert.That(Terminal.IsCompleted, Is.True);
            Assert.That(commandInput.NearbyInteractable, Is.Null, "a used-up terminal is no longer offered");
        }

        [UnityTest]
        public IEnumerator Scene_Pad_PlanInteractWhilePaused_NothingProgresses_ThenResumeCompletesIt()
        {
            yield return LoadMission();
            DisarmHostiles();
            var pad = InputSystem.AddDevice<XInputController>();
            yield return Tap(pad.selectButton);   // View is unbound: it only wakes the pad
            yield return Tap(pad.startButton);
            Assert.That(pause.IsPaused, Is.True);
            WarpNextToTerminal(squad[0]);

            cameraRig.FocusOn(squad[0].transform.position);
            yield return null;
            yield return CursorOn(squad[0].transform.position);
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Friendly));
            yield return Tap(pad.buttonSouth);

            cameraRig.FocusOn(Terminal.Position);
            yield return null;
            yield return CursorOn(Terminal.Position);
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Interactable), "the tactical cursor identifies the terminal");
            yield return Tap(pad.buttonSouth);

            Assert.That(squad[0].CurrentCommand, Is.TypeOf<InteractCommand>());
            yield return new WaitForSecondsRealtime(0.4f);
            Assert.That(Terminal.Progress, Is.Zero, "nothing runs while paused");

            yield return Tap(pad.startButton);
            Assert.That(pause.IsPaused, Is.False);
            yield return TestWorld.WaitUntil(() => Terminal.IsCompleted, 10f);
            Assert.That(Terminal.IsCompleted, Is.True);
        }

        [UnityTest]
        public IEnumerator Scene_PadConfirm_WithTheCursorHidden_InteractsWithTheTerminalInReach()
        {
            yield return LoadMission();
            DisarmHostiles();
            var pad = InputSystem.AddDevice<XInputController>();
            yield return Tap(pad.selectButton);
            Assert.That(pause.IsPaused, Is.False);
            Assert.That(cursor.IsActive, Is.False, "running: the camera owns the right stick");
            WarpNextToTerminal(squad[0]);
            yield return null;

            yield return Tap(pad.buttonSouth);

            Assert.That(squad[0].CurrentCommand, Is.TypeOf<InteractCommand>());
            yield return TestWorld.WaitUntil(() => Terminal.IsCompleted, 10f);
            Assert.That(Terminal.IsCompleted, Is.True);
        }

        [UnityTest]
        public IEnumerator Scene_ExtractionStaysLocked_ForAUnitAlreadyInTheZone_UntilTheObjectivesAreDone_ThenCompletesAtOnce()
        {
            yield return LoadMission();
            DisarmHostiles();
            squad[1].GetComponent<CompanionAI>().enabled = false;   // it must stay where it is put, not follow the leader
            Assert.That(squad[1].GetComponent<NavMeshAgent>().Warp(Zone), Is.True);
            yield return new WaitForSeconds(0.3f);
            Assert.That(director.Phase, Is.EqualTo(MissionPhase.Active));
            Assert.That(Runtime.Extraction.State, Is.EqualTo(ObjectiveState.Inactive));

            KillAllHostiles();
            WarpNextToTerminal(squad[0]);
            squad[0].Issue(new InteractCommand(Terminal));
            yield return TestWorld.WaitUntil(() => director.Phase == MissionPhase.Success, 10f);

            Assert.That(director.Phase, Is.EqualTo(MissionPhase.Success), "the unit that waited in the zone extracts the moment it opens");
        }

        [UnityTest]
        public IEnumerator Scene_TheWholeSquadDead_IsFailure_AndTheBannerSaysSo()
        {
            yield return LoadMission();
            foreach (var unit in squad)
                Kill(unit);
            yield return null;
            yield return null;

            Assert.That(director.Phase, Is.EqualTo(MissionPhase.Failure));
            Assert.That(MissionHudText.Banner(director.Phase), Does.Contain("FAILED"));
            Assert.That(Terminal.IsAvailable, Is.False);
        }

        [UnityTest]
        public IEnumerator Scene_F6_DuringAnInteraction_AndWhilePaused_ResetsTheMissionCompletely()
        {
            yield return LoadMission(interactionSeconds: 5f);
            DisarmHostiles();
            WarpNextToTerminal(squad[0]);
            squad[0].Issue(new InteractCommand(Terminal));
            yield return TestWorld.WaitUntil(() => Terminal.Progress > 0.1f, 5f);
            var oldRuntime = Runtime;
            var oldTerminal = Terminal;

            Press(keyboard.f6Key);
            yield return null;
            Release(keyboard.f6Key);
            yield return TestWorld.WaitUntil(() => director.State == MissionState.Ready && director.Runtime != oldRuntime, 20f);
            yield return null;

            Assert.That(director.Runtime, Is.Not.SameAs(oldRuntime));
            Assert.That(oldTerminal == null, Is.True);
            Assert.That(Terminal.Progress, Is.Zero);
            Assert.That(Terminal.User == null, Is.True);
            Assert.That(registry.Items, Is.EqualTo(new[] { Terminal }));
            Assert.That(Object.FindObjectsByType<Transform>(FindObjectsSortMode.None).Count(t => t.name == "ExtractionZone"), Is.EqualTo(1));

            Bind();
            pause.Pause();
            var pausedRuntime = Runtime;
            Assert.That(director.RegenerateSame(), Is.True);
            yield return TestWorld.WaitUntil(() => director.State == MissionState.Ready && director.Runtime != pausedRuntime, 20f);
            yield return null;
            Assert.That(pause.IsPaused, Is.False);
            Assert.That(director.Phase, Is.EqualTo(MissionPhase.Active));
        }
    }
}
#endif
```

- [ ] **Step 2: Run to verify failure**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.MissionObjectivesSceneTests"` → EXIT=2 (the scene has no `InteractableRegistry`, so `registry` is null and the wiring assertions fail; `MissionHud` is missing).

- [ ] **Step 3: Write the temporary scene builder**

`Assets/_Project/Editor/MissionObjectivesSceneBuilder.cs` (create the `Editor` folder if needed; **delete the file, its `.meta` and, if you created it, the folder's `.meta` in Step 5**):

```csharp
using System;
using System.Linq;
using Blackglass;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;

public static class MissionObjectivesSceneBuilder
{
    const string ScenePath = "Assets/_Project/Scenes/ProceduralMission.unity";
    const string ControlsPath = "Assets/_Project/Input/BlackglassControls.inputactions";

    static InputActionReference Reference(string actionPath)
    {
        var reference = AssetDatabase.LoadAllAssetsAtPath(ControlsPath).OfType<InputActionReference>()
            .FirstOrDefault(r => r.action != null && r.action.actionMap.name + "/" + r.action.name == actionPath);
        if (reference == null)
            throw new Exception($"No InputActionReference for {actionPath}");
        return reference;
    }

    static T Find<T>() where T : Component
    {
        var found = UnityEngine.Object.FindFirstObjectByType<T>();
        if (found == null)
            throw new Exception($"{typeof(T).Name} not found in the scene");
        return found;
    }

    static T GetOrAdd<T>(GameObject host) where T : Component
    {
        var existing = host.GetComponent<T>();
        return existing != null ? existing : host.AddComponent<T>();
    }

    static void Set(Component component, string field, UnityEngine.Object value)
    {
        var so = new SerializedObject(component);
        var property = so.FindProperty(field);
        if (property == null)
            throw new Exception($"{component.GetType().Name} has no serialized field '{field}'");
        property.objectReferenceValue = value;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    public static void Build()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var systems = GameObject.Find("Systems");
        if (systems == null)
            throw new Exception("Systems not found in ProceduralMission");
        var director = Find<MissionDirector>();
        var input = Find<PlayerCommandInput>();
        var registry = GetOrAdd<InteractableRegistry>(systems);

        Set(director, "systems.interactables", registry);
        Set(input, "interactables", registry);
        Set(input, "interactAction", Reference("Commands/Interact"));
        Set(Find<TacticalCursor>(), "interactables", registry);

        var viewCamera = Find<TacticalCameraController>().GetComponentInChildren<Camera>();
        if (viewCamera == null)
            throw new Exception("No camera under the camera rig");
        var hud = GetOrAdd<MissionHud>(director.gameObject);
        Set(hud, "director", director);
        Set(hud, "commandInput", input);
        Set(hud, "viewCamera", viewCamera);
        Set(hud, "inputDevice", Find<ActiveInputDevice>());
        Set(hud, "controls", AssetDatabase.LoadAssetAtPath<InputActionAsset>(ControlsPath));
        Set(Find<PrototypeHud>(), "missionDirector", director);

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        Debug.Log("MissionObjectivesSceneBuilder: ProceduralMission wired.");
    }
}
```

If a field name differs from what the builder expects (`systems` on the director, `interactables` on `PlayerCommandInput` and `TacticalCursor`, `missionDirector` on `PrototypeHud` are the ones this plan created), the builder throws with the field name: fix the builder, not the components.

- [ ] **Step 4: Run the builder**

Close the Unity Editor, then run (Git Bash):

```bash
cd /f/Programs/ProjectBlackglass
"/c/Program Files/Unity/Hub/Editor/6000.3.25f1/Editor/Unity.exe" -batchmode -quit -projectPath "$(pwd -W)" -executeMethod MissionObjectivesSceneBuilder.Build -logFile Logs/MissionObjectivesSceneBuilder.log
echo EXIT=$?
grep -n "MissionObjectivesSceneBuilder\|Exception\|error" Logs/MissionObjectivesSceneBuilder.log | head -20
```

Expected: EXIT=0 and the log line `MissionObjectivesSceneBuilder: ProceduralMission wired.` `git status` shows `ProceduralMission.unity` modified (and nothing under `Scenes/Prototype*`).

- [ ] **Step 5: Delete the builder, run the scene tests**

```bash
rm -f Assets/_Project/Editor/MissionObjectivesSceneBuilder.cs Assets/_Project/Editor/MissionObjectivesSceneBuilder.cs.meta
rmdir Assets/_Project/Editor 2>/dev/null && rm -f Assets/_Project/Editor.meta
```

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.MissionObjectivesSceneTests"` → EXIT=0. If a click or cursor step fails because a wall hides the terminal, the test's precondition message says so: choose a seed-12345 position that works by moving the camera (`FocusOn`) before changing production code; the layout for seed 12345 is deterministic. Then, one at a time, `Tools/run-tests.sh PlayMode "Blackglass.Tests.ProceduralMissionSceneTests"` and `"Blackglass.Tests.PrototypeSceneTests"` and `"Blackglass.Tests.PrototypeSceneControllerTests"` → EXIT=0 (the Prototype scene is unchanged).

- [ ] **Step 6: Commit**

```bash
git add Assets/_Project/Scenes/ProceduralMission.unity Assets/_Project/Tests/PlayMode/MissionObjectivesSceneTests.cs* Assets/_Project/Tests/PlayMode/ProceduralMissionSceneTests.cs
git status --short   # nothing under Assets/_Project/Editor may be staged or left behind
git commit -m "Wire the objective systems into the ProceduralMission scene and test the whole flow there

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 11: Decision record, documentation and final verification

**Files:**
- Modify: `Docs/Decisions.md` (new record 033; `Update (033)` notes in 026), `Docs/superpowers/specs/2026-10-06-mission-objectives-design.md` (status line)
- No new code.

- [ ] **Step 1: Write decision record 033**

Append to `Docs/Decisions.md`, in the file's existing style (`## 033 — Mission objectives, interaction and extraction`, then `- **Decided (Phase 9, 2026-10-06):**` with sub-bullets, `- **Why:**`, `- **Rejected:**`, `- **Implications:**`, `- **Known limitations:**`). It must state, each in one or two sentences, with the measured numbers filled in from the runs above:

- **Lifecycle:** `MissionState` (generation) is unchanged; `MissionPhase` (`Inactive`, `Active`, `ExtractionOpen`, `Success`, `Failure`) is play; why `ObjectivesComplete` and `ExtractionAvailable` are one phase; terminal phases; failure judged first.
- **Objectives:** `MissionObjective` (type, state, `IsRequired`, `IsKnown`, target, `Describe`), pull-based `Evaluate`, `EliminateHostilesObjective` (its own group, not `Encounter`), `InteractObjective`, `ReachZoneObjective`; extraction = a non-required reach-zone opened by the runtime; `IsKnown` separates existence from visibility (default true, nothing sets it false yet); prerequisites, optional, hidden, discovered and alternate objectives fit as flags and an `Inactive` start.
- **Victory:** `Encounter.Outcome` (and the `Prototype` scene's kill-all banner) is unchanged; no generated-mission behaviour reads `Victory`; `PrototypeHud` skips its banner when a director is wired.
- **Placement:** pure `ObjectivePlacer` over the layout with `SeededRandom.ForObjectives`; the rules and constants (`FreeRadius` 2, spawn spacing, `ExtractionClearance` 4 m, guards 2 to 6 tiles from the terminal, `guardCount` 2); runs before the build so the terminal is part of the NavMesh bake (deviation from the owner's listed pipeline order, and why); `ValidateObjectives` after the bake; a placement failure fails the attempt; layout hashes and golden layouts unchanged; plan hash pinned for seeds 12345, 1 and 2. Record the measured attempt-1 success (`attempt 1: N/200, mean M, max K` from `ObjectivePlacerTests`) next to the 76% (record 031) it replaces, and the number of seeds that needed more than one attempt.
- **Interaction:** `InteractCommand` (plain data), `MissionInteractable` (claim, progress, availability), `UnitInteractor` (rules), order life in `CommandableUnit` (walk, work, per-frame validation, cancel releases and loses progress, queued orders get static checks only); progress uses scaled time so pause freezes it; one user at a time; the same path serves direct control, tactical planning and queues.
- **Input:** `Commands/Interact` (keyboard R); a click or cursor Confirm on a terminal goes through `PlayerCommandInput.Act`; controller: context Confirm (cursor hidden and a terminal within 3 m of the active character interacts, otherwise Confirm attacks as before) and no new pad button; cursor snaps to a terminal within 1.5 m after friendlies and hostiles; why (owner decision during brainstorming) and the rejected alternatives (R3, the select button).
- **Extraction and failure rule:** at least `extractionUnits` (default 1) living squad members inside the zone (radius 2 m); no abandoned-unit consequences; failure = squad dead or a required objective failed.
- **Regeneration:** the runtime is detached and dropped first in every teardown, the registry is emptied in `ResetSystems`, terminal, zone and markers live under the mission root; the tests that pin it.
- **Rejected:** MonoBehaviour objectives; ScriptableObject objective definitions; placement after the bake with a runtime carve or rebake; objectives inside `MissionGenerator`/the layout hash; keeping partial interaction progress; a dedicated pad Interact button; a tick glyph in IMGUI.
- **Implications:** a new objective type is a `MissionObjective` subclass plus a line in `MissionContent.CreateRuntime`; a new interactable is a `MissionInteractable` plus an objective; placement stays room-grid specific.
- **Known limitations:** the ones in the spec section 14, plus anything found while testing (for example enemies not reacting to a unit at the terminal).

Add a short `Update (033)` sentence to record 026's "Spawn rules" bullet (generated missions also get `guardCount` extra hostiles in the terminal room, so `Hostiles` is `HostileSpawns` plus guards) and to its "Implications" (a mission feature may also be an objective: see 033).

- [ ] **Step 2: Mark the spec implemented**

In the first lines of `Docs/superpowers/specs/2026-10-06-mission-objectives-design.md` change `Status: approved in brainstorming (parts 1 and 2), spec awaiting owner review.` to `Status: implemented on branch mission-objectives (see Docs/Decisions.md record 033; where the build differs from this text, the record is authoritative).`

- [ ] **Step 3: Full verification**

Run, one at a time, with no other Unity process or implementer active:

```bash
Tools/run-tests.sh EditMode
Tools/run-tests.sh PlayMode
```

Both must exit 0. Record the totals (passed/total) for the completion report; compare with the baselines measured before Task 1 (the controller measures them). If the PlayMode run is killed for low memory, run it in class groups (`"Blackglass.Tests.Mission"`, `"Blackglass.Tests.CommandableUnit"`, `"Blackglass.Tests.Prototype"`, `"Blackglass.Tests.Controller"`, and so on) and say so. The known intermittent `CoverOrderPlayModeTests.CoverOrderWhilePaused_ReservesAtOnce_...` failure (a frame-timing flake, seen before this phase) is rerun once and reported, not hidden.

Console check: `grep -c "Exception\|error CS" Logs/TestRun-PlayMode.log` should print 0 apart from the expected, `LogAssert`-handled generation-failure message of the failed-generation tests. Any other error or exception line is a finding.

- [ ] **Step 4: Commit**

```bash
git add Docs/Decisions.md Docs/superpowers/specs/2026-10-06-mission-objectives-design.md
git commit -m "Record decision 033: mission objectives, interaction and extraction

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

- [ ] **Step 5: Report**

The controller (not the implementer) then runs a whole-branch review, reports to the owner with the completion-report items from the owner's Phase 9 brief (files created and changed, lifecycle, objective architecture and types, placement and determinism, interaction and the tactical command, controller integration, multiple objectives, extraction, victory and failure, regeneration cleanup, how existence is separated from visibility, the manual test plan, known limitations, concerns before Phase 10), updates the project memory, and offers `superpowers:finishing-a-development-branch`.

---

## Self-Review (done while writing)

Spec coverage: lifecycle and phases (Task 1, 7); objective abstraction, `IsKnown`, three types (1, 2, 3); placement, determinism, reachability, pre-bake deviation (5, 6); interaction capability, command, claim, duration, pause, cancel, validation (3, 4); direct control, tactical queue and paused planning, controller context Confirm and cursor snap, keyboard (4, 8, 10); multiple required objectives and toggles (7); extraction unlock, rule and zone (1, 2, 6, 7); victory no longer `AllEnemiesDead`, failure (7); HUD, banners, markers (9); regeneration (6, 7, 10); persistence boundary (plain fields, no UI state: 1, 9); decision record and spec status (11). Validation items 1–35 map as in spec section 13: 1–5, 8–9, 21–22 → Tasks 5–6; 6–7, 17–20, 23–24 → Tasks 2, 7, 10; 10–16 → Tasks 4, 8, 10; 25–27 → Tasks 6, 7, 10; 28–35 → existing suites run in each task plus Task 11.

Placeholder scan: the only deliberately open values are the three golden hashes in Task 5 (the step that measures and pins them is explicit) and the numbers record 033 copies from test output.

Type consistency: `MissionInteractable.Initialize(range, duration, label)` (Task 3) is what Tasks 4, 6, 8 call; `UnitInteractor.Check/InRange/TryStart/Advance/Release/Record` (Task 3) are what `CommandableUnit` uses (Task 4); `ObjectivePlan.GuardTiles/TerminalTile/ExtractionTile` (Task 5) are what Tasks 6 and 7 read; `GeneratedMission.Plan/Terminal/ExtractionZone` (Task 6) are what `MissionContent.CreateRuntime` and the tests read (7); `MissionDirector.Runtime/Phase` (7) are what the HUD and scene tests read (9, 10); `PlayerCommandInput.NearbyInteractable/WireInteraction` and `TacticalCursor.SetInteractables` (8) are what the builder and HUD use (9, 10).
