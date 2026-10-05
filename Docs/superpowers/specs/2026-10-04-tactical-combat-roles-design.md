# Tactical Combat: Roles, Line of Sight and Companions (Phase 5) — Design

Date: 2026-10-04 · Branch: `prototype/combat-roles-and-companions` · Status: implemented 2026-10-04 (see Docs/superpowers/plans/2026-10-04-tactical-combat-roles.md)

Builds on Phases 1–4 (see the four earlier specs in this folder and decisions 006–015 in `Docs/Decisions.md`). It does not redesign the control architecture; it adds decision sources and one new shared capability (line of sight) on top of the existing `Issue` path.

## 1. Goal

Make combat tactically meaningful without turning the prototype into an RPG:

- Units have a **combat role**, melee or ranged, as plain serialized data on `UnitAttacker`.
- Ranged attacks need **line of sight**; world geometry blocks it; units never do.
- A ranged unit whose target is in range but out of sight **repositions** to a nearby firing position, then fires.
- Hostiles pick a **nearest valid target**: alive, in detection range, visible, reachable.
- Companions (friendlies that are not the controlled character) **follow** the controlled character when idle and **assist** when a nearby hostile is engaged.
- **Explicit player orders always win** over companion autonomy; autonomy resumes when the orders finish.
- Tactical pause freezes every new behaviour; existing controls are untouched.

```
Decision sources                                    Shared unit capabilities
────────────────                                    ────────────────────────
PlayerCommandInput (clicks) ──┐
EnemyAI (hostiles) ───────────┼─ Issue(Attack/Move) ─► CommandableUnit ─► UnitMover   (paths, Steer, CanReach)
CompanionAI (friendlies) NEW ─┤                         │  approach         UnitAttacker (role, range, cooldown,
AutoRetaliate ────────────────┘                         │  reposition  ◄──── LineOfSight) ─► Health
                                                        │  attack           FiringPositionFinder NEW
                                             SimulationTime.IsRunning
```

Decision sources decide **what** a unit does. `CommandableUnit`, `UnitMover`, `UnitAttacker` and `Health` decide **how**. No AI script moves or attacks on its own.

## 2. Scope

**In scope:** `CombatRole` on `UnitAttacker`; a shared `LineOfSight` helper; range and sight checks inside the one attack path; the three-phase attack order (approach, reposition, attack) in `CommandableUnit`; `FiringPositionFinder`; `UnitMover.CanReach`; enemy target selection with reachability; `CompanionAI` with follow and assist; HUD debug text for roles, sight, AI states; prefab and scene changes (roles, a few new obstacles, rebaked NavMesh); tests; decision records.

**Out of scope (Phase 6 or never):** cover evaluation, crouching, suppression, stealth, perception cones, threat tables, aggro, squad formations, flanking, kiting or retreat AI, ammunition, weapon swapping, projectiles, accuracy, character stats or classes, abilities, status effects, inventory, loot, armour, destructible environments, dialogue, quests, save/load, procedural generation, production UI, animation, audio, multiplayer, ECS/DOTS, new packages, new input bindings.

## 3. Decisions made while designing

The brief fixed most of the design. These are the judgment calls it left open, each resolved the simplest reasonable way. The owner can overturn any of them before the plan is written.

