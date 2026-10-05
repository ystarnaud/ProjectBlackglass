# Tactical Cover (Phase 6) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Hand-placed cover points that one unit at a time can reserve and occupy, a `MoveToCover` order through the existing command path, ranged shots at a covered target resolved by a per-point hit chance, hostiles that take nearby cover before firing, companions and retaliation that hold ordered cover, a paused-only cover view, and an arena that shows it all.

**Architecture:**
- Decision sources (`PlayerCommandInput`, `EnemyAI`, `CompanionAI`, `AutoRetaliate`) only ever call `CommandableUnit.Issue`. Shared capabilities carry orders out: `CommandableUnit` runs a `MoveToCoverCommand` (reserve at start, occupy on arrival, give up after 3 s without progress), `UnitCover` is the one writer of `CoverPoint` claims (reservation bound to the order, occupancy bound to standing still within 0.6 m), `UnitAttacker` rolls cover before applying damage.
- `CoverPoint` knows where a unit stands and which collider protects it; `ProtectsFrom` is one `Collider.Raycast` from the attacker's eye to the defender's feet. `CoverRules` is pure (hit resolution, nearest-point search). `CoverRegistry` is a serialized list on `Systems`, like `Encounter`.
- `OccupiedByOrder` separates cover the player ordered from cover a unit happened to stop on; only the former changes companion and retaliation behaviour. Every new `Update` checks `SimulationTime.IsRunning`.

**Tech Stack:** Unity 6000.3.25f1 (Unity 6.3 LTS), URP 17.3.0, Input System 1.20.0, AI Navigation 2.0.14, Unity Test Framework 1.6.0 (NUnit), `InputTestFixture`.

**Spec:** `Docs/superpowers/specs/2026-10-05-cover-system-design.md`

## Global Constraints

- **Unity Editor:** exactly 6000.3.25f1 at `C:\Program Files\Unity\Hub\Editor\6000.3.25f1\Editor\Unity.exe`. **The Editor must be closed during every batch-mode run** (one instance per project). If a run prints "The Unity Editor has this project open", stop and ask the owner to close it.
- **Code location:** runtime code in `Assets/_Project/Scripts/` (assembly `Blackglass`, namespace `Blackglass`); the new cover types in `Assets/_Project/Scripts/Cover/`, `UnitCover` in `Scripts/Units/`; tests in `Assets/_Project/Tests/EditMode/` and `Assets/_Project/Tests/PlayMode/` (namespace `Blackglass.Tests`). No new assemblies, assembly references or packages. Input actions asset unchanged; no new bindings.
- **Commands are data** (decision 006). Attacks happen only through `CommandableUnit.Issue(new AttackCommand(target))`; cover is entered only through `Issue(new MoveToCoverCommand(point))` or by standing on a free point. AI and input scripts never call `UnitMover`, `UnitAttacker.TryAttack`, `UnitCover.TryReserve` or `Health.TakeDamage` (tests may).
- **Input components** never touch `UnitMover`, `UnitAttacker`, `UnitCover`, `CoverPoint` state, NavMeshAgents or `Health`. `DirectControlInput` and `ActiveCharacter` are not modified by this plan.
- **Pause ownership:** only `TacticalPause` writes `Time.timeScale` in game code. Tests may reset it to 1 in `TearDown`. Simulation code checks `SimulationTime.IsRunning`; camera, input, HUD use unscaled time.
- **Wiring:** serialized Inspector references only. No singletons, no `Find*`/`Camera.main` in game code, no static mutable state. Components expose `internal Initialize(...)` for tests, called while the host GameObject is inactive or before its first `Update`.
- **Unity null rule:** never use `??`, `??=` or `?.` on `UnityEngine.Object` references (a missing component is the Editor's placeholder, not a C# null). Use `== null`, `TryGetComponent`, or `ReferenceEquals` when "destroyed" must differ from "none". `??=` is fine on plain C# objects such as delegates or `GUIStyle`. `Health` stays optional on every unit: `UnitCover` must not `RequireComponent` it.
- **Lifecycle:** EditMode tests get no `Awake`/`OnEnable`/`Update`; behaviour that needs them is tested in PlayMode. PlayMode fighters are assembled on an inactive GameObject, then activated (`TestWorld.CreateFighter`). Units that must stand somewhere specific are **spawned** there, never teleported.
- **Physics:** colliders created or moved at runtime are invisible to raycasts until the next physics step; `TestWorld.CreateEnvironment` calls `Physics.SyncTransforms()` for obstacles, and tests that raycast at units created this frame first `yield return new WaitForFixedUpdate()`. Test walls stand on the ground: a 0.9 m wall is at y 0.45 with scale y 0.9.
- **Distances** between a unit and a cover point are always flat (x/z): a unit's transform is at pivot height (1 m), a point is at ground level. Use `CoverRules.FlatDistance`.
- **Prototype numbers (copy verbatim):** `hitChance` 0.5 per point; `occupyRadius` 0.6 m; `leaveRadius` 1.0 m; `stillSpeed` 0.5 m/s; `coverSearchRange` 8 m; `coverClickRadius` 1 m; `CoverWalkTimeout` 3 s; `CoverProgressStep` 0.05 m; eye height `LineOfSight.EyeHeight` 0.5 m above the pivot; stand points 0.75 m from an obstacle face; low walls 0.9 m tall (y 0.45, scale y 0.9); melee range 2 m; ranged range 8 m; detection range 12 m; think interval 0.25 s; follow 3.5 / 6 m; assist range 10 m.
- **Visuals:** debug only. IMGUI text, primitives with placeholder materials. Each unit has exactly one collider (its capsule). Cover markers have no colliders.
- **Scope:** no crouching, prone, suppression, destructible cover, blind fire, leaning, ballistics, weapon classes, armour, damage tables, body parts, crits, accuracy progression, formations, utility AI, companion cover seeking, hostile cover re-evaluation, squad cover assignment, production UI, new packages. Phase 7 is not started.
- **Git:** work on local branch `prototype/cover-system` (exists; spec committed). Never push or merge. Commit after each task. Messages are sentence case and imperative, and end with the line `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`. Commit `.meta` files with their assets. Never commit `Assets/_Project/Editor/`.
- **Test runs:** always `Tools/run-tests.sh`, 10-minute timeout for PlayMode runs. Scene tests fail on any logged error.
- **Writing style:** XML summary on every public type; comments explain why, not what.

### Running tests

```bash
Tools/run-tests.sh EditMode                                              # all EditMode tests
Tools/run-tests.sh PlayMode                                              # all PlayMode tests
Tools/run-tests.sh PlayMode Blackglass.Tests.CoverOrderPlayModeTests     # one fixture (any -testFilter expression)
```

The script prints `error CS` lines, a summary (`result= total= passed= failed=`), failed test names and messages, and `EXIT=<code>`: **0** all passed, **2** some failed, **1** could not run (a compile error). A "red" TDD step is usually **EXIT=1 with `error CS0117`/`CS1061`/`CS1501`/`CS0246`** because new members do not exist yet. Watch for the summary line, not the exit code alone.

**Baseline before this plan (measured 2026-10-05 on `main`, 21c4940):** EditMode 239/239, PlayMode 213/213.

**Expected totals after each task:**

| After task | EditMode | PlayMode |
|---|---|---|
| 1 | 250 | 217 |
| 2 | 262 | 223 |
| 3 | 267 | 237 |
| 4 | 270 | 242 |
| 5 | 272 | 243 |
| 6 | 273 | 248 |
| 7 | 273 | 256 |
| 8 | 281 | 260 |
| 9 | 281 | 261 |
| 10 | 281 | 261 |

These totals assume each task adds exactly the tests listed. If a task's count differs, correct this table in that task's commit; the sequence must stay green either way.

## Review Focus

1. **A cover order to a point another unit physically stands on** → the walker must not hold its reservation forever: after 3 s without progress it gives up silently and runs its next order. Pinned in Task 3 (`BlockedPoint_OrderGivesUpAfterThreeSeconds_AndTheNextOrderRuns`).
2. **A point destroyed or disabled while a unit is walking to it, or while paused with the HUD and queue view drawing it** → no exception, the order ends, the claim is released. Pinned in Task 2 (`DestroyedPoint_IsReleased`), Task 3 (`DestroyingThePointMidWalk_EndsTheOrderSilently_AndTheNextOrderRuns`) and Task 8 (`CoverOrder_ShowsAMarkerAtThePoint_AndAVanishedPointDrawsNothing`).
3. **A refused replacement order (off-mesh move, dead target) on a unit walking to cover** → the current cover order and its reservation survive untouched. Pinned in Task 3 (`ReplacingACoverOrderWithARefusedMove_KeepsTheReservation`).
4. **A companion whose follow move happens to end on a free point** → it still follows when the leader walks on; only ordered cover is held. Pinned in Task 7 (`CompanionThatStopsOnAPointByChance_StillFollows`).
5. **A unit walking across a free point on its way elsewhere** → it never claims it (no marker flicker, no refused clicks). Pinned in Task 2 (`WalkingThroughAFreePoint_NeverClaimsIt`).

---

### Task 1: `CoverRules`, `CoverPoint` geometry, `CoverRegistry`

**Files:**
- Create: `Assets/_Project/Scripts/Cover/CoverRules.cs`
- Create: `Assets/_Project/Scripts/Cover/CoverPoint.cs`
- Create: `Assets/_Project/Scripts/Cover/CoverRegistry.cs`
- Create: `Assets/_Project/Tests/EditMode/CoverRulesTests.cs`
- Create: `Assets/_Project/Tests/EditMode/CoverPointTests.cs`
- Create: `Assets/_Project/Tests/PlayMode/CoverPointPlayModeTests.cs`
- Modify: `Assets/_Project/Tests/PlayMode/TestSupport/TestWorld.cs`

**Interfaces:**
- Produces: `public static class CoverRules { public static bool ResolveHit(bool isProtected, float hitChance, float roll); public static bool TryChooseNearest(IReadOnlyList<CoverPoint> points, Vector3 from, float maxDistance, Func<CoverPoint, bool> accept, out CoverPoint chosen); public static float FlatDistance(Vector3 a, Vector3 b); }`
- Produces: `public sealed class CoverPoint : MonoBehaviour { public Vector3 Position; public Vector3 Forward; public float HitChance; public Collider Obstacle; public bool ProtectsFrom(Vector3 attackerPivot, Vector3 defenderPivot); internal void Initialize(Collider obstacleCollider, float chanceToHit = 0.5f); }` (claims are added in Task 2)
- Produces: `public sealed class CoverRegistry : MonoBehaviour { public IReadOnlyList<CoverPoint> Points; internal void Initialize(params CoverPoint[] scenePoints); }`
- Produces (tests): `TestWorld.CreateObstacle(Vector3 position, Vector3 scale)` (a tracked cube outside the NavMesh environment, for geometry tests that need a collider), `TestWorld.ObstacleCollider(GameObject environment, int index = 0)`, `TestWorld.CreateCoverPoint(Vector3 position, Vector3 forward, Collider obstacle, float hitChance = 0.5f)`, `TestWorld.CreateRegistry(params CoverPoint[] points)`.

- [ ] **Step 1: Write the failing EditMode tests**

Create `Assets/_Project/Tests/EditMode/CoverRulesTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class CoverRulesTests
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

        CoverPoint Point(float x, float z)
        {
            var host = new GameObject($"Cover ({x}, {z})");
            host.transform.position = new Vector3(x, 0f, z);
            hosts.Add(host);
            return host.AddComponent<CoverPoint>();
        }

        [Test]
        public void ResolveHit_ExposedTarget_AlwaysHits()
        {
            Assert.That(CoverRules.ResolveHit(false, 0.5f, 0f), Is.True);
            Assert.That(CoverRules.ResolveHit(false, 0.5f, 0.5f), Is.True);
            Assert.That(CoverRules.ResolveHit(false, 0.5f, 0.999f), Is.True);
            Assert.That(CoverRules.ResolveHit(false, 0f, 0.999f), Is.True, "The chance only matters for a protected target");
        }

        [Test]
        public void ResolveHit_ProtectedTarget_HitsBelowTheChance_AndMissesAtOrAbove()
        {
            Assert.That(CoverRules.ResolveHit(true, 0.5f, 0f), Is.True);
            Assert.That(CoverRules.ResolveHit(true, 0.5f, 0.49f), Is.True);
            Assert.That(CoverRules.ResolveHit(true, 0.5f, 0.5f), Is.False, "A roll equal to the chance misses");
            Assert.That(CoverRules.ResolveHit(true, 0.5f, 0.999f), Is.False);
            Assert.That(CoverRules.ResolveHit(true, 1f, 0.999f), Is.True, "Chance 1 never misses");
            Assert.That(CoverRules.ResolveHit(true, 0f, 0f), Is.False, "Chance 0 never hits");
        }

        [Test]
        public void TryChooseNearest_PicksTheNearestAcceptedPoint()
        {
            var far = Point(0f, 6f);
            var near = Point(0f, 2f);
            var middle = Point(0f, 4f);
            Assert.That(CoverRules.TryChooseNearest(new[] { far, middle, near }, new Vector3(0f, 1f, 0f), 10f, _ => true, out var chosen), Is.True);
            Assert.That(chosen, Is.SameAs(near), "List order must not matter; height must be ignored");
        }

        [Test]
        public void TryChooseNearest_SkipsRejectedAndOutOfRangePoints_FalseWhenNone()
        {
            var near = Point(0f, 2f);
            var middle = Point(0f, 4f);
            var far = Point(0f, 12f);
            Assert.That(CoverRules.TryChooseNearest(new[] { near, middle, far }, Vector3.zero, 10f, p => p != near, out var chosen), Is.True);
            Assert.That(chosen, Is.SameAs(middle), "A rejected nearer point loses to the next one");
            Assert.That(CoverRules.TryChooseNearest(new[] { near, middle, far }, Vector3.zero, 3f, p => p != near, out chosen), Is.False, "Nothing accepted inside the radius");
            Assert.That(chosen, Is.Null);
            Assert.That(CoverRules.TryChooseNearest(new[] { far }, Vector3.zero, 10f, _ => true, out _), Is.False, "12 m is outside 10 m");
            Assert.That(CoverRules.TryChooseNearest(new CoverPoint[0], Vector3.zero, 10f, _ => true, out _), Is.False);
        }

        [Test]
        public void TryChooseNearest_SkipsNullAndDestroyedEntries_WithoutCallingAccept()
        {
            var live = Point(0f, 5f);
            var destroyed = Point(0f, 1f);
            Object.DestroyImmediate(destroyed.gameObject);
            var inactive = Point(0f, 2f);
            inactive.gameObject.SetActive(false);
            var seen = new List<CoverPoint>();
            Assert.That(CoverRules.TryChooseNearest(new[] { null, destroyed, inactive, live }, Vector3.zero, 10f, p => { seen.Add(p); return true; }, out var chosen), Is.True);
            Assert.That(chosen, Is.SameAs(live));
            Assert.That(seen, Is.EqualTo(new[] { live }), "Dead and inactive points must never reach the predicate");
        }

        [Test]
        public void TryChooseNearest_NeverCallsAcceptOnAFartherCandidateThanOneAccepted()
        {
            var near = Point(0f, 2f);
            var far = Point(0f, 6f);
            var seen = new List<CoverPoint>();
            CoverRules.TryChooseNearest(new[] { near, far }, Vector3.zero, 10f, p => { seen.Add(p); return true; }, out _);
            Assert.That(seen, Is.EqualTo(new[] { near }), "The dear predicate must not run on points that cannot win");
        }

        [Test]
        public void TryChooseNearest_RejectsNullArguments()
        {
            Assert.Throws<ArgumentNullException>(() => CoverRules.TryChooseNearest(null, Vector3.zero, 1f, _ => true, out _));
            Assert.Throws<ArgumentNullException>(() => CoverRules.TryChooseNearest(new CoverPoint[0], Vector3.zero, 1f, null, out _));
        }
    }
}
```

Create `Assets/_Project/Tests/EditMode/CoverPointTests.cs`:

```csharp
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CoverPointTests
    {
        GameObject host;
        GameObject obstacleHost;
        CoverPoint point;

        [SetUp]
        public void SetUp()
        {
            host = new GameObject("Cover");
            point = host.AddComponent<CoverPoint>();
            obstacleHost = GameObject.CreatePrimitive(PrimitiveType.Cube);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(host);
            Object.DestroyImmediate(obstacleHost);
        }

        [Test]
        public void Forward_IsFlatAndNormalised()
        {
            host.transform.rotation = Quaternion.LookRotation(new Vector3(3f, 4f, 4f));
            Assert.That(point.Forward.y, Is.EqualTo(0f));
            Assert.That(point.Forward.magnitude, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(point.Forward.x, Is.EqualTo(0.6f).Within(1e-4f));
            Assert.That(point.Forward.z, Is.EqualTo(0.8f).Within(1e-4f));
        }

        [Test]
        public void Initialize_SetsTheObstacleAndHitChance_DefaultIsHalf()
        {
            Assert.That(point.HitChance, Is.EqualTo(0.5f));
            Assert.That(point.Obstacle, Is.Null);
            var collider = obstacleHost.GetComponent<Collider>();
            point.Initialize(collider, 0.25f);
            Assert.That(point.Obstacle, Is.SameAs(collider));
            Assert.That(point.HitChance, Is.EqualTo(0.25f));
            Assert.That(point.Position, Is.EqualTo(host.transform.position));
        }

        [Test]
        public void ProtectsFrom_WithoutAnObstacle_IsFalse_AndWarnsOnce()
        {
            LogAssert.Expect(LogType.Warning, new Regex("no obstacle"));
            Assert.That(point.ProtectsFrom(new Vector3(0f, 1f, 5f), new Vector3(0f, 1f, 0f)), Is.False);
            Assert.That(point.ProtectsFrom(new Vector3(0f, 1f, 5f), new Vector3(0f, 1f, 0f)), Is.False);
        }

        [Test]
        public void Registry_ListsThePointsItIsGiven()
        {
            var registry = host.AddComponent<CoverRegistry>();
            Assert.That(registry.Points, Is.Empty);
            registry.Initialize(point);
            Assert.That(registry.Points, Is.EqualTo(new[] { point }));
        }
    }
}
```

- [ ] **Step 2: Run the EditMode tests to verify they fail**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.CoverRulesTests|Blackglass.Tests.CoverPointTests"` → expected `EXIT=1` with `error CS0246: The type or namespace name 'CoverPoint' could not be found` and `error CS0103: The name 'CoverRules' does not exist`.

- [ ] **Step 3: Write the three runtime types**

Create `Assets/_Project/Scripts/Cover/CoverRules.cs`:

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Pure cover rules shared by UnitAttacker (hit resolution), PlayerCommandInput (cover clicks), EnemyAI (cover
    /// search) and UnitCover (automatic occupancy). Static, stateless, EditMode-tested.
    /// </summary>
    public static class CoverRules
    {
        /// <summary>A shot lands unless the target is protected and the roll (0..1) is at or above the hit chance.</summary>
        public static bool ResolveHit(bool isProtected, float hitChance, float roll) => !isProtected || roll < hitChance;

        /// <summary>
        /// The nearest point (flat distance from `from`, within `maxDistance`) that `accept` admits, or false. A
        /// candidate that is null (destroyed) or inactive in the hierarchy is skipped before its position is read, and
        /// one out of range or not nearer than the best accepted so far is skipped before `accept` is called (as
        /// EnemyAI.FindTarget does), so the predicate runs only on live points that could win; callers still order
        /// it cheap to dear.
        /// </summary>
        public static bool TryChooseNearest(IReadOnlyList<CoverPoint> points, Vector3 from, float maxDistance,
            Func<CoverPoint, bool> accept, out CoverPoint chosen)
        {
            if (points == null)
                throw new ArgumentNullException(nameof(points));
            if (accept == null)
                throw new ArgumentNullException(nameof(accept));

            chosen = null;
            var bestDistance = float.PositiveInfinity;
            for (var i = 0; i < points.Count; i++)
            {
                var point = points[i];
                if (point == null || !point.gameObject.activeInHierarchy)
                    continue;
                var distance = FlatDistance(from, point.Position);
                if (distance > maxDistance || distance >= bestDistance || !accept(point))
                    continue;
                chosen = point;
                bestDistance = distance;
            }
            return chosen != null;
        }

        /// <summary>Distance ignoring height: units stand at pivot height, points at ground level.</summary>
        public static float FlatDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }
    }
}
```

Create `Assets/_Project/Scripts/Cover/CoverPoint.cs`:

```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// A place a unit can stand to be protected by one obstacle. Position: the transform (ground level). Forward: the
    /// flat direction into the obstacle, used only to place the marker's direction nub. Protection is geometric: a ray
    /// from the attacker's eye to the defender's feet must cross the wired obstacle.
    /// </summary>
    public sealed class CoverPoint : MonoBehaviour
    {
        [SerializeField] Collider obstacle;
        [SerializeField, Range(0f, 1f)] float hitChance = 0.5f;

        bool warnedAboutObstacle;

        public Vector3 Position => transform.position;

        /// <summary>The transform's forward, flattened and normalised; world forward if it points straight up or down.</summary>
        public Vector3 Forward
        {
            get
            {
                var forward = transform.forward;
                forward.y = 0f;
                return forward.sqrMagnitude > 0.0001f ? forward.normalized : Vector3.forward;
            }
        }

        /// <summary>A protected shot's chance to hit (the one tunable per point).</summary>
        public float HitChance => hitChance;

        public Collider Obstacle => obstacle;

        /// <summary>
        /// True when the ray from the attacker's eye (pivot + LineOfSight.EyeHeight) to the defender's feet (the
        /// defender's x/z at this point's height) hits the obstacle. Only the wired collider is tested, so units and
        /// other geometry never confuse it. With no obstacle wired: false, with one warning.
        /// </summary>
        public bool ProtectsFrom(Vector3 attackerPivot, Vector3 defenderPivot)
        {
            if (obstacle == null)
            {
                if (!warnedAboutObstacle)
                {
                    Debug.LogWarning($"{name} has no obstacle collider wired, so it protects nobody.", this);
                    warnedAboutObstacle = true;
                }
                return false;
            }
            var eye = attackerPivot + Vector3.up * LineOfSight.EyeHeight;
            var feet = new Vector3(defenderPivot.x, Position.y, defenderPivot.z);
            var toFeet = feet - eye;
            var distance = toFeet.magnitude;
            if (distance <= 0.001f)
                return false;
            return obstacle.Raycast(new Ray(eye, toFeet / distance), out _, distance);
        }

        internal void Initialize(Collider obstacleCollider, float chanceToHit = 0.5f)
        {
            obstacle = obstacleCollider;
            hitChance = chanceToHit;
        }
    }
}
```

Create `Assets/_Project/Scripts/Cover/CoverRegistry.cs`:

```csharp
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The scene's cover points, as a serialized list. Holds state only, like Encounter: clicks, hostile searches,
    /// automatic occupancy and the cover view read it. Points do not register themselves.
    /// </summary>
    public sealed class CoverRegistry : MonoBehaviour
    {
        [SerializeField] List<CoverPoint> points = new List<CoverPoint>();

        public IReadOnlyList<CoverPoint> Points => points;

        internal void Initialize(params CoverPoint[] scenePoints)
        {
            points.Clear();
            points.AddRange(scenePoints);
        }
    }
}
```

- [ ] **Step 4: Run the EditMode tests to verify they pass**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.CoverRulesTests|Blackglass.Tests.CoverPointTests"` → expected `total="11" passed="11"`, `EXIT=0`.

- [ ] **Step 5: Extend `TestWorld` and write the failing PlayMode geometry tests**

In `Assets/_Project/Tests/PlayMode/TestSupport/TestWorld.cs`, add after `CreateEnvironment`:

```csharp
        /// <summary>The n-th box obstacle CreateEnvironment made (creation order), for wiring a CoverPoint to it.</summary>
        public static BoxCollider ObstacleCollider(GameObject environment, int index = 0) =>
            environment.GetComponentsInChildren<BoxCollider>()[index];

        /// <summary>
        /// A box with a collider outside the NavMesh environment (built after the bake, so it carves nothing). For
        /// geometry tests that only need a collider to raycast against.
        /// </summary>
        public GameObject CreateObstacle(Vector3 position, Vector3 scale)
        {
            var box = Track(GameObject.CreatePrimitive(PrimitiveType.Cube));
            box.name = "LooseObstacle";
            box.transform.position = position;
            box.transform.localScale = scale;
            Physics.SyncTransforms();
            return box;
        }

        /// <summary>A cover point at exactly the given stand position, facing `forward`, protected by `obstacle`.</summary>
        public CoverPoint CreateCoverPoint(Vector3 position, Vector3 forward, Collider obstacle, float hitChance = 0.5f)
        {
            var host = Track(new GameObject("TestCoverPoint"));
            host.transform.SetPositionAndRotation(position, Quaternion.LookRotation(forward));
            var point = host.AddComponent<CoverPoint>();
            point.Initialize(obstacle, hitChance);
            return point;
        }

        /// <summary>A registry on its own object listing the given points.</summary>
        public CoverRegistry CreateRegistry(params CoverPoint[] points)
        {
            var registry = Track(new GameObject("CoverRegistry")).AddComponent<CoverRegistry>();
            registry.Initialize(points);
            return registry;
        }
```

