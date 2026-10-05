# Tactical Cover (Phase 6) — Design

Date: 2026-10-05 · Branch: `prototype/cover-system` · Status: implemented 2026-10-05 (see Docs/superpowers/plans/2026-10-05-cover-system.md)

Builds on Phases 1–5 (the five earlier specs in this folder and decisions 006–018 in `Docs/Decisions.md`). It adds one shared capability (cover evaluation and occupancy), one command (`MoveToCoverCommand`), the minimum hit-resolution rule cover needs, and basic cover use by ranged hostiles. The control architecture, the `Issue` path, line of sight, repositioning and companion autonomy are kept as they are.

## 1. Goal

Give the player meaningful positional choices in tactical combat without building a cover simulator:

- The arena has **cover points**: places a unit can stand, each protected by one obstacle.
- The player can **order units into cover** with the existing click-and-queue controls, paused or not.
- A unit standing at a cover point is **protected** against ranged fire that has to cross its obstacle: such shots hit with a reduced, configurable probability. Everything else (exposed targets, melee) hits as today.
- Cover points hold **one unit**: reserved while a unit walks there, occupied when it arrives, released when it leaves, is ordered elsewhere, is driven away, or dies.
- The **directly controlled character** and **companions** use the same evaluation: standing on a free cover point occupies it; a companion ordered into cover holds it instead of following or charging out.
- **Ranged hostiles** take nearby useful cover before opening fire; melee hostiles charge as before.
- Line of sight keeps its Phase 5 meaning: tall geometry still prevents shots; waist-high geometry lets shots through at reduced odds.

```
Decision sources                                     Shared unit capabilities
────────────────                                     ────────────────────────
PlayerCommandInput (clicks) ──┐
EnemyAI (hostiles) ───────────┼─ Issue(Attack/Move/MoveToCover) ─► CommandableUnit ─► UnitMover
CompanionAI (friendlies) ─────┤                                       │               UnitAttacker ◄── LineOfSight
AutoRetaliate ────────────────┘                                       │                   │ hit roll ◄── UnitCover NEW ◄── CoverPoint NEW
                                                                      │               Health          (reserve/occupy/release)   CoverRules NEW
                                                           SimulationTime.IsRunning                                             CoverRegistry NEW
```

Decision sources decide **what** a unit does. `CommandableUnit`, `UnitMover`, `UnitAttacker`, `UnitCover` and `Health` decide **how**. No decider evaluates cover geometry itself: they ask `CoverPoint`, `UnitCover` and `CoverRules`.

## 2. Scope

**In scope:** `CoverPoint`, `CoverRegistry`, `CoverRules` (pure selection and hit resolution), `UnitCover` (per-unit reservation and occupancy), `MoveToCoverCommand` and its handling in `CommandableUnit`, hit resolution in `UnitAttacker.TryAttack`, cover-aware clicks in `PlayerCommandInput`/`CommandResolver`, basic cover seeking in `EnemyAI`, a hold-cover rule in `CompanionAI` and `AutoRetaliate`, a cover view (markers while paused or claimed), HUD debug text, arena changes (three waist-high walls and 20 cover points), prefab changes (`UnitCover`), tests, decision records.

**Out of scope (later phases or never):** crouching, prone, leaning, peeking, blind fire, suppression, destructible cover, armour or damage reduction, body parts, critical hits, accuracy progression, ballistics, weapon classes, inventory, loot, abilities, status effects, stealth, formations, utility AI, automatic cover generation for arbitrary levels, companion cover seeking, hostile cover re-evaluation while fighting, squad cover assignment (several units to several points), procedural levels, production UI, animation, art, audio, multiplayer, ECS/DOTS, new packages, new input bindings.

## 3. Decisions made while designing

The brief fixed most of the design. These are the judgment calls it left open, each resolved the simplest reasonable way. The owner can overturn any of them before the plan is written.

