# Phase 13 Stage 7: insurance, participants and admission — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

Date: 2026-10-17. **Revision 2, 2026-10-17: authorized by the owner with rulings S7-D1–S7-D9 (§A), which supersede revision 1's proposals.** **Status: implemented on branch `phase13-stage7`, awaiting the owner's review; implementation commits not pushed, not merged, not deployed. See "As built".** Base: `main` @ `a9195ef` (Stage 6 merged and pushed; schema version 6; backend ALL 1561/0). Branch: `phase13-stage7`. Execution (proposed): subagent-driven, integrated by the primary session, with three independent review checkpoints: after Task 4 (schema, templates, insurance policies and the pure admission rules), after Task 7 (persistence, locking, endpoints and the deadline worker) and over the whole branch.

**Goal:** a player can deploy: `POST /v1/attempts` with an `Idempotency-Key`. In one transaction, admission:
- checks service health, failing closed;
- enforces one active attempt per account (in PostgreSQL);
- validates the template and operatives;
- reserves the template's energy cost;
- snapshots the account's loadout and its active insurance coverage;
- generates a secret mission seed and retention salt.

It replays the stored response for the same key and body, and refuses the same key with a different body without writing anything. Before a participant starts, the energy is released (player cancel, or the assignment deadline): exactly once, never double-charged.

While a participation is active, inventory edits and advancement picks are refused (`409 InventoryLocked`), and inventory reads report `locked: true`. The check is a mandatory, fail-closed, database-backed query. The always-unlocked development fallback is deleted.

Insurance gets immutable versioned policies and UTC-timed activations, where the same policy extends its coverage, a different policy while active is refused, and the idempotency key is honoured once.

**Stage 7 does not start gameplay:**
- no claim, launch, process session, ticket or start commit;
- **the `reserved → charged` transition is Stage 8's**;
- no settlement, payout, retention selection or void (Stage 9).

**Architecture:**
- **Domain (pure).** `Accounts.Domain/Insurance`: `InsuranceTerms` (strict reader and validator of the §6.2 terms JSON), `CoverageMath.Extend`. `Accounts.Domain/Attempts`:
  - `MissionTemplate`;
  - `DeployRequest` (canonical JSON and SHA-256 `request_hash`);
  - `LoadoutSnapshot` (built from the operatives and inventory model already used by Stages 4–5);
  - `AttemptSecrets` (seed and salt from an injectable `RandomNumberGenerator` source).
- **Persistence.**
  - Migration `0007_attempts_and_insurance`:
    - insurance: policies, coverages, activations;
    - `mission_templates` (versioned, immutable, seeded);
    - `mission_hosts` (only what the health check reads);
    - `mission_attempts`, `attempt_participants` with `one_active_participation`;
    - `energy_holds`, `attempt_insurance`, `deploy_requests`;
    - triggers, privileges and invariants.
  - `ParticipationLock` replaces `IInventoryLock`/`NoParticipationsYet`. `InsuranceAdminStore` handles policy publication and activation. `ServiceHealth` is the host-row check. `AdmissionStore` is the deploy transaction. `AttemptStore` handles the read, the cancel and the release.
  - `AssignDeadlineWorker` is a hosted service, like the reconciliation.
- **API.** `POST /v1/attempts`, `GET /v1/attempts/{id}` and `POST /v1/attempts/{id}/cancel`. The `/v1/me` field `activeAttemptId` is added (and `insurance: {active, policyId, expiresAt}` per §16.1).
- **Admin CLI.** `insurance policy publish`, `insurance activate`, `mission template publish` (S7-D1, S7-D3) and `host register-dev` (S7-D2, test and smoke only; see the ruling).

**Spec:** `Docs/superpowers/specs/2026-10-14-phase-13-backend-design.md` §3, §5.2, §6.1 (holds), §6.2, §6.3 (layer 3), §7.1–§7.3, §7.6 (`assign_deadline`), §13, §15.1–§15.4, §16.1, §16.4, §17, §18 (B5, B6), §19, decision 049; the Stage 5 and Stage 6 obligations (spec §0.5 and §0.6 "As built", §15.3). It becomes revision 2.7 in Task 0. Stage plan: `Docs/superpowers/plans/2026-10-14-phase-13-backend.md` §Stage 7 and the standing `InventoryLocked` obligation.

---

## A. Owner rulings (2026-10-17)

| Ruling | Decision |
|---|---|
| **S7-D1 mission templates** | Immutable, versioned templates in PostgreSQL (`mission_templates`). `procedural-standard` v1 is seeded with provisional configuration: energy cost 10, 1..3 operatives. **Zero-cost templates are supported.** The backend owns admission rules and energy costs; Unity stays authoritative for procedural generation and gameplay configuration. A published version is immutable; rebalancing publishes a new version through an audited operator command. There is no Unity template exporter in Stage 7. **Stage 8 must verify that an assigned server build can execute the requested template and settings version** |
| **S7-D2 service health** | A database-backed host-health check that fails closed. Until Stage 8's real registration, heartbeat and capacity, an ordinary deployment is 503 when no verified capacity exists. A development host fixture is allowed only in isolated tests, and it must be: disabled by default; created only with explicit development configuration and administrator authorization; never treated as verified real mission capacity; never enabled in the eventual production configuration. A health failure reserves no energy, regenerates nothing, leaves the wallet untouched, creates no attempt and locks no inventory |
| **S7-D3 insurance** | Immutable policy versions and timed account-level activations; `standard` v1 is seeded with the spec's provisional terms. The same policy extends: `newExpiresAt = max(existingExpiresAt, databaseNow) + duration`. Benefits never stack or multiply. Switching tiers while active is refused. Publication and activation are audited, confirmed, transactional and idempotent. At admission the participant's active policy, version and **complete terms** are snapshotted. Coverage stays locked to the attempt even if the account-level coverage expires before start or settlement. Cancelled attempts get no payout. **No payout calculation or settlement in Stage 7** |
| **S7-D4 reads and locking** | Authenticated inventory reads are allowed during missions; `GET /v1/inventory` shows `locked: true`. Inventory mutations and advancement picks are `409 InventoryLocked`. Every always-unlocked fallback is replaced by a mandatory database-backed participation check, which fails closed when the lock state cannot be determined. It covers every active participant state as it is introduced (Stage 7: `admitted`; later `joined`, `left` (disconnected), `result_pending`, `unresponsive`, `quarantined`). The lock is released only by an authoritative terminal transition (`released`, later `settled`). **A stale `expectedVersion` never conceals an active `InventoryLocked`** |
| **S7-D5 deployment idempotency** | An explicit retry with the same key returns the recorded attempt, with no second reservation. A conflicting request with the same key is 409. Admission is never retried automatically with a new key. The hash is over the normalized, validated request, not the raw JSON, and keeps the operative order |
| **S7-D6 assignment timeout** | A configurable, provisional 60 s deadline. A database-backed worker cancels unassigned attempts after it. The cancellation atomically moves the attempt to `cancelled` and the participation to `released`, releases the reserved energy exactly once with its ledger entry, releases the inventory lock, and preserves the historical admission and insurance records. A retry or a competing cancellation never releases twice. An attempt already in a valid later state is not cancelled |
| **S7-D7 player cancel** | Allowed before mission start. After start, abandonment follows the separately approved failure and insurance rules and is not an energy-refunding cancellation. Stage 7 implements only the pre-start case; cancellation and the worker are concurrency-safe |
| **S7-D8 deployment snapshot** | Only the selected operatives, their advancement state, bags and equipment, plus account identity, inventory version and catalogue version. The stash is not copied. The persistent inventory is locked while the attempt is active, so omitted stash items can't move into or out of the deployed loadout. Admission validates operative ownership, duplicate selections, team size and equipment references. An owned operative's `bigint`/Base58 identity stays distinct from its authored catalogue definition id. The snapshot is immutable after admission |
| **S7-D9 squad ordering** | Operative order is meaningful. The same ids in a different order are a different request for idempotency. The requested order is kept in the snapshot, for later squad-slot and spawn assignment. Duplicate ids are refused. Two equivalent JSON requests that differ only in property order or whitespace give the same hash |

