# Phase 12 — Inventory, Equipment & Loot: design

Status: approved in conversation 2026-10-09 (owner: "looks good"); written spec awaiting owner review.
Brief: the Phase 12 prompt (sections 1–18). This document records the design choices made against the repository as it is at `b02289a`.

## 1. Goal and success criteria

Prove this loop, in memory, inside one application session:

Configure loadout → deploy into a procedural mission → discover loot → collect → use a medkit → complete objectives and extract → keep secured items → re-equip them for another mission.

Not in scope: shops, currency, crafting, rarity/affixes, durability, encumbrance, ammunition, cybernetics, corpse looting, disk save/load, new VFX/audio, campaign screens, Asset Studio changes, Phase 12.5.

## 2. Owner decisions taken during brainstorming

1. **Start in loadout.** The `ProceduralMission` scene no longer generates on start. It opens on the loadout panel; Deploy (new seed / same seed) generates. The panel reopens after Success or Failure.
2. **Scoped modal controller navigation.** The inventory panel is the one place a controller gets UI focus. This amends decision 042 ("no controller focus mode") for this panel only, and uses the reserved Select/View/Create button (amends 024).
3. **Pickups go to the collector's bag.** Per-operative capacity; leftovers stay in the container.

## 3. Conflicts with recorded decisions (and how they are resolved)

| Decision | Conflict | Resolution |
|---|---|---|
| 042 | No controller focus mode | Amended for the inventory modal only. The rest of the HUD stays click-only. Recorded as decision 043. |
| 024 | Select/View/Create reserved and unbound | Bound to `Inventory/Toggle` on every pad family. Keyboard `I` (unused). Asset tests enforce all four pad groups. |
| 036 / 025 | The definition's `Archetype` drives combat; "there is no inventory or equipment" | When a unit has a loadout, the equipped weapon's archetype drives combat. `OperativeDefinition.Archetype` stays as the starter-weapon seed and validity field. Units without a loadout (Prototype scene, test rigs, legacy slots) behave exactly as before. |
| 033 | `MissionInteractable` completes once and stays completed | Containers re-arm after the search (see 7.2). Terminals are unchanged. |
| 037 | Knowledge gates every display of hidden things | Containers and their contents are gated the same way (section 9). |
| 013 | Health max changes keep damage taken | Equipment changes happen only outside missions, so a max-health change at spawn starts at full health, matching the mission-start policy. |
| 033 (play continues after the end) | Units stay alive after Success/Failure | Loadout edits made after a result change the **session** state and apply at the next deployment; the finished mission's units keep their working state. |

## 4. Data and ownership model

New folder `Assets/_Project/Scripts/Items/`. Pure C# classes where possible so EditMode tests need no scene.

