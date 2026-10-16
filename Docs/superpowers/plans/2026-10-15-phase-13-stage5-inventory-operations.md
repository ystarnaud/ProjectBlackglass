# Phase 13 Stage 5: persistent inventory operations — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

Date: 2026-10-15; revision 2, 2026-10-16. **Status: authorized for implementation by the owner on 2026-10-16, with rulings S5-D1 to S5-D8 (§A).** Every count below is a prediction until the as-built section records it. Base: `main` @ `f3cada4` (Stage 4 merged and pushed; schema version 4; backend ALL 1001/0, UNIT 488/0; Unity EditMode 1524/0, PlayMode 1434/0). Branch: `phase13-stage5`. Execution: subagent-driven, with three independent review checkpoints (after Task 3, after Task 7, and over the whole branch).

**Goal:** a player can rearrange their own persistent inventory through one atomic, version-checked batch endpoint. The batch can equip and unequip, move non-stackables and consumable quantities between the stash and an operative's bag, and equip straight from the stash. The rules match Unity's `InventorySession` wherever the two agree, proven by shared vectors run in both xUnit and Unity EditMode. Where the owner ruled the backend stricter (exact quantities, no partial moves) or different (retired items can leave a bag), backend-only tests pin it. Edits are refused with `409 InventoryLocked` while the account is in a mission; the lock source arrives with participations in Stage 7, but the check, its place under the row lock and its HTTP mapping are built and tested now.

**Architecture:**
- **Domain (pure).** `Accounts.Domain/Inventory` gains `InventoryModel`, `InventoryOperation`, `InventoryRefusal` and `InventoryEditor`. `Apply` replays a batch against an in-memory working copy, using the catalogue's definitions and `inventory.bagCapacity`; each operation is checked against the state the earlier operations left. It then validates the complete final state (`ValidateFinal`). It returns either the final model or the first refused operation's index and code.
- **Persistence.**
  - Migration `0005_inventory_edits` adds only the grants that edits need.
  - `InventoryEditStore` locks the `inventories` row, checks provisioning, the lock and the version, replays the batch with `InventoryEditor`, and writes the difference. It checks in the database that no unit was created or destroyed, bumps the version once and reads the result back, all in one transaction.
  - An `IInventoryLock` seam answers "is this account in a mission?" under that row lock. Stage 5 ships the only implementation possible before participations exist, which always answers no; Stage 7 must replace it with the PostgreSQL participation query before live mission admission. Picks and `GET /v1/inventory`'s `locked` use the same seam.
- **API.** `POST /v1/inventory/operations {expectedVersion, operations:[…]}` → `{version, inventory}`. It refuses with 409 (`VersionMismatch`, `NotProvisioned`, `InventoryLocked`) and with 422 `{code, index}`. Every player route maps refusals through one exhaustive switch.
- **Contract vectors.** `Backend/contracts/inventory/vectors.json`. Each case has a `scope`: `shared` cases run in xUnit against `InventoryEditor` and in Unity EditMode against `InventorySession`; `backend` cases run in xUnit only. One test per case on both sides.

**Tech stack:** as Stage 4 (ASP.NET Core minimal APIs on .NET 10, PostgreSQL 17, Npgsql 10, Dapper, xUnit against the real test cluster; Unity 6 EditMode through `Tools/run-tests.sh`).

**Spec:** `Docs/superpowers/specs/2026-10-14-phase-13-backend-design.md` revision 2.4 §3, §4.7, §5.1–§5.3, §15, §16.1 (`POST /v1/inventory/operations`), §18 B4, §19. It becomes revision 2.5 in Task 0, from the owner's rulings in §A. Stage plan: `Docs/superpowers/plans/2026-10-14-phase-13-backend.md` §Stage 5.

---

## A. Owner rulings (2026-10-16)

Revision 1 proposed S5-1 to S5-8. The owner ruled on each; these rulings bind every task.

- **S5-D1 — Approved: exact transfers and atomic batches.**
  - Every operation moves exactly the requested quantity or fails without changing anything. Every batch executes atomically: if any operation fails, the whole batch rolls back (`422 {code, index}`, nothing written, version unchanged).
  - **Unity is unchanged.** Its offline partial transfers (`ItemTransfer.Move` partly moving a stack into a bag with too little room, and clamping a request larger than the stack) stay as they are.
  - **Tests:** the shared vectors cover only the semantics both sides agree on. The backend's strict behaviour (`InsufficientQuantity`; `DestinationFull` where Unity would move part) has separate, backend-only tests and vector cases (`scope: backend`).
- **S5-D2 — Approved: fungible consumable stacks.**
  - Stacks are identified by account, location and definition; they have no permanent ids. A stack operation names `{operativeId, definitionId, quantity}`.
  - Moving part of a stack *is* a split; units arriving where that definition already sits *merge* into it (one `item_stacks` row per account, location and definition). There is no separate split or merge operation.
  - Bag occupancy uses the catalogue's `maxStack` (`ceil(quantity / maxStack)` entries per definition, spec §5.1) and the authoritative `InventoryRules` capacity (`inventory.bagCapacity`, 8).
  - **No zero-quantity stack row remains:** an emptied row is deleted.
  - The Unity vector runner moves units from the definition's *last* entry first, which keeps Unity's layout canonical so the comparison can check entry counts.
- **S5-D3 — Approved: five operations only.**
  - `equip`, `unequip`, `toStash`, `toBag` and `equipFromStash`.
  - A batch may move items between operatives through the stash (`toStash` then `toBag`), atomically. There is no direct bag-to-bag move.
  - No discard, trading, crafting, recruitment or other new operation. A batch can only rearrange existing units.
- **S5-D4 — Approved: migration 0005.**
  - `0005` grants `accounts_app` `UPDATE (operative_id, equipped_slot)` on `item_instances`, and `UPDATE (quantity)` and `DELETE` on `item_stacks` (it already holds `INSERT` there from 0004).
  - Permanent non-stackable items keep their immutable account, definition, identity and origin. The app gains no `DELETE` on `item_instances` and no update of any definition, account, origin or identity column.
  - 0000–0004 are unchanged.
