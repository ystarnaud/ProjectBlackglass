# Phase 10: Persistent operative identity and shallow advancement: design

Status: design approved in conversation 2026-10-07; this file is the written spec for review.

## Goal
A squad member stops being "whatever the scene spawned" and becomes a persistent operative: a stable id, a definition (who
they are), persistent state (XP, advancement), and a disposable mission runtime unit. Operatives differ in role, health,
speed, weapon and abilities; they earn XP, reach ranks, and make one modifier choice per rank. State survives mission
regeneration in memory. Everything existing (combat, abilities, direct/tactical control, Tab, follow/park, controllers,
procedural determinism) behaves exactly as before.

## Non-goals
Inventory, loot, equipment, cybernetics, skill trees, many attributes, campaign, mission rewards/economy, currency, shops,
injuries, permadeath, relationships, dialogue, final character UI, disk save/load, multiplayer, new packages, new
controller architecture, splitting the unit prefab into gameplay + visual (see Limitations), kill XP.

## Facts the design rests on (current code)
- `MissionDirector` owns the pipeline; persistent systems (`MissionSystems`: encounter, selection, activeCharacter, ...)
  are only reset and refilled; everything generated lives under `GeneratedMissionRoot` and is destroyed on regeneration.
  A scene-level component therefore survives F6/F7.
- `MissionSpawner.TrySpawn` builds the squad from `FriendlySlot{prefab, archetype, abilities}`; the number of slots sets
  `friendlyCount` (clamped 1..6), which the layout depends on.
- Runtime state already lives per unit: `Health` (damage taken), `UnitAbilities` (`readyAt`), `UnitAttacker` (cooldown).
  Shared data is `CombatArchetype` and `AbilityDefinition` (immutable ScriptableObjects).
- Ability numbers are read from the definition in exactly three places: `UnitAbilities.TryUse` (cooldown, line 163) and
  `UnitAbilities.Apply`/`Hit` (amount, lines 253 and 279). Move speed is set from `UnitMover.speed` in `Awake`.
- `MissionRuntime.PhaseChanged` fires on Success/Failure; the runtime is recreated per mission.
- Developer keys F6-F8 are actions in the `Developer` map of `BlackglassControls.inputactions` (keyboard only).
- Decision records live in `Docs/Decisions.md` (latest: 035).

## Architecture
```
OperativeDefinition (SO, immutable)  --+
OperativeRole (SO, immutable)        --+--> EffectiveConfiguration.Evaluate  (pure)  --> EffectiveConfiguration (value)
ProgressionTrack (SO, immutable)     --+                  ^
PersistentOperativeState (plain, mutable, serializable) --+
                                                          |
SquadRoster (scene MonoBehaviour: list of states) --> MissionSpawner --> runtime unit + UnitIdentity
                                                                              | applies to
                                                          Health / UnitMover / UnitAttacker / UnitAbilities
Mission finished (Success) --> SquadRoster.AwardMissionCompletion --> PersistentOperativeState.xp
```

### Data (new folder `Assets/_Project/Scripts/Operatives/`; assets in `Assets/_Project/Data/Operatives/`)
- **`OperativeDefinition`** (ScriptableObject): `id` (GUID string, authored once, see below), `displayName`, `role`
  (`OperativeRole`), `unitPrefab` (the complete unit prefab, see Visual identity), optional `portrait` (Sprite),
  `baseMaxHealth`, `baseMoveSpeed`, `archetype` (`CombatArchetype`), `abilities` (`AbilityDefinition[]`, up to 4). No
  runtime state, ever. `OnValidate`/a `Validate()` method reports an empty id, missing role/prefab/archetype, health < 1.
- **`OperativeRole`** (ScriptableObject): `displayName`, `description`, `bonus` (`StatModifiers`). A role is data so new and
  hybrid roles need no code. Prototype roles: Assault (+20% attack damage), Recon (+0.5 m/s move speed), Support (+15%
  ability power).
