# Tactical Combat: Roles, Line of Sight and Companions (Phase 5) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Melee and ranged combat roles, a shared line-of-sight check that ranged attacks must pass, repositioning when sight is blocked, hostiles that pick the nearest visible reachable friendly, and companions that follow the controlled character and assist in fights unless the player has given them orders, all frozen by tactical pause.

**Architecture:**
- Decision sources (`PlayerCommandInput`, `EnemyAI`, the new `CompanionAI`, `AutoRetaliate`) only ever call `CommandableUnit.Issue`. Shared capabilities carry the orders out: `CommandableUnit` runs an attack order in three phases (approach, reposition, attack), `UnitAttacker` owns role, range, cooldown and the sight test, `UnitMover` owns paths and reachability, `Health` owns hit points.
- `LineOfSight` is the one sight test (a ray from an eye to a point; colliders with a `Health` in their parents never block). `FiringPositionFinder` is a pure 16-candidate search.
- `CompanionAI` tells its own orders from everyone else's by remembering the command object it issued. Priority: dead, controlled, explicit orders, assist, follow, idle. Every new `Update` checks `SimulationTime.IsRunning`.

**Tech Stack:** Unity 6000.3.25f1 (Unity 6.3 LTS), URP 17.3.0, Input System 1.20.0, AI Navigation 2.0.14, Unity Test Framework 1.6.0 (NUnit), `InputTestFixture`.

**Spec:** `Docs/superpowers/specs/2026-10-04-tactical-combat-roles-design.md`

## Global Constraints