- **S5-D5 — Approved: `InventoryLocked`.**
  - **Refusal order** under the `inventories` row lock: `NotProvisioned` → `InventoryLocked` → `VersionMismatch` → operation validation, in batch order. A locked account gets 409 `InventoryLocked` even with a stale version.
  - **Picks** get the same check, and the pick endpoint maps `InventoryLocked` to 409.
  - **Reads:** `GET /v1/inventory`'s `locked` comes from the same seam.
  - **HTTP:** `InventoryLocked` is 409 on every route, enforced by an exhaustive switch (no default arm) and a test that iterates every `PlayerError`.
  - **Stage 7 obligation:** the seam ships with an always-unlocked implementation; Stage 7 must connect it to real PostgreSQL participation records before live mission admission.
- **S5-D6 — Approved provisionally: batch limits.**
  - At most 32 operations per batch (`BLACKGLASS_INVENTORY_MAX_OPERATIONS`, 1..256) and a 16 KiB body, both provisional configuration (§19).
  - **Empty batches are rejected** (400), never treated as a state change.
  - A successful non-empty batch increments `inventoryVersion` exactly once (even when it ends where it started, e.g. re-equipping the equipped item); a failed batch increments it zero times.
  - **No automatic replay.** The server never refreshes the version and re-applies a mutation: not on `VersionMismatch`, not on a busy or unavailable database, not after an ambiguous commit. Neither the store nor the smoke test retries an edit.
- **S5-D7 — Revised: retired items do not trap capacity.**
  - An existing owned item or stack whose definition the active catalogue no longer knows **can** be unequipped and moved from a bag to the stash (`unequip`, `toStash`).
  - It **cannot** be equipped, moved from the stash to a bag, or equipped from the stash: those are refused `UnknownItem`.
  - Its permanent id, origin, ownership and quantity are preserved; nothing is silently deleted or replaced.
  - In a bag it takes one entry per instance and one entry per stack row (its `maxStack` is unknown). A partial `toStash` of a retired stack is allowed (the remainder still takes one entry).
  - **Tests:** backend-only (`scope: backend` vectors and editor/store tests), separate from Unity's offline unknown-item handling, which refuses transfers of unknown items and stays as it is.
- **S5-D8 — Approved: information-safe refusals.**
  - Foreign, nonexistent and incorrectly located item ids all get the same `422 {code: "NotFound", index}`, byte for byte. A foreign or nonexistent operative gets `UnknownOperative`, the same for both.
  - Every lookup and mutation is scoped to the session's account: the store loads only `where account_id = @a` rows and decides in memory, so the queries a request runs do not depend on whether a guessed id exists elsewhere. Knowing a Base58 id authorizes nothing.

### Original starter grants versus current holdings (owner requirement)

| Record | What it is | Changes after Stage 5 edits? |
|---|---|---|
| `inventories.starter_granted`, `starter_catalogue_version`, `starter_granted_at` | The fact, version and time of the original grant | **Never** (`inventories_forward_only`, 0004) |
| `item_instances.origin`, `origin_ref` | Each non-stackable's provenance (e.g. `starter:a42:op:…:item:item.rifle:n0`) | Never (not updatable); kept wherever the item moves |
| `item_instances.operative_id`, `equipped_slot` | Where the item is **now** | Yes (0005 grants) |
| `item_stacks` rows | Current fungible quantities per location | Yes. A stack has no provenance: once moved or merged, starter units are indistinguishable from any later units |
| `operator_actions` `account_create` / `starter_backfill` (granted) | The audited record of exactly what was granted, with counts | Never (append-only) |

Consequences built into Stage 5:
- **No reconstruction.** No code reconstructs "the starter" from current holdings. Counts of current holdings are never labelled as starter counts.
- **Backfill on an already-granted account:** today it reports the account's current counts under the starter labels (a Stage 4 deferred minor). Task 8 changes it to report `starterGranted=already` and the recorded `catalogueVersion` only; its audit row becomes `{granted: false, catalogueVersion}`.

---

## Global Constraints

- **Migrations:** forward-only, numbered, checksummed, one transaction each. **0000–0004 are applied and immutable.** Stage 5 adds `0005_inventory_edits.sql` only.
- **Ids:**
  - Operative and item ids are Base58 strings outside the database and `long` inside; definition ids stay catalogue strings. No new id format.
  - A non-stackable keeps its `bigint` identity, and therefore its Base58 form, through every transfer.
  - Mission attempts stay UUIDv7; sessions and credentials keep their UUIDs; stacks get no ids.
  - **No request carries an account id.** The account comes from the session (`AccountClaims.AccountOf`).
- **Locking (documented lock order):** `accounts` → `inventories` → owned rows (`operatives`, `item_instances`, `item_stacks`).
  - Edits and picks lock only the account's `inventories` row `FOR UPDATE` (the order minus its first step) and touch owned rows only while holding it.
  - Every writer of owned rows holds that lock: provisioning, backfill, picks, edits, and later settlement.
  - Waits are bounded by the connection timeouts; a timeout is 503 busy with nothing written, and is never retried by the server.
- **App role privileges:** exactly the grants in S5-D4. Still no privilege on identity sequences, no `DELETE` on `item_instances`, no update of any definition, account, origin or identity column.
- **Conservation:** an edit never changes the account's per-definition totals (instance count and stack quantity), nor its set of instance ids. `InventoryEditor.ValidateFinal` checks it in memory, and the store re-checks it in the database before commit; a mismatch throws, rolls back and is a 500.
- **Final state:** after a batch, every equipped instance is in its operative's bag with a known definition whose slot is the equipped slot; at most one item per operative and slot; every stack quantity is ≥ 1 and unique per location and definition; a bag whose entry count grew is within capacity (a bag already over capacity may stay over but never grow).
- **Failures:** `DbFailure.OpenAsync` and the Stage 3 classes; known refusals are 409/422 bodies `{code[, index]}`; logs carry no tokens.
- **Scope:** no recruitment, trading, crafting, purchases, mission settlement, energy, insurance, admission, item creation or destruction, new currency, respec or new progression.
- **Unity:**
  - **Allowed:** one new EditMode test file, `Assets/_Project/Tests/EditMode/InventoryContractTests.cs`, using the existing `ContractVectors.cs`.
  - **Not allowed:** any runtime, scene, prefab, input, HUD or project-settings change, including to build a test fixture. The offline scenes and gameplay, partial transfers included, are unchanged.
