# Phase 13 Stage 4: persistent operatives, starter inventory, account provisioning and advancement picks — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

Date: 2026-10-15. **Status: revision 2.** Approved by the owner on 2026-10-15 with rulings D1–D6 (§A), which supersede revision 1's recommendations wherever they conflict. Execution is subagent-driven, with the primary session owning integration. Independent reviews are required after (1) identity, schema and migrations, (2) provisioning, picks and endpoints, and (3) final integration. Base: `main` @ `fc823e8` (schema version 2). Branch `phase13-stage4`. **Every test count below is a prediction, not a measurement.**

**Goal:**
- Every account created from Stage 4 on gets, **in the same transaction as its creation**, exactly one starter roster and starter inventory from the exported catalogue.
- Accounts created earlier get theirs through an explicit, idempotent, audited **backfill**.
- Operatives and non-stackable items are permanent **PostgreSQL `bigint` identity** rows, shown outside as Base58 strings. They are distinct from the catalogue definition ids.
- A player can read their roster and inventory (reads never write) and make an advancement pick that follows the Unity rule exactly.

**Architecture:**
- **Domain (pure).** `Accounts.Domain` gains:
  - `Base58Id`, the Stage 3 codec generalised to any positive `long` (`AccountId` delegates to it unchanged);
  - `StarterRef`: starter origin references (D5);
  - `ProgressionRules`, ported from `ProgressionTrack` and `PersistentOperativeState`;
  - `InventoryRules.BagEntries`;
  - a pure `StarterPlanner`, which turns the catalogue's starter section into the exact rows to create, keyed by origin reference.
- **Catalogue.** It moves to **format 2**, carrying the authoritative bag capacity exported from a new Unity `InventoryRules` asset (D1).
- **Persistence.** `Accounts.Persistence` gains:
  - migrations `0003_operatives_and_picks` and `0004_inventory`;
  - `StarterProvisioner`, which runs inside a caller's transaction and mints or finds by origin reference (D3);
  - `AccountAdminStore.CreateAccountAsync`, which now provisions atomically (D4), and `BackfillStarterAsync`;
  - `PlayerReadStore` (read only) and `PickStore`.
- **API.** `Accounts.Api` gains `GET /v1/roster`, `GET /v1/inventory`, `POST /v1/operatives/{operativeId}/choices`, `inventoryVersion` on `GET /v1/me`, and an admin CLI command `starter backfill`.
- **Contract vectors.** Shared golden vectors run the same cases through xUnit and Unity EditMode (D2).

**Tech stack:**
- ASP.NET Core minimal APIs on .NET 10, PostgreSQL 17, Npgsql 10, Dapper 2.1, xUnit 2.9 against the real test cluster (`127.0.0.1:55432` through the SSH tunnel).
- Unity 6 EditMode tests (`Tools/run-tests.sh EditMode`), with the Editor closed for batch runs; PlayMode for regression.

**Spec:** `Docs/superpowers/specs/2026-10-14-phase-13-backend-design.md`, revision 2.4 (written in Task 0 from these rulings): §3, §4.6, §4.7, §5, §14.3, §14.4, §15.1–§15.3, §16.1, §18 B3 and §19. Decisions 036, 043 and 048–050, plus the 048 amendment 2 written in Task 0. Stage plan: `Docs/superpowers/plans/2026-10-14-phase-13-backend.md` §Stage 4.

## A. Owner rulings (2026-10-15)

- **D1 (approved): bag capacity.**
  - A small Unity `InventoryRules` ScriptableObject holds the operative bag capacity (8 entries) and is exported through **catalogue format 2**.
  - Existing offline scene behaviour and serialized references are preserved. An EditMode drift test compares the ProceduralMission scene's `SquadInventory.bagCapacity` with the asset.
  - The backend validates the exported rule and has **no hard-coded capacity**.
  - The format 1 → 2 change gives a new `catalogueVersion`, validated independently on both sides.
- **D2 (approved): shared rule vectors.**
  - Golden vectors cover starter grants and advancement picks, run by Unity EditMode and xUnit.
  - Cases include duplicate grants, invalid choices, progression limits, equipment placement, missing definitions and inventory-capacity boundaries.
- **D3 (revised): permanent ids.**
  - **Owned operatives and non-stackable owned item instances get `bigint generated always as identity` ids**, shown externally as Base58 strings (the Stage 3 convention), with `long` internally.
  - Catalogue definition ids are unchanged and never confused with instance ids.
  - Unchanged: mission attempts stay UUIDv7; sessions and credentials keep their UUIDs; consumable stacks stay fungible quantities with no per-stack id; offline Unity id generation stays as it is.
  - Compatibility with Unity's string ids is verified, not refactored.
  - **Retry safety comes from PostgreSQL ids plus a unique origin reference plus atomic transactions; UUIDv5 is not used.** A duplicate starter grant or settlement finds the existing committed item instead of minting another. One source reference never creates two instances, and each unit of a multi-unit line has its own reference.
  - Tests prove retry safety under concurrency and database failure.
  - The spec and decisions record the change from the earlier UUIDv5 proposal.
- **D4 (revised): provisioning.**
  - Starter operatives, inventory, equipment and the starter-granted state are created **atomically during account creation**, by both the operator CLI and `POST /dev/v1/accounts`. A partial failure rolls back the account too.
  - `GET /v1/roster` and `GET /v1/inventory` **never write**.
  - Accounts created before Stage 4 get an **explicit, idempotent backfill** that grants exactly once, with transactional locking and uniqueness constraints.
  - The catalogue version used is recorded. Catalogue changes never re-grant or replace an existing account's starter.
- **D5 (approved, refined): origin references.** A reference names:
  - the grant source;
  - the account;
  - the destination (an operative definition or the stash);
  - the item definition;
  - a stable instance disambiguator where needed.

  It never relies only on mutable array positions. Identical starter items each get their own permanent id. The unique constraints stay, and concurrent retries are tested.
- **D6 (approved):** `GET /v1/me` gains `inventoryVersion` only, next to the existing Base58 `accountId`.

### Ids and references (as ruled)

| Thing | Id | Minted by | Reference (unique) |
|---|---|---|---|
| Account | `bigint` identity, Base58 outside | PostgreSQL | none |
| Operative | `bigint` identity, Base58 outside | PostgreSQL | `operatives (origin, origin_ref)` |
| Non-stackable item | `bigint` identity, Base58 outside | PostgreSQL | `item_instances (origin, origin_ref)` |
| Stack | none (fungible) | n/a | `item_stacks` unique per account, location and definition |
| Attempt, session, credential | UUIDv7 / UUID as today | API | n/a |

**Starter references (D5).** Catalogue ids are well-formed (lowercase letters and digits joined by single `.` or `-`), so `:` never occurs inside a field and the parts can't run into each other.
- Operative: `starter:a<account id>:op:<operative definition id>`
- Item bound for an operative's bag: `starter:a<account id>:op:<operative definition id>:item:<item definition id>:n<k>`
- Item bound for the stash: `starter:a<account id>:stash:item:<item definition id>:n<k>`

`k` counts the units of that item definition for that destination, from 0, across all its grants in authored order. Identical items differ only by `k`. Reordering grants of *different* items changes no reference, and no reference depends on an array index. Stacks get no reference. They are created only inside the provisioning transaction, guarded by `inventories.starter_granted` and the stack uniqueness constraint.

**Minting rule (D3).** Every permanent instance is created by one statement:

```sql
insert into item_instances (...) values (...) on conflict (origin, origin_ref) do nothing returning item_id
```

When nothing is returned, it reads the existing row by `(origin, origin_ref)` and verifies the same account and definition (otherwise it throws). Operatives work the same way. A retry after a commit finds the committed row; a retry after a rollback mints fresh; concurrent mints of one reference converge on one id.

## Global Constraints

- **Migrations:** forward-only, numbered, checksummed, one transaction each. **0000, 0001 and 0002 are applied and immutable.** Stage 4 adds `0003_operatives_and_picks.sql` and `0004_inventory.sql`.
- **Account ids:**
  - Every account foreign key is `bigint`; operative and item foreign keys are `bigint`.
  - All three ids are Base58 JSON strings outside the database, converted only at the API boundary. Accounts keep `AccountIdJsonConverter`; operatives and items use the new `Base58IdJsonConverter` / `NullableBase58IdJsonConverter`.
  - **No client request carries an account id.** The account always comes from the session (`AccountClaims.AccountOf`).