- **Unity Editor:** exactly 6000.3.25f1 at `C:\Program Files\Unity\Hub\Editor\6000.3.25f1\Editor\Unity.exe`. **The Editor must be closed during every batch-mode run** (one instance per project). If a run prints "The Unity Editor has this project open", stop and ask the owner to close it.
- **Code location:** runtime code in `Assets/_Project/Scripts/` (assembly `Blackglass`, namespace `Blackglass`); tests in `Assets/_Project/Tests/EditMode/` and `Assets/_Project/Tests/PlayMode/` (namespace `Blackglass.Tests`). No new assemblies, assembly references or packages. Input actions asset unchanged; no new bindings.
- **Commands are data** (decision 006). Attacks happen only through `CommandableUnit.Issue(new AttackCommand(target))`. AI scripts never call `UnitMover`, `UnitAttacker.TryAttack` or `Health.TakeDamage` (tests may call `TakeDamage` to stage deaths and `TryAttack` to pin its rules).
- **Input components** never touch `UnitMover`, `UnitAttacker`, NavMeshAgents or `Health`. `PlayerCommandInput`, `DirectControlInput` and `ActiveCharacter` are not modified by this plan.
- **Pause ownership:** only `TacticalPause` writes `Time.timeScale` in game code. Tests may reset it to 1 in `TearDown`. Simulation code checks `SimulationTime.IsRunning`; camera, input, HUD use unscaled time.
- **Wiring:** serialized Inspector references only. No singletons, no `Find*`/`Camera.main` in game code, no static mutable state. Components expose `internal Initialize(...)` for tests, called while the host GameObject is inactive or before its first `Update`.
- **Unity null rule:** never use `??`, `??=` or `?.` on `UnityEngine.Object` references (a missing component is the Editor's placeholder, not a C# null). Use `== null`, `TryGetComponent`, or `ReferenceEquals` when "destroyed" must differ from "none". `??=` is fine on plain C# objects such as `NavMeshPath` or `GUIStyle`.
- **Lifecycle:** EditMode tests get no `Awake`/`OnEnable`/`Update`; behaviour that needs them is tested in PlayMode. PlayMode fighters are assembled on an inactive GameObject, then activated (`TestWorld.CreateFighter`).
- **Physics:** colliders created or moved at runtime are invisible to raycasts until the next physics step; `TestWorld.CreateEnvironment` calls `Physics.SyncTransforms()` for obstacles, and tests that raycast at units created this frame first `yield return new WaitForFixedUpdate()`.
- **Prototype numbers (copy verbatim):** melee range 2 m; ranged range 8 m; friendly ranged damage 15, cooldown 1.0 s; hostile ranged damage 8, cooldown 1.5 s; eye height 0.5 m above the pivot; hit buffer 8; firing candidates 8 directions × radii {2 m, 4 m} around the unit's ground point; snap radius `UnitMover.SnapRadius` 2 m everywhere; `RepositionInterval` 0.5 s; `RepositionWalkTimeout` 3 s; `MaxRepositionsWithoutShot` 3; `ChaseRepathDistance` 0.5 m; detection range 12 m; think interval 0.25 s; `followDistance` 3.5 m; `followStartDistance` 6 m; `followRepathDistance` 2 m; `assistRange` 10 m.
- **Visuals:** debug only. IMGUI text, primitives with placeholder materials. Each unit has exactly one collider (its capsule).
- **Scope:** no cover system, crouching, suppression, stealth, perception cones, threat tables, formations, flanking, kiting, ammunition, projectiles, stats, abilities, inventory, save/load, production UI, new packages. Phase 6 is not started.
- **Git:** work on local branch `prototype/combat-roles-and-companions` (exists; spec committed). Never push or merge. Commit after each task. Messages are sentence case and imperative, and end with the line `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`. Commit `.meta` files with their assets. Never commit `Assets/_Project/Editor/`.
- **Test runs:** always `Tools/run-tests.sh`, 10-minute timeout for PlayMode runs. Scene tests fail on any logged error.
- **Writing style:** XML summary on every public type; comments explain why, not what.

### Running tests

```bash
Tools/run-tests.sh EditMode                                              # all EditMode tests
Tools/run-tests.sh PlayMode                                              # all PlayMode tests
Tools/run-tests.sh PlayMode Blackglass.Tests.CompanionAIPlayModeTests    # one fixture (any -testFilter expression)
```

The script prints `error CS` lines, a summary (`result= total= passed= failed=`), failed test names and messages, and `EXIT=<code>`: **0** all passed, **2** some failed, **1** could not run (a compile error). A "red" TDD step is usually **EXIT=1 with `error CS0117`/`CS1061`/`CS1501`/`CS0246`** because new members do not exist yet. Watch for the summary line, not the exit code alone.

**Baseline before this plan (measured 2026-10-04):** EditMode 208/208, PlayMode 178/178.

**Expected totals after each task:**

| After task | EditMode | PlayMode |
|---|---|---|
| 1 | 208 | 180 |
| 2 | 212 | 181 |
| 3 | 212 | 185 |
| 4 | 217 | 185 |
| 5 | 219 | 191 |
| 6 | 219 | 193 |
| 7 | 231 | 193 |
| 8 | 231 | 206 |
| 9 | 231 | 208 |
| 10 | 238 | 208 |
| 11 | 238 | 210 |
| 12 | 238 | 210 |

These totals assume each task adds exactly the tests listed. If a task's count differs, correct this table in that task's commit; the sequence must stay green either way.

## Review Focus

1. **A ranged unit whose target hides behind a wide wall, or stands where no path leads** → it must not stand still forever, nor search every frame: it walks at the target after three fruitless repositions or when no candidate is valid, and ends the order if that walk arrives still blind. Pinned in Task 5 (`RangedFighter_TargetWalledOnThreeSides_FallsBackToApproach_AndHits`, `RangedFighter_TargetEnclosedOnFourSides_EndsTheOrder_AndRunsTheNextOne`).
2. **Shift-queued order behind a companion's own follow move** → the queued order survives and runs after the follow move; the AI issues nothing in between. Pinned in Task 8 (`QueuedOrder_BehindAFollowMove_IsNotInterrupted`).
3. **Tab onto a companion mid-follow** → the newly controlled unit drops its own follow move and nothing else; the old leader starts following. Pinned in Task 8 (`SwitchingTheControlledCharacter_SwapsRoles`).
4. **Idle, unaware hostile 5 m from a companion** → no autonomous attack; companions never start fights. Pinned in Task 8 (`UnawareIdleHostileNearby_IsLeftAlone`).
5. **Pause while a ranged unit is walking to a firing position and a companion is following** → neither moves, no order appears, health and cooldowns hold; both resume afterwards. Pinned in Task 9 (`Paused_RepositioningUnit_DoesNotAdvance`, `Paused_CompanionNeitherMovesNorGainsOrders_AndAcceptsOrders`).

---

### Task 1: `LineOfSight` helper; `UnitAttacker` owns the sight test; `EnemyAI` uses it

**Files:**
- Create: `Assets/_Project/Scripts/Combat/LineOfSight.cs`
- Modify: `Assets/_Project/Scripts/Units/UnitAttacker.cs`
- Modify: `Assets/_Project/Scripts/AI/EnemyAI.cs` (remove `sightBlockers`, `eyeHeight`, `sightHits`, `HasLineOfSight`)
- Create: `Assets/_Project/Tests/PlayMode/LineOfSightTests.cs`
- Modify: `Assets/_Project/Tests/PlayMode/EnemyAIPlayModeTests.cs` (remove `HasLineOfSight_UnitsNeverBlock_WallsDo`)

**Interfaces:**
- Produces: `public static class LineOfSight { public const float EyeHeight = 0.5f; public const int HitBufferSize = 8; public static bool IsClear(Vector3 eye, Vector3 point, LayerMask blockers, RaycastHit[] buffer); public static bool IsClear(Vector3 pivot, Health target, LayerMask blockers, RaycastHit[] buffer); }`
- Produces: `UnitAttacker.HasLineOfSight(Health target)`, `UnitAttacker.HasLineOfSightFrom(Vector3 pivot, Health target)` (both from pivot + `LineOfSight.EyeHeight` to the target's pivot, with the attacker's serialized `sightBlockers` mask).

- [ ] **Step 1: Write the failing tests**

Create `Assets/_Project/Tests/PlayMode/LineOfSightTests.cs`:

```csharp
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class LineOfSightTests
    {
        TestWorld world;

        [SetUp]
        public void SetUp() => world = new TestWorld();

        [TearDown]
        public void TearDown() => world.Dispose();

        static Health HealthOf(Component unit) => unit.GetComponent<Health>();

        [UnityTest]
        public IEnumerator IsClear_UnitsNeverBlock_WallsDo()
        {
            world.CreateEnvironment((new Vector3(5f, 1f, 0f), new Vector3(1f, 2f, 6f)));
            var seen = world.CreateFighter(new Vector3(0f, 0f, -4f));
            var blocker = world.CreateFighter(new Vector3(0f, 0f, -2f));
            var behindWall = world.CreateFighter(new Vector3(8f, 0f, 0f));
            yield return new WaitForFixedUpdate();   // colliders take their positions
            var buffer = new RaycastHit[LineOfSight.HitBufferSize];
            var eye = new Vector3(0f, 1.5f, 1f);

            Assert.That(LineOfSight.IsClear(eye, HealthOf(seen).transform.position, ~0, buffer), Is.True, "A unit in between must not block sight");
            Assert.That(LineOfSight.IsClear(eye, HealthOf(blocker).transform.position, ~0, buffer), Is.True);
            Assert.That(LineOfSight.IsClear(eye, HealthOf(behindWall).transform.position, ~0, buffer), Is.False, "A wall must block sight");
        }

        [UnityTest]
        public IEnumerator IsClear_FromAPivot_LooksFromTheEyeHeight_SoALowCrateDoesNotBlock()
        {
            // A 1 m crate halfway: the line from the eye (1.5 m) to the target's pivot (1 m) passes 1.25 m up there.
            // A 2 m wall at the same spot blocks it.
            world.CreateEnvironment((new Vector3(0f, 0.5f, 0f), new Vector3(1f, 1f, 1f)), (new Vector3(6f, 1f, 0f), new Vector3(1f, 2f, 1f)));
            var overCrate = world.CreateFighter(new Vector3(0f, 0f, 4f));
            var behindWall = world.CreateFighter(new Vector3(6f, 0f, 4f));
            yield return new WaitForFixedUpdate();
            var buffer = new RaycastHit[LineOfSight.HitBufferSize];

            Assert.That(LineOfSight.IsClear(new Vector3(0f, 1f, -4f), HealthOf(overCrate), ~0, buffer), Is.True, "A 1 m crate must not block a 1.5 m eye");
            Assert.That(LineOfSight.IsClear(new Vector3(6f, 1f, -4f), HealthOf(behindWall), ~0, buffer), Is.False, "A 2 m wall must block");
            Assert.That(LineOfSight.EyeHeight, Is.EqualTo(0.5f));
        }

        [UnityTest]
        public IEnumerator UnitAttacker_HasLineOfSight_FromItsOwnPivot_AndFromAnotherPoint()
        {
            world.CreateEnvironment((new Vector3(0f, 1f, 0f), new Vector3(3f, 2f, 1f)));
            var attacker = world.CreateFighter(new Vector3(0f, 0f, -4f));
            var target = world.CreateFighter(new Vector3(0f, 0f, 4f));
            yield return new WaitForFixedUpdate();
            var unitAttacker = attacker.GetComponent<UnitAttacker>();

            Assert.That(unitAttacker.HasLineOfSight(HealthOf(target)), Is.False, "The wall is between them");
            Assert.That(unitAttacker.HasLineOfSightFrom(new Vector3(4f, 1f, -4f), HealthOf(target)), Is.True, "From 4 m to the side the wall is cleared");
            Assert.That(unitAttacker.HasLineOfSightFrom(new Vector3(0f, 1f, -8f), HealthOf(target)), Is.False);
        }
    }
}
```

Delete the test `HasLineOfSight_UnitsNeverBlock_WallsDo` (the last test) from `Assets/_Project/Tests/PlayMode/EnemyAIPlayModeTests.cs`; its body moved to `IsClear_UnitsNeverBlock_WallsDo` above.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `Tools/run-tests.sh PlayMode Blackglass.Tests.LineOfSightTests` → expected `EXIT=1` with `error CS0103: The name 'LineOfSight' does not exist` and `error CS1061 ... 'UnitAttacker' does not contain a definition for 'HasLineOfSight'`.

- [ ] **Step 3: Create `LineOfSight`**

Create `Assets/_Project/Scripts/Combat/LineOfSight.cs`:

```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The one line-of-sight test for combat: a ray from an eye to a point, blocked by any collider that is not part of
    /// a unit (anything with a Health in its parents never blocks sight). UnitAttacker uses it for ranged attacks and
    /// firing positions, EnemyAI for acquisition. Static, stateless, allocation-free with a caller-owned buffer.
    /// </summary>
    public static class LineOfSight
    {
        /// <summary>Eye height above a unit's pivot (the capsule centre, 1 m up), so the eye is 1.5 m above the ground.</summary>
        public const float EyeHeight = 0.5f;
        /// <summary>Enough for the few units and walls a sight line can cross in the prototype.</summary>
        public const int HitBufferSize = 8;

        /// <summary>True when nothing but units lies between the eye and the point. Triggers are ignored.</summary>
        public static bool IsClear(Vector3 eye, Vector3 point, LayerMask blockers, RaycastHit[] buffer)
        {
            var toPoint = point - eye;
            var distance = toPoint.magnitude;
            if (distance <= 0.001f)
                return true;
            var count = Physics.RaycastNonAlloc(eye, toPoint / distance, buffer, distance, blockers, QueryTriggerInteraction.Ignore);
            for (var i = 0; i < count; i++)
            {
                if (buffer[i].collider.GetComponentInParent<Health>() == null)
                    return false;
            }
            return true;
        }

        /// <summary>IsClear from the eye above a unit pivot to the target's pivot.</summary>
        public static bool IsClear(Vector3 pivot, Health target, LayerMask blockers, RaycastHit[] buffer) =>
            IsClear(pivot + Vector3.up * EyeHeight, target.transform.position, blockers, buffer);
    }
}
```

- [ ] **Step 4: Give `UnitAttacker` the sight test**

In `Assets/_Project/Scripts/Units/UnitAttacker.cs`:

(a) Replace the class summary with:

```csharp
    /// <summary>
    /// Prototype attack: fixed range, damage and cooldown. Cooldown uses scaled time, so it freezes while paused.
    /// Reports each hit through Attacked and tells the target which Health hit it. Owns the unit's sight test
    /// (LineOfSight from this unit's eye), which EnemyAI uses for acquisition.
    /// </summary>
```

(b) After the `cooldown` field add:

```csharp
        // Everything blocks sight except units (see LineOfSight). Serialized so a layer scheme can narrow it later.
        [SerializeField] LayerMask sightBlockers = ~0;

        readonly RaycastHit[] sightHits = new RaycastHit[LineOfSight.HitBufferSize];
```

(c) After `IsInRange` add:

```csharp
        /// <summary>True when the line from this unit's eye to the target's pivot crosses no world geometry.</summary>
        public bool HasLineOfSight(Health target) => HasLineOfSightFrom(transform.position, target);

        /// <summary>The same test from the eye a unit would have standing at `pivot`.</summary>
        public bool HasLineOfSightFrom(Vector3 pivot, Health target) =>
            target != null && LineOfSight.IsClear(pivot, target, sightBlockers, sightHits);
```

- [ ] **Step 5: Make `EnemyAI` use it**

In `Assets/_Project/Scripts/AI/EnemyAI.cs`:

(a) Delete the `SightHitBufferSize` constant, the `sightBlockers` and `eyeHeight` fields (and the comment above `eyeHeight`), the `sightHits` field, and the whole `HasLineOfSight` method with its summary.

(b) In `FindNearestVisibleFriendly`, delete the line `var eye = transform.position + Vector3.up * eyeHeight;` and replace

```csharp
                if (!HasLineOfSight(eye, candidate, sightBlockers, sightHits))
                    continue;
```

with

```csharp
                if (!Attacker.HasLineOfSight(candidate))
                    continue;
```

(c) In the class summary, replace `Line of sight gates acquisition only: a target once taken is followed around corners.` with `Line of sight (UnitAttacker.HasLineOfSight) gates acquisition only: a target once taken is followed around corners.`

- [ ] **Step 6: Run the tests to verify they pass**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.LineOfSightTests|Blackglass.Tests.EnemyAIPlayModeTests"` → expected `total="13" passed="13"`, `EXIT=0` (3 new, 10 remaining enemy tests).
Run: `Tools/run-tests.sh PlayMode` → expected `total="180" passed="180"`, `EXIT=0`.
Run: `Tools/run-tests.sh EditMode` → expected `total="208" passed="208"`, `EXIT=0`.

- [ ] **Step 7: Commit**

```bash
git add Assets/_Project/Scripts/Combat/LineOfSight.cs Assets/_Project/Scripts/Combat/LineOfSight.cs.meta Assets/_Project/Scripts/Units/UnitAttacker.cs Assets/_Project/Scripts/AI/EnemyAI.cs Assets/_Project/Tests/PlayMode/LineOfSightTests.cs Assets/_Project/Tests/PlayMode/LineOfSightTests.cs.meta Assets/_Project/Tests/PlayMode/EnemyAIPlayModeTests.cs
git commit -m "Move the sight test into LineOfSight and UnitAttacker

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

(Unity writes the `.meta` files during the test run; if one is missing, run any test once more and add it.)

---

### Task 2: `CombatRole` on `UnitAttacker`; ranged hits need sight

**Files:**
- Modify: `Assets/_Project/Scripts/Units/UnitAttacker.cs`
- Modify: `Assets/_Project/Tests/PlayMode/TestSupport/TestWorld.cs` (`CreateFighter`, `CreateHostile` gain `role` and `range`)
- Create: `Assets/_Project/Tests/EditMode/UnitAttackerTests.cs`
- Create: `Assets/_Project/Tests/PlayMode/RangedCombatPlayModeTests.cs`

**Interfaces:**
- Produces: `public enum CombatRole { Melee, Ranged }`; `UnitAttacker.Role`, `UnitAttacker.NeedsLineOfSight`, `UnitAttacker.IsInRangeFrom(Vector3 pivot, Health target)`, `UnitAttacker.CanAttack(Health target)`, `UnitAttacker.CanAttackFrom(Vector3 pivot, Health target)`; `internal void Initialize(float attackRange, int attackDamage, float attackCooldown, CombatRole combatRole = CombatRole.Melee)`.
- Produces (tests): `TestWorld.CreateFighter(Vector3 groundPosition, int maxHealth = 100, int damage = 25, float cooldown = 1f, CombatRole role = CombatRole.Melee, float range = 2f)`, `TestWorld.CreateHostile(Vector3 groundPosition, Encounter encounter, int maxHealth = 60, int damage = 10, float cooldown = 1.2f, float detectionRange = 12f, CombatRole role = CombatRole.Melee, float range = 2f)`.

- [ ] **Step 1: Write the failing tests**

Create `Assets/_Project/Tests/EditMode/UnitAttackerTests.cs`:

```csharp
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class UnitAttackerTests
    {
        GameObject host;
        GameObject targetHost;
        UnitAttacker attacker;
        Health target;

        [SetUp]
        public void SetUp()
        {
            host = new GameObject("Attacker");
            attacker = host.AddComponent<UnitAttacker>();
            targetHost = new GameObject("Target");
            target = targetHost.AddComponent<Health>();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(host);
            Object.DestroyImmediate(targetHost);
        }

        [Test]
        public void Defaults_AreMelee_WithoutASightRequirement()
        {
            Assert.That(attacker.Role, Is.EqualTo(CombatRole.Melee));
            Assert.That(attacker.NeedsLineOfSight, Is.False);
            Assert.That(attacker.Range, Is.EqualTo(2f));
        }

        [Test]
        public void Initialize_WithARangedRole_NeedsSight()
        {
            attacker.Initialize(8f, 15, 1f, CombatRole.Ranged);
            Assert.That(attacker.Role, Is.EqualTo(CombatRole.Ranged));
            Assert.That(attacker.NeedsLineOfSight, Is.True);
            Assert.That(attacker.Range, Is.EqualTo(8f));
            Assert.That(attacker.Damage, Is.EqualTo(15));
        }

        [Test]
        public void IsInRangeFrom_IsHorizontal_AndInclusive()
        {
            attacker.Initialize(8f, 15, 1f, CombatRole.Ranged);
            targetHost.transform.position = new Vector3(0f, 1f, 0f);
            Assert.That(attacker.IsInRangeFrom(new Vector3(8f, 1f, 0f), target), Is.True, "exactly at the range");
            Assert.That(attacker.IsInRangeFrom(new Vector3(8f, 30f, 0f), target), Is.True, "height is ignored");
            Assert.That(attacker.IsInRangeFrom(new Vector3(8.1f, 1f, 0f), target), Is.False);
            Assert.That(attacker.IsInRangeFrom(Vector3.zero, null), Is.False);
        }

        [Test]
        public void CanAttackFrom_Melee_IsRangeOnly()
        {
            // No colliders exist here, so a ranged check would also pass; melee must not even ask.
            targetHost.transform.position = new Vector3(0f, 1f, 0f);
            Assert.That(attacker.CanAttackFrom(new Vector3(1.5f, 1f, 0f), target), Is.True);
            Assert.That(attacker.CanAttackFrom(new Vector3(2.5f, 1f, 0f), target), Is.False);
            Assert.That(attacker.CanAttack(null), Is.False);
        }
    }
}
```

Create `Assets/_Project/Tests/PlayMode/RangedCombatPlayModeTests.cs`:

```csharp
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class RangedCombatPlayModeTests
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

        static Health HealthOf(Component unit) => unit.GetComponent<Health>();

        [UnityTest]
        public IEnumerator RangedTryAttack_BehindAWall_DoesNotHit_AndHitsOnceTheWallIsCleared()
        {
            world.CreateEnvironment((new Vector3(0f, 1f, 0f), new Vector3(3f, 2f, 1f)));
            var ranged = world.CreateFighter(new Vector3(0f, 0f, -3f), role: CombatRole.Ranged, range: 8f);
            var dummy = world.CreateDummy(new Vector3(0f, 0f, 3f));
            yield return new WaitForFixedUpdate();
            var attacker = ranged.GetComponent<UnitAttacker>();

            Assert.That(attacker.IsInRange(dummy), Is.True, "Precondition: 6 m is inside the 8 m range");
            Assert.That(attacker.CanAttack(dummy), Is.False, "The wall blocks sight");
            Assert.That(attacker.TryAttack(dummy), Is.False, "A ranged attack must not land through a wall");
            Assert.That(dummy.Current, Is.EqualTo(dummy.Max));

            ranged.transform.position = new Vector3(4f, 1f, -3f);
            yield return new WaitForFixedUpdate();
            Assert.That(attacker.CanAttack(dummy), Is.True, "From the side the wall is cleared");
            Assert.That(attacker.TryAttack(dummy), Is.True);
            Assert.That(dummy.Current, Is.EqualTo(dummy.Max - attacker.Damage));
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `Tools/run-tests.sh EditMode Blackglass.Tests.UnitAttackerTests` → expected `EXIT=1` with `error CS0246: The type or namespace name 'CombatRole' could not be found`.

- [ ] **Step 3: Add the role to `UnitAttacker`**

In `Assets/_Project/Scripts/Units/UnitAttacker.cs`:

(a) Before the class, inside the namespace, add:

```csharp
    /// <summary>How a unit fights. Plain data on UnitAttacker; there is no class system.</summary>
    public enum CombatRole
    {
        /// <summary>Hits anything within range; walls do not matter at 2 m.</summary>
        Melee,
        /// <summary>Hits from its range, but only with a clear line of sight to the target.</summary>
        Ranged,
    }
```

(b) Replace the class summary's first sentence `Prototype attack: fixed range, damage and cooldown.` with `Prototype attack: a combat role (melee or ranged), fixed range, damage and cooldown. A ranged hit also needs line of sight.`

(c) Add the field before `range`:

```csharp
        [SerializeField] CombatRole role = CombatRole.Melee;
```

(d) After `public float Cooldown => cooldown;` add:

```csharp
        public CombatRole Role => role;

        /// <summary>Ranged units need a clear line to hit; melee units do not.</summary>
        public bool NeedsLineOfSight => role == CombatRole.Ranged;
```

(e) Replace `Initialize` with:

```csharp
        internal void Initialize(float attackRange, int attackDamage, float attackCooldown, CombatRole combatRole = CombatRole.Melee)
        {
            range = attackRange;
            damage = attackDamage;
            cooldown = attackCooldown;
            role = combatRole;
        }
```

(f) Replace `IsInRange` with:

```csharp
        /// <summary>Horizontal centre-to-centre distance check from this unit's position.</summary>
        public bool IsInRange(Health target) => IsInRangeFrom(transform.position, target);

        /// <summary>The same check from another pivot, for choosing a firing position.</summary>
        public bool IsInRangeFrom(Vector3 pivot, Health target)
        {
            if (target == null)
                return false;
            var offset = target.transform.position - pivot;
            offset.y = 0f;
            return offset.sqrMagnitude <= range * range;
        }
```

(g) After `HasLineOfSightFrom` add:

```csharp
        /// <summary>The one "could I hit it from here" test: in range, and for a ranged unit in sight.</summary>
        public bool CanAttack(Health target) => CanAttackFrom(transform.position, target);

        public bool CanAttackFrom(Vector3 pivot, Health target) =>
            IsInRangeFrom(pivot, target) && (!NeedsLineOfSight || HasLineOfSightFrom(pivot, target));
```

(h) In `TryAttack`, replace `!IsInRange(target)` with `!CanAttack(target)` and update its summary to `Hits the target if it is alive, attackable from here (range, and sight for ranged units) and the cooldown has elapsed. Returns whether it hit.`

- [ ] **Step 4: Extend `TestWorld`**

In `Assets/_Project/Tests/PlayMode/TestSupport/TestWorld.cs`:

Replace the `CreateFighter` signature and its `UnitAttacker` line:

```csharp
        public CommandableUnit CreateFighter(Vector3 groundPosition, int maxHealth = 100, int damage = 25, float cooldown = 1f,
            CombatRole role = CombatRole.Melee, float range = 2f)
```

and

```csharp
            host.AddComponent<UnitAttacker>().Initialize(range, damage, cooldown, role);
```

Replace the `CreateHostile` signature and its first line:

```csharp
        public EnemyAI CreateHostile(Vector3 groundPosition, Encounter encounter, int maxHealth = 60, int damage = 10,
            float cooldown = 1.2f, float detectionRange = 12f, CombatRole role = CombatRole.Melee, float range = 2f)
        {
            var unit = CreateFighter(groundPosition, maxHealth, damage, cooldown, role, range);
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `Tools/run-tests.sh EditMode` → expected `total="212" passed="212"`, `EXIT=0`.
Run: `Tools/run-tests.sh PlayMode` → expected `total="181" passed="181"`, `EXIT=0`.

- [ ] **Step 6: Commit**

```bash
git add Assets/_Project/Scripts/Units/UnitAttacker.cs Assets/_Project/Tests/PlayMode/TestSupport/TestWorld.cs Assets/_Project/Tests/EditMode/UnitAttackerTests.cs Assets/_Project/Tests/EditMode/UnitAttackerTests.cs.meta Assets/_Project/Tests/PlayMode/RangedCombatPlayModeTests.cs Assets/_Project/Tests/PlayMode/RangedCombatPlayModeTests.cs.meta
git commit -m "Give UnitAttacker a combat role; ranged hits need line of sight

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 3: `UnitMover.CanReach`, `TrySnap`, `PivotHeight`

**Files:**
- Modify: `Assets/_Project/Scripts/Units/UnitMover.cs`
- Create: `Assets/_Project/Tests/PlayMode/UnitMoverReachPlayModeTests.cs`

**Interfaces:**
- Produces: `public bool CanReach(Vector3 point)` (complete NavMesh path from the agent to a walkable point within 2 m of `point`); `public bool TrySnap(Vector3 point, out Vector3 onNavMesh)` (nearest walkable point within the existing 2 m `SnapRadius`); `public float PivotHeight` (the agent's base offset).

- [ ] **Step 1: Write the failing tests**

Create `Assets/_Project/Tests/PlayMode/UnitMoverReachPlayModeTests.cs`:

```csharp
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class UnitMoverReachPlayModeTests
    {
        TestWorld world;

        [SetUp]
        public void SetUp() => world = new TestWorld();

        [TearDown]
        public void TearDown() => world.Dispose();

        [UnityTest]
        public IEnumerator CanReach_OpenGround_True_FarOffTheMesh_False()
        {
            world.CreateEnvironment();
            var mover = world.CreateUnit(Vector3.zero).GetComponent<UnitMover>();
            yield return null;

            Assert.That(mover.CanReach(new Vector3(10f, 0f, 10f)), Is.True);
            Assert.That(mover.CanReach(new Vector3(10f, 1f, 10f)), Is.True, "A unit pivot 1 m up still snaps to the mesh");
            Assert.That(mover.CanReach(new Vector3(100f, 0f, 100f)), Is.False, "Nothing walkable within 2 m");
        }

        [UnityTest]
        public IEnumerator CanReach_APointRingedByLowWalls_IsFalse()
        {
            // Agents cannot climb 1 m walls, so the inside of the ring is a separate NavMesh island.
            world.CreateEnvironment(
                (new Vector3(0f, 0.5f, -8f), new Vector3(6f, 1f, 0.5f)),
                (new Vector3(0f, 0.5f, -2f), new Vector3(6f, 1f, 0.5f)),
                (new Vector3(-3f, 0.5f, -5f), new Vector3(0.5f, 1f, 6f)),
                (new Vector3(3f, 0.5f, -5f), new Vector3(0.5f, 1f, 6f)));
            var mover = world.CreateUnit(new Vector3(0f, 0f, 4f)).GetComponent<UnitMover>();
            yield return null;

            Assert.That(mover.CanReach(new Vector3(0f, 0f, -5f)), Is.False, "The ringed point has no complete path");
            Assert.That(mover.CanReach(new Vector3(0f, 0f, -1f)), Is.True, "Just outside the ring is fine");
        }

        [UnityTest]
        public IEnumerator TrySnap_MovesAPointInsideAWallToItsEdge_AcceptsPivotHeight_AndFailsFarAway()
        {
            world.CreateEnvironment((new Vector3(0f, 1f, 0f), new Vector3(1f, 2f, 1f)));
            var mover = world.CreateUnit(new Vector3(0f, 0f, 4f)).GetComponent<UnitMover>();
            yield return null;

            Assert.That(mover.TrySnap(new Vector3(0f, 0f, 0f), out var edge), Is.True);
            Assert.That(TestWorld.HorizontalDistance(edge, Vector3.zero), Is.GreaterThan(0.9f).And.LessThan(2f),
                "The snapped point sits on the eroded NavMesh around the 1 m wall");
            Assert.That(mover.TrySnap(new Vector3(5f, 1f, 5f), out var fromPivot), Is.True, "A pivot-height point snaps down to the mesh");
            Assert.That(fromPivot.y, Is.LessThan(0.3f));
            Assert.That(TestWorld.HorizontalDistance(fromPivot, new Vector3(5f, 0f, 5f)), Is.LessThan(0.01f));
            Assert.That(mover.TrySnap(new Vector3(100f, 0f, 0f), out _), Is.False);
        }

        [UnityTest]
        public IEnumerator PivotHeight_IsTheAgentBaseOffset()
        {
            world.CreateEnvironment();
            var mover = world.CreateUnit(Vector3.zero).GetComponent<UnitMover>();
            yield return null;
            Assert.That(mover.PivotHeight, Is.EqualTo(1f));
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `Tools/run-tests.sh PlayMode Blackglass.Tests.UnitMoverReachPlayModeTests` → expected `EXIT=1` with `error CS1061 ... 'UnitMover' does not contain a definition for 'CanReach'`.

- [ ] **Step 3: Implement**

In `Assets/_Project/Scripts/Units/UnitMover.cs`:

(a) After `int moveRequestFrame = -1;` add:

```csharp
        // Reused by CanReach so reachability checks allocate nothing. A plain C# object, so ??= is fine.
        NavMeshPath reachPath;
```

(b) After the `Agent` property add:

```csharp
        /// <summary>Height of the unit's pivot above the NavMesh (the agent's base offset, 1 m for the prototype capsules).</summary>
        public float PivotHeight => Agent.baseOffset;
```

(c) After `CanMoveTo` add:

```csharp
        /// <summary>
        /// Nearest walkable point within SnapRadius (2 m) of `point`, if any: the tolerance MoveTo and CanMoveTo use,
        /// so a pivot-height point (1 m up) or one in the erosion band beside a wall still snaps. No side effects.
        /// </summary>
        public bool TrySnap(Vector3 point, out Vector3 onNavMesh)
        {
            if (NavMesh.SamplePosition(point, out var hit, SnapRadius, NavMesh.AllAreas))
            {
                onNavMesh = hit.position;
                return true;
            }
            onNavMesh = point;
            return false;
        }

        /// <summary>
        /// True when a complete path exists from the agent to a walkable point within 2 m of `point`. A partial path
        /// (the point is on another NavMesh island, or ringed by walls) is not reachable. No side effects.
        /// </summary>
        public bool CanReach(Vector3 point)
        {
            if (!Agent.isOnNavMesh || !TrySnap(point, out var destination))
                return false;
            reachPath ??= new NavMeshPath();
            // The agent's own CalculatePath starts from its NavMesh location, so no snapping of the source is needed.
            return Agent.CalculatePath(destination, reachPath) && reachPath.status == NavMeshPathStatus.PathComplete;
        }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `Tools/run-tests.sh PlayMode Blackglass.Tests.UnitMoverReachPlayModeTests` → expected `total="4" passed="4"`, `EXIT=0`.
Run: `Tools/run-tests.sh PlayMode` → expected `total="185" passed="185"`, `EXIT=0`.

Troubleshooting: if `TrySnap_...` fails on the edge distance, the NavMesh erosion (agent radius 0.5) around a 1 m wall puts the edge at about 1.0 m; widen the accepted band rather than the radius.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/Units/UnitMover.cs Assets/_Project/Tests/PlayMode/UnitMoverReachPlayModeTests.cs Assets/_Project/Tests/PlayMode/UnitMoverReachPlayModeTests.cs.meta
git commit -m "Let UnitMover answer reachability and snap points to the NavMesh

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 4: `FiringPositionFinder`

**Files:**
- Create: `Assets/_Project/Scripts/Combat/FiringPositionFinder.cs`
- Create: `Assets/_Project/Tests/EditMode/FiringPositionFinderTests.cs`

**Interfaces:**
- Produces: `public static class FiringPositionFinder { public const int DirectionCount = 8; public static readonly float[] Radii = { 2f, 4f }; public const int CandidateCount = 16; public delegate bool Validator(Vector3 candidate, out Vector3 accepted); public static int Candidates(Vector3 origin, Vector3[] buffer); public static bool TryChoose(Vector3[] candidates, int count, Validator isValid, out Vector3 chosen); }`. `TryChoose` returns the validator's *accepted* point (the candidate snapped onto the NavMesh), not the raw candidate.

- [ ] **Step 1: Write the failing tests**

Create `Assets/_Project/Tests/EditMode/FiringPositionFinderTests.cs`:

```csharp
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class FiringPositionFinderTests
    {
        static float Flat(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }

        [Test]
        public void Candidates_SixteenPoints_NearRingFirst_ThenFarRing()
        {
            var origin = new Vector3(3f, 1f, -2f);
            var buffer = new Vector3[FiringPositionFinder.CandidateCount];
            var count = FiringPositionFinder.Candidates(origin, buffer);

            Assert.That(count, Is.EqualTo(16));
            for (var i = 0; i < 8; i++)
                Assert.That(Flat(buffer[i], origin), Is.EqualTo(2f).Within(0.001f), $"candidate {i} is on the 2 m ring");
            for (var i = 8; i < 16; i++)
                Assert.That(Flat(buffer[i], origin), Is.EqualTo(4f).Within(0.001f), $"candidate {i} is on the 4 m ring");
            foreach (var candidate in buffer)
                Assert.That(candidate.y, Is.EqualTo(origin.y), "candidates keep the origin's height");
        }

        [Test]
        public void Candidates_StartNorth_AndGoClockwise()
        {
            var buffer = new Vector3[FiringPositionFinder.CandidateCount];
            FiringPositionFinder.Candidates(Vector3.zero, buffer);

            Assert.That(buffer[0], Is.EqualTo(new Vector3(0f, 0f, 2f)).Using<Vector3>((a, b) => Vector3.Distance(a, b) < 0.001f ? 0 : 1), "first is north");
            Assert.That(buffer[2], Is.EqualTo(new Vector3(2f, 0f, 0f)).Using<Vector3>((a, b) => Vector3.Distance(a, b) < 0.001f ? 0 : 1), "third is east");
            Assert.That(buffer[4], Is.EqualTo(new Vector3(0f, 0f, -2f)).Using<Vector3>((a, b) => Vector3.Distance(a, b) < 0.001f ? 0 : 1), "fifth is south");
            Assert.That(buffer[12], Is.EqualTo(new Vector3(0f, 0f, -4f)).Using<Vector3>((a, b) => Vector3.Distance(a, b) < 0.001f ? 0 : 1), "far ring repeats the order");
        }

        [Test]
        public void Candidates_RejectsATooSmallBuffer()
        {
            Assert.That(() => FiringPositionFinder.Candidates(Vector3.zero, new Vector3[5]), Throws.ArgumentException);
        }

        [Test]
        public void TryChoose_ReturnsTheFirstValidCandidate_InRingOrder_AsTheValidatorsAcceptedPoint()
        {
            var buffer = new Vector3[FiringPositionFinder.CandidateCount];
            var count = FiringPositionFinder.Candidates(Vector3.zero, buffer);

            // Only east-ish points are valid; the 2 m east point (index 2) must beat the 4 m one (index 10). The
            // validator "snaps" by lowering y, and that snapped point is what comes back.
            bool EastOnly(Vector3 c, out Vector3 accepted)
            {
                accepted = c + Vector3.down * 0.5f;
                return c.x > 1.9f;
            }
            var found = FiringPositionFinder.TryChoose(buffer, count, EastOnly, out var chosen);

            Assert.That(found, Is.True);
            Assert.That(chosen, Is.EqualTo(buffer[2] + Vector3.down * 0.5f));
        }

        [Test]
        public void TryChoose_NoValidCandidate_IsFalse()
        {
            var buffer = new Vector3[FiringPositionFinder.CandidateCount];
            var count = FiringPositionFinder.Candidates(Vector3.zero, buffer);
            var calls = 0;
            bool Never(Vector3 c, out Vector3 accepted)
            {
                calls++;
                accepted = c;
                return false;
            }

            var found = FiringPositionFinder.TryChoose(buffer, count, Never, out _);

            Assert.That(found, Is.False);
            Assert.That(calls, Is.EqualTo(16), "every candidate is tried once");
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `Tools/run-tests.sh EditMode Blackglass.Tests.FiringPositionFinderTests` → expected `EXIT=1` with `error CS0103: The name 'FiringPositionFinder' does not exist`.

- [ ] **Step 3: Implement**

Create `Assets/_Project/Scripts/Combat/FiringPositionFinder.cs`:

```csharp
using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// A deliberately small search for a spot to shoot from: 8 compass points at 2 m and 4 m around the unit, tried in
    /// that order, so the first valid one is the nearest. The caller decides what "valid" means (on the NavMesh, in
    /// range, in sight, reachable). No cover value, no scoring, no search around the target.
    /// </summary>
    public static class FiringPositionFinder
    {
        public const int DirectionCount = 8;
        public static readonly float[] Radii = { 2f, 4f };
        public const int CandidateCount = 16;

        /// <summary>Fills the buffer with the candidates, nearest ring first, north first then clockwise. Returns the count.</summary>
        public static int Candidates(Vector3 origin, Vector3[] buffer)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (buffer.Length < CandidateCount)
                throw new ArgumentException($"The buffer needs {CandidateCount} entries.", nameof(buffer));
            var index = 0;
            foreach (var radius in Radii)
            {
                for (var i = 0; i < DirectionCount; i++)
                {
                    var angle = i * (2f * Mathf.PI / DirectionCount);
                    buffer[index++] = origin + new Vector3(Mathf.Sin(angle) * radius, 0f, Mathf.Cos(angle) * radius);
                }
            }
            return index;
        }

        /// <summary>
        /// A caller's validity test: accepts or rejects a raw candidate and, when accepting, returns the point to walk
        /// to (the candidate snapped onto the NavMesh).
        /// </summary>
        public delegate bool Validator(Vector3 candidate, out Vector3 accepted);

        /// <summary>The first candidate the validator accepts (the nearest, given the ring order), as its accepted point; or false.</summary>
        public static bool TryChoose(Vector3[] candidates, int count, Validator isValid, out Vector3 chosen)
        {
            if (candidates == null)
                throw new ArgumentNullException(nameof(candidates));
            if (isValid == null)
                throw new ArgumentNullException(nameof(isValid));
            for (var i = 0; i < count; i++)
            {
                if (isValid(candidates[i], out chosen))
                    return true;
            }
            chosen = default;
            return false;
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `Tools/run-tests.sh EditMode` → expected `total="217" passed="217"`, `EXIT=0`.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/Combat/FiringPositionFinder.cs Assets/_Project/Scripts/Combat/FiringPositionFinder.cs.meta Assets/_Project/Tests/EditMode/FiringPositionFinderTests.cs Assets/_Project/Tests/EditMode/FiringPositionFinderTests.cs.meta
git commit -m "Add the 16-candidate firing position search

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 5: Attack phases and repositioning in `CommandableUnit`; `EnemyState.Reposition`

**Files:**
- Modify: `Assets/_Project/Scripts/Units/CommandableUnit.cs`
- Modify: `Assets/_Project/Scripts/AI/EnemyAI.cs` (`EnemyState.Reposition`, `DeriveState(bool, UnitCommand, AttackPhase)`)
- Modify: `Assets/_Project/Tests/EditMode/EnemyAITests.cs`
- Modify: `Assets/_Project/Tests/EditMode/CommandableUnitTests.cs` (one test)
- Modify: `Assets/_Project/Tests/PlayMode/RangedCombatPlayModeTests.cs` (four tests)

**Interfaces:**
- Consumes: `UnitAttacker.IsInRange/NeedsLineOfSight/HasLineOfSight/CanAttackFrom/TryAttack` (Task 2), `UnitMover.TrySnap/CanReach/PivotHeight/HasArrived/MoveTo/Stop` (Task 3), `FiringPositionFinder` (Task 4).
- Produces: `public enum AttackPhase { None, Approach, Reposition, Attack }`; `CommandableUnit.AttackPhase` (`None` without an attack order; `Approach` for an attack order that has not ticked yet); `EnemyAI.DeriveState(bool alive, UnitCommand current, AttackPhase phase)`; `EnemyState.Reposition`.

- [ ] **Step 1: Write the failing tests**

In `Assets/_Project/Tests/EditMode/EnemyAITests.cs` replace the three `DeriveState_*` tests with:

```csharp
        [Test]
        public void DeriveState_Dead_WhateverTheOrder()
        {
            var host = new GameObject("Target");
            var attack = new AttackCommand(host.AddComponent<Health>());
            Assert.That(EnemyAI.DeriveState(false, null, AttackPhase.None), Is.EqualTo(EnemyState.Dead));
            Assert.That(EnemyAI.DeriveState(false, attack, AttackPhase.Attack), Is.EqualTo(EnemyState.Dead));
            Object.DestroyImmediate(host);
        }

        [Test]
        public void DeriveState_Idle_WithoutAnAttackOrder()
        {
            Assert.That(EnemyAI.DeriveState(true, null, AttackPhase.None), Is.EqualTo(EnemyState.Idle));
            Assert.That(EnemyAI.DeriveState(true, new MoveCommand(Vector3.zero), AttackPhase.None), Is.EqualTo(EnemyState.Idle));
        }

        [Test]
        public void DeriveState_FollowsTheAttackPhase()
        {
            var host = new GameObject("Target");
            var attack = new AttackCommand(host.AddComponent<Health>());
            Assert.That(EnemyAI.DeriveState(true, attack, AttackPhase.Approach), Is.EqualTo(EnemyState.Chase));
            Assert.That(EnemyAI.DeriveState(true, attack, AttackPhase.Reposition), Is.EqualTo(EnemyState.Reposition));
            Assert.That(EnemyAI.DeriveState(true, attack, AttackPhase.Attack), Is.EqualTo(EnemyState.Attack));
            Object.DestroyImmediate(host);
        }

        [Test]
        public void DeriveState_AnAttackOrderThatHasNotTickedYet_IsChase()
        {
            var host = new GameObject("Target");
            var attack = new AttackCommand(host.AddComponent<Health>());
            Assert.That(EnemyAI.DeriveState(true, attack, AttackPhase.None), Is.EqualTo(EnemyState.Chase));
            Object.DestroyImmediate(host);
        }
```

In `Assets/_Project/Tests/EditMode/CommandableUnitTests.cs` add one test inside the fixture (reuse its existing helpers for creating a unit and a target; if the fixture has none, build them as the other tests there do):

```csharp
        [Test]
        public void AttackPhase_IsNoneWithoutAnAttackOrder_AndApproachOnceOneIsIssued()
        {
            var host = new GameObject("Unit");
            host.AddComponent<UnitMover>();
            host.AddComponent<UnitAttacker>();
            var unit = host.AddComponent<CommandableUnit>();
            var targetHost = new GameObject("Target");
            var target = targetHost.AddComponent<Health>();

            Assert.That(unit.AttackPhase, Is.EqualTo(AttackPhase.None));
            unit.Issue(new MoveCommand(Vector3.zero));
            Assert.That(unit.AttackPhase, Is.EqualTo(AttackPhase.None), "A move is not an attack");
            Assert.That(unit.Issue(new AttackCommand(target)), Is.True);
            Assert.That(unit.AttackPhase, Is.EqualTo(AttackPhase.Approach), "An attack order that has not ticked is approaching");

            Object.DestroyImmediate(host);
            Object.DestroyImmediate(targetHost);
        }
```

(`Issue(new MoveCommand(...))` returns false here because there is no NavMesh in EditMode; that is fine, the assertion is about the phase.)

In `Assets/_Project/Tests/PlayMode/RangedCombatPlayModeTests.cs` add four tests inside the fixture:

```csharp
        // Records the phases an attack order passes through, so a test can assert the path, not just the result.
        static IEnumerator RecordPhases(CommandableUnit unit, System.Collections.Generic.HashSet<AttackPhase> seen, System.Func<bool> until, float timeout)
        {
            var deadline = Time.realtimeSinceStartup + timeout;
            while (!until() && Time.realtimeSinceStartup < deadline)
            {
                seen.Add(unit.AttackPhase);
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator RangedFighter_AttacksFromItsRange_WithoutClosingIn()
        {
            world.CreateEnvironment();
            var ranged = world.CreateFighter(new Vector3(0f, 0f, -12f), role: CombatRole.Ranged, range: 8f);
            var dummy = world.CreateDummy(Vector3.zero);
            var distanceAtFirstHit = -1f;
            ranged.GetComponent<UnitAttacker>().Attacked += _ =>
            {
                if (distanceAtFirstHit < 0f)
                    distanceAtFirstHit = TestWorld.HorizontalDistance(ranged.transform.position, dummy.transform.position);
            };
            var seen = new System.Collections.Generic.HashSet<AttackPhase>();

            Assert.That(ranged.Issue(new AttackCommand(dummy)), Is.True);
            yield return RecordPhases(ranged, seen, () => distanceAtFirstHit >= 0f, 8f);

            Assert.That(distanceAtFirstHit, Is.GreaterThanOrEqualTo(6f).And.LessThanOrEqualTo(8.5f), "A ranged unit fires from its range, not from melee");
            Assert.That(seen, Does.Contain(AttackPhase.Approach).And.Contain(AttackPhase.Attack));
            Assert.That(seen, Does.Not.Contain(AttackPhase.Reposition), "Nothing blocked the line");
        }

        [UnityTest]
        public IEnumerator RangedFighter_BlindBehindAPillar_RepositionsToTheSide_ThenHits()
        {
            // A 1 m pillar between them. The 2 m ring's north point still looks through the pillar; its north-east
            // point (1.41, -2.09) clears it by 0.3 m and is in range, so that is where the unit should go.
            world.CreateEnvironment((new Vector3(0f, 1f, 0f), new Vector3(1f, 2f, 1f)));
            var ranged = world.CreateFighter(new Vector3(0f, 0f, -3.5f), role: CombatRole.Ranged, range: 8f);
            var dummy = world.CreateDummy(new Vector3(0f, 0f, 4f));
            var seen = new System.Collections.Generic.HashSet<AttackPhase>();
            var damageWhileBlind = false;
            yield return new WaitForFixedUpdate();

            Assert.That(ranged.Issue(new AttackCommand(dummy)), Is.True);
            yield return RecordPhases(ranged, seen, () =>
            {
                if (dummy.Current < dummy.Max && !ranged.GetComponent<UnitAttacker>().HasLineOfSight(dummy))
                    damageWhileBlind = true;
                return dummy.Current < dummy.Max;
            }, 8f);

            Assert.That(dummy.Current, Is.LessThan(dummy.Max), "The unit never regained sight and fired");
            Assert.That(damageWhileBlind, Is.False, "No damage may land without sight");
            Assert.That(seen, Does.Contain(AttackPhase.Reposition), "It had to reposition first");
            Assert.That(Mathf.Abs(ranged.transform.position.x), Is.GreaterThan(1f), "It stepped to the side of the pillar");
            Assert.That(TestWorld.HorizontalDistance(ranged.transform.position, dummy.transform.position), Is.GreaterThan(4f),
                "It did not walk into the target");
            Assert.That(ranged.AttackPhase, Is.EqualTo(AttackPhase.Attack));
        }

        [UnityTest]
        public IEnumerator RangedFighter_TargetWalledOnThreeSides_FallsBackToApproach_AndHits()
        {
            // A U of 2 m walls open to the north hides the target from every candidate south of it, so the search
            // fails and the unit must walk at the target until the line clears around the side. Health 100 at 25 per
            // hit: the order must end with the target dead, which proves the walk terminates.
            world.CreateEnvironment(
                (new Vector3(0f, 1f, -2f), new Vector3(6f, 2f, 0.5f)),
                (new Vector3(-3f, 1f, 0f), new Vector3(0.5f, 2f, 4.5f)),
                (new Vector3(3f, 1f, 0f), new Vector3(0.5f, 2f, 4.5f)));
            var ranged = world.CreateFighter(new Vector3(0f, 0f, -7f), role: CombatRole.Ranged, range: 8f);
            var dummy = world.CreateDummy(Vector3.zero);
            var seen = new System.Collections.Generic.HashSet<AttackPhase>();
            yield return new WaitForFixedUpdate();

            Assert.That(ranged.Issue(new AttackCommand(dummy)), Is.True);
            yield return RecordPhases(ranged, seen, () => !dummy.IsAlive, 25f);

            Assert.That(dummy.IsAlive, Is.False, "The ranged unit must eventually get a line and finish the target");
            Assert.That(ranged.CurrentCommand, Is.Null, "The attack order ends with the target");
            Assert.That(seen, Does.Contain(AttackPhase.Reposition));
            Assert.That(ranged.transform.position.z, Is.GreaterThan(-5f), "It walked around the wall, it did not stand still");
            // Any logged error during the walk fails the test on its own.
        }

        [UnityTest]
        public IEnumerator MeleeFighter_PursuesATargetThatWalksAway_AndHits()
        {
            world.CreateEnvironment();
            var melee = world.CreateFighter(new Vector3(0f, 0f, -3f));
            var runner = world.CreateFighter(Vector3.zero);
            Assert.That(runner.Issue(new MoveCommand(new Vector3(0f, 0f, 6f))), Is.True);   // an order, so it does not retaliate
            var seen = new System.Collections.Generic.HashSet<AttackPhase>();

            Assert.That(melee.Issue(new AttackCommand(HealthOf(runner))), Is.True);
            yield return RecordPhases(melee, seen, () => HealthOf(runner).Current < HealthOf(runner).Max, 10f);

            Assert.That(HealthOf(runner).Current, Is.LessThan(HealthOf(runner).Max), "The pursuer never caught up");
            Assert.That(melee.transform.position.z, Is.GreaterThan(2f), "The pursuer followed the runner north");
            Assert.That(seen, Does.Contain(AttackPhase.Approach).And.Contain(AttackPhase.Attack));
            Assert.That(seen, Does.Not.Contain(AttackPhase.Reposition), "Melee never repositions");
        }

        [UnityTest]
        public IEnumerator RangedFighter_TargetEnclosedOnFourSides_EndsTheOrder_AndRunsTheNextOne()
        {
            // 2 m walls on every side: in range, blind, no candidate sees in, and the fallback walk ends at the box
            // still blind. The order must end (unreachable), fire nothing, and let the queued move run.
            world.CreateEnvironment(
                (new Vector3(0f, 1f, -2f), new Vector3(4.5f, 2f, 0.5f)),
                (new Vector3(0f, 1f, 2f), new Vector3(4.5f, 2f, 0.5f)),
                (new Vector3(-2f, 1f, 0f), new Vector3(0.5f, 2f, 4.5f)),
                (new Vector3(2f, 1f, 0f), new Vector3(0.5f, 2f, 4.5f)));
            var ranged = world.CreateFighter(new Vector3(0f, 0f, -6f), role: CombatRole.Ranged, range: 8f);
            var dummy = world.CreateDummy(Vector3.zero);
            var away = new Vector3(6f, 0f, -6f);
            yield return new WaitForFixedUpdate();

            Assert.That(ranged.Issue(new AttackCommand(dummy)), Is.True);
            Assert.That(ranged.Issue(new MoveCommand(away), IssueMode.Append), Is.True);
            yield return TestWorld.WaitUntil(() => ranged.CurrentCommand is MoveCommand, 12f);

            Assert.That(ranged.CurrentCommand, Is.TypeOf<MoveCommand>(), "The attack order must end on its own");
            Assert.That(dummy.Current, Is.EqualTo(dummy.Max), "Nothing may land through the walls");
            yield return TestWorld.WaitUntil(() => ranged.CurrentCommand == null, 8f);
            Assert.That(TestWorld.HorizontalDistance(ranged.transform.position, away), Is.LessThan(0.5f), "The queued move then runs");
            Assert.That(ranged.AttackPhase, Is.EqualTo(AttackPhase.None));
            // Any logged error during the run fails the test on its own.
        }

        [UnityTest]
        public IEnumerator RangedFighter_OneMetreFromACrate_StillFindsASideCandidate()
        {
            // A 2 m crate (eroded footprint ±1.5) right in front of the unit, target off to the north-east. The 2 m
            // ring's north-east point (1.41, -1.09) lands inside the erosion band and must snap onto the mesh edge at
            // x = 1.5, from where the line to (3, 4) clears the crate's east face. A 1 m snap would have rejected it.
            world.CreateEnvironment((new Vector3(0f, 1f, 0f), new Vector3(2f, 2f, 2f)));
            var ranged = world.CreateFighter(new Vector3(0f, 0f, -2.5f), role: CombatRole.Ranged, range: 8f);
            var dummy = world.CreateDummy(new Vector3(3f, 0f, 4f));
            var seen = new System.Collections.Generic.HashSet<AttackPhase>();
            yield return new WaitForFixedUpdate();
            Assert.That(ranged.GetComponent<UnitAttacker>().CanAttack(dummy), Is.False, "Precondition: the crate blocks the line");

            Assert.That(ranged.Issue(new AttackCommand(dummy)), Is.True);
            yield return RecordPhases(ranged, seen, () => dummy.Current < dummy.Max, 10f);

            Assert.That(dummy.Current, Is.LessThan(dummy.Max), "The unit must find a spot that sees past the crate");
            Assert.That(seen, Does.Contain(AttackPhase.Reposition));
            Assert.That(ranged.transform.position.x, Is.GreaterThan(1.2f), "It stepped to the crate's east side");
            Assert.That(ranged.transform.position.z, Is.LessThan(0f), "It did not walk around to the target");
        }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `Tools/run-tests.sh EditMode Blackglass.Tests.EnemyAITests` → expected `EXIT=1` with `error CS0246: The type or namespace name 'AttackPhase' could not be found`.

- [ ] **Step 3: Rewrite the attack loop in `CommandableUnit`**

Replace `Assets/_Project/Scripts/Units/CommandableUnit.cs` with:

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>Where an attack order is in its life. Derived by CommandableUnit; debug views and AI state read it.</summary>
    public enum AttackPhase
    {
        /// <summary>No attack order.</summary>
        None,
        /// <summary>Out of range: walking toward the target.</summary>
        Approach,
        /// <summary>In range but no line of sight (ranged only): walking to a firing position, or at the target.</summary>
        Reposition,
        /// <summary>In range (and in sight): standing, facing and hitting on cooldown.</summary>
        Attack,
    }

    /// <summary>
    /// The single entry point for gameplay orders and direct control. Keeps the unit's orders in a CommandQueue (the
    /// current order plus pending ones) and carries out the current order each simulation frame using UnitMover and
    /// UnitAttacker. A held move intent (direct control) takes precedence: while it is non-zero the unit drops its
    /// orders and steers instead. An attack order runs in phases: approach until in range, reposition while a ranged
    /// unit has no line of sight, attack otherwise.
    /// </summary>
    [RequireComponent(typeof(UnitMover), typeof(UnitAttacker))]
    public sealed class CommandableUnit : MonoBehaviour
    {
        const float ChaseRepathDistance = 0.5f;
        // Firing-position searches cost 16 snaps, sight rays and paths, so they are rate limited.
        const float RepositionInterval = 0.5f;
        // A reposition walk that has not arrived after this long (a spot taken by another unit) is searched again.
        const float RepositionWalkTimeout = 3f;
        // After this many repositions without landing a hit the unit walks at the target instead.
        const int MaxRepositionsWithoutShot = 3;

        readonly CommandQueue queue = new CommandQueue();
        readonly Vector3[] firingCandidates = new Vector3[FiringPositionFinder.CandidateCount];
        UnitMover mover;
        UnitAttacker attacker;
        AttackPhase attackPhase;
        // Where the target stood when the current approach or reposition path was requested.
        Vector3 lastTargetPosition;
        float nextRepositionTime;
        float searchTime;
        int repositionsWithoutShot;
        // True while the reposition fallback is walking at the target itself rather than to a firing position.
        bool walkingAtTarget;
        Vector3 moveIntent;
        Health ownHealth;

        /// <summary>The order being carried out, or null when idle.</summary>
        public UnitCommand CurrentCommand => queue.Current;

        /// <summary>Orders waiting behind the current one, in the order they will run.</summary>
        public IReadOnlyList<UnitCommand> PendingCommands => queue.Pending;

        /// <summary>The direction direct control is steering the unit in, or zero. See SetMoveIntent.</summary>
        public Vector3 MoveIntent => moveIntent;

        /// <summary>False once this unit's Health (if it has one) has died. A dead unit takes and runs no orders.</summary>
        public bool IsAlive => OwnHealth == null || OwnHealth.IsAlive;

        /// <summary>
        /// The phase of the current attack order; None without one. An attack order that has not ticked yet reads as
        /// Approach, so the HUD never shows an attacking unit as doing nothing.
        /// </summary>
        public AttackPhase AttackPhase
        {
            get
            {
                if (!(queue.Current is AttackCommand))
                    return AttackPhase.None;
                return attackPhase == AttackPhase.None ? AttackPhase.Approach : attackPhase;
            }
        }

        UnitMover Mover => mover != null ? mover : mover = GetComponent<UnitMover>();
        UnitAttacker Attacker => attacker != null ? attacker : attacker = GetComponent<UnitAttacker>();

        // Looked up lazily so a Health added after this component is still found.
        Health OwnHealth => ownHealth != null ? ownHealth : ownHealth = GetComponent<Health>();

        /// <summary>
        /// Gives the unit an order. Replace drops the current and pending orders and starts this one now; Append runs
        /// it after the pending ones (now, if the unit is idle). A Stop always halts the unit and clears every order.
        /// Returns false if the order cannot be carried out (this unit is dead, no walkable point within 2 m of the
        /// destination, dead or inactive target); the unit's orders are then unchanged.
        /// Re-issuing an attack on the current target keeps the unit moving instead of restarting its chase.
        /// </summary>
        public bool Issue(UnitCommand command, IssueMode mode = IssueMode.Replace)
        {
            if (command == null)
                throw new ArgumentNullException(nameof(command));
            if (mode != IssueMode.Replace && mode != IssueMode.Append)
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown issue mode.");
            if (!IsAlive)
                return false;

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

        void OnEnable()
        {
            if (OwnHealth != null)
                OwnHealth.Died += OnDied;
        }

        void OnDisable()
        {
            if (OwnHealth != null)
                OwnHealth.Died -= OnDied;
        }

        // A corpse keeps no plan: whatever it was doing ends here, before Health deactivates the GameObject.
        void OnDied() => StopAll();

        void Update()
        {
            // Orders and steering only advance while simulation time advances (tactical pause sets timeScale to 0).
            if (!SimulationTime.IsRunning)
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
                    ResetAttack();
                    return true;
                case AttackCommand attack:
                    if (!IsAttackable(attack.Target))
                        return false;
                    Mover.Stop();
                    ResetAttack();
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
            ResetAttack();
            queue.Clear();
        }

        void ResetAttack()
        {
            attackPhase = AttackPhase.None;
            repositionsWithoutShot = 0;
            walkingAtTarget = false;
            nextRepositionTime = 0f;
            searchTime = 0f;
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
                Approach(target);
                return;
            }

            if (Attacker.NeedsLineOfSight && !Attacker.HasLineOfSight(target))
            {
                Reposition(target);
                return;
            }

            if (attackPhase != AttackPhase.Attack)
            {
                Mover.Stop();
                attackPhase = AttackPhase.Attack;
            }
            FaceTowards(target.transform.position);
            if (Attacker.TryAttack(target))
                repositionsWithoutShot = 0;
            if (!target.IsAlive)
                FinishAttack();
        }

        // Out of range: walk toward the target, re-pathing when it has moved. Arriving while still out of range
        // means the path was partial (a complete path ends at the target, inside range): the target cannot be reached.
        void Approach(Health target)
        {
            var targetPosition = target.transform.position;
            if (attackPhase != AttackPhase.Approach || TargetMoved(targetPosition))
            {
                if (!Mover.MoveTo(targetPosition))
                {
                    FinishAttack();
                    return;
                }
                attackPhase = AttackPhase.Approach;
                lastTargetPosition = targetPosition;
            }
            else if (Mover.HasArrived)
            {
                FinishAttack();
            }
        }

        // In range but blind (ranged only): stand, then at most twice a second look for a nearby firing position
        // and walk there. Sight is re-checked every frame in UpdateAttack, so the walk ends the moment the line clears.
        // Arriving at the end of a fallback walk still blind means the path was partial: the target cannot be
        // reached from anywhere in sight, so the order ends as Approach's does for an unreachable target.
        void Reposition(Health target)
        {
            if (attackPhase != AttackPhase.Reposition)
            {
                Mover.Stop();
                attackPhase = AttackPhase.Reposition;
                lastTargetPosition = target.transform.position;
            }
            else if (walkingAtTarget && Mover.HasArrived)
            {
                FinishAttack();
                return;
            }
            else if (!Mover.HasArrived && !TargetMoved(target.transform.position) && Time.time < searchTime + RepositionWalkTimeout)
            {
                return;   // still walking to the chosen spot
            }
            if (Time.time >= nextRepositionTime)
                SearchFiringPosition(target);
        }

        void SearchFiringPosition(Health target)
        {
            searchTime = Time.time;
            nextRepositionTime = Time.time + RepositionInterval;
            lastTargetPosition = target.transform.position;
            if (repositionsWithoutShot < MaxRepositionsWithoutShot)
            {
                // Candidates start on the ground, so the 2 m snap only has to absorb the erosion band beside walls.
                var count = FiringPositionFinder.Candidates(transform.position - Vector3.up * Mover.PivotHeight, firingCandidates);
                // The closure allocates once per search (at most twice a second per blind unit); acceptable.
                if (FiringPositionFinder.TryChoose(firingCandidates, count,
                        (Vector3 candidate, out Vector3 accepted) => IsFiringPosition(candidate, target, out accepted), out var spot)
                    && Mover.MoveTo(spot))
                {
                    repositionsWithoutShot++;
                    walkingAtTarget = false;
                    return;
                }
            }
            // Fallback: walk at the target until the line clears. No walkable point near it means it is unreachable.
            if (Mover.MoveTo(lastTargetPosition))
                walkingAtTarget = true;
            else
                FinishAttack();
        }

        // On the NavMesh, in range and in sight from the eye a unit would have there (snapped point + pivot height,
        // then LineOfSight adds the eye height), and reachable. Returns the snapped point as the place to walk to.
        bool IsFiringPosition(Vector3 candidate, Health target, out Vector3 point) =>
            Mover.TrySnap(candidate, out point)
            && Attacker.CanAttackFrom(point + Vector3.up * Mover.PivotHeight, target)
            && Mover.CanReach(point);

        bool TargetMoved(Vector3 targetPosition) =>
            (targetPosition - lastTargetPosition).sqrMagnitude > ChaseRepathDistance * ChaseRepathDistance;

        // A target that is missing, dead, or deactivated while still alive can no longer be attacked.
        static bool IsAttackable(Health target) => target != null && target.IsAlive && target.gameObject.activeInHierarchy;

        void FinishAttack()
        {
            Mover.Stop();
            ResetAttack();
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

- [ ] **Step 4: Add `Reposition` to `EnemyState` and derive from the phase**

In `Assets/_Project/Scripts/AI/EnemyAI.cs`:

(a) Replace the enum with:

```csharp
    /// <summary>What a hostile is doing, as EnemyAI.DeriveState reads it from its unit.</summary>
    public enum EnemyState
    {
        Idle,
        Chase,
        Reposition,
        Attack,
        Dead,
    }
```

(b) Replace the `State` property with:

```csharp
        /// <summary>Derived each read; nothing is stored. Debug views show it.</summary>
        public EnemyState State => DeriveState(Unit.IsAlive, Unit.CurrentCommand, Unit.AttackPhase);
```

(c) Replace `DeriveState` with:

```csharp
        /// <summary>Dead beats everything; an attack order maps its phase to Chase, Reposition or Attack; otherwise Idle.</summary>
        public static EnemyState DeriveState(bool alive, UnitCommand current, AttackPhase phase)
        {
            if (!alive)
                return EnemyState.Dead;
            if (!(current is AttackCommand))
                return EnemyState.Idle;
            switch (phase)
            {
                case AttackPhase.Reposition:
                    return EnemyState.Reposition;
                case AttackPhase.Attack:
                    return EnemyState.Attack;
                default:
                    return EnemyState.Chase;
            }
        }
```

(d) In the class summary, replace `Runs on simulation time, so it freezes while paused.` with `A ranged hostile stops at its range and repositions when blind, because CommandableUnit does that for every attack order. Runs on simulation time, so it freezes while paused.`

- [ ] **Step 5: Run the tests to verify they pass**

Run: `Tools/run-tests.sh EditMode` → expected `total="219" passed="219"`, `EXIT=0`.
Run: `Tools/run-tests.sh PlayMode` → expected `total="191" passed="191"`, `EXIT=0`.

Troubleshooting:
- `RangedFighter_BlindBehindAPillar_...` ends with `|x| <= 1`: print the chosen spot (`Debug.Log` in `SearchFiringPosition`, remove afterwards). If the north 2 m candidate `(0, -1.5)` passed, the pillar's eroded NavMesh edge is nearer than expected: the pillar is 1 m wide and agent radius 0.5 m, so `(0, -1.5)` should snap to itself and the ray at x = 0 must hit the pillar; check `CreateEnvironment` passed `scale (1, 2, 1)`.
- `..._FallsBackToApproach_AndHits` times out: check `MaxRepositionsWithoutShot` is reached (no candidate is valid, so the fallback fires on the first search) and that `MoveTo(target)` is accepted (the dummy stands on the mesh inside the U).
- Melee tests from Phase 4 fail: `Approach` must behave exactly as the old `chasing` branch; compare with `git show HEAD~1:Assets/_Project/Scripts/Units/CommandableUnit.cs`.

- [ ] **Step 6: Commit**

```bash
git add Assets/_Project/Scripts/Units/CommandableUnit.cs Assets/_Project/Scripts/AI/EnemyAI.cs Assets/_Project/Tests/EditMode/EnemyAITests.cs Assets/_Project/Tests/EditMode/CommandableUnitTests.cs Assets/_Project/Tests/PlayMode/RangedCombatPlayModeTests.cs
git commit -m "Run attack orders in phases and reposition blind ranged units

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 6: Enemy target selection requires reachability; ranged hostiles

**Files:**
- Modify: `Assets/_Project/Scripts/AI/EnemyAI.cs` (`FindNearestVisibleFriendly` → `FindTarget`)
- Modify: `Assets/_Project/Tests/PlayMode/EnemyAIPlayModeTests.cs`

**Interfaces:**
- Consumes: `UnitMover.CanReach` (Task 3), `TestWorld.CreateHostile(..., role, range)` (Task 2).
- Produces: no new public API. Decision 015's rule becomes: alive, active, within detection range, in sight, reachable; nearest wins.

- [ ] **Step 1: Write the failing tests**

In `Assets/_Project/Tests/PlayMode/EnemyAIPlayModeTests.cs`:

(a) Replace `UnreachableVisibleFriendly_IsRechasedWithoutErrors` with:

```csharp
        [UnityTest]
        public IEnumerator UnreachableVisibleFriendly_IsIgnored()
        {
            // Low walls ring the friendly: agents cannot climb 1 m, but a 1.5 m eye sees over them. Phase 4 chased
            // anyway and stood at the ring; now an unreachable friendly is not a target at all.
            world.CreateEnvironment(
                (new Vector3(0f, 0.5f, -8f), new Vector3(6f, 1f, 0.5f)),
                (new Vector3(0f, 0.5f, -2f), new Vector3(6f, 1f, 0.5f)),
                (new Vector3(-3f, 0.5f, -5f), new Vector3(0.5f, 1f, 6f)),
                (new Vector3(3f, 0.5f, -5f), new Vector3(0.5f, 1f, 6f)));
            var friendly = world.CreateFighter(new Vector3(0f, 0f, -5f));
            var hostile = world.CreateHostile(new Vector3(0f, 0f, 4f), encounter);
            Arm(new[] { hostile }, friendly);
            var start = hostile.transform.position;

            yield return new WaitForSeconds(1.5f);

            Assert.That(hostile.State, Is.EqualTo(EnemyState.Idle), "A friendly with no complete path is not a target");
            Assert.That(hostile.Target, Is.Null);
            Assert.That(TestWorld.HorizontalDistance(hostile.transform.position, start), Is.LessThan(0.1f), "The hostile did not move");
            Assert.That(HealthOf(friendly).Current, Is.EqualTo(HealthOf(friendly).Max));
        }

        [UnityTest]
        public IEnumerator UnreachableFriendly_IsSkippedForAFartherReachableOne()
        {
            world.CreateEnvironment(
                (new Vector3(0f, 0.5f, -8f), new Vector3(6f, 1f, 0.5f)),
                (new Vector3(0f, 0.5f, -2f), new Vector3(6f, 1f, 0.5f)),
                (new Vector3(-3f, 0.5f, -5f), new Vector3(0.5f, 1f, 6f)),
                (new Vector3(3f, 0.5f, -5f), new Vector3(0.5f, 1f, 6f)));
            var ringed = world.CreateFighter(new Vector3(0f, 0f, -5f));
            var reachable = world.CreateFighter(new Vector3(6f, 0f, -3f));   // 9.2 m away, clear of the ring
            var hostile = world.CreateHostile(new Vector3(0f, 0f, 4f), encounter);
            Arm(new[] { hostile }, ringed, reachable);

            yield return WaitForState(hostile, EnemyState.Chase, 1.5f);

            Assert.That(hostile.Target, Is.SameAs(HealthOf(reachable)), "The nearer but unreachable friendly must lose to the reachable one");
        }

        [UnityTest]
        public IEnumerator RangedHostile_AcquiresAtDetectionRange_StopsAtItsRange_AndFires()
        {
            world.CreateEnvironment();
            var friendly = world.CreateFighter(new Vector3(0f, 0f, -11f));
            var hostile = world.CreateHostile(Vector3.zero, encounter, damage: 8, cooldown: 1.5f, role: CombatRole.Ranged, range: 8f);
            Arm(new[] { hostile }, friendly);
            var distanceAtFirstHit = -1f;
            hostile.GetComponent<UnitAttacker>().Attacked += _ =>
            {
                if (distanceAtFirstHit < 0f)
                    distanceAtFirstHit = TestWorld.HorizontalDistance(hostile.transform.position, friendly.transform.position);
            };

            yield return WaitForState(hostile, EnemyState.Chase, 1f);
            Assert.That(hostile.Target, Is.SameAs(HealthOf(friendly)), "11 m is inside the 12 m detection range");
            yield return TestWorld.WaitUntil(() => distanceAtFirstHit >= 0f, 6f);

            Assert.That(distanceAtFirstHit, Is.GreaterThanOrEqualTo(6f).And.LessThanOrEqualTo(8.5f), "A ranged hostile fires from its range");
            Assert.That(HealthOf(friendly).Current, Is.EqualTo(HealthOf(friendly).Max - 8));
        }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `Tools/run-tests.sh PlayMode Blackglass.Tests.EnemyAIPlayModeTests` → expected `EXIT=2`: `UnreachableVisibleFriendly_IsIgnored` fails ("A friendly with no complete path is not a target", the hostile is in `Chase`) and `UnreachableFriendly_IsSkippedForAFartherReachableOne` fails (the target is the ringed friendly). The ranged test passes already (Task 5 made ranged orders work).

- [ ] **Step 3: Add reachability to the rule**

In `Assets/_Project/Scripts/AI/EnemyAI.cs`:

(a) Add a lazy mover after the `Attacker` property:

```csharp
        UnitMover Mover => mover != null ? mover : mover = GetComponent<UnitMover>();
```

and the field `UnitMover mover;` next to `UnitAttacker attacker;`.

(b) Rename `FindNearestVisibleFriendly` to `FindTarget` (and its call in `Update`) and replace its body with:

```csharp
        // Nearest friendly that is alive, active, inside the detection radius, in sight and reachable. Distance and
        // sight are tested first, so the path (the dearest check) is computed only for candidates that could win.
        Health FindTarget()
        {
            Health best = null;
            var bestDistance = float.PositiveInfinity;
            foreach (var candidate in encounter.Friendlies)
            {
                if (candidate == null || !candidate.IsAlive || !candidate.gameObject.activeInHierarchy)
                    continue;
                var offset = candidate.transform.position - transform.position;
                offset.y = 0f;
                var distance = offset.magnitude;
                if (distance > detectionRange || distance >= bestDistance)
                    continue;
                if (!Attacker.HasLineOfSight(candidate) || !Mover.CanReach(candidate.transform.position))
                    continue;
                best = candidate;
                bestDistance = distance;
            }
            return best;
        }
```

(c) In the class summary replace `every think tick it looks for the nearest living friendly inside its detection radius that it can see,` with `every think tick it looks for the nearest living friendly inside its detection radius that it can see and reach,`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `Tools/run-tests.sh PlayMode` → expected `total="193" passed="193"`, `EXIT=0`.
Run: `Tools/run-tests.sh EditMode` → expected `total="219" passed="219"`, `EXIT=0`.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/AI/EnemyAI.cs Assets/_Project/Tests/PlayMode/EnemyAIPlayModeTests.cs
git commit -m "Make hostiles pick the nearest visible friendly they can reach

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 7: `CompanionAI` pure rules (state, assist target, follow point)

**Files:**
- Create: `Assets/_Project/Scripts/AI/CompanionAI.cs` (enum, component skeleton and static rules; `Update` comes in Task 8)
- Create: `Assets/_Project/Tests/EditMode/CompanionAITests.cs`

**Interfaces:**
- Produces: `public enum CompanionState { Dead, Controlled, Orders, Assist, Follow, Idle }`;
  `public static CompanionState CompanionAI.DeriveState(bool alive, bool isControlled, UnitCommand current, int pendingCount, UnitCommand ownCommand)`;
  `public static Health CompanionAI.ChooseAssistTarget(Vector3 from, float range, Health leaderTarget, IReadOnlyList<Health> hostiles, Func<Health, bool> isEngaged, Func<Vector3, bool> canReach)`;
  `public static Vector3 CompanionAI.FollowPoint(Vector3 leader, Vector3 companion, float followDistance, Vector3 leaderForward)`;
  component fields `activeCharacter`, `encounter`, `followDistance = 3.5f`, `followStartDistance = 6f`, `followRepathDistance = 2f`, `assistRange = 10f`, `thinkInterval = 0.25f`; `internal void Initialize(ActiveCharacter active, Encounter encounterToAssist, float follow = 3.5f, float followStart = 6f, float followRepath = 2f, float assist = 10f, float interval = 0.25f)`; properties `State`, `AssistTarget`, `IsFollowing`, `FollowDistance`, `FollowStartDistance`, `AssistRange`, `IsWired`.

- [ ] **Step 1: Write the failing tests**

Create `Assets/_Project/Tests/EditMode/CompanionAITests.cs`:

```csharp
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class CompanionAITests
    {
        readonly List<GameObject> hosts = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (var host in hosts)
                Object.DestroyImmediate(host);
            hosts.Clear();
        }

        Health MakeHealth(string name, Vector3 position, bool alive = true, bool active = true)
        {
            var host = new GameObject(name);
            hosts.Add(host);
            host.transform.position = position;
            var health = host.AddComponent<Health>();
            health.Initialize(10);
            if (!alive)
                health.TakeDamage(10);   // disableOnDeath deactivates it too
            if (!active)
                host.SetActive(false);
            return health;
        }

        static bool Always(Health _) => true;
        static bool Never(Health _) => false;
        static bool Reachable(Vector3 _) => true;

        // --- DeriveState ---

        [Test]
        public void DeriveState_DeadBeatsEverything()
        {
            var own = new MoveCommand(Vector3.zero);
            Assert.That(CompanionAI.DeriveState(false, false, own, 0, own), Is.EqualTo(CompanionState.Dead));
            Assert.That(CompanionAI.DeriveState(false, true, null, 0, null), Is.EqualTo(CompanionState.Dead));
        }

        [Test]
        public void DeriveState_ControlledBeatsOrders()
        {
            var order = new MoveCommand(Vector3.zero);
            Assert.That(CompanionAI.DeriveState(true, true, order, 2, null), Is.EqualTo(CompanionState.Controlled));
            Assert.That(CompanionAI.DeriveState(true, true, null, 0, null), Is.EqualTo(CompanionState.Controlled));
        }

        [Test]
        public void DeriveState_IdleWithoutOrders()
        {
            Assert.That(CompanionAI.DeriveState(true, false, null, 0, null), Is.EqualTo(CompanionState.Idle));
        }

        [Test]
        public void DeriveState_OrdersWhenTheCurrentOrderIsNotOurs_OrAnythingIsPending()
        {
            var own = new MoveCommand(Vector3.zero);
            var theirs = new MoveCommand(Vector3.one);
            Assert.That(CompanionAI.DeriveState(true, false, theirs, 0, own), Is.EqualTo(CompanionState.Orders), "someone replaced our order");
            Assert.That(CompanionAI.DeriveState(true, false, theirs, 0, null), Is.EqualTo(CompanionState.Orders), "an explicit order while we had none");
            Assert.That(CompanionAI.DeriveState(true, false, own, 1, own), Is.EqualTo(CompanionState.Orders), "a queued order behind ours");
        }

        [Test]
        public void DeriveState_AssistForOurAttack_FollowForOurMove()
        {
            var target = MakeHealth("Hostile", Vector3.zero);
            var attack = new AttackCommand(target);
            var move = new MoveCommand(Vector3.zero);
            Assert.That(CompanionAI.DeriveState(true, false, attack, 0, attack), Is.EqualTo(CompanionState.Assist));
            Assert.That(CompanionAI.DeriveState(true, false, move, 0, move), Is.EqualTo(CompanionState.Follow));
        }

        // --- ChooseAssistTarget ---

        [Test]
        public void ChooseAssistTarget_PrefersTheLeadersTarget_WhenItIsAValidHostile()
        {
            var near = MakeHealth("Near", new Vector3(2f, 1f, 0f));
            var leaders = MakeHealth("Leaders", new Vector3(8f, 1f, 0f));
            var hostiles = new[] { near, leaders };

            var chosen = CompanionAI.ChooseAssistTarget(Vector3.zero, 10f, leaders, hostiles, Always, Reachable);

            Assert.That(chosen, Is.SameAs(leaders));
        }

        [Test]
        public void ChooseAssistTarget_LeadersTargetOutsideTheList_OutOfRange_OrDead_IsIgnored()
        {
            var near = MakeHealth("Near", new Vector3(2f, 1f, 0f));
            var notAHostile = MakeHealth("Friendly", new Vector3(3f, 1f, 0f));
            var farHostile = MakeHealth("Far", new Vector3(12f, 1f, 0f));
            var deadHostile = MakeHealth("Dead", new Vector3(4f, 1f, 0f), alive: false);
            var hostiles = new[] { near, farHostile, deadHostile };

            Assert.That(CompanionAI.ChooseAssistTarget(Vector3.zero, 10f, notAHostile, hostiles, Always, Reachable), Is.SameAs(near));
            Assert.That(CompanionAI.ChooseAssistTarget(Vector3.zero, 10f, farHostile, hostiles, Always, Reachable), Is.SameAs(near));
            Assert.That(CompanionAI.ChooseAssistTarget(Vector3.zero, 10f, deadHostile, hostiles, Always, Reachable), Is.SameAs(near));
        }

        [Test]
        public void ChooseAssistTarget_OtherwiseTheNearestEngagedHostile_InRangeAndReachable()
        {
            var idle = MakeHealth("Idle", new Vector3(1f, 1f, 0f));
            var engagedFar = MakeHealth("EngagedFar", new Vector3(9f, 1f, 0f));
            var engagedNear = MakeHealth("EngagedNear", new Vector3(5f, 1f, 0f));
            var engagedOutOfRange = MakeHealth("OutOfRange", new Vector3(11f, 1f, 0f));
            var engagedUnreachable = MakeHealth("Unreachable", new Vector3(3f, 1f, 0f));
            var dead = MakeHealth("Dead", new Vector3(0.5f, 1f, 0f), alive: false);
            var inactive = MakeHealth("Inactive", new Vector3(0.5f, 1f, 0f), active: false);
            var hostiles = new[] { idle, engagedFar, engagedNear, engagedOutOfRange, engagedUnreachable, dead, inactive, null };

            var chosen = CompanionAI.ChooseAssistTarget(Vector3.zero, 10f, null, hostiles, h => h != idle, p => p.x != 3f);

            Assert.That(chosen, Is.SameAs(engagedNear));
        }

        [Test]
        public void ChooseAssistTarget_NothingEngaged_IsNull()
        {
            var idle = MakeHealth("Idle", new Vector3(1f, 1f, 0f));
            Assert.That(CompanionAI.ChooseAssistTarget(Vector3.zero, 10f, null, new[] { idle }, Never, Reachable), Is.Null);
            Assert.That(CompanionAI.ChooseAssistTarget(Vector3.zero, 10f, null, new Health[0], Always, Reachable), Is.Null);
        }

        // --- FollowPoint ---

        [Test]
        public void FollowPoint_LiesOnTheCompanionsSideOfTheLeader_AtTheFollowDistance()
        {
            var leader = new Vector3(10f, 1f, 10f);
            var companion = new Vector3(10f, 1f, 0f);
            var point = CompanionAI.FollowPoint(leader, companion, 3.5f, Vector3.forward);
            Assert.That(point.x, Is.EqualTo(10f).Within(0.001f));
            Assert.That(point.z, Is.EqualTo(6.5f).Within(0.001f));
            Assert.That(point.y, Is.EqualTo(leader.y), "the point keeps the leader's height");
        }

        [Test]
        public void FollowPoint_OnTopOfTheLeader_FallsBehindIt()
        {
            var leader = new Vector3(0f, 1f, 0f);
            var point = CompanionAI.FollowPoint(leader, leader, 3.5f, Vector3.right);
            Assert.That(point.x, Is.EqualTo(-3.5f).Within(0.001f), "behind a leader facing +x");
            Assert.That(point.z, Is.EqualTo(0f).Within(0.001f));
        }

        // --- Component ---

        [Test]
        public void RequiredComponentsAreAdded_AndDefaultsMatchThePrototype()
        {
            var host = new GameObject("Companion");
            hosts.Add(host);
            var ai = host.AddComponent<CompanionAI>();
            Assert.That(host.GetComponent<CommandableUnit>(), Is.Not.Null);
            Assert.That(host.GetComponent<Health>(), Is.Not.Null);
            Assert.That(host.GetComponent<UnitAttacker>(), Is.Not.Null);
            Assert.That(ai.FollowDistance, Is.EqualTo(3.5f));
            Assert.That(ai.FollowStartDistance, Is.EqualTo(6f));
            Assert.That(ai.AssistRange, Is.EqualTo(10f));
            Assert.That(ai.IsWired, Is.False);
            Assert.That(ai.AssistTarget, Is.Null);
            Assert.That(ai.IsFollowing, Is.False);
            Assert.That(ai.State, Is.EqualTo(CompanionState.Idle), "unwired and idle: nobody controls it, it has no orders");
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `Tools/run-tests.sh EditMode Blackglass.Tests.CompanionAITests` → expected `EXIT=1` with `error CS0103: The name 'CompanionAI' does not exist`.

- [ ] **Step 3: Create the component with its pure rules**

Create `Assets/_Project/Scripts/AI/CompanionAI.cs`:

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>What a companion is doing, as CompanionAI.DeriveState reads it. Debug views show it.</summary>
    public enum CompanionState
    {
        Dead,
        /// <summary>This unit is the directly controlled character: no autonomy.</summary>
        Controlled,
        /// <summary>Running an order it did not give itself (the player's, or a retaliation), or has orders queued.</summary>
        Orders,
        /// <summary>Running its own attack on a hostile.</summary>
        Assist,
        /// <summary>Running its own move toward the controlled character.</summary>
        Follow,
        Idle,
    }

    /// <summary>
    /// A friendly unit's autonomy while the player is not telling it what to do. Priority: dead, controlled, explicit
    /// orders, assist (attack a hostile that is fighting the squad), follow the controlled character, idle. It tells
    /// its own orders from everyone else's by remembering the command object it issued: any other current order, or
    /// any pending order, is left alone. Decides only what to do; CommandableUnit does it. Runs on simulation time.
    /// </summary>
    // After ActiveCharacter (-200) refreshes who is controlled and before DirectControlInput (-100) hands over, so a
    // companion that just became the controlled character has already dropped its own follow move when the hand-over
    // looks at its orders, and a held move key carries over as decision 012 intends.
    [DefaultExecutionOrder(-150)]
    [RequireComponent(typeof(CommandableUnit), typeof(Health), typeof(UnitAttacker))]
    public sealed class CompanionAI : MonoBehaviour
    {
        [SerializeField] ActiveCharacter activeCharacter;
        [SerializeField] Encounter encounter;
        [Header("Follow")]
        // Where a follow move aims: this far from the leader, on the companion's side.
        [SerializeField, Min(0f)] float followDistance = 3.5f;
        // A companion farther than this starts following; the gap to followDistance stops it from pacing.
        [SerializeField, Min(0f)] float followStartDistance = 6f;
        // A running follow move is re-aimed once the leader has moved this far from where it was aimed.
        [SerializeField, Min(0f)] float followRepathDistance = 2f;
        [Header("Assist")]
        [SerializeField, Min(0f)] float assistRange = 10f;
        [SerializeField, Min(0f)] float thinkInterval = 0.25f;

        CommandableUnit unit;
        UnitMover mover;
        float nextThinkTime;
        // The order this component issued, while it is still the unit's current order.
        UnitCommand ownCommand;
        Vector3 leaderPositionAtIssue;
        Func<Vector3, bool> canReach;

        public float FollowDistance => followDistance;
        public float FollowStartDistance => followStartDistance;
        public float AssistRange => assistRange;

        /// <summary>True when the scene wired both references this component needs.</summary>
        public bool IsWired => activeCharacter != null && encounter != null;

        /// <summary>Derived each read; nothing is stored except our own command.</summary>
        public CompanionState State =>
            DeriveState(Unit.IsAlive, IsControlled, Unit.CurrentCommand, Unit.PendingCommands.Count, OwnCommand);

        /// <summary>The hostile our own assist order targets, or null.</summary>
        public Health AssistTarget => OwnCommand is AttackCommand attack ? attack.Target : null;

        /// <summary>True while our own follow move is the unit's current order.</summary>
        public bool IsFollowing => OwnCommand is MoveCommand;

        CommandableUnit Unit => unit != null ? unit : unit = GetComponent<CommandableUnit>();
        UnitMover Mover => mover != null ? mover : mover = GetComponent<UnitMover>();

        bool IsControlled => activeCharacter != null && activeCharacter.Unit == Unit;

        // Our order only counts while it is still current: Replace by anyone else, Stop, death or direct control all
        // make a different (or no) order current, which reads as "not ours".
        UnitCommand OwnCommand => ownCommand != null && Unit.CurrentCommand == ownCommand ? ownCommand : null;

        internal void Initialize(ActiveCharacter active, Encounter encounterToAssist, float follow = 3.5f, float followStart = 6f,
            float followRepath = 2f, float assist = 10f, float interval = 0.25f)
        {
            activeCharacter = active;
            encounter = encounterToAssist;
            followDistance = follow;
            followStartDistance = followStart;
            followRepathDistance = followRepath;
            assistRange = assist;
            thinkInterval = interval;
        }

        /// <summary>
        /// Dead beats everything, then controlled. With no order the companion is idle. A current order that is not
        /// ours, or anything pending behind ours, means orders (explicit, or a retaliation). Otherwise our own attack
        /// is assist and our own move is follow.
        /// </summary>
        public static CompanionState DeriveState(bool alive, bool isControlled, UnitCommand current, int pendingCount, UnitCommand ownCommand)
        {
            if (!alive)
                return CompanionState.Dead;
            if (isControlled)
                return CompanionState.Controlled;
            if (current == null)
                return CompanionState.Idle;
            if (current != ownCommand || pendingCount > 0)
                return CompanionState.Orders;
            return ownCommand is AttackCommand ? CompanionState.Assist : CompanionState.Follow;
        }

        /// <summary>
        /// Who to assist against, when the companion has no assist order yet (a running one is never retargeted). The
        /// leader's own target first, when it is a living hostile in range and reachable; otherwise the nearest hostile
        /// that is engaged (already attacking someone), alive, active, in range and reachable. Unaware hostiles are
        /// never chosen: companions do not start fights.
        /// </summary>
        public static Health ChooseAssistTarget(Vector3 from, float range, Health leaderTarget,
            IReadOnlyList<Health> hostiles, Func<Health, bool> isEngaged, Func<Vector3, bool> canReach)
        {
            if (hostiles == null)
                throw new ArgumentNullException(nameof(hostiles));
            if (isEngaged == null)
                throw new ArgumentNullException(nameof(isEngaged));
            if (canReach == null)
                throw new ArgumentNullException(nameof(canReach));

            if (IsValidTarget(from, range, leaderTarget, canReach) && Contains(hostiles, leaderTarget))
                return leaderTarget;

            Health best = null;
            var bestDistance = float.PositiveInfinity;
            foreach (var hostile in hostiles)
            {
                if (!IsLiving(hostile))
                    continue;
                var distance = FlatDistance(from, hostile.transform.position);
                if (distance > range || distance >= bestDistance || !isEngaged(hostile) || !canReach(hostile.transform.position))
                    continue;
                best = hostile;
                bestDistance = distance;
            }
            return best;
        }

        /// <summary>
        /// The spot a follow move aims at: `followDistance` from the leader on the companion's side, at the leader's
        /// height. A companion standing on the leader aims behind it (opposite its forward), so the two part.
        /// </summary>
        public static Vector3 FollowPoint(Vector3 leader, Vector3 companion, float followDistance, Vector3 leaderForward)
        {
            var direction = companion - leader;
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.01f)
            {
                direction = -leaderForward;
                direction.y = 0f;
            }
            if (direction.sqrMagnitude < 0.0001f)
                direction = Vector3.back;
            return leader + direction.normalized * followDistance;
        }

        static bool IsLiving(Health health) => health != null && health.IsAlive && health.gameObject.activeInHierarchy;

        static bool IsValidTarget(Vector3 from, float range, Health target, Func<Vector3, bool> canReach) =>
            IsLiving(target) && FlatDistance(from, target.transform.position) <= range && canReach(target.transform.position);

        static bool Contains(IReadOnlyList<Health> list, Health health)
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i] == health)
                    return true;
            }
            return false;
        }

        static float FlatDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `Tools/run-tests.sh EditMode` → expected `total="231" passed="231"`, `EXIT=0`.