- **Touches:** `Backend/` (src, tests, `db/migrations/0005_inventory_edits.sql`, `contracts/inventory/`, the smoke script, README), `Docs/`, `CLAUDE.md`, and the one Unity test file.
- **Infrastructure:**
  - No live deployment, and no Docker, SSH, UFW, mission-VM or public-exposure change.
  - VM work uses the throwaway `bg-smoke`/`bg-drill` projects and the existing test cluster only.
  - Tests create or drop only the authorized test databases.
- **Preserve** the owner's two Unity settings files and `.claude/`.
- **Commit trailer:** `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- **Commands** (from `Backend/`):
  - ALL: `bash ../.claude/scratchpad/phase13/tools/dt.sh test Backend.sln`.
  - One class: `… test tests/Accounts.Integration.Tests --filter "FullyQualifiedName~<Class>"`.
  - UNIT: `--filter "Requires!=Postgres"`.
  - Unity, from the repo root with the Editor closed: `bash Tools/run-tests.sh EditMode [filter]`.

## Review Focus

1. **A guessed neighbouring id from another account.** Account B submits `equip`/`toBag` naming A's item id ± 1, or A's operative id ± 1.
   - Expected: 422 with the same `{code, index}` body, byte for byte, as an id that doesn't exist. Nothing is written, and neither account's version changes.
   - Test: Task 7 `A_guessed_neighbouring_item_or_operative_id_is_the_same_422_as_an_unknown_one`.
2. **Two batches on one version, or a batch racing a pick.** Exactly one succeeds; the other gets 409 `VersionMismatch`; the version goes up by exactly one.
   - Tests: Task 6 `Two_batches_with_the_same_version_one_wins_one_is_409` and `A_batch_and_a_pick_share_the_version`. Both are deterministic, using the held-lock pattern.
3. **A failure part-way through writing a batch.** A test-only trigger, created as superuser in the test database, raises on the batch's last stack write.
   - Expected: no instance moved, no stack changed, version unchanged, a 500 with a safe log line.
   - Test: Task 6 `A_failure_while_writing_rolls_back_the_whole_batch`.
4. **A slot swap across the unique slot index.** An `equip` that displaces the occupant, or `equipFromStash` replacing the weapon, writes without tripping `item_one_per_slot`.
   - How: the store clears departing slots before setting new ones.
   - Test: Task 6 `Equipping_over_an_occupied_slot_writes_in_index_safe_order`.
5. **A bag over capacity after a catalogue lowers `bagCapacity`, or holding retired items.**
   - Expected: `toStash`, `equip` and `unequip` still work, and `toBag` is `DestinationFull` unless the units fit in an open stack. Retired items leave the bag through `unequip`/`toStash`, and nothing else moves them.
   - Tests: `scope: backend` vector cases `over-capacity-*` and `retired-*` (Unity's public API cannot install an over-capacity state, and the owner ruled retired handling backend-only), and Task 6 `A_retired_item_can_be_recovered_to_the_stash`.

---

## Task 0 (controller, docs first): record the rulings

- **Files:**
  - The spec: revision 2.5, §0.5 summarizing S5-D1 to S5-D8; §5.3 operations, refusal order and the starter-versus-holdings table; §15 privileges planned for `0005`; §16.1 contract; §19 batch limits.
  - The stage plan's Stage 5 section (the vector path `contracts/inventory/vectors.json` replaces `contracts/inventory-vectors.json`, following Stage 4's layout).
  - `CLAUDE.md` (Stage 4 merged and pushed; Stage 5 authorized and in progress).
  - This plan: revision 2.
- **Commit:** `Phase 13 Stage 5: owner rulings S5-D1..S5-D8 recorded (spec revision 2.5, plan revision 2, CLAUDE.md)`.

## Task 1: `InventoryEditor`, the pure rules (domain)

**Files:**
- Create `src/Accounts.Domain/Inventory/InventoryModel.cs`, `InventoryOperation.cs` and `InventoryEditor.cs`.
- Test `tests/Accounts.Domain.Tests/Inventory/InventoryEditorTests.cs`.

**Interfaces (produced):**

```csharp
namespace Blackglass.Accounts.Domain.Inventory;

public sealed record HeldInstance(long ItemId, string DefinitionId, long? OperativeId, ItemSlot? Equipped); // OperativeId null = stash
public sealed record HeldStack(long? OperativeId, string DefinitionId, int Quantity);                     // Quantity >= 1
public sealed record InventoryModel(IReadOnlyList<long> Operatives, IReadOnlyList<HeldInstance> Instances, IReadOnlyList<HeldStack> Stacks);

public enum Destination { Stash, Bag }
public abstract record InventoryOperation(long OperativeId);
public sealed record Equip(long OperativeId, long ItemId) : InventoryOperation(OperativeId);
public sealed record Unequip(long OperativeId, ItemSlot Slot) : InventoryOperation(OperativeId);
public sealed record MoveInstance(long OperativeId, long ItemId, Destination To) : InventoryOperation(OperativeId);
public sealed record MoveStack(long OperativeId, string DefinitionId, int Quantity, Destination To) : InventoryOperation(OperativeId);
public sealed record EquipFromStash(long OperativeId, long ItemId) : InventoryOperation(OperativeId);

public enum InventoryRefusal { UnknownOperative, NotFound, UnknownItem, NotEquippable, NothingEquipped, Equipped, DestinationFull, InvalidQuantity, InsufficientQuantity }

public sealed record EditOutcome(InventoryModel? After, int? RefusedIndex, InventoryRefusal? Refusal)
{ public bool Applied => After is not null; }