- **`StatModifiers`** (`[Serializable]` struct): `maxHealth` (flat int), `moveSpeed` (flat m/s), `attackDamage`
  (fraction, 0.15 = +15%), `abilityPower` (fraction), `abilityCooldownReduction` (fraction, 0.15 = -15%). Sum operation
  (`StatModifiers.Combine`). Not a generic framework: five named fields.
- **`AdvancementChoice`** (ScriptableObject): stable string `id` (e.g. `combat`), `displayName`, `description`,
  `modifiers` (`StatModifiers`). Prototype choices: Combat Training (+15% attack damage), Reinforced (+25 max health),
  Fleet-footed (+0.75 m/s), Focus (+20% ability power, -15% ability cooldown).
- **`ProgressionTrack`** (ScriptableObject): `xpThresholds` (int[], strictly increasing, first element 0; default
  `[0,100,250,450,700]` = ranks 1..5), `choices` (`AdvancementChoice[]`, unique ids), `missionCompletionXp` (default 150),
  `debugXpStep` (default 50). Methods: `RankFor(xp)`, `XpForRank`, `NextThreshold(xp)`, `FindChoice(id)`.
- **`PersistentOperativeState`** (`[Serializable]` plain class): `string operativeId`, `int experience`,
  `List<string> choiceIds`. Rank is **derived** from experience and the track (never stored); pending picks =
  `Rank - 1 - choiceIds.Count` (never stored). Methods: `AddExperience(int)` (rejects negative), `TryPickChoice(track, id)`
  (needs a pending pick and a known id; the same choice may be picked again, picks stack), `ResetProgression()`. Contains
  no `UnityEngine.Object` references, only primitives and strings, so it round-trips through `JsonUtility` (verified by a
  test) and is ready for Phase 14.
- **`EffectiveConfiguration`** (readonly struct): `maxHealth`, `moveSpeed`, `attackRole`, `attackRange`, `attackDamage`,
  `attackInterval`, `abilityPower`, `abilityCooldownMultiplier`, plus `abilities` and `rank`. Built by the pure static
  `EffectiveConfiguration.Evaluate(definition, state, track)`:
  base (definition + archetype) + `role.bonus` + sum of the modifiers of the picked choices. Rounded to ints where the
  target is an int (`damage = RoundToInt(archetype.Damage * (1 + sum.attackDamage))`, minimum 0; `maxHealth` minimum 1;
  cooldown reduction clamped to 0..0.75; moveSpeed minimum 0.5). An unknown choice id in the state (asset removed) is
  skipped, not an error.
- **`SquadRoster`** (MonoBehaviour, one per scene): serialized `startingSquad` (`OperativeDefinition[]`) and `track`.
  In `Awake` builds `states` (one `PersistentOperativeState` per definition, id taken from the definition) and refuses
  duplicate ids (logs an error and skips). API: `Members` (definition + state pairs, in squad order), `Count`,
  `Find(id)`, `AwardExperience(id, amount)`, `AwardMissionCompletion()` (to all members), `TryPickChoice(id, choiceId)`,
  `ResetProgression(id)`, `Evaluate(member)`; events `Changed(memberId)` (XP, rank or pick changed) so views and live units
  refresh. Plain-data snapshot access (`States`) for the future save system; no save code now.

### Runtime unit
- **`UnitIdentity`** (MonoBehaviour on every friendly spawned from the roster): `OperativeId`, `Definition`; holds the
  roster reference. `Apply(EffectiveConfiguration)` writes to the existing components through small additive setters:
  `Health.SetMax(int)` (raises/lowers max; keeps damage already taken, so health never silently jumps beyond the delta),
  `UnitMover.SetSpeed(float)` (field and agent), `UnitAttacker.ApplyEffective(range, damage, interval, role)` (after
  `ApplyArchetype`), `UnitAbilities.SetModifiers(power, cooldownMultiplier)`. On the roster's `Changed(id)` for its own id
  it re-evaluates and re-applies, so a debug level-up or pick is visible mid-mission. Unsubscribes in `OnDisable`.