- **`ItemDefinition`** (ScriptableObject, immutable): `Id` (authored string, stable), `DisplayName`, `Category` (`Weapon`, `Armor`, `Utility`, `Consumable`), `Slot` (`None`, `Weapon`, `Armor`, `Utility`), `MaxStack` (1 = not stackable), `Modifiers` (`StatModifiers`), `Weapon` (`CombatArchetype`, weapons only), `HealAmount` (consumables), `Icon` (optional sprite), `WeaponVisual` (optional `WeaponVisualAsset`), `Description`. `IsValid(out problem)` checks category rules (a weapon needs an archetype and slot Weapon; a consumable needs `HealAmount > 0`, no slot, `MaxStack > 1` allowed; an armor/utility needs its slot).
- **`ItemCatalogue`** (ScriptableObject): the list of definitions, `Find(id)`, `Validate(out errors)` (blank id, duplicate id, null entry, invalid definition: each error names the asset). Entries whose definition cannot be resolved stay in state, are shown as "Unknown item (id)", cannot be equipped or used, and raise one logged validation error.
- **`ItemEntry`** (`[Serializable]` class): `InstanceId` (GUID string, new on creation), `DefinitionId`, `Quantity`. A non-stackable entry has quantity 1 and its id never changes. A stack is also one entry with an id, so a transfer can address it. Splitting a stack gives the moved part a new id; merging into an existing stack keeps the destination's id.
- **`ItemInventory`**: ordered list of entries with a `Capacity` (0 = uncapped). Operations: `Add(definition, quantity, out leftover)` (fill existing stacks of the definition first, then new entries while below capacity), `Remove(instanceId, quantity)`, `Find(instanceId)`, `CountOf(definitionId)`, `CanFit(definition, quantity)`.
- **`ItemTransfer.Move(source, destination, instanceId, quantity)`** returns `TransferResult { Moved, Remaining, Reason }`. Atomic: removes from the source only what the destination accepted, in one method, so a failure cannot duplicate or destroy. Insufficient capacity moves what fits and reports the leftover left at the source. Moving a non-stackable item moves the same entry (same id). Refuses an entry that is equipped.
- **`OperativeEquipment`**: three slots, each holding the `InstanceId` of an entry in the owner's bag (never a copy). `TryEquip(bag, catalogue, instanceId)` checks the entry exists, resolves, and its slot matches. Equipping a second item in an occupied slot swaps the reference. `Unequip(slot)` clears it. `Resolve(bag, catalogue)` returns `EquippedItems` (weapon, armor, utility definitions) and drops a dangling reference (the entry vanished) after reporting it.
- **`OperativeLoadout`**: `ItemInventory Bag` + `OperativeEquipment Equipment`, keyed by the existing stable operative id.
- **`InventoryState`**: `ItemInventory Stash` (uncapped) + `Dictionary<string, OperativeLoadout>`; `Clone()` deep-copies every collection (instance ids preserved: the clone is the same items, in a working copy). Plain `[Serializable]` data with no Unity object references, ready for Phase 14.
- **`SquadInventory`** (MonoBehaviour on `Systems`, next to `SquadRoster`): holds the catalogue, the starter-loadout asset, the **session** `InventoryState`, and the **working** `InventoryState` while a mission runs. API: `BeginMission(instanceId)`, `AbortMission()`, `SettleMission(instanceId, MissionPhase)`, `Mode` (`Loadout` / `InMission`), `Equip/Unequip/Transfer` (refused with a reason while `InMission`), `Changed` event. `SeedStarter()` is idempotent (a granted-set of operative ids).
- **`StarterLoadout`** (ScriptableObject): per operative id, items to grant and which to equip; plus stash items. Missing definitions give actionable errors.

## 5. Equipment and effective configuration

- `EffectiveConfiguration.Evaluate(definition, state, track, equipped)` — a new overload; the three-argument form stays and means "no equipment system" (identical to today). The result gains `HasWeapon`, `WeaponName`.
- Inputs: role bonus + choice modifiers + armor modifiers + utility modifiers (summed with `StatModifiers.Combine`), weapon archetype from the weapon item. With a loadout and an empty Weapon slot: `HasWeapon = false` and the attack numbers are zero. The definition's archetype is not used then.
- Computed from authoritative inputs on every call; nothing is added or subtracted incrementally, so a refresh cannot double a bonus.
- `UnitIdentity.Bind(roster, member, loadout, catalogue)` evaluates with the loadout's resolved equipment; `Reapply` uses the same path. `UnitAttacker.ApplyEffective` gains `hasWeapon`; `UnitAttacker.HasWeapon` is true by default (units without a loadout).
- **No weapon:** `TryAttack` and `CanAttack` return false; `CommandableUnit.Issue(AttackCommand)` returns false and records a `NoWeapon` refusal that the HUD shows as "No weapon equipped". `EnemyAI`, `CompanionAI`, `AutoRetaliate`, the cursor and the controller Attack all go through `Issue`, so none can attack. Abilities are unaffected.
- Armor: `+20 max health` (modifier only; no damage reduction). Utility: `+0.5 m/s move speed`.
- Role-based abilities and identity are untouched; equipment grants no abilities.

### Weapon visuals

