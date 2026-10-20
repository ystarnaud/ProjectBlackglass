# Ledger scenarios v1

Shared mission scenarios for the Unity item ledger (Stage 11, owner ruling D3). Each file is one participant's mission: an
admission snapshot, the attempt's loot pool and objectives, the authoritative result, the item events in order, and the
format-v1 manifest those events must produce, written by hand. The same file is judged twice, independently:
- **Unity** (`Assets/_Project/Tests/EditMode/Backend/LedgerScenarioVectorTests.cs`) replays the events through its real
  inventory code (`ItemInventory`, `ItemTransfer`, `OperativeEquipment`) and the `MissionItemLedger`, freezes, builds, and must
  produce exactly the canonical bytes of `expectedManifest`.
- **The backend** (`Backend/tests/Accounts.Domain.Tests/Settlement/LedgerScenarioTests.cs`) reconstructs the snapshot
  (`LoadoutSnapshot.Parse`), the pool and the objectives, parses `expectedManifest` with `ResultManifest.Parse`, and runs the real
  `ManifestValidator`, which must return exactly `expectedErrors`. Clean scenarios must also plan a settlement whose owned
  debits and collections are the manifest's.

**The expected manifests are the authority**, like the manifest-v1 vectors. A disagreement is investigated against the
hand-written manifest; Unity's output is never copied into a scenario, and the validator is never adjusted to one. A change to
any scenario is a contract change and needs the owner. `.gitattributes` keeps the bytes exact.

## Format

- `attempt`: `attemptId`, `processSessionId`, `buildRevision`, `layoutHash`, `lootTableVersion` (the header the process knows).
- `snapshot`: the admission snapshot in `LoadoutSnapshot.ToJson`'s shape. Its `catalogueVersion` is the golden catalogue's
  (`Backend/contracts/catalogue/golden-catalogue.json`): `item.alpha-rifle` (weapon), `item.beta-vest` (armor),
  `item.delta-boots` (utility), `item.gamma-kit` (consumable, stacks to 3); bag capacity 6; loot table version 7.
- `pool`: the registered loot pool. Unity fills each container with one `ItemInventory(0).Add` per line, in line order, and
  the fill must leave exactly one entry per line. `Add` merges a stackable into an open stack of the same definition, so two
  lines of one stackable sit in different containers.
- `objectives`: `{objectiveId, required, state}`; `result`: `{outcome, outcomeSequence, serverElapsedMs, dead}`.
- Optional `bagCapacity` overrides the catalogue's 6; optional `build` is `"normal"` (default) or `"fallback"` (Unity builds
  the D2 fallback from the snapshot and replays no events).
- `events`, in order:
  - `collect {operativeId, containerIndex, line, quantity}`: a requested take; Unity moves what fits, and the ledger records
    what moved;
  - `use {operativeId, definitionId}`: one unit;
  - `equip {operativeId, item: {itemId} | {containerIndex, line}}`: an owned item, or the collected entry of a line;
  - `rogueAdd {operativeId, definitionId, quantity}`: a bag change with no ledger event (a wiring bug), detected at the freeze.
- `expectedManifest` (any valid encoding; compared in canonical form) and `expectedErrors` (the validator's exact codes).

## The arithmetic

- **01 owned-swap-and-use:** owned rifles `2k` (weapon) and `2m`, vest `2n`, kit ×3. Equip `2m` (the weapon swaps; `2k` stays
  held), equip `2n` (armor), two uses take owned kits: consumed owned 2, held stack 1.
- **02 loot-first-consumption:** owned kit ×2; collect (0,0)×2 (one runtime stack of 3 plus 1). Three uses: loot first, so
  (0,0)×2, then owned ×1. Collected 2, consumed loot 2 and owned 1, held stack 1, no loot left.
- **03 partial-collect:** capacity 2, already full (rifle, kit ×2). Collect (0,0)×2 moves 1 (it tops the stack up to 3); the
  line keeps 1 under the same entry id. Use takes the loot unit, use takes an owned unit (stack 1). Collect (0,0)×2 moves the
  last 1 into the same lot. Collected 2, consumed loot 1 and owned 1, held loot (0,0)×1, held stack 1.
- **04 loot-equipment:** owned rifle `z` (= 57, weapon) and vest `21` (= 58, armor). Collect the rifle at (0,0), equip it:
  the weapon is the loot line (0,0), `z` stays held. Held items sort numerically: `z` before `21`.
- **05 two-operatives-defeat:** C (kit ×1) and D share line (0,0)×2: each collects 1. D uses its loot unit; C uses its loot
  unit, then its owned unit. D collects the rifle (1,0) and dies; defeat. C: collected 1, consumed loot 1 and owned 1. D:
  collected (0,0)×1 and (1,0)×1, consumed loot (0,0)×1, held loot (1,0)×1. The line's total is 2, its pool quantity.
- **06 oldest-first:** owned kit ×1; collect (1,0)×2, then (0,0)×2. Three uses: (1,0) is older, so (1,0)×2, then (0,0)×1.
  Held loot (0,0)×1, held stack 1.
- **07 ledger-mismatch:** collect (0,0)×1, then a rogue kit appears with no event. At the freeze the bag holds 3 kits and the
  ledger 2: `ledgerMismatch`, with the ledger's facts. The validator reports only `V14.integrity`; the backend quarantines it.
- **08 ledger-mismatch-fallback:** the fallback restates the admission snapshot (C: rifle `2k` equipped, kit ×2; D: vest `2m`
  equipped) with nothing collected or consumed, the authoritative result (defeat, D dead) and `ledgerMismatch`. The validator
  reports only `V14.integrity`; the backend quarantines it and settles nothing.