Run: `Tools/run-tests.sh PlayMode` → expected `total="193" passed="193"`, `EXIT=0` (nothing uses the component yet).

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/AI/CompanionAI.cs Assets/_Project/Scripts/AI/CompanionAI.cs.meta Assets/_Project/Tests/EditMode/CompanionAITests.cs Assets/_Project/Tests/EditMode/CompanionAITests.cs.meta
git commit -m "Add CompanionAI's state, assist-target and follow-point rules

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 8: `CompanionAI` behaviour: follow, assist, orders win, hand-over

**Files:**
- Modify: `Assets/_Project/Scripts/AI/CompanionAI.cs` (`Update`, `Think`, issuing)
- Modify: `Assets/_Project/Tests/PlayMode/TestSupport/TestWorld.cs` (`CreateActiveCharacter`, `CreateCompanion`)
- Create: `Assets/_Project/Tests/PlayMode/CompanionAIPlayModeTests.cs`

**Interfaces:**
- Consumes: `ActiveCharacter.Unit/HasUnit/SetUnit/Initialize(CommandableUnit, TacticalPause, UnitSelection)`, `Encounter.Hostiles`, `CommandableUnit.Issue/CurrentCommand/PendingCommands/MoveIntent/IsAlive`, `UnitMover.CanReach`.
- Produces: `internal bool CompanionAI.YieldToControl()` (steps 0–2, every simulation frame) and `internal void CompanionAI.Think()` (steps 3–6, every think tick); `[DefaultExecutionOrder(-150)]` on the class; `TestWorld.CreateActiveCharacter(CommandableUnit unit, TacticalPause pause = null)`, `TestWorld.CreateCompanion(Vector3 groundPosition, ActiveCharacter active, Encounter encounter, int maxHealth = 100)`.

