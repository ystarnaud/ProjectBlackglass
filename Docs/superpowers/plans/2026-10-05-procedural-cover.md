# Procedural Cover Discovery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the hand-placed `CoverPoint` scene objects with cover locations discovered at runtime from designated box geometry, including tall-wall corner cover that carries peek data, without changing any command, AI or pause behaviour.

**Architecture:** A pure `CoverGenerator` turns oriented boxes plus a walkability predicate into plain-class `CoverLocation`s. A thin `CoverDiscovery` component finds `CoverSurface`-tagged boxes, supplies the NavMesh predicate and pushes the result into `CoverRegistry.Rebuild`. `CoverLocation` replaces the `CoverPoint` MonoBehaviour everywhere; `UnitCover`, commands, AI and views change type only.

**Tech Stack:** Unity 6.3 LTS (6000.3.25f1), C#, NUnit via Unity Test Framework, AI Navigation (`NavMesh`), Input System (untouched).

**Spec:** `Docs/superpowers/specs/2026-10-05-procedural-cover-design.md` (read it first; decisions 019 to 022 in `Docs/Decisions.md` give the rules this plan keeps).

## Global Constraints

- Unity 6000.3.25f1; tests run with `Tools/run-tests.sh EditMode|PlayMode [filter]` and the **Unity Editor must be closed** (one instance per project). Run test commands from the repo root `F:\Programs\ProjectBlackglass` in Git Bash.
- All runtime code is under `Assets/_Project/Scripts/`, namespace `Blackglass`; tests under `Assets/_Project/Tests/{EditMode,PlayMode}/`, namespace `Blackglass.Tests`. Internals are visible to both test assemblies.
- New simulation components check `SimulationTime.IsRunning`, never `Time.deltaTime` alone. Discovery is not simulation and runs on no clock.
- No third-party packages. No new input bindings. Placeholder primitives only.
- `CoverLocation` claim writers stay `internal`; `UnitCover` is the only writer of claims.
- Generator defaults (verbatim from the spec): `standOffset` 0.75 m, `spacing` 2 m, `endMargin` 0.5 m, `minFaceLength` 1 m, `lowMaxHeight` 1.2 m, `minCornerLength` 2 m, `cornerInset` 0.35 m, `peekDistance` 1.25 m, `mergeDistance` 1 m, `hitChance` 0.5, `walkableTolerance` 0.25 m.
- Hostiles choose only `Height == Low` locations (spec assumption 3).
- Commit messages end with the trailer `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>` (project attribution rule). Work on branch `procedural-cover` (already created; the spec is committed on it). Never commit `Library/`, `Temp/`, `Logs/`, or the temporary scene builder.
- Do not change anything outside the files each task lists. Do not start Phase 7 or any controller work.

## Review Focus

Failure modes the spec implies that no single feature test would otherwise catch; each has a pinning test in the task named.

1. A location whose obstacle is deactivated or destroyed must read as invalid and be released, not protect (Task 1, `CoverLocationTests`).
2. `Discover()` run twice, or after geometry changes, must leave no claimed retired location and no duplicated live location (Task 5, `CoverDiscoveryPlayModeTests`).
3. A unit holding or walking to a location that a rebuild retires must end up with no cover and no exception, and its queued next order must still run (Task 5).
4. A thin tall wall, a crate and a pillar must not produce corner cover (crates are not walls), and a wall turned 45 degrees must give the same counts as an axis-aligned one (Task 3).
5. A corner whose peek point is off the NavMesh must stay a usable corner location with no peek data, not vanish or throw (Task 3).
6. A tall corner/face location must never be chosen by a hostile (it would stand hidden and then reposition out of cover) (Task 6).
7. No generated location may sit within the 0.6 m occupy radius of a unit's start (it would be claimed by chance before the first order) (Task 8).

---

## File Structure

| File | Responsibility |
|---|---|
| `Scripts/Cover/CoverLocation.cs` (new, replaces `CoverPoint.cs`) | The generic runtime cover representation, enums `CoverHeight`, `CoverPlacement`, claim state, `ProtectsFrom`. |
| `Scripts/Cover/CoverGenerator.cs` (new) | `CoverBox`, `CoverGenerationSettings`, pure `Generate`. |
| `Scripts/Cover/CoverSurface.cs` (new) | Tag component for designated box geometry; builds a `CoverBox`. |
| `Scripts/Cover/CoverDiscovery.cs` (new) | Finds surfaces, applies the NavMesh predicate, calls `CoverRegistry.Rebuild`. |
| `Scripts/Cover/CoverRegistry.cs` | Holds the current locations, `Rebuild`, `Version`, `Changed`. |
| `Scripts/Cover/CoverRules.cs` | `TryChooseNearest` over `CoverLocation`, skipping invalid. |
| `Scripts/Units/UnitCover.cs`, `CommandableUnit.cs`, `Commands/UnitCommands.cs`, `Controls/CommandResolver.cs`, `Controls/PlayerCommandInput.cs`, `AI/EnemyAI.cs`, `DebugUI/CommandQueueView.cs`, `DebugUI/PrototypeHud.cs` | Type change; `IsValid`; hostile Low-only rule. |
| `Scripts/DebugUI/CoverView.cs` | Rebuild markers on registry version, shapes per type, green selected destination. |
| `Tests/...` | See each task. `TestWorld.CreateCoverPoint` keeps its name and now returns `CoverLocation`. |
| `Assets/_Project/Editor/ProceduralCoverSceneBuilder.cs` (temporary) | One-off scene edit; created, run, deleted inside Task 8; never committed. |
| `Docs/Decisions.md`, `Docs/superpowers/specs/2026-10-05-cover-system-design.md` (note only) | Decision 023 and amendments (Task 9). |

---

### Task 1: `CoverLocation` replaces `CoverPoint` (atomic type migration)

The project cannot compile half-migrated, so this task changes every consumer and every test in one commit. The behaviour of the shipped cover system must not change: the whole existing suite must pass afterwards.

**Files:**
- Create: `Assets/_Project/Scripts/Cover/CoverLocation.cs`
- Delete: `Assets/_Project/Scripts/Cover/CoverPoint.cs` and its `.meta`
- Modify: `Scripts/Cover/CoverRegistry.cs`, `Scripts/Cover/CoverRules.cs`, `Scripts/Units/UnitCover.cs`, `Scripts/Units/CommandableUnit.cs`, `Scripts/Commands/UnitCommands.cs`, `Scripts/Controls/CommandResolver.cs`, `Scripts/Controls/PlayerCommandInput.cs`, `Scripts/AI/EnemyAI.cs`, `Scripts/DebugUI/CommandQueueView.cs`, `Scripts/DebugUI/PrototypeHud.cs`, `Scripts/DebugUI/CoverView.cs` (type only here; Task 7 redesigns it)
- Modify tests: `Tests/PlayMode/TestSupport/TestWorld.cs`, `Tests/EditMode/{CommandableUnitTests,CommandResolverTests,EnemyAITests,UnitAttackerTests,UnitCoverTests,CoverRulesTests}.cs`, `Tests/PlayMode/{CommandQueueViewTests,UnitCoverPlayModeTests,CoverOrderPlayModeTests,CoverCombatPlayModeTests,CoverViewTests,PrototypeSceneTests}.cs`
- Rename + rewrite: `Tests/EditMode/CoverPointTests.cs` to `Tests/EditMode/CoverLocationTests.cs`; `Tests/PlayMode/CoverPointPlayModeTests.cs` to `Tests/PlayMode/CoverLocationPlayModeTests.cs`
- Note: `PrototypeSceneTests` and the scene still use the old `CoverPoint` objects until Task 8; see Step 9 for how the scene stays loadable meanwhile.

**Interfaces:**
- Produces (used by every later task):

```csharp
public enum CoverHeight { Low, Tall }
public enum CoverPlacement { Face, Corner }

public sealed class CoverLocation
{
    internal CoverLocation(string name, Vector3 position, Vector3 facing, Collider obstacle,
        float hitChance = 0.5f, CoverHeight height = CoverHeight.Low, CoverPlacement placement = CoverPlacement.Face,
        Vector3 peekDirection = default, Vector3 peekPoint = default);
    public string Name { get; }
    public Vector3 Position { get; }
    public Vector3 Facing { get; }              // flat unit vector from the stand point into the obstacle
    public Collider Obstacle { get; }
    public float HitChance { get; }
    public CoverHeight Height { get; }
    public CoverPlacement Placement { get; }
    public Vector3 PeekDirection { get; }       // zero unless HasPeek
    public Vector3 PeekPoint { get; }           // zero unless HasPeek
    public bool HasPeek { get; }
    public UnitCover Claimant { get; }
    public bool IsClaimed { get; }
    public bool IsClaimedBy(UnitCover unit);
    public bool IsOccupied { get; }
    public bool IsValid { get; }                // not retired, obstacle alive, enabled and active
    public bool ProtectsFrom(Vector3 attackerPivot, Vector3 defenderPivot);
    internal bool TryClaim(UnitCover claimant);
    internal void Release(UnitCover claimant);
    internal void Retire();                     // clears the claimant and makes IsValid false for good
}
```
- `CoverRegistry.Points` (type `IReadOnlyList<CoverLocation>`), `CoverRegistry.Initialize(params CoverLocation[])` (internal, tests), unchanged names.
- `TestWorld.CreateCoverPoint(Vector3 position, Vector3 forward, Collider obstacle, float hitChance = 0.5f, CoverHeight height = CoverHeight.Low)` returns `CoverLocation`; `TestWorld.CreateRegistry(params CoverLocation[])`.

- [ ] **Step 1: Record the baseline**

Run from the repo root (editor closed):

```bash
Tools/run-tests.sh EditMode
Tools/run-tests.sh PlayMode
```

Expected: both end with `passed="N" failed="0"`. Write the two totals at the top of this task's commit message body later and keep them: **every later task compares to these**. (Memory from Phase 6 says 281 and 262 at that time; follow mode added more, so use what you measure.)

- [ ] **Step 2: Write the failing `CoverLocationTests` (rename the old file first)**

```bash
git mv Assets/_Project/Tests/EditMode/CoverPointTests.cs Assets/_Project/Tests/EditMode/CoverLocationTests.cs
git mv Assets/_Project/Tests/EditMode/CoverPointTests.cs.meta Assets/_Project/Tests/EditMode/CoverLocationTests.cs.meta
```

Replace the contents of `CoverLocationTests.cs` with:

```csharp
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CoverLocationTests
    {
        GameObject obstacleHost;
        CoverLocation location;
        readonly List<GameObject> extraHosts = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            obstacleHost = GameObject.CreatePrimitive(PrimitiveType.Cube);
            location = new CoverLocation("Cover", new Vector3(1f, 0f, 2f), new Vector3(3f, 4f, 4f), null);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var extra in extraHosts)
                Object.DestroyImmediate(extra);
            extraHosts.Clear();
            Object.DestroyImmediate(obstacleHost);
        }

        [Test]
        public void Facing_IsFlatAndNormalised()
        {
            Assert.That(location.Facing.y, Is.EqualTo(0f));
            Assert.That(location.Facing.magnitude, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(location.Facing.x, Is.EqualTo(0.6f).Within(1e-4f));
            Assert.That(location.Facing.z, Is.EqualTo(0.8f).Within(1e-4f));
        }

        [Test]
        public void Facing_StraightUpOrDown_FallsBackToWorldForward()
        {
            var straight = new CoverLocation("Up", Vector3.zero, Vector3.up, null);
            Assert.That(straight.Facing, Is.EqualTo(Vector3.forward));
        }

        [Test]
        public void Constructor_StoresTheData_DefaultsAreLowFaceHalf()
        {
            Assert.That(location.Name, Is.EqualTo("Cover"));
            Assert.That(location.Position, Is.EqualTo(new Vector3(1f, 0f, 2f)));
            Assert.That(location.HitChance, Is.EqualTo(0.5f));
            Assert.That(location.Height, Is.EqualTo(CoverHeight.Low));
            Assert.That(location.Placement, Is.EqualTo(CoverPlacement.Face));
            Assert.That(location.Obstacle, Is.Null);
            Assert.That(location.HasPeek, Is.False);
            Assert.That(location.PeekDirection, Is.EqualTo(Vector3.zero));
            Assert.That(location.PeekPoint, Is.EqualTo(Vector3.zero));
            var collider = obstacleHost.GetComponent<Collider>();
            var tuned = new CoverLocation("Tuned", Vector3.zero, Vector3.forward, collider, 0.25f);
            Assert.That(tuned.Obstacle, Is.SameAs(collider));
            Assert.That(tuned.HitChance, Is.EqualTo(0.25f));
        }

        [Test]
        public void Peek_IsKeptOnlyWithADirection_AndTheDirectionIsFlatAndNormalised()
        {
            var corner = new CoverLocation("Corner", Vector3.zero, Vector3.forward, null, 0.5f, CoverHeight.Tall,
                CoverPlacement.Corner, new Vector3(2f, 3f, 0f), new Vector3(1.25f, 0f, 0f));
            Assert.That(corner.HasPeek, Is.True);
            Assert.That(corner.PeekDirection, Is.EqualTo(Vector3.right));
            Assert.That(corner.PeekPoint, Is.EqualTo(new Vector3(1.25f, 0f, 0f)));
            var plain = new CoverLocation("NoPeek", Vector3.zero, Vector3.forward, null, 0.5f, CoverHeight.Tall,
                CoverPlacement.Corner, Vector3.zero, new Vector3(9f, 9f, 9f));
            Assert.That(plain.HasPeek, Is.False);
            Assert.That(plain.PeekPoint, Is.EqualTo(Vector3.zero), "A peek point without a direction is discarded");
        }

        [Test]
        public void HitChance_IsClampedToZeroOne()
        {
            Assert.That(new CoverLocation("A", Vector3.zero, Vector3.forward, null, 2f).HitChance, Is.EqualTo(1f));
            Assert.That(new CoverLocation("B", Vector3.zero, Vector3.forward, null, -1f).HitChance, Is.EqualTo(0f));
        }

        [Test]
        public void ProtectsFrom_WithoutAnObstacle_IsFalse_AndWarnsOnce()
        {
            LogAssert.Expect(LogType.Warning, new Regex("no obstacle"));
            Assert.That(location.ProtectsFrom(new Vector3(0f, 1f, 5f), new Vector3(0f, 1f, 0f)), Is.False);
            Assert.That(location.ProtectsFrom(new Vector3(0f, 1f, 5f), new Vector3(0f, 1f, 0f)), Is.False);
        }

        [Test]
        public void IsValid_TrueWithoutAnObstacle_FalseAfterRetire()
        {
            Assert.That(location.IsValid, Is.True, "A location that never had an obstacle is valid (test points)");
            location.Retire();
            Assert.That(location.IsValid, Is.False);
        }

        [Test]
        public void IsValid_FollowsTheObstacle_DeactivatedDisabledOrDestroyedIsInvalid()
        {
            var collider = obstacleHost.GetComponent<Collider>();
            var wired = new CoverLocation("Wired", Vector3.zero, Vector3.forward, collider);
            Assert.That(wired.IsValid, Is.True);
            collider.enabled = false;
            Assert.That(wired.IsValid, Is.False, "A disabled collider does not protect");
            collider.enabled = true;
            obstacleHost.SetActive(false);
            Assert.That(wired.IsValid, Is.False, "An inactive obstacle does not protect");
            obstacleHost.SetActive(true);
            Assert.That(wired.IsValid, Is.True);
            Object.DestroyImmediate(obstacleHost);
            Assert.That(wired.IsValid, Is.False, "A destroyed obstacle does not protect");
            obstacleHost = GameObject.CreatePrimitive(PrimitiveType.Cube);   // for TearDown
        }

        [Test]
        public void Retire_ClearsTheClaimant_AndTheLocationCannotBeValidAgain()
        {
            var a = NewUnit("A");
            location.TryClaim(a);
            location.Retire();
            Assert.That(location.Claimant, Is.Null);
            Assert.That(location.IsClaimed, Is.False);
            Assert.That(location.IsValid, Is.False);
        }

        UnitCover NewUnit(string name)
        {
            var unitHost = new GameObject(name);
            unitHost.transform.position = location.Position + Vector3.up;
            extraHosts.Add(unitHost);
            return unitHost.AddComponent<UnitCover>();
        }

        [Test]
        public void TryClaim_SucceedsWhenUnclaimedOrOwn_FailsForAnotherUnit()
        {
            var a = NewUnit("A");
            var b = NewUnit("B");
            Assert.That(location.IsClaimed, Is.False);
            Assert.That(location.TryClaim(a), Is.True);
            Assert.That(location.TryClaim(a), Is.True, "Re-claiming an own point is fine");
            Assert.That(location.TryClaim(b), Is.False, "One unit per point");
            Assert.That(location.Claimant, Is.SameAs(a));
            Assert.That(location.IsClaimed, Is.True);
        }

        [Test]
        public void Release_ByTheWrongClaimant_IsANoOp()
        {
            var a = NewUnit("A");
            var b = NewUnit("B");
            location.TryClaim(a);
            location.Release(b);
            Assert.That(location.Claimant, Is.SameAs(a));
            location.Release(a);
            Assert.That(location.Claimant, Is.Null);
            Assert.That(location.IsClaimed, Is.False);
        }

        [Test]
        public void IsClaimedBy_NamesTheClaimant()
        {
            var a = NewUnit("A");
            var b = NewUnit("B");
            Assert.That(location.IsClaimedBy(a), Is.False);
            location.TryClaim(a);
            Assert.That(location.IsClaimedBy(a), Is.True);
            Assert.That(location.IsClaimedBy(b), Is.False);
            Assert.That(location.IsClaimedBy(null), Is.False);
        }

        [Test]
        public void IsOccupied_FollowsTheClaimantsStatus()
        {
            var a = NewUnit("A");   // standing on the point (same x/z), so TryOccupy is within the radius
            Assert.That(a.TryReserve(location), Is.True);
            Assert.That(location.IsOccupied, Is.False, "Reserved is not occupied");
            Assert.That(a.TryOccupy(), Is.True);
            Assert.That(location.IsOccupied, Is.True);
        }

        [Test]
        public void Registry_ListsThePointsItIsGiven()
        {
            var host = new GameObject("Registry");
            extraHosts.Add(host);
            var registry = host.AddComponent<CoverRegistry>();
            Assert.That(registry.Points, Is.Empty);
            registry.Initialize(location);
            Assert.That(registry.Points, Is.EqualTo(new[] { location }));
        }
    }
}
```

