# Combat Encounter (Phase 4) — Design

Date: 2026-10-04 · Branch: `prototype/combat-encounter` · Status: implemented 2026-10-04 (see Docs/superpowers/plans/2026-10-04-combat-encounter.md)

Builds on Phases 1–3 (`2026-10-04-prototype-control-loop-design.md`, `2026-10-04-tactical-squad-control-design.md`, `2026-10-04-primary-character-takeover-design.md`, `2026-10-04-active-character-switching-design.md`) and decisions 006–012 in `Docs/Decisions.md`. It does not redesign the control architecture.

## 1. Goal

The first small, repeatable combat encounter: three friendly units against three hostile units in `Prototype.unity`.

- Every unit has health and dies at zero.
- Attacks have explicit range, damage and cooldown, on simulation time.
- One combat path serves direct clicks, tactical Attack orders, enemy AI and retaliation.
- Hostiles run a tiny AI: idle, acquire a visible friendly in range, chase, attack, reacquire, dead.
- Units hit while idle fight back; any player order overrides that.
- Tactical pause, queued orders, direct control and Tab switching keep working during combat. A dead controlled character hands control to the next living friendly.
- Victory when every hostile is dead; defeat when every friendly is dead. Both are temporary HUD text.

```
Clicks ──► PlayerCommandInput ─┐
Enemy AI ──► EnemyAI ──────────┼─ Issue(AttackCommand) ─► CommandableUnit ─► UnitMover (chase, stop)
Hit while idle ─► AutoRetaliate ┘                               │             UnitAttacker (range, cooldown)
                                                               ▼                     │ TakeDamage(amount, attacker)
                                                      SimulationTime.IsRunning       ▼
                                                                                   Health ─► Died ─► DeathMarker, orders cleared
                                                                                     │
                                                       Encounter (friendlies, hostiles) ─► Outcome ─► PrototypeHud
```

Different systems decide **what** a unit does; `CommandableUnit`, `UnitMover`, `UnitAttacker` and `Health` decide **how**.

## 2. Scope

**In scope:** health on every unit; attacker-side hit feedback; death (deactivate + corpse marker); orders cleared on death; a shared "simulation is running" check that also covers the pause frame; retaliation; `Encounter` with two sides and an outcome; `EnemyAI`; automatic hand-over of direct control when the controlled character dies; hostile prefab; scene encounter with new obstacles and a rebaked NavMesh; HUD debug text; automated tests; decision records.

**Out of scope:** armour, resistances, damage types, shields, regeneration, attributes, status effects, inventory, loot, equipment, weapons, ammunition, levelling, skills, abilities, cover mechanics, stealth systems, perception cones, alert states, memory of lost targets, leash or retreat AI, squad AI, companion AI beyond retaliation, a "hold fire" order, factions, dialogue, quests, save/load, procedural generation, production UI/animation/VFX/audio, multiplayer, ECS/DOTS, new packages, restart or menu flow. Phase 5 is not started.

## 3. Decisions made during brainstorming

| Topic | Decision |
|---|---|
| Death presentation | The unit's GameObject is **deactivated** (as the training dummy already was) and a flat grey **corpse marker** is spawned where it fell. Every existing check already treats an inactive unit as gone. Rejected: keeping the corpse as the unit with components disabled (more code, more edge cases: clicks on corpses, agents blocking paths). |
| Enemy awareness | A hostile acquires a friendly only when it is **inside a detection radius (12 m) and in line of sight**: one linecast from the hostile's eye to the friendly's centre, blocked by level geometry only. Units never block sight. Rejected: always aggressive (every run opens the same way; positioning matters less). |
| Line of sight gates acquisition only | Once a hostile has a target it chases it until the target dies or is unreachable, corners included. Otherwise a chase would drop the moment the target rounded a wall. Hiding works before you are spotted, not after. |
| Retaliation | **Shared** `AutoRetaliate` on both sides: a unit hit while it has **no orders** attacks its attacker through the normal `Issue` path. Any order wins (a Move away is a retreat); held WASD cancels it like any order. Rejected: friendly-only retaliation (hostiles hit from a blind spot would wait for a think tick); a per-unit "hold fire" flag (becomes a `UnitCommand` if ever needed). |
| Sides | `Encounter` holds two explicit lists of `Health`, friendlies and hostiles. No faction system. Rejected: using `UnitSelection.Roster` as the friendly list (ties enemy AI to selection, and allied non-controllable units would be impossible). |
| Controlled character dies | `ActiveCharacter` moves control to the **next eligible friendly in roster order**; with none it holds **no unit** and the game stays safe (camera pans, no intent, HUD says "Controlled: none"). Takeover mode and the selection are left alone (the dead unit drops out of the selection on its own). |
| Pause frame | A shared `SimulationTime.IsRunning` check (scaled delta time **and** time scale above zero) replaces per-component `deltaTime` checks, so no order, attack or AI step runs on the frame Space is pressed (closes the note in decision 007). |
| Training dummy | Removed from the scene; the hostiles replace it. The HUD line for it goes too. |
| Companions | Follow orders and retaliate. No other autonomy. |