Create `Assets/_Project/Tests/PlayMode/CoverPointPlayModeTests.cs`:

```csharp
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CoverPointPlayModeTests
    {
        TestWorld world;

        [SetUp]
        public void SetUp() => world = new TestWorld();

        [TearDown]
        public void TearDown() => world.Dispose();

        static Vector3 Pivot(float x, float z) => new Vector3(x, 1f, z);

        [UnityTest]
        public IEnumerator OverAWaistHighWall_ProtectsStraightOn_FromNearAndFar_ButNotFromBehind()
        {
            // A 0.9 m wall from z -0.25 to 0.25; the stand point is 0.75 m south of its face. With the 1.5 m eye and
            // the ray ending at the feet, the ray meets the near face at 1.125 / D: 0.56 m from 2 m, 0.14 m from 8 m.
            var environment = world.CreateEnvironment((new Vector3(0f, 0.45f, 0f), new Vector3(4f, 0.9f, 0.5f)));
            var point = world.CreateCoverPoint(new Vector3(0f, 0f, -1f), Vector3.forward, TestWorld.ObstacleCollider(environment));
            yield return new WaitForFixedUpdate();
            var defender = Pivot(0f, -1f);

            Assert.That(point.ProtectsFrom(Pivot(0f, 1f), defender), Is.True, "2 m away over the wall");
            Assert.That(point.ProtectsFrom(Pivot(0f, 7f), defender), Is.True, "8 m away over the wall");
            Assert.That(point.ProtectsFrom(Pivot(0f, -5f), defender), Is.False, "From behind the defender the wall is not between them");
        }

        [UnityTest]
        public IEnumerator BesideAPillar_TheFlankIsOpen_PastTheCorner()
        {
            // A 1.5 m pillar centred at the origin (x and z -0.75..0.75), the point 0.75 m south of its face. The
            // near corner lies at exactly 45 deg off the point's forward, so the tests sit on either side of it:
            // 30 deg (the ray crosses the face at x 0.43) and 55 deg (the ray passes the corner about 0.2 m clear).
            var environment = world.CreateEnvironment((new Vector3(0f, 1.5f, 0f), new Vector3(1.5f, 3f, 1.5f)));
            var point = world.CreateCoverPoint(new Vector3(0f, 0f, -1.5f), Vector3.forward, TestWorld.ObstacleCollider(environment));
            yield return new WaitForFixedUpdate();
            var defender = Pivot(0f, -1.5f);
            var at30 = Pivot(4f * Mathf.Sin(30f * Mathf.Deg2Rad), -1.5f + 4f * Mathf.Cos(30f * Mathf.Deg2Rad));
            var at55 = Pivot(4f * Mathf.Sin(55f * Mathf.Deg2Rad), -1.5f + 4f * Mathf.Cos(55f * Mathf.Deg2Rad));

            Assert.That(point.ProtectsFrom(at30, defender), Is.True, "30 deg: the pillar is between");
            Assert.That(point.ProtectsFrom(at55, defender), Is.False, "55 deg: the ray clears the corner, the flank is open");
        }

        [UnityTest]
        public IEnumerator OnlyTheWiredObstacleCounts()
        {
            // Two walls: the point is wired to the far one, and the near one (which the ray crosses) must not count.
            var environment = world.CreateEnvironment(
                (new Vector3(0f, 0.45f, 0f), new Vector3(4f, 0.9f, 0.5f)),
                (new Vector3(0f, 0.45f, 4f), new Vector3(4f, 0.9f, 0.5f)));
            var wiredToFarWall = world.CreateCoverPoint(new Vector3(0f, 0f, -1f), Vector3.forward, TestWorld.ObstacleCollider(environment, 1));
            yield return new WaitForFixedUpdate();

            Assert.That(wiredToFarWall.ProtectsFrom(Pivot(0f, 2f), Pivot(0f, -1f)), Is.False,
                "The attacker stands between the two walls: the wired (far) wall is not between it and the defender");
        }

        [UnityTest]
        public IEnumerator ALooseObstacle_WorksLikeASceneOne()
        {
            world.CreateEnvironment();
            var box = world.CreateObstacle(new Vector3(0f, 0.45f, 0f), new Vector3(4f, 0.9f, 0.5f));
            var point = world.CreateCoverPoint(new Vector3(0f, 0f, -1f), Vector3.forward, box.GetComponent<Collider>());
            yield return new WaitForFixedUpdate();

            Assert.That(point.ProtectsFrom(Pivot(0f, 5f), Pivot(0f, -1f)), Is.True);
            Assert.That(point.Obstacle, Is.SameAs(box.GetComponent<Collider>()));
        }
    }
}
```

- [ ] **Step 6: Run the PlayMode tests**

Run: `Tools/run-tests.sh PlayMode Blackglass.Tests.CoverPointPlayModeTests` → expected `total="4" passed="4"`, `EXIT=0`. (These compile against Step 3's types, so the first run is green; if a geometry assertion fails, re-check the wall position: y 0.45 with scale y 0.9, not y 0.)

Run: `Tools/run-tests.sh EditMode` → expected `total="250" passed="250"`, `EXIT=0`.
Run: `Tools/run-tests.sh PlayMode` → expected `total="217" passed="217"`, `EXIT=0`.

- [ ] **Step 7: Commit**

```bash
git add Assets/_Project/Scripts/Cover Assets/_Project/Tests/EditMode/CoverRulesTests.cs Assets/_Project/Tests/EditMode/CoverPointTests.cs Assets/_Project/Tests/PlayMode/CoverPointPlayModeTests.cs Assets/_Project/Tests/PlayMode/TestSupport/TestWorld.cs
git add Assets/_Project/Tests/EditMode/*.meta Assets/_Project/Tests/PlayMode/*.meta Assets/_Project/Scripts/Cover.meta
git status --short   # every new .cs must have its .meta staged
git commit -m "Add cover points, the cover rules and the cover registry

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 2: `UnitCover`: claims, reservation, occupancy, automatic occupancy

**Files:**
- Create: `Assets/_Project/Scripts/Units/UnitCover.cs`
- Modify: `Assets/_Project/Scripts/Cover/CoverPoint.cs` (claims)
- Create: `Assets/_Project/Tests/EditMode/UnitCoverTests.cs`
- Modify: `Assets/_Project/Tests/EditMode/CoverPointTests.cs`
- Create: `Assets/_Project/Tests/PlayMode/UnitCoverPlayModeTests.cs`
- Modify: `Assets/_Project/Tests/PlayMode/TestSupport/TestWorld.cs` (`CreateUnit`/`CreateFighter` add `UnitCover`; optional registry)

**Interfaces:**
- Consumes: `CoverPoint`, `CoverRegistry`, `CoverRules.TryChooseNearest`, `CoverRules.FlatDistance` (Task 1).
- Produces: `public enum CoverStatus { None, Reserved, Occupied }`; `public sealed class UnitCover : MonoBehaviour { public CoverPoint Point; public CoverStatus Status; public bool OccupiedByOrder; public bool IsWired; public float HitChance; public bool TryReserve(CoverPoint point); public bool TryOccupy(); public void ReleaseReservation(); public void Release(); public bool IsProtectedFrom(Vector3 attackerPivot); internal void Initialize(CoverRegistry coverRegistry, float occupy = 0.6f, float leave = 1f, float still = 0.5f); }`
- Produces on `CoverPoint`: `public UnitCover Claimant; public bool IsClaimed; public bool IsClaimedBy(UnitCover unit); public bool IsOccupied; internal bool TryClaim(UnitCover claimant); internal void Release(UnitCover claimant);`
- Produces (tests): `TestWorld.CreateUnit(position)` and `CreateFighter(..., CoverRegistry registry = null)` give every unit a `UnitCover` (wired when a registry is passed).

- [ ] **Step 1: Write the failing EditMode tests**

Append to `Assets/_Project/Tests/EditMode/CoverPointTests.cs`, inside the class, before its closing brace:

```csharp
        UnitCover NewUnit(string name)
        {
            var unitHost = new GameObject(name);
            unitHost.transform.position = host.transform.position + Vector3.up;
            extraHosts.Add(unitHost);
            return unitHost.AddComponent<UnitCover>();
        }

        [Test]
        public void TryClaim_SucceedsWhenUnclaimedOrOwn_FailsForAnotherUnit()
        {
            var a = NewUnit("A");
            var b = NewUnit("B");
            Assert.That(point.IsClaimed, Is.False);
            Assert.That(point.TryClaim(a), Is.True);
            Assert.That(point.TryClaim(a), Is.True, "Re-claiming an own point is fine");
            Assert.That(point.TryClaim(b), Is.False, "One unit per point");
            Assert.That(point.Claimant, Is.SameAs(a));
            Assert.That(point.IsClaimed, Is.True);
        }

        [Test]
        public void Release_ByTheWrongClaimant_IsANoOp()
        {
            var a = NewUnit("A");
            var b = NewUnit("B");
            point.TryClaim(a);
            point.Release(b);
            Assert.That(point.Claimant, Is.SameAs(a));
            point.Release(a);
            Assert.That(point.Claimant, Is.Null);
            Assert.That(point.IsClaimed, Is.False);
        }

        [Test]
        public void IsClaimedBy_NamesTheClaimant()
        {
            var a = NewUnit("A");
            var b = NewUnit("B");
            Assert.That(point.IsClaimedBy(a), Is.False);
            point.TryClaim(a);
            Assert.That(point.IsClaimedBy(a), Is.True);
            Assert.That(point.IsClaimedBy(b), Is.False);
            Assert.That(point.IsClaimedBy(null), Is.False);
        }

        [Test]
        public void IsOccupied_FollowsTheClaimantsStatus()
        {
            var a = NewUnit("A");   // standing on the point (same x/z), so TryOccupy is within the radius
            Assert.That(a.TryReserve(point), Is.True);
            Assert.That(point.IsOccupied, Is.False, "Reserved is not occupied");
            Assert.That(a.TryOccupy(), Is.True);
            Assert.That(point.IsOccupied, Is.True);
        }
```

Add the field `readonly System.Collections.Generic.List<GameObject> extraHosts = new System.Collections.Generic.List<GameObject>();` next to the other fields, and in `TearDown` destroy them first:

```csharp
            foreach (var extra in extraHosts)
                Object.DestroyImmediate(extra);
            extraHosts.Clear();
```

Create `Assets/_Project/Tests/EditMode/UnitCoverTests.cs`:

```csharp
using System;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class UnitCoverTests
    {
        GameObject unitHost;
        GameObject otherHost;
        GameObject pointHostA;
        GameObject pointHostB;
        UnitCover cover;
        UnitCover other;
        CoverPoint pointA;
        CoverPoint pointB;

        [SetUp]
        public void SetUp()
        {
            pointHostA = new GameObject("A");
            pointHostA.transform.position = new Vector3(0f, 0f, 0f);
            pointA = pointHostA.AddComponent<CoverPoint>();
            pointHostB = new GameObject("B");
            pointHostB.transform.position = new Vector3(5f, 0f, 0f);
            pointB = pointHostB.AddComponent<CoverPoint>();
            unitHost = new GameObject("Unit");
            unitHost.transform.position = new Vector3(0f, 1f, 0f);   // on A, at pivot height
            cover = unitHost.AddComponent<UnitCover>();
            otherHost = new GameObject("Other");
            otherHost.transform.position = new Vector3(5f, 1f, 0f);
            other = otherHost.AddComponent<UnitCover>();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(unitHost);
            Object.DestroyImmediate(otherHost);
            Object.DestroyImmediate(pointHostA);
            Object.DestroyImmediate(pointHostB);
        }

        [Test]
        public void StartsWithNoCover_AndIsNotWired()
        {
            Assert.That(cover.Point, Is.Null);
            Assert.That(cover.Status, Is.EqualTo(CoverStatus.None));
            Assert.That(cover.OccupiedByOrder, Is.False);
            Assert.That(cover.IsWired, Is.False);
            Assert.Throws<ArgumentNullException>(() => cover.TryReserve(null));
        }

        [Test]
        public void TryReserve_ClaimsThePoint()
        {
            Assert.That(cover.TryReserve(pointA), Is.True);
            Assert.That(cover.Point, Is.SameAs(pointA));
            Assert.That(cover.Status, Is.EqualTo(CoverStatus.Reserved));
            Assert.That(pointA.Claimant, Is.SameAs(cover));
            Assert.That(cover.OccupiedByOrder, Is.False);
        }

        [Test]
        public void TryReserve_RefusedWhenAnotherUnitHoldsIt_ChangesNothing()
        {
            Assert.That(other.TryReserve(pointA), Is.True);
            Assert.That(cover.TryReserve(pointB), Is.True);
            Assert.That(cover.TryReserve(pointA), Is.False);
            Assert.That(cover.Point, Is.SameAs(pointB), "A refused reservation must not drop the point already held");
            Assert.That(cover.Status, Is.EqualTo(CoverStatus.Reserved));
            Assert.That(pointA.Claimant, Is.SameAs(other));
        }

        [Test]
        public void TryReserve_ASecondPoint_ReleasesTheFirst()
        {
            cover.TryReserve(pointA);
            Assert.That(cover.TryReserve(pointB), Is.True);
            Assert.That(cover.Point, Is.SameAs(pointB));
            Assert.That(pointA.IsClaimed, Is.False, "A unit never holds two points");
            Assert.That(pointB.Claimant, Is.SameAs(cover));
        }

        [Test]
        public void TryOccupy_OnlyWithinTheRadius_AndMarksTheOrder()
        {
            Assert.That(cover.TryOccupy(), Is.False, "Nothing reserved");
            cover.TryReserve(pointB);   // 5 m away
            Assert.That(cover.TryOccupy(), Is.False);
            Assert.That(cover.Status, Is.EqualTo(CoverStatus.Reserved));
            cover.TryReserve(pointA);   // under the unit
            Assert.That(cover.TryOccupy(), Is.True);
            Assert.That(cover.Status, Is.EqualTo(CoverStatus.Occupied));
            Assert.That(cover.OccupiedByOrder, Is.True);
            Assert.That(pointA.IsOccupied, Is.True);
        }

        [Test]
        public void TryReserve_TheOccupiedOwnPoint_KeepsItOccupied()
        {
            cover.TryReserve(pointA);
            cover.TryOccupy();
            Assert.That(cover.TryReserve(pointA), Is.True);
            Assert.That(cover.Status, Is.EqualTo(CoverStatus.Occupied));
            Assert.That(cover.OccupiedByOrder, Is.True);
        }

        [Test]
        public void ReleaseReservation_LeavesAnOccupancyAlone_AndRelease_ClearsEverything()
        {
            cover.TryReserve(pointA);
            cover.ReleaseReservation();
            Assert.That(cover.Status, Is.EqualTo(CoverStatus.None));
            Assert.That(pointA.IsClaimed, Is.False);

            cover.TryReserve(pointA);
            cover.TryOccupy();
            cover.ReleaseReservation();
            Assert.That(cover.Status, Is.EqualTo(CoverStatus.Occupied), "Only a reservation is released");
            Assert.That(pointA.Claimant, Is.SameAs(cover));

            cover.Release();
            Assert.That(cover.Status, Is.EqualTo(CoverStatus.None));
            Assert.That(cover.Point, Is.Null);
            Assert.That(cover.OccupiedByOrder, Is.False);
            Assert.That(pointA.IsClaimed, Is.False);
            cover.Release();   // idempotent
        }

        [Test]
        public void HitChance_FallsBackToOne_AndProtection_NeedsAnOccupancy()
        {
            Assert.That(cover.HitChance, Is.EqualTo(1f));
            Assert.That(cover.IsProtectedFrom(new Vector3(0f, 1f, 5f)), Is.False);
            pointA.Initialize(null, 0.3f);
            cover.TryReserve(pointA);
            Assert.That(cover.HitChance, Is.EqualTo(0.3f));
            Assert.That(cover.IsProtectedFrom(new Vector3(0f, 1f, 5f)), Is.False, "Reserved is not occupied: no protection, and no obstacle ray is cast");
        }
    }
}
```

- [ ] **Step 2: Run the EditMode tests to verify they fail**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.UnitCoverTests|Blackglass.Tests.CoverPointTests"` → expected `EXIT=1` with `error CS0246: The type or namespace name 'UnitCover' could not be found`.

- [ ] **Step 3: Add claims to `CoverPoint` and write `UnitCover`**

In `Assets/_Project/Scripts/Cover/CoverPoint.cs`, after `public Collider Obstacle => obstacle;` add:

```csharp
        /// <summary>The unit that reserved or occupies this point, or null. Written only by UnitCover.</summary>
        public UnitCover Claimant { get; private set; }

        public bool IsClaimed => Claimant != null;

        public bool IsClaimedBy(UnitCover unit) => unit != null && Claimant == unit;

        /// <summary>Claimed, and the claimant stands on it.</summary>
        public bool IsOccupied => Claimant != null && Claimant.Status == CoverStatus.Occupied;

        /// <summary>True when the point is unclaimed or already this unit's. A destroyed claimant counts as none.</summary>
        internal bool TryClaim(UnitCover claimant)
        {
            if (claimant == null)
                throw new System.ArgumentNullException(nameof(claimant));
            if (Claimant != null && Claimant != claimant)
                return false;
            Claimant = claimant;
            return true;
        }

        /// <summary>Frees the point if `claimant` holds it; otherwise nothing happens.</summary>
        internal void Release(UnitCover claimant)
        {
            if (Claimant == claimant)
                Claimant = null;
        }
```

Update the class summary's last sentence to: `Holds its claimant; UnitCover is the only writer.`

Create `Assets/_Project/Scripts/Units/UnitCover.cs`:

```csharp
using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>Where a unit stands with respect to its cover point.</summary>
    public enum CoverStatus
    {
        None,
        /// <summary>A MoveToCover order is walking there; nobody else may take the point.</summary>
        Reserved,
        /// <summary>The unit stands on the point and is protected by its obstacle.</summary>
        Occupied,
    }

    /// <summary>
    /// The unit's cover: the one point it has reserved or occupies. The only writer of CoverPoint claims. A reservation
    /// is driven by CommandableUnit (a MoveToCover order); an occupancy by standing on the point, by order (TryOccupy)
    /// or by chance (standing still within occupyRadius of a free point, which needs the registry). Health is optional,
    /// as everywhere: looked up lazily, Died subscribed to only when present. Runs on simulation time.
    /// </summary>
    public sealed class UnitCover : MonoBehaviour
    {
        [SerializeField] CoverRegistry registry;
        [SerializeField, Min(0f)] float occupyRadius = 0.6f;
        [SerializeField, Min(0f)] float leaveRadius = 1f;
        // Below this flat speed a unit counts as standing; above it a unit walking across a point claims nothing.
        [SerializeField, Min(0f)] float stillSpeed = 0.5f;

        // The method group converted once; passing IsUnclaimed directly would allocate a delegate every frame.
        static readonly Func<CoverPoint, bool> isUnclaimed = IsUnclaimed;

        Health ownHealth;
        Vector3 lastFlatPosition;
        bool hasLastPosition;

        /// <summary>The reserved or occupied point, else null.</summary>
        public CoverPoint Point { get; private set; }

        public CoverStatus Status { get; private set; }

        /// <summary>True while an occupancy came from a MoveToCover arrival; false for one reached by standing there.</summary>
        public bool OccupiedByOrder { get; private set; }

        /// <summary>True when a registry is wired, so standing on a free point occupies it.</summary>
        public bool IsWired => registry != null;

        public float OccupyRadius => occupyRadius;

        /// <summary>The held point's hit chance, or 1 (every shot lands) without one.</summary>
        public float HitChance => Point != null ? Point.HitChance : 1f;

        // Looked up lazily so a Health added after this component is still found.
        Health OwnHealth => ownHealth != null ? ownHealth : ownHealth = GetComponent<Health>();

        internal void Initialize(CoverRegistry coverRegistry, float occupy = 0.6f, float leave = 1f, float still = 0.5f)
        {
            registry = coverRegistry;
            occupyRadius = occupy;
            leaveRadius = leave;
            stillSpeed = still;
        }

        /// <summary>
        /// Claims the point as reserved. If another unit holds it: false, nothing changes (including any point this
        /// unit already holds). If it is this unit's own point: the status is kept (an occupied point stays occupied).
        /// Otherwise any other point this unit held, reserved or occupied, is released first, so a unit never holds
        /// two points.
        /// </summary>
        public bool TryReserve(CoverPoint point)
        {
            if (point == null)
                throw new ArgumentNullException(nameof(point));
            if (point == Point)
                return true;
            if (!point.TryClaim(this))
                return false;
            Release();
            Point = point;
            Status = CoverStatus.Reserved;
            OccupiedByOrder = false;
            return true;
        }

        /// <summary>
        /// Held (reserved or occupied) and within occupyRadius → Occupied, by order. Returns whether the unit now
        /// occupies its point. Called by CommandableUnit when a cover walk arrives.
        /// </summary>
        public bool TryOccupy()
        {
            if (Status == CoverStatus.None || !IsWithin(occupyRadius))
                return false;
            Status = CoverStatus.Occupied;
            OccupiedByOrder = true;
            return true;
        }

        /// <summary>Releases only a reservation; an occupancy is left alone.</summary>
        public void ReleaseReservation()
        {
            if (Status == CoverStatus.Reserved)
                Release();
        }

        /// <summary>Releases whatever is held.</summary>
        public void Release()
        {
            if (Point != null)
                Point.Release(this);
            Point = null;
            Status = CoverStatus.None;
            OccupiedByOrder = false;
        }

        /// <summary>Occupied, and the point protects this unit from an attacker at that pivot.</summary>
        public bool IsProtectedFrom(Vector3 attackerPivot) =>
            Status == CoverStatus.Occupied && Point != null && Point.ProtectsFrom(attackerPivot, transform.position);

        void OnEnable()
        {
            hasLastPosition = false;
            if (OwnHealth != null)
                OwnHealth.Died += OnDied;
        }

        // Death is covered twice: Health deactivates the unit (this runs), and Died handles a Health that does not.
        void OnDisable()
        {
            if (OwnHealth != null)
                OwnHealth.Died -= OnDied;
            Release();
        }

        void OnDied() => Release();

        void Update()
        {
            if (!SimulationTime.IsRunning)
                return;
            var flat = transform.position;
            flat.y = 0f;
            // Tracked here rather than read from the mover, so paths, direct steering and avoidance pushes all count.
            var speed = hasLastPosition ? Vector3.Distance(flat, lastFlatPosition) / Time.deltaTime : float.PositiveInfinity;
            lastFlatPosition = flat;
            hasLastPosition = true;

            if (Status != CoverStatus.None && (Point == null || !Point.gameObject.activeInHierarchy))
            {
                Release();   // the cover became invalid
                return;
            }
            if (Status == CoverStatus.Occupied)
            {
                if (!IsWithin(leaveRadius))
                    Release();
                return;
            }
            if (Status == CoverStatus.Reserved || registry == null || speed >= stillSpeed)
                return;
            if (CoverRules.TryChooseNearest(registry.Points, transform.position, occupyRadius, isUnclaimed, out var nearest)
                && nearest.TryClaim(this))
            {
                Point = nearest;
                Status = CoverStatus.Occupied;
                OccupiedByOrder = false;
            }
        }

        static bool IsUnclaimed(CoverPoint point) => !point.IsClaimed;

        bool IsWithin(float radius) => Point != null && CoverRules.FlatDistance(transform.position, Point.Position) <= radius;
    }
}
```

- [ ] **Step 4: Run the EditMode tests to verify they pass**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.UnitCoverTests|Blackglass.Tests.CoverPointTests"` → expected `total="16" passed="16"`, `EXIT=0`.

- [ ] **Step 5: Give test units a `UnitCover` and write the failing PlayMode tests**

In `Assets/_Project/Tests/PlayMode/TestSupport/TestWorld.cs`:

Change `CreateUnit` to add the component before `CommandableUnit`:

```csharp
        public CommandableUnit CreateUnit(Vector3 groundPosition)
        {
            var unit = Track(GameObject.CreatePrimitive(PrimitiveType.Capsule));
            unit.name = "TestUnit";
            unit.transform.position = groundPosition + Vector3.up;
            unit.AddComponent<UnitMover>();
            unit.GetComponent<NavMeshAgent>().baseOffset = 1f;
            unit.AddComponent<UnitAttacker>();
            unit.AddComponent<UnitCover>();
            return unit.AddComponent<CommandableUnit>();
        }
```

Change `CreateFighter`'s signature and body:

```csharp
        public CommandableUnit CreateFighter(Vector3 groundPosition, int maxHealth = 100, int damage = 25, float cooldown = 1f,
            CombatRole role = CombatRole.Melee, float range = 2f, CoverRegistry registry = null)
        {
            var host = Track(GameObject.CreatePrimitive(PrimitiveType.Capsule));
            host.name = "TestFighter";
            host.SetActive(false);
            host.transform.position = groundPosition + Vector3.up;
            host.AddComponent<UnitMover>();
            host.GetComponent<NavMeshAgent>().baseOffset = 1f;
            host.AddComponent<UnitAttacker>().Initialize(range, damage, cooldown, role);
            host.AddComponent<Health>().Initialize(maxHealth);
            var cover = host.AddComponent<UnitCover>();
            if (registry != null)
                cover.Initialize(registry);
            var unit = host.AddComponent<CommandableUnit>();
            host.AddComponent<AutoRetaliate>();
            host.SetActive(true);
            return unit;
        }
```

Create `Assets/_Project/Tests/PlayMode/UnitCoverPlayModeTests.cs`:

```csharp
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class UnitCoverPlayModeTests
    {
        TestWorld world;
        CoverPoint point;
        CoverRegistry registry;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            // A 0.9 m wall from z -0.25 to 0.25 and one point 0.75 m south of it.
            var environment = world.CreateEnvironment((new Vector3(0f, 0.45f, 0f), new Vector3(4f, 0.9f, 0.5f)));
            point = world.CreateCoverPoint(new Vector3(0f, 0f, -1f), Vector3.forward, TestWorld.ObstacleCollider(environment));
            registry = world.CreateRegistry(point);
        }

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        static UnitCover CoverOf(Component unit) => unit.GetComponent<UnitCover>();

        IEnumerator WalkOntoThePoint(CommandableUnit unit)
        {
            Assert.That(unit.Issue(new MoveCommand(point.Position)), Is.True);
            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 10f);
            Assert.That(unit.CurrentCommand, Is.Null, "Precondition: the unit arrived");
            yield return TestWorld.WaitUntil(() => CoverOf(unit).Status == CoverStatus.Occupied, 2f);
        }

        [UnityTest]
        public IEnumerator PlainMoveOntoAFreePoint_OccupiesItAfterStopping_WithoutTheOrderFlag()
        {
            var unit = world.CreateFighter(new Vector3(0f, 0f, -6f), registry: registry);
            yield return WalkOntoThePoint(unit);

            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.Occupied));
            Assert.That(CoverOf(unit).Point, Is.SameAs(point));
            Assert.That(CoverOf(unit).OccupiedByOrder, Is.False, "Standing there is not an ordered occupancy");
            Assert.That(point.Claimant, Is.SameAs(CoverOf(unit)));
        }

        [UnityTest]
        public IEnumerator WalkingThroughAFreePoint_NeverClaimsIt()
        {
            var unit = world.CreateFighter(new Vector3(4f, 0f, -1f), registry: registry);
            Assert.That(unit.Issue(new MoveCommand(new Vector3(-4f, 0f, -1f))), Is.True);   // straight across the point
            var everClaimed = false;
            var deadline = Time.realtimeSinceStartup + 6f;
            while (unit.CurrentCommand != null && Time.realtimeSinceStartup < deadline)
            {
                everClaimed |= point.IsClaimed;
                yield return null;
            }
            yield return new WaitForSeconds(0.5f);

            Assert.That(unit.CurrentCommand, Is.Null, "Precondition: the walk finished");
            Assert.That(everClaimed, Is.False, "A moving unit claims nothing");
            Assert.That(point.IsClaimed, Is.False, "3 m away at the end: nothing to claim");
        }

        [UnityTest]
        public IEnumerator OccupiedUnit_MovingAway_ReleasesThePoint()
        {
            var unit = world.CreateFighter(new Vector3(0f, 0f, -6f), registry: registry);
            yield return WalkOntoThePoint(unit);
            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.Occupied), "Precondition");

            Assert.That(unit.Issue(new MoveCommand(new Vector3(0f, 0f, -6f))), Is.True);
            yield return TestWorld.WaitUntil(() => CoverOf(unit).Status == CoverStatus.None, 3f);

            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.None), "Beyond 1 m the point is released");
            Assert.That(point.IsClaimed, Is.False);
        }

        [UnityTest]
        public IEnumerator DyingUnit_ReleasesItsPoint()
        {
            var unit = world.CreateFighter(new Vector3(0f, 0f, -6f), registry: registry);
            yield return WalkOntoThePoint(unit);
            Assert.That(point.IsClaimed, Is.True, "Precondition");

            unit.GetComponent<Health>().TakeDamage(1000);
            yield return null;

            Assert.That(point.IsClaimed, Is.False, "A corpse holds no cover");
            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.None));
        }

        [UnityTest]
        public IEnumerator DestroyedPoint_IsReleased()
        {
            var unit = world.CreateFighter(new Vector3(0f, 0f, -6f), registry: registry);
            yield return WalkOntoThePoint(unit);
            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.Occupied), "Precondition");

            Object.Destroy(point.gameObject);
            yield return null;
            yield return null;

            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.None), "Cover that no longer exists is let go");
            Assert.That(CoverOf(unit).Point, Is.Null);
        }

        [UnityTest]
        public IEnumerator SteeringAway_ReleasesThePoint()
        {
            var unit = world.CreateFighter(new Vector3(0f, 0f, -6f), registry: registry);
            yield return WalkOntoThePoint(unit);
            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.Occupied), "Precondition");

            unit.SetMoveIntent(Vector3.back);   // direct control walks it south
            yield return TestWorld.WaitUntil(() => CoverOf(unit).Status == CoverStatus.None, 3f);
            unit.SetMoveIntent(Vector3.zero);

            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.None));
            Assert.That(point.IsClaimed, Is.False);
        }
    }
}
```

- [ ] **Step 6: Run the PlayMode tests**

Run: `Tools/run-tests.sh PlayMode Blackglass.Tests.UnitCoverPlayModeTests` → expected `total="6" passed="6"`, `EXIT=0`. Troubleshooting: if `PlainMoveOntoAFreePoint...` times out on the occupancy wait, the unit stopped more than 0.6 m from the point (check `UnitMover.stoppingDistance` is 0.1 and the point is at (0, 0, −1), not inside the wall's erosion band at z ≥ −0.75).

Run: `Tools/run-tests.sh EditMode` → expected `total="262" passed="262"`, `EXIT=0`.
Run: `Tools/run-tests.sh PlayMode` → expected `total="223" passed="223"`, `EXIT=0`.

- [ ] **Step 7: Commit**

```bash
git add Assets/_Project/Scripts/Units/UnitCover.cs Assets/_Project/Scripts/Units/UnitCover.cs.meta Assets/_Project/Scripts/Cover/CoverPoint.cs Assets/_Project/Tests/EditMode/UnitCoverTests.cs Assets/_Project/Tests/EditMode/UnitCoverTests.cs.meta Assets/_Project/Tests/EditMode/CoverPointTests.cs Assets/_Project/Tests/PlayMode/UnitCoverPlayModeTests.cs Assets/_Project/Tests/PlayMode/UnitCoverPlayModeTests.cs.meta Assets/_Project/Tests/PlayMode/TestSupport/TestWorld.cs
git commit -m "Add UnitCover: reservation, occupancy and automatic occupancy of cover points

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 3: `MoveToCoverCommand` and its handling in `CommandableUnit`

**Files:**
- Modify: `Assets/_Project/Scripts/Commands/UnitCommands.cs`
- Modify: `Assets/_Project/Scripts/Units/CommandableUnit.cs`
- Modify: `Assets/_Project/Tests/EditMode/CommandableUnitTests.cs`
- Create: `Assets/_Project/Tests/PlayMode/CoverOrderPlayModeTests.cs`

**Interfaces:**
- Consumes: `UnitCover` (`TryReserve`, `TryOccupy`, `ReleaseReservation`, `Status`), `CoverPoint` (`IsClaimed`, `IsClaimedBy`, `Position`), `CoverRules.FlatDistance` (Tasks 1–2).
- Produces: `public sealed class MoveToCoverCommand : UnitCommand { public MoveToCoverCommand(CoverPoint point); public CoverPoint Point { get; } }`
- Produces on `CommandableUnit`: `[RequireComponent(typeof(UnitMover), typeof(UnitAttacker), typeof(UnitCover))]`; `public UnitCover Cover { get; }`; `public Health AttackTarget { get; }` (the current attack's target, else the first pending attack's target while a cover order is current, else null); `Issue` accepts `MoveToCoverCommand`; `const float CoverWalkTimeout = 3f`, `const float CoverProgressStep = 0.05f`.

- [ ] **Step 1: Write the failing EditMode tests**

Append to `Assets/_Project/Tests/EditMode/CommandableUnitTests.cs`, inside the class before its closing brace:

```csharp
        CoverPoint NewPoint(string name, out GameObject pointHost)
        {
            pointHost = new GameObject(name);
            return pointHost.AddComponent<CoverPoint>();
        }

        [Test]
        public void UnitCover_IsRequired()
        {
            Assert.That(unitHost.GetComponent<UnitCover>(), Is.Not.Null);
            Assert.That(unit.Cover, Is.SameAs(unitHost.GetComponent<UnitCover>()));
            Assert.That(unitHost.GetComponent<Health>(), Is.Null, "Requiring UnitCover must not drag a Health onto every unit");
        }

        [Test]
        public void MoveToCoverCommand_RejectsANullPoint()
        {
            Assert.Throws<ArgumentNullException>(() => new MoveToCoverCommand(null));
        }

        [Test]
        public void Issue_MoveToCover_OnAPointAnotherUnitHolds_IsRefused_AndChangesNothing()
        {
            var point = NewPoint("Cover", out var pointHost);
            var otherHost = new GameObject("Other");
            var other = otherHost.AddComponent<UnitCover>();
            Assert.That(other.TryReserve(point), Is.True);
            var attack = new AttackCommand(target);
            unit.Issue(attack);

            Assert.That(unit.Issue(new MoveToCoverCommand(point)), Is.False);
            Assert.That(unit.CurrentCommand, Is.SameAs(attack));
            Assert.That(point.Claimant, Is.SameAs(other));
            Assert.That(unit.Cover.Status, Is.EqualTo(CoverStatus.None));
            Object.DestroyImmediate(otherHost);
            Object.DestroyImmediate(pointHost);
        }

        [Test]
        public void Issue_MoveToCover_WithoutANavMesh_IsRefusedSilently_AndThePointStaysUnclaimed()
        {
            var point = NewPoint("Cover", out var pointHost);
            // CanMoveTo is silent (unlike MoveTo), so no warning is expected here.
            Assert.That(unit.Issue(new MoveToCoverCommand(point)), Is.False);
            Assert.That(unit.CurrentCommand, Is.Null);
            Assert.That(point.IsClaimed, Is.False, "A refused start must not leave a claim behind");
            Assert.That(unit.Cover.Status, Is.EqualTo(CoverStatus.None));
            Object.DestroyImmediate(pointHost);
        }

        [Test]
        public void AttackTarget_ReadsTheCurrentAttack_AndIsNullOtherwise()
        {
            Assert.That(unit.AttackTarget, Is.Null);
            unit.Issue(new AttackCommand(target));
            Assert.That(unit.AttackTarget, Is.SameAs(target));
            unit.Issue(new StopCommand());
            Assert.That(unit.AttackTarget, Is.Null);
        }
```

- [ ] **Step 2: Run the EditMode tests to verify they fail**

Run: `Tools/run-tests.sh EditMode Blackglass.Tests.CommandableUnitTests` → expected `EXIT=1` with `error CS0246: The type or namespace name 'MoveToCoverCommand' could not be found` and `error CS1061 ... 'CommandableUnit' does not contain a definition for 'Cover'`.

- [ ] **Step 3: Add the command and handle it in `CommandableUnit`**

In `Assets/_Project/Scripts/Commands/UnitCommands.cs`, after `AttackCommand`:

```csharp
    /// <summary>
    /// Walk to a cover point and occupy it. The point is reserved when the order starts and refused when another unit
    /// holds it; the reservation lives exactly as long as this order is current.
    /// </summary>
    public sealed class MoveToCoverCommand : UnitCommand
    {
        public MoveToCoverCommand(CoverPoint point)
        {
            if (point == null)
                throw new ArgumentNullException(nameof(point));
            Point = point;
        }

        public CoverPoint Point { get; }
    }
```

Update the base class comment: `Future commands (Interact) are new subclasses.` stays; the summary now lists Move, Attack, MoveToCover and Stop.

In `Assets/_Project/Scripts/Units/CommandableUnit.cs`:

1. The attribute: `[RequireComponent(typeof(UnitMover), typeof(UnitAttacker), typeof(UnitCover))]`. Extend the class summary with: `A MoveToCover order reserves its point when it starts, occupies it on arrival, and gives up after CoverWalkTimeout without progress.`

2. Constants, after `MaxRepositionsWithoutShot`:

```csharp
        // A cover walk that has not come closer to its point for this long is held off (another unit stands there).
        const float CoverWalkTimeout = 3f;
        // The least the flat distance to the point must fall for the walk to count as progressing.
        const float CoverProgressStep = 0.05f;
```

3. Fields, after `Health ownHealth;`:

```csharp
        UnitCover cover;
        float coverBestDistance;
        float coverLastProgressTime;
```

4. Properties, after `AttackPhase`:

```csharp
        /// <summary>The unit's cover state; AI and views read cover through the unit.</summary>
        public UnitCover Cover => cover != null ? cover : cover = GetComponent<UnitCover>();

        /// <summary>
        /// Whom the unit is attacking: the current attack's target, else the first pending attack's target while a
        /// cover order is current (a unit walking to cover with its attack queued is already engaged), else null.
        /// </summary>
        public Health AttackTarget
        {
            get
            {
                switch (queue.Current)
                {
                    case AttackCommand attack:
                        return attack.Target;
                    case MoveToCoverCommand _ when queue.Pending.Count > 0 && queue.Pending[0] is AttackCommand pending:
                        return pending.Target;
                    default:
                        return null;
                }
            }
        }
```

5. `Issue`: add `case MoveToCoverCommand _:` to the `case MoveCommand _: case AttackCommand _: break;` group. Extend the summary's refusal list with `a cover point another unit holds`.

6. `Update`: add a case to the switch:

```csharp
                case MoveToCoverCommand toCover:
                    UpdateCover(toCover);
                    break;
```

7. `CanStart`: add

```csharp
                case MoveToCoverCommand toCover:
                    return CanTakeCover(toCover.Point);
```

8. `TryStart` becomes (every check before any side effect; the cover reservation and the release of an old one happen only once the new order is accepted):

```csharp
        // Starts carrying out an order. Returns false, without side effects, if it cannot be carried out.
        bool TryStart(UnitCommand command)
        {
            switch (command)
            {
                case MoveCommand move:
                    if (!Mover.MoveTo(move.Destination))
                        return false;
                    Cover.ReleaseReservation();
                    ResetAttack();
                    return true;
                case AttackCommand attack:
                    if (!IsAttackable(attack.Target))
                        return false;
                    Mover.Stop();
                    Cover.ReleaseReservation();
                    ResetAttack();
                    return true;
                case MoveToCoverCommand toCover:
                    // MoveTo returns SetDestination's result, which can be false even after CanMoveTo passed, so it
                    // runs before the claim; TryReserve cannot fail once CanTakeCover has passed.
                    if (!CanTakeCover(toCover.Point) || !Mover.MoveTo(toCover.Point.Position))
                        return false;
                    Cover.TryReserve(toCover.Point);
                    ResetAttack();
                    coverBestDistance = float.PositiveInfinity;
                    coverLastProgressTime = Time.time;
                    return true;
                default:
                    return false;
            }
        }
```

9. `StopAll`: add `Cover.ReleaseReservation();` as its first line (an occupancy survives a Stop; the leave rule ends it).

10. New members, after `StopAll`:

```csharp
        // A point that exists, is active, is unclaimed or ours, and has walkable mesh within the usual 2 m.
        bool CanTakeCover(CoverPoint point) =>
            point != null && point.gameObject.activeInHierarchy && (!point.IsClaimed || point.IsClaimedBy(Cover))
            && Mover.CanMoveTo(point.Position);

        // Arrival occupies the point. A vanished point ends the order silently, as a vanished attack target does. A
        // walk that stops making progress (another unit stands on the spot, so HasArrived never turns true) gives up
        // after CoverWalkTimeout without a log: an expected situation in play. Scaled time never advances while paused,
        // so a pause is not a stall.
        void UpdateCover(MoveToCoverCommand order)
        {
            var point = order.Point;
            if (point == null || !point.gameObject.activeInHierarchy || Cover.Status == CoverStatus.None)
            {
                Cover.ReleaseReservation();
                Mover.Stop();
                StartNext();
                return;
            }
            if (Mover.HasArrived)
            {
                if (!Cover.TryOccupy())
                {
                    Debug.LogWarning($"{name} could not occupy {point.name}: the path ended {CoverRules.FlatDistance(transform.position, point.Position):0.0} m from it.", this);
                    Cover.ReleaseReservation();
                }
                StartNext();
                return;
            }
            var distance = CoverRules.FlatDistance(transform.position, point.Position);
            if (distance < coverBestDistance - CoverProgressStep)
            {
                coverBestDistance = distance;
                coverLastProgressTime = Time.time;
            }
            else if (Time.time >= coverLastProgressTime + CoverWalkTimeout)
            {
                Cover.ReleaseReservation();
                Mover.Stop();
                StartNext();
            }
        }
```

11. In `UpdateAttack`, the comment above `if (Attacker.TryAttack(target))` is unchanged for now (Task 4 changes what `TryAttack` returns and updates the comment).

- [ ] **Step 4: Run the EditMode tests to verify they pass**

Run: `Tools/run-tests.sh EditMode Blackglass.Tests.CommandableUnitTests` → expected `total="29" passed="29"`, `EXIT=0`.
Run: `Tools/run-tests.sh EditMode` → expected `total="267" passed="267"`, `EXIT=0` (every fixture that adds `CommandableUnit` or `SelectableUnit` now also gets a `UnitCover`, which requires nothing; `IsAlive_WithoutHealth_IsTrue` and the `ActiveCharacterTests` that kill a unit must still pass).

- [ ] **Step 4b: Give the unit prefabs a `UnitCover`**

`RequireComponent` only acts when a component is added; the six units in `Prototype.unity` are prefab instances that already exist, so `CommandableUnit.Cover` would be null in every scene test until Task 9. Add the component to both prefabs now with a one-off editor script (same pattern as the scene builders; never committed).

Create `Assets/_Project/Editor/AddUnitCover.cs`:

```csharp
// TEMPORARY: gives both unit prefabs the UnitCover that CommandableUnit now requires, so the scene's prefab
// instances carry it before Task 9 wires the registry. Deleted after running (never committed).
using Blackglass;
using UnityEditor;
using UnityEngine;

public static class AddUnitCover
{
    static readonly string[] PrefabPaths =
    {
        "Assets/_Project/Prefabs/FriendlyUnit.prefab",
        "Assets/_Project/Prefabs/HostileUnit.prefab",
    };

    public static void Run()
    {
        foreach (var path in PrefabPaths)
        {
            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                // The Editor may already have added the dependency to the loaded root; adding twice would be an error.
                if (root.GetComponent<UnitCover>() == null)
                    root.AddComponent<UnitCover>();
                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }
        AssetDatabase.SaveAssets();
        Debug.Log("[AddUnitCover] Both unit prefabs carry UnitCover");
    }
}
```

Run it, check, and delete it:

```bash
"/c/Program Files/Unity/Hub/Editor/6000.3.25f1/Editor/Unity.exe" -batchmode -quit -projectPath "$(pwd -W)" -executeMethod AddUnitCover.Run -logFile "$(pwd -W)/Logs/AddUnitCover.log"; echo "EXIT=$?"
grep -E '\[AddUnitCover\]|error CS|Exception' Logs/AddUnitCover.log | head -10
guid=$(grep guid Assets/_Project/Scripts/Units/UnitCover.cs.meta | sed 's/.*guid: //'); grep -c "$guid" Assets/_Project/Prefabs/FriendlyUnit.prefab Assets/_Project/Prefabs/HostileUnit.prefab   # expect 1 each
rm -r Assets/_Project/Editor Assets/_Project/Editor.meta
git status --short   # the two prefabs modified; no Editor folder
```

**Expected:** `EXIT=0`, the log line `[AddUnitCover] Both unit prefabs carry UnitCover`, a count of 1 for each prefab.

- [ ] **Step 5: Write the failing PlayMode tests**

Create `Assets/_Project/Tests/PlayMode/CoverOrderPlayModeTests.cs`:

```csharp
using System.Collections;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CoverOrderPlayModeTests
    {
        TestWorld world;
        GameObject environment;
        CoverPoint point;
        CoverRegistry registry;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            // A 0.9 m wall from z -0.25 to 0.25 (x -2..2) with one point 0.75 m south of it.
            environment = world.CreateEnvironment((new Vector3(0f, 0.45f, 0f), new Vector3(4f, 0.9f, 0.5f)));
            point = world.CreateCoverPoint(new Vector3(0f, 0f, -1f), Vector3.forward, TestWorld.ObstacleCollider(environment));
            registry = world.CreateRegistry(point);
        }

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        static UnitCover CoverOf(Component unit) => unit.GetComponent<UnitCover>();

        CommandableUnit Fighter(Vector3 at, CombatRole role = CombatRole.Melee, float range = 2f) =>
            world.CreateFighter(at, role: role, range: range, registry: registry);

        IEnumerator WaitOccupied(CommandableUnit unit, float timeout = 10f) =>
            TestWorld.WaitUntil(() => CoverOf(unit).Status == CoverStatus.Occupied, timeout);

        [UnityTest]
        public IEnumerator MoveToCover_ReservesOnIssue_ThenOccupiesOnArrival()
        {
            var unit = Fighter(new Vector3(0f, 0f, -8f));
            var order = new MoveToCoverCommand(point);

            Assert.That(unit.Issue(order), Is.True);
            Assert.That(unit.CurrentCommand, Is.SameAs(order));
            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.Reserved), "Reserved the moment the order starts");
            Assert.That(point.Claimant, Is.SameAs(CoverOf(unit)));
            Assert.That(point.IsOccupied, Is.False);

            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 10f);

            Assert.That(unit.CurrentCommand, Is.Null, "The order ends on arrival");
            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.Occupied));
            Assert.That(CoverOf(unit).OccupiedByOrder, Is.True);
            Assert.That(point.IsOccupied, Is.True);
            Assert.That(CoverRules.FlatDistance(unit.transform.position, point.Position), Is.LessThan(0.6f));
        }

        [UnityTest]
        public IEnumerator SecondUnit_OrderedToAClaimedPoint_IsRefused_AndKeepsItsOrders()
        {
            var first = Fighter(new Vector3(0f, 0f, -8f));
            var second = Fighter(new Vector3(4f, 0f, -8f));
            var move = new MoveCommand(new Vector3(4f, 0f, -4f));
            Assert.That(second.Issue(move), Is.True);
            Assert.That(first.Issue(new MoveToCoverCommand(point)), Is.True);
            yield return null;

            Assert.That(second.Issue(new MoveToCoverCommand(point)), Is.False);
            Assert.That(second.CurrentCommand, Is.SameAs(move), "A refused order leaves the unit's orders alone");
            Assert.That(CoverOf(second).Status, Is.EqualTo(CoverStatus.None));
            Assert.That(point.Claimant, Is.SameAs(CoverOf(first)));
        }

        [UnityTest]
        public IEnumerator ThreeIdleUnits_GivenTheSameCoverOrder_OnlyOneTakesIt()
        {
            var units = new[] { Fighter(new Vector3(-3f, 0f, -8f)), Fighter(new Vector3(0f, 0f, -8f)), Fighter(new Vector3(3f, 0f, -8f)) };
            yield return null;

            Assert.That(GroupOrders.Issue(units, new MoveToCoverCommand(point), IssueMode.Replace), Is.EqualTo(1));
            Assert.That(units.Count(u => u.CurrentCommand is MoveToCoverCommand), Is.EqualTo(1));
            Assert.That(units.Count(u => u.CurrentCommand == null), Is.EqualTo(2), "The others keep their (empty) orders");
            Assert.That(units.Count(u => CoverOf(u).Status == CoverStatus.Reserved), Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator TwoBusyUnits_QueueTheSamePoint_TheSecondIsSkipped()
        {
            // The near unit's move is 2 m, the far unit's 6 m, so the near one reaches its cover order first.
            var near = Fighter(new Vector3(-2f, 0f, -6f));
            var far = Fighter(new Vector3(2f, 0f, -16f));
            Assert.That(near.Issue(new MoveCommand(new Vector3(-2f, 0f, -4f))), Is.True);
            Assert.That(far.Issue(new MoveCommand(new Vector3(2f, 0f, -10f))), Is.True);
            var order = new MoveToCoverCommand(point);
            Assert.That(near.Issue(order, IssueMode.Append), Is.True);
            Assert.That(far.Issue(order, IssueMode.Append), Is.True);
            Assert.That(point.IsClaimed, Is.False, "A queued cover order reserves nothing until it starts");

            yield return TestWorld.WaitUntil(() => CoverOf(near).Status == CoverStatus.Occupied && far.CurrentCommand == null, 15f);

            Assert.That(CoverOf(near).Status, Is.EqualTo(CoverStatus.Occupied));
            Assert.That(far.CurrentCommand, Is.Null, "The second cover order was skipped when its turn came");
            Assert.That(CoverOf(far).Status, Is.EqualTo(CoverStatus.None));
            Assert.That(point.Claimant, Is.SameAs(CoverOf(near)));
        }

        [UnityTest]
        public IEnumerator ReplacingACoverOrderWithAMove_ReleasesTheReservation()
        {
            var unit = Fighter(new Vector3(0f, 0f, -8f));
            Assert.That(unit.Issue(new MoveToCoverCommand(point)), Is.True);
            yield return null;

            Assert.That(unit.Issue(new MoveCommand(new Vector3(4f, 0f, -8f))), Is.True);
            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.None));
            Assert.That(point.IsClaimed, Is.False);
        }

        [UnityTest]
        public IEnumerator ReplacingACoverOrderWithARefusedMove_KeepsTheReservation()
        {
            var unit = Fighter(new Vector3(0f, 0f, -8f));
            var order = new MoveToCoverCommand(point);
            Assert.That(unit.Issue(order), Is.True);
            yield return null;

            LogAssert.Expect(LogType.Warning, new Regex("no walkable NavMesh point"));
            Assert.That(unit.Issue(new MoveCommand(new Vector3(100f, 0f, 100f))), Is.False);
            Assert.That(unit.CurrentCommand, Is.SameAs(order));
            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.Reserved), "A refused replacement changes nothing");
            Assert.That(point.Claimant, Is.SameAs(CoverOf(unit)));
        }

        [UnityTest]
        public IEnumerator ACoverOrderToASecondPoint_ReleasesTheFirst()
        {
            var second = world.CreateCoverPoint(new Vector3(1.5f, 0f, -1f), Vector3.forward, TestWorld.ObstacleCollider(environment));
            registry.Initialize(point, second);
            var unit = Fighter(new Vector3(0f, 0f, -8f));
            Assert.That(unit.Issue(new MoveToCoverCommand(point)), Is.True);
            yield return null;

            Assert.That(unit.Issue(new MoveToCoverCommand(second)), Is.True);
            Assert.That(CoverOf(unit).Point, Is.SameAs(second));
            Assert.That(point.IsClaimed, Is.False, "A unit never holds two points");
            Assert.That(second.Claimant, Is.SameAs(CoverOf(unit)));
        }

        [UnityTest]
        public IEnumerator Stop_ReleasesAReservation_ButNotAnOccupancy()
        {
            var unit = Fighter(new Vector3(0f, 0f, -8f));
            Assert.That(unit.Issue(new MoveToCoverCommand(point)), Is.True);
            yield return null;
            Assert.That(unit.Issue(new StopCommand()), Is.True);
            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.None), "Stop cancels the walk, so the reservation goes");
            Assert.That(point.IsClaimed, Is.False);

            Assert.That(unit.Issue(new MoveToCoverCommand(point)), Is.True);
            yield return WaitOccupied(unit);
            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.Occupied), "Precondition");

            Assert.That(unit.Issue(new StopCommand()), Is.True);
            yield return null;
            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.Occupied), "A unit standing in cover stays in cover when stopped");
            Assert.That(point.Claimant, Is.SameAs(CoverOf(unit)));
        }

        [UnityTest]
        public IEnumerator WalkingAwayFromOrderedCover_ReleasesIt()
        {
            var unit = Fighter(new Vector3(0f, 0f, -8f));
            Assert.That(unit.Issue(new MoveToCoverCommand(point)), Is.True);
            yield return WaitOccupied(unit);
            Assert.That(CoverOf(unit).OccupiedByOrder, Is.True, "Precondition");

            Assert.That(unit.Issue(new MoveCommand(new Vector3(0f, 0f, -8f))), Is.True);
            yield return TestWorld.WaitUntil(() => CoverOf(unit).Status == CoverStatus.None, 3f);

            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.None));
            Assert.That(CoverOf(unit).OccupiedByOrder, Is.False);
            Assert.That(point.IsClaimed, Is.False);
        }

        [UnityTest]
        public IEnumerator DirectControl_DropsTheCoverOrder_AndSteeringAwayLater_ReleasesTheOccupancy()
        {
            var unit = Fighter(new Vector3(0f, 0f, -8f));
            Assert.That(unit.Issue(new MoveToCoverCommand(point)), Is.True);
            unit.SetMoveIntent(Vector3.back);
            yield return null;
            yield return null;
            Assert.That(unit.CurrentCommand, Is.Null, "Held keys drop every order");
            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.None), "...and the reservation with it");
            Assert.That(point.IsClaimed, Is.False);

            unit.SetMoveIntent(Vector3.zero);
            Assert.That(unit.Issue(new MoveToCoverCommand(point)), Is.True);
            yield return WaitOccupied(unit);
            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.Occupied), "Precondition");

            unit.SetMoveIntent(Vector3.back);
            yield return TestWorld.WaitUntil(() => CoverOf(unit).Status == CoverStatus.None, 3f);
            unit.SetMoveIntent(Vector3.zero);
            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.None), "Driven more than 1 m away, the occupancy ends");
        }

        [UnityTest]
        public IEnumerator AppendedCover_BehindAMove_RunsAfterIt()
        {
            var unit = Fighter(new Vector3(0f, 0f, -8f));
            Assert.That(unit.Issue(new MoveCommand(new Vector3(3f, 0f, -6f))), Is.True);
            var order = new MoveToCoverCommand(point);
            Assert.That(unit.Issue(order, IssueMode.Append), Is.True);
            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.None), "Not reserved while pending");

            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == order, 10f);
            Assert.That(unit.CurrentCommand, Is.SameAs(order), "The cover order started after the move");
            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.Reserved));
            yield return WaitOccupied(unit);
            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.Occupied));
        }

        [UnityTest]
        public IEnumerator CoverOrderWhilePaused_ReservesAtOnce_ThenRunsAfterResume_AndTheAppendedAttackFiresFromCover()
        {
            var pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            var unit = Fighter(new Vector3(0f, 0f, -8f), CombatRole.Ranged, 8f);
            var dummy = world.CreateDummy(new Vector3(0f, 0f, 5f));   // 6 m north of the point, over the wall
            yield return new WaitForFixedUpdate();

            pause.Pause();
            Assert.That(unit.Issue(new MoveToCoverCommand(point)), Is.True);
            Assert.That(unit.Issue(new AttackCommand(dummy), IssueMode.Append), Is.True);
            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.Reserved), "Planning while paused reserves the point");
            var start = unit.transform.position;
            yield return new WaitForSecondsRealtime(1f);
            Assert.That(Vector3.Distance(unit.transform.position, start), Is.LessThan(0.01f), "Nothing moves while paused");
            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.Reserved));

            pause.Resume();
            yield return WaitOccupied(unit);
            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.Occupied));
            yield return TestWorld.WaitUntil(() => dummy.Current < dummy.Max, 4f);

            Assert.That(dummy.Current, Is.LessThan(dummy.Max), "The appended attack ran");
            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.Occupied), "A ranged unit fires from cover without leaving it");
            Assert.That(unit.AttackPhase, Is.EqualTo(AttackPhase.Attack));
        }

        [UnityTest]
        public IEnumerator DestroyingThePointMidWalk_EndsTheOrderSilently_AndTheNextOrderRuns()
        {
            var unit = Fighter(new Vector3(0f, 0f, -10f));
            Assert.That(unit.Issue(new MoveToCoverCommand(point)), Is.True);
            var next = new MoveCommand(new Vector3(4f, 0f, -8f));
            Assert.That(unit.Issue(next, IssueMode.Append), Is.True);
            yield return null;

            Object.Destroy(point.gameObject);
            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == next, 2f);

            Assert.That(unit.CurrentCommand, Is.SameAs(next), "The vanished point ends the cover order and the queue moves on");
            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.None));
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator BlockedPoint_OrderGivesUpAfterThreeSeconds_AndTheNextOrderRuns()
        {
            // The blocker stands on the point itself. It would auto-occupy it (within occupyRadius) once it has stood
            // still for a frame, but the walker's order reserves the point synchronously in Issue, before either
            // unit's first Update. Agent avoidance keeps the walker about 1 m (two agent radii) from the blocker,
            // so HasArrived (0.2 m from the point) never turns true and the walk stalls. Once the walker gives up,
            // the blocker takes the point by standing there, which is why the final claim check is "not the walker".
            var walker = Fighter(new Vector3(0f, 0f, -8f));
            var blocker = Fighter(new Vector3(0f, 0f, -1f));
            // Highest avoidance priority: the blocker ignores the walker, so avoidance never nudges it off the spot.
            blocker.GetComponent<UnityEngine.AI.NavMeshAgent>().avoidancePriority = 0;
            Assert.That(walker.Issue(new MoveToCoverCommand(point)), Is.True);
            var next = new MoveCommand(new Vector3(4f, 0f, -8f));
            Assert.That(walker.Issue(next, IssueMode.Append), Is.True);
            Assert.That(point.Claimant, Is.SameAs(CoverOf(walker)), "Precondition: the walker holds the reservation");

            var closest = float.PositiveInfinity;
            var deadline = Time.realtimeSinceStartup + 8f;
            while (walker.CurrentCommand != next && Time.realtimeSinceStartup < deadline)
            {
                closest = Mathf.Min(closest, CoverRules.FlatDistance(walker.transform.position, point.Position));
                yield return null;
            }

            Assert.That(closest, Is.GreaterThan(0.2f), "Precondition: avoidance held the walker off the point (it never arrived)");
            Assert.That(walker.CurrentCommand, Is.SameAs(next), "After 3 s without progress the cover order gives up");
            Assert.That(CoverOf(walker).Status, Is.EqualTo(CoverStatus.None), "No stale reservation");
            Assert.That(point.Claimant, Is.Not.SameAs(CoverOf(walker)));
            Assert.That(blocker.CurrentCommand, Is.Null);
            LogAssert.NoUnexpectedReceived();
        }
    }
}
```

- [ ] **Step 6: Run the PlayMode tests**

Run: `Tools/run-tests.sh PlayMode Blackglass.Tests.CoverOrderPlayModeTests` → expected `total="14" passed="14"`, `EXIT=0`.

Troubleshooting:
- `BlockedPoint_...` fails its precondition (the walker got within 0.2 m): avoidance let two 0.5 m agents overlap; check the blocker spawned exactly on the point with `avoidancePriority` 0. It times out instead: `CoverWalkTimeout` must count scaled time, and the stall must not be reset by avoidance jitter (the step is 0.05 m; a jittering agent does not gain 5 cm repeatedly).
- `DestroyingThePointMidWalk_...` fails on `NoUnexpectedReceived`: `UpdateCover` logged the arrival warning, meaning it took the `HasArrived` branch; the vanished-point check must come first.
- `CoverOrderWhilePaused_...` never damages the dummy: the dummy must be north of the wall at (0, 0, 5) so the eye line from the point (1.5 m) clears the 0.9 m wall; check the fighter is ranged with range 8.

Run: `Tools/run-tests.sh PlayMode` → expected `total="237" passed="237"`, `EXIT=0` (the scene tests pass because Step 4b put `UnitCover` on the prefab instances).
Run: `Tools/run-tests.sh EditMode` → expected `total="267" passed="267"`, `EXIT=0`.

- [ ] **Step 7: Commit**

```bash
git add Assets/_Project/Scripts/Commands/UnitCommands.cs Assets/_Project/Scripts/Units/CommandableUnit.cs Assets/_Project/Tests/EditMode/CommandableUnitTests.cs Assets/_Project/Tests/PlayMode/CoverOrderPlayModeTests.cs Assets/_Project/Tests/PlayMode/CoverOrderPlayModeTests.cs.meta Assets/_Project/Prefabs/FriendlyUnit.prefab Assets/_Project/Prefabs/HostileUnit.prefab
git status --short   # must NOT list Assets/_Project/Editor
git commit -m "Add the MoveToCover order: reserve on start, occupy on arrival, give up when blocked

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 4: Hit resolution in `UnitAttacker`; `Missed`; counters; the attack line on a miss

