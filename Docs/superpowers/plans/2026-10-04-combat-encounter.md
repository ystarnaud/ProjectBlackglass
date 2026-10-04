# Combat Encounter (Phase 4) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Three friendly units fight three hostile units in `Prototype.unity`: health, timed attacks, death, retaliation, a tiny enemy AI with detection radius and line of sight, clean hand-over when the controlled character dies, and victory/defeat text, all obeying tactical pause.

**Architecture:**
- One combat path. Everything that attacks issues an `AttackCommand` through `CommandableUnit.Issue`; `CommandableUnit` chases and stops, `UnitAttacker` applies range, cooldown and damage, `Health` dies once and deactivates. `EnemyAI` and `AutoRetaliate` only decide *what* to attack.
- `SimulationTime.IsRunning` (scaled delta time and time scale both above zero) is the one check every simulation `Update` makes, so nothing advances on the pause frame.
- `Encounter` holds the two sides as lists of `Health` and computes the outcome on read. `ActiveCharacter` cycles to the next eligible friendly when its unit stops being eligible.

**Tech Stack:** Unity 6000.3.25f1 (Unity 6.3 LTS), URP 17.3.0, Input System 1.20.0, AI Navigation 2.0.14, Unity Test Framework 1.6.0 (NUnit), `InputTestFixture`.

**Spec:** `Docs/superpowers/specs/2026-10-04-combat-encounter-design.md`

## Global Constraints

- **Unity Editor:** exactly 6000.3.25f1 at `C:\Program Files\Unity\Hub\Editor\6000.3.25f1\Editor\Unity.exe`.
  - **The Editor must be closed during every batch-mode run** (only one instance can open the project). If a run prints "The Unity Editor has this project open", stop and ask the owner to close it.
- **Code location:** runtime code in `Assets/_Project/Scripts/` (assembly `Blackglass`, namespace `Blackglass`); tests in `Assets/_Project/Tests/EditMode/` and `Assets/_Project/Tests/PlayMode/` (namespace `Blackglass.Tests`). No new assemblies, assembly references or packages. Input actions asset unchanged.
- **Commands are data** (decision 006). Attacks happen only through `CommandableUnit.Issue(new AttackCommand(target))`. Nothing calls `UnitAttacker.TryAttack` or `Health.TakeDamage` except `CommandableUnit`/`UnitAttacker` (tests may call `TakeDamage` to stage deaths).
- **Input components** never touch `UnitMover`, `UnitAttacker`, NavMeshAgents or `Health`.
- **Pause ownership:** only `TacticalPause` writes `Time.timeScale` in game code. Tests may reset it to 1 in `TearDown`. Simulation code uses scaled time and checks `SimulationTime.IsRunning`; camera, input, HUD use unscaled time.
- **Wiring:** serialized Inspector references only. No singletons, no `Find*`/`Camera.main` in game code, no static mutable state. Components expose `internal Initialize(...)` for tests, called while the host GameObject is inactive or before its first `Update`.
- **Lifecycle:** `OnEnable`/`OnDisable`/`Awake` are not relied on in EditMode tests (they are not invoked there); behaviour that needs them is tested in PlayMode.
- **Visuals:** debug only. IMGUI text, primitives with placeholder materials. Debug objects (attack line, corpse marker) have **no colliders**. Each unit has exactly one collider (its capsule).
- **Scope:** no armour, damage types, regeneration, attributes, status effects, inventory, equipment, abilities, cover, perception cones, target memory, leash, squad AI, factions, dialogue, save/load, production UI/VFX/audio, restart flow, new packages. Phase 5 is not started.
- **Git:** work on local branch `prototype/combat-encounter` (exists; spec committed). Never push or merge. Commit after each task. Messages are sentence case and imperative, and end with the line `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`. Rename assets with `git mv` **together with their `.meta`**; commit `.meta` files with their assets. Never commit `Assets/_Project/Editor/`.
- **Test runs:** always `Tools/run-tests.sh`, 10-minute timeout for PlayMode runs. Scene tests fail on any logged error or exception.
- **Writing style:** XML summary on every public type; comments explain why, not what.

### Running tests

```bash
Tools/run-tests.sh EditMode                                        # all EditMode tests
Tools/run-tests.sh PlayMode                                        # all PlayMode tests
Tools/run-tests.sh PlayMode Blackglass.Tests.EnemyAIPlayModeTests  # one fixture (any -testFilter expression)
```

The script prints `error CS` lines, a summary (`result= total= passed= failed=`), failed test names and messages, and `EXIT=<code>`: **0** all passed, **2** some failed, **3** could not run (usually a compile error). A "red" step is usually **EXIT=3 with `error CS0117`/`CS1061`/`CS1501`/`CS0246`** because new members do not exist yet.

**Baseline before this plan (measured 2026-10-04):** EditMode 162/162, PlayMode 132/132.

**Expected totals after each task:**

| After task | EditMode | PlayMode |
|---|---|---|
| 1 | 162 | 135 |
| 2 | 168 | 137 |
| 3 | 170 | 138 |
| 4 | 170 | 145 |
| 5 | 170 | 148 |
| 6 | 170 | 151 |
| 7 | 183 | 151 |
| 8 | 187 | 165 |
| 9 | 197 | 170 |
| 10 | 208 | 170 |
| 11 | 208 | 172 |
| 12 | 208 | 172 |

These totals assume each task adds exactly the tests listed. If a task's count differs, correct this table in that task's commit; the sequence must stay green either way.

## Review Focus

1. **Space pressed on the very frame a cooldown elapses** → no damage lands on that frame; the hit arrives after resume. Pinned in Task 1 (`AttackIssuedAndPausedInTheSameEarlyUpdate_DealsNoDamageUntilResume`).
2. **A hit that kills its victim** → the dead victim must not retaliate (no order on a corpse, no exception). Pinned in Task 4 (`KillingBlow_DoesNotMakeTheVictimRetaliate`).
3. **A visible but unreachable friendly** (ringed by low walls) → the hostile re-chases without errors and never throws or logs. Pinned in Task 8 (`UnreachableVisibleFriendly_IsRechasedWithoutErrors`).
4. **A destroyed unit in an encounter list** → counts as dead; HUD, AI and outcome keep working. Pinned in Task 7 (`LivingCounts_IgnoreNullAndDestroyedEntries`) and Task 8 (`DestroyedFriendlyInTheList_IsIgnored`).
5. **The controlled character dies while W is held and the next friendly has orders** → control passes, the new unit keeps its orders until W is released and pressed again. Pinned in Task 9 (`ControlledCharacterDies_WhileWIsHeld_NextFriendlyKeepsItsOrders`).

---

### Task 1: `SimulationTime` and the pause frame

**Files:**
- Create: `Assets/_Project/Scripts/GameTime/SimulationTime.cs`
- Modify: `Assets/_Project/Scripts/Units/CommandableUnit.cs` (`Update`), `Assets/_Project/Scripts/Units/UnitMover.cs` (`Steer`), `Assets/_Project/Scripts/Combat/HitFlash.cs` (`Update`)
- Test: `Assets/_Project/Tests/PlayMode/SimulationTimeTests.cs` (new)

**Interfaces:**
- Produces: `public static class SimulationTime { public static bool IsRunning { get; } }`.

- [ ] **Step 1: Write the failing tests**

Create `Assets/_Project/Tests/PlayMode/SimulationTimeTests.cs`:

```csharp
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class SimulationTimeTests
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
        public IEnumerator IsRunning_WhileSimulationAdvances()
        {
            yield return null;
            Assert.That(SimulationTime.IsRunning, Is.True);
        }

        [UnityTest]
        public IEnumerator IsRunning_FalseOnThePauseFrame_WhilePaused_AndTrueAgainAfterResume()
        {
            var pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            yield return null;

            pause.Pause();
            Assert.That(Time.deltaTime, Is.GreaterThan(0f), "Precondition: deltaTime still holds this frame's value on the pause frame");
            Assert.That(SimulationTime.IsRunning, Is.False, "Simulation must not run on the frame it was paused");

            yield return null;
            Assert.That(SimulationTime.IsRunning, Is.False, "Simulation must not run while paused");

            pause.Resume();
            yield return null;
            yield return null;
            Assert.That(SimulationTime.IsRunning, Is.True, "Simulation must run again after resume");
        }

        // Runs before every unit's Update in the same frame, like an input callback that pauses the game: on that
        // frame Time.deltaTime is still positive while Time.timeScale is already zero.
        [DefaultExecutionOrder(-1000)]
        sealed class EarlyUpdatePauser : MonoBehaviour
        {
            public TacticalPause Pause;
            public CommandableUnit Unit;
            public Health Target;
            public bool Fire;

            void Update()
            {
                if (!Fire)
                    return;
                Fire = false;
                Unit.Issue(new AttackCommand(Target));
                Pause.Pause();
            }
        }

        [UnityTest]
        public IEnumerator AttackIssuedAndPausedInTheSameEarlyUpdate_DealsNoDamageUntilResume()
        {
            world.CreateEnvironment();
            var pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            var unit = world.CreateUnit(new Vector3(0f, 0f, 0f));
            var dummy = world.CreateDummy(new Vector3(0f, 0f, 1.5f));
            var pauser = world.Track(new GameObject("Pauser")).AddComponent<EarlyUpdatePauser>();
            pauser.Pause = pause;
            pauser.Unit = unit;
            pauser.Target = dummy;
            yield return null;

            pauser.Fire = true;
            yield return null;   // the pauser issues the attack and pauses before the unit's Update runs
            yield return null;

            Assert.That(unit.CurrentCommand, Is.TypeOf<AttackCommand>(), "The order must be accepted while paused");
            Assert.That(dummy.Current, Is.EqualTo(dummy.Max), "Damage landed on the pause frame");

            pause.Resume();
            yield return TestWorld.WaitUntil(() => dummy.Current < dummy.Max, 2f);
            Assert.That(dummy.Current, Is.EqualTo(75), "The hit did not land after resume");
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `Tools/run-tests.sh PlayMode Blackglass.Tests.SimulationTimeTests`
Expected: `EXIT=3` with `error CS0103: The name 'SimulationTime' does not exist`.

- [ ] **Step 3: Create `SimulationTime` and use it**

Create `Assets/_Project/Scripts/GameTime/SimulationTime.cs`:

```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Whether gameplay simulation advances this frame. Time.deltaTime still holds the previous value on the frame
    /// TacticalPause sets the time scale to 0, so both are checked; this is the one test every simulation Update
    /// makes. Camera, input and debug UI do not use it: they run on unscaled time.
    /// </summary>
    public static class SimulationTime
    {
        public static bool IsRunning => Time.deltaTime > 0f && Time.timeScale > 0f;
    }
}
```

In `Assets/_Project/Scripts/Units/CommandableUnit.cs`, in `Update`, replace

```csharp
            // Orders and steering only advance while simulation time advances (tactical pause sets timeScale to 0).
            if (Time.deltaTime <= 0f)
                return;
```

with

```csharp
            // Orders and steering only advance while simulation time advances (tactical pause sets timeScale to 0).
            if (!SimulationTime.IsRunning)
                return;
```

In `Assets/_Project/Scripts/Units/UnitMover.cs`, in `Steer`, replace

```csharp
            // Time.deltaTime keeps last frame's value on the frame the game is paused, so check the time scale too.
            var deltaTime = Time.deltaTime;
            if (deltaTime <= 0f || Time.timeScale <= 0f || !Agent.isOnNavMesh)
                return;
```

with

```csharp
            if (!SimulationTime.IsRunning || !Agent.isOnNavMesh)
                return;
            var deltaTime = Time.deltaTime;
```

In `Assets/_Project/Scripts/Combat/HitFlash.cs`, in `Update`, replace

```csharp
            if (remaining <= 0f)
                return;
```

with

```csharp
            if (remaining <= 0f || !SimulationTime.IsRunning)
                return;
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `Tools/run-tests.sh PlayMode Blackglass.Tests.SimulationTimeTests` → expected `total="3" passed="3"`, `EXIT=0`.
Run: `Tools/run-tests.sh PlayMode` → expected `total="135" passed="135"`, `EXIT=0`.
Run: `Tools/run-tests.sh EditMode` → expected `total="162" passed="162"`, `EXIT=0`.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/GameTime/SimulationTime.cs Assets/_Project/Scripts/GameTime/SimulationTime.cs.meta \
  Assets/_Project/Scripts/Units/CommandableUnit.cs Assets/_Project/Scripts/Units/UnitMover.cs Assets/_Project/Scripts/Combat/HitFlash.cs \
  Assets/_Project/Tests/PlayMode/SimulationTimeTests.cs Assets/_Project/Tests/PlayMode/SimulationTimeTests.cs.meta
git commit -m "Stop simulation on the pause frame with a shared SimulationTime check

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

(The `.meta` files are generated by the test run. If one is missing, run `Tools/run-tests.sh EditMode` once more and add it.)

---

### Task 2: `Health` knows its attacker; `UnitAttacker` reports hits and cooldown

**Files:**
- Modify: `Assets/_Project/Scripts/Combat/Health.cs`, `Assets/_Project/Scripts/Units/UnitAttacker.cs`
- Test: `Assets/_Project/Tests/EditMode/HealthTests.cs`, `Assets/_Project/Tests/PlayMode/CombatPlayModeTests.cs` (new)

**Interfaces:**
- Produces: `Health.TakeDamage(int amount, Health attacker = null)`; `public event Action<Health> Health.AttackedBy` (raised after `Damaged`, before `Died`, only when `attacker` is non-null and the hit was applied); `internal void Health.Initialize(int maximum)`. `UnitAttacker`: `public event Action<Health> Attacked` (raised with the target after each hit), `public float CooldownRemaining { get; }`, `internal void Initialize(float attackRange, int attackDamage, float attackCooldown)`; `TryAttack` passes the attacker's own `Health` (if any) to `TakeDamage`.

- [ ] **Step 1: Write the failing EditMode tests**

Append inside the `HealthTests` class in `Assets/_Project/Tests/EditMode/HealthTests.cs` (before the final closing braces):

```csharp
        Health CreateAttacker(out GameObject host)
        {
            host = new GameObject("Attacker");
            return host.AddComponent<Health>();
        }

        [Test]
        public void TakeDamage_WithAnAttacker_RaisesAttackedBy_AfterDamaged()
        {
            var attacker = CreateAttacker(out var attackerHost);
            var order = new List<string>();
            health.Damaged += _ => order.Add("damaged");
            health.AttackedBy += by => order.Add(by == attacker ? "attackedBy" : "wrongAttacker");

            health.TakeDamage(10, attacker);

            Assert.That(order, Is.EqualTo(new[] { "damaged", "attackedBy" }));
            Assert.That(health.Current, Is.EqualTo(90));
            Object.DestroyImmediate(attackerHost);
        }

        [Test]
        public void TakeDamage_WithoutAnAttacker_DoesNotRaiseAttackedBy()
        {
            var attacked = 0;
            health.AttackedBy += _ => attacked++;
            health.TakeDamage(10);
            Assert.That(attacked, Is.EqualTo(0));
            Assert.That(damaged, Is.EqualTo(new[] { 10 }));
        }

        [Test]
        public void TakeDamage_ZeroOrOnADeadTarget_DoesNotRaiseAttackedBy()
        {
            var attacker = CreateAttacker(out var attackerHost);
            var attacked = 0;
            health.AttackedBy += _ => attacked++;

            health.TakeDamage(0, attacker);
            Assert.That(attacked, Is.EqualTo(0), "Zero damage is not a hit");

            health.TakeDamage(health.Max, attacker);
            Assert.That(attacked, Is.EqualTo(1), "The killing blow is a hit");
            health.TakeDamage(5, attacker);
            Assert.That(attacked, Is.EqualTo(1), "A dead target is not hit again");
            Object.DestroyImmediate(attackerHost);
        }

        [Test]
        public void TakeDamage_KillingBlow_RaisesAttackedByBeforeDied()
        {
            var attacker = CreateAttacker(out var attackerHost);
            var order = new List<string>();
            health.AttackedBy += _ => order.Add("attackedBy");
            health.Died += () => order.Add("died");

            health.TakeDamage(health.Max, attacker);

            Assert.That(order, Is.EqualTo(new[] { "attackedBy", "died" }));
            Object.DestroyImmediate(attackerHost);
        }

        [Test]
        public void TakeDamage_KillingBlow_IsAlreadyDeadInsideAttackedBy()
        {
            var attacker = CreateAttacker(out var attackerHost);
            bool? aliveDuringCallback = null;
            health.AttackedBy += _ => aliveDuringCallback = health.IsAlive;

            health.TakeDamage(health.Max, attacker);

            Assert.That(aliveDuringCallback, Is.False, "Handlers of the killing blow must see a dead target");
            Object.DestroyImmediate(attackerHost);
        }

        [Test]
        public void Initialize_SetsTheMaximum_AndRestoresFullHealth()
        {
            health.TakeDamage(30);
            health.Initialize(40);
            Assert.That(health.Max, Is.EqualTo(40));
            Assert.That(health.Current, Is.EqualTo(40));
            Assert.That(health.IsAlive, Is.True);
        }
```

(6 new tests. The file already has `using System.Collections.Generic;`.)

- [ ] **Step 2: Write the failing PlayMode tests**

Create `Assets/_Project/Tests/PlayMode/CombatPlayModeTests.cs`:

```csharp
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CombatPlayModeTests
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
        public IEnumerator Attack_RaisesAttackedWithTheTarget_AndTellsTheTargetWhoHitIt()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(0f, 0f, 0f));
            var ownHealth = unit.gameObject.AddComponent<Health>();
            var attacker = unit.GetComponent<UnitAttacker>();
            var dummy = world.CreateDummy(new Vector3(0f, 0f, 1.5f));
            var hits = new List<Health>();
            var hitBy = new List<Health>();
            attacker.Attacked += hits.Add;
            dummy.AttackedBy += hitBy.Add;
            yield return null;

            unit.Issue(new AttackCommand(dummy));
            yield return TestWorld.WaitUntil(() => dummy.Current < dummy.Max, 2f);

            Assert.That(hits, Is.EqualTo(new[] { dummy }), "Attacked must fire once per hit with the target");
            Assert.That(hitBy, Is.EqualTo(new[] { ownHealth }), "The target must learn which Health hit it");
        }

        [UnityTest]
        public IEnumerator CooldownRemaining_IsZeroWhenReady_AndCountsDownAfterAHit()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(0f, 0f, 0f));
            var attacker = unit.GetComponent<UnitAttacker>();
            var dummy = world.CreateDummy(new Vector3(0f, 0f, 1.5f));
            yield return null;
            Assert.That(attacker.CooldownRemaining, Is.EqualTo(0f));

            unit.Issue(new AttackCommand(dummy));
            yield return TestWorld.WaitUntil(() => dummy.Current < dummy.Max, 2f);
            var justAfterHit = attacker.CooldownRemaining;
            // (t + 1f) - t is 1 up to float rounding, so allow a tolerance.
            Assert.That(justAfterHit, Is.EqualTo(1f).Within(0.01f), "A fresh hit must leave about a full cooldown");

            yield return new WaitForSeconds(0.4f);
            Assert.That(attacker.CooldownRemaining, Is.LessThan(justAfterHit - 0.3f));

            unit.Issue(new StopCommand());
            yield return new WaitForSeconds(0.7f);
            Assert.That(attacker.CooldownRemaining, Is.EqualTo(0f));
        }
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `Tools/run-tests.sh EditMode Blackglass.Tests.HealthTests` → expected `EXIT=3` with `error CS1061` (`AttackedBy`, `Initialize`) and `CS1501` (`TakeDamage` with 2 arguments).
Run: `Tools/run-tests.sh PlayMode Blackglass.Tests.CombatPlayModeTests` → expected `EXIT=3` with `error CS1061` (`Attacked`, `CooldownRemaining`).

- [ ] **Step 4: Implement**

Replace `Assets/_Project/Scripts/Combat/Health.cs` with:

```csharp
using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>Hit points for anything that can be attacked. Dies once, at zero, and by default deactivates then.</summary>
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
        /// <summary>
        /// Raised with the attacker, after Damaged, whenever a living target takes damage from a known attacker.
        /// Also raised for the killing blow (before Died); IsAlive is already false by then, so handlers that must not
        /// act on a corpse check it.
        /// </summary>
        public event Action<Health> AttackedBy;
        /// <summary>Raised exactly once, when hit points reach zero.</summary>
        public event Action Died;

        internal void Initialize(int maximum)
        {
            if (maximum < 1)
                throw new ArgumentOutOfRangeException(nameof(maximum), maximum, "Maximum health must be at least 1.");
            max = maximum;
            damageTaken = 0;
            hasDied = false;
        }

        /// <summary>Applies damage. The attacker, when given, is reported through AttackedBy so the target can respond.</summary>
        public void TakeDamage(int amount, Health attacker = null)
        {
            if (amount < 0)
                throw new ArgumentOutOfRangeException(nameof(amount), amount, "Damage cannot be negative.");
            if (hasDied || amount == 0)
                return;

            damageTaken = (int)Math.Min((long)max, (long)damageTaken + amount);
            // Death is decided before any handler runs, so IsAlive is already false inside Damaged and AttackedBy for
            // the killing blow. A handler that re-enters TakeDamage then returns at the hasDied guard above.
            var dies = damageTaken >= max;
            if (dies)
                hasDied = true;

            Damaged?.Invoke(amount);
            if (attacker != null)
                AttackedBy?.Invoke(attacker);

            if (!dies)
                return;
            Died?.Invoke();
            if (disableOnDeath)
                gameObject.SetActive(false);
        }
    }
}
```

Replace `Assets/_Project/Scripts/Units/UnitAttacker.cs` with:

```csharp
using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Prototype melee attack: fixed range, damage and cooldown. Cooldown uses scaled time, so it freezes while
    /// paused. Reports each hit through Attacked and tells the target which Health hit it.
    /// </summary>
    public sealed class UnitAttacker : MonoBehaviour
    {
        [SerializeField, Min(0.1f)] float range = 2f;
        [SerializeField, Min(0)] int damage = 25;
        [SerializeField, Min(0f)] float cooldown = 1f;

        float nextAttackTime;
        Health ownHealth;

        public float Range => range;
        public int Damage => damage;
        public float Cooldown => cooldown;

        /// <summary>Seconds of scaled time until the next hit may land; 0 when ready.</summary>
        public float CooldownRemaining => Mathf.Max(0f, nextAttackTime - Time.time);

        /// <summary>Raised with the target after every hit.</summary>
        public event Action<Health> Attacked;

        // This unit's own Health, if it has one. Looked up lazily so a Health added after this component is found.
        Health OwnHealth => ownHealth != null ? ownHealth : ownHealth = GetComponent<Health>();

        internal void Initialize(float attackRange, int attackDamage, float attackCooldown)
        {
            range = attackRange;
            damage = attackDamage;
            cooldown = attackCooldown;
        }

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
            target.TakeDamage(damage, OwnHealth);
            Attacked?.Invoke(target);
            return true;
        }
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `Tools/run-tests.sh EditMode` → expected `total="168" passed="168"`, `EXIT=0`. (162 + 6.)
Run: `Tools/run-tests.sh PlayMode` → expected `total="137" passed="137"`, `EXIT=0`.

- [ ] **Step 6: Commit**

```bash
git add Assets/_Project/Scripts/Combat/Health.cs Assets/_Project/Scripts/Units/UnitAttacker.cs \
  Assets/_Project/Tests/EditMode/HealthTests.cs Assets/_Project/Tests/PlayMode/CombatPlayModeTests.cs Assets/_Project/Tests/PlayMode/CombatPlayModeTests.cs.meta
git commit -m "Tell a Health who hit it and report hits and cooldown from UnitAttacker

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 3: A dead `CommandableUnit` drops its orders and refuses new ones

**Files:**
- Modify: `Assets/_Project/Scripts/Units/CommandableUnit.cs`
- Modify: `Assets/_Project/Tests/PlayMode/TestSupport/TestWorld.cs` (add `CreateFighter`)
- Test: `Assets/_Project/Tests/EditMode/CommandableUnitTests.cs`, `Assets/_Project/Tests/PlayMode/CombatPlayModeTests.cs`

**Interfaces:**
- Produces: `public bool CommandableUnit.IsAlive` (true without a `Health`); `Issue` returns false for every command when dead; on `Health.Died` the unit clears all orders and stops. `TestWorld.CreateFighter(Vector3 groundPosition, int maxHealth = 100, int damage = 25, float cooldown = 1f)` returning `CommandableUnit`: a `CreateUnit`-style capsule with `Health`, assembled while inactive so `OnEnable` sees every component.

- [ ] **Step 1: Write the failing EditMode tests**

Append inside the `CommandableUnitTests` class in `Assets/_Project/Tests/EditMode/CommandableUnitTests.cs`:

```csharp
        [Test]
        public void IsAlive_WithoutHealth_IsTrue()
        {
            Assert.That(unit.IsAlive, Is.True);
        }

        [Test]
        public void Issue_OnADeadUnit_IsRejectedForEveryCommand()
        {
            var own = unitHost.AddComponent<Health>();
            own.TakeDamage(own.Max);
            Assert.That(unit.IsAlive, Is.False);

            Assert.That(unit.Issue(new AttackCommand(target)), Is.False);
            Assert.That(unit.Issue(new MoveCommand(Vector3.one)), Is.False);
            Assert.That(unit.Issue(new StopCommand()), Is.False);
            Assert.That(unit.Issue(new AttackCommand(target), IssueMode.Append), Is.False);
            Assert.That(unit.CurrentCommand, Is.Null);
            Assert.That(unit.PendingCommands, Is.Empty);
        }
```

- [ ] **Step 2: Add `CreateFighter` to `TestWorld` and write the failing PlayMode test**

In `Assets/_Project/Tests/PlayMode/TestSupport/TestWorld.cs`, add after `CreateFriendly`:

```csharp
        /// <summary>
        /// A unit with Health that can fight and die. Assembled while inactive so every component's OnEnable sees
        /// the others (CommandableUnit subscribes to its Health there).
        /// </summary>
        public CommandableUnit CreateFighter(Vector3 groundPosition, int maxHealth = 100, int damage = 25, float cooldown = 1f)
        {
            var host = Track(GameObject.CreatePrimitive(PrimitiveType.Capsule));
            host.name = "TestFighter";
            host.SetActive(false);
            host.transform.position = groundPosition + Vector3.up;
            host.AddComponent<UnitMover>();
            host.GetComponent<NavMeshAgent>().baseOffset = 1f;
            host.AddComponent<UnitAttacker>().Initialize(2f, damage, cooldown);
            host.AddComponent<Health>().Initialize(maxHealth);
            var unit = host.AddComponent<CommandableUnit>();
            host.SetActive(true);
            return unit;
        }
```

Append inside `CombatPlayModeTests`:

```csharp
        [UnityTest]
        public IEnumerator UnitKilled_DropsItsOrders_StopsMoving_AndRefusesNewOnes()
        {
            world.CreateEnvironment();
            var unit = world.CreateFighter(new Vector3(-8f, 0f, 0f));
            var dummy = world.CreateDummy(new Vector3(8f, 0f, 0f));
            yield return null;
            unit.Issue(new MoveCommand(new Vector3(8f, 0f, 4f)));
            unit.Issue(new AttackCommand(dummy), IssueMode.Append);
            yield return new WaitForSeconds(0.3f);
            Assert.That(unit.PendingCommands, Has.Count.EqualTo(1), "Precondition: a pending order exists");

            var health = unit.GetComponent<Health>();
            health.TakeDamage(health.Max);
            yield return null;

            Assert.That(unit.IsAlive, Is.False);
            Assert.That(unit.CurrentCommand, Is.Null, "Death must clear the current order");
            Assert.That(unit.PendingCommands, Is.Empty, "Death must clear pending orders");
            Assert.That(unit.gameObject.activeSelf, Is.False);
            Assert.That(unit.Issue(new MoveCommand(Vector3.zero)), Is.False);
            Assert.That(unit.CurrentCommand, Is.Null);
        }
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `Tools/run-tests.sh EditMode Blackglass.Tests.CommandableUnitTests` → expected `EXIT=3`, `error CS1061: 'CommandableUnit' does not contain a definition for 'IsAlive'`.

- [ ] **Step 4: Implement**

In `Assets/_Project/Scripts/Units/CommandableUnit.cs`:

(a) Add a field after `Vector3 moveIntent;`:

```csharp
        Health ownHealth;
```

(b) Add after the `MoveIntent` property:

```csharp
        /// <summary>False once this unit's Health (if it has one) has died. A dead unit takes and runs no orders.</summary>
        public bool IsAlive => OwnHealth == null || OwnHealth.IsAlive;
```

(c) Add after the `Attacker` property:

```csharp
        // Looked up lazily so a Health added after this component is still found.
        Health OwnHealth => ownHealth != null ? ownHealth : ownHealth = GetComponent<Health>();
```

(d) In `Issue`, after the two argument checks (`ArgumentNullException`, `ArgumentOutOfRangeException`) and before the `switch`, add:

```csharp
            if (!IsAlive)
                return false;
```

Update the summary of `Issue`: change "Returns false if the order cannot be carried out (no walkable point within 2 m of the destination, dead or inactive target); the unit's orders are then unchanged." to "Returns false if the order cannot be carried out (this unit is dead, no walkable point within 2 m of the destination, dead or inactive target); the unit's orders are then unchanged."

(e) Add before `void Update()`:

```csharp
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
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `Tools/run-tests.sh EditMode` → expected `total="170" passed="170"`, `EXIT=0`.
Run: `Tools/run-tests.sh PlayMode` → expected `total="138" passed="138"`, `EXIT=0`.

- [ ] **Step 6: Commit**

```bash
git add Assets/_Project/Scripts/Units/CommandableUnit.cs Assets/_Project/Tests/EditMode/CommandableUnitTests.cs \
  Assets/_Project/Tests/PlayMode/TestSupport/TestWorld.cs Assets/_Project/Tests/PlayMode/CombatPlayModeTests.cs
git commit -m "Clear a unit's orders when it dies and refuse orders to the dead

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 4: `AutoRetaliate`

**Files:**
- Create: `Assets/_Project/Scripts/Units/AutoRetaliate.cs`
- Modify: `Assets/_Project/Tests/PlayMode/TestSupport/TestWorld.cs` (`CreateFighter` adds `AutoRetaliate`)
- Test: `Assets/_Project/Tests/PlayMode/AutoRetaliatePlayModeTests.cs` (new)

**Interfaces:**
- Consumes: `Health.AttackedBy`, `CommandableUnit.IsAlive`, `CommandableUnit.CurrentCommand`, `CommandableUnit.MoveIntent`, `CommandableUnit.Issue`.
- Produces: `public sealed class AutoRetaliate : MonoBehaviour` (`[RequireComponent(typeof(CommandableUnit), typeof(Health))]`), no public members. `TestWorld.CreateFighter` now also adds `AutoRetaliate`.

- [ ] **Step 1: Make `CreateFighter` add `AutoRetaliate` and write the failing tests**

In `TestWorld.CreateFighter`, after `var unit = host.AddComponent<CommandableUnit>();` add:

```csharp
            host.AddComponent<AutoRetaliate>();
```

Create `Assets/_Project/Tests/PlayMode/AutoRetaliatePlayModeTests.cs`:

```csharp
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class AutoRetaliatePlayModeTests
    {
        TestWorld world;
        CommandableUnit victim;
        CommandableUnit aggressor;
        Health victimHealth;
        Health aggressorHealth;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            world.CreateEnvironment();
            // Adjacent, inside each other's 2 m range.
            victim = world.CreateFighter(new Vector3(0f, 0f, 0f));
            aggressor = world.CreateFighter(new Vector3(0f, 0f, 1.5f), cooldown: 0.2f);
            victimHealth = victim.GetComponent<Health>();
            aggressorHealth = aggressor.GetComponent<Health>();
        }

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        static bool IsAttacking(CommandableUnit unit, Health target) =>
            unit.CurrentCommand is AttackCommand attack && attack.Target == target;

        IEnumerator WaitForFirstHit() => TestWorld.WaitUntil(() => victimHealth.Current < victimHealth.Max, 2f);

        [UnityTest]
        public IEnumerator IdleUnitHit_AttacksItsAttacker()
        {
            yield return null;
            aggressor.Issue(new AttackCommand(victimHealth));
            yield return WaitForFirstHit();
            yield return null;

            Assert.That(victimHealth.Current, Is.LessThan(victimHealth.Max), "Precondition: the victim was hit");
            Assert.That(IsAttacking(victim, aggressorHealth), Is.True, "The idle victim must attack back");
            yield return TestWorld.WaitUntil(() => aggressorHealth.Current < aggressorHealth.Max, 2f);
            Assert.That(aggressorHealth.Current, Is.LessThan(aggressorHealth.Max), "The retaliation never landed");
        }

        [UnityTest]
        public IEnumerator UnitWithAMoveOrder_KeepsMoving_WhenHit()
        {
            yield return null;
            victim.Issue(new MoveCommand(new Vector3(0f, 0f, -12f)));
            aggressor.Issue(new AttackCommand(victimHealth));
            yield return WaitForFirstHit();
            yield return null;

            Assert.That(victimHealth.Current, Is.LessThan(victimHealth.Max), "Precondition: the victim was hit");
            Assert.That(victim.CurrentCommand, Is.TypeOf<MoveCommand>(), "An order must win over retaliation");
            Assert.That(victim.PendingCommands, Is.Empty);
        }

        [UnityTest]
        public IEnumerator AfterStop_UnitRetaliatesOnTheNextHit()
        {
            yield return null;
            victim.Issue(new MoveCommand(new Vector3(0f, 0f, -12f)));
            yield return null;
            victim.Issue(new StopCommand());
            aggressor.Issue(new AttackCommand(victimHealth));
            yield return WaitForFirstHit();
            yield return null;

            Assert.That(IsAttacking(victim, aggressorHealth), Is.True, "A stopped unit is idle and fights back when hit");
        }

        [UnityTest]
        public IEnumerator RetaliatingUnit_OrderedAway_Retreats_AndStaysOnItsOrderWhileHit()
        {
            yield return null;
            aggressor.Issue(new AttackCommand(victimHealth));
            yield return WaitForFirstHit();
            yield return null;
            Assert.That(IsAttacking(victim, aggressorHealth), Is.True, "Precondition: the victim is retaliating");
            var start = victim.transform.position;

            var retreat = new MoveCommand(new Vector3(0f, 0f, -12f));
            Assert.That(victim.Issue(retreat), Is.True);
            yield return new WaitForSeconds(0.6f);   // the aggressor chases and hits again (0.2 s cooldown)

            Assert.That(victim.CurrentCommand, Is.SameAs(retreat), "Later hits must not replace the retreat order");
            Assert.That(TestWorld.HorizontalDistance(victim.transform.position, start), Is.GreaterThan(1.5f), "The unit did not retreat");
        }

        [UnityTest]
        public IEnumerator UnitWithAHeldMoveIntent_DoesNotRetaliate()
        {
            yield return null;
            victim.SetMoveIntent(Vector3.forward * 0.01f);   // held, barely moving, so it stays in range
            aggressor.Issue(new AttackCommand(victimHealth));
            yield return WaitForFirstHit();
            yield return null;

            Assert.That(victimHealth.Current, Is.LessThan(victimHealth.Max), "Precondition: the victim was hit");
            Assert.That(victim.CurrentCommand, Is.Null, "Held keys win: no retaliation order");
        }

        [UnityTest]
        public IEnumerator KillingBlow_DoesNotMakeTheVictimRetaliate()
        {
            yield return null;
            var fragile = world.CreateFighter(new Vector3(1.5f, 0f, 1.5f), maxHealth: 10);
            var fragileHealth = fragile.GetComponent<Health>();
            aggressor.Issue(new AttackCommand(fragileHealth));
            yield return TestWorld.WaitUntil(() => !fragileHealth.IsAlive, 2f);
            yield return null;

            Assert.That(fragileHealth.IsAlive, Is.False, "Precondition: the first hit killed it");
            Assert.That(fragile.CurrentCommand, Is.Null, "A corpse must not carry a retaliation order");
            Assert.That(aggressorHealth.Current, Is.EqualTo(aggressorHealth.Max), "The dead unit must not have struck back");
        }

        [UnityTest]
        public IEnumerator Retaliation_ChasesAnAttackerThatSteppedOutOfRange()
        {
            yield return null;
            aggressor.Issue(new AttackCommand(victimHealth));
            yield return WaitForFirstHit();
            aggressor.Issue(new MoveCommand(new Vector3(0f, 0f, 8f)));
            yield return new WaitForSeconds(1.5f);

            Assert.That(IsAttacking(victim, aggressorHealth), Is.True, "The retaliation order must keep going");
            Assert.That(TestWorld.HorizontalDistance(victim.transform.position, aggressor.transform.position), Is.LessThan(3f),
                "The retaliating unit did not chase");
        }
    }
}
```

(7 new tests.)

- [ ] **Step 2: Run the tests to verify they fail**

Run: `Tools/run-tests.sh PlayMode Blackglass.Tests.AutoRetaliatePlayModeTests` → expected `EXIT=3`, `error CS0246: The type or namespace name 'AutoRetaliate' could not be found`.

- [ ] **Step 3: Implement**

Create `Assets/_Project/Scripts/Units/AutoRetaliate.cs`:

```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Fights back: a unit hit while it has no orders attacks its attacker, through the normal order path. Any order
    /// wins (a Move away is a retreat) and so do held movement keys. Shared by friendlies and hostiles; it decides
    /// what to attack and nothing about how.
    /// </summary>
    [RequireComponent(typeof(CommandableUnit), typeof(Health))]
    public sealed class AutoRetaliate : MonoBehaviour
    {
        CommandableUnit unit;
        Health health;

        void Awake()
        {
            unit = GetComponent<CommandableUnit>();
            health = GetComponent<Health>();
        }

        void OnEnable() => health.AttackedBy += OnAttackedBy;

        void OnDisable() => health.AttackedBy -= OnAttackedBy;

        void OnAttackedBy(Health attacker)
        {
            if (!unit.IsAlive || unit.CurrentCommand != null || unit.MoveIntent != Vector3.zero)
                return;
            if (attacker == null || !attacker.IsAlive || !attacker.gameObject.activeInHierarchy)
                return;
            unit.Issue(new AttackCommand(attacker));
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `Tools/run-tests.sh PlayMode Blackglass.Tests.AutoRetaliatePlayModeTests` → expected `total="7" passed="7"`, `EXIT=0`.
Run: `Tools/run-tests.sh PlayMode` → expected `total="145" passed="145"`, `EXIT=0`.

If `UnitWithAHeldMoveIntent_DoesNotRetaliate` fails because the victim steered out of range before the hit, lower the intent to `Vector3.forward * 0.001f`; the intent only has to be non-zero.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/Units/AutoRetaliate.cs Assets/_Project/Scripts/Units/AutoRetaliate.cs.meta \
  Assets/_Project/Tests/PlayMode/TestSupport/TestWorld.cs \
  Assets/_Project/Tests/PlayMode/AutoRetaliatePlayModeTests.cs Assets/_Project/Tests/PlayMode/AutoRetaliatePlayModeTests.cs.meta
git commit -m "Make idle units fight back when hit

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 5: `DeathMarker`

**Files:**
- Create: `Assets/_Project/Scripts/Combat/DeathMarker.cs`
- Test: `Assets/_Project/Tests/PlayMode/DeathMarkerTests.cs` (new)

**Interfaces:**
- Consumes: `Health.Died`.
- Produces: `public sealed class DeathMarker : MonoBehaviour` (`[RequireComponent(typeof(Health))]`) with serialized `markerMaterial`, `diameter` (1.2), `groundHeight` (0.03); `internal void Initialize(Material material)`; `internal GameObject LastMarker { get; }` (the marker spawned on death, for tests). The field name `markerMaterial` is used by the scene builder in Task 11.

- [ ] **Step 1: Write the failing tests**

Create `Assets/_Project/Tests/PlayMode/DeathMarkerTests.cs`:

```csharp
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class DeathMarkerTests
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

        DeathMarker AddMarker(CommandableUnit unit)
        {
            var marker = unit.gameObject.AddComponent<DeathMarker>();
            marker.Initialize(null);
            return marker;
        }

        [UnityTest]
        public IEnumerator Death_LeavesAFlatMarkerWhereTheUnitFell_AndTheUnitIsGone()
        {
            world.CreateEnvironment();
            var unit = world.CreateFighter(new Vector3(3f, 0f, -4f));
            unit.name = "Victim";
            var marker = AddMarker(unit);
            var health = unit.GetComponent<Health>();
            yield return null;
            Assert.That(marker.LastMarker, Is.Null);

            health.TakeDamage(health.Max);
            yield return null;

            var corpse = marker.LastMarker;
            Assert.That(corpse, Is.Not.Null, "No marker was spawned");
            world.Track(corpse);
            Assert.That(corpse.name, Is.EqualTo("Victim (dead)"));
            Assert.That(corpse.transform.parent, Is.Null, "The marker must be a root object so it outlives the unit");
            Assert.That(corpse.activeInHierarchy, Is.True);
            Assert.That(TestWorld.HorizontalDistance(corpse.transform.position, unit.transform.position), Is.LessThan(0.01f));
            Assert.That(corpse.transform.position.y, Is.EqualTo(0.03f).Within(0.001f));
            Assert.That(corpse.transform.localScale.y, Is.LessThan(0.05f), "The marker must be flat");
            Assert.That(corpse.GetComponentsInChildren<Collider>(true), Is.Empty, "The marker must never block clicks");
            Assert.That(unit.gameObject.activeSelf, Is.False);
        }

        [UnityTest]
        public IEnumerator TwoFighters_FightUntilOneDies_TheLoserLeavesAMarker()
        {
            world.CreateEnvironment();
            var strong = world.CreateFighter(new Vector3(0f, 0f, -6f), maxHealth: 100, damage: 25, cooldown: 0.3f);
            var weak = world.CreateFighter(new Vector3(0f, 0f, 6f), maxHealth: 50, damage: 5, cooldown: 0.3f);
            var strongMarker = AddMarker(strong);
            var weakMarker = AddMarker(weak);
            var weakHealth = weak.GetComponent<Health>();
            yield return null;

            strong.Issue(new AttackCommand(weakHealth));
            yield return TestWorld.WaitUntil(() => !weakHealth.IsAlive, 15f);
            yield return null;

            Assert.That(weakHealth.IsAlive, Is.False, "The fight did not end in time");
            Assert.That(weak.gameObject.activeSelf, Is.False);
            Assert.That(weakMarker.LastMarker, Is.Not.Null);
            world.Track(weakMarker.LastMarker);
            Assert.That(strongMarker.LastMarker, Is.Null, "Only the loser leaves a marker");
            Assert.That(strong.GetComponent<Health>().Current, Is.LessThan(100), "The weak unit should have fought back");
            Assert.That(strong.CurrentCommand, Is.Null, "The winner's attack order must finish");
        }

        [UnityTest]
        public IEnumerator Marker_UsesTheGivenMaterial()
        {
            world.CreateEnvironment();
            var unit = world.CreateFighter(Vector3.zero);
            var marker = unit.gameObject.AddComponent<DeathMarker>();
            var material = world.Track(new Material(Shader.Find("Universal Render Pipeline/Unlit")));
            marker.Initialize(material);
            var health = unit.GetComponent<Health>();
            yield return null;

            health.TakeDamage(health.Max);
            yield return null;

            world.Track(marker.LastMarker);
            Assert.That(marker.LastMarker.GetComponent<Renderer>().sharedMaterial, Is.SameAs(material));
        }
    }
}
```

(3 new tests.)

- [ ] **Step 2: Run the tests to verify they fail**

Run: `Tools/run-tests.sh PlayMode Blackglass.Tests.DeathMarkerTests` → expected `EXIT=3`, `error CS0246: ... 'DeathMarker'`.

- [ ] **Step 3: Implement**

Create `Assets/_Project/Scripts/Combat/DeathMarker.cs`:

```csharp
using UnityEngine;
using UnityEngine.Rendering;

namespace Blackglass
{
    /// <summary>
    /// Debug death presentation: when the unit dies, leaves a flat disc where it fell. The disc is a root object
    /// (Health deactivates the unit right after Died) and has no collider, so clicks on it reach the ground.
    /// </summary>
    [RequireComponent(typeof(Health))]
    public sealed class DeathMarker : MonoBehaviour
    {
        [SerializeField] Material markerMaterial;
        [SerializeField, Min(0.1f)] float diameter = 1.2f;
        // The prototype ground is flat at y = 0; the disc floats just above it, under the queue lines (0.05).
        [SerializeField] float groundHeight = 0.03f;

        Health health;

        /// <summary>The marker spawned by this unit's death, or null while it lives.</summary>
        internal GameObject LastMarker { get; private set; }

        internal void Initialize(Material material) => markerMaterial = material;

        void Awake() => health = GetComponent<Health>();

        void OnEnable() => health.Died += OnDied;

        void OnDisable() => health.Died -= OnDied;

        void OnDied()
        {
            var marker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            marker.name = $"{name} (dead)";
            DestroyImmediate(marker.GetComponent<Collider>());
            var position = transform.position;
            position.y = groundHeight;
            marker.transform.SetPositionAndRotation(position, Quaternion.identity);
            marker.transform.localScale = new Vector3(diameter, 0.01f, diameter);
            var markerRenderer = marker.GetComponent<Renderer>();
            if (markerMaterial != null)
                markerRenderer.sharedMaterial = markerMaterial;
            markerRenderer.shadowCastingMode = ShadowCastingMode.Off;
            markerRenderer.receiveShadows = false;
            LastMarker = marker;
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `Tools/run-tests.sh PlayMode Blackglass.Tests.DeathMarkerTests` → expected `total="3" passed="3"`, `EXIT=0`.
Run: `Tools/run-tests.sh PlayMode` → expected `total="148" passed="148"`, `EXIT=0`.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/Combat/DeathMarker.cs Assets/_Project/Scripts/Combat/DeathMarker.cs.meta \
  Assets/_Project/Tests/PlayMode/DeathMarkerTests.cs Assets/_Project/Tests/PlayMode/DeathMarkerTests.cs.meta
git commit -m "Leave a flat marker where a unit dies

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 6: `AttackLineView`

**Files:**
- Create: `Assets/_Project/Scripts/DebugUI/AttackLineView.cs`
- Test: `Assets/_Project/Tests/PlayMode/AttackLineViewTests.cs` (new)

**Interfaces:**
- Consumes: `UnitAttacker.Attacked`, `SimulationTime.IsRunning`.
- Produces: `public sealed class AttackLineView : MonoBehaviour` (`[RequireComponent(typeof(UnitAttacker))]`) with serialized `line` (`LineRenderer`), `duration` (0.15), `height` (0.5); `internal void Initialize(LineRenderer lineRenderer)`; `internal bool IsShowing`. The field name `line` is used by the scene builder in Task 11.

- [ ] **Step 1: Write the failing tests**

Create `Assets/_Project/Tests/PlayMode/AttackLineViewTests.cs`:

```csharp
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class AttackLineViewTests
    {
        TestWorld world;
        CommandableUnit unit;
        Health dummy;
        AttackLineView view;
        LineRenderer line;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            world.CreateEnvironment();
            unit = world.CreateUnit(new Vector3(0f, 0f, 0f));
            dummy = world.CreateDummy(new Vector3(0f, 0f, 1.5f));
            // The line lives on a child so a unit's own LineRenderer (the queue view) stays free.
            var lineObject = new GameObject("AttackLine");
            lineObject.transform.SetParent(unit.transform, false);
            line = lineObject.AddComponent<LineRenderer>();
            unit.gameObject.SetActive(false);
            view = unit.gameObject.AddComponent<AttackLineView>();
            view.Initialize(line);
            unit.gameObject.SetActive(true);
        }

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        [UnityTest]
        public IEnumerator StartsHidden()
        {
            yield return null;
            Assert.That(view.IsShowing, Is.False);
            Assert.That(line.enabled, Is.False);
        }

        [UnityTest]
        public IEnumerator Hit_ShowsALineFromAttackerToTarget_ThenHidesIt()
        {
            yield return null;
            unit.Issue(new AttackCommand(dummy));
            yield return TestWorld.WaitUntil(() => dummy.Current < dummy.Max, 2f);

            Assert.That(view.IsShowing, Is.True, "The line must show on a hit");
            Assert.That(line.positionCount, Is.EqualTo(2));
            Assert.That(Vector3.Distance(line.GetPosition(0), unit.transform.position + Vector3.up * 0.5f), Is.LessThan(0.01f));
            Assert.That(Vector3.Distance(line.GetPosition(1), dummy.transform.position + Vector3.up * 0.5f), Is.LessThan(0.01f));

            yield return new WaitForSeconds(0.3f);
            Assert.That(view.IsShowing, Is.False, "The line must hide after its duration");
        }

        [UnityTest]
        public IEnumerator Line_StaysWhilePaused_AndHidesAfterResume()
        {
            var pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            yield return null;
            unit.Issue(new AttackCommand(dummy));
            yield return TestWorld.WaitUntil(() => dummy.Current < dummy.Max, 2f);
            Assert.That(view.IsShowing, Is.True, "Precondition: the line is showing");

            pause.Pause();
            yield return new WaitForSecondsRealtime(0.4f);
            Assert.That(view.IsShowing, Is.True, "The line runs on simulation time and must freeze while paused");

            pause.Resume();
            yield return new WaitForSeconds(0.3f);
            Assert.That(view.IsShowing, Is.False);
        }
    }
}
```

(3 new tests.)

- [ ] **Step 2: Run the tests to verify they fail**

Run: `Tools/run-tests.sh PlayMode Blackglass.Tests.AttackLineViewTests` → expected `EXIT=3`, `error CS0246: ... 'AttackLineView'`.

- [ ] **Step 3: Implement**

Create `Assets/_Project/Scripts/DebugUI/AttackLineView.cs`:

```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Debug feedback: shows a line from this unit to its target for a moment after each hit. The LineRenderer lives
    /// on a collider-free child, because a friendly unit's own LineRenderer belongs to CommandQueueView. Counts down
    /// on simulation time, so it freezes while paused.
    /// </summary>
    [RequireComponent(typeof(UnitAttacker))]
    public sealed class AttackLineView : MonoBehaviour
    {
        [SerializeField] LineRenderer line;
        [SerializeField, Min(0f)] float duration = 0.15f;
        // Height above the unit pivots (the capsule centre) where the line is drawn.
        [SerializeField] float height = 0.5f;

        UnitAttacker attacker;
        float remaining;

        internal bool IsShowing => line != null && line.enabled;

        internal void Initialize(LineRenderer lineRenderer) => line = lineRenderer;

        void Awake() => attacker = GetComponent<UnitAttacker>();

        void OnEnable()
        {
            attacker.Attacked += OnAttacked;
            Hide();
        }

        void OnDisable()
        {
            attacker.Attacked -= OnAttacked;
            remaining = 0f;
            Hide();
        }

        void Update()
        {
            if (remaining <= 0f || !SimulationTime.IsRunning)
                return;
            remaining -= Time.deltaTime;
            if (remaining <= 0f)
                Hide();
        }

        void OnAttacked(Health target)
        {
            if (line == null || target == null)
                return;
            line.useWorldSpace = true;
            line.positionCount = 2;
            line.SetPosition(0, transform.position + Vector3.up * height);
            line.SetPosition(1, target.transform.position + Vector3.up * height);
            line.enabled = true;
            remaining = duration;
        }

        void Hide()
        {
            if (line != null)
                line.enabled = false;
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `Tools/run-tests.sh PlayMode Blackglass.Tests.AttackLineViewTests` → expected `total="3" passed="3"`, `EXIT=0`.
Run: `Tools/run-tests.sh PlayMode` → expected `total="151" passed="151"`, `EXIT=0`.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/DebugUI/AttackLineView.cs Assets/_Project/Scripts/DebugUI/AttackLineView.cs.meta \
  Assets/_Project/Tests/PlayMode/AttackLineViewTests.cs Assets/_Project/Tests/PlayMode/AttackLineViewTests.cs.meta
git commit -m "Show a brief line from attacker to target on each hit

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 7: `Encounter`

**Files:**
- Create: `Assets/_Project/Scripts/Combat/Encounter.cs`
- Modify: `Assets/_Project/Tests/PlayMode/TestSupport/TestWorld.cs` (add `CreateEncounter`)
- Test: `Assets/_Project/Tests/EditMode/EncounterTests.cs` (new)

**Interfaces:**
- Produces: `public enum EncounterOutcome { Ongoing, Victory, Defeat }`; `public sealed class Encounter : MonoBehaviour` with `IReadOnlyList<Health> Friendlies`, `IReadOnlyList<Health> Hostiles`, `int LivingFriendlies`, `int LivingHostiles`, `EncounterOutcome Outcome`, `public static EncounterOutcome Resolve(int friendlyCount, int livingFriendlies, int hostileCount, int livingHostiles)`, `internal void Initialize(IEnumerable<Health> friendlyUnits, IEnumerable<Health> hostileUnits)`. Serialized field names `friendlies` and `hostiles` are used by the scene builder in Task 11. `TestWorld.CreateEncounter()` returns an empty, tracked `Encounter`.

- [ ] **Step 1: Write the failing tests**

Create `Assets/_Project/Tests/EditMode/EncounterTests.cs`:

```csharp
using System;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class EncounterTests
    {
        GameObject systems;
        Encounter encounter;
        GameObject[] hosts;

        [SetUp]
        public void SetUp()
        {
            systems = new GameObject("Systems");
            encounter = systems.AddComponent<Encounter>();
            hosts = Array.Empty<GameObject>();
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
        }

        Health[] CreateUnits(params string[] names)
        {
            var units = new Health[names.Length];
            var created = new GameObject[names.Length];
            for (var i = 0; i < names.Length; i++)
            {
                created[i] = new GameObject(names[i]);
                units[i] = created[i].AddComponent<Health>();
            }
            var all = new GameObject[hosts.Length + created.Length];
            hosts.CopyTo(all, 0);
            created.CopyTo(all, hosts.Length);
            hosts = all;
            return units;
        }

        static void Kill(Health unit) => unit.TakeDamage(unit.Max);

        [TestCase(3, 3, 3, 3, EncounterOutcome.Ongoing)]
        [TestCase(3, 1, 3, 1, EncounterOutcome.Ongoing)]
        [TestCase(3, 2, 3, 0, EncounterOutcome.Victory)]
        [TestCase(3, 0, 3, 2, EncounterOutcome.Defeat)]
        [TestCase(3, 0, 3, 0, EncounterOutcome.Defeat)]
        [TestCase(0, 0, 0, 0, EncounterOutcome.Ongoing)]
        [TestCase(3, 3, 0, 0, EncounterOutcome.Ongoing)]
        [TestCase(0, 0, 3, 3, EncounterOutcome.Ongoing)]
        public void Resolve_FollowsTheRules(int friendlies, int livingFriendlies, int hostiles, int livingHostiles, EncounterOutcome expected)
        {
            Assert.That(Encounter.Resolve(friendlies, livingFriendlies, hostiles, livingHostiles), Is.EqualTo(expected));
        }

        [Test]
        public void Resolve_RejectsImpossibleCounts()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Encounter.Resolve(-1, 0, 3, 3));
            Assert.Throws<ArgumentOutOfRangeException>(() => Encounter.Resolve(3, 4, 3, 3));
            Assert.Throws<ArgumentOutOfRangeException>(() => Encounter.Resolve(3, 3, 3, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => Encounter.Resolve(3, 3, 2, 3));
        }

        [Test]
        public void Outcome_ReadsTheLists()
        {
            var friendlies = CreateUnits("F1", "F2");
            var hostiles = CreateUnits("H1", "H2");
            encounter.Initialize(friendlies, hostiles);
            Assert.That(encounter.Friendlies, Is.EqualTo(friendlies));
            Assert.That(encounter.Hostiles, Is.EqualTo(hostiles));
            Assert.That(encounter.Outcome, Is.EqualTo(EncounterOutcome.Ongoing));

            Kill(hostiles[0]);
            Assert.That(encounter.LivingHostiles, Is.EqualTo(1));
            Assert.That(encounter.Outcome, Is.EqualTo(EncounterOutcome.Ongoing));

            Kill(hostiles[1]);
            Assert.That(encounter.LivingHostiles, Is.EqualTo(0));
            Assert.That(encounter.Outcome, Is.EqualTo(EncounterOutcome.Victory));

            Kill(friendlies[0]);
            Kill(friendlies[1]);
            Assert.That(encounter.Outcome, Is.EqualTo(EncounterOutcome.Defeat), "Everyone dead is a defeat");
        }

        [Test]
        public void LivingCounts_IgnoreNullAndDestroyedEntries()
        {
            var friendlies = CreateUnits("F1", "F2", "F3");
            var hostiles = CreateUnits("H1");
            encounter.Initialize(new[] { friendlies[0], null, friendlies[1], friendlies[2] }, hostiles);
            Assert.That(encounter.Friendlies, Has.Count.EqualTo(4));
            Assert.That(encounter.LivingFriendlies, Is.EqualTo(3));

            Object.DestroyImmediate(friendlies[2].gameObject);
            Assert.That(encounter.LivingFriendlies, Is.EqualTo(2), "A destroyed unit counts as dead");
            Assert.That(encounter.Outcome, Is.EqualTo(EncounterOutcome.Ongoing));
        }

        [Test]
        public void EmptyEncounter_IsOngoing()
        {
            Assert.That(encounter.Friendlies, Is.Empty);
            Assert.That(encounter.Hostiles, Is.Empty);
            Assert.That(encounter.LivingFriendlies, Is.EqualTo(0));
            Assert.That(encounter.Outcome, Is.EqualTo(EncounterOutcome.Ongoing));
        }

        [Test]
        public void Initialize_Null_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => encounter.Initialize(null, Array.Empty<Health>()));
            Assert.Throws<ArgumentNullException>(() => encounter.Initialize(Array.Empty<Health>(), null));
        }
    }
}
```

(8 test cases + 5 tests = 13 new.)

Add to `TestWorld` (after `CreateDummy`):

```csharp
        /// <summary>An empty Encounter on its own object; call Initialize with the two sides once they exist.</summary>
        public Encounter CreateEncounter() => Track(new GameObject("Encounter")).AddComponent<Encounter>();
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `Tools/run-tests.sh EditMode Blackglass.Tests.EncounterTests` → expected `EXIT=3`, `error CS0246: ... 'Encounter'`.

- [ ] **Step 3: Implement**