**The owner's technical requirements** (verbatim intent, mapped):

| # | Requirement | Where it is met |
|---|---|---|
| 1 | `0007_attempts_and_insurance`; 0000–0006 byte-identical | Task 2; the path audit |
| 2 | One active participation per account: a partial unique index plus transaction locking | Task 2 index; Task 6 locks |
| 3 | A globally consistent lock order with account creation, inventory edits, grants and publication, addressing the account foreign-key interaction | Global Constraints, Locking (`accounts` `FOR KEY SHARE` first) |
| 4 | Deterministic tests of admission racing an edit, a pick, a grant, a publication, a player cancel and assignment expiry | Task 6, Task 7 |
| 5 | Reserve at deployment; no start charge | Task 6; scope |
| 6 | No second charge on reconnect or retry of one attempt | Task 6 idempotency; there is no reconnect path in Stage 7 |
| 7 | A zero-cost template is admitted with no invalid wallet or ledger entry | Controller rulings below; Task 6 |
| 8 | Secure seed and salt, never in client DTOs, errors, player logs or other unauthorized output | Task 1, Task 6, Task 7 |
| 9 | A co-op-compatible model; multiple participants not enabled | Task 2, Task 6 |
| 10 | PostgreSQL time; timestamp boundaries and transaction waits tested | Global Constraints, Clock; Tasks 3, 6, 7 |
| 11 | Complete rollback; no orphaned locks, attempts, deductions or partial snapshots | Failure-injection tests, Tasks 6 and 7 |
| 12 | Strict authentication, Base58 ownership, information-safe errors | Tasks 6 and 7 |
| 13 | Energy and insurance behaviour preserved; no payments or monetization | Scope |

**Controller rulings needed to implement the owner's rulings** (each with what it costs if wrong):
- **Zero-cost admission (D1, requirement 7).**
  - A template with energy cost 0 takes no energy path: no wallet lock, no materialization, no policy lock and no ledger entry. 0006 forbids a `reserve` entry with delta 0, and none is needed.
  - The participant gets an `energy_holds` row with amount 0 in state `reserved` (uniform model), released the same way.
  - The 0007 invariants accept a 0-amount hold without a ledger entry and require exactly one entry for a positive hold.
  - An account without a wallet may deploy a zero-cost template; a positive cost with no wallet is `409 InsufficientEnergy`.
  - Cost if wrong: zero-cost admissions would also have to lock the wallet.
- **The development host fixture (D2).**
  - `mission_hosts.kind` is `'dev_fixture'` or `'agent'`; `'agent'` rows are created by Stage 8's registration.
  - The health check counts `dev_fixture` hosts only when the API process runs with `BLACKGLASS_DEV_HOSTS=enabled`. It defaults to disabled, the compose default is disabled, and `serve` logs a warning at startup when it is enabled.
  - `admin host register-dev` (confirmed, audited) refuses unless the same setting is enabled in the admin process.
  - Production configuration never sets it; the Stage 17 deployment checklist must assert it is unset.
  - `agent` hosts always count when healthy. With none, every deploy is 503.
  - Cost if wrong: one column and one setting.