**Files:**
- Modify: `Assets/_Project/Scripts/Units/UnitAttacker.cs`
- Modify: `Assets/_Project/Scripts/Units/CommandableUnit.cs` (one comment)
- Modify: `Assets/_Project/Scripts/DebugUI/AttackLineView.cs`
- Modify: `Assets/_Project/Tests/EditMode/UnitAttackerTests.cs`
- Create: `Assets/_Project/Tests/PlayMode/CoverCombatPlayModeTests.cs`

**Interfaces:**
- Consumes: `UnitCover.IsProtectedFrom(Vector3)`, `UnitCover.HitChance` (Task 2); `CoverRules.ResolveHit` (Task 1); `MoveToCoverCommand` (Task 3, tests only).
- Produces on `UnitAttacker`: `public event Action<Health> Missed;`, `public int ShotsFired { get; }`, `public int Hits { get; }`, `public bool IsTargetInCover(Health target, out float hitChance)`, `public bool IsTargetInCover(Health target)`, `internal Func<float> HitRoll { get; set; }` (default `UnityEngine.Random.value`). `TryAttack` now returns true when a shot was **fired** (hit or miss).

- [ ] **Step 1: Write the failing EditMode tests**

Append to `Assets/_Project/Tests/EditMode/UnitAttackerTests.cs`, inside the class before its closing brace:

```csharp
        [Test]
        public void IsTargetInCover_IsFalseForMelee_AndForATargetWithoutUnitCover()
        {
            targetHost.transform.position = new Vector3(0f, 1f, 1f);
            Assert.That(attacker.IsTargetInCover(target, out var chance), Is.False, "Melee never asks about cover");
            Assert.That(chance, Is.EqualTo(1f));
            attacker.Initialize(8f, 15, 1f, CombatRole.Ranged);
            Assert.That(attacker.IsTargetInCover(target), Is.False, "A target without a UnitCover is exposed");
            Assert.That(attacker.IsTargetInCover(null), Is.False);
        }

        [Test]
        public void IsTargetInCover_IsFalseWhileTheTargetOnlyReservesAPoint()
        {
            attacker.Initialize(8f, 15, 1f, CombatRole.Ranged);
            targetHost.transform.position = new Vector3(0f, 1f, 4f);
            var pointHost = new GameObject("Cover");
            pointHost.transform.position = new Vector3(0f, 0f, 4f);
            var point = pointHost.AddComponent<CoverPoint>();
            point.Initialize(null, 0.25f);
            var cover = targetHost.AddComponent<UnitCover>();
            Assert.That(cover.TryReserve(point), Is.True);

            Assert.That(attacker.IsTargetInCover(target, out var chance), Is.False, "Reserved is not occupied");
            Assert.That(chance, Is.EqualTo(1f));
            Object.DestroyImmediate(pointHost);
        }

        [Test]
        public void TryAttack_CountsShotsAndHits_AndAnExposedTargetAlwaysTakesDamage()
        {
            targetHost.transform.position = new Vector3(0f, 1f, 1f);
            attacker.HitRoll = () => 0.999f;   // would miss a covered target; an exposed one is hit regardless
            Assert.That(attacker.ShotsFired, Is.EqualTo(0));
            Assert.That(attacker.TryAttack(target), Is.True);
            Assert.That(attacker.ShotsFired, Is.EqualTo(1));
            Assert.That(attacker.Hits, Is.EqualTo(1));
            Assert.That(target.Current, Is.EqualTo(target.Max - attacker.Damage));
            Assert.That(attacker.TryAttack(target), Is.False, "Cooling down: no shot");
            Assert.That(attacker.ShotsFired, Is.EqualTo(1));
        }
```

- [ ] **Step 2: Run the EditMode tests to verify they fail**

Run: `Tools/run-tests.sh EditMode Blackglass.Tests.UnitAttackerTests` → expected `EXIT=1` with `error CS1061 ... 'UnitAttacker' does not contain a definition for 'IsTargetInCover'` and `... 'HitRoll'`.

- [ ] **Step 3: Resolve cover in `TryAttack`**

In `Assets/_Project/Scripts/Units/UnitAttacker.cs`:

1. Replace the class summary with:

```csharp
    /// <summary>
    /// Prototype attack: a combat role (melee or ranged), fixed range, damage and cooldown. A ranged hit also needs
    /// line of sight, and a ranged shot at a target occupying cover that protects it from here lands only with that
    /// cover's hit chance; melee ignores cover. Cooldown uses scaled time, so it freezes while paused. Reports each
    /// hit through Attacked and each miss through Missed, and tells the target which Health hit it. Owns the unit's
    /// sight test (LineOfSight from this unit's eye), which EnemyAI uses for acquisition.
    /// </summary>
```

2. Fields and members. After `float nextAttackTime;` add:

```csharp
        // The roll is replaceable so tests can force a hit or a miss; the default is Unity's random stream.
        static readonly Func<float> defaultRoll = () => UnityEngine.Random.value;
        Func<float> hitRoll;
```

After `public event Action<Health> Attacked;` add:

```csharp
        /// <summary>Raised with the target after every shot that cover turned away.</summary>
        public event Action<Health> Missed;

        /// <summary>Shots fired since the component was enabled (debug counter).</summary>
        public int ShotsFired { get; private set; }

        /// <summary>Shots that landed (debug counter).</summary>
        public int Hits { get; private set; }

        /// <summary>The 0..1 roll compared with a covered target's hit chance. A plain delegate, so ?? is fine.</summary>
        internal Func<float> HitRoll
        {
            get => hitRoll ?? defaultRoll;
            set => hitRoll = value;
        }
```

After `CanAttackFrom` add:

```csharp
        /// <summary>
        /// True when the target is in cover against a shot from this unit's position: ranged role, a target with a
        /// UnitCover that occupies a point, and that point protecting it from here. Melee: always false. hitChance
        /// is that point's chance when in cover, else 1. The HUD uses the same overload.
        /// </summary>
        public bool IsTargetInCover(Health target, out float hitChance)
        {
            hitChance = 1f;
            if (!NeedsLineOfSight || target == null || !target.TryGetComponent<UnitCover>(out var cover)
                || !cover.IsProtectedFrom(transform.position))
                return false;
            hitChance = cover.HitChance;
            return true;
        }

        public bool IsTargetInCover(Health target) => IsTargetInCover(target, out _);
```

3. Replace `TryAttack`:

```csharp
        /// <summary>
        /// Fires at the target if it is alive, attackable from here (range, and sight for ranged units) and the
        /// cooldown has elapsed. A target in cover against this unit is hit with its cover's chance; otherwise every
        /// shot lands. Returns whether a shot was fired (hit or miss); Attacked and Missed say which.
        /// </summary>
        public bool TryAttack(Health target)
        {
            if (target == null || !target.IsAlive || !CanAttack(target) || Time.time < nextAttackTime)
                return false;
            nextAttackTime = Time.time + cooldown;
            ShotsFired++;
            var inCover = IsTargetInCover(target, out var hitChance);
            if (CoverRules.ResolveHit(inCover, hitChance, HitRoll()))
            {
                Hits++;
                target.TakeDamage(damage, OwnHealth);
                Attacked?.Invoke(target);
            }
            else
            {
                Missed?.Invoke(target);
            }
            return true;
        }
```

4. In `Assets/_Project/Scripts/Units/CommandableUnit.cs`, inside `UpdateAttack`, give the `TryAttack` call its new meaning:

```csharp
            // A fired shot (hit or miss) proves the firing position; whether it lands is cover, not positioning.
            if (Attacker.TryAttack(target))
                repositionsWithoutShot = 0;
```

5. In `Assets/_Project/Scripts/DebugUI/AttackLineView.cs`, subscribe to misses too (the line shows every shot; the HUD counters tell hits from misses). In `OnEnable`, after `attacker.Attacked += OnAttacked;` add `attacker.Missed += OnAttacked;`; in `OnDisable`, after `attacker.Attacked -= OnAttacked;` add `attacker.Missed -= OnAttacked;`. Change the summary's first sentence to `Debug feedback: shows a line from this unit to its target for a moment after each shot, hit or miss.`

- [ ] **Step 4: Run the EditMode tests to verify they pass**

Run: `Tools/run-tests.sh EditMode Blackglass.Tests.UnitAttackerTests` → expected `total="7" passed="7"`, `EXIT=0`.

- [ ] **Step 5: Write the failing PlayMode tests**

Create `Assets/_Project/Tests/PlayMode/CoverCombatPlayModeTests.cs`:

```csharp
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CoverCombatPlayModeTests
    {
        TestWorld world;
        GameObject environment;
        CoverPoint point;
        CoverRegistry registry;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            // A 0.9 m wall from z -0.25 to 0.25 (x -2..2); the point 0.75 m south of it. A shooter north of the wall
            // sees over it (the 1.5 m eye line to a 1 m pivot stays above 1 m) while its eye-to-feet ray crosses it.
            environment = world.CreateEnvironment((new Vector3(0f, 0.45f, 0f), new Vector3(4f, 0.9f, 0.5f)));
            point = world.CreateCoverPoint(new Vector3(0f, 0f, -1f), Vector3.forward, TestWorld.ObstacleCollider(environment));
            registry = world.CreateRegistry(point);
        }

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        static Health HealthOf(Component unit) => unit.GetComponent<Health>();
        static UnitCover CoverOf(Component unit) => unit.GetComponent<UnitCover>();
        static UnitAttacker AttackerOf(Component unit) => unit.GetComponent<UnitAttacker>();

        // Ranged units retaliate from where they stand (range 8, sight over the wall), so a covered defender stays put.
        CommandableUnit Ranged(Vector3 at) => world.CreateFighter(at, damage: 10, cooldown: 0.3f, role: CombatRole.Ranged, range: 8f, registry: registry);

        IEnumerator PutInCover(CommandableUnit unit)
        {
            Assert.That(unit.Issue(new MoveToCoverCommand(point)), Is.True);
            yield return TestWorld.WaitUntil(() => CoverOf(unit).Status == CoverStatus.Occupied, 10f);
            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.Occupied), "Precondition: the defender is in cover");
            yield return new WaitForFixedUpdate();
        }

        [UnityTest]
        public IEnumerator AlwaysMissRoll_CoveredTarget_TakesNoDamage_AndAlwaysHitRoll_Does()
        {
            var defender = Ranged(new Vector3(0f, 0f, -3f));
            yield return PutInCover(defender);
            var shooter = Ranged(new Vector3(0f, 0f, 5f));
            AttackerOf(shooter).HitRoll = () => 0.99f;
            yield return new WaitForFixedUpdate();
            Assert.That(AttackerOf(shooter).IsTargetInCover(HealthOf(defender), out var chance), Is.True, "Precondition: the wall is between them");
            Assert.That(chance, Is.EqualTo(0.5f));

            Assert.That(shooter.Issue(new AttackCommand(HealthOf(defender))), Is.True);
            yield return TestWorld.WaitUntil(() => AttackerOf(shooter).ShotsFired >= 3, 4f);
            Assert.That(AttackerOf(shooter).ShotsFired, Is.GreaterThanOrEqualTo(3), "Precondition: shots were fired");
            Assert.That(AttackerOf(shooter).Hits, Is.EqualTo(0));
            Assert.That(HealthOf(defender).Current, Is.EqualTo(HealthOf(defender).Max), "Every shot at a covered target missed");

            AttackerOf(shooter).HitRoll = () => 0f;
            yield return TestWorld.WaitUntil(() => HealthOf(defender).Current < HealthOf(defender).Max, 2f);
            Assert.That(HealthOf(defender).Current, Is.EqualTo(HealthOf(defender).Max - 10), "A winning roll lands as usual");
            Assert.That(AttackerOf(shooter).Hits, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ShotFromTheFlank_IgnoresCover()
        {
            var defender = Ranged(new Vector3(0f, 0f, -3f));
            yield return PutInCover(defender);
            // East of the defender, past the wall's end (x 2): the eye-to-feet ray never crosses the wall.
            var flanker = Ranged(new Vector3(6f, 0f, -1f));
            AttackerOf(flanker).HitRoll = () => 0.99f;
            yield return new WaitForFixedUpdate();
            Assert.That(AttackerOf(flanker).IsTargetInCover(HealthOf(defender)), Is.False, "No obstacle between them");

            Assert.That(flanker.Issue(new AttackCommand(HealthOf(defender))), Is.True);
            yield return TestWorld.WaitUntil(() => HealthOf(defender).Current < HealthOf(defender).Max, 2f);

            Assert.That(HealthOf(defender).Current, Is.LessThan(HealthOf(defender).Max), "Cover does not protect from the wrong direction");
        }

        [UnityTest]
        public IEnumerator MeleeAttacker_IgnoresCover()
        {
            var defender = Ranged(new Vector3(0f, 0f, -3f));
            yield return PutInCover(defender);
            var brawler = world.CreateFighter(new Vector3(0f, 0f, -2.5f), cooldown: 0.3f, registry: registry);   // 1.5 m south, in melee range
            AttackerOf(brawler).HitRoll = () => 0.99f;
            yield return null;

            Assert.That(brawler.Issue(new AttackCommand(HealthOf(defender))), Is.True);
            yield return TestWorld.WaitUntil(() => HealthOf(defender).Current < HealthOf(defender).Max, 2f);

            Assert.That(HealthOf(defender).Current, Is.LessThan(HealthOf(defender).Max), "Melee is unaffected by cover");
        }

        [UnityTest]
        public IEnumerator ATallWall_StillBlocksTheShotEntirely()
        {
            // Swap the waist-high wall for a 2 m one, 20 m wide so no firing position within 4 m sees past it.
            Object.Destroy(environment);
            Object.Destroy(point.gameObject);
            yield return null;
            environment = world.CreateEnvironment((new Vector3(0f, 1f, 0f), new Vector3(20f, 2f, 0.5f)));
            point = world.CreateCoverPoint(new Vector3(0f, 0f, -1f), Vector3.forward, TestWorld.ObstacleCollider(environment));
            registry.Initialize(point);
            var defender = Ranged(new Vector3(0f, 0f, -3f));
            yield return PutInCover(defender);
            var shooter = Ranged(new Vector3(0f, 0f, 5f));
            AttackerOf(shooter).HitRoll = () => 0f;
            yield return new WaitForFixedUpdate();

            Assert.That(shooter.Issue(new AttackCommand(HealthOf(defender))), Is.True);
            yield return new WaitForSeconds(1.5f);

            Assert.That(AttackerOf(shooter).ShotsFired, Is.EqualTo(0), "No sight, no shot: cover never comes up");
            Assert.That(HealthOf(defender).Current, Is.EqualTo(HealthOf(defender).Max));
            Assert.That(shooter.AttackPhase, Is.Not.EqualTo(AttackPhase.Attack));
        }

        [UnityTest]
        public IEnumerator Miss_RaisesMissed_CountsTheShot_AndShowsTheAttackLine()
        {
            var defender = Ranged(new Vector3(0f, 0f, -3f));
            yield return PutInCover(defender);
            var shooter = Ranged(new Vector3(0f, 0f, 5f));
            AttackerOf(shooter).HitRoll = () => 0.99f;
            var lineObject = new GameObject("AttackLine");
            lineObject.transform.SetParent(shooter.transform, false);
            var line = lineObject.AddComponent<LineRenderer>();
            shooter.gameObject.SetActive(false);
            var view = shooter.gameObject.AddComponent<AttackLineView>();
            view.Initialize(line);
            shooter.gameObject.SetActive(true);
            var misses = 0;
            AttackerOf(shooter).Missed += _ => misses++;
            yield return new WaitForFixedUpdate();

            Assert.That(shooter.Issue(new AttackCommand(HealthOf(defender))), Is.True);
            yield return TestWorld.WaitUntil(() => misses >= 1, 3f);

            Assert.That(misses, Is.GreaterThanOrEqualTo(1), "Missed must be raised");
            Assert.That(AttackerOf(shooter).ShotsFired, Is.EqualTo(misses));
            Assert.That(AttackerOf(shooter).Hits, Is.EqualTo(0));
            Assert.That(view.IsShowing, Is.True, "The attack line shows on a miss too");
        }
    }
}
```

