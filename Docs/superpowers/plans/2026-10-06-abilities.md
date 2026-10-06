# Phase 7 Combat Archetypes and Abilities Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Three weapon archetypes (melee, ranged, long-range marksman) and a small reusable ability layer (Aimed Shot, Blast, Mend) with per-unit cooldowns, single-target and ground-targeted targeting, tactical-pause planning through the existing command queue, controller-native selection (hold R2 + D-pad) and targeting through the existing tactical cursor, one validation pipeline, and debug previews.

**Architecture:** `AbilityDefinition` (immutable ScriptableObject) + `UnitAbilities` (per-unit slots and ready-times on scaled time) + pure `AbilityRules` validation. An ability is an `AbilityCommand` in the existing `CommandQueue`; `CommandableUnit` executes it on the first running frame and re-validates then. `AbilityTargeting` holds the armed slot and the live preview, reusing `PointerTarget` and `TacticalCursor`; `PlayerCommandInput.Act` diverts clicks and cursor Confirm to it while armed. The basic Attack path is untouched.

**Tech Stack:** Unity 6.3 LTS (6000.3.25f1), C#, `com.unity.inputsystem` 1.20.0 (no new packages), NUnit + Unity Test Framework + `InputTestFixture`.

**Spec:** `Docs/superpowers/specs/2026-10-06-abilities-design.md` (approved 2026-10-06; §7 was corrected while planning, see Deviations). Read it before starting.

## Global Constraints

- No new packages; Input System stays at **1.20.0**.
- Existing keyboard/mouse bindings and behaviour stay identical. Every existing test must pass unchanged **except** the three deliberate edits listed under Deviations (items 4 and 5).
- `Gamepad.current`, `Keyboard.current`, `Mouse.current`, face-button names (`buttonSouth`…) and device types appear **only** under `Assets/_Project/Scripts/Input/` (enforced by `NoDeviceTypesInGameplayTests`). Ability code reads semantic `InputActionReference`s and never names a button, a trigger or a device.
- `TacticalPause` stays the only writer of `Time.timeScale`. Simulation code (cooldowns, ability execution) uses scaled time (`Time.time`, `SimulationTime.IsRunning`); input, targeting and debug views use unscaled time or input events so they work while paused.
- No singletons, no static mutable state, no `FindObjectOfType` at runtime; components are wired through serialized fields and `internal Initialize(...)` (decisions 009/010). Every new optional parameter of an existing `Initialize` is added **at the end with a default** so existing tests compile unchanged.
- Never use `??`, `?.` or `is null` on `UnityEngine.Object` references (fake-null): use `== null` / `!= null` / `TryGetComponent`.
- Mutable state (cooldowns, last failure) lives on the unit (`UnitAbilities`), **never** in a ScriptableObject asset.
- Offensive area damage affects **only units hostile to the caster**; no ability can hurt the caster's own side (the owner does not want friendly fire; decision 025).
- New or changed files under `Scripts/` follow the surrounding style: namespace `Blackglass`, 4-space indent, `///` summaries, no commented-out code, no `Debug.Log` for expected failures (a failed ability is reported through `UnitAbilities.LastFailure` and events, never the Console).
- Commits end with `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`. Commit after each task. Unity creates `.meta` files on the next import: **`git add` directories/paths, not individual files, so `.meta` files go in too.**
- No physical controller is available to Claude. Never write "tested on hardware"; every pad test uses simulated devices. The owner has a PS5 DualSense for the manual check.
- Unity batch runs need the Editor closed. Run tests with `Tools/run-tests.sh EditMode|PlayMode ["<filter>"]` from the repo root (EXIT=0 passed, 1 compile error, 2 test failures).
- **Baseline measured 2026-10-06 on `main` (e463a25 + the spec commit):** EditMode **439** passed, PlayMode **405** passed, 0 failed. Re-measure in Task 0 before trusting any total below.
- Scene `Prototype.unity` and the data assets are edited **only** through a temporary editor builder that is created, run and deleted inside Task 10 and never committed (the pattern of earlier phases).

## Deviations from the spec (decided while planning; the owner approved the spec, not these refinements)

1. **Shortcut consumption is off by default in Input System 1.20** (the package source marks it an opt-in feature), so the spec's reliance on it was wrong. The ability chord (`ButtonWithOneModifier`: trigger + D-pad direction) is kept, and a small **`AbilityMenuGate`** component disables the plain D-pad actions (Stop, ToggleFollow, NextTarget, PreviousTarget; a serialized list) while the new semantic **`AbilityMenu`** action (bound to the same trigger) is held. Nothing project-wide is switched on. The spec §7 text has been corrected.
2. **Caster resolution:** the caster is the first *eligible* selected unit; with none selected it is the active character, **also while paused** (the spec said "selected unit or, when paused, selected only"). A pause with nothing selected would otherwise make every ability unusable although the controlled character is highlighted.
3. **`Vector3?` for "no point":** `UnitAbilities.Check` takes `Vector3? point` (null for unit-targeted abilities), instead of a sentinel, so `NoPosition` can be reported honestly.
4. **`FriendlyUnit_2` becomes the Marksman** (range 16, damage 40, interval 2.5) so the long-range archetype is visible in the arena; `FriendlyUnit_1` keeps melee (2/25/1) and `FriendlyUnit_3` keeps ranged (8/15/1) through archetype assets that equal their current values. Hostiles keep their inline values. `PrototypeSceneTests` pins the friendlies' stats (lines ~157-160); Task 10 updates that one expectation for `FriendlyUnit_2`. If another existing scene test fails because `FriendlyUnit_2` is now ranged, change only an assertion that is about its role/stats and report it; never loosen a behaviour assertion.
5. **The unit label** in the HUD shows the archetype name instead of the role when a unit has one (`PrototypeHud.DescribeUnit` gains an optional parameter; existing calls and tests are unchanged). The control-hint text block is left alone: the ability bar shows its own prompts, so no existing HUD layout moves.
6. **Arming never survives a caster change** (selection change, Tab, death) and pressing the armed slot's key again disarms.
7. **Queued preview scope:** while the queue modifier is held and the caster has orders, the preview uses the static checks only (the same rule `Issue` applies when appending), and says so in the HUD.

## Review Focus

Failure modes the spec implies but a feature-by-feature test list would miss, most likely first. Each has a test in the owning task.

1. **A queued ability whose target dies, walks out of range or is walled off before it runs** must fail with a reason and the unit must carry on with the next order (never stuck, never an exception, nothing in the Console). → Task 5.
2. **R2 + D-pad down must arm an ability and must not also Stop** (and Stop must work again after R2 is released, including after a family switch cancels the held trigger). → Task 6 and Task 8.
3. **Stale armed state:** Tab, a selection change, the caster dying, or the armed slot disappearing while armed must disarm cleanly and leave the cursor in its normal mode (no stuck `Aiming`, no stuck snap side). → Task 8.
4. **Blast edge cases:** no hostile in the radius, a hostile exactly on the radius, the caster standing inside the radius (caster is unaffected), a dead or deactivated hostile inside the radius, and a unit with no `Encounter` wired (fails closed, no exception). → Task 4.
5. **Direct control:** a held move key while an ability is issued must not drop the ability; and a cast must not break follow/park for a companion (an ability order parks it, like any explicit order). → Task 5.

---

## File Structure

New runtime code, `Assets/_Project/Scripts/Abilities/` (assembly `Blackglass`):

| File | Responsibility |
|---|---|
| `AbilityDefinition.cs` | `AbilityTargetMode`, `AbilityTargetSide`, `AbilityEffect`, `AbilityCoverRule` enums; the immutable ScriptableObject. |
| `AbilityFailure.cs` | `AbilityFailure` enum + `Describe()` text. |
| `AbilityRules.cs` | `AbilityCheckScope`, `AbilityFacts`, pure `AbilityRules` (ordered checks, range, area). |
| `UnitAbilities.cs` | `AbilityCheck`; per-unit slots, ready-times, `Check`, `TryUse`, effects, failure record. |
| `AbilityTargeting.cs` | `AbilityPreview`; armed slot, caster resolution, preview, `Confirm`. |
| `AbilityDescriptions.cs` | Pure text formatters for the debug views. |

Also new: `Scripts/Combat/CombatArchetype.cs`, `Scripts/Controls/AbilityMenuGate.cs`, `Scripts/DebugUI/AbilityTargetingView.cs`, `Scripts/DebugUI/AbilityBarView.cs`, data assets under `Assets/_Project/Data/` (Task 10).

Modified: `Commands/UnitCommands.cs` (`AbilityCommand`), `Combat/Health.cs` (`Heal`, `Healed`), `Combat/Encounter.cs` (`OpponentsOf`, `AreHostile`, `AreAllied`), `Units/UnitAttacker.cs` (archetype, point sight, roll), `Units/CommandableUnit.cs`, `Input/StickRole.cs`, `Input/TacticalCursor.cs`, `Input/PromptResolver.cs`, `CameraControl/TacticalCameraController.cs`, `Controls/PlayerCommandInput.cs`, `DebugUI/CommandQueueView.cs`, `DebugUI/PrototypeHud.cs`, `Input/BlackglassControls.inputactions` (+ a generator in the scratchpad, not committed), `Scenes/Prototype.unity` (builder), `Tests/PlayMode/TestSupport/TestWorld.cs`, `Tests/EditMode/InputAssetTests.cs` (one list), `Tests/PlayMode/PrototypeSceneTests.cs` (one expectation), `Docs/Decisions.md`.

New tests: `Tests/EditMode/`: `HealthHealTests`, `EncounterSidesTests`, `AbilityDefinitionTests`, `AbilityRulesTests`, `AbilityCommandTests`, `AbilityFailureTextTests`, `CombatArchetypeTests`, `AbilityInputAssetTests`, `AbilityPromptTests`, `StickRoleAimingTests`, `AbilityDescriptionsTests`; `Tests/PlayMode/`: `UnitAbilitiesPlayModeTests`, `UnitAbilitiesCoverPlayModeTests`, `CommandableUnitAbilityPlayModeTests`, `CommandableUnitAbilityCoverPlayModeTests`, `AbilityMenuGateTests`, `TacticalCursorAimingTests`, `CameraAimingTests`, `AbilityTargetingTests`, `AbilityTargetingPadTests`, `AbilityViewsTests`, `PrototypeSceneAbilityTests`; test support `TestSupport/AbilityRig.cs`.

Decision/doc: `Docs/Decisions.md` decision 025 (Task 11).

## Task execution notes (read once)

- Test fixtures that touch the shared project `InputActionAsset` reset it in `TearDown` **before** `base.TearDown()`: `TestControls.Reset(actions)`. Pad tests select a family with `TestControls.UseGroup(actions, "Xbox")`.
- Waking a simulated pad in a scene that has `ActiveInputDevice`: tap its Select/View button (`pad.selectButton`, unbound and reserved, so it only announces the device); Start is pause. Fixtures without `ActiveInputDevice` need no waking.
- `TestWorld.CreateFighter` assembles a unit while inactive; `UnitAbilities` is added afterwards with `world.AddAbilities(...)` (added in Task 4) because it has no `OnEnable` dependencies.
- Colliders created at runtime are invisible to raycasts until a physics step: `TestWorld.CreateEnvironment` and `CreateObstacle` call `Physics.SyncTransforms()`. Always `yield return null` after creating units before asserting sight.
- Never teleport a NavMeshAgent unit by setting `transform.position`; use `GetComponent<NavMeshAgent>().Warp(point)` or spawn a second unit.
- Quick compile check without running tests: `Tools/run-tests.sh EditMode "Blackglass.Tests.NoSuchFixture"` returns EXIT=1 with `error CS...` lines on a compile error, otherwise 0 tests run.
- Totals quoted below are `baseline + new tests`; re-derive them from the Task 0 numbers if they differ.

---

### Task 0: Branch and baseline

**Files:** none.

- [ ] **Step 1: Create the branch**

```bash
git switch -c phase-7-abilities
git status --short
```
Expected: `?? .claude/` only (the spec and plan are committed on `main` before this branch is made).

- [ ] **Step 2: Re-measure the baseline**

```bash
Tools/run-tests.sh EditMode; Tools/run-tests.sh PlayMode
```
Expected: `total="439" passed="439"` and `total="405" passed="405"`, EXIT=0 both. If the numbers differ, use the measured ones everywhere this plan quotes totals.

- [ ] **Step 3: Keep the baseline logs for the final Console comparison (Task 11)**

```bash
cp Logs/TestRun-EditMode.log Logs/phase7-baseline-EditMode.log
cp Logs/TestRun-PlayMode.log Logs/phase7-baseline-PlayMode.log
```

---

### Task 1: `Health.Heal` and `Encounter` sides

**Files:**
- Modify: `Assets/_Project/Scripts/Combat/Health.cs`, `Assets/_Project/Scripts/Combat/Encounter.cs`
- Test: `Assets/_Project/Tests/EditMode/HealthHealTests.cs`, `Assets/_Project/Tests/EditMode/EncounterSidesTests.cs`

**Interfaces:**
- Consumes: existing `Health` (`Max`, `Current`, `IsAlive`, `TakeDamage`), `Encounter` (`Friendlies`, `Hostiles`, `Initialize`).
- Produces: `int Health.Heal(int amount)` (returns the hit points actually restored; 0 for the dead, at full health or for 0; throws `ArgumentOutOfRangeException` for a negative amount), `event Action<int> Health.Healed`; `IReadOnlyList<Health> Encounter.OpponentsOf(Health unit)` (hostiles for a friendly, friendlies for a hostile, empty otherwise or for null), `bool Encounter.AreHostile(Health a, Health b)`, `bool Encounter.AreAllied(Health a, Health b)` (same side, or the same unit; false with a null).

- [ ] **Step 1: Write the failing tests**

`Assets/_Project/Tests/EditMode/HealthHealTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class HealthHealTests
    {
        GameObject host;
        Health health;
        List<int> healed;

        [SetUp]
        public void SetUp()
        {
            host = new GameObject("HealTest");
            health = host.AddComponent<Health>();
            healed = new List<int>();
            health.Healed += healed.Add;
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(host);

        [Test]
        public void Heal_RestoresDamage_AndReportsTheAmountActuallyRestored()
        {
            health.TakeDamage(60);

            var restored = health.Heal(25);

            Assert.That(restored, Is.EqualTo(25));
            Assert.That(health.Current, Is.EqualTo(65));
            Assert.That(healed, Is.EqualTo(new[] { 25 }));
        }

        [Test]
        public void Heal_NeverGoesAboveMax_AndReportsOnlyWhatWasMissing()
        {
            health.TakeDamage(10);

            var restored = health.Heal(40);

            Assert.That(restored, Is.EqualTo(10));
            Assert.That(health.Current, Is.EqualTo(health.Max));
            Assert.That(healed, Is.EqualTo(new[] { 10 }));
        }

        [Test]
        public void Heal_AtFullHealth_DoesNothingAndRaisesNothing()
        {
            Assert.That(health.Heal(30), Is.EqualTo(0));
            Assert.That(health.Current, Is.EqualTo(health.Max));
            Assert.That(healed, Is.Empty);
        }

        [Test]
        public void Heal_Zero_DoesNothing()
        {
            health.TakeDamage(20);
            Assert.That(health.Heal(0), Is.EqualTo(0));
            Assert.That(health.Current, Is.EqualTo(80));
            Assert.That(healed, Is.Empty);
        }

        [Test]
        public void Heal_TheDead_DoesNothing()
        {
            health.TakeDamage(health.Max);

            Assert.That(health.Heal(50), Is.EqualTo(0));
            Assert.That(health.IsAlive, Is.False);
            Assert.That(health.Current, Is.EqualTo(0));
            Assert.That(healed, Is.Empty);
        }

        [Test]
        public void Heal_Negative_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => health.Heal(-1));
        }
    }
}
```

`Assets/_Project/Tests/EditMode/EncounterSidesTests.cs`:

```csharp
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class EncounterSidesTests
    {
        GameObject systems;
        Encounter encounter;
        readonly List<GameObject> hosts = new List<GameObject>();
        Health friendlyA;
        Health friendlyB;
        Health hostileA;
        Health hostileB;
        Health stranger;

        Health Unit(string name)
        {
            var host = new GameObject(name);
            hosts.Add(host);
            return host.AddComponent<Health>();
        }

        [SetUp]
        public void SetUp()
        {
            systems = new GameObject("Systems");
            encounter = systems.AddComponent<Encounter>();
            friendlyA = Unit("FriendlyA");
            friendlyB = Unit("FriendlyB");
            hostileA = Unit("HostileA");
            hostileB = Unit("HostileB");
            stranger = Unit("Stranger");
            encounter.Initialize(new[] { friendlyA, friendlyB }, new[] { hostileA, hostileB });
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(systems);
            foreach (var host in hosts)
            {
                if (host != null)
                    Object.DestroyImmediate(host);
            }
            hosts.Clear();
        }

        [Test]
        public void OpponentsOf_AFriendlyIsTheHostiles_AHostileIsTheFriendlies_AStrangerIsNobody()
        {
            Assert.That(encounter.OpponentsOf(friendlyA), Is.EqualTo(new[] { hostileA, hostileB }));
            Assert.That(encounter.OpponentsOf(hostileB), Is.EqualTo(new[] { friendlyA, friendlyB }));
            Assert.That(encounter.OpponentsOf(stranger), Is.Empty);
            Assert.That(encounter.OpponentsOf(null), Is.Empty);
        }

        [Test]
        public void AreHostile_IsTrueOnlyAcrossSides()
        {
            Assert.That(encounter.AreHostile(friendlyA, hostileA), Is.True);
            Assert.That(encounter.AreHostile(hostileB, friendlyB), Is.True);
            Assert.That(encounter.AreHostile(friendlyA, friendlyB), Is.False);
            Assert.That(encounter.AreHostile(hostileA, hostileB), Is.False);
            Assert.That(encounter.AreHostile(friendlyA, friendlyA), Is.False);
            Assert.That(encounter.AreHostile(friendlyA, stranger), Is.False);
        }

        [Test]
        public void AreAllied_IsTrueForTheSameSideOrTheSameUnit()
        {
            Assert.That(encounter.AreAllied(friendlyA, friendlyB), Is.True);
            Assert.That(encounter.AreAllied(hostileA, hostileB), Is.True);
            Assert.That(encounter.AreAllied(friendlyA, friendlyA), Is.True);
            Assert.That(encounter.AreAllied(friendlyA, hostileA), Is.False);
            Assert.That(encounter.AreAllied(friendlyA, stranger), Is.False);
        }

        [Test]
        public void Nulls_AndDestroyedUnits_AreNeverHostileOrAllied()
        {
            Assert.That(encounter.AreHostile(null, hostileA), Is.False);
            Assert.That(encounter.AreHostile(friendlyA, null), Is.False);
            Assert.That(encounter.AreAllied(null, null), Is.False);

            Object.DestroyImmediate(hostileA.gameObject);

            Assert.That(encounter.AreHostile(friendlyA, hostileA), Is.False);
            Assert.That(encounter.AreAllied(hostileA, hostileB), Is.False);
            Assert.That(encounter.OpponentsOf(hostileA), Is.Empty);
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail to compile**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.HealthHealTests"`
Expected: EXIT=1 with `error CS1061` mentioning `Heal` / `Healed` / `OpponentsOf` / `AreHostile` / `AreAllied`.

- [ ] **Step 3: Implement `Health.Heal`**

In `Assets/_Project/Scripts/Combat/Health.cs`, add the event under the existing `Died` event:

```csharp
        /// <summary>Raised with the hit points actually restored whenever a living target is healed.</summary>
        public event Action<int> Healed;
```

and the method after `TakeDamage`:

```csharp
        /// <summary>
        /// Restores up to `amount` hit points, never above Max. Returns how many were restored (0 for the dead, at full
        /// health or for 0). The dead stay dead.
        /// </summary>
        public int Heal(int amount)
        {
            if (amount < 0)
                throw new ArgumentOutOfRangeException(nameof(amount), amount, "Healing cannot be negative.");
            if (hasDied || amount == 0 || damageTaken == 0)
                return 0;

            var restored = Math.Min(amount, damageTaken);
            damageTaken -= restored;
            Healed?.Invoke(restored);
            return restored;
        }
```

Update the class summary line to: `/// <summary>Hit points for anything that can be attacked or healed. Dies once, at zero, and by default deactivates then.</summary>`.

- [ ] **Step 4: Implement the `Encounter` side queries**

In `Assets/_Project/Scripts/Combat/Encounter.cs` add a static field next to the serialized lists:

```csharp
        static readonly IReadOnlyList<Health> NoUnits = Array.Empty<Health>();
```

and these members after `Outcome`:

```csharp
        /// <summary>
        /// The units that fight `unit`: the hostiles for a friendly, the friendlies for a hostile, nobody for a unit
        /// that is on neither side (or null).
        /// </summary>
        public IReadOnlyList<Health> OpponentsOf(Health unit)
        {
            if (unit == null)
                return NoUnits;
            if (friendlies.Contains(unit))
                return hostiles;
            return hostiles.Contains(unit) ? friendlies : NoUnits;
        }

        /// <summary>True when `a` and `b` are on opposite sides. False for the same unit, a stranger or a null.</summary>
        public bool AreHostile(Health a, Health b)
        {
            if (a == null || b == null)
                return false;
            var opponents = OpponentsOf(a);
            for (var i = 0; i < opponents.Count; i++)
            {
                if (opponents[i] == b)
                    return true;
            }
            return false;
        }

        /// <summary>True when `a` and `b` are the same unit or on the same side. False for a stranger or a null.</summary>
        public bool AreAllied(Health a, Health b)
        {
            if (a == null || b == null)
                return false;
            if (a == b)
                return true;
            return (friendlies.Contains(a) && friendlies.Contains(b)) || (hostiles.Contains(a) && hostiles.Contains(b));
        }
```

Update the class summary to add: `AreHostile`, `AreAllied` and `OpponentsOf` answer side questions for abilities.

- [ ] **Step 5: Run the tests**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.HealthHealTests"` then `Tools/run-tests.sh EditMode "Blackglass.Tests.EncounterSidesTests"`
Expected: EXIT=0; 6 and 4 tests pass. Then `Tools/run-tests.sh EditMode` → EXIT=0, total = baseline + 10 (449).

- [ ] **Step 6: Commit**

```bash
git add Assets/_Project/Scripts/Combat Assets/_Project/Tests/EditMode
git commit -m "Add Health.Heal and the Encounter side queries abilities need

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Ability definition, validation rules and the ability command

**Files:**
- Create: `Assets/_Project/Scripts/Abilities/AbilityDefinition.cs`, `AbilityFailure.cs`, `AbilityRules.cs`
- Modify: `Assets/_Project/Scripts/Commands/UnitCommands.cs`
- Test: `Assets/_Project/Tests/EditMode/AbilityDefinitionTests.cs`, `AbilityRulesTests.cs`, `AbilityCommandTests.cs`

**Interfaces:**
- Consumes: `Health` (`IsAlive`, `transform`), `UnitCommand`.
- Produces:
  - enums `AbilityTargetMode { Unit, Ground }`, `AbilityTargetSide { Hostile, Friendly }`, `AbilityEffect { Damage, Heal }`, `AbilityCoverRule { Applies, Ignored }`.
  - `sealed class AbilityDefinition : ScriptableObject` with read-only properties `DisplayName`, `TargetMode`, `TargetSide`, `Range`, `RequiresLineOfSight`, `CoverRule`, `Cooldown`, `Effect`, `Amount`, `Radius`; `internal static AbilityDefinition Create(string displayName, AbilityTargetMode mode, AbilityTargetSide side, float range, bool requiresLineOfSight, AbilityCoverRule coverRule, float cooldown, AbilityEffect effect, int amount, float radius = 0f)` (throws `ArgumentException` for a Ground ability that is not Damage or has no radius, and `ArgumentOutOfRangeException` for a non-positive range).
  - `enum AbilityFailure { None, CasterDead, UnknownAbility, NoTarget, TargetDead, WrongSide, NoPosition, OnCooldown, OutOfRange, NoLineOfSight }` and `static string AbilityFailureText.Describe(this AbilityFailure)`.
  - `enum AbilityCheckScope { Full, Static }`; `readonly struct AbilityFacts(bool casterAlive, bool hasAbility, bool hasTarget, bool targetAlive, bool targetOnRequiredSide, bool hasPosition, float cooldownRemaining, float flatDistance)` with the same names as properties (`CasterAlive`, `HasAbility`, `HasTarget`, `TargetAlive`, `TargetOnRequiredSide`, `HasPosition`, `CooldownRemaining`, `FlatDistance`).
  - `static class AbilityRules`: `AbilityFailure CheckBasics(AbilityDefinition ability, in AbilityFacts facts, AbilityCheckScope scope)`, `AbilityFailure CheckSight(AbilityDefinition ability, bool lineOfSightClear)`, `bool IsInRange(float flatDistance, float range)` (inclusive), `bool IsInArea(Vector3 center, Vector3 position, float radius)` (flat, inclusive).
  - `sealed class AbilityCommand : UnitCommand` with `static AbilityCommand OnUnit(AbilityDefinition, Health target)`, `static AbilityCommand AtGround(AbilityDefinition, Vector3 point)`, properties `Definition`, `Target`, `Point`, `Vector3 AimPoint`.

- [ ] **Step 1: Write the failing tests**

`Assets/_Project/Tests/EditMode/AbilityDefinitionTests.cs`:

```csharp
using System;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class AbilityDefinitionTests
    {
        AbilityDefinition created;

        [TearDown]
        public void TearDown()
        {
            if (created != null)
                Object.DestroyImmediate(created);
        }

        [Test]
        public void Create_ExposesEveryConfiguredValue()
        {
            created = AbilityDefinition.Create("Aimed Shot", AbilityTargetMode.Unit, AbilityTargetSide.Hostile, 14f, true,
                AbilityCoverRule.Applies, 6f, AbilityEffect.Damage, 45);

            Assert.That(created.DisplayName, Is.EqualTo("Aimed Shot"));
            Assert.That(created.TargetMode, Is.EqualTo(AbilityTargetMode.Unit));
            Assert.That(created.TargetSide, Is.EqualTo(AbilityTargetSide.Hostile));
            Assert.That(created.Range, Is.EqualTo(14f));
            Assert.That(created.RequiresLineOfSight, Is.True);
            Assert.That(created.CoverRule, Is.EqualTo(AbilityCoverRule.Applies));
            Assert.That(created.Cooldown, Is.EqualTo(6f));
            Assert.That(created.Effect, Is.EqualTo(AbilityEffect.Damage));
            Assert.That(created.Amount, Is.EqualTo(45));
            Assert.That(created.Radius, Is.EqualTo(0f));
        }

        [Test]
        public void Create_AGroundAbility_IsAlwaysHostileAreaDamage()
        {
            created = AbilityDefinition.Create("Blast", AbilityTargetMode.Ground, AbilityTargetSide.Friendly, 12f, true,
                AbilityCoverRule.Ignored, 10f, AbilityEffect.Damage, 35, 3f);

            Assert.That(created.TargetSide, Is.EqualTo(AbilityTargetSide.Hostile), "Area damage is hostile-only: no friendly fire");
            Assert.That(created.Radius, Is.EqualTo(3f));
        }

        [Test]
        public void Create_AGroundHeal_IsRejected()
        {
            Assert.Throws<ArgumentException>(() => AbilityDefinition.Create("Aura", AbilityTargetMode.Ground,
                AbilityTargetSide.Friendly, 8f, false, AbilityCoverRule.Ignored, 5f, AbilityEffect.Heal, 10, 3f));
        }

        [Test]
        public void Create_AGroundAbilityWithoutARadius_IsRejected()
        {
            Assert.Throws<ArgumentException>(() => AbilityDefinition.Create("Blast", AbilityTargetMode.Ground,
                AbilityTargetSide.Hostile, 12f, true, AbilityCoverRule.Ignored, 10f, AbilityEffect.Damage, 35, 0f));
        }

        [Test]
        public void Create_ARangeThatIsNotPositive_IsRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => AbilityDefinition.Create("Nothing", AbilityTargetMode.Unit,
                AbilityTargetSide.Hostile, 0f, false, AbilityCoverRule.Applies, 1f, AbilityEffect.Damage, 1));
        }
    }
}
```

`Assets/_Project/Tests/EditMode/AbilityRulesTests.cs`:

```csharp
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class AbilityRulesTests
    {
        AbilityDefinition aimed;
        AbilityDefinition mend;
        AbilityDefinition blast;

        [SetUp]
        public void SetUp()
        {
            aimed = AbilityDefinition.Create("Aimed Shot", AbilityTargetMode.Unit, AbilityTargetSide.Hostile, 14f, true,
                AbilityCoverRule.Applies, 6f, AbilityEffect.Damage, 45);
            mend = AbilityDefinition.Create("Mend", AbilityTargetMode.Unit, AbilityTargetSide.Friendly, 8f, false,
                AbilityCoverRule.Ignored, 8f, AbilityEffect.Heal, 40);
            blast = AbilityDefinition.Create("Blast", AbilityTargetMode.Ground, AbilityTargetSide.Hostile, 12f, true,
                AbilityCoverRule.Ignored, 10f, AbilityEffect.Damage, 35, 3f);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(aimed);
            Object.DestroyImmediate(mend);
            Object.DestroyImmediate(blast);
        }

        static AbilityFacts Facts(bool casterAlive = true, bool hasAbility = true, bool hasTarget = true,
            bool targetAlive = true, bool targetOnSide = true, bool hasPosition = true, float cooldown = 0f,
            float distance = 5f) =>
            new AbilityFacts(casterAlive, hasAbility, hasTarget, targetAlive, targetOnSide, hasPosition, cooldown, distance);

        const AbilityCheckScope Full = AbilityCheckScope.Full;
        const AbilityCheckScope Static = AbilityCheckScope.Static;

        [Test]
        public void EverythingInOrder_IsNone()
        {
            Assert.That(AbilityRules.CheckBasics(aimed, Facts(), Full), Is.EqualTo(AbilityFailure.None));
            Assert.That(AbilityRules.CheckBasics(blast, Facts(), Full), Is.EqualTo(AbilityFailure.None));
        }

        [Test]
        public void EachFailure_IsReported()
        {
            Assert.That(AbilityRules.CheckBasics(aimed, Facts(casterAlive: false), Full), Is.EqualTo(AbilityFailure.CasterDead));
            Assert.That(AbilityRules.CheckBasics(aimed, Facts(hasAbility: false), Full), Is.EqualTo(AbilityFailure.UnknownAbility));
            Assert.That(AbilityRules.CheckBasics(aimed, Facts(hasTarget: false), Full), Is.EqualTo(AbilityFailure.NoTarget));
            Assert.That(AbilityRules.CheckBasics(aimed, Facts(targetAlive: false), Full), Is.EqualTo(AbilityFailure.TargetDead));
            Assert.That(AbilityRules.CheckBasics(aimed, Facts(targetOnSide: false), Full), Is.EqualTo(AbilityFailure.WrongSide));
            Assert.That(AbilityRules.CheckBasics(blast, Facts(hasPosition: false), Full), Is.EqualTo(AbilityFailure.NoPosition));
            Assert.That(AbilityRules.CheckBasics(aimed, Facts(cooldown: 0.1f), Full), Is.EqualTo(AbilityFailure.OnCooldown));
            Assert.That(AbilityRules.CheckBasics(aimed, Facts(distance: 14.01f), Full), Is.EqualTo(AbilityFailure.OutOfRange));
        }

        [Test]
        public void TheFirstFailureInTheDocumentedOrderWins()
        {
            var everythingWrong = Facts(casterAlive: false, hasAbility: false, hasTarget: false, targetAlive: false,
                targetOnSide: false, cooldown: 3f, distance: 99f);
            Assert.That(AbilityRules.CheckBasics(aimed, everythingWrong, Full), Is.EqualTo(AbilityFailure.CasterDead));

            var noAbility = Facts(hasAbility: false, hasTarget: false, cooldown: 3f, distance: 99f);
            Assert.That(AbilityRules.CheckBasics(aimed, noAbility, Full), Is.EqualTo(AbilityFailure.UnknownAbility));

            var noTarget = Facts(hasTarget: false, targetAlive: false, cooldown: 3f, distance: 99f);
            Assert.That(AbilityRules.CheckBasics(aimed, noTarget, Full), Is.EqualTo(AbilityFailure.NoTarget));

            var deadTarget = Facts(targetAlive: false, targetOnSide: false, cooldown: 3f, distance: 99f);
            Assert.That(AbilityRules.CheckBasics(aimed, deadTarget, Full), Is.EqualTo(AbilityFailure.TargetDead));

            var wrongSide = Facts(targetOnSide: false, cooldown: 3f, distance: 99f);
            Assert.That(AbilityRules.CheckBasics(aimed, wrongSide, Full), Is.EqualTo(AbilityFailure.WrongSide));

            var coolingAndFar = Facts(cooldown: 3f, distance: 99f);
            Assert.That(AbilityRules.CheckBasics(aimed, coolingAndFar, Full), Is.EqualTo(AbilityFailure.OnCooldown));
        }

        [Test]
        public void ARangeCheckIsInclusive()
        {
            Assert.That(AbilityRules.CheckBasics(aimed, Facts(distance: 14f), Full), Is.EqualTo(AbilityFailure.None));
            Assert.That(AbilityRules.IsInRange(14f, 14f), Is.True);
            Assert.That(AbilityRules.IsInRange(14.0001f, 14f), Is.False);
        }

        [Test]
        public void TheStaticScope_OnlyChecksWhoAndWhat_NotWhenOrHowFar()
        {
            var late = Facts(cooldown: 5f, distance: 99f);
            Assert.That(AbilityRules.CheckBasics(aimed, late, Static), Is.EqualTo(AbilityFailure.None));
            Assert.That(AbilityRules.CheckBasics(aimed, Facts(targetAlive: false, cooldown: 5f), Static), Is.EqualTo(AbilityFailure.TargetDead));
            Assert.That(AbilityRules.CheckBasics(aimed, Facts(targetOnSide: false), Static), Is.EqualTo(AbilityFailure.WrongSide));
            Assert.That(AbilityRules.CheckBasics(aimed, Facts(casterAlive: false), Static), Is.EqualTo(AbilityFailure.CasterDead));
        }

        [Test]
        public void AGroundAbility_IgnoresTheTargetFacts_AndNeedsAPosition()
        {
            var noTargetAtAll = Facts(hasTarget: false, targetAlive: false, targetOnSide: false);
            Assert.That(AbilityRules.CheckBasics(blast, noTargetAtAll, Full), Is.EqualTo(AbilityFailure.None));
            Assert.That(AbilityRules.CheckBasics(blast, Facts(hasPosition: false), Static), Is.EqualTo(AbilityFailure.NoPosition));
        }

        [Test]
        public void ANullAbility_IsUnknown_NotACrash()
        {
            Assert.That(AbilityRules.CheckBasics(null, Facts(hasAbility: false), Full), Is.EqualTo(AbilityFailure.UnknownAbility));
        }

        [Test]
        public void Sight_IsOnlyRequiredWhenTheAbilityAsksForIt()
        {
            Assert.That(AbilityRules.CheckSight(aimed, false), Is.EqualTo(AbilityFailure.NoLineOfSight));
            Assert.That(AbilityRules.CheckSight(aimed, true), Is.EqualTo(AbilityFailure.None));
            Assert.That(AbilityRules.CheckSight(blast, false), Is.EqualTo(AbilityFailure.NoLineOfSight));
            Assert.That(AbilityRules.CheckSight(mend, false), Is.EqualTo(AbilityFailure.None), "Mend ignores walls");
        }

        [Test]
        public void IsInArea_IsFlatAndInclusive()
        {
            var center = new Vector3(0f, 0f, 4f);
            Assert.That(AbilityRules.IsInArea(center, new Vector3(0f, 0f, 4f), 3f), Is.True);
            Assert.That(AbilityRules.IsInArea(center, new Vector3(3f, 0f, 4f), 3f), Is.True, "On the edge counts");
            Assert.That(AbilityRules.IsInArea(center, new Vector3(3.01f, 0f, 4f), 3f), Is.False);
            Assert.That(AbilityRules.IsInArea(center, new Vector3(0f, 1f, 4f), 0.5f), Is.True, "Height is ignored: a pivot is 1 m up");
            // (2, 6) is a flat distance of 2.828 from (0, 4).
            Assert.That(AbilityRules.IsInArea(center, new Vector3(2f, 0f, 6f), 2.83f), Is.True);
            Assert.That(AbilityRules.IsInArea(center, new Vector3(2f, 0f, 6f), 2.8f), Is.False);
        }
    }
}
```

`Assets/_Project/Tests/EditMode/AbilityCommandTests.cs`:

```csharp
using System;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class AbilityCommandTests
    {
        AbilityDefinition aimed;
        AbilityDefinition blast;
        GameObject host;
        Health target;

        [SetUp]
        public void SetUp()
        {
            aimed = AbilityDefinition.Create("Aimed Shot", AbilityTargetMode.Unit, AbilityTargetSide.Hostile, 14f, true,
                AbilityCoverRule.Applies, 6f, AbilityEffect.Damage, 45);
            blast = AbilityDefinition.Create("Blast", AbilityTargetMode.Ground, AbilityTargetSide.Hostile, 12f, true,
                AbilityCoverRule.Ignored, 10f, AbilityEffect.Damage, 35, 3f);
            host = new GameObject("Target");
            host.transform.position = new Vector3(2f, 1f, 3f);
            target = host.AddComponent<Health>();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(aimed);
            Object.DestroyImmediate(blast);
            Object.DestroyImmediate(host);
        }

        [Test]
        public void OnUnit_KeepsTheDefinitionAndTheTarget_AndAimsAtIt()
        {
            var command = AbilityCommand.OnUnit(aimed, target);

            Assert.That(command.Definition, Is.SameAs(aimed));
            Assert.That(command.Target, Is.SameAs(target));
            Assert.That(command.AimPoint, Is.EqualTo(new Vector3(2f, 1f, 3f)));
        }

        [Test]
        public void AtGround_KeepsTheDefinitionAndThePoint_AndAimsAtIt()
        {
            var command = AbilityCommand.AtGround(blast, new Vector3(5f, 0f, -2f));

            Assert.That(command.Definition, Is.SameAs(blast));
            Assert.That(command.Target, Is.Null);
            Assert.That(command.Point, Is.EqualTo(new Vector3(5f, 0f, -2f)));
            Assert.That(command.AimPoint, Is.EqualTo(new Vector3(5f, 0f, -2f)));
        }

        [Test]
        public void TheFactories_RejectNullsAndTheWrongTargetMode()
        {
            Assert.Throws<ArgumentNullException>(() => AbilityCommand.OnUnit(null, target));
            Assert.Throws<ArgumentNullException>(() => AbilityCommand.OnUnit(aimed, null));
            Assert.Throws<ArgumentNullException>(() => AbilityCommand.AtGround(null, Vector3.zero));
            Assert.Throws<ArgumentException>(() => AbilityCommand.OnUnit(blast, target));
            Assert.Throws<ArgumentException>(() => AbilityCommand.AtGround(aimed, Vector3.zero));
        }

        [Test]
        public void AUnitCommand_WhoseTargetWasDestroyed_StillAimsWithoutThrowing()
        {
            var command = AbilityCommand.OnUnit(aimed, target);
            Object.DestroyImmediate(host);

            Assert.That(() => command.AimPoint, Throws.Nothing);
        }

        [Test]
        public void ItIsAnOrderLikeTheOthers()
        {
            UnitCommand command = AbilityCommand.OnUnit(aimed, target);
            Assert.That(command, Is.InstanceOf<AbilityCommand>());
        }
    }
}
```

Add one more small fixture to `AbilityRulesTests.cs`'s file? No: put the failure-text test in `AbilityDefinitionTests.cs`' neighbour file `Assets/_Project/Tests/EditMode/AbilityFailureTextTests.cs`:

```csharp
using System;
using NUnit.Framework;