## 4. Architecture

All runtime code stays in the `Blackglass` assembly and namespace. No new packages or assembly references. Components reference each other through serialized fields and `internal Initialize(...)` for tests; no singletons or `Find*` in game code.

### 4.1 `SimulationTime` — `GameTime/SimulationTime.cs` (new)

```csharp
/// True while gameplay simulation advances this frame: scaled delta time and time scale both above zero.
/// Time.deltaTime keeps the previous frame's value on the frame TacticalPause sets the time scale to 0,
/// so both are checked. Camera, input and debug UI do not use this; they run on unscaled time.
public static bool IsRunning => Time.deltaTime > 0f && Time.timeScale > 0f;
```

Pure read of Unity time, no state. Used by `CommandableUnit.Update`, `UnitMover.Steer` (replacing its inline check), `EnemyAI.Update` and `HitFlash.Update`/`AttackLineView.Update` where they count down.

### 4.2 `Health` — `Combat/Health.cs` (changed)

- `public void TakeDamage(int amount, Health attacker = null)`. Behaviour of amount, clamping, `Damaged(int)`, `Died` and `disableOnDeath` is unchanged; existing callers compile.
- New `public event Action<Health> AttackedBy;` raised **after** the damage is applied and `Damaged`, and only when `attacker` is non-null and the hit was applied (target was alive, amount > 0). It is raised even if the hit killed the target; death is decided before any event, so `IsAlive` is already false inside the handlers of a killing blow. Order: damage applied and death decided → `Damaged` → `AttackedBy` → `Died` → deactivate.
- `disableOnDeath` stays **true** for units and the prefab default.

### 4.3 `UnitAttacker` — `Units/UnitAttacker.cs` (changed)

- `TryAttack(Health target)` calls `target.TakeDamage(damage, OwnHealth)` where `OwnHealth` is this unit's `Health` (cached `TryGetComponent`; null when the attacker has none, e.g. test units).
- New `public event Action<Health> Attacked;` raised after each hit with the target.
- New `public float CooldownRemaining => Mathf.Max(0f, nextAttackTime - Time.time);` for the HUD.
- Range, damage and cooldown stay serialized per prefab. Cooldown still uses `Time.time`, so it freezes while paused.

### 4.4 `CommandableUnit` — `Units/CommandableUnit.cs` (changed)

- `Update` returns unless `SimulationTime.IsRunning`.
- Optional `Health` on the same GameObject (cached `TryGetComponent`). In `OnEnable`/`OnDisable` it subscribes to and from `Health.Died`; on death it calls `StopAll()` (orders cleared, mover stopped) so a corpse never keeps a queue.
- `public bool IsAlive => health == null || health.IsAlive;`
- `Issue` returns false, without side effects, when `!IsAlive` (checked before anything else, including Stop).
- The chase/attack loop (`UpdateAttack`, `IsAttackable`, `FinishAttack`) is unchanged: a dead, inactive or unreachable target finishes the attack and starts the next pending order.

### 4.5 `AutoRetaliate` — `Units/AutoRetaliate.cs` (new)

`[RequireComponent(typeof(CommandableUnit), typeof(Health))]`. Subscribes to its `Health.AttackedBy` in `OnEnable`/`OnDisable`.

Rule, on `AttackedBy(attacker)`:

```
if this unit is dead → ignore
if this unit has a current order (CurrentCommand != null) → ignore (orders win)
if this unit's move intent is non-zero → ignore (keys win)
if attacker is null, dead or inactive → ignore
else unit.Issue(new AttackCommand(attacker), IssueMode.Replace)
```