- [ ] **Step 1: Write the failing tests**

Add to `Assets/_Project/Tests/PlayMode/TestSupport/TestWorld.cs`, after `CreateHostile`:

```csharp
        /// <summary>An ActiveCharacter on its own object, controlling the given unit (no roster, so Cycle does nothing).</summary>
        public ActiveCharacter CreateActiveCharacter(CommandableUnit unit, TacticalPause pause = null)
        {
            var active = Track(new GameObject("ActiveCharacter")).AddComponent<ActiveCharacter>();
            active.Initialize(unit, pause);
            return active;
        }

        /// <summary>A friendly fighter with CompanionAI wired to the active character and the encounter.</summary>
        public CompanionAI CreateCompanion(Vector3 groundPosition, ActiveCharacter active, Encounter encounter, int maxHealth = 100)
        {
            var unit = CreateFighter(groundPosition, maxHealth);
            unit.name = "TestCompanion";
            unit.gameObject.SetActive(false);
            var ai = unit.gameObject.AddComponent<CompanionAI>();
            ai.Initialize(active, encounter);
            unit.gameObject.SetActive(true);
            return ai;
        }
```

Create `Assets/_Project/Tests/PlayMode/CompanionAIPlayModeTests.cs`:

```csharp
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CompanionAIPlayModeTests
    {
        TestWorld world;
        Encounter encounter;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            encounter = world.CreateEncounter();
        }

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        static Health HealthOf(Component unit) => unit.GetComponent<Health>();
        static CommandableUnit UnitOf(Component c) => c.GetComponent<CommandableUnit>();
        static float Gap(Component a, Component b) => TestWorld.HorizontalDistance(a.transform.position, b.transform.position);

        void Arm(Health[] hostiles, params Component[] friendlies) =>
            encounter.Initialize(friendlies.Select(f => HealthOf(f)), hostiles);

        // A leader on open ground with a companion at the given offset; the encounter has no hostiles.
        (CommandableUnit leader, ActiveCharacter active, CompanionAI companion) Squad(Vector3 companionPosition)
        {
            var leader = world.CreateFighter(Vector3.zero);
            leader.name = "Leader";
            var active = world.CreateActiveCharacter(leader);
            var companion = world.CreateCompanion(companionPosition, active, encounter);
            Arm(new Health[0], leader, companion);
            return (leader, active, companion);
        }

        [UnityTest]
        public IEnumerator IdleCompanionFarFromTheLeader_WalksCloser_AndStops()
        {
            world.CreateEnvironment();
            var (leader, _, companion) = Squad(new Vector3(0f, 0f, -12f));

            yield return TestWorld.WaitUntil(() => companion.State == CompanionState.Follow, 1f);
            Assert.That(companion.State, Is.EqualTo(CompanionState.Follow), "A companion 12 m away must start following");
            yield return TestWorld.WaitUntil(() => UnitOf(companion).CurrentCommand == null, 10f);
            var arrived = Gap(companion, leader);
            yield return new WaitForSeconds(1f);

            Assert.That(UnitOf(companion).CurrentCommand, Is.Null, "Once close it stays put");
            Assert.That(companion.State, Is.EqualTo(CompanionState.Idle));
            Assert.That(arrived, Is.LessThanOrEqualTo(4.5f).And.GreaterThanOrEqualTo(2.5f), "It stops short of the leader, not on top of it");
            Assert.That(Gap(companion, leader), Is.EqualTo(arrived).Within(0.3f), "No pacing after arrival");
        }

        [UnityTest]
        public IEnumerator CompanionNearTheLeader_IssuesNoMove()
        {
            world.CreateEnvironment();
            var (_, _, companion) = Squad(new Vector3(3f, 0f, 0f));
            var start = companion.transform.position;
            var everOrdered = false;

            var deadline = Time.realtimeSinceStartup + 2f;
            while (Time.realtimeSinceStartup < deadline)
            {
                everOrdered |= UnitOf(companion).CurrentCommand != null;
                yield return null;
            }

            Assert.That(everOrdered, Is.False, "Inside the start distance the companion must not move");
            Assert.That(TestWorld.HorizontalDistance(companion.transform.position, start), Is.LessThan(0.05f));
            Assert.That(companion.State, Is.EqualTo(CompanionState.Idle));
        }

        [UnityTest]
        public IEnumerator LeaderWalksAway_CompanionRepathsAndKeepsUp()
        {
            world.CreateEnvironment();
            var (leader, _, companion) = Squad(new Vector3(2f, 0f, 0f));
            Assert.That(leader.Issue(new MoveCommand(new Vector3(0f, 0f, 15f))), Is.True);

            yield return TestWorld.WaitUntil(() => leader.CurrentCommand == null, 10f);
            Assert.That(leader.CurrentCommand, Is.Null, "Precondition: the leader arrived");
            yield return TestWorld.WaitUntil(() => UnitOf(companion).CurrentCommand == null && Gap(companion, leader) < 6f, 10f);

            Assert.That(Gap(companion, leader), Is.LessThan(6f), "The companion must end up near the leader");
            Assert.That(companion.transform.position.z, Is.GreaterThan(8f), "It followed the leader north");
        }

        [UnityTest]
        public IEnumerator ExplicitMove_IsNotInterrupted_AndFollowResumesAfterwards()
        {
            world.CreateEnvironment();
            var (leader, _, companion) = Squad(new Vector3(0f, 0f, -10f));
            var away = new Vector3(0f, 0f, -16f);
            var order = new MoveCommand(away);
            Assert.That(UnitOf(companion).Issue(order), Is.True);   // same frame as creation: before the first think

            yield return TestWorld.WaitUntil(() => UnitOf(companion).CurrentCommand != order, 10f);
            var whereTheOrderEnded = companion.transform.position;
            Assert.That(TestWorld.HorizontalDistance(whereTheOrderEnded, away), Is.LessThan(0.5f), "The explicit move must run to its end");

            yield return TestWorld.WaitUntil(() => Gap(companion, leader) < 6f, 12f);
            Assert.That(Gap(companion, leader), Is.LessThan(6f), "Following resumes once the explicit order is done");
        }

        [UnityTest]
        public IEnumerator QueuedOrder_BehindAFollowMove_IsNotInterrupted()
        {
            world.CreateEnvironment();
            var (leader, _, companion) = Squad(new Vector3(0f, 0f, -12f));
            yield return TestWorld.WaitUntil(() => companion.IsFollowing, 1f);
            Assert.That(companion.IsFollowing, Is.True, "Precondition: a follow move is running");

            var away = new Vector3(10f, 0f, -12f);
            Assert.That(UnitOf(companion).Issue(new MoveCommand(away), IssueMode.Append), Is.True);
            Assert.That(companion.State, Is.EqualTo(CompanionState.Orders), "A pending order makes the companion 'under orders'");

            yield return TestWorld.WaitUntil(() => TestWorld.HorizontalDistance(companion.transform.position, away) < 0.5f, 15f);
            Assert.That(TestWorld.HorizontalDistance(companion.transform.position, away), Is.LessThan(0.5f),
                "The queued order must run after the follow move, not be dropped");
        }

        [UnityTest]
        public IEnumerator SwitchingTheControlledCharacter_SwapsRoles()
        {
            world.CreateEnvironment();
            var (leader, active, companion) = Squad(new Vector3(0f, 0f, -12f));
            // The leader can be a companion too. Added while inactive, so OnEnable sees the wiring and logs no warning.
            leader.gameObject.SetActive(false);
            var leaderAi = leader.gameObject.AddComponent<CompanionAI>();
            leaderAi.Initialize(active, encounter);
            leader.gameObject.SetActive(true);
            yield return TestWorld.WaitUntil(() => companion.IsFollowing, 1f);
            Assert.That(companion.IsFollowing, Is.True, "Precondition");
            Assert.That(leaderAi.State, Is.EqualTo(CompanionState.Controlled));

            active.SetUnit(UnitOf(companion));
            yield return null;   // one simulation frame: the per-frame controlled check, not the 0.25 s tick
            Assert.That(UnitOf(companion).CurrentCommand, Is.Null, "The newly controlled unit drops its own follow move on the next frame");
            yield return TestWorld.WaitUntil(() => leaderAi.IsFollowing, 1f);

            Assert.That(companion.State, Is.EqualTo(CompanionState.Controlled));
            Assert.That(leaderAi.State, Is.EqualTo(CompanionState.Follow), "The old leader now follows the new one");
        }

        [UnityTest]
        public IEnumerator LeaderWalksIntoAFollowingCompanion_NeverSendsItBackwards()
        {
            world.CreateEnvironment();
            var (leader, _, companion) = Squad(new Vector3(0f, 0f, -13f));
            yield return TestWorld.WaitUntil(() => companion.IsFollowing, 1f);
            Assert.That(companion.IsFollowing, Is.True, "Precondition");
            Assert.That(leader.Issue(new MoveCommand(new Vector3(0f, 0f, -10f))), Is.True);   // straight at the companion

            // Every follow move must aim no farther from the leader than the companion already stands.
            UnitCommand lastSeen = null;
            var deadline = Time.realtimeSinceStartup + 6f;
            while (Time.realtimeSinceStartup < deadline)
            {
                var current = UnitOf(companion).CurrentCommand;
                if (current is MoveCommand move && current != lastSeen)
                {
                    lastSeen = current;
                    var leaderNow = leader.transform.position;
                    Assert.That(TestWorld.HorizontalDistance(move.Destination, leaderNow),
                        Is.LessThanOrEqualTo(TestWorld.HorizontalDistance(companion.transform.position, leaderNow) + 0.1f),
                        "A follow move was aimed away from the leader");
                }
                yield return null;
            }
            Assert.That(Gap(companion, leader), Is.LessThan(6f), "It settles near the leader");
        }

        [UnityTest]
        public IEnumerator FollowPointInsideABlock_IsSnapped_AndTheCompanionStillArrives()
        {
            // A 3 m block between them: the raw follow point (3.5 m from the leader, toward the companion) is inside
            // it, farther than MoveTo's 2 m snap from the eroded mesh. No warning may be logged; the companion arrives.
            world.CreateEnvironment((new Vector3(0f, 1f, -4f), new Vector3(3f, 2f, 3f)));
            var (leader, _, companion) = Squad(new Vector3(0f, 0f, -9f));

            yield return TestWorld.WaitUntil(() => UnitOf(companion).CurrentCommand == null && Gap(companion, leader) < 6f, 12f);

            Assert.That(Gap(companion, leader), Is.LessThan(6f), "The companion must get around the block");
            Assert.That(companion.State, Is.EqualTo(CompanionState.Idle), "It arrived and settled; nothing is being retried");
        }

        [UnityTest]
        public IEnumerator Switching_LeavesAnExplicitOrderOnTheNewLeaderAlone()
        {
            world.CreateEnvironment();
            var (leader, active, companion) = Squad(new Vector3(0f, 0f, -12f));
            var order = new MoveCommand(new Vector3(0f, 0f, -16f));
            Assert.That(UnitOf(companion).Issue(order), Is.True);

            active.SetUnit(UnitOf(companion));
            yield return new WaitForSeconds(0.6f);

            Assert.That(UnitOf(companion).CurrentCommand, Is.SameAs(order).Or.Null, "An explicit order is never stopped by the hand-over");
        }

        [UnityTest]
        public IEnumerator HostileAttackingTheLeader_IsAttackedByTheCompanion()
        {
            world.CreateEnvironment();
            var leader = world.CreateFighter(Vector3.zero);
            var active = world.CreateActiveCharacter(leader);
            var companion = world.CreateCompanion(new Vector3(3f, 0f, 0f), active, encounter);
            var hostile = world.CreateHostile(new Vector3(0f, 0f, 6f), encounter);
            Arm(new[] { HealthOf(hostile) }, leader, companion);

            yield return TestWorld.WaitUntil(() => companion.State == CompanionState.Assist, 3f);

            Assert.That(companion.State, Is.EqualTo(CompanionState.Assist), "An engaged hostile 6 m away must be assisted against");
            Assert.That(companion.AssistTarget, Is.SameAs(HealthOf(hostile)));
            Assert.That(UnitOf(companion).CurrentCommand, Is.TypeOf<AttackCommand>());
        }

        [UnityTest]
        public IEnumerator UnawareIdleHostileNearby_IsLeftAlone()
        {
            world.CreateEnvironment();
            var leader = world.CreateFighter(Vector3.zero);
            var active = world.CreateActiveCharacter(leader);
            var companion = world.CreateCompanion(new Vector3(3f, 0f, 0f), active, encounter);
            var hostile = world.CreateHostile(new Vector3(3f, 0f, 5f), encounter, detectionRange: 0f);   // never notices anyone
            Arm(new[] { HealthOf(hostile) }, leader, companion);

            yield return new WaitForSeconds(1.5f);

            Assert.That(UnitOf(companion).CurrentCommand, Is.Null, "Companions do not start fights with unaware hostiles");
            Assert.That(HealthOf(hostile).Current, Is.EqualTo(HealthOf(hostile).Max));
            Assert.That(companion.State, Is.EqualTo(CompanionState.Idle));
        }

        [UnityTest]
        public IEnumerator LeadersTarget_IsPreferredOverANearerEngagedHostile()
        {
            world.CreateEnvironment();
            var leader = world.CreateFighter(Vector3.zero);
            var active = world.CreateActiveCharacter(leader);
            var companion = world.CreateCompanion(new Vector3(0f, 0f, -2f), active, encounter);
            var leaderTarget = world.CreateHostile(new Vector3(0f, 0f, 7f), encounter, detectionRange: 0f);
            var nearer = world.CreateHostile(new Vector3(-4f, 0f, -2f), encounter, detectionRange: 0f);
            Arm(new[] { HealthOf(leaderTarget), HealthOf(nearer) }, leader, companion);
            Assert.That(UnitOf(nearer).Issue(new AttackCommand(HealthOf(leader))), Is.True);   // engaged, with the leader
            Assert.That(leader.Issue(new AttackCommand(HealthOf(leaderTarget))), Is.True);

            yield return TestWorld.WaitUntil(() => companion.AssistTarget != null, 2f);

            Assert.That(companion.AssistTarget, Is.SameAs(HealthOf(leaderTarget)), "The leader's target beats a nearer engaged hostile");
        }

        [UnityTest]
        public IEnumerator ExplicitOrderDuringAssist_ReplacesIt_AndIsNotRetaken()
        {
            world.CreateEnvironment();
            var leader = world.CreateFighter(Vector3.zero);
            var active = world.CreateActiveCharacter(leader);
            var companion = world.CreateCompanion(new Vector3(3f, 0f, 0f), active, encounter);
            var hostile = world.CreateHostile(new Vector3(0f, 0f, 6f), encounter);
            Arm(new[] { HealthOf(hostile) }, leader, companion);
            yield return TestWorld.WaitUntil(() => companion.State == CompanionState.Assist, 3f);
            Assert.That(companion.State, Is.EqualTo(CompanionState.Assist), "Precondition");

            var retreat = new MoveCommand(new Vector3(12f, 0f, -8f));
            Assert.That(UnitOf(companion).Issue(retreat), Is.True);
            var deadline = Time.realtimeSinceStartup + 1.5f;
            while (Time.realtimeSinceStartup < deadline)
            {
                Assert.That(UnitOf(companion).CurrentCommand, Is.SameAs(retreat), "The AI must not retake control while the explicit move runs");
                Assert.That(companion.State, Is.EqualTo(CompanionState.Orders));
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator DeadCompanion_DoesNothing()
        {
            world.CreateEnvironment();
            var (_, _, companion) = Squad(new Vector3(0f, 0f, -12f));
            HealthOf(companion).TakeDamage(1000);

            yield return new WaitForSeconds(0.8f);

            Assert.That(companion.State, Is.EqualTo(CompanionState.Dead));
            Assert.That(UnitOf(companion).CurrentCommand, Is.Null);
            // Any exception logged while the dead companion's object is inactive fails the test on its own.
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `Tools/run-tests.sh PlayMode Blackglass.Tests.CompanionAIPlayModeTests` → expected `EXIT=2`: every follow and assist test fails on its first wait (the component has no `Update` yet, so `State` stays `Idle` and nothing moves); `CompanionNearTheLeader_IssuesNoMove`, `UnawareIdleHostileNearby_IsLeftAlone`, `Switching_LeavesAnExplicitOrderOnTheNewLeaderAlone` and `DeadCompanion_DoesNothing` pass vacuously.

- [ ] **Step 3: Implement the think tick**

In `Assets/_Project/Scripts/AI/CompanionAI.cs`, after `Initialize` and before `DeriveState`, add:

```csharp
        void OnEnable()
        {
            if (!IsWired)
                Debug.LogWarning($"{name} has no ActiveCharacter or Encounter wired, so it will neither follow nor assist.", this);
        }

        void Update()
        {
            if (!SimulationTime.IsRunning || !Unit.IsAlive)
                return;
            // "Am I controlled now?" is answered every frame so the stop lands on the Tab frame; the rest is a tick.
            if (YieldToControl())
                return;
            if (Time.time < nextThinkTime)
                return;
            nextThinkTime = Time.time + thinkInterval;
            Think();
        }

        /// <summary>
        /// Steps 0-2 of the priority: forget a finished or replaced own order, then, if this unit is the controlled
        /// character (or is being driven), drop our own order once and report that autonomy must stay out.
        /// </summary>
        internal bool YieldToControl()
        {
            ownCommand = OwnCommand;   // forget an order that finished or was replaced
            if (!IsControlled && Unit.MoveIntent == Vector3.zero)
                return false;
            // No autonomy for the controlled character: drop our own order once, never anyone else's.
            if (ownCommand != null && Unit.PendingCommands.Count == 0)
            {
                Unit.Issue(new StopCommand());
                ownCommand = null;
            }
            return true;
        }

        /// <summary>Steps 3-6 of the priority: orders win, then assist, then follow. Internal so tests can drive it.</summary>
        internal void Think()
        {
            ownCommand = OwnCommand;

            // Explicit orders (or a retaliation) win: a current order that is not ours, or anything queued.
            if (Unit.CurrentCommand != null && (ownCommand == null || Unit.PendingCommands.Count > 0))
                return;

            // Acquisition only: a running assist is never retargeted; it ends with its target, then we look again.
            if (ownCommand is AttackCommand)
                return;
            var target = ChooseAssistTarget();
            if (target != null)
            {
                IssueOwn(new AttackCommand(target));
                return;
            }

            if (activeCharacter == null || !activeCharacter.HasUnit || !activeCharacter.Unit.IsAlive)
                return;
            var leader = activeCharacter.Unit.transform;
            var distanceToLeader = FlatDistance(leader.position, transform.position);
            if (ownCommand is MoveCommand)
            {
                // Re-aim only while still beyond the follow distance: FollowPoint lies on the far side of a companion
                // the leader has walked into, so re-aiming then would send it away from the leader.
                if (distanceToLeader > followDistance && FlatDistance(leader.position, leaderPositionAtIssue) >= followRepathDistance)
                    IssueFollow(leader);
                return;
            }
            if (distanceToLeader > followStartDistance)
                IssueFollow(leader);
        }

        Health ChooseAssistTarget()
        {
            if (encounter == null)
                return null;
            Health leaderTarget = null;
            if (activeCharacter != null && activeCharacter.HasUnit && activeCharacter.Unit.CurrentCommand is AttackCommand leaderAttack)
                leaderTarget = leaderAttack.Target;
            canReach ??= Mover.CanReach;   // a plain delegate, cached so ticks allocate nothing
            return ChooseAssistTarget(transform.position, assistRange, leaderTarget, encounter.Hostiles, IsEngaged, canReach);
        }

        // A hostile that is attacking anyone is engaged; its order is read from the shared unit, not from EnemyAI.
        static bool IsEngaged(Health hostile) =>
            hostile.TryGetComponent<CommandableUnit>(out var hostileUnit) && hostileUnit.CurrentCommand is AttackCommand;

        // The raw follow point can sit inside a wall or past the ground edge, farther than MoveTo's 2 m snap; snap it
        // first and otherwise aim at the leader itself, which is always on the mesh. A rejected order is forgotten.
        void IssueFollow(Transform leader)
        {
            var point = FollowPoint(leader.position, transform.position, followDistance, leader.forward);
            if (!Mover.TrySnap(point, out var onMesh) || FlatDistance(onMesh, point) > followDistance)
                onMesh = leader.position;
            if (IssueOwn(new MoveCommand(onMesh)))
                leaderPositionAtIssue = leader.position;
        }

        // Replace is safe here: Think only reaches an issue when nothing but our own order is current.
        bool IssueOwn(UnitCommand command)
        {
            if (Unit.Issue(command))
            {
                ownCommand = command;
                return true;
            }
            ownCommand = null;
            return false;
        }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `Tools/run-tests.sh PlayMode Blackglass.Tests.CompanionAIPlayModeTests` → expected `total="13" passed="13"`, `EXIT=0`.