| Topic | Decision |
|---|---|
| Where the role lives | `UnitAttacker` gets `CombatRole Role` (Melee, Ranged) next to range, damage and cooldown. No class system, no ScriptableObject: two roles and four numbers do not justify a data asset yet. Scene instances override the prefab values, as they already can. |
| Who checks line of sight | `UnitAttacker` owns the sight check for its own attacks (`HasLineOfSight`, `CanAttack`), built on a static `LineOfSight` helper. `EnemyAI`'s private copy is removed. `TryAttack` refuses a ranged hit without sight, so no caller can shoot through a wall. |
| Melee and sight | Melee ignores line of sight for attacking (2 m centre to centre, as today). Both roles use sight for **acquisition** in `EnemyAI`, as Phase 4 does. |
| Ranged standoff | A ranged unit approaches only until its target is in range, then stops and fires. It does **not** back away from a target that closes in (no kiting). Melee range is still reachable for enemies, which keeps melee hostiles dangerous. |
| Sight ray | One ray from the attacker's eye (pivot + 0.5 m, so 1.5 m above the ground) to the target's pivot (1 m), as Phase 4 did. Colliders with a `Health` in their parents never block. Unchanged so Phase 4 geometry keeps its meaning. |
| Repositioning search | 16 candidates around the **unit's ground point** (its pivot minus `UnitMover.PivotHeight`): 8 compass directions at 2 m and 4 m. Each is snapped to the NavMesh with `UnitMover`'s existing 2 m `SnapRadius` (enough to absorb the 0.5 m erosion band beside obstacles), must be within attack range of the target, must have sight of the target from the eye a unit would have there (snapped point + pivot height + eye height), and must be reachable. The nearest valid candidate wins. No search around the target, no scoring, no cover evaluation. |
| Repositioning fallback | With no valid candidate, or after three consecutive repositions without a shot, the unit **walks toward the target itself** (its position) until sight returns. At a reachable target's position sight is trivially clear, so the fallback makes progress. If the fallback walk ends with the unit still blind (the path was partial: the target stands where no path leads), or no walkable point lies within 2 m of the target, the order **ends**, exactly as Approach ends for an unreachable target. So every attack order terminates. |
| Sight re-check cadence | While its target is in range, an attacking ranged unit re-checks sight every simulation frame (one raycast). A unit walking to a **validated firing position commits to it**: it finishes the walk even if the line clears early, so it ends clear of the corner rather than on its edge, where a small target move would blind it again (ruled during implementation, 2026-10-04). During the walk-at-the-target fallback there is no validated endpoint, so the unit stops the moment sight returns. A new firing-position search happens at most once per `RepositionInterval` (0.5 s), and also whenever a reposition walk has lasted longer than `RepositionWalkTimeout` (3 s) without arriving (a spot occupied by another unit, for example). |
| Enemy target rule | Nearest friendly that is alive, active, within detection range, in sight **and reachable** (complete NavMesh path). Phase 4's "unreachable but visible friendly is re-chased every tick" limitation closes. Acquisition only; no retargeting while the order runs. |
| Telling explicit orders from autonomous ones | `CompanionAI` **remembers the command object it issued**. Any other current order, or any pending order, counts as explicit and is left alone. Commands stay plain data (006); nothing is tagged. A retaliation order from `AutoRetaliate` therefore also counts as "not ours", which is the wanted behaviour (fighting back is never interrupted by following). Rejected: a `Source` field on `UnitCommand` (a project-wide change to command data for one consumer). |
| Pending orders behind an autonomous move | If the player **queues** (Shift) an order while a companion is on an autonomous follow move, that move finishes first, then the queued order runs. The AI issues nothing more. Accepted: a short walk, and the HUD shows `Move +1`. |
| What "combat is occurring" means | A hostile is an **assist target** when it is alive, active, within `assistRange` of the companion, reachable, and either (a) is the controlled character's current attack target or (b) is **engaged**: its current order is an attack. Preference: (a), then nearest (b). Idle, unaware hostiles are never attacked autonomously, so companions never start a fight the player did not start. "Engaged" reads the hostile's `CommandableUnit.CurrentCommand`, a shared capability, not `EnemyAI`. |
| Assist is acquisition only | A running assist order is never retargeted: the companion fights its target until it dies or the order ends, then looks again. Same rule as hostiles (015); avoids flip-flopping between two engaged hostiles. No leash: the player can always order it back. |
| Follow geometry | Follow starts when the companion is farther than `followStartDistance` (6 m) from the controlled character and moves to a point `followDistance` (3.5 m) from the leader, on the companion's own side of it. While a follow move runs it is re-aimed only when the leader has moved more than `followRepathDistance` (2 m) from where it was when the move was issued **and** the companion is still farther than `followDistance` from the leader; a companion the leader walks into lets its move finish and is never sent away from the leader. The follow point is snapped to the NavMesh (within `followDistance`) before issuing, falling back to the leader's own position, so a point inside a wall or off the ground never produces a rejected order. Hysteresis and the dead band keep companions still when the leader is still. |
| Becoming the controlled character | A companion checks "am I controlled now?" **every simulation frame**, before `DirectControlInput` runs (`[DefaultExecutionOrder(-150)]`, between `ActiveCharacter` at −200 and `DirectControlInput` at −100). The first frame it is controlled while its own autonomous order is current and nothing is pending, it **stops that order once** (`StopCommand`). `DirectControlInput.HandOver` therefore sees an idle unit on the Tab frame and a held move key carries over as decision 012 intends. Explicit orders on it are left alone. Everything else (assist, follow) runs on the 0.25 s think tick. |
| Direct control | A companion with a non-zero move intent (impossible by construction, but defensive) issues nothing. |
| Self-preservation override | Not implemented: autonomy never replaces an explicit order. Recorded as a limitation. |
| Debug states | `CompanionState` { Dead, Controlled, Orders, Assist, Follow, Idle } and `EnemyState` gains `Reposition`. Both are derived on read from unit state plus one remembered command, never stored separately. |
| Arena | Two 3 m pillars, one long low-high barrier and two 2 m crates added on the squad's approach; HostileUnit_3 and FriendlyUnit_3 become ranged. |

## 4. Architecture