- **Stamping:** rows are stamped with `statement_timestamp()`, never `now()`. No stamping statement may itself wait on a lock: lock first in an earlier statement, then stamp (Stage 3 lesson).
- **App role privileges:**
  - `accounts_app` gets column-level `INSERT`/`UPDATE` only for the columns Stage 4 writes, no `DELETE`, no `TRUNCATE`, and **no privilege on any identity sequence**.
  - `operative_choices` is append-only: no respec.
  - Stage 4 grants no `UPDATE` on `item_instances` or `item_stacks`; Stage 5 adds those.
- **Lock order:** `accounts` row → `inventories` row → operative, choice, instance and stack rows.
  - Account creation creates new rows only.
  - Backfill locks the `accounts` row `FOR UPDATE`, then the `inventories` row.
  - Picks lock only the `inventories` row `FOR UPDATE`.
  - No transaction locks two accounts.
- **Failures:** every store opens connections through `DbFailure.OpenAsync`. Failures map to the Stage 3 classes (503, busy 503 with `Retry-After`, 500), and waits are bounded by the connection-level timeouts.
- **Catalogue ids** (items, operative definition GUIDs, choice ids, `role.*`, `ability.*`) are permanent and stored as text with the catalogue id rule. They are validated against the embedded catalogue in code.
- **Scope:** no new progression features (no respec, no new choices, no XP writer outside test setup), and no inventory editing, energy, insurance, admission, settlement, recruitment, trading, crafting or purchases.
- **Touches:**
  - `Backend/`, `Docs/` and `CLAUDE.md`;
  - Unity: `Assets/_Project/Scripts/Items/InventoryRules.cs` (new) and its asset under `Assets/_Project/Data/Items/`, `Assets/_Project/Editor/Backend/*`, and `Assets/_Project/Tests/EditMode/*`. **No scene, prefab or gameplay change.**
- **Infrastructure:** no live deployment, and no Docker, SSH, UFW or mission-VM change. VM work uses only the throwaway `bg-smoke` and `bg-drill` projects and the existing test cluster.
- **Preserve** the owner's two modified Unity settings files and `.claude/`.
- **Commit trailer:** `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- **Commands** (from `Backend/`): ALL `bash ../.claude/scratchpad/phase13/tools/dt.sh test Backend.sln`; one class `… test tests/Accounts.Integration.Tests --filter "FullyQualifiedName~<Class>"`; UNIT `--filter "Requires!=Postgres"`. Unity: `bash Tools/run-tests.sh EditMode [filter]` from the repo root.

## Review Focus

1. **A cross-account guess with sequential ids.** Account B posts a pick for A's operative id (A's id + 1 or − 1, Base58). Expected: the same 404 body as an id that doesn't exist; nothing written; A's version unchanged. → Task 10 `A_pick_for_a_guessed_neighbouring_operative_is_the_same_404_as_an_unknown_one`.
2. **Concurrent backfill of one account.** Eight parallel backfills grant exactly once, with no 500 and no duplicate rows. One audit row records the grant; the others record "already granted". → Task 7 `Concurrent_backfills_grant_once`.
3. **Two picks racing on one version.** Exactly one succeeds; the other gets 409 `VersionMismatch`; no ordinal is duplicated. → Task 9 `Two_picks_with_the_same_version_one_wins_one_is_409`.
4. **A catalogue that no longer knows a stored id.** Reads return the stored id with `definitionKnown: false` instead of a 500. A pick of a removed choice gets 422 `UnknownChoice`. → Task 8 `A_stored_id_the_catalogue_no_longer_knows_reads_as_unknown_not_500`.
5. **A failure inside account creation.** A provisioning failure part-way leaves no account, credential, identity, audit row, operative, item or stack. Retrying creation then succeeds. → Task 6 `A_provisioning_failure_rolls_back_the_whole_account`.

---

## Task 0 (controller, docs first): record the rulings

- **Files:** `Docs/Decisions.md` (048 amendment 2: owned-instance ids and provisioning, preserving amendment 1 and noting what it supersedes in spec revision 2.3 §4.7), the spec (revision 2.4, §0.4, and §3, §4.6, §4.7, §5.1–§5.3, §15.1 planned 0003/0004, §15.3, §16.1, §16.4, §19), the stage plan's Stage 4 section, `CLAUDE.md` (Stage 3 merged; Stage 4 authorized on `phase13-stage4`; the owned-id policy), and this plan.
- **Commit:** `Phase 13 Stage 4: owner rulings D1-D6 recorded (decision 048 amendment 2, spec revision 2.4, plan revision 2, CLAUDE.md)`.

### Task 1: `Base58Id` and starter references (pure)

**Files:**
- Create `src/Accounts.Domain/Ids/Base58Id.cs`, `Ids/Base58IdJsonConverter.cs` (both converters) and `Ids/StarterRef.cs`.
- Modify `Accounts/AccountId.cs`: delegate to `Base58Id` and keep its public API and behaviour.
- Tests: `tests/Accounts.Domain.Tests/Ids/Base58IdTests.cs`, `Base58IdJsonConverterTests.cs`, `StarterRefTests.cs`. All existing `AccountIdTests` stay green, unchanged.

**Interfaces:**
- Produces:
  - `Base58Id.Format(long)`, `Base58Id.TryParse(string?, out long)` and `Base58Id.MaxLength`, with exactly the Stage 3 `AccountId` rules (positive only, canonical, case-sensitive, at most 11 characters, no leading `1`, overflow-safe);
  - `Base58IdJsonConverter : JsonConverter<long>` and `NullableBase58IdJsonConverter : JsonConverter<long?>`, which writes `null` for null;
  - `StarterRef.Operative(long accountId, string operativeDefinitionId)`;
  - `StarterRef.OperativeItem(long accountId, string operativeDefinitionId, string itemDefinitionId, int k)`;
  - `StarterRef.StashItem(long accountId, string itemDefinitionId, int k)`.
- `StarterRef` validates: account > 0; every id well formed per `CanonicalCatalogueJson.IsWellFormedId`; k ≥ 0. Anything else throws `ArgumentException` or `ArgumentOutOfRangeException`.

- [ ] **Step 1: Write the failing tests.**
  - **`Base58IdTests`:**
    - `Matches_AccountId_for_every_vector`: reuse the Stage 3 vectors (`123456789` ↔ `BukQL`, `long.MaxValue` ↔ `NQm6nKp8qFC`, 1 ↔ `2`);
    - `Refuses_what_AccountId_refuses`: the leading `1`, `0`, the characters `0OIl`, whitespace, more than 11 characters, overflow `NQm6nKp8qFD`, empty and null.
  - **`Base58IdJsonConverterTests`:**
    - `Writes_a_string_and_reads_only_a_canonical_string`: a number or a non-canonical string → `JsonException`;
    - `Nullable_writes_null_and_reads_null`.
  - **`StarterRefTests`:**
    - `Operative_ref` = `starter:a123456789:op:2c84b7e9-51a6-4d03-9f7e-c3a0165d8b03`;
    - `Operative_item_ref` = `starter:a1:op:2c84b7e9-51a6-4d03-9f7e-c3a0165d8b03:item:item.medkit:n0`;
    - `Stash_item_ref` = `starter:a1:stash:item:item.service-rifle:n2`;
    - `Account_must_be_positive`;
    - `Ids_must_be_well_formed`: `Item.Medkit`, `a:b`, empty and a 65-character id all throw;
    - `K_must_not_be_negative`.
- [ ] **Step 2: Run them.** `… test tests/Accounts.Domain.Tests --filter "FullyQualifiedName~Base58Id|FullyQualifiedName~StarterRef"`. Expected: a compile failure.
- [ ] **Step 3: Implement.**
  - Move the codec body from `AccountId` into `Base58Id`. `AccountId.Format`/`TryParse`/`Alphabet`/`MaxLength` forward to it.
  - `AccountIdJsonConverter` stays as it is. It may delegate, but its error behaviour must not change.
  - `StarterRef` uses invariant formatting.
- [ ] **Step 4: Run** Domain tests (all). Expected: PASS, with `AccountIdTests` untouched and green.
- [ ] **Step 5: Commit.** `Phase 13 Stage 4: Base58Id (the Stage 3 codec for any owned id) and starter origin references`.

### Task 2: Progression rules and the progression vector (backend side)

**Files:** create `src/Accounts.Domain/Progression/ProgressionRules.cs` and `Backend/contracts/progression/vectors.json`. Test `tests/Accounts.Domain.Tests/Progression/ProgressionRulesTests.cs` and `ProgressionVectorTests.cs`; the test csproj copies `..\..\contracts\**\*.json` to the output as `contracts\…`.

**Interfaces:**
- Produces:
  - `enum PickRefusal { UnknownChoice, NoPendingPick }`;
  - `static int ProgressionRules.RankFor(Progression track, int xp)`;
  - `static int ProgressionRules.PendingPicks(Progression track, int xp, int picksMade)`;
  - `static PickRefusal? ProgressionRules.CheckPick(Progression track, int xp, int picksMade, string? choiceId)`.
- The rules port Unity exactly:
  - **Rank** loops `i = 1..n−1` and sets `rank = i + 1` while `xp >= thresholds[i]`; negative XP is rank 1.
  - **Pending** is `max(0, rank − 1 − picksMade)`.
  - **Pick** reports an unknown choice (null, empty or not in `ChoiceIds`, compared ordinally) **before** no pending pick (`TryPickChoice`'s order). The same choice may be picked again.

**Vector format** (`vectors.json`):

```json
{ "track": { "xpThresholds": [0,100,250,450,700], "choiceIds": ["ability","combat","mobility","survivability"] },
  "cases": [ { "name": "rank-1-at-zero", "xp": 0, "picksMade": 0, "choiceId": "combat", "rank": 1, "pending": 0, "outcome": "NoPendingPick" } ] }
