# Procedural cover discovery: design

Date: 2026-10-05. Revises the Phase 6 cover system (specs/2026-10-05-cover-system-design.md, decisions 019 to 021). Owner-approved design, this file is the written record.

## 1. Purpose

Missions will be procedural (decision 019, Direction). Cover must therefore come from geometry, not from hand-placed scene objects:

```
Geometry (CoverSurface-tagged boxes)
        -> CoverGenerator (pure box maths + walkability predicate)
        -> CoverLocation list in CoverRegistry
        -> CoverRules / ProtectsFrom (evaluation)
        -> player commands, companion rules, enemy AI (unchanged consumers)
```

Owner answer on scope: **revise** the shipped system. Everything that already works stays (reservation and occupancy lifecycle, `MoveToCoverCommand`, hit chance, hostile cover seeking, companion hold rule, parking and follow interaction, pause behaviour, cover view while paused). What changes is where locations come from, what they are, and the new corner cover with peek data.

## 2. Assumptions (stated, not asked)

1. `CoverSurface` supports **`BoxCollider` only**, upright, any yaw. Every arena obstacle is a box. Other shapes need another extractor feeding the same `CoverBox` input.
2. Low or Tall is **derived** from the collider's height (top at or below 1.2 m: Low), never set by hand.
3. **Hostiles do not choose corner or tall-face locations yet.** Straight on, a tall obstacle blocks their sight, and without a peek behaviour a hostile would stand hidden and then leave through the reposition phase. They keep choosing Low locations. The peek data is what lets a later phase change this.
4. `HitChance` becomes a generator setting (0.5), copied onto each location. The per-point serialized value disappears.
5. Location names change; tests that name locations are updated. Names are debug labels, not keys.
6. Discovery assumes the NavMesh already covers the geometry. Procedural flow must build the NavMesh first.
7. The one-off scene builder is never committed (as in Phases 2 to 6).

## 3. Representation: `CoverLocation`

Plain C# class (`Scripts/Cover/CoverLocation.cs`), replacing the `CoverPoint` MonoBehaviour. It carries only what gameplay needs:

| Member | Meaning |
|---|---|
| `Name` | Debug label, e.g. `Cover_LowWall_L_S1`, `Cover_Barrier_I_NCorner2`. |
| `Position` | Stand point, ground level. |
| `Facing` | Flat unit direction from the stand point into the obstacle. The protected side is the far side of the obstacle along it. |
| `Obstacle` | The protecting `Collider` (source geometry reference). |
| `Height` | `CoverHeight.Low` (can be shot over, firing cover) or `Tall` (blocks sight, hiding cover). |
| `Placement` | `CoverPlacement.Face` or `Corner`. |
| `HitChance` | Chance a protected ranged shot lands. |
| `PeekDirection` | Corner only: flat unit direction along the wall, out past the wall end. `Vector3.zero` otherwise. |
| `PeekPoint` | Corner only: where a peeking unit would stand to see round the end (stand point plus `PeekDistance` along `PeekDirection`). Present only if that point is walkable. |
| `HasPeek` | `PeekDirection != zero`. |
| `Claimant`, `IsClaimed`, `IsClaimedBy`, `IsOccupied` | As today. `TryClaim` and `Release` stay `internal`; `UnitCover` is the only writer. |
| `IsValid` | Not retired, `Obstacle` alive and its object active in the hierarchy. Replaces every `point.gameObject.activeInHierarchy` check. |
| `ProtectsFrom(attackerPivot, defenderPivot)` | Unchanged ray test (decision 020): eye to defender's feet against `Obstacle` alone. The one directional rule, so a wall never protects from behind. |

Reachability stays what it is: `Mover.CanReach` at the moment of use (EnemyAI, CommandableUnit). Discovery only guarantees the stand point is on the NavMesh.

**Peek data and the future.** A corner location records the side (`PeekDirection`) and the point to step to. A later peek system can: stay at `Position` (protected), move to `PeekPoint` (exposed to the side the wall covered), fire, return. Nothing in this phase reads these fields except the cover view (arrow) and tests.