namespace Blackglass.Tests
{
    public class AbilityFailureTextTests
    {
        [Test]
        public void EveryFailure_HasAReadableDescription()
        {
            foreach (AbilityFailure failure in Enum.GetValues(typeof(AbilityFailure)))
                Assert.That(failure.Describe(), Is.Not.Empty, failure.ToString());
        }

        [TestCase(AbilityFailure.None, "ready")]
        [TestCase(AbilityFailure.OutOfRange, "out of range")]
        [TestCase(AbilityFailure.NoLineOfSight, "no line of sight")]
        [TestCase(AbilityFailure.OnCooldown, "on cooldown")]
        [TestCase(AbilityFailure.TargetDead, "target is down")]
        public void TheCommonReasons_ReadAsExpected(AbilityFailure failure, string text)
        {
            Assert.That(failure.Describe(), Is.EqualTo(text));
        }
    }
}
```

- [ ] **Step 2: Run to verify compile failure**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.AbilityRulesTests"`
Expected: EXIT=1 with `error CS0246` for `AbilityDefinition`, `AbilityFacts`, `AbilityCommand` etc.

- [ ] **Step 3: Implement `AbilityDefinition`**

`Assets/_Project/Scripts/Abilities/AbilityDefinition.cs`:

```csharp
using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>What an ability is aimed at: a unit, or a point on the ground (an area).</summary>
    public enum AbilityTargetMode
    {
        Unit,
        Ground,
    }

    /// <summary>For a unit-targeted ability, whose side the target must be on, relative to the caster.</summary>
    public enum AbilityTargetSide
    {
        Hostile,
        Friendly,
    }

    public enum AbilityEffect
    {
        Damage,
        Heal,
    }

    /// <summary>Whether a covered target's hit chance applies to the ability (explicit per ability, never accidental).</summary>
    public enum AbilityCoverRule
    {
        /// <summary>A target that holds cover protecting it from the caster (or the blast point) is hit with its cover's chance.</summary>
        Applies,
        /// <summary>Cover does not matter: the effect always lands.</summary>
        Ignored,
    }

    /// <summary>
    /// One ability's rules as shared, immutable data: how it is aimed, how far, whether it needs line of sight, how it
    /// treats cover, its cooldown and its effect. Never holds runtime state: cooldowns and the last failure live on the
    /// unit (UnitAbilities), so two units sharing one definition never share a cooldown. A ground ability is always
    /// area damage to the caster's hostiles (no friendly fire, decision 025).
    /// </summary>
    [CreateAssetMenu(menuName = "Blackglass/Ability", fileName = "Ability")]
    public sealed class AbilityDefinition : ScriptableObject
    {
        [SerializeField] string displayName = "Ability";
        [SerializeField] AbilityTargetMode targetMode = AbilityTargetMode.Unit;
        [SerializeField] AbilityTargetSide targetSide = AbilityTargetSide.Hostile;
        [SerializeField, Min(0.5f)] float range = 10f;
        [SerializeField] bool requiresLineOfSight = true;
        [SerializeField] AbilityCoverRule coverRule = AbilityCoverRule.Applies;
        [SerializeField, Min(0f)] float cooldown = 5f;
        [SerializeField] AbilityEffect effect = AbilityEffect.Damage;
        [SerializeField, Min(0)] int amount = 25;
        // Ground only: the blast radius in metres.
        [SerializeField, Min(0f)] float radius;

        public string DisplayName => displayName;
        public AbilityTargetMode TargetMode => targetMode;
        public AbilityTargetSide TargetSide => targetSide;
        /// <summary>Flat distance in metres from the caster to the target or point; inclusive.</summary>
        public float Range => range;
        public bool RequiresLineOfSight => requiresLineOfSight;
        public AbilityCoverRule CoverRule => coverRule;
        /// <summary>Seconds of scaled time before the same unit can use it again.</summary>
        public float Cooldown => cooldown;
        public AbilityEffect Effect => effect;
        /// <summary>Damage dealt or hit points restored.</summary>
        public int Amount => amount;
        /// <summary>Ground abilities only.</summary>
        public float Radius => radius;

        internal static AbilityDefinition Create(string displayName, AbilityTargetMode mode, AbilityTargetSide side,
            float range, bool requiresLineOfSight, AbilityCoverRule coverRule, float cooldown, AbilityEffect effect,
            int amount, float radius = 0f)
        {
            if (range <= 0f)
                throw new ArgumentOutOfRangeException(nameof(range), range, "An ability needs a positive range.");
            if (mode == AbilityTargetMode.Ground)
            {
                if (effect != AbilityEffect.Damage)
                    throw new ArgumentException("A ground ability is area damage; it cannot heal.", nameof(effect));
                if (radius <= 0f)
                    throw new ArgumentException("A ground ability needs a positive radius.", nameof(radius));
                side = AbilityTargetSide.Hostile;
            }

            var definition = CreateInstance<AbilityDefinition>();
            definition.displayName = displayName;
            definition.targetMode = mode;
            definition.targetSide = side;
            definition.range = range;
            definition.requiresLineOfSight = requiresLineOfSight;
            definition.coverRule = coverRule;
            definition.cooldown = cooldown;
            definition.effect = effect;
            definition.amount = amount;
            definition.radius = radius;
            return definition;
        }

        // Keeps an asset edited in the Inspector inside the rules Create enforces.
        void OnValidate()
        {
            if (targetMode != AbilityTargetMode.Ground)
                return;
            effect = AbilityEffect.Damage;
            targetSide = AbilityTargetSide.Hostile;
            if (radius <= 0f)
                radius = 1f;
        }
    }
}
```

- [ ] **Step 4: Implement `AbilityFailure`**

`Assets/_Project/Scripts/Abilities/AbilityFailure.cs`:

```csharp
namespace Blackglass
{
    /// <summary>Why an ability cannot be used. Checked in this order; the first that applies is the reason.</summary>
    public enum AbilityFailure
    {
        None,
        CasterDead,
        UnknownAbility,
        NoTarget,
        TargetDead,
        WrongSide,
        NoPosition,
        OnCooldown,
        OutOfRange,
        NoLineOfSight,
    }

    public static class AbilityFailureText
    {
        /// <summary>Short lower-case text for the debug HUD, e.g. "out of range".</summary>
        public static string Describe(this AbilityFailure failure)
        {
            switch (failure)
            {
                case AbilityFailure.None:
                    return "ready";
                case AbilityFailure.CasterDead:
                    return "caster is down";
                case AbilityFailure.UnknownAbility:
                    return "unit does not have this ability";
                case AbilityFailure.NoTarget:
                    return "no target";
                case AbilityFailure.TargetDead:
                    return "target is down";
                case AbilityFailure.WrongSide:
                    return "wrong side for this ability";
                case AbilityFailure.NoPosition:
                    return "no position";
                case AbilityFailure.OnCooldown:
                    return "on cooldown";
                case AbilityFailure.OutOfRange:
                    return "out of range";
                case AbilityFailure.NoLineOfSight:
                    return "no line of sight";
                default:
                    return failure.ToString();
            }
        }
    }
}
```

- [ ] **Step 5: Implement `AbilityRules`**

`Assets/_Project/Scripts/Abilities/AbilityRules.cs`:

```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// How much of the validation to run. Full is everything. Static is only the checks that do not depend on where
    /// the caster will be or when: used when an ability is queued behind other orders (the caster will have moved on
    /// by the time it runs) and re-run in full when it starts.
    /// </summary>
    public enum AbilityCheckScope
    {
        Full,
        Static,
    }

    /// <summary>The facts about one attempt to use an ability, gathered by UnitAbilities so the rules stay pure.</summary>
    public readonly struct AbilityFacts
    {
        public AbilityFacts(bool casterAlive, bool hasAbility, bool hasTarget, bool targetAlive, bool targetOnRequiredSide,
            bool hasPosition, float cooldownRemaining, float flatDistance)
        {
            CasterAlive = casterAlive;
            HasAbility = hasAbility;
            HasTarget = hasTarget;
            TargetAlive = targetAlive;
            TargetOnRequiredSide = targetOnRequiredSide;
            HasPosition = hasPosition;
            CooldownRemaining = cooldownRemaining;
            FlatDistance = flatDistance;
        }

        public bool CasterAlive { get; }
        public bool HasAbility { get; }
        public bool HasTarget { get; }
        /// <summary>The target is alive and active in the hierarchy.</summary>
        public bool TargetAlive { get; }
        public bool TargetOnRequiredSide { get; }
        public bool HasPosition { get; }
        public float CooldownRemaining { get; }
        /// <summary>Flat distance from the caster to the target or point.</summary>
        public float FlatDistance { get; }
    }

    /// <summary>
    /// The one ordered validation of an ability use: caster alive, ability known, target (or position) valid, cooldown,
    /// range, then line of sight. The preview, Issue and execution all use it, so what the player sees is what runs.
    /// Pure and static; sight is a separate call because it costs a raycast.
    /// </summary>
    public static class AbilityRules
    {
        public static AbilityFailure CheckBasics(AbilityDefinition ability, in AbilityFacts facts, AbilityCheckScope scope)
        {
            if (!facts.CasterAlive)
                return AbilityFailure.CasterDead;
            if (!facts.HasAbility || ability == null)
                return AbilityFailure.UnknownAbility;

            if (ability.TargetMode == AbilityTargetMode.Unit)
            {
                if (!facts.HasTarget)
                    return AbilityFailure.NoTarget;
                if (!facts.TargetAlive)
                    return AbilityFailure.TargetDead;
                if (!facts.TargetOnRequiredSide)
                    return AbilityFailure.WrongSide;
            }
            else if (!facts.HasPosition)
            {
                return AbilityFailure.NoPosition;
            }

            if (scope == AbilityCheckScope.Static)
                return AbilityFailure.None;
            if (facts.CooldownRemaining > 0f)
                return AbilityFailure.OnCooldown;
            if (!IsInRange(facts.FlatDistance, ability.Range))
                return AbilityFailure.OutOfRange;
            return AbilityFailure.None;
        }

        /// <summary>NoLineOfSight when the ability needs a clear line and it is blocked.</summary>
        public static AbilityFailure CheckSight(AbilityDefinition ability, bool lineOfSightClear) =>
            ability != null && ability.RequiresLineOfSight && !lineOfSightClear
                ? AbilityFailure.NoLineOfSight
                : AbilityFailure.None;

        /// <summary>Inclusive: a target exactly at range is in range.</summary>
        public static bool IsInRange(float flatDistance, float range) => flatDistance <= range;

        /// <summary>Flat and inclusive: units stand at pivot height, blast points on the ground.</summary>
        public static bool IsInArea(Vector3 center, Vector3 position, float radius) =>
            CoverRules.FlatDistance(center, position) <= radius;
    }
}
```

- [ ] **Step 6: Implement `AbilityCommand`**

In `Assets/_Project/Scripts/Commands/UnitCommands.cs` change the `UnitCommand` summary's last sentences to: `The commands are Move, Attack, MoveToCover, Ability and Stop. Future commands (Interact) are new subclasses.` and add after `MoveToCoverCommand`:

```csharp
    /// <summary>
    /// Use an ability on a unit or at a ground point. Plain data like every order: the unit validates it when it starts
    /// and again when it runs (UnitAbilities), and a failure ends the order without a cooldown. Instant: there is no
    /// cast time.
    /// </summary>
    public sealed class AbilityCommand : UnitCommand
    {
        AbilityCommand(AbilityDefinition definition, Health target, Vector3 point)
        {
            Definition = definition;
            Target = target;
            Point = point;
        }

        /// <summary>An ability aimed at a unit (Unit mode).</summary>
        public static AbilityCommand OnUnit(AbilityDefinition definition, Health target)
        {
            if (definition == null)
                throw new ArgumentNullException(nameof(definition));
            if (target == null)
                throw new ArgumentNullException(nameof(target));
            if (definition.TargetMode != AbilityTargetMode.Unit)
                throw new ArgumentException($"{definition.DisplayName} is aimed at the ground, not at a unit.", nameof(definition));
            return new AbilityCommand(definition, target, target.transform.position);
        }

        /// <summary>An ability aimed at a point on the ground (Ground mode).</summary>
        public static AbilityCommand AtGround(AbilityDefinition definition, Vector3 point)
        {
            if (definition == null)
                throw new ArgumentNullException(nameof(definition));
            if (definition.TargetMode != AbilityTargetMode.Ground)
                throw new ArgumentException($"{definition.DisplayName} is aimed at a unit, not at the ground.", nameof(definition));
            return new AbilityCommand(definition, null, point);
        }

        public AbilityDefinition Definition { get; }

        /// <summary>The unit aimed at (Unit mode), else null.</summary>
        public Health Target { get; }

        /// <summary>The ground point (Ground mode); for a unit ability, where the target stood when ordered.</summary>
        public Vector3 Point { get; }

        /// <summary>Where the ability is aimed now: the target's position while it exists, else the stored point.</summary>
        public Vector3 AimPoint => Target != null ? Target.transform.position : Point;
    }
```

- [ ] **Step 7: Run the tests**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.AbilityDefinitionTests"`, then the same for `AbilityRulesTests`, `AbilityCommandTests`, `AbilityFailureTextTests`.
Expected: EXIT=0; 5, 9, 5, 6 tests. Then `Tools/run-tests.sh EditMode` → EXIT=0, total = 449 + 25 = 474. (`AbilityFailureTextTests` has 1 + 5 cases.)

- [ ] **Step 8: Commit**

```bash
git add Assets/_Project/Scripts Assets/_Project/Tests/EditMode
git commit -m "Add the ability definition, the ordered validation rules and the ability command

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Combat archetypes

**Files:**
- Create: `Assets/_Project/Scripts/Combat/CombatArchetype.cs`
- Modify: `Assets/_Project/Scripts/Units/UnitAttacker.cs`
- Test: `Assets/_Project/Tests/EditMode/CombatArchetypeTests.cs`

**Interfaces:**
- Consumes: `CombatRole { Melee, Ranged }`, `UnitAttacker` (`Initialize`, `Role`, `Range`, `Damage`, `Cooldown`, `NeedsLineOfSight`, `HitRoll`).
- Produces: `sealed class CombatArchetype : ScriptableObject` with `DisplayName`, `Role`, `Range`, `Damage`, `AttackInterval`; `internal static CombatArchetype Create(string displayName, CombatRole role, float range, int damage, float attackInterval)`. On `UnitAttacker`: `CombatArchetype Archetype`, `void ApplyArchetype(CombatArchetype preset)` (copies role, range, damage and interval; null throws `ArgumentNullException`), `bool HasLineOfSightToPoint(Vector3 point)` (from this unit's eye to the point; same blockers as `HasLineOfSight`), `internal float RollHit()` (the unit's replaceable 0..1 roll, shared with abilities). `UnitAttacker.Awake` applies an assigned archetype, so a serialized archetype wins over the inline fields; with none assigned nothing changes and `Initialize` keeps working. Serialized field name (the scene builder sets it): `archetype`.

- [ ] **Step 1: Write the failing tests**

`Assets/_Project/Tests/EditMode/CombatArchetypeTests.cs`:

```csharp
using System;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class CombatArchetypeTests
    {
        CombatArchetype melee;
        CombatArchetype ranged;
        CombatArchetype marksman;
        GameObject host;
        UnitAttacker attacker;

        [SetUp]
        public void SetUp()
        {
            melee = CombatArchetype.Create("Melee", CombatRole.Melee, 2f, 25, 1f);
            ranged = CombatArchetype.Create("Ranged", CombatRole.Ranged, 8f, 15, 1f);
            marksman = CombatArchetype.Create("Marksman", CombatRole.Ranged, 16f, 40, 2.5f);
            host = new GameObject("Attacker");
            attacker = host.AddComponent<UnitAttacker>();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(melee);
            Object.DestroyImmediate(ranged);
            Object.DestroyImmediate(marksman);
            Object.DestroyImmediate(host);
        }

        [Test]
        public void Create_ExposesTheConfiguredValues()
        {
            Assert.That(marksman.DisplayName, Is.EqualTo("Marksman"));
            Assert.That(marksman.Role, Is.EqualTo(CombatRole.Ranged));
            Assert.That(marksman.Range, Is.EqualTo(16f));
            Assert.That(marksman.Damage, Is.EqualTo(40));
            Assert.That(marksman.AttackInterval, Is.EqualTo(2.5f));
        }

        [Test]
        public void ApplyArchetype_CopiesTheValuesOntoTheAttacker()
        {
            attacker.ApplyArchetype(marksman);

            Assert.That(attacker.Archetype, Is.SameAs(marksman));
            Assert.That(attacker.Role, Is.EqualTo(CombatRole.Ranged));
            Assert.That(attacker.NeedsLineOfSight, Is.True);
            Assert.That(attacker.Range, Is.EqualTo(16f));
            Assert.That(attacker.Damage, Is.EqualTo(40));
            Assert.That(attacker.Cooldown, Is.EqualTo(2.5f));
        }

        [Test]
        public void TheThreeArchetypes_FeelDifferent_InRangeDamageIntervalAndSight()
        {
            Assert.That(melee.Range, Is.LessThan(ranged.Range));
            Assert.That(ranged.Range, Is.LessThan(marksman.Range));
            Assert.That(marksman.Damage, Is.GreaterThan(ranged.Damage));
            Assert.That(marksman.AttackInterval, Is.GreaterThan(ranged.AttackInterval));
            Assert.That(melee.Role, Is.EqualTo(CombatRole.Melee), "Melee needs no line of sight");
            Assert.That(ranged.Role, Is.EqualTo(CombatRole.Ranged));
            Assert.That(marksman.Role, Is.EqualTo(CombatRole.Ranged));

            attacker.ApplyArchetype(melee);
            Assert.That(attacker.NeedsLineOfSight, Is.False);
            attacker.ApplyArchetype(ranged);
            Assert.That(attacker.NeedsLineOfSight, Is.True);
        }

        [Test]
        public void ApplyArchetype_Null_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => attacker.ApplyArchetype(null));
        }

        [Test]
        public void WithoutAnArchetype_InitializeStillConfiguresTheAttacker()
        {
            attacker.Initialize(8f, 15, 1f, CombatRole.Ranged);

            Assert.That(attacker.Archetype, Is.Null);
            Assert.That(attacker.Range, Is.EqualTo(8f));
            Assert.That(attacker.Role, Is.EqualTo(CombatRole.Ranged));
        }

        [Test]
        public void Create_RejectsAnEmptyShape()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => CombatArchetype.Create("Nothing", CombatRole.Melee, 0f, 1, 1f));
            Assert.Throws<ArgumentOutOfRangeException>(() => CombatArchetype.Create("Nothing", CombatRole.Melee, 2f, -1, 1f));
            Assert.Throws<ArgumentOutOfRangeException>(() => CombatArchetype.Create("Nothing", CombatRole.Melee, 2f, 1, -1f));
        }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.CombatArchetypeTests"`
Expected: EXIT=1 with `error CS0246` for `CombatArchetype` and `error CS1061` for `ApplyArchetype`.

- [ ] **Step 3: Implement `CombatArchetype`**

`Assets/_Project/Scripts/Combat/CombatArchetype.cs`:

```csharp
using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// A weapon/combat preset as shared, immutable data: role (which decides line of sight and whether cover applies),
    /// range, damage and attack interval. A unit points at one from its UnitAttacker; there is no inventory or
    /// equipment. Holds no runtime state.
    /// </summary>
    [CreateAssetMenu(menuName = "Blackglass/Combat Archetype", fileName = "Archetype")]
    public sealed class CombatArchetype : ScriptableObject
    {
        [SerializeField] string displayName = "Archetype";
        [SerializeField] CombatRole role = CombatRole.Melee;
        [SerializeField, Min(0.1f)] float range = 2f;
        [SerializeField, Min(0)] int damage = 25;
        [SerializeField, Min(0f)] float attackInterval = 1f;

        public string DisplayName => displayName;
        public CombatRole Role => role;
        public float Range => range;
        public int Damage => damage;
        /// <summary>Seconds of scaled time between attacks.</summary>
        public float AttackInterval => attackInterval;

        internal static CombatArchetype Create(string displayName, CombatRole role, float range, int damage, float attackInterval)
        {
            if (range <= 0f)
                throw new ArgumentOutOfRangeException(nameof(range), range, "An archetype needs a positive range.");
            if (damage < 0)
                throw new ArgumentOutOfRangeException(nameof(damage), damage, "Damage cannot be negative.");
            if (attackInterval < 0f)
                throw new ArgumentOutOfRangeException(nameof(attackInterval), attackInterval, "An interval cannot be negative.");

            var archetype = CreateInstance<CombatArchetype>();
            archetype.displayName = displayName;
            archetype.role = role;
            archetype.range = range;
            archetype.damage = damage;
            archetype.attackInterval = attackInterval;
            return archetype;
        }
    }
}
```

- [ ] **Step 4: Extend `UnitAttacker`**

In `Assets/_Project/Scripts/Units/UnitAttacker.cs`:

1. Add the serialized field under `sightBlockers`:

```csharp
        // Optional preset. When assigned it overrides the inline role, range, damage and cooldown at Awake.
        [SerializeField] CombatArchetype archetype;
```

2. Add under `public CombatRole Role => role;`:

```csharp
        /// <summary>The assigned combat preset, or null when the unit is configured inline.</summary>
        public CombatArchetype Archetype => archetype;
```

3. Add after `Initialize(...)`:

```csharp
        void Awake()
        {
            if (archetype != null)
                ApplyArchetype(archetype);
        }

        /// <summary>Copies a preset's role, range, damage and attack interval onto this attacker.</summary>
        public void ApplyArchetype(CombatArchetype preset)
        {
            if (preset == null)
                throw new ArgumentNullException(nameof(preset));
            archetype = preset;
            role = preset.Role;
            range = preset.Range;
            damage = preset.Damage;
            cooldown = preset.AttackInterval;
        }

        /// <summary>The 0..1 roll the unit uses against a target's cover chance; abilities share it so tests can force it.</summary>
        internal float RollHit() => HitRoll();
```

4. Add after `HasLineOfSightFrom`:

```csharp
        /// <summary>True when the line from this unit's eye to the point crosses no world geometry (units never block).</summary>
        public bool HasLineOfSightToPoint(Vector3 point) =>
            LineOfSight.IsClear(transform.position + Vector3.up * LineOfSight.EyeHeight, point, sightBlockers, sightHits);
```

Update the class summary to mention: "An assigned CombatArchetype overrides the inline values."

- [ ] **Step 5: Run the tests**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.CombatArchetypeTests"` → EXIT=0, 6 tests. Then `Tools/run-tests.sh EditMode` → EXIT=0, total 474 + 6 = 480. Then `Tools/run-tests.sh PlayMode` → EXIT=0, 405 passed (Awake with no archetype is a no-op).

- [ ] **Step 6: Commit**

```bash
git add Assets/_Project/Scripts Assets/_Project/Tests/EditMode
git commit -m "Add combat archetypes: weapon presets applied to the unit's attacker

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 4: `UnitAbilities` — per-unit slots, cooldowns, validation and effects

**Files:**
- Create: `Assets/_Project/Scripts/Abilities/UnitAbilities.cs`
- Modify: `Assets/_Project/Tests/PlayMode/TestSupport/TestWorld.cs`
- Test: `Assets/_Project/Tests/PlayMode/UnitAbilitiesPlayModeTests.cs`, `Assets/_Project/Tests/PlayMode/UnitAbilitiesCoverPlayModeTests.cs`

**Interfaces:**
- Consumes: Tasks 1-3 (`Health.Heal`, `Encounter.OpponentsOf/AreHostile/AreAllied`, `AbilityDefinition`, `AbilityRules`, `AbilityFacts`, `AbilityCheckScope`, `AbilityFailure`, `AbilityCommand`, `UnitAttacker.HasLineOfSight/HasLineOfSightToPoint/RollHit`), `UnitCover.IsProtectedFrom/HitChance`, `CoverRules.ResolveHit/FlatDistance`.
- Produces:
  - `readonly struct AbilityCheck { AbilityFailure Failure; bool IsValid; float Distance; bool SightClear; bool TargetInCover; float HitChance }`.
  - `sealed class UnitAbilities : MonoBehaviour` (requires `UnitAttacker`): `int Count`; `AbilityDefinition Definition(int slot)` (null when out of range); `int IndexOf(AbilityDefinition)`; `float CooldownRemaining(int slot)`; `bool IsReady(int slot)`; `bool IsAlive`; `AbilityCheck Check(AbilityDefinition ability, Health target, Vector3? point, AbilityCheckScope scope = Full)`; `bool TryUse(AbilityCommand command)`; `bool CanStartNow(AbilityCommand)` and `bool CanQueue(AbilityCommand)` (both record the failure when they return false); `void ReportFailure(AbilityDefinition, AbilityFailure)`; `void CollectArea(AbilityDefinition ability, Vector3 center, List<Health> into)`; `AbilityFailure LastFailure`; `AbilityDefinition LastFailedAbility`; `float LastFailureTime` (`Time.unscaledTime`); `int UsedCount`; events `Used(AbilityDefinition)`, `Failed(AbilityDefinition, AbilityFailure)`, `Missed(AbilityDefinition, Health)`; `internal void Initialize(Encounter encounter, params AbilityDefinition[] definitions)`. Serialized field names (the scene builder sets them): `abilities`, `encounter`.
  - `TestWorld`: `UnitAbilities AddAbilities(CommandableUnit unit, Encounter encounter, params AbilityDefinition[] definitions)`, `AbilityDefinition CreateAimedShot()` (Unit/Hostile, range 14, LOS, cover applies, cooldown 6, damage 45), `CreateBlast()` (Ground, range 12, LOS, cover ignored, cooldown 10, damage 35, radius 3), `CreateMend()` (Unit/Friendly, range 8, no LOS, cooldown 8, heal 40). All three are tracked for cleanup.

- [ ] **Step 1: Add the `TestWorld` helpers**

In `Assets/_Project/Tests/PlayMode/TestSupport/TestWorld.cs`, after the line `public Encounter CreateEncounter() => Track(new GameObject("Encounter")).AddComponent<Encounter>();` add:

```csharp

        /// <summary>Adds UnitAbilities (wired to the encounter, which may be null, and the given definitions) to a unit.</summary>
        public UnitAbilities AddAbilities(CommandableUnit unit, Encounter encounter, params AbilityDefinition[] definitions)
        {
            var abilities = unit.gameObject.AddComponent<UnitAbilities>();
            abilities.Initialize(encounter, definitions);
            return abilities;
        }

        // The three prototype abilities with the shipped numbers, so scene and unit tests agree.
        public AbilityDefinition CreateAimedShot() => Track(AbilityDefinition.Create("Aimed Shot", AbilityTargetMode.Unit,
            AbilityTargetSide.Hostile, 14f, true, AbilityCoverRule.Applies, 6f, AbilityEffect.Damage, 45));

        public AbilityDefinition CreateBlast() => Track(AbilityDefinition.Create("Blast", AbilityTargetMode.Ground,
            AbilityTargetSide.Hostile, 12f, true, AbilityCoverRule.Ignored, 10f, AbilityEffect.Damage, 35, 3f));

        public AbilityDefinition CreateMend() => Track(AbilityDefinition.Create("Mend", AbilityTargetMode.Unit,
            AbilityTargetSide.Friendly, 8f, false, AbilityCoverRule.Ignored, 8f, AbilityEffect.Heal, 40));