- [ ] **Step 3: Run it to verify it fails to compile**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.CoverLocationTests"`
Expected: `error CS0246: The type or namespace name 'CoverLocation' could not be found` (and similar for `CoverHeight`).

- [ ] **Step 4: Create `CoverLocation.cs` and delete `CoverPoint.cs`**

```bash
git rm Assets/_Project/Scripts/Cover/CoverPoint.cs Assets/_Project/Scripts/Cover/CoverPoint.cs.meta
```

Create `Assets/_Project/Scripts/Cover/CoverLocation.cs`:

```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>Whether the obstacle can be shot over (Low, firing cover) or blocks sight (Tall, hiding cover).</summary>
    public enum CoverHeight
    {
        Low,
        Tall,
    }

    /// <summary>Where on the obstacle a location sits: along a face, or at the end of a tall wall (a corner, with peek data).</summary>
    public enum CoverPlacement
    {
        Face,
        Corner,
    }

    /// <summary>
    /// A place a unit can stand to be protected by one obstacle: the single runtime cover representation. Plain data
    /// plus the claim, no scene object, so geometry (the prototype arena today, a mission generator later) produces
    /// it and every consumer treats it the same. Protection is geometric: a ray from the attacker's eye to the
    /// defender's feet must cross the obstacle. UnitCover is the only writer of claims.
    /// </summary>
    public sealed class CoverLocation
    {
        readonly Collider obstacle;
        // A destroyed collider reads as null, same as one never wired; remember which it was.
        readonly bool hadObstacle;
        bool warnedAboutObstacle;
        bool retired;

        internal CoverLocation(string name, Vector3 position, Vector3 facing, Collider obstacle,
            float hitChance = 0.5f, CoverHeight height = CoverHeight.Low, CoverPlacement placement = CoverPlacement.Face,
            Vector3 peekDirection = default, Vector3 peekPoint = default)
        {
            Name = name ?? string.Empty;
            Position = position;
            facing.y = 0f;
            Facing = facing.sqrMagnitude > 0.0001f ? facing.normalized : Vector3.forward;
            this.obstacle = obstacle;
            hadObstacle = obstacle != null;
            HitChance = Mathf.Clamp01(hitChance);
            Height = height;
            Placement = placement;
            peekDirection.y = 0f;
            if (peekDirection.sqrMagnitude > 0.0001f)
            {
                PeekDirection = peekDirection.normalized;
                PeekPoint = peekPoint;
            }
        }

        /// <summary>A debug label, e.g. Cover_LowWall_L_S1. Not a key.</summary>
        public string Name { get; }

        /// <summary>The stand point, at ground level.</summary>
        public Vector3 Position { get; }

        /// <summary>The flat unit direction from the stand point into the obstacle: the protected side is its far side.</summary>
        public Vector3 Facing { get; }

        /// <summary>The one collider that protects this location (the source geometry).</summary>
        public Collider Obstacle => obstacle;

        /// <summary>A protected shot's chance to hit.</summary>
        public float HitChance { get; }

        public CoverHeight Height { get; }

        public CoverPlacement Placement { get; }

        /// <summary>Corner only: the flat unit direction along the wall, out past its end. Zero without a peek.</summary>
        public Vector3 PeekDirection { get; }

        /// <summary>Corner only: where a peeking unit would stand to see round the end. Zero without a peek.</summary>
        public Vector3 PeekPoint { get; }

        public bool HasPeek => PeekDirection != Vector3.zero;

        /// <summary>The unit that reserved or occupies this location, or null. Written only by UnitCover.</summary>
        public UnitCover Claimant { get; private set; }

        public bool IsClaimed => Claimant != null;

        public bool IsClaimedBy(UnitCover unit) => unit != null && Claimant == unit;

        /// <summary>Claimed, and the claimant stands on it.</summary>
        public bool IsOccupied => Claimant != null && Claimant.Status == CoverStatus.Occupied;

        /// <summary>
        /// False once retired, or when the obstacle it was built from is gone, disabled or inactive. A location that
        /// never had an obstacle (test locations) stays valid until retired.
        /// </summary>
        public bool IsValid => !retired
            && (!hadObstacle || (obstacle != null && obstacle.enabled && obstacle.gameObject.activeInHierarchy));

        /// <summary>True when the location is unclaimed or already this unit's. A destroyed claimant counts as none.</summary>
        internal bool TryClaim(UnitCover claimant)
        {
            if (claimant == null)
                throw new System.ArgumentNullException(nameof(claimant));
            if (Claimant != null && Claimant != claimant)
                return false;
            Claimant = claimant;
            return true;
        }

        /// <summary>Frees the location if `claimant` holds it; otherwise nothing happens.</summary>
        internal void Release(UnitCover claimant)
        {
            if (Claimant == claimant)
                Claimant = null;
        }

        /// <summary>Takes the location out of play for good: drops the claim and makes IsValid false. Used by the registry on a rebuild.</summary>
        internal void Retire()
        {
            retired = true;
            Claimant = null;
        }

        /// <summary>
        /// True when the ray from the attacker's eye (pivot + LineOfSight.EyeHeight) to the defender's feet (the
        /// defender's x/z at this location's height) hits the obstacle. Only the wired collider is tested, so units and
        /// other geometry never confuse it. With no obstacle: false, with one warning.
        /// </summary>
        public bool ProtectsFrom(Vector3 attackerPivot, Vector3 defenderPivot)
        {
            if (obstacle == null)
            {
                if (!warnedAboutObstacle)
                {
                    Debug.LogWarning($"{Name} has no obstacle collider, so it protects nobody.");
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
    }
}
```

The `Debug.LogWarning` has no context object (a location is not a `UnityEngine.Object`).

- [ ] **Step 5: Migrate the runtime consumers**