## 4. Discovery

**`CoverSurface`** (`Scripts/Cover/CoverSurface.cs`): `[RequireComponent(typeof(BoxCollider))]` tag on a designated obstacle. No data. A procedural mission generator adds it to the geometry it spawns.

**`CoverBox`** (struct): centre, yaw, half extents (horizontal and height), ground y, the `Collider`. `CoverSurface.ToBox()` builds it from the `BoxCollider` (centre via `TransformPoint`, half extents scaled by lossy scale; only yaw of the rotation is used).

**`CoverGenerationSettings`** (serializable class, defaults):

| Setting | Default | Use |
|---|---|---|
| `standOffset` | 0.75 m | Stand point distance from a face (agent radius 0.5 m plus margin). |
| `spacing` | 2 m | Target gap between face points. |
| `endMargin` | 0.5 m | Keep face points this far from a face's ends. |
| `minFaceLength` | 1 m | Shorter faces (a wall's 0.5 m ends) get no points. |
| `lowMaxHeight` | 1.2 m | Top of box at or below: Low. |
| `minCornerLength` | 2 m | A box gets corners only if its horizontal length is at least this **and** at least twice its thickness (so crates and pillars are not walls). |
| `cornerInset` | 0.35 m | How far inside the wall end a corner stands (kept in the wall's shadow). |
| `peekDistance` | 1.25 m | Stand point to `PeekPoint`. |
| `mergeDistance` | 1 m | A location within this of an earlier accepted one is dropped. |
| `hitChance` | 0.5 | Copied to every location. |
| `walkableTolerance` | 0.25 m | `NavMesh.SamplePosition` radius used by `CoverDiscovery`. |

**`CoverGenerator.Generate(IReadOnlyList<CoverBox> boxes, CoverGenerationSettings settings, Func<Vector3, bool> isWalkable)`** (pure static, no Unity scene access, EditMode-tested). For each box, in input order:

1. **Corner locations** (Tall boxes passing the corner test). Let the long axis be the box's longer horizontal axis, `L` its length. For each long face (normal `n`) and each end (`t` = +/- long axis): stand = face centre + `n * standOffset` + `t * (L/2 - cornerInset)`; `Facing = -n`; `PeekDirection = t`; `PeekPoint = stand + t * peekDistance` (kept only if walkable, else `PeekDirection` is zero and the location is a plain face-style point). Four corners per wall.
2. **Face locations.** For each of the four side faces of length `>= minFaceLength`: usable length = face length - 2 * `endMargin`; count = `floor(usable / spacing) + 1`, spread evenly over the usable length (one point sits at the centre). Stand = point on face + `n * standOffset`; `Facing = -n`. This reproduces the hand placement: two points on a 3 m face, one on a 2 m face. Both Low and Tall boxes get face locations.
3. **Filtering.** A location is dropped if its stand point fails `isWalkable`, or lies within `mergeDistance` of an already accepted location (so a corner supersedes the face point nearest the wall end, and abutting surfaces do not stack points).

Result: Low boxes give face points only; a Tall wall gives four corners plus the face points that survive; a Tall block that is not a wall gives face points only. A typical wall gets single-digit counts, not hundreds. The arena total is a number the plan fixes from measured output.

**`CoverDiscovery`** (`Scripts/Cover/CoverDiscovery.cs`, on `Systems`): serialized `CoverRegistry`, serialized `CoverGenerationSettings`, `discoverOnStart` (default true).
- `Discover()` finds all `CoverSurface`s (sorted by name then position for deterministic order), converts to boxes, calls the generator with `NavMesh.SamplePosition(p, ..., walkableTolerance, AllAreas)` as the predicate, and calls `registry.Rebuild(result)`.
- Runs in `Start` (NavMesh data is loaded by then) and is public so any later code can re-run it.

**Future procedural flow (not built now):** generate geometry, tag it with `CoverSurface`, build the NavMesh, call `CoverDiscovery.Discover()`, start the encounter. No command, AI or view code changes.

## 5. Registry and lifecycle

`CoverRegistry` keeps being a list holder, not a manager. It gains:
- `Rebuild(IEnumerable<CoverLocation>)`: retires every old location (clears its claimant, sets it invalid), replaces the list, bumps `Version`, raises `Changed`.
- `Version` (int) for views that poll.
- `Initialize(params CoverLocation[])` kept for tests.

Effects of a rebuild:
- Units holding old locations lose them on their next `UnitCover.Update` (the existing "cover became invalid" path), and `IsProtectedFrom` checks `IsValid` so there is no stale-protection frame.
- A pending `MoveToCoverCommand` to a retired location ends through the existing "vanished point" branch.
- A unit standing still on a new location claims it through the existing stillness rule. No stale reservations survive.

## 6. Consumers (type change only)

| File | Change |
|---|---|
| `UnitCover.cs` | `CoverPoint` to `CoverLocation`; `activeInHierarchy` to `IsValid`; `IsProtectedFrom` checks `IsValid`. |
| `CommandableUnit.cs` | `CanTakeCover` and the arrival branch use `IsValid`. |
| `UnitCommands.cs` | `MoveToCoverCommand(CoverLocation)`. |
| `CommandResolver.cs`, `PlayerCommandInput.cs` | Type change. |
| `CoverRules.cs` | `TryChooseNearest` over `CoverLocation`; skip invalid instead of inactive. |
| `EnemyAI.cs` | Type change; `IsUsefulCover` additionally requires `Height == Low`, which is assumption 3, not a new rule. |
| `CommandQueueView.cs`, `PrototypeHud.cs` | `IsValid`; HUD uses `Name`. |
| `UnitAttacker`, `AutoRetaliate`, `CompanionAI` | Compile through `UnitCover`; no logic change. |

Deleted: `CoverPoint.cs` (replaced by `CoverLocation.cs`).

## 7. Visualization (`CoverView`)

Debug only, as today: every marker while paused, only claimed markers in real time.
- **State colour** (unchanged): available white, reserved yellow, occupied cyan.
- **Selected destination:** the location held or reserved by a selected unit is **green** (new `UnitSelection` reference, wired in the scene; selected units' `Cover.Point`).
- **Type by shape:** Low face = disc and short nub; Tall face = disc and tall nub; Corner = disc, tall nub and a thin arrow along `PeekDirection`.
- Markers are rebuilt when `registry.Version` changes (no longer built once in `Start`).

## 8. Arena

No new obstacles: the existing set already contains each case.

| Case | Provided by |
|---|---|
| Low barriers | `LowWall_L`, `LowWall_M`, `LowWall_N` (0.9 m). |
| Tall walls with exposed ends | `Barrier_I` (6 x 1, 2 m) and `Obstacle_CentralWall` (8 x 1, 2 m, **yawed 45 degrees**, which tests oriented boxes). |
| Tall blocks (face points only) | `Pillar_G`, `Crate_J`. |
| Fully LOS-blocking structures | All tall obstacles; untagged ones (`Obstacle_A` to `F`, `Pillar_H`, `Crate_K`) give no cover and still block sight. |
| Exposed open terrain | Ground away from tagged geometry. |
| Enemy cover use, reservation conflicts | `LowWall_N` (hostile `HostileUnit_3`), `LowWall_L` and `LowWall_N` (squad and hostiles from opposite sides). |

Scene edits (done by a temporary editor script, never committed):
- Add `CoverSurface` to `LowWall_L/M/N`, `Pillar_G`, `Crate_J`, `Barrier_I`, `Obstacle_CentralWall`.
- Delete the `CoverPoints` root and its 20 `CoverPoint` children.
- `Systems`: `CoverRegistry` loses its serialized list; add `CoverDiscovery` wired to it; wire `CoverView` to `UnitSelection`.
- The NavMesh is not rebaked (geometry is unchanged).

Risk to check in the plan: generated points near unit starts could be claimed by standing still (`HostileUnit_1` stood 1.1 m from a hand point for this reason). The plan measures the generated set against every unit start and the existing scene tests.

## 9. Tactical pause and input

Unchanged. `CoverDiscovery` is not simulation: it runs at start or on request and never on a clock. Cover planning while paused (markers, selecting, queueing `MoveToCover`) uses the existing path. No input code changes; cover clicks already go through semantic `PlayerCommandInput` and `CommandResolver`.

## 10. Testing

Baseline before work: measured at the start of the plan (memory says 281 EditMode, 262 PlayMode at the end of Phase 6; follow mode added more).

**New EditMode (`CoverGeneratorTests`):** face spacing (3 m face gives 2, 2 m gives 1, 4 m gives 2, 6 m gives 3); 0.5 m end faces skipped; Low box gives no corners; Tall wall gives four corners with correct `PeekDirection`; crate and pillar give no corners; yawed 45 degree box gives mirrored results (rotation invariance of counts); non-walkable candidates dropped; merge distance drops the face point nearest a corner; Low or Tall by height threshold; deterministic output order.

**Directional tests (`CoverLocationTests`):** protection from the far side, none from behind the defender, none from a flank past the end; for a corner location, `ProtectsFrom` is true at `Position` and false at `PeekPoint` for the same attacker (peek data is geometrically meaningful); `IsValid` false after `Retire`; claim rules unchanged (existing `CoverPointTests` migrated).

**New PlayMode:**
- `CoverDiscoveryPlayModeTests`: boxes with `CoverSurface` produce locations; a surface added at runtime appears after `Discover()`; a surface removed disappears; locations lie on the NavMesh; **rebuild releases claims** (reserved and occupied units lose cover, no claimant remains on retired locations); a unit standing still claims a new location after a rebuild; repeated `Discover()` leaves no stale claims.
- Arena test: the Prototype scene's discovered set contains Low locations at the three low walls, four corners on `Barrier_I` and on `Obstacle_CentralWall` (as far as the NavMesh allows), no location within 0.6 m of any unit start, and no stale `CoverPoint`.
- Migrated existing cover PlayMode and EditMode tests (`TestWorld` builds `CoverLocation`s through the registry).
- `CoverView`: green for a selected unit's destination; shapes per type; markers rebuilt after `Rebuild`.

**Regression (must stay green):** Tab and Shift+Tab, direct control, combat, victory and defeat, follow and parking, pause freezing, enemy cover use (`HostileUnit_3` at `LowWall_N`), melee hostiles unchanged. Unity Console free of errors.

**Owner manual checks:** paused view shows low and corner markers distinctly; cover click on a corner marker moves a unit there and it parks there; green marker for the selected unit's destination; a wall protects from the far side and not from behind; hostiles use low walls and ignore corners; Tab/Shift+Tab and victory/defeat; nothing left yellow, cyan or green after a fight.

## 11. Decision records

Add decision **023, Procedural cover discovery** to `Docs/Decisions.md`: what was decided, why, rejected alternatives (self-registering surfaces; NavMesh-edge derivation; keeping a MonoBehaviour point; hostiles using corners before peeking exists), implications. Amend 019 (representation and "rejected: generating points" superseded by 023; the Direction bullet marked done for discovery, open for peek behaviour and arbitrary meshes), 020 (`ProtectsFrom` now on `CoverLocation`), 021 (hostile search admits Low only).

## 12. Out of scope

Peek behaviour or animation, non-box geometry, a mission generator, hostiles using corners, cover on moving or destructible geometry, NavMesh rebuild orchestration, any new input binding, controller work (next phase).

## 13. Known limitations

Boxes only, upright; yaw only. Corner data is unused until a peek phase. Discovery assumes the NavMesh is current. `PeekPoint` walkability is checked only at the point itself. Spacing is even-spread, so the gap on long faces is between 2 m and 4 m.