```

(`Dispose` already destroys tracked objects with `Object.Destroy`, which is fine for ScriptableObjects.)

- [ ] **Step 2: Write the failing tests**

`Assets/_Project/Tests/PlayMode/UnitAbilitiesPlayModeTests.cs`:

```csharp
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class UnitAbilitiesPlayModeTests
    {
        TestWorld world;
        Encounter encounter;
        AbilityDefinition aimed;
        AbilityDefinition blast;
        AbilityDefinition mend;
        CommandableUnit caster;
        Health casterHealth;
        UnitAbilities abilities;
        CommandableUnit allyUnit;
        Health ally;
        Health hostile;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            // A 3 m tall wall west of the middle: the line from the caster (0, -6) to (-4, 2) or (-3, 0) crosses it.
            world.CreateEnvironment((new Vector3(-3f, 1.5f, -2f), new Vector3(4f, 3f, 0.5f)));
            encounter = world.CreateEncounter();
            aimed = world.CreateAimedShot();
            blast = world.CreateBlast();
            mend = world.CreateMend();
            caster = world.CreateFighter(new Vector3(0f, 0f, -6f));
            casterHealth = caster.GetComponent<Health>();
            abilities = world.AddAbilities(caster, encounter, aimed, blast, mend);
            allyUnit = world.CreateFighter(new Vector3(4f, 0f, -6f));
            ally = allyUnit.GetComponent<Health>();
            hostile = world.CreateDummy(new Vector3(3f, 0f, 2f));
            encounter.Initialize(new[] { casterHealth, ally }, new[] { hostile });
        }

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        [UnityTest]
        public IEnumerator Check_EverythingInOrder_IsValid_WithDistanceAndSightReported()
        {
            yield return null;

            var check = abilities.Check(aimed, hostile, null);

            Assert.That(check.Failure, Is.EqualTo(AbilityFailure.None));
            Assert.That(check.IsValid, Is.True);
            Assert.That(check.Distance, Is.EqualTo(Mathf.Sqrt(9f + 64f)).Within(0.01f));
            Assert.That(check.SightClear, Is.True);
            Assert.That(check.TargetInCover, Is.False);
            Assert.That(check.HitChance, Is.EqualTo(1f));
        }

        [UnityTest]
        public IEnumerator Check_ADeadCaster_CannotUseAnything()
        {
            yield return null;
            casterHealth.TakeDamage(casterHealth.Max);

            Assert.That(abilities.Check(aimed, hostile, null).Failure, Is.EqualTo(AbilityFailure.CasterDead));
            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(aimed, hostile)), Is.False);
            Assert.That(hostile.Current, Is.EqualTo(hostile.Max));
            Assert.That(abilities.LastFailure, Is.EqualTo(AbilityFailure.CasterDead));
        }

        [UnityTest]
        public IEnumerator Check_AnAbilityTheUnitDoesNotHave_IsUnknown()
        {
            yield return null;
            var foreign = world.CreateAimedShot();

            Assert.That(abilities.Check(foreign, hostile, null).Failure, Is.EqualTo(AbilityFailure.UnknownAbility));
            Assert.That(abilities.Check(null, hostile, null).Failure, Is.EqualTo(AbilityFailure.UnknownAbility));
        }

        [UnityTest]
        public IEnumerator Check_NoTarget_DeadTarget_AndWrongSide_AreRejected()
        {
            yield return null;
            Assert.That(abilities.Check(aimed, null, null).Failure, Is.EqualTo(AbilityFailure.NoTarget));
            Assert.That(abilities.Check(aimed, ally, null).Failure, Is.EqualTo(AbilityFailure.WrongSide), "An attack on a friendly");
            Assert.That(abilities.Check(mend, hostile, null).Failure, Is.EqualTo(AbilityFailure.WrongSide), "A heal on a hostile");

            hostile.TakeDamage(hostile.Max);
            Assert.That(abilities.Check(aimed, hostile, null).Failure, Is.EqualTo(AbilityFailure.TargetDead));
        }

        [UnityTest]
        public IEnumerator Check_ARangeAtTheEdgeIsInRange_AndAHairBeyondIsNot()
        {
            yield return null;
            var edge = world.CreateDummy(new Vector3(0f, 0f, 8f));       // exactly 14 m from the caster at z = -6
            var beyond = world.CreateDummy(new Vector3(6f, 0f, 8.01f));  // farther than 14 m, off the wall's line
            encounter.Initialize(new[] { casterHealth, ally }, new[] { hostile, edge, beyond });

            Assert.That(abilities.Check(aimed, edge, null).Failure, Is.EqualTo(AbilityFailure.None));
            Assert.That(abilities.Check(aimed, beyond, null).Failure, Is.EqualTo(AbilityFailure.OutOfRange));
        }

        [UnityTest]
        public IEnumerator Check_AWallBlocksAnAbilityThatNeedsSight_ButNotOneThatDoesNot()
        {
            yield return null;
            var walledHostile = world.CreateDummy(new Vector3(-4f, 0f, 2f));
            var walledAllyUnit = world.CreateFighter(new Vector3(-3f, 0f, 0f));
            var walledAlly = walledAllyUnit.GetComponent<Health>();
            encounter.Initialize(new[] { casterHealth, ally, walledAlly }, new[] { hostile, walledHostile });
            yield return null;

            var blocked = abilities.Check(aimed, walledHostile, null);
            Assert.That(blocked.Failure, Is.EqualTo(AbilityFailure.NoLineOfSight));
            Assert.That(blocked.SightClear, Is.False);
            Assert.That(abilities.Check(mend, walledAlly, null).Failure, Is.EqualTo(AbilityFailure.None), "Mend ignores walls");
            Assert.That(abilities.Check(blast, null, new Vector3(-3f, 0f, 0f)).Failure, Is.EqualTo(AbilityFailure.NoLineOfSight),
                "A blast needs a clear line to its point");
            Assert.That(abilities.Check(blast, null, new Vector3(3f, 0f, 2f)).Failure, Is.EqualTo(AbilityFailure.None));
        }

        [UnityTest]
        public IEnumerator Check_AGroundAbilityWithoutAPoint_HasNoPosition()
        {
            yield return null;
            Assert.That(abilities.Check(blast, null, null).Failure, Is.EqualTo(AbilityFailure.NoPosition));
        }

        [UnityTest]
        public IEnumerator Check_TheStaticScope_SkipsRangeSightAndCooldown_ButNotWhoOrWhat()
        {
            yield return null;
            var far = world.CreateDummy(new Vector3(15f, 0f, 15f));
            encounter.Initialize(new[] { casterHealth, ally }, new[] { hostile, far });

            Assert.That(abilities.Check(aimed, far, null, AbilityCheckScope.Full).Failure, Is.EqualTo(AbilityFailure.OutOfRange));
            Assert.That(abilities.Check(aimed, far, null, AbilityCheckScope.Static).Failure, Is.EqualTo(AbilityFailure.None));
            Assert.That(abilities.Check(aimed, ally, null, AbilityCheckScope.Static).Failure, Is.EqualTo(AbilityFailure.WrongSide));
        }

        [UnityTest]
        public IEnumerator TryUse_SingleTargetDamage_AppliesTheAmount_AndStartsTheCooldown()
        {
            yield return null;
            var used = 0;
            abilities.Used += _ => used++;

            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(aimed, hostile)), Is.True);

            Assert.That(hostile.Current, Is.EqualTo(hostile.Max - 45));
            Assert.That(abilities.CooldownRemaining(0), Is.EqualTo(6f).Within(0.1f));
            Assert.That(abilities.IsReady(0), Is.False);
            Assert.That(abilities.IsReady(1), Is.True, "Other slots are unaffected");
            Assert.That(used, Is.EqualTo(1));
            Assert.That(abilities.UsedCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator TryUse_AFailure_RecordsTheReason_AndStartsNoCooldown()
        {
            yield return null;
            var reported = AbilityFailure.None;
            abilities.Failed += (_, failure) => reported = failure;

            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(aimed, ally)), Is.False);

            Assert.That(abilities.LastFailure, Is.EqualTo(AbilityFailure.WrongSide));
            Assert.That(abilities.LastFailedAbility, Is.SameAs(aimed));
            Assert.That(reported, Is.EqualTo(AbilityFailure.WrongSide));
            Assert.That(abilities.CooldownRemaining(0), Is.EqualTo(0f));
            Assert.That(ally.Current, Is.EqualTo(ally.Max));
        }

        [UnityTest]
        public IEnumerator TryUse_WhileOnCooldown_IsRefused()
        {
            yield return null;
            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(aimed, hostile)), Is.True);

            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(aimed, hostile)), Is.False);
            Assert.That(abilities.LastFailure, Is.EqualTo(AbilityFailure.OnCooldown));
            Assert.That(hostile.Current, Is.EqualTo(hostile.Max - 45), "No second hit");
        }

        [UnityTest]
        public IEnumerator Cooldowns_ArePerUnit_EvenWhenTwoUnitsShareOneDefinition()
        {
            yield return null;
            var secondUnit = world.CreateFighter(new Vector3(2f, 0f, -6f));
            var second = world.AddAbilities(secondUnit, encounter, aimed);
            encounter.Initialize(new[] { casterHealth, ally, secondUnit.GetComponent<Health>() }, new[] { hostile });

            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(aimed, hostile)), Is.True);

            Assert.That(abilities.IsReady(0), Is.False);
            Assert.That(second.IsReady(0), Is.True, "The second unit's cooldown is its own");
            Assert.That(second.TryUse(AbilityCommand.OnUnit(aimed, hostile)), Is.True);
            Assert.That(hostile.Current, Is.EqualTo(hostile.Max - 90));
        }

        [UnityTest]
        public IEnumerator Cooldown_FreezesWhilePaused_AndRunsAgainAfterResume()
        {
            yield return null;
            var pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(aimed, hostile)), Is.True);

            pause.Pause();
            var frozen = abilities.CooldownRemaining(0);
            yield return new WaitForSecondsRealtime(0.5f);
            Assert.That(abilities.CooldownRemaining(0), Is.EqualTo(frozen).Within(0.001f), "The cooldown must not run while paused");

            pause.Resume();
            yield return new WaitForSeconds(0.5f);
            Assert.That(abilities.CooldownRemaining(0), Is.LessThan(frozen - 0.3f), "The cooldown runs again after the pause");
        }

        [UnityTest]
        public IEnumerator Mend_HealsAnAlly_ClampedAtMax()
        {
            yield return null;
            ally.TakeDamage(60);
            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(mend, ally)), Is.True);
            Assert.That(ally.Current, Is.EqualTo(ally.Max - 20), "60 missing, 40 restored");

            var secondUnit = world.CreateFighter(new Vector3(2f, 0f, -6f));
            var second = world.AddAbilities(secondUnit, encounter, mend);
            encounter.Initialize(new[] { casterHealth, ally, secondUnit.GetComponent<Health>() }, new[] { hostile });
            Assert.That(second.TryUse(AbilityCommand.OnUnit(mend, ally)), Is.True);

            Assert.That(ally.Current, Is.EqualTo(ally.Max), "Only the 20 that were missing");
        }

        [UnityTest]
        public IEnumerator Mend_OnTheCasterItself_IsAllowed()
        {
            yield return null;
            casterHealth.TakeDamage(50);

            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(mend, casterHealth)), Is.True);

            Assert.That(casterHealth.Current, Is.EqualTo(casterHealth.Max - 10));
        }

        [UnityTest]
        public IEnumerator Mend_OnADeadAlly_IsRefused()
        {
            yield return null;
            ally.TakeDamage(ally.Max);

            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(mend, ally)), Is.False);
            Assert.That(abilities.LastFailure, Is.EqualTo(AbilityFailure.TargetDead));
            Assert.That(ally.IsAlive, Is.False);
        }

        [UnityTest]
        public IEnumerator Blast_HitsOnlyHostilesInsideTheRadius_EdgeIncluded_NeverFriendliesOrTheCaster()
        {
            yield return null;
            var center = new Vector3(0f, 0f, 4f);
            var inside = world.CreateDummy(center);
            var onTheEdge = world.CreateDummy(new Vector3(3f, 0f, 4f));
            var outside = world.CreateDummy(new Vector3(3.1f, 0f, 4f));
            var friendlyInside = world.CreateFighter(new Vector3(1f, 0f, 4f));
            var friendlyHealth = friendlyInside.GetComponent<Health>();
            encounter.Initialize(new[] { casterHealth, ally, friendlyHealth }, new[] { hostile, inside, onTheEdge, outside });
            yield return null;

            Assert.That(abilities.TryUse(AbilityCommand.AtGround(blast, center)), Is.True);

            Assert.That(inside.Current, Is.EqualTo(inside.Max - 35));
            Assert.That(onTheEdge.Current, Is.EqualTo(onTheEdge.Max - 35), "A unit exactly on the radius is hit");
            Assert.That(outside.Current, Is.EqualTo(outside.Max));
            Assert.That(hostile.Current, Is.EqualTo(hostile.Max), "3.6 m from the blast point");
            Assert.That(friendlyHealth.Current, Is.EqualTo(friendlyHealth.Max), "No friendly fire");
            Assert.That(ally.Current, Is.EqualTo(ally.Max));
            Assert.That(casterHealth.Current, Is.EqualTo(casterHealth.Max));
            Assert.That(abilities.CooldownRemaining(1), Is.EqualTo(10f).Within(0.1f));
        }

        [UnityTest]
        public IEnumerator Blast_CentredOnTheCaster_NeverHurtsTheCaster_OrItsSide()
        {
            yield return null;
            var near = world.CreateDummy(new Vector3(2f, 0f, -6f));
            encounter.Initialize(new[] { casterHealth, ally }, new[] { hostile, near });
            yield return null;

            Assert.That(abilities.TryUse(AbilityCommand.AtGround(blast, new Vector3(0f, 0f, -6f))), Is.True);

            Assert.That(near.Current, Is.EqualTo(near.Max - 35));
            Assert.That(casterHealth.Current, Is.EqualTo(casterHealth.Max));
            Assert.That(ally.Current, Is.EqualTo(ally.Max));
        }

        [UnityTest]
        public IEnumerator Blast_WithNobodyInTheRadius_StillSucceeds_AndStartsTheCooldown()
        {
            yield return null;

            Assert.That(abilities.TryUse(AbilityCommand.AtGround(blast, new Vector3(-8f, 0f, -12f))), Is.True);

            Assert.That(abilities.UsedCount, Is.EqualTo(1));
            Assert.That(abilities.IsReady(1), Is.False);
        }

        [UnityTest]
        public IEnumerator Blast_IgnoresDeadAndDeactivatedHostiles()
        {
            yield return null;
            var center = new Vector3(0f, 0f, 4f);
            var alive = world.CreateDummy(center);
            var dead = world.CreateDummy(new Vector3(1f, 0f, 4f));
            encounter.Initialize(new[] { casterHealth, ally }, new[] { hostile, alive, dead });
            dead.TakeDamage(dead.Max);
            yield return null;

            Assert.That(() => abilities.TryUse(AbilityCommand.AtGround(blast, center)), Throws.Nothing);

            Assert.That(alive.Current, Is.EqualTo(alive.Max - 35));
            Assert.That(dead.IsAlive, Is.False);
            var area = new List<Health>();
            abilities.CollectArea(blast, center, area);
            Assert.That(area, Is.EqualTo(new[] { alive }));
        }

        [UnityTest]
        public IEnumerator Blast_OutOfRange_AndBehindAWall_AreRefused_WithoutACooldown()
        {
            yield return null;

            Assert.That(abilities.TryUse(AbilityCommand.AtGround(blast, new Vector3(0f, 0f, 7f))), Is.False, "13 m, range 12");
            Assert.That(abilities.LastFailure, Is.EqualTo(AbilityFailure.OutOfRange));
            Assert.That(abilities.TryUse(AbilityCommand.AtGround(blast, new Vector3(-3f, 0f, 0f))), Is.False);
            Assert.That(abilities.LastFailure, Is.EqualTo(AbilityFailure.NoLineOfSight));
            Assert.That(abilities.CooldownRemaining(1), Is.EqualTo(0f));
        }

        [UnityTest]
        public IEnumerator AUnitWithNoEncounter_FailsClosed_WithoutAnException()
        {
            yield return null;
            var loner = world.CreateFighter(new Vector3(-6f, 0f, -6f));
            var lonerHealth = loner.GetComponent<Health>();
            var lonerAbilities = world.AddAbilities(loner, null, aimed, blast, mend);

            Assert.That(lonerAbilities.Check(aimed, hostile, null).Failure, Is.EqualTo(AbilityFailure.WrongSide));
            Assert.That(lonerAbilities.Check(mend, ally, null).Failure, Is.EqualTo(AbilityFailure.WrongSide));
            Assert.That(lonerAbilities.Check(mend, lonerHealth, null).Failure, Is.EqualTo(AbilityFailure.None), "It can still mend itself");
            Assert.That(() => lonerAbilities.TryUse(AbilityCommand.AtGround(blast, new Vector3(0f, 0f, 4f))), Throws.Nothing);
            Assert.That(hostile.Current, Is.EqualTo(hostile.Max));
        }

        [UnityTest]
        public IEnumerator CanQueue_ChecksOnlyWhoAndWhat_AndCanStartNow_ChecksEverything()
        {
            yield return null;
            var far = world.CreateDummy(new Vector3(15f, 0f, 15f));
            encounter.Initialize(new[] { casterHealth, ally }, new[] { hostile, far });

            Assert.That(abilities.CanQueue(AbilityCommand.OnUnit(aimed, far)), Is.True);
            Assert.That(abilities.CanStartNow(AbilityCommand.OnUnit(aimed, far)), Is.False);
            Assert.That(abilities.LastFailure, Is.EqualTo(AbilityFailure.OutOfRange));
            Assert.That(abilities.CanQueue(AbilityCommand.OnUnit(aimed, ally)), Is.False);
            Assert.That(abilities.LastFailure, Is.EqualTo(AbilityFailure.WrongSide));
        }

        [UnityTest]
        public IEnumerator TheCasterFacesItsAim_WhenItUsesAnAbility()
        {
            yield return null;
            caster.transform.rotation = Quaternion.LookRotation(Vector3.back);

            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(aimed, hostile)), Is.True);

            var toTarget = hostile.transform.position - caster.transform.position;
            toTarget.y = 0f;
            Assert.That(Vector3.Angle(caster.transform.forward, toTarget), Is.LessThan(1f));
        }
    }
}
```

`Assets/_Project/Tests/PlayMode/UnitAbilitiesCoverPlayModeTests.cs`:

```csharp
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class UnitAbilitiesCoverPlayModeTests
    {
        TestWorld world;
        CoverLocation point;
        CoverRegistry registry;
        Encounter encounter;
        AbilityDefinition aimed;
        AbilityDefinition blast;
        CommandableUnit shooter;
        UnitAbilities abilities;
        CommandableUnit defender;
        Health defenderHealth;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            // The CoverCombatPlayModeTests geometry: a 0.9 m wall from z -0.25 to 0.25; the point 0.75 m south of it. A
            // shooter north of the wall sees over it while its eye-to-feet ray crosses it.
            var environment = world.CreateEnvironment((new Vector3(0f, 0.45f, 0f), new Vector3(4f, 0.9f, 0.5f)));
            point = world.CreateCoverPoint(new Vector3(0f, 0f, -1f), Vector3.forward, TestWorld.ObstacleCollider(environment));
            registry = world.CreateRegistry(point);
            encounter = world.CreateEncounter();
            aimed = world.CreateAimedShot();
            blast = world.CreateBlast();
            defender = world.CreateFighter(new Vector3(0f, 0f, -3f), registry: registry);
            defenderHealth = defender.GetComponent<Health>();
            shooter = world.CreateFighter(new Vector3(0f, 0f, 5f));
            abilities = world.AddAbilities(shooter, encounter, aimed, blast);
            encounter.Initialize(new[] { shooter.GetComponent<Health>() }, new[] { defenderHealth });
        }

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        IEnumerator PutDefenderInCover()
        {
            yield return null;
            Assert.That(defender.Issue(new MoveToCoverCommand(point)), Is.True);
            yield return TestWorld.WaitUntil(() => defender.Cover.Status == CoverStatus.Occupied, 10f);
            Assert.That(defender.Cover.Status, Is.EqualTo(CoverStatus.Occupied), "Precondition: the defender is in cover");
            yield return new WaitForFixedUpdate();
        }

        [UnityTest]
        public IEnumerator Check_ReportsACoveredTarget_AndItsHitChance()
        {
            yield return PutDefenderInCover();

            var check = abilities.Check(aimed, defenderHealth, null);

            Assert.That(check.Failure, Is.EqualTo(AbilityFailure.None));
            Assert.That(check.TargetInCover, Is.True);
            Assert.That(check.HitChance, Is.EqualTo(0.5f));
        }

        [UnityTest]
        public IEnumerator AimedShot_AtACoveredTarget_MissesOnAHighRoll_ButStillSpendsTheCooldown()
        {
            yield return PutDefenderInCover();
            shooter.GetComponent<UnitAttacker>().HitRoll = () => 0.99f;
            var missed = 0;
            abilities.Missed += (_, __) => missed++;

            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(aimed, defenderHealth)), Is.True);

            Assert.That(defenderHealth.Current, Is.EqualTo(defenderHealth.Max), "Cover turned the shot away");
            Assert.That(missed, Is.EqualTo(1));
            Assert.That(abilities.IsReady(0), Is.False, "A missed shot still spends the cooldown");
            Assert.That(abilities.UsedCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator AimedShot_AtACoveredTarget_LandsOnALowRoll()
        {
            yield return PutDefenderInCover();
            shooter.GetComponent<UnitAttacker>().HitRoll = () => 0f;

            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(aimed, defenderHealth)), Is.True);

            Assert.That(defenderHealth.Current, Is.EqualTo(defenderHealth.Max - 45));
        }

        [UnityTest]
        public IEnumerator AimedShot_AtAnExposedTarget_AlwaysLands_WhateverTheRoll()
        {
            yield return null;
            shooter.GetComponent<UnitAttacker>().HitRoll = () => 0.99f;

            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(aimed, defenderHealth)), Is.True);

            Assert.That(defenderHealth.Current, Is.EqualTo(defenderHealth.Max - 45));
        }

        [UnityTest]
        public IEnumerator Blast_IgnoresCover_AndAlwaysLands()
        {
            yield return PutDefenderInCover();
            shooter.GetComponent<UnitAttacker>().HitRoll = () => 0.99f;

            Assert.That(abilities.TryUse(AbilityCommand.AtGround(blast, point.Position)), Is.True);

            Assert.That(defenderHealth.Current, Is.EqualTo(defenderHealth.Max - 35), "The blast's cover rule is Ignored");
        }
    }
}
```

- [ ] **Step 3: Run to verify they fail to compile**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.UnitAbilitiesPlayModeTests"`
Expected: EXIT=1 with `error CS0246` for `UnitAbilities` / `AbilityCheck`.

- [ ] **Step 4: Implement `UnitAbilities`**

`Assets/_Project/Scripts/Abilities/UnitAbilities.cs`:

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>What a validation found: the first failure (None when the ability can be used) and the numbers previews show.</summary>
    public readonly struct AbilityCheck
    {
        public AbilityCheck(AbilityFailure failure, float distance, bool sightClear, bool targetInCover, float hitChance)
        {
            Failure = failure;
            Distance = distance;
            SightClear = sightClear;
            TargetInCover = targetInCover;
            HitChance = hitChance;
        }

        public AbilityFailure Failure { get; }
        public bool IsValid => Failure == AbilityFailure.None;
        /// <summary>Flat distance from the caster to the target or point; 0 when there is no aim yet.</summary>
        public float Distance { get; }
        /// <summary>Whether the line from the caster's eye to the target (or a unit standing at the point) is clear.</summary>
        public bool SightClear { get; }
        /// <summary>The ability treats cover as applying and the target holds cover that protects it from the caster.</summary>
        public bool TargetInCover { get; }
        /// <summary>The cover's chance to be hit when TargetInCover, else 1.</summary>
        public float HitChance { get; }
    }

    /// <summary>
    /// A unit's abilities: up to four definitions (shared, immutable data) and, per slot, the scaled time at which the
    /// slot is ready again. Cooldowns therefore freeze while the game is paused and belong to the unit, so they survive
    /// control-mode changes and character switching, and two units sharing one definition never share a cooldown. Check
    /// is the single validation (AbilityRules plus the facts gathered here); TryUse validates and then applies the effect
    /// through Health, with cover and line of sight from the same systems basic attacks use. Instant: it does not wait
    /// for the pause service, whoever calls it (CommandableUnit) decides when. Reports failures through LastFailure and
    /// events, never the Console.
    /// </summary>
    [RequireComponent(typeof(UnitAttacker))]
    public sealed class UnitAbilities : MonoBehaviour
    {
        // A blast point is on the ground; sight to it is judged as if a unit stood there (its pivot is 1 m up).
        const float GroundAimHeight = 1f;

        [SerializeField] List<AbilityDefinition> abilities = new List<AbilityDefinition>();
        // Who is on which side. Without it nothing is hostile and only the caster itself counts as friendly.
        [SerializeField] Encounter encounter;

        readonly List<float> readyAt = new List<float>();
        readonly List<Health> areaBuffer = new List<Health>();
        Health ownHealth;
        UnitAttacker attacker;

        public int Count => abilities.Count;

        /// <summary>The definition in a slot, or null for an empty or out-of-range slot.</summary>
        public AbilityDefinition Definition(int slot) => slot >= 0 && slot < abilities.Count ? abilities[slot] : null;

        /// <summary>The slot holding this definition, or -1.</summary>
        public int IndexOf(AbilityDefinition ability) => ability == null ? -1 : abilities.IndexOf(ability);

        /// <summary>False once this unit's Health (if it has one) has died.</summary>
        public bool IsAlive => OwnHealth == null || OwnHealth.IsAlive;

        /// <summary>Seconds of scaled time until the slot is ready; 0 when ready or when there is no such slot.</summary>
        public float CooldownRemaining(int slot)
        {
            if (slot < 0 || slot >= abilities.Count)
                return 0f;
            EnsureSlots();
            return Mathf.Max(0f, readyAt[slot] - Time.time);
        }

        public bool IsReady(int slot) => CooldownRemaining(slot) <= 0f;

        /// <summary>The reason of the most recent refusal or failure (None before any).</summary>
        public AbilityFailure LastFailure { get; private set; }

        public AbilityDefinition LastFailedAbility { get; private set; }

        /// <summary>Unscaled time of the last failure, so the debug HUD can show it for a few seconds even while paused.</summary>
        public float LastFailureTime { get; private set; }

        /// <summary>Successful uses since creation (debug counter).</summary>
        public int UsedCount { get; private set; }

        /// <summary>Raised after an ability was used (also when cover turned its single shot away).</summary>
        public event Action<AbilityDefinition> Used;

        /// <summary>Raised whenever a use or an order was refused or failed, with the reason.</summary>
        public event Action<AbilityDefinition, AbilityFailure> Failed;

        /// <summary>Raised when cover turned a unit-targeted or area hit away.</summary>
        public event Action<AbilityDefinition, Health> Missed;

        Health OwnHealth => ownHealth != null ? ownHealth : ownHealth = GetComponent<Health>();
        UnitAttacker Attacker => attacker != null ? attacker : attacker = GetComponent<UnitAttacker>();

        internal void Initialize(Encounter currentEncounter, params AbilityDefinition[] definitions)
        {
            encounter = currentEncounter;
            abilities.Clear();
            abilities.AddRange(definitions);
            readyAt.Clear();
        }

        /// <summary>
        /// Validates one use. Unit abilities take a target and no point; ground abilities take a point (null = none yet)
        /// and no target. Full checks everything; Static only the checks that do not depend on where the caster will
        /// be (for an order queued behind others). The reported distance, sight and cover are filled in whenever there
        /// is something to measure, so a preview can show them even for a refused use.
        /// </summary>
        public AbilityCheck Check(AbilityDefinition ability, Health target, Vector3? point, AbilityCheckScope scope = AbilityCheckScope.Full)
        {
            var slot = IndexOf(ability);
            var unitMode = ability != null && ability.TargetMode == AbilityTargetMode.Unit;
            var hasTarget = unitMode && target != null;
            var hasPosition = ability != null && !unitMode && point.HasValue;
            var aim = hasTarget ? target.transform.position : hasPosition ? point.Value : transform.position;
            var distance = hasTarget || hasPosition ? CoverRules.FlatDistance(transform.position, aim) : 0f;

            var sightClear = true;
            var inCover = false;
            var hitChance = 1f;
            if (hasTarget)
            {
                sightClear = Attacker.HasLineOfSight(target);
                inCover = ability.CoverRule == AbilityCoverRule.Applies && IsCovered(target, transform.position, out hitChance);
            }
            else if (hasPosition)
            {
                sightClear = Attacker.HasLineOfSightToPoint(aim + Vector3.up * GroundAimHeight);
            }

            var facts = new AbilityFacts(IsAlive, slot >= 0, hasTarget, hasTarget && IsActive(target),
                hasTarget && IsOnRequiredSide(ability, target), hasPosition, CooldownRemaining(slot), distance);
            var failure = AbilityRules.CheckBasics(ability, facts, scope);
            if (failure == AbilityFailure.None && scope == AbilityCheckScope.Full)
                failure = AbilityRules.CheckSight(ability, sightClear);
            return new AbilityCheck(failure, distance, sightClear, inCover, hitChance);
        }

        /// <summary>
        /// Validates in full and, when valid, uses the ability: faces the aim, starts the cooldown, applies the effect.
        /// A refusal records its reason (LastFailure, Failed) and costs nothing. Returns whether it was used.
        /// </summary>
        public bool TryUse(AbilityCommand command)
        {
            if (command == null)
                throw new ArgumentNullException(nameof(command));
            var ability = command.Definition;
            var check = CheckCommand(command, AbilityCheckScope.Full);
            if (!check.IsValid)
            {
                RecordFailure(ability, check.Failure);
                return false;
            }

            var aim = command.AimPoint;
            FaceTowards(aim);
            EnsureSlots();
            readyAt[abilities.IndexOf(ability)] = Time.time + ability.Cooldown;
            Apply(ability, command.Target, aim);
            UsedCount++;
            Used?.Invoke(ability);
            return true;
        }

        /// <summary>The order would start now: the full check passes. Records the reason when it does not.</summary>
        public bool CanStartNow(AbilityCommand command) => Accept(command, AbilityCheckScope.Full);

        /// <summary>The order may wait behind others: only who and what are checked. Records the reason when it fails.</summary>
        public bool CanQueue(AbilityCommand command) => Accept(command, AbilityCheckScope.Static);

        /// <summary>Records a failure found outside this class (a click on nothing), so it shows like any other.</summary>
        public void ReportFailure(AbilityDefinition ability, AbilityFailure failure) => RecordFailure(ability, failure);

        /// <summary>
        /// The living, active units hostile to this unit whose pivot is inside the blast radius at `center`. Used by the
        /// effect and by previews, so both always agree. Empty without an encounter.
        /// </summary>
        public void CollectArea(AbilityDefinition ability, Vector3 center, List<Health> into)
        {
            into.Clear();
            if (ability == null || encounter == null)
                return;
            var opponents = encounter.OpponentsOf(OwnHealth);
            for (var i = 0; i < opponents.Count; i++)
            {
                var candidate = opponents[i];
                if (candidate != null && IsActive(candidate) && AbilityRules.IsInArea(center, candidate.transform.position, ability.Radius))
                    into.Add(candidate);
            }
        }

        AbilityCheck CheckCommand(AbilityCommand command, AbilityCheckScope scope)
        {
            var ability = command.Definition;
            var unitMode = ability.TargetMode == AbilityTargetMode.Unit;
            return Check(ability, unitMode ? command.Target : null, unitMode ? (Vector3?)null : command.Point, scope);
        }

        bool Accept(AbilityCommand command, AbilityCheckScope scope)
        {
            if (command == null)
                throw new ArgumentNullException(nameof(command));
            var check = CheckCommand(command, scope);
            if (check.IsValid)
                return true;
            RecordFailure(command.Definition, check.Failure);
            return false;
        }

        void Apply(AbilityDefinition ability, Health target, Vector3 aim)
        {
            var source = OwnHealth;
            switch (ability.Effect)
            {
                case AbilityEffect.Heal:
                    target.Heal(ability.Amount);
                    break;
                case AbilityEffect.Damage when ability.TargetMode == AbilityTargetMode.Unit:
                    Hit(ability, target, transform.position, source);
                    break;
                case AbilityEffect.Damage:
                    // A snapshot first: a victim dying (and deactivating) mid-blast must not change who else is hit.
                    CollectArea(ability, aim, areaBuffer);
                    for (var i = 0; i < areaBuffer.Count; i++)
                        Hit(ability, areaBuffer[i], aim, source);
                    break;
            }
        }

        // The effect on one unit: cover (when the ability says it applies) may turn it away; otherwise it takes the damage.
        void Hit(AbilityDefinition ability, Health victim, Vector3 from, Health source)
        {
            var lands = true;
            if (ability.CoverRule == AbilityCoverRule.Applies && victim.TryGetComponent<UnitCover>(out var cover)
                && cover.IsProtectedFrom(from))
                lands = CoverRules.ResolveHit(true, cover.HitChance, Attacker.RollHit());
            if (!lands)
            {
                Missed?.Invoke(ability, victim);
                return;
            }
            victim.TakeDamage(ability.Amount, source);
        }

        static bool IsCovered(Health target, Vector3 from, out float hitChance)
        {
            hitChance = 1f;
            if (!target.TryGetComponent<UnitCover>(out var cover) || !cover.IsProtectedFrom(from))
                return false;
            hitChance = cover.HitChance;
            return true;
        }

        static bool IsActive(Health unit) => unit.IsAlive && unit.gameObject.activeInHierarchy;

        // Hostile: opposite sides. Friendly: the same side, or the caster itself (also without an encounter).
        bool IsOnRequiredSide(AbilityDefinition ability, Health target)
        {
            var self = OwnHealth;
            if (ability.TargetSide == AbilityTargetSide.Friendly)
                return (self != null && target == self) || (encounter != null && encounter.AreAllied(self, target));
            return encounter != null && encounter.AreHostile(self, target);
        }

        void RecordFailure(AbilityDefinition ability, AbilityFailure failure)
        {
            LastFailure = failure;
            LastFailedAbility = ability;
            LastFailureTime = Time.unscaledTime;
            Failed?.Invoke(ability, failure);
        }

        void EnsureSlots()
        {
            while (readyAt.Count < abilities.Count)
                readyAt.Add(0f);
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

- [ ] **Step 5: Run the tests**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.UnitAbilitiesPlayModeTests"` then `Tools/run-tests.sh PlayMode "Blackglass.Tests.UnitAbilitiesCoverPlayModeTests"`
Expected: EXIT=0; 24 and 5 tests pass. If a probe fails for an environmental reason (a position blocked, a distance off by a hair), adjust the **test position** and say so in your report; if the failure is in `UnitAbilities`, fix the code.
Then `Tools/run-tests.sh PlayMode` → EXIT=0, total 405 + 29 = 434; `Tools/run-tests.sh EditMode` → 480 unchanged.

- [ ] **Step 6: Commit**

```bash
git add Assets/_Project/Scripts Assets/_Project/Tests/PlayMode
git commit -m "Add per-unit abilities: slots, cooldowns on scaled time, one validation, effects through Health

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Abilities in the command queue (`CommandableUnit`)

**Files:**
- Modify: `Assets/_Project/Scripts/Units/CommandableUnit.cs`
- Test: `Assets/_Project/Tests/PlayMode/CommandableUnitAbilityPlayModeTests.cs`, `Assets/_Project/Tests/PlayMode/CommandableUnitAbilityCoverPlayModeTests.cs`

**Interfaces:**
- Consumes: `AbilityCommand`, `UnitAbilities` (`CanStartNow`, `CanQueue`, `TryUse`, `LastFailure`, `Used`), `AbilityCheckScope`, the existing `CommandQueue`/`StartNext`/`StopAll`.
- Produces: `CommandableUnit.Issue(AbilityCommand, mode)` behaviour: returns false (orders unchanged, reason in the unit's `UnitAbilities.LastFailure`) for a dead caster, a unit with no `UnitAbilities`, an ability it does not have, a dead/wrong-side target, and (only when the order would start now) a cooldown, range or sight failure. Appended behind other orders only the static checks apply. An ability command becomes current without doing anything; on the first **running** frame (never while paused) `CommandableUnit.Update` calls `UnitAbilities.TryUse` (before the move-intent branch), then moves on with `StartNext` whether it succeeded or failed.

- [ ] **Step 1: Write the failing tests**

`Assets/_Project/Tests/PlayMode/CommandableUnitAbilityPlayModeTests.cs`:

```csharp
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CommandableUnitAbilityPlayModeTests
    {
        TestWorld world;
        TacticalPause pause;
        Encounter encounter;
        AbilityDefinition aimed;
        AbilityDefinition blast;
        AbilityDefinition mend;
        CommandableUnit caster;
        Health casterHealth;
        UnitAbilities abilities;
        Health ally;
        Health hostile;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            world.CreateEnvironment((new Vector3(-3f, 1.5f, -2f), new Vector3(4f, 3f, 0.5f)));
            pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            encounter = world.CreateEncounter();
            aimed = world.CreateAimedShot();
            blast = world.CreateBlast();
            mend = world.CreateMend();
            caster = world.CreateFighter(new Vector3(0f, 0f, -6f));
            casterHealth = caster.GetComponent<Health>();
            abilities = world.AddAbilities(caster, encounter, aimed, blast, mend);
            ally = world.CreateFighter(new Vector3(4f, 0f, -6f)).GetComponent<Health>();
            hostile = world.CreateDummy(new Vector3(3f, 0f, 2f));
            encounter.Initialize(new[] { casterHealth, ally }, new[] { hostile });
        }

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        IEnumerator WaitUntilIdle() => TestWorld.WaitUntil(() => caster.CurrentCommand == null, 15f);

        [UnityTest]
        public IEnumerator AnAbilityOnAnIdleUnit_RunsOnTheNextFrame_AndTheUnitIsIdleAgain()
        {
            yield return null;

            Assert.That(caster.Issue(AbilityCommand.OnUnit(aimed, hostile)), Is.True);
            Assert.That(caster.CurrentCommand, Is.TypeOf<AbilityCommand>(), "Accepted and waiting for the next running frame");
            yield return WaitUntilIdle();

            Assert.That(caster.CurrentCommand, Is.Null);
            Assert.That(hostile.Current, Is.EqualTo(hostile.Max - 45));
            Assert.That(abilities.IsReady(0), Is.False);
        }

        [UnityTest]
        public IEnumerator AnAbilityIssuedWhilePaused_IsAccepted_ButNothingRunsUntilTheGameResumes()
        {
            yield return null;
            pause.Pause();

            Assert.That(caster.Issue(AbilityCommand.OnUnit(aimed, hostile)), Is.True);
            yield return new WaitForSecondsRealtime(0.3f);

            Assert.That(hostile.Current, Is.EqualTo(hostile.Max), "Nothing may run while paused");
            Assert.That(caster.CurrentCommand, Is.TypeOf<AbilityCommand>());
            Assert.That(abilities.IsReady(0), Is.True, "No cooldown started");

            pause.Resume();
            yield return WaitUntilIdle();
            Assert.That(hostile.Current, Is.EqualTo(hostile.Max - 45));
        }

        [UnityTest]
        public IEnumerator AnAbilityThatCannotStartNow_IsRejectedWithItsReason_AndTheOrdersAreUnchanged()
        {
            yield return null;
            var far = world.CreateDummy(new Vector3(15f, 0f, 15f));
            encounter.Initialize(new[] { casterHealth, ally }, new[] { hostile, far });
            var move = new MoveCommand(new Vector3(-6f, 0f, -6f));
            Assert.That(caster.Issue(move), Is.True);

            Assert.That(caster.Issue(AbilityCommand.OnUnit(aimed, far), IssueMode.Replace), Is.False);

            Assert.That(abilities.LastFailure, Is.EqualTo(AbilityFailure.OutOfRange));
            Assert.That(caster.CurrentCommand, Is.SameAs(move));
            Assert.That(caster.PendingCommands, Is.Empty);
        }

        [UnityTest]
        public IEnumerator AnAbilityOnACooldown_IsRejectedAtIssue()
        {
            yield return null;
            Assert.That(caster.Issue(AbilityCommand.OnUnit(aimed, hostile)), Is.True);
            yield return WaitUntilIdle();

            Assert.That(caster.Issue(AbilityCommand.OnUnit(aimed, hostile)), Is.False);
            Assert.That(abilities.LastFailure, Is.EqualTo(AbilityFailure.OnCooldown));
        }

        [UnityTest]
        public IEnumerator AUnitWithoutAbilities_RejectsAnAbilityOrder()
        {
            yield return null;
            var plain = world.CreateFighter(new Vector3(-6f, 0f, -6f));

            Assert.That(plain.Issue(AbilityCommand.OnUnit(aimed, hostile)), Is.False);
            Assert.That(plain.CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator ADeadCaster_TakesNoAbilityOrders()
        {
            yield return null;
            casterHealth.TakeDamage(casterHealth.Max);

            Assert.That(caster.Issue(AbilityCommand.OnUnit(aimed, hostile)), Is.False);
            Assert.That(hostile.Current, Is.EqualTo(hostile.Max));
        }

        [UnityTest]
        public IEnumerator AnAbilityQueuedBehindAMove_IsAcceptedEvenOutOfRangeNow_AndFailsCleanlyWhenItRuns_ThenTheNextOrderStillRuns()
        {
            yield return null;
            var far = world.CreateDummy(new Vector3(15f, 0f, 15f));
            encounter.Initialize(new[] { casterHealth, ally }, new[] { hostile, far });
            var first = new Vector3(-6f, 0f, -6f);
            var last = new Vector3(6f, 0f, -10f);

            Assert.That(caster.Issue(new MoveCommand(first)), Is.True);
            Assert.That(caster.Issue(AbilityCommand.OnUnit(aimed, far), IssueMode.Append), Is.True, "Only the static checks apply behind other orders");
            Assert.That(caster.Issue(new MoveCommand(last), IssueMode.Append), Is.True);
            yield return WaitUntilIdle();

            Assert.That(far.Current, Is.EqualTo(far.Max), "It was out of range when it ran");
            Assert.That(abilities.LastFailure, Is.EqualTo(AbilityFailure.OutOfRange));
            Assert.That(abilities.IsReady(0), Is.True, "A failure costs no cooldown");
            Assert.That(TestWorld.HorizontalDistance(caster.transform.position, last), Is.LessThan(0.5f), "The next order still ran");
        }

        [UnityTest]
        public IEnumerator AQueuedAbility_WhoseTargetDiesBeforeItRuns_FailsAndTheNextOrderStillRuns()
        {
            yield return null;
            var last = new Vector3(6f, 0f, -10f);
            Assert.That(caster.Issue(new MoveCommand(new Vector3(-6f, 0f, -6f))), Is.True);
            Assert.That(caster.Issue(AbilityCommand.OnUnit(aimed, hostile), IssueMode.Append), Is.True);
            Assert.That(caster.Issue(new MoveCommand(last), IssueMode.Append), Is.True);

            hostile.TakeDamage(hostile.Max);
            yield return WaitUntilIdle();

            Assert.That(abilities.LastFailure, Is.EqualTo(AbilityFailure.TargetDead));
            Assert.That(abilities.UsedCount, Is.EqualTo(0));
            Assert.That(TestWorld.HorizontalDistance(caster.transform.position, last), Is.LessThan(0.5f));
        }

        [UnityTest]
        public IEnumerator AQueuedAbility_WhoseTargetWalksOutOfRange_FailsWithOutOfRange()
        {
            yield return null;
            var last = new Vector3(6f, 0f, -10f);
            Assert.That(caster.Issue(new MoveCommand(new Vector3(3f, 0f, -6f))), Is.True);
            Assert.That(caster.Issue(AbilityCommand.OnUnit(aimed, hostile), IssueMode.Append), Is.True);
            Assert.That(caster.Issue(new MoveCommand(last), IssueMode.Append), Is.True);

            hostile.transform.position = new Vector3(15f, 1f, 15f);
            yield return WaitUntilIdle();

            Assert.That(abilities.LastFailure, Is.EqualTo(AbilityFailure.OutOfRange));
            Assert.That(hostile.Current, Is.EqualTo(hostile.Max));
            Assert.That(TestWorld.HorizontalDistance(caster.transform.position, last), Is.LessThan(0.5f));
        }

        [UnityTest]
        public IEnumerator AQueuedAbility_WhoseLineGetsBlocked_FailsWithNoLineOfSight()
        {
            yield return null;
            var last = new Vector3(6f, 0f, -10f);
            Assert.That(caster.Issue(new MoveCommand(new Vector3(3f, 0f, -6f))), Is.True);
            Assert.That(caster.Issue(AbilityCommand.OnUnit(aimed, hostile), IssueMode.Append), Is.True);
            Assert.That(caster.Issue(new MoveCommand(last), IssueMode.Append), Is.True);

            world.CreateObstacle(new Vector3(3f, 1.5f, -2f), new Vector3(4f, 3f, 0.5f));
            yield return WaitUntilIdle();

            Assert.That(abilities.LastFailure, Is.EqualTo(AbilityFailure.NoLineOfSight));
            Assert.That(hostile.Current, Is.EqualTo(hostile.Max));
            Assert.That(TestWorld.HorizontalDistance(caster.transform.position, last), Is.LessThan(0.5f));
        }

        [UnityTest]
        public IEnumerator AQueuedAbility_RunsFromWhereTheUnitEndsUp_NotWhereItWasQueued()
        {
            yield return null;
            // From the start (0, -6) the far target is 16 m away (range 14); from the move's end (4, -8) it is 12.2 m away.
            var far = world.CreateDummy(new Vector3(16f, 0f, -6f));
            encounter.Initialize(new[] { casterHealth, ally }, new[] { hostile, far });
            Assert.That(abilities.Check(aimed, far, null).Failure, Is.EqualTo(AbilityFailure.OutOfRange), "Precondition");

            Assert.That(caster.Issue(new MoveCommand(new Vector3(4f, 0f, -8f))), Is.True);
            Assert.That(caster.Issue(AbilityCommand.OnUnit(aimed, far), IssueMode.Append), Is.True);
            yield return WaitUntilIdle();

            Assert.That(far.Current, Is.EqualTo(far.Max - 45));
        }

        [UnityTest]
        public IEnumerator AnAbilityIssuedWhileDirectControlSteers_IsNotDroppedBySteering()
        {
            yield return null;
            caster.SetMoveIntent(Vector3.right);
            yield return new WaitForSeconds(0.2f);
            var before = caster.transform.position;

            Assert.That(caster.Issue(AbilityCommand.OnUnit(aimed, hostile)), Is.True);
            yield return new WaitForSeconds(0.3f);

            Assert.That(hostile.Current, Is.EqualTo(hostile.Max - 45), "The ability ran although a move key is held");
            Assert.That(caster.CurrentCommand, Is.Null);
            Assert.That(caster.transform.position.x, Is.GreaterThan(before.x), "And steering carried on");
            Assert.That(caster.MoveIntent, Is.Not.EqualTo(Vector3.zero));
        }

        [UnityTest]
        public IEnumerator Replace_DropsAPendingAbility_AndStopClearsAQueuedOne()
        {
            yield return null;
            pause.Pause();
            Assert.That(caster.Issue(new MoveCommand(new Vector3(-6f, 0f, -6f))), Is.True);
            Assert.That(caster.Issue(AbilityCommand.OnUnit(aimed, hostile), IssueMode.Append), Is.True);
            Assert.That(caster.PendingCommands, Has.Count.EqualTo(1));

            Assert.That(caster.Issue(new MoveCommand(new Vector3(-6f, 0f, -8f)), IssueMode.Replace), Is.True);
            Assert.That(caster.PendingCommands, Is.Empty, "Replace drops the pending ability");

            Assert.That(caster.Issue(AbilityCommand.OnUnit(aimed, hostile), IssueMode.Append), Is.True);
            Assert.That(caster.Issue(new StopCommand()), Is.True);
            Assert.That(caster.CurrentCommand, Is.Null);
            Assert.That(caster.PendingCommands, Is.Empty);

            pause.Resume();
            yield return new WaitForSeconds(0.3f);
            Assert.That(hostile.Current, Is.EqualTo(hostile.Max), "A stopped unit uses nothing");
        }

        [UnityTest]
        public IEnumerator TwoQueuedAbilities_RunInOrder_InTheSameFrame_ThenTheQueueIsEmpty()
        {
            yield return null;
            ally.TakeDamage(60);
            pause.Pause();
            Assert.That(caster.Issue(AbilityCommand.OnUnit(aimed, hostile)), Is.True);
            Assert.That(caster.Issue(AbilityCommand.OnUnit(mend, ally), IssueMode.Append), Is.True);

            pause.Resume();
            yield return WaitUntilIdle();

            Assert.That(hostile.Current, Is.EqualTo(hostile.Max - 45));
            Assert.That(ally.Current, Is.EqualTo(ally.Max - 20));
            Assert.That(abilities.UsedCount, Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator AnAbilityOrder_ParksACompanion_AndTheParkSticks()
        {
            yield return null;
            var leader = world.CreateFighter(new Vector3(6f, 0f, -10f));
            var leaderHealth = leader.GetComponent<Health>();
            var active = world.CreateActiveCharacter(leader, pause);
            var companionAi = world.CreateCompanion(new Vector3(6f, 0f, -6f), active, encounter);
            var companion = companionAi.GetComponent<CommandableUnit>();
            var companionAbilities = world.AddAbilities(companion, encounter, mend);
            // No hostiles: the companion must not start an assist attack of its own while the test watches its orders.
            encounter.Initialize(new[] { casterHealth, ally, leaderHealth, companion.GetComponent<Health>() }, new Health[0]);
            leaderHealth.TakeDamage(50);
            yield return new WaitForSeconds(0.3f);
            Assert.That(companionAi.IsParked, Is.False, "Precondition: attached, within follow distance");
            var standing = companion.transform.position;

            Assert.That(companion.Issue(AbilityCommand.OnUnit(mend, leaderHealth)), Is.True);
            yield return new WaitForSeconds(0.5f);

            Assert.That(companionAbilities.UsedCount, Is.EqualTo(1));
            Assert.That(leaderHealth.Current, Is.EqualTo(leaderHealth.Max - 10));
            Assert.That(companionAi.IsParked, Is.True, "An ability order is an explicit order: it parks the companion");
            Assert.That(companion.CurrentCommand, Is.Null);
            Assert.That(TestWorld.HorizontalDistance(companion.transform.position, standing), Is.LessThan(0.3f), "It did not wander off to follow");
        }
    }
}
```

`Assets/_Project/Tests/PlayMode/CommandableUnitAbilityCoverPlayModeTests.cs`:

```csharp
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CommandableUnitAbilityCoverPlayModeTests
    {
        TestWorld world;
        CoverLocation point;
        CoverRegistry registry;
        Encounter encounter;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            var environment = world.CreateEnvironment((new Vector3(0f, 0.45f, 0f), new Vector3(4f, 0.9f, 0.5f)));
            point = world.CreateCoverPoint(new Vector3(0f, 0f, -1f), Vector3.forward, TestWorld.ObstacleCollider(environment));
            registry = world.CreateRegistry(point);
            encounter = world.CreateEncounter();
        }

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        [UnityTest]
        public IEnumerator MoveToCover_Ability_Move_Attack_RunInOrder_AndTheAbilityFiresFromCover()
        {
            var unit = world.CreateFighter(new Vector3(0f, 0f, -8f), registry: registry);
            var abilities = world.AddAbilities(unit, encounter, world.CreateAimedShot());
            var aimed = abilities.Definition(0);
            var target = world.CreateDummy(new Vector3(0f, 0f, 5f));
            var brawlTarget = world.CreateDummy(new Vector3(-8f, 0f, -6f));
            encounter.Initialize(new[] { unit.GetComponent<Health>() }, new[] { target, brawlTarget });
            yield return null;

            var statusWhenUsed = CoverStatus.None;
            var positionWhenUsed = Vector3.zero;
            abilities.Used += _ =>
            {
                statusWhenUsed = unit.Cover.Status;
                positionWhenUsed = unit.transform.position;
            };
            var destination = new Vector3(-6f, 0f, -6f);

            Assert.That(unit.Issue(new MoveToCoverCommand(point)), Is.True);
            Assert.That(unit.Issue(AbilityCommand.OnUnit(aimed, target), IssueMode.Append), Is.True);
            Assert.That(unit.Issue(new MoveCommand(destination), IssueMode.Append), Is.True);
            Assert.That(unit.Issue(new AttackCommand(brawlTarget.GetComponent<Health>()), IssueMode.Append), Is.True);
            yield return TestWorld.WaitUntil(() => brawlTarget.Current < brawlTarget.Max, 25f);

            Assert.That(statusWhenUsed, Is.EqualTo(CoverStatus.Occupied), "The ability was used from the cover point");
            Assert.That(TestWorld.HorizontalDistance(positionWhenUsed, point.Position), Is.LessThan(1f));
            Assert.That(target.Current, Is.EqualTo(target.Max - 45));
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, destination), Is.LessThan(2.5f), "The move and the attack ran after it");
            Assert.That(brawlTarget.Current, Is.LessThan(brawlTarget.Max));
        }
    }
}
```

Note: `brawlTarget` and `target` above are `Health` components (`CreateDummy` returns `Health`), so `brawlTarget.GetComponent<Health>()` is just `brawlTarget`; keep the call as written, it is harmless.

- [ ] **Step 2: Run to verify they fail**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.CommandableUnitAbilityPlayModeTests"`
Expected: EXIT=2. `Issue` throws `ArgumentException: Unsupported command type AbilityCommand` in every test that issues one.