Run: `Tools/run-tests.sh PlayMode` → expected `total="206" passed="206"`, `EXIT=0`.
Run: `Tools/run-tests.sh EditMode` → expected `total="231" passed="231"`, `EXIT=0`.

Troubleshooting:
- `IdleCompanionFarFromTheLeader_...` arrives farther than 4.5 m: `MoveTo` snaps the follow point within 2 m and the agent stops 0.1 m short; the point is 3.5 m out, so 3.6–3.7 m is expected. If it is more, check `FollowPoint` used `followDistance` and not `followStartDistance`.
- `LeadersTarget_IsPreferredOverANearerEngagedHostile` picks the nearer one: the leader's order must be issued before the companion's first `Think` (same frame as creation, before any `yield`). Check the test issues it before yielding.
- `HostileAttackingTheLeader_...`: the hostile's `EnemyAI` needs `encounter.Friendlies` to contain the leader (`Arm` lists both friendlies).

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/AI/CompanionAI.cs Assets/_Project/Tests/PlayMode/TestSupport/TestWorld.cs Assets/_Project/Tests/PlayMode/CompanionAIPlayModeTests.cs Assets/_Project/Tests/PlayMode/CompanionAIPlayModeTests.cs.meta
git commit -m "Make companions follow the controlled character and assist in fights

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 9: Tactical pause freezes repositioning and companions

