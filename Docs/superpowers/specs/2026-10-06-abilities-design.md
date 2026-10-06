# Phase 7 — Combat archetypes and abilities: design

Date: 2026-10-06. Status: implemented on branch phase-7-abilities (see Docs/Decisions.md record 025).

## 1. Goal and scope

Introduce the first combat choices beyond the generic Attack: a few weapon archetypes, and a small reusable ability layer with cooldowns, single-target and ground-targeted abilities, tactical-pause planning, controller-native targeting and clean validation. Existing direct control, tactical pause, command queues, squad switching, follow/park, melee and ranged combat, enemy and companion AI, LOS, cover and controller input are preserved.

Success is the owner's Phase 7 validation list (29 items). This spec fixes how.

Out of scope (owner list): inventory, equipment, loot, ammo, armor, stats, levelling, skill trees, status-effect framework, production ability UI, final effects/animation/sound, procedural missions, dialogue, quests, save/load, multiplayer, ECS. Phase 8 is not started.

Owner decisions taken in brainstorming (2026-10-06):

- **Controller ability selection: hold R2 (RT) to show the ability menu, then press a D-pad direction** to arm that slot (up to four). Implemented as Input System chord bindings on semantic actions `Ability1`–`Ability4`, not as a code mode (§7).
- Offensive area damage hits **only units hostile to the caster** (§5).
- Abilities queued behind other orders are checked for range, LOS and cooldown **when they run**, not when queued (§4).
- The basic Attack path is **not migrated** onto the ability layer in this phase.
- No auto-approach: an ability that is out of range at execution **fails** (Attack keeps its approach).

Assumptions I made (say so if wrong):

- One caster per cast. The caster is the first selected unit when anything is selected or the game is paused, otherwise the active character. There is no group casting.
- A heal may target the caster itself.
- Abilities are instant: no cast time, wind-up, recovery or movement lock.
- Enemy AI keeps using basic attacks only (the optional enemy ability is skipped).

## 2. Architecture

```
Keyboard 1-4 · pad R2 + D-pad · mouse click · cursor Confirm
        ↓
Semantic actions (Ability1-4, Confirm, Cancel, QueueModifier, existing)
        ↓
AbilityTargeting (armed slot, pointer → preview)  ← reuses PointerTarget / TacticalCursor
        ↓  Confirm
AbilityCommand  →  CommandableUnit.Issue  (the existing queue)
        ↓  first running frame
UnitAbilities (per-unit slots, cooldowns) ── AbilityRules.Validate ── LOS / cover / encounter sides
        ↓
Health (TakeDamage / Heal)
```

New code lives in `Scripts/Abilities/`. Input and view code only ask; they hold no ability rules. `AbilityRules` and the effect maths are pure and unit-testable.

### Components

| Piece | Kind | Role |
|---|---|---|
| `AbilityDefinition` | ScriptableObject, immutable data | name, `TargetMode` (Unit / Ground), `TargetSide` (Hostile / Friendly), range, `RequiresLineOfSight`, `CoverRule` (Applies / Ignored), cooldown, effect (`Damage` / `Heal`), amount, radius (Ground only) |
| `AbilityFailure` | enum | None, CasterDead, UnknownAbility, NoTarget, TargetDead, WrongSide, OnCooldown, OutOfRange, NoLineOfSight, NoPosition |
| `AbilityRules` | pure static | ordered validation and effect selection (area membership); no Unity physics |
| `UnitAbilities` | MonoBehaviour on each unit | the unit's slots, per-slot ready time, `Validate`, `TryExecute`, `LastFailure`, events |
| `AbilityCommand` | `UnitCommand` | ability definition + target `Health` or ground point |
| `AbilityTargeting` | MonoBehaviour on `Systems` | armed slot, caster, pointer resolution, live `Preview`, `Confirm` |
| `AbilityTargetingView`, `AbilityBarView` | debug views | range/AOE circles, validity colour, ability bar, debug lines |
| `CombatArchetype` | ScriptableObject | weapon preset: role, range, damage, attack interval |
| `AbilityMenuGate` | MonoBehaviour on `Systems` | while the ability-menu action is held, disables the plain D-pad actions that would clash with the ability chord |