- [ ] **Step 3: Implement the `CommandableUnit` changes**

In `Assets/_Project/Scripts/Units/CommandableUnit.cs`:

1. Extend the class summary (add before the closing `</summary>`): `An AbilityCommand is instant: it becomes current without doing anything and runs (UnitAbilities.TryUse, which validates again) on the first running frame, before steering is considered, whether it then succeeds or fails the queue moves on.`

2. Add the field with the other private fields (after `UnitCover cover;`): `UnitAbilities abilities;`

3. Add the lazy property after `Cover`:

```csharp
        UnitAbilities Abilities => abilities != null ? abilities : abilities = GetComponent<UnitAbilities>();
```

4. In `Issue`, add the new case to the pass-through switch:

```csharp
                case MoveCommand _:
                case AttackCommand _:
                case MoveToCoverCommand _:
                case AbilityCommand _:
                    break;
```

5. In `Issue`, immediately before `if (!TryStart(command))` insert:

```csharp
            // An ability that would start now is checked in full now (cooldown, range, sight); behind other orders only
            // its target is, because the caster will have moved on by the time it runs.
            if (command is AbilityCommand startingAbility && !CanStartAbility(startingAbility, AbilityCheckScope.Full))
                return false;

```

6. In `CanStart`, add a case before `default`:

```csharp
                case AbilityCommand ability:
                    return CanStartAbility(ability, AbilityCheckScope.Static);
```

7. In `TryStart`, add a case before `default` (a queued ability is not re-judged here; it is judged, and recorded, when it runs):

```csharp
                case AbilityCommand _:
                    Mover.Stop();
                    Cover.ReleaseReservation();
                    ResetAttack();
                    return true;
```

8. In `Update`, directly after the `SimulationTime.IsRunning` guard add:

```csharp
            RunAbilities();

```

9. Add the two methods next to `CanStart` / `StartNext`:

```csharp
        bool CanStartAbility(AbilityCommand ability, AbilityCheckScope scope)
        {
            var owned = Abilities;
            if (owned == null)
                return false;
            return scope == AbilityCheckScope.Full ? owned.CanStartNow(ability) : owned.CanQueue(ability);
        }

        // Abilities are instant: each one succeeds or fails on the frame it becomes current, then the queue moves on, so a
        // refused ability never leaves the unit stuck. It runs before steering is considered, so an ability that direct
        // control just issued is not dropped by a held move key. The guard bounds the loop by the queue length.
        void RunAbilities()
        {
            var guard = queue.Pending.Count + 1;
            while (guard-- > 0 && queue.Current is AbilityCommand ability)
            {
                var owned = Abilities;
                if (owned != null)
                    owned.TryUse(ability);
                StartNext();
            }
        }
```

- [ ] **Step 4: Run the tests**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.CommandableUnitAbilityPlayModeTests"` then `Tools/run-tests.sh PlayMode "Blackglass.Tests.CommandableUnitAbilityCoverPlayModeTests"`
Expected: EXIT=0; 15 and 1 tests pass. Probe problems (a path blocked by the wall, a timing margin) are fixed in the test; a behaviour problem is fixed in the code. Then the whole PlayMode suite (`Tools/run-tests.sh PlayMode`): EXIT=0, total 434 + 16 = 450, and EditMode unchanged at 480.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts Assets/_Project/Tests/PlayMode
git commit -m "Run abilities through the command queue: instant, validated again when they run

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Ability input actions, chord prompts and the menu gate

**Files:**
- Modify: `Assets/_Project/Input/BlackglassControls.inputactions` (through a PowerShell generator in the scratchpad, **not committed**), `Assets/_Project/Scripts/Input/PromptResolver.cs`, `Assets/_Project/Tests/EditMode/InputAssetTests.cs` (one list)
- Create: `Assets/_Project/Scripts/Controls/AbilityMenuGate.cs`
- Test: `Assets/_Project/Tests/EditMode/AbilityInputAssetTests.cs`, `Assets/_Project/Tests/EditMode/AbilityPromptTests.cs`, `Assets/_Project/Tests/PlayMode/AbilityMenuGateTests.cs`

**Interfaces:**
- Consumes: the Commands map, `InputActionUtility` (internal, same assembly), `PromptResolver`, `GamepadLabels`.
- Produces:
  - Actions `Commands/Ability1`..`Commands/Ability4` and `Commands/AbilityMenu` (all `Button`). Keyboard/mouse: keys `1`..`4` for the abilities, nothing for the menu. Every pad family (Xbox, PlayStation, Nintendo, Gamepad): Ability1 = right trigger + D-pad up, Ability2 = + right, Ability3 = + down, Ability4 = + left, as `ButtonWithOneModifier` chords; `AbilityMenu` = the right trigger (the same control as `Camera/CameraModifier`).
  - `PromptResolver.GetPrompt` describes a `ButtonWithOneModifier` composite as `"<modifier> + <button>"` (e.g. `"RT + D-pad Up"`); other composites keep their name.
  - `sealed class AbilityMenuGate : MonoBehaviour` (in `Scripts/Controls/`): while `menuAction` is pressed it disables each action in `suppressedWhileHeld` that was enabled, and re-enables exactly those on release, on cancellation and every `Update` (a reconcile, so a missed callback or a family switch never leaves them off). `internal bool IsSuppressing`; `internal void Initialize(InputActionReference menu, params InputActionReference[] suppressed)`. Serialized field names: `menuAction`, `suppressedWhileHeld`.

- [ ] **Step 1: Write the failing tests**

In `Assets/_Project/Tests/EditMode/InputAssetTests.cs` extend the `PadActions` list: replace the line `"Character/ToggleFollow", "UI/Navigate", "UI/Submit", "UI/Cancel",` with:

```csharp
            "Character/ToggleFollow", "UI/Navigate", "UI/Submit", "UI/Cancel",
            "Commands/Ability1", "Commands/Ability2", "Commands/Ability3", "Commands/Ability4", "Commands/AbilityMenu",
```

`Assets/_Project/Tests/EditMode/AbilityInputAssetTests.cs`:

```csharp
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.InputSystem;

namespace Blackglass.Tests
{
    public class AbilityInputAssetTests
    {
        const string AssetPath = "Assets/_Project/Input/BlackglassControls.inputactions";
        static readonly string[] PadGroups = { "Xbox", "PlayStation", "Nintendo", "Gamepad" };
        static readonly string[] Abilities = { "Ability1", "Ability2", "Ability3", "Ability4" };
        static readonly string[] Directions = { "up", "right", "down", "left" };

        static InputActionAsset Load()
        {
            var asset = AssetDatabase.LoadAssetAtPath<InputActionAsset>(AssetPath);
            Assert.That(asset, Is.Not.Null, $"Input actions not found at {AssetPath}");
            return asset;
        }

        static InputBinding[] BindingsIn(string actionPath, string group) =>
            Load().FindAction(actionPath, throwIfNotFound: true).bindings
                .Where(b => b.groups.Split(InputBinding.Separator).Contains(group)).ToArray();

        [Test]
        public void TheAbilityActionsAndTheMenuAction_ExistAsButtons()
        {
            foreach (var name in Abilities.Concat(new[] { "AbilityMenu" }))
            {
                var action = Load().FindAction("Commands/" + name);
                Assert.That(action, Is.Not.Null, name);
                Assert.That(action.type, Is.EqualTo(InputActionType.Button), name);
            }
        }

        [Test]
        public void Keyboard_BindsTheDigitsOneToFour()
        {
            for (var i = 0; i < Abilities.Length; i++)
            {
                var bindings = BindingsIn("Commands/" + Abilities[i], "KeyboardMouse");
                Assert.That(bindings.Select(b => b.path), Is.EqualTo(new[] { $"<Keyboard>/{i + 1}" }), Abilities[i]);
            }
            Assert.That(BindingsIn("Commands/AbilityMenu", "KeyboardMouse"), Is.Empty, "The menu is a controller thing");
        }

        [Test]
        public void EveryPadFamily_BindsAbilitiesAsTheRightTriggerPlusADpadDirection()
        {
            foreach (var group in PadGroups)
            {
                for (var i = 0; i < Abilities.Length; i++)
                {
                    var bindings = BindingsIn("Commands/" + Abilities[i], group);
                    Assert.That(bindings, Has.Length.EqualTo(3), $"{Abilities[i]} in {group}: a composite and two parts");
                    Assert.That(bindings[0].isComposite, Is.True, $"{Abilities[i]} in {group}");
                    Assert.That(bindings[0].path, Is.EqualTo("ButtonWithOneModifier"), $"{Abilities[i]} in {group}");
                    Assert.That(bindings[1].name, Is.EqualTo("modifier"));
                    Assert.That(bindings[1].path, Is.EqualTo("<Gamepad>/rightTrigger"), $"{Abilities[i]} in {group}");
                    Assert.That(bindings[2].name, Is.EqualTo("button"));
                    Assert.That(bindings[2].path, Is.EqualTo($"<Gamepad>/dpad/{Directions[i]}"), $"{Abilities[i]} in {group}");
                }
            }
        }

        [Test]
        public void TheMenuAction_IsTheSameTriggerAsTheCameraModifier_InEveryPadFamily()
        {
            foreach (var group in PadGroups)
            {
                var menu = BindingsIn("Commands/AbilityMenu", group).Select(b => b.path).ToArray();
                var modifier = BindingsIn("Camera/CameraModifier", group).Select(b => b.path).ToArray();
                Assert.That(menu, Is.EqualTo(modifier), group);
                Assert.That(menu, Is.EqualTo(new[] { "<Gamepad>/rightTrigger" }), group);
            }
        }

        [Test]
        public void TheDpadActionsTheChordClashesWith_AreStillBoundPlain()
        {
            foreach (var group in PadGroups)
            {
                Assert.That(BindingsIn("Commands/Stop", group).Select(b => b.path), Is.EqualTo(new[] { "<Gamepad>/dpad/down" }), group);
                Assert.That(BindingsIn("Character/ToggleFollow", group).Select(b => b.path), Is.EqualTo(new[] { "<Gamepad>/dpad/up" }), group);
                Assert.That(BindingsIn("Commands/NextTarget", group).Select(b => b.path), Is.EqualTo(new[] { "<Gamepad>/dpad/right" }), group);
                Assert.That(BindingsIn("Commands/PreviousTarget", group).Select(b => b.path), Is.EqualTo(new[] { "<Gamepad>/dpad/left" }), group);
            }
        }
    }
}
```

`Assets/_Project/Tests/EditMode/AbilityPromptTests.cs`:

```csharp
using NUnit.Framework;
using UnityEditor;
using UnityEngine.InputSystem;

namespace Blackglass.Tests
{
    public class AbilityPromptTests
    {
        const string AssetPath = "Assets/_Project/Input/BlackglassControls.inputactions";

        static string Prompt(string actionPath, InputFamily family) =>
            PromptResolver.GetPrompt(AssetDatabase.LoadAssetAtPath<InputActionAsset>(AssetPath).FindAction(actionPath, throwIfNotFound: true), family);

        [TestCase("Commands/Ability1", InputFamily.Xbox, "RT + D-pad Up")]
        [TestCase("Commands/Ability2", InputFamily.Xbox, "RT + D-pad Right")]
        [TestCase("Commands/Ability3", InputFamily.PlayStation, "R2 + D-pad Down")]
        [TestCase("Commands/Ability4", InputFamily.Nintendo, "ZR + D-pad Left")]
        [TestCase("Commands/Ability1", InputFamily.GenericGamepad, "Right Trigger + D-pad Up")]
        [TestCase("Commands/AbilityMenu", InputFamily.Xbox, "RT")]
        public void AChordReadsAsModifierPlusButton(string actionPath, InputFamily family, string expected)
        {
            Assert.That(Prompt(actionPath, family), Is.EqualTo(expected));
        }

        [Test]
        public void KeyboardShowsTheDigit_AndOtherCompositesKeepTheirName()
        {
            Assert.That(Prompt("Commands/Ability2", InputFamily.KeyboardMouse), Is.EqualTo("2"));
            Assert.That(Prompt("Character/Move", InputFamily.KeyboardMouse), Is.EqualTo("WASD"));
            Assert.That(Prompt("Commands/Ability1", InputFamily.Xbox), Does.Not.Contain("ButtonWithOneModifier"));
        }

        [Test]
        public void AFamilyWithoutABinding_StillShowsADash()
        {
            Assert.That(Prompt("Commands/AbilityMenu", InputFamily.KeyboardMouse), Is.EqualTo("-"));
        }
    }
}
```

`Assets/_Project/Tests/PlayMode/AbilityMenuGateTests.cs`:

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
    public class AbilityMenuGateTests : InputTestFixture
    {
        Gamepad pad;
        InputActionAsset actions;
        TestWorld world;
        AbilityMenuGate gate;
        InputAction stop;
        InputAction follow;
        InputAction ability1;
        InputAction ability3;
        int stops;
        int ability3Uses;
        int ability1Uses;

        public override void Setup()
        {
            base.Setup();
            pad = InputSystem.AddDevice<XInputController>();
            actions = TestControls.Load();
            TestControls.UseGroup(actions, "Xbox");
            world = new TestWorld();
            stop = actions.FindAction("Commands/Stop", true);
            follow = actions.FindAction("Character/ToggleFollow", true);
            ability1 = actions.FindAction("Commands/Ability1", true);
            ability3 = actions.FindAction("Commands/Ability3", true);
            stop.Enable();
            follow.Enable();
            ability1.Enable();
            ability3.Enable();
            stop.performed += OnStop;
            ability1.performed += OnAbility1;
            ability3.performed += OnAbility3;
            stops = 0;
            ability1Uses = 0;
            ability3Uses = 0;

            var host = world.Track(new GameObject("Gate"));
            host.SetActive(false);
            gate = host.AddComponent<AbilityMenuGate>();
            gate.Initialize(TestControls.Ref(actions, "Commands/AbilityMenu"),
                TestControls.Ref(actions, "Commands/Stop"), TestControls.Ref(actions, "Character/ToggleFollow"),
                TestControls.Ref(actions, "Commands/NextTarget"), TestControls.Ref(actions, "Commands/PreviousTarget"));
            host.SetActive(true);
        }

        public override void TearDown()
        {
            // The actions belong to the shared project asset: drop this fixture's handlers or they pile up across tests.
            stop.performed -= OnStop;
            ability1.performed -= OnAbility1;
            ability3.performed -= OnAbility3;
            world.Dispose();
            TestControls.Reset(actions);
            base.TearDown();
        }

        void OnStop(InputAction.CallbackContext context) => stops++;
        void OnAbility1(InputAction.CallbackContext context) => ability1Uses++;
        void OnAbility3(InputAction.CallbackContext context) => ability3Uses++;

        IEnumerator Tap(ButtonControl button)
        {
            Press(button);
            yield return null;
            Release(button);
            yield return null;
        }

        [UnityTest]
        public IEnumerator WithoutTheTrigger_TheDpadStopsAsBefore_AndNoAbilityIsPicked()
        {
            yield return Tap(pad.dpad.down);

            Assert.That(stops, Is.EqualTo(1));
            Assert.That(ability3Uses, Is.EqualTo(0));
            Assert.That(gate.IsSuppressing, Is.False);
        }

        [UnityTest]
        public IEnumerator HoldingTheTrigger_ADpadDirectionPicksTheAbility_AndDoesNotAlsoStop()
        {
            Press(pad.rightTrigger);
            yield return null;
            Assert.That(gate.IsSuppressing, Is.True);

            yield return Tap(pad.dpad.down);
            yield return Tap(pad.dpad.up);

            Assert.That(ability3Uses, Is.EqualTo(1), "RT + down is Ability3");
            Assert.That(ability1Uses, Is.EqualTo(1), "RT + up is Ability1");
            Assert.That(stops, Is.EqualTo(0), "The held trigger turns the D-pad into ability slots only");
            Release(pad.rightTrigger);
            yield return null;
        }

        [UnityTest]
        public IEnumerator ReleasingTheTrigger_GivesTheDpadItsOrdinaryJobsBack()
        {
            Press(pad.rightTrigger);
            yield return null;
            yield return Tap(pad.dpad.down);
            Release(pad.rightTrigger);
            yield return null;
            yield return null;

            Assert.That(gate.IsSuppressing, Is.False);
            Assert.That(stop.enabled, Is.True);
            yield return Tap(pad.dpad.down);
            Assert.That(stops, Is.EqualTo(1), "Stop works again after the trigger is released");
            Assert.That(ability3Uses, Is.EqualTo(1), "and only the held-trigger press picked an ability");
        }

        [UnityTest]
        public IEnumerator ADisabledAction_IsNotEnabledByTheGate()
        {
            follow.Disable();
            Press(pad.rightTrigger);
            yield return null;
            Release(pad.rightTrigger);
            yield return null;
            yield return null;

            Assert.That(follow.enabled, Is.False, "The gate only gives back what it took");
            Assert.That(stop.enabled, Is.True);
        }

        [UnityTest]
        public IEnumerator UnpluggingThePadWhileTheTriggerIsHeld_RestoresTheSuppressedActions()
        {
            Press(pad.rightTrigger);
            yield return null;
            Assert.That(stop.enabled, Is.False, "Precondition: suppressed while held");

            InputSystem.RemoveDevice(pad);
            yield return null;
            yield return null;

            Assert.That(gate.IsSuppressing, Is.False);
            Assert.That(stop.enabled, Is.True);
            Assert.That(follow.enabled, Is.True);
        }

        [UnityTest]
        public IEnumerator SwitchingBindingGroupsWhileTheTriggerIsHeld_NeverLeavesActionsOff()
        {
            Press(pad.rightTrigger);
            yield return null;
            TestControls.UseGroup(actions, "KeyboardMouse");
            yield return null;
            yield return null;

            Assert.That(gate.IsSuppressing, Is.False, "No pad binding is active, so the menu is not held");
            Assert.That(stop.enabled, Is.True);
        }
    }
}
#endif
```

- [ ] **Step 2: Run to verify the failures**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.AbilityInputAssetTests"`
Expected: EXIT=2 (the actions do not exist: `FindAction` returns null or throws). `AbilityMenuGateTests` does not compile yet (EXIT=1) for the missing `AbilityMenuGate`.

- [ ] **Step 3: Generate the actions and bindings (one-off script, not committed)**

Create `C:\Users\Yann\AppData\Local\Temp\claude\F--Programs-ProjectBlackglass\773b1f81-7908-48a3-b6ad-0c616b4883a4\scratchpad\AddAbilityInput.ps1` (any scratch folder is fine) with:

```powershell
$ErrorActionPreference = 'Stop'
$path = (Resolve-Path 'Assets/_Project/Input/BlackglassControls.inputactions').Path
$bytes = [System.IO.File]::ReadAllBytes($path)
$hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
$text = [System.IO.File]::ReadAllText($path)
$nl = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }

function NewId { [guid]::NewGuid().ToString() }
function ActionLine($name) {
    '                { "name": "' + $name + '", "type": "Button", "id": "' + (NewId) + '", "expectedControlType": "Button", "processors": "", "interactions": "", "initialStateCheck": false }'
}
function BindingLine($name, $bindingPath, $group, $action, $isComposite, $isPart) {
    '                { "name": "' + $name + '", "id": "' + (NewId) + '", "path": "' + $bindingPath + '", "interactions": "", "processors": "", "groups": "' + $group + '", "action": "' + $action + '", "isComposite": ' + $isComposite + ', "isPartOfComposite": ' + $isPart + ' }'
}

if ($text.Contains('"name": "Ability1"')) { throw 'The ability actions are already in the asset.' }

# 1. Actions: after the last action of the Commands map (CursorMove).
$cursor = [regex]::Match($text, '\{ "name": "CursorMove", "type": "Value"[^\r\n]*\}')
if (-not $cursor.Success) { throw 'CursorMove action not found' }
$actionLines = 'Ability1', 'Ability2', 'Ability3', 'Ability4', 'AbilityMenu' | ForEach-Object { ActionLine $_ }
$text = $text.Insert($cursor.Index + $cursor.Length, ',' + $nl + ($actionLines -join (',' + $nl)))

# 2. Bindings: after the last binding of the Commands map (PreviousTarget, generic pad).
$last = [regex]::Match($text, '\{ "name": "", "id": "fb990c6b-9d4c-4f8b-b8ba-2f35bef0842b"[^\r\n]*\}')
if (-not $last.Success) { throw 'The last Commands binding was not found' }
$lines = New-Object System.Collections.Generic.List[string]
$abilities = 'Ability1', 'Ability2', 'Ability3', 'Ability4'
$digits = @{ Ability1 = '1'; Ability2 = '2'; Ability3 = '3'; Ability4 = '4' }
$dpad = @{ Ability1 = 'up'; Ability2 = 'right'; Ability3 = 'down'; Ability4 = 'left' }
foreach ($a in $abilities) {
    $lines.Add((BindingLine '' ('<Keyboard>/' + $digits[$a]) 'KeyboardMouse' $a 'false' 'false'))
}
foreach ($g in 'Xbox', 'PlayStation', 'Nintendo', 'Gamepad') {
    foreach ($a in $abilities) {
        $lines.Add((BindingLine 'ButtonWithOneModifier' 'ButtonWithOneModifier' $g $a 'true' 'false'))
        $lines.Add((BindingLine 'modifier' '<Gamepad>/rightTrigger' $g $a 'false' 'true'))
        $lines.Add((BindingLine 'button' ('<Gamepad>/dpad/' + $dpad[$a]) $g $a 'false' 'true'))
    }
    $lines.Add((BindingLine '' '<Gamepad>/rightTrigger' $g 'AbilityMenu' 'false' 'false'))
}
$text = $text.Insert($last.Index + $last.Length, ',' + $nl + ($lines -join (',' + $nl)))

[System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($hasBom)))
$null = Get-Content -Raw $path | ConvertFrom-Json
Write-Output "Added $($actionLines.Count) actions and $($lines.Count) bindings."
```

Run it from the repo root: `powershell -NoProfile -ExecutionPolicy Bypass -File "<path to the script>"`.
Expected output: `Added 5 actions and 56 bindings.` (4 keys, plus per pad family 4 chords × 3 entries + 1 menu binding = 13, times 4 families = 52). Then:

```bash
git diff --stat Assets/_Project/Input/BlackglassControls.inputactions
```
Expected: 61 inserted lines (5 actions + 56 bindings) and 2 changed lines (the two anchor lines gained a trailing comma). If the diff shows a whole-file change, line endings or the BOM were not preserved: `git checkout` the asset and fix the script.

- [ ] **Step 4: Extend `PromptResolver`**

In `Assets/_Project/Scripts/Input/PromptResolver.cs` add `using UnityEngine.InputSystem.Utilities;` and replace the body of `GetPrompt` and `Describe` with:

```csharp
        public static string GetPrompt(InputAction action, InputFamily family)
        {
            if (action == null)
                return None;
            var group = family.BindingGroup();
            var prompts = new List<string>();
            var bindings = action.bindings;
            for (var i = 0; i < bindings.Count; i++)
            {
                var binding = bindings[i];
                if (binding.isPartOfComposite || !InGroup(binding, group))
                    continue;
                prompts.Add(binding.isComposite ? DescribeComposite(bindings, i, family) : DescribePath(binding.effectivePath, family));
            }
            return prompts.Count == 0 ? None : string.Join(" / ", prompts);
        }
```

```csharp
        // A chord reads "modifier + button" ("RT + D-pad Up"); any other composite (WASD) keeps its own name.
        static string DescribeComposite(ReadOnlyArray<InputBinding> bindings, int index, InputFamily family)
        {
            var header = bindings[index];
            if (header.path != "ButtonWithOneModifier")
                return header.name;
            string modifier = null;
            string button = null;
            for (var i = index + 1; i < bindings.Count && bindings[i].isPartOfComposite; i++)
            {
                if (bindings[i].name == "modifier")
                    modifier = DescribePath(bindings[i].effectivePath, family);
                else if (bindings[i].name == "button")
                    button = DescribePath(bindings[i].effectivePath, family);
            }
            return modifier != null && button != null ? $"{modifier} + {button}" : header.name;
        }

        static string DescribePath(string path, InputFamily family)
        {
            if (family == InputFamily.KeyboardMouse)
                return InputControlPath.ToHumanReadableString(path, InputControlPath.HumanReadableStringOptions.OmitDevice);
            var slash = path.IndexOf('/');
            return GamepadLabels.Label(slash < 0 ? path : path.Substring(slash + 1), family);
        }
```