- `UnitAbilities` gains `PowerMultiplier`/`CooldownMultiplier` (defaults 1) and two helpers `EffectiveCooldown(ability)` and
  `EffectiveAmount(ability)` used at the three read sites; nothing else in the ability system changes. A cooldown already
  running keeps its set time; a changed multiplier affects the next use.
- Display name: `UnitIdentity` renames nothing (the GameObject keeps `FriendlyUnit_n` so existing name lookups in tests and
  `PrototypeHud` still work); the HUD and panel show `Definition.displayName`.

### Mission integration
- `MissionSystems` gains an optional `roster` (`SquadRoster`), like `camera` and `interactables`.
- `MissionDirector.Run`: when `systems.roster` is set, `request.friendlyCount = Clamp(roster.Count, 1, 6)`; otherwise the
  legacy slot count. `TryAttempt` passes the roster to `MissionSpawner.TrySpawn`.
- `MissionSpawner.TrySpawn`: for friendly `i`, if a roster is present, the member `i % Count` supplies prefab
  (`unitPrefab`), archetype, and abilities (`ApplyArchetype`, `AddComponent<UnitAbilities>().Initialize`), then
  `UnitIdentity.Bind(roster, member)` evaluates and applies the effective configuration **before** the Actors object is
  activated (same ordering rule as the rest of the spawner). With no roster the legacy `FriendlySlot` path runs unchanged,
  so existing tests and the Prototype scene are untouched. Hostiles are unchanged.
- `MissionDirector` raises `MissionFinished(MissionPhase)` when its runtime reaches Success or Failure (it subscribes to
  `Runtime.PhaseChanged` in `TryAttempt` and unsubscribes in `DetachRuntime`). `SquadRoster` has a serialized
  `MissionDirector` reference, subscribes in `OnEnable` (unsubscribes in `OnDisable`) and calls `AwardMissionCompletion()`
  on Success only; a null director just disables the award. Everyone deployed gets the award, dead or alive (assumption, one constant to change; permadeath and injuries are later).
- Determinism: layout and objective generation read only the seed and the friendly count (unchanged for the 3-member
  squad). Progression is never read by the generator and uses no `SeededRandom` stream or `UnityEngine.Random`.

### Health and death boundary (to be recorded in decision 036)
Runtime health exists only in `Health` and ends with the unit. Each mission spawns every operative at its effective maximum
(base + role + advancement). A unit dying changes nothing in `PersistentOperativeState`; the dead unit stays dead for the
mission (existing behaviour, Tab and selection exclude it as before). Persistent health, injuries, recovery and permadeath
are not built; the seam for them is the spawn step where effective max health is computed.