`CommandableUnit` gains an `AbilityCommand` case and a call into `UnitAbilities`. Small, additive changes elsewhere: `Health.Heal`, `Encounter.AreHostile`, `UnitAttacker.ApplyArchetype`, `StickRole` (armed overload), `TacticalCursor` (snap side), `PlayerCommandInput.Act`/Cancel diversion, `PromptResolver` (composite prompts), `CommandQueueView`, `PrototypeHud`, the Input Actions asset, the scene.

## 3. Weapon / combat archetypes

`CombatArchetype` is a ScriptableObject preset applied to `UnitAttacker` (`ApplyArchetype`, called in `Awake` when one is assigned; the inline fields stay as the fallback so existing `Initialize` calls and tests are untouched). The role keeps deciding LOS and cover (`NeedsLineOfSight => role == Ranged`; melee ignores walls and cover). Archetypes:

- **Melee** — the current values (range 2, damage 25, interval 1).
- **Ranged** — the current ranged values.
- **Marksman** — long range, high damage, slow interval, ranged role (needs LOS, is affected by cover).

Scene units are given distinct archetypes. No inventory or equipment: the archetype is plain unit configuration. The HUD unit label shows the archetype name.

## 4. Abilities

### 4.1 Definition and runtime state

`AbilityDefinition` is shared and never mutated. Runtime state lives on the unit: `UnitAbilities` holds a list of definitions (serialised, up to four used) and a parallel ready-time per slot on **scaled time** (`Time.time`), exactly as `UnitAttacker` does. Scaled time stops under tactical pause (`TacticalPause` sets the time scale), so cooldowns freeze and resume with no extra code. State belongs to the unit, so it survives control-mode changes, character switching and selection changes. Two units sharing one definition have separate ready times.

Only a successful execution starts the cooldown; a failed attempt costs nothing.

### 4.2 Validation (one pipeline)

`AbilityRules.Validate` is the only place the rules live. The preview, `Issue` and execution all use it, so what the player sees is what executes. Checks run in this order and the first failure is the reason:

1. caster alive → `CasterDead`
2. caster has the ability → `UnknownAbility`
3. Unit mode: target present → `NoTarget`; alive and active in the hierarchy → `TargetDead`; right side (below) → `WrongSide`. Ground mode: a position exists → `NoPosition`
4. cooldown ready → `OnCooldown`
5. flat distance (caster pivot to target pivot or point) within range → `OutOfRange`
6. LOS when `RequiresLineOfSight`: `LineOfSight.IsClear` from the caster's eye, using the attacker's `sightBlockers`; to the target pivot, or for ground to the point lifted 1 m (as if a unit stood there) → `NoLineOfSight`

Side: `Hostile` needs `Encounter.AreHostile(caster, target)`; `Friendly` needs the same side as the caster (the caster itself included). Sides come from `Encounter` (the one place that knows them); with no encounter wired a side check fails closed.

LOS and cover are never re-implemented per ability: the same `LineOfSight`, `UnitAttacker.sightBlockers` and `UnitCover`/`CoverRules` are used, and each definition says explicitly whether it needs LOS and whether cover applies.

### 4.3 Command and queue

`AbilityCommand(definition, target | point)` is plain data, issued with `CommandableUnit.Issue` and `IssueMode.Replace` or `Append` like every other order, so `MoveToCover → Ability → Move → Attack` is a normal queue.