(`InGroup` stays as it is; delete the old `Describe`.) Update the class summary: "A chord (modifier plus button) reads `RT + D-pad Up`."

- [ ] **Step 5: Implement `AbilityMenuGate`**

`Assets/_Project/Scripts/Controls/AbilityMenuGate.cs`:

```csharp
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Blackglass
{
    /// <summary>
    /// Holding the ability-menu action (a pad's right trigger) turns the D-pad into ability slots: the ability chords
    /// (trigger + a D-pad direction) are bindings, but the plain D-pad actions (Stop, Follow, previous/next target)
    /// would fire on the same press, and Input System shortcut consumption is off by default. So while the menu action
    /// is held this disables the plain actions it was given, and gives back exactly the ones it took when the menu is
    /// released, cancelled (a family switch, an unplugged pad) or no longer pressed at the next Update. Contains no
    /// gameplay: it knows actions, not buttons.
    /// </summary>
    public sealed class AbilityMenuGate : MonoBehaviour
    {
        [SerializeField] InputActionReference menuAction;
        [SerializeField] InputActionReference[] suppressedWhileHeld = System.Array.Empty<InputActionReference>();

        readonly List<InputAction> taken = new List<InputAction>();

        /// <summary>True while the plain actions are switched off because the menu is held.</summary>
        internal bool IsSuppressing { get; private set; }

        internal void Initialize(InputActionReference menu, params InputActionReference[] suppressed)
        {
            menuAction = menu;
            suppressedWhileHeld = suppressed;
        }

        void OnEnable()
        {
            InputActionUtility.SetEnabled(true, menuAction);
            if (menuAction != null)
            {
                menuAction.action.started += OnMenuChanged;
                menuAction.action.canceled += OnMenuChanged;
            }
        }

        void OnDisable()
        {
            if (menuAction != null)
            {
                menuAction.action.started -= OnMenuChanged;
                menuAction.action.canceled -= OnMenuChanged;
            }
            Suppress(false);
            InputActionUtility.SetEnabled(false, menuAction);
        }

        void Update() => Reconcile();

        void OnMenuChanged(InputAction.CallbackContext context) => Reconcile();

        void Reconcile() => Suppress(InputActionUtility.IsPressed(menuAction));

        void Suppress(bool on)
        {
            if (on == IsSuppressing)
                return;
            IsSuppressing = on;
            if (on)
            {
                foreach (var reference in suppressedWhileHeld)
                {
                    if (reference == null || reference.action == null || !reference.action.enabled)
                        continue;
                    reference.action.Disable();
                    taken.Add(reference.action);
                }
                return;
            }
            foreach (var action in taken)
                action.Enable();
            taken.Clear();
        }
    }
}
```

- [ ] **Step 6: Run the tests**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.AbilityInputAssetTests"`, `"Blackglass.Tests.AbilityPromptTests"`, `"Blackglass.Tests.InputAssetTests"`, `"Blackglass.Tests.PromptResolverTests"` → EXIT=0 each; the new fixtures have 5 and 8 tests. Then `Tools/run-tests.sh PlayMode "Blackglass.Tests.AbilityMenuGateTests"` → EXIT=0, 6 tests.
If the chord test shows that `ButtonWithOneModifier` fires the ability on a press *without* the trigger, or does not fire with it held first, stop and report: the chord is the heart of this task.
Then both full suites: EditMode = 480 + the new EditMode tests (5 + 8 = 13 → 493), PlayMode = 450 + 6 = 456. EXIT=0 for both. `PrototypeSceneTests` loading the scene must still pass (action IDs of existing actions are unchanged).

- [ ] **Step 7: Commit**

```bash
git add Assets/_Project/Input Assets/_Project/Scripts Assets/_Project/Tests
git commit -m "Add the ability actions: keys 1-4 and a trigger + D-pad chord on every pad, with a gate for the clashing D-pad actions

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 7: The cursor owns the stick while aiming, and snaps to the side an ability needs

**Files:**
- Modify: `Assets/_Project/Scripts/Input/StickRole.cs`, `Assets/_Project/Scripts/Input/TacticalCursor.cs`, `Assets/_Project/Scripts/CameraControl/TacticalCameraController.cs`
- Test: `Assets/_Project/Tests/EditMode/StickRoleAimingTests.cs`, `Assets/_Project/Tests/PlayMode/TacticalCursorAimingTests.cs`, `Assets/_Project/Tests/PlayMode/CameraAimingTests.cs`

**Interfaces:**
- Consumes: `StickRole`, `TacticalCursor`, `PointerTarget(Kind)`, `HostileTargets.Cycle`, `Encounter.Friendlies`.
- Produces: `StickRole.CameraOwnsRightStick(bool isRunning, bool modifierHeld, bool aiming = false)` and the `ActiveCharacter` overload with the same optional `aiming` (aiming means the cursor always owns the stick). On `TacticalCursor`: `bool Aiming { get; set; }`, `PointerTargetKind SnapTo { get; set; }` (`None` = default friendly→hostile→cover snapping; `Friendly` / `Hostile` = only that side; `Ground` = no snapping: the exact point under the cursor); with `SnapTo == Friendly` the D-pad cycle (`CycleTarget`) walks the living friendlies by distance from the controlled character and moves the cursor onto the next one, without setting the soft target. `TacticalCameraController.Initialize(..., InputActionReference cameraModifier = null, TacticalCursor tacticalCursor = null)` and the serialized field `cursor`: while `cursor.Aiming` the camera does not take the right stick.

- [ ] **Step 1: Write the failing tests**

`Assets/_Project/Tests/EditMode/StickRoleAimingTests.cs`:

```csharp
using NUnit.Framework;

namespace Blackglass.Tests
{
    public class StickRoleAimingTests
    {
        [TestCase(true, false)]
        [TestCase(true, true)]
        [TestCase(false, false)]
        [TestCase(false, true)]
        public void WhileAiming_TheCursorAlwaysOwnsTheStick(bool running, bool modifierHeld)
        {
            Assert.That(StickRole.CameraOwnsRightStick(running, modifierHeld, true), Is.False);
        }

        [TestCase(true, false, true)]
        [TestCase(true, true, false)]
        [TestCase(false, false, false)]
        [TestCase(false, true, true)]
        public void WhenNotAiming_TheOldRuleStands(bool running, bool modifierHeld, bool cameraOwns)
        {
            Assert.That(StickRole.CameraOwnsRightStick(running, modifierHeld), Is.EqualTo(cameraOwns));
            Assert.That(StickRole.CameraOwnsRightStick(running, modifierHeld, false), Is.EqualTo(cameraOwns));
        }

        [Test]
        public void WithNoActiveCharacter_AimingStillHandsTheStickToTheCursor()
        {
            Assert.That(StickRole.CameraOwnsRightStick((ActiveCharacter)null, false), Is.True);
            Assert.That(StickRole.CameraOwnsRightStick((ActiveCharacter)null, false, true), Is.False);
        }
    }
}
```

`Assets/_Project/Tests/PlayMode/TacticalCursorAimingTests.cs`:

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
    public class TacticalCursorAimingTests : InputTestFixture
    {
        // The friendly and the hostile are 2.2 m apart; the ground between them is 1.1 m from each (inside the 1.2 m snap).
        static readonly Vector3 FriendlyGround = new Vector3(-4f, 0f, -6f);
        static readonly Vector3 OtherFriendlyGround = new Vector3(-4f, 0f, -10f);
        static readonly Vector3 HostileGround = new Vector3(-1.8f, 0f, -6f);
        static readonly Vector3 BetweenThem = new Vector3(-2.9f, 0f, -6f);
        static readonly Vector3 OpenGround = new Vector3(4f, 0f, 2f);

        Gamepad pad;
        InputActionAsset actions;
        TestWorld world;
        Camera viewCamera;
        SelectableUnit friendly;
        SelectableUnit otherFriendly;
        Health hostile;
        TacticalPause pause;
        ActiveCharacter active;
        TacticalCursor cursor;

        public override void Setup()
        {
            base.Setup();
            pad = InputSystem.AddDevice<XInputController>();
            actions = TestControls.Load();
            TestControls.UseGroup(actions, "Xbox");
            world = new TestWorld();
            world.CreateEnvironment();
            friendly = world.CreateFriendlyFighter(FriendlyGround);
            otherFriendly = world.CreateFriendlyFighter(OtherFriendlyGround);
            hostile = world.CreateDummy(HostileGround);
            var encounter = world.CreateEncounter();
            encounter.Initialize(new[] { friendly.GetComponent<Health>(), otherFriendly.GetComponent<Health>() }, new[] { hostile });

            var cameraObject = world.Track(new GameObject("Camera"));
            cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 25f, -20f), Quaternion.Euler(50f, 0f, 0f));
            viewCamera = cameraObject.AddComponent<Camera>();

            var systems = world.Track(new GameObject("Systems"));
            systems.SetActive(false);
            pause = systems.AddComponent<TacticalPause>();
            var selection = systems.AddComponent<UnitSelection>();
            selection.Initialize(friendly, otherFriendly);
            active = systems.AddComponent<ActiveCharacter>();
            active.Initialize(friendly.Unit, pause, selection);
            cursor = systems.AddComponent<TacticalCursor>();
            cursor.Initialize(viewCamera, active, selection, encounter, null, null,
                TestControls.Ref(actions, "Commands/CursorMove"),
                TestControls.Ref(actions, "Camera/CameraModifier"),
                TestControls.Ref(actions, "Commands/NextTarget"),
                TestControls.Ref(actions, "Commands/PreviousTarget"));
            systems.SetActive(true);
        }

        public override void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
            TestControls.Reset(actions);
            base.TearDown();
        }

        Vector2 ScreenPointOf(Vector3 worldPoint) => viewCamera.WorldToScreenPoint(worldPoint);

        IEnumerator Tap(ButtonControl button)
        {
            Press(button);
            yield return null;
            Release(button);
            yield return null;
        }

        [UnityTest]
        public IEnumerator Aiming_MakesTheCursorOwnTheStick_WhileTheGameRuns_WithOrWithoutTheTriggerHeld()
        {
            yield return null;
            Assert.That(cursor.IsActive, Is.False, "Running, nothing armed: the camera owns the stick");

            cursor.Aiming = true;
            yield return null;
            Assert.That(cursor.IsActive, Is.True);

            Press(pad.rightTrigger);
            yield return null;
            Assert.That(cursor.IsActive, Is.True, "The held menu trigger does not hand the stick back while aiming");
            Release(pad.rightTrigger);

            cursor.Aiming = false;
            yield return null;
            Assert.That(cursor.IsActive, Is.False);
        }

        [UnityTest]
        public IEnumerator SnapTo_DecidesWhichSideTheCursorSnapsTo()
        {
            pause.Pause();
            cursor.SetScreenPosition(ScreenPointOf(BetweenThem));
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Friendly), "Default snapping prefers a friendly");

            cursor.SnapTo = PointerTargetKind.Hostile;
            cursor.Refresh();
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Hostile));
            Assert.That(cursor.Target.Hostile, Is.SameAs(hostile));

            cursor.SnapTo = PointerTargetKind.Friendly;
            cursor.Refresh();
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Friendly));
            Assert.That(cursor.Target.Friendly, Is.SameAs(friendly));

            cursor.SnapTo = PointerTargetKind.Ground;
            cursor.Refresh();
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Ground), "An area is aimed freely: no snapping");
            Assert.That(CoverRules.FlatDistance(cursor.Target.Point, BetweenThem), Is.LessThan(0.2f));
            yield return null;
        }

        [UnityTest]
        public IEnumerator SnapTo_AHostile_WithNobodyNear_IsPlainGround_AndSnapToNoneIsTheOldBehaviour()
        {
            pause.Pause();
            cursor.SnapTo = PointerTargetKind.Hostile;
            cursor.SetScreenPosition(ScreenPointOf(OpenGround));
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Ground));

            cursor.SnapTo = PointerTargetKind.None;
            cursor.SetScreenPosition(ScreenPointOf(FriendlyGround));
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Friendly));
            yield return null;
        }

        [UnityTest]
        public IEnumerator SnapTo_Hostile_DoesNotRelabelAFriendlyUnderTheCursor()
        {
            pause.Pause();
            cursor.SnapTo = PointerTargetKind.Hostile;

            cursor.SetScreenPosition(ScreenPointOf(friendly.transform.position));

            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Friendly),
                "A cursor right on a friendly is a friendly: the ability will say 'wrong side'");
            yield return null;
        }

        [UnityTest]
        public IEnumerator DpadCycling_WithSnapToFriendly_WalksTheFriendlies_AndSetsNoSoftTarget()
        {
            pause.Pause();
            cursor.SnapTo = PointerTargetKind.Friendly;
            cursor.SetScreenPosition(ScreenPointOf(friendly.transform.position));
            yield return null;
            Assert.That(cursor.Target.Friendly, Is.SameAs(friendly), "Precondition");

            yield return Tap(pad.dpad.right);

            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Friendly));
            Assert.That(cursor.Target.Friendly, Is.SameAs(otherFriendly));
            Assert.That(cursor.SoftTarget, Is.Null, "Attack must never pick a friendly");

            yield return Tap(pad.dpad.left);
            Assert.That(cursor.Target.Friendly, Is.SameAs(friendly));
        }

        [UnityTest]
        public IEnumerator DpadCycling_WithSnapToHostile_StillSetsTheSoftTarget()
        {
            pause.Pause();
            cursor.SnapTo = PointerTargetKind.Hostile;
            cursor.SetScreenPosition(ScreenPointOf(OpenGround));
            yield return null;

            yield return Tap(pad.dpad.right);

            Assert.That(cursor.SoftTarget, Is.SameAs(hostile));
            Assert.That(cursor.Target.Hostile, Is.SameAs(hostile));
        }
    }
}
#endif
```

`Assets/_Project/Tests/PlayMode/CameraAimingTests.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CameraAimingTests : InputTestFixture
    {
        Gamepad pad;
        TestWorld world;
        InputActionAsset actions;
        TacticalCameraController controller;
        TacticalCursor cursor;

        public override void Setup()
        {
            base.Setup();
            pad = InputSystem.AddDevice<Gamepad>();
            world = new TestWorld();
            actions = TestControls.Load();
            TestControls.UseGroup(actions, "Gamepad");

            var pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            var activeCharacter = world.Track(new GameObject("Player")).AddComponent<ActiveCharacter>();
            // Only its Aiming flag is read by the camera; it needs no scene wiring.
            cursor = world.Track(new GameObject("Cursor")).AddComponent<TacticalCursor>();
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
                TestControls.Ref(actions, "Camera/Zoom"),
                activeCharacter,
                TestControls.Ref(actions, "Camera/Look"),
                TestControls.Ref(actions, "Camera/CameraModifier"),
                cursor);
            rig.SetActive(true);
            world.CreateEnvironment();
            activeCharacter.Initialize(world.CreateUnit(Vector3.zero), pause);
        }

        public override void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
            TestControls.Reset(actions);
            base.TearDown();
        }

        IEnumerator HoldStick(Vector2 value, float seconds)
        {
            Set(pad.rightStick, value);
            yield return new WaitForSecondsRealtime(seconds);
            Set(pad.rightStick, Vector2.zero);
            yield return null;
        }

        [UnityTest]
        public IEnumerator WhileAnAbilityIsArmed_TheRightStickDoesNotRotateTheCamera()
        {
            var start = controller.Yaw;
            cursor.Aiming = true;
            yield return HoldStick(new Vector2(1f, 0f), 0.3f);

            Assert.That(controller.Yaw, Is.EqualTo(start).Within(0.01f));
        }

        [UnityTest]
        public IEnumerator OnceDisarmed_TheRightStickRotatesTheCameraAgain()
        {
            cursor.Aiming = true;
            yield return null;
            cursor.Aiming = false;
            var start = controller.Yaw;
            yield return HoldStick(new Vector2(1f, 0f), 0.3f);

            Assert.That(Mathf.Abs(Mathf.DeltaAngle(start, controller.Yaw)), Is.GreaterThan(15f));
        }
    }
}
#endif
```

- [ ] **Step 2: Run to verify the failures**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.StickRoleAimingTests"`
Expected: EXIT=1 (`CameraOwnsRightStick` has no 3-argument overload; `Aiming`, `SnapTo` and the 9-argument `Initialize` do not exist).

- [ ] **Step 3: Implement `StickRole`**

Replace `Assets/_Project/Scripts/Input/StickRole.cs` with:

```csharp
namespace Blackglass
{
    /// <summary>
    /// Who owns the right stick. While the game is running (takeover on or off) it looks around (camera); while it is
    /// paused it moves the tactical cursor. Holding the camera modifier swaps the two, so a controller can point while
    /// the game runs and rotate the camera while planning. While an ability is armed (aiming) the cursor always owns it,
    /// in real time too and whatever the modifier does, so aiming never needs the camera.
    /// </summary>
    public static class StickRole
    {
        public static bool CameraOwnsRightStick(bool isRunning, bool modifierHeld, bool aiming = false) =>
            !aiming && isRunning != modifierHeld;

        /// <summary>The same rule for a game with this active character; with none there is nothing to pause, so it runs.</summary>
        public static bool CameraOwnsRightStick(ActiveCharacter activeCharacter, bool modifierHeld, bool aiming = false) =>
            CameraOwnsRightStick(activeCharacter == null || !activeCharacter.IsPaused, modifierHeld, aiming);
    }
}
```

- [ ] **Step 4: Implement the `TacticalCursor` changes**

In `Assets/_Project/Scripts/Input/TacticalCursor.cs`:

1. Add after `public Vector2 ScreenPosition => screenPosition;`:

```csharp
        /// <summary>True while an ability is armed: the stick is the cursor's whatever else would own it (see StickRole).</summary>
        public bool Aiming { get; set; }

        /// <summary>
        /// What the cursor snaps to. None: the default (a friendly, then a hostile, then a cover location). Friendly or
        /// Hostile: only that side. Ground: nothing, the exact point under the cursor (an area is aimed freely).
        /// </summary>
        public PointerTargetKind SnapTo { get; set; }
```

2. Change `IsActive` to:

```csharp
        public bool IsActive =>
            (inputDevice == null || inputDevice.Family.IsController())
            && !StickRole.CameraOwnsRightStick(activeCharacter, InputActionUtility.IsPressed(cameraModifierAction), Aiming);
```

3. Replace `CycleTarget` with:

```csharp
        internal void CycleTarget(int direction)
        {
            if (encounter == null)
                return;
            if (SnapTo == PointerTargetKind.Friendly)
            {
                CycleFriendly(direction);
                return;
            }
            var next = HostileTargets.Cycle(encounter.Hostiles, CycleOrigin(), SoftTarget, direction);
            if (next == null)
                return;
            softTarget = next;
            MoveOnto(next);
        }

        // Aiming at a friend (a heal): walk the living friendlies by distance and put the cursor on the next one. The
        // soft target stays hostile-only, so an Attack can never pick a friendly.
        void CycleFriendly(int direction)
        {
            var current = target.Kind == PointerTargetKind.Friendly && target.Friendly != null
                ? target.Friendly.GetComponent<Health>()
                : null;
            var next = HostileTargets.Cycle(encounter.Friendlies, CycleOrigin(), current, direction);
            if (next != null)
                MoveOnto(next);
        }

        void MoveOnto(Health unit)
        {
            if (!IsActive || viewCamera == null)
                return;
            var screen = viewCamera.WorldToScreenPoint(unit.transform.position);
            if (screen.z > 0f)
                SetScreenPosition(new Vector2(screen.x, screen.y));
        }
```

4. In `Resolve`, replace everything after `var ground = raw.Point;` with:

```csharp
            var ground = raw.Point;
            switch (SnapTo)
            {
                case PointerTargetKind.Ground:
                    return raw;
                case PointerTargetKind.Friendly:
                {
                    var onlyFriendly = NearestFriendly(ground);
                    return onlyFriendly != null ? PointerTarget.OnFriendly(onlyFriendly, ground) : raw;
                }
                case PointerTargetKind.Hostile:
                {
                    var onlyHostile = NearestHostile(ground);
                    return onlyHostile != null ? PointerTarget.OnHostile(onlyHostile, ground) : raw;
                }
            }

            var friendly = NearestFriendly(ground);
            if (friendly != null)
                return PointerTarget.OnFriendly(friendly, ground);
            var hostile = NearestHostile(ground);
            if (hostile != null)
                return PointerTarget.OnHostile(hostile, ground);
            if (coverRegistry != null && CoverRules.TryChooseNearest(coverRegistry.Points, ground, coverSnapRadius, acceptAny, out var cover))
                return PointerTarget.OnCover(cover, ground);
            return raw;
```

Update the class summary: add "While an ability is armed (`Aiming`) it owns the stick whatever the game is doing, and `SnapTo` narrows what it snaps to."

- [ ] **Step 5: Implement the camera change**

In `Assets/_Project/Scripts/CameraControl/TacticalCameraController.cs`: add `[SerializeField] TacticalCursor cursor;` under `cameraModifierAction`; add the parameter `TacticalCursor tacticalCursor = null` at the end of `Initialize` and assign `cursor = tacticalCursor;`; and change the stick-ownership line to:

```csharp
            var cameraOwnsStick = StickRole.CameraOwnsRightStick(activeCharacter,
                InputActionUtility.IsPressed(cameraModifierAction), cursor != null && cursor.Aiming);
```

- [ ] **Step 6: Run the tests**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.StickRoleAimingTests"` (9 tests, EXIT=0), `Tools/run-tests.sh PlayMode "Blackglass.Tests.TacticalCursorAimingTests"` (6), `Tools/run-tests.sh PlayMode "Blackglass.Tests.CameraAimingTests"` (2). If a snapping probe fails because a collider sits under the cursor point, adjust the test's world positions (not the production code) and report it.
Then both full suites: EditMode 493 + 9 = 502, PlayMode 456 + 8 = 464, EXIT=0; `TacticalCursorTests`, `CameraGamepadTests` and `ControllerCommandInputTests` must pass unchanged.

- [ ] **Step 7: Commit**

```bash
git add Assets/_Project/Scripts Assets/_Project/Tests
git commit -m "Let an armed ability hand the right stick to the cursor and narrow what the cursor snaps to

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 8: Ability targeting — arm, preview, confirm (mouse and controller)

**Files:**
- Create: `Assets/_Project/Scripts/Abilities/AbilityTargeting.cs`, `Assets/_Project/Tests/PlayMode/TestSupport/AbilityRig.cs`
- Modify: `Assets/_Project/Scripts/Controls/PlayerCommandInput.cs`
- Test: `Assets/_Project/Tests/PlayMode/AbilityTargetingTests.cs` (keyboard and mouse), `Assets/_Project/Tests/PlayMode/AbilityTargetingPadTests.cs` (controller)

**Interfaces:**
- Consumes: `UnitAbilities` (`Definition`, `Count`, `Check`, `CollectArea`, `ReportFailure`, `LastFailure`), `AbilityCommand`, `CommandableUnit.Issue/CurrentCommand/IsAlive`, `UnitSelection.Selected`, `ActiveCharacter.Unit/HasUnit/IsEligible`, `TacticalCursor` (`Aiming`, `SnapTo`, `IsActive`, `Target`), `PointerTarget`/`PointerTargetResolver`, `InputActionUtility`, `AbilityMenuGate`.
- Produces:
  - `readonly struct AbilityPreview` (`Ability`, `Kind`, `Target`, `Point`, `HasAim`, `Check`, `Queued`, `IsArmed`, `IsValid`, `Failure`; `static None`).
  - `sealed class AbilityTargeting : MonoBehaviour`: `CommandableUnit Caster`, `UnitAbilities CasterAbilities`, `int ArmedSlot` (-1 when not armed), `bool IsArmed`, `AbilityDefinition ArmedAbility`, `AbilityPreview Preview`, `IReadOnlyList<Health> AreaHits` (the hostiles a ground ability would hit now), `bool Arm(int slot)`, `void Disarm()`, `bool Confirm(PointerTarget pointed, bool queue)` (issues the command with Append when `queue`, else Replace; disarms on success; returns whether the order was accepted; on a refusal the reason is on `CasterAbilities.LastFailure` and the ability stays armed), `internal AbilityPreview Evaluate(AbilityDefinition ability, PointerTarget pointed, bool queued)`, `internal void Pick(int slot)` (arm, or disarm when that slot is already armed), `internal void Initialize(Camera camera, UnitSelection selection, ActiveCharacter active, TacticalCursor cursor, InputActionReference pointerPosition, InputActionReference queueModifier, InputActionReference ability1, InputActionReference ability2, InputActionReference ability3, InputActionReference ability4)`. Serialized field names (the scene builder sets them): `viewCamera`, `selection`, `activeCharacter`, `cursor`, `pointerPositionAction`, `queueModifierAction`, `ability1Action`..`ability4Action`.
  - `PlayerCommandInput`: serialized field `abilityTargeting` and a trailing optional `AbilityTargeting targeting = null` parameter on `Initialize`. While armed, `Act` hands the click or cursor Confirm to `AbilityTargeting.Confirm(target, ModifierHeld)` instead of giving an order, and the first Cancel disarms before it clears the selection.
  - Test support: `AbilityRig.Build(InputActionAsset actions, bool withCursor)`.

**Caster rule (Deviation 2):** the first eligible selected unit, else the active character (also while paused).

- [ ] **Step 1: Write the test rig**

`Assets/_Project/Tests/PlayMode/TestSupport/AbilityRig.cs`:

```csharp
#if UNITY_EDITOR
using UnityEngine;
using UnityEngine.InputSystem;

namespace Blackglass.Tests
{
    /// <summary>
    /// The common arrangement of the ability input tests: a caster with the three prototype abilities, an ally, hostiles
    /// (one far, one behind a wall) in an open arena, a camera, and the input components wired like the scene. Nothing is
    /// selected at the start: the active character (the caster) is who abilities are cast by.
    /// </summary>
    internal sealed class AbilityRig
    {
        public static readonly Vector3 CasterGround = new Vector3(0f, 0f, -6f);
        public static readonly Vector3 AllyGround = new Vector3(4f, 0f, -6f);
        public static readonly Vector3 HostileGround = new Vector3(3f, 0f, 2f);
        public static readonly Vector3 FarHostileGround = new Vector3(7f, 0f, 8f);
        // Behind a 3 m wall (x -8..-4, z -0.25..0.25) as seen from the caster.
        public static readonly Vector3 WalledHostileGround = new Vector3(-6f, 0f, 2f);
        // Open ground 2 m east of the near hostile.
        public static readonly Vector3 BlastGround = new Vector3(5f, 0f, 2f);

        public TestWorld World;
        public Camera ViewCamera;
        public TacticalPause Pause;
        public UnitSelection Selection;
        public ActiveCharacter Active;
        public Encounter Encounter;
        public SelectableUnit Caster;
        public SelectableUnit Ally;
        public UnitAbilities Abilities;
        public Health CasterHealth;
        public Health AllyHealth;
        public Health Hostile;
        public Health FarHostile;
        public Health WalledHostile;
        public PlayerCommandInput Input;
        public AbilityTargeting Targeting;
        public TacticalCursor Cursor;
        public AbilityMenuGate Gate;
        public AbilityDefinition Aimed;
        public AbilityDefinition Blast;
        public AbilityDefinition Mend;

        public Vector2 ScreenPointOf(Vector3 worldPoint) => ViewCamera.WorldToScreenPoint(worldPoint);

        public static AbilityRig Build(InputActionAsset actions, bool withCursor)
        {
            var rig = new AbilityRig { World = new TestWorld() };
            var world = rig.World;
            world.CreateEnvironment((new Vector3(-6f, 1.5f, 0f), new Vector3(4f, 3f, 0.5f)));
            rig.Encounter = world.CreateEncounter();
            rig.Aimed = world.CreateAimedShot();
            rig.Blast = world.CreateBlast();
            rig.Mend = world.CreateMend();
            rig.Caster = world.CreateFriendlyFighter(CasterGround);
            rig.Ally = world.CreateFriendlyFighter(AllyGround);
            rig.CasterHealth = rig.Caster.GetComponent<Health>();
            rig.AllyHealth = rig.Ally.GetComponent<Health>();
            rig.Abilities = world.AddAbilities(rig.Caster.Unit, rig.Encounter, rig.Aimed, rig.Blast, rig.Mend);
            rig.Hostile = world.CreateDummy(HostileGround);
            rig.FarHostile = world.CreateDummy(FarHostileGround);
            rig.WalledHostile = world.CreateDummy(WalledHostileGround);
            rig.Encounter.Initialize(new[] { rig.CasterHealth, rig.AllyHealth }, new[] { rig.Hostile, rig.FarHostile, rig.WalledHostile });

            var cameraObject = world.Track(new GameObject("Camera"));
            cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 25f, -20f), Quaternion.Euler(50f, 0f, 0f));
            rig.ViewCamera = cameraObject.AddComponent<Camera>();