### Visual identity
`OperativeDefinition.unitPrefab` names the complete runtime unit prefab (today `Darius_Player` or `FriendlyUnit`, which
already contains its visual child). The scene no longer decides who looks like whom; the definition does. Replacing a model
means changing that reference; id, XP and picks live in the state and are untouched. A true gameplay-prefab + visual-prefab
split is deferred (it would rework Darius's animator wiring).

### Prototype operatives
| Operative | Role | Archetype | Max HP | Speed | Abilities |
|---|---|---|---|---|---|
| Darius (slot 0, `Darius_Player`) | Assault | Ranged (8 m, 15, 1 s) | 130 | 5.0 | Blast, Aimed Shot |
| Kestrel (`FriendlyUnit`) | Recon | Marksman (16 m, 40, 2.5 s) | 80 | 6.5 | Aimed Shot |
| Sable (`FriendlyUnit`) | Support | Ranged (8 m, 15, 1 s) | 100 | 5.5 | Mend, Aimed Shot |

Names for Kestrel and Sable are placeholders. The existing `Melee` archetype and the legacy slot path keep melee covered by
tests. Ids are three fixed GUIDs authored into the assets.

### Debug tooling (isolated from gameplay)
- New `Developer` actions in `BlackglassControls.inputactions`: `ToggleOperativePanel` (F9), `AddExperience` (F10),
  `ResetProgression` (F11). Keyboard only, like F6-F8.
- `OperativeDeveloperInput` (like `MissionDeveloperInput`): calls the roster for the controlled operative
  (`ActiveCharacter.Unit` -> `UnitIdentity`) and the panel; contains no rules.
- `OperativePanelView` (IMGUI, debug-only, works while paused): controlled operative's name, role, short id, rank, XP and next
  threshold, effective stats as "base -> effective", abilities, picks made, and one button per `AdvancementChoice` while a
  pick is pending (mouse; picks call `roster.TryPickChoice`). A compact roster list of all operatives (name, role, rank, XP,
  alive/dead/not deployed). The pure text builders are `internal static` and unit tested, as in `PrototypeHud`.
- `PrototypeHud.DescribeUnit` shows the operative's display name and role for friendlies that have a `UnitIdentity`.

## Testing
TDD. Baselines (EditMode, PlayMode) are measured with `Tools/run-tests.sh` before the plan's totals table is written.
- **EditMode:** `StatModifiers.Combine`; `ProgressionTrack` rank/threshold edges (below, at, above, past the last); pending
  pick arithmetic; `PersistentOperativeState` (negative XP rejected, pick needs pending, picks stack, reset, `JsonUtility`
  round trip); `EffectiveConfiguration.Evaluate` (base only, role, each choice, stacking, clamps, unknown id); definition
  validation; **shared-definition isolation** (two states, one definition: A picks and gains XP, B unchanged, definition
  asset fields unchanged); `SquadRoster` (ids unique and taken from the definition, duplicate refused, XP independent,
  mission award to all, events); roster/ability modifier helpers; dev input asset has the three actions; panel text builders.
- **PlayMode:** a spawned unit's `Health.Max`, speed, attacker stats and ability numbers equal the evaluated configuration,
  and differ across the three operatives; live re-apply on a pick keeps damage taken and does not reset cooldowns; ability
  cooldown/amount modifiers change real behaviour (Mend heals more, cooldown shorter); XP and picks survive `RegenerateSame`
  and `GenerateNew` while units are new objects with the same `OperativeId`; a killed operative's state is intact and the next
  mission starts it at full effective health; mission Success awards XP once, Failure none; same seed with different XP gives
  identical `LayoutHash` and `ObjectiveHash`; Tab/Shift+Tab, follow/park, direct control and commands unchanged (existing
  suites stay green); the `ProceduralMission` scene wires the roster and spawns the three operatives.
- **Not tested automatically:** IMGUI appearance, real-controller feel. Both are in the manual checklist.

## Documentation
Decision 036 in `Docs/Decisions.md`: definition vs persistent state vs runtime unit; GUID id in the definition and why not
display name/instance id/spawn order; derived rank and pending picks (nothing redundant to desynchronise); explicit
`StatModifiers` instead of a generic modifier framework; the health and death boundary; the in-memory-only persistence and
what Phase 14 will serialise (`PersistentOperativeState` list keyed by id, definitions referenced by id).

## Limitations and concerns to report
- The unit prefab still bundles gameplay and visual.
- The legacy `FriendlySlot` path remains alongside the roster path (to be removed once all scenes use rosters).
- State is lost on scene reload; there is no cross-scene persistence until Phase 14.
- `UnitAbilities` rank changes affect the next use only; running cooldowns are not rescaled.
- The advancement panel is a debug IMGUI mouse panel, not a controller-navigable UI.
- Mission-completion XP goes to dead operatives too (a deliberate prototype simplification).
- No kill XP: `Health.Died` carries no killer.