- [ ] **Step 6: Run the PlayMode tests**

Run: `Tools/run-tests.sh PlayMode Blackglass.Tests.CoverCombatPlayModeTests` → expected `total="5" passed="5"`, `EXIT=0`.

Troubleshooting:
- `AlwaysMissRoll_...` precondition "the wall is between them" fails: `IsTargetInCover` needs the defender Occupied (check `PutInCover` waited) and the shooter ranged; the obstacle collider must be the environment's wall (index 0).
- `ATallWall_...` fires a shot: the 20 m wall must span x −10..10 so neither 2 m nor 4 m ring candidate sees past it within 1.5 s.

Run: `Tools/run-tests.sh PlayMode` → expected `total="242" passed="242"`, `EXIT=0` (`RangedCombatPlayModeTests` and `EnemyAIPlayModeTests` still pass: their targets are exposed, so every shot lands as before).
Run: `Tools/run-tests.sh EditMode` → expected `total="270" passed="270"`, `EXIT=0`.

- [ ] **Step 7: Commit**

```bash
git add Assets/_Project/Scripts/Units/UnitAttacker.cs Assets/_Project/Scripts/Units/CommandableUnit.cs Assets/_Project/Scripts/DebugUI/AttackLineView.cs Assets/_Project/Tests/EditMode/UnitAttackerTests.cs Assets/_Project/Tests/PlayMode/CoverCombatPlayModeTests.cs Assets/_Project/Tests/PlayMode/CoverCombatPlayModeTests.cs.meta
git commit -m "Resolve ranged shots against cover with a per-point hit chance

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 5: Cover clicks: `CommandResolver` and `PlayerCommandInput`

**Files:**
- Modify: `Assets/_Project/Scripts/Controls/CommandResolver.cs`
- Modify: `Assets/_Project/Scripts/Controls/PlayerCommandInput.cs`
- Modify: `Assets/_Project/Tests/EditMode/CommandResolverTests.cs`
- Modify: `Assets/_Project/Tests/PlayMode/PlayerCommandInputTests.cs`

**Interfaces:**
- Consumes: `CoverRegistry.Points`, `CoverRules.TryChooseNearest` (Task 1), `MoveToCoverCommand` (Task 3).
- Produces: `CommandResolver.Resolve(Health clicked, Vector3 point, CoverPoint cover = null)`; `PlayerCommandInput` serialized `coverRegistry` and `coverClickRadius = 1f`, `internal bool IsCoverWired`, and `Initialize(..., ActiveCharacter active = null, CoverRegistry registry = null)`.

- [ ] **Step 1: Write the failing EditMode tests**

Append to `Assets/_Project/Tests/EditMode/CommandResolverTests.cs`, inside the class before its closing brace:

```csharp
        [Test]
        public void ClickNearACoverPoint_ResolvesToMoveToCover()
        {
            var pointHost = new GameObject("Cover");
            var point = pointHost.AddComponent<CoverPoint>();
            var command = CommandResolver.Resolve(null, new Vector3(1f, 0f, 2f), point);
            Assert.That(command, Is.TypeOf<MoveToCoverCommand>());
            Assert.That(((MoveToCoverCommand)command).Point, Is.SameAs(point));
            Object.DestroyImmediate(pointHost);
        }

        [Test]
        public void ClickOnLivingTarget_BeatsCover_AndADeadTargetDoesNot()
        {
            var pointHost = new GameObject("Cover");
            var point = pointHost.AddComponent<CoverPoint>();
            Assert.That(CommandResolver.Resolve(health, Vector3.zero, point), Is.TypeOf<AttackCommand>());
            health.TakeDamage(health.Max);
            Assert.That(CommandResolver.Resolve(health, Vector3.zero, point), Is.TypeOf<MoveToCoverCommand>());
            Object.DestroyImmediate(pointHost);
        }
```

- [ ] **Step 2: Run the EditMode tests to verify they fail**

Run: `Tools/run-tests.sh EditMode Blackglass.Tests.CommandResolverTests` → expected `EXIT=1` with `error CS1501: No overload for method 'Resolve' takes 3 arguments`.

- [ ] **Step 3: Resolve cover clicks**

Replace `Assets/_Project/Scripts/Controls/CommandResolver.cs`:

```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Chooses the command for a context click: attack a living target, otherwise move into the cover point near the
    /// click, otherwise move to the clicked point.
    /// </summary>
    public static class CommandResolver
    {
        public static UnitCommand Resolve(Health clicked, Vector3 point, CoverPoint cover = null)
        {
            if (clicked != null && clicked.IsAlive)
                return new AttackCommand(clicked);
            if (cover != null)
                return new MoveToCoverCommand(cover);
            return new MoveCommand(point);
        }
    }
}
```

In `Assets/_Project/Scripts/Controls/PlayerCommandInput.cs`:

1. Add `using System;` at the top.
2. Summary: change `a click anywhere else gives an order (attack the clicked target, or move to the clicked point)` to `a click anywhere else gives an order (attack the clicked target, move into the cover point within coverClickRadius of the click, or move to the clicked point)`.
3. After `[SerializeField, FormerlySerializedAs("primary")] ActiveCharacter activeCharacter;` add `[SerializeField] CoverRegistry coverRegistry;`.
4. Under `[Header("Tuning")]`, after `groupSpacing`, add:

```csharp
        // A ground click this close to a cover point orders the unit into that point instead of onto the ground.
        [SerializeField, Min(0f)] float coverClickRadius = 1f;
```

5. After the `boxedUnits` field add `static readonly Func<CoverPoint, bool> acceptAny = _ => true;` and after `DragRect` add `internal bool IsCoverWired => coverRegistry != null;`.
6. `Initialize` gains a final parameter `CoverRegistry registry = null` and the line `coverRegistry = registry;`.
7. In `HandleClick`, replace the `var command = ...` line with:

```csharp
            CoverPoint cover = null;
            if (coverRegistry != null)
                CoverRules.TryChooseNearest(coverRegistry.Points, hit.point, coverClickRadius, acceptAny, out cover);
            var command = CommandResolver.Resolve(hit.collider.GetComponentInParent<Health>(), hit.point, cover);
```

- [ ] **Step 4: Run the EditMode tests to verify they pass**

Run: `Tools/run-tests.sh EditMode Blackglass.Tests.CommandResolverTests` → expected `total="6" passed="6"`, `EXIT=0`.

- [ ] **Step 5: Write the failing PlayMode test**

In `Assets/_Project/Tests/PlayMode/PlayerCommandInputTests.cs`:

1. Add the fields `static readonly Vector3 CoverGroundPoint = new Vector3(-6f, 0f, 2f);`, `CoverPoint coverPoint;` and `CoverRegistry registry;`.
2. In `Setup`, after `dummy = world.CreateDummy(...)`:

```csharp
            // A loose waist-high wall north of the cover point (the environment is already baked; the box is only a
            // collider for the point). Clicks at CoverGroundPoint reach the ground: the wall is farther from the camera.
            var wall = world.CreateObstacle(CoverGroundPoint + new Vector3(0f, 0.45f, 1f), new Vector3(2f, 0.9f, 0.5f));
            coverPoint = world.CreateCoverPoint(CoverGroundPoint, Vector3.forward, wall.GetComponent<Collider>());
            registry = world.CreateRegistry(coverPoint);
```

3. Pass `registry` as the last argument of `input.Initialize(...)` (after `activeCharacter`).
4. Add the test:

```csharp
        [UnityTest]
        public IEnumerator ClickNearACoverPoint_OrdersTheSelectionIntoCover()
        {
            yield return LeftClickAt(ScreenPointOf(unitA));
            Assert.That(selection.Selected, Is.EqualTo(new[] { unitA }), "Precondition: unit A is selected");
            Assert.That(input.IsCoverWired, Is.True);

            yield return LeftClickAt(ScreenPointOf(CoverGroundPoint + new Vector3(0.4f, 0f, 0f)));

            Assert.That(unitA.Unit.CurrentCommand, Is.TypeOf<MoveToCoverCommand>(), "A click within 1 m of a cover point is a cover order");
            Assert.That(((MoveToCoverCommand)unitA.Unit.CurrentCommand).Point, Is.SameAs(coverPoint));
            Assert.That(unitA.Unit.Cover.Status, Is.EqualTo(CoverStatus.Reserved));
            Assert.That(unitB.Unit.CurrentCommand, Is.Null);
        }
```

- [ ] **Step 6: Run the PlayMode tests**

Run: `Tools/run-tests.sh PlayMode Blackglass.Tests.PlayerCommandInputTests` → expected `total="31" passed="31"`, `EXIT=0`. The existing click tests keep passing: every other click point in that fixture is at least 4 m from (−6, 0, 2).

Run: `Tools/run-tests.sh PlayMode` → expected `total="243" passed="243"`, `EXIT=0`.
Run: `Tools/run-tests.sh EditMode` → expected `total="272" passed="272"`, `EXIT=0`.

- [ ] **Step 7: Commit**

```bash
git add Assets/_Project/Scripts/Controls/CommandResolver.cs Assets/_Project/Scripts/Controls/PlayerCommandInput.cs Assets/_Project/Tests/EditMode/CommandResolverTests.cs Assets/_Project/Tests/PlayMode/PlayerCommandInputTests.cs
git commit -m "Turn a ground click near a cover point into a MoveToCover order

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 6: Ranged hostiles take nearby cover; `EnemyState.Cover`

**Files:**
- Modify: `Assets/_Project/Scripts/AI/EnemyAI.cs`
- Modify: `Assets/_Project/Tests/EditMode/EnemyAITests.cs`
- Modify: `Assets/_Project/Tests/PlayMode/EnemyAIPlayModeTests.cs`
- Modify: `Assets/_Project/Tests/PlayMode/TestSupport/TestWorld.cs` (`CreateHostile` takes a registry)

**Interfaces:**
- Consumes: `CommandableUnit.AttackTarget`, `CommandableUnit.Cover` (Task 3); `CoverPoint.ProtectsFrom`, `IsClaimed`, `IsClaimedBy` (Tasks 1–2); `CoverRules.TryChooseNearest`; `UnitAttacker.CanAttackFrom`, `UnitMover.CanReach`, `UnitMover.PivotHeight` (existing).
- Produces: `EnemyState.Cover`; `EnemyAI.DeriveState` returns it for a current `MoveToCoverCommand`; `EnemyAI.Target` reads `Unit.AttackTarget`; serialized `coverRegistry` and `coverSearchRange = 8f`; `public float CoverSearchRange`; `internal bool IsCoverWired`; `internal void Initialize(Encounter encounterToFight, float range = 12f, float interval = 0.25f, CoverRegistry registry = null)`.
- Produces (tests): `TestWorld.CreateHostile(..., CoverRegistry registry = null)`.

- [ ] **Step 1: Write the failing EditMode test**

Append to `Assets/_Project/Tests/EditMode/EnemyAITests.cs`, inside the class before its closing brace:

```csharp
        [Test]
        public void DeriveState_Cover_WhileACoverOrderIsCurrent()
        {
            var host = new GameObject("Cover");
            var toCover = new MoveToCoverCommand(host.AddComponent<CoverPoint>());
            Assert.That(EnemyAI.DeriveState(true, toCover, AttackPhase.None), Is.EqualTo(EnemyState.Cover));
            Assert.That(EnemyAI.DeriveState(false, toCover, AttackPhase.None), Is.EqualTo(EnemyState.Dead));
            Object.DestroyImmediate(host);
        }
```

In `RequiredComponentsAreAdded_AndDefaultsMatchThePrototype`, after the `DetectionRange` assertion add `Assert.That(ai.CoverSearchRange, Is.EqualTo(8f));` and `Assert.That(ai.IsCoverWired, Is.False);`.

- [ ] **Step 2: Run the EditMode tests to verify they fail**

Run: `Tools/run-tests.sh EditMode Blackglass.Tests.EnemyAITests` → expected `EXIT=1` with `error CS0117: 'EnemyState' does not contain a definition for 'Cover'` and `error CS1061 ... 'CoverSearchRange'`.

- [ ] **Step 3: Rewrite `EnemyAI`**

Replace `Assets/_Project/Scripts/AI/EnemyAI.cs` with:

```csharp
using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>What a hostile is doing, as EnemyAI.DeriveState reads it from its unit.</summary>
    public enum EnemyState
    {
        Idle,
        Chase,
        Reposition,
        Attack,
        /// <summary>Walking to a cover point before attacking.</summary>
        Cover,
        Dead,
    }

    /// <summary>
    /// The smallest hostile brain: while idle, every think tick it looks for the nearest living friendly inside its
    /// detection radius that it can see and reach, and attacks it through the normal order path. A ranged hostile
    /// first looks for useful cover nearby (unclaimed or its own, protecting from the target, letting it shoot the
    /// target, reachable) and, when there is some, walks there before attacking; a melee hostile never looks.
    /// CommandableUnit then chases, stops and hits; when the target dies or cannot be reached that order ends on its
    /// own and the brain looks again. Line of sight (UnitAttacker.HasLineOfSight) gates acquisition only: a target
    /// once taken is followed around corners. Cover is chosen at acquisition only. Runs on simulation time, so it
    /// freezes while paused.
    /// </summary>
    [RequireComponent(typeof(CommandableUnit), typeof(Health), typeof(UnitAttacker))]
    public sealed class EnemyAI : MonoBehaviour
    {
        [SerializeField] Encounter encounter;
        [SerializeField, Min(0f)] float detectionRange = 12f;
        [SerializeField, Min(0f)] float thinkInterval = 0.25f;
        [Header("Cover")]
        // Optional: without a registry the hostile never seeks cover.
        [SerializeField] CoverRegistry coverRegistry;
        [SerializeField, Min(0f)] float coverSearchRange = 8f;

        CommandableUnit unit;
        UnitAttacker attacker;
        UnitMover mover;
        float nextThinkTime;
        // The target of the search in progress, read by the predicate (a cached delegate, so ticks allocate nothing).
        Health coverTarget;
        Func<CoverPoint, bool> isUsefulCover;

        public float DetectionRange => detectionRange;
        public float CoverSearchRange => coverSearchRange;

        /// <summary>The friendly this hostile is after (attacking, or about to once it reaches cover), or null while idle.</summary>
        public Health Target => Unit.AttackTarget;

        /// <summary>Derived each read; nothing is stored. Debug views show it.</summary>
        public EnemyState State => DeriveState(Unit.IsAlive, Unit.CurrentCommand, Unit.AttackPhase);

        internal bool IsCoverWired => coverRegistry != null;

        CommandableUnit Unit => unit != null ? unit : unit = GetComponent<CommandableUnit>();
        UnitAttacker Attacker => attacker != null ? attacker : attacker = GetComponent<UnitAttacker>();
        UnitMover Mover => mover != null ? mover : mover = GetComponent<UnitMover>();

        internal void Initialize(Encounter encounterToFight, float range = 12f, float interval = 0.25f, CoverRegistry registry = null)
        {
            encounter = encounterToFight;
            detectionRange = range;
            thinkInterval = interval;
            coverRegistry = registry;
        }

        /// <summary>
        /// Dead beats everything; a cover order is Cover; an attack order maps its phase to Chase, Reposition or
        /// Attack; otherwise Idle.
        /// </summary>
        public static EnemyState DeriveState(bool alive, UnitCommand current, AttackPhase phase)
        {
            if (!alive)
                return EnemyState.Dead;
            if (current is MoveToCoverCommand)
                return EnemyState.Cover;
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

        void OnEnable()
        {
            if (encounter == null)
                Debug.LogWarning($"{name} has no Encounter wired, so it will never acquire a target.", this);
        }

        void Update()
        {
            if (!SimulationTime.IsRunning || !Unit.IsAlive)
                return;
            if (Time.time < nextThinkTime)
                return;
            nextThinkTime = Time.time + thinkInterval;

            // Busy units are left alone: CommandableUnit runs the walk, the chase and the attack.
            if (Unit.CurrentCommand != null || encounter == null)
                return;
            var target = FindTarget();
            if (target == null)
                return;
            // The cover order can still be refused (someone claimed the point this frame); then attack without cover.
            if (Attacker.NeedsLineOfSight && TryFindCover(target, out var point) && Unit.Issue(new MoveToCoverCommand(point)))
            {
                Unit.Issue(new AttackCommand(target), IssueMode.Append);
                return;
            }
            Unit.Issue(new AttackCommand(target));
        }

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

        // The nearest registry point within coverSearchRange that IsUsefulCover admits against this target.
        bool TryFindCover(Health target, out CoverPoint point)
        {
            point = null;
            if (coverRegistry == null)
                return false;
            coverTarget = target;
            isUsefulCover ??= IsUsefulCover;
            var found = CoverRules.TryChooseNearest(coverRegistry.Points, transform.position, coverSearchRange, isUsefulCover, out point);
            coverTarget = null;
            return found;
        }

        // Unclaimed or ours; protects the point from the target (one obstacle ray); the target can be attacked from
        // the eye a unit would have there (range and sight); reachable (a path, so last).
        bool IsUsefulCover(CoverPoint point)
        {
            if (point.IsClaimed && !point.IsClaimedBy(Unit.Cover))
                return false;
            var pivot = point.Position + Vector3.up * Mover.PivotHeight;
            return point.ProtectsFrom(coverTarget.transform.position, pivot)
                && Attacker.CanAttackFrom(pivot, coverTarget)
                && Mover.CanReach(point.Position);
        }
    }
}
```

- [ ] **Step 4: Run the EditMode tests to verify they pass**

Run: `Tools/run-tests.sh EditMode Blackglass.Tests.EnemyAITests` → expected `total="6" passed="6"`, `EXIT=0`.

- [ ] **Step 5: Extend `TestWorld.CreateHostile` and write the failing PlayMode tests**

In `Assets/_Project/Tests/PlayMode/TestSupport/TestWorld.cs`, change `CreateHostile`:

```csharp
        /// <summary>A fighter with EnemyAI wired to the encounter (and a cover registry, when given). Hostile prototype stats by default.</summary>
        public EnemyAI CreateHostile(Vector3 groundPosition, Encounter encounter, int maxHealth = 60, int damage = 10,
            float cooldown = 1.2f, float detectionRange = 12f, CombatRole role = CombatRole.Melee, float range = 2f,
            CoverRegistry registry = null)
        {
            var unit = CreateFighter(groundPosition, maxHealth, damage, cooldown, role, range, registry);
            unit.name = "TestHostile";
            unit.gameObject.SetActive(false);
            var ai = unit.gameObject.AddComponent<EnemyAI>();
            ai.Initialize(encounter, detectionRange, registry: registry);
            unit.gameObject.SetActive(true);
            return ai;
        }
```

Append to `Assets/_Project/Tests/PlayMode/EnemyAIPlayModeTests.cs`, inside the class before its closing brace:

```csharp
        static UnitCover CoverOf(Component unit) => unit.GetComponent<UnitCover>();

        // A 0.9 m wall from z -0.25 to 0.25 (x -2..2), one point 0.75 m north of it facing south (into the wall), and a
        // sturdy friendly 5 m south of the wall. From the point a ranged hostile shoots the friendly over the wall
        // (6 m, in range; the 1.5 m eye line clears 0.9 m) while the friendly's eye-to-feet ray crosses the wall.
        (CoverPoint point, CoverRegistry registry, CommandableUnit friendly) CoverLayout()
        {
            var environment = world.CreateEnvironment((new Vector3(0f, 0.45f, 0f), new Vector3(4f, 0.9f, 0.5f)));
            var point = world.CreateCoverPoint(new Vector3(0f, 0f, 1f), Vector3.back, TestWorld.ObstacleCollider(environment));
            var registry = world.CreateRegistry(point);
            var friendly = world.CreateFighter(new Vector3(0f, 0f, -5f), maxHealth: 300);
            return (point, registry, friendly);
        }

        [UnityTest]
        public IEnumerator RangedHostile_TakesNearbyCover_ThenFiresFromIt()
        {
            var (point, registry, friendly) = CoverLayout();
            // 10 m from the friendly: inside the 12 m detection range, outside the 8 m attack range; the point is 4 m away.
            var hostile = world.CreateHostile(new Vector3(0f, 0f, 5f), encounter, damage: 8, cooldown: 1.5f, role: CombatRole.Ranged, range: 8f, registry: registry);
            Arm(new[] { hostile }, friendly);
            var distanceAtFirstHit = -1f;
            hostile.GetComponent<UnitAttacker>().Attacked += _ =>
            {
                if (distanceAtFirstHit < 0f)
                    distanceAtFirstHit = TestWorld.HorizontalDistance(hostile.transform.position, friendly.transform.position);
            };

            yield return WaitForState(hostile, EnemyState.Cover, 1.5f);
            Assert.That(hostile.State, Is.EqualTo(EnemyState.Cover), "A ranged hostile with useful cover nearby walks to it first");
            Assert.That(hostile.Target, Is.SameAs(HealthOf(friendly)), "The queued attack already names the target");
            Assert.That(point.Claimant, Is.SameAs(CoverOf(hostile)));
            yield return TestWorld.WaitUntil(() => CoverOf(hostile).Status == CoverStatus.Occupied, 5f);
            Assert.That(CoverOf(hostile).Status, Is.EqualTo(CoverStatus.Occupied), "It arrived in cover");
            yield return TestWorld.WaitUntil(() => distanceAtFirstHit >= 0f, 6f);

            Assert.That(distanceAtFirstHit, Is.EqualTo(6f).Within(0.75f), "It fires from the point, 6 m from the friendly");
            Assert.That(CoverOf(hostile).Status, Is.EqualTo(CoverStatus.Occupied), "Still in cover after firing");
            Assert.That(hostile.State, Is.EqualTo(EnemyState.Attack));
        }

        [UnityTest]
        public IEnumerator MeleeHostile_InTheSameLayout_ChargesWithoutClaimingAPoint()
        {
            var (point, registry, friendly) = CoverLayout();
            var hostile = world.CreateHostile(new Vector3(0f, 0f, 5f), encounter, registry: registry);   // melee
            Arm(new[] { hostile }, friendly);
            var everCover = false;
            var everClaimed = false;

            var deadline = Time.realtimeSinceStartup + 8f;
            while (HealthOf(friendly).Current == HealthOf(friendly).Max && Time.realtimeSinceStartup < deadline)
            {
                everCover |= hostile.State == EnemyState.Cover;
                everClaimed |= point.IsClaimed;
                yield return null;
            }

            Assert.That(HealthOf(friendly).Current, Is.LessThan(HealthOf(friendly).Max), "Precondition: the melee hostile reached and hit the friendly");
            Assert.That(everCover, Is.False, "A melee hostile never seeks cover");
            Assert.That(everClaimed, Is.False, "...and claims no point on its way");
        }

        [UnityTest]
        public IEnumerator RangedHostile_WithTheOnlyPointClaimed_AttacksWithoutCover()
        {
            var (point, registry, friendly) = CoverLayout();
            var squatter = world.CreateFighter(new Vector3(0f, 0f, 3f), registry: registry);   // not in the encounter
            Assert.That(squatter.Issue(new MoveToCoverCommand(point)), Is.True);
            var hostile = world.CreateHostile(new Vector3(0f, 0f, 5f), encounter, damage: 8, cooldown: 1.5f, role: CombatRole.Ranged, range: 8f, registry: registry);
            Arm(new[] { hostile }, friendly);

            yield return WaitForState(hostile, EnemyState.Chase, 1.5f);
            Assert.That(hostile.State, Is.EqualTo(EnemyState.Chase), "No usable point: a plain attack, approaching to range");
            Assert.That(hostile.Target, Is.SameAs(HealthOf(friendly)));
            Assert.That(CoverOf(hostile).Status, Is.EqualTo(CoverStatus.None));
            yield return TestWorld.WaitUntil(() => HealthOf(friendly).Current < HealthOf(friendly).Max, 8f);

            Assert.That(HealthOf(friendly).Current, Is.LessThan(HealthOf(friendly).Max), "It still fights");
            Assert.That(point.Claimant, Is.SameAs(CoverOf(squatter)), "The squatter keeps the point");
        }

        [UnityTest]
        public IEnumerator RangedHostile_StandingOnAUsefulPoint_FiresFromIt_WithoutMoving()
        {
            var (point, registry, friendly) = CoverLayout();
            var hostile = world.CreateHostile(new Vector3(0f, 0f, 1f), encounter, damage: 8, cooldown: 1.5f, role: CombatRole.Ranged, range: 8f, registry: registry);
            Arm(new[] { hostile }, friendly);
            var start = hostile.transform.position;

            yield return TestWorld.WaitUntil(() => HealthOf(friendly).Current < HealthOf(friendly).Max, 6f);

            Assert.That(HealthOf(friendly).Current, Is.LessThan(HealthOf(friendly).Max), "Precondition: it fired");
            Assert.That(TestWorld.HorizontalDistance(hostile.transform.position, start), Is.LessThan(0.3f), "Its own point is useful: no walk");
            Assert.That(CoverOf(hostile).Point, Is.SameAs(point));
            Assert.That(CoverOf(hostile).Status, Is.EqualTo(CoverStatus.Occupied));
            Assert.That(CoverOf(hostile).OccupiedByOrder, Is.True, "The cover order on its own spot completed at once");
        }

        [UnityTest]
        public IEnumerator IdleHostile_StandingBesideAFreePoint_OccupiesIt()
        {
            var (point, registry, _) = CoverLayout();
            var hostile = world.CreateHostile(new Vector3(0.3f, 0f, 1f), encounter, registry: registry);
            Arm(new[] { hostile });   // no friendlies: it stays idle

            yield return TestWorld.WaitUntil(() => CoverOf(hostile).Status == CoverStatus.Occupied, 2f);

            Assert.That(hostile.State, Is.EqualTo(EnemyState.Idle));
            Assert.That(CoverOf(hostile).Status, Is.EqualTo(CoverStatus.Occupied));
            Assert.That(CoverOf(hostile).Point, Is.SameAs(point));
            Assert.That(CoverOf(hostile).OccupiedByOrder, Is.False, "It only happens to stand there");
        }
```

- [ ] **Step 6: Run the PlayMode tests**

Run: `Tools/run-tests.sh PlayMode Blackglass.Tests.EnemyAIPlayModeTests` → expected `total="17" passed="17"`, `EXIT=0`.

Troubleshooting:
- `RangedHostile_TakesNearbyCover_...` never reaches `Cover`: check the point faces `Vector3.back` (the wall is south of it) and `IsUsefulCover`'s order (claim, `ProtectsFrom`, `CanAttackFrom`, `CanReach`); `ProtectsFrom` is called with the friendly's pivot as the attacker and the point's pivot (ground + 1 m) as the defender.
- `RangedHostile_StandingOnAUsefulPoint_...` moved: the first think tick runs before the hostile's own auto-occupancy, which is expected: it then reserves its own spot and `MoveTo` targets its own position; a walk means the point was not accepted (re-check `IsClaimedBy(Unit.Cover)`).

Run: `Tools/run-tests.sh PlayMode` → expected `total="248" passed="248"`, `EXIT=0`.
Run: `Tools/run-tests.sh EditMode` → expected `total="273" passed="273"`, `EXIT=0`.

- [ ] **Step 7: Commit**

```bash
git add Assets/_Project/Scripts/AI/EnemyAI.cs Assets/_Project/Tests/EditMode/EnemyAITests.cs Assets/_Project/Tests/PlayMode/EnemyAIPlayModeTests.cs Assets/_Project/Tests/PlayMode/TestSupport/TestWorld.cs
git commit -m "Make ranged hostiles take nearby useful cover before attacking

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 7: Companions and retaliation hold ordered cover; pause keeps a reservation

**Files:**
- Modify: `Assets/_Project/Scripts/AI/CompanionAI.cs`
- Modify: `Assets/_Project/Scripts/Units/AutoRetaliate.cs`
- Modify: `Assets/_Project/Tests/PlayMode/CompanionAIPlayModeTests.cs`
- Modify: `Assets/_Project/Tests/PlayMode/AutoRetaliatePlayModeTests.cs`
- Modify: `Assets/_Project/Tests/PlayMode/CombatPausePlayModeTests.cs`
- Modify: `Assets/_Project/Tests/PlayMode/TestSupport/TestWorld.cs` (`CreateCompanion` takes a registry)

**Interfaces:**
- Consumes: `CommandableUnit.Cover`, `CommandableUnit.AttackTarget` (Task 3); `UnitCover.OccupiedByOrder` (Task 2); `UnitAttacker.CanAttack` (existing); `MoveToCoverCommand` (Task 3).
- Produces: no new public API. `CompanionAI.Think` holds ordered cover (no follow; assist only against a target it can attack from where it stands); `IsEngaged` and the leader's target read `AttackTarget`. `AutoRetaliate` retaliates from ordered cover only against an attacker it can attack from where it stands.
- Produces (tests): `TestWorld.CreateCompanion(..., CoverRegistry registry = null)`.

- [ ] **Step 1: Extend `TestWorld.CreateCompanion` and write the failing PlayMode tests**

In `Assets/_Project/Tests/PlayMode/TestSupport/TestWorld.cs`, change `CreateCompanion`:

```csharp
        /// <summary>A friendly fighter with CompanionAI wired to the active character and the encounter (and a cover registry, when given).</summary>
        public CompanionAI CreateCompanion(Vector3 groundPosition, ActiveCharacter active, Encounter encounter, int maxHealth = 100,
            CoverRegistry registry = null)
        {
            var unit = CreateFighter(groundPosition, maxHealth, registry: registry);
            unit.name = "TestCompanion";
            unit.gameObject.SetActive(false);
            var ai = unit.gameObject.AddComponent<CompanionAI>();
            ai.Initialize(active, encounter);
            unit.gameObject.SetActive(true);
            return ai;
        }
```

Append to `Assets/_Project/Tests/PlayMode/CompanionAIPlayModeTests.cs`, inside the class before its closing brace:

```csharp
        static UnitCover CoverOf(Component unit) => unit.GetComponent<UnitCover>();

        // A 0.9 m wall from z -0.25 to 0.25 (x -2..2) with one point 0.75 m south of it, a sturdy leader and a
        // companion whose UnitCover is wired to the registry.
        (CommandableUnit leader, ActiveCharacter active, CompanionAI companion, CoverPoint point, CoverRegistry registry) CoverSquad(Vector3 leaderAt, Vector3 companionAt)
        {
            var environment = world.CreateEnvironment((new Vector3(0f, 0.45f, 0f), new Vector3(4f, 0.9f, 0.5f)));
            var point = world.CreateCoverPoint(new Vector3(0f, 0f, -1f), Vector3.forward, TestWorld.ObstacleCollider(environment));
            var registry = world.CreateRegistry(point);
            var leader = world.CreateFighter(leaderAt, maxHealth: 300);
            leader.name = "Leader";
            var active = world.CreateActiveCharacter(leader);
            var companion = world.CreateCompanion(companionAt, active, encounter, registry: registry);
            return (leader, active, companion, point, registry);
        }

        [UnityTest]
        public IEnumerator CompanionOrderedIntoCover_HoldsIt_InsteadOfFollowing()
        {
            var (leader, _, companion, point, _) = CoverSquad(new Vector3(0f, 0f, -12f), new Vector3(0f, 0f, -4f));
            Arm(new Health[0], leader, companion);
            Assert.That(UnitOf(companion).Issue(new MoveToCoverCommand(point)), Is.True);   // same frame as creation: before the first think
            yield return TestWorld.WaitUntil(() => CoverOf(companion).Status == CoverStatus.Occupied, 5f);
            Assert.That(CoverOf(companion).OccupiedByOrder, Is.True, "Precondition: ordered cover");
            yield return new WaitForSeconds(2f);

            Assert.That(UnitOf(companion).CurrentCommand, Is.Null, "No follow move was issued");
            Assert.That(companion.State, Is.EqualTo(CompanionState.Idle));
            Assert.That(CoverOf(companion).Status, Is.EqualTo(CoverStatus.Occupied));
            Assert.That(Gap(companion, leader), Is.GreaterThan(6f), "It stayed 11 m from the leader, well beyond the follow start distance");
        }

        [UnityTest]
        public IEnumerator MeleeCompanionHoldingCover_DoesNotAssist_AgainstAHostileItCannotHitFromThere()
        {
            // The leader and a hostile fight in melee 7.8 m from the companion's point: engaged and inside assist range.
            var (leader, _, companion, point, registry) = CoverSquad(new Vector3(6f, 0f, 5.5f), new Vector3(0f, 0f, -4f));
            var hostile = world.CreateHostile(new Vector3(6f, 0f, 4f), encounter, maxHealth: 300, registry: registry);
            Arm(new[] { HealthOf(hostile) }, leader, companion);
            Assert.That(UnitOf(companion).Issue(new MoveToCoverCommand(point)), Is.True);
            yield return TestWorld.WaitUntil(() => CoverOf(companion).Status == CoverStatus.Occupied, 5f);
            Assert.That(CoverOf(companion).OccupiedByOrder, Is.True, "Precondition: ordered cover");
            yield return TestWorld.WaitUntil(() => hostile.Target == HealthOf(leader), 2f);
            Assert.That(hostile.Target, Is.SameAs(HealthOf(leader)), "Precondition: the hostile is engaged with the leader");

            var everLeft = false;
            var deadline = Time.realtimeSinceStartup + 2f;
            while (Time.realtimeSinceStartup < deadline)
            {
                everLeft |= companion.State == CompanionState.Assist || UnitOf(companion).CurrentCommand != null;
                yield return null;
            }

            Assert.That(everLeft, Is.False, "A melee companion cannot hit the hostile from its cover, so it holds instead of charging");
            Assert.That(CoverOf(companion).Status, Is.EqualTo(CoverStatus.Occupied));
        }

        [UnityTest]
        public IEnumerator CompanionOrderedAwayFromCover_FollowsAgain()
        {
            var (leader, _, companion, point, _) = CoverSquad(new Vector3(0f, 0f, -12f), new Vector3(0f, 0f, -4f));
            Arm(new Health[0], leader, companion);
            Assert.That(UnitOf(companion).Issue(new MoveToCoverCommand(point)), Is.True);
            yield return TestWorld.WaitUntil(() => CoverOf(companion).Status == CoverStatus.Occupied, 5f);
            yield return new WaitForSeconds(1f);
            Assert.That(UnitOf(companion).CurrentCommand, Is.Null, "Precondition: holding cover");

            Assert.That(UnitOf(companion).Issue(new MoveCommand(new Vector3(0f, 0f, -3f))), Is.True);   // 2 m off the point
            yield return TestWorld.WaitUntil(() => Gap(companion, leader) < 6f, 12f);

            Assert.That(Gap(companion, leader), Is.LessThan(6f), "Once it has left its cover, following resumes");
            Assert.That(CoverOf(companion).Status, Is.EqualTo(CoverStatus.None));
            Assert.That(point.IsClaimed, Is.False);
        }

        [UnityTest]
        public IEnumerator CompanionThatStopsOnAPointByChance_StillFollows()
        {
            // A second point exactly where the follow move ends: 3.5 m from the leader on the companion's side.
            var (leader, _, companion, point, registry) = CoverSquad(new Vector3(0f, 0f, -12f), new Vector3(0f, 0f, 2f));
            var byChance = world.CreateCoverPoint(new Vector3(0f, 0f, -8.5f), Vector3.forward, point.Obstacle);
            registry.Initialize(point, byChance);
            Arm(new Health[0], leader, companion);

            yield return TestWorld.WaitUntil(() => CoverOf(companion).Status == CoverStatus.Occupied, 12f);
            Assert.That(CoverOf(companion).Point, Is.SameAs(byChance), "The follow move ended on the point and the companion stood still");
            Assert.That(CoverOf(companion).OccupiedByOrder, Is.False);

            Assert.That(leader.Issue(new MoveCommand(new Vector3(0f, 0f, -18f))), Is.True);
            yield return TestWorld.WaitUntil(() => leader.CurrentCommand == null, 10f);
            yield return TestWorld.WaitUntil(() => Gap(companion, leader) < 6f && CoverOf(companion).Status == CoverStatus.None, 12f);

            Assert.That(Gap(companion, leader), Is.LessThan(6f), "An incidental occupancy never holds a companion");
            Assert.That(byChance.IsClaimed, Is.False);
        }
```

Append to `Assets/_Project/Tests/PlayMode/AutoRetaliatePlayModeTests.cs`, inside the class before its closing brace:

```csharp
        // Cover away from the fixture's duel. The loose box is only there so the point has an obstacle; the hold rule
        // is about the order, not the geometry, and the shooter's roll always hits.
        (CoverPoint point, CoverRegistry registry) LooseCover(Vector3 standAt)
        {
            var box = world.CreateObstacle(standAt + new Vector3(0f, 0.45f, 1f), new Vector3(2f, 0.9f, 0.5f));
            var point = world.CreateCoverPoint(standAt, Vector3.forward, box.GetComponent<Collider>());
            return (point, world.CreateRegistry(point));
        }

        [UnityTest]
        public IEnumerator UnitHoldingOrderedCover_HitByADistantShooter_StaysPut()
        {
            yield return null;
            var (point, registry) = LooseCover(new Vector3(6f, 0f, 0f));
            var covered = world.CreateFighter(new Vector3(6f, 0f, -3f), registry: registry);
            var coveredHealth = covered.GetComponent<Health>();
            Assert.That(covered.Issue(new MoveToCoverCommand(point)), Is.True);
            yield return TestWorld.WaitUntil(() => covered.Cover.Status == CoverStatus.Occupied, 5f);
            Assert.That(covered.Cover.OccupiedByOrder, Is.True, "Precondition: ordered cover");
            var shooter = world.CreateFighter(new Vector3(6f, 0f, 6f), cooldown: 0.2f, role: CombatRole.Ranged, range: 8f, registry: registry);
            shooter.GetComponent<UnitAttacker>().HitRoll = () => 0f;
            yield return new WaitForFixedUpdate();

            shooter.Issue(new AttackCommand(coveredHealth));
            yield return TestWorld.WaitUntil(() => coveredHealth.Current < coveredHealth.Max, 2f);
            yield return null;

            Assert.That(coveredHealth.Current, Is.LessThan(coveredHealth.Max), "Precondition: it was hit");
            Assert.That(covered.CurrentCommand, Is.Null, "A melee unit holding ordered cover does not charge a shooter it cannot reach");
            Assert.That(covered.Cover.Status, Is.EqualTo(CoverStatus.Occupied));
        }

        [UnityTest]
        public IEnumerator UnitHoldingOrderedCover_HitByAnAdjacentMeleeAttacker_FightsBackInPlace()
        {
            yield return null;
            var (point, registry) = LooseCover(new Vector3(6f, 0f, 0f));
            var covered = world.CreateFighter(new Vector3(6f, 0f, -3f), registry: registry);
            var coveredHealth = covered.GetComponent<Health>();
            Assert.That(covered.Issue(new MoveToCoverCommand(point)), Is.True);
            yield return TestWorld.WaitUntil(() => covered.Cover.Status == CoverStatus.Occupied, 5f);
            Assert.That(covered.Cover.OccupiedByOrder, Is.True, "Precondition: ordered cover");
            var brawler = world.CreateFighter(new Vector3(6f, 0f, -1.5f), cooldown: 0.2f, registry: registry);   // 1.5 m away
            var brawlerHealth = brawler.GetComponent<Health>();
            yield return null;

            brawler.Issue(new AttackCommand(coveredHealth));
            yield return TestWorld.WaitUntil(() => coveredHealth.Current < coveredHealth.Max, 2f);
            yield return null;

            Assert.That(IsAttacking(covered, brawlerHealth), Is.True, "An attacker it can hit from cover is fought back");
            yield return TestWorld.WaitUntil(() => brawlerHealth.Current < brawlerHealth.Max, 2f);
            Assert.That(brawlerHealth.Current, Is.LessThan(brawlerHealth.Max));
            Assert.That(covered.Cover.Status, Is.EqualTo(CoverStatus.Occupied), "...without leaving the point");
        }

        [UnityTest]
        public IEnumerator UnitStandingOnAPointByChance_ChargesTheShooterAsBefore()
        {
            yield return null;
            var (point, registry) = LooseCover(new Vector3(6f, 0f, 0f));
            var standing = world.CreateFighter(new Vector3(6f, 0f, -3f), registry: registry);
            var standingHealth = standing.GetComponent<Health>();
            Assert.That(standing.Issue(new MoveCommand(point.Position)), Is.True);
            yield return TestWorld.WaitUntil(() => standing.Cover.Status == CoverStatus.Occupied, 6f);
            Assert.That(standing.Cover.OccupiedByOrder, Is.False, "Precondition: incidental cover");
            var shooter = world.CreateFighter(new Vector3(6f, 0f, 6f), cooldown: 0.2f, role: CombatRole.Ranged, range: 8f, registry: registry);
            var shooterHealth = shooter.GetComponent<Health>();
            shooter.GetComponent<UnitAttacker>().HitRoll = () => 0f;
            yield return new WaitForFixedUpdate();

            shooter.Issue(new AttackCommand(standingHealth));
            yield return TestWorld.WaitUntil(() => standingHealth.Current < standingHealth.Max, 2f);
            yield return null;

            Assert.That(IsAttacking(standing, shooterHealth), Is.True, "Cover a unit merely stands on does not change retaliation");
            yield return TestWorld.WaitUntil(() => standing.Cover.Status == CoverStatus.None, 3f);
            Assert.That(standing.Cover.Status, Is.EqualTo(CoverStatus.None), "It charged out of the point");
        }
```

Append to `Assets/_Project/Tests/PlayMode/CombatPausePlayModeTests.cs`, inside the class before its closing brace:

```csharp
        [UnityTest]
        public IEnumerator Paused_ReservationIsUnchanged_AndTheWalkResumesAfterwards()
        {
            yield return StartTheFight();
            var box = world.CreateObstacle(new Vector3(-6f, 0.45f, -5f), new Vector3(2f, 0.9f, 0.5f));
            var point = world.CreateCoverPoint(new Vector3(-6f, 0f, -6f), Vector3.forward, box.GetComponent<Collider>());
            Assert.That(friendly.Issue(new MoveToCoverCommand(point)), Is.True);
            Assert.That(friendly.Cover.Status, Is.EqualTo(CoverStatus.Reserved));

            pause.Pause();
            var at = friendly.transform.position;
            yield return new WaitForSecondsRealtime(1f);

            Assert.That(friendly.Cover.Status, Is.EqualTo(CoverStatus.Reserved), "Pause neither releases nor completes a reservation");
            Assert.That(point.Claimant, Is.SameAs(friendly.Cover));
            Assert.That(Vector3.Distance(friendly.transform.position, at), Is.LessThan(0.01f));

            pause.Resume();
            yield return TestWorld.WaitUntil(() => friendly.Cover.Status == CoverStatus.Occupied, 6f);
            Assert.That(friendly.Cover.Status, Is.EqualTo(CoverStatus.Occupied), "The walk resumed and arrived");
        }
```

- [ ] **Step 2: Run the PlayMode tests to verify they fail**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.CompanionAIPlayModeTests|Blackglass.Tests.AutoRetaliatePlayModeTests|Blackglass.Tests.CombatPausePlayModeTests"` → expected `EXIT=2` with four failures. Without the hold rule a companion follows as soon as its cover order completes (11 m from the leader, a 7.5 m walk), so: `CompanionOrderedIntoCover_HoldsIt_InsteadOfFollowing` fails (on "No follow move was issued" if the walk is still running at the 2 s check, or on the `Status == Occupied` / `Gap > 6f` assertions if it has arrived: either way the companion followed); `CompanionOrderedAwayFromCover_FollowsAgain` fails on "Precondition: holding cover" (a follow move is already running one second after occupancy); `MeleeCompanionHoldingCover_DoesNotAssist_...` fails (it assists); `UnitHoldingOrderedCover_HitByADistantShooter_StaysPut` fails (it retaliates and charges). `CompanionThatStopsOnAPointByChance_StillFollows`, the two other retaliation tests and the pause test pass already.

- [ ] **Step 3: Hold ordered cover in `CompanionAI` and `AutoRetaliate`**

In `Assets/_Project/Scripts/AI/CompanionAI.cs`:

1. Extend the class summary: after `Priority: dead, controlled, explicit orders, assist (attack a hostile that is fighting the squad), follow the controlled character, idle.` add `A companion holding cover it was ordered into does not follow, and assists only against a target it can attack from where it stands; cover it merely stopped on changes nothing. Both checks are made when its own order is issued (acquisition only, like the rest).`

2. Fields, after `UnitMover mover;`: add `UnitAttacker attacker;` and `UnitCover coverComponent;`. After the `Mover` property add:

```csharp
        UnitAttacker Attacker => attacker != null ? attacker : attacker = GetComponent<UnitAttacker>();
        UnitCover Cover => coverComponent != null ? coverComponent : coverComponent = Unit.Cover;
```

3. Replace `Think`:

```csharp
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
                // Ordered cover is held: a companion that cannot hit the target from where it stands does not charge.
                if (Cover.OccupiedByOrder && !Attacker.CanAttack(target))
                    return;
                IssueOwn(new AttackCommand(target));
                return;
            }
            // Ordered cover is held instead of following; a point the companion merely stopped on is not.
            if (Cover.OccupiedByOrder)
                return;

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
```

4. In `ChooseAssistTarget()`, replace the three lines from `Health leaderTarget = null;` through `leaderTarget = leaderAttack.Target;` (the declaration, the `if (... is AttackCommand leaderAttack)` line and the assignment) with:

```csharp
            Health leaderTarget = null;
            if (activeCharacter != null && activeCharacter.HasUnit)
                leaderTarget = activeCharacter.Unit.AttackTarget;   // also set while the leader walks to cover with an attack queued
```

5. Replace `IsEngaged`:

```csharp
        // A hostile that is attacking anyone, or walking to cover with its attack queued, is engaged; read from the
        // shared unit, never from EnemyAI.
        static bool IsEngaged(Health hostile) =>
            hostile.TryGetComponent<CommandableUnit>(out var hostileUnit) && hostileUnit.AttackTarget != null;
```