Create `Assets/_Project/Scripts/Combat/Encounter.cs`:

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    public enum EncounterOutcome
    {
        Ongoing,
        Victory,
        Defeat,
    }

    /// <summary>
    /// The two sides of the prototype encounter, as lists of Health, and its outcome. The only place that knows who
    /// is friendly and who is hostile: enemy AI reads Friendlies from here and the HUD reads Outcome. Holds state
    /// only; everything is computed on read. No faction system.
    /// </summary>
    public sealed class Encounter : MonoBehaviour
    {
        [SerializeField] List<Health> friendlies = new List<Health>();
        [SerializeField] List<Health> hostiles = new List<Health>();

        public IReadOnlyList<Health> Friendlies => friendlies;
        public IReadOnlyList<Health> Hostiles => hostiles;

        /// <summary>Friendly entries that exist and are alive. Null and destroyed entries count as dead.</summary>
        public int LivingFriendlies => CountLiving(friendlies);

        public int LivingHostiles => CountLiving(hostiles);

        public EncounterOutcome Outcome => Resolve(friendlies.Count, LivingFriendlies, hostiles.Count, LivingHostiles);

        internal void Initialize(IEnumerable<Health> friendlyUnits, IEnumerable<Health> hostileUnits)
        {
            if (friendlyUnits == null)
                throw new ArgumentNullException(nameof(friendlyUnits));
            if (hostileUnits == null)
                throw new ArgumentNullException(nameof(hostileUnits));
            friendlies.Clear();
            friendlies.AddRange(friendlyUnits);
            hostiles.Clear();
            hostiles.AddRange(hostileUnits);
        }

        /// <summary>
        /// All friendlies dead is a defeat (checked first, so everyone dead is a defeat); otherwise all hostiles dead
        /// is a victory. An empty side never resolves, so a scene without hostiles is not an instant win.
        /// </summary>
        public static EncounterOutcome Resolve(int friendlyCount, int livingFriendlies, int hostileCount, int livingHostiles)
        {
            CheckCounts(friendlyCount, livingFriendlies, nameof(livingFriendlies));
            CheckCounts(hostileCount, livingHostiles, nameof(livingHostiles));
            if (friendlyCount > 0 && livingFriendlies == 0)
                return EncounterOutcome.Defeat;
            if (hostileCount > 0 && livingHostiles == 0)
                return EncounterOutcome.Victory;
            return EncounterOutcome.Ongoing;
        }

        static void CheckCounts(int count, int living, string livingName)
        {
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count), count, "A side cannot have a negative size.");
            if (living < 0 || living > count)
                throw new ArgumentOutOfRangeException(livingName, living, "Living units must be between 0 and the side's size.");
        }

        static int CountLiving(List<Health> units)
        {
            var living = 0;
            foreach (var unit in units)
            {
                if (unit != null && unit.IsAlive)
                    living++;
            }
            return living;
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `Tools/run-tests.sh EditMode` → expected `total="183" passed="183"`, `EXIT=0`. (170 + 13.)
Run: `Tools/run-tests.sh PlayMode` → expected `total="151" passed="151"`, `EXIT=0` (compiles `CreateEncounter`).

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/Combat/Encounter.cs Assets/_Project/Scripts/Combat/Encounter.cs.meta \
  Assets/_Project/Tests/EditMode/EncounterTests.cs Assets/_Project/Tests/EditMode/EncounterTests.cs.meta \
  Assets/_Project/Tests/PlayMode/TestSupport/TestWorld.cs
git commit -m "Add Encounter with the two sides and a victory or defeat outcome

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 8: `EnemyAI`, plus the combat pause tests

**Files:**
- Create: `Assets/_Project/Scripts/AI/EnemyAI.cs`
- Modify: `Assets/_Project/Tests/PlayMode/TestSupport/TestWorld.cs` (add `CreateHostile`)
- Test: `Assets/_Project/Tests/EditMode/EnemyAITests.cs` (new), `Assets/_Project/Tests/PlayMode/EnemyAIPlayModeTests.cs` (new), `Assets/_Project/Tests/PlayMode/CombatPausePlayModeTests.cs` (new)

**Interfaces:**
- Consumes: `Encounter.Friendlies`, `CommandableUnit.IsAlive`/`CurrentCommand`/`Issue`, `UnitAttacker.IsInRange`, `SimulationTime.IsRunning`.
- Produces: `public enum EnemyState { Idle, Chase, Attack, Dead }`; `public sealed class EnemyAI : MonoBehaviour` (`[RequireComponent(typeof(CommandableUnit), typeof(Health), typeof(UnitAttacker))]`) with serialized `encounter`, `detectionRange` (12), `thinkInterval` (0.25), `sightBlockers` (`~0`), `eyeHeight` (0.5); `EnemyState State`, `Health Target`, `float DetectionRange`; `public static EnemyState DeriveState(bool alive, UnitCommand current, bool inRange)`; `public static bool HasLineOfSight(Vector3 eye, Health target, LayerMask blockers, RaycastHit[] buffer)`; `internal void Initialize(Encounter encounterToFight, float range = 12f, float interval = 0.25f)`. The field name `encounter` is used by the scene builder in Task 11. `TestWorld.CreateHostile(Vector3 groundPosition, Encounter encounter, int maxHealth = 60, int damage = 10, float cooldown = 1.2f, float detectionRange = 12f)` returns the `EnemyAI`.

- [ ] **Step 1: Write the failing EditMode tests**

Create `Assets/_Project/Tests/EditMode/EnemyAITests.cs`:

```csharp
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class EnemyAITests
    {
        [Test]
        public void DeriveState_Dead_WhateverTheOrder()
        {
            var host = new GameObject("Target");
            var attack = new AttackCommand(host.AddComponent<Health>());
            Assert.That(EnemyAI.DeriveState(false, null, false), Is.EqualTo(EnemyState.Dead));
            Assert.That(EnemyAI.DeriveState(false, attack, true), Is.EqualTo(EnemyState.Dead));
            Object.DestroyImmediate(host);
        }

        [Test]
        public void DeriveState_Idle_WithoutAnAttackOrder()
        {
            Assert.That(EnemyAI.DeriveState(true, null, false), Is.EqualTo(EnemyState.Idle));
            Assert.That(EnemyAI.DeriveState(true, new MoveCommand(Vector3.zero), false), Is.EqualTo(EnemyState.Idle));
        }

        [Test]
        public void DeriveState_ChaseOutOfRange_AttackInRange()
        {
            var host = new GameObject("Target");
            var attack = new AttackCommand(host.AddComponent<Health>());
            Assert.That(EnemyAI.DeriveState(true, attack, false), Is.EqualTo(EnemyState.Chase));
            Assert.That(EnemyAI.DeriveState(true, attack, true), Is.EqualTo(EnemyState.Attack));
            Object.DestroyImmediate(host);
        }

        [Test]
        public void RequiredComponentsAreAdded_AndDefaultsMatchThePrototype()
        {
            var host = new GameObject("Hostile");
            var ai = host.AddComponent<EnemyAI>();
            Assert.That(host.GetComponent<CommandableUnit>(), Is.Not.Null);
            Assert.That(host.GetComponent<Health>(), Is.Not.Null);
            Assert.That(host.GetComponent<UnitAttacker>(), Is.Not.Null);
            Assert.That(ai.DetectionRange, Is.EqualTo(12f));
            Assert.That(ai.Target, Is.Null);
            Object.DestroyImmediate(host);
        }
    }
}
```

(4 new tests.)

- [ ] **Step 2: Add `CreateHostile` to `TestWorld` and write the failing PlayMode tests**

Add to `TestWorld` after `CreateFighter`:

```csharp
        /// <summary>A fighter with EnemyAI wired to the encounter. Hostile prototype stats by default.</summary>
        public EnemyAI CreateHostile(Vector3 groundPosition, Encounter encounter, int maxHealth = 60, int damage = 10,
            float cooldown = 1.2f, float detectionRange = 12f)
        {
            var unit = CreateFighter(groundPosition, maxHealth, damage, cooldown);
            unit.name = "TestHostile";
            unit.gameObject.SetActive(false);
            var ai = unit.gameObject.AddComponent<EnemyAI>();
            ai.Initialize(encounter, detectionRange);
            unit.gameObject.SetActive(true);
            return ai;
        }
```

Create `Assets/_Project/Tests/PlayMode/EnemyAIPlayModeTests.cs`:

```csharp
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class EnemyAIPlayModeTests
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

        // Wires the sides once every unit exists.
        void Arm(EnemyAI[] hostiles, params CommandableUnit[] friendlies) =>
            encounter.Initialize(friendlies.Select(f => HealthOf(f)), hostiles.Select(h => HealthOf(h)));

        IEnumerator WaitForState(EnemyAI ai, EnemyState state, float timeout) =>
            TestWorld.WaitUntil(() => ai.State == state, timeout);

        [UnityTest]
        public IEnumerator FriendlyBeyondTheRadius_HostileStaysIdle()
        {
            world.CreateEnvironment();
            var friendly = world.CreateFighter(new Vector3(0f, 0f, -14f));
            var hostile = world.CreateHostile(new Vector3(0f, 0f, 2f), encounter, detectionRange: 12f);
            Arm(new[] { hostile }, friendly);

            yield return new WaitForSeconds(0.8f);

            Assert.That(hostile.State, Is.EqualTo(EnemyState.Idle));
            Assert.That(hostile.Target, Is.Null);
            Assert.That(hostile.GetComponent<CommandableUnit>().CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator FriendlyInsideTheRadius_HostileAcquiresChasesAndAttacks()
        {
            world.CreateEnvironment();
            var friendly = world.CreateFighter(new Vector3(0f, 0f, -6f));
            var hostile = world.CreateHostile(new Vector3(0f, 0f, 4f), encounter);
            Arm(new[] { hostile }, friendly);

            yield return WaitForState(hostile, EnemyState.Chase, 1f);
            Assert.That(hostile.State, Is.EqualTo(EnemyState.Chase), "The hostile never acquired its target");
            Assert.That(hostile.Target, Is.SameAs(HealthOf(friendly)));

            yield return WaitForState(hostile, EnemyState.Attack, 6f);
            Assert.That(hostile.State, Is.EqualTo(EnemyState.Attack), "The hostile never reached attack range");
            yield return TestWorld.WaitUntil(() => HealthOf(friendly).Current < HealthOf(friendly).Max, 2f);
            Assert.That(HealthOf(friendly).Current, Is.EqualTo(90), "One hostile hit deals 10");
        }

        [UnityTest]
        public IEnumerator FriendlyBehindAWall_IsNotSeen_UntilItStepsOut()
        {
            // A 2 m high, 6 m wide wall (x -3..3) between them: the eye (1.5 m) cannot see over it.
            world.CreateEnvironment((new Vector3(0f, 1f, 0f), new Vector3(6f, 2f, 1f)));
            var friendly = world.CreateFighter(new Vector3(0f, 0f, -4f));
            var hostile = world.CreateHostile(new Vector3(0f, 0f, 4f), encounter);
            Arm(new[] { hostile }, friendly);

            yield return new WaitForSeconds(0.8f);
            Assert.That(hostile.State, Is.EqualTo(EnemyState.Idle), "A wall must block line of sight");

            // The sight line from the eye (0, 1.5, 4) to the friendly's centre (8, 1, -3) crosses the wall's slab at
            // x 4.0..5.1, clear of its x = 3 end, and the horizontal distance (10.6 m) stays inside the 12 m radius.
            friendly.Issue(new MoveCommand(new Vector3(8f, 0f, -3f)));
            yield return WaitForState(hostile, EnemyState.Chase, 6f);
            Assert.That(hostile.State, Is.Not.EqualTo(EnemyState.Idle), "The hostile must see the friendly once it clears the wall");
            Assert.That(hostile.Target, Is.SameAs(HealthOf(friendly)));
        }

        [UnityTest]
        public IEnumerator Hostile_PicksTheNearestVisibleFriendly()
        {
            world.CreateEnvironment();
            var near = world.CreateFighter(new Vector3(0f, 0f, -5f));
            var far = world.CreateFighter(new Vector3(0f, 0f, -9f));
            var hostile = world.CreateHostile(new Vector3(0f, 0f, 0f), encounter);
            Arm(new[] { hostile }, far, near);   // list order must not matter

            yield return WaitForState(hostile, EnemyState.Chase, 1f);
            Assert.That(hostile.Target, Is.SameAs(HealthOf(near)));
        }

        [UnityTest]
        public IEnumerator TargetDies_HostileReacquiresTheNextFriendly()
        {
            world.CreateEnvironment();
            var fragile = world.CreateFighter(new Vector3(0f, 0f, -3f), maxHealth: 10);
            var other = world.CreateFighter(new Vector3(6f, 0f, -3f));
            var hostile = world.CreateHostile(new Vector3(0f, 0f, 1f), encounter);
            Arm(new[] { hostile }, fragile, other);

            yield return TestWorld.WaitUntil(() => !HealthOf(fragile).IsAlive, 6f);
            Assert.That(HealthOf(fragile).IsAlive, Is.False, "The fragile friendly should have died first");

            yield return TestWorld.WaitUntil(() => hostile.Target == HealthOf(other), 2f);
            Assert.That(hostile.Target, Is.SameAs(HealthOf(other)), "The hostile must reacquire the other friendly");
            Assert.That(hostile.State, Is.EqualTo(EnemyState.Chase).Or.EqualTo(EnemyState.Attack));
        }

        [UnityTest]
        public IEnumerator NoLivingFriendly_HostileIdlesWithoutErrors()
        {
            world.CreateEnvironment();
            var friendly = world.CreateFighter(new Vector3(0f, 0f, -3f));
            var hostile = world.CreateHostile(new Vector3(0f, 0f, 1f), encounter);
            Arm(new[] { hostile }, friendly);
            HealthOf(friendly).TakeDamage(1000);

            yield return new WaitForSeconds(0.8f);

            Assert.That(hostile.State, Is.EqualTo(EnemyState.Idle));
            Assert.That(hostile.Target, Is.Null);
        }

        [UnityTest]
        public IEnumerator DeadHostile_DoesNothing()
        {
            world.CreateEnvironment();
            var friendly = world.CreateFighter(new Vector3(0f, 0f, -3f));
            var hostile = world.CreateHostile(new Vector3(0f, 0f, 1f), encounter);
            Arm(new[] { hostile }, friendly);
            HealthOf(hostile).TakeDamage(1000);

            yield return new WaitForSeconds(0.8f);

            Assert.That(hostile.State, Is.EqualTo(EnemyState.Dead));
            Assert.That(hostile.Target, Is.Null);
            Assert.That(HealthOf(friendly).Current, Is.EqualTo(HealthOf(friendly).Max));
        }

        [UnityTest]
        public IEnumerator HostileHitThroughAWall_RetaliatesAtOnce()
        {
            // A thin (0.2 m), 2 m tall wall: it blocks sight (the ray crosses it at y 1.25), and the NavMesh, eroded
            // 0.5 m for the agent radius, still reaches to 0.6 m of it, so the agents stay at +-0.9: 1.8 m apart,
            // inside melee range. A thicker wall would push them out of range when they snap onto the NavMesh.
            world.CreateEnvironment((new Vector3(0f, 1f, 0f), new Vector3(10f, 2f, 0.2f)));
            var friendly = world.CreateFighter(new Vector3(0f, 0f, -0.9f));
            var hostile = world.CreateHostile(new Vector3(0f, 0f, 0.9f), encounter);
            Arm(new[] { hostile }, friendly);
            yield return new WaitForSeconds(0.6f);
            Assert.That(TestWorld.HorizontalDistance(friendly.transform.position, hostile.transform.position), Is.LessThan(2f),
                "Precondition: the NavMesh must not have pushed the units out of melee range");
            Assert.That(hostile.State, Is.EqualTo(EnemyState.Idle), "Precondition: no line of sight through the wall");

            friendly.Issue(new AttackCommand(HealthOf(hostile)));
            yield return TestWorld.WaitUntil(() => HealthOf(hostile).Current < HealthOf(hostile).Max, 2f);
            yield return null;

            Assert.That(HealthOf(hostile).Current, Is.LessThan(HealthOf(hostile).Max), "Precondition: the friendly hit the hostile through the wall");
            Assert.That(hostile.Target, Is.SameAs(HealthOf(friendly)), "A hostile hit from a blind spot must fight back");
        }

        [UnityTest]
        public IEnumerator UnreachableVisibleFriendly_IsRechasedWithoutErrors()
        {
            // Low walls ring the friendly: agents cannot step over 0.6 m, but a 1.5 m eye sees over them.
            world.CreateEnvironment(
                (new Vector3(0f, 0.3f, -8f), new Vector3(6f, 0.6f, 0.5f)),
                (new Vector3(0f, 0.3f, -2f), new Vector3(6f, 0.6f, 0.5f)),
                (new Vector3(-3f, 0.3f, -5f), new Vector3(0.5f, 0.6f, 6f)),
                (new Vector3(3f, 0.3f, -5f), new Vector3(0.5f, 0.6f, 6f)));
            var friendly = world.CreateFighter(new Vector3(0f, 0f, -5f));
            var hostile = world.CreateHostile(new Vector3(0f, 0f, 4f), encounter);
            Arm(new[] { hostile }, friendly);

            yield return new WaitForSeconds(3f);

            Assert.That(HealthOf(friendly).Current, Is.EqualTo(HealthOf(friendly).Max), "The hostile must not reach the ringed friendly");
            Assert.That(hostile.State, Is.Not.EqualTo(EnemyState.Dead));
            Assert.That(TestWorld.HorizontalDistance(hostile.transform.position, friendly.transform.position), Is.LessThan(6f),
                "The hostile should have walked up to the ring");
            // Any logged error during the 3 s fails the test on its own.
        }

        [UnityTest]
        public IEnumerator DestroyedFriendlyInTheList_IsIgnored()
        {
            world.CreateEnvironment();
            var doomed = world.CreateFighter(new Vector3(0f, 0f, -3f));
            var other = world.CreateFighter(new Vector3(0f, 0f, -6f));
            var hostile = world.CreateHostile(new Vector3(0f, 0f, 1f), encounter);
            Arm(new[] { hostile }, doomed, other);
            Object.DestroyImmediate(doomed.gameObject);

            yield return WaitForState(hostile, EnemyState.Chase, 1f);

            Assert.That(hostile.Target, Is.SameAs(HealthOf(other)));
        }

        [UnityTest]
        public IEnumerator HasLineOfSight_UnitsNeverBlock_WallsDo()
        {
            world.CreateEnvironment((new Vector3(5f, 1f, 0f), new Vector3(1f, 2f, 6f)));
            var seen = world.CreateFighter(new Vector3(0f, 0f, -4f));
            var blocker = world.CreateFighter(new Vector3(0f, 0f, -2f));
            var behindWall = world.CreateFighter(new Vector3(8f, 0f, 0f));
            yield return new WaitForFixedUpdate();   // colliders take their positions
            var buffer = new RaycastHit[8];
            var eye = new Vector3(0f, 1.5f, 1f);

            Assert.That(EnemyAI.HasLineOfSight(eye, HealthOf(seen), ~0, buffer), Is.True, "A unit in between must not block sight");
            Assert.That(EnemyAI.HasLineOfSight(eye, HealthOf(blocker), ~0, buffer), Is.True);
            Assert.That(EnemyAI.HasLineOfSight(eye, HealthOf(behindWall), ~0, buffer), Is.False, "A wall must block sight");
        }
    }
}
```

(11 new tests.)

Create `Assets/_Project/Tests/PlayMode/CombatPausePlayModeTests.cs`:

```csharp
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CombatPausePlayModeTests
    {
        TestWorld world;
        TacticalPause pause;
        Encounter encounter;
        CommandableUnit friendly;
        EnemyAI hostile;
        Health friendlyHealth;
        Health hostileHealth;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            world.CreateEnvironment();
            pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            encounter = world.CreateEncounter();
            friendly = world.CreateFighter(new Vector3(0f, 0f, -6f), cooldown: 0.4f);
            hostile = world.CreateHostile(new Vector3(0f, 0f, 4f), encounter, maxHealth: 300, cooldown: 0.4f);
            friendlyHealth = friendly.GetComponent<Health>();
            hostileHealth = hostile.GetComponent<Health>();
            encounter.Initialize(new[] { friendlyHealth }, new[] { hostileHealth });
        }

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        IEnumerator StartTheFight()
        {
            yield return TestWorld.WaitUntil(() => friendlyHealth.Current < friendlyHealth.Max && hostileHealth.Current < hostileHealth.Max, 8f);
            Assert.That(friendlyHealth.Current, Is.LessThan(friendlyHealth.Max), "Precondition: the hostile hit the friendly");
            Assert.That(hostileHealth.Current, Is.LessThan(hostileHealth.Max), "Precondition: the friendly retaliated");
        }

        [UnityTest]
        public IEnumerator Paused_NothingInTheFightChanges_AndItResumesAfterwards()
        {
            yield return StartTheFight();

            pause.Pause();
            var friendlyHp = friendlyHealth.Current;
            var hostileHp = hostileHealth.Current;
            var state = hostile.State;
            var friendlyCooldown = friendly.GetComponent<UnitAttacker>().CooldownRemaining;
            var hostileCooldown = hostile.GetComponent<UnitAttacker>().CooldownRemaining;
            var friendlyAt = friendly.transform.position;
            var hostileAt = hostile.transform.position;
            yield return new WaitForSecondsRealtime(1f);

            Assert.That(friendlyHealth.Current, Is.EqualTo(friendlyHp), "Friendly health changed while paused");
            Assert.That(hostileHealth.Current, Is.EqualTo(hostileHp), "Hostile health changed while paused");
            Assert.That(hostile.State, Is.EqualTo(state), "Enemy AI advanced while paused");
            Assert.That(friendly.GetComponent<UnitAttacker>().CooldownRemaining, Is.EqualTo(friendlyCooldown), "A cooldown progressed while paused");
            Assert.That(hostile.GetComponent<UnitAttacker>().CooldownRemaining, Is.EqualTo(hostileCooldown), "A cooldown progressed while paused");
            Assert.That(Vector3.Distance(friendly.transform.position, friendlyAt), Is.LessThan(0.01f), "The friendly moved while paused");
            Assert.That(Vector3.Distance(hostile.transform.position, hostileAt), Is.LessThan(0.01f), "The hostile moved while paused");

            pause.Resume();
            yield return TestWorld.WaitUntil(() => friendlyHealth.Current < friendlyHp || hostileHealth.Current < hostileHp, 3f);
            Assert.That(friendlyHealth.Current < friendlyHp || hostileHealth.Current < hostileHp, Is.True, "The fight did not resume");
        }

        [UnityTest]
        public IEnumerator RepeatedPauseAndResume_DuringTheFight_LogsNoErrors_AndEndsConsistent()
        {
            yield return StartTheFight();

            for (var i = 0; i < 10; i++)
            {
                pause.Pause();
                yield return new WaitForSecondsRealtime(0.1f);
                pause.Resume();
                yield return new WaitForSeconds(0.15f);
            }

            Assert.That(pause.IsPaused, Is.False);
            Assert.That(Time.timeScale, Is.EqualTo(1f));
            foreach (var unit in new[] { friendly, hostile.GetComponent<CommandableUnit>() })
            {
                var health = unit.GetComponent<Health>();
                if (health.IsAlive)
                    Assert.That(unit.gameObject.activeSelf, Is.True, $"{unit.name} is alive but inactive");
                else
                    Assert.That(unit.CurrentCommand, Is.Null, $"{unit.name} is dead but still has orders");
            }
            Assert.That(encounter.Outcome, Is.EqualTo(EncounterOutcome.Ongoing).Or.EqualTo(EncounterOutcome.Defeat));
        }

        [UnityTest]
        public IEnumerator OrdersIssuedWhilePaused_RunAfterResume_AndTheHostileKeepsFighting()
        {
            yield return StartTheFight();
            pause.Pause();
            var retreat = new MoveCommand(new Vector3(0f, 0f, -14f));
            Assert.That(friendly.Issue(retreat), Is.True, "Orders must be accepted while paused");
            var friendlyAt = friendly.transform.position;
            yield return new WaitForSecondsRealtime(0.3f);
            Assert.That(Vector3.Distance(friendly.transform.position, friendlyAt), Is.LessThan(0.01f));

            pause.Resume();
            yield return new WaitForSeconds(1.5f);

            Assert.That(TestWorld.HorizontalDistance(friendly.transform.position, friendlyAt), Is.GreaterThan(2f), "The queued retreat did not run");
            Assert.That(hostile.State, Is.EqualTo(EnemyState.Chase).Or.EqualTo(EnemyState.Attack), "The hostile must keep after the friendly");
        }
    }
}
```

(3 new tests.)

- [ ] **Step 3: Run the tests to verify they fail**

Run: `Tools/run-tests.sh EditMode Blackglass.Tests.EnemyAITests` → expected `EXIT=3`, `error CS0246: ... 'EnemyAI'`.

- [ ] **Step 4: Implement**

Create `Assets/_Project/Scripts/AI/EnemyAI.cs`:

```csharp
using UnityEngine;

namespace Blackglass
{
    public enum EnemyState
    {
        Idle,
        Chase,
        Attack,
        Dead,
    }

    /// <summary>
    /// The smallest hostile brain: while idle, every think tick it looks for the nearest living friendly inside its
    /// detection radius that it can see, and attacks it through the normal order path. CommandableUnit then chases,
    /// stops and hits; when the target dies or cannot be reached that order ends on its own and the brain looks
    /// again. Line of sight gates acquisition only: a target once taken is followed around corners. Runs on
    /// simulation time, so it freezes while paused.
    /// </summary>
    [RequireComponent(typeof(CommandableUnit), typeof(Health), typeof(UnitAttacker))]
    public sealed class EnemyAI : MonoBehaviour
    {
        // Enough for the few units and walls a sight line can cross in the prototype.
        const int SightHitBufferSize = 8;

        [SerializeField] Encounter encounter;
        [SerializeField, Min(0f)] float detectionRange = 12f;
        [SerializeField, Min(0f)] float thinkInterval = 0.25f;
        [SerializeField] LayerMask sightBlockers = ~0;
        // Above the unit's pivot (the capsule centre, 1 m up), so the eye is at 1.5 m.
        [SerializeField] float eyeHeight = 0.5f;

        readonly RaycastHit[] sightHits = new RaycastHit[SightHitBufferSize];
        CommandableUnit unit;
        UnitAttacker attacker;
        float nextThinkTime;

        public float DetectionRange => detectionRange;

        /// <summary>The friendly this hostile is after, or null while idle.</summary>
        public Health Target => Unit.CurrentCommand is AttackCommand attack ? attack.Target : null;

        /// <summary>Derived each read; nothing is stored. Debug views show it.</summary>
        public EnemyState State => DeriveState(Unit.IsAlive, Unit.CurrentCommand, Target != null && Attacker.IsInRange(Target));

        CommandableUnit Unit => unit != null ? unit : unit = GetComponent<CommandableUnit>();
        UnitAttacker Attacker => attacker != null ? attacker : attacker = GetComponent<UnitAttacker>();

        internal void Initialize(Encounter encounterToFight, float range = 12f, float interval = 0.25f)
        {
            encounter = encounterToFight;
            detectionRange = range;
            thinkInterval = interval;
        }

        /// <summary>Dead beats everything; an attack order is Chase out of range and Attack in range; otherwise Idle.</summary>
        public static EnemyState DeriveState(bool alive, UnitCommand current, bool inRange)
        {
            if (!alive)
                return EnemyState.Dead;
            if (current is AttackCommand)
                return inRange ? EnemyState.Attack : EnemyState.Chase;
            return EnemyState.Idle;
        }

        /// <summary>
        /// True when nothing but units lies between the eye and the target's centre. Units (anything with a Health in
        /// its parents) never block sight; other colliders do. Uses the given buffer so think ticks allocate nothing.
        /// </summary>
        public static bool HasLineOfSight(Vector3 eye, Health target, LayerMask blockers, RaycastHit[] buffer)
        {
            var toTarget = target.transform.position - eye;
            var distance = toTarget.magnitude;
            if (distance <= 0.001f)
                return true;
            var count = Physics.RaycastNonAlloc(eye, toTarget / distance, buffer, distance, blockers, QueryTriggerInteraction.Ignore);
            for (var i = 0; i < count; i++)
            {
                if (buffer[i].collider.GetComponentInParent<Health>() == null)
                    return false;
            }
            return true;
        }

        void Update()
        {
            if (!SimulationTime.IsRunning || !Unit.IsAlive)
                return;
            if (Time.time < nextThinkTime)
                return;
            nextThinkTime = Time.time + thinkInterval;

            // Busy units are left alone: CommandableUnit runs the chase and the attack.
            if (Unit.CurrentCommand != null || encounter == null)
                return;
            var target = FindNearestVisibleFriendly();
            if (target != null)
                Unit.Issue(new AttackCommand(target));
        }

        Health FindNearestVisibleFriendly()
        {
            Health best = null;
            var bestDistance = float.PositiveInfinity;
            var eye = transform.position + Vector3.up * eyeHeight;
            foreach (var candidate in encounter.Friendlies)
            {
                if (candidate == null || !candidate.IsAlive || !candidate.gameObject.activeInHierarchy)
                    continue;
                var offset = candidate.transform.position - transform.position;
                offset.y = 0f;
                var distance = offset.magnitude;
                if (distance > detectionRange || distance >= bestDistance)
                    continue;
                if (!HasLineOfSight(eye, candidate, sightBlockers, sightHits))
                    continue;
                best = candidate;
                bestDistance = distance;
            }
            return best;
        }
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `Tools/run-tests.sh EditMode` → expected `total="187" passed="187"`, `EXIT=0`. (183 + 4.)
Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.EnemyAIPlayModeTests|Blackglass.Tests.CombatPausePlayModeTests"` → expected `total="14" passed="14"`.
Run: `Tools/run-tests.sh PlayMode` → expected `total="165" passed="165"`, `EXIT=0`. (151 + 14.)

Troubleshooting:
- `FriendlyBehindAWall_IsNotSeen_UntilItStepsOut` acquires at once → the ray is reaching over or around the wall; check the wall is 2 m tall at y = 1 (spans 0–2) and the eye is at 1.5 m.
- `HasLineOfSight_UnitsNeverBlock_WallsDo` fails on the blocker case → the NavMeshAgent snapped a unit elsewhere; read the units' actual positions before asserting, or move `blocker` to exactly between the eye and `seen`.
- `UnreachableVisibleFriendly_...` reaches the friendly → the ring has a gap or the agent steps over 0.6 m; make the walls 0.8 m tall (still below the 1.5 m eye).

- [ ] **Step 6: Commit**

```bash
git add Assets/_Project/Scripts/AI Assets/_Project/Scripts/AI.meta \
  Assets/_Project/Tests/EditMode/EnemyAITests.cs Assets/_Project/Tests/EditMode/EnemyAITests.cs.meta \
  Assets/_Project/Tests/PlayMode/EnemyAIPlayModeTests.cs Assets/_Project/Tests/PlayMode/EnemyAIPlayModeTests.cs.meta \
  Assets/_Project/Tests/PlayMode/CombatPausePlayModeTests.cs Assets/_Project/Tests/PlayMode/CombatPausePlayModeTests.cs.meta \
  Assets/_Project/Tests/PlayMode/TestSupport/TestWorld.cs
git commit -m "Add EnemyAI: idle, see a friendly in range, chase and attack it

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 9: `ActiveCharacter` hands control over when its unit dies

**Files:**
- Modify: `Assets/_Project/Scripts/Controls/ActiveCharacter.cs`
- Modify: `Assets/_Project/Tests/PlayMode/TestSupport/TestWorld.cs` (add `CreateFriendlyFighter`)
- Test: `Assets/_Project/Tests/EditMode/ActiveCharacterTests.cs`, `Assets/_Project/Tests/PlayMode/ActiveCharacterDeathPlayModeTests.cs` (new)

**Interfaces:**
- Consumes: `CommandableUnit.IsAlive`, `ActiveCharacter.Cycle`, `ActiveCharacter.SetUnit`.
- Produces: `public static bool ActiveCharacter.IsEligible(CommandableUnit candidate)` (new overload; the `SelectableUnit` overload delegates to it); `internal void ActiveCharacter.RefreshEligibility()` (called from `Update`): when the unit is no longer eligible, `Cycle(1)`, else `SetUnit(null)`. `ActiveCharacter` gets `[DefaultExecutionOrder(-200)]` so it runs before `DirectControlInput` (-100). `TestWorld.CreateFriendlyFighter(Vector3 groundPosition, int maxHealth = 100)` returns the `SelectableUnit` of a fighter.

- [ ] **Step 1: Write the failing EditMode tests**

First, in `Assets/_Project/Tests/EditMode/ActiveCharacterTests.cs`, change the body of the existing `IsEligible_NullIsFalse` test from `Assert.That(ActiveCharacter.IsEligible(null), Is.False);` to `Assert.That(ActiveCharacter.IsEligible((SelectableUnit)null), Is.False);` (a bare `null` becomes ambiguous once the `CommandableUnit` overload exists).

Then append inside the `ActiveCharacterTests` class (it already has `CreateSquad`, `CreateFriendly`, `KillWithoutDeactivating`, `squad`, `selection`):

```csharp
        // --- Hand-over when the active unit stops being eligible.

        // TryGetComponent, not GetComponent with ??: in the Editor a missing component is a placeholder, not a C# null.
        static void Kill(GameObject host)
        {
            if (!host.TryGetComponent<Health>(out var health))
                health = host.AddComponent<Health>();
            health.TakeDamage(health.Max);
        }

        [Test]
        public void RefreshEligibility_WhileEligible_ChangesNothing()
        {
            CreateSquad();
            active.SetTakeover(true);
            active.RefreshEligibility();
            Assert.That(active.Unit, Is.SameAs(squad[0].Unit));
            Assert.That(active.IsTakeoverOn, Is.True);
        }

        [Test]
        public void RefreshEligibility_WhenTheActiveUnitDies_MovesToTheNextFriendly()
        {
            CreateSquad();
            Kill(squad[0].gameObject);
            active.RefreshEligibility();
            Assert.That(active.Unit, Is.SameAs(squad[1].Unit));
            Assert.That(active.HasUnit, Is.True);
        }

        [Test]
        public void RefreshEligibility_DeadButStillActiveUnit_IsReplaced()
        {
            CreateSquad();
            KillWithoutDeactivating(squad[0].gameObject);
            active.RefreshEligibility();
            Assert.That(active.Unit, Is.SameAs(squad[1].Unit));
        }

        [Test]
        public void RefreshEligibility_WrapsToTheFirstFriendly()
        {
            CreateSquad();
            active.SetUnit(squad[2].Unit);
            Kill(squad[2].gameObject);
            active.RefreshEligibility();
            Assert.That(active.Unit, Is.SameAs(squad[0].Unit));
        }

        [Test]
        public void RefreshEligibility_SkipsDeadFriendlies()
        {
            CreateSquad();
            Kill(squad[0].gameObject);
            Kill(squad[1].gameObject);
            active.RefreshEligibility();
            Assert.That(active.Unit, Is.SameAs(squad[2].Unit));
        }

        [Test]
        public void RefreshEligibility_WithNobodyEligible_ClearsTheUnit_AndKeepsTakeover()
        {
            CreateSquad();
            active.SetTakeover(true);
            foreach (var friendly in squad)
                Kill(friendly.gameObject);
            active.RefreshEligibility();
            Assert.That(active.Unit, Is.Null);
            Assert.That(active.HasUnit, Is.False);
            Assert.That(active.IsDriving, Is.False);
            Assert.That(active.IsTakeoverOn, Is.True, "Takeover mode is the player's choice; death does not change it");
            active.RefreshEligibility();   // idempotent with no unit
            Assert.That(active.Unit, Is.Null);
        }

        [Test]
        public void RefreshEligibility_DeactivatedOrDisabledUnit_IsReplaced()
        {
            CreateSquad();
            squad[0].gameObject.SetActive(false);
            active.RefreshEligibility();
            Assert.That(active.Unit, Is.SameAs(squad[1].Unit));

            squad[1].Unit.enabled = false;
            active.RefreshEligibility();
            Assert.That(active.Unit, Is.SameAs(squad[2].Unit));
        }

        [Test]
        public void RefreshEligibility_WithoutASelection_ClearsADeadUnit()
        {
            Kill(unitHost);
            active.RefreshEligibility();
            Assert.That(active.Unit, Is.Null);
        }

        [Test]
        public void IsEligible_CommandableUnit_Cases()
        {
            Assert.That(ActiveCharacter.IsEligible((CommandableUnit)null), Is.False);
            Assert.That(ActiveCharacter.IsEligible(unit), Is.True, "A living unit without Health or SelectableUnit is eligible");
            unit.enabled = false;
            Assert.That(ActiveCharacter.IsEligible(unit), Is.False);
            unit.enabled = true;
            unitHost.SetActive(false);
            Assert.That(ActiveCharacter.IsEligible(unit), Is.False);
            unitHost.SetActive(true);
            var selectable = unitHost.AddComponent<SelectableUnit>();
            selectable.enabled = false;
            Assert.That(ActiveCharacter.IsEligible(unit), Is.False, "A disabled SelectableUnit makes the unit ineligible");
            selectable.enabled = true;
            KillWithoutDeactivating(unitHost);
            Assert.That(ActiveCharacter.IsEligible(unit), Is.False);
        }

        [Test]
        public void IsEligible_SelectableUnit_AgreesWithTheCommandableUnitOverload()
        {
            CreateSquad();
            Assert.That(ActiveCharacter.IsEligible(squad[0]), Is.EqualTo(ActiveCharacter.IsEligible(squad[0].Unit)));
            Kill(squad[0].gameObject);
            Assert.That(ActiveCharacter.IsEligible(squad[0]), Is.False);
            Assert.That(ActiveCharacter.IsEligible(squad[0].Unit), Is.False);
        }
```

(10 new tests.)

- [ ] **Step 2: Add `CreateFriendlyFighter` and write the failing PlayMode tests**

Add to `TestWorld` after `CreateFighter`:

```csharp
        /// <summary>A fighter the player can select and control (a CreateFighter unit plus SelectableUnit).</summary>
        public SelectableUnit CreateFriendlyFighter(Vector3 groundPosition, int maxHealth = 100)
        {
            var unit = CreateFighter(groundPosition, maxHealth);
            unit.name = "TestFriendlyFighter";
            return unit.gameObject.AddComponent<SelectableUnit>();
        }
```

Create `Assets/_Project/Tests/PlayMode/ActiveCharacterDeathPlayModeTests.cs`:

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
    public class ActiveCharacterDeathPlayModeTests : InputTestFixture
    {
        Keyboard keyboard;
        TestWorld world;
        CommandableUnit first;
        CommandableUnit second;
        CommandableUnit third;
        UnitSelection selection;
        TacticalPause pause;
        ActiveCharacter active;

        public override void Setup()
        {
            base.Setup();
            keyboard = InputSystem.AddDevice<Keyboard>();
            world = new TestWorld();
            world.CreateEnvironment();
            first = world.CreateFriendlyFighter(new Vector3(-6f, 0f, -6f)).Unit;
            second = world.CreateFriendlyFighter(new Vector3(6f, 0f, -6f)).Unit;
            third = world.CreateFriendlyFighter(new Vector3(0f, 0f, -12f)).Unit;
            first.name = "First";
            second.name = "Second";
            third.name = "Third";

            var cameraObject = world.Track(new GameObject("Camera"));
            cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 25f, -20f), Quaternion.Euler(50f, 0f, 0f));
            var viewCamera = cameraObject.AddComponent<Camera>();

            var systems = world.Track(new GameObject("Systems"));
            systems.SetActive(false);
            pause = systems.AddComponent<TacticalPause>();
            selection = systems.AddComponent<UnitSelection>();
            selection.Initialize(SelectableOf(first), SelectableOf(second), SelectableOf(third));
            active = systems.AddComponent<ActiveCharacter>();
            active.Initialize(first, pause, selection);
            var actions = TestControls.Load();
            var input = systems.AddComponent<DirectControlInput>();
            input.Initialize(active, viewCamera,
                TestControls.Ref(actions, "Character/Move"),
                TestControls.Ref(actions, "Character/Takeover"),
                selection,
                TestControls.Ref(actions, "Character/CycleCharacter"),
                TestControls.Ref(actions, "Character/CycleReverse"));
            systems.SetActive(true);
        }

        static SelectableUnit SelectableOf(CommandableUnit unit) => unit.GetComponent<SelectableUnit>();

        static void Kill(CommandableUnit unit)
        {
            var health = unit.GetComponent<Health>();
            health.TakeDamage(health.Max);
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

        [UnityTest]
        public IEnumerator ControlledCharacterDies_WhileDriving_ControlPassesToTheNextFriendly()
        {
            yield return Tap(keyboard.vKey);
            Press(keyboard.wKey);
            yield return new WaitForSeconds(0.3f);
            Assert.That(first.MoveIntent, Is.Not.EqualTo(Vector3.zero), "Precondition: W drives the first unit");
            var secondStart = second.transform.position;

            Kill(first);
            yield return null;
            yield return null;

            Assert.That(active.Unit, Is.SameAs(second), "Control must pass to the next eligible friendly");
            Assert.That(active.IsTakeoverOn, Is.True);
            Assert.That(first.MoveIntent, Is.EqualTo(Vector3.zero), "The dead unit must not keep an intent");
            Assert.That(first.gameObject.activeSelf, Is.False);

            // The new unit is idle, so the held key carries over (decision 012 hybrid rule).
            yield return new WaitForSeconds(0.5f);
            Release(keyboard.wKey);
            yield return null;
            Assert.That(TestWorld.HorizontalDistance(second.transform.position, secondStart), Is.GreaterThan(0.5f),
                "A held key carries over to an idle new unit");
        }

        [UnityTest]
        public IEnumerator ControlledCharacterDies_WhileWIsHeld_NextFriendlyKeepsItsOrders()
        {
            var plan = new MoveCommand(new Vector3(6f, 0f, 6f));
            Assert.That(second.Issue(plan), Is.True);
            yield return Tap(keyboard.vKey);
            Press(keyboard.wKey);
            yield return new WaitForSeconds(0.3f);

            Kill(first);
            yield return new WaitForSeconds(0.5f);

            Assert.That(active.Unit, Is.SameAs(second));
            Assert.That(second.CurrentCommand, Is.SameAs(plan), "A key held across the hand-over must not wipe the new unit's orders");

            Release(keyboard.wKey);
            yield return null;
            Press(keyboard.wKey);
            yield return new WaitForSeconds(0.3f);
            Release(keyboard.wKey);
            yield return null;
            Assert.That(second.CurrentCommand, Is.Null, "A fresh press after release drives the new unit and clears its orders");
        }

        [UnityTest]
        public IEnumerator DeadFriendlies_AreSkippedByTabAndShiftTab()
        {
            Kill(second);
            yield return null;

            yield return Tap(keyboard.tabKey);
            Assert.That(active.Unit, Is.SameAs(third), "Tab must skip the dead second unit");

            Press(keyboard.leftShiftKey);
            yield return null;
            yield return Tap(keyboard.tabKey);
            Release(keyboard.leftShiftKey);
            yield return null;
            Assert.That(active.Unit, Is.SameAs(first), "Shift+Tab must skip the dead second unit");
        }

        [UnityTest]
        public IEnumerator ControlledCharacterDies_TheSelectionIsNotReplaced()
        {
            selection.SetSelection(new[] { SelectableOf(second), SelectableOf(third) });
            yield return null;

            Kill(first);
            yield return null;
            yield return null;

            Assert.That(active.Unit, Is.SameAs(second));
            Assert.That(selection.Selected, Is.EqualTo(new[] { SelectableOf(second), SelectableOf(third) }),
                "Hand-over on death must leave the selection alone");
        }

        [UnityTest]
        public IEnumerator LastFriendlyDies_LeavesNoControlledCharacter_AndInputStaysSafe()
        {
            yield return Tap(keyboard.vKey);
            Kill(first);
            Kill(second);
            yield return null;
            Assert.That(active.Unit, Is.SameAs(third), "Precondition: the last friendly took over");

            Kill(third);
            yield return null;
            yield return null;

            Assert.That(active.Unit, Is.Null);
            Assert.That(active.HasUnit, Is.False);
            Assert.That(active.IsDriving, Is.False);
            Assert.That(selection.Selected, Is.Empty);

            Press(keyboard.wKey);
            yield return new WaitForSeconds(0.2f);
            Release(keyboard.wKey);
            yield return Tap(keyboard.vKey);
            yield return Tap(keyboard.tabKey);
            Press(keyboard.leftShiftKey);
            yield return Tap(keyboard.tabKey);
            Release(keyboard.leftShiftKey);
            yield return null;

            Assert.That(active.Unit, Is.Null, "Nothing is eligible, so nothing becomes active");
            // Any exception or logged error during the inputs above fails the test on its own.
        }
    }
}
#endif
```

(5 new tests.)

Two existing tests in `Assets/_Project/Tests/PlayMode/DirectControlInputTests.cs` assumed a deactivated or destroyed active unit stays active until Tab. That changes with this task, so replace them (same file, same place):

Replace the whole `PrimaryUnitDeactivated_WhileDriving_StopsDrivingWithoutErrors` test with:

```csharp
        [UnityTest]
        public IEnumerator PrimaryUnitDeactivated_WhileDriving_HandsControlToTheNextFriendlyWithoutErrors()
        {
            yield return null;
            active.SetTakeover(true);
            yield return null;
            Press(keyboard.wKey);
            yield return new WaitForSecondsRealtime(0.1f);

            primaryUnit.gameObject.SetActive(false);
            yield return new WaitForSecondsRealtime(0.2f);

            Assert.That(active.Unit, Is.SameAs(companion), "Control must pass to the next eligible friendly");
            Assert.That(primaryUnit.MoveIntent, Is.EqualTo(Vector3.zero), "The old unit must not keep an intent");
            Assert.That(active.IsDriving, Is.True, "Takeover stays on, so the new unit can be driven");
            Release(keyboard.wKey);
            yield return null;
            // Any error or exception logged meanwhile fails the test.
        }
```

Replace the whole `ActiveUnitDestroyed_WhileDriving_ThenTab_MovesOnWithoutErrors` test with:

```csharp
        [UnityTest]
        public IEnumerator ActiveUnitDestroyed_WhileDriving_ControlPassesOn_AndTabMovesFurther()
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
            Assert.That(active.Unit, Is.SameAs(companion), "A destroyed active unit hands control to the next friendly");

            yield return Tap(keyboard.tabKey);

            Assert.That(active.Unit, Is.SameAs(third));
            Assert.That(selection.Selected, Is.EqualTo(new[] { SelectableOf(third) }));
            // Any error or exception logged meanwhile fails the test.
        }
```

(Test count unchanged by these two replacements.)

- [ ] **Step 3: Run the tests to verify they fail**

Run: `Tools/run-tests.sh EditMode Blackglass.Tests.ActiveCharacterTests` → expected `EXIT=3`, `error CS1061: ... 'RefreshEligibility'` and `CS1503`/`CS1502` for `IsEligible((CommandableUnit)null)`.

- [ ] **Step 4: Implement**

In `Assets/_Project/Scripts/Controls/ActiveCharacter.cs`:

(a) Replace the `IsEligible` method and its summary with:

```csharp
        /// <summary>
        /// Whether a unit can be the active character: present, enabled, active in the hierarchy, alive, and if it
        /// has a SelectableUnit, that one enabled too.
        /// </summary>
        public static bool IsEligible(CommandableUnit candidate)
        {
            if (candidate == null || !candidate.enabled || !candidate.gameObject.activeInHierarchy || !candidate.IsAlive)
                return false;
            return !candidate.TryGetComponent<SelectableUnit>(out var selectable) || selectable.enabled;
        }

        /// <summary>Whether a roster unit can become the active character (see the CommandableUnit overload).</summary>
        public static bool IsEligible(SelectableUnit candidate) =>
            candidate != null && candidate.enabled && IsEligible(candidate.Unit);
```

(b) Add after `ToggleTakeover`:

```csharp
        void Update() => RefreshEligibility();

        /// <summary>
        /// Keeps the active character valid: when it dies, is disabled, deactivated or destroyed, control passes to the
        /// next eligible roster unit (roster order, wrapping), or to nobody when none is left. Takeover mode and the
        /// selection are left alone. Runs every frame, paused or not, so the HUD and camera never see a dead unit as
        /// controlled for more than a frame.
        /// </summary>
        internal void RefreshEligibility()
        {
            if (ReferenceEquals(unit, null) || IsEligible(unit))
                return;
            if (!Cycle(1))
                SetUnit(null);
        }
```

(c) Put the component ahead of `DirectControlInput` in the frame. Replace the class declaration line `public sealed class ActiveCharacter : MonoBehaviour` with:

```csharp
    // Hands over before DirectControlInput (-100) reads Unit, so a death never shows as a one-frame HasUnit dip that
    // would re-arm its release gate.
    [DefaultExecutionOrder(-200)]
    public sealed class ActiveCharacter : MonoBehaviour
```

(`using UnityEngine;` is already present.) On the frame after a death, `ActiveCharacter.Update` switches first; `DirectControlInput.Update` then sees the new unit, zeroes the dead unit's intent in `HandOver` and arms the release gate only if a key is held and the new unit has orders (decision 012). `IsDriving` never reads false between two living units.

(d) Update the class summary's first sentence to end with "...the marker and the HUD read it. When the active character stops being eligible (it dies), control moves to the next eligible roster unit, or to nobody."

- [ ] **Step 5: Run the tests to verify they pass**

Run: `Tools/run-tests.sh EditMode` → expected `total="197" passed="197"`, `EXIT=0`. (187 + 10.)
Run: `Tools/run-tests.sh PlayMode` → expected `total="170" passed="170"`, `EXIT=0`. (165 + 5.)

If `ControlledCharacterDies_WhileDriving_...` fails on the carry-over distance, check that `ActiveCharacter` carries `[DefaultExecutionOrder(-200)]`: without it `DirectControlInput` (-100) sees `IsDriving` false for one frame while the dead unit is still assigned, and `driving && !wasDriving` re-arms the release gate on the next frame.

- [ ] **Step 6: Commit**

```bash
git add Assets/_Project/Scripts/Controls/ActiveCharacter.cs Assets/_Project/Tests/EditMode/ActiveCharacterTests.cs \
  Assets/_Project/Tests/PlayMode/TestSupport/TestWorld.cs Assets/_Project/Tests/PlayMode/DirectControlInputTests.cs \
  Assets/_Project/Tests/PlayMode/ActiveCharacterDeathPlayModeTests.cs Assets/_Project/Tests/PlayMode/ActiveCharacterDeathPlayModeTests.cs.meta
git commit -m "Pass control to the next friendly when the controlled character dies

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 10: HUD: health labels, enemy state, sides and outcome

**Files:**
- Modify: `Assets/_Project/Scripts/DebugUI/PrototypeHud.cs`
- Test: `Assets/_Project/Tests/EditMode/PrototypeHudTests.cs`

**Interfaces:**
- Consumes: `Encounter`, `EnemyAI.State`/`Target`, `UnitAttacker.CooldownRemaining`, `Health.Current`/`Max`.
- Produces: `PrototypeHud` serialized field `encounter` (used by the scene builder in Task 11); fields `target` and `targetLabel` removed. Static formatters: `DescribeSides(int livingFriendlies, int friendlies, int livingHostiles, int hostiles)`, `DescribeNoActive()`, `DescribeUnit(string unitName, int current, int max)`, `DescribeEnemy(EnemyState state, string targetName, float cooldownRemaining)`, `AppendCooldown(string text, float cooldownRemaining)`, `DescribeOutcome(EncounterOutcome outcome)`.

- [ ] **Step 1: Write the failing tests**

Append inside the `PrototypeHudTests` class:

```csharp
        [Test]
        public void DescribeSides_CountsBothSides()
        {
            Assert.That(PrototypeHud.DescribeSides(2, 3, 0, 3), Is.EqualTo("Friendlies alive 2/3 | Hostiles alive 0/3"));
        }

        [Test]
        public void DescribeNoActive_SaysNone()
        {
            Assert.That(PrototypeHud.DescribeNoActive(), Is.EqualTo("Controlled: none"));
        }

        [Test]
        public void DescribeUnit_NameAndHealth()
        {
            Assert.That(PrototypeHud.DescribeUnit("FriendlyUnit_2", 75, 100), Is.EqualTo("FriendlyUnit_2 75/100"));
        }

        [TestCase(EnemyState.Idle, null, 0f, "Idle")]
        [TestCase(EnemyState.Chase, "FriendlyUnit_1", 0f, "Chase -> FriendlyUnit_1")]
        [TestCase(EnemyState.Attack, "FriendlyUnit_1", 0.44f, "Attack -> FriendlyUnit_1 CD 0.4")]
        [TestCase(EnemyState.Dead, null, 0f, "Dead")]
        public void DescribeEnemy_StateTargetAndCooldown(EnemyState state, string target, float cooldown, string expected)
        {
            Assert.That(PrototypeHud.DescribeEnemy(state, target, cooldown), Is.EqualTo(expected));
        }

        [Test]
        public void AppendCooldown_OnlyWhileCoolingDown()
        {
            Assert.That(PrototypeHud.AppendCooldown("Attack", 0f), Is.EqualTo("Attack"));
            Assert.That(PrototypeHud.AppendCooldown("Attack", 0.96f), Is.EqualTo("Attack CD 1.0"));
            Assert.That(PrototypeHud.AppendCooldown("", 0.5f), Is.EqualTo("CD 0.5"));
        }

        [TestCase(EncounterOutcome.Ongoing, "")]
        [TestCase(EncounterOutcome.Victory, "VICTORY - all hostiles are down")]
        [TestCase(EncounterOutcome.Defeat, "DEFEAT - the squad is down")]
        public void DescribeOutcome_BannerText(EncounterOutcome outcome, string expected)
        {
            Assert.That(PrototypeHud.DescribeOutcome(outcome), Is.EqualTo(expected));
        }
```

(4 + 4 + 3 = 11 new test cases.)

- [ ] **Step 2: Run the tests to verify they fail**

Run: `Tools/run-tests.sh EditMode Blackglass.Tests.PrototypeHudTests` → expected `EXIT=3`, `error CS0117: 'PrototypeHud' does not contain a definition for 'DescribeSides'` (and the others).

- [ ] **Step 3: Implement**

Replace `Assets/_Project/Scripts/DebugUI/PrototypeHud.cs` with:

```csharp
using UnityEngine;
using UnityEngine.Serialization;

namespace Blackglass
{
    /// <summary>Debug-only on-screen text (IMGUI). Not production UI. Works while paused.</summary>
    public sealed class PrototypeHud : MonoBehaviour
    {
        const string ControlHints =
            "WASD: pan camera   Q/E: rotate   Right-drag: rotate/tilt   Wheel: zoom   V: takeover (WASD moves character)\n" +
            "Left-click unit: select (Shift: add/remove)   Left-drag: box select   Esc: clear selection\n" +
            "Left-click ground/enemy: controlled character moves/attacks (paused: selected units)   Shift: queue   X: stop selected\n" +
            "Space: tactical pause   Tab / Shift+Tab: switch controlled character";
        // Unit labels float this far above a unit's centre (the capsule is 2 m tall).
        const float UnitLabelHeight = 1.5f;

        [SerializeField] TacticalPause tacticalPause;
        [SerializeField] Encounter encounter;
        [SerializeField] Camera viewCamera;
        [SerializeField] UnitSelection selection;
        [SerializeField] PlayerCommandInput commandInput;
        [SerializeField, FormerlySerializedAs("primary")] ActiveCharacter activeCharacter;

        GUIStyle pausedStyle;
        GUIStyle outcomeStyle;
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

        /// <summary>One status line for the active character, such as "Controlled: Ana | Takeover ON (V) | Manual control".</summary>
        internal static string DescribeActive(string unitName, bool takeoverOn, bool isPaused, bool hasOrders)
        {
            var mode = !takeoverOn ? "Takeover OFF (V)" : isPaused ? "Takeover ON (after pause)" : "Takeover ON (V)";
            var activity = hasOrders ? "Following orders" : takeoverOn && !isPaused ? "Manual control" : "Idle";
            return $"Controlled: {unitName} | {mode} | {activity}";
        }

        internal static string DescribeNoActive() => "Controlled: none";

        internal static string DescribeSides(int livingFriendlies, int friendlies, int livingHostiles, int hostiles) =>
            $"Friendlies alive {livingFriendlies}/{friendlies} | Hostiles alive {livingHostiles}/{hostiles}";

        internal static string DescribeUnit(string unitName, int current, int max) => $"{unitName} {current}/{max}";

        /// <summary>A hostile's line: its AI state, its target if any, and the cooldown while one runs.</summary>
        internal static string DescribeEnemy(EnemyState state, string targetName, float cooldownRemaining)
        {
            var text = string.IsNullOrEmpty(targetName) ? state.ToString() : $"{state} -> {targetName}";
            return AppendCooldown(text, cooldownRemaining);
        }

        internal static string AppendCooldown(string text, float cooldownRemaining)
        {
            if (cooldownRemaining <= 0f)
                return text;
            var cooldown = $"CD {cooldownRemaining:0.0}";
            return text.Length == 0 ? cooldown : $"{text} {cooldown}";
        }

        internal static string DescribeOutcome(EncounterOutcome outcome)
        {
            switch (outcome)
            {
                case EncounterOutcome.Victory:
                    return "VICTORY - all hostiles are down";
                case EncounterOutcome.Defeat:
                    return "DEFEAT - the squad is down";
                default:
                    return string.Empty;
            }
        }

        void OnGUI()
        {
            GUI.Label(new Rect(10f, 10f, 820f, 80f), ControlHints);

            if (encounter != null)
            {
                GUI.Label(new Rect(10f, 95f, 420f, 22f), DescribeSides(encounter.LivingFriendlies, encounter.Friendlies.Count,
                    encounter.LivingHostiles, encounter.Hostiles.Count));
            }

            if (selection != null)
                GUI.Label(new Rect(10f, 115f, 320f, 22f), $"Selected: {selection.Selected.Count}");

            if (activeCharacter != null)
            {
                var text = activeCharacter.HasUnit
                    ? DescribeActive(activeCharacter.Unit.name, activeCharacter.IsTakeoverOn, activeCharacter.IsPaused,
                        activeCharacter.Unit.CurrentCommand != null)
                    : DescribeNoActive();
                GUI.Label(new Rect(10f, 135f, 640f, 22f), text);
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

            if (encounter != null)
            {
                var outcome = DescribeOutcome(encounter.Outcome);
                if (outcome.Length > 0)
                {
                    outcomeStyle ??= new GUIStyle(GUI.skin.label)
                    {
                        alignment = TextAnchor.UpperCenter,
                        fontSize = 30,
                        fontStyle = FontStyle.Bold,
                    };
                    GUI.Label(new Rect(0f, 205f, Screen.width, 50f), outcome, outcomeStyle);
                }
            }
        }

        void DrawUnitLabels()
        {
            if (encounter == null || viewCamera == null)
                return;
            unitLabelStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperCenter };
            foreach (var health in encounter.Friendlies)
                DrawUnitLabel(health, false);
            foreach (var health in encounter.Hostiles)
                DrawUnitLabel(health, true);
        }

        // Debug only: a handful of units, so per-frame GetComponent calls are fine here.
        void DrawUnitLabel(Health health, bool hostile)
        {
            if (health == null || !health.IsAlive || !health.gameObject.activeInHierarchy)
                return;
            var text = DescribeUnit(health.name, health.Current, health.Max);
            var cooldown = health.TryGetComponent<UnitAttacker>(out var attacker) ? attacker.CooldownRemaining : 0f;
            string activity;
            if (hostile && health.TryGetComponent<EnemyAI>(out var ai))
                activity = DescribeEnemy(ai.State, ai.Target != null ? ai.Target.name : null, cooldown);
            else if (health.TryGetComponent<CommandableUnit>(out var unit))
                activity = AppendCooldown(DescribeOrders(unit.CurrentCommand, unit.PendingCommands.Count), cooldown);
            else
                activity = string.Empty;
            if (activity.Length > 0)
                text += "\n" + activity;

            var screen = viewCamera.WorldToScreenPoint(health.transform.position + Vector3.up * UnitLabelHeight);
            if (screen.z <= 0f)
                return;
            GUI.Label(new Rect(screen.x - 80f, Screen.height - screen.y - 22f, 160f, 44f), text, unitLabelStyle);
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `Tools/run-tests.sh EditMode` → expected `total="208" passed="208"`, `EXIT=0`. (197 + 11.)
Run: `Tools/run-tests.sh PlayMode` → expected `total="170" passed="170"`, `EXIT=0`. The scene test `Scene_ContainsWiredSquad_AndRunsWithoutErrors` still passes: the HUD's stale `target` data is ignored until the scene is re-saved in Task 11.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/DebugUI/PrototypeHud.cs Assets/_Project/Tests/EditMode/PrototypeHudTests.cs
git commit -m "Show health, enemy state, sides and the outcome in the debug HUD

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 11: Prefabs, scene encounter and scene tests

**Files:**
- Modify: `Assets/_Project/Tests/PlayMode/PrototypeSceneTests.cs`
- Create (temporary, never committed): `Assets/_Project/Editor/CombatSceneBuilder.cs`
- Modify (generated): `Assets/_Project/Prefabs/FriendlyUnit.prefab`, `Assets/_Project/Scenes/Prototype.unity`, `Assets/_Project/Scenes/Prototype/NavMesh-Environment.asset` (+`.meta`)
- Create (generated): `Assets/_Project/Prefabs/HostileUnit.prefab` (+`.meta`), `Assets/_Project/Materials/DeathMarker.mat`, `Assets/_Project/Materials/AttackLine.mat` (+`.meta`s)
- Rename (generated): `Assets/_Project/Materials/Dummy.mat` (+`.meta`) → `Hostile.mat` (+`.meta`), GUID kept

**Interfaces:**
- Consumes: serialized field names `DeathMarker.markerMaterial`, `AttackLineView.line`, `EnemyAI.encounter`, `Encounter.friendlies`/`hostiles`, `PrototypeHud.encounter`, `UnitMover.speed`, `UnitAttacker.range`/`damage`/`cooldown`, `Health.max`.
- Produces: `Prototype.unity` with no `TrainingDummy`, `HostileUnit_1..3`, `Obstacle_E`/`Obstacle_F`, a rebaked NavMesh, `Encounter` on `Systems` wired to both sides, every `EnemyAI` and the HUD wired.

- [ ] **Step 1: Update the scene tests**

Replace `Assets/_Project/Tests/PlayMode/PrototypeSceneTests.cs` with:

```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class PrototypeSceneTests : InputTestFixture
    {
        static readonly string[] FriendlyNames = { "FriendlyUnit_1", "FriendlyUnit_2", "FriendlyUnit_3" };
        static readonly string[] HostileNames = { "HostileUnit_1", "HostileUnit_2", "HostileUnit_3" };

        CommandableUnit unit;
        Health firstHostile;
        TacticalPause pause;
        UnitSelection selection;
        Encounter encounter;

        // Loaded from each test rather than [UnitySetUp]: the scene must load after InputTestFixture has isolated the
        // input system, otherwise the scene's actions (shared InputActionAsset) leak into later fixtures.
        IEnumerator LoadScene()
        {
            yield return SceneManager.LoadSceneAsync("Prototype", LoadSceneMode.Single);
            yield return null;
            unit = GameObject.Find(FriendlyNames[0]).GetComponent<CommandableUnit>();
            firstHostile = FindHostileHealth(HostileNames[0]);
            pause = Object.FindFirstObjectByType<TacticalPause>();
            selection = Object.FindFirstObjectByType<UnitSelection>();
            encounter = Object.FindFirstObjectByType<Encounter>();
        }

        internal static CommandableUnit[] FindSquad() =>
            FriendlyNames.Select(n => GameObject.Find(n).GetComponent<CommandableUnit>()).ToArray();

        internal static EnemyAI[] FindHostiles() =>
            HostileNames.Select(n => GameObject.Find(n).GetComponent<EnemyAI>()).ToArray();

        internal static Health FindHostileHealth(string name) => GameObject.Find(name).GetComponent<Health>();

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
        public IEnumerator Scene_ContainsWiredSquadAndHostiles_AndRunsWithoutErrors()
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
                Assert.That(friendly.GetComponent<Health>(), Is.Not.Null, $"{friendly.name} has no Health");
                Assert.That(friendly.GetComponent<Health>().Max, Is.EqualTo(100), friendly.name);
                Assert.That(friendly.GetComponent<HitFlash>(), Is.Not.Null, friendly.name);
                Assert.That(friendly.GetComponent<DeathMarker>(), Is.Not.Null, friendly.name);
                Assert.That(friendly.GetComponent<AutoRetaliate>(), Is.Not.Null, friendly.name);
                Assert.That(friendly.GetComponent<AttackLineView>(), Is.Not.Null, friendly.name);
                Assert.That(friendly.GetComponent<EnemyAI>(), Is.Null, $"{friendly.name} must not have enemy AI");
                Assert.That(friendly.GetComponentsInChildren<Collider>(true), Has.Length.EqualTo(1),
                    $"{friendly.name}: only the capsule may have a collider, so debug visuals never block clicks");
            }

            Assert.That(GameObject.Find("TrainingDummy"), Is.Null, "The training dummy should be gone");
            var hostiles = Object.FindObjectsByType<EnemyAI>(FindObjectsSortMode.None);
            Assert.That(hostiles.Select(h => h.name), Is.EquivalentTo(HostileNames));
            foreach (var hostile in hostiles)
            {
                Assert.That(hostile.GetComponent<Health>().Max, Is.EqualTo(60), hostile.name);
                Assert.That(hostile.GetComponent<UnitAttacker>().Damage, Is.EqualTo(10), hostile.name);
                Assert.That(hostile.GetComponent<UnitAttacker>().Cooldown, Is.EqualTo(1.2f).Within(0.001f), hostile.name);
                Assert.That(hostile.GetComponent<HitFlash>(), Is.Not.Null, hostile.name);
                Assert.That(hostile.GetComponent<DeathMarker>(), Is.Not.Null, hostile.name);
                Assert.That(hostile.GetComponent<AutoRetaliate>(), Is.Not.Null, hostile.name);
                Assert.That(hostile.GetComponent<AttackLineView>(), Is.Not.Null, hostile.name);
                Assert.That(hostile.GetComponent<SelectableUnit>(), Is.Null, $"{hostile.name} must not be selectable");
                Assert.That(hostile.GetComponentsInChildren<Collider>(true), Has.Length.EqualTo(1), hostile.name);
                Assert.That(hostile.State, Is.EqualTo(EnemyState.Idle), $"{hostile.name} should start idle, out of range of the squad");
            }

            Assert.That(encounter, Is.Not.Null, "Encounter missing");
            Assert.That(encounter.Friendlies.Select(h => h.name), Is.EquivalentTo(FriendlyNames));
            Assert.That(encounter.Hostiles.Select(h => h.name), Is.EquivalentTo(HostileNames));
            Assert.That(encounter.Outcome, Is.EqualTo(EncounterOutcome.Ongoing));
            Assert.That(GameObject.Find("Obstacle_E"), Is.Not.Null);
            Assert.That(GameObject.Find("Obstacle_F"), Is.Not.Null);

            Assert.That(pause, Is.Not.Null, "TacticalPause missing");
            Assert.That(Object.FindFirstObjectByType<PlayerCommandInput>(), Is.Not.Null);
            Assert.That(Object.FindFirstObjectByType<TacticalCameraController>(), Is.Not.Null);
            Assert.That(Object.FindFirstObjectByType<PrototypeHud>(), Is.Not.Null);
            var active = Object.FindFirstObjectByType<ActiveCharacter>();
            Assert.That(active, Is.Not.Null, "ActiveCharacter missing");
            Assert.That(active.Unit, Is.Not.Null, "ActiveCharacter has no unit");
            Assert.That(active.Unit.name, Is.EqualTo(FriendlyNames[0]), "The game starts controlling FriendlyUnit_1");
            Assert.That(active.IsTakeoverOn, Is.False, "The game starts in free mode");
            var marker = Object.FindFirstObjectByType<ActiveCharacterMarker>();
            Assert.That(marker, Is.Not.Null, "ActiveMarker missing");
            Assert.That(marker.GetComponentsInChildren<Collider>(true), Is.Empty, "The marker must not block clicks");
            Assert.That(Object.FindFirstObjectByType<DirectControlInput>(), Is.Not.Null, "DirectControlInput missing");
            Assert.That(Camera.main, Is.Not.Null);

            // Any error or exception logged during this second fails the test automatically.
            yield return new WaitForSeconds(1f);
            Assert.That(hostiles.All(h => h.State == EnemyState.Idle), Is.True, "Hostiles must stay idle while the squad is far away");
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
        public IEnumerator Squad_AttacksEveryHostile_AndWins()
        {
            yield return LoadScene();
            var squad = FindSquad();
            var hostiles = HostileNames.Select(FindHostileHealth).ToArray();
            Assert.That(GroupOrders.Issue(squad, new AttackCommand(hostiles[0]), IssueMode.Replace), Is.EqualTo(3));
            Assert.That(GroupOrders.Issue(squad, new AttackCommand(hostiles[1]), IssueMode.Append), Is.EqualTo(3));
            Assert.That(GroupOrders.Issue(squad, new AttackCommand(hostiles[2]), IssueMode.Append), Is.EqualTo(3));

            yield return TestWorld.WaitUntil(() => encounter.Outcome != EncounterOutcome.Ongoing, 60f);

            Assert.That(encounter.Outcome, Is.EqualTo(EncounterOutcome.Victory), "The squad should beat three hostiles");
            Assert.That(hostiles.All(h => !h.IsAlive && !h.gameObject.activeSelf), Is.True);
            Assert.That(encounter.LivingFriendlies, Is.GreaterThan(0));
            foreach (var name in HostileNames)
                Assert.That(GameObject.Find($"{name} (dead)"), Is.Not.Null, $"{name} left no corpse marker");
            Assert.That(PrototypeHud.DescribeOutcome(encounter.Outcome), Does.StartWith("VICTORY"));
        }

        [UnityTest]
        public IEnumerator FriendlyWalksIntoView_HostilesEngageIt()
        {
            yield return LoadScene();
            var hostiles = FindHostiles();
            var health = unit.GetComponent<Health>();
            // The gap between Obstacle_E and Obstacle_F, in plain sight of HostileUnit_1.
            Assert.That(unit.Issue(new MoveCommand(new Vector3(10f, 0f, 6f))), Is.True);

            yield return TestWorld.WaitUntil(() => hostiles.Any(h => h.Target == health) || health.Current < health.Max, 25f);

            Assert.That(hostiles.Any(h => h.Target == health) || health.Current < health.Max, Is.True,
                "No hostile engaged the friendly that walked into view");
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
        public IEnumerator RepeatedPauseDuringTheFight_InScene_LogsNoErrors()
        {
            yield return LoadScene();
            var squad = FindSquad();
            GroupOrders.Issue(squad, new AttackCommand(firstHostile), IssueMode.Replace);
            yield return TestWorld.WaitUntil(() => firstHostile.Current < firstHostile.Max, 20f);
            Assert.That(firstHostile.Current, Is.LessThan(firstHostile.Max), "Precondition: the fight started");

            for (var i = 0; i < 6; i++)
            {
                pause.Pause();
                yield return new WaitForSecondsRealtime(0.15f);
                pause.Resume();
                yield return new WaitForSeconds(0.2f);
            }

            Assert.That(pause.IsPaused, Is.False);
            Assert.That(encounter.Outcome, Is.Not.EqualTo(EncounterOutcome.Defeat));
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
            Time.timeScale = 1f;
            PrototypeSceneTests.DestroySceneObjects();
            base.TearDown();
        }

        static IEnumerator LoadScene()
        {
            yield return SceneManager.LoadSceneAsync("Prototype", LoadSceneMode.Single);
            yield return null;
        }

        static Health FirstHostile() => PrototypeSceneTests.FindHostileHealth("HostileUnit_1");

        IEnumerator LeftClickAt(Mouse mouse, Vector2 screenPoint)
        {
            Set(mouse.position, screenPoint);
            yield return null;
            Press(mouse.leftButton);
            yield return null;
            Release(mouse.leftButton);
            yield return null;
        }

        IEnumerator Tap(KeyControl key)
        {
            Press(key);
            yield return null;
            Release(key);
            yield return null;
        }

        [UnityTest]
        public IEnumerator PausedClickCompanionThenHostile_InScene_OnlyThatUnitAttacks()
        {
            var mouse = InputSystem.AddDevice<Mouse>();
            var keyboard = InputSystem.AddDevice<Keyboard>();
            yield return LoadScene();
            var squad = PrototypeSceneTests.FindSquad();
            var hostile = FirstHostile();

            yield return Tap(keyboard.spaceKey);
            yield return LeftClickAt(mouse, Camera.main.WorldToScreenPoint(squad[1].transform.position));
            yield return LeftClickAt(mouse, Camera.main.WorldToScreenPoint(hostile.transform.position));

            Assert.That(squad[1].CurrentCommand, Is.TypeOf<AttackCommand>());
            Assert.That(((AttackCommand)squad[1].CurrentCommand).Target, Is.SameAs(hostile));
            Assert.That(squad[0].CurrentCommand, Is.Null);
            Assert.That(squad[2].CurrentCommand, Is.Null);
        }

        // Drags a selection box around every squad member.
        IEnumerator BoxSelect(Mouse mouse, CommandableUnit[] squad)
        {
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
        }

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

        [UnityTest]
        public IEnumerator StopAndClearSelection_InScene_UseTheSceneBindings()
        {
            var mouse = InputSystem.AddDevice<Mouse>();
            var keyboard = InputSystem.AddDevice<Keyboard>();
            yield return LoadScene();
            var squad = PrototypeSceneTests.FindSquad();
            var selection = Object.FindFirstObjectByType<UnitSelection>();
            yield return Tap(keyboard.spaceKey);   // paused: clicks order the selection

            yield return BoxSelect(mouse, squad);
            Assert.That(selection.Selected, Has.Count.EqualTo(3));
            yield return LeftClickAt(mouse, Camera.main.WorldToScreenPoint(new Vector3(-6f, 0f, 0f)));
            foreach (var member in squad)
                Assert.That(member.CurrentCommand, Is.TypeOf<MoveCommand>(), member.name);

            Press(keyboard.leftShiftKey);
            yield return null;
            yield return LeftClickAt(mouse, Camera.main.WorldToScreenPoint(new Vector3(-6f, 0f, 4f)));
            Release(keyboard.leftShiftKey);
            yield return null;
            foreach (var member in squad)
                Assert.That(member.PendingCommands, Has.Count.EqualTo(1), $"{member.name}: Shift-click did not queue an order");

            Press(keyboard.xKey);
            yield return null;
            Release(keyboard.xKey);
            yield return null;
            foreach (var member in squad)
            {
                Assert.That(member.CurrentCommand, Is.Null, $"{member.name}: X did not stop the unit");
                Assert.That(member.PendingCommands, Is.Empty, member.name);
            }

            Press(keyboard.escapeKey);
            yield return null;
            Release(keyboard.escapeKey);
            yield return null;
            Assert.That(selection.Selected, Is.Empty, "Esc did not clear the selection");
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

        [UnityTest]
        public IEnumerator RealTimeClickOnHostile_InScene_TheControlledCharacterAttacks()
        {
            var mouse = InputSystem.AddDevice<Mouse>();
            InputSystem.AddDevice<Keyboard>();
            yield return LoadScene();
            var squad = PrototypeSceneTests.FindSquad();
            var hostile = FirstHostile();

            yield return LeftClickAt(mouse, Camera.main.WorldToScreenPoint(hostile.transform.position));

            Assert.That(squad[0].CurrentCommand, Is.TypeOf<AttackCommand>(), "The controlled character did not attack");
            Assert.That(((AttackCommand)squad[0].CurrentCommand).Target, Is.SameAs(hostile));
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

        [UnityTest]
        public IEnumerator Tab_InScene_SwitchesToTheNextFriendly_AndItsClicksAttack()
        {
            var mouse = InputSystem.AddDevice<Mouse>();
            var keyboard = InputSystem.AddDevice<Keyboard>();
            yield return LoadScene();
            var squad = PrototypeSceneTests.FindSquad();
            var hostile = FirstHostile();
            var active = Object.FindFirstObjectByType<ActiveCharacter>();
            var selection = Object.FindFirstObjectByType<UnitSelection>();
            var marker = Object.FindFirstObjectByType<ActiveCharacterMarker>();

            yield return Tap(keyboard.tabKey);
            Assert.That(active.Unit, Is.SameAs(squad[1]), "Tab did not switch to FriendlyUnit_2");
            Assert.That(selection.Selected, Is.EqualTo(new[] { squad[1].GetComponent<SelectableUnit>() }));
            yield return new WaitForSecondsRealtime(1f);   // let the camera finish focusing before aiming the click

            Assert.That(TestWorld.HorizontalDistance(marker.transform.position, squad[1].transform.position), Is.LessThan(0.01f),
                "The marker did not move to the new active character");
            var hostileOnScreen = Camera.main.WorldToScreenPoint(hostile.transform.position);
            Assert.That(new Rect(0f, 0f, Screen.width, Screen.height).Contains(hostileOnScreen), Is.True,
                "Precondition: HostileUnit_1 is on screen after the camera focused FriendlyUnit_2");
            yield return LeftClickAt(mouse, hostileOnScreen);

            Assert.That(squad[1].CurrentCommand, Is.TypeOf<AttackCommand>(), "The attack must come from FriendlyUnit_2");
            Assert.That(((AttackCommand)squad[1].CurrentCommand).Target, Is.SameAs(hostile));
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
    }
}
#endif
```

(Net +2 tests: `FriendlyWalksIntoView_HostilesEngageIt` and `RepeatedPauseDuringTheFight_InScene_LogsNoErrors` are new; `Attack_DestroysTrainingDummy` became `Squad_AttacksEveryHostile_AndWins`; the dummy click tests now click `HostileUnit_1`.)

- [ ] **Step 2: Run the scene tests to verify they fail**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.PrototypeSceneTests|Blackglass.Tests.PrototypeSceneInputTests"` → expected `EXIT=2`: every test that looks up a hostile fails with a `NullReferenceException` from `GameObject.Find("HostileUnit_1")`, and the wiring test fails on "The training dummy should be gone". `Move_AroundTheCentralWall_Arrives`, `Pause_HoldsTheUnitUntilResume`, `GroupMove_...`, `HoldingW_...`, `VThenW_...`, `PausedSquadOrder_...`, `ShiftTab_...` still pass.

- [ ] **Step 3: Create the temporary scene builder**

Create `Assets/_Project/Editor/CombatSceneBuilder.cs`:

```csharp
// TEMPORARY: builds the Phase 4 encounter into the prefabs and Prototype.unity, then this file is deleted (never committed).
using System;
using System.Linq;
using Blackglass;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

public static class CombatSceneBuilder
{
    const string ScenePath = "Assets/_Project/Scenes/Prototype.unity";
    const string NavMeshPath = "Assets/_Project/Scenes/Prototype/NavMesh-Environment.asset";
    const string FriendlyPrefabPath = "Assets/_Project/Prefabs/FriendlyUnit.prefab";
    const string HostilePrefabPath = "Assets/_Project/Prefabs/HostileUnit.prefab";
    const string DummyMaterialPath = "Assets/_Project/Materials/Dummy.mat";
    const string HostileMaterialPath = "Assets/_Project/Materials/Hostile.mat";
    const string ObstacleMaterialPath = "Assets/_Project/Materials/Obstacle.mat";

    static readonly Vector3[] HostileSpawns =
    {
        new Vector3(8f, 1f, 8f),
        new Vector3(13f, 1f, 10f),
        new Vector3(4f, 1f, 13f),
    };

    public static void Build()
    {
        if (AssetDatabase.LoadAssetAtPath<Material>(DummyMaterialPath) != null)
        {
            var renameError = AssetDatabase.RenameAsset(DummyMaterialPath, "Hostile");
            if (!string.IsNullOrEmpty(renameError))
                throw new Exception("Could not rename Dummy.mat: " + renameError);
        }
        var hostileMaterial = Require(AssetDatabase.LoadAssetAtPath<Material>(HostileMaterialPath), HostileMaterialPath);
        var obstacleMaterial = Require(AssetDatabase.LoadAssetAtPath<Material>(ObstacleMaterialPath), ObstacleMaterialPath);
        var deathMaterial = CreateUnlitMaterial("DeathMarker", new Color(0.25f, 0.25f, 0.25f));
        var lineMaterial = CreateUnlitMaterial("AttackLine", new Color(1f, 0.85f, 0.2f));

        UpdateFriendlyUnitPrefab(deathMaterial, lineMaterial);
        var hostilePrefab = CreateHostileUnitPrefab(hostileMaterial, deathMaterial, lineMaterial);

        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var systems = Require(GameObject.Find("Systems"), "Systems");
        var environment = Require(GameObject.Find("Environment"), "Environment");
        var dummy = Require(GameObject.Find("TrainingDummy"), "TrainingDummy");
        if (systems.GetComponent<Encounter>() != null)
            throw new Exception("Systems already has an Encounter; the builder has already run.");
        Object.DestroyImmediate(dummy);

        CreateObstacle(environment, "Obstacle_E", new Vector3(6f, 1f, 3f), new Vector3(5f, 2f, 1f), obstacleMaterial);
        CreateObstacle(environment, "Obstacle_F", new Vector3(12f, 1f, 3f), new Vector3(1f, 2f, 6f), obstacleMaterial);
        var surface = Require(environment.GetComponent<NavMeshSurface>(), "NavMeshSurface on Environment");
        surface.BuildNavMesh();
        AssetDatabase.DeleteAsset(NavMeshPath);
        AssetDatabase.CreateAsset(surface.navMeshData, NavMeshPath);

        var encounter = systems.AddComponent<Encounter>();
        var hostiles = new Health[HostileSpawns.Length];
        for (var i = 0; i < HostileSpawns.Length; i++)
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(hostilePrefab, scene);
            instance.name = $"HostileUnit_{i + 1}";
            instance.transform.position = HostileSpawns[i];
            hostiles[i] = instance.GetComponent<Health>();
            SetReference(instance.GetComponent<EnemyAI>(), "encounter", encounter);
        }
        var friendlies = new[] { "FriendlyUnit_1", "FriendlyUnit_2", "FriendlyUnit_3" }
            .Select(name => Require(GameObject.Find(name), name).GetComponent<Health>())
            .ToArray();
        if (friendlies.Any(h => h == null))
            throw new Exception("A FriendlyUnit instance has no Health; the prefab update did not reach the scene.");
        SetHealthList(encounter, "friendlies", friendlies);
        SetHealthList(encounter, "hostiles", hostiles);

        var hud = Require(systems.GetComponent<PrototypeHud>(), "PrototypeHud on Systems");
        SetReference(hud, "encounter", encounter);

        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene))
            throw new Exception("Failed to save " + ScenePath);
        AssetDatabase.SaveAssets();
        Debug.Log($"[CombatSceneBuilder] Built the combat encounter into {ScenePath}");
    }

    static void UpdateFriendlyUnitPrefab(Material deathMaterial, Material lineMaterial)
    {
        var root = PrefabUtility.LoadPrefabContents(FriendlyPrefabPath);
        try
        {
            if (root.GetComponent<Health>() != null)
                throw new Exception("FriendlyUnit already has Health; the builder has already run.");
            root.AddComponent<Health>();   // max 100 by default
            root.AddComponent<HitFlash>();
            SetReference(root.AddComponent<DeathMarker>(), "markerMaterial", deathMaterial);
            root.AddComponent<AutoRetaliate>();
            AddAttackLine(root, lineMaterial);
            PrefabUtility.SaveAsPrefabAsset(root, FriendlyPrefabPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    static GameObject CreateHostileUnitPrefab(Material hostileMaterial, Material deathMaterial, Material lineMaterial)
    {
        var unit = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        unit.name = "HostileUnit";
        unit.GetComponent<Renderer>().sharedMaterial = hostileMaterial;
        var mover = unit.AddComponent<UnitMover>();
        SetFloat(mover, "speed", 3.5f);
        unit.GetComponent<NavMeshAgent>().baseOffset = 1f;
        var attacker = unit.AddComponent<UnitAttacker>();
        SetFloat(attacker, "range", 2f);
        SetInt(attacker, "damage", 10);
        SetFloat(attacker, "cooldown", 1.2f);
        SetInt(unit.AddComponent<Health>(), "max", 60);
        unit.AddComponent<CommandableUnit>();
        unit.AddComponent<HitFlash>();
        SetReference(unit.AddComponent<DeathMarker>(), "markerMaterial", deathMaterial);
        unit.AddComponent<AutoRetaliate>();
        AddAttackLine(unit, lineMaterial);
        unit.AddComponent<EnemyAI>();   // 12 m, 0.25 s, eye 0.5 m by default; encounter wired per instance

        AssetDatabase.DeleteAsset(HostilePrefabPath);
        var prefab = PrefabUtility.SaveAsPrefabAsset(unit, HostilePrefabPath);
        Object.DestroyImmediate(unit);
        return prefab != null ? prefab : throw new Exception("Failed to save " + HostilePrefabPath);
    }

    // The attack line lives on a child: a friendly's own LineRenderer belongs to CommandQueueView. No collider.
    static void AddAttackLine(GameObject unit, Material lineMaterial)
    {
        var lineObject = new GameObject("AttackLine");
        lineObject.transform.SetParent(unit.transform, false);
        var line = lineObject.AddComponent<LineRenderer>();
        line.sharedMaterial = lineMaterial;
        line.widthMultiplier = 0.06f;
        line.positionCount = 0;
        line.useWorldSpace = true;
        line.shadowCastingMode = ShadowCastingMode.Off;
        line.receiveShadows = false;
        line.enabled = false;
        SetReference(unit.AddComponent<AttackLineView>(), "line", line);
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

    static Material CreateUnlitMaterial(string name, Color color)
    {
        var path = $"Assets/_Project/Materials/{name}.mat";
        AssetDatabase.DeleteAsset(path);
        var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? throw new Exception("URP Unlit shader not found");
        var material = new Material(shader);
        material.SetColor("_BaseColor", color);
        AssetDatabase.CreateAsset(material, path);
        return material;
    }

    static T Require<T>(T value, string what) where T : Object =>
        value != null ? value : throw new Exception(what + " not found");

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

    static void SetInt(Object target, string field, int value)
    {
        var serialized = new SerializedObject(target);
        var property = serialized.FindProperty(field) ?? throw new Exception($"{target.GetType().Name}.{field} not found");
        property.intValue = value;
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }

    static void SetHealthList(Encounter encounter, string field, Health[] units)
    {
        var serialized = new SerializedObject(encounter);
        var list = serialized.FindProperty(field) ?? throw new Exception($"Encounter.{field} not found");
        list.arraySize = units.Length;
        for (var i = 0; i < units.Length; i++)
            list.GetArrayElementAtIndex(i).objectReferenceValue = units[i];
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }
}
```

- [ ] **Step 4: Run the builder, then delete it**

```bash
"/c/Program Files/Unity/Hub/Editor/6000.3.25f1/Editor/Unity.exe" -batchmode -quit -projectPath "$(pwd -W)" -executeMethod CombatSceneBuilder.Build -logFile "$(pwd -W)/Logs/BuildCombatScene.log"; echo "EXIT=$?"
grep -E '\[CombatSceneBuilder\]|error CS|Exception' Logs/BuildCombatScene.log | head -20
grep -c "TrainingDummy" Assets/_Project/Scenes/Prototype.unity              # expect 0
grep -c "m_Name: HostileUnit_" Assets/_Project/Scenes/Prototype.unity        # expect 0 (names live in prefab-instance modifications)
grep -c "value: HostileUnit_" Assets/_Project/Scenes/Prototype.unity         # expect 3
grep -c "m_Name: Obstacle_E\|m_Name: Obstacle_F" Assets/_Project/Scenes/Prototype.unity  # expect 2
grep -c "targetLabel" Assets/_Project/Scenes/Prototype.unity                 # expect 0 (stale HUD field dropped on save)
grep -c "Blackglass.Health" Assets/_Project/Prefabs/FriendlyUnit.prefab      # expect 1
grep -c "Blackglass.EnemyAI" Assets/_Project/Prefabs/HostileUnit.prefab      # expect 1
git status --short Assets/_Project/Materials                                  # Dummy.mat -> Hostile.mat rename, DeathMarker.mat, AttackLine.mat (+ .meta)
grep guid Assets/_Project/Materials/Hostile.mat.meta                          # the GUID Dummy.mat had
```

**Expected:** `EXIT=0`; the log contains `[CombatSceneBuilder] Built the combat encounter into Assets/_Project/Scenes/Prototype.unity` and no exceptions; the counts match. Check the material GUID was kept: `git show HEAD:Assets/_Project/Materials/Dummy.mat.meta | grep guid` must equal the `Hostile.mat.meta` GUID.

Then delete the builder:

```bash
rm -r Assets/_Project/Editor Assets/_Project/Editor.meta
```

- [ ] **Step 5: Run all tests to verify they pass**

Run: `Tools/run-tests.sh PlayMode` → expected `total="172" passed="172"`, `EXIT=0`. This run also recompiles without the deleted builder.
Run: `Tools/run-tests.sh EditMode` → expected `total="208" passed="208"`, `EXIT=0`.

Troubleshooting:
- `Squad_AttacksEveryHostile_AndWins` times out → check the hostiles can be reached (NavMesh rebaked: `NavMesh-Environment.asset` changed) and that `HostileUnit_n` have `Health` 60.
- `FriendlyWalksIntoView_HostilesEngageIt` times out → (10, 0, 6) may be off the NavMesh after the new walls; move it to (10, 0, 7) and check `Obstacle_F` spans z 0–6 at x 11.5–12.5.
- Scene wiring test fails on `State == Idle` at start → a hostile sees a friendly: the squad at (-12..-8, -10) is about 25 m away, so check the detection range saved in the prefab is 12.

- [ ] **Step 6: Commit**

```bash
git add -A Assets/_Project/Scenes Assets/_Project/Prefabs Assets/_Project/Materials Assets/_Project/Tests/PlayMode/PrototypeSceneTests.cs
git status --short   # must NOT list Assets/_Project/Editor; Dummy.mat shows as a rename to Hostile.mat
git commit -m "Build the combat encounter into the prototype scene

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 12: Decision records and final verification

**Files:**
- Modify: `Docs/Decisions.md`
- Modify: `Docs/superpowers/specs/2026-10-04-combat-encounter-design.md` (status line only)

- [ ] **Step 1: Amend decisions 007, 011 and 012**

In `Docs/Decisions.md`:

(a) In **007**, replace the last bullet (starting `- **Pause frame (Phase 3, 2026-10-04):**`) with:

```markdown
- **Pause frame (Phase 3, 2026-10-04; closed in Phase 4):** `Time.deltaTime` still holds the previous frame's value on the frame `Pause()` is called, so a scaled-time check alone lets one more frame of simulation through. Since Phase 4 every simulation `Update` (`CommandableUnit`, `UnitMover.Steer`, `EnemyAI`, `HitFlash`, `AttackLineView`) checks `SimulationTime.IsRunning` (scaled delta time **and** time scale above zero), so no order, hit or AI step runs on the pause frame (see 013).
```

(b) In **011**, under Implications, replace the bullet `- Switching the active character at runtime is designed in 012. What happens when it dies is not designed yet; friendly units have no `Health`.` with:

```markdown
  - Switching the active character at runtime is designed in 012; what happens when it dies, in 013 (control passes to the next eligible friendly, or to nobody).
```

(c) In **012**, replace `- **Implications:** No automatic switch when the active character dies.` (the start of the Implications bullet) with `- **Implications:** Since Phase 4 the active character's death hands control to the next eligible roster unit (013).` and keep the rest of that bullet.

- [ ] **Step 2: Add decisions 013, 014 and 015**

Append to `Docs/Decisions.md`:

```markdown

## 013 — Health, death and the simulation clock

- **Decided (Phase 4, 2026-10-04):** `Health` is the one hit-point implementation for every unit, friendly or hostile (max, current, `TakeDamage(amount, attacker)`, `Damaged`, `AttackedBy`, `Died`). At zero it raises `Died` once and deactivates its GameObject; a `DeathMarker` leaves a flat grey disc where the unit fell. `CommandableUnit` listens to its own `Health`: on death it clears every order and stops, and `Issue` returns false while dead (`IsAlive`). Every existing check (`IsAttackable`, `UnitSelection.CanSelect`, `ActiveCharacter.IsEligible`, `HasUnit`, the HUD) already treats an inactive unit as gone, so death needs no further plumbing.
- **Simulation clock:** `SimulationTime.IsRunning` is the single "may simulation advance this frame" test, true when scaled delta time and time scale are both above zero. It closes the pause-frame leak noted in 007.
- **Controlled character dies:** `ActiveCharacter.RefreshEligibility` (run every frame) moves control to the next eligible roster unit in roster order, wrapping, or to nobody. Takeover mode and the selection are left alone; the dead unit drops out of the selection on its own. With no unit, `HasUnit` is false: WASD pans, no intent is set, clicks order the selection, the HUD says "Controlled: none".
- **Why:** one hit-point type keeps the two sides symmetric; deactivating on death reuses the checks the rest of the code already makes; a single clock check is cheaper to reason about than per-component time rules.
- **Rejected:** keeping the corpse as the unit with components disabled (more code and edge cases: clicks on corpses, agents blocking paths); a separate `Damaged` event carrying the attacker (two events is simpler than breaking `HitFlash` and tests); switching selection on death (would wipe a group selection mid-fight).
- **Implications:** a future revive would need the corpse approach instead of deactivation. Corpse markers accumulate until the scene reloads. New simulation components must check `SimulationTime.IsRunning`, not `Time.deltaTime`.

## 014 — One combat path, three deciders

- **Decided (Phase 4, 2026-10-04):** The only way anything attacks is `CommandableUnit.Issue(new AttackCommand(target))`. `CommandableUnit` chases, stops in range and faces; `UnitAttacker` applies range, cooldown and damage on scaled time and raises `Attacked`. Three deciders feed it: player clicks (`PlayerCommandInput`, unchanged), `EnemyAI` (015) and `AutoRetaliate`: a unit hit while it has **no orders** and no held move intent attacks its attacker. Any order wins (a Move away is a retreat); held WASD wins. Both prefabs carry `AutoRetaliate`.
- **Sides:** `Encounter` (on `Systems`) holds two serialized lists of `Health`, friendlies and hostiles, and computes `Outcome` on read: all friendlies dead → Defeat (checked first), all hostiles dead → Victory, empty sides never resolve. Enemy AI reads friendlies from it; the HUD reads the outcome. No faction system.
- **Attack timing:** damage only in `UnitAttacker.TryAttack`, never per frame; cooldown uses `Time.time`, so it freezes while paused.
- **Why:** player, AI and retaliation share every rule about range, timing, death and target validation, so a fix lands once. `Encounter` keeps "who is on which side" in one place without a faction system the prototype does not need.
- **Rejected:** friendly-only retaliation (a hostile hit from a blind spot would wait for its think tick); a per-unit "hold fire" flag (a future `UnitCommand` if ever wanted); using `UnitSelection.Roster` as the friendly side (ties enemy AI to selection and rules out allied units the player does not control); events on `Encounter` (views read state each frame, as in 006).
- **Implications:** Interact and ranged weapons go through `Issue` the same way. A team concept later replaces the two lists in `Encounter`. `AttackLineView` and `HitFlash` are the only hit feedback and are debug-only.

## 015 — Enemy awareness

- **Decided (Phase 4, 2026-10-04):** `EnemyAI` idles until a living friendly is within its detection radius (12 m) **and** in line of sight: one ray from the hostile's eye (1.5 m) to the friendly's centre, blocked by any collider without a `Health` in its parents. Units never block sight. It then issues an `AttackCommand` and leaves the rest to `CommandableUnit`; when that order ends (target dead, unreachable) it looks again on its next think tick (0.25 s, scaled time). Line of sight gates **acquisition only**: a target once taken is followed around corners until it dies. State (`Idle`, `Chase`, `Attack`, `Dead`) is derived from the unit's order, range and health, never stored.
- **Why:** the radius and sight check make approach, positioning and pause matter without a perception system; chasing without re-checking sight avoids oscillation at corners; deriving state keeps one source of truth.
- **Rejected:** always aggressive (every run opens the same way); losing the target when sight breaks (oscillation); target memory, leash, retargeting to a closer unit, cones, alert states (out of scope).
- **Implications:** hiding works before you are spotted, not after. An unreachable but visible friendly is re-chased every tick (the hostile stands at the closest point). Perception can grow inside `EnemyAI.FindNearestVisibleFriendly` without touching combat.
```

- [ ] **Step 3: Update the spec status**

In `Docs/superpowers/specs/2026-10-04-combat-encounter-design.md`, change the `Status:` on line 3 to `Status: implemented 2026-10-04 (see Docs/superpowers/plans/2026-10-04-combat-encounter.md)`.

- [ ] **Step 4: Final verification**

```bash
git status --short                 # only the two docs
ls Assets/_Project/Editor 2>/dev/null && echo "BUILDER STILL PRESENT" || echo "builder gone"
grep -rn "TrainingDummy\|Dummy.mat" Assets/_Project/Scenes Assets/_Project/Prefabs | grep -v "\.meta" ; echo "(expect no matches)"
Tools/run-tests.sh EditMode        # expected total="208" passed="208" EXIT=0
Tools/run-tests.sh PlayMode        # expected total="172" passed="172" EXIT=0
```

- [ ] **Step 5: Commit**

```bash
git add Docs/Decisions.md Docs/superpowers/specs/2026-10-04-combat-encounter-design.md
git commit -m "Record the combat encounter decisions

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```