- **Issue.** The unit must be alive and have the ability. If the command would start now (Replace, or Append onto an idle unit) the full pipeline runs and a failure rejects the order with the reason in `LastFailure`; the queue is unchanged. If it is appended behind other orders only the static checks run (caster alive, ability known, target valid and on the right side): the caster will be elsewhere when it runs, so range, LOS and cooldown are decided then.
- **Start.** Becoming current does nothing but record the order; no ability ever fires while paused, because `CommandableUnit.Update` only advances while `SimulationTime.IsRunning`.
- **Execution.** On the first running frame, before the move-intent branch (so held steering cannot drop an ability that direct control just issued), the unit validates again with the full pipeline. Success: face the target or point, apply the effect, start the cooldown. Failure: record the reason (`LastFailure`, event), no cooldown. Either way the command ends and `StartNext` runs, so a failed ability never leaves the unit stuck, and no exception or log error is produced for an expected failure (target died, moved out of range, LOS blocked). Several consecutive abilities in one queue run in order within one frame at most.
- **Direct control.** The controlled character uses the same command through the same input path: arm, confirm, `Issue(Replace)`. There is no player-only ability code.
- **Follow/park.** An `AbilityCommand` is an explicit order to `CompanionAI`, which already parks a unit on any current or pending order that is not its own or a retaliation, and never follows while one exists. No companion code changes; a test pins it.
- **Death.** A dead unit takes no ability orders (`Issue` already returns false) and `OnDied` clears its queue. A dead target fails validation (`TargetDead`).

### 4.4 Effects and cover rules

- **Damage, single target:** if `CoverRule == Applies` and the target holds cover that protects it from the caster, the hit lands with that cover's chance (the same `CoverRules.ResolveHit`, with the unit's replaceable roll so tests can force it); otherwise it lands. Damage goes through `Health.TakeDamage(amount, casterHealth)`, so `AutoRetaliate` and the death rules need no change.
- **Damage, area:** every living, active unit hostile to the caster whose flat distance to the point is within the radius takes the damage once. `CoverRule` is applied per unit from the blast point; the prototype blast sets `Ignored` (explicit, not accidental).
- **Heal:** `Health.Heal(amount)` raises current health, clamped at max, and does nothing to the dead.

### 4.5 Friendly fire

Offensive area damage affects only units hostile to the caster. No ability in this phase can hurt the caster's own side. Recorded as decision 025.

### 4.6 Prototype abilities (placeholder names and numbers)

| Slot | Name | Mode / side | Range | LOS | Cover | Effect | Cooldown |
|---|---|---|---|---|---|---|---|
| 1 | Aimed Shot | Unit / Hostile | 14 | required | applies | 45 damage | 6 s |
| 2 | Blast | Ground, radius 3 | 12 | to the point | ignored | 35 damage | 10 s |
| 3 | Mend | Unit / Friendly (self allowed) | 8 | not required | n/a | heal 40 | 8 s |

All three friendlies carry all three definitions, which also proves per-unit cooldowns. Numbers are tuned in play.

## 5. Targeting and tactical pause

`AbilityTargeting` holds the armed slot for a resolved caster. **Arm(slot)** is requested by the `Ability1`–`Ability4` actions; it is ignored for an empty slot or a dead or missing caster. **Disarm** happens on Cancel, after a confirmed cast, and whenever the caster changes (selection, active character) or dies.

While armed, `AbilityTargeting` resolves a `PointerTarget` every frame: the mouse through `PointerTargetResolver` at the pointer-position action, the controller through `TacticalCursor.Refresh()`. A `Preview` (validation of the pointed target or point, distance, LOS result, cover chance, units the blast would hit) is exposed for the views. Confirming goes through the existing path: `PlayerCommandInput.Act` diverts a click or cursor Confirm to `AbilityTargeting.Confirm(target, queueModifierHeld)` while armed, which builds the `AbilityCommand` and issues it (Append when the queue modifier is held, otherwise Replace). An invalid preview is not issued; the reason is shown instead. Cancel disarms before it clears the selection.

**Tactical pause.** While paused the player selects a unit, arms a slot, points, sees the validation, and confirms; the command is queued and does not execute until simulation resumes (§4.3). Selection and orders already work while paused; nothing about this phase touches the pause service.

**Controller targeting reuses the Phase 6.5 cursor:**