Replace `Assets/_Project/Scripts/Units/AutoRetaliate.cs`:

```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Fights back: a unit hit while it has no orders attacks its attacker, through the normal order path. Any order
    /// wins (a Move away is a retreat) and so do held movement keys. A unit holding cover it was ordered into fights
    /// back only at an attacker it can attack from where it stands (a melee unit behind a wall does not charge a
    /// shooter); cover it merely stopped on changes nothing. Shared by friendlies and hostiles; it decides what to
    /// attack and nothing about how.
    /// </summary>
    [RequireComponent(typeof(CommandableUnit), typeof(Health))]
    public sealed class AutoRetaliate : MonoBehaviour
    {
        CommandableUnit unit;
        Health health;
        UnitAttacker attacker;

        void Awake()
        {
            unit = GetComponent<CommandableUnit>();
            health = GetComponent<Health>();
            attacker = GetComponent<UnitAttacker>();   // CommandableUnit requires it
        }

        void OnEnable() => health.AttackedBy += OnAttackedBy;

        void OnDisable() => health.AttackedBy -= OnAttackedBy;

        void OnAttackedBy(Health attackerHealth)
        {
            if (!unit.IsAlive || unit.CurrentCommand != null || unit.MoveIntent != Vector3.zero)
                return;
            if (attackerHealth == null || !attackerHealth.IsAlive || !attackerHealth.gameObject.activeInHierarchy)
                return;
            // Decided here, once: a retaliation that starts from cover runs like any attack order afterwards.
            if (unit.Cover.OccupiedByOrder && !attacker.CanAttack(attackerHealth))
                return;
            unit.Issue(new AttackCommand(attackerHealth));
        }
    }
}
```

- [ ] **Step 4: Run the PlayMode tests to verify they pass**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.CompanionAIPlayModeTests|Blackglass.Tests.AutoRetaliatePlayModeTests|Blackglass.Tests.CombatPausePlayModeTests"` → expected `total="35" passed="35"`, `EXIT=0` (16 + 4 companion, 7 + 3 retaliation, 4 + 1 pause).

Troubleshooting:
- `CompanionThatStopsOnAPointByChance_StillFollows` never occupies: the follow move must end within 0.6 m of (0, 0, −8.5); the companion starts straight north of the leader at (0, 0, 2), so `FollowPoint` is exactly (0, 0, −8.5). If the companion ended short, check `followDistance` is 3.5 and the leader did not move.
- `MeleeCompanionHoldingCover_...` sees an assist: the hostile walked within 2 m of the companion. It must fight the leader at (6, 5.5) from (6, 4) and never leave; check both have 300 hit points and the leader is the active character (no autonomy).

Run: `Tools/run-tests.sh PlayMode` → expected `total="256" passed="256"`, `EXIT=0`.
Run: `Tools/run-tests.sh EditMode` → expected `total="273" passed="273"`, `EXIT=0`.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/AI/CompanionAI.cs Assets/_Project/Scripts/Units/AutoRetaliate.cs Assets/_Project/Tests/PlayMode/CompanionAIPlayModeTests.cs Assets/_Project/Tests/PlayMode/AutoRetaliatePlayModeTests.cs Assets/_Project/Tests/PlayMode/CombatPausePlayModeTests.cs Assets/_Project/Tests/PlayMode/TestSupport/TestWorld.cs
git commit -m "Hold ordered cover: companions do not follow and nobody charges a shooter they cannot reach

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 8: Views: `CoverView`, cover orders in the queue view, HUD text

**Files:**
- Create: `Assets/_Project/Scripts/DebugUI/CoverView.cs`
- Modify: `Assets/_Project/Scripts/DebugUI/CommandQueueView.cs`
- Modify: `Assets/_Project/Scripts/DebugUI/PrototypeHud.cs`
- Modify: `Assets/_Project/Tests/EditMode/PrototypeHudTests.cs`
- Create: `Assets/_Project/Tests/PlayMode/CoverViewTests.cs`
- Modify: `Assets/_Project/Tests/PlayMode/CommandQueueViewTests.cs`

**Interfaces:**
- Consumes: `CoverRegistry.Points`, `CoverPoint` (`Position`, `Forward`, `IsClaimed`, `IsOccupied`), `UnitCover` (`Status`, `Point`, `OccupiedByOrder`), `UnitAttacker` (`IsTargetInCover(target, out chance)`, `Hits`, `ShotsFired`), `EnemyState.Cover`, `MoveToCoverCommand`, `TacticalPause.IsPaused`.
- Produces: `public sealed class CoverView : MonoBehaviour { internal static readonly Color AvailableColor, ReservedColor, OccupiedColor; internal IReadOnlyList<GameObject> Markers; internal int VisibleMarkerCount; internal static Color ColorFor(CoverPoint point); internal Color ShownColor(int index); internal void Initialize(CoverRegistry coverRegistry, TacticalPause pause, Material material); }`; `PrototypeHud.DescribeCover(CoverStatus status, string pointName, bool byOrder)`, `PrototypeHud.AppendTargetCover(string text, bool inCover, float hitChance)`, `PrototypeHud.AppendHits(string text, int hits, int shots)`.

- [ ] **Step 1: Write the failing EditMode tests**

Append to `Assets/_Project/Tests/EditMode/PrototypeHudTests.cs`, inside the class before its closing brace:

```csharp
        [TestCase(CoverStatus.None, "Cover_LowWall_L_S1", false, "")]
        [TestCase(CoverStatus.Reserved, "Cover_LowWall_L_S1", false, "Cover: Cover_LowWall_L_S1 (reserved)")]
        [TestCase(CoverStatus.Occupied, "Cover_LowWall_L_S1", false, "Cover: Cover_LowWall_L_S1 (occupied)")]
        [TestCase(CoverStatus.Occupied, "Cover_LowWall_L_S1", true, "Cover: Cover_LowWall_L_S1 (occupied, ordered)")]
        [TestCase(CoverStatus.Occupied, null, true, "")]
        public void DescribeCover_StatusAndPoint(CoverStatus status, string pointName, bool byOrder, string expected)
        {
            Assert.That(PrototypeHud.DescribeCover(status, pointName, byOrder), Is.EqualTo(expected));
        }

        [Test]
        public void AppendTargetCover_SaysInCoverWithTheChance_OrExposed()
        {
            Assert.That(PrototypeHud.AppendTargetCover("Attack", true, 0.5f), Is.EqualTo("Attack target in cover 50%"));
            Assert.That(PrototypeHud.AppendTargetCover("Attack LOS clear", true, 0.25f), Is.EqualTo("Attack LOS clear target in cover 25%"));
            Assert.That(PrototypeHud.AppendTargetCover("", false, 1f), Is.EqualTo("target exposed"));
        }

        [Test]
        public void AppendHits_OnlyAfterTheFirstShot()
        {
            Assert.That(PrototypeHud.AppendHits("Attack", 0, 0), Is.EqualTo("Attack"));
            Assert.That(PrototypeHud.AppendHits("Attack", 3, 7), Is.EqualTo("Attack hits 3/7"));
            Assert.That(PrototypeHud.AppendHits("", 0, 2), Is.EqualTo("hits 0/2"));
        }
```

And add one row to `DescribeEnemy_StateTargetAndCooldown`, after the `Reposition` row:

```csharp
        [TestCase(EnemyState.Cover, "FriendlyUnit_1", 0f, "Cover -> FriendlyUnit_1")]
```

- [ ] **Step 2: Run the EditMode tests to verify they fail**

Run: `Tools/run-tests.sh EditMode Blackglass.Tests.PrototypeHudTests` → expected `EXIT=1` with `error CS0117: 'PrototypeHud' does not contain a definition for 'DescribeCover'`.

- [ ] **Step 3: Add the HUD text, the queue-view case and `CoverView`**

In `Assets/_Project/Scripts/DebugUI/PrototypeHud.cs`:

1. `ControlHints`: add a fifth line: `"\nLeft-click cover marker: move into cover (one unit per marker; markers show while paused)"` (append to the last string literal).
2. Summary: `Unit labels show role, health, orders, AI state, line of sight and cover.`
3. After `AppendSight` add:

```csharp
        /// <summary>A unit's cover line: the point it holds and how, e.g. "Cover: Cover_LowWall_L_S1 (occupied, ordered)". Empty without one.</summary>
        internal static string DescribeCover(CoverStatus status, string pointName, bool byOrder)
        {
            if (status == CoverStatus.None || string.IsNullOrEmpty(pointName))
                return string.Empty;
            var how = status == CoverStatus.Reserved ? "reserved" : byOrder ? "occupied, ordered" : "occupied";
            return $"Cover: {pointName} ({how})";
        }

        /// <summary>Appends whether a ranged unit's target is in cover against it, with the chance to hit.</summary>
        internal static string AppendTargetCover(string text, bool inCover, float hitChance)
        {
            var verdict = inCover ? $"target in cover {Mathf.RoundToInt(hitChance * 100f)}%" : "target exposed";
            return text.Length == 0 ? verdict : $"{text} {verdict}";
        }

        /// <summary>Appends a ranged unit's hits over shots once it has fired.</summary>
        internal static string AppendHits(string text, int hits, int shots)
        {
            if (shots == 0)
                return text;
            var tally = $"hits {hits}/{shots}";
            return text.Length == 0 ? tally : $"{text} {tally}";
        }
```

4. In `DrawUnitLabel`, replace the block from `if (hasAttacker && attacker.NeedsLineOfSight && unit != null && unit.CurrentCommand is AttackCommand attack)` through `GUI.Label(...)` with:

```csharp
            if (hasAttacker && attacker.NeedsLineOfSight && unit != null && unit.CurrentCommand is AttackCommand attack)
            {
                activity = AppendSight(activity, attacker.HasLineOfSight(attack.Target));
                activity = AppendTargetCover(activity, attacker.IsTargetInCover(attack.Target, out var hitChance), hitChance);
            }
            if (hasAttacker && attacker.NeedsLineOfSight)
                activity = AppendHits(activity, attacker.Hits, attacker.ShotsFired);
            if (activity.Length > 0)
                text += "\n" + activity;
            if (unit != null)
            {
                // A point destroyed during a pause reads as null until UnitCover releases it on resume: draw nothing.
                var cover = unit.Cover;
                var coverText = DescribeCover(cover.Status, cover.Point != null ? cover.Point.name : null, cover.OccupiedByOrder);
                if (coverText.Length > 0)
                    text += "\n" + coverText;
            }

            var screen = viewCamera.WorldToScreenPoint(health.transform.position + Vector3.up * UnitLabelHeight);
            if (screen.z <= 0f)
                return;
            GUI.Label(new Rect(screen.x - 110f, Screen.height - screen.y - 22f, 220f, 66f), text, unitLabelStyle);
```

In `Assets/_Project/Scripts/DebugUI/CommandQueueView.cs`, in `AddOrder`, add a case after the `MoveCommand` case:

```csharp
                case MoveToCoverCommand toCover when toCover.Point != null && toCover.Point.gameObject.activeInHierarchy:
                    var spot = OnGround(toCover.Point.Position);
                    points.Add(spot);
                    ShowMarker(spot);
                    break;
```

(The `when` guard mirrors the attack case: a vanished point adds nothing and throws nothing.) Update the summary: `a small disc at every move or cover destination`.

Create `Assets/_Project/Scripts/DebugUI/CoverView.cs`:

```csharp
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Blackglass
{
    /// <summary>
    /// Debug view of the scene's cover points: a flat disc at each stand point and a nub on the obstacle side. Shows
    /// every point while paused and only claimed points in real time, coloured by state (available white, reserved
    /// yellow, occupied cyan). Reads the registry every frame and never changes it. Lives on Systems. Markers have no
    /// colliders, so they never block click raycasts. Works while paused.
    /// </summary>
    public sealed class CoverView : MonoBehaviour
    {
        internal static readonly Color AvailableColor = Color.white;
        internal static readonly Color ReservedColor = Color.yellow;
        internal static readonly Color OccupiedColor = Color.cyan;
        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");

        [SerializeField] CoverRegistry registry;
        [SerializeField] TacticalPause tacticalPause;
        [SerializeField] Material markerMaterial;
        [SerializeField, Min(0.05f)] float discDiameter = 0.5f;
        // The prototype ground is flat at y = 0; markers float just above it.
        [SerializeField] float groundHeight = 0.05f;

        // One marker per registry point, in registry order (built once in Start).
        readonly List<GameObject> markers = new List<GameObject>();
        readonly List<Renderer[]> markerRenderers = new List<Renderer[]>();
        MaterialPropertyBlock block;

        internal IReadOnlyList<GameObject> Markers => markers;

        internal int VisibleMarkerCount
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

        /// <summary>The colour a point's marker shows for its state.</summary>
        internal static Color ColorFor(CoverPoint point) =>
            !point.IsClaimed ? AvailableColor : point.IsOccupied ? OccupiedColor : ReservedColor;

        /// <summary>The colour currently applied to the marker at `index` (tests).</summary>
        internal Color ShownColor(int index)
        {
            block ??= new MaterialPropertyBlock();
            markerRenderers[index][0].GetPropertyBlock(block);
            return block.GetColor(BaseColor);
        }

        internal void Initialize(CoverRegistry coverRegistry, TacticalPause pause, Material material)
        {
            registry = coverRegistry;
            tacticalPause = pause;
            markerMaterial = material;
        }

        void Start()
        {
            if (registry == null)
                return;
            foreach (var point in registry.Points)
                markers.Add(CreateMarker(point));
        }

        void LateUpdate()
        {
            if (registry == null)
                return;
            var paused = tacticalPause != null && tacticalPause.IsPaused;
            for (var i = 0; i < markers.Count && i < registry.Points.Count; i++)
            {
                var point = registry.Points[i];
                var show = point != null && point.gameObject.activeInHierarchy && (paused || point.IsClaimed);
                if (markers[i].activeSelf != show)
                    markers[i].SetActive(show);
                if (show)
                    Paint(i, ColorFor(point));
            }
        }

        // A disc at the stand point plus a nub 0.4 m toward the obstacle. Parented here for cleanup; positions are world.
        GameObject CreateMarker(CoverPoint point)
        {
            var root = new GameObject("CoverMarker");
            root.transform.SetParent(transform, false);
            if (point != null)
            {
                var disc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                disc.name = "Disc";
                DestroyImmediate(disc.GetComponent<Collider>());
                disc.transform.SetParent(root.transform, false);
                disc.transform.position = point.Position + Vector3.up * groundHeight;
                disc.transform.localScale = new Vector3(discDiameter, 0.01f, discDiameter);
                var nub = GameObject.CreatePrimitive(PrimitiveType.Cube);
                nub.name = "Nub";
                DestroyImmediate(nub.GetComponent<Collider>());
                nub.transform.SetParent(root.transform, false);
                nub.transform.SetPositionAndRotation(point.Position + point.Forward * 0.4f + Vector3.up * 0.1f, Quaternion.LookRotation(point.Forward));
                nub.transform.localScale = new Vector3(0.15f, 0.15f, 0.3f);
            }
            var renderers = root.GetComponentsInChildren<Renderer>();
            foreach (var markerRenderer in renderers)
            {
                if (markerMaterial != null)
                    markerRenderer.sharedMaterial = markerMaterial;
                markerRenderer.shadowCastingMode = ShadowCastingMode.Off;
                markerRenderer.receiveShadows = false;
            }
            markerRenderers.Add(renderers);
            root.SetActive(false);
            return root;
        }

        void Paint(int index, Color color)
        {
            block ??= new MaterialPropertyBlock();
            block.SetColor(BaseColor, color);
            foreach (var markerRenderer in markerRenderers[index])
                markerRenderer.SetPropertyBlock(block);
        }
    }
}
```

- [ ] **Step 4: Run the EditMode tests to verify they pass**

Run: `Tools/run-tests.sh EditMode Blackglass.Tests.PrototypeHudTests` → expected `total="38" passed="38"`, `EXIT=0`.

- [ ] **Step 5: Write the failing PlayMode tests**

Create `Assets/_Project/Tests/PlayMode/CoverViewTests.cs`:

```csharp
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CoverViewTests
    {
        TestWorld world;
        TacticalPause pause;
        CoverPoint a;
        CoverPoint b;
        CoverRegistry registry;
        CoverView view;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            var environment = world.CreateEnvironment((new Vector3(0f, 0.45f, 0f), new Vector3(4f, 0.9f, 0.5f)));
            a = world.CreateCoverPoint(new Vector3(-1f, 0f, -1f), Vector3.forward, TestWorld.ObstacleCollider(environment));
            b = world.CreateCoverPoint(new Vector3(1f, 0f, -1f), Vector3.forward, TestWorld.ObstacleCollider(environment));
            registry = world.CreateRegistry(a, b);
            pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            var systems = world.Track(new GameObject("Systems"));
            systems.SetActive(false);
            view = systems.AddComponent<CoverView>();
            view.Initialize(registry, pause, null);
            systems.SetActive(true);
        }

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        [UnityTest]
        public IEnumerator BuildsOneColliderFreeMarkerPerPoint_HiddenInRealTime()
        {
            yield return null;
            Assert.That(view.Markers.Count, Is.EqualTo(2));
            foreach (var marker in view.Markers)
            {
                Assert.That(marker.GetComponentsInChildren<Collider>(true), Is.Empty, "Markers must never block click raycasts");
                Assert.That(marker.GetComponentsInChildren<Renderer>(true), Has.Length.EqualTo(2), "a disc and a nub");
            }
            Assert.That(view.VisibleMarkerCount, Is.EqualTo(0), "Unclaimed points are hidden in real time");
        }

        [UnityTest]
        public IEnumerator ShowsEveryPointWhilePaused_AndOnlyClaimedOnesInRealTime()
        {
            yield return null;
            pause.Pause();
            yield return null;
            Assert.That(view.VisibleMarkerCount, Is.EqualTo(2), "Paused: every point shows");

            pause.Resume();
            yield return null;
            Assert.That(view.VisibleMarkerCount, Is.EqualTo(0));

            var unit = world.CreateFighter(new Vector3(0f, 0f, -6f), registry: registry);
            Assert.That(unit.Issue(new MoveToCoverCommand(a)), Is.True);
            yield return null;
            Assert.That(view.VisibleMarkerCount, Is.EqualTo(1), "Only the claimed point shows in real time");
            Assert.That(view.Markers[0].activeSelf, Is.True);
            Assert.That(view.Markers[1].activeSelf, Is.False);
        }

        [UnityTest]
        public IEnumerator ColoursFollowTheState()
        {
            yield return null;
            pause.Pause();
            yield return null;
            Assert.That(view.ShownColor(0), Is.EqualTo(CoverView.AvailableColor));

            var unit = world.CreateFighter(new Vector3(0f, 0f, -6f), registry: registry);
            Assert.That(unit.Issue(new MoveToCoverCommand(a)), Is.True);
            yield return null;
            Assert.That(view.ShownColor(0), Is.EqualTo(CoverView.ReservedColor));
            Assert.That(view.ShownColor(1), Is.EqualTo(CoverView.AvailableColor));

            pause.Resume();
            yield return TestWorld.WaitUntil(() => unit.Cover.Status == CoverStatus.Occupied, 10f);
            yield return null;
            Assert.That(view.ShownColor(0), Is.EqualTo(CoverView.OccupiedColor));
        }
    }
}
```

Append to `Assets/_Project/Tests/PlayMode/CommandQueueViewTests.cs`, inside the class before its closing brace:

```csharp
        [UnityTest]
        public IEnumerator CoverOrder_ShowsAMarkerAtThePoint_AndAVanishedPointDrawsNothing()
        {
            var environment = world.CreateEnvironment((new Vector3(0f, 0.45f, 0f), new Vector3(4f, 0.9f, 0.5f)));
            var point = world.CreateCoverPoint(new Vector3(0f, 0f, -1f), Vector3.forward, TestWorld.ObstacleCollider(environment));
            var pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            var unit = world.CreateUnit(new Vector3(0f, 0f, -8f));
            var view = unit.gameObject.AddComponent<CommandQueueView>();
            yield return null;
            // Paused, so the walk never starts and the order stays current while its point is destroyed.
            pause.Pause();
            Assert.That(unit.Issue(new MoveToCoverCommand(point)), Is.True);
            yield return null;
            Assert.That(view.LinePointCount, Is.EqualTo(2), "unit + the cover point");
            Assert.That(view.ActiveMarkerCount, Is.EqualTo(1));

            Object.Destroy(point.gameObject);
            yield return null;
            yield return null;

            Assert.That(unit.CurrentCommand, Is.TypeOf<MoveToCoverCommand>(), "Precondition: paused, the order is still current");
            Assert.That(view.LinePointCount, Is.EqualTo(0), "A vanished point draws nothing, and nothing throws");
            Assert.That(view.ActiveMarkerCount, Is.EqualTo(0));
        }
```

- [ ] **Step 6: Run the PlayMode tests**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.CoverViewTests|Blackglass.Tests.CommandQueueViewTests"` → expected `total="6" passed="6"`, `EXIT=0`.

Troubleshooting: `ColoursFollowTheState` reads white after the reservation: `LateUpdate` must paint on every shown frame (not only on visibility changes); `ShownColor` reads the first renderer's property block.

Run: `Tools/run-tests.sh PlayMode` → expected `total="260" passed="260"`, `EXIT=0`.
Run: `Tools/run-tests.sh EditMode` → expected `total="281" passed="281"`, `EXIT=0`.

- [ ] **Step 7: Commit**

```bash
git add Assets/_Project/Scripts/DebugUI/CoverView.cs Assets/_Project/Scripts/DebugUI/CoverView.cs.meta Assets/_Project/Scripts/DebugUI/CommandQueueView.cs Assets/_Project/Scripts/DebugUI/PrototypeHud.cs Assets/_Project/Tests/EditMode/PrototypeHudTests.cs Assets/_Project/Tests/PlayMode/CoverViewTests.cs Assets/_Project/Tests/PlayMode/CoverViewTests.cs.meta Assets/_Project/Tests/PlayMode/CommandQueueViewTests.cs
git commit -m "Show cover points, cover orders and cover state in the debug views

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 9: Prefabs, arena, cover points and scene tests

**Files:**
- Modify: `Assets/_Project/Tests/PlayMode/PrototypeSceneTests.cs`
- Create then delete (never committed): `Assets/_Project/Editor/CoverSceneBuilder.cs`
- Modify (generated): `Assets/_Project/Scenes/Prototype.unity`, `Assets/_Project/Scenes/Prototype/NavMesh-Environment.asset` (+ `.meta`); the prefabs already carry `UnitCover` since Task 3 and are re-saved unchanged
- Create (generated): `Assets/_Project/Materials/CoverMarker.mat` (+ `.meta`)

**Interfaces:**
- Consumes: every component and serialized field name from Tasks 1–8: `CoverPoint.obstacle`, `CoverRegistry.points`, `CoverView.registry/tacticalPause/markerMaterial`, `PlayerCommandInput.coverRegistry`, `EnemyAI.coverRegistry`, `UnitCover.registry`; `PlayerCommandInput.IsCoverWired`, `EnemyAI.IsCoverWired`, `UnitCover.IsWired`.
- Produces: `Prototype.unity` with `UnitCover` on all six units wired to the registry; `Systems` carrying `CoverRegistry` (20 points) and `CoverView`; the walls `LowWall_L`, `LowWall_M`, `LowWall_N`; a `CoverPoints` root with 20 named children; a rebaked NavMesh.

- [ ] **Step 1: Update the scene tests**

In `Assets/_Project/Tests/PlayMode/PrototypeSceneTests.cs`, inside `Scene_ContainsWiredSquadAndHostiles_AndRunsWithoutErrors`, after the `foreach (var obstacle in new[] { "Pillar_G", ... })` loop, add:

```csharp
            foreach (var wall in new[] { "LowWall_L", "LowWall_M", "LowWall_N" })
                Assert.That(GameObject.Find(wall), Is.Not.Null, $"{wall} missing");
            var registry = Object.FindFirstObjectByType<CoverRegistry>();
            Assert.That(registry, Is.Not.Null, "CoverRegistry missing");
            Assert.That(registry.Points.Count, Is.EqualTo(20));
            var pointsRoot = GameObject.Find("CoverPoints");
            Assert.That(pointsRoot, Is.Not.Null, "CoverPoints root missing");
            Assert.That(registry.Points, Is.EquivalentTo(pointsRoot.GetComponentsInChildren<CoverPoint>()));
            foreach (var point in registry.Points)
            {
                Assert.That(point.Obstacle, Is.Not.Null, $"{point.name} has no obstacle");
                Assert.That(point.IsClaimed, Is.False, $"{point.name} starts claimed");
                Assert.That(UnityEngine.AI.NavMesh.SamplePosition(point.Position, out _, 0.5f, UnityEngine.AI.NavMesh.AllAreas), Is.True,
                    $"{point.name} at {point.Position} is off the NavMesh");
            }
            Assert.That(Object.FindFirstObjectByType<CoverView>(), Is.Not.Null, "CoverView missing");
            Assert.That(Object.FindFirstObjectByType<PlayerCommandInput>().IsCoverWired, Is.True, "PlayerCommandInput.coverRegistry is not wired");
            foreach (var hostile in hostiles)
                Assert.That(hostile.IsCoverWired, Is.True, $"{hostile.name}'s EnemyAI has no cover registry");
            foreach (var unit in friendlies.Select(f => f.Unit).Concat(hostiles.Select(h => h.GetComponent<CommandableUnit>())))
                Assert.That(unit.Cover.IsWired, Is.True, $"{unit.name}'s UnitCover is not wired");