`Scripts/Cover/CoverRegistry.cs` (replace the whole file):

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The scene's current cover locations. Holds state only, like Encounter: clicks, hostile searches, automatic
    /// occupancy and the cover view read it. CoverDiscovery fills it (Rebuild); nothing registers itself.
    /// </summary>
    public sealed class CoverRegistry : MonoBehaviour
    {
        readonly List<CoverLocation> locations = new List<CoverLocation>();

        public IReadOnlyList<CoverLocation> Points => locations;

        /// <summary>Bumped by every Rebuild, so views can tell when to redraw.</summary>
        public int Version { get; private set; }

        /// <summary>Raised after every Rebuild.</summary>
        public event Action Changed;

        /// <summary>
        /// Replaces the current locations. Every old location that is not in `replacement` is retired: its claim is
        /// dropped and it reads as invalid, so units holding it let go on their next update and orders towards it end.
        /// </summary>
        public void Rebuild(IEnumerable<CoverLocation> replacement)
        {
            if (replacement == null)
                throw new ArgumentNullException(nameof(replacement));
            var next = new List<CoverLocation>(replacement);
            var keep = new HashSet<CoverLocation>(next);
            foreach (var old in locations)
            {
                if (!keep.Contains(old))
                    old.Retire();
            }
            locations.Clear();
            locations.AddRange(next);
            Version++;
            Changed?.Invoke();
        }

        internal void Initialize(params CoverLocation[] scenePoints) => Rebuild(scenePoints);
    }
}
```

`Scripts/Cover/CoverRules.cs`: replace both `CoverPoint` with `CoverLocation` in the `TryChooseNearest` signature, update its summary to say "invalid" instead of "inactive in the hierarchy", and replace the skip test:

```csharp
        public static bool TryChooseNearest(IReadOnlyList<CoverLocation> points, Vector3 from, float maxDistance,
            Func<CoverLocation, bool> accept, out CoverLocation chosen)
        {
            ...
                var point = points[i];
                if (point == null || !point.IsValid)
                    continue;
```

`Scripts/Units/UnitCover.cs`:
- `static readonly Func<CoverPoint, bool> isUnclaimed` → `Func<CoverLocation, bool>`; `public CoverPoint Point` → `public CoverLocation Point`; `TryReserve(CoverPoint point)` → `TryReserve(CoverLocation point)`; `static bool IsUnclaimed(CoverPoint point)` → `(CoverLocation point)`; class summary "CoverPoint claims" → "CoverLocation claims".
- In `IsProtectedFrom`: `Status == CoverStatus.Occupied && Point != null && Point.IsValid && Point.ProtectsFrom(attackerPivot, transform.position)`.
- In `Update`: `if (Status != CoverStatus.None && (Point == null || !Point.IsValid))`.

`Scripts/Units/CommandableUnit.cs`:
- `CanTakeCover(CoverPoint point)` → `CanTakeCover(CoverLocation point)` and `point.gameObject.activeInHierarchy` → `point.IsValid`.
- In `UpdateCover`: `if (point == null || !point.IsValid || Cover.Status == CoverStatus.None)` and `{point.name}` → `{point.Name}`.

`Scripts/Commands/UnitCommands.cs`: `MoveToCoverCommand(CoverPoint point)` → `(CoverLocation point)` and `public CoverPoint Point` → `public CoverLocation Point`; comment "cover point" stays.

`Scripts/Controls/CommandResolver.cs`: `CoverPoint cover = null` → `CoverLocation cover = null` in `Resolve`.

`Scripts/Controls/PlayerCommandInput.cs`: `static readonly Func<CoverPoint, bool> acceptAny` → `Func<CoverLocation, bool>`; `CoverPoint cover = null;` → `CoverLocation cover = null;`.

`Scripts/AI/EnemyAI.cs`: `Func<CoverPoint, bool> isUsefulCover` → `Func<CoverLocation, bool>`; `TryFindCover(Health target, out CoverPoint point)` → `out CoverLocation point`; `IsUsefulCover(CoverPoint point)` → `(CoverLocation point)`. (The Low-only rule is Task 6.)

`Scripts/DebugUI/CommandQueueView.cs`: `toCover.Point.gameObject.activeInHierarchy` → `toCover.Point.IsValid`.

`Scripts/DebugUI/PrototypeHud.cs`: `cover.Point.name` → `cover.Point.Name`; also fix the comment above it ("A point destroyed during a pause reads as null..." → "A location retired during a pause reads as invalid until UnitCover releases it on resume: draw nothing"). Behaviour: a retired location should draw nothing, so use `cover.Point != null && cover.Point.IsValid ? cover.Point.Name : null`.

`Scripts/DebugUI/CoverView.cs` (type-only edits, redesigned in Task 6): `ColorFor(CoverPoint point)` → `(CoverLocation point)`; `CreateMarker(CoverPoint point)` → `(CoverLocation point)`; `point.Forward` → `point.Facing` (two places); in `LateUpdate`: `point != null && point.gameObject.activeInHierarchy && ...` → `point != null && point.IsValid && ...`.

- [ ] **Step 6: Migrate the test support and existing tests**

`Tests/PlayMode/TestSupport/TestWorld.cs`: replace `CreateCoverPoint` and `CreateRegistry`:

```csharp
        /// <summary>A cover location at exactly the given stand position, facing `forward`, protected by `obstacle`.</summary>
        public CoverLocation CreateCoverPoint(Vector3 position, Vector3 forward, Collider obstacle, float hitChance = 0.5f,
            CoverHeight height = CoverHeight.Low) =>
            new CoverLocation("TestCover", position, forward, obstacle, hitChance, height);

        /// <summary>A registry on its own object listing the given locations.</summary>
        public CoverRegistry CreateRegistry(params CoverLocation[] points)
        {
            var registry = Track(new GameObject("CoverRegistry")).AddComponent<CoverRegistry>();
            registry.Initialize(points);
            return registry;
        }
```
and fix the `ObstacleCollider` summary ("for wiring a CoverPoint to it" → "for wiring a CoverLocation to it").

Mechanical edits in the other tests (mapping rules, apply to each file below):

| Old | New |
|---|---|
| `CoverPoint` as a type | `CoverLocation` |
| `new GameObject("Cover")` + `AddComponent<CoverPoint>()` (with or without `Initialize`) | `new CoverLocation("Cover", position, Vector3.forward, null)`; drop the host and its `DestroyImmediate` |
| `point.Initialize(null, 0.25f)` | pass `0.25f` as the `hitChance` constructor argument |
| `Object.Destroy(point.gameObject)` (a point vanishing) | `point.Retire()` |
| `host.transform.position = X` on a point host | the `position` constructor argument |

Per file:
- `Tests/EditMode/CommandableUnitTests.cs`: `NewPoint(string name, out GameObject pointHost)` becomes `CoverLocation NewPoint(string name) => new CoverLocation(name, Vector3.zero, Vector3.forward, null);` and the two callers drop `out var pointHost` and the `Object.DestroyImmediate(pointHost)` lines.
- `Tests/EditMode/CommandResolverTests.cs`: both tests build `var point = new CoverLocation("Cover", Vector3.zero, Vector3.forward, null);`; remove `pointHost` and its destroy.
- `Tests/EditMode/EnemyAITests.cs` `DeriveState_Cover_WhileACoverOrderIsCurrent`: `var toCover = new MoveToCoverCommand(new CoverLocation("Cover", Vector3.zero, Vector3.forward, null));` (no host, no destroy).
- `Tests/EditMode/UnitAttackerTests.cs` `IsTargetInCover_IsFalseWhileTheTargetOnlyReservesAPoint`: `var point = new CoverLocation("Cover", new Vector3(0f, 0f, 4f), Vector3.forward, null, 0.25f);` (no host, no destroy).
- `Tests/EditMode/UnitCoverTests.cs`: remove `pointHostA/pointHostB` fields and their destroy calls; `pointA = new CoverLocation("A", new Vector3(0f, 0f, 0f), Vector3.forward, null); pointB = new CoverLocation("B", new Vector3(5f, 0f, 0f), Vector3.forward, null);`. Any test in this file that moves or deactivates a point host (search `pointHost`, `SetActive`) instead calls `pointA.Retire()` for the "became invalid" case; read each such test and keep its intent.
- `Tests/EditMode/CoverRulesTests.cs`: `Point(float x, float z)` becomes `CoverLocation Point(float x, float z) => new CoverLocation($"Cover ({x}, {z})", new Vector3(x, 0f, z), Vector3.forward, null);` (drop `hosts`, keep the TearDown loop only if still needed; remove it). In `TryChooseNearest_SkipsNullAndDestroyedEntries_WithoutCallingAccept` replace the destroyed/inactive setup with: `var retired = Point(0f, 1f); retired.Retire(); var alsoRetired = Point(0f, 2f); alsoRetired.Retire();` and the array `new[] { null, retired, alsoRetired, live }`; rename the test `..._SkipsNullAndRetiredEntries_...`; `new CoverPoint[0]` → `new CoverLocation[0]`; `List<CoverPoint>` → `List<CoverLocation>`.
- `Tests/PlayMode/CommandQueueViewTests.cs`, `CoverCombatPlayModeTests.cs`, `CoverOrderPlayModeTests.cs`, `UnitCoverPlayModeTests.cs`: change `CoverPoint` types and replace each `Object.Destroy(point.gameObject);` with `point.Retire();`. In `CoverCombatPlayModeTests.ATallWall_StillBlocksTheShotEntirely` the old point is retired and a fresh one is created and registered via `registry.Initialize(point)`, which now retires the old one itself because `Rebuild` retires locations missing from the replacement; keep the explicit `point.Retire()` out and let `Initialize` do it (delete the `Object.Destroy(point.gameObject);` line there).
- `Tests/PlayMode/EnemyAIPlayModeTests.cs`, `CompanionAIPlayModeTests.cs`, `AutoRetaliatePlayModeTests.cs`, `CombatPausePlayModeTests.cs`, `PlayerCommandInputTests.cs`: change `CoverPoint` in tuples/fields to `CoverLocation` only.
- `Tests/PlayMode/CoverViewTests.cs`: change the two `CoverPoint` fields to `CoverLocation`. Nothing else yet (Task 7).
- `Tests/PlayMode/PrototypeSceneTests.cs`: leave for Task 8, **but** it must still compile: `registry.Points, Is.EquivalentTo(pointsRoot.GetComponentsInChildren<CoverPoint>())` and `GetComponent<CoverPoint>()` no longer compile. Comment out only those two assertions' bodies with `// Task 8 rewrites this once the scene is migrated` and make the Task 8 `[Ignore("Rewritten in Task 8")]` attributes explicit on the two tests that use them (`Scene_HasTheFriendlyAndHostileSetup`-style wiring test and `PausedClickNearACoverMarker_InScene_...`). Record the two test names you ignored in your report.

Rename and rewrite `Tests/PlayMode/CoverPointPlayModeTests.cs` to `CoverLocationPlayModeTests.cs`:

```bash
git mv Assets/_Project/Tests/PlayMode/CoverPointPlayModeTests.cs Assets/_Project/Tests/PlayMode/CoverLocationPlayModeTests.cs
git mv Assets/_Project/Tests/PlayMode/CoverPointPlayModeTests.cs.meta Assets/_Project/Tests/PlayMode/CoverLocationPlayModeTests.cs.meta
```
Change the class name to `CoverLocationPlayModeTests`; the four existing tests only need `point` to be a `CoverLocation` (they already call `ProtectsFrom` and `Obstacle`, which exist). Keep them verbatim otherwise.

- [ ] **Step 7: Run both suites**

```bash
Tools/run-tests.sh EditMode
Tools/run-tests.sh PlayMode
```
Expected: no `error CS`; EditMode passed = baseline EditMode (`CoverPointTests` had 8 tests; `CoverLocationTests` has more, so the total is higher than baseline by the difference: count it and report it). PlayMode passed = baseline minus the two ignored scene tests. `failed="0"`.

- [ ] **Step 8: Grep for leftovers**

Run: `grep -rn "CoverPoint" Assets/_Project --include=*.cs | grep -v "CreateCoverPoint\|LooseCover\|CoverPoints\|coverPoint\b"` 
Expected: only comments you deliberately kept (none preferred). Fix any remaining type reference.

- [ ] **Step 9: Commit**

```bash
git add -A Assets/_Project
git commit -m "$(cat <<'EOF'
Replace the CoverPoint component with the plain CoverLocation

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>
EOF
)"
```

The scene still holds the old `CoverPoints` objects and a serialized `points` list on `CoverRegistry`; with the MonoBehaviour gone they deserialize as missing scripts and the registry's list is dropped, so the scene has **no cover** until Task 8. Scene tests that need cover were ignored in Step 6; the rest of the scene tests must pass. (If a Missing Script warning appears in the PlayMode log from the old `CoverPoint` objects, that is expected until Task 8; it must not fail a test. If it does, report it rather than patching around it.)

---

### Task 2: Generator core (face locations)

**Files:**
- Create: `Assets/_Project/Scripts/Cover/CoverGenerator.cs`
- Test: `Assets/_Project/Tests/EditMode/CoverGeneratorTests.cs`

**Interfaces:**
- Consumes: `CoverLocation` constructor, `CoverHeight`, `CoverPlacement` (Task 1); `CoverRules.FlatDistance(Vector3, Vector3)`.
- Produces:

```csharp
[Serializable] public sealed class CoverGenerationSettings { public float standOffset = 0.75f; ... }   // fields in Global Constraints
public readonly struct CoverBox
{
    public CoverBox(string name, Vector3 center, float yawDegrees, Vector3 halfExtents, Collider collider);
    public string Name; public Vector3 Center; public float YawDegrees; public Vector3 HalfExtents; public Collider Collider;
    public float Height { get; }    // 2 * HalfExtents.y
    public float GroundY { get; }   // Center.y - HalfExtents.y
}
public static class CoverGenerator
{
    public static List<CoverLocation> Generate(IReadOnlyList<CoverBox> boxes, CoverGenerationSettings settings, Func<Vector3, bool> isWalkable);
}
```

- [ ] **Step 1: Write the failing face-location tests**

Create `Assets/_Project/Tests/EditMode/CoverGeneratorTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class CoverGeneratorTests
    {
        static readonly Func<Vector3, bool> Everywhere = _ => true;

        static CoverBox Box(string name, Vector3 center, Vector3 size, float yaw = 0f) =>
            new CoverBox(name, center, yaw, size * 0.5f, null);

        static List<CoverLocation> Generate(CoverBox box, Func<Vector3, bool> walkable = null, CoverGenerationSettings settings = null) =>
            CoverGenerator.Generate(new[] { box }, settings ?? new CoverGenerationSettings(), walkable ?? Everywhere);

        static Vector3 P(float x, float y, float z) => new Vector3(x, y, z);

        [Test]
        public void ThreeMetreLowWall_GivesTwoPointsOnEachLongFace_AndNoneOnItsEnds()
        {
            // LowWall_L of the prototype arena: x 5..8, z -3.25..-2.75, 0.9 m tall.
            var result = Generate(Box("LowWall_L", P(6.5f, 0.45f, -3f), P(3f, 0.9f, 0.5f)));

            Assert.That(result, Has.Count.EqualTo(4));
            Assert.That(result.Select(l => l.Name), Is.EquivalentTo(new[]
                { "Cover_LowWall_L_N1", "Cover_LowWall_L_N2", "Cover_LowWall_L_S1", "Cover_LowWall_L_S2" }));
            var s1 = result.Single(l => l.Name == "Cover_LowWall_L_S1");
            var s2 = result.Single(l => l.Name == "Cover_LowWall_L_S2");
            Assert.That(s1.Position.x, Is.EqualTo(5.5f).Within(0.001f));
            Assert.That(s1.Position.y, Is.EqualTo(0f).Within(0.001f));
            Assert.That(s1.Position.z, Is.EqualTo(-4f).Within(0.001f), "0.75 m south of the south face");
            Assert.That(s2.Position.x, Is.EqualTo(7.5f).Within(0.001f));
            Assert.That(s1.Facing.z, Is.EqualTo(1f).Within(0.001f), "South points face north, into the wall");
            var n1 = result.Single(l => l.Name == "Cover_LowWall_L_N1");
            Assert.That(n1.Position.z, Is.EqualTo(-2f).Within(0.001f));
            Assert.That(n1.Facing.z, Is.EqualTo(-1f).Within(0.001f));
            Assert.That(result.All(l => l.Height == CoverHeight.Low && l.Placement == CoverPlacement.Face && !l.HasPeek), Is.True);
        }

        [Test]
        public void FaceLength_DecidesHowManyPointsAFaceGets()
        {
            Assert.That(Generate(Box("Short", P(0f, 0.45f, 0f), P(2f, 0.9f, 0.5f))), Has.Count.EqualTo(2), "a 2 m face: one point each");
            Assert.That(Generate(Box("Four", P(0f, 0.45f, 0f), P(4f, 0.9f, 0.5f))), Has.Count.EqualTo(4), "a 4 m face: two each");
            Assert.That(Generate(Box("Six", P(0f, 0.45f, 0f), P(6f, 0.9f, 0.5f))), Has.Count.EqualTo(6), "a 6 m face: three each");
        }

        [Test]
        public void FacesShorterThanMinFaceLength_GetNoPoints()
        {
            var result = Generate(Box("Thin", P(0f, 0.45f, 0f), P(0.8f, 0.9f, 0.5f)));
            Assert.That(result, Is.Empty, "0.8 m by 0.5 m: every face is shorter than 1 m");
        }

        [Test]
        public void Pillar_GetsOnePointPerFace_AllTall_NoneWithPeek()
        {
            var result = Generate(Box("Pillar_G", P(-2f, 1.5f, -6f), P(1.5f, 3f, 1.5f)));
            Assert.That(result, Has.Count.EqualTo(4));
            Assert.That(result.All(l => l.Height == CoverHeight.Tall && l.Placement == CoverPlacement.Face && !l.HasPeek), Is.True);
            var south = result.Single(l => l.Name == "Cover_Pillar_G_S1");
            Assert.That(south.Position.x, Is.EqualTo(-2f).Within(0.001f));
            Assert.That(south.Position.z, Is.EqualTo(-7.5f).Within(0.001f), "0.75 m from the face at -6.75");
            var west = result.Single(l => l.Name == "Cover_Pillar_G_W1");
            Assert.That(west.Position.x, Is.EqualTo(-3.5f).Within(0.001f));
            Assert.That(west.Facing.x, Is.EqualTo(1f).Within(0.001f));
        }

        [Test]
        public void Crate_TwoByTwo_IsNotAWall_FourFacePointsNoCorners()
        {
            var result = Generate(Box("Crate_J", P(10f, 1f, -5f), P(2f, 2f, 2f)));
            Assert.That(result, Has.Count.EqualTo(4));
            Assert.That(result.Any(l => l.Placement == CoverPlacement.Corner), Is.False);
        }

        [Test]
        public void HeightThreshold_DecidesLowOrTall()
        {
            Assert.That(Generate(Box("A", P(0f, 0.6f, 0f), P(2f, 1.2f, 0.5f)))[0].Height, Is.EqualTo(CoverHeight.Low), "1.2 m is Low");
            Assert.That(Generate(Box("B", P(0f, 0.65f, 0f), P(2f, 1.3f, 0.5f)))[0].Height, Is.EqualTo(CoverHeight.Tall), "1.3 m is Tall");
        }

        [Test]
        public void NonWalkableCandidates_AreDropped()
        {
            var result = Generate(Box("Wall", P(0f, 0.45f, 0f), P(3f, 0.9f, 0.5f)), walkable: p => p.z < 0f);
            Assert.That(result, Has.Count.EqualTo(2));
            Assert.That(result.All(l => l.Position.z < 0f), Is.True);
        }

        [Test]
        public void DuplicateSurfaces_DoNotStackPoints()
        {
            var one = Box("A", P(0f, 0.45f, 0f), P(3f, 0.9f, 0.5f));
            var two = Box("B", P(0f, 0.45f, 0f), P(3f, 0.9f, 0.5f));
            var result = CoverGenerator.Generate(new[] { one, two }, new CoverGenerationSettings(), Everywhere);
            Assert.That(result, Has.Count.EqualTo(4), "The second surface's points all fall inside the merge distance of the first's");
            Assert.That(result.All(l => l.Name.StartsWith("Cover_A_")), Is.True);
        }

        [Test]
        public void HitChance_IsCopiedFromTheSettings()
        {
            var settings = new CoverGenerationSettings { hitChance = 0.3f };
            var result = Generate(Box("Wall", P(0f, 0.45f, 0f), P(3f, 0.9f, 0.5f)), settings: settings);
            Assert.That(result.All(l => Mathf.Approximately(l.HitChance, 0.3f)), Is.True);
        }

        [Test]
        public void Generate_RejectsNullArguments()
        {
            var boxes = new CoverBox[0];
            Assert.Throws<ArgumentNullException>(() => CoverGenerator.Generate(null, new CoverGenerationSettings(), Everywhere));
            Assert.Throws<ArgumentNullException>(() => CoverGenerator.Generate(boxes, null, Everywhere));
            Assert.Throws<ArgumentNullException>(() => CoverGenerator.Generate(boxes, new CoverGenerationSettings(), null));
        }

        [Test]
        public void Generate_IsDeterministic()
        {
            var box = Box("Wall", P(0f, 1f, 0f), P(8f, 2f, 1f), 30f);
            var first = Generate(box).Select(l => (l.Name, l.Position)).ToList();
            var second = Generate(box).Select(l => (l.Name, l.Position)).ToList();
            Assert.That(second, Is.EqualTo(first));
        }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.CoverGeneratorTests"`
Expected: `error CS0246: ... 'CoverBox' could not be found`.

- [ ] **Step 3: Implement the settings, the box and the face generation**

Create `Assets/_Project/Scripts/Cover/CoverGenerator.cs`:

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>Plain tuning data for cover discovery. Defaults are the values the prototype arena was tuned with.</summary>
    [Serializable]
    public sealed class CoverGenerationSettings
    {
        /// <summary>Stand point distance from a face (agent radius 0.5 m plus margin).</summary>
        public float standOffset = 0.75f;
        /// <summary>Target gap between face points along a face.</summary>
        public float spacing = 2f;
        /// <summary>Keep face points this far from a face's ends.</summary>
        public float endMargin = 0.5f;
        /// <summary>Faces shorter than this get no points (a wall's thin ends).</summary>
        public float minFaceLength = 1f;
        /// <summary>A box whose top is at or below this is Low cover, else Tall.</summary>
        public float lowMaxHeight = 1.2f;
        /// <summary>A Tall box gets corner cover only if its horizontal length is at least this and twice its thickness.</summary>
        public float minCornerLength = 2f;
        /// <summary>How far inside the wall end a corner stands, so it stays in the wall's shadow.</summary>
        public float cornerInset = 0.35f;
        /// <summary>Stand point to peek point.</summary>
        public float peekDistance = 1.25f;
        /// <summary>A location this close (flat) to an earlier accepted one is dropped.</summary>
        public float mergeDistance = 1f;
        /// <summary>Copied to every generated location.</summary>
        public float hitChance = 0.5f;
        /// <summary>NavMesh.SamplePosition radius CoverDiscovery uses to decide a point is walkable.</summary>
        public float walkableTolerance = 0.25f;
    }

    /// <summary>An upright box, any yaw: the only geometry the generator understands.</summary>
    public readonly struct CoverBox
    {
        public CoverBox(string name, Vector3 center, float yawDegrees, Vector3 halfExtents, Collider collider)
        {
            Name = name;
            Center = center;
            YawDegrees = yawDegrees;
            HalfExtents = halfExtents;
            Collider = collider;
        }

        public string Name { get; }
        public Vector3 Center { get; }
        public float YawDegrees { get; }
        /// <summary>Half sizes along the box's own x, y and z (z is "forward" at yaw 0).</summary>
        public Vector3 HalfExtents { get; }
        /// <summary>The collider that will protect locations built from this box. May be null in pure tests.</summary>
        public Collider Collider { get; }

        public float Height => HalfExtents.y * 2f;
        public float GroundY => Center.y - HalfExtents.y;
    }

    /// <summary>
    /// Turns boxes into cover locations. Pure: no scene access, the NavMesh arrives as a predicate. Per box, corner
    /// candidates come first (so they win a merge), then face candidates; candidates that are not walkable or lie
    /// within mergeDistance of an earlier accepted location are dropped; the survivors are named and returned.
    /// </summary>
    public static class CoverGenerator
    {
        struct Candidate
        {
            public Vector3 Position;
            public Vector3 Facing;
            public CoverPlacement Placement;
            public Vector3 PeekDirection;
            public Vector3 PeekPoint;
        }

        // A face of the box: outward normal, direction along the face, half the face's length, and the distance from
        // the box centre to the face plane.
        readonly struct Face
        {
            public Face(Vector3 normal, Vector3 tangent, float halfLength, float depth)
            {
                Normal = normal;
                Tangent = tangent;
                HalfLength = halfLength;
                Depth = depth;
            }

            public Vector3 Normal { get; }
            public Vector3 Tangent { get; }
            public float HalfLength { get; }
            public float Depth { get; }
        }

        public static List<CoverLocation> Generate(IReadOnlyList<CoverBox> boxes, CoverGenerationSettings settings,
            Func<Vector3, bool> isWalkable)
        {
            if (boxes == null)
                throw new ArgumentNullException(nameof(boxes));
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            if (isWalkable == null)
                throw new ArgumentNullException(nameof(isWalkable));

            var result = new List<CoverLocation>();
            foreach (var box in boxes)
            {
                var candidates = new List<Candidate>();
                AddFaces(box, settings, candidates);

                var accepted = new List<Candidate>();
                foreach (var candidate in candidates)
                {
                    if (!isWalkable(candidate.Position) || IsNearAny(candidate.Position, result, accepted, settings.mergeDistance))
                        continue;
                    accepted.Add(candidate);
                }
                AddNamed(box, settings, accepted, result);
            }
            return result;
        }

        static Face[] FacesOf(CoverBox box)
        {
            var rotation = Quaternion.Euler(0f, box.YawDegrees, 0f);
            var right = rotation * Vector3.right;
            var forward = rotation * Vector3.forward;
            var hx = box.HalfExtents.x;
            var hz = box.HalfExtents.z;
            return new[]
            {
                new Face(right, forward, hz, hx),
                new Face(-right, forward, hz, hx),
                new Face(forward, right, hx, hz),
                new Face(-forward, right, hx, hz),
            };
        }

        static Vector3 OnGround(CoverBox box, Vector3 point) => new Vector3(point.x, box.GroundY, point.z);

        static void AddFaces(CoverBox box, CoverGenerationSettings settings, List<Candidate> candidates)
        {
            foreach (var face in FacesOf(box))
            {
                var length = face.HalfLength * 2f;
                if (length < settings.minFaceLength)
                    continue;
                var usable = length - 2f * settings.endMargin;
                var count = usable <= 0f ? 1 : Mathf.FloorToInt(usable / settings.spacing + 1e-4f) + 1;
                for (var i = 0; i < count; i++)
                {
                    var along = count == 1 ? 0f : -usable * 0.5f + i * usable / (count - 1);
                    var point = box.Center + face.Normal * (face.Depth + settings.standOffset) + face.Tangent * along;
                    candidates.Add(new Candidate
                    {
                        Position = OnGround(box, point),
                        Facing = -face.Normal,
                        Placement = CoverPlacement.Face,
                    });
                }
            }
        }

        static bool IsNearAny(Vector3 position, List<CoverLocation> locations, List<Candidate> candidates, float distance)
        {
            foreach (var location in locations)
            {
                if (CoverRules.FlatDistance(position, location.Position) < distance)
                    return true;
            }
            foreach (var candidate in candidates)
            {
                if (CoverRules.FlatDistance(position, candidate.Position) < distance)
                    return true;
            }
            return false;
        }

        // The compass side a location stands on, from the outward normal (-Facing). Ties go to the z axis.
        static string Label(Vector3 outward)
        {
            if (Mathf.Abs(outward.x) > Mathf.Abs(outward.z) + 1e-4f)
                return outward.x > 0f ? "E" : "W";
            return outward.z >= 0f ? "N" : "S";
        }

        // Numbering runs along world +x on N/S sides and +z on E/W sides.
        static (float primary, float secondary) AlongKey(string label, Vector3 position) =>
            label == "N" || label == "S" ? (position.x, position.z) : (position.z, position.x);

        static int Compare(Candidate a, Candidate b)
        {
            var byPlacement = a.Placement.CompareTo(b.Placement);
            if (byPlacement != 0)
                return byPlacement;
            var la = Label(-a.Facing);
            var lb = Label(-b.Facing);
            var byLabel = string.CompareOrdinal(la, lb);
            if (byLabel != 0)
                return byLabel;
            var ka = AlongKey(la, a.Position);
            var kb = AlongKey(lb, b.Position);
            var byPrimary = ka.primary.CompareTo(kb.primary);
            return byPrimary != 0 ? byPrimary : ka.secondary.CompareTo(kb.secondary);
        }

        static void AddNamed(CoverBox box, CoverGenerationSettings settings, List<Candidate> accepted, List<CoverLocation> result)
        {
            var height = box.Height <= settings.lowMaxHeight ? CoverHeight.Low : CoverHeight.Tall;
            accepted.Sort(Compare);
            var counts = new Dictionary<string, int>();
            foreach (var candidate in accepted)
            {
                var label = Label(-candidate.Facing);
                var kind = candidate.Placement == CoverPlacement.Corner ? "Corner" : string.Empty;
                var key = label + kind;
                counts.TryGetValue(key, out var number);
                number++;
                counts[key] = number;
                result.Add(new CoverLocation($"Cover_{box.Name}_{key}{number}", candidate.Position, candidate.Facing,
                    box.Collider, settings.hitChance, height, candidate.Placement, candidate.PeekDirection, candidate.PeekPoint));
            }
        }
    }
}
```

(`AddCorners` arrives in Task 3, hence `Generate` only calls `AddFaces` now.)

- [ ] **Step 4: Run to verify it passes**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.CoverGeneratorTests"`
Expected: all tests in the class pass (10), no `error CS`. If `Generate_IsDeterministic`'s tuple `Is.EqualTo` complains about `Vector3` equality, compare `l.Position.ToString("F3")` strings instead.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/Cover/CoverGenerator.cs Assets/_Project/Scripts/Cover/CoverGenerator.cs.meta Assets/_Project/Tests/EditMode/CoverGeneratorTests.cs Assets/_Project/Tests/EditMode/CoverGeneratorTests.cs.meta
git commit -m "$(cat <<'EOF'
Add the cover generator: box faces to cover locations

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: Tall-wall corner locations with peek data