- `StickRole` gains an armed input: while an ability is armed the right stick always drives the cursor (in real time too, and R2 no longer swaps it back), so aiming never needs the camera. `TacticalCursor.IsActive` and `TacticalCameraController` both read the one rule.
- `TacticalCursor` gets a **snap side**. For a Unit ability it snaps first to the required side (hostile or friendly) and D-pad left/right cycle that side; for a Ground ability the point under the cursor is used freely (`PointerTarget.Point`), with no unit or cover snapping to the aim point. The soft target stays hostile-only, so Attack can never pick a friendly.
- Invalid targets and positions are shown by the preview colour and reason.

## 6. Preview and debug

- `AbilityTargetingView`: a range circle around the caster; a line from caster to target; for Ground abilities a radius circle at the aim point; green when `Preview` is valid, red when not; the units a blast would hit are listed in the HUD text. Prototype quality, built like the existing line and ring views.
- `AbilityBarView` / HUD additions (IMGUI, debug, works while paused): the caster, each slot with its prompt (key or controller chord from `PromptResolver`), name, ready or remaining cooldown and an armed marker; a preview line (target, distance/range, LOS clear/blocked, cover, VALID or the failure reason); the current ability command; the last failure with its reason for a few seconds of unscaled time. Pure formatting helpers carry the text so tests can pin it.
- The unit label already prints the order name, so a queued `Ability` shows as an order. `CommandQueueView` gets a case drawing a marker for an ability's target or point.
- While R2 is held with a controller active, the bar shows the D-pad layout (the "menu").

## 7. Input

New semantic actions `Ability1`–`Ability4` and `AbilityMenu` in the Commands map. Keyboard/mouse: keys 1–4. Pad, in all four families: a `ButtonWithOneModifier` chord of the right trigger (R2 / RT / ZR) with D-pad up, right, down, left. Nothing in gameplay code names a device or button (the existing scan test keeps passing).

- Per decision 024, every new action is bound in all four pad groups and every binding, composite parts included, has a group; the asset tests enforce it.
- The chord must not also fire the plain D-pad actions (Stop on down, Follow on up, target cycling left/right). Input System shortcut-key consumption would do this, but in Input System 1.20 `shortcutKeysConsumeInput` is **off** by default (the package source calls it an opt-in feature), and turning it on is a project-wide change. Instead a small `AbilityMenuGate` component disables the conflicting plain actions (a serialized list of action references) while a semantic `AbilityMenu` action (bound to the same trigger) is held, and re-enables them on release, on cancellation (a family switch or an unplugged pad) and every frame as a reconcile. A PlayMode test holds R2 and presses D-pad down on a simulated pad and asserts that the ability arms and **no Stop is issued**, and that Stop works again after R2 is released. *(Amended 2026-10-06 while planning: this spec first said the setting was on by default, which is wrong.)*
- `PromptResolver` is extended to describe a composite binding by its parts ("RT + D-pad Up"), with a test; prompts stay derived from the bindings, so rebinding changes them.
- R2 held also keeps its existing meaning in `TacticalCameraController` and `TacticalCursor` (the stick swap) when nothing is armed; arming flips the stick to the cursor at once.

Select / View / Create stays reserved and unbound.

## 8. Data and scene

- `AbilityDefinition` and `CombatArchetype` assets under `Assets/_Project/Data/`. Mutable state never lives in them.
- The prototype scene gets `UnitAbilities` and archetype references on the friendly and hostile prefabs (hostiles: archetype only), `AbilityTargeting` and the two views on `Systems`, the new references wired into `PlayerCommandInput`, `TacticalCursor`, `TacticalCameraController` and the HUD, and the actions asset updated.
- Decision record **025** (abilities, archetypes, friendly-fire policy, queue-time vs execution-time checks, armed-stick rule, chord bindings) is added with the implementation, with the rejected alternatives below.

## 9. Testing

