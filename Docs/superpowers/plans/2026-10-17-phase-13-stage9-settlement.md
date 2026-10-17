# Phase 13 Stage 9: settlement, receipts, quarantine and protective voids — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans (recommended, per the owner's lean-execution instruction) or superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

Date: 2026-10-17. **Revision 1: awaiting the owner's review. Nothing here is implemented, and no number below is a measurement.**
- **Base:** `main` @ `e65370b` (Stage 8 merged and pushed; schema version 8; backend 713 domain and 1,602 integration tests).
- **Branch:** `phase13-stage9`. It holds only this plan until the owner approves it.
- **Execution:** native, in the primary session, with two independent reviews on the most capable model:
  - Checkpoint 1, after Task 4: settlement correctness, conservation, lock order and the trust model;
  - a final whole-branch review: voids, operator recovery, concurrency, fault injection and secrecy.
- **Tests:** the VM-local runner (`Backend/tools/vm-test.sh`), with the Windows tunnel runner as the fallback.

**Goal:** a started participant's mission ends in exactly one durable, backend-computed settlement:
- a relayed manifest that passes validation settles every persistent effect in one transaction: items, XP, energy, the insurance payout and its receipt;
- an invalid manifest quarantines the participant, which stays locked under live alerts until an operator recovers it;
- a protective void, with its energy refund, happens only on positive recorded evidence or by an audited operator decision.

**Stage 9 does not:**
- write agent or Unity code. Tests drive the agent and process routes over HTTP with hand-written manifests, as in Stage 8;
- enable co-op. The schema and the transactions stay per participant;
- reconcile a void against a later valid result (Phase 14);
- add tooling to correct and resubmit a manifest (Phase 14);
- deploy anything.

**Architecture:**
- **Domain (pure, `Accounts.Domain/Settlement`).**
  - `ResultManifest`: strict parse, canonical bytes and `manifest_hash`.
  - `ManifestValidator`: V4–V17 (§F). It returns every error, with no side effects.
  - `SettlementPlanner`: from a validated manifest, the snapshot, the insurance snapshot, the retention salt and the catalogue, it computes an immutable `SettlementPlan`:
    - owned consumption;
    - loot to grant, with each minted unit's `origin_ref`;
    - stacks to add, and equipment;
    - XP per operative;
    - the insurance payout: refunds, retained lots and progress XP;
    - the expected post-settlement bags;
    - the receipt.
  - `RetentionRanker`: category priority, then the salted SHA-256.
  - `VoidPlan`: the infrastructure-void receipt.
- **Persistence.**
  - Migration `0009_settlement` (§D).
  - New stores:
    - `SettlementStore`: results, settle, quarantine, the duplicate, the conflict and the late result after a void;
    - `VoidStore`: the shared protective-void transaction;
    - `RecoveryStore`: the operator's re-validation, participant void and host-loss declaration;
    - `ReceiptStore`;
    - `MonitoringStore`: escalation, the unresponsive alert and the auto-drain.
  - `HostStore.ExitedAsync` gains the positive-evidence void.
  - `ProcessStore` gains `RecordedAsync`.
- **API.**
  - `POST /agent/v1/results` (host credential).
  - `POST /server/v1/attempts/{aid}/participants/{acc}/recorded` (process credential).
  - `GET /v1/attempts/{id}/receipt` (bearer).
  - `AttemptDeadlineWorker` gains the monitoring sweeps.
- **Admin CLI.** `attempt show`, `attempt revalidate`, `attempt void-participant`, `host declare-lost`, `anomaly list` and `anomaly review`. Each is confirmed and audited; there are no HTTP admin routes (§B, S9-B9).

**Tech stack:** .NET 10 minimal APIs, PostgreSQL 17, Npgsql and Dapper, xUnit. Analyzers treat warnings as errors.

**Spec:** `Docs/superpowers/specs/2026-10-14-phase-13-backend-design.md`:
- §6.2–§6.3, §8, §9, §10, §11.1, §11.3–§11.4, §12–§13, §15–§16 and §18–§19;
- the §0.8 Stage 9 obligations and the host-disable decision (R8).

Decisions 048–050 also apply. Task 0 adds revision 2.9. Stage plan: `Docs/superpowers/plans/2026-10-14-phase-13-backend.md` §Stage 9.

---

## A. The owner's attention points, mapped

| # | Point | Where it is met |
|---|---|---|
| 1 | Authenticated host and process evidence | §F trust model. Results need the host credential, a current and unrevoked secret, the attempt's own host and its bound session (V1–V2); the result-recorded hash needs the process credential (S9-B5); the void evidence is only the agent's post-scan exit report. Tasks 3–5 |
| 2 | Evidence from hosts later disabled or compromised | S9-B1: evidence for a revoked attempt is stored, but it never settles or voids automatically. It enters a review hold that only an operator can clear. Tasks 4, 5 and 6 |
| 3 | Original manifests; durable outbox retries | S9-B3: every received manifest is stored once, verbatim and canonical, in the insert-only `result_submissions`. Every answer is idempotent; 5xx is never definitive for the agent. Tasks 2–4 |
| 4 | Deterministic settlement and item minting | `SettlementPlanner` is pure. Minted `origin_ref`s are a function of attempt, account, line and unit. The retention selection is stored and never recomputed (S9-B11). Tasks 1 and 3 |
| 5 | Exactly-once inventory, energy and XP changes | The `participant_settlements` primary key. Minting uses plain inserts on a unique `(origin, origin_ref)`. Ledger keys are `settle:`/`void_refund:` per attempt and account; the hold has guarded edges. Everything is one transaction, with no automatic retry (S9-B10). Tasks 2, 3 and 8 |
| 6 | Immutable loot pool and objective records | Already insert-only (0008). Settlement only reads them. The running per-line totals are `pool_collections`, which only grow and never exceed the pool (trigger). Tasks 2–3 |
| 7 | Conservation checks | V8–V12 before any write. Base check V16: the inventory version is unchanged since the snapshot. After the writes, an in-transaction check that the bags equal the plan's (S9-B14). §18.3 invariants extended. Tasks 1–3 and 9 |
| 8 | Recorded seed, loot hash and layout hash | The manifest must carry the stored `layoutHash`, `lootTableVersion` and `lootHash` (V5, V5b). The seed never leaves `attempt_secrets`; only the retention salt is read, inside the transaction (S9-B4). Tasks 1, 3 and 10 |
| 9 | Participant-specific settlement | One transaction per participant. V9 sums over the attempt's already settled participants, through `pool_collections` under the attempt lock. Tests are co-op-shaped, with direct SQL. Task 3 |
| 10 | Safe protective voids from positive evidence | Exhaustive void conditions (§F). Every void path uses one `VoidStore` transaction. Tasks 5–6 |
| 11 | Operator-controlled quarantine recovery | Quarantine; escalation that never voids; re-validation of the stored original; an operator void with recorded checks; host-loss declaration. Tasks 4, 6 and 7 |
| 12 | Database lock ordering | §E; deadlock tests in Task 8 |
| 13 | Concurrency and fault-injection tests | Task 8 (races, step faults, `pg_terminate_backend`, lost responses); Task 9 (a crash drill on a disposable database container; S9-B17) |

## B. Decisions that need the owner's approval

Each comes with a recommendation and its cost if wrong.