No state, no timers. Used on both prefabs. A unit whose retaliation target dies goes idle and retaliates again on the next hit.

### 4.6 `DeathMarker` — `Combat/DeathMarker.cs` (new)

`[RequireComponent(typeof(Health))]`. Serialized: `Material markerMaterial`, `float diameter = 1.2f`, `float groundHeight = 0.03f`. On `Health.Died`: creates a cylinder primitive named `"<unit name> (dead)"` at the unit's horizontal position and `groundHeight`, scale `(diameter, 0.01, diameter)`, **collider removed**, no shadows, with the marker material. It is a **root** scene object (not parented), so it stays visible after the unit deactivates. Nothing references it later; scene teardown in tests destroys root objects. `internal Initialize(Material)` for tests.

### 4.7 `AttackLineView` — `DebugUI/AttackLineView.cs` (new)

`[RequireComponent(typeof(UnitAttacker))]`. Serialized: `LineRenderer line` (on a collider-free child `AttackLine`, because the friendly prefab's own `LineRenderer` belongs to `CommandQueueView`), `float duration = 0.15f`, `float height = 0.5f`. On `UnitAttacker.Attacked(target)`: sets two world positions (this unit's position + `height`, target position + `height`), enables the line, resets the timer. `Update` counts down on scaled time while `SimulationTime.IsRunning`; disables the line at zero. `OnDisable` hides the line. `internal Initialize(LineRenderer)`.

### 4.8 `Encounter` — `Combat/Encounter.cs` (new, on `Systems`)

```csharp
public enum EncounterOutcome { Ongoing, Victory, Defeat }

[SerializeField] List<Health> friendlies;
[SerializeField] List<Health> hostiles;
public IReadOnlyList<Health> Friendlies, Hostiles;
public int LivingFriendlies, LivingHostiles;        // counts entries that are non-null and IsAlive
public EncounterOutcome Outcome => Resolve(Friendlies.Count, LivingFriendlies, Hostiles.Count, LivingHostiles);
internal void Initialize(IEnumerable<Health> friendlies, IEnumerable<Health> hostiles);
```

`public static EncounterOutcome Resolve(int friendlyCount, int livingFriendlies, int hostileCount, int livingHostiles)` (pure, EditMode-tested):

1. `friendlyCount > 0 && livingFriendlies == 0` → **Defeat** (checked first, so "everyone dead" is a defeat).
2. `hostileCount > 0 && livingHostiles == 0` → **Victory**.
3. otherwise **Ongoing** (including empty lists).

Computed on read; no events, no stored state (decision 006 style: views read state each frame). Destroyed entries count as dead. Nothing else is tracked.

### 4.9 `EnemyAI` — `AI/EnemyAI.cs` (new)

`[RequireComponent(typeof(CommandableUnit), typeof(Health), typeof(UnitAttacker))]`. Serialized: `Encounter encounter`, `float detectionRange = 12f`, `float thinkInterval = 0.25f`, `LayerMask sightBlockers = ~0`, `float eyeHeight = 0.5f` (above the pivot; the capsule pivot is its centre at 1 m, so the eye is at 1.5 m). `internal Initialize(Encounter)`.

```csharp
public enum EnemyState { Idle, Chase, Attack, Dead }
public EnemyState State   // derived each read, see below
public Health Target      // current AttackCommand's target, or null
```

Behaviour, in `Update`:

1. Return unless `SimulationTime.IsRunning` and `unit.IsAlive`.
2. Return until `Time.time >= nextThinkTime`; then set `nextThinkTime = Time.time + thinkInterval`. (Scaled time: frozen while paused.)
3. If `unit.CurrentCommand != null` → return (the unit is busy; `CommandableUnit` runs the chase and attack).
4. `AcquireTarget()`: among `encounter.Friendlies` that are non-null, alive, active, within `detectionRange` (horizontal distance, centre to centre) **and visible**, pick the nearest. If found, `unit.Issue(new AttackCommand(target))`.