Baselines (to be re-measured with a real run before the plan's totals table): EditMode 439, PlayMode 405. TDD, per the project rules; game-domain logic in EditMode, anything needing `Awake`, physics, time or input in PlayMode.

- **EditMode:** `AbilityRules` matrix (every failure and its order, side rules, area membership including the boundary, dead and inactive units); `Health.Heal` (clamp, dead, zero); `Encounter.AreHostile`; `StickRole` armed overload; `PromptResolver` composites; `CombatArchetype`/`UnitAttacker.ApplyArchetype`; actions-asset tests (new actions in four groups, all grouped); description formatters; `AbilityCommand` factories; queue ordering of mixed commands.
- **PlayMode:** per-unit cooldowns (two units, one definition); cooldown frozen across a pause and across a resume; a queued ability not executing while paused and executing after resume; target dying, moving out of range or being blocked after queueing fails cleanly with the unit still obeying the next order; dead caster refused; single-target, Blast (hostile-only, boundary, no friendly damage) and Mend through the real scene; cover roll honoured for Aimed Shot and ignored for Blast; `MoveToCover → Ability → Move → Attack` in order; direct-control cast while steering; companion parked by an ability order and parked rules preserved; mouse arm/confirm; controller arm via R2 + D-pad, aim with the cursor, snap side, Confirm, Cancel; chord does not also Stop; hot-switching families mid-targeting; Tab while armed disarms; victory/defeat unchanged; no unexpected Console output.
- A scene test per archetype proves the three combat roles differ in range, damage and interval.
- Final whole-branch review before merge, as in earlier phases.

## 10. Rejected alternatives

- **One MonoBehaviour per ability:** duplicates targeting, cooldown and validation. Rejected for one definition type plus one runtime component.
- **Ability subclass per effect (strategy ScriptableObjects):** more flexible but larger than the three effects this phase needs; the `AbilityEffect` enum is extended when a fourth effect exists.
- **Basic Attack as an ability now:** would rewrite working combat (approach, reposition, cover, AI, 700+ tests). Revisit in Phase 8.
- **A separate tactical ability scheduler:** the existing queue already orders commands and parks companions.
- **Auto-approach for abilities, or projecting the caster's future position for queue-time checks:** the brief says fail cleanly; projection needs path prediction. Deferred.
- **Mutable cooldown on the ScriptableObject:** shared state; forbidden by the brief.
- **A radial menu or an ability mode that remaps LB/RB:** needs code that knows about modes; the chord is binding-only.
- **Friendly-fire on blasts:** not predictable enough for a first pass; a definition field can add it later.

## 11. Known limitations (expected)

- One caster per cast, up to four slots, no ability groups or pages.
- Instant casts: no wind-up, interruptions or projectiles.
- A queued ability behind a move is judged from where the unit ends up; the planning preview judges from where it stands now and says so.
- While an ability is armed the camera stick is the cursor, so the camera cannot be moved with the right stick until Cancel or a cast.
- Pad chord ergonomics (R2 held with the right index finger plus the D-pad thumb) are untested on hardware; hot-switching tests use simulated devices.
- Enemy AI does not use abilities.

## 12. Manual checks for the owner

1. Keys 1–3 arm Aimed Shot, Blast and Mend; the bar and range circle show; Esc disarms.
2. Aimed Shot: out of range, behind a wall and on a covered target; failure reasons show.
3. Blast: free aim on the ground, red when out of range, the preview lists the hostiles it would hit, and only hostiles take damage.
4. Mend: on a wounded friendly and on the caster.
5. Pause (Space), queue Move → Aimed Shot → Blast with Shift, confirm nothing fires, resume, watch the order.
6. Kill a target while an ability is queued on it: the unit moves on to its next order.
7. Cooldowns stay still during pause and agree between two units.
8. Controller: hold R2, press each D-pad direction, aim with the right stick, Confirm, Cancel; D-pad down with R2 held must not stop the unit; LT queues; plug in or move the mouse mid-aim.
9. Tab / Shift+Tab while armed disarms; companion parking after an ability order; victory and defeat; Console clean.