**Files:**
- Modify: `Assets/_Project/Scripts/Cover/CoverGenerator.cs`
- Test: `Assets/_Project/Tests/EditMode/CoverGeneratorTests.cs`

**Interfaces:**
- Consumes: Task 2's `Candidate`, `Face`, `FacesOf`, `OnGround`, `Generate`.
- Produces: corner candidates (`Placement == Corner`, `PeekDirection`/`PeekPoint` set only when the peek point is walkable) inside `Generate`.

- [ ] **Step 1: Write the failing corner tests (append to `CoverGeneratorTests`)**

```csharp
        // Barrier_I of the arena: 6 x 1, 2 m tall, centred (-8, 1, 2).
        static CoverBox Barrier() => Box("Barrier_I", P(-8f, 1f, 2f), P(6f, 2f, 1f));

        [Test]
        public void TallWall_GivesFourCorners_OneAtEachEndOfEachLongFace()
        {
            var result = Generate(Barrier());
            var corners = result.Where(l => l.Placement == CoverPlacement.Corner).ToList();
            Assert.That(corners, Has.Count.EqualTo(4));
            Assert.That(corners.All(l => l.Height == CoverHeight.Tall && l.HasPeek), Is.True);
            Assert.That(corners.Select(l => l.Name), Is.EquivalentTo(new[]
                { "Cover_Barrier_I_NCorner1", "Cover_Barrier_I_NCorner2", "Cover_Barrier_I_SCorner1", "Cover_Barrier_I_SCorner2" }));
        }

        [Test]
        public void Corner_StandsInTheWallsShadow_AndPeeksOutPastTheEnd()
        {
            var south = Generate(Barrier()).Single(l => l.Name == "Cover_Barrier_I_SCorner2");   // the east end of the south face
            Assert.That(south.Position.x, Is.EqualTo(-5.35f).Within(0.001f), "0.35 m inside the wall end at x -5");
            Assert.That(south.Position.z, Is.EqualTo(0.75f).Within(0.001f), "0.75 m south of the south face at z 1.5");
            Assert.That(south.Facing.z, Is.EqualTo(1f).Within(0.001f), "Faces the wall");
            Assert.That(south.PeekDirection.x, Is.EqualTo(1f).Within(0.001f), "Peeks east, out past the end");
            Assert.That(south.PeekPoint.x, Is.EqualTo(-4.1f).Within(0.001f), "1.25 m from the stand point");
            Assert.That(south.PeekPoint.z, Is.EqualTo(0.75f).Within(0.001f));
            var west = Generate(Barrier()).Single(l => l.Name == "Cover_Barrier_I_SCorner1");
            Assert.That(west.Position.x, Is.EqualTo(-10.65f).Within(0.001f));
            Assert.That(west.PeekDirection.x, Is.EqualTo(-1f).Within(0.001f));
        }

        [Test]
        public void ACorner_SupersedesTheFacePointNearestTheWallEnd()
        {
            var result = Generate(Barrier());
            var southFace = result.Where(l => l.Facing.z > 0.5f).ToList();
            Assert.That(southFace.Count(l => l.Placement == CoverPlacement.Face), Is.EqualTo(1), "Only the centre face point survives");
            Assert.That(southFace.Single(l => l.Placement == CoverPlacement.Face).Position.x, Is.EqualTo(-8f).Within(0.001f));
            Assert.That(result, Has.Count.EqualTo(8), "4 corners + 2 centre points + 2 end-face points");
        }

        [Test]
        public void LowWalls_NeverGetCorners()
        {
            var result = Generate(Box("Low", P(0f, 0.45f, 0f), P(8f, 0.9f, 1f)));
            Assert.That(result.Any(l => l.Placement == CoverPlacement.Corner), Is.False);
        }

        [Test]
        public void ATallBoxThatIsNotMuchLongerThanItIsThick_GetsNoCorners()
        {
            Assert.That(Generate(Box("Stubby", P(0f, 1f, 0f), P(3f, 2f, 2f))).Any(l => l.Placement == CoverPlacement.Corner), Is.False, "3 x 2 is not twice as long as thick");
            Assert.That(Generate(Box("Short", P(0f, 1f, 0f), P(1.5f, 2f, 0.5f))).Any(l => l.Placement == CoverPlacement.Corner), Is.False, "1.5 m is under minCornerLength");
        }

        [Test]
        public void AWallTurnedFortyFiveDegrees_GivesTheSameCounts_AndUnitDirections()
        {
            var straight = Generate(Box("Wall", P(0f, 1f, 0f), P(8f, 2f, 1f), 0f));
            var turned = Generate(Box("Wall", P(0f, 1f, 0f), P(8f, 2f, 1f), 45f));
            Assert.That(turned, Has.Count.EqualTo(straight.Count));
            Assert.That(turned.Count(l => l.Placement == CoverPlacement.Corner), Is.EqualTo(4));
            foreach (var corner in turned.Where(l => l.Placement == CoverPlacement.Corner))
            {
                Assert.That(corner.Facing.magnitude, Is.EqualTo(1f).Within(1e-4f));
                Assert.That(corner.PeekDirection.magnitude, Is.EqualTo(1f).Within(1e-4f));
                Assert.That(Vector3.Dot(corner.Facing, corner.PeekDirection), Is.EqualTo(0f).Within(1e-4f), "Peek runs along the wall, not through it");
            }
        }

        [Test]
        public void ACornerWhosePeekPointIsOffTheNavMesh_StaysACornerWithoutPeekData()
        {
            // Reject only the south side's peek points (z 0.75, more than 3.5 m from the wall centre along x).
            Func<Vector3, bool> walkable = p => !(Mathf.Abs(p.x + 8f) > 3.5f && Mathf.Abs(p.z - 0.75f) < 0.1f);
            var result = Generate(Barrier(), walkable);

            var southCorners = result.Where(l => l.Placement == CoverPlacement.Corner && l.Facing.z > 0.5f).ToList();
            Assert.That(southCorners, Has.Count.EqualTo(2), "The corners themselves are still walkable");
            Assert.That(southCorners.All(l => !l.HasPeek && l.PeekPoint == Vector3.zero), Is.True);
            var northCorners = result.Where(l => l.Placement == CoverPlacement.Corner && l.Facing.z < -0.5f).ToList();
            Assert.That(northCorners, Has.Count.EqualTo(2));
            Assert.That(northCorners.All(l => l.HasPeek), Is.True);
        }

        [Test]
        public void ANonWalkableCornerStandPoint_DropsTheCorner()
        {
            var result = Generate(Barrier(), p => p.x > -9f);   // only the east half is walkable
            Assert.That(result.Count(l => l.Placement == CoverPlacement.Corner), Is.EqualTo(2), "Only the two east corners remain");
        }
```