Visibility (`static bool HasLineOfSight(Vector3 eye, Health target, LayerMask blockers, RaycastHit[] buffer)`): `Physics.RaycastNonAlloc` from the eye toward the target's centre, length = distance to the target, `QueryTriggerInteraction.Ignore`. Visible iff **no hit closer than the target lies on a collider without a `Health` in its parents**. Units (anything with `Health`) never block sight. The buffer has 8 entries; if it fills, the farthest hits are simply unknown, which is acceptable for the prototype. Called only on think ticks for candidates inside the radius.

Derived state: `Dead` if `!unit.IsAlive`; `Attack` if `CurrentCommand is AttackCommand a && attacker.IsInRange(a.Target)`; `Chase` if `CurrentCommand is AttackCommand`; otherwise `Idle`. `public static EnemyState DeriveState(bool alive, UnitCommand current, bool inRange)` is the pure, EditMode-tested core.

Reacquire: when the target dies or cannot be reached, `CommandableUnit` finishes the order on its own; the AI sees `CurrentCommand == null` on its next tick and runs step 4 again. An unreachable but visible friendly is therefore re-chased every tick (the unit walks to the closest point and stands there); accepted for the prototype.

Death: `unit.IsAlive` false → step 1 returns; the GameObject is deactivated anyway.

### 4.10 `ActiveCharacter` — `Controls/ActiveCharacter.cs` (changed)

Still state-holding, with one self-consistency rule, run in `Update` (unscaled: it must also work while paused, although death only happens in simulation time):

```
if unit != null && !IsEligible(unit):
    if !Cycle(+1): SetUnit(null)
```

`public static bool IsEligible(CommandableUnit unit)` (new overload): `unit != null && unit.enabled && unit.gameObject.activeInHierarchy && unit.IsAlive`, and if it has a `SelectableUnit`, that one is enabled too. The existing `IsEligible(SelectableUnit)` delegates to it.

`internal void RefreshEligibility()` is the method `Update` calls, so EditMode tests can drive it. Takeover mode is not changed. The selection is not changed (a deactivated `SelectableUnit` already leaves the selection through `UnitSelection.OnUnitDisabled`). With `Unit == null`, `HasUnit` is false, so: `IsDriving` is false (WASD pans the camera), `DirectControlInput` hands over to null and sets no intent, the camera neither follows nor glides, the marker hides, real-time clicks order the selection (existing rule in `PlayerCommandInput.OrderedUnits`).

Order of operations on the frame after a death: `ActiveCharacter` carries `[DefaultExecutionOrder(-200)]`, so it switches before `DirectControlInput` (`-100`) reads it; `DirectControlInput.HandOver` then zeroes the dead unit's intent and arms the release gate only if a key is held and the new unit has orders (decision 012 rules, unchanged). `IsDriving` never reads false between two living units, so the switch itself never re-arms the gate: a held key carries over to an idle survivor. The camera glides to the new unit as for any switch.

### 4.11 `UnitMover` — `Units/UnitMover.cs` (changed, one line)

`Steer` uses `SimulationTime.IsRunning` instead of its inline `deltaTime`/`timeScale` check. No behaviour change.

### 4.12 `PrototypeHud` — `DebugUI/PrototypeHud.cs` (changed)

- Remove `target` and `targetLabel`. Add `[SerializeField] Encounter encounter`.
- **Status line** (replaces the dummy line): `Friendlies alive 3/3 | Hostiles alive 3/3` via `static string DescribeSides(int livingFriendlies, int friendlies, int livingHostiles, int hostiles)`.
- **Controlled line:** `DescribeActive` unchanged; when `!activeCharacter.HasUnit` it shows `Controlled: none` (`static string DescribeNoActive()`).
- **Unit labels** for every living unit in `encounter.Friendlies` and `encounter.Hostiles` (replacing the roster loop): line 1 `"<name> 75/100"`, line 2 for friendlies the existing `DescribeOrders` text, for hostiles `"<State>"` or `"<State> → <target name>"`; plus `" CD 0.4"` appended while `CooldownRemaining > 0`. `static string DescribeUnit(string name, int current, int max)` and `static string DescribeEnemy(EnemyState state, string targetName, float cooldownRemaining)` are the tested formatters. Components are looked up with `GetComponent` per frame; six units, debug only.
- **Outcome banner:** when `Outcome != Ongoing`, a large centred label: `VICTORY - all hostiles are down` / `DEFEAT - the squad is down` (`static string DescribeOutcome(EncounterOutcome)` returns empty for Ongoing). Drawn below the pause banner so both can show.
- Control hints: "dummy" wording becomes "enemy".