- **Equipment references (D8).** Admission refuses (`422 UnknownItem`, with the operative's index) a selected operative whose bag or equipment holds an item whose definition the catalogue no longer knows. A retired item can still be moved to the stash (Stage 5), and the mission server can't use an unknown definition. Cost if wrong: retired items would deploy as inert entries.
- **Release takes the `inventories` row lock** after the participant (spec §15.3 order: attempts → participants → inventories → wallets). The lock state therefore changes while that lock is held, and an edit waiting on it sees the terminal state. Cost if wrong: an extra lock on the release path.
- **The insurance snapshot uses the coverage active at admission's `now`.** It is read without a lock (spec §15.3; activations lock only `insurance_coverages`). An activation committing after admission's snapshot statement does not apply to that attempt. Cost if wrong: nothing; the boundary is the admission `now`.
- **No automatic retry of admission, release or activation** (S7-D5, and the Stage 5 and 6 rule).

## Global Constraints

- **Migrations:** forward-only, numbered, checksummed, one transaction each. **0000–0006 are applied and immutable.** Stage 7 adds `0007_attempts_and_insurance.sql` only. Its number is `0007` everywhere: the stage plan, spec §15.1's sketch note and this plan.
- **Ids:**
  - Account, operative and item ids are `bigint` inside and Base58 outside.
  - Attempt ids are UUIDv7, insurance activation ids UUIDv7, and idempotency keys UUIDs.
  - No request carries an account id: the session's account only.
  - Another account's attempt is the same 404 as an unknown one.
- **Clock:** every time used in admission, activation, release and deadline sweeps is PostgreSQL's `clock_timestamp()`, read after the transaction's locks (and, where energy is touched, after the energy policy lock; Stage 6).
- **Locking (documented lock order; spec §15.3, extended):**
  - **Admission:**
    1. `accounts` `FOR KEY SHARE`. This comes first, because inserting attempt, participant and deploy-request rows checks their account foreign keys. That check, taken after the energy policy lock, could wait on an operator's `FOR UPDATE` while a publisher is queued: the Stage 5 precedent and the Stage 6 leaf rule.
    2. `inventories` `FOR UPDATE`, ascending `account_id`.
    3. `energy_wallets` `FOR UPDATE`, ascending.
    4. The energy policy lock (shared), then the clock.
    5. Inserts of new rows only. Coverage and policy rows are read without locking.
  - **Release (cancel or deadline):** `accounts` `FOR KEY SHARE` → `mission_attempts` `FOR UPDATE` → `attempt_participants` → `energy_holds` → `inventories` `FOR UPDATE` → (positive amount only) `energy_wallets` `FOR UPDATE` → the policy lock → clock → writes. (As built: the account key-share step was added after the Checkpoint 2 test round found a deadlock with a same-account grant; see "As built".)
  - **Activation:** `accounts` `FOR UPDATE` → `insurance_coverages` `FOR UPDATE` (or the insert) → clock. It takes no energy lock.
  - **Inventory edits and picks:** unchanged, plus the participation query under the `inventories` lock.
  - **The leaf rule** (Stage 6) holds after the energy policy lock: only own or new rows, policy rows, and at most one global unique key per transaction.
  - Waits are bounded by the connection timeouts. A timeout is busy (503 or exit 75), with nothing written. **No automatic retry** (S7-D5).
- **Energy:** reservation, release and their ledger entries, with the hold's state change, happen in one transaction under the wallet lock. The ledger keys are `reserve:<attempt>` and `release:<attempt>`, unique per account. A hold's transition is guarded by its source state, so a second release is a no-op returning the stored outcome. **There is no `charge` in Stage 7.**
- **Insurance:**
  - A policy `(policy_id, version)` is immutable.
  - There is at most one coverage per account.
  - The same `policy_id` extends: `greatest(expires_at, now) + duration`.
  - A different `policy_id` while active is `CrossTierDeferred`, with nothing written.
  - The activation key is honoured once.
  - The snapshot is copied at admission if the coverage is active at the admission's `now` (`starts_at <= now < expires_at`), and is immutable afterwards.
- **Secrets:** the seed and retention salt are never in a client response, a log, an audit row, an anomaly or an error. Admin output never shows them either.
- **Failures:** `DbFailure` and the Stage 3 classes. Known refusals are ProblemDetails `{code}` (409 `AttemptActive`, `InsufficientEnergy`, `IdempotencyKeyReused`, `InventoryLocked`, `InvalidState`; 422 `UnknownTemplate`, `UnknownOperative`, `OperativeCount`; 503 `ServiceUnavailable` with `Retry-After`).
- **Scope:** no claim, assignment, launch, process session, ticket, start commit, charge, settlement, payout, retention selection, void, quarantine, receipts, gameplay, Unity change, co-op enablement, purchases or premium currency.
- **Touches:** `Backend/` (src, tests, `db/migrations/0007_attempts_and_insurance.sql`, `db/invariants/0007_attempts.sql`, the compose passthrough of new settings, the smoke script, README), `Docs/`, `CLAUDE.md`. **No Unity path.**
- **Infrastructure:**
  - No live deployment; no firewall, SSH, Docker networking or mission-VM change.
  - The VM work uses the throwaway `bg-smoke`/`bg-drill` projects and the test cluster.
  - Tests create or drop only `bg_t_*` databases.
- **Preserve** the owner's two Unity settings files and `.claude/`.
- **Git:** logically separated commits on `phase13-stage7`. Nothing is pushed or merged without the owner's approval.
- **Commit trailer:** `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- **Commands:** as Stage 6, including the low-memory recipe: `-m:1` build, then each test project in turn with the low-memory runsettings, never with Unity, one run at a time.

## Review Focus

1. **Fifty concurrent deploys for one account.**
   - Expected: exactly one attempt, one hold and one `reserve` entry, and energy deducted once; every other call is `409 AttemptActive` (or, with the same key, the stored response).
   - A raw SQL insert of a second active participation fails on `one_active_participation`.
   - Tests: Task 6 `Fifty_parallel_deploys_for_one_account_admit_once` and `A_direct_insert_of_a_second_active_participation_fails_on_the_index`.
2. **One idempotency key with two bodies, sequential and concurrent.**
   - Expected: the first body admits. The other is `409 IdempotencyKeyReused` with no attempt, hold, ledger or wallet change. Twenty identical requests give one attempt and byte-identical bodies.
   - Tests: Task 6 `A_reused_key_with_a_different_body_writes_nothing` (both orders, and concurrent) and `Twenty_identical_requests_give_one_attempt_and_identical_bodies`.
3. **A deploy racing an operator grant and a queued energy policy publication** (the foreign-key key-share hazard).
   - Expected: everything drains, with no 40P01.
   - Test: Task 6 `Admission_drains_with_a_grant_and_a_queued_publication_without_deadlock`. A mutation that drops the `accounts` `FOR KEY SHARE` step must fail it.
4. **A release racing a second release or the deadline worker.**
   - Expected: one `release` entry; the second caller gets the stored outcome; the balance is restored exactly once, even above the cap.
   - Tests: Task 7 `A_cancel_and_the_deadline_worker_release_once` and `Two_concurrent_cancels_release_once`.
5. **An edit, a pick or a read during an active participation, or when the participation query fails.**
   - Expected: the edit and the pick are 409 `InventoryLocked` with nothing written; the read is 200 `locked: true`. A failing query is a 500 or 503, never "unlocked".
   - Tests: Task 5 `An_edit_and_a_pick_during_a_participation_are_InventoryLocked`, `A_failing_participation_query_refuses_the_mutation`, and `Serve_wires_the_database_backed_lock_only`.

---

## Task 0 (controller, docs first): record the rulings

- **Spec** revision 2.7:
  - §0.7 rulings S7-D1–S7-D9;
  - §6.2/§7.3 as planned (lock order with the `accounts` key-share step, S7-D9 canonical form);
  - §15.1 note (`0007_attempts_and_insurance`, the tables narrowed to Stage 7; hosts' agent columns and builds, tickets, pools and objectives move to Stage 8's migration);
  - §15.3;
  - §16.1 (`activeAttemptId`, `insurance` in `/v1/me`; `GET /v1/attempts/{id}` without `join` until Stage 8);
  - §16.4 (CLI verbs);
  - §19 (template cost and count, deadlines, sweep interval, deploy rate limit).
- **Stage plan:** the Stage 7 section (authorized; this plan; migration `0007`).
- **`CLAUDE.md`.**
- **This plan:** revision 2.
- **Commit:** `Phase 13 Stage 7: owner rulings S7-D1..S7-D9 recorded (spec revision 2.7, plan revision 2, CLAUDE.md)`.

## Task 1: Pure rules (domain)

**Additions from the owner's rulings (revision 2):**
- `MissionTemplate` allows `EnergyCost = 0`.
- `DeployRequest`:
  - normalizes (property order and whitespace are insignificant: the parsed, validated request is serialized canonically);
  - keeps the operative order;
  - refuses duplicate ids.
- Tests: `Equivalent_JSON_with_reordered_properties_or_whitespace_hashes_the_same` and `Order_matters_for_the_hash`.
- `LoadoutSnapshot`:
  - keeps the requested operative order (slot index 0..n−1);
  - carries the account id (Base58 in JSON), the inventory version, the catalogue version and per operative both the owned id (Base58) and the definition id;
  - `Build` reports any item whose definition the catalogue doesn't know.
- Tests: `LoadoutSnapshot_keeps_the_requested_order_and_both_identities` and `LoadoutSnapshot_reports_unknown_item_definitions`.

**Files:** `src/Accounts.Domain/Insurance/{InsuranceTerms,CoverageMath}.cs`, `src/Accounts.Domain/Attempts/{MissionTemplate,DeployRequest,LoadoutSnapshot,AttemptSecrets}.cs`; tests under `tests/Accounts.Domain.Tests/{Insurance,Attempts}/`.

**Interfaces (produced):**

```csharp
namespace Blackglass.Accounts.Domain.Insurance;
/// The §6.2 terms, read strictly (unknown members, duplicates, wrong types, out-of-range values refused).
public sealed record InsuranceTerms(IReadOnlyList<string> Covers, ConsumableRefund ConsumableRefund, LootRetention LootRetention, ProgressXp ProgressXp)
{ public static InsuranceTerms Parse(string json); public string Canonical(); }
public static class CoverageMath
{
    /// greatest(expiresAt, now) + duration; a new coverage starts now. UTC, whole microseconds; duration > 0.
    public static (DateTime StartsAt, DateTime ExpiresAt) Extend(DateTime? startsAt, DateTime? expiresAt, DateTime now, TimeSpan duration);
    public static bool IsActive(DateTime startsAt, DateTime expiresAt, DateTime now); // starts_at <= now < expires_at
}

namespace Blackglass.Accounts.Domain.Attempts;
public sealed record MissionTemplate(string TemplateId, int Version, int EnergyCost, int MinOperatives, int MaxOperatives);
public sealed record DeployRequest(string MissionTemplate, IReadOnlyList<long> OperativeIds)
{
    /// Strict parse of the HTTP body (S7-D9): exactly the two members, canonical Base58 ids, no duplicates, 1..16 ids.
    public static DeployRequest Parse(ReadOnlySpan<byte> utf8Json);
    public byte[] CanonicalJson();   // {"missionTemplate":"…","operativeIds":["…",…]}
    public byte[] RequestHash();     // SHA-256 of CanonicalJson()
}
public sealed record LoadoutSnapshot(long InventoryVersion, string CatalogueVersion, IReadOnlyList<SnapshotOperative> Operatives)
{ public static LoadoutSnapshot Build(/* the roster and inventory model rows for the chosen operatives */); public string ToJson(); }
public interface IAttemptSecrets { int NextSeed(); byte[] NextRetentionSalt(); } // production: RandomNumberGenerator
```

- [ ] **Step 1: Write the failing tests:**
  - **Insurance terms:**
    - `Terms_parse_the_6_2_example_and_round_trip_canonically`
    - `Terms_refuse_unknown_members_duplicates_and_out_of_range_values` (theory)
  - **Coverage:**
    - `Extend_keeps_the_remaining_time_and_adds_the_duration`
    - `Extend_after_expiry_restarts_from_now`
    - `IsActive_excludes_expires_at`
  - **Deploy request:**
    - `DeployRequest_parses_strictly` (theory: unknown members, duplicates, non-canonical ids, an empty or oversized list, a duplicate id, a wrong type)
    - `The_canonical_form_and_hash_are_stable` (fixed vectors)
    - `Order_matters_for_the_hash` (S7-D9)
  - **Loadout snapshot:**
    - `LoadoutSnapshot_holds_only_the_chosen_operatives_bags_and_equipment`
    - `LoadoutSnapshot_json_is_deterministic`
  - **Secrets:**
    - `Secrets_seed_is_31_bit_non_negative_and_salt_32_bytes`
    - `Production_secrets_use_RandomNumberGenerator` (a source audit)
- [ ] **Step 2:** run (FAIL: types missing), implement, run UNIT (PASS).
- [ ] **Step 3: Commit.** `Phase 13 Stage 7: pure rules for insurance terms and coverage, deploy requests, loadout snapshots and attempt secrets`.

## Task 2: Migration `0007_attempts_and_insurance`, invariants and privileges

**Additions from the owner's rulings (revision 2):**
- `mission_templates.energy_cost >= 0`.
- `mission_hosts.kind text not null check (kind in ('dev_fixture','agent'))`.
- `energy_holds.amount >= 0`.
- The 0007 invariants: a positive hold has exactly one `reserve` entry (and one `release` entry when released); a 0-amount hold has none.
- `attempt_participants.loadout_snapshot` and `attempt_insurance` are immutable (triggers); only `state` (and the timestamps later stages add) may change on a participant.
- Test: `A_zero_amount_hold_needs_no_ledger_entry_and_a_positive_one_needs_exactly_one`.

**Files:** `db/migrations/0007_attempts_and_insurance.sql`, `db/invariants/0007_attempts.sql`; tests `AttemptSchemaTests.cs`, `AttemptPrivilegeTests.cs`, `InsuranceSchemaTests.cs`. Update every test that pins the latest schema, triggers or privileges (the Stage 6 lesson: `RevocationPermanenceTests`, `AccountsSchemaTests`, `PlayerSchemaTests`, `EnergyPrivilegeTests`, `UpgradeTests`).

**Schema (narrowed from the §15.1 sketch to what Stage 7 uses):**
- **Insurance:**
  - `insurance_policies (policy_id, version, terms jsonb, created_at)`: immutable; seeded with `standard` v1.
  - `insurance_coverages (account_id PK, policy_id, policy_version, activation_id, starts_at, expires_at)`.
  - `insurance_activations (activation_id uuid PK, account_id, policy_id, policy_version, duration, previous_expires_at, resulting_expires_at, source 'dev_grant', idempotency_key uuid, created_at, unique (account_id, idempotency_key))`: append-only, no truncate.
- **`mission_templates (template_id, version, energy_cost ≥ 0, min_operatives ≥ 1, max_operatives ≥ min, created_at, pk (template_id, version))`:** immutable; seeded with `procedural-standard` v1 (10, 1, 3).
- **`mission_hosts (host_id, secret_hash, enabled, drained, capacity ≥ 0, last_heartbeat_at, created_at)`:** as sketched. Stage 8 adds what claims need.
- **`mission_attempts`:**
  - `attempt_id uuid PK`, `created_by bigint` (references `accounts`);
  - `template_id`, `template_version` (references `mission_templates`);
  - `seed int ≥ 0`, `retention_salt bytea(32)`;
  - `state` (the full §7.2 list);
  - `cancel_reason` (`no_capacity`, `player_cancel`);
  - `created_at`, `assign_deadline`, `ended_at`.
  - Stage 8 adds the host, port, process and build columns.
- **`attempt_participants`:** as sketched, plus `template_version`. `one_active_participation` is the partial unique index.
- **`energy_holds`:** as sketched; its state transitions are guarded by a trigger: `reserved → released`, plus Stage 8's `reserved → charged`.
- **`attempt_insurance`:** as sketched; immutable.
- **`deploy_requests`:** as sketched; immutable after insert.
- **`energy_ledger` (forward changes):**
  - `attempt_id` references `mission_attempts`;
  - a CHECK that `reserve`, `release`, `charge`, `settle` and `void_refund` entries carry an `attempt_id`;
  - the per-kind key pattern for `reserve` and `release` is `^(reserve|release):<uuid>$` and must match `attempt_id`.
- **Triggers:**
  - immutability or append-only for policies, activations, `attempt_insurance`, `deploy_requests` and templates;
  - guarded hold and participant state transitions;
  - no truncate.
- **Operator actions:** add `insurance_policy_publish`, `insurance_activate`, `mission_template_publish`, `host_register_dev`.
- **Privileges (`accounts_app`):** the exact columns each path writes. No `UPDATE` or `DELETE` on the immutable tables; `UPDATE` only of `state`, `cancel_reason` and `ended_at` on attempts, and of `state` on participants and holds. Nothing on sequences.
- **Invariants (one row per violation):**
  - a participant's hold state and the participant state disagree;
  - a `reserved` hold without exactly one `reserve` entry;
  - a `released` hold without exactly one `release` entry;
  - an account with two active participations;
  - an attempt whose participants' template differs from it;
  - an `attempt_insurance` row whose terms differ from its policy version's terms.

- [ ] **Step 1: Write the failing tests.**
  - One test per CHECK and trigger, violated directly in SQL.
  - `A_direct_insert_of_a_second_active_participation_fails_on_the_index`.
  - `Co_op_shaped_data_keeps_holds_snapshots_and_insurance_independent` (two participants by direct SQL).
  - `The_app_role_has_exactly_the_stage7_privileges`.
  - `Upgrading_a_stage6_database_to_0007_keeps_every_row`.
  - `Each_attempt_invariant_reports_its_violation`.
  - `Seeds_are_the_provisional_values`.
- [ ] **Step 2:** run (FAIL), implement, run the classes plus every pinning class (PASS).
- [ ] **Step 3: Commit.** `Phase 13 Stage 7: migration 0007 (insurance, templates, hosts, attempts, participants, holds, snapshots, deploy requests) and invariants`.

## Task 3: Insurance policies and activations (operator verbs)

**Additions from the owner's rulings (revision 2):**
- The extension rule is exactly `max(existingExpiresAt, databaseNow) + duration`, with `databaseNow` = `clock_timestamp()` after the coverage lock.
- Tests (boundaries): `Activation_exactly_at_expiry_restarts_from_now` and `Activation_one_microsecond_before_expiry_extends_from_the_old_expiry`.
- No payout or settlement code.

**Files:**
- `src/Accounts.Persistence/Insurance/{InsuranceAdminStore,InsuranceReader}.cs`;
- the CLI verbs `insurance policy publish --policy-id <id> --terms-file <path>` and `insurance activate --account-base58 <id> --policy-id <id> --duration-seconds <s> --idempotency-key <uuid>`, both confirmed and audited;
- tests `InsuranceAdminStoreTests.cs` and `InsuranceCliTests.cs`.

**Behaviour:**
- **Publication:** the terms are validated by `InsuranceTerms.Parse` before any connection. The version is `coalesce(max(version), 0) + 1` for that `policy_id`, computed under a per-policy transaction advisory lock (`pg_advisory_xact_lock(hashtext('insurance-policy:' || policy_id))`), with the primary key as the backstop (a 23505 there is reported, never retried). The same rule applies to `mission template publish`.
- **Activation:**
  1. `accounts` `FOR UPDATE`, then the coverage `FOR UPDATE` (or the insert), then the clock.
  2. The key is looked up first: the same account, policy and duration replays the stored result and writes nothing; anything else is `IdempotencyKeyReused` (the Stage 6 correction's rule).
  3. For a new key:
     - the same policy extends;
     - a different policy while active is `CrossTierDeferred`;
     - a different policy after expiry starts fresh;
     - one append-only activation row, plus the coverage update and the audit.

- [ ] **Tests:**
  - `Activating_the_same_policy_extends_from_the_later_of_expiry_and_now`
  - `An_expired_coverage_restarts_from_now`
  - `A_different_policy_while_active_is_CrossTierDeferred_and_writes_nothing`
  - `A_duplicate_key_does_not_extend_twice` (sequential, and 20 concurrent)
  - `A_reused_key_with_other_parameters_is_refused`
  - `Activation_uses_the_database_clock_after_the_coverage_lock`
  - `Policies_are_immutable_and_versions_increase`
  - `An_invalid_terms_file_is_refused_before_any_connection`
  - failure injection on the audit (rolls back)
  - a lock timeout (busy, nothing written)
  - the CLI outputs and exit codes
  - `Insurance_verbs_never_load_the_catalogue`
- [ ] **Commit:** `Phase 13 Stage 7: insurance policy versions and activations as audited operator verbs`.

## Task 4: Service health and mission templates

**Additions from the owner's rulings (revision 2):**
- The health check counts `kind = 'agent'` hosts always, and `kind = 'dev_fixture'` hosts only when `BLACKGLASS_DEV_HOSTS=enabled` in the API process (default disabled; a warning is logged at startup when enabled).
- `host register-dev` is confirmed and audited, and refuses unless the admin process has `BLACKGLASS_DEV_HOSTS=enabled`.
- Tests:
  - `A_dev_fixture_host_is_not_capacity_unless_dev_hosts_are_enabled`;
  - `Register_dev_is_refused_without_the_development_setting`;
  - `The_compose_default_leaves_dev_hosts_disabled` (ComposeText);
  - `A_health_failure_touches_no_wallet_attempt_or_inventory_lock`: no `pg_locks` row on `inventories` or `energy_wallets` from the refused call, the wallet xmin unchanged, no attempt row.
- `mission template publish` allows cost 0. Tests: `A_zero_cost_template_can_be_published` and `Templates_are_immutable_and_publish_audited`.

**Files:**
- `src/Accounts.Persistence/Attempts/{ServiceHealth,MissionTemplates}.cs`;
- the CLI verbs `mission template publish` and `host register-dev`. `host register-dev` creates an enabled host row with a random secret hash that is never printed, for test and smoke use only, and refuses unless `BLACKGLASS_DEV_HOSTS=enabled`, which defaults to off. A smoke helper touches the heartbeat on the throwaway stack only;
- tests `ServiceHealthTests.cs` and `MissionTemplateTests.cs`.

**Behaviour:**
- `ServiceHealth.CheckAsync(conn, tx)` returns healthy only when some host is enabled, not drained, has a fresh heartbeat (lease `BLACKGLASS_HOST_LEASE_SECONDS`, provisional 30) and has free capacity.
- Any exception propagates, which makes admission answer 503 or 500, never admit.

- [ ] **Tests:**
  - `No_host_is_unhealthy`
  - `A_stale_drained_disabled_or_full_host_is_unhealthy` (theory)
  - `A_fresh_host_with_capacity_is_healthy`
  - `A_failing_health_query_never_admits`
  - `Templates_are_immutable_and_publish_audited`
  - `Host_register_dev_is_refused_unless_enabled_and_prints_no_secret`
- [ ] **Commit:** `Phase 13 Stage 7: fail-closed service health and versioned mission templates`.

**Checkpoint 1 (independent, most capable model):** Tasks 1–4: schema, templates, insurance policies and the pure admission rules.

## Task 5: Mandatory, fail-closed `InventoryLocked`

**Checkpoint 1 notes (binding):** the active-state list exists in three places: 0007's `one_active_participation` predicate, the `0007_attempts.sql` invariant, and the C# constant. One test pins all three against each other.

**Additions from the owner's rulings (revision 2):**
- The active-state list is one constant shared with `one_active_participation` (a test pins that the index predicate and the C# list agree).
- The order under the row lock stays `NotProvisioned` → `InventoryLocked` → `VersionMismatch`.
- Test: `A_stale_expected_version_never_hides_InventoryLocked` (an edit and a pick with a stale version during a participation are `InventoryLocked`, not `VersionMismatch`).
- Tests: `Reads_during_a_participation_report_locked_true`; `The_lock_releases_only_on_a_terminal_transition`.

**Files:**
- Replace `src/Accounts.Persistence/Player/InventoryLock.cs` with `ParticipationLock` (a sealed class).
- Make the lock a required constructor parameter of `PickStore`, `InventoryEditStore` and `PlayerReadStore`, with no default and no optional parameter.
- Delete `NoParticipationsYet`.
- Update `ServeCommand`.
- Move the test doubles (`AlwaysLocked`, `RowProbe`, `Gate`) to wrap or replace the interface in the test project only.

**Behaviour:**
- `IsLockedAsync(conn, tx, accountId)` runs `select exists (select 1 from attempt_participants where account_id = @a and state in (<active states>))` in the caller's transaction (under the `inventories` row lock for mutations, or in the read snapshot for reads).
- An exception propagates; nothing ever defaults to unlocked.

- [ ] **Tests:**
  - `An_edit_and_a_pick_during_a_participation_are_InventoryLocked`: nothing is written and the version is unchanged.
  - `Reads_during_a_participation_report_locked_true` (S7-D4).
  - `After_release_the_account_can_edit_and_pick_again`.
  - `A_failing_participation_query_refuses_the_mutation`: revoke `SELECT` on `attempt_participants` in a test database, giving 500 with nothing written; the lock and statement timeouts give 503.
  - `Serve_wires_the_database_backed_lock_only`: resolve the stores from `serve`'s container and assert the implementation type.
  - `No_production_type_implements_an_unlocked_fallback`: a reflection source audit over the production assemblies.
  - Every existing `InventoryLockTests` and Stage 5 lock-order test, carried over.
- [ ] **Commit:** `Phase 13 Stage 7: InventoryLocked is a mandatory, fail-closed participation query; the always-unlocked fallback is deleted`.

## Task 6: Admission (`POST /v1/attempts`)

**Checkpoint 1 notes (binding; they correct the step list below):**
- **F1, insert order.** 0007's ledger `attempt_id` foreign key is immediate. After the clock, insert in this order: attempt → participant → hold → (positive cost only) the wallet update and the `reserve:<attempt>` entry → the insurance snapshot → `deploy_requests`.
- **F2, idempotency under concurrency.** Keep the early `deploy_requests` lookup (a recorded attempt replays even while health is down). **Re-read `deploy_requests (account, key)` again after the `inventories` lock and before the `AttemptActive` check**: the same hash replays the stored body; a different hash is `IdempotencyKeyReused`. A 23505 on `deploy_requests_pkey` re-reads the row; one on `one_active_participation` answers `AttemptActive`.
- **F3, byte-identical bodies.** Answer the first request from `insert … returning response::text`, exactly as a replay reads it.
- **Advisory notes, adopted:**
  - **Retired operatives.** Check them in admission against the catalogue before `LoadoutSnapshot.Build`: `422 RetiredOperative`, with the index.
  - **Choices** are read `order by ordinal`.
  - **The snapshot** uses the locked `inventories.version`.
  - **The insurance snapshot** copies `terms` by `insert … select terms from insurance_policies`. `activation_id` is the coverage's. "Active" is `CoverageMath.IsActive` at admission's `now`.
  - **The health check** runs before the transaction begins. The admission-level test proves the refusal holds no `inventories`/`energy_wallets` lock and allocates no transaction id.
  - **The body** is capped (`BLACKGLASS_DEPLOY_MAX_BODY_BYTES`, provisional 4 KiB) before parsing.
  - **Zero cost:** after `inventories`, read the clock directly, with no wallet and no policy lock.

**Additions from the owner's rulings (revision 2):**
- **The lock order.** `accounts` `FOR KEY SHARE` comes first (before `inventories`).
- **Zero-cost path.** A template with cost 0 takes no wallet lock, no materialization, no policy lock and no ledger entry, and writes a 0-amount `reserved` hold. A wallet is not required.
- **Equipment references.** An operative carrying an unknown definition is refused with `422 UnknownItem`, with its index.
- **Snapshot.** It keeps the requested order and carries the account id.
- **The service-health refusal** comes before any lock or energy step.
- **Deterministic race tests** (held-lock pattern, waiters seen in `pg_stat_activity`):
  - `Admission_racing_an_inventory_edit_serializes_and_the_loser_is_refused_cleanly`;
  - `Admission_racing_a_pick_serializes`;
  - `Admission_racing_a_grant_serializes_without_deadlock`;
  - `Admission_drains_with_a_grant_and_a_queued_publication_without_deadlock` (a mutation that drops the `accounts` `FOR KEY SHARE` step must fail it);
  - `Admission_racing_a_player_cancel_of_the_previous_attempt`;
  - `Admission_racing_assignment_expiry_of_the_previous_attempt`.
- **Zero-cost tests:** `A_zero_cost_template_admits_with_no_wallet_change_or_ledger_entry` and `A_zero_cost_template_admits_an_account_without_a_wallet`.
- **Boundary tests:**
  - `Coverage_expiring_exactly_at_admission_now_is_not_covered`;
  - `Coverage_active_at_admission_now_is_snapshotted_with_complete_terms`;
  - `The_snapshot_survives_coverage_expiry_extension_and_new_policy_versions`.
- **Identity test:** `A_retry_of_the_same_attempt_never_reserves_again`.

**Files:** `src/Accounts.Persistence/Attempts/AdmissionStore.cs`; `src/Accounts.Api/Attempts/AttemptEndpoints.cs` (route, `Idempotency-Key` header parsing, ProblemDetails, the per-account deploy rate limit `BLACKGLASS_RATE_DEPLOY_PER_MINUTE`, provisional 6); DTOs; tests `AdmissionStoreTests.cs`, `AttemptApiTests.cs`.

**The transaction** (spec §7.3, as amended by §A and the Global Constraints):
1. Parse and hash the body (400 on a bad body, before any connection).
2. Look up `deploy_requests (account, key)`.
   - Same hash: return the stored status and body byte for byte.
   - Different hash: `409 IdempotencyKeyReused`, nothing written.
3. Service health. When unhealthy: 503 `ServiceUnavailable` with `Retry-After`, nothing written.
4. `accounts` `FOR KEY SHARE`, then `inventories` `FOR UPDATE`. NotProvisioned gives 409. An active participation gives `409 AttemptActive`.
5. Template (latest version; `422 UnknownTemplate`) and operatives (owned and distinct, a count within the template's range; `422 UnknownOperative` or `OperativeCount`; a foreign operative is the same as an unknown one).
6. The wallet `FOR UPDATE` (no wallet gives `409 InsufficientEnergy`, never a zero), then `MaterializeLockedAsync` (Stage 6), then check and deduct. Short of energy: `409 InsufficientEnergy`, and everything rolls back. Then the `reserve:<attempt>` entry.
7. The coverage is read without locking; if active at `now`, copy the policy's terms into `attempt_insurance`.
8. The loadout snapshot, under the inventories lock.
9. Seed and salt from `IAttemptSecrets`; the attempt (`admitted`, `assign_deadline = now + BLACKGLASS_ASSIGN_DEADLINE_SECONDS`), the participant (`admitted`) and the hold (`reserved`).
10. The `deploy_requests` row with the 201 body; commit. A 23505 on `one_active_participation` maps to `409 AttemptActive`, and a 23505 on `deploy_requests`' key re-reads it and answers per step 2.

The response is `201 {attemptId, state, energy:{cost, balanceAfter}, insurance:{covered, policyId, policyVersion}}`. It never carries the seed or the salt.

- [ ] **Tests** (real PostgreSQL; names verbatim):
  - **Concurrency and idempotency:**
    - `Fifty_parallel_deploys_for_one_account_admit_once`
    - `Twenty_identical_requests_give_one_attempt_and_identical_bodies`
    - `A_reused_key_with_a_different_body_writes_nothing` (sequential, both orders; concurrent first use)
    - `Admission_drains_with_a_grant_and_a_queued_publication_without_deadlock` (with the mutation check)
  - **Refusals and atomicity:**
    - `Insufficient_energy_writes_nothing`
    - `No_wallet_is_InsufficientEnergy_not_zero`
    - `No_healthy_host_is_503_with_energy_untouched`
    - `A_foreign_or_unknown_operative_is_the_same_422`
    - `Operative_count_outside_the_template_is_422`
    - `Failure_injection_at_each_insert_rolls_back_everything` (theory over the attempt, participant, hold, ledger, insurance snapshot and deploy_requests inserts)
    - `A_lock_timeout_is_503_and_writes_nothing`
    - `A_terminated_connection_writes_nothing`
  - **Snapshots and secrets:**
    - `The_insurance_snapshot_is_copied_when_active_and_unchanged_by_later_extension_expiry_or_new_versions`
    - `An_expired_or_absent_coverage_is_not_covered`
    - `The_loadout_snapshot_matches_the_inventory_at_admission`
    - `Seed_and_salt_are_stored_and_absent_from_every_response_and_log`
    - `No_client_DTO_has_a_seed_or_salt_member` (reflection)
  - **Inventory lock:**
    - `An_admitted_account_is_InventoryLocked`
  - **Co-op shape:**
    - `Admission_refuses_more_than_one_participant` (Phase 13; there is no body field, so a test proves the store refuses a second account)
- [ ] **Commit:** `Phase 13 Stage 7: admission reserves energy, snapshots loadout and insurance, and enforces one active attempt per account`.

## Task 7: Attempt read, cancel, release and the assignment deadline

**Checkpoint 1 notes (binding):**
- **The attempt update.** `state`, `cancel_reason` and `ended_at = clock_timestamp()` go in one UPDATE.
- **Hold `updated_at`** is never set by the app; the trigger stamps it.
- **Guards.** Each of the three guarded UPDATEs (attempt `admitted`, participant `admitted`, hold `reserved`) must affect exactly one row, or the transaction throws and rolls back (with the invariant `ended_attempt_active_participant` as the net).
- **Zero amount.** A 0-amount release takes no wallet or policy lock.
- **Order.** The release takes `inventories` after the hold, never `accounts`.

**Additions from the owner's rulings (revision 2):**
- **The release lock order:** `mission_attempts` → `attempt_participants` → `energy_holds` → `inventories` (`FOR UPDATE`) → (positive amount only) `energy_wallets` → the policy lock → clock.
- **Zero-amount release:** a 0-amount hold is released with no energy step.
- **Guards.** An attempt in any state other than `admitted` is never cancelled by the worker or a player (`409 InvalidState` for the player; the worker skips it).
- **What survives a release:** the admission records (attempt, participant, snapshot, insurance snapshot, deploy request) remain.
- **Tests:**
  - `The_worker_never_cancels_an_attempt_in_a_later_state` (a fixture moves an attempt to `assigned` by direct SQL);
  - `The_deadline_boundary_is_database_time` (`assign_deadline` exactly at `now` is due; 1 µs later is not);
  - `A_release_keeps_the_admission_and_insurance_records`;
  - `A_zero_amount_release_writes_no_ledger_entry`.

**Files:**
- `src/Accounts.Persistence/Attempts/AttemptStore.cs` (read, cancel, `ReleaseLockedAsync`) and `src/Accounts.Api/Attempts/AssignDeadlineWorker.cs`;
- the routes `GET /v1/attempts/{id}` → `{state, participantState, deadlines:{assign}, energy:{held, state}, insurance:{covered}}` (404 for a foreign or unknown id; no `join` until Stage 8) and `POST /v1/attempts/{id}/cancel`;
- `/v1/me` gains `activeAttemptId` and `insurance`;
- tests `AttemptStoreTests.cs`, `AssignDeadlineWorkerTests.cs`, `AttemptApiTests.cs` (extended) and `MeEnergyApiTests` (extended).

**Release** (one transaction, in the documented order; guarded):
- The attempt goes `admitted` → `cancelled` (with `cancel_reason`), the participant goes `admitted` → `released`, and the hold goes `reserved` → `released`.
- The energy step: materialize, then credit +cost (may exceed the cap; checked arithmetic), then the `release:<attempt>` entry.
- A repeat returns the stored outcome. Releasing an attempt in any other state is `409 InvalidState`.

**Worker:** every `BLACKGLASS_ATTEMPT_SWEEP_SECONDS`, it selects attempts with `state = 'admitted' and assign_deadline <= clock_timestamp() order by assign_deadline for update skip locked limit N` and releases each in its own transaction. It never sweeps at startup. Failures are logged with the SQLSTATE only, and the next sweep still runs.

- [ ] **Tests:**
  - `Cancel_before_start_releases_once_and_restores_the_balance`
  - `A_release_above_the_cap_is_kept`
  - `Two_concurrent_cancels_release_once`
  - `A_cancel_and_the_deadline_worker_release_once`
  - `Two_workers_never_release_one_attempt_twice`
  - `The_worker_releases_only_past_the_deadline`
  - `A_failure_during_release_rolls_back`
  - `Get_attempt_is_404_for_another_account`
  - `Get_attempt_never_shows_seed_or_salt`
  - `Me_shows_the_active_attempt_and_insurance`
  - `After_release_the_account_may_deploy_again`
  - `The_worker_starts_without_sweeping_and_survives_failures`
- [ ] **Commit:** `Phase 13 Stage 7: attempt read, player cancel and the assignment deadline release holds exactly once`.

**Checkpoint 2 (independent, most capable model):** Tasks 5–7: persistence, locking, endpoints and the deadline worker.

## Task 8: Smoke and VM

- **Smoke**, on the throwaway stack, with `BLACKGLASS_DEV_HOSTS=enabled` for that section only:
  - Without a host, a deploy is `503` and the energy is unchanged.
  - The test registers a dev host and touches its heartbeat.
  - The deploy is then `201`, with energy 50 → 40.
  - Replaying the same key returns the identical body. The same key with another body is `409`.
  - An inventory edit is `409 InventoryLocked`, and `GET /v1/inventory` shows `locked: true`.
  - The cancel releases the energy (back to 50). After it, the edit succeeds.
  - No seed, salt, key or token appears in the transcript.
- **VM:** smoke, restore drill (0007 invariants) and the off-host probe.

## Task 9: Docs as built, final review, stop

- Spec as built, README (an attempts and insurance section, settings, `0007` privileges, CLI verbs), the stage plan, `CLAUDE.md` and this plan's "As built".
- Checkpoint 3 reviews the whole branch.
- Completion checks: the full suite (low-memory recipe), VM evidence, the path audit (no Unity path), and 0000–0006 byte-identical.
- No leftover `bg_t_` databases or tunnels.
- **Stop** before Stage 8, merging or pushing.

---

## Acceptance criteria

1. `0007` applies to a Stage 6 database, and 0000–0006 are unchanged. The 0000–0007 invariants hold after every test and after the restore drill.
2. **One active attempt per account.** It is enforced by `one_active_participation`: 50 parallel deploys give one attempt, one hold and one deduction.
3. **Deploy idempotency.** The same key and body give the stored response byte for byte. The same key with a different body is 409 with nothing written, sequential and concurrent.
4. **Energy.** It is reserved once at admission and released once before start, the release possibly above the cap. Every change has its ledger entry. There is no charge transition in Stage 7.
5. **Insurance.**
   - Versions are immutable.
   - Extension is `greatest(expires_at, now) + duration`.
   - A cross-tier activation is refused.
   - The activation key is honoured once.
   - The per-participant snapshot is immutable against expiry, extension and new versions.
6. **`InventoryLocked`.** It is mandatory, database-backed and fail-closed for edits and picks; reads report `locked: true`; no always-unlocked type remains in production code.
7. **Service health** fails closed: no healthy host gives 503 before any write.
8. **Seed and salt** are generated by a CSPRNG, stored, and absent from every client response, log, audit and error.
9. **Co-op shape.** The schema keeps holds, snapshots and insurance independent per participant (direct SQL), while admission refuses more than one participant.
10. **Failures.** Every injected failure, lock timeout and terminated connection leaves no partial state.
11. **No automatic retry** of admission, release or activation.
12. **Infrastructure.** No Unity path, firewall, SSH, Docker networking or exposure change, and no deployment.

## Out of scope

- Claims, assignment, launch, process sessions, tickets, the start commit and the `charge` transition (Stage 8).
- Settlement, payout, retention selection, void, quarantine and receipts (Stage 9).
- Gameplay and every Unity change.
- Enabling co-op.
- Purchases, premium currency and trading.
- The HTTP admin routes for insurance and hosts.

---

## As built (2026-10-17)

**Branch `phase13-stage7`.** The documentation baseline `e615902` is pushed; the implementation commits after it are local only. Schema version 7. 0000–0006 are byte-identical to `main` (`a9195ef`). No Unity path changed, so the Unity suites were not run.

**Measured:**
- **Final suite at `7e95c47`:** **2031/0** (Domain 629, Integration 1402; 52 m 29 s); exit 0. The later commits are docs only.
- **At `15ead9b`:** 2026/0.
- **VM at `7e95c47`:**
  - smoke 59/0 (13 Stage 7 checks), exit 0, no key, token, seed or salt in the transcript;
  - off-host probe: 5080, 5432 and 55432 closed, 22 answers;
  - restore drill 16 ok, `DRILL PASS`, 5 invariant files held at schema 7;
  - VM state identical before and after.

**Reviews:**
- **Checkpoint 1** (Tasks 1–4): 0 Critical / 1 Important / 5 Minor, all fixed. I1 was a missing invariant for an ended attempt with an active participant.
- **Checkpoint 2** (Tasks 5–7): 0 / 0 / 4. Two test gaps closed; M3 deferred to Stage 8.
- **The test round:** it found an **Important deadlock**, a release against a same-account grant: the second wallet write's foreign-key re-check took the account lock late. Fixed in `3a6d45b` by taking `accounts` `FOR KEY SHARE` first; the race test failed with 40P01 before the fix.
- **Checkpoint 3** (whole branch): **Ready to merge**, 0 / 0 / 5. The doc fixes are in `706acfe`; M5 is a Stage 8 obligation.

**Mutation checks** (each made its protecting test fail):
- removing admission's `accounts` key share (40P01);
- removing the release's `inventories` lock;
- removing the release's `accounts` key share (40P01);
- removing the after-lock idempotency re-read.

**Departures and rulings:**
- **Codes:** `RetiredOperative` (422), `AttemptNotFound` (404).
- **Zero cost:** `balanceAfter` is null for zero-cost and zero-amount operations.
- **Bodies:** responses are stored `jsonb` text, so replays are byte-identical, and a replay after release returns the admission record.
- **The activation's `startsAt`** is the start of the period that activation bought.
- **Heartbeats** are audited as `host_heartbeat_dev`.
- **Lock order:** the release takes `accounts` `FOR KEY SHARE` first. This supersedes the Checkpoint 1 note "never `accounts`".
- **Tests:** a frozen database clock is used in the exact-boundary tests only.
- **Evidence caveat:** the Checkpoint 1 suite run overlapped a fixer's build, so it counted as indicative only.

**Deferred:**
- Checkpoint 2 M3 (worker batch hardening).
- Checkpoint 3 M5 (move the seed and salt out of a CHECK-bearing table before Stage 8 adds CHECKs).

**Obligations:**
- Stage 8 and Stage 17 obligations are in spec §0.7 "As built" and the stage plan.
- Stage 8 must also verify that an assigned build can run the requested template and settings version.

**Owner feedback:** the stage took about 8 hours and too many tokens. The next stage defaults to lean execution: native work, one final review, a local test database, one full suite, and no polling.