**S9-B1.** **Evidence from a revoked attempt.** An attempt whose `credential_revoked_at` is set, by a host disable, may later receive:
- a manifest, through a re-enabled host with a rotated secret;
- an exit report.

Either is **stored as evidence and acted on by no automatic path**. A manifest is answered `202 {disposition:"held"}`: the participant moves to `quarantined` with `quarantine_reason = 'host_revoked'`, and an anomaly is raised. An exit report stores its rows, as R8 already allows, and voids nothing. Only an operator can clear the hold, by re-validating the stored manifest with `--host-trust-reviewed` and a reason, or by voiding the participant after the §10.4 checks, or through host-loss declaration.

*Recommendation:* **Yes.** Suspected compromise must neither void (owner) nor be silently trusted. Cost if wrong: an operator step for every result from a disabled host, which is rare by design.

**S9-B2.** **One commit per request.** Spec §10.3 rolls back and then writes quarantine, `rejected_results` and the anomaly in a separate transaction. Instead, the manifest is validated (pure) under the attempt and participant locks, and then one of two things happens in the same transaction:
- the quarantine records are written;
- the settlement is applied.

The duplicate, conflict and late-after-void answers write their submission row and anomaly in that one transaction too.

*Recommendation:* **Yes.** §12's rule (no endpoint makes two commits) then holds. No crash between two commits can lose a rejection. Cost if wrong: none found. Validation runs before the inventory and wallet locks, so they are not held longer.

**S9-B3.** **Original manifests are stored once, in `result_submissions`** (insert-only), replacing the sketch's `rejected_results`:
- the row is keyed `(attempt_id, account_id, manifest_hash)` and holds the canonical bytes (`bytea`, ≤ 64 KiB), the host, the process session and the first and last receipt times;
- `disposition` is one of `settled`, `quarantined`, `held`, `conflict` or `late_after_void`;
- a repeat of the same hash adds no row: it updates `last_received_at` and `receive_count` only, and nothing else in the row can change (trigger);
- `participant_settlements.manifest_hash` references the submission.

Re-validation always reads these stored bytes.

*Recommendation:* **Yes.** One table answers "what exactly did we receive, when, and from whom". Cost if wrong: a later split into two tables, which is cheap because the rows are insert-only.

**S9-B4.** **Manifest format v1 tightened:**
- integers only (`serverElapsedMs` replaces `serverElapsedSeconds`);
- `lootTableVersion` and `lootHash` added;
- canonical form: UTF-8, ordinal-sorted keys, no whitespace, minimal string escapes, the rules `CanonicalCatalogueJson` already uses;
- unknown members, duplicate keys and non-canonical UUIDs or account ids are refused (`StrictJson`);
- body cap 64 KiB (provisional).

The seed is never in a manifest or a receipt.

*Recommendation:* **Yes.** Floating point is the classic source of canonical-hash disagreements between .NET and Unity. Stage 11 shares the vectors. Cost if wrong: none for Stage 9, since the format is not yet produced by anyone.

**S9-B5.** **The process records its result:** `POST /server/v1/attempts/{aid}/participants/{acc}/recorded {manifestHash}`, sent with the process credential after the outbox `fsync`.
- It moves `joined`/`left` → `result_pending` and stores `recorded_manifest_hash` once.
- The relayed manifest's hash must equal it (V15), or the participant is quarantined.
- Without a recorded hash (the process died after its write), relay alone settles.
- An agent `exited` with `present: true` also moves the participant to `result_pending`.

*Recommendation:* **Yes.** It gives `result_pending` and `ending` a real source (§7.2), and it cross-checks the process-credential evidence against the host relay. Cost if wrong: one small route. Without it, `result_pending` is never used.

**S9-B6.** **No process-side settlement polling.** §9.2.6's 120 s poll by the process conflicts with R4: a process credential gets 401 once its attempt is settled, and a solo attempt settles with its only participant. The process tells its clients "pending" and exits. The client polls `GET /v1/attempts/{id}/receipt` itself, as §9.2.6 already requires.

*Recommendation:* **Yes.** R4 stays intact. Cost if wrong: the confirmation reaches the player from the receipt poll a moment later than from the process.

**S9-B7.** **The positive-evidence void is applied in the agent's `exited` transaction.** For each participant reported `present: false` that is started and unsettled, and has no stored submission, the void is applied in that transaction when all of these hold:
- the report comes from the attempt's host, with a current, unrevoked secret;
- the attempt's `credential_revoked_at` is null;
- the attempt has started.

`attempt_exit_reports.outcome` gains `voided`. `exited` takes `accounts` `FOR KEY SHARE` before the attempt lock (§E).

*Recommendation:* **Yes.** The evidence and its consequence commit together, so no evidence ever sits unevaluated. The agent's retries are idempotent through the stored report. Cost if wrong: an evidence-sweep worker instead, a small change.

**S9-B8.** **Host-loss declaration** (`admin host declare-lost`). It is refused unless all of these hold:
- the host is disabled with its secret revoked, so disable comes first;
- its last heartbeat is older than a provisional 10 minutes;
- `--reason` is given;
- `--outbox-unrecoverable` is given;
- `--evidence "<operator's note>"` is given.

It stamps `mission_hosts.declared_lost_at`, which is permanent: enable and rotate are refused forever, and a recovered machine gets a new host id. It then voids every started, unsettled, non-quarantined participant of the host's attempts, one transaction each, each with its `operator_actions` row under one correlation id. Quarantined participants keep their own §10.4 path. A re-run resumes idempotently.

*Recommendation:* **Yes.** It requires positive recorded evidence, never revocation alone (owner). Cost if wrong: a lost host's quarantined participants need one operator void each.

**S9-B9.** **Operator recovery is CLI-only and audited.** The verbs are:
- `admin attempt show <aid>`: states, submissions with hashes, exit evidence, actions; never a secret or a manifest's raw bytes unless `--manifest`;
- `admin attempt revalidate <aid> <acc> --reason`, plus `--host-trust-reviewed` for a `host_revoked` hold;
- `admin attempt void-participant <aid> <acc> --reason --revalidated --outbox-checked --note`;
- `admin host declare-lost …`;
- `admin anomaly list [--open]` and `admin anomaly review <id> --note`.

The spec's `/dev/v1/*` routes are dropped.

*Recommendation:* **Yes.** It follows B3's operator pattern: confirmed, audited, loopback-only by construction. Cost if wrong: HTTP routes later.