### 4.13 Unchanged

`PlayerCommandInput`, `DirectControlInput`, `UnitSelection`, `SelectableUnit`, `CommandResolver`, `GroupOrders`, `CommandQueue`, `UnitCommands`, `TacticalPause`, `TacticalCameraController`, `ActiveCharacterMarker`, `CommandQueueView`, `HitFlash` (except the `SimulationTime` check), `ControlCycle`, `ClickDragDetector`, `ScreenBox`, `GroupMoveOffsets`, `InputActionUtility`. The input actions asset is unchanged: no new bindings.

## 5. Rules summary

1. Damage happens only inside `UnitAttacker.TryAttack`: target alive, in range, cooldown elapsed, simulation running. Never per frame.
2. Attack order: approach while out of range (repath when the target moved 0.5 m), stop in range, face, hit on cooldown, finish when the target is dead, inactive, destroyed or unreachable, then start the next pending order.
3. A unit at zero health is deactivated after `Died`; it drops its orders, leaves the selection, is skipped by Tab, cannot be ordered (`Issue` false), cannot be targeted (`IsAttackable` false) and leaves a corpse marker.
4. A hostile idles until a living friendly is within 12 m and in sight, then attacks it through the normal order path until it dies; then it looks again.
5. Any unit hit while idle attacks its attacker. Any order, or held WASD, overrides that.
6. While paused nothing in rules 1–5 advances, the pause frame included. Orders, selection, Tab, V and the camera keep working.
7. If the controlled character dies, control passes to the next eligible friendly in roster order; if none, to nobody, safely.
8. All friendlies dead → Defeat; otherwise all hostiles dead → Victory; both shown as HUD text.

## 6. Scene and assets

Generated by a temporary editor script (`Assets/_Project/Editor/CombatSceneBuilder.cs`, created and deleted within the task, never committed), as in Phases 2 and 3.

**Materials** (`Assets/_Project/Materials/`): `Dummy.mat` renamed `Hostile.mat` with its `.meta` (GUID kept); new `DeathMarker.mat` (URP Unlit, dark grey 0.25) and `AttackLine.mat` (URP Unlit, yellow 1, 0.85, 0.2), both built like `QueueLine.mat`.

**`FriendlyUnit.prefab`:** add `Health` (max 100), `HitFlash`, `DeathMarker` (DeathMarker.mat), `AutoRetaliate`, `AttackLineView` wired to a new child `AttackLine` (LineRenderer, world space, width 0.06, AttackLine.mat, disabled, no collider). `UnitAttacker` stays 2 m / 25 / 1.0 s. The capsule stays the only collider.

**`HostileUnit.prefab`** (new): capsule with `Hostile.mat`, CapsuleCollider, NavMeshAgent (radius 0.5, height 2, base offset 1), `UnitMover` (speed 3.5, angular 720, acceleration 20, stopping 0.1), `UnitAttacker` (range 2, damage 10, cooldown 1.2), `CommandableUnit`, `Health` (max 60), `HitFlash`, `DeathMarker`, `AutoRetaliate`, `AttackLineView` + `AttackLine` child, `EnemyAI` (12 m, 0.25 s, eye 0.5). No `SelectableUnit`, `SelectionIndicator` or `CommandQueueView`.

**`Prototype.unity`:**

- Delete `TrainingDummy`.
- Instantiate `HostileUnit_1` at (8, 1, 8), `HostileUnit_2` at (13, 1, 10), `HostileUnit_3` at (4, 1, 13). The squad starts around (-10, 1, -10), about 27 m away, outside the detection radius.
- New obstacles under `Environment`, with `Obstacle.mat`: `Obstacle_E` at (6, 1, 3) scale (5, 2, 1) and `Obstacle_F` at (12, 1, 3) scale (1, 2, 6). Together with `Obstacle_CentralWall` they screen the hostiles from the south and east approaches so line of sight matters.
- Rebake the NavMesh through the existing `NavMeshSurface` on `Environment` and save `Scenes/Prototype/NavMesh-Environment.asset`.
- `Systems` gains `Encounter` with friendlies = the three `FriendlyUnit_n` `Health`s and hostiles = the three `HostileUnit_n` `Health`s. Each `EnemyAI.encounter` is wired to it. `PrototypeHud.encounter` is wired; its dummy fields disappear with the code change.