```

Cases (D2), at least:
- **XP:** −1, 0, 99, 100, 249, 250, 449, 450, 699, 700, 701, `2147483647`;
- **picks made:** 0, 1, 3, 4 and 6, the last more than ranks allow (pending stays 0, and the pick is `NoPendingPick`);
- **choices:** known (`ok`), unknown (`blink`), `""`, `null`, wrong case (`Combat`), and the same choice again (`ok`);
- an unknown choice with no pending pick (`UnknownChoice`);
- **limits:** the max rank at exactly 700 and above 700.

The `outcome` value is `ok`, `UnknownChoice` or `NoPendingPick`.

- [ ] **Step 1: Write** the vector file (by hand, from the Unity code quoted above) and the failing tests.
  - `ProgressionVectorTests.Every_case_matches`: one assertion message per case name.
  - `ProgressionRulesTests.Rank_never_exceeds_the_track`.
  - `Pending_is_never_negative`.
  - `Unknown_choice_is_reported_before_no_pending_pick`.
  - `A_single_rank_track_never_offers_a_pick`, with thresholds `[0]`.
- [ ] **Step 2: Run them.** Expected: a compile failure.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run them.** Expected: PASS.
- [ ] **Step 5: Commit.** `Phase 13 Stage 4: progression rules ported from PersistentOperativeState and ProgressionTrack, with the shared vector`.

### Task 3: Catalogue format 2: `InventoryRules` and the starter-fit rule (Unity and backend)

**Files:**
- **Unity:**
  - create `Assets/_Project/Scripts/Items/InventoryRules.cs` (`[CreateAssetMenu(menuName = "Blackglass/Inventory Rules")]`, `[SerializeField, Min(1)] int bagCapacity = 8`, `public int BagCapacity`, an `internal static InventoryRules Create(int bagCapacity)` for tests) and `Assets/_Project/Data/Items/InventoryRules.asset` (bag capacity 8, created by an idempotent Editor builder step or a menu item in `InventoryDataBuilder`; never by hand-editing YAML);
  - modify `Assets/_Project/Editor/Backend/CatalogueSources.cs` (`InventoryRulesPath`, `InventoryRules` field), `CatalogueBuilder.cs` (exports `inventory.bagCapacity`, refuses a missing asset or a capacity outside 1–64, refuses a starter whose bag entries exceed it), Unity's `CatalogueDocument.cs` and `CanonicalCatalogueWriter.cs` (`formatVersion: 2`, member `inventory` after `formatVersion`).
- **Backend:** `Catalogue/CatalogueDocument.cs` (`SupportedFormatVersion = 2`, `record InventorySettings(int BagCapacity)`, `CatalogueDocument.Inventory`), `CatalogueReader.cs`, `CatalogueValidator.cs`, `CanonicalCatalogueJson.cs`, and the new `src/Accounts.Domain/Inventory/InventoryRules.cs`.
- **Artifacts:** re-export `Backend/catalogue/catalogue.json`; regenerate `Backend/contracts/catalogue/golden-catalogue.json` and `.version`.
- **Tests:** backend `tests/Accounts.Domain.Tests/Catalogue/*`; Unity `Assets/_Project/Tests/EditMode/CatalogueBuilderTests.cs`, `CatalogueExportTests.cs` and a new `InventoryRulesSceneTests.cs`.

**Interfaces:**
- Produces:
  - `GameCatalogue.Document.Inventory.BagCapacity`;
  - `static int InventoryRules.BagEntries(IEnumerable<(CatalogueItem Item, int Quantity)> contents)`: the entry count `ItemInventory.Add` would produce when filling open stacks first. That is the non-stackable units plus, per stackable definition, `ceil(total quantity / maxStack)`.
- Validator rules:
  - for each starter operative, `BagEntries(grants) ≤ bagCapacity`, with the error `incompatible: starter.operatives[i]: N bag entries exceed the bag capacity C`;
  - `inventory.bagCapacity` in 1..64;
  - format 1 refused with `incompatible: format version 1 is not supported (this backend reads 2); re-export the catalogue from Unity`.

- [ ] **Step 1: Write the failing backend tests.**
  - `Format_2_round_trips_canonically`.
  - `Format_1_is_refused_with_a_reexport_message`.
  - `Bag_capacity_outside_1_to_64_is_refused`: 0, 65, and a missing member.
  - `A_starter_that_does_not_fit_the_bag_is_refused`: capacity 2 with 3 rifles.
  - `A_starter_that_exactly_fits_is_accepted`: 8 entries at capacity 8.
  - `Grants_of_one_stackable_definition_merge_like_ItemInventory_Add`: 2 + 2 medkits with max stack 3 is 2 entries.
  - `The_committed_catalogue_is_format_2_with_bag_capacity_8`.
  - The golden test, with the new version.
- [ ] **Step 2: Write the failing Unity tests.**
  - `CatalogueBuilderTests.Bag_capacity_is_exported_from_InventoryRules`.
  - `A_missing_InventoryRules_asset_refuses_the_export`.
  - `A_starter_that_does_not_fit_refuses_the_export`.
  - `InventoryRulesSceneTests.The_ProceduralMission_scene_bag_capacity_equals_InventoryRules`: `EditorSceneManager.OpenScene(path, OpenSceneMode.Additive)`, read `SquadInventory`'s serialized `bagCapacity` through `SerializedObject`, close without saving, and leave no change in git.
  - The existing `CatalogueExportTests` fails until the re-export.
- [ ] **Step 3: Run both.** Expected: FAIL.
- [ ] **Step 4: Implement both sides.** Create the asset through the builder. With the Editor closed, run `bash Tools/run-method.sh Blackglass.BackendTools.CatalogueExporter.ExportFromCommandLine`, then copy the export to the golden vector.
- [ ] **Step 5: Validate the new hash independently on both sides (D1).**
  - `sha256sum Backend/catalogue/catalogue.json`.
  - The backend `GameCatalogue.Embedded.Version`, through a test.
  - Unity's `CanonicalCatalogueWriter.VersionOf`, from the export log line.
  - All three must be equal. Record the value in the commit message and the report.
- [ ] **Step 6: Run backend ALL and Unity EditMode.** Expected: green. `git status` shows no scene or prefab change.
- [ ] **Step 7: Commit.** `Phase 13 Stage 4: catalogue format 2 carries the authoritative bag capacity (InventoryRules); the starter must fit; new catalogueVersion <sha256:…>`.

### Task 4: The starter planner (pure) and the starter vector (backend side)

**Files:** create `src/Accounts.Domain/Inventory/StarterPlanner.cs` and `Backend/contracts/starter/vectors.json`. Test `tests/Accounts.Domain.Tests/Inventory/StarterPlannerTests.cs` and `StarterVectorTests.cs`.

**Interfaces:**
- Consumes `GameCatalogue`, `StarterRef` and `InventoryRules.BagEntries`.
- Produces:

```csharp
public sealed record PlannedOperative(string DefinitionId, string OriginRef);
public sealed record PlannedInstance(string DefinitionId, string? OperativeDefinitionId, string? EquippedSlot, string OriginRef); // null operative = stash; slot "weapon"|"armor"|"utility"
public sealed record PlannedStack(string? OperativeDefinitionId, string DefinitionId, int Quantity);
public sealed record StarterPlan(IReadOnlyList<PlannedOperative> Operatives, IReadOnlyList<PlannedInstance> Instances, IReadOnlyList<PlannedStack> Stacks);
public static class StarterPlanner { public static StarterPlan For(long accountId, CatalogueDocument catalogue); }
```

**Rules**, mirroring `InventorySession.SeedStarter` and `ItemInventory.Add`:
- **Operatives.** One per starter operative, in catalogue order.
- **Non-stackable grants.** Each unit becomes one instance, with `k` counting units of that item definition for that destination (D5).
- **Equipping.** `equip` sets the slot on the instance that is the **first entry of that definition in the bag**: the lowest `k`.
- **Stackable grants.** They sum per (destination, definition) into one stack.
- **Stash.** Instances and stacks with a null operative.
- The planner assumes a validated catalogue. It throws `InvalidOperationException` on anything the validator refuses (an equip on a stackable or slotless item, an unknown item).

**Starter vector** (`vectors.json`, D2). Each case is a small self-contained catalogue fragment: items with id, category, slot, stackable, maxStack; bag capacity; starter operatives with grants; stash. The expected outcome is either `{"refused": "<class>"}` or a result per operative definition:
- `bag`: entries as `[definitionId, quantity]` in `ItemInventory.Add` order, split by `maxStack`;
- `equipped: {weapon, armor, utility}`: definition id or null;
- the stash entries.

No instance ids appear, so the same vector checks Unity. Required cases:

| Case | Expected |
|---|---|
| single equipped rifle plus 2 medkits | ok |
| **duplicate grants** of one non-stackable (2 rifles, equip true): two entries, the first equipped | ok |
| duplicate grants of one stackable across two grant lines (2 + 2 medkits, max 3): entries `[medkit,3],[medkit,1]` | ok |
| **equipment placement:** one rifle, one vest, one boots, all equipped | three slots set |
| second equip in an occupied slot (two different weapons, both equip) | `refused: second-equip-in-slot` |
| equip on a consumable | `refused: equip-without-slot` |
| **missing definition:** a grant naming an item not in the items list | `refused: unknown-item` |
| **capacity boundary:** exactly at capacity | ok |
| capacity + 1 entry | `refused: bag-capacity` |
| stash with a non-stackable and a stackable | ok |

- [ ] **Step 1: Write** the vector (by hand, from the Unity rules) and the failing tests.
  - `StarterVectorTests.Every_case_matches`: it builds a `CatalogueDocument` from the fragment, runs `CatalogueValidator` (refusal class from the error prefix mapping in the test) and then `StarterPlanner`.
  - `StarterPlannerTests.References_are_unique_and_follow_StarterRef`.
  - `Identical_items_get_distinct_references`.
  - `Reordering_grants_of_different_items_keeps_every_reference`.
  - `Different_accounts_get_different_references`.
  - `The_committed_catalogue_plans_without_error`.
- [ ] **Step 2: Run them.** Expected: a compile failure.
- [ ] **Step 3: Implement.** Map validator errors to the vector's refusal classes in one test helper. Add any validator rule the vector needs that the Stage 2 validator lacks, with its own test; `second-equip-in-slot` and `equip-without-slot` already exist.
- [ ] **Step 4: Run them.** Expected: PASS.
- [ ] **Step 5: Commit.** `Phase 13 Stage 4: the starter planner (Unity's equip and stacking rules, D5 references) with the shared vector`.

### Task 5: Migrations 0003 and 0004, privileges and the invariant

**Files:**
- Create `Backend/db/migrations/0003_operatives_and_picks.sql`, `Backend/db/migrations/0004_inventory.sql` and `Backend/db/invariants/0004_inventory_consistency.sql`.
- Test `tests/Accounts.Integration.Tests/Database/PlayerSchemaTests.cs`. `AccountsSchemaTests.Every_account_id_column_is_bigint_and_other_ids_stay_uuid` is extended: operative and item ids are bigint, and attempts are not affected.

```sql
-- 0003 operatives and advancement picks (Stage 4; decision 048 amendment 2; spec §5.3, §15.1).
-- Operative ids are bigint identities minted only by PostgreSQL (Base58 outside). definition_id is the catalogue's
-- permanent operative GUID (validated in code). (origin, origin_ref) makes every grant mint at most one operative.
create table operatives (
  operative_id bigint generated always as identity primary key check (operative_id > 0),
  account_id bigint not null references accounts,
  definition_id text not null check (length(definition_id) <= 64 and definition_id ~ '^[a-z0-9]+([.-][a-z0-9]+)*$'),
  experience integer not null default 0 check (experience >= 0),
  origin text not null check (origin in ('starter')),
  origin_ref text not null check (length(origin_ref) <= 300 and origin_ref !~ '[[:space:][:cntrl:]]'),
  created_at timestamptz not null default statement_timestamp(),
  unique (account_id, operative_id),
  unique (origin, origin_ref));
create table operative_choices (                        -- append-only: no respec
  operative_id bigint not null references operatives,
  ordinal smallint not null check (ordinal >= 0),
  choice_id text not null check (length(choice_id) <= 64 and choice_id ~ '^[a-z0-9]+([.-][a-z0-9]+)*$'),
  picked_at timestamptz not null default statement_timestamp(),
  primary key (operative_id, ordinal));
revoke insert, update, delete on operatives from accounts_app;
grant insert (account_id, definition_id, origin, origin_ref) on operatives to accounts_app;
revoke insert, update, delete on operative_choices from accounts_app;
grant insert (operative_id, ordinal, choice_id) on operative_choices to accounts_app;
revoke all on sequence operatives_operative_id_seq from accounts_app;
```

```sql
-- 0004 inventory (Stage 4; spec §5). One inventories row per account: the lock anchor and the version shared by picks
-- and Stage 5 edits; it records which catalogue's starter the account received (never re-granted). Non-stackables are
-- bigint-identity instances; stackables are fungible stacks per location (operative_id null = the stash).
create table inventories (
  account_id bigint primary key references accounts,
  version bigint not null default 1 check (version >= 1),
  starter_granted boolean not null default false,
  starter_catalogue_version text check (starter_catalogue_version ~ '^sha256:[0-9a-f]{64}$'),
  starter_granted_at timestamptz,
  check ((starter_granted and starter_catalogue_version is not null and starter_granted_at is not null)
      or (not starter_granted and starter_catalogue_version is null and starter_granted_at is null)));
create table item_instances (
  item_id bigint generated always as identity primary key check (item_id > 0),
  account_id bigint not null references accounts,
  definition_id text not null check (length(definition_id) <= 64 and definition_id ~ '^[a-z0-9]+([.-][a-z0-9]+)*$'),
  operative_id bigint,
  equipped_slot text check (equipped_slot in ('weapon', 'armor', 'utility')),
  origin text not null check (origin in ('starter', 'settlement', 'insurance', 'dev_grant')),
  origin_ref text not null check (length(origin_ref) <= 300 and origin_ref !~ '[[:space:][:cntrl:]]'),
  created_at timestamptz not null default statement_timestamp(),
  foreign key (account_id, operative_id) references operatives (account_id, operative_id),
  check (equipped_slot is null or operative_id is not null),
  unique (origin, origin_ref));
create unique index item_one_per_slot on item_instances (operative_id, equipped_slot) where equipped_slot is not null;
create index item_instances_account on item_instances (account_id);
create table item_stacks (
  account_id bigint not null references accounts,
  operative_id bigint,
  definition_id text not null check (length(definition_id) <= 64 and definition_id ~ '^[a-z0-9]+([.-][a-z0-9]+)*$'),
  quantity integer not null check (quantity > 0),
  foreign key (account_id, operative_id) references operatives (account_id, operative_id),
  unique nulls not distinct (account_id, operative_id, definition_id));
-- The starter backfill is an operator action (0001's list is extended here, forward-only).
alter table operator_actions drop constraint operator_actions_action_check;
alter table operator_actions add constraint operator_actions_action_check check (action in (
  'account_create', 'credential_rotate', 'credential_revoke', 'sessions_revoke', 'account_suspend', 'account_reactivate',
  'starter_backfill',
  'revalidate', 'participant_void', 'host_declared_lost', 'host_drain', 'host_enable', 'build_disable', 'energy_adjust'));
revoke insert, update, delete on inventories from accounts_app;
grant insert (account_id), update (version, starter_granted, starter_catalogue_version, starter_granted_at) on inventories to accounts_app;
revoke insert, update, delete on item_instances from accounts_app;
grant insert (account_id, definition_id, operative_id, equipped_slot, origin, origin_ref) on item_instances to accounts_app;
revoke insert, update, delete on item_stacks from accounts_app;
grant insert (account_id, operative_id, definition_id, quantity) on item_stacks to accounts_app;
revoke all on sequence item_instances_item_id_seq from accounts_app;
```

The implementer must confirm the real name of 0001's action check constraint, `operator_actions_action_check`, from `pg_constraint` in a test before relying on it. If the name differs, use the real one; the migration stays forward-only.

**"Equipped items belong to the corresponding operative's bag":**
- `check (equipped_slot is null or operative_id is not null)` makes an equipped item sit in a bag, and the composite FK makes that bag belong to the same account.
- The slot matching the item definition's slot is a code rule (the database doesn't know the catalogue). It is asserted by the planner tests and by the invariant query below, through a test-only list.

**`0004_inventory_consistency.sql`** is one query counting violations:
- an operative, instance or stack whose account has no `inventories` row with `starter_granted = true`;
- an operative whose choices' ordinals aren't exactly `0..n−1`.

- [ ] **Step 1: Write the failing schema tests**, as `accounts_app` unless stated, following `AccountsSchemaTests`:
  - **Ids.**
    - `PostgreSQL_mints_sequential_bigint_operative_and_item_ids`.
    - `The_app_cannot_choose_an_operative_or_item_id`: `overriding system value` → 42501 or 428C9; and the sequence → 42501.
  - **Composite ownership.**
    - `An_item_cannot_be_placed_with_another_accounts_operative`: 23503.
    - `A_stack_cannot_be_placed_with_another_accounts_operative`: 23503.
  - **Slots, stacks and minting.**
    - `One_item_per_slot_per_operative`: 23505.
    - `An_equipped_item_must_be_in_a_bag`: 23514.
    - `Stacks_are_unique_per_location_and_definition_including_the_stash`: 23505.
    - `Stack_quantity_must_be_positive`: 23514.
    - `An_origin_ref_mints_at_most_one_instance_or_operative`: 23505.
  - **Inventory row.**
    - `The_starter_granted_state_is_all_or_nothing`: 23514 for a granted row with no version or time, and the reverse.
    - `Experience_cannot_be_negative`.
    - `Stored_catalogue_ids_follow_the_catalogue_id_rule`: 23514.
  - **Privileges.**
    - `The_app_holds_exactly_the_stage_4_privileges`: a table and column privilege matrix; no `DELETE` or `TRUNCATE` anywhere; no `UPDATE` on `operatives`, `operative_choices`, `item_instances` or `item_stacks`; no sequence privilege.
    - `Choices_are_append_only`: 42501 for update and delete.
  - **Audit.** `The_starter_backfill_audit_action_is_accepted`, plus all of 0001's actions.
  - **Stamps.** `Rows_are_stamped_with_statement_timestamp`.
  - **Invariant.** `The_inventory_invariant_holds_and_catches_an_ungranted_owner_and_an_ordinal_gap`.
  - **Upgrade.** `Migration_0004_upgrades_a_database_with_stage_3_data`: migrate to 2, create an account through the Stage 3 SQL path, then migrate to latest. The account has no inventory row (it will need the backfill), and the invariant reports no violation, because an account with no rows in the new tables has no owned rows.
- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Write** the migrations and the invariant.
- [ ] **Step 4: Run** the class, then ALL. Expected: PASS. Tests deriving the version from the catalog need no edit.
- [ ] **Step 5: Commit.** `Phase 13 Stage 4: migrations 0003 (operatives, picks) and 0004 (inventory) with bigint identities, origin references, composite ownership and least privilege`.

**Review checkpoint 1 (independent, most capable model):** identity, schema and migrations, over Tasks 1–5 and the Unity export. Fix Critical and Important findings before Task 6.

### Task 6: `StarterProvisioner` and atomic provisioning in account creation (CLI and route)

**Files:**
- Create `src/Accounts.Persistence/Player/StarterProvisioner.cs`.
- Modify `Accounts/AccountAdminStore.cs`: constructor `AccountAdminStore(NpgsqlDataSource db, GameCatalogue? catalogue = null)`, defaulting to `GameCatalogue.Embedded`; `CreateAccountAsync` provisions in its own transaction.
- Modify `Accounts.Api/ServeCommand.cs` (pass the loaded catalogue) and `Admin/AdminCommand.cs` (load `GameCatalogue.Embedded` before touching the database; an invalid catalogue is exit 4 `InvalidCatalogue`, as for `serve`).
- Test `tests/Accounts.Integration.Tests/Database/ProvisioningTests.cs`; extend `AccountAdminStoreTests` and `AdminTests`.

**Interfaces:**
- Produces:
  - `sealed record ProvisionResult(long InventoryVersion, int Operatives, int Instances, int Stacks, string CatalogueVersion)`;
  - `static Task<ProvisionResult> StarterProvisioner.ProvisionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long accountId, GameCatalogue catalogue, CancellationToken ct)`. It never opens a transaction itself.
  - `static Task<long> StarterProvisioner.MintOperativeAsync(...)` and `MintItemAsync(...)`, the minting rule in §A. A found row whose account or definition differs throws `InvalidOperationException("origin reference collision")`.

**Algorithm:**
1. Precondition: the caller holds the account row (just inserted, or locked `FOR UPDATE` by the backfill).
2. `insert into inventories (account_id) values (@a) on conflict do nothing`, then `select starter_granted from inventories where account_id = @a for update`.
3. If already granted, return the current state. The caller decides whether that counts as success.
4. Run `StarterPlanner.For(accountId, catalogue.Document)`. Mint each operative and remember definition → operative id. Mint each instance (operative definition → id, slot as planned). Insert the stacks with a plain `insert` (the uniqueness constraint backs it up).
5. `update inventories set starter_granted = true, starter_catalogue_version = @v, starter_granted_at = statement_timestamp()`.

`CreateAccountAsync` calls this after the credential and before the audit. The audit's `result` gains `starter: {catalogueVersion, operatives, instances, stacks}`. Any exception rolls back the whole transaction (D4).

- [ ] **Step 1: Write the failing tests.**
  - `Creating_an_account_provisions_its_starter_atomically`: one transaction; rows equal `StarterPlanner.For`; `starter_granted` and the catalogue version are set; the audit `result.starter` is present.
  - `Both_the_cli_and_the_admin_route_provision`: one account each way; same row shape.
  - `A_provisioning_failure_rolls_back_the_whole_account` (Review Focus 5): a superuser trigger on `item_stacks` raises. Afterwards there are zero accounts, credentials, identities, audit rows, operatives, instances and stacks, and a retry after dropping the trigger succeeds.
  - `Minting_the_same_reference_twice_returns_the_same_id` (in one transaction, and in two sequential transactions).
  - `Concurrent_mints_of_one_reference_converge_on_one_id`: 8 parallel transactions, each minting the same reference and committing; one row, one id, no exception.
  - `A_mint_after_a_rolled_back_mint_creates_one_row`.
  - `A_reference_held_by_another_account_is_a_collision_not_a_find`.
  - `Identical_starter_items_get_distinct_ids`.
  - `Stage_3_account_tests_still_pass`: the existing suites stay green; any expectation about audit `result` JSON or row counts is updated with a ledgered ruling.
- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the class, then ALL. Expected: green.
- [ ] **Step 5: Commit.** `Phase 13 Stage 4: account creation provisions the starter atomically (CLI and admin route); mint-or-find by origin reference`.

### Task 7: The explicit starter backfill (store and CLI)

**Files:**
- Modify `Accounts/AccountAdminStore.cs`: add `Task<BackfillResult> BackfillStarterAsync(long accountId, OperatorContext op, CancellationToken ct)`, with `record BackfillResult(bool Granted, ProvisionResult State)`.
- Modify `Admin/AdminArguments.cs`: the verb `starter backfill --account-base58 <id>` with `--operator`, `--reason` and `--yes`. It follows the Stage 3 confirmation rule: target preview with `target.starterGranted=true|false`; a terminal must type the id or pass `--yes`.
- Modify `Admin/AdminCommand.cs` (prints `starterGranted=` `granted|already`, the counts and `correlationId`) and `AccountAdminStore.DescribeAccountAsync` (adds `StarterGranted`).
- Tests: `tests/Accounts.Integration.Tests/Database/BackfillTests.cs` and `Unit/AdminArgumentsTests.cs`.

**Algorithm:**
1. Begin the transaction.
2. `LockAccountAsync` (`FOR UPDATE`; unknown → `NotFound`).
3. `StarterProvisioner.ProvisionAsync`.
4. Audit `starter_backfill` with `result {granted: true|false, catalogueVersion, counts}`. The audit is written whether or not anything was granted.
5. Commit.

Suspended accounts are backfilled too (the starter is property, not access), and the preview shows the status.

- [ ] **Step 1: Write the failing tests.**
  - `Backfill_grants_a_pre_stage_4_account_exactly_once`. The account is created through the Stage 3 SQL path: migrate to version 2, insert, then migrate to latest. A second call grants nothing and is audited as already granted.
  - `Concurrent_backfills_grant_once` (Review Focus 2): 8 parallel backfills; one set of rows; one audit with `granted: true` and seven with `false`.
  - `Backfill_of_an_account_created_in_stage_4_grants_nothing`.
  - `Backfill_failure_rolls_back_and_a_retry_grants_once`: trigger fault injection.
  - `Backfill_records_the_catalogue_version_and_never_regrants_after_a_catalogue_change`. A second catalogue with a different starter (built in the test) passed to a second store grants nothing, and the recorded version stays the first one.
  - `The_cli_previews_and_needs_confirmation`: no `--yes` and no terminal → 64; with `--yes` → granted.
  - `An_unknown_account_is_65`.
  - `Backfill_waits_are_bounded`: a held account lock → busy, exit 75.
- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the classes, then ALL.
- [ ] **Step 5: Commit.** `Phase 13 Stage 4: explicit, audited, idempotent starter backfill for accounts created before Stage 4`.

### Task 8: `PlayerReadStore`: read-only roster and inventory

**Files:** create `src/Accounts.Persistence/Player/PlayerReadStore.cs` and `Player/PlayerException.cs` (`enum PlayerError { NotProvisioned, OperativeNotFound, VersionMismatch, NoPendingPick, UnknownChoice }`). Test `tests/Accounts.Integration.Tests/Database/PlayerReadStoreTests.cs`.

**Interfaces:**
- Produces:
  - `Task<RosterView> GetRosterAsync(long accountId, CancellationToken ct)`;
  - `Task<InventoryView> GetInventoryAsync(long accountId, CancellationToken ct)`;
  - `Task<long?> GetInventoryVersionAsync(long accountId, CancellationToken ct)`: null when there is no `inventories` row with `starter_granted`.
- Both reads run in one `repeatable read, read only` transaction. A missing or ungranted `inventories` row throws `PlayerException(NotProvisioned)`.
- **No statement writes.** A test asserts this through `pg_stat_xact` counters, or through a role with only `SELECT`.

```csharp
public sealed record OperativeView(long OperativeId, string DefinitionId, bool DefinitionKnown, int Experience, int Rank, int PendingPicks, IReadOnlyList<string> Choices);
public sealed record RosterView(long Version, IReadOnlyList<OperativeView> Operatives);        // ordered by operative_id
public sealed record InstanceView(long ItemId, string DefinitionId, bool DefinitionKnown);
public sealed record StackView(string DefinitionId, bool DefinitionKnown, int Quantity);
public sealed record LocationView(IReadOnlyList<InstanceView> Instances, IReadOnlyList<StackView> Stacks);
public sealed record EquipmentView(long? Weapon, long? Armor, long? Utility);
public sealed record BagView(long OperativeId, LocationView Bag, EquipmentView Equipment);
public sealed record InventoryView(long Version, bool Locked, LocationView Stash, IReadOnlyList<BagView> Operatives);  // Locked is false until participations exist (Stage 6+)
```

- [ ] **Step 1: Write the failing tests.**
  - `A_new_account_reads_its_starter`.
  - `Reads_write_nothing`: run as a role with `SELECT` only on the player tables.
  - `An_unprovisioned_account_is_NotProvisioned`.
  - `Reads_never_show_another_accounts_rows`.
  - `Rank_and_pending_picks_come_from_xp_and_the_catalogue_track`: XP set by superuser, using the vector's values.
  - `A_stored_id_the_catalogue_no_longer_knows_reads_as_unknown_not_500` (Review Focus 4).
  - `Equipment_lists_the_equipped_item_ids_per_slot`.
  - `Version_and_rows_come_from_one_snapshot`.
- [ ] **Step 2: Run** (expected FAIL), **implement**, **run** (expected PASS), then commit `Phase 13 Stage 4: read-only roster and inventory, scoped to the account`.

### Task 9: `PickStore`: advancement picks under the inventory lock and version

**Files:** create `src/Accounts.Persistence/Player/PickStore.cs`; test `tests/Accounts.Integration.Tests/Database/PickStoreTests.cs`.

**Interfaces:**
- Produces `Task<(long Version, OperativeView Operative)> PickAsync(long accountId, long operativeId, string choiceId, long expectedVersion, CancellationToken ct)`.

**Algorithm**, in one transaction:
1. `select version, starter_granted from inventories where account_id = @a for update`. A missing or ungranted row → `NotProvisioned`.
2. A version other than the expected one → `VersionMismatch`.
3. `select experience, (select count(*) from operative_choices c where c.operative_id = o.operative_id) from operatives o where o.operative_id = @op and o.account_id = @a`. No row → `OperativeNotFound`, the same error whether unknown or another account's.
4. `ProgressionRules.CheckPick` with the catalogue track; a refusal maps to its error.
5. Insert the choice at `ordinal = count`.
6. `update inventories set version = version + 1`.
7. Commit, then return the new version and the operative view.

`InventoryLocked` is deferred until participations exist (spec §5.3), with a comment.

- [ ] **Step 1: Write the failing tests.**
  - `A_pick_needs_a_pending_pick`.
  - `A_pick_needs_a_known_choice`: including wrong case and a choice removed from the catalogue.
  - `Unknown_choice_is_reported_before_no_pending_pick`.
  - `A_pick_takes_the_next_ordinal_and_bumps_the_version`.
  - `The_same_choice_may_be_picked_again`.
  - `Picks_stop_at_the_number_of_ranks_gained`.
  - `A_stale_version_is_VersionMismatch_and_writes_nothing`.
  - `Two_picks_with_the_same_version_one_wins_one_is_409` (Review Focus 3): deterministic, using the held-lock pattern.
  - `Another_accounts_operative_is_OperativeNotFound_and_writes_nothing`.
  - `An_unprovisioned_account_is_NotProvisioned`.
  - `A_pick_behind_a_held_inventory_lock_times_out_and_writes_nothing`.
- [ ] **Step 2: Run** (expected FAIL), **implement**, **run** (expected PASS), then commit `Phase 13 Stage 4: advancement picks under the inventory lock and version (the PersistentOperativeState rule)`.

### Task 10: Endpoints and `GET /v1/me` `inventoryVersion`

**Files:**
- Create `src/Accounts.Api/Player/PlayerEndpoints.cs` and `Player/PlayerDtos.cs`.
- Modify `ServeCommand.cs` (register the stores with the loaded `GameCatalogue`, then `MapPlayer()`) and `Auth/AuthEndpoints.cs` (`MeResponse(AccountId, InventoryVersion)` with `long? InventoryVersion`; null only for an account that still needs the backfill).
- Tests: `Database/PlayerApiTests.cs`, `Database/PlayerAuthorizationTests.cs`, and extended `Unit/DtoAuditTests.cs` and `Database/LogSecrecyTests.cs`.

**Contract:**
- **Ids:** every operative and item id is a Base58 string; equipment slots are a Base58 string or `null`.
- **`GET /v1/roster`** → `{"version":1,"operatives":[{"operativeId":"2","definitionId":"2c84…","definitionKnown":true,"experience":0,"rank":1,"pendingPicks":0,"choices":[]}]}`.
- **`GET /v1/inventory`** → `{"version":1,"locked":false,"stash":{"instances":[{"itemId","definitionId","definitionKnown"}],"stacks":[{"definitionId","definitionKnown","quantity"}]},"operatives":[{"operativeId","bag":{…},"equipment":{"weapon","armor","utility"}}]}`.
- **`POST /v1/operatives/{operativeId}/choices`**, body `{"choiceId":"combat","expectedVersion":1}` (at most 4 KiB; unknown members refused) → 200 `{"version":2,"operative":{…}}`.
  - 400 `InvalidBody` / `InvalidOperativeId`: malformed JSON, a missing member, or a non-canonical Base58 id.
  - 404 `OperativeNotFound`.
  - 409 `VersionMismatch` / `NotProvisioned`.
  - 422 `NoPendingPick` / `UnknownChoice`.
- **`GET /v1/roster`** and **`GET /v1/inventory`** on an unprovisioned account → 409 `NotProvisioned` (they never write).
- **`GET /v1/me`** → `{"accountId":"<Base58>","inventoryVersion":1}`.
- **Every route:** `RequireAuthorization()`, `RequireRateLimiting(RateLimits.PerAccount)`, `Cache-Control: no-store`, the uniform 401, `DbFailure` classes, and the account from the session only.

- [ ] **Step 1: Write the failing tests.**
  - **`PlayerApiTests`:**
    - `Roster_and_inventory_show_the_starter_after_login`;
    - `A_pick_round_trip_persists_and_returns_the_new_version`;
    - `Stale_version_is_409`, `No_pending_pick_is_422`, `Unknown_choice_is_422`;
    - `Malformed_bodies_and_ids_are_400`: a missing member, an extra member, a non-integer version, an id with a leading `1`, an id with `0`; and 413 over 4 KiB;
    - `Every_player_route_without_a_token_is_the_uniform_401`;
    - `Me_reports_the_inventory_version`;
    - `An_unprovisioned_account_reads_409_NotProvisioned_and_nothing_is_written`;
    - `A_held_inventory_lock_is_503_busy`.
  - **`PlayerAuthorizationTests`:**
    - `A_pick_for_a_guessed_neighbouring_operative_is_the_same_404_as_an_unknown_one` (Review Focus 1): byte-identical bodies;
    - `An_account_id_in_query_header_or_body_changes_nothing`.
  - **`DtoAuditTests`:** no request DTO has an account member.
  - **`LogSecrecyTests`:** the new routes log no token.
- [ ] **Step 2: Run** (expected FAIL: 404s), **implement**, **run ALL** (expected green), then commit `Phase 13 Stage 4: roster, inventory and pick endpoints (Base58 owned ids, account from the session only); /v1/me inventoryVersion`.

**Review checkpoint 2 (independent, most capable model):** provisioning, backfill, picks and endpoints, over Tasks 6–10. Fix Critical and Important findings before Task 11.

### Task 11: Unity contract tests and id compatibility (D2, D3)

**Files:** create `Assets/_Project/Tests/EditMode/ProgressionContractTests.cs`, `StarterContractTests.cs` and `OwnedIdCompatibilityTests.cs`. The files under `Backend/contracts/` are read by a path relative to the project root.

- [ ] **Step 1: Write the tests.**
  - **`ProgressionContractTests.Every_case_matches_PersistentOperativeState`**:
    - build the track with `ProgressionTrack.Create` from the vector, and one `PersistentOperativeState` per case;
    - add XP with `AddExperience`; negative XP can't be added, so the −1 case asserts `RankFor(-1)` on the track directly;
    - make `picksMade` picks of a known choice, which must all succeed while picks are pending;
    - assert `Rank`, `PendingPicks` and the outcome of `TryPickChoice`. False maps to `UnknownChoice` when `FindChoice` is null, else `NoPendingPick`.
  - **`StarterContractTests.Every_case_matches_SeedStarter`**:
    - for each case, build `ItemDefinition`s through the internal factory, an `ItemCatalogue`, `OperativeDefinition` stubs and a `StarterLoadout.Create(...)`;
    - **refused** cases: assert that `CatalogueBuilder` refuses the equivalent sources with the mapped class, or for `bag-capacity` that `SeedStarter` reports a no-room problem;
    - **ok** cases: run `InventorySession.SeedStarter` with the case's bag capacity, then compare bag entries, equipped definitions and stash entries with the vector.
  - **`OwnedIdCompatibilityTests`** (D3, verify, not refactor):
    - `A_Base58_operative_id_round_trips_PersistentOperativeState`, with `"2"` and `"NQm6nKp8qFC"` through `JsonUtility`;
    - `A_Base58_item_id_works_as_an_InventoryState_instance_id`: an `ItemEntry` built with an explicit instance id (or through `TryAddEntry`), then `Find`, `MoveToStash`, `MoveToBag` and `Equip` all work with it.
- [ ] **Step 2: Run** `bash Tools/run-tests.sh EditMode` (Editor closed). Expected: PASS. **Mutation check:** change one expected quantity in `Backend/contracts/starter/vectors.json` → both xUnit and EditMode FAIL; revert.
- [ ] **Step 3: Commit.** `Phase 13 Stage 4: Unity EditMode runs the shared progression and starter vectors; Base58 owned ids verified with Unity's string ids`.

### Task 12: Docs as built, verification and acceptance

- [ ] **Step 1: Update the docs.** The spec (§0.4 measured, §15.1 as built, §16.1 contracts, §19), `Backend/README.md` (routes, provisioning, backfill CLI, catalogue format 2, bag capacity's home), the stage plan's Stage 4 status, `CLAUDE.md`, and this plan's "As built" with every ledger ruling.
- [ ] **Step 2: Run** backend ALL and UNIT, and Unity EditMode and PlayMode, and record the counts against the baselines.
- [ ] **Step 3: VM** (throwaway projects only): smoke at schema 0004, plus a smoke check that the admin CLI's account create reports `starter` counts and `starter backfill` on it says `already`. Then the drill (the 0004 invariant runs), the off-host probe during the hold, and an after-state check.
- [ ] **Step 4: Final review** (checkpoint 3, most capable model) over the whole branch. Fix Critical and Important findings.
- [ ] **Step 5: Commit, then stop.** No merge, push or Stage 5.

## Acceptance criteria

| # | Criterion | Proof |
|---|---|---|
| 1 | A new account and its starter are created in one transaction by the CLI and the route; a partial failure leaves nothing | `ProvisioningTests` |
| 2 | Backfill grants exactly once, concurrently and on retry, audited; it never re-grants after a catalogue change | `BackfillTests` |
| 3 | One origin reference never creates two instances; retries find the committed row; concurrent mints converge | `ProvisioningTests` minting cases |
| 4 | Owned ids are PostgreSQL bigint identities, shown as Base58; definition ids are unchanged; attempts stay UUIDv7 | `PlayerSchemaTests`, DTO tests |
| 5 | Ownership never crosses accounts; one item per slot; an equipped item is in a bag | `PlayerSchemaTests` |
| 6 | Picks follow `TryPickChoice` exactly and bump the shared version; a stale version gets 409 | `PickStoreTests`, shared vectors in both runners |
| 7 | Reads never write; an unprovisioned account gets 409 `NotProvisioned` | `PlayerReadStoreTests`, `PlayerApiTests` |
| 8 | No route takes an account id; a guessed neighbouring operative id gets the same 404 as an unknown one | `PlayerAuthorizationTests` |
| 9 | Bag capacity lives in `InventoryRules` → catalogue format 2; the backend has no hard-coded value; scene drift is tested; the new `catalogueVersion` is equal on both sides and in `sha256sum` | Catalogue tests, Unity tests, Task 3 evidence |
| 10 | Migrations 0003/0004 apply on 0002, privileges are exact, and the drill and smoke pass | Schema and upgrade tests, VM transcripts |

**Predicted counts (predictions, not measurements):** backend ALL ≈ 669 + 140; Unity EditMode ≈ +12.

## Out of scope
- Inventory edits (Stage 5);
- XP awards (settlement);
- `InventoryLocked` (participations);
- recruitment, respec, trading, crafting and purchases;
- wiring the offline-scene `SquadInventory` or `SquadRoster` to `InventoryRules` or the backend;
- live deployment.

## As built (2026-10-15)

**Status:** implemented on branch `phase13-stage4`; awaiting the owner's review; not merged, not pushed, the live accounts stack not deployed.

**Measured** (predictions were backend ALL about 669 + 140 and Unity EditMode about +12):

| Suite | Baseline (`fc823e8`) | Stage 4 |
|---|---|---|
| Backend ALL (`Backend.sln`) | 669/0 (Domain 191, Integration 478) | 974/0 (Domain 292, Integration 682) |
| Backend UNIT (`--filter "Requires!=Postgres"`) | not recorded | 480/0 |
| Unity EditMode | 1500/0 | 1524/0 |
| Unity PlayMode | 1434/0 | 1434/0 |

Catalogue: format 2, `catalogueVersion` `sha256:e83352b1c9d8764a92281551a25bea42621da2b592bc30d35986a5f0a86c496b` (three-way equal: Unity's writer, the backend's writer, `sha256sum`). Schema version 4.

**VM: pending (controller).** Smoke at schema 0004 (including the create output's starter counts and `starter backfill` saying `already`), the restore drill with the 0004 invariant, the off-host probe during the hold, and the after-state check.

**Rulings** (from the execution ledger, short form):
- Pre-flight: `Reads_write_nothing` creates no cluster role (roles are cluster-global and the owner limits tests to the authorized test databases); it uses a data source whose connections set `default_transaction_read_only=on`, asserts the reads succeed and that the store's transactions are read only. Cost if wrong: a weaker proof than a SELECT-only role.
- Pre-flight: Unity batch runs are serialized with the controller's own Unity runs (one Unity instance).
- Task 2: the namespace is `Blackglass.Accounts.Domain.Advancement` (folder `Progression/`); a `Progression` namespace shadows the catalogue record `Progression` (CS0118).
- Task 5: the invariant query returns one row per violation (not a count row); the restore drill fails on any row. `origin_ref` must be non-empty (the plan's SQL allowed an empty string; fixed before review).
- Checkpoint 1 (Task 5, folded into the opus review of `5b98ccc..0935ea0`): one Important finding, fixed: the app could mutate the granted starter record and the version, so `0004` gained the trigger `inventories_forward_only`. Spec section 15.2 corrected.
- Task 7: the `starter_backfill` audit result carries flat `operatives`, `instances`, `stacks` counts (same shape as `account_create`'s starter).
- Task 8: `Bag.Instances` includes the equipped items and `Equipment` lists the item id per slot (Unity's `OperativeEquipment` points into the bag; the schema stores `equipped_slot` on the carried row).
- Checkpoint 2 (Task 10, folded into the opus review of `8d0e032..062b1f4`): no Critical or Important findings. A pick is allowed for an operative whose definition the catalogue no longer knows (picks depend only on XP and the track).
- Task 12: `account create` prints `starterGranted=granted` and `starterOperatives=`, `starterInstances=`, `starterStacks=` (operators see the grant; the smoke test needs a visible signal). The smoke test also checks `starter backfill` reports `already`.

**Departures from the plan text:**
- Namespace `Advancement` instead of `Progression` (above).
- `Bag.Instances` includes equipped items (above).
- `ProvisionResult` gained `bool Granted` (Task 7), and `IssuedCredential` carries the optional `ProvisionResult` of account creation (Task 12). `AdminCommand.RunAsync` has an optional `loadCatalogue` test seam (Task 6).
- The read-only proof uses `default_transaction_read_only` on the data source rather than a SELECT-only role (above).
- Unity's contract tests read the vectors through `ContractVectors.cs`, a strict JSON reader in the test assembly (no Newtonsoft reference).
- The `inventories_forward_only` trigger was added at checkpoint 1 (above).
- `origin_ref` is non-empty (above).
- `/v1/me` also sends `Cache-Control: no-store`; four existing `/v1/me` assertions now expect `inventoryVersion`.

**Deferred minors** (none blocks the stage):
- Task 1: `Base58IdJsonConverter` error text says "An id" where `AccountIdJsonConverter` says "An account id" (intentional); `StarterRefTests` bad ids lack a trailing-newline case (the `\z` anchor covers it).
- Task 2: `ProgressionRules.PendingPicks` overflows for `int.MinValue` picks made (callers pass counts; validate at the caller if a database value is ever fed in); `Pending_is_never_negative` is weak for in-range rows (the vectors cover exact values); redundant `using` lines.
- Task 3: the `bagCapacity` 1..64 bounds are duplicated in Unity and the backend (covered by the export and the golden vector).
- Task 4: the planner throws for an equip on a slotted stackable that the validator would accept (unreachable: the validator forces consumables to slot none and equipment to max stack 1; add a validator rule if categories change); `StarterPlanner.Destination` has seven parameters; `AssertPlanMatches` compares stack totals (entry order is checked by the test-side model and by Unity).
- Checkpoint 1: `item_stacks` has no primary key (no replica identity); split null and non-null operative predicates in Stage 5 updates; the invariant's stack rows report `account_id` as the id.
- Task 6: the invalid-catalogue CLI test does not prove the database was untouched, and no test tells a passed catalogue from `Embedded`; the creation test name overstates atomicity; the rolled-back-mint test is near-tautological; two provisioning tests use exactly the pool's `MaxPoolSize` of 5; the wait poll busy-yields; the "nobody minted while waiting" count check is weak (the lock or deadlock is the real guard).
- Task 7: `WaitUntilBackendsWaitOnLocksAsync` is duplicated in `BackfillTests`, `ProvisioningTests` and `AccountTestKit`; the account-first lock order is not pinned by a `FOR NO KEY UPDATE` holder; the hard-coded 5 waiters equal the test pool size; a long doc-comment line in `AdminCommand`.
- Task 8: `Reads_write_nothing` cannot catch a dropped `set transaction read only` without an added write (inherent to the ruling; the data-source layer is covered by mutation); the utility slot is never exercised; per-operative list filtering is O(n·m); the transaction is not disposed if `set transaction read only` throws (the connection is).
- Task 9: no null `choiceId` case in `PickStoreTests` (the route rejects it as `InvalidBody`, tested at the route); the ordinal cast overflow is unreachable; the timeout test cannot tell which statement waited (the race test pins the lock).
- Checkpoint 2: `PlayerEndpoints`' refusal switch `_ => 422` would mis-map a future `InventoryLocked` (409); an already-granted `ProvisionResult` reports current holdings under the starter-count labels (identical in Stage 4, diverges from Stage 5 in backfill audits and CLI lines); the origin-reference collision exception is unmapped in the CLI and route (unreachable today); no 429 or unavailable-503 test on the new routes (shared middleware); an invalid catalogue blocks every admin verb including security revocations (exit 4): owner awareness.
- Task 11: the Unity runners' case-count guards are lower bounds (30 and 10), not exactly 32 and 17; one test per runner (the first failing case hides the rest); the JSON reader is lenient on `+1`, leading zeros and raw control characters.