public static class InventoryEditor
{
    /// Replays the batch in order against a working copy; each operation sees the earlier ones. The first refusal stops it
    /// and nothing applies. An applied batch then passes ValidateFinal. An empty batch is an ArgumentException (the API
    /// refuses it before it gets here, S5-D6).
    public static EditOutcome Apply(InventoryModel before, CatalogueDocument catalogue, IReadOnlyList<InventoryOperation> operations);
    /// Bag entries: instances + Σ ceil(qty / maxStack) per known stack definition + 1 per unknown-definition stack row (S5-D7).
    public static int BagEntries(InventoryModel model, CatalogueDocument catalogue, long operativeId);
    /// Throws InventoryInvariantException (a bug, never a refusal) unless `after` keeps the Global Constraints' final-state
    /// and conservation rules relative to `before`.
    public static void ValidateFinal(InventoryModel before, InventoryModel after, CatalogueDocument catalogue);
}
```

**Rules per operation**, in Unity's check order where Unity has the check. **API** marks a check or outcome Unity doesn't make (backend-only tests).
- **`Equip`** (`InventorySession.Equip` → `OperativeEquipment.TryEquip`):
  1. unknown operative → `UnknownOperative`;
  2. item not in *that* bag → `NotFound`;
  3. definition unknown → `UnknownItem`;
  4. `slot == none` → `NotEquippable`;
  5. else it takes the slot, and the occupant stays in the bag, unequipped. Re-equipping the equipped item succeeds.
- **`Unequip`** (`Unequip`): unknown operative → `UnknownOperative`; empty slot → `NothingEquipped`. No definition check: a retired item can be unequipped (S5-D7).
- **`MoveInstance` to the stash** (`InventoryState.MoveToStash`, then `ItemTransfer.Move`):
  1. unknown operative → `UnknownOperative` (**API**: Unity says `NotFound`);
  2. equipped → `Equipped` (checked before the lookup, as Unity does);
  3. not in the bag → `NotFound`;
  4. move (the stash is uncapped). A retired definition is allowed (**API**, S5-D7; Unity refuses `UnknownItem`).
- **`MoveInstance` to the bag** (`MoveToBag`): unknown operative → `UnknownOperative` (**API**); not in the stash → `NotFound`; `UnknownItem`; no free entry → `DestinationFull`.
- **`MoveStack`**, either direction:
  1. unknown operative → `UnknownOperative` (**API**);
  2. `quantity < 1` → `InvalidQuantity`;
  3. no stack of that definition at the source → `NotFound`;
  4. to the bag only: definition unknown → `UnknownItem` (to the stash a retired stack is allowed, **API**, S5-D7);
  5. held < quantity → `InsufficientQuantity` (**API**, S5-D1; Unity clamps);
  6. to the bag only: `ceil((held_d + q) / max) − ceil(held_d / max) > freeEntries` → `DestinationFull` (**API** when room is partial: Unity moves part; `freeEntries = max(0, capacity − entries)`, as `ItemInventory.FreeEntries`);
  7. move, merging into the destination's row; an emptied source row disappears.
- **`EquipFromStash`** (`InventorySession.EquipFromStash`):
  1. `UnknownOperative`;
  2. not in the stash → `NotFound`;
  3. `UnknownItem`;
  4. `slot == none` or stackable → `NotEquippable`;
  5. no free entry → `DestinationFull`;
  6. else move and equip, displacing the occupant (which stays in the bag). A refusal at any step leaves the item in the stash, unequipped.

- [ ] **Step 1: Write the failing tests.** One test per rule line above, plus the following.
  - **Displacement and batches:**
    - `Equip_displaces_the_occupant_which_stays_in_the_bag`
    - `A_batch_sees_its_own_earlier_operations`
    - `Several_operations_on_one_item_in_a_batch` (`toBag`, `equip`, `unequip`, `toStash` of the same item)
    - `The_first_refusal_stops_the_batch_and_returns_the_untouched_model`
    - `A_refused_equipFromStash_leaves_the_item_in_the_stash_unequipped` (as the second operation of a batch)
    - `A_bag_to_bag_move_is_toStash_then_toBag`
    - `An_empty_batch_is_an_argument_error`
  - **Capacity boundaries:**
    - `A_stack_fills_its_open_entry_in_a_full_bag`
    - `A_stack_needing_one_more_entry_than_free_is_DestinationFull`
    - `Exactly_filling_the_bag_succeeds`
  - **Strict transfers (S5-D1):** `More_than_held_is_InsufficientQuantity_not_a_clamp`, `Partial_room_is_DestinationFull_not_a_partial_move`.
  - **Conservation and stacks:**
    - `Splitting_and_merging_conserve_quantity`
    - `An_emptied_stack_disappears_from_the_model`
    - `Instances_keep_their_ids_through_every_move`
  - **Over capacity:** `Over_capacity_bags_allow_toStash_equip_and_unequip_but_not_toBag`.
  - **Retired definitions (S5-D7):**
    - `A_retired_item_can_be_unequipped_and_moved_to_the_stash`
    - `A_retired_stack_can_be_moved_to_the_stash_whole_or_in_part`
    - `A_retired_item_cannot_be_equipped_moved_to_a_bag_or_equipped_from_the_stash`
    - `BagEntries_counts_instances_canonical_stacks_and_unknown_rows`
  - **Final state:** `ValidateFinal_rejects_each_broken_invariant` (a theory over crafted bad models: created or lost units, a changed instance id or definition, an equipped item in another bag or the stash, a slot mismatch, two items in one slot, a zero stack, a duplicate stack row, a bag grown past capacity) and `ValidateFinal_allows_an_over_capacity_bag_that_did_not_grow`.
- [ ] **Step 2: Run** (expected FAIL: types missing), **implement**, **run** UNIT (expected PASS).
- [ ] **Step 3: Commit.** `Phase 13 Stage 5: InventoryEditor, the pure batch rules ported from InventorySession`.

## Task 2: The inventory vectors, backend side

**Files:**
- Create `Backend/contracts/inventory/vectors.json`.
- Test `tests/Accounts.Domain.Tests/Inventory/InventoryVectorTests.cs` (`[Theory]` over `MemberData`, one test per case, every scope).

**Format** (hand-written like `contracts/starter/vectors.json`; labels stand for ids so each runner maps them to its own ids):

```json
{ "note": "…", "cases": [ {
  "name": "equip-displaces-occupant",
  "scope": "shared",
  "bagCapacity": 8,
  "items": [ { "id": "item.rifle", "category": "weapon", "slot": "weapon", "stackable": false, "maxStack": 1, "healAmount": 0 } ],
  "operatives": ["op1"],
  "initial": { "stash": { "instances": [], "stacks": [] },
               "bags": { "op1": { "instances": [["r1","item.rifle"],["r2","item.rifle"]], "stacks": [], "equipped": { "weapon": "r1", "armor": null, "utility": null } } } },
  "operations": [ { "op": "equip", "operative": "op1", "item": "r2" } ],
  "expected": { "state": { "stash": { "instances": [], "stacks": [] },
                           "bags": { "op1": { "instances": [["r1","item.rifle"],["r2","item.rifle"]], "stacks": [], "equipped": { "weapon": "r2", "armor": null, "utility": null } } } } }
} ] }
```

- **Scope:** `shared` (both runners) or `backend` (xUnit only). A case is `backend` when it exercises a backend-only rule (**API** above: strict quantities and partial room, unknown operatives on transfers, retired definitions) or a state Unity's public API cannot build (over-capacity bags). Each `backend` case carries a `"why"` string naming the reason.
- **Operations:** `{op: equip|unequip|toStash|toBag|equipFromStash, operative, item | slot | (definition, quantity)}`.
- **Expected:** either `{"state": …}` or `{"refused": {"index": n, "code": "<InventoryRefusal>"}}`.
- **Labels:** `ghost` names an item that exists nowhere, and `opX` an operative not in the case.
- **Items:** each case lists only the items it needs, including a definition absent from `items` for the retired cases.

**Cases (at least 40):**
- **Shared:**
  - **Equip:** from the bag; displacing the occupant; re-equipping the equipped item; a consumable (`NotEquippable`); an item in the stash (`NotFound`); a ghost; an unknown operative.
  - **Unequip:** unequip; unequip an empty slot; an unknown operative.
  - **To the stash:** an instance; an equipped instance (`Equipped`); part of a stack; a whole stack; quantity 0 (`InvalidQuantity`).
  - **To the bag:** an instance into a full bag (`DestinationFull`); an instance into one free entry; a stack into the open entry of a full bag; a stack that exactly fills the bag; a stack into a full bag with no open entry (`DestinationFull`, nothing moved on either side).
  - **Equip from the stash:** success; full bag; a consumable; displacing the weapon; a ghost.
  - **Batches:** the second operation refused (index 1, the first not applied); a bag-to-bag move between two operatives; an equip after a `toBag` in the same batch; an unequip, then `toStash`; several operations on one item.
  - **Merge and split:** a merge into an existing stash stack; a split that leaves one canonical entry.
  - **Capacity 1:** boundary cases.
- **Backend only:**
  - **Strict:** more than held (`InsufficientQuantity`); a stack needing one more entry than free (`DestinationFull`, Unity would move part); one unit over the exact fill.
  - **Unknown operative** on `toStash` and `toBag` (`UnknownOperative`; Unity says `NotFound`).
  - **Retired:** equip, `toBag` and `equipFromStash` refused (`UnknownItem`); unequip then `toStash` of a retired instance; a retired stack to the stash, whole and in part; a retired stack merging into a stash row.
  - **Over capacity:** `toStash`, `equip` and `unequip` allowed; `toBag` refused unless the units fit an open stack.

- [ ] **Step 1: Write the vectors and the runner** (the runner builds an `InventoryModel` with ids 1, 2, … per label in file order, and fails on a missing or unknown `scope`).
- [ ] **Step 2: Run.** Expected PASS. **Mutation check:** change one expected quantity → FAIL; revert.
- [ ] **Step 3: Commit.** `Phase 13 Stage 5: shared inventory vectors (xUnit runner)`.

## Task 3: The inventory vectors, Unity side

**Files:** create `Assets/_Project/Tests/EditMode/InventoryContractTests.cs` (the only Unity file Stage 5 touches).

**The runner:** a `TestCaseSource` with one NUnit case per `shared` vector case (Stage 4's single-test runners hid the cases after a first failure). `backend` cases are skipped by the source, never run; one extra test asserts that every case has a known scope and that the shared count is at least 30, so a mass reclassification is caught.
- **Setup:**
  - Build `ItemDefinition`s through the internal `ItemDefinition.Create` (the test assembly already sees internals), an `ItemCatalogue`, and `new InventorySession(catalogue, bagCapacity)`.
  - Create the initial state with `Session.EnsureLoadout` and `ItemInventory.Add`, mapping each label to the created entry's `InstanceId` in creation order, then `TryEquip`.
  - No runtime code changes; over-capacity and retired cases are `backend` scope (§A S5-D1, S5-D7; `InventorySession.Session` has no public setter to install an over-capacity state).
- **Operations:** apply each through `InventorySession.Equip`, `Unequip`, `ToStash`, `FromStash` and `EquipFromStash`.
- **Addressing adapter** (not a rule change): a stack operation names a definition and a quantity; the runner moves units from the definition's last entry first, possibly over several calls, stopping at the first failure. A shared case never asks for more than is held or for partial room, so a `TransferResult.IsPartial` in a shared case fails the test as a misclassified vector.
- **Mapping Unity's outcomes to codes:**
  - `TransferFailure` maps by enum.
  - `EditResult.Reason` maps through a fixed table of Unity's reason strings: "That item is not in this bag." → `NotFound`, "Unknown operative." → `UnknownOperative`, "Unknown item '…' cannot be equipped." → `UnknownItem`, "… can't be equipped." → `NotEquippable`, "Nothing is equipped there." → `NothingEquipped`, "That item is no longer in the stash." → `NotFound`, "The bag is full." → `DestinationFull`.
  - A reason not in the table fails the test, so a Unity text change is caught.
- **Comparison:**
  - Refused cases: the code and index only. Unity applies operations one at a time and has no batch rollback; atomicity is a backend property, tested in Tasks 1 and 6.
  - Applied cases: per location, the set of instance labels, the per-definition stack totals, **the per-definition bag entry count equal to `ceil(total / maxStack)`** (proving the adapter keeps Unity canonical), and the equipped label per slot.

- [ ] **Step 1: Measure the EditMode baseline** (expected 1524/0 after Stage 4).
- [ ] **Step 2: Write the runner; run** `bash Tools/run-tests.sh EditMode InventoryContract`. Expected: all shared cases PASS.
  - **If a shared case fails because Unity really differs:** report it; the controller rules either to align the domain and vector to Unity (common semantics) or to reclassify the case `backend` with a `why` (an owner-ruled difference), and ledgers the ruling. Unity code is never changed.
  - **Mutation check:** change one expected code in a shared vector → xUnit and EditMode both FAIL; revert.
- [ ] **Step 3: Run** the full EditMode suite (expected baseline + one per shared case + 1), then **commit** `Phase 13 Stage 5: Unity EditMode runs the shared inventory vectors against InventorySession`.

**Review checkpoint 1 (independent, most capable model):** rules parity over Tasks 1–3, including the domain, the vectors and their scopes, both runners and the adapter. Fix Critical and Important findings before Task 4.

## Task 4: Migration `0005_inventory_edits` and privileges

**Files:**
- Create `db/migrations/0005_inventory_edits.sql`.
- Tests: extend `Database/PlayerSchemaTests.cs`; create `Database/InventoryPrivilegeTests.cs`.

```sql
-- 0005 inventory edits (Stage 5; spec §5.3, rulings S5-D3, S5-D4). Edits move instances between the stash, bags and slots,
-- and change stack quantities; they never create or destroy an instance or touch a definition, account, origin or identity.
grant update (operative_id, equipped_slot) on item_instances to accounts_app;
grant update (quantity), delete on item_stacks to accounts_app;
```

- [ ] **Step 1: Write the failing tests** (as `accounts_app`, against the real cluster).
  - **Grants:**
    - `The_app_can_move_and_equip_an_instance_but_not_change_what_or_whose_it_is`: updates of `operative_id` and `equipped_slot` pass; `item_id`, `definition_id`, `account_id`, `origin` and `origin_ref` are 42501.
    - `The_app_cannot_delete_an_instance` (42501).
    - `The_app_can_change_a_stack_quantity_and_delete_a_stack_but_not_move_its_row`: `operative_id`, `definition_id` and `account_id` are 42501.
    - `The_app_still_has_no_sequence_privilege`.
    - `The_grants_are_exactly_these` (reads `information_schema` column and table privileges for `accounts_app` on the two tables and compares them with the full expected set).
  - **The schema still holds:**
    - `A_cross_account_move_is_refused_by_the_composite_key`: updating an item's `operative_id` to another account's operative is 23503.
    - `An_equipped_item_cannot_go_to_the_stash_in_the_database`.
    - `The_slot_index_still_holds_for_updates`.
    - `A_zero_stack_quantity_is_refused` (the `quantity > 0` check, 23514).
  - **Upgrade:** `Upgrade_from_0004_with_data_keeps_every_row` (version 4 with a provisioned account, migrated to 5, invariants hold).
- [ ] **Step 2: Run** (expected FAIL), **write the migration**, **run** (expected PASS).
- [ ] **Step 3: Commit.** `Phase 13 Stage 5: migration 0005 grants exactly the inventory-edit privileges`.

## Task 5: The `IInventoryLock` seam; picks and reads use it

**Files:**
- Create `src/Accounts.Persistence/Player/InventoryLock.cs`.
- Modify `PickStore.cs`, `PlayerReadStore.cs`, `PlayerException.cs` (add `InventoryLocked`), `PlayerEndpoints.cs` (the pick route maps `InventoryLocked` → 409) and `ServeCommand.cs` (register `NoParticipationsYet`).
- Tests: `Database/InventoryLockTests.cs`, and extend `PickStoreTests` and the pick API tests.

```csharp
/// Answers, under the caller's inventories row lock (or in a read snapshot), whether the account is in a mission.
public interface IInventoryLock { Task<bool> IsLockedAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long accountId, CancellationToken ct); }
/// Stage 5: participations do not exist yet (spec §7.1, Stage 7), so no account is ever locked. Stage 7 must replace this
/// with the PostgreSQL participation query before live mission admission.
public sealed class NoParticipationsYet : IInventoryLock { public Task<bool> IsLockedAsync(...) => Task.FromResult(false); }
```

- [ ] **Step 1: Write the failing tests.** They use a test double `AlwaysLocked` from the test project.
  - `A_pick_on_a_locked_inventory_is_InventoryLocked_and_writes_nothing`.
  - `InventoryLocked_comes_before_VersionMismatch`.
  - `NotProvisioned_comes_before_InventoryLocked`.
  - `The_lock_is_asked_while_the_inventories_row_is_held` (the double records that the row is locked when it is called, e.g. by a `NOWAIT` probe from a second connection failing with 55P03).
  - `The_inventory_read_reports_locked_from_the_seam`.
  - `Unlocked_is_the_Stage_5_default`.
  - API: `A_locked_pick_is_409_InventoryLocked`.
- [ ] **Step 2: Run** (expected FAIL), **implement** (stores take `IInventoryLock`; the existing constructors default to `NoParticipationsYet`), **run** (expected PASS).
- [ ] **Step 3: Commit.** `Phase 13 Stage 5: the InventoryLocked check under the row lock (picks and reads), always unlocked until participations exist`.

## Task 6: `InventoryEditStore`

**Files:**
- Create `src/Accounts.Persistence/Player/InventoryEditStore.cs` and `InventoryRefusedException`.
- Move the inventory-view reader in `PlayerReadStore` into an internal static helper shared with the edit store.
- Test `Database/InventoryEditStoreTests.cs`.

**Interface:** `Task<(long Version, InventoryView Inventory)> ApplyAsync(long accountId, long expectedVersion, IReadOnlyList<InventoryOperation> operations, CancellationToken ct)`.

**Algorithm**, in one READ COMMITTED transaction, with no retry anywhere (S5-D6):
1. **Lock and check.** `select version, starter_granted from inventories where account_id = @a for update`. Then, in this order: `NotProvisioned` → `IInventoryLock` (`InventoryLocked`) → `VersionMismatch`.
2. **Load and replay.** Load the account's operative ids, instances and stacks (always `where account_id = @a`) into an `InventoryModel`. Run `InventoryEditor.Apply`; a refusal throws `InventoryRefusedException(index, code)` with nothing written.
3. **Write the difference:**
   1. clear `equipped_slot` on every instance whose slot changes;
   2. update `operative_id` and `equipped_slot` on every moved or newly equipped instance;
   3. update, insert or delete each changed stack row, with null and non-null `operative_id` predicates kept separate (the stack table has no key to update by). No zero row is ever written.

   Every statement carries `account_id = @a` and asserts its affected-row count.
4. **Check conservation.** Re-read the per-definition totals and the instance id set, and compare them with the loaded model; a mismatch throws (rollback, 500).
5. **Finish.** `update inventories set version = version + 1 returning version`, read the view in the same transaction, then commit.

- [ ] **Step 1: Write the failing tests.**
  - **Behaviour:**
    - `A_batch_applies_every_operation_and_bumps_the_version_once`
    - `A_refused_batch_writes_nothing_and_reports_the_index_and_code`
    - `A_refused_equipFromStash_late_in_a_batch_leaves_everything_as_it_was`
    - `Several_operations_on_one_item_in_a_batch_persist_the_final_position`
    - `A_stale_version_is_VersionMismatch`
    - `An_unprovisioned_account_is_NotProvisioned`
    - `A_locked_inventory_is_InventoryLocked` (with `AlwaysLocked`)
    - `Equipping_over_an_occupied_slot_writes_in_index_safe_order` (Review Focus 4)
    - `An_emptied_stack_row_is_deleted_and_a_new_location_gets_a_row`
    - `Instances_keep_their_ids_and_origins_through_every_move`
    - `A_retired_item_can_be_recovered_to_the_stash` (the definition removed from the catalogue the store is given; unequip and `toStash` succeed, `toBag` back is `UnknownItem`; ids, origin and quantities preserved)
  - **Isolation between accounts:**
    - `Another_accounts_item_is_NotFound_and_its_operative_UnknownOperative`
    - `Nothing_of_another_account_is_read_or_written` (two accounts; B's rows and version are byte-identical after A's batch)
  - **Concurrency:** deterministic, held-lock pattern from Stage 4.
    - `Two_batches_with_the_same_version_one_wins_one_is_409` (Review Focus 2)
    - `A_batch_and_a_pick_share_the_version`
    - `Batches_on_one_account_serialize_behind_the_row_lock`
    - `A_batch_behind_a_held_lock_times_out_and_writes_nothing`
    - `Concurrent_batches_conserve_units` (several real concurrent batches with fresh versions, retried by the test only, end with the same per-definition totals)
  - **Failure and conservation:**
    - `A_failure_while_writing_rolls_back_the_whole_batch` (Review Focus 3; the trigger is created and dropped as superuser in the test database only)
    - `Conservation_holds_for_every_vector_case`: replay each applied vector case (every scope) against PostgreSQL and compare the stored state with the vector's expected state, so the vectors also cover persistence.
- [ ] **Step 2: Run** (expected FAIL), **implement**, **run** the class, then ALL (expected PASS).
- [ ] **Step 3: Commit.** `Phase 13 Stage 5: atomic, version-checked inventory batches under the row lock`.

## Task 7: `POST /v1/inventory/operations`, and exhaustive refusal mapping

**Files:**
- Modify `Player/PlayerEndpoints.cs` and `Player/PlayerDtos.cs` (`InventoryOperationsRequest`, `OperationDto`, `InventoryOperationsResponse`), and `ServeCommand.cs`.
- Tests: `Database/InventoryApiTests.cs`; extend `Database/PlayerAuthorizationTests.cs`, `Unit/DtoAuditTests.cs` and `Database/LogSecrecyTests.cs`; create `Unit/PlayerRefusalMappingTests.cs`.

**Contract:**
- **Request:** `{"expectedVersion":3,"operations":[{"op":"equip","operativeId":"2","itemId":"9"},{"op":"unequip","operativeId":"2","slot":"armor"},{"op":"toStash","operativeId":"2","itemId":"9"},{"op":"toBag","operativeId":"2","definitionId":"item.medkit","quantity":2},{"op":"equipFromStash","operativeId":"2","itemId":"12"}]}`.
  - Each operation takes exactly its members. `toStash`/`toBag` take either `itemId`, or `definitionId` plus `quantity`.
  - Numbers are strict; ids are canonical Base58; an unknown member, an account id included, is refused.
- **200:** `{"version":4,"inventory":{…as GET /v1/inventory…}}`.
- **400** `InvalidBody`: malformed JSON, a missing or unknown member, an empty list, or an over-limit list (`TooManyOperations`). **400** `{code:"InvalidOperation", index}`: an operation with the wrong members or a bad id.
- **413** over 16 KiB. **415** for a non-JSON content type; Stage 4's pick route is aligned to 415 here, from its deferred minor.
- **409** `VersionMismatch`, `NotProvisioned` or `InventoryLocked`. **422** `{code, index}` with an `InventoryRefusal` code.
- **Every route:** session only, `PerAccount` rate limit, `Cache-Control: no-store`, the uniform 401, and the `DbFailure` classes. No server-side retry.
- **`Refusal`** becomes a switch over every `PlayerError` with **no default arm**: `InventoryLocked`, `VersionMismatch` and `NotProvisioned` → 409; `OperativeNotFound` → 404; `NoPendingPick` and `UnknownChoice` → 422.

- [ ] **Step 1: Write the failing tests.**
  - **API:**
    - `A_batch_round_trip_persists_and_returns_the_new_inventory` (item ids in the response are the same Base58 strings as before)
    - `Each_operation_shape_is_accepted_and_wrong_shapes_are_400_with_the_index`
    - `Refusals_are_422_with_code_and_index`
    - `Stale_unprovisioned_and_locked_are_409` (locked through a test-configured `AlwaysLocked`)
    - `Limits_are_enforced` (33 operations, empty list, 16 KiB + 1, `text/plain`)
    - `Every_inventory_route_without_a_token_is_the_uniform_401`
    - `A_held_inventory_lock_is_503_busy_and_nothing_is_replayed` (after the lock is released, the version and rows are unchanged)
  - **Authorization:**
    - `A_guessed_neighbouring_item_or_operative_id_is_the_same_422_as_an_unknown_one` (Review Focus 1)
    - `An_account_id_anywhere_in_the_request_changes_nothing`
  - **Mapping:** `PlayerRefusalMappingTests.Every_PlayerError_maps_and_InventoryLocked_is_409` (iterates `Enum.GetValues<PlayerError>()`).
  - **Logs:** the new route logs no token.
- [ ] **Step 2: Run** (expected FAIL), **implement**, **run ALL** (expected green), then **commit** `Phase 13 Stage 5: POST /v1/inventory/operations; every player refusal mapped exhaustively (InventoryLocked 409)`.

**Review checkpoint 2 (independent, most capable model):** schema, privileges, the lock seam, the store and the endpoint, over Tasks 4–7, with emphasis on IDOR, atomicity, lock order, conservation and retired items. Fix Critical and Important findings before Task 8.

## Task 8: Starter grant versus current holdings

**Files:**
- Modify `AccountAdminStore.BackfillStarterAsync`/`BackfillResult` and `AdminCommand` (`starter backfill` output).
- Tests: `BackfillTests`, `AdminTests`.
- Docs: the spec §5.4 table (from §A).

- [ ] **Step 1: Write the failing tests.**
  - `An_already_granted_backfill_reports_the_recorded_version_and_no_counts` (after an edit has moved items, so current counts differ from the grant).
  - `The_already_audit_row_has_no_holdings_counts`.
  - `A_granting_backfill_still_reports_the_grant_counts`.
- [ ] **Step 2: Run** (expected FAIL), **implement**, **run** (expected PASS); update the smoke script's backfill check; **commit** `Phase 13 Stage 5: backfill never labels current holdings as the starter`.

## Task 9: Smoke and VM

- [ ] **Step 1: Extend** `deploy/services/tests/smoke-test.sh` with two checks.
  - After creating an account and logging in, a `POST /v1/inventory/operations` that unequips and re-equips the starter weapon returns 200 with the version up by one.
  - A replay of the same body with the old version gets 409 `VersionMismatch` and is not retried.
- [ ] **Step 2: VM** (throwaway projects only, after `vm-sync.sh`):
  - smoke at schema 0005 (with the hold), the off-host probe during the hold, and the drill (invariants);
  - afterwards: only `blackglass-testdb` running, listeners unchanged, no throwaway volumes.
  - Transcripts go to `.claude/scratchpad/phase13/stage-05/`.
- [ ] **Step 3: Commit** the smoke change: `Phase 13 Stage 5: smoke test round-trips an inventory batch`.

## Task 10: Docs as built, final review, stop

- [ ] **Step 1:**
  - **Docs:** spec (§0.5 measured, §15 as built, §16.1, §19), `Backend/README.md` (route, codes, limits, privileges, no replay), the stage plan's Stage 5 status, `CLAUDE.md`, and this plan's "As built" with every ledger ruling and deferred minor.
  - **Counts:** backend ALL and UNIT, Unity EditMode and PlayMode, against the baselines.
  - **Path audit:** 0000–0004 unchanged; only the listed paths changed; the owner's files and `.claude/` untouched; secret scan.
- [ ] **Step 2: Review checkpoint 3 (final, independent, most capable model)** over the whole branch. Fix Critical and Important findings.
- [ ] **Step 3: Commit and stop.** No merge, push, live deployment or Stage 6 without the owner.

## Acceptance criteria

| # | Criterion | Proof |
|---|---|---|
| 1 | The five operations follow `InventorySession`'s rules where both agree; the backend-only rules (exact quantities, no partial moves, retired recovery) are explicit and tested separately | `InventoryEditorTests`; shared vectors in xUnit and EditMode, backend vectors in xUnit, one test per case |
| 2 | A batch is all-or-nothing: a refusal or a failure part-way writes nothing and leaves the version unchanged; intermediate operations see the working state and the final state is validated | `InventoryEditStoreTests` (refused, injected failure), `ValidateFinal` tests |
| 3 | Edits are version-checked; concurrent batches and picks serialize on the row lock; exactly one of two same-version requests wins; nothing is replayed | Concurrency and API tests |
| 4 | `InventoryLocked` is checked under the row lock before the version, for edits and picks; it is 409 everywhere; the mapping is exhaustive | `InventoryLockTests`, `PlayerRefusalMappingTests`, API tests |
| 5 | Bag capacity is the catalogue's `bagCapacity` (8); stack entries are canonical; boundaries and over-capacity bags behave as ruled | Vectors, editor tests |
| 6 | Stack splits and merges conserve units; no zero row remains; conservation is checked in every transaction and under concurrency | Editor and store tests |
| 7 | No route takes an account id; a foreign or guessed item or operative id is indistinguishable from an unknown one; ownership can't cross accounts in the database | Authorization tests, composite-key test |
| 8 | Privileges are exactly S5-D4; 0000–0004 unchanged; 0005 applies on 0004 with data; drill and smoke pass | Privilege and upgrade tests, VM transcripts |
| 9 | Original starter grants are distinct from current holdings in schema, audit and CLI output | §A table, Task 8 tests |
| 10 | Retired items can leave a bag (unequip, `toStash`) and nothing else; their ids, origin and quantities are preserved | Editor, vector and store tests |
| 11 | No Unity runtime, scene or settings change; only `InventoryContractTests.cs` is added | Path audit |

**Predicted counts (predictions, not measurements):** backend ALL ≈ 1001 + 190; Unity EditMode ≈ 1524 + one per shared vector case (≈ 32) + 1; PlayMode unchanged.

## Out of scope

- Recruitment, trading, crafting, purchases, energy, insurance, admission and mission settlement.
- Item creation, destruction or use outside missions; discard.
- The participation source behind `InventoryLocked` (Stage 7).
- Wiring the offline client or `SquadInventory` to the backend; any change to Unity's offline transfer rules.
- Live deployment.