            var systems = world.Track(new GameObject("Systems"));
            systems.SetActive(false);
            rig.Pause = systems.AddComponent<TacticalPause>();
            rig.Selection = systems.AddComponent<UnitSelection>();
            rig.Selection.Initialize(rig.Caster, rig.Ally);
            rig.Active = systems.AddComponent<ActiveCharacter>();
            rig.Active.Initialize(rig.Caster.Unit, rig.Pause, rig.Selection);
            if (withCursor)
            {
                rig.Cursor = systems.AddComponent<TacticalCursor>();
                rig.Cursor.Initialize(rig.ViewCamera, rig.Active, rig.Selection, rig.Encounter, null, null,
                    TestControls.Ref(actions, "Commands/CursorMove"),
                    TestControls.Ref(actions, "Camera/CameraModifier"),
                    TestControls.Ref(actions, "Commands/NextTarget"),
                    TestControls.Ref(actions, "Commands/PreviousTarget"));
                rig.Gate = systems.AddComponent<AbilityMenuGate>();
                rig.Gate.Initialize(TestControls.Ref(actions, "Commands/AbilityMenu"),
                    TestControls.Ref(actions, "Commands/Stop"), TestControls.Ref(actions, "Character/ToggleFollow"),
                    TestControls.Ref(actions, "Commands/NextTarget"), TestControls.Ref(actions, "Commands/PreviousTarget"));
            }
            rig.Targeting = systems.AddComponent<AbilityTargeting>();
            rig.Targeting.Initialize(rig.ViewCamera, rig.Selection, rig.Active, rig.Cursor,
                TestControls.Ref(actions, "Commands/PointerPosition"), TestControls.Ref(actions, "Commands/QueueModifier"),
                TestControls.Ref(actions, "Commands/Ability1"), TestControls.Ref(actions, "Commands/Ability2"),
                TestControls.Ref(actions, "Commands/Ability3"), TestControls.Ref(actions, "Commands/Ability4"));
            rig.Input = systems.AddComponent<PlayerCommandInput>();
            rig.Input.Initialize(rig.ViewCamera, rig.Selection, rig.Pause,
                TestControls.Ref(actions, "Commands/Command"),
                TestControls.Ref(actions, "Commands/PointerPosition"),
                TestControls.Ref(actions, "Commands/ToggleTacticalPause"),
                TestControls.Ref(actions, "Commands/QueueModifier"),
                TestControls.Ref(actions, "Commands/Stop"),
                TestControls.Ref(actions, "Commands/Cancel"),
                rig.Active, null, rig.Cursor,
                withCursor ? TestControls.Ref(actions, "Commands/Confirm") : null,
                withCursor ? TestControls.Ref(actions, "Commands/Attack") : null,
                rig.Targeting);
            systems.SetActive(true);
            return rig;
        }
    }
}
#endif
```

- [ ] **Step 2: Write the failing mouse/keyboard tests**

`Assets/_Project/Tests/PlayMode/AbilityTargetingTests.cs`:

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
    public class AbilityTargetingTests : InputTestFixture
    {
        Keyboard keyboard;
        Mouse mouse;
        InputActionAsset actions;
        AbilityRig rig;

        public override void Setup()
        {
            base.Setup();
            keyboard = InputSystem.AddDevice<Keyboard>();
            mouse = InputSystem.AddDevice<Mouse>();
            actions = TestControls.Load();
            rig = AbilityRig.Build(actions, false);
        }

        public override void TearDown()
        {
            rig.World.Dispose();
            Time.timeScale = 1f;
            TestControls.Reset(actions);
            base.TearDown();
        }

        IEnumerator Tap(ButtonControl key)
        {
            Press(key);
            yield return null;
            Release(key);
            yield return null;
        }

        IEnumerator LeftClickAt(Vector2 screenPoint)
        {
            Set(mouse.position, screenPoint);
            yield return null;
            Press(mouse.leftButton);
            yield return null;
            Release(mouse.leftButton);
            yield return null;
        }

        [UnityTest]
        public IEnumerator DigitOne_ArmsTheFirstAbility_PressingItAgainDisarms_AndAnotherDigitSwitches()
        {
            yield return null;
            Assert.That(rig.Targeting.IsArmed, Is.False);

            yield return Tap(keyboard.digit1Key);
            Assert.That(rig.Targeting.IsArmed, Is.True);
            Assert.That(rig.Targeting.ArmedSlot, Is.EqualTo(0));
            Assert.That(rig.Targeting.ArmedAbility, Is.SameAs(rig.Aimed));
            Assert.That(rig.Targeting.Caster, Is.SameAs(rig.Caster.Unit), "No selection: the active character casts");

            yield return Tap(keyboard.digit2Key);
            Assert.That(rig.Targeting.ArmedSlot, Is.EqualTo(1));
            Assert.That(rig.Targeting.ArmedAbility, Is.SameAs(rig.Blast));

            yield return Tap(keyboard.digit2Key);
            Assert.That(rig.Targeting.IsArmed, Is.False, "The same key again backs out");
        }

        [UnityTest]
        public IEnumerator AnEmptySlot_ArmsNothing()
        {
            yield return null;

            yield return Tap(keyboard.digit4Key);

            Assert.That(rig.Targeting.IsArmed, Is.False);
        }

        [UnityTest]
        public IEnumerator ClickingAHostileInRange_UsesAimedShot_AndDisarms_WithoutSelectingAnything()
        {
            yield return null;
            yield return Tap(keyboard.digit1Key);

            yield return LeftClickAt(rig.ScreenPointOf(rig.Hostile.transform.position));
            yield return TestWorld.WaitUntil(() => rig.Hostile.Current < rig.Hostile.Max, 2f);

            Assert.That(rig.Hostile.Current, Is.EqualTo(rig.Hostile.Max - 45));
            Assert.That(rig.Targeting.IsArmed, Is.False);
            Assert.That(rig.Selection.Selected, Is.Empty, "The click picked a target, it did not select or order");
            Assert.That(rig.Caster.Unit.CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator AUnitAbility_ClickedOnTheGround_ReportsNoTarget_AndStaysArmed()
        {
            yield return null;
            yield return Tap(keyboard.digit1Key);

            yield return LeftClickAt(rig.ScreenPointOf(new Vector3(8f, 0f, -2f)));

            Assert.That(rig.Abilities.LastFailure, Is.EqualTo(AbilityFailure.NoTarget));
            Assert.That(rig.Targeting.IsArmed, Is.True);
            Assert.That(rig.Hostile.Current, Is.EqualTo(rig.Hostile.Max));
        }

        [UnityTest]
        public IEnumerator AnAttack_ClickedOnAFriendly_IsRefusedAsTheWrongSide()
        {
            yield return null;
            yield return Tap(keyboard.digit1Key);

            yield return LeftClickAt(rig.ScreenPointOf(rig.Ally.transform.position));

            Assert.That(rig.Abilities.LastFailure, Is.EqualTo(AbilityFailure.WrongSide));
            Assert.That(rig.AllyHealth.Current, Is.EqualTo(rig.AllyHealth.Max));
            Assert.That(rig.Targeting.IsArmed, Is.True);
            Assert.That(rig.Selection.Selected, Is.Empty, "While armed a click on a friendly does not select it");
        }

        [UnityTest]
        public IEnumerator AHostileOutOfRange_IsRefusedWithItsReason_AndNothingHappens()
        {
            yield return null;
            yield return Tap(keyboard.digit1Key);

            yield return LeftClickAt(rig.ScreenPointOf(rig.FarHostile.transform.position));
            yield return new WaitForSeconds(0.2f);

            Assert.That(rig.Abilities.LastFailure, Is.EqualTo(AbilityFailure.OutOfRange));
            Assert.That(rig.FarHostile.Current, Is.EqualTo(rig.FarHostile.Max));
            Assert.That(rig.Targeting.IsArmed, Is.True, "Still armed: try another target");
            Assert.That(rig.Caster.Unit.CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator Blast_ClickedOnTheGround_HitsOnlyTheHostilesNearThePoint()
        {
            yield return null;
            yield return Tap(keyboard.digit2Key);

            yield return LeftClickAt(rig.ScreenPointOf(AbilityRig.BlastGround));
            yield return TestWorld.WaitUntil(() => rig.Hostile.Current < rig.Hostile.Max, 2f);

            Assert.That(rig.Hostile.Current, Is.EqualTo(rig.Hostile.Max - 35), "2 m from the blast point");
            Assert.That(rig.FarHostile.Current, Is.EqualTo(rig.FarHostile.Max));
            Assert.That(rig.AllyHealth.Current, Is.EqualTo(rig.AllyHealth.Max));
            Assert.That(rig.CasterHealth.Current, Is.EqualTo(rig.CasterHealth.Max));
            Assert.That(rig.Targeting.IsArmed, Is.False);
            Assert.That(rig.Abilities.IsReady(1), Is.False);
        }

        [UnityTest]
        public IEnumerator Mend_ClickedOnAnAlly_HealsThem()
        {
            yield return null;
            rig.AllyHealth.TakeDamage(60);
            yield return Tap(keyboard.digit3Key);

            yield return LeftClickAt(rig.ScreenPointOf(rig.Ally.transform.position));
            yield return TestWorld.WaitUntil(() => rig.AllyHealth.Current > rig.AllyHealth.Max - 60, 2f);

            Assert.That(rig.AllyHealth.Current, Is.EqualTo(rig.AllyHealth.Max - 20));
        }

        [UnityTest]
        public IEnumerator Escape_DisarmsFirst_AndOnlyTheNextEscapeClearsTheSelection()
        {
            yield return null;
            rig.Selection.Select(rig.Caster);
            yield return Tap(keyboard.digit1Key);

            yield return Tap(keyboard.escapeKey);
            Assert.That(rig.Targeting.IsArmed, Is.False);
            Assert.That(rig.Selection.Selected, Has.Count.EqualTo(1), "The first Escape only backed out of the ability");

            yield return Tap(keyboard.escapeKey);
            Assert.That(rig.Selection.Selected, Is.Empty);
        }

        [UnityTest]
        public IEnumerator ChangingTheSelection_DisarmsTheAbility()
        {
            yield return null;
            rig.Selection.Select(rig.Caster);
            yield return Tap(keyboard.digit1Key);
            Assert.That(rig.Targeting.IsArmed, Is.True);

            rig.Selection.Select(rig.Ally);
            yield return null;

            Assert.That(rig.Targeting.IsArmed, Is.False, "A different caster: nothing stays armed");
            Assert.That(rig.Targeting.Caster, Is.SameAs(rig.Ally.Unit));
        }

        [UnityTest]
        public IEnumerator TheCasterDying_DisarmsTheAbility()
        {
            yield return null;
            yield return Tap(keyboard.digit1Key);

            rig.CasterHealth.TakeDamage(rig.CasterHealth.Max);
            yield return null;
            yield return null;

            Assert.That(rig.Targeting.IsArmed, Is.False);
            yield return Tap(keyboard.digit1Key);
            Assert.That(rig.Targeting.IsArmed, Is.False, "The ally who inherits the role has no abilities");
        }

        [UnityTest]
        public IEnumerator Paused_AClickQueuesTheAbility_ButNothingRunsUntilTheGameResumes()
        {
            yield return null;
            rig.Selection.Select(rig.Caster);
            rig.Pause.Pause();
            yield return Tap(keyboard.digit1Key);

            yield return LeftClickAt(rig.ScreenPointOf(rig.Hostile.transform.position));
            yield return new WaitForSecondsRealtime(0.3f);

            Assert.That(rig.Caster.Unit.CurrentCommand, Is.TypeOf<AbilityCommand>());
            Assert.That(rig.Hostile.Current, Is.EqualTo(rig.Hostile.Max), "Nothing runs while paused");
            Assert.That(rig.Abilities.IsReady(0), Is.True);

            rig.Pause.Resume();
            yield return TestWorld.WaitUntil(() => rig.Hostile.Current < rig.Hostile.Max, 2f);
            Assert.That(rig.Hostile.Current, Is.EqualTo(rig.Hostile.Max - 45));
        }

        [UnityTest]
        public IEnumerator ShiftClick_QueuesTheAbilityBehindTheCastersOtherOrders()
        {
            yield return null;
            rig.Selection.Select(rig.Caster);
            rig.Pause.Pause();
            Assert.That(rig.Caster.Unit.Issue(new MoveCommand(new Vector3(-6f, 0f, -8f))), Is.True);
            yield return Tap(keyboard.digit1Key);

            Press(keyboard.leftShiftKey);
            yield return null;
            yield return LeftClickAt(rig.ScreenPointOf(rig.Hostile.transform.position));
            Release(keyboard.leftShiftKey);
            yield return null;

            Assert.That(rig.Caster.Unit.CurrentCommand, Is.TypeOf<MoveCommand>());
            Assert.That(rig.Caster.Unit.PendingCommands, Has.Count.EqualTo(1));
            Assert.That(rig.Caster.Unit.PendingCommands[0], Is.TypeOf<AbilityCommand>());
        }

        [UnityTest]
        public IEnumerator ThePreview_FollowsTheMouse_AndSaysWhy()
        {
            yield return null;
            yield return Tap(keyboard.digit1Key);

            Set(mouse.position, rig.ScreenPointOf(rig.Hostile.transform.position));
            yield return null;
            yield return null;
            Assert.That(rig.Targeting.Preview.IsValid, Is.True);
            Assert.That(rig.Targeting.Preview.Target, Is.SameAs(rig.Hostile));
            Assert.That(rig.Targeting.Preview.Check.Distance, Is.EqualTo(Mathf.Sqrt(9f + 64f)).Within(0.05f));

            Set(mouse.position, rig.ScreenPointOf(rig.FarHostile.transform.position));
            yield return null;
            yield return null;
            Assert.That(rig.Targeting.Preview.IsValid, Is.False);
            Assert.That(rig.Targeting.Preview.Failure, Is.EqualTo(AbilityFailure.OutOfRange));

            Set(mouse.position, rig.ScreenPointOf(new Vector3(8f, 0f, -2f)));
            yield return null;
            yield return null;
            Assert.That(rig.Targeting.Preview.HasAim, Is.False);
            Assert.That(rig.Targeting.Preview.Failure, Is.EqualTo(AbilityFailure.NoTarget));
        }

        [UnityTest]
        public IEnumerator ABlastPreview_ListsTheHostilesItWouldHit()
        {
            yield return null;
            yield return Tap(keyboard.digit2Key);

            Set(mouse.position, rig.ScreenPointOf(AbilityRig.BlastGround));
            yield return null;
            yield return null;

            Assert.That(rig.Targeting.Preview.IsValid, Is.True);
            Assert.That(rig.Targeting.AreaHits, Is.EqualTo(new[] { rig.Hostile }));
        }

        [UnityTest]
        public IEnumerator Evaluate_ATargetBehindAWall_HasNoLineOfSight()
        {
            yield return null;
            yield return Tap(keyboard.digit1Key);

            var preview = rig.Targeting.Evaluate(rig.Aimed, PointerTarget.OnHostile(rig.WalledHostile, rig.WalledHostile.transform.position), false);

            Assert.That(preview.Failure, Is.EqualTo(AbilityFailure.NoLineOfSight));
            Assert.That(preview.Check.SightClear, Is.False);
        }

        [UnityTest]
        public IEnumerator HoldingTheQueueKey_WhileTheCasterHasOrders_PreviewsOnlyTheStaticChecks()
        {
            yield return null;
            rig.Selection.Select(rig.Caster);
            Assert.That(rig.Caster.Unit.Issue(new MoveCommand(new Vector3(-6f, 0f, -8f))), Is.True);
            yield return Tap(keyboard.digit1Key);
            Set(mouse.position, rig.ScreenPointOf(rig.FarHostile.transform.position));
            yield return null;
            yield return null;
            Assert.That(rig.Targeting.Preview.Failure, Is.EqualTo(AbilityFailure.OutOfRange), "Replacing the order: judged from here");

            Press(keyboard.leftShiftKey);
            yield return null;
            yield return null;

            Assert.That(rig.Targeting.Preview.Queued, Is.True);
            Assert.That(rig.Targeting.Preview.IsValid, Is.True, "Queued: range is judged when it runs");
            Release(keyboard.leftShiftKey);
            yield return null;
        }
    }
}
#endif
```

- [ ] **Step 3: Write the failing controller tests**

`Assets/_Project/Tests/PlayMode/AbilityTargetingPadTests.cs`:

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
    public class AbilityTargetingPadTests : InputTestFixture
    {
        Gamepad pad;
        InputActionAsset actions;
        AbilityRig rig;

        public override void Setup()
        {
            base.Setup();
            pad = InputSystem.AddDevice<XInputController>();
            actions = TestControls.Load();
            TestControls.UseGroup(actions, "Xbox");
            rig = AbilityRig.Build(actions, true);
        }

        public override void TearDown()
        {
            rig.World.Dispose();
            Time.timeScale = 1f;
            TestControls.Reset(actions);
            base.TearDown();
        }

        IEnumerator Tap(ButtonControl button)
        {
            Press(button);
            yield return null;
            Release(button);
            yield return null;
        }

        // Holds the right trigger, taps a D-pad direction, releases the trigger: the ability menu gesture.
        IEnumerator PickWithTheMenu(ButtonControl direction)
        {
            Press(pad.rightTrigger);
            yield return null;
            yield return Tap(direction);
            Release(pad.rightTrigger);
            yield return null;
            yield return null;
        }

        [UnityTest]
        public IEnumerator TriggerPlusDpadUp_ArmsAimedShot_AndTheCursorOwnsTheStickEvenWhileTheGameRuns()
        {
            yield return null;
            Assert.That(rig.Cursor.IsActive, Is.False, "Precondition: running, the camera owns the stick");

            yield return PickWithTheMenu(pad.dpad.up);

            Assert.That(rig.Targeting.IsArmed, Is.True);
            Assert.That(rig.Targeting.ArmedAbility, Is.SameAs(rig.Aimed));
            Assert.That(rig.Cursor.Aiming, Is.True);
            Assert.That(rig.Cursor.IsActive, Is.True, "Aiming hands the stick to the cursor");
            Assert.That(rig.Cursor.SnapTo, Is.EqualTo(PointerTargetKind.Hostile));
        }

        [UnityTest]
        public IEnumerator EachDpadDirection_PicksItsOwnSlot()
        {
            yield return null;

            yield return PickWithTheMenu(pad.dpad.right);
            Assert.That(rig.Targeting.ArmedSlot, Is.EqualTo(1));
            Assert.That(rig.Cursor.SnapTo, Is.EqualTo(PointerTargetKind.Ground), "A blast is aimed freely");

            yield return PickWithTheMenu(pad.dpad.down);
            Assert.That(rig.Targeting.ArmedSlot, Is.EqualTo(2));
            Assert.That(rig.Cursor.SnapTo, Is.EqualTo(PointerTargetKind.Friendly), "A heal snaps to friendlies");

            yield return PickWithTheMenu(pad.dpad.left);
            Assert.That(rig.Targeting.ArmedSlot, Is.EqualTo(2), "Slot 4 is empty: the armed slot does not change");
        }

        [UnityTest]
        public IEnumerator DpadDown_WithTheTriggerHeld_PicksMend_AndDoesNotStopTheCaster_ButStopWorksAfterwards()
        {
            yield return null;
            rig.Selection.Select(rig.Caster);
            Assert.That(rig.Caster.Unit.Issue(new MoveCommand(new Vector3(-6f, 0f, -10f))), Is.True);

            yield return PickWithTheMenu(pad.dpad.down);

            Assert.That(rig.Targeting.ArmedSlot, Is.EqualTo(2));
            Assert.That(rig.Caster.Unit.StopCount, Is.EqualTo(0), "The held trigger turns the D-pad into ability slots only");
            Assert.That(rig.Caster.Unit.CurrentCommand, Is.TypeOf<MoveCommand>());

            yield return Tap(pad.dpad.down);
            Assert.That(rig.Caster.Unit.StopCount, Is.EqualTo(1), "Released: the D-pad stops the unit again");
        }

        [UnityTest]
        public IEnumerator Confirm_OnAHostile_UsesAimedShot_ThenTheCursorIsAnOrdinaryCursorAgain()
        {
            yield return null;
            yield return PickWithTheMenu(pad.dpad.up);
            rig.Cursor.SetScreenPosition(rig.ScreenPointOf(rig.Hostile.transform.position));
            yield return null;

            yield return Tap(pad.buttonSouth);
            yield return TestWorld.WaitUntil(() => rig.Hostile.Current < rig.Hostile.Max, 2f);

            Assert.That(rig.Hostile.Current, Is.EqualTo(rig.Hostile.Max - 45));
            Assert.That(rig.Targeting.IsArmed, Is.False);
            Assert.That(rig.Cursor.Aiming, Is.False);
            Assert.That(rig.Cursor.SnapTo, Is.EqualTo(PointerTargetKind.None));
            Assert.That(rig.Cursor.IsActive, Is.False, "Running again with nothing armed: the camera has its stick back");
        }

        [UnityTest]
        public IEnumerator Blast_OnAPad_UsesTheExactCursorPoint()
        {
            yield return null;
            yield return PickWithTheMenu(pad.dpad.right);
            rig.Cursor.SetScreenPosition(rig.ScreenPointOf(AbilityRig.BlastGround));
            yield return null;
            Assert.That(rig.Targeting.Preview.IsValid, Is.True);

            yield return Tap(pad.buttonSouth);
            yield return TestWorld.WaitUntil(() => rig.Hostile.Current < rig.Hostile.Max, 2f);

            Assert.That(rig.Hostile.Current, Is.EqualTo(rig.Hostile.Max - 35));
        }

        [UnityTest]
        public IEnumerator Mend_OnAPad_SnapsToTheAlly_AndHealsThem()
        {
            yield return null;
            rig.AllyHealth.TakeDamage(60);
            yield return PickWithTheMenu(pad.dpad.down);
            rig.Cursor.SetScreenPosition(rig.ScreenPointOf(rig.Ally.transform.position + new Vector3(0.6f, -1f, 0f)));
            yield return null;
            Assert.That(rig.Cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Friendly), "Snapped to the ally");

            yield return Tap(pad.buttonSouth);
            yield return TestWorld.WaitUntil(() => rig.AllyHealth.Current > rig.AllyHealth.Max - 60, 2f);

            Assert.That(rig.AllyHealth.Current, Is.EqualTo(rig.AllyHealth.Max - 20));
        }

        [UnityTest]
        public IEnumerator Cancel_DisarmsFirst_AndOnlyTheNextCancelClearsTheSelection()
        {
            yield return null;
            rig.Selection.Select(rig.Caster);
            yield return PickWithTheMenu(pad.dpad.up);

            yield return Tap(pad.buttonEast);
            Assert.That(rig.Targeting.IsArmed, Is.False);
            Assert.That(rig.Cursor.Aiming, Is.False);
            Assert.That(rig.Selection.Selected, Has.Count.EqualTo(1));

            yield return Tap(pad.buttonEast);
            Assert.That(rig.Selection.Selected, Is.Empty);
        }

        [UnityTest]
        public IEnumerator Paused_TheQueueModifierQueuesTheAbilityBehindTheCastersOrders()
        {
            yield return null;
            rig.Selection.Select(rig.Caster);
            rig.Pause.Pause();
            Assert.That(rig.Caster.Unit.Issue(new MoveCommand(new Vector3(-6f, 0f, -8f))), Is.True);
            yield return PickWithTheMenu(pad.dpad.up);
            rig.Cursor.SetScreenPosition(rig.ScreenPointOf(rig.Hostile.transform.position));
            yield return null;

            Press(pad.leftTrigger);
            yield return null;
            yield return Tap(pad.buttonSouth);
            Release(pad.leftTrigger);
            yield return null;

            Assert.That(rig.Caster.Unit.CurrentCommand, Is.TypeOf<MoveCommand>());
            Assert.That(rig.Caster.Unit.PendingCommands, Has.Count.EqualTo(1));
            Assert.That(rig.Caster.Unit.PendingCommands[0], Is.TypeOf<AbilityCommand>());
            Assert.That(rig.Hostile.Current, Is.EqualTo(rig.Hostile.Max), "Paused: nothing ran");
        }

        [UnityTest]
        public IEnumerator Paused_ThePadPlansTheAbility_AndItRunsAfterResume()
        {
            yield return null;
            rig.Selection.Select(rig.Caster);
            rig.Pause.Pause();
            yield return PickWithTheMenu(pad.dpad.up);
            rig.Cursor.SetScreenPosition(rig.ScreenPointOf(rig.Hostile.transform.position));
            yield return null;

            yield return Tap(pad.buttonSouth);
            Assert.That(rig.Caster.Unit.CurrentCommand, Is.TypeOf<AbilityCommand>());
            yield return new WaitForSecondsRealtime(0.2f);
            Assert.That(rig.Hostile.Current, Is.EqualTo(rig.Hostile.Max));

            rig.Pause.Resume();
            yield return TestWorld.WaitUntil(() => rig.Hostile.Current < rig.Hostile.Max, 2f);
            Assert.That(rig.Hostile.Current, Is.EqualTo(rig.Hostile.Max - 45));
        }

        [UnityTest]
        public IEnumerator DpadCycling_WhileMendIsArmed_WalksTheFriendlies_AndNeverSetsTheSoftTarget()
        {
            yield return null;
            yield return PickWithTheMenu(pad.dpad.down);
            rig.Cursor.SetScreenPosition(rig.ScreenPointOf(rig.Caster.transform.position));
            yield return null;
            Assert.That(rig.Cursor.Target.Friendly, Is.SameAs(rig.Caster), "Precondition");

            yield return Tap(pad.dpad.right);

            Assert.That(rig.Cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Friendly));
            Assert.That(rig.Cursor.Target.Friendly, Is.SameAs(rig.Ally));
            Assert.That(rig.Cursor.SoftTarget, Is.Null, "Cycling friendlies never sets the soft target Attack uses");
            Assert.That(rig.Targeting.Preview.Target, Is.SameAs(rig.AllyHealth));
        }
    }
}
#endif
```

- [ ] **Step 4: Run to verify they fail to compile**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.AbilityTargetingTests"`
Expected: EXIT=1 with `error CS0246` for `AbilityTargeting` and `AbilityPreview`.

- [ ] **Step 5: Implement `AbilityTargeting`**

`Assets/_Project/Scripts/Abilities/AbilityTargeting.cs`:

```csharp
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Blackglass
{
    /// <summary>The live answer to "what would happen if I confirmed now": the armed ability, what the pointer is on, its validation.</summary>
    public readonly struct AbilityPreview
    {
        public AbilityPreview(AbilityDefinition ability, PointerTargetKind kind, Health target, Vector3 point, bool hasAim,
            AbilityCheck check, bool queued)
        {
            Ability = ability;
            Kind = kind;
            Target = target;
            Point = point;
            HasAim = hasAim;
            Check = check;
            Queued = queued;
        }

        public static AbilityPreview None => default;

        /// <summary>The armed ability; null when nothing is armed.</summary>
        public AbilityDefinition Ability { get; }
        public PointerTargetKind Kind { get; }
        /// <summary>The unit pointed at (unit abilities), else null.</summary>
        public Health Target { get; }
        /// <summary>The aim: the target's position, or the ground point.</summary>
        public Vector3 Point { get; }
        /// <summary>False while the pointer is on nothing usable (no unit under it, or off the world).</summary>
        public bool HasAim { get; }
        public AbilityCheck Check { get; }
        /// <summary>The queue modifier is held and the caster has orders: only the static checks were run (it is judged when it runs).</summary>
        public bool Queued { get; }

        public bool IsArmed => Ability != null;
        public bool IsValid => HasAim && Check.IsValid;

        public AbilityFailure Failure =>
            !IsArmed ? AbilityFailure.None
            : HasAim ? Check.Failure
            : Ability.TargetMode == AbilityTargetMode.Ground ? AbilityFailure.NoPosition : AbilityFailure.NoTarget;
    }

    /// <summary>
    /// The armed ability and its aiming. Input (the Ability1..4 actions) arms a slot of the caster's abilities; while
    /// armed this resolves what the pointer is on every frame (the mouse through PointerTargetResolver, the controller
    /// through the TacticalCursor, which it also puts into aiming mode and tells what to snap to), validates it with the
    /// caster's UnitAbilities, and exposes the result as Preview for the views. Confirm turns a click or the cursor's
    /// Confirm into an AbilityCommand through the normal Issue path (so tactical queueing, pause and direct control all
    /// share it) and disarms. It holds no ability rules and no combat. The caster is the first eligible selected unit,
    /// else the active character; arming never survives a change of caster or the caster's death.
    /// </summary>
    public sealed class AbilityTargeting : MonoBehaviour
    {
        [SerializeField] Camera viewCamera;
        [SerializeField] UnitSelection selection;
        [SerializeField] ActiveCharacter activeCharacter;
        // Optional: the controller's pointer. Armed, it owns the right stick and snaps to the side the ability needs.
        [SerializeField] TacticalCursor cursor;
        [SerializeField, Min(1f)] float maxDistance = 500f;
        [SerializeField] LayerMask clickableLayers = ~0;

        [Header("Input")]
        // The pointer and the queue modifier are owned (and disabled) by PlayerCommandInput; this only enables and reads them.
        [SerializeField] InputActionReference pointerPositionAction;
        [SerializeField] InputActionReference queueModifierAction;
        [SerializeField] InputActionReference ability1Action;
        [SerializeField] InputActionReference ability2Action;
        [SerializeField] InputActionReference ability3Action;
        [SerializeField] InputActionReference ability4Action;

        readonly List<Health> areaHits = new List<Health>();
        CommandableUnit caster;
        UnitAbilities casterAbilities;
        int armedSlot = -1;
        AbilityPreview preview;

        public CommandableUnit Caster => caster;
        public UnitAbilities CasterAbilities => casterAbilities;
        public int ArmedSlot => armedSlot;
        public bool IsArmed => armedSlot >= 0;
        public AbilityDefinition ArmedAbility => IsArmed && casterAbilities != null ? casterAbilities.Definition(armedSlot) : null;
        public AbilityPreview Preview => preview;

        /// <summary>The hostiles a ground ability would hit at the current aim (valid until the next update).</summary>
        public IReadOnlyList<Health> AreaHits => areaHits;

        bool QueueHeld => InputActionUtility.IsPressed(queueModifierAction);

        internal void Initialize(Camera camera, UnitSelection unitSelection, ActiveCharacter active, TacticalCursor tacticalCursor,
            InputActionReference pointerPosition, InputActionReference queueModifier, InputActionReference ability1,
            InputActionReference ability2, InputActionReference ability3, InputActionReference ability4)
        {
            viewCamera = camera;
            selection = unitSelection;
            activeCharacter = active;
            cursor = tacticalCursor;
            pointerPositionAction = pointerPosition;
            queueModifierAction = queueModifier;
            ability1Action = ability1;
            ability2Action = ability2;
            ability3Action = ability3;
            ability4Action = ability4;
        }

        void OnEnable()
        {
            InputActionUtility.SetEnabled(true, pointerPositionAction, queueModifierAction, ability1Action, ability2Action,
                ability3Action, ability4Action);
            Subscribe(ability1Action, OnAbility1);
            Subscribe(ability2Action, OnAbility2);
            Subscribe(ability3Action, OnAbility3);
            Subscribe(ability4Action, OnAbility4);
        }

        void OnDisable()
        {
            Unsubscribe(ability1Action, OnAbility1);
            Unsubscribe(ability2Action, OnAbility2);
            Unsubscribe(ability3Action, OnAbility3);
            Unsubscribe(ability4Action, OnAbility4);
            InputActionUtility.SetEnabled(false, ability1Action, ability2Action, ability3Action, ability4Action);
            Disarm();
        }

        static void Subscribe(InputActionReference reference, System.Action<InputAction.CallbackContext> handler)
        {
            if (reference != null)
                reference.action.performed += handler;
        }

        static void Unsubscribe(InputActionReference reference, System.Action<InputAction.CallbackContext> handler)
        {
            if (reference != null)
                reference.action.performed -= handler;
        }

        void OnAbility1(InputAction.CallbackContext context) => Pick(0);
        void OnAbility2(InputAction.CallbackContext context) => Pick(1);
        void OnAbility3(InputAction.CallbackContext context) => Pick(2);
        void OnAbility4(InputAction.CallbackContext context) => Pick(3);

        void Update()
        {
            RefreshCaster();
            if (IsArmed && (caster == null || !caster.IsAlive || casterAbilities == null || casterAbilities.Definition(armedSlot) == null))
                Disarm();
            preview = IsArmed ? BuildPreview() : AbilityPreview.None;
        }

        /// <summary>Arms the slot for the current caster. False for an empty slot or when nobody can cast.</summary>
        public bool Arm(int slot)
        {
            RefreshCaster();
            if (caster == null || !caster.IsAlive || casterAbilities == null)
                return false;
            var ability = casterAbilities.Definition(slot);
            if (ability == null)
                return false;
            armedSlot = slot;
            if (cursor != null)
            {
                cursor.Aiming = true;
                cursor.SnapTo = SnapFor(ability);
            }
            return true;
        }

        /// <summary>Backs out of the armed ability and gives the cursor and the stick back.</summary>
        public void Disarm()
        {
            armedSlot = -1;
            preview = AbilityPreview.None;
            areaHits.Clear();
            if (cursor != null)
            {
                cursor.Aiming = false;
                cursor.SnapTo = PointerTargetKind.None;
            }
        }

        /// <summary>
        /// Uses the armed ability on what was clicked or confirmed: builds the command and issues it to the caster (queued
        /// behind its orders when `queue`, else replacing them). Disarms and returns true when the order was accepted. On
        /// a refusal the reason is on CasterAbilities.LastFailure and the ability stays armed, so another target can be tried.
        /// </summary>
        public bool Confirm(PointerTarget pointed, bool queue)
        {
            RefreshCaster();
            var ability = ArmedAbility;
            if (ability == null || caster == null || pointed.Kind == PointerTargetKind.None)
                return false;

            AbilityCommand command;
            if (ability.TargetMode == AbilityTargetMode.Ground)
            {
                command = AbilityCommand.AtGround(ability, pointed.Point);
            }
            else
            {
                var target = TargetHealth(pointed);
                if (target == null)
                {
                    casterAbilities.ReportFailure(ability, AbilityFailure.NoTarget);
                    return false;
                }
                command = AbilityCommand.OnUnit(ability, target);
            }

            if (!caster.Issue(command, queue ? IssueMode.Append : IssueMode.Replace))
                return false;
            Disarm();
            return true;
        }

        /// <summary>What confirming `pointed` would do for `ability`: its validation, and for an area the hostiles it would hit.</summary>
        internal AbilityPreview Evaluate(AbilityDefinition ability, PointerTarget pointed, bool queued)
        {
            var scope = queued ? AbilityCheckScope.Static : AbilityCheckScope.Full;
            areaHits.Clear();
            if (ability.TargetMode == AbilityTargetMode.Ground)
            {
                if (pointed.Kind == PointerTargetKind.None)
                    return new AbilityPreview(ability, pointed.Kind, null, default, false, default, queued);
                var check = casterAbilities.Check(ability, null, pointed.Point, scope);
                casterAbilities.CollectArea(ability, pointed.Point, areaHits);
                return new AbilityPreview(ability, pointed.Kind, null, pointed.Point, true, check, queued);
            }

            var target = TargetHealth(pointed);
            if (target == null)
                return new AbilityPreview(ability, pointed.Kind, null, pointed.Point, false, default, queued);
            return new AbilityPreview(ability, pointed.Kind, target, target.transform.position, true,
                casterAbilities.Check(ability, target, null, scope), queued);
        }

        /// <summary>Arms the slot, or backs out when that slot is the armed one.</summary>
        internal void Pick(int slot)
        {
            if (IsArmed && armedSlot == slot)
                Disarm();
            else
                Arm(slot);
        }

        AbilityPreview BuildPreview()
        {
            var queued = QueueHeld && caster.CurrentCommand != null;
            return Evaluate(ArmedAbility, ResolvePointer(), queued);
        }

        // The controller's cursor when it is the pointer in use, else the mouse.
        PointerTarget ResolvePointer()
        {
            if (cursor != null && cursor.IsActive)
                return cursor.Target;
            if (viewCamera == null)
                return PointerTarget.None;
            return PointerTargetResolver.Resolve(viewCamera, InputActionUtility.Read<Vector2>(pointerPositionAction),
                maxDistance, clickableLayers, null, 0f);
        }

        static Health TargetHealth(PointerTarget pointed)
        {
            switch (pointed.Kind)
            {
                case PointerTargetKind.Hostile:
                    return pointed.Hostile;
                case PointerTargetKind.Friendly:
                    return pointed.Friendly != null ? pointed.Friendly.GetComponent<Health>() : null;
                default:
                    return null;
            }
        }

        static PointerTargetKind SnapFor(AbilityDefinition ability) =>
            ability.TargetMode == AbilityTargetMode.Ground ? PointerTargetKind.Ground
            : ability.TargetSide == AbilityTargetSide.Friendly ? PointerTargetKind.Friendly
            : PointerTargetKind.Hostile;

        void RefreshCaster()
        {
            var current = ResolveCaster();
            if (current == caster)
                return;
            caster = current;
            casterAbilities = current != null ? current.GetComponent<UnitAbilities>() : null;
            Disarm();
        }

        // The first eligible selected unit, else the active character (also while paused: the controlled character is
        // the one highlighted, so a pause with nothing selected must not leave abilities unusable).
        CommandableUnit ResolveCaster()
        {
            if (selection != null)
            {
                for (var i = 0; i < selection.Selected.Count; i++)
                {
                    var selected = selection.Selected[i];
                    if (selected != null && ActiveCharacter.IsEligible(selected))
                        return selected.Unit;
                }
            }
            return activeCharacter != null && activeCharacter.HasUnit ? activeCharacter.Unit : null;
        }
    }
}
```

- [ ] **Step 6: Divert `PlayerCommandInput`**

In `Assets/_Project/Scripts/Controls/PlayerCommandInput.cs`:

1. Add under the `cursor` field:

```csharp
        // Optional. While an ability is armed, a click or the cursor's Confirm picks its target instead of giving an order.
        [SerializeField] AbilityTargeting abilityTargeting;
```

2. Add `, AbilityTargeting targeting = null` after `InputActionReference attack = null` in `Initialize`, and `abilityTargeting = targeting;` at the end of its body.

3. Replace `OnClearSelection` with:

```csharp
        void OnClearSelection(InputAction.CallbackContext context)
        {
            // The first Cancel backs out of an armed ability; only the next one clears the selection.
            if (abilityTargeting != null && abilityTargeting.IsArmed)
            {
                abilityTargeting.Disarm();
                return;
            }
            if (selection != null)
                selection.Clear();
        }
```

4. At the top of `Act`, before the `switch`, add:

```csharp
            if (abilityTargeting != null && abilityTargeting.IsArmed)
            {
                abilityTargeting.Confirm(target, ModifierHeld);
                return;
            }
```

5. Append to the class summary: `While an ability is armed (AbilityTargeting) a click or the cursor's Confirm picks its target instead of giving an order, and Cancel disarms before it clears the selection.`

- [ ] **Step 7: Run the tests**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.AbilityTargetingTests"` (17 tests) and `Tools/run-tests.sh PlayMode "Blackglass.Tests.AbilityTargetingPadTests"` (10 tests), EXIT=0 each. Rules for failures: a screen-space probe that lands on the wrong collider (a click point under a unit's capsule, a point off-screen) is a **test** fix: move the probe and say so; a wrong arm/disarm/confirm behaviour is a code fix. If the chord does not arm in `AbilityTargetingPadTests` while it passed in `AbilityMenuGateTests`, check the `AbilityRig` wiring first (the gate's action list).
Then both full suites: EditMode 502 unchanged, PlayMode 464 + 27 = 491, EXIT=0; `PlayerCommandInputTests`, `ControllerCommandInputTests` and `PrototypeSceneTests` unchanged.

- [ ] **Step 8: Commit**