- [ ] **Step 2: Run to verify they fail**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.CoverGeneratorTests"`
Expected: the new tests FAIL (no corners are produced: `Expected: 4 But was: 0` and similar); Task 2's tests still pass.

- [ ] **Step 3: Implement `AddCorners` and call it first**

In `CoverGenerator.cs`, change `Generate` so corners are added **before** faces and receive the walkability predicate:

```csharp
                var candidates = new List<Candidate>();
                AddCorners(box, settings, isWalkable, candidates);
                AddFaces(box, settings, candidates);
```

Add the method next to `AddFaces`:

```csharp
        // Tall walls only: one corner at each end of each long face, inset into the wall's shadow, peeking out past the end.
        static void AddCorners(CoverBox box, CoverGenerationSettings settings, Func<Vector3, bool> isWalkable, List<Candidate> candidates)
        {
            if (box.Height <= settings.lowMaxHeight)
                return;
            var longHalf = Mathf.Max(box.HalfExtents.x, box.HalfExtents.z);
            var shortHalf = Mathf.Min(box.HalfExtents.x, box.HalfExtents.z);
            var length = longHalf * 2f;
            if (length < settings.minCornerLength || length < shortHalf * 4f)
                return;
            foreach (var face in FacesOf(box))
            {
                // The long faces are the ones that run the whole length of the box.
                if (!Mathf.Approximately(face.HalfLength, longHalf))
                    continue;
                for (var end = 0; end < 2; end++)
                {
                    var along = face.Tangent * (end == 0 ? 1f : -1f);
                    var stand = OnGround(box, box.Center + face.Normal * (face.Depth + settings.standOffset)
                        + along * (face.HalfLength - settings.cornerInset));
                    var peek = stand + along * settings.peekDistance;
                    var hasPeek = isWalkable(peek);
                    candidates.Add(new Candidate
                    {
                        Position = stand,
                        Facing = -face.Normal,
                        Placement = CoverPlacement.Corner,
                        PeekDirection = hasPeek ? along : Vector3.zero,
                        PeekPoint = hasPeek ? peek : Vector3.zero,
                    });
                }
            }
        }
```

(`shortHalf * 4f` is "length at least twice the thickness": thickness = 2 * shortHalf.)

- [ ] **Step 4: Run to verify they pass**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.CoverGeneratorTests"`
Expected: all pass (Task 2's and Task 3's). If `ACorner_Supersedes...` reports 9 or 7 total, re-derive from the geometry (centre face point at x -8; end faces at x -4.25 and -11.75, each 1.66 m from the nearest corner) before touching the code; the numbers in the test are derived, not guessed.

- [ ] **Step 5: Commit**

```bash
git add -A Assets/_Project/Scripts/Cover/CoverGenerator.cs Assets/_Project/Tests/EditMode/CoverGeneratorTests.cs
git commit -m "$(cat <<'EOF'
Generate tall-wall corner cover with peek data

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: `CoverSurface`, `CoverDiscovery` and registry rebuild semantics

**Files:**
- Create: `Assets/_Project/Scripts/Cover/CoverSurface.cs`, `Assets/_Project/Scripts/Cover/CoverDiscovery.cs`
- Test: `Assets/_Project/Tests/EditMode/CoverRegistryTests.cs` (EditMode, registry semantics), `Assets/_Project/Tests/PlayMode/CoverDiscoveryPlayModeTests.cs` (discovery from real boxes)

**Interfaces:**
- Consumes: `CoverGenerator.Generate`, `CoverBox`, `CoverGenerationSettings` (Tasks 2 and 3); `CoverRegistry.Rebuild` (Task 1).
- Produces:

```csharp
[RequireComponent(typeof(BoxCollider))]
public sealed class CoverSurface : MonoBehaviour { public CoverBox ToBox(); }

public sealed class CoverDiscovery : MonoBehaviour
{
    public int Discover();                                   // scan, generate, rebuild; returns the new location count
    public CoverGenerationSettings Settings { get; }
    internal void Initialize(CoverRegistry registry, bool discoverAtStart = true, CoverGenerationSettings settings = null);
}
```
(Task 5/7/8 call `Discover()` and `Initialize`.)

- [ ] **Step 1: Write the failing registry tests**

Create `Assets/_Project/Tests/EditMode/CoverRegistryTests.cs`:

```csharp
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class CoverRegistryTests
    {
        readonly List<GameObject> hosts = new List<GameObject>();
        CoverRegistry registry;

        [SetUp]
        public void SetUp() => registry = Host("Registry").AddComponent<CoverRegistry>();

        [TearDown]
        public void TearDown()
        {
            foreach (var host in hosts)
                Object.DestroyImmediate(host);
            hosts.Clear();
        }

        GameObject Host(string name)
        {
            var host = new GameObject(name);
            hosts.Add(host);
            return host;
        }

        static CoverLocation At(float x) => new CoverLocation($"C{x}", new Vector3(x, 0f, 0f), Vector3.forward, null);

        [Test]
        public void Rebuild_ReplacesTheList_BumpsTheVersion_AndRaisesChanged()
        {
            var raised = 0;
            registry.Changed += () => raised++;
            var versionBefore = registry.Version;
            var a = At(0f);
            registry.Rebuild(new[] { a });
            Assert.That(registry.Points, Is.EqualTo(new[] { a }));
            Assert.That(registry.Version, Is.EqualTo(versionBefore + 1));
            Assert.That(raised, Is.EqualTo(1));
        }

        [Test]
        public void Rebuild_RetiresLocationsThatAreNotInTheReplacement_AndDropsTheirClaims()
        {
            var kept = At(0f);
            var dropped = At(5f);
            registry.Rebuild(new[] { kept, dropped });
            var unit = Host("Unit").AddComponent<UnitCover>();
            Assert.That(unit.TryReserve(dropped), Is.True);

            registry.Rebuild(new[] { kept });

            Assert.That(dropped.IsValid, Is.False);
            Assert.That(dropped.IsClaimed, Is.False, "A retired location holds no claim");
            Assert.That(kept.IsValid, Is.True, "A location that is still listed is left alone");
        }

        [Test]
        public void Rebuild_KeepsTheClaimOnALocationThatStaysListed()
        {
            var kept = At(0f);
            registry.Rebuild(new[] { kept });
            var unit = Host("Unit").AddComponent<UnitCover>();
            unit.TryReserve(kept);
            registry.Rebuild(new[] { kept, At(9f) });
            Assert.That(kept.Claimant, Is.SameAs(unit));
        }

        [Test]
        public void Rebuild_RejectsNull_AndInitializeIsRebuild()
        {
            Assert.Throws<System.ArgumentNullException>(() => registry.Rebuild(null));
            var a = At(1f);
            registry.Initialize(a);
            Assert.That(registry.Points, Is.EqualTo(new[] { a }));
        }
    }
}
```

- [ ] **Step 2: Run to verify it passes already (Task 1 built Rebuild)**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.CoverRegistryTests"`
Expected: PASS (4). These pin the Task 1 behaviour before discovery relies on it. (If any fails, fix `CoverRegistry`, not the test.)

- [ ] **Step 3: Write the failing discovery PlayMode tests**

Create `Assets/_Project/Tests/PlayMode/CoverDiscoveryPlayModeTests.cs`:

```csharp
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CoverDiscoveryPlayModeTests
    {
        TestWorld world;
        GameObject environment;
        CoverRegistry registry;
        CoverDiscovery discovery;

        // Obstacle 0: a 4 m low wall at the origin. Obstacle 1: a 6 m tall wall at z 10. Obstacle 2: an untagged box.
        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            environment = world.CreateEnvironment(
                (new Vector3(0f, 0.45f, 0f), new Vector3(4f, 0.9f, 0.5f)),
                (new Vector3(0f, 1f, 10f), new Vector3(6f, 2f, 1f)),
                (new Vector3(-12f, 1f, -8f), new Vector3(2f, 2f, 2f)));
            Tag(0);
            Tag(1);
            var systems = world.Track(new GameObject("Systems"));
            registry = systems.AddComponent<CoverRegistry>();
            discovery = systems.AddComponent<CoverDiscovery>();
            discovery.Initialize(registry, discoverAtStart: false);
        }

        [TearDown]
        public void TearDown() => world.Dispose();

        CoverSurface Tag(int obstacleIndex) =>
            TestWorld.ObstacleCollider(environment, obstacleIndex).gameObject.AddComponent<CoverSurface>();

        [UnityTest]
        public IEnumerator Discover_BuildsLocationsFromTaggedBoxesOnly()
        {
            yield return new WaitForFixedUpdate();
            var count = discovery.Discover();

            Assert.That(count, Is.GreaterThan(0));
            Assert.That(registry.Points, Has.Count.EqualTo(count));
            var tagged = new[] { TestWorld.ObstacleCollider(environment, 0), TestWorld.ObstacleCollider(environment, 1) };
            Assert.That(registry.Points.All(l => tagged.Contains(l.Obstacle)), Is.True, "Nothing comes from the untagged box");
            Assert.That(registry.Points.Any(l => l.Obstacle == tagged[0] && l.Height == CoverHeight.Low), Is.True);
            Assert.That(registry.Points.Count(l => l.Obstacle == tagged[1] && l.Placement == CoverPlacement.Corner), Is.EqualTo(4));
        }

        [UnityTest]
        public IEnumerator EveryDiscoveredLocation_LiesOnTheNavMesh()
        {
            yield return new WaitForFixedUpdate();
            discovery.Discover();
            foreach (var location in registry.Points)
                Assert.That(NavMesh.SamplePosition(location.Position, out _, 0.5f, NavMesh.AllAreas), Is.True, $"{location.Name} at {location.Position}");
        }

        [UnityTest]
        public IEnumerator Discover_AfterAddingASurface_AddsItsLocations_AndAfterRemovingOneDropsThem()
        {
            yield return new WaitForFixedUpdate();
            var before = discovery.Discover();
            var untagged = TestWorld.ObstacleCollider(environment, 2);
            var surface = untagged.gameObject.AddComponent<CoverSurface>();
            var withThird = discovery.Discover();
            Assert.That(withThird, Is.GreaterThan(before));
            Assert.That(registry.Points.Any(l => l.Obstacle == untagged), Is.True);

            Object.Destroy(surface);
            yield return null;
            var without = discovery.Discover();
            Assert.That(without, Is.EqualTo(before));
            Assert.That(registry.Points.Any(l => l.Obstacle == untagged), Is.False);
        }

        [UnityTest]
        public IEnumerator RepeatedDiscover_GivesTheSameLocations_NoDuplicates()
        {
            yield return new WaitForFixedUpdate();
            var first = discovery.Discover();
            var firstNames = registry.Points.Select(l => l.Name).ToList();
            var second = discovery.Discover();
            Assert.That(second, Is.EqualTo(first));
            Assert.That(registry.Points.Select(l => l.Name), Is.EqualTo(firstNames));
        }

        [UnityTest]
        public IEnumerator DiscoverAtStart_RunsOnceOnTheFirstFrame()
        {
            var systems = world.Track(new GameObject("StartSystems"));
            systems.SetActive(false);
            var otherRegistry = systems.AddComponent<CoverRegistry>();
            var other = systems.AddComponent<CoverDiscovery>();
            other.Initialize(otherRegistry);   // discoverAtStart defaults to true
            systems.SetActive(true);
            yield return null;
            Assert.That(otherRegistry.Points, Is.Not.Empty);
        }

        [UnityTest]
        public IEnumerator ACornerProtectsFromAcrossTheWall_AndNotFromItsPeekPoint()
        {
            yield return new WaitForFixedUpdate();
            discovery.Discover();
            var corner = registry.Points.First(l => l.Placement == CoverPlacement.Corner && l.HasPeek);
            var standPivot = corner.Position + Vector3.up;
            var peekPivot = corner.PeekPoint + Vector3.up;
            // An attacker straight across the wall from the stand point, 8 m beyond it along Facing.
            var attacker = corner.Position + corner.Facing * 8f + Vector3.up;

            Assert.That(corner.ProtectsFrom(attacker, standPivot), Is.True, "Behind the wall end: protected");
            Assert.That(corner.ProtectsFrom(attacker, peekPivot), Is.False, "Stepped out past the end: exposed");
            var behind = corner.Position - corner.Facing * 8f + Vector3.up;
            Assert.That(corner.ProtectsFrom(behind, standPivot), Is.False, "From behind the defender the wall is not between them");
        }
    }
}
```

- [ ] **Step 4: Run to verify it fails**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.CoverDiscoveryPlayModeTests"`
Expected: `error CS0246: ... 'CoverSurface'` / `'CoverDiscovery'`.