All runtime code stays in the `Blackglass` assembly and namespace. No new packages or assembly references. Components reference each other through serialized fields plus `internal Initialize(...)` for tests; no singletons or `Find*` in game code. Every simulation `Update` checks `SimulationTime.IsRunning`.

### 4.1 `LineOfSight` — `Combat/LineOfSight.cs` (new, static)

```csharp
public static class LineOfSight
{
    /// Eye height above a unit's pivot (the capsule centre, 1 m up): 1.5 m above the ground.
    public const float EyeHeight = 0.5f;
    public const int HitBufferSize = 8;

    /// True when nothing but units lies between `eye` and `point`. Colliders with a Health in their parents never
    /// block; any other collider does. Triggers are ignored. Allocation-free with a caller-owned buffer.
    public static bool IsClear(Vector3 eye, Vector3 point, LayerMask blockers, RaycastHit[] buffer);

    /// IsClear from the eye above `groundPivot` to the target's pivot.
    public static bool IsClear(Vector3 pivot, Health target, LayerMask blockers, RaycastHit[] buffer)
        => IsClear(pivot + Vector3.up * EyeHeight, target.transform.position, blockers, buffer);
}
```

This is `EnemyAI.HasLineOfSight` moved and renamed, with the eye-offset overload added. The 8-hit cap and its implication (a line crossing more than 8 colliders may miss a wall) carry over unchanged.

### 4.2 `UnitAttacker` — `Units/UnitAttacker.cs` (changed)

```csharp
public enum CombatRole { Melee, Ranged }

[SerializeField] CombatRole role = CombatRole.Melee;
[SerializeField] LayerMask sightBlockers = ~0;   // moved here from EnemyAI
readonly RaycastHit[] sightHits = new RaycastHit[LineOfSight.HitBufferSize];

public CombatRole Role { get; }
public bool NeedsLineOfSight => role == CombatRole.Ranged;
public bool IsInRange(Health target);                         // unchanged
public bool IsInRangeFrom(Vector3 pivot, Health target);      // same test from another point
public bool HasLineOfSight(Health target);                    // from this unit's pivot
public bool HasLineOfSightFrom(Vector3 pivot, Health target);
/// In range, and (melee or in sight). The one "could I hit it from here" test for CommandableUnit and AI.
public bool CanAttack(Health target);
public bool CanAttackFrom(Vector3 pivot, Health target);
public bool TryAttack(Health target);                         // also false when NeedsLineOfSight && !HasLineOfSight
internal void Initialize(float range, int damage, float cooldown, CombatRole role = CombatRole.Melee);
```

Prototype values: melee range 2 m (unchanged); ranged range 8 m. Friendly ranged: damage 15, cooldown 1.0 s. Hostile ranged: damage 8, cooldown 1.5 s. Everything else about `TryAttack` (alive, cooldown on `Time.time`, `Attacked`, attacker passed to `TakeDamage`) is unchanged.

### 4.3 `UnitMover` — `Units/UnitMover.cs` (changed)

```csharp
/// True when a complete NavMesh path exists from the agent to a walkable point within 2 m (SnapRadius) of `point`.
/// A partial path (another NavMesh island, a ringed point) is not reachable. No side effects.
public bool CanReach(Vector3 point);
/// Nearest walkable point within SnapRadius (2 m) of `point`, the tolerance MoveTo and CanMoveTo already use, so a
/// pivot-height point (1 m up) or a point in the erosion band beside a wall still snaps. No side effects.
public bool TrySnap(Vector3 point, out Vector3 onNavMesh);
/// Height of the unit's pivot above the NavMesh (the agent's base offset, 1 m for the prototype capsules).
public float PivotHeight => Agent.baseOffset;
```