```

In the `PrototypeSceneInputTests` class, add:

```csharp
        [UnityTest]
        public IEnumerator PausedClickNearACoverMarker_InScene_OrdersCover_ThenTheUnitOccupiesItAfterResume()
        {
            var mouse = InputSystem.AddDevice<Mouse>();
            var keyboard = InputSystem.AddDevice<Keyboard>();
            yield return LoadScene();
            var squad = PrototypeSceneTests.FindSquad();
            var point = GameObject.Find("Cover_LowWall_L_S1").GetComponent<CoverPoint>();

            yield return Tap(keyboard.spaceKey);
            yield return LeftClickAt(mouse, Camera.main.WorldToScreenPoint(squad[0].transform.position));
            // 0.5 m east of the point, on open ground south of LowWall_L (nothing between it and the camera).
            yield return LeftClickAt(mouse, Camera.main.WorldToScreenPoint(point.Position + new Vector3(0.5f, 0f, 0f)));

            Assert.That(squad[0].CurrentCommand, Is.TypeOf<MoveToCoverCommand>(), "A paused click beside a marker orders cover");
            Assert.That(((MoveToCoverCommand)squad[0].CurrentCommand).Point, Is.SameAs(point));
            Assert.That(squad[0].Cover.Status, Is.EqualTo(CoverStatus.Reserved));
            Assert.That(squad[1].CurrentCommand, Is.Null, "Only the selected unit is ordered");

            yield return Tap(keyboard.spaceKey);
            yield return TestWorld.WaitUntil(() => squad[0].Cover.Status == CoverStatus.Occupied, 20f);

            Assert.That(squad[0].Cover.Status, Is.EqualTo(CoverStatus.Occupied), "After resume the unit walks there and takes it");
            Assert.That(squad[0].Cover.Point, Is.SameAs(point));
        }
```

- [ ] **Step 2: Run the scene tests to verify they fail**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.PrototypeSceneTests|Blackglass.Tests.PrototypeSceneInputTests"` → expected `EXIT=2`: the wiring test fails on `LowWall_L missing`; the new input test fails with a `NullReferenceException` on `GameObject.Find("Cover_LowWall_L_S1")` (no points in the scene yet). The others pass.

- [ ] **Step 3: Create the temporary scene builder**

Create `Assets/_Project/Editor/CoverSceneBuilder.cs`:

```csharp
// TEMPORARY: builds the Phase 6 cover arena, points, registry, view and wiring into the prefabs and Prototype.unity,
// then this file is deleted (never committed).
using System;
using System.Collections.Generic;
using Blackglass;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;

public static class CoverSceneBuilder
{
    const string ScenePath = "Assets/_Project/Scenes/Prototype.unity";
    const string NavMeshPath = "Assets/_Project/Scenes/Prototype/NavMesh-Environment.asset";
    const string FriendlyPrefabPath = "Assets/_Project/Prefabs/FriendlyUnit.prefab";
    const string HostilePrefabPath = "Assets/_Project/Prefabs/HostileUnit.prefab";
    const string ObstacleMaterialPath = "Assets/_Project/Materials/Obstacle.mat";
    const string MarkerMaterialPath = "Assets/_Project/Materials/CoverMarker.mat";

    // Waist-high (0.9 m) walls: the 1.5 m eye line clears them, the eye-to-feet cover ray crosses them.
    static readonly (string name, Vector3 position, Vector3 scale)[] LowWalls =
    {
        ("LowWall_L", new Vector3(6.5f, 0.45f, -3f), new Vector3(3f, 0.9f, 0.5f)),
        ("LowWall_M", new Vector3(-4f, 0.45f, 6f), new Vector3(4f, 0.9f, 0.5f)),
        ("LowWall_N", new Vector3(8.5f, 0.45f, 6f), new Vector3(2f, 0.9f, 0.5f)),
    };

    // Stand points 0.75 m from a face (agent radius 0.5 m plus margin), facing into the obstacle.
    static readonly (string name, string obstacle, Vector3 position, Vector3 forward)[] Points =
    {
        ("Cover_LowWall_L_S1", "LowWall_L", new Vector3(5.5f, 0f, -4f), Vector3.forward),
        ("Cover_LowWall_L_S2", "LowWall_L", new Vector3(7.5f, 0f, -4f), Vector3.forward),
        ("Cover_LowWall_L_N1", "LowWall_L", new Vector3(5.5f, 0f, -2f), Vector3.back),
        ("Cover_LowWall_L_N2", "LowWall_L", new Vector3(7.5f, 0f, -2f), Vector3.back),
        ("Cover_LowWall_M_S1", "LowWall_M", new Vector3(-5f, 0f, 5f), Vector3.forward),
        ("Cover_LowWall_M_S2", "LowWall_M", new Vector3(-3f, 0f, 5f), Vector3.forward),
        ("Cover_LowWall_M_N1", "LowWall_M", new Vector3(-5f, 0f, 7f), Vector3.back),
        ("Cover_LowWall_M_N2", "LowWall_M", new Vector3(-3f, 0f, 7f), Vector3.back),
        ("Cover_LowWall_N_S", "LowWall_N", new Vector3(8.5f, 0f, 5f), Vector3.forward),
        ("Cover_LowWall_N_N", "LowWall_N", new Vector3(8.5f, 0f, 7f), Vector3.back),
        ("Cover_Pillar_G_S", "Pillar_G", new Vector3(-2f, 0f, -7.5f), Vector3.forward),
        ("Cover_Pillar_G_N", "Pillar_G", new Vector3(-2f, 0f, -4.5f), Vector3.back),
        ("Cover_Pillar_G_W", "Pillar_G", new Vector3(-3.5f, 0f, -6f), Vector3.right),
        ("Cover_Pillar_G_E", "Pillar_G", new Vector3(-0.5f, 0f, -6f), Vector3.left),
        ("Cover_Crate_J_S", "Crate_J", new Vector3(10f, 0f, -6.75f), Vector3.forward),
        ("Cover_Crate_J_N", "Crate_J", new Vector3(10f, 0f, -3.25f), Vector3.back),
        ("Cover_Crate_J_W", "Crate_J", new Vector3(8.25f, 0f, -5f), Vector3.right),
        ("Cover_Crate_J_E", "Crate_J", new Vector3(11.75f, 0f, -5f), Vector3.left),
        ("Cover_Barrier_I_S1", "Barrier_I", new Vector3(-9f, 0f, 0.75f), Vector3.forward),
        ("Cover_Barrier_I_S2", "Barrier_I", new Vector3(-7f, 0f, 0.75f), Vector3.forward),
    };

    public static void Build()
    {
        var obstacleMaterial = Require(AssetDatabase.LoadAssetAtPath<Material>(ObstacleMaterialPath), ObstacleMaterialPath);
        AddUnitCoverToPrefab(FriendlyPrefabPath);
        AddUnitCoverToPrefab(HostilePrefabPath);
        var markerMaterial = CreateUnlitMaterial(MarkerMaterialPath, Color.white);

        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var systems = Require(GameObject.Find("Systems"), "Systems");
        var environment = Require(GameObject.Find("Environment"), "Environment");
        if (GameObject.Find("CoverPoints") != null)
            throw new Exception("CoverPoints already exists; the builder has already run.");

        foreach (var (name, position, scale) in LowWalls)
            CreateObstacle(environment, name, position, scale, obstacleMaterial);

        var pointsRoot = new GameObject("CoverPoints");
        var points = new List<CoverPoint>();
        foreach (var (name, obstacleName, position, forward) in Points)
        {
            var obstacle = Require(GameObject.Find(obstacleName), obstacleName);
            var collider = Require(obstacle.GetComponent<BoxCollider>(), $"BoxCollider on {obstacleName}");
            var host = new GameObject(name);
            host.transform.SetParent(pointsRoot.transform, false);
            host.transform.SetPositionAndRotation(position, Quaternion.LookRotation(forward));
            var point = host.AddComponent<CoverPoint>();
            SetReference(point, "obstacle", collider);
            points.Add(point);
        }

        var registry = systems.AddComponent<CoverRegistry>();
        var serializedRegistry = new SerializedObject(registry);
        var list = Property(serializedRegistry, "points");
        list.arraySize = points.Count;
        for (var i = 0; i < points.Count; i++)
            list.GetArrayElementAtIndex(i).objectReferenceValue = points[i];
        serializedRegistry.ApplyModifiedPropertiesWithoutUndo();

        var view = systems.AddComponent<CoverView>();
        SetReference(view, "registry", registry);
        SetReference(view, "tacticalPause", Require(systems.GetComponent<TacticalPause>(), "TacticalPause on Systems"));
        SetReference(view, "markerMaterial", markerMaterial);

        SetReference(Require(systems.GetComponent<PlayerCommandInput>(), "PlayerCommandInput on Systems"), "coverRegistry", registry);
        foreach (var ai in Object.FindObjectsByType<EnemyAI>(FindObjectsSortMode.None))
            SetReference(ai, "coverRegistry", registry);
        var covers = Object.FindObjectsByType<UnitCover>(FindObjectsSortMode.None);
        if (covers.Length != 6)
            throw new Exception($"Expected 6 UnitCover components in the scene (one per prefab instance; Task 3 Step 4b put it on the prefabs), found {covers.Length}.");
        foreach (var cover in covers)
            SetReference(cover, "registry", registry);

        var surface = Require(environment.GetComponent<NavMeshSurface>(), "NavMeshSurface on Environment");
        surface.BuildNavMesh();
        AssetDatabase.DeleteAsset(NavMeshPath);
        AssetDatabase.CreateAsset(surface.navMeshData, NavMeshPath);

        foreach (var point in points)
        {
            if (!NavMesh.SamplePosition(point.Position, out _, 0.5f, NavMesh.AllAreas))
                throw new Exception($"{point.name} at {point.Position} has no walkable NavMesh within 0.5 m.");
        }
        // The corridor midpoint between the central wall's south-east corner and LowWall_L lies on the mesh only if
        // the 0.84 m eroded corridor survived the bake (a path test would pass round the wall's other end regardless).
        if (!NavMesh.SamplePosition(new Vector3(4.1f, 0f, -2.6f), out _, 0.25f, NavMesh.AllAreas))
            throw new Exception("The corridor between the central wall and LowWall_L did not survive the bake.");

        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene))
            throw new Exception("Failed to save " + ScenePath);
        AssetDatabase.SaveAssets();
        Debug.Log($"[CoverSceneBuilder] Built the Phase 6 cover arena into {ScenePath}");
    }

    // Task 3 already added UnitCover to both prefabs; this only repairs a prefab that lost it. Idempotent.
    static void AddUnitCoverToPrefab(string path)
    {
        var root = PrefabUtility.LoadPrefabContents(path);
        try
        {
            if (root.GetComponent<UnitCover>() == null)
                root.AddComponent<UnitCover>();   // defaults; the registry is wired per scene instance
            PrefabUtility.SaveAsPrefabAsset(root, path);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    static Material CreateUnlitMaterial(string path, Color color)
    {
        AssetDatabase.DeleteAsset(path);
        var shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null)
            throw new Exception("URP Unlit shader not found");
        var material = new Material(shader);
        material.SetColor("_BaseColor", color);
        AssetDatabase.CreateAsset(material, path);
        return material;
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
"/c/Program Files/Unity/Hub/Editor/6000.3.25f1/Editor/Unity.exe" -batchmode -quit -projectPath "$(pwd -W)" -executeMethod CoverSceneBuilder.Build -logFile "$(pwd -W)/Logs/BuildCoverScene.log"; echo "EXIT=$?"
grep -E '\[CoverSceneBuilder\]|error CS|Exception' Logs/BuildCoverScene.log | head -20
grep -c "m_Name: LowWall_" Assets/_Project/Scenes/Prototype.unity                       # expect 3
grep -c "m_Name: Cover_" Assets/_Project/Scenes/Prototype.unity                         # expect 20
grep -c "propertyPath: registry" Assets/_Project/Scenes/Prototype.unity                 # expect 6 (one per unit's UnitCover)
guid=$(grep guid Assets/_Project/Scripts/Units/UnitCover.cs.meta | sed 's/.*guid: //'); grep -c "$guid" Assets/_Project/Prefabs/FriendlyUnit.prefab Assets/_Project/Prefabs/HostileUnit.prefab   # expect 1 each
ls Assets/_Project/Materials/CoverMarker.mat*                                           # the material and its .meta
git status --short                                                                       # prefabs, scene, NavMesh asset, material, Editor/ (uncommitted)
```

**Expected:** `EXIT=0`; the log contains `[CoverSceneBuilder] Built the Phase 6 cover arena into Assets/_Project/Scenes/Prototype.unity` and no exceptions; the counts match.

Troubleshooting:
- `Expected 6 UnitCover components ... found 0`: the prefab change did not propagate; confirm `AddUnitCoverToPrefab` ran for both prefabs (the log shows no earlier exception) and that the scene's units are prefab instances.
- `... has no walkable NavMesh within 0.5 m`: that point sits in an erosion band; re-check its position against section 6 of the spec (0.75 m from the face) and the wall's position.
- `The corridor ... did not survive the bake`: LowWall_L must be at (6.5, 0.45, −3) with scale (3, 0.9, 0.5), so its west face is at x 5.

Then delete the builder:

```bash
rm -r Assets/_Project/Editor Assets/_Project/Editor.meta
```

- [ ] **Step 5: Run all tests to verify they pass**

Run: `Tools/run-tests.sh PlayMode` → expected `total="261" passed="261"`, `EXIT=0`. This run also recompiles without the deleted builder.
Run: `Tools/run-tests.sh EditMode` → expected `total="281" passed="281"`, `EXIT=0`.

Troubleshooting:
- `Move_AroundTheCentralWall_Arrives` fails on the 0.3 m tolerance: LowWall_N's west face must be at x 7.5 (1.5 m from the destination (6, 0, 6)); check its position and scale.
- `PausedClickNearACoverMarker_...` never occupies: the walk from (−12, −10) to (5.5, −4) must pass the corridor west of LowWall_L; if the unit is held off, check `Cover_LowWall_L_S1` is at (5.5, 0, −4) and no companion is standing on it (companions follow 3.5 m behind, so they should not).
- Hostiles not idle at start: `HostileUnit_1` at (8, 8) is 1.1 m from `Cover_LowWall_N_N`, outside the 0.6 m occupy radius, and 12.3 m from the squad; nothing should have changed for it.

- [ ] **Step 6: Commit**

```bash
git add -A Assets/_Project/Scenes Assets/_Project/Prefabs Assets/_Project/Materials Assets/_Project/Tests/PlayMode/PrototypeSceneTests.cs
git status --short   # must NOT list Assets/_Project/Editor
git commit -m "Build the cover arena, cover points and cover wiring into the prototype scene

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 10: Decision records, spec status and final verification

**Files:**
- Modify: `Docs/Decisions.md`
- Modify: `Docs/superpowers/specs/2026-10-05-cover-system-design.md` (status line)

**Interfaces:** none (documentation only).

- [ ] **Step 1: Append the three new records to `Docs/Decisions.md`**

Append after record 018:

```markdown

## 019 — Cover points and occupancy

- **Decided (Phase 6, 2026-10-05):** Cover is a set of hand-placed `CoverPoint`s (`Scripts/Cover/`), each a stand position at ground level with a reference to the one collider that protects it and a per-point `hitChance` (0.5); the arena's 20 points are generated once by the scene builder, like the obstacles. `CoverRegistry` on `Systems` lists them, exactly as `Encounter` lists the sides: a list, not a manager. Every unit carries `UnitCover`, the **only writer of claims**: a **reservation** is made when a `MoveToCoverCommand` starts and lives exactly as long as that order is current (`Replace`, `Stop`, death, direct control, a failed arrival, a vanished point and a walk that makes no progress for 3 s all release it, always after the replacing order has been accepted); an **occupancy** is reached by arriving (`TryOccupy`, within 0.6 m) or by standing still (flat speed under 0.5 m/s) within 0.6 m of an unclaimed point, and lives until the unit is more than 1 m away, the point is destroyed or disabled, or the unit is disabled or dies. `OccupiedByOrder` tells the two apart; only an ordered occupancy changes autonomous behaviour (021). One unit per point: `TryClaim` succeeds only for an unclaimed point or the claimant's own. Standing on a free point is the whole direct-control cover system.
- **Why:** hand-placed points keep the prototype's cover explicit and debuggable; a serialized list matches the project's wiring rule; binding the reservation to the order means no path that clears a queue can leak a claim, and binding the occupancy to standing there means the controlled character, a plain move and a chase all get cover through one rule; the stillness test stops units walking across points from claiming them.
- **Rejected:** generating points at runtime around tagged obstacles (rules per shape and a NavMesh test per face for a benefit the builder already gives); a cover manager or static registry; reserving at queue time (every queue-clearing path would have to release); tagging commands with their source; `UnitMover.HasArrived` as the stillness test (true while steering); `RequireComponent(Health)` on `UnitCover` (it would chain through `CommandableUnit` and force a `Health` onto every unit, including test units).
- **Implications:** new obstacles need new points. A unit jostled more than 1 m from its point loses it; one standing farther than 0.6 m from the centre is exposed. A cover order to a point another unit physically stands on gives up after 3 s. `Health` stays optional on every unit.

## 020 — Cover and ranged fire

- **Decided (Phase 6, 2026-10-05):** The one directional test is geometric: `CoverPoint.ProtectsFrom` casts a ray from the attacker's eye (pivot + 0.5 m) to the defender's feet (the defender's x/z at the point's height) against the point's obstacle collider alone (`Collider.Raycast`); the obstacle must actually lie between the two. The one defensive effect is **hit probability**: `UnitAttacker.TryAttack` fires at an exposed target as before and at a target in cover with that point's `hitChance`; melee ignores cover. `TryAttack` now means "a shot was fired": `Attacked` is a hit, `Missed` a miss; `ShotsFired` and `Hits` are debug counters; the roll is an injectable `Func<float>` (default `Random.value`). Sight is checked first, cover second: tall geometry blocks the shot entirely, waist-high (0.9 m) geometry lets it through at reduced odds. Points at tall obstacles are therefore hiding spots, points at waist-high walls firing spots. `CommandableUnit` resets its reposition counter on a fired shot.
- **Why:** the ray answers "is the obstacle between" for any shape without a per-point tuning angle; a probability is the smallest hit-resolution rule and keeps every exposed-target test unchanged; the injectable roll keeps combat tests deterministic.
- **Rejected:** an angular cone around the point's forward (redundant with the ray in every arena case, and it denied protection a wide wall gives at steep angles); damage reduction, armour, body parts, criticals, accuracy stats, ballistics.
- **Implications:** with the 0.75 m stand offset the ray meets an obstacle's near face at height 1.125 / D, so a 0.9 m wall registers beyond 1.25 m and lower obstacles only from farther away (0.6 m: about 1.9 m). Weapons later become data feeding the same four numbers.

## 021 — Cover decisions by the player and the AI

- **Decided (Phase 6, 2026-10-05):** A ground click within 1 m of a cover point is a `MoveToCoverCommand` (`CommandResolver`, with the point found by `PlayerCommandInput` through `CoverRules.TryChooseNearest`); Attack on a living `Health` still wins. A group ordered to one point sends the first unit that can reserve it and refuses the rest with their orders unchanged (`GroupOrders` unchanged). A ranged hostile that acquires a target first looks, at acquisition only, for the nearest registry point within 8 m that is unclaimed or its own, protects from the target, lets it attack the target from there and is reachable, and issues `MoveToCover` then `Attack` appended; melee hostiles never look. A companion holding **ordered** cover does not follow and assists only against a target it can attack from where it stands; `AutoRetaliate` applies the same rule; both decide when the own order is issued and never re-examine a running order (acquisition only, as 018). "Whom is it attacking" is read everywhere from `CommandableUnit.AttackTarget` (the current attack's target, else the first pending attack's while a cover order is current), so a hostile walking to cover with its attack queued is engaged for companions and shows its target in the HUD. The cover view shows every point while paused and only claimed points in real time.
- **Why:** reusing the click and `Issue` path keeps input free of cover logic; one unit per marker is the simplest rule the HUD can explain; a hostile that already stands on useful cover must not walk to the next point; without the hold rules the first ranged hit would undo every cover order given to a melee unit; `AttackTarget` gives `EnemyAI` and `CompanionAI` one definition instead of two.
- **Rejected:** moving the rest of a group onto a lattice around the point (slots land on the exposed side) or assigning neighbouring points (squad cover assignment); companion cover seeking; hostile cover re-evaluation while fighting; a mid-order Stop when a covered assister's target leaves range (contradicts 018's acquisition-only model and needs machinery `AutoRetaliate` does not have); cover-aware repositioning (a Phase 7 candidate: feed cover value into `FiringPositionFinder`'s predicate); a selection-aware marker colour (the HUD and the queue view already identify the selected unit's point).
- **Implications:** hostile cover depends on where the target is first seen; from some approaches a ranged hostile finds no useful point and attacks in the open. A covered assister or retaliator whose target leaves its range or sight follows it out of cover through the normal attack phases.
```

- [ ] **Step 2: Amend the earlier records**

In `Docs/Decisions.md`:

- **006**, after the "Direct control" bullet, add: `- **Cover (Phase 6, 2026-10-05):** a fourth command type, \`MoveToCoverCommand(CoverPoint)\`, with its cases in \`CommandableUnit\` (019); \`Issue\` also refuses a point another unit holds.`
- **008**, in the left-button bullet, change `anything else is an order: Attack on a living \`Health\`, otherwise Move.` to `anything else is an order: Attack on a living \`Health\`, otherwise MoveToCover when a cover point lies within 1 m of the click (Phase 6, 021), otherwise Move.`
- **014**, add to the **Implications** bullet: `Phase 6 (2026-10-05): still four deciders and one attack path; \`EnemyAI\` may issue \`MoveToCover\` ahead of its \`AttackCommand\`, \`UnitAttacker\` rolls cover before applying damage and may raise \`Missed\` (020), and \`AutoRetaliate\` holds ordered cover (021).`
- **015**, add to the **Implications** bullet: `Phase 6 (2026-10-05): a ranged hostile may first take nearby useful cover (021); \`EnemyState\` gains \`Cover\`.`
- **017**, in the **Decided** bullet, change `After three repositions without landing a hit` to `After three repositions without firing a shot (since Phase 6 a fired shot, hit or miss, resets the counter: whether it lands is cover, 020)`.
- **018**, add to the **Implications** bullet: `Phase 6 (2026-10-05): a companion holding cover it was ordered into does not follow and assists only against a target it can attack from there (021); "engaged" reads \`CommandableUnit.AttackTarget\`.`

- [ ] **Step 3: Update the spec's status line**

In `Docs/superpowers/specs/2026-10-05-cover-system-design.md`, change the `Status:` on line 3 to `implemented 2026-10-05 (see Docs/superpowers/plans/2026-10-05-cover-system.md)`.

- [ ] **Step 4: Final verification**

```bash
git status --short                 # only the two docs modified
Tools/run-tests.sh EditMode        # expected total="281" passed="281" EXIT=0
Tools/run-tests.sh PlayMode        # expected total="261" passed="261" EXIT=0
grep -rn "Assets/_Project/Editor" .gitignore Assets/_Project 2>/dev/null; ls Assets/_Project/Editor 2>/dev/null   # expect nothing: the builder is gone
git log --oneline main..HEAD       # nine task commits so far, plus the spec and plan commits (Task 10's own commit follows in Step 5)
```

Then walk the spec's manual checks in the Editor (owner): the paused cover view, a unit behind `LowWall_M` taking fewer hits than one in the open (the shooter's `hits` tally), `HostileUnit_3` walking to `Cover_LowWall_N_N` when the squad comes up the corridor north of `Obstacle_E`, a melee companion holding `LowWall_L` under fire, Tab and Shift+Tab, victory and defeat with the console open, and no marker left yellow or cyan after a fight.

- [ ] **Step 5: Commit**

```bash
git add Docs/Decisions.md Docs/superpowers/specs/2026-10-05-cover-system-design.md
git commit -m "Record the cover point, cover fire and cover decision records

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```