## 7. Testing and validation

Baseline before this phase (2026-10-04): EditMode 162/162, PlayMode 132/132. Scene tests fail on any logged error.

**EditMode**

- `HealthTests`: `AttackedBy` raised with the attacker after `Damaged`, not raised for a null attacker, zero damage or a dead target; raised on the killing blow before `Died`.
- `EncounterTests`: `Resolve` for ongoing, victory, defeat, both sides dead (defeat), empty lists (ongoing); `LivingFriendlies`/`LivingHostiles` ignore null and dead entries.
- `EnemyAITests`: `DeriveState` for dead, attack in range, chase, idle (null and Move orders).
- `CommandableUnitTests`: `Issue` on a dead unit returns false and changes nothing (Move, Attack and Stop); death clears current and pending orders.
- `ActiveCharacterTests`: `RefreshEligibility` moves to the next eligible friendly when the unit dies, is disabled or deactivated; wraps; sets null when nobody is eligible; does nothing while eligible; takeover flag untouched; `IsEligible(CommandableUnit)` cases.
- `PrototypeHudTests`: `DescribeSides`, `DescribeUnit`, `DescribeEnemy`, `DescribeOutcome`, `DescribeNoActive`.

**PlayMode** (test worlds; `TestWorld` gains `CreateFighter(position, maxHealth, damage, cooldown)` = unit + `Health` + `AutoRetaliate`, `CreateHostile(position, encounter)` = fighter + `EnemyAI`, `CreateEncounter(friendlies, hostiles)`)

- Combat: two fighters ordered to attack each other fight until one dies; damage lands on the cooldown, not per frame; the loser is inactive and has no orders; a corpse marker exists at its position with no collider; the winner's order finishes and its next queued Move starts; `Attacked` fires per hit and `AttackLineView` shows then hides its line.
- Retaliation: an idle fighter hit by another attacks it; a fighter with a Move order is hit and keeps moving; after Stop it retaliates on the next hit; a retaliating fighter ordered away stops attacking and moves; a unit with a held move intent does not retaliate.
- Enemy AI: idle beyond the radius; acquires inside it and chases; stays idle with a wall between (`CreateEnvironment` obstacle); reaches range and hits; reacquires the other friendly after its target dies; stays idle when no friendly is alive; a dead hostile does nothing; a hostile hit from behind a wall retaliates at once.
- Pause: during a fight, one real-time second of pause changes no health, no cooldown, no AI state, no position; after resume the fight continues; pressing pause ten times in a row during combat logs nothing and ends consistent; no damage lands on the pause frame (hit scheduled for that frame is delayed until resume).
- Control: the controlled character dying moves `ActiveCharacter.Unit` to the next eligible friendly, zeroes the old intent, keeps takeover, logs no errors; dead friendlies are skipped by Tab and Shift+Tab; when the last friendly dies `Unit` is null, `HasUnit` false, W pans the camera, V and Tab do nothing harmful, no exceptions.
- Outcome: `Encounter.Outcome` turns Victory when the last hostile dies and Defeat when the last friendly dies.

**Scene** (`PrototypeSceneTests`): wiring (three `HostileUnit_n` with `EnemyAI`, `Health`, `AutoRetaliate`, no `SelectableUnit`; `Encounter` lists match the units; no `TrainingDummy`; every unit has exactly one collider; HUD wired); the squad ordered to attack all hostiles wins within 60 s and the HUD outcome text reads victory; a real-time click on a hostile makes the active character attack it; Tab then click attacks from the second friendly (existing test, retargeted); repeated pause/resume during the fight logs no errors.

**Manual checks** (owner, in the Editor): the 26 validation items of the brief, in particular feel of detection radius and line of sight, retaliation versus retreat, and the hand-over when the controlled character dies.

## 8. Known limitations, accepted for the prototype