`CanReach` snaps the destination with `TrySnap`, then uses `NavMeshAgent.CalculatePath` (the source is the agent's own NavMesh location) into one reusable `NavMeshPath`; `PathComplete` status means reachable. Called only on think ticks and firing-position searches, for a handful of candidates.

### 4.4 `FiringPositionFinder` — `Combat/FiringPositionFinder.cs` (new, static)

```csharp
public static class FiringPositionFinder
{
    public const int CandidateCount = 16;
    public static readonly float[] Radii = { 2f, 4f };

    /// Fills `buffer` (length ≥ 16) with 8 compass points at each radius around `origin`, nearest ring first.
    /// Pure; EditMode-tested.
    public static int Candidates(Vector3 origin, Vector3[] buffer);

    /// A caller's validity test: accepts or rejects a raw candidate and, when accepting, returns the point to walk to
    /// (the candidate snapped onto the NavMesh).
    public delegate bool Validator(Vector3 candidate, out Vector3 accepted);

    /// The first candidate (nearest, given the ring order) the validator accepts, as the validator's accepted point.
    public static bool TryChoose(Vector3[] candidates, int count, Validator isValid, out Vector3 chosen);
}
```

`CommandableUnit` feeds `Candidates` the unit's **ground point** (`transform.position - Vector3.up * Mover.PivotHeight`) and supplies the predicate: `Mover.TrySnap(c, out p) && Attacker.CanAttackFrom(p + Vector3.up * Mover.PivotHeight, target) && Mover.CanReach(p)`. The snapped point lies on the NavMesh; the unit's pivot would stand `PivotHeight` above it, and `LineOfSight` adds its eye height on top, so the test ray starts where the unit's eye would actually be (1.5 m up, never 0.5 m). Because `Candidates` orders by radius and `TryChoose` scans in order, the first valid candidate is the nearest; ties within a ring go to the first compass direction (north, then clockwise), which is deterministic and good enough. The unit walks to the **snapped** point, which the predicate validated, not to the raw candidate.

### 4.5 `CommandableUnit` — `Units/CommandableUnit.cs` (changed)

The attack order becomes three phases, exposed read-only for debug and AI state:

```csharp
public enum AttackPhase { None, Approach, Reposition, Attack }
public AttackPhase AttackPhase { get; }   // None unless CurrentCommand is an AttackCommand
```

`UpdateAttack(target)` each simulation frame:

```
1. target not attackable (missing, dead, inactive) → FinishAttack (unchanged)
2. !Attacker.IsInRange(target) → Approach:
      existing chase: MoveTo(target) when not chasing or the target moved 0.5 m; arriving out of range means
      unreachable → FinishAttack. Phase = Approach. (Unchanged behaviour; the flag `chasing` becomes the phase.)
2b. Phase == Reposition, not walkingAtTarget, not arrived, target not moved > 0.5 m, walk younger than
      RepositionWalkTimeout → return (committed to a validated firing position; finish the walk).
3. in range, Attacker.NeedsLineOfSight && !Attacker.HasLineOfSight(target) → Reposition (new, below).
4. otherwise Attack: stop if moving, face, TryAttack. A landed hit resets `repositionsWithoutShot`.
```

Reposition:

```
entering = Phase != Reposition
if entering: Mover.Stop(); Phase = Reposition; remember the target position     // phase is right from the first blind frame
else if walkingAtTarget and Mover.HasArrived:
    FinishAttack(); return        // walked as far as the NavMesh allows and still blind: unreachable, as in Approach
else if not Mover.HasArrived and target not moved > 0.5 m and Time.time < searchTime + RepositionWalkTimeout (3 s):
    return                        // still walking to the chosen spot
if Time.time < nextRepositionTime: return         // throttle: at most one search per RepositionInterval (0.5 s)
searchTime = Time.time; nextRepositionTime = Time.time + RepositionInterval; remember the target position
if repositionsWithoutShot < MaxRepositionsWithoutShot (3)
   and FiringPositionFinder.TryChoose(candidates around the ground point, IsFiringPosition(target), out spot)
   and Mover.MoveTo(spot):
    repositionsWithoutShot++; walkingAtTarget = false
else if Mover.MoveTo(target position):              // fallback: walk at the target until sight returns
    walkingAtTarget = true
else:
    FinishAttack()                                   // no walkable point within 2 m of the target: unreachable
```

Sight is re-checked in step 3 every frame; a unit on the fallback walk drops into Attack as soon as the line clears, while one walking to a validated spot finishes the walk first (step 2b). `repositionsWithoutShot`, `walkingAtTarget`, `searchTime` and `nextRepositionTime` reset when an attack order starts or finishes; `repositionsWithoutShot` also resets on a landed hit. `StopAll` and `FinishAttack` reset the internal phase to `None`; the public `AttackPhase` property reports `Approach` for an attack order whose internal phase is still `None` (issued but not yet ticked, including while paused), so an attacking unit never reads as doing nothing.

Melee units never enter step 3, so their behaviour is Phase 4's exactly.

### 4.6 `EnemyAI` — `AI/EnemyAI.cs` (changed)

- Fields `sightBlockers`, `eyeHeight` and the hit buffer move to `UnitAttacker`/`LineOfSight`. `HasLineOfSight` is removed (callers use `LineOfSight.IsClear`).
- `EnemyState` gains `Reposition`. `DeriveState(bool alive, UnitCommand current, AttackPhase phase)`: Dead; no attack order → Idle; attack order → `Reposition`→Reposition, `Attack`→Attack, anything else (`Approach`, or `None` before the order's first simulation update) → Chase. `State` reads `Unit.AttackPhase`.
- Target rule (`FindTarget`): among `encounter.Friendlies`: non-null, alive, active, horizontal distance ≤ `detectionRange`, nearer than the best so far, `Attacker.HasLineOfSight(candidate)`, `Mover.CanReach(candidate.transform.position)`. Nearest wins. Distance and sight are checked before the path so the path is computed only for candidates that could win.
- Everything else (think interval on scaled time, busy units left alone, acquisition only) is unchanged.

### 4.7 `CompanionAI` — `AI/CompanionAI.cs` (new, friendlies only)

`[RequireComponent(typeof(CommandableUnit), typeof(Health), typeof(UnitAttacker))]`. Serialized: `ActiveCharacter activeCharacter`, `Encounter encounter`, `float followDistance = 3.5f`, `float followStartDistance = 6f`, `float followRepathDistance = 2f`, `float assistRange = 10f`, `float thinkInterval = 0.25f`. `internal Initialize(ActiveCharacter, Encounter, …)`.

```csharp
public enum CompanionState { Dead, Controlled, Orders, Assist, Follow, Idle }
public CompanionState State { get; }      // derived on read
public Health AssistTarget { get; }       // target of our own assist order, or null
public bool IsFollowing { get; }          // our own follow move is current
```

`[DefaultExecutionOrder(-150)]`: after `ActiveCharacter` (−200) refreshes who is controlled and before `DirectControlInput` (−100) hands over, so the stop in step 2 lands on the Tab frame itself.

State derivation (pure, EditMode-tested as `static CompanionState DeriveState(bool alive, bool isControlled, UnitCommand current, int pendingCount, UnitCommand ownCommand)`):

```
!alive                                   → Dead
isControlled                             → Controlled
current == null                          → Idle
current != ownCommand || pendingCount>0  → Orders       (explicit, or retaliation)
ownCommand is AttackCommand              → Assist
ownCommand is MoveCommand                → Follow
```

`Update` (every simulation frame, only while `SimulationTime.IsRunning` and alive) runs steps 0–2 every frame and steps 3–6 every `thinkInterval` of scaled time:

```
0. Bookkeeping: if unit.CurrentCommand != ownCommand → ownCommand = null (ours finished, or someone replaced it).
1. Dead: nothing (also: the unit's Issue refuses orders anyway).
2. Controlled (activeCharacter.Unit == this unit), or the unit's MoveIntent is non-zero: if ownCommand != null and
   no orders are pending → Issue(Stop) once and forget it. Return.
   ---- the rest only on a think tick ----
3. Orders: CurrentCommand != null and (ownCommand == null or PendingCommands.Count > 0) → return.
4. Assist: if ownCommand is an AttackCommand → return (acquisition only: a running assist is never retargeted; it
   ends when the target dies or the order finishes, then this step looks again). Otherwise target =
   ChooseAssistTarget(); if found → Issue(Attack(target)), remember it, return.
5. Follow: leader = activeCharacter.Unit (must be present and alive); otherwise fall through to Idle.
   a. If ownCommand is a Move (a follow move running): if horizontal distance to the leader > followDistance and the
      leader moved ≥ followRepathDistance from where it was when that move was issued → IssueFollow. Else return
      (a companion already within followDistance lets its move finish; FollowPoint would lie on its far side).
   b. Else if horizontal distance to the leader > followStartDistance → IssueFollow.
6. Idle: nothing.

IssueFollow: point = FollowPoint(leader, companion, followDistance, leader.forward);
             if not Mover.TrySnap(point, out point): point = leader position;
             if Issue(Move(point)) succeeds: remember the command and the leader's position; else remember nothing.
```

The AI uses `Replace` (steps 4 and 5) and `Stop` (step 2), always on its own order only: steps 0 and 3 guarantee nothing else is current and nothing is pending whenever it issues. Switching from a follow move to an assist attack, or re-aiming a follow move, therefore never touches a player's order.

`ChooseAssistTarget()`: the controlled character's current `AttackCommand` target if it is a living, active hostile in `encounter.Hostiles`, within `assistRange` of this companion and reachable; otherwise the nearest hostile that is alive, active, within `assistRange`, reachable, and whose `CommandableUnit.CurrentCommand is AttackCommand`. Null when none. `static Health ChooseAssistTarget(Vector3 from, float range, Health leaderTarget, IReadOnlyList<Health> hostiles, Func<Health, bool> isEngaged, Func<Vector3, bool> canReach)` is the pure, EditMode-tested core.

`FollowPoint(leader, companion, followDistance, leaderForward)` (pure, EditMode-tested): `leader + (companion − leader).normalized(horizontal) × followDistance`; with the companion on top of the leader, use the leader's backward direction. The raw point can lie farther than `MoveTo`'s 2 m snap from the mesh (the centre of the 3 × 3 `Obstacle_A`, or past the ground edge), which is why `IssueFollow` snaps it to the NavMesh first (the 2 m snap radius) and otherwise aims at the leader's own position, which is always on the mesh. A rejected `Issue` remembers nothing, so the next tick simply tries again.

Why remembering one command object is enough: `Issue(Replace)` by anyone else makes a different object current; `Issue(Append)` leaves ours current but makes `PendingCommands` non-empty; `Stop`, death and direct control clear the queue. All three cases read as "not ours".

### 4.8 `PrototypeHud` — `DebugUI/PrototypeHud.cs` (changed)

- Line 1 of a unit label: `"<name> 75/100 [Melee]"` / `"[Ranged]"` (`DescribeUnit` gains the role).
- Line 2, friendlies: `"<orders> | <CompanionState>"` where orders is the existing `DescribeOrders` text (empty when idle) and the state comes from `CompanionAI` if present, e.g. `"Move | Follow"`, `"Attack | Assist -> HostileUnit_1"`, `"Attack +1 | Orders"`, `"| Controlled"`. Without a `CompanionAI`, line 2 is as today.
- Line 2, hostiles: `"<State> -> <target>"` as today, `Reposition` included.
- Any unit with an attack order and a ranged role appends `" LOS clear"` or `" LOS blocked"`.
- Cooldown suffix unchanged. All formatters stay static, locale-safe and EditMode-tested.
- Control hints unchanged (no new keys).

### 4.9 Unchanged

`PlayerCommandInput`, `DirectControlInput`, `ActiveCharacter`, `UnitSelection`, `SelectableUnit`, `CommandResolver`, `GroupOrders`, `GroupMoveOffsets`, `CommandQueue`, `UnitCommands`, `TacticalPause`, `SimulationTime`, `TacticalCameraController`, `ActiveCharacterMarker`, `CommandQueueView`, `AttackLineView`, `HitFlash`, `DeathMarker`, `Health`, `Encounter`, `AutoRetaliate`, `ControlCycle`, `ClickDragDetector`, `ScreenBox`, `InputActionUtility`. The input actions asset is unchanged.

## 5. Rules summary

1. A melee unit attacks within 2 m, sight or not. A ranged unit attacks within its range **and** with a clear line from its eye to the target's pivot; world geometry blocks, units do not.
2. An attack order runs approach → (reposition) → attack. Approach walks until in range, as before. Reposition picks the nearest of 16 nearby points that is on the NavMesh, in range, in sight and reachable; after three fruitless repositions, or with no candidate, the unit walks at the target until sight returns, and ends the order if it arrives still blind or the target stands where no path leads (unreachable, as in approach). Sight is re-checked every frame. Every attack order terminates.
3. Hostiles acquire the nearest living friendly that is within 12 m, in sight and reachable, then run the order to its end.
4. A companion acts only when it has no order of anyone else's and no pending orders. Priority: dead, controlled, explicit orders, assist, follow, idle.
5. Assist targets are hostiles within 10 m that are the controlled character's target or are already attacking someone; nearest wins after the leader's target.
6. Follow starts beyond 6 m, aims 3.5 m short of the leader, re-aims when the leader moves 2 m while the companion is still farther than 3.5 m, and never moves a companion that is already close, nor away from the leader. A running assist order is never retargeted.
7. The controlled character has no autonomy; when a companion becomes controlled it drops its own autonomous order once.
8. While paused nothing in rules 1–7 advances (`SimulationTime.IsRunning`); orders, selection, Tab, V, camera and HUD keep working.

## 6. Scene and assets

Generated by a temporary editor script (`Assets/_Project/Editor/TacticalSceneBuilder.cs`, created and deleted within the task, never committed), as in Phases 2–4.

**`FriendlyUnit.prefab`:** add `CompanionAI` (defaults; `activeCharacter` and `encounter` wired per instance). `UnitAttacker` stays Melee 2 m / 25 / 1.0 s.

**`HostileUnit.prefab`:** `EnemyAI` loses its sight fields (dropped on save). `UnitAttacker` stays Melee 2 m / 10 / 1.2 s.

**`Prototype.unity`:**

- `FriendlyUnit_3`: `UnitAttacker` overrides role Ranged, range 8, damage 15, cooldown 1.0.
- `HostileUnit_3`: `UnitAttacker` overrides role Ranged, range 8, damage 8, cooldown 1.5; moved to (10, 1, 14) so it covers the approach from behind the melee pair.
- New obstacles under `Environment`, `Obstacle.mat`, all at least 2 m tall so they block the 1.5 m eye line: `Pillar_G` at (−2, 1.5, −6) scale (1.5, 3, 1.5); `Pillar_H` at (3, 1.5, 5) scale (1.5, 3, 1.5); `Barrier_I` at (−8, 1, 2) scale (6, 2, 1); `Crate_J` at (10, 1, −5) scale (2, 2, 2); `Crate_K` at (1, 1, 11) scale (2, 2, 2). None overlaps existing geometry (`Obstacle_E` spans x 3.5–8.5 at z 2.5–3.5; `Pillar_H` sits at z 4.25–5.75). The squad's start (−12…−8, −10) stays more than 18 m from every hostile, so nothing is seen at load. The pillars and crates are 1.5–2 m wide, so a unit standing behind one is blind to a target straight across it while a spot 2 m to either side is clear: that is what the 2 m candidate ring is tuned for. `Barrier_I` and `Obstacle_E` are 5–6 m wide: a ranged unit centred behind them needs the 4 m ring or the walk-at-the-target fallback, which exercises both paths.
- Rebake the NavMesh and save `Scenes/Prototype/NavMesh-Environment.asset`.
- Each `FriendlyUnit_n`'s `CompanionAI` is wired to `Systems`' `ActiveCharacter` and `Encounter`.

## 7. Testing and validation

Baseline before this phase (measured 2026-10-04): EditMode 208/208, PlayMode 178/178. Scene tests fail on any logged error.

**EditMode**

- `FiringPositionFinderTests`: 16 candidates, two rings, nearest ring first, all at the right distance; `TryChoose` returns the first valid in ring order, false when none valid.
- `CompanionAITests`: `DeriveState` for every row of the table; `ChooseAssistTarget` prefers the leader's target, then nearest engaged, ignores dead/inactive/out-of-range/unreachable/idle hostiles; `FollowPoint` geometry including the coincident case.
- `EnemyAITests`: `DeriveState` with the three phases.
- `UnitAttackerTests` (new): `Role`, `NeedsLineOfSight`, `IsInRangeFrom`, `Initialize` with a role.
- `PrototypeHudTests`: role in `DescribeUnit`, companion line, LOS suffix, `Reposition`.

**PlayMode** (`TestWorld` gains `CreateFighter(..., CombatRole role, float range)`, `CreateCompanion(position, activeCharacter, encounter)`, `CreateActiveCharacter(unit, pause)`)

- `LineOfSightTests`: clear on open ground; blocked by a 2 m wall; not blocked by a unit standing between; a 1 m crate does not block the eye line.
- `RangedCombatPlayModeTests`: a ranged fighter attacks from 8 m without closing in (its final distance ≥ 6 m); ordered to attack a target behind a pillar it deals no damage while blind, repositions to a side point and then hits; standing 1 m from a 2 m crate it still finds a side candidate (pins the near-obstacle snap); with the target walled in on three sides it falls back to walking at the target and eventually hits without errors (the order ends with the target dead within the timeout); with the target in range but enclosed on four sides by 2 m walls the order ends without a shot and an appended Move then runs (no endless loop); a melee fighter pursues a target that is ordered away and hits it; `AttackPhase` reads Approach, Reposition, Attack along the way, and Approach immediately after `Issue`.
- `EnemyAIPlayModeTests`: `UnreachableVisibleFriendly_IsRechasedWithoutErrors` becomes `UnreachableVisibleFriendly_IsIgnored` (the hostile stays idle and does not walk to the ring); a ringed friendly is ignored in favour of a farther reachable one; the ranged hostile acquires at 12 m, stops at about 8 m and fires; `HasLineOfSight_UnitsNeverBlock_WallsDo` moves to `LineOfSightTests` against `LineOfSight.IsClear`.
- `CompanionAIPlayModeTests`: idle companion 12 m from the leader walks to within 4.5 m and stops; a companion 3 m away issues no move for 2 s; the leader walks 15 m and the companion re-paths and arrives; the leader walks into a following companion and no follow move is ever aimed farther from the leader than the companion stands (no backstep); a 3 m block between leader and companion (follow point inside it) still lets the companion arrive without a logged warning; a companion with an explicit Move keeps it and does not follow; Shift-queued orders are not interrupted; after the explicit order finishes the companion follows again; Tab (`ActiveCharacter.SetUnit`) makes the old leader follow the new one and the new leader drop its follow move on the very next frame; an explicit order on the new leader survives the switch; a hostile attacking the leader is attacked by the companion; an idle unaware hostile 5 m away is not; the leader's attack target is preferred over a nearer engaged hostile; an explicit Move during an assist replaces it and the companion walks; while paused for one real second a following companion neither moves nor gains orders, and a repositioning ranged unit does not advance; a dead companion does nothing.
- `CombatPausePlayModeTests`: extended with a reposition-in-progress freeze.

**Scene** (`PrototypeSceneTests`): wiring (roles per unit, `CompanionAI` on every friendly wired to `ActiveCharacter` and `Encounter`, no `CompanionAI` on hostiles, new obstacles present, one collider per unit); companions start idle (within follow distance); the controlled character ordered 12 m away pulls both companions within 6 m within 20 s; Tab then the same from the second friendly; the existing victory test still passes with mixed roles.

**Manual checks** (owner, in the Editor): the 20 validation items of the brief; in particular the feel of follow distance, whether assist starts fights too eagerly or too lazily, and whether repositioning reads as sensible.

## 8. Known limitations, accepted for the prototype

- Ranged units never back away; a melee attacker that reaches them fights at melee range.
- The reposition fallback walks at the target with no standoff: where sight only clears near the target (an enclosed target, or a moving one once three searches have failed, since the counter resets only on a hit) a ranged unit may end up firing from melee range.
- Repositioning searches around the unit only, 16 points, no cover value; it may pick a spot in the open.
- Sight is one ray to the target's pivot; the 8-hit buffer cap carries over.
- No target memory, leash or retargeting for hostiles or assisting companions.
- Companions never start fights with unaware hostiles, even when the player might want them to.
- A Shift-queued order waits for a companion's current follow move to finish.
- Autonomy never overrides explicit orders, even for self-preservation.
- A Stop (X) on a companion more than 6 m from the controlled character is followed by a follow move on the next tick; there is no hold-position order yet.
- Tab onto a companion that is mid-assist stops its own attack as well as a follow move; the player's orders are never touched.
- Acquisition requires reachability, so a ranged hostile or companion will not shoot a visible, in-range target standing where no path leads, although an explicit attack order on it works.
- Follow uses a straight line from the leader; two companions on the same side may jostle through agent avoidance.
- No formation, no cover system, no crouching, no suppression.

## 9. Files

```
Assets/_Project/
├── Prefabs/FriendlyUnit.prefab                    + CompanionAI (generated)
├── Prefabs/HostileUnit.prefab                     EnemyAI sight fields dropped (generated)
├── Scenes/Prototype.unity                         roles, HostileUnit_3 moved, 5 obstacles, CompanionAI wiring (generated)
├── Scenes/Prototype/NavMesh-Environment.asset     rebaked
├── Scripts/AI/CompanionAI.cs                      NEW
├── Scripts/AI/EnemyAI.cs                          target rule, Reposition state, sight fields removed
├── Scripts/Combat/FiringPositionFinder.cs         NEW
├── Scripts/Combat/LineOfSight.cs                  NEW
├── Scripts/DebugUI/PrototypeHud.cs                roles, companion state, LOS
├── Scripts/Units/CommandableUnit.cs               AttackPhase, reposition
├── Scripts/Units/UnitAttacker.cs                  CombatRole, sight, CanAttack
├── Scripts/Units/UnitMover.cs                     CanReach, TrySnap
└── Tests/
    ├── EditMode/CompanionAITests.cs               NEW
    ├── EditMode/FiringPositionFinderTests.cs      NEW
    ├── EditMode/UnitAttackerTests.cs              NEW
    ├── EditMode/EnemyAITests.cs
    ├── EditMode/PrototypeHudTests.cs
    ├── PlayMode/TestSupport/TestWorld.cs
    ├── PlayMode/LineOfSightTests.cs               NEW
    ├── PlayMode/RangedCombatPlayModeTests.cs      NEW
    ├── PlayMode/CompanionAIPlayModeTests.cs       NEW
    ├── PlayMode/EnemyAIPlayModeTests.cs
    ├── PlayMode/CombatPausePlayModeTests.cs
    └── PlayMode/PrototypeSceneTests.cs
Docs/Decisions.md                                  016, 017, 018 added; 014, 015 amended
```

## 10. Decision records (`Docs/Decisions.md`)

- **016 — Combat roles and line of sight** (new): role as data on `UnitAttacker`; `LineOfSight` as the one sight check; ranged `TryAttack` refuses blind shots; melee ignores sight; no kiting; rejected alternatives (class system, ScriptableObject roles, per-caller sight checks).
- **017 — Repositioning** (new): the three-phase attack order; the 16-candidate search from the ground point with the 2 m snap; the walk-at-the-target fallback, and the two unreachable exits (arrived still blind, no mesh within 2 m of the target) that make every attack order terminate.
- **018 — Companion autonomy** (new): the priority model; "ours" by command identity instead of tagging commands; what counts as combat; acquisition-only assist; follow hysteresis and the no-backstep rule; snapped follow points; the per-frame controlled check at execution order −150 so Tab hands over an idle unit; no leash, no self-preservation override.
- **014** amended: four deciders (CompanionAI added); the attack path now has phases.
- **015** amended: reachability joins the target rule; the sight helper moved to `LineOfSight`; the "re-chased every tick" implication is closed.