- `WeaponVisualAsset` (ScriptableObject): prefab, local position, local rotation, local scale. Offsets live here, not in combat code.
- `UnitWeaponVisual` (presentation component on the unit root): finds `WeaponSocket` under the right hand (the existing convention), owns one **managed** child named `EquippedWeapon`, and replaces only that child when the equipped weapon changes. It never touches the unit root, its scale or its identity. If no socket exists, it logs once and does nothing (capsule units).
- Darius's baked `Darius_Rifle` is removed from the prefab's socket and recreated by `UnitWeaponVisual` from the equipped item, so an unarmed Darius shows no rifle. EnemyUnit's nested `Rifle` prefab is treated the same way. (Prefab changes are limited to removing those two baked rifles; model, materials and animators are untouched.)
- Placeholders, documented in the decision record: the Combat Blade uses a primitive box mesh; the Marksman Rifle reuses `Rifle.prefab` at 1.25x length. Team tint is unaffected (it tints the body only).
- Armor and utility items change numbers and HUD text only.

## 6. Consumables and commands

- `UseItemCommand(instanceId)` and `CollectCommand(container, instanceId | all)` are new `UnitCommand`s handled by `CommandableUnit` like `AbilityCommand`/`InteractCommand`. No new scheduler.
- **`UnitItems`** (component on friendlies, like `UnitAbilities`): reaches the unit's working `OperativeLoadout` and the catalogue. `Check(command)` returns `ItemUseFailure` (`None`, `Dead`, `NotOwned`, `NotUsable`, `NoEffect`, `NoLoadout`). `TryUse` re-runs the check, then in this order heals through `Health.Heal` (the one healing implementation) and removes one unit. A consumed unit exists only if `Heal` restored more than 0; full health is rejected without consuming.
- **Timing:** `UseItem` is instant and runs on the first running frame, before steering is considered (the ability pattern); a failure ends the order and the queue moves on. No use interval in this phase: quantity is the limit (recorded as a decision).
- **`Issue` rules:** when it would start now the full check runs; behind other orders only static checks run (alive, has the component, entry exists as a consumable); full validation happens again at execution. Two queued uses with one medkit: the first succeeds, the second fails cleanly.
- **Collect:** `Issue` checks the unit is alive and the container exists and is searched; at execution the unit walks into the container's range (the interact approach and stall rules), then transfers via `ItemTransfer.Move` from the container inventory to the collector's bag. The source is revalidated inside the transfer, so two operatives taking the same entry cannot duplicate it. Container destroyed, unit dead, order cancelled, bag full: the order ends without exception and the queue continues.
- **Pause:** orders issued during tactical pause queue normally; nothing heals or moves until simulation time runs. Selecting an item or opening a panel never issues or reserves anything.
- Precedence, companion parking, explicit-order rules: unchanged (both new orders are explicit orders like Interact).

## 7. World loot

### 7.1 Placement (`LootPlacer`, pure)

- Input: `MissionLayout`, `ObjectivePlan`, `SecurityPlan`, `MissionSettings`, `SeededRandom.ForLoot(seed, attempt)` (a new salted stream; the layout, objective and security streams and their golden hashes are untouched), and a `LootTable`.
- `LootTable` (ScriptableObject): a `Version` int, `ContainerCount`, and weighted entries (definition, min/max quantity, weight). Output: a `LootPlan` of containers (tile + a list of `(definitionId, quantity)`) with its own FNV-1a hash that includes the table version.
- Tile rule: a 3x3 floor block free of boxes, inset from room edges by one tile (never in a doorway), at least 2.5 m from every spawn, guard, terminal, camera terminal and extraction zone, and not the same room as the friendly spawns when other rooms exist. Candidates are shuffled by the loot stream and bounded (tries per container = 40). Failure to place fewer than the requested count is reported in the generation report (`LootPlaced/LootRequested`) and logged once; it does not fail the attempt (loot is optional).
- Loot is **not** a required objective.
- Containers are solid crates created in the same pre-bake callback as the terminal, so the NavMesh bake includes them and `MissionNavigation` gets a new `ValidateLoot` check (a path from the first friendly spawn to a standing place within interaction range of each container). A container that fails the check fails the attempt, like terminals.
- Determinism: same seed + settings + table version gives the same layout, the same container tiles, and the same item types and quantities. Instance ids are new GUIDs per deployment.