**S9-B10.** **No automatic retry** of settlement, quarantine or void transactions (the Stage 6 energy rule overrides §10.3's "loop up to 3 times"). A `40001` or `40P01` error or a lock timeout answers 503 with `Retry-After`; the agent's retry is idempotent through the stored submission and the primary key.

*Recommendation:* **Yes**, a standing rule. Cost if wrong: an agent retry about a second later.

**S9-B11.** **Minting.**
- Loot and retained instances are minted with a **plain `INSERT`** (no `ON CONFLICT`), so a repeated effect aborts the whole transaction (§10.1.4).
- `origin_ref` is `settlement:<attemptId>:<accountBase58>:c<ci>:l<line>:u<k>`, or `insurance:…` with the same tail.
- Stackables are upserted on the nulls-not-distinct `(account, operative, definition)` key.
- Granted loot goes to the collecting operative's bag (V11 has already checked capacity).
- Mission equipment is applied on success only; on any other outcome the stored equipment simply stays (§8.1, A-23).

*Recommendation:* **Yes.** Cost if wrong: none found. A replay cannot reach minting at all, because the settlement primary key is checked first.

**S9-B12.** **Payout destinations and categories.**
- Insurance refunds and retained lots go to the **stash**, so a full bag never blocks a payout.
- The policy's `Weapon`/`Armor`/`Utility`/`Consumable` map to the catalogue's lowercase categories through a fixed table. Any other category is ineligible.

*Recommendation:* **Yes.** Cost if wrong: retained lots could later go to the bag through a new policy version.

**S9-B13.** **XP.**
- Success: `missionCompletionXp` to each snapshot operative, dead included (C-2).
- Insured failure: `floor(MCX × completedRequired × percent / (totalRequired × 100))`, a single floor in 64-bit arithmetic; 0 when no objective is required.
- Experience is capped at `int.MaxValue`.
- `alive` has no persistent effect in Phase 13 and is shown in the receipt.

*Recommendation:* **Yes.** A single floor is the exact value; the spec's formula, read literally, floors twice. Cost if wrong: one XP point at the boundary.

**S9-B14.** **Base and conservation checks.**
- V16: `inventories.version` must equal the participant's snapshot `inventory_version`. Otherwise `quarantine_reason = 'inventory_base_changed'` plus an anomaly: the backend never applies effects to a changed base.
- After applying, inside the transaction, the participant's operatives' bags and equipment must equal the plan's expected bags. A mismatch aborts (500 and a deduplicated anomaly in a fresh transaction) and changes nothing.

*Recommendation:* **Yes.** Cost if wrong: a quarantine in a case that "cannot happen".

**S9-B15.** **Energy edges and keys.**
- Hold edges: `charged → settled` and `charged → refunded`.
- Ledger entries: `settle:<attemptId>` (delta 0, positive holds only, as B9) and `void_refund:<attemptId>` (delta `+amount`, positive only).
- These keys are tightened to the exact attempt form, like `reserve`/`release`/`charge`.
- A zero-cost hold moves with no entry.

*Recommendation:* **Yes**, it follows Stage 8's pattern. Cost if wrong: none.

**S9-B16.** **State edges added.**
- Participants: `joined`/`left` → `settled`, so a void, or a relay without `recorded`, settles directly. `quarantined` → `quarantined` changes only the escalation columns.
- Attempts: none new. A last settlement from `running` writes `running → ending → settled` in one transaction; from `unresponsive`, `settled` directly.
- A settled attempt's slot still waits for its exit evidence (B6).

*Recommendation:* **Yes.** Cost if wrong: none.

**S9-B17.** **The "kill PostgreSQL under load" test** conflicts with B13: outage tests must never stop the shared test container. Proposal: `Backend/tools/vm-crash-drill.sh` runs outside the runner, like the smoke test.
- It starts a **throwaway** `blackglass-crashdb` container (own volume, no published port, the test cluster's settings) on `bg-dev-services01` and migrates it.
- It drives settlements at it from the runner image, `docker kill`s it mid-load, restarts it, and runs the invariant queries.
- It removes the container and its volume.

*Recommendation:* **Yes.** The shared cluster is never touched. Cost if wrong: the drill could instead run against the restore-drill container.

**S9-B18.** **Monitoring:**
- an `unresponsive` alert at 10 minutes, repeated (provisional);
- quarantine escalation at 24 h, repeated every 24 h;
- anomaly kinds: `result_quarantined`, `result_held`, `settlement_conflict`, `late_result_after_void`, `infra_void`, `quarantine_escalation`, `unresponsive_attempt`, `catalogue_unavailable`, `host_auto_drained`;
- **host auto-drain** when a host's infrastructure voids reach a provisional 3 within 60 minutes. Auto-drain only drains, which is the safe, planned-maintenance operation; an operator re-enables.

*Recommendation:* **Yes**, it is spec §6.3 layer 3 and small. Alternative: defer the auto-drain to Phase 14.

**S9-B19.** **The agent's answers are minimal:** `200 {disposition:"settled"|"duplicate", outcome}`, `202 {disposition:"held"|"quarantined"}`, or `409 {code}`. The full receipt goes only to the participant's own client.

*Recommendation:* **Yes.** It discloses less to the semi-trusted host. Cost if wrong: none.

**S9-B20.** **A manifest for an attempt admitted under another catalogue than the API's** answers `503 CatalogueUnavailable` with `Retry-After` and a deduplicated anomaly. It is never definitive and never judged against the wrong catalogue. The result waits for an API with that catalogue (multi-catalogue support is Phase 14).

*Recommendation:* **Yes.** A 409 would make the agent stop relaying a possibly valid result. Cost if wrong: a stuck relay that an operator can see in the anomalies.

## C. API contracts (Stage 9)

**`POST /agent/v1/results`** (Host scheme). The body is one manifest (§9.1 as amended by S9-B4).

| Status | Body | Agent relay state (Stage 10) |
|---|---|---|
| 200 | `{disposition:"settled", outcome}` (applied) or `{disposition:"duplicate", outcome}` (the same hash was already settled) | `done` |
| 202 | `{disposition:"quarantined", errors:[code…]}` (V4–V17 failed; the submission is stored) or `{disposition:"held"}` (S9-B1) | `rejected` or `held` (definitive) |
| 400 | the body is not a manifest (parse or size) | `rejected` (definitive; anomaly) |
| 401 | the uniform unauthenticated answer: unknown, foreign or revoked host secret, a disabled host | retry with backoff (not definitive) |
| 409 | `WrongAttempt` (not this host's attempt, V1; or the wrong session, V2); `InvalidState` (participant `admitted` or `released`, or attempt not `running`/`ending`/`unresponsive`); `Conflict` (a different hash was already settled); `AlreadySettledAsVoid` | `conflict` (definitive; anomaly) |
| 503 | outage, lock timeout, serialization failure (`Retry-After`), `CatalogueUnavailable` | retry (not definitive) |

Quarantine answers 202, not the spec's 422, because the submission was accepted and stored; the agent must not resend it. The errors are codes only, with no echo of the body.

**`POST /server/v1/attempts/{aid}/participants/{acc}/recorded`** (Process scheme). The body is `{manifestHash:"sha256:<hex>"}`.
- `200 {state:"result_pending"}`, also on an identical repeat.
- `409 ConflictingReport` for a different hash.
- `409 InvalidState` when the participant has not started, or is settled or quarantined.
- 401 when revoked or ended (R4/R8).

**`GET /v1/attempts/{id}/receipt`** (Bearer).
- `200` returns the stored receipt bytes verbatim.
- `202 {state}`, where `state` is:
  - `running` (participant `admitted`, `joined` or `left`);
  - `result_pending`;
  - `unresponsive` (attempt `unresponsive`, participant unsettled);
  - `under_review` (`quarantined`, either reason).
- `404 AttemptNotFound` for an unknown, foreign or non-canonical id.
- `409 NoReceipt` for a released participant.

**Receipt v1** (§10.5, extended):
- `receiptVersion`, `attemptId`, `accountId` (Base58), `outcome`, `settledAt`;
- `energy {cost, state, refunded}`;
- `consumed[]`, `granted[]` (with minted item ids, in Base58), `xp[]`, `operatives[{operativeId, alive}]`;
- `insurance {covered, policyId, policyVersion, refunds[], retained[], xp[]}` or `{covered:false}`;
- `inventoryVersion`.

It never contains the seed, salt, manifest bytes, host secrets or credential material.

## D. Migration `0009_settlement`

`0000`–`0008` stay byte-identical. The objects, in creation order:
1. **`result_submissions`.**
   - Columns: `attempt_id`, `account_id` (FK to the participant), `manifest_hash bytea(32)`, `manifest bytea` (canonical, 1..65536 bytes), `host_id`, `process_session_id` (FK `(attempt_id, process_session_id)`), `disposition`, `errors text[]`, `first_received_at`, `last_received_at`, `receive_count`.
   - PK `(attempt_id, account_id, manifest_hash)`.
   - Trigger: only `last_received_at`/`receive_count` may change, upward; no delete or truncate.
2. **`participant_settlements`.**
   - Columns: PK `(attempt_id, account_id)` with FK to the participant; `outcome`; `manifest_hash` (null iff `infra_void`), with FK `(attempt_id, account_id, manifest_hash)` to `result_submissions`; `void_basis` (`exit_evidence`, `operator_quarantine` or `host_declared_lost`; non-null iff `infra_void`); `retention_selection jsonb`; `receipt jsonb`; `settled_at`.
   - Immutable; no truncate.
3. **`pool_collections`.**
   - PK `(attempt_id, container_index, line)`, with FK to `attempt_loot_pool`; `collected > 0`.
   - Trigger: `collected` only grows and stays ≤ the pool line's quantity; no delete.
4. **Participants.**
   - New columns: `quarantined_at`, `escalate_at`, `quarantine_reason` (`invalid_manifest`, `host_revoked` or `inventory_base_changed`) and `recorded_manifest_hash`.
   - CHECK: the quarantine columns are set iff the state is `quarantined`, except that `quarantined_at` and `quarantine_reason` stay after a settlement.
   - `recorded_manifest_hash` is written once.
   - The transition function is replaced, adding the S9-B16 edges.
5. **Holds.** The transition function is replaced, adding `charged → settled` and `charged → refunded`.
6. **Hosts.** `declared_lost_at`, permanent. A trigger refuses `enabled = true` and secret rotation once it is set.
7. **Exit reports.** `attempt_exit_reports.outcome` gains `voided`; the CHECK is replaced by name.
8. **Ledger.** The key CHECK is replaced: `settle` and `void_refund` keys must be exactly `<kind>:` plus the entry's `attempt_id`.
9. **`operator_actions`.**
   - Kept: `revalidate`, `participant_void` and `host_declared_lost`, which are already listed.
   - Added: `anomaly_review`.
10. **Privileges.**
    - Column-level INSERT on the new tables.
    - UPDATE of exactly the columns each path writes.
    - No DELETE, and no privilege on the identity sequences.
11. **Invariants (§18.3 additions):**
    - every settled participant has exactly one settlement row, and every settlement row's participant is settled;
    - a settled hold has a `settle:` entry iff its amount is > 0;
    - a refunded hold has a `void_refund:` entry of `+amount` iff its amount is > 0;
    - every non-void settlement references a submission with `disposition = 'settled'`;
    - every `pool_collections` row is ≤ its pool line and equals the sum of the settled manifests' collected quantities;
    - every `settlement:`/`insurance:` minted item matches a `granted`/`retained` entry in its receipt;
    - no quarantined participant lacks an outstanding or reviewed `result_quarantined`/`result_held` anomaly;
    - a settled attempt has no unsettled, unreleased participant.

## E. Lock orders (spec §15.3, extended)

| Transaction | Order |
|---|---|
| **Settle** (`results`, valid) | `accounts` `FOR KEY SHARE` (the participant's account) → `mission_attempts` `FOR UPDATE` → host row: plain read only (V1: enabled, secret current, not revoked; no lock, as R7 requires) → `attempt_participants` `FOR UPDATE` → `participant_settlements`/`result_submissions` read → validate (pure) → `energy_holds` `FOR UPDATE` → `inventories` `FOR UPDATE` → item and operative rows (covered by the inventory lock) → `pool_collections` (covered by the attempt lock) → `energy_wallets` `FOR UPDATE` → the energy policy lock (shared) → clock → ledger → settlement row → participant and attempt state |
| **Quarantine, hold, duplicate, conflict, late after void** | `accounts` `FOR KEY SHARE` → `mission_attempts` → `attempt_participants` → submission row and anomaly inserts → participant state. No inventory, wallet or policy lock |
| **Recorded** (process) | `mission_attempts` → `attempt_participants`. No accounts lock is needed: the process path writes no account-referencing row |
| **Exited with void** (S9-B7) | Participants' account ids read unlocked (immutable since admission) → `accounts` `FOR KEY SHARE` ascending → `mission_attempts` → host secret plain read → `attempt_participants` → per voided participant, the void order below → exit report rows |
| **Void** (shared by evidence, operator and declare-lost) | `accounts` `FOR KEY SHARE` → `mission_attempts` → `attempt_participants` → `energy_holds` → `inventories` (version bump only) → (positive amount) `energy_wallets` → the policy lock → clock → `void_refund` entry → settlement row → states |
| **Declare-lost** | The host row `FOR UPDATE` (stamp, audit), committed alone; then one void transaction per participant. The host lock is never held together with an attempt lock, so it never runs in the reverse order to the claim (R7) |
| **Monitoring sweeps** | Attempt or participant rows `FOR UPDATE SKIP LOCKED` → anomaly insert (`accounts` `FOR KEY SHARE` through the FK, ascending, taken first). Auto-drain: the host row `FOR UPDATE` alone |

Rules carried forward:
- one global unique key per transaction after the policy lock (Stage 9 adds none; `settle:`/`void_refund:` are unique per account);
- any transaction that writes account-owned rows takes `accounts` `FOR KEY SHARE` first;
- no energy-touching transaction is retried automatically.

## F. Validation and the trust model

**Who may say what:**

| Evidence | Credential | Accepted as | Never accepted as |
|---|---|---|---|
| Manifest | Host secret: current, not revoked; the attempt's own host; the bound session | facts to validate (V4–V17), then settle | trusted without validation; settled if the attempt was revoked (S9-B1) |
| Result-recorded hash | Process credential: current generation, not revoked, attempt live | `result_pending`; the hash the relay must match | a settlement |
| Exit report `present: false` after the outbox scan | Host secret, as above; attempt not revoked; attempt started | positive evidence of absence: a void (S9-B7) | evidence when the attempt was revoked (stored only) |
| Process `failed` after start | Process credential | nothing (409, as in Stage 8) | evidence of absence |
| Lease lapse, outage, restart, timers | none | an alert | a void |
| Host disable or credential revocation | operator | isolation | a void (owner) |

**Exhaustive void conditions** (§11.1, restated):
1. S9-B7's exit evidence.
2. S9-B8's host-loss declaration.
3. An operator void of a quarantined participant (§10.4), including a `host_revoked` hold.

Nothing else voids.

**Validation (V1–V3 answer 401/409 and write nothing; V4–V17 quarantine):**
- V4: build and catalogue equal the session's.
- V5: `layoutHash` equals the stored one.
- V5b: `lootTableVersion` and `lootHash` equal the stored ones.
- V6: the operatives are exactly the snapshot's, in slot order.
- V7: the objective ids equal the registered set; states are `active`, `completed` or `failed`; `success` requires every required objective completed.
- V8: owned consumption.
- V9: per-line collection across settled participants.
- V10: conservation.
- V11: bag capacity (`InventoryRules.BagEntries` ≤ `inventory.bagCapacity`).
- V12: equipment.
- V13: plausibility. The participant's `started_at` ≤ the receipt time; `serverElapsedMs` ≤ the hard cap plus slack, provisionally 2 h 5 min, measured from the attempt's `started_at` to now plus a provisional 60 s tolerance.
- V14: integrity is `ok`.
- V15: the hash equals `recorded_manifest_hash` when one is set.
- V16: the inventory base is unchanged (checked under the inventory lock, so it follows the pure validation).
- V17: the outcome is one of the five manifest outcomes. `infra_void` is backend-only.

## Global Constraints

- Keep `0000`–`0008` byte-identical. One new migration, `0009_settlement`. Schema version 9.
- Mission attempt ids stay UUIDv7. Account ids are `bigint` inside the database and canonical Base58 outside. Item and operative ids are `bigint`, Base58 outside.
- Never commit secrets. Never print tokens, host secrets, process credentials, join tickets, seeds, retention salts or administrator keys in logs, database errors, transcripts, receipts, anomaly details or audit rows. Never preserve plaintext test credentials in evidence files.
- Tests create and drop only `bg_t_*` databases on the `blackglass-test` cluster (verify `cluster_name`). Outage tests take only their own database offline. The crash drill (S9-B17) uses its own throwaway container.
- No energy-touching or inventory-mutating transaction is retried automatically.
- Every timer, threshold and size limit here is provisional configuration (spec §19), with an environment variable and a default.
- No change to SSH, firewall, Docker daemon networking, the mission VM or the live accounts stack. No deployment.
- Touch only `Backend/`, `Docs/` and `CLAUDE.md`. Keep the owner's `Assets/Settings/Mobile_RPAsset.asset`, `ProjectSettings/URPProjectSettings.asset` and `.claude/` uncommitted and unchanged; stage by explicit paths only, never `git commit -a`.
- Visibility policy and the backend core stay free of networking-library types.

## Review Focus

1. **A manifest whose numbers are valid individually but whose provenance does not add up.** Examples: loot consumed from a line the operative never collected; an owned non-stackable held by another operative; a stack split across two `held` entries to dodge V11. The expected behaviour is quarantine, never a partial settlement. Task 1 pins each case with a vector.
2. **The same relay arriving through two connections at once while the operator revalidates** (three-way race). Exactly one settlement, the others answering `duplicate`; one `revalidate` action recorded with its result. Task 8.
3. **A host disabled between a manifest's authentication and its transaction.** The in-transaction secret re-check returns the uniform 401 and writes nothing (the R8 pattern). Task 3 adds the store-level test; Task 8 races it.
4. **An API restarted with a new catalogue while results wait.** Every result gets 503 `CatalogueUnavailable`; none is judged against the wrong catalogue; one deduplicated anomaly. Task 3.
5. **A manifest close to the size cap, or with deep nesting or long strings.** It is refused with 400 before any lock and is never logged verbatim. Task 1 (parser) and Task 3 (route).

---

## Task 0 (docs first, after the owner's approval): record the rulings

**Files:**
- Modify: `Docs/superpowers/specs/2026-10-14-phase-13-backend-design.md` (§0.9, revision 2.9: the approved S9-B rulings; the §9.1 manifest, §9.2.6, §10.2–§10.5 and §16.2–§16.4 amendments by reference)
- Modify: `Docs/superpowers/plans/2026-10-14-phase-13-backend.md` (§Stage 9 status and touches)
- Modify: `CLAUDE.md` (Stage 9 authorized)
- Modify: this plan (revision 2 with the owner's rulings)

- [ ] **Step 1:** Write §0.9 with the rulings table, exactly as the owner approves it.
- [ ] **Step 2:** Commit docs only, by explicit paths.

## Task 1: Pure settlement rules (domain)

**Files:**
- Create: `Backend/src/Accounts.Domain/Settlement/ResultManifest.cs`, `ManifestValidator.cs`, `SettlementPlanner.cs`, `RetentionRanker.cs`, `Receipt.cs`, `CanonicalJson.cs` (extracted from `CanonicalCatalogueJson` and reused by it, with no behaviour change: its existing tests must stay green)
- Test: `Backend/tests/Accounts.Domain.Tests/Settlement/*.cs`, `Backend/tests/Accounts.Domain.Tests/Settlement/Vectors/*.json` (shared vectors for Stage 11)

**Interfaces (produced):**
```csharp
public sealed record ManifestOperative(long OperativeId, bool Alive, IReadOnlyList<Consumed> Consumed,
    IReadOnlyList<Held> Held, IReadOnlyList<Collected> Collected, IReadOnlyList<EquipmentRef> Equipment);
public sealed record ResultManifest(Guid AttemptId, long AccountId, Guid ProcessSessionId, string BuildRevision,
    string CatalogueVersion, string Integrity, string Outcome, long OutcomeSequence, long ServerElapsedMs,
    string LayoutHash, int LootTableVersion, string LootHash, IReadOnlyList<ObjectiveState> Objectives,
    IReadOnlyList<ManifestOperative> Operatives)
{
    public static ParsedManifest Parse(ReadOnlySpan<byte> utf8Json);   // ManifestFormatException on any violation; cap 65536 bytes
}
public sealed record ParsedManifest(ResultManifest Manifest, byte[] CanonicalBytes, byte[] Hash);   // Hash = SHA-256(CanonicalBytes)

public sealed record SettlementContext(LoadoutSnapshot Snapshot, AttemptFacts Attempt, IReadOnlyList<PoolLine> Pool,
    IReadOnlyList<Objective> Objectives, IReadOnlyDictionary<(int Ci, int Line), int> SettledCollections,
    GameCatalogue Catalogue, byte[]? RecordedHash);
public static class ManifestValidator { public static IReadOnlyList<string> Validate(ParsedManifest m, SettlementContext c); }  // codes "V4".."V17" + detail word, sorted, distinct

public sealed record SettlementPlan(string Outcome, IReadOnlyList<OwnedConsumption> ConsumedOwned,
    IReadOnlyList<MintUnit> Mint, IReadOnlyList<StackGrant> Stacks, IReadOnlyList<EquipChange> Equip,
    IReadOnlyList<XpGrant> Xp, InsurancePayout? Insurance, IReadOnlyList<ExpectedBag> ExpectedBags,
    IReadOnlyDictionary<(int Ci, int Line), int> Collections);
public static class SettlementPlanner
{
    public static SettlementPlan Plan(ResultManifest m, SettlementContext c, InsuranceTerms? terms, byte[] retentionSalt);
}
public static class RetentionRanker { public static IReadOnlyList<RetainedLot> Select(...); }   // stored, never recomputed on replay
public static class ReceiptWriter { public static string Settled(...); public static string Void(...); }   // deterministic JSON
```

- [ ] **Step 1: Write the failing tests.**
  - Parser: valid vectors parse. Each of these is refused:
    - unknown member;
    - duplicate key;
    - float;
    - negative or overflowing integer;
    - non-canonical UUID;
    - non-canonical Base58;
    - over 64 KiB;
    - nesting deeper than the format allows;
    - `infra_void` outcome.
  - The same manifest with different whitespace and key order gives the same canonical bytes and hash.
  - Validator: one test per V-rule, each with a minimal failing vector and the expected code:
    - V8: consuming a non-consumable; consuming more than the snapshot holds; loot consumption from a line not collected;
    - V9: summed with the settled collections;
    - V10: each of its three equations, and a snapshot non-stackable held by another operative;
    - V11: via `BagEntries`;
    - V12: wrong slot, two in one slot, a reference to an item not held;
    - V13: elapsed over the cap; `started_at` in the future;
    - V15: a mismatch, and an absent recorded hash (passes).
  - Planner:
    - success grants loot to the collector; mints `quantity` units for a non-stackable line with refs `…:u0`, `…:u1`;
    - consumption plans delete-or-decrement per (operative, definition);
    - failure without insurance: consumption only;
    - insured defeat: `floor` refund to the stash; retention capped at `maxEntries`/`maxUnitsPerEntry` and ranked by category, then salted hash; progress XP with a single floor; 0 when none required;
    - `alive: false` still gets XP on success;
    - retention is identical across 3 calls, and changes when the salt changes;
    - every receipt field is deterministic, and the receipt never contains the salt (byte search).
  - Run: `dotnet test Backend/tests/Accounts.Domain.Tests --filter "FullyQualifiedName~Settlement"`. Expected: FAIL (types missing).
- [ ] **Step 2: Implement** the parser (on `StrictJson`), canonicalization (extracted `CanonicalJson`), validator, planner, ranker and receipt writer. The ranking key is `SHA-256(salt ‖ accountId as 8 bytes big-endian ‖ ci as 4 bytes ‖ line as 4 bytes)`, compared bytewise.
- [ ] **Step 3: Run** the domain suite. Expected: PASS. The `CanonicalCatalogueJson` tests are unchanged and green.
- [ ] **Step 4: Commit** (`Backend/src/Accounts.Domain/Settlement`, `Backend/tests/Accounts.Domain.Tests/Settlement`, `CanonicalCatalogueJson.cs`).

## Task 2: Migration `0009_settlement`, invariants and privileges

**Files:**
- Create: `Backend/db/migrations/0009_settlement.sql`
- Modify: `Backend/src/Accounts.Persistence/Migrations/*` (expected schema version 9), `Backend/tools/invariants.sql` (or wherever §18.3 lives)
- Test: `Backend/tests/Accounts.Integration.Tests/Database/SettlementSchemaTests.cs`, `SettlementPrivilegeTests.cs`, `MigratorTests.cs` (upgrade from a populated schema 8)

- [ ] **Step 1: Write the failing tests.**
  - Each §D object exists.
  - The settlement row is immutable; submissions change only their receive columns, upward.
  - `pool_collections` refuses decreasing and over-pool values.
  - The hold refuses `reserved → settled` and `released → refunded`, and accepts `charged → settled`/`refunded`.
  - Participant edges: `joined → settled` allowed; `admitted → settled` refused.
  - `declared_lost_at` is permanent and refuses enable and rotation.
  - Ledger keys `settle:`/`void_refund:` refuse any other attempt id.
  - Exit reports accept `voided`.
  - Privilege lists match exactly (sorted).
  - Upgrade: a populated schema-8 database migrates and its invariants hold.
  - The §18.3 additions return zero rows on a fresh database and on seeded fixtures.
  - Every constraint violation's message and DETAIL, read from the client exception, contains no seed, salt or credential bytes.
  - Expected: FAIL.
- [ ] **Step 2: Write the migration** in §D's order.
- [ ] **Step 3: Run** the schema, privilege, migrator and invariant tests. Expected: PASS. `0000`–`0008` are byte-identical (the migrator's checksum test).
- [ ] **Step 4: Commit.**

## Task 3: Settlement store, the results route and receipts

**Files:**
- Create: `Backend/src/Accounts.Persistence/Settlement/SettlementStore.cs`, `ReceiptStore.cs`, `SettlementSql.cs`; `Backend/src/Accounts.Api/Hosts/ResultEndpoints.cs`; the receipt route in `Backend/src/Accounts.Api/Attempts/AttemptEndpoints.cs`
- Test: `Backend/tests/Accounts.Integration.Tests/Database/SettlementTests.cs`, `ReceiptTests.cs`, `Support/ManifestKit.cs` (builds valid manifests from a `MissionKit` attempt brought to `running`)

**Interfaces:**
- Consumes: Task 1's parser, validator, planner and receipt writer; Stage 8's `HostStore` authentication (`HostSecretHashOf`) and `MissionKit`.
- Produces:
  ```csharp
  public abstract record ResultOutcome
  {
      public sealed record Settled(string Outcome) : ResultOutcome;
      public sealed record Duplicate(string Outcome) : ResultOutcome;
      public sealed record Quarantined(IReadOnlyList<string> Errors) : ResultOutcome;
      public sealed record Held : ResultOutcome;
      public sealed record Refused(string Code) : ResultOutcome;    // WrongAttempt | InvalidState | Conflict | AlreadySettledAsVoid
      public sealed record Unauthenticated : ResultOutcome;
      public sealed record CatalogueUnavailable : ResultOutcome;
  }
  Task<ResultOutcome> SubmitAsync(string hostId, byte[] secretHash, ParsedManifest manifest, CancellationToken ct);
  Task<ReceiptView> ReceiptAsync(long accountId, Guid attemptId, CancellationToken ct);
  ```

- [ ] **Step 1: Write the failing tests.**
  - Each outcome settles once: `success`, `defeat`, `abandoned`, `disconnected`, `expired`.
  - Consumption deletes an exactly consumed stack and decrements a partially consumed one; nothing ever writes `quantity = 0`; insufficient stock aborts with nothing written.
  - Non-stackable line ×2 mints two items with the expected refs; a replay mints none.
  - Insured defeat writes refunds and retained lots to the stash and stores the selection; uninsured defeat applies consumption only.
  - XP lands, capped.
  - The hold becomes `settled`, with one `settle:` entry for a positive hold and none for zero cost.
  - The inventory version is bumped once.
  - `pool_collections` sums across two participants (co-op-shaped, with direct SQL).
  - Attempt states: last participant `running → ending → settled`, `unresponsive → settled`.
  - The participation lock is released only by the settlement.
  - Duplicate: the same hash returns `Duplicate` with nothing changed; `receive_count` is 2.
  - Conflict: a different hash returns `Conflict`, with the submission stored as `conflict` and an anomaly.
  - Wrong host or session returns `WrongAttempt`. A disabled host, or a secret rotated between authentication and the transaction, returns `Unauthenticated` with nothing written.
  - A participant in `admitted` or `released` gets `InvalidState`.
  - Catalogue mismatch returns `CatalogueUnavailable` with one deduplicated anomaly.
  - V16: a changed inventory base quarantines.
  - The post-apply bag check aborts on a corrupted plan (a test-only plan mutation through an internal seam in the test assembly).
  - Receipts: 202 per state; 200 byte-identical across calls, across an API restart and across a duplicate relay; 404 for a foreign account; 409 for a released participant.
  - HTTP: the 200/202/400/401/409/503 bodies as in §C, with no echo of the body.
  - Expected: FAIL.
- [ ] **Step 2: Implement** `SubmitAsync` in §E order:
  1. authenticate, then lock `accounts` `FOR KEY SHARE`, then the attempt;
  2. re-check the host secret by plain read;
  3. lock the participant;
  4. look up the existing settlement or submission;
  5. on a catalogue mismatch, answer 503;
  6. build the context (pool, objectives, `pool_collections`, the recorded hash, the snapshot);
  7. validate; on errors, take the quarantine path (Task 4);
  8. lock the hold and the inventory and check V16;
  9. read the salt from `attempt_secrets`;
  10. apply the plan;
  11. lock the wallet, take the policy lock, materialize and write the ledger entry;
  12. insert the submission (`settled`) and the settlement; update the participant and attempt states;
  13. run the post-apply bag check, then commit.

  Map `40001`/`40P01`/`55P03`/`57014` to 503, with no retry.
- [ ] **Step 3: Run** the settlement and receipt classes, then the whole integration suite. Expected: PASS.
- [ ] **Step 4: Commit.**

## Task 4: Quarantine, the review hold and `recorded`

**Files:**
- Modify: `SettlementStore.cs`, `Backend/src/Accounts.Persistence/Attempts/ProcessStore.cs`, `Backend/src/Accounts.Api/Hosts/ProcessEndpoints.cs`
- Test: `Database/QuarantineTests.cs`, `Database/RecordedTests.cs`

- [ ] **Step 1: Write the failing tests.**
  - Each V4–V17 failure stores the submission verbatim as `quarantined` with its error codes, moves the participant to `quarantined` (`invalid_manifest`, `escalate_at = now + 24 h`) and raises `result_quarantined`. The inventory, wallet, hold and items are untouched, and the lock is kept.
  - A later valid manifest from `quarantined` settles normally.
  - A second invalid manifest adds a submission and keeps the state.
  - A revoked attempt's manifest (a disable, then rotate and enable, then relay) is `Held`: `host_revoked`, `result_held`, and nothing applied even when the manifest is valid.
  - `recorded`: `joined → result_pending` with the hash stored; an identical repeat gets 200; a different hash gets 409; revoked or ended gets 401.
  - A relay whose hash differs from the recorded one quarantines (V15).
  - When every participant is `result_pending` or settled, the attempt is `ending`.
  - Expected: FAIL.
- [ ] **Step 2: Implement** the quarantine and hold writes in the same transaction (S9-B2), and `RecordedAsync` (§E).
- [ ] **Step 3: Run** the affected classes and the suite. Expected: PASS.
- [ ] **Step 4: Commit.**

### Checkpoint 1: independent review (most capable model)

Scope: Tasks 1–4. Conservation and provenance; minting; the energy edges; lock order against §E and Stage 6–8 rules; the trust model (§F, S9-B1); secrecy of the salt and of the manifests. Critical and Important findings are fixed test-first before Task 5. Minor findings are recorded.

## Task 5: The positive-evidence void and the late result after a void

**Files:**
- Create: `Backend/src/Accounts.Persistence/Settlement/VoidStore.cs`
- Modify: `Backend/src/Accounts.Persistence/Hosts/HostStore.cs` (`ExitedAsync`), `Backend/src/Accounts.Persistence/Attempts/ExitReports.cs`
- Test: `Database/EvidenceVoidTests.cs`

**Interfaces:**
```csharp
// Locks and order per §E; the caller holds accounts KEY SHARE and the attempt lock.
Task<VoidResult> VoidParticipantLockedAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid attemptId, long accountId,
    string basis, CancellationToken ct);
```

- [ ] **Step 1: Write the failing tests.**
  - `exited {present:false}` for a started participant voids it:
    - hold `refunded`, with one `void_refund:` entry of `+amount` (none for zero cost);
    - wallet balance restored;
    - nothing consumed, no loot, no XP, insurance unused;
    - equipment unchanged; inventory version bumped; lock released;
    - receipt `infra_void`; anomaly `infra_void` with host, build and exit code;
    - exit report `outcome = 'voided'`.
  - `present:true` gives `result_pending` and no void.
  - Mixed presence across two participants (co-op-shaped) voids only the absent one.
  - A repeat exit report is idempotent; a conflicting one is `ConflictingReport` and voids nothing.
  - A revoked attempt (disable, rotate, enable, `exited`) stores evidence and voids nothing.
  - A participant with a stored submission is not voided.
  - A late result after a void gets `AlreadySettledAsVoid`: the submission is stored as `late_after_void`, an anomaly is raised, and the receipt is byte-identical.
  - An `unresponsive` attempt with no evidence is never voided by the worker (a sweep runs; the state is unchanged).
  - A process `failed` after a start is still 409.
  - Expected: FAIL.
- [ ] **Step 2: Implement** `VoidStore`, and `ExitedAsync`'s new lock order and void branch.
- [ ] **Step 3: Run** the class and the suite. Expected: PASS.
- [ ] **Step 4: Commit.**

## Task 6: Operator recovery verbs

**Files:**
- Create: `Backend/src/Accounts.Persistence/Settlement/RecoveryStore.cs`, `Backend/src/Accounts.Persistence/Settlement/AnomalyStore.cs`
- Modify: `Backend/src/Accounts.Api/Admin/AdminCommand.cs`, `AdminArguments.cs`, `Backend/README.md` (the operator runbook: quarantine recovery, host loss)
- Test: `Database/RecoveryTests.cs`, `Database/RecoveryCliTests.cs`

- [ ] **Step 1: Write the failing tests.**
  - `revalidate`:
    - re-runs V1-free validation (the host is not re-authenticated: this is the operator path) on the stored bytes of the participant's latest quarantined submission;
    - on a pass, it settles through the same transaction code as Task 3, and the `revalidate` action records the hash and `settled`;
    - on a failure, it records `failed` with the codes, and the state is unchanged;
    - a `host_revoked` hold requires `--host-trust-reviewed`, or it exits 64.
  - `void-participant`:
    - refused without `--reason`, `--revalidated` (a `revalidate` action after the latest submission must exist), `--outbox-checked` and `--note`;
    - refused for a participant that is not quarantined;
    - otherwise voids with basis `operator_quarantine`, with the action and its checks in the same transaction.
  - `declare-lost`:
    - refused for an enabled host, a host with an unrevoked secret, a fresh heartbeat, or a missing flag;
    - otherwise stamps the host and voids every started, unsettled, non-quarantined participant, one action each under one correlation id;
    - a re-run resumes; enable and rotate are refused forever afterwards;
    - quarantined participants are untouched.
  - `anomaly list/review` works, and review is audited.
  - `attempt show` prints no secret, salt or credential and no raw manifest without `--manifest`.
  - Every verb asks for confirmation (exit 64 without `--yes` in a non-interactive run, matching the existing verbs).
  - A failure partway through (an injected fault) leaves no partial state for that participant.
  - Expected: FAIL.
- [ ] **Step 2: Implement.**
- [ ] **Step 3: Run** the classes and the suite. Expected: PASS.
- [ ] **Step 4: Commit.**

## Task 7: Monitoring sweeps

**Files:**
- Create: `Backend/src/Accounts.Persistence/Settlement/MonitoringStore.cs`
- Modify: `Backend/src/Accounts.Api/Attempts/AttemptDeadlineWorker.cs`, `AttemptSettings.cs` (provisional values)
- Test: `Database/MonitoringTests.cs`

- [ ] **Step 1: Write the failing tests.**
  - Escalation:
    - at `escalate_at`, one `quarantine_escalation` anomaly is raised and `escalate_at` moves forward 24 h;
    - repeats are deduplicated per period;
    - **the state, holds, inventory and lock never change**, through 3 periods simulated with database time.
  - Unresponsive alert: at 10 min, repeated per period; no state change.
  - Auto-drain:
    - the third infrastructure void on a host within 60 min drains it and raises `host_auto_drained`;
    - its running attempts and process credentials keep working (drain semantics);
    - re-enabling stays an operator action.
  - Two workers race and the outcome is still one anomaly per period.
  - Expected: FAIL.
- [ ] **Step 2: Implement.**
- [ ] **Step 3: Run** the class and the suite. Expected: PASS.
- [ ] **Step 4: Commit.**

## Task 8: Concurrency and fault injection

**Files:**
- Test: `Database/SettlementConcurrencyTests.cs`, `Database/SettlementFaultTests.cs`, `Support/FaultTriggers.cs` (test-only: superuser-installed triggers in the test database that raise on a named table and operation, plus a pausing trigger that waits on an advisory lock; no production seam)

- [ ] **Step 1: Write the tests.** Each must fail against a deliberately broken variant before it passes, which proves it can fail. Record which variant was used.
  - **20 identical manifests in parallel:** one `Settled` and 19 `Duplicate`; one settlement, one `settle:` entry, the minted items once; invariants hold.
  - Two different manifests in parallel: one settles; the other gets `Conflict`.
  - Settlement racing `exited{present:false}`: exactly one wins (settled with the real outcome, or voided); the loser gets `Duplicate`/`AlreadySettledAsVoid`, or a stored `voided`/`none` report; invariants hold; 50 rounds.
  - Settlement racing `host disable`: either it commits before the disable, or it is `Unauthenticated` after it; there is never a settlement after the disable commits.
  - Settlement racing `declare-lost`'s participant void: exactly one.
  - Settlement racing `revalidate`: exactly one settlement.
  - Settlement racing an inventory edit and a pick on the same account: the edit and the pick are `InventoryLocked` until the settlement commits, and succeed afterwards.
  - Settlement racing an energy grant and a policy publication: no deadlock (lock-order proof; a queued publication drains).
  - **Fault after every step:** a raising trigger on each written table in turn: `item_stacks` delete and update, `item_instances` insert, `operatives` update, `pool_collections`, `energy_holds`, `energy_ledger`, `inventories`, `result_submissions`, `participant_settlements`, `attempt_participants`, `mission_attempts`. Each leaves the database byte-identical to before (a table-by-table digest), and a retry after removing the trigger settles once.
  - **`pg_terminate_backend`** of the settling connection while it waits on the pausing trigger: nothing committed; the retry settles once.
  - **Lost response:** the transaction commits, then the client connection is dropped before the response; the retry gets `Duplicate` with the identical receipt.
  - Deadlock and serialization classes answer 503 and are not retried (the trigger raises `40P01`).
- [ ] **Step 2: Run** the two classes 3 times in a row on the VM runner. Expected: PASS every time.
- [ ] **Step 3: Commit.**

## Task 9: The crash drill, smoke and restore drill

**Files:**
- Create: `Backend/tools/vm-crash-drill.sh` (S9-B17)
- Modify: `Backend/tools/vm-smoke.sh` (or the existing smoke) to cover one settlement, one quarantine and one evidence void end to end over HTTP; the restore drill's invariant list (schema 9)
- Test: the scripts' own transcripts, saved under `.claude/scratchpad/phase13/stage-09/`

- [ ] **Step 1: Write `vm-crash-drill.sh`:**
  1. it refuses to run unless the target container name is `blackglass-crashdb`, with no published port and its own volume;
  2. it starts the container and migrates it;
  3. the runner image (its network namespace) drives 200 settlements and voids;
  4. it `docker kill`s the container mid-load, then restarts it;
  5. it runs the invariant queries;
  6. it runs a resubmission pass: every result gets `Settled` or `Duplicate`;
  7. it removes the container and volume, and verifies they are gone and that the shared test container's ID and uptime are unchanged.
- [ ] **Step 2: Run** the smoke test, the crash drill and the restore drill. Expected:
  - 0 failures, invariants hold, no secret in any transcript;
  - the off-host probe still shows only port 22 open;
  - Docker containers, volumes and listeners match before and after, except while the drill runs.
- [ ] **Step 3: Commit.**

## Task 10: Secrecy, docs as built, final review and stop

**Files:**
- Test: `Database/LogSecrecyTests.cs` (extended across settlement, quarantine, void and the recovery verbs: captured logs, anomaly details, `operator_actions`, receipts and CLI output contain no seed, salt, host secret, process credential or ticket)
- Modify: the spec (§0.9 "As built"), the stage plan, `CLAUDE.md`, this plan ("As built"), `Backend/README.md`

- [ ] **Step 1:** Run the extended secrecy test. Expected: PASS.
- [ ] **Step 2:** Run the full suite once on the VM runner and record the counts and duration. Run the concurrency and fault classes once more.
- [ ] **Step 3:** Dispatch the final whole-branch review (most capable model) with this plan, the spec and the review focus. Fix Critical and Important findings test-first in one pass, re-run the suite, and record minors.
- [ ] **Step 4:** Write "As built" (counts, transcripts' locations, rulings, obligations). Commit docs by explicit paths. **Stop and report**: no merge, push or deployment without the owner.

## Acceptance criteria

- Spec B10, B11, B14, B16 and the settlement half of B5 pass at the HTTP level with hand-written manifests.
- Every §A point has a passing test named in the "As built" section.
- No path voids without one of §F's three conditions. A lapsed lease, a timer, a restart, an outage, a disable or a revocation never voids (tests).
- The full suite is green on the VM runner. The concurrency and fault classes are green 3 times running. Smoke, the crash drill and the restore drill pass at schema 9.
- `0000`–`0008` are byte-identical. Only `Backend/`, `Docs/` and `CLAUDE.md` changed. The owner's two Unity files and `.claude/` are untouched.
- Both independent reviews report no open Critical or Important finding.

## Out of scope

- Any agent, Unity or client code (Stages 10–16). Co-op enablement. Reconciling a void against a later valid result. Manifest correction tooling. Multi-catalogue settlement. Incremental reconciliation. Deployment.

## Obligations this stage hands on (proposed)

- **Stage 10 (agent):**
  - relay every outbox file before reporting any exit;
  - report per-participant `present` only after a full outbox scan;
  - treat `200`, `202` (`held`/`quarantined`), `400` and `409` as definitive, and `401`, `5xx` and timeouts as retry with backoff;
  - record relay state in its own directory;
  - provide the trusted emergency stop (R8).
- **Stage 11 (Unity core):** produce manifests in format v1 (integers only, with `lootTableVersion`/`lootHash`), canonicalized exactly as the shared vectors require.
- **Stage 12 (server):**
  - write the outbox file, then call `recorded` with its hash before telling the client "result saved";
  - exit after informing its clients, without polling settlement (S9-B6);
  - treat a 401 as a stop, keeping the outbox file.
- **Stage 16 (client):** poll `GET /v1/attempts/{id}/receipt` and show only committed rewards; show "under review" for `under_review`.
- **Phase 14:** void-versus-late-result reconciliation; manifest correction and resubmission; outbox cleanup; multi-catalogue settlement; incremental reconciliation.