```bash
git add Assets/_Project/Scripts Assets/_Project/Tests
git commit -m "Add ability targeting: arm, preview and confirm through the existing pointer, cursor and command path

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 9: Debug previews, the ability bar and the HUD label

**Files:**
- Create: `Assets/_Project/Scripts/Abilities/AbilityDescriptions.cs`, `Assets/_Project/Scripts/DebugUI/AbilityBarView.cs`, `Assets/_Project/Scripts/DebugUI/AbilityTargetingView.cs`
- Modify: `Assets/_Project/Scripts/DebugUI/CommandQueueView.cs`, `Assets/_Project/Scripts/DebugUI/PrototypeHud.cs`
- Test: `Assets/_Project/Tests/EditMode/AbilityDescriptionsTests.cs`, `Assets/_Project/Tests/PlayMode/AbilityViewsTests.cs`

**Interfaces:**
- Consumes: `AbilityTargeting` (`Caster`, `CasterAbilities`, `ArmedSlot`, `ArmedAbility`, `IsArmed`, `Preview`, `AreaHits`), `AbilityPreview`, `AbilityCheck`, `UnitAbilities` (`Count`, `Definition`, `CooldownRemaining`, `LastFailure`, `LastFailedAbility`, `LastFailureTime`), `PromptResolver`, `ActiveInputDevice.Family`, `InputFamily.IsController()`, `CommandableUnit.CurrentCommand/PendingCommands`.
- Produces:
  - `static class AbilityDescriptions`: `string Slot(int slot, string prompt, string name, float cooldownRemaining, bool armed)`, `string Preview(AbilityPreview preview, IReadOnlyList<Health> areaHits)`, `string Failure(string abilityName, AbilityFailure failure)`, `string Order(AbilityCommand command)`.
  - `sealed class AbilityBarView : MonoBehaviour` (IMGUI, bottom-left): `internal IReadOnlyList<string> BuildLines()`, `internal void Initialize(AbilityTargeting targeting, ActiveInputDevice device, InputActionAsset controls, InputActionReference menu)`. Serialized names: `targeting`, `inputDevice`, `controls`, `menuAction`.
  - `sealed class AbilityTargetingView : MonoBehaviour`: world-space line circles (range around the caster; the blast radius or a unit ring at the aim) and a caster-to-aim line, green when the preview is valid and red when not; `internal bool IsShowingRange`, `internal bool IsShowingAim`, `internal LineRenderer RangeCircle`, `internal LineRenderer AimCircle`, `internal Color AimColor`, `internal static readonly Color ValidColor/InvalidColor`, `internal void Initialize(AbilityTargeting targeting, Material material)`. Serialized names: `targeting`, `lineMaterial`.
  - `CommandQueueView` draws an ability order's aim (a point in the line, a marker for a ground aim). `PrototypeHud.DescribeUnit(..., string archetypeName = null)` shows the archetype name instead of the role when the unit has one.

- [ ] **Step 1: Write the failing formatter tests**

`Assets/_Project/Tests/EditMode/AbilityDescriptionsTests.cs`:

```csharp
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class AbilityDescriptionsTests
    {
        AbilityDefinition aimed;
        AbilityDefinition blast;
        AbilityDefinition mend;
        GameObject bandit;
        GameObject friend;
        Health banditHealth;
        Health friendHealth;

        [SetUp]
        public void SetUp()
        {
            aimed = AbilityDefinition.Create("Aimed Shot", AbilityTargetMode.Unit, AbilityTargetSide.Hostile, 14f, true,
                AbilityCoverRule.Applies, 6f, AbilityEffect.Damage, 45);
            blast = AbilityDefinition.Create("Blast", AbilityTargetMode.Ground, AbilityTargetSide.Hostile, 12f, true,
                AbilityCoverRule.Ignored, 10f, AbilityEffect.Damage, 35, 3f);
            mend = AbilityDefinition.Create("Mend", AbilityTargetMode.Unit, AbilityTargetSide.Friendly, 8f, false,
                AbilityCoverRule.Ignored, 8f, AbilityEffect.Heal, 40);
            bandit = new GameObject("Bandit");
            banditHealth = bandit.AddComponent<Health>();
            friend = new GameObject("Ally");
            friendHealth = friend.AddComponent<Health>();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(aimed);
            Object.DestroyImmediate(blast);
            Object.DestroyImmediate(mend);
            Object.DestroyImmediate(bandit);
            Object.DestroyImmediate(friend);
        }

        static AbilityCheck Check(AbilityFailure failure, float distance, bool sight = true, bool inCover = false, float chance = 1f) =>
            new AbilityCheck(failure, distance, sight, inCover, chance);

        AbilityPreview OnUnit(AbilityDefinition ability, Health target, AbilityCheck check, bool queued = false) =>
            new AbilityPreview(ability, PointerTargetKind.Hostile, target, Vector3.zero, true, check, queued);

        [Test]
        public void Slot_ShowsTheKeyNameAndReadinessOrTheCooldown()
        {
            Assert.That(AbilityDescriptions.Slot(0, "1", "Aimed Shot", 0f, true), Is.EqualTo("> [1] Aimed Shot  1  ready"));
            Assert.That(AbilityDescriptions.Slot(2, "RT + D-pad Down", "Mend", 3.24f, false), Is.EqualTo("  [3] Mend  RT + D-pad Down  CD 3.2"));
        }

        [Test]
        public void Preview_AValidUnitShot_ShowsDistanceSightCoverAndOk()
        {
            var text = AbilityDescriptions.Preview(OnUnit(aimed, banditHealth, Check(AbilityFailure.None, 8.544f)), new Health[0]);
            Assert.That(text, Is.EqualTo("Aimed Shot -> Bandit | 8.5/14.0 m | LOS clear | exposed | OK"));
        }

        [Test]
        public void Preview_ACoveredTarget_ShowsItsHitChance()
        {
            var text = AbilityDescriptions.Preview(OnUnit(aimed, banditHealth, Check(AbilityFailure.None, 6f, true, true, 0.5f)), new Health[0]);
            Assert.That(text, Is.EqualTo("Aimed Shot -> Bandit | 6.0/14.0 m | LOS clear | cover 50% | OK"));
        }

        [Test]
        public void Preview_AFailure_NamesTheReasonInsteadOfOk()
        {
            Assert.That(AbilityDescriptions.Preview(OnUnit(aimed, banditHealth, Check(AbilityFailure.OutOfRange, 17f)), new Health[0]),
                Is.EqualTo("Aimed Shot -> Bandit | 17.0/14.0 m | LOS clear | exposed | out of range"));
            Assert.That(AbilityDescriptions.Preview(OnUnit(aimed, banditHealth, Check(AbilityFailure.NoLineOfSight, 9f, false)), new Health[0]),
                Is.EqualTo("Aimed Shot -> Bandit | 9.0/14.0 m | LOS blocked | exposed | no line of sight"));
            Assert.That(AbilityDescriptions.Preview(OnUnit(aimed, friendHealth, Check(AbilityFailure.WrongSide, 4f)), new Health[0]),
                Does.EndWith("| wrong side for this ability"));
        }

        [Test]
        public void Preview_AGroundAbility_ShowsThePointTheRuleAboutCoverAndWhoItWouldHit()
        {
            var ground = new AbilityPreview(blast, PointerTargetKind.Ground, null, new Vector3(5f, 0f, 2f), true,
                Check(AbilityFailure.None, 9.43f), false);

            Assert.That(AbilityDescriptions.Preview(ground, new[] { banditHealth }),
                Is.EqualTo("Blast @ (5.0, 2.0) | 9.4/12.0 m | LOS clear | ignores cover | hits 1 (Bandit) | OK"));
            Assert.That(AbilityDescriptions.Preview(ground, new Health[0]),
                Is.EqualTo("Blast @ (5.0, 2.0) | 9.4/12.0 m | LOS clear | ignores cover | hits 0 | OK"));
        }

        [Test]
        public void Preview_AHeal_NeedsNoSightAndMentionsNoCover()
        {
            var text = AbilityDescriptions.Preview(
                new AbilityPreview(mend, PointerTargetKind.Friendly, friendHealth, Vector3.zero, true, Check(AbilityFailure.None, 4f), false),
                new Health[0]);
            Assert.That(text, Is.EqualTo("Mend -> Ally | 4.0/8.0 m | no LOS needed | OK"));
        }

        [Test]
        public void Preview_NoAimYet_AsksForOne_AndAQueuedPreviewSaysItIsCheckedLater()
        {
            Assert.That(AbilityDescriptions.Preview(new AbilityPreview(aimed, PointerTargetKind.Ground, null, Vector3.zero, false, default, false), new Health[0]),
                Is.EqualTo("Aimed Shot: choose a target"));
            Assert.That(AbilityDescriptions.Preview(new AbilityPreview(blast, PointerTargetKind.None, null, Vector3.zero, false, default, false), new Health[0]),
                Is.EqualTo("Blast: choose a position"));
            Assert.That(AbilityDescriptions.Preview(AbilityPreview.None, new Health[0]), Is.Empty);

            var queued = AbilityDescriptions.Preview(OnUnit(aimed, banditHealth, Check(AbilityFailure.None, 17f), true), new Health[0]);
            Assert.That(queued, Does.EndWith("| OK (range, sight and cooldown are checked when it runs)"));
        }

        [Test]
        public void Failure_AndOrder_ReadAsShortSentences()
        {
            Assert.That(AbilityDescriptions.Failure("Aimed Shot", AbilityFailure.OnCooldown), Is.EqualTo("Aimed Shot: on cooldown"));
            Assert.That(AbilityDescriptions.Order(AbilityCommand.OnUnit(aimed, banditHealth)), Is.EqualTo("Aimed Shot -> Bandit"));
            Assert.That(AbilityDescriptions.Order(AbilityCommand.AtGround(blast, new Vector3(5f, 0f, 2f))), Is.EqualTo("Blast @ (5.0, 2.0)"));
        }

        [Test]
        public void TheHudUnitLabel_ShowsTheArchetypeNameWhenThereIsOne()
        {
            Assert.That(PrototypeHud.DescribeUnit("FriendlyUnit_2", 100, 100, CombatRole.Ranged, "Marksman"), Is.EqualTo("FriendlyUnit_2 100/100 [Marksman]"));
            Assert.That(PrototypeHud.DescribeUnit("FriendlyUnit_2", 100, 100, CombatRole.Ranged, null), Is.EqualTo("FriendlyUnit_2 100/100 [Ranged]"));
            Assert.That(PrototypeHud.DescribeUnit("FriendlyUnit_2", 100, 100, CombatRole.Ranged, ""), Is.EqualTo("FriendlyUnit_2 100/100 [Ranged]"));
        }
    }
}
```

- [ ] **Step 2: Run to verify the compile failure**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.AbilityDescriptionsTests"`
Expected: EXIT=1 with `error CS0103`/`CS0117` for `AbilityDescriptions`.

- [ ] **Step 3: Implement `AbilityDescriptions`**

`Assets/_Project/Scripts/Abilities/AbilityDescriptions.cs`:

```csharp
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace Blackglass
{
    /// <summary>Pure text for the ability debug views, so the exact wording is pinned by tests. Invariant culture, placeholder style.</summary>
    public static class AbilityDescriptions
    {
        const int MaxNamedVictims = 3;

        /// <summary>One bar line: the armed marker, the slot number, the name, the prompt and "ready" or the cooldown.</summary>
        public static string Slot(int slot, string prompt, string name, float cooldownRemaining, bool armed)
        {
            var state = cooldownRemaining > 0f ? "CD " + Number(cooldownRemaining) : "ready";
            return $"{(armed ? ">" : " ")} [{slot + 1}] {name}  {prompt}  {state}";
        }

        /// <summary>The preview line: what is aimed at, distance/range, sight, the cover rule, who a blast hits, and OK or why not.</summary>
        public static string Preview(AbilityPreview preview, IReadOnlyList<Health> areaHits)
        {
            if (!preview.IsArmed)
                return string.Empty;
            var ability = preview.Ability;
            var name = ability.DisplayName;
            if (!preview.HasAim)
                return ability.TargetMode == AbilityTargetMode.Ground ? $"{name}: choose a position" : $"{name}: choose a target";

            var check = preview.Check;
            var text = new StringBuilder();
            text.Append(preview.Target != null
                ? $"{name} -> {preview.Target.name}"
                : $"{name} @ ({Number(preview.Point.x)}, {Number(preview.Point.z)})");
            text.Append($" | {Number(check.Distance)}/{Number(ability.Range)} m");
            text.Append(ability.RequiresLineOfSight ? (check.SightClear ? " | LOS clear" : " | LOS blocked") : " | no LOS needed");
            if (ability.Effect == AbilityEffect.Damage)
            {
                if (ability.CoverRule == AbilityCoverRule.Ignored)
                    text.Append(" | ignores cover");
                else if (preview.Target != null)
                    text.Append(check.TargetInCover ? $" | cover {Mathf.RoundToInt(check.HitChance * 100f)}%" : " | exposed");
            }
            if (ability.TargetMode == AbilityTargetMode.Ground)
                text.Append($" | hits {areaHits.Count}{VictimNames(areaHits)}");
            text.Append(" | ");
            text.Append(preview.IsValid ? "OK" : preview.Failure.Describe());
            if (preview.Queued)
                text.Append(" (range, sight and cooldown are checked when it runs)");
            return text.ToString();
        }

        public static string Failure(string abilityName, AbilityFailure failure) => $"{abilityName}: {failure.Describe()}";

        /// <summary>An ability order as one phrase: "Aimed Shot -> Bandit" or "Blast @ (5.0, 2.0)".</summary>
        public static string Order(AbilityCommand command) =>
            command.Target != null
                ? $"{command.Definition.DisplayName} -> {command.Target.name}"
                : $"{command.Definition.DisplayName} @ ({Number(command.Point.x)}, {Number(command.Point.z)})";

        static string VictimNames(IReadOnlyList<Health> victims)
        {
            if (victims.Count == 0)
                return string.Empty;
            var names = new List<string>();
            for (var i = 0; i < victims.Count && i < MaxNamedVictims; i++)
                names.Add(victims[i].name);
            var more = victims.Count > MaxNamedVictims ? ", ..." : string.Empty;
            return $" ({string.Join(", ", names)}{more})";
        }

        static string Number(float value) => value.ToString("0.0", CultureInfo.InvariantCulture);
    }
}
```

- [ ] **Step 4: Extend `PrototypeHud` and `CommandQueueView`**

In `Assets/_Project/Scripts/DebugUI/PrototypeHud.cs`: replace `DescribeUnit` with

```csharp
        internal static string DescribeUnit(string unitName, int current, int max, CombatRole role, string archetypeName = null) =>
            $"{unitName} {current}/{max} [{(string.IsNullOrEmpty(archetypeName) ? role.ToString() : archetypeName)}]";
```

and in `DrawUnitLabel` replace the `var text = DescribeUnit(...)` line with:

```csharp
            var archetypeName = hasAttacker && attacker.Archetype != null ? attacker.Archetype.DisplayName : null;
            var text = DescribeUnit(health.name, health.Current, health.Max, hasAttacker ? attacker.Role : CombatRole.Melee, archetypeName);
```

In `Assets/_Project/Scripts/DebugUI/CommandQueueView.cs`, in `AddOrder` add before the closing brace of the `switch`:

```csharp
                case AbilityCommand ability when ability.Definition != null
                    && (ability.Definition.TargetMode == AbilityTargetMode.Ground
                        || (ability.Target != null && ability.Target.gameObject.activeInHierarchy)):
                    var aim = OnGround(ability.AimPoint);
                    points.Add(aim);
                    if (ability.Definition.TargetMode == AbilityTargetMode.Ground)
                        ShowMarker(aim);
                    break;
```

and extend its summary: "...and a disc at every move, cover or ground-ability destination."

- [ ] **Step 5: Implement `AbilityBarView`**

`Assets/_Project/Scripts/DebugUI/AbilityBarView.cs`:

```csharp
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Blackglass
{
    /// <summary>
    /// Debug-only on-screen text (IMGUI) for abilities, bottom-left, working while paused: who casts, each slot with its
    /// prompt, name and readiness or cooldown (the armed one marked), the preview of what the pointer is on (target,
    /// range, sight, cover, hits, OK or why not), the ability orders running or queued, and the last failure for a few
    /// seconds. With a controller, holding the menu trigger says so. Not production UI. The text itself is built by
    /// AbilityDescriptions and BuildLines so tests can read it.
    /// </summary>
    public sealed class AbilityBarView : MonoBehaviour
    {
        const float FailureSeconds = 3f;
        const float LineHeight = 20f;

        [SerializeField] AbilityTargeting targeting;
        // Optional: without it the bar shows keyboard prompts.
        [SerializeField] ActiveInputDevice inputDevice;
        [SerializeField] InputActionAsset controls;
        // Read only (the AbilityMenuGate owns it): true while a controller's menu trigger is held.
        [SerializeField] InputActionReference menuAction;

        readonly List<string> lines = new List<string>();

        internal void Initialize(AbilityTargeting abilityTargeting, ActiveInputDevice device, InputActionAsset actions, InputActionReference menu)
        {
            targeting = abilityTargeting;
            inputDevice = device;
            controls = actions;
            menuAction = menu;
        }

        InputFamily Family => inputDevice != null ? inputDevice.Family : InputFamily.KeyboardMouse;

        /// <summary>The lines for the current state. Rebuilt on every call; the list is reused.</summary>
        internal IReadOnlyList<string> BuildLines()
        {
            lines.Clear();
            if (targeting == null)
                return lines;
            var caster = targeting.Caster;
            var abilities = targeting.CasterAbilities;
            if (caster == null || abilities == null)
            {
                lines.Add("Abilities: the unit in control has none");
                return lines;
            }

            var family = Family;
            var header = $"Abilities: {caster.name}";
            if (family.IsController())
            {
                header += InputActionUtility.IsPressed(menuAction)
                    ? "   [ABILITY MENU: press a D-pad direction]"
                    : $"   (hold {Prompt("Commands/AbilityMenu", "-", family)} to pick)";
            }
            lines.Add(header);

            for (var i = 0; i < abilities.Count; i++)
            {
                var ability = abilities.Definition(i);
                if (ability == null)
                    continue;
                lines.Add(AbilityDescriptions.Slot(i, Prompt($"Commands/Ability{i + 1}", (i + 1).ToString(), family),
                    ability.DisplayName, abilities.CooldownRemaining(i), targeting.ArmedSlot == i));
            }

            if (targeting.IsArmed)
                lines.Add(AbilityDescriptions.Preview(targeting.Preview, targeting.AreaHits));

            if (caster.CurrentCommand is AbilityCommand casting)
                lines.Add("Casting: " + AbilityDescriptions.Order(casting));
            for (var i = 0; i < caster.PendingCommands.Count; i++)
            {
                if (caster.PendingCommands[i] is AbilityCommand queued)
                    lines.Add("Queued: " + AbilityDescriptions.Order(queued));
            }

            if (abilities.LastFailedAbility != null && Time.unscaledTime - abilities.LastFailureTime < FailureSeconds)
                lines.Add(AbilityDescriptions.Failure(abilities.LastFailedAbility.DisplayName, abilities.LastFailure));
            return lines;
        }

        string Prompt(string actionPath, string fallback, InputFamily family) =>
            controls != null ? PromptResolver.GetPrompt(controls.FindAction(actionPath), family) : fallback;

        void OnGUI()
        {
            var shown = BuildLines();
            for (var i = 0; i < shown.Count; i++)
                GUI.Label(new Rect(10f, Screen.height - 10f - LineHeight * (shown.Count - i), 900f, LineHeight + 2f), shown[i]);
        }
    }
}
```

- [ ] **Step 6: Implement `AbilityTargetingView`**

`Assets/_Project/Scripts/DebugUI/AbilityTargetingView.cs`:

```csharp
using UnityEngine;
using UnityEngine.Rendering;

namespace Blackglass
{
    /// <summary>
    /// Debug view of an armed ability, in the world (placeholder look): a white circle of the ability's range around the
    /// caster, a line from the caster to the aim, and at the aim a ring (the blast radius for a ground ability, a
    /// target ring for a unit), green while the preview is valid and red while it is not. Reads AbilityTargeting every
    /// frame, never changes it, has no colliders (so it never blocks a click ray), and works while paused.
    /// </summary>
    public sealed class AbilityTargetingView : MonoBehaviour
    {
        const int CircleSegments = 48;
        const float TargetRingRadius = 0.9f;
        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        static readonly Color RangeColor = new Color(0.9f, 0.9f, 0.9f, 1f);
        internal static readonly Color ValidColor = new Color(0.2f, 1f, 0.3f, 1f);
        internal static readonly Color InvalidColor = new Color(1f, 0.25f, 0.2f, 1f);

        [SerializeField] AbilityTargeting targeting;
        [SerializeField] Material lineMaterial;
        // The prototype ground is flat at y = 0; the lines float just above it.
        [SerializeField] float groundHeight = 0.08f;
        [SerializeField, Min(0.01f)] float lineWidth = 0.08f;

        LineRenderer rangeCircle;
        LineRenderer aimCircle;
        LineRenderer aimLine;
        MaterialPropertyBlock block;

        internal bool IsShowingRange => rangeCircle != null && rangeCircle.enabled;
        internal bool IsShowingAim => aimCircle != null && aimCircle.enabled;
        internal LineRenderer RangeCircle => rangeCircle;
        internal LineRenderer AimCircle => aimCircle;
        internal Color AimColor { get; private set; }

        internal void Initialize(AbilityTargeting abilityTargeting, Material material)
        {
            targeting = abilityTargeting;
            lineMaterial = material;
        }

        void Awake()
        {
            block = new MaterialPropertyBlock();
            rangeCircle = CreateLine("AbilityRange", true);
            aimCircle = CreateLine("AbilityAimRing", true);
            aimLine = CreateLine("AbilityAimLine", false);
            HideAll();
        }

        void LateUpdate()
        {
            if (targeting == null || !targeting.IsArmed || targeting.Caster == null || targeting.ArmedAbility == null)
            {
                HideAll();
                return;
            }

            var ability = targeting.ArmedAbility;
            var origin = targeting.Caster.transform.position;
            DrawCircle(rangeCircle, origin, ability.Range, RangeColor);

            var preview = targeting.Preview;
            if (!preview.HasAim)
            {
                aimCircle.enabled = false;
                aimLine.enabled = false;
                return;
            }

            var color = preview.IsValid ? ValidColor : InvalidColor;
            AimColor = color;
            var radius = ability.TargetMode == AbilityTargetMode.Ground ? ability.Radius : TargetRingRadius;
            DrawCircle(aimCircle, preview.Point, radius, color);

            aimLine.positionCount = 2;
            aimLine.SetPosition(0, Flat(origin));
            aimLine.SetPosition(1, Flat(preview.Point));
            Paint(aimLine, color);
            aimLine.enabled = true;
        }

        void OnDisable() => HideAll();

        void HideAll()
        {
            if (rangeCircle != null)
                rangeCircle.enabled = false;
            if (aimCircle != null)
                aimCircle.enabled = false;
            if (aimLine != null)
                aimLine.enabled = false;
        }

        void DrawCircle(LineRenderer line, Vector3 center, float radius, Color color)
        {
            line.positionCount = CircleSegments;
            for (var i = 0; i < CircleSegments; i++)
            {
                var angle = i * Mathf.PI * 2f / CircleSegments;
                line.SetPosition(i, Flat(center) + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius);
            }
            Paint(line, color);
            line.enabled = true;
        }

        Vector3 Flat(Vector3 point) => new Vector3(point.x, groundHeight, point.z);

        void Paint(LineRenderer line, Color color)
        {
            line.GetPropertyBlock(block);
            block.SetColor(BaseColor, color);
            line.SetPropertyBlock(block);
        }

        // Each line lives on a collider-free child so no component of the host is touched.
        LineRenderer CreateLine(string objectName, bool loop)
        {
            var child = new GameObject(objectName);
            child.transform.SetParent(transform, false);
            var line = child.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.loop = loop;
            line.widthMultiplier = lineWidth;
            line.shadowCastingMode = ShadowCastingMode.Off;
            line.receiveShadows = false;
            if (lineMaterial != null)
                line.sharedMaterial = lineMaterial;
            return line;
        }
    }
}
```

- [ ] **Step 7: Write the view tests**

`Assets/_Project/Tests/PlayMode/AbilityViewsTests.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class AbilityViewsTests : InputTestFixture
    {
        Keyboard keyboard;
        Mouse mouse;
        InputActionAsset actions;
        AbilityRig rig;
        AbilityBarView bar;
        AbilityTargetingView view;

        public override void Setup()
        {
            base.Setup();
            keyboard = InputSystem.AddDevice<Keyboard>();
            mouse = InputSystem.AddDevice<Mouse>();
            actions = TestControls.Load();
            rig = AbilityRig.Build(actions, false);
            var views = rig.World.Track(new GameObject("Views"));
            bar = views.AddComponent<AbilityBarView>();
            bar.Initialize(rig.Targeting, null, actions, null);
            view = views.AddComponent<AbilityTargetingView>();
            view.Initialize(rig.Targeting, null);
        }

        public override void TearDown()
        {
            rig.World.Dispose();
            Time.timeScale = 1f;
            TestControls.Reset(actions);
            base.TearDown();
        }

        IEnumerator Tap(ButtonControl key)
        {
            Press(key);
            yield return null;
            Release(key);
            yield return null;
        }

        [UnityTest]
        public IEnumerator TheBar_ListsEverySlot_AndMarksTheArmedOne()
        {
            yield return null;
            yield return null;

            var idle = bar.BuildLines().ToArray();
            Assert.That(idle[0], Is.EqualTo($"Abilities: {rig.Caster.name}"));
            Assert.That(idle[1], Is.EqualTo("  [1] Aimed Shot  1  ready"));
            Assert.That(idle[2], Is.EqualTo("  [2] Blast  2  ready"));
            Assert.That(idle[3], Is.EqualTo("  [3] Mend  3  ready"));
            Assert.That(idle, Has.Length.EqualTo(4), "Nothing armed: no preview line");

            yield return Tap(keyboard.digit2Key);
            var armed = bar.BuildLines().ToArray();
            Assert.That(armed[2], Does.StartWith("> [2] Blast"));
            Assert.That(armed[armed.Length - 1], Is.EqualTo("Blast: choose a position"));
        }

        [UnityTest]
        public IEnumerator TheBar_ShowsTheCooldown_AndTheQueuedAndRunningOrders()
        {
            yield return null;
            rig.Pause.Pause();
            Assert.That(rig.Caster.Unit.Issue(AbilityCommand.OnUnit(rig.Aimed, rig.Hostile)), Is.True);
            Assert.That(rig.Caster.Unit.Issue(AbilityCommand.AtGround(rig.Blast, AbilityRig.BlastGround), IssueMode.Append), Is.True);
            yield return null;

            var lines = bar.BuildLines().ToArray();
            Assert.That(lines, Has.Member($"Casting: Aimed Shot -> {rig.Hostile.name}"));
            Assert.That(lines, Has.Member("Queued: Blast @ (5.0, 2.0)"));

            rig.Pause.Resume();
            yield return TestWorld.WaitUntil(() => rig.Caster.Unit.CurrentCommand == null, 3f);
            Assert.That(bar.BuildLines().ToArray()[1], Does.Contain("CD "));
        }

        [UnityTest]
        public IEnumerator ARefusedClick_ShowsItsReasonOnTheBar()
        {
            yield return null;
            yield return Tap(keyboard.digit1Key);
            Set(mouse.position, rig.ScreenPointOf(rig.FarHostile.transform.position));
            yield return null;
            yield return null;
            Assert.That(bar.BuildLines().Last(), Does.Contain("out of range"), "The preview already says why");

            Press(mouse.leftButton);
            yield return null;
            Release(mouse.leftButton);
            yield return null;

            Assert.That(bar.BuildLines().ToArray(), Has.Member("Aimed Shot: out of range"));
        }

        [UnityTest]
        public IEnumerator TheRangeCircle_IsDrawnAtTheAbilitysRangeAroundTheCaster_OnlyWhileArmed()
        {
            yield return null;
            yield return null;
            Assert.That(view.IsShowingRange, Is.False);

            yield return Tap(keyboard.digit1Key);
            yield return null;

            Assert.That(view.IsShowingRange, Is.True);
            var casterAt = rig.Caster.transform.position;
            for (var i = 0; i < view.RangeCircle.positionCount; i += 8)
                Assert.That(CoverRules.FlatDistance(view.RangeCircle.GetPosition(i), casterAt), Is.EqualTo(14f).Within(0.05f));

            yield return Tap(keyboard.escapeKey);
            yield return null;
            Assert.That(view.IsShowingRange, Is.False);
            Assert.That(view.IsShowingAim, Is.False);
        }

        [UnityTest]
        public IEnumerator TheAimRing_IsGreenWhenValid_AndRedWhenNot()
        {
            yield return null;
            yield return Tap(keyboard.digit1Key);

            Set(mouse.position, rig.ScreenPointOf(rig.Hostile.transform.position));
            yield return null;
            yield return null;
            Assert.That(view.IsShowingAim, Is.True);
            Assert.That(view.AimColor, Is.EqualTo(AbilityTargetingView.ValidColor));

            Set(mouse.position, rig.ScreenPointOf(rig.FarHostile.transform.position));
            yield return null;
            yield return null;
            Assert.That(view.AimColor, Is.EqualTo(AbilityTargetingView.InvalidColor));
        }

        [UnityTest]
        public IEnumerator ABlastRing_HasTheBlastRadius_AroundTheAimPoint()
        {
            yield return null;
            yield return Tap(keyboard.digit2Key);

            Set(mouse.position, rig.ScreenPointOf(AbilityRig.BlastGround));
            yield return null;
            yield return null;

            Assert.That(view.IsShowingAim, Is.True);
            var aim = rig.Targeting.Preview.Point;
            Assert.That(CoverRules.FlatDistance(view.AimCircle.GetPosition(0), aim), Is.EqualTo(3f).Within(0.05f));
            Assert.That(CoverRules.FlatDistance(view.AimCircle.GetPosition(12), aim), Is.EqualTo(3f).Within(0.05f));
        }

        [UnityTest]
        public IEnumerator TheViews_HaveNoColliders_SoTheyNeverBlockAClick()
        {
            yield return null;
            yield return Tap(keyboard.digit1Key);
            yield return null;

            Assert.That(view.GetComponentsInChildren<Collider>(true), Is.Empty);
        }

        [UnityTest]
        public IEnumerator TheQueueView_DrawsAnAbilityOrder_AndAMarkerForAGroundAim()
        {
            yield return null;
            var queueView = rig.Caster.Unit.gameObject.AddComponent<CommandQueueView>();
            rig.Pause.Pause();
            yield return null;
            Assert.That(queueView.LinePointCount, Is.EqualTo(0));

            Assert.That(rig.Caster.Unit.Issue(AbilityCommand.OnUnit(rig.Aimed, rig.Hostile)), Is.True);
            yield return null;
            Assert.That(queueView.LinePointCount, Is.EqualTo(2), "The unit and the target");
            Assert.That(queueView.ActiveMarkerCount, Is.EqualTo(0));

            Assert.That(rig.Caster.Unit.Issue(AbilityCommand.AtGround(rig.Blast, AbilityRig.BlastGround), IssueMode.Append), Is.True);
            yield return null;
            Assert.That(queueView.LinePointCount, Is.EqualTo(3));
            Assert.That(queueView.ActiveMarkerCount, Is.EqualTo(1), "A ground aim gets a disc");
        }
    }
}
#endif
```

- [ ] **Step 8: Run the tests**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.AbilityDescriptionsTests"` → EXIT=0, 9 tests. `Tools/run-tests.sh PlayMode "Blackglass.Tests.AbilityViewsTests"` → EXIT=0, 8 tests. If a bar-line or circle-index probe is off by one (a different segment index, an extra line), fix the test. Then both full suites: EditMode 502 + 9 = 511, PlayMode 491 + 8 = 499, EXIT=0; `PrototypeHudTests` and `CommandQueueViewTests` unchanged.

- [ ] **Step 9: Commit**