### 7.2 Runtime

- `LootContainer` (MonoBehaviour on the crate, beside a `MissionInteractable`): owns an `ItemInventory` (uncapped) filled from the plan with new entry ids. `IsSearched`.
- First interaction: `MissionInteractable` with `duration = 1.5 s`. On `Completed` the container marks itself searched and calls `Rearm(duration: 0)`, a small addition to `MissionInteractable` (`Rearm`, and a `LastUser` captured before completion). Every later `InteractCommand` completes immediately in range and the container raises `OpenRequested(unit)`.
- Because the container is an ordinary interactable it is registered in `InteractableRegistry`, so click, R, context Confirm, queued Interact during pause, cursor snapping, `Knowledge.CanInteract` and the HUD interact prompt work with no new input paths. The prompt label comes from `displayName` ("Search container" / "Open container").
- The registry list is rebuilt by the director per mission; the crate lives under `GeneratedMissionRoot`, so teardown removes it, its inventory, its `OpenRequested` subscribers and pending interactions. The loot panel drops its container reference on `MissionStateChanged`/teardown.

## 8. Settlement and the mission boundary

- `MissionDirector.InstanceId` (GUID string) is created in `Run()` for every generation, including a repeat of the same seed.
- `Run()` calls `SquadInventory.BeginMission(instanceId)` after teardown and before spawn: the working state is `session.Clone()`. A failed generation calls `AbortMission()`; no working state survives it.
- `MissionFinished(Success)` → `SquadInventory.SettleMission(instanceId, Success)`: if `instanceId` is the active mission and not yet settled, the session state becomes the working state (replace, not merge) and the instance is marked settled; any other call is ignored. `MissionFinished(Failure)`, `Clear()`, regeneration and F6/F7 mid-mission discard the working state (the documented forgiving prototype policy; not the final extraction economy).
- Session and working collections never share a list (`Clone()` is deep); pickups touch only the working state.
- XP/progression is unchanged: `SquadRoster` still awards XP on Success through its own subscription. Squad-level extraction rule unchanged.
- Death never removes anything from session state (a runtime unit dying only ends that unit; its working bag is discarded with the mission on failure, and committed on success because the squad-level rule decides success).
- Starter seeding runs once when the `SquadInventory` builds its session state (after the roster); regeneration never reseeds.

## 9. Fog of war and information

- A container's location follows the existing static-object rule (`IntelligenceService.CanInteract`: its point's region is not Unknown). Unknown containers appear in no world marker, cursor snap, soft-target cycle, proximity list or HUD counter.
- Contents are readable only when `IsSearched`. The generator knowing the loot never makes the HUD, the panel or a log line show it; searched state is the only gate (`Knowledge`-style helper `LootKnowledge.CanSeeContents`).
- The player's own bag and equipment are always inspectable.
- No live updates for unobserved containers (nothing about a container changes unless a unit of the squad interacts with it).
- The HUD sweep test for Phase 11.5 is extended to assert that an unsearched container's items never appear in HUD text.

## 10. UI and input

- **Panel (`InventoryPanel`)**: a modal built through `HudFactory` in the existing canvas. Tabs per roster member; a clear header "Operative: <name>". Shows: carried entries with quantities, three equipment slots, item detail with a short comparison (damage / range / interval / modifier deltas against the currently equipped item in that slot), and the permitted actions:
  - Loadout mode: Equip / Unequip, Move to stash / Take from stash (all validated by `SquadInventory`; results shown as text), Deploy (new seed), Deploy (same seed).
  - Mission mode: Use / Queue use (consumables), the working bag with a "NEW" tag on entries not present in the pre-mission bag, and equipment changes disabled with the reason "Equipment can't be changed during a mission".
  - Container view: container entries, Take / Queue take, Take what fits (a single `CollectCommand` for all), the result text ("Bag full: 2 left in the container").