- [ ] **Step 5: Implement `CoverSurface` and `CoverDiscovery`**

`Assets/_Project/Scripts/Cover/CoverSurface.cs`:

```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Marks a box obstacle as cover-generating geometry. Carries no data: the generator reads the box. A mission
    /// generator tags the geometry it spawns the same way. Upright boxes only; only the yaw of the rotation is used.
    /// </summary>
    [RequireComponent(typeof(BoxCollider))]
    public sealed class CoverSurface : MonoBehaviour
    {
        public CoverBox ToBox()
        {
            var box = GetComponent<BoxCollider>();
            var lossy = transform.lossyScale;
            var size = Vector3.Scale(box.size, new Vector3(Mathf.Abs(lossy.x), Mathf.Abs(lossy.y), Mathf.Abs(lossy.z)));
            return new CoverBox(name, transform.TransformPoint(box.center), transform.eulerAngles.y, size * 0.5f, box);
        }
    }
}
```

`Assets/_Project/Scripts/Cover/CoverDiscovery.cs`:

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace Blackglass
{
    /// <summary>
    /// Finds the scene's CoverSurfaces, runs the generator over them and hands the result to the registry. Assumes the
    /// NavMesh already covers the geometry: a procedural mission builds its geometry, then its NavMesh, then calls
    /// Discover(). Runs once at start by default; can be run again at any time (a rebuild retires the old locations).
    /// </summary>
    public sealed class CoverDiscovery : MonoBehaviour
    {
        [SerializeField] CoverRegistry registry;
        [SerializeField] CoverGenerationSettings settings = new CoverGenerationSettings();
        [SerializeField] bool discoverOnStart = true;

        public CoverGenerationSettings Settings => settings;

        internal void Initialize(CoverRegistry coverRegistry, bool discoverAtStart = true, CoverGenerationSettings generationSettings = null)
        {
            registry = coverRegistry;
            discoverOnStart = discoverAtStart;
            if (generationSettings != null)
                settings = generationSettings;
        }

        void Start()
        {
            if (discoverOnStart)
                Discover();
        }

        /// <summary>Scans, generates and rebuilds the registry. Returns the number of locations now listed.</summary>
        public int Discover()
        {
            if (registry == null)
            {
                Debug.LogWarning($"{name}: CoverDiscovery has no registry wired, so nothing was discovered.", this);
                return 0;
            }
            var surfaces = new List<CoverSurface>(FindObjectsByType<CoverSurface>(FindObjectsSortMode.None));
            // A stable order makes names and tests repeatable whatever order Unity returns the objects in.
            surfaces.Sort(CompareSurfaces);
            var boxes = new List<CoverBox>(surfaces.Count);
            foreach (var surface in surfaces)
                boxes.Add(surface.ToBox());

            var tolerance = settings.walkableTolerance;
            var locations = CoverGenerator.Generate(boxes, settings,
                point => NavMesh.SamplePosition(point, out _, tolerance, NavMesh.AllAreas));
            registry.Rebuild(locations);
            return locations.Count;
        }

        static int CompareSurfaces(CoverSurface a, CoverSurface b)
        {
            var byName = string.CompareOrdinal(a.name, b.name);
            if (byName != 0)
                return byName;
            var byX = a.transform.position.x.CompareTo(b.transform.position.x);
            return byX != 0 ? byX : a.transform.position.z.CompareTo(b.transform.position.z);
        }
    }
}
```

- [ ] **Step 6: Run to verify they pass**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.CoverDiscoveryPlayModeTests"`
Expected: 6 pass, no unexpected log errors. Notes for debugging: `FindObjectsByType` skips inactive objects (intended). In `Discover_AfterAddingASurface...`, `Object.Destroy(surface)` removes only the component, which is what we want. If `ACornerProtects...` fails with `ProtectsFrom` false at the stand point, check `Physics.SyncTransforms()` (done by `TestWorld.CreateEnvironment`) and the `WaitForFixedUpdate` before `Discover`.

- [ ] **Step 7: Commit**

```bash
git add -A Assets/_Project/Scripts/Cover Assets/_Project/Tests
git commit -m "$(cat <<'EOF'
Discover cover from tagged box geometry into the registry

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 5: Rebuild lifecycle: claims, orders and repeated discovery

Pins Review Focus items 2 and 3 (no stale claims; units and orders survive a rebuild). No production code is expected to change; if a test exposes a bug, fix the production code in the smallest way and say so.

**Files:**
- Test: `Assets/_Project/Tests/PlayMode/CoverRebuildPlayModeTests.cs`
- Modify only if a test fails: `Scripts/Units/UnitCover.cs` or `Scripts/Units/CommandableUnit.cs`

**Interfaces:**
- Consumes: `CoverDiscovery.Discover()` / `Initialize`, `CoverSurface`, `TestWorld.CreateEnvironment/CreateFighter`, `UnitCover.Status/Point`, `CommandableUnit.Issue(new MoveToCoverCommand(...))`.
- Produces: nothing new.

- [ ] **Step 1: Write the tests**

Create `Assets/_Project/Tests/PlayMode/CoverRebuildPlayModeTests.cs`:

```csharp
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CoverRebuildPlayModeTests
    {
        TestWorld world;
        GameObject environment;
        CoverRegistry registry;
        CoverDiscovery discovery;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            environment = world.CreateEnvironment((new Vector3(0f, 0.45f, 0f), new Vector3(4f, 0.9f, 0.5f)));
            TestWorld.ObstacleCollider(environment).gameObject.AddComponent<CoverSurface>();
            var systems = world.Track(new GameObject("Systems"));
            registry = systems.AddComponent<CoverRegistry>();
            discovery = systems.AddComponent<CoverDiscovery>();
            discovery.Initialize(registry, discoverAtStart: false);
        }

        [TearDown]
        public void TearDown() => world.Dispose();

        CommandableUnit Fighter(Vector3 at) => world.CreateFighter(at, registry: registry);

        // The south-face location nearest x = -1 of the 4 m low wall.
        CoverLocation SouthPoint() => registry.Points.Where(l => l.Facing.z > 0.5f).OrderBy(l => l.Position.x).First();

        [UnityTest]
        public IEnumerator Rebuild_WhileAUnitOccupiesALocation_ReleasesIt_AndTheUnitReclaimsTheEquivalentOneBystanding()
        {
            yield return new WaitForFixedUpdate();
            discovery.Discover();
            var old = SouthPoint();
            var unit = Fighter(new Vector3(old.Position.x, 0f, old.Position.z - 5f));
            Assert.That(unit.Issue(new MoveToCoverCommand(old)), Is.True);
            yield return TestWorld.WaitUntil(() => unit.Cover.Status == CoverStatus.Occupied, 10f);
            Assert.That(unit.Cover.Status, Is.EqualTo(CoverStatus.Occupied), "Precondition");

            discovery.Discover();

            Assert.That(old.IsValid, Is.False);
            Assert.That(old.IsClaimed, Is.False, "The retired location holds no claim");
            yield return null;
            yield return null;
            yield return TestWorld.WaitUntil(() => unit.Cover.Status == CoverStatus.Occupied && unit.Cover.Point != old, 3f);
            Assert.That(unit.Cover.Point, Is.Not.SameAs(old));
            Assert.That(unit.Cover.Point.IsValid, Is.True, "Standing still on the new equivalent location claims it");
            Assert.That(unit.Cover.OccupiedByOrder, Is.False, "It is a chance occupancy, not an ordered one");
        }

        [UnityTest]
        public IEnumerator Rebuild_MidWalk_EndsTheCoverOrderSilently_AndTheNextQueuedOrderRuns()
        {
            yield return new WaitForFixedUpdate();
            discovery.Discover();
            var old = SouthPoint();
            var unit = Fighter(new Vector3(old.Position.x, 0f, old.Position.z - 10f));
            Assert.That(unit.Issue(new MoveToCoverCommand(old)), Is.True);
            var next = new MoveCommand(new Vector3(6f, 0f, -8f));
            Assert.That(unit.Issue(next, IssueMode.Append), Is.True);
            yield return null;

            discovery.Discover();
            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == next, 2f);

            Assert.That(unit.CurrentCommand, Is.SameAs(next), "The retired location ends the cover order and the queue moves on");
            Assert.That(unit.Cover.Status, Is.EqualTo(CoverStatus.None));
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator MoveToCover_OnARetiredLocation_IsRefused()
        {
            yield return new WaitForFixedUpdate();
            discovery.Discover();
            var old = SouthPoint();
            discovery.Discover();
            var unit = Fighter(new Vector3(0f, 0f, -8f));

            Assert.That(unit.Issue(new MoveToCoverCommand(old)), Is.False, "A retired location is not a valid destination");
            Assert.That(unit.Cover.Status, Is.EqualTo(CoverStatus.None));
        }

        [UnityTest]
        public IEnumerator RepeatedRebuilds_WithUnitsInCover_LeaveNoStaleClaims()
        {
            yield return new WaitForFixedUpdate();
            discovery.Discover();
            var units = new[]
            {
                Fighter(new Vector3(-1.5f, 0f, -5f)),
                Fighter(new Vector3(1.5f, 0f, -5f)),
            };
            var points = registry.Points.Where(l => l.Facing.z > 0.5f).OrderBy(l => l.Position.x).Take(2).ToList();
            for (var i = 0; i < units.Length; i++)
                Assert.That(units[i].Issue(new MoveToCoverCommand(points[i])), Is.True);
            yield return TestWorld.WaitUntil(() => units.All(u => u.Cover.Status == CoverStatus.Occupied), 10f);

            var retired = registry.Points.ToList();
            for (var round = 0; round < 5; round++)
            {
                discovery.Discover();
                yield return null;
                yield return null;
            }

            Assert.That(retired.Any(l => l.IsClaimed), Is.False, "No retired location keeps a claimant");
            var claimed = registry.Points.Where(l => l.IsClaimed).ToList();
            Assert.That(claimed.Count, Is.LessThanOrEqualTo(units.Length), "Claims never outnumber the units");
            Assert.That(claimed.All(l => units.Any(u => u.Cover.Point == l)), Is.True, "Every claim belongs to a unit that points at it");
            Assert.That(registry.Points.Select(l => l.Name).Distinct().Count(), Is.EqualTo(registry.Points.Count), "No duplicated locations");
        }
    }
}
```

- [ ] **Step 2: Run**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.CoverRebuildPlayModeTests"`
Expected: PASS (4). If `MoveToCover_OnARetiredLocation_IsRefused` fails, `CanTakeCover` is missing the `IsValid` check from Task 1 Step 5: fix there. If the first test's final wait times out, the unit left the location radius or the stillness rule did not fire; read `UnitCover.Update` before changing it (a unit that arrived has speed ~0, so it should re-claim within a frame or two; the test allows 3 s of simulation).

- [ ] **Step 3: Commit**

```bash
git add Assets/_Project/Tests/PlayMode/CoverRebuildPlayModeTests.cs Assets/_Project/Tests/PlayMode/CoverRebuildPlayModeTests.cs.meta
git commit -m "$(cat <<'EOF'
Pin the cover rebuild lifecycle: claims, orders and repeated discovery

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>
EOF
)"
```
(Include any production fix in the same commit and say so in the message.)

---

### Task 6: Hostiles choose Low cover only

**Files:**
- Modify: `Assets/_Project/Scripts/AI/EnemyAI.cs`
- Test: `Assets/_Project/Tests/PlayMode/EnemyAIPlayModeTests.cs`

**Interfaces:**
- Consumes: `CoverLocation.Height` (Task 1); the existing `CoverLayout()` helper and `TestWorld.CreateCoverPoint(..., CoverHeight height)` (Task 1).
- Produces: nothing new.

- [ ] **Step 1: Write the failing test (add to `EnemyAIPlayModeTests` after `RangedHostile_TakesNearbyCover_ThenFiresFromIt`)**

```csharp
        [UnityTest]
        public IEnumerator RangedHostile_IgnoresATallLocation_AndAttacksWithoutCover()
        {
            // The same layout as CoverLayout, but the only location is Tall (a hiding spot): a hostile standing there
            // could not shoot, and without a peek behaviour it would leave it again, so it never takes one.
            var environment = world.CreateEnvironment((new Vector3(0f, 0.45f, 0f), new Vector3(4f, 0.9f, 0.5f)));
            var tall = world.CreateCoverPoint(new Vector3(0f, 0f, 1f), Vector3.back, TestWorld.ObstacleCollider(environment), 0.5f, CoverHeight.Tall);
            var registry = world.CreateRegistry(tall);
            var friendly = world.CreateFighter(new Vector3(0f, 0f, -5f), maxHealth: 300);
            var hostile = world.CreateHostile(new Vector3(0f, 0f, 5f), encounter, damage: 8, cooldown: 1.5f, role: CombatRole.Ranged, range: 8f, registry: registry);
            Arm(new[] { hostile }, friendly);

            yield return WaitForState(hostile, EnemyState.Chase, 1.5f);

            Assert.That(hostile.State, Is.EqualTo(EnemyState.Chase), "A plain attack: no Low location within reach");
            Assert.That(tall.IsClaimed, Is.False);
            Assert.That(CoverOf(hostile).Status, Is.EqualTo(CoverStatus.None));
        }
```

- [ ] **Step 2: Run to verify it fails**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.EnemyAIPlayModeTests.RangedHostile_IgnoresATallLocation_AndAttacksWithoutCover"`
Expected: FAIL (the hostile reaches `EnemyState.Cover` or `WaitForState` times out), because `IsUsefulCover` accepts it.

- [ ] **Step 3: Add the Low-only rule**

In `EnemyAI.IsUsefulCover`, make the height check the first (cheapest) test and update the comment above it:

```csharp
        // Low (a firing spot) and unclaimed or ours; protects the point from the target (one obstacle ray); the target
        // can be attacked from the eye a unit would have there (range and sight); reachable (a path, so last). Tall
        // locations are hiding spots: without a peek behaviour a hostile there could not fire and would leave again.
        bool IsUsefulCover(CoverLocation point)
        {
            if (point.Height != CoverHeight.Low)
                return false;
            if (point.IsClaimed && !point.IsClaimedBy(Unit.Cover))
                return false;
            ...
```
(keep the rest of the method as is).

- [ ] **Step 4: Run to verify it passes, then the whole class**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.EnemyAIPlayModeTests"`
Expected: all pass, including `RangedHostile_TakesNearbyCover_ThenFiresFromIt` (Low by default).

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/AI/EnemyAI.cs Assets/_Project/Tests/PlayMode/EnemyAIPlayModeTests.cs
git commit -m "$(cat <<'EOF'
Keep hostiles off tall hiding locations until peeking exists

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 7: Cover view: rebuild on change, shapes per type, green selected destination

**Files:**
- Modify: `Assets/_Project/Scripts/DebugUI/CoverView.cs`
- Test: `Assets/_Project/Tests/PlayMode/CoverViewTests.cs`