- No leash, no lost-sight behaviour, no target memory: a hostile that has acquired a target follows it anywhere until it dies.
- Hostiles never switch to a closer or more dangerous friendly.
- An unreachable but visible friendly makes a hostile re-chase every think tick.
- Line of sight is one ray to the target's centre; a half-exposed unit is either seen or not depending on that one point.
- Retaliation has no "hold fire"; a Stop makes the unit idle, so it fights back on the next hit.
- Death deactivates the unit; a future revive would need the corpse approach instead. Corpse markers accumulate until the scene reloads.
- Victory and defeat are HUD text only; there is no restart.
- Hand-over on death follows roster order, not proximity or screen position.
- Melee only; range is centre-to-centre horizontal distance.

## 9. Files

```
Assets/_Project/
├── Materials/Hostile.mat                          renamed from Dummy.mat (GUID kept)
├── Materials/DeathMarker.mat                      NEW (generated)
├── Materials/AttackLine.mat                       NEW (generated)
├── Prefabs/FriendlyUnit.prefab                    Health, HitFlash, DeathMarker, AutoRetaliate, AttackLineView + AttackLine child
├── Prefabs/HostileUnit.prefab                     NEW (generated)
├── Scenes/Prototype.unity                         dummy removed, 3 hostiles, 2 obstacles, Encounter wired (generated)
├── Scenes/Prototype/NavMesh-Environment.asset     rebaked
├── Scripts/AI/EnemyAI.cs                          NEW
├── Scripts/Combat/DeathMarker.cs                  NEW
├── Scripts/Combat/Encounter.cs                    NEW
├── Scripts/Combat/Health.cs                       attacker argument, AttackedBy
├── Scripts/Combat/HitFlash.cs                     SimulationTime check
├── Scripts/Controls/ActiveCharacter.cs            RefreshEligibility, IsEligible(CommandableUnit), DefaultExecutionOrder(-200)
├── Scripts/DebugUI/AttackLineView.cs              NEW
├── Scripts/DebugUI/PrototypeHud.cs                encounter, labels, banner
├── Scripts/GameTime/SimulationTime.cs             NEW
├── Scripts/Units/AutoRetaliate.cs                 NEW
├── Scripts/Units/CommandableUnit.cs               IsAlive, death clears orders, SimulationTime
├── Scripts/Units/UnitAttacker.cs                  Attacked, CooldownRemaining, attacker passed
├── Scripts/Units/UnitMover.cs                     SimulationTime check
└── Tests/
    ├── EditMode/EncounterTests.cs                 NEW
    ├── EditMode/EnemyAITests.cs                   NEW
    ├── EditMode/HealthTests.cs
    ├── EditMode/CommandableUnitTests.cs
    ├── EditMode/ActiveCharacterTests.cs
    ├── EditMode/PrototypeHudTests.cs
    ├── PlayMode/TestSupport/TestWorld.cs          CreateFighter, CreateHostile, CreateEncounter
    ├── PlayMode/CombatPlayModeTests.cs            NEW
    ├── PlayMode/AutoRetaliatePlayModeTests.cs     NEW
    ├── PlayMode/EnemyAIPlayModeTests.cs           NEW
    ├── PlayMode/CombatPausePlayModeTests.cs       NEW
    ├── PlayMode/ActiveCharacterDeathPlayModeTests.cs  NEW
    ├── PlayMode/AttackLineViewTests.cs            NEW
    └── PlayMode/PrototypeSceneTests.cs
Docs/Decisions.md                                  013, 014, 015 added; 007, 011, 012 amended
```

## 10. Decision records (`Docs/Decisions.md`)

- **013 — Health, death and the simulation clock** (new): `Health` is the one hit-point implementation for both sides; death deactivates and leaves a marker (and why the corpse-as-unit option was rejected); `CommandableUnit` clears orders and refuses them when dead; `SimulationTime.IsRunning` and the pause frame.
- **014 — One combat path, three deciders** (new): `AttackCommand` through `Issue` is the only way anything attacks; `EnemyAI` and `AutoRetaliate` decide what, `CommandableUnit`/`UnitAttacker` decide how; retaliation rule and the rejected hold-fire flag; `Encounter` as the only holder of sides and why the roster was not used.
- **015 — Enemy awareness** (new): detection radius plus acquisition-only line of sight; units never block sight; no leash or memory; the alternatives rejected.
- **007** amended: the pause-frame leak is closed by `SimulationTime.IsRunning`.
- **011 / 012** amended: the controlled character's death now hands control to the next eligible friendly, or to nobody.