- **Authority:** the panel reads an inventory snapshot built in `HudSnapshotBuilder` (extended, not duplicated) and sends requests through `InventoryRequests`; it never edits items. The snapshot carries the inspected operative id; every request carries that id, never "the active character".
- **Input:** new actions `Inventory/Toggle` (keyboard `I`; pad Select/View/Create in all four pad groups), and the `UI` map's `Navigate`, `Submit`, `Cancel` get keyboard bindings (arrows, Enter, Esc) next to the existing pad ones, plus `TabPrevious`/`TabNext` (LB/RB, Q/E... only on the UI map, so no clash). While the panel is open:
  1. Gameplay action maps are disabled and the UI map enabled (the previous enabled state is stored and restored on close).
  2. `PointerOnlyInputModule` navigation is enabled for the panel's lifetime only.
  3. `DirectControlInput` is released through its existing release gate so no held key leaves a stuck move intent.
  4. Time, tactical pause and the real-time clock are untouched.
- Unclear controller coverage is stated honestly in the docs: the DualSense layout was seen by the owner earlier; this modal has been exercised with simulated devices only until the owner reports otherwise.
- The pre-mission loadout panel is open at scene start; Deploy hides it and generates.

## 11. Cleanup and data safety

- Regeneration removes old containers, the working state, pending Collect/Interact orders (units are replaced), loot-panel references and container markers.
- Session ownership survives per the settlement policy; starter equipment is never re-granted.
- Missing definitions: actionable validation errors from the catalogue and `StarterLoadout`; missing art falls back to a placeholder icon/visual.
- No existing operative definition, progression choice or imported art is rewritten, other than removing the two baked rifles named in section 5.

## 12. Tests

EditMode (pure): item definition validation, catalogue validation, inventory add/stack/capacity/leftover, transfer atomicity and failure paths (no duplication, no loss), equipment compatibility and swap, equipped-item transfer refusal, effective-configuration with and without equipment (recompute is idempotent; repeated equip/unequip does not accumulate; empty weapon slot), working-state isolation (clone is deep), one-time settlement (duplicate callback, failure discards, new instance for the same seed), starter seeding idempotence, loot-plan determinism (same seed/table), independence from the layout hash (table change does not change `MissionLayout.Hash`), placement rules, input asset tests (all pad families bound, no occupied key).

PlayMode: `UseItem` (alive/owned/full-health/queue/cancel/insufficient), `Collect` (partial take, two collectors, container destroyed, dead collector), no-weapon refusal for every decider, weapon visual replacement (only the managed child changes, root scale unchanged), mission cleanup (no stale containers), fog (unsearched contents never shown; HUD sweep), settlement end to end, input gating (no leak through the modal, no stuck input on close), scene wiring tests.

Manual checks, listed in `docs/Phase12-ManualTests.md` with the automated results kept separate. Physical-controller checks are listed as not performed.

## 13. Milestones

- **A** Items, inventory, transfer, equipment, state, catalogue, starter data and tests.
- **B** Effective configuration, `HasWeapon`, `UnitIdentity` binding, `UnitWeaponVisual`, assets for the six items.
- **C** `UnitItems`, `UseItemCommand`, `CollectCommand`, `LootContainer`, `LootPlacer`, director/spawner wiring, fog.
- **D** Inventory panel, input actions and modal gating, deploy flow, settlement, cleanup, regression, documentation.

Each milestone leaves the project compiling with all tests green. No commit and no push are made without the owner's request; the owner's request not to auto-commit overrides the brainstorming skill's default of committing the spec.

## 14. Decisions to record (decision 043 and following)

043 Inventory model, equipment, settlement and the modal exception to 042/024; loot generation stream; the no-use-interval ruling; the forgiving failure policy (explicitly a prototype policy); placeholder visuals.

## 15. Known limitations to document up front

In-memory only; no disk persistence; modal is the only pad focus mode; Blade and Marksman visuals are placeholders; the failure policy is not the final economy; unsearched container contents are not scannable by the Recon Scan; no corpse looting; transfers between operatives happen only in loadout mode.