**Interfaces:**
- Consumes: `CoverRegistry.Version`, `CoverLocation.Height/Placement/Facing/PeekDirection/PeekPoint`, `UnitSelection.Selected` (`IReadOnlyList<SelectableUnit>`), `SelectableUnit.Unit.Cover` (`UnitCover.Point/Status`).
- Produces: `CoverView.SelectedColor` (internal static readonly `Color`, `Color.green`); `CoverView.Initialize(CoverRegistry, TacticalPause, Material, UnitSelection selection = null)`; `static Color ColorFor(CoverLocation, bool isSelectedDestination = false)`.

- [ ] **Step 1: Write the failing tests**

In `Tests/PlayMode/CoverViewTests.cs`:

1. Change `SetUp`'s wiring call to the new signature: `view.Initialize(registry, pause, null, selection);` where `selection` is a field `UnitSelection selection;` created in SetUp as `selection = world.Track(new GameObject("Selection")).AddComponent<UnitSelection>();` (check how other tests in `Tests/PlayMode` construct a `UnitSelection`, e.g. in `PlayerCommandInputTests`, and mirror that exactly).
2. Replace `BuildsOneColliderFreeMarkerPerPoint_HiddenInRealTime`'s per-marker renderer assertion `Has.Length.EqualTo(2), "a disc and a nub"` (both points are Low faces, so it stays 2).
3. Add:

```csharp
        [UnityTest]
        public IEnumerator MarkersAreRebuilt_WhenTheRegistryChanges()
        {
            yield return null;
            Assert.That(view.Markers.Count, Is.EqualTo(2));

            var c = world.CreateCoverPoint(new Vector3(3f, 0f, -1f), Vector3.forward, TestWorld.ObstacleCollider(environment));
            registry.Rebuild(new[] { a, b, c });
            yield return null;

            Assert.That(view.Markers.Count, Is.EqualTo(3));
            registry.Rebuild(new[] { c });
            yield return null;
            Assert.That(view.Markers.Count, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ShapeShowsTheType_LowDiscAndNub_TallTallerNub_CornerAddsAPeekArrow()
        {
            var tall = world.CreateCoverPoint(new Vector3(3f, 0f, -1f), Vector3.forward, TestWorld.ObstacleCollider(environment), 0.5f, CoverHeight.Tall);
            var corner = new CoverLocation("Corner", new Vector3(5f, 0f, -1f), Vector3.forward, TestWorld.ObstacleCollider(environment),
                0.5f, CoverHeight.Tall, CoverPlacement.Corner, Vector3.right, new Vector3(6.25f, 0f, -1f));
            registry.Rebuild(new[] { a, tall, corner });
            yield return null;

            Assert.That(view.Markers[0].GetComponentsInChildren<Renderer>(true), Has.Length.EqualTo(2), "Low: disc and nub");
            Assert.That(view.Markers[1].GetComponentsInChildren<Renderer>(true), Has.Length.EqualTo(2), "Tall: disc and nub");
            Assert.That(view.Markers[2].GetComponentsInChildren<Renderer>(true), Has.Length.EqualTo(3), "Corner: disc, nub and peek arrow");
            var lowNub = view.Markers[0].transform.Find("Nub").localScale.y;
            var tallNub = view.Markers[1].transform.Find("Nub").localScale.y;
            Assert.That(tallNub, Is.GreaterThan(lowNub), "A tall obstacle shows a taller nub");
            Assert.That(view.Markers[2].transform.Find("Peek"), Is.Not.Null);
        }

        [UnityTest]
        public IEnumerator TheSelectedUnitsDestination_IsGreen_OthersKeepTheirStateColour()
        {
            yield return null;
            var friendly = world.CreateFriendlyFighter(new Vector3(0f, 0f, -6f));
            friendly.Unit.Cover.Initialize(registry);
            selection.AddToRoster(friendly);
            selection.Select(friendly);
            Assert.That(friendly.Unit.Issue(new MoveToCoverCommand(a)), Is.True);
            pause.Pause();
            yield return null;

            Assert.That(view.ShownColor(0), Is.EqualTo(CoverView.SelectedColor), "The selected unit's reserved point is green");
            Assert.That(view.ShownColor(1), Is.EqualTo(CoverView.AvailableColor));

            selection.Clear();   // use the real deselect API (see UnitSelection); if it is named differently, use that
            yield return null;
            Assert.That(view.ShownColor(0), Is.EqualTo(CoverView.ReservedColor), "Deselected: back to the state colour");
        }
```
(Read `Scripts/Selection/UnitSelection.cs` first: use its real method names for roster registration, select and deselect. `friendly.Unit.Cover.Initialize(registry)` is optional if not needed for a reservation; `TryReserve` works without a registry.)

- [ ] **Step 2: Run to verify they fail**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.CoverViewTests"`
Expected: compile error on `Initialize(..., selection)` / `SelectedColor`, then (once compiled) the new tests fail.

- [ ] **Step 3: Rewrite `CoverView`**

Replace `Assets/_Project/Scripts/DebugUI/CoverView.cs` with:

```csharp
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Blackglass
{
    /// <summary>
    /// Debug view of the registry's cover locations. Colour is the state: available white, reserved yellow, occupied
    /// cyan, and green for the location a selected unit is going to or holds. Shape is the type: a disc and a nub
    /// toward the obstacle for Low cover, a taller nub for Tall cover, and for a corner also a thin arrow along the
    /// peek direction. Shows every location while paused and only claimed ones in real time. Rebuilds its markers when
    /// the registry's version changes, and never changes the registry. Lives on Systems. Markers have no colliders, so
    /// they never block click raycasts. Works while paused.
    /// </summary>
    public sealed class CoverView : MonoBehaviour
    {
        internal static readonly Color AvailableColor = Color.white;
        internal static readonly Color ReservedColor = Color.yellow;
        internal static readonly Color OccupiedColor = Color.cyan;
        internal static readonly Color SelectedColor = Color.green;
        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");

        [SerializeField] CoverRegistry registry;
        [SerializeField] TacticalPause tacticalPause;
        [SerializeField] UnitSelection selection;
        [SerializeField] Material markerMaterial;
        [SerializeField, Min(0.05f)] float discDiameter = 0.5f;
        // The prototype ground is flat at y = 0; markers float just above it.
        [SerializeField] float groundHeight = 0.05f;

        // One marker per registry location, in registry order (rebuilt whenever the registry's version changes).
        readonly List<GameObject> markers = new List<GameObject>();
        readonly List<Renderer[]> markerRenderers = new List<Renderer[]>();
        readonly HashSet<CoverLocation> destinations = new HashSet<CoverLocation>();
        MaterialPropertyBlock block;
        int builtVersion = -1;

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

        /// <summary>The colour a location's marker shows: green for a selected unit's destination, else its state.</summary>
        internal static Color ColorFor(CoverLocation point, bool isSelectedDestination = false) =>
            isSelectedDestination ? SelectedColor
            : !point.IsClaimed ? AvailableColor
            : point.IsOccupied ? OccupiedColor
            : ReservedColor;

        /// <summary>The colour currently applied to the marker at `index` (tests).</summary>
        internal Color ShownColor(int index)
        {
            block ??= new MaterialPropertyBlock();
            markerRenderers[index][0].GetPropertyBlock(block);
            return block.GetColor(BaseColor);
        }

        internal void Initialize(CoverRegistry coverRegistry, TacticalPause pause, Material material, UnitSelection unitSelection = null)
        {
            registry = coverRegistry;
            tacticalPause = pause;
            markerMaterial = material;
            selection = unitSelection;
        }

        void LateUpdate()
        {
            if (registry == null)
                return;
            if (registry.Version != builtVersion)
                RebuildMarkers();
            CollectDestinations();
            var paused = tacticalPause != null && tacticalPause.IsPaused;
            for (var i = 0; i < markers.Count && i < registry.Points.Count; i++)
            {
                var point = registry.Points[i];
                var show = point != null && point.IsValid && (paused || point.IsClaimed);
                if (markers[i].activeSelf != show)
                    markers[i].SetActive(show);
                if (show)
                    Paint(i, ColorFor(point, destinations.Contains(point)));
            }
        }

        void RebuildMarkers()
        {
            foreach (var marker in markers)
            {
                if (marker != null)
                    Destroy(marker);
            }
            markers.Clear();
            markerRenderers.Clear();
            foreach (var point in registry.Points)
                markers.Add(CreateMarker(point));
            builtVersion = registry.Version;
        }

        // The locations the selected units hold or are walking to.
        void CollectDestinations()
        {
            destinations.Clear();
            if (selection == null)
                return;
            foreach (var selected in selection.Selected)
            {
                if (selected == null)
                    continue;
                var cover = selected.Unit.Cover;
                if (cover != null && cover.Status != CoverStatus.None && cover.Point != null)
                    destinations.Add(cover.Point);
            }
        }

        // A disc at the stand point plus a nub 0.4 m toward the obstacle (taller for Tall), plus for a corner an arrow
        // along the peek direction. Parented here for cleanup; positions are world.
        GameObject CreateMarker(CoverLocation point)
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

                var nubHeight = point.Height == CoverHeight.Tall ? 0.8f : 0.15f;
                var nub = GameObject.CreatePrimitive(PrimitiveType.Cube);
                nub.name = "Nub";
                DestroyImmediate(nub.GetComponent<Collider>());
                nub.transform.SetParent(root.transform, false);
                nub.transform.SetPositionAndRotation(point.Position + point.Facing * 0.4f + Vector3.up * (nubHeight * 0.5f + 0.025f),
                    Quaternion.LookRotation(point.Facing));
                nub.transform.localScale = new Vector3(0.15f, nubHeight, 0.3f);

                if (point.HasPeek)
                {
                    var length = Vector3.Distance(point.Position, point.PeekPoint);
                    var arrow = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    arrow.name = "Peek";
                    DestroyImmediate(arrow.GetComponent<Collider>());
                    arrow.transform.SetParent(root.transform, false);
                    arrow.transform.SetPositionAndRotation(point.Position + point.PeekDirection * (length * 0.5f) + Vector3.up * 0.1f,
                        Quaternion.LookRotation(point.PeekDirection));
                    arrow.transform.localScale = new Vector3(0.08f, 0.05f, length);
                }
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

(The old `Start` is gone on purpose: `LateUpdate` builds the markers on the first frame because `builtVersion` starts at -1.)

- [ ] **Step 4: Run to verify they pass**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.CoverViewTests"`
Expected: all pass (the 3 old + 3 new). Then `Tools/run-tests.sh PlayMode "Blackglass.Tests.CommandQueueViewTests"` still passes.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/DebugUI/CoverView.cs Assets/_Project/Tests/PlayMode/CoverViewTests.cs
git commit -m "$(cat <<'EOF'
Show cover by type and selected destination, rebuilding on registry changes

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 8: Prototype scene and scene tests

**Files:**
- Temporary (never committed): `Assets/_Project/Editor/ProceduralCoverSceneBuilder.cs`
- Modify: `Assets/_Project/Scenes/Prototype.unity`
- Modify: `Assets/_Project/Tests/PlayMode/PrototypeSceneTests.cs`

**Interfaces:**
- Consumes: `CoverSurface`, `CoverDiscovery` (public `Settings`, `Discover()`), `CoverRegistry`, `CoverView.selection` (private serialized `selection`), `UnitSelection`, scene object names: `LowWall_L`, `LowWall_M`, `LowWall_N`, `Pillar_G`, `Crate_J`, `Barrier_I`, `Obstacle_CentralWall`, `Systems`, `CoverPoints`.
- Produces: a scene whose `Systems` object has `CoverRegistry` (no list), `CoverDiscovery` (wired to it) and a `CoverView` wired to `UnitSelection`; no `CoverPoints` object.

- [ ] **Step 1: Create the temporary builder**

Create `Assets/_Project/Editor/ProceduralCoverSceneBuilder.cs`:

```csharp
using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Blackglass;

public static class ProceduralCoverSceneBuilder
{
    const string ScenePath = "Assets/_Project/Scenes/Prototype.unity";
    static readonly string[] SurfaceNames =
        { "LowWall_L", "LowWall_M", "LowWall_N", "Pillar_G", "Crate_J", "Barrier_I", "Obstacle_CentralWall" };

    public static void Build()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        foreach (var surfaceName in SurfaceNames)
        {
            var go = GameObject.Find(surfaceName);
            if (go == null)
                throw new Exception($"{surfaceName} not found");
            if (go.GetComponent<BoxCollider>() == null)
                throw new Exception($"{surfaceName} has no BoxCollider");
            if (go.GetComponent<CoverSurface>() == null)
                go.AddComponent<CoverSurface>();
        }

        var points = GameObject.Find("CoverPoints");
        if (points != null)
            UnityEngine.Object.DestroyImmediate(points);

        var systems = GameObject.Find("Systems");
        if (systems == null)
            throw new Exception("Systems not found");
        var registry = systems.GetComponent<CoverRegistry>();
        if (registry == null)
            throw new Exception("CoverRegistry not found on Systems");

        var discovery = systems.GetComponent<CoverDiscovery>() ?? systems.AddComponent<CoverDiscovery>();
        var discoverySo = new SerializedObject(discovery);
        discoverySo.FindProperty("registry").objectReferenceValue = registry;
        discoverySo.FindProperty("discoverOnStart").boolValue = true;
        discoverySo.ApplyModifiedPropertiesWithoutUndo();

        var view = systems.GetComponent<CoverView>();
        if (view == null)
            throw new Exception("CoverView not found on Systems");
        var selection = UnityEngine.Object.FindFirstObjectByType<UnitSelection>();
        if (selection == null)
            throw new Exception("UnitSelection not found");
        var viewSo = new SerializedObject(view);
        viewSo.FindProperty("selection").objectReferenceValue = selection;
        viewSo.ApplyModifiedPropertiesWithoutUndo();

        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene))
            throw new Exception("Saving the scene failed");
        Debug.Log($"[ProceduralCoverSceneBuilder] Tagged {SurfaceNames.Length} surfaces, removed CoverPoints, wired CoverDiscovery and CoverView in {ScenePath}");
    }
}
```

- [ ] **Step 2: Run the builder in batch mode (editor closed)**

```bash
"/c/Program Files/Unity/Hub/Editor/6000.3.25f1/Editor/Unity.exe" -batchmode -quit -projectPath "$(pwd -W)" -executeMethod ProceduralCoverSceneBuilder.Build -logFile "$(pwd -W)/Logs/BuildProceduralCover.log"; echo "EXIT=$?"
grep -E '\[ProceduralCoverSceneBuilder\]|error CS|Exception' Logs/BuildProceduralCover.log | head -20
```
Expected: `EXIT=0`; the log shows `Tagged 7 surfaces, removed CoverPoints, wired ...` and no exception. If the editor is open elsewhere, close it first.

- [ ] **Step 3: Verify the scene diff is what we expect, then delete the builder**