```bash
git add Assets/_Project/Scripts Assets/_Project/Tests
git commit -m "Add the ability debug views: preview circles, the ability bar, order lines and the archetype label

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 10: Data assets, scene wiring and the real-arena tests

**Files:**
- Create (temporary, never committed): `Assets/_Project/Editor/AbilitySceneBuilder.cs` (+ its `.meta`, deleted afterwards)
- Create (committed): `Assets/_Project/Data/Abilities/{AimedShot,Blast,Mend}.asset`, `Assets/_Project/Data/Archetypes/{Melee,Ranged,Marksman}.asset` (+ `.meta` files and the folder `.meta` files)
- Modify: `Assets/_Project/Scenes/Prototype.unity` (through the builder), `Assets/_Project/Tests/PlayMode/PrototypeSceneTests.cs` (one expectation)
- Test: `Assets/_Project/Tests/PlayMode/PrototypeSceneAbilityTests.cs`

**Interfaces:**
- Consumes: every earlier task. Serialized field names the builder sets: `UnitAttacker.archetype`; `UnitAbilities.{abilities, encounter}`; `AbilityTargeting.{viewCamera, selection, activeCharacter, cursor, pointerPositionAction, queueModifierAction, ability1Action..ability4Action}`; `AbilityMenuGate.{menuAction, suppressedWhileHeld}`; `AbilityTargetingView.{targeting, lineMaterial}`; `AbilityBarView.{targeting, inputDevice, controls, menuAction}`; `PlayerCommandInput.abilityTargeting`; `TacticalCameraController.cursor`.
- Produces: the prototype scene with the three friendlies carrying `UnitAbilities` (Aimed Shot, Blast, Mend) and archetypes (`FriendlyUnit_1` Melee, `FriendlyUnit_2` Marksman, `FriendlyUnit_3` Ranged), and `Systems` carrying `AbilityTargeting`, `AbilityMenuGate`, `AbilityTargetingView` and `AbilityBarView`, all wired. Hostiles are unchanged (inline stats, no abilities: enemy AI keeps using basic attacks).

Asset values (the shipped numbers; the tests in Tasks 4 and 10 pin them):

| Asset | Values |
|---|---|
| `AimedShot` | "Aimed Shot", Unit, Hostile, range 14, LOS yes, cover Applies, cooldown 6, Damage 45 |
| `Blast` | "Blast", Ground, Hostile, range 12, LOS yes, cover Ignored, cooldown 10, Damage 35, radius 3 |
| `Mend` | "Mend", Unit, Friendly, range 8, LOS no, cover Ignored, cooldown 8, Heal 40 |
| `Melee` | "Melee", Melee, range 2, damage 25, interval 1 |
| `Ranged` | "Ranged", Ranged, range 8, damage 15, interval 1 |
| `Marksman` | "Marksman", Ranged, range 16, damage 40, interval 2.5 |

- [ ] **Step 1: Write the scene tests first (they fail until the scene is wired)**

`Assets/_Project/Tests/PlayMode/PrototypeSceneAbilityTests.cs`:

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
    /// The abilities in the real arena. The squad starts about 28 m from the hostiles, so a test that needs a hostile in
    /// range stands one next to the caster (agent.Warp, its AI off so it neither moves nor fights while the test aims).
    /// Only an Xbox-style simulated pad is used: a simulated DualSense discards delta events (see PrototypeSceneControllerTests).
    /// </summary>
    public class PrototypeSceneAbilityTests : InputTestFixture
    {
        Keyboard keyboard;
        Mouse mouse;
        InputActionAsset actions;
        CommandableUnit[] squad;
        EnemyAI[] hostiles;
        TacticalPause pause;
        UnitSelection selection;
        Encounter encounter;
        AbilityTargeting targeting;
        TacticalCursor cursor;
        ActiveInputDevice family;
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
            PrototypeSceneTests.DestroySceneObjects();
            TestControls.Reset(actions);
            base.TearDown();
        }

        IEnumerator LoadScene()
        {
            yield return SceneManager.LoadSceneAsync("Prototype", LoadSceneMode.Single);
            yield return null;
            squad = PrototypeSceneTests.FindSquad();
            hostiles = PrototypeSceneTests.FindHostiles();
            pause = Object.FindFirstObjectByType<TacticalPause>();
            selection = Object.FindFirstObjectByType<UnitSelection>();
            encounter = Object.FindFirstObjectByType<Encounter>();
            targeting = Object.FindFirstObjectByType<AbilityTargeting>();
            cursor = Object.FindFirstObjectByType<TacticalCursor>();
            family = Object.FindFirstObjectByType<ActiveInputDevice>();
            viewCamera = Camera.main;
            Assert.That(targeting, Is.Not.Null, "The scene has no AbilityTargeting: run the scene builder");
        }

        static UnitAbilities AbilitiesOf(CommandableUnit unit) => unit.GetComponent<UnitAbilities>();
        static Health HealthOf(Component unit) => unit.GetComponent<Health>();

        Vector2 ScreenPointOf(Vector3 world) => viewCamera.WorldToScreenPoint(world);

        bool OnScreen(Vector3 world)
        {
            var p = viewCamera.WorldToScreenPoint(world);
            return p.z > 0f && p.x > 20f && p.x < viewCamera.pixelWidth - 20f && p.y > 20f && p.y < viewCamera.pixelHeight - 20f;
        }

        void Select(int index) => selection.Select(squad[index].GetComponent<SelectableUnit>());

        // Stands a hostile `distance` metres from the caster on open, visible, on-screen ground, with its AI off.
        Health BringHostileNear(EnemyAI hostile, CommandableUnit caster, float distance)
        {
            hostile.enabled = false;
            hostile.GetComponent<CommandableUnit>().Issue(new StopCommand());
            var attacker = caster.GetComponent<UnitAttacker>();
            var ground = caster.transform.position - Vector3.up;
            for (var step = 0; step < 16; step++)
            {
                var angle = step * Mathf.PI * 2f / 16f;
                var candidate = ground + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * distance;
                if (!NavMesh.SamplePosition(candidate, out var hit, 0.5f, NavMesh.AllAreas))
                    continue;
                if (!attacker.HasLineOfSightToPoint(hit.position + Vector3.up) || !OnScreen(hit.position + Vector3.up))
                    continue;
                hostile.GetComponent<NavMeshAgent>().Warp(hit.position);
                return HealthOf(hostile);
            }
            Assert.Fail($"No open, visible spot {distance} m from {caster.name}: change the probe distance");
            return null;
        }

        IEnumerator Tap(ButtonControl button)
        {
            Press(button);
            yield return null;
            Release(button);
            yield return null;
        }

        IEnumerator LeftClickAt(Vector2 screenPoint)
        {
            Set(mouse.position, screenPoint);
            yield return null;
            Press(mouse.leftButton);
            yield return null;
            Release(mouse.leftButton);
            yield return null;
        }

        [UnityTest]
        public IEnumerator Scene_EveryFriendly_HasTheThreeAbilities_AndACooldownOfItsOwn_AndHostilesHaveNone()
        {
            yield return LoadScene();

            foreach (var unit in squad)
            {
                var abilities = AbilitiesOf(unit);
                Assert.That(abilities, Is.Not.Null, $"{unit.name} has no UnitAbilities");
                Assert.That(Enumerable.Range(0, abilities.Count).Select(i => abilities.Definition(i).DisplayName),
                    Is.EqualTo(new[] { "Aimed Shot", "Blast", "Mend" }), unit.name);
            }
            foreach (var hostile in hostiles)
                Assert.That(hostile.GetComponent<UnitAbilities>(), Is.Null, $"{hostile.name} keeps using basic attacks only");

            var first = AbilitiesOf(squad[0]);
            var second = AbilitiesOf(squad[1]);
            Assert.That(first.Definition(2), Is.SameAs(second.Definition(2)), "One shared Mend definition");
            Assert.That(first.TryUse(AbilityCommand.OnUnit(first.Definition(2), HealthOf(squad[0]))), Is.True);
            Assert.That(first.IsReady(2), Is.False);
            Assert.That(second.IsReady(2), Is.True, "The second unit's Mend is not on cooldown");
        }

        [UnityTest]
        public IEnumerator Scene_TheArchetypes_GiveTheThreeFriendliesDifferentRoles()
        {
            yield return LoadScene();
            var melee = squad[0].GetComponent<UnitAttacker>();
            var marksman = squad[1].GetComponent<UnitAttacker>();
            var ranged = squad[2].GetComponent<UnitAttacker>();

            Assert.That(melee.Archetype.DisplayName, Is.EqualTo("Melee"));
            Assert.That(melee.Role, Is.EqualTo(CombatRole.Melee));
            Assert.That((melee.Range, melee.Damage, melee.Cooldown), Is.EqualTo((2f, 25, 1f)));
            Assert.That(ranged.Archetype.DisplayName, Is.EqualTo("Ranged"));
            Assert.That((ranged.Range, ranged.Damage, ranged.Cooldown), Is.EqualTo((8f, 15, 1f)));
            Assert.That(marksman.Archetype.DisplayName, Is.EqualTo("Marksman"));
            Assert.That(marksman.Role, Is.EqualTo(CombatRole.Ranged));
            Assert.That((marksman.Range, marksman.Damage, marksman.Cooldown), Is.EqualTo((16f, 40, 2.5f)));
            Assert.That(marksman.NeedsLineOfSight, Is.True);
        }

        [UnityTest]
        public IEnumerator Scene_AimedShot_ByKeyboardAndMouse_HitsAHostileInRange()
        {
            yield return LoadScene();
            Select(1);
            var hostile = BringHostileNear(hostiles[0], squad[1], 9f);
            var before = hostile.Current;
            yield return null;

            yield return Tap(keyboard.digit1Key);
            Assert.That(targeting.ArmedAbility.DisplayName, Is.EqualTo("Aimed Shot"));
            yield return LeftClickAt(ScreenPointOf(hostile.transform.position));
            yield return TestWorld.WaitUntil(() => hostile.Current < before, 2f);

            Assert.That(hostile.Current, Is.LessThanOrEqualTo(before - 45));
            Assert.That(AbilitiesOf(squad[1]).IsReady(0), Is.False);
            Assert.That(targeting.IsArmed, Is.False);
        }

        [UnityTest]
        public IEnumerator Scene_Blast_OnTheGround_HitsTheHostileNearThePoint_AndNoFriendly()
        {
            yield return LoadScene();
            Select(0);
            var hostile = BringHostileNear(hostiles[1], squad[0], 8f);
            var before = hostile.Current;
            var attacker = squad[0].GetComponent<UnitAttacker>();
            var spot = hostile.transform.position - Vector3.up;
            Vector3? aim = null;
            foreach (var offset in new[] { new Vector3(1.5f, 0f, 0f), new Vector3(-1.5f, 0f, 0f), new Vector3(0f, 0f, 1.5f), new Vector3(0f, 0f, -1.5f) })
            {
                var candidate = spot + offset;
                if (OnScreen(candidate) && attacker.HasLineOfSightToPoint(candidate + Vector3.up))
                {
                    aim = candidate;
                    break;
                }
            }
            Assert.That(aim.HasValue, Is.True, "No open ground point beside the hostile: change the probe offsets");
            yield return null;

            yield return Tap(keyboard.digit2Key);
            yield return LeftClickAt(ScreenPointOf(aim.Value));
            yield return TestWorld.WaitUntil(() => hostile.Current < before, 2f);

            Assert.That(hostile.Current, Is.LessThanOrEqualTo(before - 35));
            foreach (var friendly in squad)
                Assert.That(HealthOf(friendly).Current, Is.EqualTo(HealthOf(friendly).Max), $"{friendly.name} is never hit by its own side's blast");
        }

        [UnityTest]
        public IEnumerator Scene_Mend_HealsAFriendlyClickedWithTheMouse()
        {
            yield return LoadScene();
            Select(0);
            var ally = HealthOf(squad[2]);
            ally.TakeDamage(50);
            Assert.That(OnScreen(squad[2].transform.position), Is.True, "Precondition: the ally is on screen");
            yield return null;

            yield return Tap(keyboard.digit3Key);
            yield return LeftClickAt(ScreenPointOf(squad[2].transform.position));
            yield return TestWorld.WaitUntil(() => ally.Current > ally.Max - 50, 2f);

            Assert.That(ally.Current, Is.EqualTo(ally.Max - 10));
        }

        [UnityTest]
        public IEnumerator Scene_AnAbilityOutOfRange_IsRefusedWithItsReason_AndNothingHappens()
        {
            yield return LoadScene();
            Select(0);
            var hostile = HealthOf(hostiles[0]);
            yield return null;
            yield return Tap(keyboard.digit1Key);

            Assert.That(targeting.Confirm(PointerTarget.OnHostile(hostile, hostile.transform.position), false), Is.False);

            Assert.That(AbilitiesOf(squad[0]).LastFailure, Is.EqualTo(AbilityFailure.OutOfRange));
            Assert.That(hostile.Current, Is.EqualTo(hostile.Max));
            Assert.That(targeting.IsArmed, Is.True);
        }

        [UnityTest]
        public IEnumerator Scene_PausedPlanning_MoveThenMendThenMove_RunsInOrderAfterResume()
        {
            yield return LoadScene();
            Select(1);
            var unit = squad[1];
            var patient = HealthOf(squad[0]);
            patient.TakeDamage(50);
            var start = unit.transform.position;
            var first = start + new Vector3(0f, 0f, 3f);
            var last = first + new Vector3(3f, 0f, 0f);
            yield return null;

            pause.Pause();
            Assert.That(unit.Issue(new MoveCommand(first)), Is.True);
            yield return Tap(keyboard.digit3Key);
            Press(keyboard.leftShiftKey);
            yield return null;
            yield return LeftClickAt(ScreenPointOf(squad[0].transform.position));
            Release(keyboard.leftShiftKey);
            yield return null;
            Assert.That(unit.Issue(new MoveCommand(last), IssueMode.Append), Is.True);
            yield return new WaitForSecondsRealtime(0.3f);

            Assert.That(unit.PendingCommands.Select(c => c.GetType()), Is.EqualTo(new[] { typeof(AbilityCommand), typeof(MoveCommand) }));
            Assert.That(patient.Current, Is.EqualTo(patient.Max - 50), "Nothing ran while paused");
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, start), Is.LessThan(0.05f));

            var positionWhenUsed = Vector3.zero;
            AbilitiesOf(unit).Used += _ => positionWhenUsed = unit.transform.position;
            pause.Resume();
            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 20f);

            Assert.That(patient.Current, Is.EqualTo(patient.Max - 10), "The Mend ran");
            Assert.That(TestWorld.HorizontalDistance(positionWhenUsed, first), Is.LessThan(1f), "It ran after the first move");
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, last), Is.LessThan(1.5f), "and the last move followed");
        }

        [UnityTest]
        public IEnumerator Scene_ThePad_TriggerPlusDpad_ArmsMend_TheCursorSnapsAndConfirmHeals_WithoutStoppingTheUnit()
        {
            yield return LoadScene();
            var pad = InputSystem.AddDevice<XInputController>();
            yield return Tap(pad.selectButton);
            Assert.That(family.Family, Is.EqualTo(InputFamily.Xbox));
            Select(0);
            Assert.That(squad[0].Issue(new MoveCommand(squad[0].transform.position + new Vector3(0f, 0f, 6f))), Is.True);
            var ally = HealthOf(squad[2]);
            ally.TakeDamage(50);
            Assert.That(OnScreen(squad[2].transform.position), Is.True, "Precondition: the ally is on screen");

            Press(pad.rightTrigger);
            yield return null;
            yield return Tap(pad.dpad.down);
            Release(pad.rightTrigger);
            yield return null;
            yield return null;

            Assert.That(targeting.ArmedAbility.DisplayName, Is.EqualTo("Mend"));
            Assert.That(squad[0].StopCount, Is.EqualTo(0), "The held trigger made the D-pad pick, not stop");
            Assert.That(cursor.IsActive, Is.True, "Aiming gives the cursor the stick while the game runs");
            cursor.SetScreenPosition(ScreenPointOf(squad[2].transform.position));
            yield return null;
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Friendly));

            yield return Tap(pad.buttonSouth);
            yield return TestWorld.WaitUntil(() => ally.Current > ally.Max - 50, 2f);

            Assert.That(ally.Current, Is.EqualTo(ally.Max - 10));
            Assert.That(cursor.Aiming, Is.False);
            Assert.That(cursor.IsActive, Is.False, "The camera has its stick back");
        }

        [UnityTest]
        public IEnumerator Scene_SwitchingFromPadToMouseWhileAiming_KeepsTheAbilityArmed_AndTheMouseThenCasts()
        {
            yield return LoadScene();
            var pad = InputSystem.AddDevice<XInputController>();
            yield return Tap(pad.selectButton);
            Select(0);
            var ally = HealthOf(squad[2]);
            ally.TakeDamage(50);
            Press(pad.rightTrigger);
            yield return null;
            yield return Tap(pad.dpad.down);
            Release(pad.rightTrigger);
            yield return null;
            Assert.That(targeting.IsArmed, Is.True);

            Press(mouse.leftButton);
            yield return null;
            Release(mouse.leftButton);
            yield return null;
            yield return null;

            Assert.That(family.Family, Is.EqualTo(InputFamily.KeyboardMouse), "The mouse took the input back");
            Assert.That(targeting.IsArmed, Is.True, "Still armed");
            Assert.That(ally.Current, Is.EqualTo(ally.Max - 50), "The click that woke the mouse did not cast");

            yield return LeftClickAt(ScreenPointOf(squad[2].transform.position));
            yield return TestWorld.WaitUntil(() => ally.Current > ally.Max - 50, 2f);

            Assert.That(ally.Current, Is.EqualTo(ally.Max - 10));
            Assert.That(targeting.IsArmed, Is.False);
            Assert.That(cursor.Aiming, Is.False);
        }

        [UnityTest]
        public IEnumerator Scene_AbilityKills_CountTowardsVictory()
        {
            yield return LoadScene();
            Select(1);
            var target = BringHostileNear(hostiles[0], squad[1], 9f);
            HealthOf(hostiles[1]).TakeDamage(HealthOf(hostiles[1]).Max);
            HealthOf(hostiles[2]).TakeDamage(HealthOf(hostiles[2]).Max);
            var abilities = AbilitiesOf(squad[1]);
            yield return null;

            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(abilities.Definition(0), target)), Is.True);
            Assert.That(abilities.TryUse(AbilityCommand.AtGround(abilities.Definition(1), target.transform.position - Vector3.up)), Is.True);

            Assert.That(target.IsAlive, Is.False, "45 + 35 against 60 hit points");
            Assert.That(encounter.Outcome, Is.EqualTo(EncounterOutcome.Victory));
        }
    }
}
#endif
```

- [ ] **Step 2: Run to verify they fail for the right reason**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.PrototypeSceneAbilityTests"`
Expected: EXIT=2; every test fails with `The scene has no AbilityTargeting: run the scene builder`.

- [ ] **Step 3: Create the temporary scene builder**

`Assets/_Project/Editor/AbilitySceneBuilder.cs` (create the `Editor` folder if needed; **delete the file and its `.meta` in Step 5**):

```csharp
using System;
using System.Linq;
using Blackglass;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;

public static class AbilitySceneBuilder
{
    const string ScenePath = "Assets/_Project/Scenes/Prototype.unity";
    const string ControlsPath = "Assets/_Project/Input/BlackglassControls.inputactions";
    const string DataFolder = "Assets/_Project/Data";
    const string AbilityFolder = DataFolder + "/Abilities";
    const string ArchetypeFolder = DataFolder + "/Archetypes";
    const string LineMaterialPath = "Assets/_Project/Materials/AttackLine.mat";

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

    static void SetList(Component component, string field, params UnityEngine.Object[] values)
    {
        var so = new SerializedObject(component);
        var list = so.FindProperty(field);
        if (list == null || !list.isArray)
            throw new Exception($"{component.GetType().Name} has no serialized list '{field}'");
        list.arraySize = values.Length;
        for (var i = 0; i < values.Length; i++)
            list.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    static void EnsureFolder(string parent, string child)
    {
        if (!AssetDatabase.IsValidFolder(parent + "/" + child))
            AssetDatabase.CreateFolder(parent, child);
    }

    static T Asset<T>(string path, Action<SerializedObject> fill) where T : ScriptableObject
    {
        var existing = AssetDatabase.LoadAssetAtPath<T>(path);
        var asset = existing != null ? existing : ScriptableObject.CreateInstance<T>();
        var so = new SerializedObject(asset);
        fill(so);
        so.ApplyModifiedPropertiesWithoutUndo();
        if (existing == null)
            AssetDatabase.CreateAsset(asset, path);
        EditorUtility.SetDirty(asset);
        return asset;
    }

    static AbilityDefinition Ability(string file, string displayName, AbilityTargetMode mode, AbilityTargetSide side, float range,
        bool lineOfSight, AbilityCoverRule cover, float cooldown, AbilityEffect effect, int amount, float radius) =>
        Asset<AbilityDefinition>($"{AbilityFolder}/{file}.asset", so =>
        {
            so.FindProperty("displayName").stringValue = displayName;
            so.FindProperty("targetMode").enumValueIndex = (int)mode;
            so.FindProperty("targetSide").enumValueIndex = (int)side;
            so.FindProperty("range").floatValue = range;
            so.FindProperty("requiresLineOfSight").boolValue = lineOfSight;
            so.FindProperty("coverRule").enumValueIndex = (int)cover;
            so.FindProperty("cooldown").floatValue = cooldown;
            so.FindProperty("effect").enumValueIndex = (int)effect;
            so.FindProperty("amount").intValue = amount;
            so.FindProperty("radius").floatValue = radius;
        });

    static CombatArchetype Archetype(string file, string displayName, CombatRole role, float range, int damage, float interval) =>
        Asset<CombatArchetype>($"{ArchetypeFolder}/{file}.asset", so =>
        {
            so.FindProperty("displayName").stringValue = displayName;
            so.FindProperty("role").enumValueIndex = (int)role;
            so.FindProperty("range").floatValue = range;
            so.FindProperty("damage").intValue = damage;
            so.FindProperty("attackInterval").floatValue = interval;
        });

    public static void Build()
    {
        EnsureFolder("Assets/_Project", "Data");
        EnsureFolder(DataFolder, "Abilities");
        EnsureFolder(DataFolder, "Archetypes");

        var aimed = Ability("AimedShot", "Aimed Shot", AbilityTargetMode.Unit, AbilityTargetSide.Hostile, 14f, true,
            AbilityCoverRule.Applies, 6f, AbilityEffect.Damage, 45, 0f);
        var blast = Ability("Blast", "Blast", AbilityTargetMode.Ground, AbilityTargetSide.Hostile, 12f, true,
            AbilityCoverRule.Ignored, 10f, AbilityEffect.Damage, 35, 3f);
        var mend = Ability("Mend", "Mend", AbilityTargetMode.Unit, AbilityTargetSide.Friendly, 8f, false,
            AbilityCoverRule.Ignored, 8f, AbilityEffect.Heal, 40, 0f);
        var melee = Archetype("Melee", "Melee", CombatRole.Melee, 2f, 25, 1f);
        var ranged = Archetype("Ranged", "Ranged", CombatRole.Ranged, 8f, 15, 1f);
        var marksman = Archetype("Marksman", "Marksman", CombatRole.Ranged, 16f, 40, 2.5f);
        AssetDatabase.SaveAssets();

        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var systems = GameObject.Find("Systems");
        if (systems == null)
            throw new Exception("Systems not found");
        var controls = AssetDatabase.LoadAssetAtPath<InputActionAsset>(ControlsPath);
        var cameraRig = Find<TacticalCameraController>();
        var viewCamera = cameraRig.GetComponentInChildren<Camera>();
        if (viewCamera == null)
            throw new Exception("No camera under the camera rig");
        var encounter = Find<Encounter>();

        var archetypes = new[] { ("FriendlyUnit_1", melee), ("FriendlyUnit_2", marksman), ("FriendlyUnit_3", ranged) };
        foreach (var (unitName, archetype) in archetypes)
        {
            var unit = GameObject.Find(unitName);
            if (unit == null)
                throw new Exception($"{unitName} not found");
            Set(unit.GetComponent<UnitAttacker>(), "archetype", archetype);
            var abilities = GetOrAdd<UnitAbilities>(unit);
            SetList(abilities, "abilities", aimed, blast, mend);
            Set(abilities, "encounter", encounter);
        }

        var cursor = Find<TacticalCursor>();
        var inputDevice = Find<ActiveInputDevice>();

        var targeting = GetOrAdd<AbilityTargeting>(systems);
        Set(targeting, "viewCamera", viewCamera);
        Set(targeting, "selection", Find<UnitSelection>());
        Set(targeting, "activeCharacter", Find<ActiveCharacter>());
        Set(targeting, "cursor", cursor);
        Set(targeting, "pointerPositionAction", Reference("Commands/PointerPosition"));
        Set(targeting, "queueModifierAction", Reference("Commands/QueueModifier"));
        Set(targeting, "ability1Action", Reference("Commands/Ability1"));
        Set(targeting, "ability2Action", Reference("Commands/Ability2"));
        Set(targeting, "ability3Action", Reference("Commands/Ability3"));
        Set(targeting, "ability4Action", Reference("Commands/Ability4"));

        var gate = GetOrAdd<AbilityMenuGate>(systems);
        Set(gate, "menuAction", Reference("Commands/AbilityMenu"));
        SetList(gate, "suppressedWhileHeld", Reference("Commands/Stop"), Reference("Character/ToggleFollow"),
            Reference("Commands/NextTarget"), Reference("Commands/PreviousTarget"));

        var targetingView = GetOrAdd<AbilityTargetingView>(systems);
        Set(targetingView, "targeting", targeting);
        Set(targetingView, "lineMaterial", AssetDatabase.LoadAssetAtPath<Material>(LineMaterialPath));

        var bar = GetOrAdd<AbilityBarView>(systems);
        Set(bar, "targeting", targeting);
        Set(bar, "inputDevice", inputDevice);
        Set(bar, "controls", controls);
        Set(bar, "menuAction", Reference("Commands/AbilityMenu"));

        Set(Find<PlayerCommandInput>(), "abilityTargeting", targeting);
        Set(cameraRig, "cursor", cursor);

        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene))
            throw new Exception("Saving the scene failed");
        Debug.Log($"[AbilitySceneBuilder] Wired abilities and archetypes into {ScenePath}");
    }
}
```

- [ ] **Step 4: Run the builder (Editor closed) and check the result**

```bash
"/c/Program Files/Unity/Hub/Editor/6000.3.25f1/Editor/Unity.exe" -batchmode -quit -projectPath "$(pwd -W)" -executeMethod AbilitySceneBuilder.Build -logFile "$(pwd -W)/Logs/BuildAbilityScene.log"; echo "EXIT=$?"
grep -E '\[AbilitySceneBuilder\]|error CS|Exception' Logs/BuildAbilityScene.log | head -20
git status --short
git diff --stat Assets/_Project/Scenes/Prototype.unity
```
Expected: `EXIT=0`, the `[AbilitySceneBuilder] Wired ...` line and no exception; `git status` shows the new `Data/` folders and assets (with `.meta`), the modified scene, and the builder (to be deleted); the scene diff is added components plus changed reference lines only (no NavMesh, transform or unrelated churn). If the diff is large, report before continuing.

- [ ] **Step 5: Delete the builder, update the one pinned expectation, run the scene tests**

```bash
rm Assets/_Project/Editor/AbilitySceneBuilder.cs Assets/_Project/Editor/AbilitySceneBuilder.cs.meta
rmdir Assets/_Project/Editor 2>/dev/null; rm -f Assets/_Project/Editor.meta
```

In `Assets/_Project/Tests/PlayMode/PrototypeSceneTests.cs` replace the four lines

```csharp
                var expectedRole = friendly.name == "FriendlyUnit_3" ? CombatRole.Ranged : CombatRole.Melee;
                Assert.That(friendly.GetComponent<UnitAttacker>().Role, Is.EqualTo(expectedRole), friendly.name);
                Assert.That(friendly.GetComponent<UnitAttacker>().Range, Is.EqualTo(expectedRole == CombatRole.Ranged ? 8f : 2f), friendly.name);
                Assert.That(friendly.GetComponent<UnitAttacker>().Damage, Is.EqualTo(expectedRole == CombatRole.Ranged ? 15 : 25), friendly.name);
```

with:

```csharp
                // Phase 7 archetypes: FriendlyUnit_2 is the Marksman (long range, hard hitting), FriendlyUnit_3 the standard ranged.
                var (expectedRole, expectedRange, expectedDamage) = friendly.name switch
                {
                    "FriendlyUnit_2" => (CombatRole.Ranged, 16f, 40),
                    "FriendlyUnit_3" => (CombatRole.Ranged, 8f, 15),
                    _ => (CombatRole.Melee, 2f, 25),
                };
                Assert.That(friendly.GetComponent<UnitAttacker>().Role, Is.EqualTo(expectedRole), friendly.name);
                Assert.That(friendly.GetComponent<UnitAttacker>().Range, Is.EqualTo(expectedRange), friendly.name);
                Assert.That(friendly.GetComponent<UnitAttacker>().Damage, Is.EqualTo(expectedDamage), friendly.name);
```

Then:

```bash
Tools/run-tests.sh PlayMode "Blackglass.Tests.PrototypeSceneAbilityTests"
Tools/run-tests.sh PlayMode "Blackglass.Tests.PrototypeSceneTests"
Tools/run-tests.sh PlayMode "Blackglass.Tests.PrototypeSceneControllerTests"
```
Expected: EXIT=0; 10 ability scene tests. If `PrototypeSceneTests` or `PrototypeSceneControllerTests` fail because `FriendlyUnit_2` is now ranged (it stands off at 16 m instead of walking up), see Deviation 4: change only an assertion about its role or stats, report it, and never loosen a behaviour assertion. If an ability scene test fails because a probe cannot find an open, visible spot (`BringHostileNear` fails) or a click point is off-screen, adjust the probe distance or offsets in the test and report it; if the failure is in production code, fix the code.

- [ ] **Step 6: Run both full suites**

`Tools/run-tests.sh EditMode` → EXIT=0, 511 passed. `Tools/run-tests.sh PlayMode` → EXIT=0, 499 + 10 = 509 passed.

- [ ] **Step 7: Commit**

```bash
git add Assets/_Project/Data Assets/_Project/Scenes Assets/_Project/Tests
git status --short   # no Assets/_Project/Editor left behind
git commit -m "Give the squad abilities and archetypes in the prototype scene and test them in the real arena

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 11: Decision record, verification and hand-off

**Files:**
- Modify: `Docs/Decisions.md`
- Modify: `Docs/superpowers/specs/2026-10-06-abilities-design.md` (status line only)

- [ ] **Step 1: Record decision 025 and the amendments**

Append to `Docs/Decisions.md`:

```markdown

## 025 — Combat archetypes and abilities

- **Decided (Phase 7, 2026-10-06):**
  - **Archetypes:** `CombatArchetype` ScriptableObjects (role, range, damage, attack interval) assigned on `UnitAttacker.archetype` and applied at `Awake`; the inline fields remain the fallback, so existing units and tests are unchanged. The role still decides line of sight and cover (melee needs neither and ignores cover; ranged needs sight and is affected by cover). Three exist: Melee (2 / 25 / 1.0 s), Ranged (8 / 15 / 1.0 s) and Marksman (16 / 40 / 2.5 s); `FriendlyUnit_1`/`_2`/`_3` use them. Hostiles keep inline values. There is no inventory or equipment.
  - **Abilities:** `AbilityDefinition` is an immutable ScriptableObject (target mode Unit or Ground, side Hostile or Friendly, range, line-of-sight flag, cover rule Applies or Ignored, cooldown, effect Damage or Heal, amount, radius). `UnitAbilities` on each unit holds up to four definitions and, per slot, the **scaled** time at which it is ready again, so cooldowns freeze while paused and belong to the unit (they survive control-mode changes and character switching, and two units sharing a definition never share a cooldown). Mutable state is never stored in an asset. Prototype abilities: Aimed Shot (single target, needs sight, cover applies), Blast (ground area, needs sight to the point, ignores cover) and Mend (heal a friendly, itself included, no sight needed).
  - **One validation:** `AbilityRules` and `UnitAbilities.Check`, in this order: caster alive, ability known, target (or position) valid and on the right side, cooldown, range (flat, inclusive), line of sight. The preview, `Issue` and execution all use it. Failures are typed (`AbilityFailure`), kept on the unit (`LastFailure`) and raised as events; an expected failure never writes to the Console.
  - **Command integration:** `AbilityCommand` rides the existing queue (Replace and Append, group orders, Stop, companion park rules all unchanged). It is instant. When it would start now `Issue` checks everything; behind other orders it checks only who and what (the caster will have moved on). It is validated again on the first **running** frame, before steering is considered, so nothing fires while paused and a held move key cannot drop an ability that direct control just issued. A failure ends the order without a cooldown and the queue moves on, so a unit never sticks. No auto-approach: an out-of-range ability fails (Attack keeps its approach).
  - **No friendly fire:** area damage hits only units hostile to the caster (`Encounter.OpponentsOf`); no ability can hurt its own side. The owner does not want friendly fire in the game.
  - **Cover is an explicit rule per ability**, not an accident: Aimed Shot uses the same hit-chance roll as basic ranged attacks (`UnitAttacker.RollHit`), Blast ignores cover.
  - **Targeting:** `AbilityTargeting` arms a slot of the caster's abilities (the caster is the first eligible selected unit, else the active character, also while paused), resolves the pointer every frame (the mouse through `PointerTargetResolver`, the controller through the Phase 6.5 `TacticalCursor`, which is reused, not duplicated), and exposes a live preview. While armed, `PlayerCommandInput.Act` hands a click or Confirm to it, Cancel disarms before it clears the selection, and a change of caster or its death disarms. The cursor gets `Aiming` (the right stick is the cursor's whatever else would own it, `StickRole`) and `SnapTo` (hostile, friendly or none for an area).
  - **Input:** semantic actions `Ability1`-`Ability4` and `AbilityMenu`. Keyboard: keys 1-4. Pads (every family): hold the right trigger and press a D-pad direction (up, right, down, left), as `ButtonWithOneModifier` chord bindings; Confirm/Cancel/queue modifier are the existing ones. Input System shortcut consumption is **off** by default in 1.20, so `AbilityMenuGate` disables the plain D-pad actions (Stop, Follow, previous/next target) while the menu trigger is held and gives back exactly what it took. Select/View/Create stays reserved and unbound.
  - **Debug only:** `AbilityTargetingView` (range circle, aim ring, line, green/red), `AbilityBarView` (slots, prompts, cooldowns, preview line, orders, last failure), the unit label shows the archetype name, `CommandQueueView` draws ability orders.
- **Why:** the owner wants the first real combat choices without an inventory, stats or a scripting layer; immutable data plus a small per-unit runtime keeps the framework smaller than the future feature list while a grenade, a burst or a buff stays a new field or effect, and the existing command queue, pause, cursor, LOS and cover are reused instead of duplicated.
- **Rejected:** one MonoBehaviour per ability (duplicated targeting, cooldown and validation); a strategy ScriptableObject per effect (more than three effects need); migrating the basic Attack onto abilities now (it would rewrite working combat, AI and about 700 tests); a separate tactical ability scheduler (the queue already orders and parks); auto-approach or projecting the caster's future position for queue-time checks (the brief says fail cleanly; projection needs path prediction); cooldown state on the ScriptableObject; a radial menu or a mode that remaps LB/RB (code that knows modes); turning on Input System shortcut consumption project-wide; friendly fire.
- **Implications:** a new ability is a new `AbilityDefinition` asset when it fits the fields, otherwise a new `AbilityEffect` handled in `UnitAbilities.Apply`; every new action still needs bindings in all four pad groups (the asset tests enforce it); gameplay code must not name devices or buttons. The cursor's `Aiming` and `SnapTo` are public state set by `AbilityTargeting` only. A queued ability is judged from where the unit ends up, while the planning preview judges from where it stands (it says so while the queue modifier is held).
- **Known limitations:** one caster per cast and four slots (no groups, pages or hotkey rebinding UI); instant casts (no wind-up, interruption or projectile); enemy AI does not use abilities; the basic Attack is not an ability yet (a Phase 8 question: one action framework for both); while armed the camera cannot be moved with the right stick; the chord ergonomics (right index finger on the trigger, thumb on the D-pad) are untested on hardware (every pad test uses simulated devices); FriendlyUnit_2 stands off at 16 m now, which changes how that companion fights.
```

Also append one sentence to each of these existing records (find their last bullet and add an `- **Amended (Phase 7, 2026-10-06):**` bullet): **006** ("an `AbilityCommand` joins Move, Attack, MoveToCover and Stop; it is validated at issue and again when it runs") and **024** ("the right trigger plus a D-pad direction picks an ability slot (`AbilityMenuGate` suppresses the plain D-pad actions while it is held); while an ability is armed the right stick is the cursor's in real time too; Select/View/Create is still reserved").

In the spec file change the status line to `Date: 2026-10-06. Status: implemented on branch phase-7-abilities (see Docs/Decisions.md record 025).`

- [ ] **Step 2: Full verification (superpowers:verification-before-completion)**

```bash
Tools/run-tests.sh EditMode
Tools/run-tests.sh PlayMode
```
Expected: EXIT=0 for both; EditMode **511** passed, PlayMode **509** passed (baseline 439 / 405 plus 72 and 104 new tests). Report the exact numbers from the run.

Console check: compare the new logs with the baselines and list anything new.

```bash
grep -hE "Exception|error CS|\bError\b|LogError|Assertion failed" Logs/TestRun-PlayMode.log | sort | uniq -c | sort -rn | head -20 > Logs/phase7-final-errors.txt
grep -hE "Exception|error CS|\bError\b|LogError|Assertion failed" Logs/phase7-baseline-PlayMode.log | sort | uniq -c | sort -rn | head -20 > Logs/phase7-baseline-errors.txt
diff Logs/phase7-baseline-errors.txt Logs/phase7-final-errors.txt && echo "Console: no new error lines"
```
Expected: identical (or only lines that also appeared in the baseline). Any new line is investigated, not dismissed.

- [ ] **Step 3: Check the scope list and the working tree**

```bash
git status --short
git grep -nE "Gamepad\.current|Keyboard\.current|Mouse\.current|buttonSouth|buttonEast|buttonWest|buttonNorth|rightTrigger" -- Assets/_Project/Scripts ':!Assets/_Project/Scripts/Input'
```
Expected: `git status` shows only `?? .claude/`; the grep prints nothing (ability code names no device or button; the actions asset is not under `Scripts/`). `Assets/_Project/Editor` must not exist. No `inventory`, `ammo`, `armor`, `loot` or status-effect code was added: `git diff main --stat` shows only the files listed in the File Structure section.

- [ ] **Step 4: Request the whole-branch review (superpowers:requesting-code-review)**

The final reviewer reads the spec, this plan, decision 025 and `git diff main...HEAD`, and checks in particular the five Review Focus items, the chord/gate interaction on a family switch, that `AbilityTargeting` leaves `TacticalCursor` in its normal mode after every exit path, and the friendly-fire policy (no code path lets an ability hurt the caster's own side). Fix Critical and Important findings and re-run both suites.

- [ ] **Step 5: Commit the documentation and stop**

```bash
git add Docs
git commit -m "Record decision 025: combat archetypes and abilities

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

Do **not** merge or push: use `superpowers:finishing-a-development-branch` and let the owner choose. Update the memory notes (phase 7 state, the no-friendly-fire preference, the Input System facts learned: shortcut consumption off by default, DualSense simulation limits unchanged).

- [ ] **Step 6: The completion report**

Report, in this order (the owner's Phase 7 list): files created; files changed; ability architecture; weapon/combat archetype implementation; data-definition approach; runtime cooldown storage; abilities implemented; target-validation flow; single-target targeting; ground/AOE targeting; controller ability-selection method (hold the right trigger + a D-pad direction, with the gate); controller targeting integration (the reused cursor, `Aiming`, `SnapTo`); tactical command integration; LOS/cover integration; the manual tests below; known limitations; architecture concerns before Phase 8 (the basic Attack outside the ability framework, the one-caster rule, the public `Aiming`/`SnapTo` state on the cursor, the IMGUI debug UI's growth, enemy AI not using abilities, archetype-vs-ability overlap of range and damage fields). State the 29 validation items against evidence (test names) and say plainly which could only be checked with simulated devices.

Manual checks for the owner (also in the spec, §12): keys 1-3 arm Aimed Shot, Blast and Mend, and Esc disarms; Aimed Shot out of range, behind a wall and on a covered target (reasons show); Blast free aim on the ground, red out of range, the preview lists the hostiles it would hit, only hostiles take damage; Mend on a wounded friendly and on the caster; pause (Space), queue Move, Aimed Shot, Blast with Shift, confirm nothing fires, resume and watch the order; kill a target while an ability is queued on it; cooldowns stand still during a pause and differ between two units; on a controller hold the right trigger and press each D-pad direction, aim with the right stick, Confirm and Cancel, check that D-pad down with the trigger held does not stop the unit, LT queues, and plugging in or moving the mouse mid-aim keeps the ability armed; Tab / Shift+Tab while armed disarms; a companion is parked after an ability order; victory and defeat; the Console stays clean.

---

## Self-review (run after the plan was written)

**Spec coverage.** §1 scope/limits: Global Constraints, Deviations. §2 architecture/components: File Structure; `AbilityDefinition`/`AbilityFailure`/`AbilityRules`/`AbilityCommand` (Task 2), `CombatArchetype` (3), `UnitAbilities` (4), `CommandableUnit` (5), `AbilityMenuGate` (6), `AbilityTargeting` (8), views (9), data and scene (10). §3 archetypes: Tasks 3 and 10. §4.1 definition and runtime state: Tasks 2 and 4. §4.2 validation order: Task 2 (`AbilityRulesTests` order test) and Task 4. §4.3 command and queue (issue scopes, execution, direct control, follow/park, death): Task 5. §4.4 effects and cover: Task 4 (cover fixture). §4.5 friendly fire: Tasks 4, 8 and 10. §4.6 prototype abilities: `TestWorld` helpers and Task 10 assets. §5 targeting and pause: Tasks 7 and 8. §6 preview and debug: Task 9. §7 input (actions, chord, gate, prompts): Task 6; stick/cursor/camera: Task 7. §8 data and scene: Task 10; decision 025: Task 11. §9 testing list: covered across Tasks 1-10. §11/§12: Task 11.

**Placeholder scan.** No "TBD"/"TODO"; every code step has code; the only deferred value is "re-measure the baseline" (Task 0), with the measured numbers stated.

**Type consistency.** `AbilityCheck`, `AbilityPreview`, `AbilityFacts`, `AbilityCheckScope`, `UnitAbilities.Check(ability, target, Vector3? point, scope)`, `CanStartNow/CanQueue`, `AbilityTargeting.Confirm(PointerTarget, bool)`, `TacticalCursor.Aiming/SnapTo`, `StickRole` third parameter, `PlayerCommandInput.Initialize(..., AbilityTargeting)`, `TacticalCameraController.Initialize(..., TacticalCursor)`, `AbilityMenuGate.Initialize(menu, params suppressed)` are used with the same names in every later task and in `AbilityRig`.

**Review Focus coverage.** (1) Task 5 (three failing-when-run tests and the next-order assertion); (2) Task 6 gate tests and Task 8/10 pad tests; (3) Task 8 (selection change, caster death, cancel, cursor mode reset) and Task 10 (hot switch); (4) Task 4 (edge, caster-centred, dead hostile, no encounter); (5) Task 5 (direct control, companion park).