| Topic | Decision |
|---|---|
| Cover representation | A `CoverPoint` component placed in the scene: its position is where the unit stands (ground level), its forward points **into** the obstacle (used to place the marker's direction nub), and it references that obstacle's collider. No automatic generation: the prototype arena gets 20 hand-placed points, generated once by the temporary scene builder, like the obstacles themselves. Rejected: generating points at runtime around tagged obstacles (needs rules for every obstacle shape and a NavMesh test per face, for a benefit the builder already gives us). |
| Where cover points are listed | `CoverRegistry` on `Systems` holds a serialized list of the scene's points, exactly as `Encounter` lists the two sides. Clicks, enemy searches, the view and auto-occupancy read it. Points do not register themselves (no `Find*`, no static state). It is a list, not a manager: it holds no logic beyond the list. |
| Is a unit "in cover"? | A unit is in cover when its `UnitCover` **occupies** a point: it claimed the point and stands within `occupyRadius` (0.6 m, flat x/z distance) of it. Occupancy is reached two ways with one rule: a `MoveToCover` order reserves the point and occupies it on arrival; a unit without a reservation that is **still** (flat speed below `stillSpeed`, 0.5 m/s) within 0.6 m of an unclaimed point (stopped there by direct control, at the end of a plain move, or where a chase ended) occupies it automatically. Walking through a point never claims it. Occupancy ends when the unit moves farther than `leaveRadius` (1.0 m) from the point, the point is destroyed or disabled, or the unit is disabled or dies. |
| Direction: when does cover protect? | `CoverPoint.ProtectsFrom(attacker, defender)` is true when a ray from the attacker's eye (pivot + 0.5 m, the sight eye) to the defender's **feet** (the defender's x/z at the point's ground height) hits the point's obstacle collider (`Collider.Raycast`: one collider, no layers, no other geometry). The obstacle must actually lie between the two: a shot from behind the defender cannot cross it, a flank shot past a pillar or along a wall misses it, a shot over a waist-high wall crosses it. With the 0.75 m stand offset the ray meets the obstacle's near face at height 1.125 / D (D the attacker's distance), so a 0.9 m wall registers at every distance beyond 1.25 m (closer than an attacker can stand, given the wall's 0.5 m thickness and the 0.5 m agent radius); a 0.6 m obstacle would register only beyond about 1.9 m. Rejected: an angular cone around the point's forward (redundant with the ray for every case in the arena, and it would have denied protection a wide wall actually gives at steep angles). |
| Defensive effect | **Hit probability.** A ranged shot at an unprotected target hits, as today. A ranged shot at a protected target hits with probability `CoverPoint.hitChance` (default 0.5, serialized per point: that is the "low/high" knob the brief allows, with no extra code). Melee ignores cover entirely. The roll is one `Func<float>` on `UnitAttacker` (default `UnityEngine.Random.value`), replaceable in tests. No damage reduction, no armour. |
| What `TryAttack` means now | It returns true when a **shot is fired** (cooldown consumed), hit or miss; a miss raises a new `Missed` event; `Attacked` still means a hit. `CommandableUnit` resets its reposition counter on a fired shot (the unit has a firing position; whether the shot lands is cover, not positioning). `UnitAttacker` counts `ShotsFired` and `Hits` so the HUD can show the cover effect. |
| Cover and line of sight | Unchanged Phase 5 sight test first, then cover. A tall obstacle blocks the eye-to-pivot ray, so no shot is fired and cover never comes up. A waist-high obstacle (0.9 m) lets the 1.5 m eye line reach a 1 m pivot, so the shot is fired and the cover roll applies. Cover never grants or removes line of sight. Points at tall obstacles (`Pillar_G`, `Crate_J`, `Barrier_I`) are therefore **hiding** spots: straight on, nothing can shoot at the unit; from the flanks it is exposed. Points at the waist-high walls are **firing** spots: the unit shoots out and is shot at with reduced odds. |
| The command | A dedicated `MoveToCoverCommand(CoverPoint)`, not a flag on `MoveCommand`: it has semantics a move has not (reserve at start, occupy on arrival, release on cancel, refuse when the point is claimed), and the HUD, queue view and AI states can name it. |
| When a reservation is made | When the order **starts** (`TryStart`), not when it is queued. A Shift-queued cover order behind a move reserves nothing until the move finishes; if the point is taken by then, the order is skipped like any order that can no longer start. Rejected: reserving at queue time (reservations held by pending orders would have to be released on every path that clears a queue). |
| Reservation and occupancy lifetimes | A **reservation** lives exactly as long as its cover order is current: `Replace`, `Stop`, death, direct control, a failed arrival and a walk that makes no progress for 3 s (another unit standing on the spot) all release it (`UnitCover.ReleaseReservation`, called from `CommandableUnit`'s existing `StopAll`/`TryStart` paths, always **after** the replacing order has been accepted, so a refused order changes nothing). An **occupancy** lives as long as the unit stands there: a new move or attack order does not release it (an attack from cover keeps the unit in place), moving away does, and so does a cover order to a different point. So a unit ordered to attack a target in range stays in cover; a melee unit charging out leaves it a few frames later. |
| Ordered versus incidental occupancy | `UnitCover.OccupiedByOrder` remembers whether the occupancy came from a `MoveToCover` arrival or from standing there. Only an **ordered** occupancy changes autonomous behaviour (companion hold, retaliation hold); an incidental one only affects incoming fire. Ordering a unit onto the point it already stands on upgrades it to ordered. |
| One unit per point | `CoverPoint.TryClaim(UnitCover)` succeeds only when the point is unclaimed or already claimed by the same unit. A `MoveToCover` on a point claimed by someone else is refused (`Issue` returns false, no log). Auto-occupancy only takes unclaimed points. |
| Clicking a cover point | A ground click within `coverClickRadius` (1 m) of a cover point is a `MoveToCover` on that point; otherwise it is a move, as before. Markers have no colliders, so the raycast still hits the ground. Attack on a living `Health` still wins. No new input action. |
| A group ordered to one point | `GroupOrders` is unchanged: the cover command goes to every unit in order through the existing generic path; the first whose `Issue` succeeds reserves the point and every other unit is refused with its orders unchanged (in `Append` mode a unit that already has orders queues it and is skipped when its turn comes if the point is held by then; an idle unit starts it at once and is refused exactly as under `Replace`, since `Issue` only queues behind a current order, 006). A click on a point held by a unit outside the group is refused for all. One unit per marker, as the HUD hint says. Rejected: moving the rest onto a lattice around the point (the lattice is not oriented to the wall, so slots land on the exposed side) and assigning neighbouring points to the rest (squad cover assignment, not in the brief). |
| Hostile cover use | When an idle **ranged** hostile acquires a target it first looks for the **nearest** registry point within `coverSearchRange` (8 m) that is unclaimed or its own, protects from the target (the point itself as the hypothetical defender), lets it attack the target from there (`CanAttackFrom`: in range and in sight from the eye a unit would have there) and is reachable. If one exists it issues `MoveToCover(point)` then `Attack(target)` appended; otherwise `Attack(target)` as today. A hostile already standing on a useful point keeps it (the cover order on its own point completes at once). Melee hostiles never look. Cover is chosen at acquisition only; the hostile keeps shooting from it even if the target moves out of its protection (recorded limitation). |
| Companions and cover | Two rules added to `CompanionAI.Think`, both keyed on `OccupiedByOrder`: a companion holding ordered cover does not **follow**, and it **assists** only against a target it can attack from where it stands (`UnitAttacker.CanAttack`); a melee companion in cover therefore stays put while a ranged one fires. The check is made when the companion issues its own order (acquisition only, as every rule in 018): a target that later walks out of range or sight is pursued by the existing attack phases, which walk the companion out of cover and release it. Explicit orders keep winning over everything, as in 018. An incidental occupancy changes nothing: the companion follows as before and the leave rule releases the point. No companion cover seeking. |
| Retaliation from cover | `AutoRetaliate` keeps its rule (fight back when idle) with one exception: a unit holding **ordered** cover retaliates only against an attacker it can attack from where it stands. A melee unit the player put behind a wall does not charge a shooter it cannot reach; a ranged unit with the shooter in range and sight fires back from cover; a unit hit by an adjacent melee attacker fights back as before. Checked when the retaliation is issued, like assist. Without this rule the first ranged hit would undo every cover order given to a melee unit. |
| Direct control | Nothing new: `UnitCover`'s auto-occupancy and leave rules are the direct-control cover system. Driving away releases the occupancy on the next simulation frames. A `MoveToCover` issued to the controlled character works like any order (held keys still win while held, 011). |
| Visualization | One `CoverView` on `Systems` (debug, like the other views) keeps one collider-free marker per point: a flat disc at the stand point and a small nub on the obstacle side showing which way the cover faces. Markers show while **paused**, and in real time only for points that are **claimed**. Colour: available white, reserved yellow, occupied cyan, through a `MaterialPropertyBlock` on one marker material. The selected unit's own point is identified by its HUD label and by `CommandQueueView`'s line to it; no fourth colour. |
| Where the numbers live | Per point: `hitChance` 0.5. Per unit (`UnitCover`): `occupyRadius` 0.6 m, `leaveRadius` 1.0 m, `stillSpeed` 0.5 m/s. Per hostile (`EnemyAI`): `coverSearchRange` 8 m. Input: `coverClickRadius` 1 m. All serialized, none global. |
| Arena | Three waist-high walls (0.9 m) on the squad's routes and in the hostiles' area, with cover points on both long faces (two per face on the 3–4 m walls, one per face on the 2 m wall, so neighbouring points are never closer than 2 m); points around `Pillar_G`, `Crate_J` and on the south face of `Barrier_I` (tall: sight blocked straight on, flanks open). 20 points. Unit stats and starts unchanged. |

## 4. Architecture

All runtime code stays in the `Blackglass` assembly and namespace; the new cover types go in `Scripts/Cover/`, except `UnitCover`, a unit capability, which sits with the other unit components in `Scripts/Units/`. No new packages or assembly references. Components reference each other through serialized fields plus `internal Initialize(...)` for tests; no singletons or `Find*` in game code. Every simulation `Update` checks `SimulationTime.IsRunning`. All distances between a unit and a point are **flat** (x/z): the unit's transform is at pivot height (1 m) while a point is at ground level.

### 4.1 `CoverPoint` — `Cover/CoverPoint.cs` (new)

```csharp
/// A place a unit can stand to be protected by one obstacle. Position: the transform (ground level). Forward: the
/// flat direction into the obstacle (marker nub only). Holds its claimant; UnitCover is the only writer.
public sealed class CoverPoint : MonoBehaviour
{
    [SerializeField] Collider obstacle;                        // the geometry that protects; required
    [SerializeField, Range(0f, 1f)] float hitChance = 0.5f;    // a protected shot's chance to hit

    public Vector3 Position => transform.position;
    public Vector3 Forward { get; }               // transform.forward flattened and normalised
    public float HitChance => hitChance;
    public Collider Obstacle => obstacle;
    public UnitCover Claimant { get; private set; }
    public bool IsClaimed => Claimant != null;
    public bool IsClaimedBy(UnitCover unit) => Claimant == unit;
    public bool IsOccupied => Claimant != null && Claimant.Status == CoverStatus.Occupied;

    /// True when the ray from the attacker's eye (pivot + LineOfSight.EyeHeight) to the defender's feet
    /// (defender x/z at this point's height) hits the obstacle. With no obstacle wired: false, with one warning.
    public bool ProtectsFrom(Vector3 attackerPivot, Vector3 defenderPivot);

    internal bool TryClaim(UnitCover claimant);   // true when unclaimed or already this claimant's
    internal void Release(UnitCover claimant);    // no-op unless claimant holds it
    internal void Initialize(Collider obstacleCollider, float chanceToHit = 0.5f);
}
```

`Collider.Raycast` tests only the wired collider, so units, other obstacles and triggers never confuse it. Points live on their own GameObjects under a `CoverPoints` root, never as children of a scaled obstacle. For a pillar 1.5 m wide with the 0.75 m stand offset the open flank begins past 45° off the forward (53° for the 2 m crate): tests avoid those exact angles.

### 4.2 `CoverRules` — `Cover/CoverRules.cs` (new, static, pure, EditMode-tested)

```csharp
public static class CoverRules
{
    /// A shot lands unless the target is protected and the roll (0..1) is at or above the hit chance.
    public static bool ResolveHit(bool isProtected, float hitChance, float roll);

    /// The nearest point (flat distance from `from`, within maxDistance) that `accept` admits, or false. A candidate
    /// that is null (destroyed) or inactive in the hierarchy is skipped before its position is read, and one out of
    /// range or not nearer than the best accepted so far is skipped before `accept` is called (as EnemyAI.FindTarget
    /// does), so the predicate runs only on live points that could win; callers still order it cheap to dear.
    public static bool TryChooseNearest(IReadOnlyList<CoverPoint> points, Vector3 from, float maxDistance,
        Func<CoverPoint, bool> accept, out CoverPoint chosen);
}
```

### 4.3 `CoverRegistry` — `Cover/CoverRegistry.cs` (new, on `Systems`)

```csharp
/// The scene's cover points, as a serialized list. Holds state only, like Encounter.
public sealed class CoverRegistry : MonoBehaviour
{
    [SerializeField] List<CoverPoint> points = new List<CoverPoint>();
    public IReadOnlyList<CoverPoint> Points => points;
    internal void Initialize(params CoverPoint[] scenePoints);
}
```

### 4.4 `UnitCover` — `Units/UnitCover.cs` (new, on every unit)

```csharp
public enum CoverStatus { None, Reserved, Occupied }

/// The unit's cover: the one point it has reserved or occupies. The only writer of CoverPoint claims. Reservation is
/// driven by CommandableUnit (a MoveToCover order); occupancy by standing at the point, by order or by chance.
/// Health is optional, as everywhere: looked up lazily, Died subscribed to only when present.
public sealed class UnitCover : MonoBehaviour
{
    [SerializeField] CoverRegistry registry;                  // optional: without it, no automatic occupancy
    [SerializeField, Min(0f)] float occupyRadius = 0.6f;
    [SerializeField, Min(0f)] float leaveRadius = 1f;
    [SerializeField, Min(0f)] float stillSpeed = 0.5f;        // below this flat speed a unit counts as standing

    public CoverPoint Point { get; }       // reserved or occupied point, else null
    public CoverStatus Status { get; }
    /// True while an occupancy came from a MoveToCover arrival (set by TryOccupy, false for incidental occupancy,
    /// cleared by Release).
    public bool OccupiedByOrder { get; }
    public bool IsWired => registry != null;

    /// Claims the point as reserved. If another unit holds it: false, nothing changes (including any point this
    /// unit already holds). If it is this unit's own point: the status is kept (an occupied point stays occupied).
    /// Otherwise any other point this unit held (reserved or occupied) is released first, so a unit never holds
    /// two points.
    public bool TryReserve(CoverPoint point);
    /// Reserved or occupied, and within occupyRadius → Occupied with OccupiedByOrder = true; returns whether the
    /// unit now occupies the point.
    public bool TryOccupy();
    /// Releases only a reservation; an occupancy is left alone.
    public void ReleaseReservation();
    /// Releases whatever is held.
    public void Release();
    /// Occupied, and the point protects this unit from an attacker at that pivot.
    public bool IsProtectedFrom(Vector3 attackerPivot);
    public float HitChance => Point != null ? Point.HitChance : 1f;

    internal void Initialize(CoverRegistry coverRegistry, float occupy = 0.6f, float leave = 1f, float still = 0.5f);
}
```

`Update` (only while `SimulationTime.IsRunning`; the flat speed is this frame's flat displacement over `Time.deltaTime`, tracked by the component itself so paths, direct steering and avoidance pushes are all treated alike):

```
Point held but destroyed or inactive                        → Release()          (cover became invalid)
Occupied and flat distance to Point > leaveRadius           → Release()
None, registry wired, speed < stillSpeed                    → the nearest unclaimed point within occupyRadius
                                                              (CoverRules.TryChooseNearest, so destroyed and inactive
                                                              points are skipped), if any: TryClaim, Status = Occupied,
                                                              OccupiedByOrder = false
Reserved                                                    → nothing (CommandableUnit calls TryOccupy on arrival)
```

`OnEnable` subscribes to `Health.Died` → `Release()` when the unit has a Health; `OnDisable` releases and unsubscribes. Death is covered twice: `Health` deactivates the unit so `OnDisable` fires, and the `Died` subscription handles a Health with `disableOnDeath` off. A unit that reappears starts with `None`.

### 4.5 Commands — `Commands/UnitCommands.cs` (changed)

```csharp
/// Walk to a cover point and occupy it. Refused when another unit holds the point.
public sealed class MoveToCoverCommand : UnitCommand
{
    public MoveToCoverCommand(CoverPoint point);   // ArgumentNullException on null
    public CoverPoint Point { get; }
}
```

### 4.6 `CommandableUnit` — `Units/CommandableUnit.cs` (changed)

`[RequireComponent(typeof(UnitMover), typeof(UnitAttacker), typeof(UnitCover))]`; `Cover` looked up lazily like the others and exposed read-only (`public UnitCover Cover`) so AI and views read cover state through the unit. New read-only `public Health AttackTarget`: the target of the current `AttackCommand`, else of the first pending one while a `MoveToCoverCommand` is current, else null. `EnemyAI.Target`, `CompanionAI.IsEngaged` and `CompanionAI`'s read of the leader's target all use it, so a hostile walking to cover with its attack queued counts as engaged exactly as a chasing one did in Phase 5, and a leader walking to cover with an attack queued already gives companions their assist target. A cover walk also keeps `bestDistance`/`lastProgressTime` (flat distance to the point, reset in `TryStart`) for the stall exit below; `const float CoverWalkTimeout = 3f` sits beside `RepositionWalkTimeout`.

| Path | Added behaviour |
|---|---|
| `Issue` | `MoveToCoverCommand` joins the accepted types. |
| `CanStart` | Cover: the point exists and is active, is unclaimed or ours, and `Mover.CanMoveTo(point.Position)` (queued cover orders are vetted like queued moves). |
| `TryStart` | Every check runs before any cover state changes, so a refused start leaves the unit, its orders and its claims exactly as they were (the documented contract). Cover: `CanStart` test, then `Mover.MoveTo(point.Position)` (if refused, return false: nothing has been touched; `MoveTo` returns `SetDestination`'s result, which can be false even after `CanMoveTo` passed), then `Cover.TryReserve(point)` (cannot fail: `CanStart` just verified the point is unclaimed or ours; it drops a different point the unit held), then `ResetAttack()`. Move: after `Mover.MoveTo` succeeded, `Cover.ReleaseReservation()` next to `ResetAttack()`. Attack: after `IsAttackable` passed, the same. |
| `Update` | Cover, in this order: (1) if `Cover.Status` is `None` the point vanished mid-walk (`UnitCover.Update` released it because it was destroyed or disabled): `Mover.Stop()`, `StartNext()`, no warning, as an attack on a vanished target ends. (2) When `Mover.HasArrived`: `Cover.TryOccupy()`; if that fails (a partial path ended more than 0.6 m from the point: another NavMesh island, or ringed by walls) `Cover.ReleaseReservation()` and log one warning; either way `StartNext()`. (3) Otherwise, if the flat distance to the point has not fallen by more than 0.05 m for `CoverWalkTimeout` (3 s of scaled time: another unit stands on the spot and agent avoidance holds this one off, so `HasArrived` never turns true) `Cover.ReleaseReservation()` and `StartNext()`, no log (an expected situation in play, like Phase 5's re-search). Scaled time never advances while paused, so a pause never counts as a stall. |
| `StopAll` | `Cover.ReleaseReservation()`. Covers Stop, death and direct control (which already route through `StopAll`). |
| `UpdateAttack` | `if (Attacker.TryAttack(target)) repositionsWithoutShot = 0;` keeps its shape; the comment now says "a shot was fired". |

Pause needs nothing: a cover order issued while paused reserves its point at once (as a move issued while paused already requests its path), walks on resume, and occupies on arrival. The view therefore shows the reservation during planning.

### 4.7 `UnitAttacker` — `Units/UnitAttacker.cs` (changed)

```csharp
public event Action<Health> Attacked;   // unchanged: a hit
public event Action<Health> Missed;     // new: a shot that cover turned away
public int ShotsFired { get; }          // debug counters
public int Hits { get; }
/// True when the target is in cover against a shot from this unit's position: ranged role, target has a UnitCover
/// that occupies a point, and that point protects it from here. Melee: always false. hitChance is that point's
/// HitChance when in cover, else 1. The HUD uses the same overload.
public bool IsTargetInCover(Health target, out float hitChance);
public bool IsTargetInCover(Health target) => IsTargetInCover(target, out _);
internal Func<float> HitRoll { get; set; }   // default () => UnityEngine.Random.value; tests inject

public bool TryAttack(Health target)
{
    if (target == null || !target.IsAlive || !CanAttack(target) || Time.time < nextAttackTime) return false;
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
        Missed?.Invoke(target);
    return true;
}
```

Melee never misses (`IsTargetInCover` is false for melee), so Phase 4 melee tests keep their exact timings. Every ranged test that expects hits either leaves the target exposed (unchanged behaviour) or injects a roll.

### 4.8 Player input — `Controls/PlayerCommandInput.cs`, `Controls/CommandResolver.cs` (changed)

- `PlayerCommandInput` gains `[SerializeField] CoverRegistry coverRegistry` and `coverClickRadius = 1f`. In `HandleClick`, after the friendly-selection branch: `CoverRules.TryChooseNearest(coverRegistry.Points, hit.point, coverClickRadius, _ => true, out var cover)` (null registry: no cover), then `CommandResolver.Resolve(clickedHealth, hit.point, cover)`.
- `CommandResolver.Resolve(Health clicked, Vector3 point, CoverPoint cover = null)`: living `Health` → `AttackCommand`; else `cover != null` → `MoveToCoverCommand(cover)`; else `MoveCommand(point)`.
- `GroupOrders` is unchanged (see the group row in section 3).
- Input never touches `UnitCover` or `CoverPoint` state; it only names the point in a command.

### 4.9 `EnemyAI` — `AI/EnemyAI.cs` (changed)

- `[SerializeField] CoverRegistry coverRegistry` (optional; `Initialize` gains it) and `coverSearchRange = 8f`.
- `EnemyState` gains `Cover` (current order is a `MoveToCoverCommand`); `DeriveState(alive, current, phase)` returns it before the attack cases. `Target` returns `Unit.AttackTarget`, so the HUD reads `Cover -> <target>` during the walk.
- `Update`, after `FindTarget` returns a target:

```
if Attacker.NeedsLineOfSight and coverRegistry != null
   and CoverRules.TryChooseNearest(coverRegistry.Points, position, coverSearchRange, IsUsefulCover(target), out point):
    Unit.Issue(new MoveToCoverCommand(point)); Unit.Issue(new AttackCommand(target), IssueMode.Append)
else:
    Unit.Issue(new AttackCommand(target))

IsUsefulCover(point): (!point.IsClaimed || point.IsClaimedBy(Unit.Cover))
    && point.ProtectsFrom(target.position, point.Position + up * Mover.PivotHeight)      // one Collider.Raycast
    && Attacker.CanAttackFrom(point.Position + up * Mover.PivotHeight, target)           // range + sight ray
    && Mover.CanReach(point.Position)                                                    // path, last
```

If the cover order is refused (someone claimed the point between the search and the issue), the attack is issued alone.

### 4.10 `CompanionAI` — `AI/CompanionAI.cs` and `AutoRetaliate` — `Units/AutoRetaliate.cs` (changed)

- `CompanionAI.Think`, assist step: after `ChooseAssistTarget` returns a target, `if (Cover.OccupiedByOrder && !Attacker.CanAttack(target)) return;` (hold the cover rather than charge). Between assist and follow: `if (Cover.OccupiedByOrder) return;`. A running own assist is never re-examined (018: acquisition only), so a target that leaves range or sight pulls the companion out through the normal attack phases. `CompanionState` is unchanged (a companion holding cover with no order reads `Idle`; the HUD's cover text says why it stays). `IsEngaged` becomes `hostileUnit.AttackTarget != null`, and the leader's target in `ChooseAssistTarget` is `activeCharacter.Unit.AttackTarget`. `YieldToControl` is unchanged: a companion in cover that becomes the controlled character keeps its occupancy until driven away.
- `AutoRetaliate.OnAttackedBy`, after the existing guards: `if (cover.OccupiedByOrder && !attacker.CanAttack(attackerHealth)) return;` (`UnitCover` and `UnitAttacker` cached in `Awake`, as the others are).

### 4.11 Views — `DebugUI/CoverView.cs` (new, on `Systems`), `DebugUI/CommandQueueView.cs`, `DebugUI/AttackLineView.cs`, `DebugUI/PrototypeHud.cs` (changed)

- `CoverView`: serialized `CoverRegistry`, `TacticalPause`, `Material markerMaterial`. `Start` builds one marker per registry point (a cylinder disc 0.5 m × 0.01 m at the point plus a 0.15 × 0.15 × 0.3 m cube nub 0.4 m along `Forward`; colliders destroyed; shadows off). `LateUpdate` sets each marker active when paused or the point is claimed (a destroyed or inactive point's marker is hidden), and its colour by state (available white, reserved yellow, occupied cyan) through a `MaterialPropertyBlock` (`_BaseColor`). Markers never block clicks. `internal` counters (`VisibleMarkerCount`) for tests.
- `CommandQueueView`: a `MoveToCoverCommand` whose point is alive (`Point != null && Point.isActiveAndEnabled`, the guard the attack branch applies to its target) adds a line point and marker at the cover point, like a move; a vanished point adds nothing.
- `AttackLineView`: also draws its line on `Missed` (same material; the HUD counters tell hits from misses).
- `PrototypeHud`: unit labels gain a cover suffix from `DescribeCover(CoverStatus status, string pointName, bool byOrder)` → `"Cover: Cover_LowWall_L_S1 (occupied)"` / `"(occupied, ordered)"` / `"(reserved)"`, nothing when `None` or when the point is null (a point destroyed during a pause draws nothing until `UnitCover` releases it on resume); a ranged unit with an attack order appends `AppendTargetCover(text, bool inCover, float hitChance)` (from `IsTargetInCover(target, out hitChance)`) → `" target in cover 50%"` or `" target exposed"`; a ranged unit that has fired appends `AppendHits(text, hits, shots)` → `" hits 3/7"`. Hostiles: `DescribeEnemy` prints `Cover -> <target>` for the new state. Control hints gain `Left-click cover marker: move into cover (one unit per marker; markers show while paused)`. All formatters static, invariant-culture, EditMode-tested.

### 4.12 Unchanged

`DirectControlInput`, `ActiveCharacter`, `UnitSelection`, `SelectableUnit`, `SelectionIndicator`, `ActiveCharacterMarker`, `GroupOrders`, `GroupMoveOffsets`, `CommandQueue`, `TacticalPause`, `SimulationTime`, `TacticalCameraController`, `HitFlash`, `DeathMarker`, `Health` (still optional on every unit), `Encounter`, `LineOfSight`, `FiringPositionFinder`, `UnitMover`, `ControlCycle`, `ClickDragDetector`, `ScreenBox`, `InputActionUtility`. The input actions asset is unchanged.

## 5. Rules summary

1. A cover point protects a unit occupying it from any attacker whose eye-to-feet ray crosses the point's obstacle. Otherwise the unit is exposed.
2. A ranged shot at a protected target hits with the point's `hitChance` (0.5); at an exposed target it hits as before. Melee is unaffected. Sight is checked first, as in Phase 5: no sight, no shot.
3. `MoveToCover(point)` reserves the point when it starts and is refused if another unit holds it; the unit occupies the point on arrival; the reservation is released whenever the order stops being current, including when the walk makes no progress for 3 s or the point vanishes; the occupancy is released when the unit moves more than 1 m away, takes a cover order to another point, loses the point (destroyed or disabled), is disabled, or dies.
4. A unit without a reservation that stands still within 0.6 m of an unclaimed point occupies it, however it got there; walking through a point claims nothing.
5. A ground click within 1 m of a cover point orders cover; with several units selected, the first that can takes the point and the rest keep their orders.
6. A ranged hostile that acquires a target first walks to the nearest reachable point within 8 m that is unclaimed or its own, protects from the target and lets it shoot the target, then attacks; melee hostiles attack at once.
7. A unit holding ordered cover does not follow, and assists or retaliates only against a target it can attack from where it stands (checked when that order is issued; a target that then leaves range or sight is pursued). Explicit orders are unchanged.
8. While paused nothing in rules 1–7 advances; cover orders issued while paused reserve their point immediately and the view shows every point and its state.

## 6. Scene and assets

Generated by a temporary editor script (`Assets/_Project/Editor/CoverSceneBuilder.cs`, created and deleted within the task, never committed), as in Phases 2–5.

**Prefabs:** `FriendlyUnit.prefab` and `HostileUnit.prefab` gain `UnitCover` (defaults; `registry` wired per scene instance).

**`Prototype.unity`:**

- `Systems` gains `CoverRegistry` (list of the 20 points) and `CoverView` (wired to the registry, `TacticalPause`, a new `CoverMarker.mat`).
- `PlayerCommandInput` wired to the registry; every `EnemyAI` and every `UnitCover` wired to the registry.
- New waist-high walls under `Environment`, `Obstacle.mat`, 0.9 m tall so the 1.5 m eye line clears them to a 1 m pivot at every range and the eye-to-feet cover ray hits them at every range beyond 1.25 m:
  - `LowWall_L` at (6.5, 0.45, −3), scale (3, 0.9, 0.5) (x 5–8, z −3.25…−2.75): on the squad's south-east route past the central wall, 1.84 m clear of the wall's south-east corner (3.18, −2.47), so that corridor stays walkable (0.84 m) after the 0.5 m erosion; the 1.25 m diagonal gap to `Crate_J`'s corner (9, −4) may or may not survive the bake and is not relied on.
  - `LowWall_M` at (−4, 0.45, 6), scale (4, 0.9, 0.5) (x −6…−2): the west route, between `Barrier_I` and `Obstacle_D`, 2.6 m from the central wall's north-west end.
  - `LowWall_N` at (8.5, 0.45, 6), scale (2, 0.9, 0.5) (x 7.5–9.5): in the hostiles' area, 2.25 m north of `Obstacle_E`, 2 m west of `Obstacle_F` (that gap stays walkable), and 1.5 m east of the scene test destination (6, 0, 6) used by `Move_AroundTheCentralWall_Arrives` (the eroded NavMesh edge at x 7.0 is 1 m from it, well outside that test's 0.3 m arrival tolerance); `HostileUnit_1` starts 1.75 m north of it.
- Cover points (`CoverPoints` root at the origin; each point a child named `Cover_<obstacle>_<face><n>`, at ground level, forward into the obstacle, `obstacle` wired to that obstacle's `BoxCollider`, `hitChance` 0.5). Stand points are 0.75 m from a face (agent radius 0.5 m plus margin), so they lie on the NavMesh outside the erosion band:
  - `LowWall_L`: S1 (5.5, 0, −4) and S2 (7.5, 0, −4) facing +z; N1 (5.5, 0, −2) and N2 (7.5, 0, −2) facing −z.
  - `LowWall_M`: S1 (−5, 0, 5), S2 (−3, 0, 5) facing +z; N1 (−5, 0, 7), N2 (−3, 0, 7) facing −z.
  - `LowWall_N` (2 m long, so one point per face): S (8.5, 0, 5) facing +z; N (8.5, 0, 7) facing −z. `HostileUnit_1` starts 1.1 m from N, outside the 0.6 m occupy radius.
  - `Pillar_G` (tall, 1.5 m wide): S (−2, 0, −7.5) facing +z; N (−2, 0, −4.5) facing −z; W (−3.5, 0, −6) facing +x; E (−0.5, 0, −6) facing −x.
  - `Crate_J` (tall, 2 m): S (10, 0, −6.75) facing +z; N (10, 0, −3.25) facing −z; W (8.25, 0, −5) facing +x; E (11.75, 0, −5) facing −x.
  - `Barrier_I` (tall, 6 m wide): S1 (−9, 0, 0.75), S2 (−7, 0, 0.75) facing +z.
- Rebake the NavMesh and save `Scenes/Prototype/NavMesh-Environment.asset`.
- The builder asserts after the bake that every point has a walkable NavMesh point within 0.5 m, and that `NavMesh.SamplePosition((4.1, 0, −2.6), 0.25 m)` succeeds: the midpoint of the corridor between the central wall's south-east corner and `LowWall_L`, 0.92 m from each, which lies on the mesh only if the 0.84 m eroded corridor survived the bake (a path test would pass round the wall's other end regardless).

Situations the arena creates: a unit at `LowWall_M` S1 is protected from the north and exposed from the south; `Pillar_G`, `Crate_J` and `Barrier_I` points block sight straight on and leave the flanks open (hiding spots); `LowWall_N`'s south point is the squad's cover against the hostiles and its north point is `HostileUnit_3`'s cover when the first friendly it sees is south of the wall, in the corridor between `Obstacle_E` and `LowWall_N` or in the lane beside `Obstacle_F` (a friendly first seen north of `Pillar_H` draws a plain attack, because no point within 8 m protects from there); the squad and the hostiles compete for `LowWall_L`'s and `LowWall_N`'s points from opposite sides.

## 7. Testing and validation

Baseline before this phase (measured 2026-10-05 on `main`): EditMode 239/239, PlayMode 213/213. Scene tests fail on any logged error. No existing fixture gains a Health or changes meaning: `UnitCover` requires nothing.

**EditMode**

- `CoverRulesTests` (new): `ResolveHit` tables (exposed always hits; protected hits below the chance, misses at and above it); `TryChooseNearest` picks the nearest accepted point, skips rejected and out-of-range ones, skips a null entry and a `DestroyImmediate`d point without calling `accept`, never calls `accept` on a candidate farther than one already accepted, false when none.
- `CoverPointTests` (new): claim rules (unclaimed, own, other), `IsClaimedBy`, release by the wrong claimant is a no-op, `ProtectsFrom` without an obstacle is false and warns once.
- `UnitCoverTests` (new): reserve; reserve refused when another unit holds the point and nothing changes; reserving a second point releases the first; re-reserving an occupied own point keeps it occupied; `ReleaseReservation` leaves an occupancy alone; `Release` clears everything including `OccupiedByOrder`; `HitChance` falls back to 1.
- `CommandableUnitTests`: `UnitCover` is added by `RequireComponent`; a `MoveToCoverCommand` is accepted as a type; on a claimed point `Issue` is false and the orders are unchanged; without a NavMesh a cover order is refused silently and the point stays unclaimed; `AttackTarget` reads a current attack and is null otherwise.
- `CommandResolverTests`: cover beats move, attack beats cover.
- `EnemyAITests`: `DeriveState` → `Cover`.
- `UnitAttackerTests`: `IsTargetInCover` is false for melee and for a target without `UnitCover`.
- `PrototypeHudTests`: `DescribeCover` (three forms, and empty for a null point name), `AppendTargetCover`, `AppendHits`, `Cover -> target`.

**PlayMode** (`TestWorld` gains `CreateObstacle(position, scale)` returning the box so tests can wire its collider, `CreateCoverPoint(position, forward, obstacle)` which takes the stand position verbatim, `CreateRegistry(points)`, and `CreateFighter`/`CreateHostile`/`CreateCompanion` wire a registry when given one). Test walls stand on the ground like the scene's: a 0.9 m wall is at y 0.45 with scale y 0.9. Units that must stand somewhere specific are spawned there, never teleported.

- `CoverPointPlayModeTests`: `ProtectsFrom` over a 0.9 m wall at (0, 0.45, 0), scale (4, 0.9, 0.5), with the point 0.75 m from its face, from 2 m and 8 m straight on (true: the eye-to-feet ray meets the near face at 0.56 m and 0.14 m); from 55° on the flank of a 1.5 m pillar with the point 0.75 m from its face (false: the near corner is at 45°, so the ray passes about 0.2 m clear of it); from 30° on the same point (true); from behind (false).
- `CoverOrderPlayModeTests`: `MoveToCover` reserves on issue, the unit arrives and the status is `Occupied` with `OccupiedByOrder`; a second unit ordered to the same point is refused and keeps its orders; three idle units given the same cover command: exactly one reserves it and the other two stay idle; two busy units Shift-ordered to one point both queue it, the first to finish reserves it and the second's is skipped; replacing the order with a move releases the reservation, and replacing it with a refused move (off-mesh destination, the existing warning) keeps it; a cover order to a second point releases the first; `Stop` releases a reservation and leaves an occupancy; the unit dying releases its occupancy; walking away (a move 5 m off) releases it; direct control (move intent held) drops the order, and steering away later releases the occupancy; a unit moved by a plain `MoveCommand` onto an unclaimed point occupies it after it stops, without `OccupiedByOrder`; a unit walking through a point on its way elsewhere never claims it; `Append`ed cover behind a move runs after it; a cover order issued while paused reserves immediately, nothing moves for one real second, and after resume the unit occupies then runs an appended attack; destroying the occupied point releases it; destroying the point mid-walk ends the order silently (no warning) and an appended move runs; a second fighter standing on the point itself (the walker reserved it first, so it cannot claim it; agent avoidance keeps the walker about 1 m off) makes the cover order give up within about 4 s with no log, releasing the reservation, and the appended move runs. The plain-move occupancy, walk-through, walk-away, death and destroyed-point cases live in `UnitCoverPlayModeTests` (they need no order).
- `CoverCombatPlayModeTests`: a ranged fighter with an always-miss roll deals no damage to a target occupying a point behind a 0.9 m wall, and with an always-hit roll it does; a second ranged fighter spawned at the target's flank (past the wall's end) hits with the always-miss roll; a melee fighter hits a covered target; a 2 m wall still blocks the shot entirely (no shot fired, counters unchanged); `Missed` fires and `ShotsFired`/`Hits` count.
- `AutoRetaliatePlayModeTests`: a melee unit holding ordered cover hit by a ranged attacker 6 m away stays in cover with no order; the same unit hit by an adjacent melee attacker fights back; a unit standing incidentally on a point charges the shooter as before.
- `EnemyAIPlayModeTests`: a 0.9 m wall at (0, 0.45, 0), scale (4, 0.9, 0.5) (x −2…2, z −0.25…0.25), with one point at (0, 0, 1) facing −z, a ranged hostile (range 8) at (0, 0, 5) and a friendly at (0, 0, −5) (10 m apart, inside the 12 m detection range, outside the 8 m attack range): the hostile walks to the point (state `Cover`, `Target` the friendly during the walk), then fires from 6 m; a melee hostile in the same layout goes straight at the friendly and never claims a point; a ranged hostile with the only point claimed attacks without cover; a ranged hostile already occupying a useful point attacks from it without moving; an idle hostile left standing beside a free point occupies it.
- `CompanionAIPlayModeTests`: a companion ordered into cover 10 m from the leader stays there for 2 s after the order finishes (no follow move); a melee companion holding ordered cover does not assist against a hostile 6 m away that is attacking the leader; once ordered away it follows again; a companion whose follow move ends on a free point (incidental occupancy) still follows when the leader walks on.
- `CombatPausePlayModeTests`: a reservation made before a pause is unchanged after one real second.
- `CoverViewTests` (new): markers exist for every point, none has a collider, all are visible while paused, only claimed ones in real time, colours follow state.

**Scene** (`PrototypeSceneTests`): wiring (`UnitCover` on every unit wired to the registry, `CoverRegistry` with 20 points each with an obstacle and a walkable NavMesh point within 0.5 m, `CoverView` present, the three low walls present, `EnemyAI` and `PlayerCommandInput` wired to the registry); a paused click 0.5 m from `Cover_LowWall_L_S1` gives the selected unit a `MoveToCoverCommand`, which it occupies after resume; the existing victory, follow, Tab, central-wall move and pause tests still pass.

**Manual checks** (owner, in the Editor): the 25 validation items of the brief, in particular: that the paused view reads clearly; that a unit behind `LowWall_M` takes visibly fewer hits than one in the open (the HUD's hits counter on the shooter); that `HostileUnit_3` walks to `Cover_LowWall_N_N` when the squad comes up the corridor north of `Obstacle_E`; that a melee companion holds `LowWall_L` under fire; that nothing stays yellow or cyan after a fight.

## 8. Known limitations, accepted for the prototype

- Hostiles choose cover at acquisition only and never re-evaluate: a target that walks around the cover is shot at from the same spot while it stays in range and sight; one that breaks sight triggers the Phase 5 reposition walk, which takes the hostile out of its point. Hostile cover also depends on where the target is first seen; from some approaches a ranged hostile finds no useful point and attacks in the open.
- The hold-cover rules for companions and retaliation are decided when the own order is issued, like every acquisition rule in 018: a covered assister or retaliator whose target later leaves its range or sight follows it out of cover through the normal attack phases.
- Companions never seek cover on their own.
- A group ordered to one point sends only one unit into cover; the rest keep their orders.
- A queued cover order reserves nothing until it starts; two busy units can queue the same point and the second is skipped when its turn comes; idle units never queue one.
- A cover order to a point another unit physically stands on (without claiming it) is held off by agent avoidance and gives up after 3 s without progress, releasing its reservation; the unit then runs its next order from where it stopped.
- The diagonal gap between `LowWall_L` and `Crate_J` (1.25 m before erosion) may not be walkable; the route past the central wall's south-east end is the corridor west of `LowWall_L`.
- Cover is binary per point: no low/high distinction beyond the per-point `hitChance`.
- The cover ray goes to the feet, so an obstacle lower than 0.9 m counts as cover only beyond 1.125 / height metres (0.6 m: about 1.9 m), that is at long range and not up close; the arena has no obstacle lower than 0.9 m.
- Points at tall obstacles never apply `hitChance`: straight on nothing can shoot, from the flanks the unit is exposed. They are hiding spots.
- Points are hand-placed; new obstacles need new points.
- The view hides unclaimed points in real time; the player must pause to see them.
- A unit physically standing on a point but farther than 0.6 m from its centre (agent avoidance) is exposed; a unit jostled more than 1 m away loses it.

## 9. Files

```
Assets/_Project/
├── Materials/CoverMarker.mat                       NEW (generated)
├── Prefabs/FriendlyUnit.prefab                     + UnitCover (generated)
├── Prefabs/HostileUnit.prefab                      + UnitCover (generated)
├── Scenes/Prototype.unity                          low walls, cover points, registry, view, wiring (generated)
├── Scenes/Prototype/NavMesh-Environment.asset      rebaked
├── Scripts/AI/CompanionAI.cs                       hold ordered cover; IsEngaged via AttackTarget
├── Scripts/AI/EnemyAI.cs                           cover seeking, Cover state, Target via AttackTarget
├── Scripts/Commands/UnitCommands.cs                MoveToCoverCommand
├── Scripts/Controls/CommandResolver.cs             cover clicks
├── Scripts/Controls/PlayerCommandInput.cs          registry, click radius
├── Scripts/Cover/CoverPoint.cs                     NEW
├── Scripts/Cover/CoverRegistry.cs                  NEW
├── Scripts/Cover/CoverRules.cs                     NEW
├── Scripts/DebugUI/AttackLineView.cs               misses
├── Scripts/DebugUI/CommandQueueView.cs             cover orders
├── Scripts/DebugUI/CoverView.cs                    NEW
├── Scripts/DebugUI/PrototypeHud.cs                 cover text
├── Scripts/Units/AutoRetaliate.cs                  hold ordered cover
├── Scripts/Units/CommandableUnit.cs                MoveToCover handling, Cover, AttackTarget
├── Scripts/Units/UnitAttacker.cs                   hit roll, Missed, counters
├── Scripts/Units/UnitCover.cs                      NEW
└── Tests/
    ├── EditMode/CoverRulesTests.cs                 NEW
    ├── EditMode/CoverPointTests.cs                 NEW
    ├── EditMode/UnitCoverTests.cs                  NEW
    ├── EditMode/CommandableUnitTests.cs
    ├── EditMode/CommandResolverTests.cs
    ├── EditMode/EnemyAITests.cs
    ├── EditMode/UnitAttackerTests.cs
    ├── EditMode/PrototypeHudTests.cs
    ├── PlayMode/TestSupport/TestWorld.cs
    ├── PlayMode/CoverPointPlayModeTests.cs         NEW
    ├── PlayMode/UnitCoverPlayModeTests.cs          NEW
    ├── PlayMode/CoverOrderPlayModeTests.cs         NEW
    ├── PlayMode/CoverCombatPlayModeTests.cs        NEW
    ├── PlayMode/CoverViewTests.cs                  NEW
    ├── PlayMode/AutoRetaliatePlayModeTests.cs
    ├── PlayMode/EnemyAIPlayModeTests.cs
    ├── PlayMode/CompanionAIPlayModeTests.cs
    ├── PlayMode/CombatPausePlayModeTests.cs
    └── PlayMode/PrototypeSceneTests.cs
Docs/Decisions.md                                   019, 020, 021 added; 006, 008, 014, 015, 017, 018 amended
```

## 10. Decision records (`Docs/Decisions.md`)

- **019 — Cover points and occupancy** (new): hand-placed `CoverPoint`s with an obstacle reference; the registry as a list; `UnitCover` as the one writer of claims; reservation bound to the order (with the 3 s no-progress exit and the vanished-point exit), occupancy bound to standing there (still, within 0.6 m); ordered versus incidental occupancy; auto-occupancy as the direct-control path; rejected alternatives (runtime generation, a manager, reserving at queue time, tagging commands, a stillness test borrowed from `UnitMover.HasArrived`, which is true while steering).
- **020 — Cover and ranged fire** (new): the eye-to-feet obstacle ray as the one directional test (cone rejected as redundant); hit probability as the one defensive effect, per point; `TryAttack` means "fired"; `Missed`; the injected roll; sight first, cover second; melee untouched; tall-obstacle points as hiding spots.
- **021 — Cover decisions by AI and player** (new): the click rule; one unit per point for a group (lattice fallback rejected); hostile cover at acquisition only (nearest useful point within 8 m, own point allowed); companions and retaliation hold ordered cover unless they can fight from it (decided at acquisition, never re-checked while the own order runs); `AttackTarget` as the shared "whom is it attacking" read; rejected: companion cover seeking, hostile re-evaluation, cover-aware repositioning (a Phase 7 candidate: feed cover value into `FiringPositionFinder`'s predicate).
- **006** amended: a fourth command type, `MoveToCoverCommand`, with its cases in `CommandableUnit`; `Issue` also refuses a point another unit holds.
- **008** amended: a ground click within 1 m of a cover point is `MoveToCover`; Attack on a living `Health` still wins; Shift still queues.
- **014** amended: still four deciders and one attack path; `EnemyAI` may issue `MoveToCover` ahead of its `AttackCommand`; `UnitAttacker` rolls cover before applying damage and may raise `Missed`; `AutoRetaliate` holds ordered cover.
- **015** amended: a ranged hostile may first take cover (021); `EnemyState` gains `Cover`.
- **017** amended: the reposition counter resets on a fired shot.
- **018** amended: the hold-cover rules in assist and before follow; "engaged" reads `AttackTarget`.