```bash
git diff --stat Assets/_Project/Scenes/Prototype.unity
grep -c "CoverPoint" Assets/_Project/Scenes/Prototype.unity   # expect 0 (no stale references to the deleted class)
grep -n "Cover_" Assets/_Project/Scenes/Prototype.unity | head -3   # expect nothing
rm Assets/_Project/Editor/ProceduralCoverSceneBuilder.cs Assets/_Project/Editor/ProceduralCoverSceneBuilder.cs.meta
rmdir Assets/_Project/Editor 2>/dev/null; rm -f Assets/_Project/Editor.meta
ls Assets/_Project/Editor 2>&1 | head -1   # expect: No such file or directory
```
Expected: the scene diff contains the removed `Cover_*` objects, seven new `CoverSurface` components and a `CoverDiscovery` component; no `NavMesh` asset changed (`git status` shows only `Prototype.unity`). If a `Missing Script` reference to the old `CoverPoint` class remains in the YAML (a line like `m_Script: {fileID: 0}`), the builder's `DestroyImmediate(points)` missed an object: report it.

- [ ] **Step 4: Write the failing scene tests (edit `PrototypeSceneTests.cs`)**

1. Remove the `[Ignore]` attributes Task 1 added.
2. In the wiring test, replace the block from `var registry = Object.FindFirstObjectByType<CoverRegistry>();` through the `foreach (var point in registry.Points) { ... }` loop with:

```csharp
            var registry = Object.FindFirstObjectByType<CoverRegistry>();
            Assert.That(registry, Is.Not.Null, "CoverRegistry missing");
            Assert.That(GameObject.Find("CoverPoints"), Is.Null, "Hand-placed cover points are gone: cover is discovered");
            Assert.That(Object.FindFirstObjectByType<CoverDiscovery>(), Is.Not.Null, "CoverDiscovery missing");
            foreach (var surfaceName in new[] { "LowWall_L", "LowWall_M", "LowWall_N", "Pillar_G", "Crate_J", "Barrier_I", "Obstacle_CentralWall" })
                Assert.That(GameObject.Find(surfaceName).GetComponent<CoverSurface>(), Is.Not.Null, $"{surfaceName} is not cover-generating");
            yield return TestWorld.WaitUntil(() => registry.Points.Count > 0, 2f);
            Assert.That(registry.Points, Is.Not.Empty, "Cover is discovered at start");
            foreach (var point in registry.Points)
            {
                Assert.That(point.Obstacle, Is.Not.Null, $"{point.Name} has no obstacle");
                Assert.That(point.IsValid, Is.True, point.Name);
                Assert.That(point.IsClaimed, Is.False, $"{point.Name} starts claimed");
                Assert.That(UnityEngine.AI.NavMesh.SamplePosition(point.Position, out _, 0.5f, UnityEngine.AI.NavMesh.AllAreas), Is.True,
                    $"{point.Name} at {point.Position} is off the NavMesh");
            }
```
(If the enclosing test is not an `IEnumerator` `[UnityTest]`, make it one; check its signature.) Also assert the view is wired to the selection: `Assert.That(new UnityEditor...` is not available at runtime, so instead add to the same test: nothing; the green-colour behaviour is covered by Task 7's test and the manual checks.

3. Add these tests (use the file's existing `LoadScene()` and `FindSquad()` helpers; `friendlies`/`hostiles` lookups copy what the wiring test does):

```csharp
        [UnityTest]
        public IEnumerator Cover_IsDiscoveredFromTheArenaGeometry_LowWallsAndTallWallCorners()
        {
            yield return LoadScene();
            var registry = Object.FindFirstObjectByType<CoverRegistry>();
            yield return TestWorld.WaitUntil(() => registry.Points.Count > 0, 2f);

            foreach (var wall in new[] { "LowWall_L", "LowWall_M", "LowWall_N" })
            {
                var collider = GameObject.Find(wall).GetComponent<Collider>();
                var own = registry.Points.Where(p => p.Obstacle == collider).ToList();
                Assert.That(own, Is.Not.Empty, $"{wall} has cover");
                Assert.That(own.All(p => p.Height == CoverHeight.Low && p.Placement == CoverPlacement.Face), Is.True, wall);
            }
            Assert.That(registry.Points.Any(p => p.Name == "Cover_LowWall_L_S1"), Is.True, "The south-west point of LowWall_L keeps its name");

            var barrier = GameObject.Find("Barrier_I").GetComponent<Collider>();
            var barrierCorners = registry.Points.Where(p => p.Obstacle == barrier && p.Placement == CoverPlacement.Corner).ToList();
            Assert.That(barrierCorners, Has.Count.EqualTo(4), "A free-standing tall wall has four corners");
            Assert.That(barrierCorners.All(p => p.HasPeek && p.Height == CoverHeight.Tall), Is.True);

            var central = GameObject.Find("Obstacle_CentralWall").GetComponent<Collider>();
            var centralCorners = registry.Points.Where(p => p.Obstacle == central && p.Placement == CoverPlacement.Corner).ToList();
            Assert.That(centralCorners.Count, Is.GreaterThanOrEqualTo(2),
                $"The 45-degree central wall gets corners (found {centralCorners.Count}; some may fall off the NavMesh near LowWall_L's corridor)");

            foreach (var untagged in new[] { "Obstacle_A", "Obstacle_B", "Obstacle_D", "Obstacle_E", "Obstacle_F", "Pillar_H", "Crate_K" })
            {
                var collider = GameObject.Find(untagged).GetComponent<Collider>();
                Assert.That(registry.Points.Any(p => p.Obstacle == collider), Is.False, $"{untagged} is not cover-generating");
            }
        }

        [UnityTest]
        public IEnumerator NoDiscoveredLocation_SitsWithinTheOccupyRadiusOfAnyUnitStart()
        {
            yield return LoadScene();
            var registry = Object.FindFirstObjectByType<CoverRegistry>();
            yield return TestWorld.WaitUntil(() => registry.Points.Count > 0, 2f);

            foreach (var unit in Object.FindObjectsByType<CommandableUnit>(FindObjectsSortMode.None))
            {
                var radius = unit.Cover.OccupyRadius;
                foreach (var point in registry.Points)
                    Assert.That(CoverRules.FlatDistance(unit.transform.position, point.Position), Is.GreaterThan(radius),
                        $"{point.Name} would be claimed by {unit.name} standing at its start");
            }
        }
```
Add `using System.Linq;` if missing.

4. In `PausedClickNearACoverMarker_InScene_OrdersCover_...`: replace `GameObject.Find("Cover_LowWall_L_S1").GetComponent<CoverPoint>()` with:

```csharp
            var registry = Object.FindFirstObjectByType<CoverRegistry>();
            yield return TestWorld.WaitUntil(() => registry.Points.Count > 0, 2f);
            var point = registry.Points.Single(p => p.Name == "Cover_LowWall_L_S1");
```

- [ ] **Step 5: Run the scene tests**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.PrototypeSceneTests"`
Expected: PASS. Possible outcomes and what to do:
- `NoDiscoveredLocation_...` fails for one unit/point pair: a generated point lies within 0.6 m of a start. Do **not** loosen the assertion. Decide per case: if the unit is a hostile that starts near a point and the previous hand layout kept 1.1 m (e.g. `HostileUnit_1` and `Cover_LowWall_N_N`), nothing should differ (same point); report any other pair with the two coordinates and stop for the owner's decision (moving a unit start changes the arena).
- `corners EqualTo(4)` fails for `Barrier_I`: print `registry.Points.Where(p => p.Obstacle == barrier)` names and positions, check that each corner stand point is within 0.25 m of the baked NavMesh; if the NavMesh genuinely excludes a corner, report instead of weakening.
- Missing Script warnings are no longer expected; any `error`-level log fails scene tests by design.

- [ ] **Step 6: Run both full suites**

```bash
Tools/run-tests.sh EditMode
Tools/run-tests.sh PlayMode
```
Expected: both `failed="0"`; counts equal Task 1's baseline plus the tests added since (list the delta by task in your report).

- [ ] **Step 7: Commit (scene and tests only; the builder is already deleted)**

```bash
git status --short   # expect only Prototype.unity and PrototypeSceneTests.cs (plus .meta churn, if any)
git add Assets/_Project/Scenes/Prototype.unity Assets/_Project/Tests/PlayMode/PrototypeSceneTests.cs
git commit -m "$(cat <<'EOF'
Build the prototype arena's cover from tagged geometry instead of hand-placed points

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 9: Decision records and documentation

**Files:**
- Modify: `Docs/Decisions.md`
- Modify: `Docs/superpowers/specs/2026-10-05-cover-system-design.md` (one status line at the top)

**Interfaces:** none (documentation). Read decisions 019 to 022 first; keep the tone and the bullet structure (Decided / Why / Rejected / Implications).

- [ ] **Step 1: Append decision 023 to `Docs/Decisions.md`**

```markdown
## 023 — Procedural cover discovery

- **Decided (2026-10-05):** Cover locations are no longer hand-placed scene objects. A cover location is the plain class `CoverLocation` (position, facing into the obstacle, protecting collider, `Low`/`Tall` height, `Face`/`Corner` placement, hit chance, claim, and for corners a peek direction and peek point). Geometry produces them: obstacles tagged `CoverSurface` (upright `BoxCollider`s, any yaw) are turned into oriented boxes, `CoverGenerator` (pure, EditMode-tested) emits locations, `CoverDiscovery` filters them against the NavMesh and calls `CoverRegistry.Rebuild`, which retires the old locations (dropping their claims) and bumps a version the cover view watches. Face locations: every face of at least 1 m, 0.75 m off the face, spread at about 2 m spacing with a 0.5 m end margin. Tall walls (top above 1.2 m, at least 2 m long and twice as long as thick) also get four corner locations, 0.35 m inside each end of each long face, with `PeekDirection` along the wall past the end and `PeekPoint` 1.25 m out (kept only if walkable). Candidates off the NavMesh or within 1 m of an earlier one are dropped. Everything downstream is unchanged except its type: reservation, `MoveToCover`, the directional ray test (020), hostile search (021), parking and follow (022). Hostiles choose only Low locations. The view colours by state (a selected unit's destination is green) and shows type by shape (corner: peek arrow).
- **Why:** missions will be procedural (019, Direction), so cover must follow geometry; keeping generation pure and the NavMesh a predicate makes it testable and lets a mission generator call one method (`Discover()`) after it builds geometry and the NavMesh; a plain class lets any source (arena, generator, future tools) supply locations without a scene object; recording peek data now costs a few fields and makes the later peek behaviour an addition rather than a redesign.
- **Rejected:** surfaces that generate and register their own points (no cross-surface dedupe, unclear rebuild order, brings back self-registering points); deriving cover from NavMesh boundary edges (no height or wall-end meaning); keeping a `CoverPoint` MonoBehaviour created at runtime (a scene object per location for no gain); letting hostiles take corner or tall-face locations before peeking exists (they would stand hidden and then leave through the reposition phase).
- **Implications:** supersedes 019's representation (hand-placed `CoverPoint`s, per-point `hitChance`) and its rejection of runtime generation; 019's Direction bullet is done for automatic placement and corners, still open for a real peek behaviour and for non-box geometry. New obstacles need only a `CoverSurface`. Discovery assumes the NavMesh is current, so a procedural mission must build it first. Names are labels (`Cover_<surface>_<N|S|E|W>[Corner]<n>`), not keys, and may repeat on a yawed box. Hit chance is one generator setting. A rebuild frees every claim; a unit standing still on a location re-claims the equivalent new one, a walking cover order ends silently. Corner peek data is unused until a peek phase.
```

- [ ] **Step 2: Amend 019, 020 and 021 with one sentence each**

Append at the end of the "Implications"/last bullet of each record (do not rewrite the original text):
- 019: ` Superseded in part by 023 (2026-10-05): locations are now plain `CoverLocation`s discovered from `CoverSurface` geometry, the hand-placed points and the per-point `hitChance` are gone, and generation is no longer rejected.`
- 020: ` Since 023 the directional test lives on `CoverLocation.ProtectsFrom` (same ray, same rule).`
- 021: ` Since 023 the hostile search admits only Low locations (corner and tall-face locations wait for a peek behaviour).`
Also in 019's **Direction** bullet append: ` Update (023): automatic placement, including corner locations at the ends of tall walls, is built; a peek behaviour is still deferred.`

- [ ] **Step 3: Add the status line to the old cover spec**

At the top of `Docs/superpowers/specs/2026-10-05-cover-system-design.md`, directly under the title, add:

`> Revised by 2026-10-05-procedural-cover-design.md (decision 023): hand-placed points, the cover point component and the arena's 20 points are replaced by discovered `CoverLocation`s. Read both.`

- [ ] **Step 4: Commit**

```bash
git add Docs
git commit -m "$(cat <<'EOF'
Record decision 023, procedural cover discovery, and amend 019 to 021

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 10: Final verification

**Files:** none (report only; fix and re-run if anything fails).

- [ ] **Step 1: Full suites**

```bash
Tools/run-tests.sh EditMode
Tools/run-tests.sh PlayMode
```
Expected: `failed="0"`, no `error CS`. Report both totals against Task 1's baseline.

- [ ] **Step 2: Leftover scan**

```bash
grep -rn "class CoverPoint\|AddComponent<CoverPoint>\|CoverPoints" Assets/_Project --include=*.cs --include=*.unity | head
git status --short
ls Assets/_Project/Editor 2>&1 | head -1
git log --oneline c416407..HEAD
```
Expected: no matches; clean tree; no Editor folder; the commit list matches Tasks 1 to 9 (spec commit included).

- [ ] **Step 3: Console check with the arena loaded**

Run the prototype scene's PlayMode scene tests (already in step 1) and grep the PlayMode log for `Exception` and `Missing`:

```bash
grep -c "Exception" Logs/TestRun-PlayMode.log; grep -c "Missing" Logs/TestRun-PlayMode.log
```
Expected: no unexpected exceptions (tests that assert `LogAssert` expectations may log intentionally; compare against the baseline run of Task 1 Step 1 if a count is non-zero) and no Missing Script lines.

- [ ] **Step 4: Write the completion report for the owner**

Cover, in this order, the items the brief asks for: files created and changed; how locations are represented; how geometry produces candidates; low-cover and tall-wall/corner generation; how a procedural mission calls `Discover()`; directional evaluation and LOS interaction (unchanged); the defensive effect (unchanged hit chance); `MoveToCover` behaviour; reservation and occupancy lifecycle including rebuild; interaction with parking and follow (unchanged: `MoveToCover` is still an explicit order); enemy cover use (Low only) and why; how peeking is supported (corner data); the manual checks from spec section 10; known limitations (spec section 13 plus anything found); architecture concerns before the controller phase. Then stop. Do not start the next phase and do not merge: finishing the branch is a separate step (superpowers:finishing-a-development-branch) the owner approves.