**Files:**
- Modify: `Assets/_Project/Tests/PlayMode/CombatPausePlayModeTests.cs` (one test)
- Modify: `Assets/_Project/Tests/PlayMode/CompanionAIPlayModeTests.cs` (one test)

**Interfaces:**
- Consumes: `TacticalPause.Pause/Resume/IsPaused`, `CommandableUnit.AttackPhase`, `CompanionAI.State`.
- Produces: no code change expected; these tests pin the pause rules for the new behaviour. If one fails, the fix belongs in the component (a missing `SimulationTime.IsRunning` check), never in the test.

- [ ] **Step 1: Write the tests**

In `Assets/_Project/Tests/PlayMode/CombatPausePlayModeTests.cs` add (reuse the fixture's `world`/`pause` fields; if the fixture has no `TacticalPause` field, create one with `world.Track(new GameObject("Pause")).AddComponent<TacticalPause>()` inside the test):

```csharp
        [UnityTest]
        public IEnumerator Paused_RepositioningUnit_DoesNotAdvance()
        {
            // The pillar scenario from RangedCombatPlayModeTests: the unit is blind and walks to a side spot.
            world.CreateEnvironment((new Vector3(0f, 1f, 0f), new Vector3(1f, 2f, 1f)));
            var pauseObject = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            var ranged = world.CreateFighter(new Vector3(0f, 0f, -3.5f), role: CombatRole.Ranged, range: 8f);
            var dummy = world.CreateDummy(new Vector3(0f, 0f, 4f));
            yield return new WaitForFixedUpdate();
            Assert.That(ranged.Issue(new AttackCommand(dummy)), Is.True);
            yield return TestWorld.WaitUntil(() => ranged.AttackPhase == AttackPhase.Reposition
                && TestWorld.HorizontalDistance(ranged.transform.position, new Vector3(0f, 0f, -3.5f)) > 0.2f, 3f);
            Assert.That(ranged.AttackPhase, Is.EqualTo(AttackPhase.Reposition), "Precondition: it is walking to a firing position");

            pauseObject.Pause();
            var frozenAt = ranged.transform.position;
            var health = dummy.Current;
            yield return new WaitForSecondsRealtime(1f);

            Assert.That(ranged.transform.position, Is.EqualTo(frozenAt), "Repositioning must not advance while paused");
            Assert.That(ranged.AttackPhase, Is.EqualTo(AttackPhase.Reposition));
            Assert.That(dummy.Current, Is.EqualTo(health));

            pauseObject.Resume();
            yield return TestWorld.WaitUntil(() => dummy.Current < health, 8f);
            Assert.That(dummy.Current, Is.LessThan(health), "After resume the unit finishes repositioning and fires");
        }
```

In `Assets/_Project/Tests/PlayMode/CompanionAIPlayModeTests.cs` add:

```csharp
        [UnityTest]
        public IEnumerator Paused_CompanionNeitherMovesNorGainsOrders_AndAcceptsOrders()
        {
            world.CreateEnvironment();
            var pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            pause.Pause();
            var (leader, _, companion) = Squad(new Vector3(0f, 0f, -12f));   // created paused: the first think must wait
            var start = companion.transform.position;

            yield return new WaitForSecondsRealtime(1f);
            Assert.That(UnitOf(companion).CurrentCommand, Is.Null, "No autonomous order while paused");
            Assert.That(companion.transform.position, Is.EqualTo(start));

            var order = new MoveCommand(new Vector3(0f, 0f, -16f));
            Assert.That(UnitOf(companion).Issue(order), Is.True, "Tactical orders are still accepted while paused");
            yield return new WaitForSecondsRealtime(0.3f);
            Assert.That(companion.transform.position, Is.EqualTo(start), "Accepted, but not run until resume");

            pause.Resume();
            yield return TestWorld.WaitUntil(() => UnitOf(companion).CurrentCommand != order, 8f);
            Assert.That(TestWorld.HorizontalDistance(companion.transform.position, order.Destination), Is.LessThan(0.5f),
                "After resume the explicit order runs first, uninterrupted by follow");
            yield return TestWorld.WaitUntil(() => Gap(companion, leader) < 6f, 12f);
            Assert.That(Gap(companion, leader), Is.LessThan(6f), "Then follow resumes");
        }
```

- [ ] **Step 2: Run the tests**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.CombatPausePlayModeTests|Blackglass.Tests.CompanionAIPlayModeTests"` → expected `total="18" passed="18"`, `EXIT=0`. Both components already check `SimulationTime.IsRunning`, so these should pass first time; if not, fix the component.
Run: `Tools/run-tests.sh PlayMode` → expected `total="208" passed="208"`, `EXIT=0`.

- [ ] **Step 3: Commit**

```bash
git add Assets/_Project/Tests/PlayMode/CombatPausePlayModeTests.cs Assets/_Project/Tests/PlayMode/CompanionAIPlayModeTests.cs
git commit -m "Pin that tactical pause freezes repositioning and companion autonomy

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 10: HUD: roles, companion state, sight

**Files:**
- Modify: `Assets/_Project/Scripts/DebugUI/PrototypeHud.cs`
- Modify: `Assets/_Project/Tests/EditMode/PrototypeHudTests.cs`

**Interfaces:**
- Consumes: `UnitAttacker.Role/NeedsLineOfSight/HasLineOfSight`, `CompanionAI.State/AssistTarget`, `CommandableUnit.CurrentCommand`, `EnemyState.Reposition`.
- Produces: `static string DescribeUnit(string unitName, int current, int max, CombatRole role)` (replaces the 3-argument overload); `static string DescribeCompanion(string orders, CompanionState state, string assistTargetName)`; `static string AppendSight(string text, bool hasLineOfSight)`.

- [ ] **Step 1: Write the failing tests**

In `Assets/_Project/Tests/EditMode/PrototypeHudTests.cs`:

(a) Replace `DescribeUnit_NameAndHealth` with:

```csharp
        [Test]
        public void DescribeUnit_NameHealthAndRole()
        {
            Assert.That(PrototypeHud.DescribeUnit("FriendlyUnit_2", 75, 100, CombatRole.Melee), Is.EqualTo("FriendlyUnit_2 75/100 [Melee]"));
            Assert.That(PrototypeHud.DescribeUnit("FriendlyUnit_3", 100, 100, CombatRole.Ranged), Is.EqualTo("FriendlyUnit_3 100/100 [Ranged]"));
        }
```

(b) Add a `Reposition` row to `DescribeEnemy_StateTargetAndCooldown`:

```csharp
        [TestCase(EnemyState.Reposition, "FriendlyUnit_3", 0f, "Reposition -> FriendlyUnit_3")]
```

(c) Add:

```csharp
        [TestCase("", CompanionState.Idle, null, "Idle")]
        [TestCase("", CompanionState.Controlled, null, "Controlled")]
        [TestCase("Move", CompanionState.Follow, null, "Move | Follow")]
        [TestCase("Attack", CompanionState.Assist, "HostileUnit_1", "Attack | Assist -> HostileUnit_1")]
        [TestCase("Attack +1", CompanionState.Orders, null, "Attack +1 | Orders")]
        [TestCase("", CompanionState.Dead, null, "Dead")]
        public void DescribeCompanion_OrdersAndState(string orders, CompanionState state, string assistTarget, string expected)
        {
            Assert.That(PrototypeHud.DescribeCompanion(orders, state, assistTarget), Is.EqualTo(expected));
        }

        [Test]
        public void AppendSight_SaysClearOrBlocked()
        {
            Assert.That(PrototypeHud.AppendSight("Attack", true), Is.EqualTo("Attack LOS clear"));
            Assert.That(PrototypeHud.AppendSight("Reposition -> FriendlyUnit_1", false), Is.EqualTo("Reposition -> FriendlyUnit_1 LOS blocked"));
            Assert.That(PrototypeHud.AppendSight("", false), Is.EqualTo("LOS blocked"));
        }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `Tools/run-tests.sh EditMode Blackglass.Tests.PrototypeHudTests` → expected `EXIT=1` with `error CS1501: No overload for method 'DescribeUnit' takes 4 arguments` and `error CS0117 ... 'DescribeCompanion'`.

- [ ] **Step 3: Implement**

In `Assets/_Project/Scripts/DebugUI/PrototypeHud.cs`:

(a) Replace `DescribeUnit` with:

```csharp
        internal static string DescribeUnit(string unitName, int current, int max, CombatRole role) =>
            $"{unitName} {current}/{max} [{role}]";
```

(b) After `DescribeEnemy` add:

```csharp
        /// <summary>A companion's line: its orders (if any) and what its autonomy is doing, e.g. "Attack | Assist -> HostileUnit_1".</summary>
        internal static string DescribeCompanion(string orders, CompanionState state, string assistTargetName)
        {
            var text = state == CompanionState.Assist && !string.IsNullOrEmpty(assistTargetName)
                ? $"{state} -> {assistTargetName}"
                : state.ToString();
            return string.IsNullOrEmpty(orders) ? text : $"{orders} | {text}";
        }

        /// <summary>Appends the line-of-sight verdict for a ranged unit with an attack order.</summary>
        internal static string AppendSight(string text, bool hasLineOfSight)
        {
            var sight = hasLineOfSight ? "LOS clear" : "LOS blocked";
            return text.Length == 0 ? sight : $"{text} {sight}";
        }
```

(c) Replace `DrawUnitLabel` with:

```csharp
        // Debug only: a handful of units, so per-frame GetComponent calls and one sight ray per ranged attacker are fine.
        void DrawUnitLabel(Health health, bool hostile)
        {
            if (health == null || !health.IsAlive || !health.gameObject.activeInHierarchy)
                return;
            var hasAttacker = health.TryGetComponent<UnitAttacker>(out var attacker);
            var text = DescribeUnit(health.name, health.Current, health.Max, hasAttacker ? attacker.Role : CombatRole.Melee);
            var cooldown = hasAttacker ? attacker.CooldownRemaining : 0f;
            health.TryGetComponent<CommandableUnit>(out var unit);
            string activity;
            if (hostile && health.TryGetComponent<EnemyAI>(out var ai))
                activity = DescribeEnemy(ai.State, ai.Target != null ? ai.Target.name : null, cooldown);
            else if (unit != null && health.TryGetComponent<CompanionAI>(out var companion))
                activity = AppendCooldown(DescribeCompanion(DescribeOrders(unit.CurrentCommand, unit.PendingCommands.Count),
                    companion.State, companion.AssistTarget != null ? companion.AssistTarget.name : null), cooldown);
            else if (unit != null)
                activity = AppendCooldown(DescribeOrders(unit.CurrentCommand, unit.PendingCommands.Count), cooldown);
            else
                activity = string.Empty;
            if (hasAttacker && attacker.NeedsLineOfSight && unit != null && unit.CurrentCommand is AttackCommand attack)
                activity = AppendSight(activity, attacker.HasLineOfSight(attack.Target));
            if (activity.Length > 0)
                text += "\n" + activity;

            var screen = viewCamera.WorldToScreenPoint(health.transform.position + Vector3.up * UnitLabelHeight);
            if (screen.z <= 0f)
                return;
            GUI.Label(new Rect(screen.x - 100f, Screen.height - screen.y - 22f, 200f, 44f), text, unitLabelStyle);
        }
```

(d) In the class summary add: `Unit labels show role, health, orders, AI state and line of sight.`

- [ ] **Step 4: Run the tests to verify they pass**

Run: `Tools/run-tests.sh EditMode` → expected `total="238" passed="238"`, `EXIT=0`.
Run: `Tools/run-tests.sh PlayMode` → expected `total="208" passed="208"`, `EXIT=0`.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/DebugUI/PrototypeHud.cs Assets/_Project/Tests/EditMode/PrototypeHudTests.cs
git commit -m "Show combat role, companion state and line of sight in the debug HUD

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 11: Prefabs, arena and scene tests

**Files:**
- Modify: `Assets/_Project/Tests/PlayMode/PrototypeSceneTests.cs`
- Create (temporary, never committed): `Assets/_Project/Editor/TacticalSceneBuilder.cs`
- Modify (generated): `Assets/_Project/Prefabs/FriendlyUnit.prefab`, `Assets/_Project/Prefabs/HostileUnit.prefab`, `Assets/_Project/Scenes/Prototype.unity`, `Assets/_Project/Scenes/Prototype/NavMesh-Environment.asset` (+`.meta`)

**Interfaces:**
- Consumes: serialized field names `CompanionAI.activeCharacter`/`encounter`, `UnitAttacker.role`/`range`/`damage`/`cooldown`, `Encounter.friendlies`/`hostiles`.
- Produces: `Prototype.unity` with `CompanionAI` on every friendly wired to `Systems`' `ActiveCharacter` and `Encounter`; `FriendlyUnit_3` and `HostileUnit_3` ranged; `HostileUnit_3` at (10, 1, 14); obstacles `Pillar_G`, `Pillar_H`, `Barrier_I`, `Crate_J`, `Crate_K`; a rebaked NavMesh.

- [ ] **Step 1: Update the scene tests**

In `Assets/_Project/Tests/PlayMode/PrototypeSceneTests.cs`:

(a) In `Scene_ContainsWiredSquadAndHostiles_AndRunsWithoutErrors`, inside the `foreach (var friendly in friendlies)` loop, after the `AttackLineView` assertion add:

```csharp
                Assert.That(friendly.GetComponent<CompanionAI>(), Is.Not.Null, $"{friendly.name} has no CompanionAI");
                Assert.That(friendly.GetComponent<CompanionAI>().IsWired, Is.True, $"{friendly.name}'s CompanionAI is not wired");
                var expectedRole = friendly.name == "FriendlyUnit_3" ? CombatRole.Ranged : CombatRole.Melee;
                Assert.That(friendly.GetComponent<UnitAttacker>().Role, Is.EqualTo(expectedRole), friendly.name);
                Assert.That(friendly.GetComponent<UnitAttacker>().Range, Is.EqualTo(expectedRole == CombatRole.Ranged ? 8f : 2f), friendly.name);
                Assert.That(friendly.GetComponent<UnitAttacker>().Damage, Is.EqualTo(expectedRole == CombatRole.Ranged ? 15 : 25), friendly.name);
```

(b) In the `foreach (var hostile in hostiles)` loop, replace the two `Damage`/`Cooldown` assertions with:

```csharp
                var ranged = hostile.name == "HostileUnit_3";
                Assert.That(hostile.GetComponent<UnitAttacker>().Role, Is.EqualTo(ranged ? CombatRole.Ranged : CombatRole.Melee), hostile.name);
                Assert.That(hostile.GetComponent<UnitAttacker>().Range, Is.EqualTo(ranged ? 8f : 2f), hostile.name);
                Assert.That(hostile.GetComponent<UnitAttacker>().Damage, Is.EqualTo(ranged ? 8 : 10), hostile.name);
                Assert.That(hostile.GetComponent<UnitAttacker>().Cooldown, Is.EqualTo(ranged ? 1.5f : 1.2f).Within(0.001f), hostile.name);
                Assert.That(hostile.GetComponent<CompanionAI>(), Is.Null, $"{hostile.name} must not have companion AI");
```

(c) After the `Obstacle_F` assertion add:

```csharp
            foreach (var obstacle in new[] { "Pillar_G", "Pillar_H", "Barrier_I", "Crate_J", "Crate_K" })
                Assert.That(GameObject.Find(obstacle), Is.Not.Null, $"{obstacle} missing");
```

(d) At the end of that test, after the hostiles-idle assertion, add:

```csharp
            var companions = FindSquad().Select(u => u.GetComponent<CompanionAI>()).ToArray();
            Assert.That(companions[0].State, Is.EqualTo(CompanionState.Controlled), "FriendlyUnit_1 is controlled");
            Assert.That(companions[1].State, Is.EqualTo(CompanionState.Idle), "FriendlyUnit_2 starts within follow distance");
            Assert.That(companions[2].State, Is.EqualTo(CompanionState.Idle), "FriendlyUnit_3 starts within follow distance");
```

(e) Add two tests to `PrototypeSceneTests`:

```csharp
        [UnityTest]
        public IEnumerator ControlledCharacterMoves_CompanionsFollow()
        {
            yield return LoadScene();
            var squad = FindSquad();
            Assert.That(unit.Issue(new MoveCommand(new Vector3(-14f, 0f, 0f))), Is.True);

            yield return TestWorld.WaitUntil(() => squad.Skip(1).All(u => u.CurrentCommand == null
                && TestWorld.HorizontalDistance(u.transform.position, unit.transform.position) < 6f), 25f);

            foreach (var companion in squad.Skip(1))
                Assert.That(TestWorld.HorizontalDistance(companion.transform.position, unit.transform.position), Is.LessThan(6f),
                    $"{companion.name} did not follow the controlled character");
        }

        [UnityTest]
        public IEnumerator Tab_ThenMove_TheOldLeaderFollowsTheNewOne()
        {
            yield return LoadScene();
            var squad = FindSquad();
            var active = Object.FindFirstObjectByType<ActiveCharacter>();
            Assert.That(active.Cycle(1), Is.True);
            Assert.That(active.Unit, Is.SameAs(squad[1]));
            Assert.That(squad[1].Issue(new MoveCommand(new Vector3(-14f, 0f, 0f))), Is.True);

            yield return TestWorld.WaitUntil(() => squad[0].CurrentCommand == null
                && TestWorld.HorizontalDistance(squad[0].transform.position, squad[1].transform.position) < 6f, 25f);

            Assert.That(TestWorld.HorizontalDistance(squad[0].transform.position, squad[1].transform.position), Is.LessThan(6f),
                "FriendlyUnit_1 must follow once FriendlyUnit_2 is controlled");
            Assert.That(squad[0].GetComponent<CompanionAI>().State, Is.Not.EqualTo(CompanionState.Controlled));
        }
```

- [ ] **Step 2: Run the scene tests to verify they fail**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.PrototypeSceneTests|Blackglass.Tests.PrototypeSceneInputTests"` → expected `EXIT=2`: the wiring test fails on `FriendlyUnit_1 has no CompanionAI`; `ControlledCharacterMoves_CompanionsFollow` and `Tab_ThenMove_...` time out (no `CompanionAI` in the scene). The others pass.

- [ ] **Step 3: Create the temporary scene builder**

Create `Assets/_Project/Editor/TacticalSceneBuilder.cs`:

```csharp
// TEMPORARY: builds the Phase 5 arena, roles and companion wiring into the prefabs and Prototype.unity, then this
// file is deleted (never committed).
using System;
using System.Linq;
using Blackglass;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

public static class TacticalSceneBuilder
{
    const string ScenePath = "Assets/_Project/Scenes/Prototype.unity";
    const string NavMeshPath = "Assets/_Project/Scenes/Prototype/NavMesh-Environment.asset";
    const string FriendlyPrefabPath = "Assets/_Project/Prefabs/FriendlyUnit.prefab";
    const string HostilePrefabPath = "Assets/_Project/Prefabs/HostileUnit.prefab";
    const string ObstacleMaterialPath = "Assets/_Project/Materials/Obstacle.mat";

    static readonly (string name, Vector3 position, Vector3 scale)[] Obstacles =
    {
        ("Pillar_G", new Vector3(-2f, 1.5f, -6f), new Vector3(1.5f, 3f, 1.5f)),
        ("Pillar_H", new Vector3(3f, 1.5f, 5f), new Vector3(1.5f, 3f, 1.5f)),
        ("Barrier_I", new Vector3(-8f, 1f, 2f), new Vector3(6f, 2f, 1f)),
        ("Crate_J", new Vector3(10f, 1f, -5f), new Vector3(2f, 2f, 2f)),
        ("Crate_K", new Vector3(1f, 1f, 11f), new Vector3(2f, 2f, 2f)),
    };

    public static void Build()
    {
        var obstacleMaterial = Require(AssetDatabase.LoadAssetAtPath<Material>(ObstacleMaterialPath), ObstacleMaterialPath);
        AddCompanionAIToFriendlyPrefab();
        ResaveHostilePrefab();

        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var systems = Require(GameObject.Find("Systems"), "Systems");
        var environment = Require(GameObject.Find("Environment"), "Environment");
        var active = Require(systems.GetComponent<ActiveCharacter>(), "ActiveCharacter on Systems");
        var encounter = Require(systems.GetComponent<Encounter>(), "Encounter on Systems");
        if (GameObject.Find("Pillar_G") != null)
            throw new Exception("Pillar_G already exists; the builder has already run.");

        foreach (var (name, position, scale) in Obstacles)
            CreateObstacle(environment, name, position, scale, obstacleMaterial);

        var rangedHostile = Require(GameObject.Find("HostileUnit_3"), "HostileUnit_3");
        rangedHostile.transform.position = new Vector3(10f, 1f, 14f);
        SetRanged(rangedHostile.GetComponent<UnitAttacker>(), 8f, 8, 1.5f);

        foreach (var name in new[] { "FriendlyUnit_1", "FriendlyUnit_2", "FriendlyUnit_3" })
        {
            var friendly = Require(GameObject.Find(name), name);
            var companion = Require(friendly.GetComponent<CompanionAI>(), $"CompanionAI on {name} (prefab update did not reach the scene)");
            SetReference(companion, "activeCharacter", active);
            SetReference(companion, "encounter", encounter);
        }
        SetRanged(Require(GameObject.Find("FriendlyUnit_3"), "FriendlyUnit_3").GetComponent<UnitAttacker>(), 8f, 15, 1f);

        var surface = Require(environment.GetComponent<NavMeshSurface>(), "NavMeshSurface on Environment");
        surface.BuildNavMesh();
        AssetDatabase.DeleteAsset(NavMeshPath);
        AssetDatabase.CreateAsset(surface.navMeshData, NavMeshPath);

        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene))
            throw new Exception("Failed to save " + ScenePath);
        AssetDatabase.SaveAssets();
        Debug.Log($"[TacticalSceneBuilder] Built the Phase 5 arena into {ScenePath}");
    }

    static void AddCompanionAIToFriendlyPrefab()
    {
        var root = PrefabUtility.LoadPrefabContents(FriendlyPrefabPath);
        try
        {
            if (root.GetComponent<CompanionAI>() != null)
                throw new Exception("FriendlyUnit already has CompanionAI; the builder has already run.");
            root.AddComponent<CompanionAI>();   // defaults; references are wired per scene instance
            PrefabUtility.SaveAsPrefabAsset(root, FriendlyPrefabPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    // Re-saving drops EnemyAI's removed sight fields from the asset.
    static void ResaveHostilePrefab()
    {
        var root = PrefabUtility.LoadPrefabContents(HostilePrefabPath);
        try
        {
            PrefabUtility.SaveAsPrefabAsset(root, HostilePrefabPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    static void SetRanged(UnitAttacker attacker, float range, int damage, float cooldown)
    {
        var serialized = new SerializedObject(Require(attacker, "UnitAttacker"));
        Property(serialized, "role").enumValueIndex = (int)CombatRole.Ranged;
        Property(serialized, "range").floatValue = range;
        Property(serialized, "damage").intValue = damage;
        Property(serialized, "cooldown").floatValue = cooldown;
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }

    static void CreateObstacle(GameObject environment, string name, Vector3 position, Vector3 scale, Material material)
    {
        var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        box.name = name;
        box.transform.SetParent(environment.transform, false);
        box.transform.position = position;
        box.transform.localScale = scale;
        box.GetComponent<Renderer>().sharedMaterial = material;
    }

    static T Require<T>(T value, string what) where T : Object =>
        value != null ? value : throw new Exception(what + " not found");

    static SerializedProperty Property(SerializedObject serialized, string field) =>
        serialized.FindProperty(field) ?? throw new Exception($"{serialized.targetObject.GetType().Name}.{field} not found");

    static void SetReference(Object target, string field, Object value)
    {
        var serialized = new SerializedObject(target);
        Property(serialized, field).objectReferenceValue = value;
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }
}
```

- [ ] **Step 4: Run the builder, then delete it**

```bash
"/c/Program Files/Unity/Hub/Editor/6000.3.25f1/Editor/Unity.exe" -batchmode -quit -projectPath "$(pwd -W)" -executeMethod TacticalSceneBuilder.Build -logFile "$(pwd -W)/Logs/BuildTacticalScene.log"; echo "EXIT=$?"
grep -E '\[TacticalSceneBuilder\]|error CS|Exception' Logs/BuildTacticalScene.log | head -20
grep -c "Blackglass.CompanionAI\|CompanionAI" Assets/_Project/Prefabs/FriendlyUnit.prefab        # expect >= 1 (the script reference by GUID; check the GUID of CompanionAI.cs.meta appears)
grep -c "sightBlockers\|eyeHeight" Assets/_Project/Prefabs/HostileUnit.prefab                     # expect 0
grep -c "m_Name: Pillar_G\|m_Name: Pillar_H\|m_Name: Barrier_I\|m_Name: Crate_J\|m_Name: Crate_K" Assets/_Project/Scenes/Prototype.unity   # expect 5
grep -c "propertyPath: role" Assets/_Project/Scenes/Prototype.unity                                 # expect 2 (FriendlyUnit_3, HostileUnit_3)
grep -c "propertyPath: activeCharacter" Assets/_Project/Scenes/Prototype.unity                      # expect 3
git status --short                                                                                  # prefabs, scene, NavMesh asset, Editor/ (uncommitted)
```

**Expected:** `EXIT=0`; the log contains `[TacticalSceneBuilder] Built the Phase 5 arena into Assets/_Project/Scenes/Prototype.unity` and no exceptions; the counts match. (Script references in YAML are GUIDs: compare `grep guid Assets/_Project/Scripts/AI/CompanionAI.cs.meta` with the prefab if the first count reads 0.)

Then delete the builder:

```bash
rm -r Assets/_Project/Editor Assets/_Project/Editor.meta
```

- [ ] **Step 5: Run all tests to verify they pass**

Run: `Tools/run-tests.sh PlayMode` → expected `total="210" passed="210"`, `EXIT=0`. This run also recompiles without the deleted builder.
Run: `Tools/run-tests.sh EditMode` → expected `total="238" passed="238"`, `EXIT=0`.

Troubleshooting:
- `Scene_Contains...` fails on `FriendlyUnit_2 starts within follow distance` with `Follow`: the squad spawns at x −12, −10, −8 (2 and 4 m from the leader, under the 6 m start distance); check the spawn positions were not moved.
- Hostiles not idle at start: `HostileUnit_3` at (10, 1, 14) is 26 m from the squad; check its position and that `detectionRange` is still 12.
- `ControlledCharacterMoves_CompanionsFollow` times out: (−14, 0, 0) must be on the NavMesh (the ground spans ±20; `Barrier_I` spans x −11…−5 at z 1.5…2.5, clear of it). Check the NavMesh asset was rebaked (its file changed).
- `Squad_AttacksEveryHostile_AndWins` slower or failing: the ranged hostile now fires at 8 m; the squad still has 300 hit points against 180. Check its damage is 8, not 80.

- [ ] **Step 6: Commit**

```bash
git add -A Assets/_Project/Scenes Assets/_Project/Prefabs Assets/_Project/Tests/PlayMode/PrototypeSceneTests.cs
git status --short   # must NOT list Assets/_Project/Editor
git commit -m "Build the roles, companions and sight arena into the prototype scene

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 12: Decision records and final verification

**Files:**
- Modify: `Docs/Decisions.md`
- Modify: `Docs/superpowers/specs/2026-10-04-tactical-combat-roles-design.md` (status line only)

- [ ] **Step 1: Amend decisions 014 and 015**

In `Docs/Decisions.md`:

(a) In **014**, replace `Three deciders feed it: player clicks (`PlayerCommandInput`, unchanged), `EnemyAI` (015) and `AutoRetaliate`:` with `Four deciders feed it: player clicks (`PlayerCommandInput`, unchanged), `EnemyAI` (015), `CompanionAI` (018, Phase 5) and `AutoRetaliate`:` and replace `` `CommandableUnit` chases, stops in range and faces; `` with `` `CommandableUnit` approaches, repositions when a ranged unit is blind (017), stops in range and faces; ``.

(b) In **015**, replace `**and** in line of sight: one ray from the hostile's eye (1.5 m) to the friendly's centre, blocked by any collider without a `Health` in its parents. Units never block sight.` with `**and** in line of sight (`UnitAttacker.HasLineOfSight`, built on `LineOfSight`, 016: one ray from the hostile's eye at 1.5 m to the friendly's centre, blocked by any collider without a `Health` in its parents; units never block sight) **and reachable** (`UnitMover.CanReach`, a complete NavMesh path; added in Phase 5, 2026-10-04).` and replace the implication sentence `An unreachable but visible friendly is re-chased every tick (the hostile stands at the closest point).` with `An unreachable friendly is not a target (until Phase 5 it was re-chased every tick).` and `Perception can grow inside `EnemyAI.FindNearestVisibleFriendly` without touching combat.` with `Perception can grow inside `EnemyAI.FindTarget` without touching combat.`

- [ ] **Step 2: Add decisions 016, 017 and 018**

Append to `Docs/Decisions.md`:

```markdown

## 016 — Combat roles and line of sight

- **Decided (Phase 5, 2026-10-04):** `UnitAttacker` carries a `CombatRole` (`Melee`, `Ranged`) next to its range, damage and cooldown; scene instances override the prefab values. `LineOfSight` is the one sight test: a ray from an eye (pivot + 0.5 m, so 1.5 m up) to the target's pivot, blocked by any collider without a `Health` in its parents; units never block. `UnitAttacker.HasLineOfSight`/`CanAttack` wrap it for the unit's own attacks, and `TryAttack` refuses a ranged hit without sight, so no caller can shoot through a wall. Melee ignores sight for hitting; both roles use it for acquisition (`EnemyAI`). A ranged unit stops approaching once in range and never backs away.
- **Why:** two roles and four numbers do not justify a data asset or class system; one sight function shared by attacks, firing positions and enemy acquisition means a change lands once; gating the hit itself keeps the rule out of every decider.
- **Rejected:** a `ScriptableObject` per role or a class system (speculative data); sight checks per caller (duplicated, drift); kiting or minimum range (out of scope, keeps melee hostiles dangerous).
- **Implications:** weapons later become data that fills the same four fields plus the role. The 8-hit buffer cap carries over from 015. Sight is one ray to the pivot, so a half-exposed unit is either seen or not.

## 017 — Repositioning

- **Decided (Phase 5, 2026-10-04):** an attack order runs in three phases inside `CommandableUnit` (`AttackPhase`): **Approach** while out of range (unchanged chase: re-path when the target moves 0.5 m, arriving out of range means unreachable), **Reposition** while a ranged unit is in range but blind, **Attack** otherwise. Reposition searches at most twice a second (`RepositionInterval` 0.5 s) among 16 candidates around the unit's ground point (`FiringPositionFinder`: 8 compass points at 2 m and 4 m, nearest ring first), keeping the first that snaps to the NavMesh (the 2 m `SnapRadius`), is in range and in sight from the eye a unit would have there, and is reachable; the unit walks to the snapped point. A walk that has not arrived after 3 s is searched again. After three repositions without landing a hit, or when no candidate is valid, the unit **walks at the target** until the line clears; at a reachable target's position sight is trivially clear. If that walk ends with the unit still blind (partial path), or no walkable point lies within 2 m of the target, the order **ends** as Approach's does for an unreachable target, so every attack order terminates. Sight is re-checked every frame, so the walk ends as soon as the line clears.
- **Why:** the brief asked for a modest, predictable search; the fallback plus the two unreachable exits bound the loop without a planner; keeping the phases in `CommandableUnit` means every decider (player, enemy, companion, retaliation) gets the behaviour for free.
- **Rejected:** candidates around the target or scored by cover (a cover system is Phase 6 or later); searching every frame (16 snaps, rays and paths per blind unit); giving up when no candidate is valid (a ranged unit would stand uselessly behind a wide wall); a 1 m snap radius (candidates in the 0.5 m erosion band beside walls would be rejected exactly where repositioning is needed).
- **Implications:** the search may pick an exposed spot, or one on the far side of an obstacle (nearest by ring, not by travel); cover value would plug into the candidate predicate. The fallback has no standoff, so a ranged unit may end up firing from melee range. `EnemyState` gained `Reposition`, derived from the phase.

## 018 — Companion autonomy

- **Decided (Phase 5, 2026-10-04):** `CompanionAI` (friendlies only, wired to `ActiveCharacter` and `Encounter`, `DefaultExecutionOrder(-150)`) checks every simulation frame whether it is the controlled character (then it stops its own order once and does nothing else) and otherwise thinks every 0.25 s with this priority: **dead** (nothing), **controlled**, **orders** (a current order it did not issue, or anything pending: left alone), **assist** (attack a hostile within 10 m that is the controlled character's target or is already attacking someone; the leader's target first, then the nearest engaged; acquisition only, a running assist is never retargeted), **follow** (beyond 6 m of the controlled character, move to 3.5 m from it on the companion's own side; a running follow move is re-aimed once the leader has moved 2 m and only while the companion is still farther than 3.5 m, so a leader walking into it never pushes it back; the point is snapped to the NavMesh first, else the leader's position), **idle**. It tells its own orders from everyone else's by **remembering the command object it issued**; `Replace` by anyone, `Append` (pending orders), `Stop`, death and direct control all read as "not ours". It only ever `Replace`s or `Stop`s its own order. `CompanionState` is derived on read.
- **Why:** the brief's priority model, implemented as two small methods; command identity needs no change to command data (006) and treats a retaliation like an explicit order (never interrupted by following); requiring an engaged target means companions never start a fight the player did not; the two follow distances and the no-backstep rule stop pacing; running the controlled check before `DirectControlInput` keeps 012's "a held key carries over to an idle unit" true on Tab.
- **Rejected:** a `Source` tag on `UnitCommand` (project-wide change for one consumer); attacking any visible hostile (wakes sleeping enemies); retargeting a running assist to the leader's new target (flip-flopping between engaged hostiles); formations or slots (out of scope); a leash on assist (the player can always order the companion back); interrupting the current follow move when the player queues behind it (the queue semantics of 006 are kept: the queued order runs after it).
- **Implications:** a Shift-queued order waits for a short follow move. Autonomy never overrides explicit orders, even for self-preservation. The controlled character has no autonomy at all; Tab moves that status with it. Later AI (scripts, allied NPCs) can reuse the same component or the same "own command" trick.
```

- [ ] **Step 3: Update the spec status**

In `Docs/superpowers/specs/2026-10-04-tactical-combat-roles-design.md`, change the `Status:` on line 3 to `Status: implemented 2026-10-04 (see Docs/superpowers/plans/2026-10-04-tactical-combat-roles.md)`.

- [ ] **Step 4: Final verification**

```bash
git status --short                 # only the two docs
ls Assets/_Project/Editor 2>/dev/null && echo "BUILDER STILL PRESENT" || echo "builder gone"
grep -rn "sightBlockers\|eyeHeight" Assets/_Project/Scripts/AI/EnemyAI.cs ; echo "(expect no matches)"
Tools/run-tests.sh EditMode        # expected total="238" passed="238" EXIT=0
Tools/run-tests.sh PlayMode        # expected total="210" passed="210" EXIT=0
```

- [ ] **Step 5: Commit**

```bash
git add Docs/Decisions.md Docs/superpowers/specs/2026-10-04-tactical-combat-roles-design.md
git commit -m "Record the combat roles, repositioning and companion decisions

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```
