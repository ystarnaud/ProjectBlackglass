# Phase 13 Stage 9: settlement, receipts, quarantine and protective voids — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans (recommended, per the owner's lean-execution instruction) or superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

Date: 2026-10-17. **Revision 2: the owner's rulings of 2026-10-17 on revision 1 are incorporated. Stage 9 is approved in principle; implementation is not authorized. Nothing here is implemented, and no number below is a measurement.**
- **Base:** `main` @ `e65370b` (Stage 8 merged and pushed; schema version 8; backend 713 domain and 1,602 integration tests).
- **Branch:** `phase13-stage9`. It holds only documentation until the owner authorizes implementation.
- **Execution:** native, in the primary session, with two independent reviews on the most capable model:
  - Checkpoint 1, after Task 4: settlement correctness, conservation, lock order, the trust model and the response contract;
  - a final whole-branch review: voids, operator recovery, catalogue history, concurrency, fault injection and secrecy.
- **Tests:** the VM-local runner (`Backend/tools/vm-test.sh`), with the Windows tunnel runner as the fallback.

**Goal:** a started participant's mission ends in exactly one durable, backend-computed settlement:
- a relayed manifest that passes validation settles every persistent effect in one transaction: items, XP, energy, the insurance payout and its receipt;
- a manifest that can't be settled is preserved, verbatim, and the participant stays locked under live alerts until an operator recovers it;
- a protective void, with its energy refund, happens only on positive, authenticated evidence or by an audited operator decision.

**Stage 9 does not:**
- write agent or Unity code. Tests drive the agent and process routes over HTTP with hand-written manifests, as in Stage 8;
- enable co-op. The schema and the transactions stay per participant;
- reconcile a void against a later valid result (Phase 14);
- add tooling to correct and resubmit a manifest (Phase 14);
- deploy anything.

**Architecture:**
- **Domain (pure, `Accounts.Domain/Settlement`).**
  - `ResultManifest`: strict parse, bounds, canonical bytes and `manifest_hash`.
  - `ManifestValidator`: V4–V17 (§F). It returns every error, with no side effects.
  - `SettlementPlanner`: from a validated manifest, the snapshot, the insurance snapshot, the retention salt and the attempt's admission catalogue, it computes an immutable `SettlementPlan`:
    - owned consumption;
    - loot to grant, with each minted unit's `origin_ref`;
    - stacks to add, and equipment;
    - XP per operative;
    - the insurance payout: refunds, retained lots and progress XP;
    - the expected post-settlement bags;
    - the receipt.
  - `StructuralCheck`: the planned inventory against the current catalogue's structural limits (S9-B20.4).
  - `RetentionRanker`: category priority, then the salted SHA-256.
  - `ReceiptWriter`: deterministic receipt bytes, settled and void.
- **Persistence.**
  - Migration `0009_settlement` (§D).
  - New stores:
    - `SettlementStore`: the results route's whole decision (§C) in one transaction;
    - `VoidStore`: the shared protective-void transaction;
    - `RecoveryStore`: re-validation, result import, participant void and host-loss declaration;
    - `CatalogueHistoryStore`: register, look up and import catalogue versions;
    - `ReceiptStore`;
    - `MonitoringStore`: alerts and the auto-drain.
  - `HostStore.ExitedAsync` gains the positive-evidence void.
  - `ProcessStore` gains `RecordedAsync`.
- **API.**
  - `POST /agent/v1/results` (host credential).
  - `POST /server/v1/attempts/{aid}/participants/{acc}/recorded` (process credential).
  - `GET /v1/attempts/{id}/receipt` (bearer).
  - `serve` registers its catalogue in the catalogue history at startup.
  - `AttemptDeadlineWorker` gains the monitoring sweeps.
- **Admin CLI.** `attempt show`, `attempt revalidate`, `attempt import-result`, `attempt void-participant`, `host declare-lost`, `catalogue import`, `anomaly list` and `anomaly review`. Each is confirmed and audited; there are no new HTTP admin routes (S9-B9).

**Tech stack:** .NET 10 minimal APIs, PostgreSQL 17, Npgsql and Dapper, xUnit. Analyzers treat warnings as errors.

**Spec:** `Docs/superpowers/specs/2026-10-14-phase-13-backend-design.md`:
- §0.9 (revision 2.9: these rulings, recorded with this revision);
- §6.2–§6.3, §8, §9, §10, §11.1, §11.3–§11.4, §12–§13, §15–§16 and §18–§19;
- the §0.8 Stage 9 obligations and the host-disable decision (R8).

Decisions 048–050 also apply. Stage plan: `Docs/superpowers/plans/2026-10-14-phase-13-backend.md` §Stage 9.

---

## Revision 2: what changed, by the owner's ruling

| Owner item | Change | Where |
|---|---|---|
| 1 (B1) | Revoked credentials are refused on every normal route. A result for a revoked attempt is refused (`409 AttemptRevoked`) and not stored; pre-revocation evidence is kept and marked. Post-revocation evidence enters only through `attempt import-result` | S9-B1, §C, §F, Tasks 3 and 6 |
| 2 (B2) | Every definitive answer follows a committed record | S9-B2, §C |
| 3 (B3) | The raw payload and its hash are kept beside the canonical form. Distinct submissions each get their own row. A settlement stays final | S9-B3, S9-B21, S9-B22, §D |
| 4 (B4) | Integer bounds per field; canonical form pinned by golden vectors that Stage 11 must reproduce | S9-B4, §G, Task 1 |
| 5 (B5) | The recorded hash is supporting evidence: its absence neither blocks settlement nor causes a void; a mismatch is never accepted | S9-B5, §C, Task 4 |
| 6 (B6) | Approved as proposed | S9-B6 |
| 7 (B7) | A void needs proof of exit **and** a complete, successful outbox scan that lists the participant as absent | S9-B7, §C, Task 5 |
| 8 (B8) | Approved as proposed | S9-B8 |
| 9 (B9) | Approved; no new HTTP admin routes | S9-B9 |
| 10 (B10) | Transient errors (503, nothing committed), committed quarantines (202) and permanent conflicts (409) are distinct answers | S9-B10, §C |
| 11 (B11–B15) | Each decision is written out on its own | S9-B11.1–S9-B15.3 |
| 12 (B17) | Approved as proposed | S9-B17 |
| 13 (B18) | Configurable thresholds; distinct attempts counted; never an automatic disable or refund | S9-B18 |
| 14 (B20) | Revised: an immutable, hash-verified catalogue history; settlement uses the admission catalogue; if it is genuinely unavailable, the result is preserved | S9-B20.1–S9-B20.5, Task 2a |
| 15 | The full response matrix with the agent's handling of its relay file | §C |
| 16 | The five named tests | Tasks 3, 5 and 8 (named in "Required race and crash tests") |
| 17 | B16 and B19 surfaced with alternatives and consequences | S9-B16, S9-B19 |

Revision 2 also raises three new points for the owner: S9-B21 (a quarantine is never cleared automatically), S9-B22 (a cap on stored submissions per participant) and S9-B23 (the relay-ordering obligation that makes a hash mismatch detectable).

## A. The owner's attention points, mapped

| # | Point | Where it is met |
|---|---|---|
| 1 | Authenticated host and process evidence | §F trust model. Results need the host credential, a current and unrevoked secret, the attempt's own host and its bound session; the recorded hash needs the process credential (S9-B5); the void evidence is only the agent's exit report with proof of exit and a complete outbox scan (S9-B7). Tasks 3–5 |
| 2 | Evidence from hosts later disabled or compromised | S9-B1: no normal route accepts a revoked credential or a result for a revoked attempt. Evidence received before the revocation is kept and marked as such. Nothing about a revoked attempt settles or voids automatically. Tasks 3, 5 and 6 |
| 3 | Original manifests; durable outbox retries | S9-B3: every submission keeps its raw bytes and raw hash beside its canonical form, insert-only. Every definitive answer follows a commit; 401, 5xx and timeouts are never definitive (§C). Tasks 2–4 |
| 4 | Deterministic settlement and item minting | `SettlementPlanner` is pure and uses the admission catalogue. Minted `origin_ref`s are a function of attempt, account, line and unit. The retention selection is stored and never recomputed (S9-B11.2, S9-B12.3). Tasks 1 and 3 |
| 5 | Exactly-once inventory, energy and XP changes | The `participant_settlements` primary key. Minting uses plain inserts on a unique `(origin, origin_ref)`. Ledger keys are exact per attempt; the hold has guarded edges. Everything is one transaction, with no automatic retry (S9-B10). Tasks 2, 3 and 8 |
| 6 | Immutable loot pool and objective records | Already insert-only (0008). Settlement only reads them. The running per-line totals are `pool_collections`, which only grow and never exceed the pool (trigger). Tasks 2–3 |
| 7 | Conservation checks | V8–V12 before any write. V16: the inventory version is unchanged since the snapshot. After the writes, an in-transaction check that the bags equal the plan's (S9-B14.3). §18.3 invariants extended. Tasks 1–3 and 9 |
| 8 | Recorded seed, loot hash and layout hash | The manifest must carry the stored `layoutHash`, `lootTableVersion` and `lootHash` (V5, V5b). The seed never leaves `attempt_secrets`; only the retention salt is read, inside the transaction (S9-B4). Tasks 1, 3 and 10 |
| 9 | Participant-specific settlement | One transaction per participant. V9 sums over the attempt's already settled participants, through `pool_collections` under the attempt lock. Tests are co-op-shaped, with direct SQL. Task 3 |
| 10 | Safe protective voids from positive evidence | Exhaustive void conditions (§F). Every void path uses one `VoidStore` transaction. Tasks 5–6 |
| 11 | Operator-controlled quarantine recovery | A quarantine is never cleared automatically (S9-B21); escalation never voids; re-validation of a chosen stored submission; result import; an operator void with recorded checks; host-loss declaration. Tasks 4, 6 and 7 |
| 12 | Database lock ordering | §E; deadlock tests in Task 8 |
| 13 | Concurrency and fault-injection tests | Task 8 (races, step faults, `pg_terminate_backend`, lost responses); Task 9 (a crash drill on a disposable database container; S9-B17) |
| 14 | Catalogue upgrades during a mission | S9-B20: the catalogue history; settlement under the admission catalogue; a preserved result when it is genuinely unavailable. Tasks 2a, 3 and 8 |

## B. Decisions

Status key:
- **Approved:** the owner approved it as proposed on 2026-10-17.
- **Revised per owner:** rewritten to the owner's ruling. It still needs the owner's confirmation that the wording matches.
- **Needs approval:** new in revision 2, or surfaced at the owner's request.

### S9-B1. Revoked credentials and revoked attempts. *Revised per owner.*

An attempt is revoked when `credential_revoked_at` is set, which happens through a host disable (R8).
- **No normal route accepts a revoked credential.**
  - A revoked or replaced host secret gets the uniform 401 on every agent route, as in Stage 8.
  - A revoked process credential gets the uniform 401 on every process route, as in Stage 8. This includes `recorded`.
  - Both are re-checked inside the transaction, under the attempt lock (the R8 pattern).
- **A result for a revoked attempt is refused, even from a re-enabled host with a rotated secret:** `409 AttemptRevoked`.
  - Its payload is not stored.
  - A deduplicated `result_refused_revoked` anomaly is committed before the answer. It records the host, the claimed participant, the raw hash and the byte length.
  - The agent keeps the file for the operator (§C).
- **Pre-revocation evidence is preserved.** The disable never alters or deletes evidence (R8), and every Stage 9 evidence row records whether its attempt was already revoked when it was written. The fields are `result_submissions.attempt_revoked`, and `attempt_exit_reports.attempt_revoked`, added by 0009.
  - Both are written inside the transaction that holds the attempt row, so they order exactly against the disable.
- **Nothing about a revoked attempt settles or voids automatically.**
  - A pre-revocation submission that was quarantined stays quarantined.
  - A post-revocation exit report keeps Stage 8's behaviour: it is stored and frees the slot. Stage 9 never treats it as void evidence, and it raises `exit_evidence_post_revocation`.
- **Recovery of post-revocation evidence is the explicitly authorized operator workflow** (S9-B9):
  1. `attempt import-result --host-trust-reviewed` imports the operator-retrieved outbox file. It is stored with `source = 'operator_import'` and quarantined (`host_revoked`).
  2. `attempt revalidate --submission <id> --host-trust-reviewed` settles it.
  - Otherwise `host declare-lost`. R8 makes it the only path to a protective void for a disabled host's attempts, so `attempt void-participant` refuses a revoked attempt's participant until its host is declared lost.
- A participant of a revoked attempt shows `under_review` on its receipt. A repeating `revoked_attempt_unsettled` alert runs until it is settled (S9-B18).

*Interpretations to confirm:*
- "Not accepted" is read as "not stored". The alternative is to store the refused payload as inert evidence, which would save the operator fetching the file from the host. It would also mean the normal route takes in post-revocation data.
- R8's "declare-lost is the only path to protective voids" is kept literally. The alternative is an operator void of a single revoked attempt's participant, with `--host-trust-reviewed` and the outbox evidence, for a host that is back in service after rotation. Without it, such a participant whose result file is lost stays under review until the owner decides.

*Cost if wrong:* an operator retrieves the file from the host, which is rare by design.

### S9-B2. One commit per request; a definitive answer only after a commit. *Approved.*

- The manifest is validated (pure) under the attempt and participant locks. One transaction then writes exactly one of these:
  - the settlement;
  - the quarantine records;
  - the refusal record.
- Every 200, 202, 409 and 422 is sent only after its transaction has committed a durable record (§C).
- 401, 500 and 503 commit nothing, except a 500's deduplicated anomaly in a fresh transaction.
- A post-apply check failure rolls back. A fresh transaction then quarantines (`settlement_check_failed`), so it ends in a 202 and not in an endless 500 loop. That is still a single commit.

### S9-B3. Original payloads are preserved. *Revised per owner.*

`result_submissions` is insert-only and has a surrogate `submission_id` (bigint identity). Each row holds:
- the raw request bytes as received (`raw_bytes`, 1..65,536 bytes; null only for an over-cap body, of which only the length is kept);
- `raw_hash`, the SHA-256 of the raw bytes;
- when the body parsed as a manifest: `canonical_bytes` and `manifest_hash` (the SHA-256 of the canonical bytes);
- `source` (`agent` or `operator_import`), `host_id`, `process_session_id`, the claimed attempt and account (a foreign key to the participant when they name one), `attempt_revoked`, `disposition`, `codes`, `first_received_at`, `last_received_at` and `receive_count`.

Rules:
- A byte-identical repeat (the same participant and raw hash, or for an unidentified body the same host and raw hash) adds no row. It raises `receive_count` and `last_received_at` only, and returns the stored answer. A trigger refuses any other change, and any delete or truncate.
- A different raw payload is a new row, even when its canonical form equals an earlier one. Its disposition is then `same_as`, with `same_as_submission_id` pointing at the first row, and it gets that row's answer. No earlier row is ever replaced.
- `participant_settlements.submission_id` references the settled row. A settlement is final: later submissions are stored and answered, but never change it (S9-B21).
- Re-validation reads the stored canonical bytes of the submission the operator names.

### S9-B4. Manifest format v1: bounded integers and strict canonicalization. *Approved; detail in §G.*

- Integers only, each with a format bound (§G). For example, `serverElapsedMs` is 0..86,400,000 and replaces `serverElapsedSeconds`.
- `lootTableVersion` and `lootHash` are added.
- Strict parsing refuses unknown members, duplicate keys, fractions, exponents, leading zeros, `-0`, negative numbers, non-canonical UUIDs, account ids or hashes, unknown enumerations, nesting beyond the format, and duplicate list entries.
- One canonical form: UTF-8, keys in ordinal order, no whitespace, minimal escapes, and lists sorted by their stated key. A parseable but non-canonical encoding is accepted and canonicalized; both forms are stored. `manifest_hash` is always the hash of the canonical form.
- The seed is never in a manifest or a receipt.
- **Golden vectors** in `Backend/contracts/manifest-v1/` pin the canonical bytes, hashes and refusals (§G). Stage 11's Unity EditMode tests must reproduce every vector byte for byte.

### S9-B5. The process's recorded hash is supporting evidence. *Revised per owner.*

`POST /server/v1/attempts/{aid}/participants/{acc}/recorded {manifestHash}` is sent with the process credential after the outbox `fsync`.
- It moves the participant from `joined` or `left` to `result_pending` and stores `recorded_manifest_hash` once.
- **It is not a prerequisite.** A durably written outbox result relayed without any recorded hash (the process died after its write) is validated and settled normally. The settlement row records `recorded_hash = 'absent'`, and a low-severity `result_without_recorded_hash` anomaly is raised for monitoring.
- **A mismatch is never accepted silently:**
  - a relay whose canonical hash differs from a recorded hash is quarantined (V15, `recorded_hash_mismatch`);
  - a `recorded` call whose hash differs from a stored submission or settlement for that participant gets `409 ConflictingReport` and raises `recorded_hash_mismatch`. A settlement that already committed stays final, but the participant's account is flagged for review by that anomaly.
- **A missing notification alone never causes a void.** The void rule (S9-B7) does not look at it, except that a recorded hash *blocks* a void: it proves a result file was written.
- An agent `exited` with `present: true` also moves the participant to `result_pending`.

### S9-B6. No process-side settlement polling. *Approved.*

The process tells its clients "pending" and exits. The client polls `GET /v1/attempts/{id}/receipt`, authenticated by its own bearer session. R4 stays intact.

### S9-B7. The positive-evidence void. *Revised per owner.*

The void is applied inside the agent's `exited` transaction. For a participant, it happens **only when all of these hold**:
1. The report comes from the attempt's own host with a current, unrevoked secret, re-checked under the attempt lock.
2. The attempt's `credential_revoked_at` is null.
3. The attempt has started, and the participant has started (`started_at` set) and is not settled or quarantined.
4. **The report proves the process exited:** `exitCode` or `signal` is present.
5. **The report declares a complete, successful outbox examination** (`outbox: {complete: true}`), and it lists this participant with `present: false`.
6. No submission for the participant is stored (any disposition), and it has no `recorded_manifest_hash`.

Anything less is not proof of absence, and nothing is voided:
- missing connectivity or silence;
- `outbox.complete` false or absent, or a failed or partial enumeration;
- a participant missing from the list;
- no exit status.

In those cases the report is stored as Stage 8 stores it, it frees the slot as before, and a deduplicated `exit_evidence_inconclusive` anomaly names each started, unsettled participant it did not prove absent. A recorded hash or a stored submission that contradicts `present: false` raises `exit_evidence_contradiction`.

Further rules:
- `attempt_exit_reports` gains `outbox_complete` and `attempt_revoked`, and its `outcome` gains `voided`.
- `exited` takes `accounts` `FOR KEY SHARE` before the attempt lock (§E).
- An identical repeat returns the stored outcome. A conflicting one is `409 ConflictingReport` and voids nothing (Stage 8).

### S9-B8. Host-loss declaration. *Approved.*

`admin host declare-lost` is refused unless all of these hold:
- the host is disabled with its secret revoked;
- its last heartbeat is older than a provisional 10 minutes;
- `--reason` is given;
- `--outbox-unrecoverable` is given;
- `--evidence "<operator's note>"` is given.

It stamps `mission_hosts.declared_lost_at`, which is permanent: enable and rotate are refused forever, and a recovered machine gets a new host id. It then voids every started, unsettled, non-quarantined participant of the host's attempts, revoked or not, one transaction each, each with its `operator_actions` row under one correlation id. Quarantined participants keep their own recovery path. A re-run resumes idempotently. Host loss is never inferred from a heartbeat timeout alone.

### S9-B9. Operator recovery is CLI-only and audited. *Approved.*

The verbs:
- `admin attempt show <aid>`: states, submissions with hashes, dispositions and pre- or post-revocation marks, exit evidence and actions. It never shows a secret or salt. A raw payload is shown only with `--submission <id> --raw`.
- `admin attempt revalidate <aid> <acc> --submission <id> --reason`, plus `--host-trust-reviewed` for a revoked attempt or a `host_revoked` hold.
- `admin attempt import-result <aid> <acc> --file <path> --reason --evidence --host-trust-reviewed`, for a revoked attempt only.
- `admin attempt void-participant <aid> <acc> --reason --outbox-checked --evidence`:
  - for a quarantined participant only;
  - `--revalidated` when the participant has a stored submission;
  - for a revoked attempt, only after its host is declared lost, and with `--host-trust-reviewed` (R8).
- `admin host declare-lost …`.
- `admin catalogue import <file> --reason` (S9-B20.5).
- `admin anomaly list [--open]` and `admin anomaly review <id> --note`.

Each verb asks for confirmation (exit 64 without `--yes` in a non-interactive run) and writes `operator_actions` in the same transaction as its effect. There are no new HTTP admin routes; the spec's `/dev/v1/*` recovery routes are dropped.

### S9-B10. No automatic retry; error classes are distinct. *Approved.*

Settlement, quarantine and void transactions are never retried by the backend (the Stage 6 rule overrides §10.3's "loop up to 3 times").
- **Transient:** a connection failure, `40001`, `40P01`, `55P03`, `57014`, `57P01` or shutdown. The answer is 503 with `Retry-After`, and nothing is committed.
- **Committed quarantine or hold:** 202.
- **Permanent conflict:** 409, after its refusal record committed.
- The agent's retries are idempotent through the stored submission, the settlement primary key and the stored exit report.

### S9-B11. Minting and item identity. *Approved; written out per decision.*

- **S9-B11.1. BIGINT identities.** Minted items use the existing `item_instances` bigint identity, Base58 outside the database (048 amendment 2). No new id scheme.
- **S9-B11.2. Unique origin references.** Each unit's `origin_ref` is `settlement:<attemptId>:<accountBase58>:c<ci>:l<line>:u<k>` for loot, or `insurance:<attemptId>:<accountBase58>:c<ci>:l<line>:u<k>` for a retained lot. `k` runs from 0 to quantity − 1. The pair `(origin, origin_ref)` is unique.
- **S9-B11.3. Plain inserts.** Minting uses a plain `INSERT`, with no `ON CONFLICT`, so a repeated effect aborts the whole transaction (§10.1.4). A replay can't reach minting at all, because the settlement primary key is checked first.
- **S9-B11.4. Stacks.** Stackable grants upsert on the nulls-not-distinct `(account, operative, definition)` key. Consumption deletes an exactly consumed stack and decrements a partly consumed one; nothing ever writes `quantity = 0`.
- **S9-B11.5. Destinations.** Granted loot goes to the collecting operative's bag (V11 has checked capacity).
- **S9-B11.6. Equipment.** Mission equipment is applied on success only. On any other outcome the stored equipment stays (§8.1, A-23).

### S9-B12. Insurance benefits. *Approved; written out per decision.*

- **S9-B12.1. Snapshot-based.** Every benefit comes from the insurance snapshot captured at admission (Stage 7): coverage, policy id and version, percentages and caps. The current policy is never read at settlement.
- **S9-B12.2. Destination.** Refunds and retained lots go to the **stash**, so a full bag never blocks a payout.
- **S9-B12.3. Retention selection.**
  - The selection is capped at `maxEntries` and `maxUnitsPerEntry`.
  - It is ranked by category priority, then by `SHA-256(salt ‖ accountId as 8 bytes big-endian ‖ ci as 4 bytes ‖ line as 4 bytes)`, compared bytewise.
  - It is stored in the settlement row and never recomputed.
- **S9-B12.4. Categories.** The policy's `Weapon`, `Armor`, `Utility` and `Consumable` map to the catalogue's lowercase categories through a fixed table. Any other category is ineligible.

### S9-B13. Deterministic rounding. *Approved; written out per decision.*

- **S9-B13.1. Success XP.** `missionCompletionXp`, from the admission catalogue, goes to each snapshot operative, dead included (C-2).
- **S9-B13.2. Insured-failure XP.** `floor(MCX × completedRequired × percent / (totalRequired × 100))`, a single floor in 64-bit integer arithmetic. It is 0 when no objective is required.
- **S9-B13.3. Refund quantities.** `floor(units × percent / 100)` per entry, in 64-bit integer arithmetic, with no other rounding step.
- **S9-B13.4. Caps.** Experience is capped at `int.MaxValue`.
- **S9-B13.5. Alive.** `alive` has no persistent effect in Phase 13 and is shown in the receipt.

### S9-B14. Inventory verification and capacity. *Approved; written out per decision.*

- **S9-B14.1. Base version.** V16: `inventories.version` must equal the participant's snapshot `inventory_version`. It is checked under the inventory lock. Otherwise the participant is quarantined (`inventory_base_changed`), and the backend never applies effects to a changed base.
- **S9-B14.2. Capacity before writing.** V11: each operative's `InventoryRules.BagEntries` is at most the admission catalogue's `inventory.bagCapacity`. The current catalogue's limits are checked too (S9-B20.4).
- **S9-B14.3. Capacity and contents after writing.** Inside the transaction, the participant's operatives' bags and equipment must equal the plan's expected bags. A mismatch rolls back, and the participant is then quarantined (`settlement_check_failed`) in a fresh transaction (S9-B2).

### S9-B15. Energy edges and ledger keys. *Approved; written out per decision.*

- **S9-B15.1. Hold edges.** `charged → settled` and `charged → refunded` are added. No other edge is added.
- **S9-B15.2. Ledger keys.** `settle:<attemptId>` (delta 0, positive holds only, as Stage 8's B9) and `void_refund:<attemptId>` (delta `+amount`, positive holds only). The key CHECK is tightened to the exact attempt form, like `reserve`, `release` and `charge`.
- **S9-B15.3. Zero cost.** A zero-cost hold changes state with no ledger entry.

### S9-B16. Participant and attempt state edges. *Needs approval (surfaced).*

**Proposal (a):**
- Add `joined → settled` and `left → settled`, used by a void and by a relay that settles without a recorded hash.
- A same-state update of a `quarantined` row may change only the escalation columns.
- Attempts get no new edge. The last settlement from `running` writes `running → ending → settled` in one transaction; from `unresponsive`, it writes `settled` directly.
- A settled attempt's slot still waits for its exit evidence (Stage 8's B6).

**Alternatives and consequences:**
- **(b) Always pass through `result_pending`:** two updates in one transaction, and no new edge.
  - Consequence: `result_pending` would no longer mean "the process recorded a hash or the agent saw a file", so the audit trail would show evidence that never existed.
  - It changes nothing visible to players.
- **(c) A separate `voided` participant state:** a void is visible from the state alone.
  - Consequence: 0007's state CHECK, the participation lock, receipts, monitoring and every state reader would change.
  - The settlement row's `outcome = 'infra_void'` already records it.

*Recommendation:* (a). *Cost if wrong:* a small migration later. No data is lost either way, because the settlement row records the outcome and the void basis.

### S9-B17. Destructive outage tests on a throwaway container. *Approved.*

`Backend/tools/vm-crash-drill.sh` runs outside the runner, like the smoke test.
- It starts a throwaway `blackglass-crashdb` container on `bg-dev-services01`, with its own volume, no published port and the test cluster's settings, and migrates it.
- It drives settlements at it from the runner image, `docker kill`s it mid-load, restarts it, and runs the invariant queries.
- It removes the container and its volume.

The shared test cluster is never stopped or damaged.

### S9-B18. Monitoring and the auto-drain. *Revised per owner.*

- **Alerts repeat; time never voids.**
  - `unresponsive_attempt`: at 10 minutes, repeated every period.
  - `quarantine_escalation`: at 24 h, repeated every 24 h.
  - `revoked_attempt_unsettled`: every 24 h.
  - None of them changes a state, hold, inventory or lock.
- **Auto-drain on repeated confirmed infrastructure failures.**
  - A host is drained when the number of **distinct attempts** on it with at least one exit-evidence void (`void_basis = 'exit_evidence'`) within the window reaches the threshold. The participant count is never used.
  - Operator voids and declare-lost voids are not counted, because they are operator decisions and not confirmed host failures.
  - The thresholds are configurable: `BLACKGLASS_AUTO_DRAIN_ATTEMPTS` (provisional 3; 0 turns the auto-drain off) and `BLACKGLASS_AUTO_DRAIN_WINDOW` (provisional 60 minutes).
  - The auto-drain only drains, which is the planned-maintenance operation: running attempts and process credentials continue. It raises `host_auto_drained`, and an operator re-enables.
  - It **never disables, revokes, voids or refunds.**
- **Anomaly kinds:**
  - `result_quarantined`, `result_malformed`, `result_refused`, `result_refused_revoked`;
  - `settlement_conflict`, `late_result_after_void`, `result_without_recorded_hash`, `recorded_hash_mismatch`;
  - `infra_void`, `exit_evidence_inconclusive`, `exit_evidence_contradiction`, `exit_evidence_post_revocation`;
  - `quarantine_escalation`, `unresponsive_attempt`, `revoked_attempt_unsettled`;
  - `catalogue_unavailable`, `catalogue_conflict`, `submission_limit`, `host_auto_drained`.

### S9-B19. What the agent is told. *Needs approval (surfaced).*

**Proposal (a), minimal answers:**
- `200 {disposition, outcome}`;
- `202 {disposition, codes}`, where the codes are V-rule codes, with no echo of the body;
- `409 {code}` and `422 {code}`.

The full receipt goes only to the participant's own client, through its bearer session.

**Alternatives and consequences:**
- **(b) Return the full receipt to the agent.**
  - The agent and process could forward it to players without the receipt poll.
  - The semi-trusted host would see minted item ids, the inventory version and insurance details. A compromised host would learn more about accounts.
- **(c) Return a receipt hash as well.**
  - It lets agent logs reference a receipt verifiably, without its contents.
  - Cheap, but nothing in Phase 13 consumes it.
- **(d) Omit the V-codes from 202.**
  - It gives a compromised host less feedback for probing.
  - Diagnosis gets harder.
  - Probing is already closed by S9-B21, because a quarantine never clears automatically.

*Recommendation:* (a). *Cost if wrong:* a response field added later, which is backward-compatible for the agent.

### S9-B20. Catalogue history and settlement under the admission catalogue. *Revised per owner.*

- **S9-B20.1. Immutable history.** New table `catalogue_versions (version, bytes, registered_at)`.
  - `version` is the existing `sha256:<hex>` of the exporter's bytes.
  - A CHECK enforces `version = 'sha256:' || encode(sha256(bytes), 'hex')`.
  - Rows are insert-only: a trigger refuses update, delete and truncate.
- **S9-B20.2. Registration.**
  - At startup, `serve` inserts its validated catalogue if absent, then verifies the stored row.
  - 0009 adds a foreign key from `mission_attempts.catalogue_version` to the history, `NOT VALID`, so schema-8 rows are not checked but every new admission is. An attempt can therefore only be admitted under a catalogue the history holds.
  - Registration refuses a catalogue that removes a definition id present in any registered version, or changes such an id's identity kind (stackable or instance). `serve` then exits with a new code. Item ids are permanent keys (CLAUDE.md), so this only enforces an existing rule.
- **S9-B20.3. Lookup.**
  - Settlement loads the attempt's `catalogue_version` from the history, verifies the hash in the application, and validates and caches the parsed catalogue by version.
  - Validation (V4–V17), the plan, XP and capacity use that admission catalogue; the current catalogue is never applied in its place.
  - Stage 8's `ready` is switched to the same lookup, so its `409 CatalogueMismatch` remains only for a genuinely unavailable version. *Alternative:* keep Stage 8's behaviour, in which case an attempt admitted just before an API upgrade can't start.
- **S9-B20.4. Structural check against the current catalogue.**
  - The planned inventory must also fit the current catalogue's structural limits: each definition exists, its identity kind, max stack and bag capacity.
  - Otherwise the result is preserved, and the participant is quarantined (`catalogue_conflict`). It is never partly applied.
  - S9-B20.2 makes this case rare. It is reached only when a newer catalogue tightened a limit.
- **S9-B20.5. Genuinely unavailable.** The version has no history row, its bytes fail verification, or they no longer parse.
  - The submission is stored, the participant is quarantined (`catalogue_unavailable`) and `catalogue_unavailable` is raised. The answer is `202 {disposition:"held"}`.
  - The result is never judged against another catalogue, and the agent stops resending it.
  - Recovery: `admin catalogue import <file>` registers the version (verified, audited), then `attempt revalidate`.

*Cost if wrong:* a catalogue table that grows by one row per exported catalogue, which is small.

### S9-B21. A quarantine is never cleared automatically. *Needs approval (new).*

Once a participant is quarantined, any later distinct submission is stored (disposition `quarantined`, with its codes, even when it would validate) and answered `202`. Only `attempt revalidate --submission <id>` or an operator void ends the quarantine.

*Why:* a normal process writes exactly one result per participant, so a second distinct manifest is itself an anomaly. Settling it automatically would let a faulty or compromised host iterate on rejected manifests.

*Alternative:* revision 1's rule, under which a later valid manifest settles automatically. *Cost if wrong:* one operator command per legitimate resubmission, which has no producer in Phase 13.

### S9-B22. A cap on stored submissions. *Needs approval (new).*

- Distinct submissions per participant are capped at a provisional 16 (`BLACKGLASS_RESULT_SUBMISSIONS_MAX`).
- Bodies that identify no participant are capped at a provisional 64 per host per 24 h.
- Past a cap, nothing more is stored except a deduplicated `submission_limit` anomaly's count, committed before the answer. The answer is `409 SubmissionLimit`.

*Why:* each submission stores up to 64 KiB, twice, and a faulty host must not fill the database. *Cost if wrong:* an operator raises the setting.

### S9-B23. Relay ordering makes a hash mismatch detectable. *Needs approval (new; Stage 10 and 12 obligations).*

- The process calls `recorded` before it exits.
- The agent relays an outbox file only after its mission unit has exited.

Then a recorded hash, when one exists, is always stored before the relay arrives, and a mismatch is always caught (S9-B5). Without this ordering, a relay could settle a solo attempt first, and the process's late `recorded` would get the uniform 401 (R4), so a mismatch would go unseen.

*Cost if wrong:* a result is relayed a few seconds later.

## C. API contracts (Stage 9)

### `POST /agent/v1/results` (Host scheme)

The body is one manifest (§G). Steps, in order:
1. The Host scheme authenticates before the body is read.
2. The body is read up to 65,536 bytes.
3. The body is parsed strictly.
4. One transaction makes the decision (§E).

Every definitive answer below follows a committed record (S9-B2). "Relay file" is the agent's durable copy (Stage 10). In every case the agent never deletes the file before Phase 14's cleanup, and records each answer in its own relay state.

**200 `settled`** — `{disposition:"settled", outcome}`.
- When: this request's transaction committed the settlement.
- Committed record: the submission (`settled`) and the settlement.
- Agent: definitive. The file becomes `done` and is never resent.

**200 `duplicate`** — `{disposition:"duplicate", outcome}`.
- When: the participant is already settled with this canonical hash, by any earlier request.
- Committed record: the receive count, or a `same_as` row.
- Agent: definitive. The file becomes `done`.

**202 `quarantined`** — `{disposition:"quarantined", codes}`. When:
- V4–V17 failed, including V15 (`recorded_hash_mismatch`), V16 (`inventory_base_changed`) and S9-B20.4 (`catalogue_conflict`);
- a post-apply check failed (`settlement_check_failed`);
- any distinct submission arrived while the participant was quarantined (S9-B21).

Committed record: the submission, the participant `quarantined` (or still quarantined) and an anomaly. Agent: definitive. The file becomes `under_review` and is never resent.

**202 `held`** — `{disposition:"held"}`.
- When: the admission catalogue is genuinely unavailable (S9-B20.5).
- Committed record: the submission, the participant `quarantined` (`catalogue_unavailable`) and an anomaly.
- Agent: definitive. The file becomes `under_review`.

**409 `Conflict`** — `{code}`.
- When: the participant is already settled with a different canonical hash.
- Committed record: the submission (`conflict`) and a `settlement_conflict` anomaly. The settlement is unchanged.
- Agent: definitive. The file becomes `conflict` and is surfaced to the operator.

**409 `AlreadySettledAsVoid`** — `{code}`.
- When: the participant was settled by a protective void.
- Committed record: the submission (`late_after_void`) and an anomaly. The receipt is byte-identical.
- Agent: definitive. The file becomes `conflict` (Phase 14 reconciliation).

**409 `AttemptRevoked`** — `{code}`.
- When: the attempt's credentials were revoked (S9-B1).
- Committed record: a `result_refused_revoked` anomaly (host, claimed participant, raw hash, length). The payload is **not** stored.
- Agent: definitive for the normal relay. The file becomes `operator_recovery` and is kept for `import-result`.

**409 `WrongAttempt`** — `{code}`.
- When: the attempt is not this host's, or the manifest names another process session.
- Committed record: the submission (`refused`) and a `result_refused` anomaly.
- Agent: definitive. The file becomes `conflict`.

**409 `InvalidState`** — `{code}`.
- When: the participant never started (`admitted`) or was `released`, or the attempt never started.
- Committed record: the submission (`refused`) and a `result_refused` anomaly.
- Agent: definitive. The file becomes `conflict`.

**409 `SubmissionLimit`** — `{code}`.
- When: S9-B22's cap was reached.
- Committed record: the `submission_limit` anomaly's count.
- Agent: definitive. The file becomes `conflict`.

**422 `Malformed`** — `{code}`, where the code is `TooLarge`, `NotJson`, `UnknownMember`, `DuplicateKey`, `OutOfBounds`, `NonCanonicalId`, `BadEnum` or `BackendOnlyOutcome`.
- When: the body is not a format-v1 manifest.
- Committed record: the submission (`malformed`) with its raw bytes, or only the length when over the cap, plus `result_malformed`.
  - The participant is moved to `quarantined` (`malformed_manifest`) only when all of these hold: the strict top-level `attemptId` and `accountId` could be read and are canonical; they name a started, unsettled, unrevoked participant; and the attempt belongs to this host.
- Agent: definitive. The file becomes `rejected` and is never resent.

**401** (uniform).
- When: the host secret is unknown, foreign, revoked or replaced, or the host is disabled, including a change between authentication and the transaction.
- Committed record: nothing.
- Agent: not definitive. The file stays `pending`. Retry with backoff, reload credentials after an operator rotation, and alert after a provisional 1 h.

**503** with `Retry-After`.
- When: a transient database failure (S9-B10) or the service shutting down.
- Committed record: nothing.
- Agent: not definitive. The file stays `pending`. Retry with backoff and jitter, with no limit, and alert after a provisional 1 h.

**500.**
- When: an unexpected failure.
- Committed record: nothing, except a deduplicated anomaly in a fresh transaction.
- Agent: not definitive. Retry like a 503.

Further rules:
- A byte-identical repeat of any stored submission returns the stored answer, with the same status and body.
- 202 replaces the spec's 422 for an invalid manifest, because the submission was accepted, stored and put under review, so the agent must not resend it. 422 now means only "not a manifest at all".
- No answer echoes the body.

### `POST /server/v1/attempts/{aid}/participants/{acc}/recorded` (Process scheme)

The body is `{manifestHash:"sha256:<hex>"}`, the canonical hash.
- `200 {state:"result_pending"}`, also on an identical repeat.
- `409 ConflictingReport` for a hash different from the stored recorded hash, or from a stored submission or settlement for that participant. It raises `recorded_hash_mismatch`.
- `409 InvalidState` when the participant has not started.
- The uniform 401 when the credential is revoked or superseded, or the attempt has ended (R4, R8).
- 503 during an outage.

### `POST /agent/v1/attempts/{aid}/exited` (Host scheme; Stage 8, amended)

The body gains an optional `outbox: {complete: boolean}`. A missing value is stored as `false`.
- With `complete: true`, every started participant must be listed in `results`, or the report is `400 IncompleteResults` and nothing is stored.
- The void rule is S9-B7. The answers are Stage 8's, and `outcome` may now be `voided`.

### `GET /v1/attempts/{id}/receipt` (Bearer)

- `200` returns the stored receipt bytes verbatim.
- `202 {state}`, where `state` is:
  - `running` (participant `admitted`, `joined` or `left`);
  - `result_pending`;
  - `unresponsive` (attempt `unresponsive`, participant unsettled);
  - `under_review` (participant `quarantined`, or attempt revoked and participant unsettled).
- `404 AttemptNotFound` for an unknown, foreign or non-canonical id.
- `409 NoReceipt` for a released participant.

**Receipt v1** (§10.5, extended):
- `receiptVersion`, `attemptId`, `accountId` (Base58), `outcome`, `settledAt`, `catalogueVersion`;
- `energy {cost, state, refunded}`;
- `consumed[]`, `granted[]` (with minted item ids, in Base58), `xp[]`, `operatives[{operativeId, alive}]`;
- `insurance {covered, policyId, policyVersion, refunds[], retained[], xp[]}` or `{covered:false}`;
- `inventoryVersion`.

It never contains the seed, salt, manifest bytes, host secrets or credential material.

## D. Migration `0009_settlement`

`0000`–`0008` stay byte-identical. The objects, in creation order:
1. **`catalogue_versions`** (S9-B20.1).
   - Columns: `version` (PK), `bytes bytea`, `registered_at`.
   - The hash CHECK; insert-only; no truncate.
   - Then the `NOT VALID` foreign key from `mission_attempts.catalogue_version`.
2. **`result_submissions`** (S9-B3).
   - Columns:
     - `submission_id` (bigint identity PK), `source`, `host_id`, `process_session_id`;
     - `attempt_id` and `account_id`, nullable, with a foreign key to the participant when both are set;
     - `attempt_revoked`;
     - `raw_bytes` (1..65,536 bytes, null only when `raw_too_large`), `raw_too_large`, `raw_length`, `raw_hash`;
     - `canonical_bytes` and `manifest_hash` (both set iff the body parsed);
     - `disposition` (`settled`, `same_as`, `quarantined`, `held`, `conflict`, `late_after_void`, `refused` or `malformed`), `same_as_submission_id`, `codes text[]`;
     - `first_received_at`, `last_received_at`, `receive_count`.
   - Unique `(attempt_id, account_id, raw_hash)` where the participant is set; unique `(host_id, raw_hash)` where it is not.
   - Trigger: only `last_received_at` and `receive_count` may change, upward; no delete or truncate.
3. **`participant_settlements`.**
   - Columns: PK `(attempt_id, account_id)` with a foreign key to the participant; `outcome`; `submission_id` (null iff `infra_void`); `void_basis` (`exit_evidence`, `operator_quarantine` or `host_declared_lost`; non-null iff `infra_void`); `recorded_hash` (`matched` or `absent`; null for a void); `catalogue_version`; `retention_selection jsonb`; `receipt jsonb`; `settled_at`.
   - Immutable; no truncate.
4. **`pool_collections`.**
   - PK `(attempt_id, container_index, line)`, with a foreign key to `attempt_loot_pool`; `collected > 0`.
   - Trigger: `collected` only grows and stays at or below the pool line's quantity; no delete.
5. **Participants.**
   - New columns: `quarantined_at`, `escalate_at`, `quarantine_reason` and `recorded_manifest_hash`.
     - The reasons are `invalid_manifest`, `malformed_manifest`, `recorded_hash_mismatch`, `inventory_base_changed`, `catalogue_unavailable`, `catalogue_conflict`, `settlement_check_failed` and `host_revoked`.
   - CHECK: the quarantine columns are set iff the state is `quarantined`, except that `quarantined_at` and `quarantine_reason` stay after a settlement.
   - `recorded_manifest_hash` is written once.
   - The transition function is replaced, adding the S9-B16 edges.
6. **Holds.** The transition function is replaced, adding `charged → settled` and `charged → refunded`.
7. **Hosts.** `declared_lost_at`, permanent. A trigger refuses `enabled = true` and secret rotation once it is set.
8. **Exit reports.**
   - Columns `outbox_complete` and `attempt_revoked` are added, each with a `NOT VALID` CHECK that agent rows set them.
   - `outcome` gains `voided`; the CHECK is replaced by name.
9. **Ledger.** The key CHECK is replaced: `settle` and `void_refund` keys must be exactly `<kind>:` plus the entry's `attempt_id`.
10. **`operator_actions`.**
    - Kept: `revalidate`, `participant_void` and `host_declared_lost`, which are already listed.
    - Added: `result_import`, `catalogue_import` and `anomaly_review`. The CHECK is replaced by name.
11. **Privileges.**
    - Column-level INSERT on the new tables.
    - UPDATE of exactly the columns each path writes.
    - No DELETE, and no privilege on the identity sequences.
12. **Invariants (§18.3 additions):**
    - every settled participant has exactly one settlement row, and every settlement row's participant is settled;
    - a settled hold has a `settle:` entry iff its amount is > 0;
    - a refunded hold has a `void_refund:` entry of `+amount` iff its amount is > 0;
    - every non-void settlement references a submission of its participant with `disposition = 'settled'`;
    - no settlement's attempt was revoked before the settlement, unless an operator action covers it;
    - every `exit_evidence` void references an agent exit report with `outbox_complete`, an exit status, `present = false` for the participant and `attempt_revoked = false`;
    - every `pool_collections` row is at or below its pool line and equals the sum of the settled manifests' collected quantities;
    - every `settlement:` or `insurance:` minted item matches a `granted` or `retained` entry in its receipt;
    - every settlement's `catalogue_version` equals its attempt's and has a history row;
    - no quarantined participant lacks an outstanding or reviewed quarantine anomaly;
    - a settled attempt has no unsettled, unreleased participant.

## E. Lock orders (spec §15.3, extended)

**Settle** (`results`, valid):
1. `accounts` `FOR KEY SHARE` (the participant's account);
2. `mission_attempts` `FOR UPDATE`;
3. the host row: plain read only (enabled, secret current, not revoked; no lock, as R7 requires);
4. the revocation check;
5. `attempt_participants` `FOR UPDATE`;
6. read the submissions and settlement;
7. read the catalogue history (no lock; immutable);
8. validate (pure);
9. `energy_holds` `FOR UPDATE`;
10. `inventories` `FOR UPDATE`, then V16;
11. item and operative rows (covered by the inventory lock);
12. `pool_collections` (covered by the attempt lock);
13. `energy_wallets` `FOR UPDATE`;
14. the energy policy lock (shared);
15. clock;
16. ledger;
17. submission and settlement rows;
18. participant and attempt state;
19. the post-apply check.

**Quarantine, held, duplicate, conflict, late after void, refused, malformed (identified)**:
1. `accounts` `FOR KEY SHARE`;
2. `mission_attempts`;
3. `attempt_participants`;
4. the submission and anomaly inserts;
5. participant state.

These take no inventory, wallet or policy lock, except that V16 and S9-B20.4 run after the inventory lock in the settle order. Their quarantine is written in the same transaction, which then skips steps 13–18.

**Refused revoked, malformed (unidentified), submission limit:** anomaly insert only. When the anomaly references an account, `accounts` `FOR KEY SHARE` is taken first, through the foreign key.

**Recorded** (process):
1. `mission_attempts`;
2. `attempt_participants`.

No accounts lock is needed: the process path writes no account-referencing row; the anomaly, when there is one, takes it first.

**Exited with void** (S9-B7):
1. the participants' account ids, read unlocked (immutable since admission);
2. `accounts` `FOR KEY SHARE`, ascending;
3. `mission_attempts`;
4. the host secret, by plain read;
5. `attempt_participants`;
6. for each voided participant, the void order below;
7. the exit report rows.

**Void** (shared by evidence, operator and declare-lost):
1. `accounts` `FOR KEY SHARE`;
2. `mission_attempts`;
3. `attempt_participants`;
4. `energy_holds`;
5. `inventories` (version bump only);
6. for a positive amount, `energy_wallets`;
7. the policy lock;
8. clock;
9. the `void_refund` entry;
10. the settlement row;
11. states.

**Declare-lost:**
- The host row `FOR UPDATE` (stamp, audit) is committed alone.
- Then one void transaction runs per participant.
- The host lock is never held together with an attempt lock, so it never runs in the reverse order to the claim (R7).

**Import result:**
- The same order as quarantine.

**Catalogue register and import:**
- An insert into `catalogue_versions` alone.

**Monitoring sweeps:**
- Attempt or participant rows `FOR UPDATE SKIP LOCKED`, then the anomaly insert (`accounts` `FOR KEY SHARE` through the foreign key, ascending, taken first).
- Auto-drain: the host row `FOR UPDATE` alone.

Rules carried forward:
- one global unique key per transaction after the policy lock (Stage 9 adds none; `settle:` and `void_refund:` are unique per account);
- any transaction that writes account-owned rows takes `accounts` `FOR KEY SHARE` first;
- no energy-touching transaction is retried automatically.

## F. Validation and the trust model

**Who may say what:**

| Evidence | Credential | Accepted as | Never accepted as |
|---|---|---|---|
| Manifest | Host secret: current, not revoked; the attempt's own host; the bound session; the attempt not revoked | facts to validate (V4–V17), then settle | trusted without validation; anything at all for a revoked attempt (409, not stored) |
| Imported manifest | An operator, with `--host-trust-reviewed` | facts to validate, held for an explicit `revalidate` | an automatic settlement |
| Recorded hash | Process credential: current generation, not revoked, attempt live | `result_pending`; the hash a relay must match; a block on any void | a settlement, or a prerequisite for one |
| Exit report with an exit status, `outbox.complete` and `present: false` | Host secret, as above; attempt not revoked; attempt started | positive evidence of absence: a void (S9-B7) | evidence when incomplete, without an exit status, contradicted, or post-revocation |
| Process `failed` after start | Process credential | nothing (409, as in Stage 8) | evidence of absence |
| Lease lapse, outage, restart, timers | none | an alert | a void |
| Host disable or credential revocation | operator | isolation | a void (owner) |

**Exhaustive void conditions** (§11.1, restated):
1. S9-B7's exit evidence.
2. S9-B8's host-loss declaration.
3. An operator void (S9-B9) of a quarantined participant. For a revoked attempt, this is allowed only after its host is declared lost (R8).

Nothing else voids.

**Validation.** Authentication, V1 (the attempt's host) and V2 (the session) answer 401 or 409 (§C). V4–V17 quarantine:
- V4: build and catalogue equal the session's.
- V5: `layoutHash` equals the stored one.
- V5b: `lootTableVersion` and `lootHash` equal the stored ones.
- V6: the operatives are exactly the snapshot's, in slot order.
- V7: the objective ids equal the registered set; states are `active`, `completed` or `failed`; `success` requires every required objective completed.
- V8: owned consumption.
- V9: per-line collection across settled participants.
- V10: conservation.
- V11: bag capacity under the admission catalogue.
- V12: equipment.
- V13: plausibility. The participant's `started_at` is at or before the receipt time; `serverElapsedMs` is at most the hard cap plus slack, provisionally 2 h 5 min, measured from the attempt's `started_at` to now plus a provisional 60 s tolerance.
- V14: integrity is `ok`.
- V15: the canonical hash equals `recorded_manifest_hash` when one is set.
- V16: the inventory base is unchanged, checked under the inventory lock, so it follows the pure validation.
- V17: the outcome is one of the five manifest outcomes. `infra_void` is backend-only and is refused by the parser (422).

## G. Manifest format v1 and the golden vectors (S9-B4)

**Members** (all required; no others):
- `formatVersion`: 1.
- `attemptId`: canonical lowercase UUIDv7.
- `accountId`: canonical Base58.
- `processSessionId`: canonical lowercase UUID.
- `buildRevision` and `catalogueVersion` (`sha256:` + 64 lowercase hex).
- `integrity`: `ok` or a fixed enumeration.
- `outcome`: `success`, `defeat`, `abandoned`, `disconnected` or `expired`.
- `outcomeSequence`, `serverElapsedMs`.
- `layoutHash`, `lootTableVersion`, `lootHash`.
- `objectives[]`: `{objectiveId, state}`, sorted by `objectiveId`.
- `operatives[]`: sorted by slot. Each holds `operativeId` (Base58), `slot`, `alive`, and `consumed[]`, `held[]`, `collected[]` and `equipment[]`, each sorted by its stated key, with duplicates refused.

**Integer bounds** (format bounds; the V-rules apply tighter, provisional limits):

| Field | Bound |
|---|---|
| `serverElapsedMs` | 0..86,400,000 |
| `outcomeSequence` | 0..2,147,483,647 |
| `lootTableVersion` | 1..2,147,483,647 |
| `slot` | 0..7 |
| quantities | 1..2,147,483,647 |
| `containerIndex`, `line` | 0..65,535 |

**Size and shape limits:**
- Lists: `operatives` 1..8; `objectives` 0..64; each per-operative list 0..256 entries.
- Strings: at most 128 UTF-8 bytes, except the fixed-form hashes.
- Nesting depth: 4.
- Body: 65,536 bytes.

All numbers are integer literals: no sign, fraction, exponent or leading zero.

**Golden vectors**, in `Backend/contracts/manifest-v1/` (outside `Assets/`, so Unity's EditMode tests can read them by path in Stage 11):
- `README.md`: the format, the bounds and the canonical rules, with the vectors as the authority where prose and vector disagree.
- `valid/NN-name.input.json`, `.canonical.json` and `.hash.txt`. The canonical file holds the exact bytes, and the hash file holds the `sha256:` line. The set covers:
  - a minimal manifest and a full manifest;
  - every outcome;
  - non-ASCII text, escapes and the maximum bounds;
  - lists in reverse order (canonicalized);
  - whitespace and key-order variants of one manifest (the same canonical bytes and hash).
- `invalid/NN-name.input.json` and `.code.txt`: one vector per refusal class in §C's 422 codes. The set includes:
  - a float, an exponent, `-0`, a leading zero and an over-bound value;
  - a duplicate key, an unknown member and a non-canonical UUID or Base58 id;
  - nesting depth 5, a duplicate list entry and `infra_void`.
- `retention/NN.json`: a salt, account id and lines, with the expected ranking (S9-B12.3).
- `receipt/NN.json`: settlement inputs and the exact receipt bytes.

The domain tests read every vector. A vector change is a format change and needs the owner's approval.

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
2. **The same relay arriving through two connections at once while the operator revalidates** (three-way race). Exactly one settlement; the others answer `duplicate` or are refused by the operator path; one `revalidate` action records its result. Task 8.
3. **A host disabled between a manifest's authentication and its transaction.** The in-transaction re-check returns the uniform 401 and writes nothing; after a rotate and enable, the same relay gets `409 AttemptRevoked` and stores nothing. Tasks 3 and 8.
4. **An API restarted with a new catalogue while results wait.** Each result settles under its admission catalogue, with no 503 loop; a tightened limit quarantines `catalogue_conflict`; a missing version is `202 held` and recovers through `catalogue import`. Tasks 2a, 3 and 8.
5. **A body close to the size cap, with deep nesting, long strings or a non-canonical encoding.** Over the cap or out of shape: `422` with the raw length or bytes kept and never logged verbatim. A non-canonical but valid encoding: canonicalized, both forms stored. Tasks 1 and 3.

---

## Task 0 (after the owner authorizes implementation): record the authorization

**Files:**
- Modify: `Docs/superpowers/specs/2026-10-14-phase-13-backend-design.md` (§0.9 status: confirmed or amended by the owner; it was written with plan revision 2)
- Modify: `Docs/superpowers/plans/2026-10-14-phase-13-backend.md` (§Stage 9 status)
- Modify: `CLAUDE.md` (Stage 9 authorized)
- Modify: this plan (the owner's answers to the "Needs approval" items)

- [ ] **Step 1:** Record the owner's answers exactly as given.
- [ ] **Step 2:** Commit docs only, by explicit paths.

## Task 1: Pure settlement rules (domain) and the golden vectors

**Files:**
- Create: `Backend/src/Accounts.Domain/Settlement/ResultManifest.cs`, `ManifestValidator.cs`, `SettlementPlanner.cs`, `StructuralCheck.cs`, `RetentionRanker.cs`, `Receipt.cs`, `CanonicalJson.cs` (extracted from `CanonicalCatalogueJson` and reused by it, with no behaviour change: its existing tests must stay green)
- Create: `Backend/contracts/manifest-v1/README.md` and the vector files (§G)
- Test: `Backend/tests/Accounts.Domain.Tests/Settlement/*.cs`, including `GoldenVectorTests.cs`, which reads every file under `Backend/contracts/manifest-v1/`

**Interfaces (produced):**
```csharp
public sealed record ManifestOperative(long OperativeId, int Slot, bool Alive, IReadOnlyList<Consumed> Consumed,
    IReadOnlyList<Held> Held, IReadOnlyList<Collected> Collected, IReadOnlyList<EquipmentRef> Equipment);
public sealed record ResultManifest(Guid AttemptId, long AccountId, Guid ProcessSessionId, string BuildRevision,
    string CatalogueVersion, string Integrity, string Outcome, long OutcomeSequence, long ServerElapsedMs,
    string LayoutHash, int LootTableVersion, string LootHash, IReadOnlyList<ObjectiveState> Objectives,
    IReadOnlyList<ManifestOperative> Operatives)
{
    public static ParseResult Parse(ReadOnlySpan<byte> raw);   // never throws on bad input; cap 65536 bytes
}
public abstract record ParseResult
{
    public sealed record Parsed(ResultManifest Manifest, byte[] CanonicalBytes, byte[] ManifestHash) : ParseResult;
    public sealed record Malformed(string Code, Guid? ClaimedAttemptId, long? ClaimedAccountId) : ParseResult;  // §C 422 codes
}

public sealed record SettlementContext(LoadoutSnapshot Snapshot, AttemptFacts Attempt, IReadOnlyList<PoolLine> Pool,
    IReadOnlyList<Objective> Objectives, IReadOnlyDictionary<(int Ci, int Line), int> SettledCollections,
    GameCatalogue AdmissionCatalogue, byte[]? RecordedHash);
public static class ManifestValidator { public static IReadOnlyList<string> Validate(ResultManifest m, byte[] manifestHash, SettlementContext c); }  // "V4".."V17" + detail word, sorted, distinct

public sealed record SettlementPlan(string Outcome, IReadOnlyList<OwnedConsumption> ConsumedOwned,
    IReadOnlyList<MintUnit> Mint, IReadOnlyList<StackGrant> Stacks, IReadOnlyList<EquipChange> Equip,
    IReadOnlyList<XpGrant> Xp, InsurancePayout? Insurance, IReadOnlyList<ExpectedBag> ExpectedBags,
    IReadOnlyDictionary<(int Ci, int Line), int> Collections);
public static class SettlementPlanner
{
    public static SettlementPlan Plan(ResultManifest m, SettlementContext c, InsuranceSnapshot? insurance, byte[] retentionSalt);
}
public static class StructuralCheck { public static IReadOnlyList<string> Check(SettlementPlan p, GameCatalogue current); }  // S9-B20.4
public static class RetentionRanker { public static IReadOnlyList<RetainedLot> Select(...); }   // stored, never recomputed on replay
public static class ReceiptWriter { public static string Settled(...); public static string Void(...); }   // deterministic JSON
```

- [ ] **Step 1: Write the failing tests.**
  - Golden vectors: every `valid/` input gives exactly its canonical bytes and hash; every `invalid/` input gives exactly its code; the retention and receipt vectors match byte for byte.
  - Parser:
    - the claimed ids of a malformed body are returned only when canonical;
    - a 65,537-byte body is `TooLarge`.
  - Validator: one test per V-rule, each with a minimal failing manifest and the expected code:
    - V8: consuming a non-consumable; consuming more than the snapshot holds; loot consumption from a line not collected;
    - V9: summed with the settled collections;
    - V10: each of its three equations, and a snapshot non-stackable held by another operative;
    - V11: via `BagEntries` against the admission catalogue;
    - V12: wrong slot, two items in one slot, a reference to an item not held;
    - V13: elapsed over the cap; `started_at` in the future;
    - V15: a mismatch fails; an absent recorded hash passes.
  - Planner:
    - success grants loot to the collector, and mints `quantity` units for a non-stackable line with refs `…:u0`, `…:u1`;
    - consumption plans delete-or-decrement per (operative, definition);
    - failure without insurance applies consumption only;
    - insured defeat: the `floor` refund goes to the stash; retention is capped and ranked; progress XP takes a single floor and is 0 when nothing is required;
    - the values come from the insurance snapshot even when a different "current" policy is supplied;
    - XP comes from the admission catalogue, not a second catalogue;
    - `alive: false` still gets XP on success;
    - retention is identical across 3 calls, and changes when the salt changes;
    - every receipt field is deterministic, and the receipt never contains the salt (byte search).
  - Structural check: a removed definition, a changed identity kind, a lower max stack and a smaller bag each produce their code.
  - Run: `dotnet test Backend/tests/Accounts.Domain.Tests --filter "FullyQualifiedName~Settlement"`. Expected: FAIL (types missing).
- [ ] **Step 2: Implement** the parser (on `StrictJson`), canonicalization (the extracted `CanonicalJson`), validator, planner, structural check, ranker and receipt writer, and write the vectors.
- [ ] **Step 3: Run** the domain suite. Expected: PASS. The `CanonicalCatalogueJson` tests are unchanged and green.
- [ ] **Step 4: Commit** (`Backend/src/Accounts.Domain/Settlement`, `Backend/contracts/manifest-v1`, `Backend/tests/Accounts.Domain.Tests/Settlement`, `CanonicalCatalogueJson.cs`).

## Task 2: Migration `0009_settlement`, invariants and privileges

**Files:**
- Create: `Backend/db/migrations/0009_settlement.sql`
- Modify: `Backend/src/Accounts.Persistence/Migrations/*` (expected schema version 9), `Backend/tools/invariants.sql` (or wherever §18.3 lives)
- Test: `Backend/tests/Accounts.Integration.Tests/Database/SettlementSchemaTests.cs`, `SettlementPrivilegeTests.cs`, `MigratorTests.cs` (upgrade from a populated schema 8)

- [ ] **Step 1: Write the failing tests.**
  - Each §D object exists.
  - `catalogue_versions`:
    - refuses bytes whose hash differs from the version;
    - refuses update and delete;
    - a new attempt under an unregistered version is refused by the foreign key;
    - a schema-8 attempt without a row survives the upgrade.
  - The settlement row is immutable. Submissions change only their receive columns, upward. Two distinct raw payloads for one participant are two rows.
  - `pool_collections` refuses decreasing and over-pool values.
  - The hold refuses `reserved → settled` and `released → refunded`, and accepts `charged → settled` and `charged → refunded`.
  - Participant edges: `joined → settled` is allowed; `admitted → settled` is refused.
  - `declared_lost_at` is permanent and refuses enable and rotation.
  - Ledger keys `settle:` and `void_refund:` refuse any other attempt id.
  - Exit reports accept `voided`; a new agent row without `outbox_complete` is refused; schema-8 rows survive.
  - Privilege lists match exactly (sorted).
  - Upgrade: a populated schema-8 database migrates, and its invariants hold.
  - The §18.3 additions return zero rows on a fresh database and on seeded fixtures.
  - Every constraint violation's message and DETAIL, read from the client exception, contains no seed, salt, credential or manifest bytes.
  - Expected: FAIL.
- [ ] **Step 2: Write the migration** in §D's order.
- [ ] **Step 3: Run** the schema, privilege, migrator and invariant tests. Expected: PASS. `0000`–`0008` are byte-identical (the migrator's checksum test).
- [ ] **Step 4: Commit.**

## Task 2a: Catalogue history (S9-B20)

**Files:**
- Create: `Backend/src/Accounts.Persistence/Catalogue/CatalogueHistoryStore.cs`
- Modify: `Backend/src/Accounts.Api/Program.cs` (or the `serve` startup) to register; Stage 8's `ready` pool validation (S9-B20.3); the admin CLI (`catalogue import`); `Backend/tests/Accounts.Integration.Tests/Support/MissionKit.cs` (registers its catalogue)
- Test: `Database/CatalogueHistoryTests.cs`

**Interfaces:**
```csharp
Task RegisterAsync(GameCatalogue catalogue, CancellationToken ct);            // insert if absent; verify; refuse removed ids or changed identity kinds
Task<CatalogueLookup> LookupAsync(string version, CancellationToken ct);       // Found(GameCatalogue) | Unavailable(reason); cached by version
```

- [ ] **Step 1: Write the failing tests.**
  - Registration is idempotent.
  - A catalogue that drops an id or flips an id's kind is refused, and `serve` exits with its new code.
  - Lookup verifies the hash and returns `Unavailable` for a missing row.
  - `ready` for an attempt admitted under catalogue X, with the API on Y, validates against X.
  - `catalogue import` verifies, inserts and audits, and refuses a mismatched file.
  - Expected: FAIL.
- [ ] **Step 2: Implement.**
- [ ] **Step 3: Run** the class and the Stage 8 ready tests. Expected: PASS. Stage 8's `CatalogueMismatch` test is rewritten to the genuinely-unavailable case and recorded as a ruling.
- [ ] **Step 4: Commit.**

## Task 3: Settlement store, the results route and receipts

**Files:**
- Create: `Backend/src/Accounts.Persistence/Settlement/SettlementStore.cs`, `ReceiptStore.cs`, `SettlementSql.cs`; `Backend/src/Accounts.Api/Hosts/ResultEndpoints.cs`; the receipt route in `Backend/src/Accounts.Api/Attempts/AttemptEndpoints.cs`
- Test: `Backend/tests/Accounts.Integration.Tests/Database/SettlementTests.cs`, `ResultResponseTests.cs`, `ReceiptTests.cs`, `Support/ManifestKit.cs` (builds valid manifests from a `MissionKit` attempt brought to `running`)

**Interfaces:**
- Consumes: Task 1's parser, validator, planner, structural check and receipt writer; Task 2a's lookup; Stage 8's `HostStore` authentication (`HostSecretHashOf`) and `MissionKit`.
- Produces:
  ```csharp
  public abstract record ResultOutcome
  {
      public sealed record Settled(string Outcome) : ResultOutcome;
      public sealed record Duplicate(string Outcome) : ResultOutcome;
      public sealed record Quarantined(IReadOnlyList<string> Codes) : ResultOutcome;
      public sealed record Held : ResultOutcome;
      public sealed record Refused(string Code) : ResultOutcome;    // Conflict | AlreadySettledAsVoid | AttemptRevoked | WrongAttempt | InvalidState | SubmissionLimit
      public sealed record Malformed(string Code) : ResultOutcome;
      public sealed record Unauthenticated : ResultOutcome;
  }
  Task<ResultOutcome> SubmitAsync(string hostId, byte[] secretHash, byte[] raw, ParseResult parsed, CancellationToken ct);
  Task<ReceiptView> ReceiptAsync(long accountId, Guid attemptId, CancellationToken ct);
  ```
  Transient database failures surface as the existing `DbFailure` exception, which the route maps to 503.

- [ ] **Step 1: Write the failing tests.**
  - Each outcome settles once: `success`, `defeat`, `abandoned`, `disconnected` and `expired`.
  - Consumption deletes an exactly consumed stack and decrements a partly consumed one; nothing ever writes `quantity = 0`; insufficient stock aborts with nothing written.
  - A non-stackable line ×2 mints two items with the expected refs; a replay mints none.
  - Insured defeat writes refunds and retained lots to the stash and stores the selection; uninsured defeat applies consumption only.
  - XP lands, capped.
  - The hold becomes `settled`, with one `settle:` entry for a positive hold and none for zero cost.
  - The inventory version is bumped once.
  - `pool_collections` sums across two participants (co-op-shaped, with direct SQL).
  - Attempt states: the last participant gives `running → ending → settled`, or `unresponsive → settled`.
  - The participation lock is released only by the settlement.
  - **Repeated settlement after a committed result** (owner item 16):
    - the identical raw body returns `Duplicate` with nothing changed and `receive_count` 2;
    - a whitespace or key-order variant returns `Duplicate` and adds one `same_as` row with its raw bytes;
    - both still hold after an API restart (a new host and store instance);
    - both still hold after the process credential has ended.
  - Conflict: a different canonical hash returns `Conflict`, with the submission stored as `conflict`, an anomaly, and the settlement and receipt unchanged.
  - Wrong host or session returns `WrongAttempt`, with the submission stored as `refused`.
  - A disabled host, or a secret rotated between authentication and the transaction, returns `Unauthenticated` with nothing written.
  - **A revoked attempt** (disable, then rotate and enable, then relay) returns `AttemptRevoked`. No submission row is written, and one `result_refused_revoked` anomaly holds no payload bytes (byte search).
  - A participant in `admitted` or `released` gets `InvalidState`, stored as `refused`.
  - Malformed bodies: each 422 code; an identified participant is quarantined (`malformed_manifest`); an unidentified body is stored once per host and raw hash.
  - **Catalogue upgrade during a mission** (owner item 16):
    - admit under X, start, then restart the API on Y: the relay settles under X (XP from X), and the settlement row records X;
    - a Y with a smaller bag gives `Quarantined` with `catalogue_conflict` and nothing applied;
    - a schema-8 attempt whose version has no row gives `Held`; `catalogue import` and `revalidate` then settle it.
  - V16: a changed inventory base quarantines.
  - The post-apply bag check: a corrupted plan (a test-only mutation through an internal seam in the test assembly) rolls back and ends `Quarantined` with `settlement_check_failed`.
  - The submission cap: the 17th distinct body gets `SubmissionLimit`, and nothing is stored but the anomaly count.
  - Receipts: 202 per state, including `under_review` for a revoked attempt; 200 byte-identical across calls, across an API restart and across a duplicate relay; 404 for a foreign account; 409 for a released participant.
  - HTTP (`ResultResponseTests`): every row of §C, with its status, body and committed record. Each 503 class is tested with nothing committed. No body is echoed.
  - Expected: FAIL.
- [ ] **Step 2: Implement** `SubmitAsync` in §E order:
  1. authenticate;
  2. lock `accounts` `FOR KEY SHARE`, then the attempt;
  3. re-check the host secret by plain read;
  4. check revocation;
  5. lock the participant;
  6. look up the existing settlement and submissions, and the cap;
  7. look up the admission catalogue;
  8. build the context (pool, objectives, `pool_collections`, the recorded hash, the snapshot);
  9. validate; on errors, take the quarantine path (Task 4);
  10. lock the hold and the inventory; check V16 and the structural limits;
  11. read the salt from `attempt_secrets`;
  12. apply the plan;
  13. lock the wallet, take the policy lock, materialize and write the ledger entry;
  14. insert the submission (`settled`) and the settlement; update the participant and attempt states;
  15. run the post-apply bag check, then commit.

  Map `40001`, `40P01`, `55P03`, `57014` and `57P01` to 503, with no retry.
- [ ] **Step 3: Run** the settlement, response and receipt classes, then the whole integration suite. Expected: PASS.
- [ ] **Step 4: Commit.**

## Task 4: Quarantine, `recorded` and the hash evidence

**Files:**
- Modify: `SettlementStore.cs`, `Backend/src/Accounts.Persistence/Attempts/ProcessStore.cs`, `Backend/src/Accounts.Api/Hosts/ProcessEndpoints.cs`
- Test: `Database/QuarantineTests.cs`, `Database/RecordedTests.cs`

- [ ] **Step 1: Write the failing tests.**
  - Each V4–V17 failure stores the submission (raw and canonical) as `quarantined` with its codes, moves the participant to `quarantined` (with its reason and `escalate_at = now + 24 h`) and raises `result_quarantined`. The inventory, wallet, hold and items are untouched, and the lock is kept.
  - **Sticky quarantine (S9-B21):** a later *valid* distinct manifest is stored as `quarantined` and settles nothing. A second invalid one adds a row. Earlier rows are byte-identical.
  - `recorded`:
    - `joined → result_pending` with the hash stored;
    - an identical repeat gets 200;
    - a different hash gets 409 and raises `recorded_hash_mismatch`;
    - a revoked or ended credential gets 401.
  - A relay whose hash differs from the recorded one quarantines (V15, `recorded_hash_mismatch`).
  - A `recorded` call after a settlement with a different hash, using a still-live credential in a two-participant fixture, gets 409 and raises the anomaly; the settlement is unchanged.
  - **Crash before the hash notification** (owner item 16): no `recorded` call; `exited` with `present: true` gives `result_pending`; the relay settles with `recorded_hash = 'absent'` and `result_without_recorded_hash`; no void happens at any point.
  - When every participant is `result_pending` or settled, the attempt is `ending`.
  - Expected: FAIL.
- [ ] **Step 2: Implement** the quarantine and hold writes in the same transaction (S9-B2), and `RecordedAsync` (§E).
- [ ] **Step 3: Run** the affected classes and the suite. Expected: PASS.
- [ ] **Step 4: Commit.**

### Checkpoint 1: independent review (most capable model)

Scope: Tasks 1–4. Conservation and provenance; minting; the energy edges; lock order against §E and the Stage 6–8 rules; the trust model (§F, S9-B1); the §C response matrix and its committed records; the catalogue history; the secrecy of the salt and of the manifests. Critical and Important findings are fixed test-first before Task 5. Minor findings are recorded.

## Task 5: The positive-evidence void and the late result after a void

**Files:**
- Create: `Backend/src/Accounts.Persistence/Settlement/VoidStore.cs`
- Modify: `Backend/src/Accounts.Persistence/Hosts/HostStore.cs` (`ExitedAsync`), `Backend/src/Accounts.Persistence/Attempts/ExitReports.cs` (the `outbox` field and `Same`), the exit route's request type
- Test: `Database/EvidenceVoidTests.cs`

**Interfaces:**
```csharp
// Locks and order per §E; the caller holds accounts KEY SHARE and the attempt lock.
Task<VoidResult> VoidParticipantLockedAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid attemptId, long accountId,
    string basis, CancellationToken ct);
```

- [ ] **Step 1: Write the failing tests.**
  - `exited` with an exit status, `outbox.complete: true` and `present: false` for a started participant voids it:
    - the hold becomes `refunded`, with one `void_refund:` entry of `+amount` (none for zero cost);
    - the wallet balance is restored;
    - nothing is consumed, no loot or XP is granted, and the insurance is unused;
    - equipment is unchanged, the inventory version is bumped and the lock is released;
    - the receipt says `infra_void`; an `infra_void` anomaly names the host, build and exit code;
    - the exit report's `outcome` is `voided`.
  - **Not proof** (each voids nothing, stores the report and raises `exit_evidence_inconclusive`):
    - `outbox` absent;
    - `outbox.complete: false`;
    - no exit status;
    - the participant missing from the list (with `complete: false`; with `complete: true` the report is `400 IncompleteResults`).
  - Contradiction: a recorded hash or a stored submission with `present: false` voids nothing and raises `exit_evidence_contradiction`.
  - `present: true` gives `result_pending` and no void.
  - Mixed presence across two participants (co-op-shaped) voids only the absent one.
  - A repeat report is idempotent; a conflicting one (including a later `complete: true` after a stored `complete: false`) is `ConflictingReport` and voids nothing.
  - A revoked attempt (disable, rotate, enable, `exited`) stores the report with `attempt_revoked = true`, frees the slot, voids nothing and raises `exit_evidence_post_revocation`.
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
- Modify: `Backend/src/Accounts.Api/Admin/AdminCommand.cs`, `AdminArguments.cs`, `Backend/README.md` (the operator runbook: quarantine recovery, revoked attempts, result import, catalogue import, host loss)
- Test: `Database/RecoveryTests.cs`, `Database/RecoveryCliTests.cs`

- [ ] **Step 1: Write the failing tests.**
  - `revalidate --submission <id>`:
    - re-runs validation on that stored submission's canonical bytes, under the admission catalogue. The host is not re-authenticated: this is the operator path;
    - on a pass, it settles through the same transaction code as Task 3, and the `revalidate` action records the submission, hash and `settled`;
    - on a failure, it records `failed` with the codes, and the state is unchanged;
    - for a revoked attempt or a `host_revoked` hold, it requires `--host-trust-reviewed`, or it exits 64;
    - a submission of another participant is refused.
  - `import-result`:
    - refused unless the attempt is revoked and `--host-trust-reviewed`, `--reason` and `--evidence` are given;
    - it stores the file (`source = 'operator_import'`, the raw and canonical forms) and quarantines (`host_revoked`), but never settles by itself;
    - importing the same file twice adds no row;
    - a file whose canonical hash equals a pre-revocation recorded hash is shown as matching in `attempt show`.
  - `void-participant`:
    - refused without `--reason`, `--outbox-checked` and `--evidence`;
    - refused without `--revalidated` when a submission is stored (a `revalidate` action after the latest submission must exist);
    - refused for a participant that is not quarantined;
    - refused for a revoked attempt's participant before its host is declared lost, and afterwards without `--host-trust-reviewed` (R8);
    - otherwise it voids with basis `operator_quarantine`, with the action and its checks in the same transaction.
  - `declare-lost`:
    - refused for an enabled host, a host with an unrevoked secret, a fresh heartbeat, or a missing flag;
    - otherwise it stamps the host and voids every started, unsettled, non-quarantined participant, one action each under one correlation id;
    - a re-run resumes; enable and rotate are refused forever afterwards;
    - quarantined participants are untouched.
  - `catalogue import`: covered in Task 2a; here only its confirmation and audit.
  - `anomaly list` and `anomaly review` work, and a review is audited.
  - `attempt show` prints no secret, salt or credential, and no raw payload without `--submission <id> --raw`. It marks each piece of evidence as pre- or post-revocation.
  - Every verb asks for confirmation (exit 64 without `--yes` in a non-interactive run, matching the existing verbs).
  - A failure partway through (an injected fault) leaves no partial state for that participant.
  - Expected: FAIL.
- [ ] **Step 2: Implement.**
- [ ] **Step 3: Run** the classes and the suite. Expected: PASS.
- [ ] **Step 4: Commit.**

## Task 7: Monitoring sweeps

**Files:**
- Create: `Backend/src/Accounts.Persistence/Settlement/MonitoringStore.cs`
- Modify: `Backend/src/Accounts.Api/Attempts/AttemptDeadlineWorker.cs`, `AttemptSettings.cs` (provisional values and their environment variables)
- Test: `Database/MonitoringTests.cs`

- [ ] **Step 1: Write the failing tests.**
  - Escalation:
    - at `escalate_at`, one `quarantine_escalation` anomaly is raised and `escalate_at` moves forward 24 h;
    - repeats are deduplicated per period;
    - **the state, holds, inventory and lock never change**, through 3 periods simulated with database time.
  - The unresponsive alert, at 10 min and repeated each period, changes no state. The same holds for the revoked-attempt alert, every 24 h.
  - Auto-drain:
    - voids on 3 distinct attempts within the window drain the host and raise `host_auto_drained`;
    - **3 voided participants of one attempt do not** (distinct attempts are counted);
    - operator and declare-lost voids are not counted;
    - a threshold of 0 never drains;
    - its running attempts and process credentials keep working (drain semantics);
    - nothing is disabled, revoked, voided or refunded by it (row digests before and after);
    - re-enabling stays an operator action.
  - Two workers race, and the outcome is still one anomaly per period.
  - Expected: FAIL.
- [ ] **Step 2: Implement.**
- [ ] **Step 3: Run** the class and the suite. Expected: PASS.
- [ ] **Step 4: Commit.**

## Task 8: Concurrency and fault injection

**Files:**
- Test: `Database/SettlementConcurrencyTests.cs`, `Database/SettlementFaultTests.cs`, `Support/FaultTriggers.cs` (test-only: superuser-installed triggers in the test database that raise on a named table and operation, plus a pausing trigger that waits on an advisory lock; no production seam)

**Required race and crash tests** (owner item 16), each named in "As built":
- **Disable racing settlement.** 50 rounds, with the pausing trigger placing the disable before, during and after the settlement's attempt lock. Either the settlement commits first and stays final, or the relay is `Unauthenticated` with nothing written. Then, after rotate and enable, the relay is `AttemptRevoked` with nothing stored. There is never a settlement after the disable commits.
- **Result submission racing the infrastructure void.** 50 rounds of a relay against `exited` with complete-outbox `present: false`. Exactly one wins: settled with the real outcome, or voided. The loser gets `Duplicate` or `AlreadySettledAsVoid`, or a stored report with `outcome = 'none'` (the submission blocks the void). The invariants hold.
- **Crash before the hash notification.** Covered at the store level in Task 4. Here: the process credential's connection is terminated (`pg_terminate_backend`) inside `recorded`, so nothing is committed; then the relay settles with `recorded_hash = 'absent'`, and no void follows.
- **Catalogue upgrade during a mission.** Admissions under X run concurrently with an API restart on Y (registration racing lookups). Every result settles under X, and none is judged under Y.
- **Repeated settlement after a committed result.** 20 identical manifests in parallel give one `Settled` and 19 `Duplicate`; there is one settlement, one `settle:` entry and the minted items once; the invariants hold. Also the lost-response case below.

- [ ] **Step 1: Write the tests.** Each must fail against a deliberately broken variant before it passes, which proves it can fail. Record which variant was used.
  - The five required tests above.
  - Two different manifests in parallel: one settles; the other gets `Conflict`.
  - Settlement racing `declare-lost`'s participant void: exactly one.
  - Settlement racing `revalidate`: exactly one settlement.
  - Settlement racing an inventory edit and a pick on the same account: the edit and the pick are `InventoryLocked` until the settlement commits, and succeed afterwards.
  - Settlement racing an energy grant and a policy publication: no deadlock (lock-order proof; a queued publication drains).
  - **Fault after every step:** a raising trigger on each written table in turn: `item_stacks` delete and update, `item_instances` insert, `operatives` update, `pool_collections`, `energy_holds`, `energy_ledger`, `inventories`, `result_submissions`, `participant_settlements`, `attempt_participants`, `mission_attempts`. Each leaves the database byte-identical to before (a table-by-table digest), and a retry after removing the trigger settles once.
  - **`pg_terminate_backend`** of the settling connection while it waits on the pausing trigger: nothing is committed; the retry settles once.
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
  7. it removes the container and volume, and verifies that they are gone and that the shared test container's ID and uptime are unchanged.
- [ ] **Step 2: Run** the smoke test, the crash drill and the restore drill. Expected:
  - 0 failures, invariants hold, no secret in any transcript;
  - the off-host probe still shows only port 22 open;
  - Docker containers, volumes and listeners match before and after, except while the drill runs.
- [ ] **Step 3: Commit.**

## Task 10: Secrecy, docs as built, final review and stop

**Files:**
- Test: `Database/LogSecrecyTests.cs` (extended across settlement, quarantine, void, import and the recovery verbs: captured logs, anomaly details, `operator_actions`, receipts and CLI output contain no seed, salt, host secret, process credential, ticket or raw manifest)
- Modify: the spec (§0.9 "As built"), the stage plan, `CLAUDE.md`, this plan ("As built"), `Backend/README.md`

- [ ] **Step 1:** Run the extended secrecy test. Expected: PASS.
- [ ] **Step 2:** Run the full suite once on the VM runner and record the counts and duration. Run the concurrency and fault classes once more.
- [ ] **Step 3:** Dispatch the final whole-branch review (most capable model) with this plan, the spec and the review focus. Fix Critical and Important findings test-first in one pass, re-run the suite, and record minors.
- [ ] **Step 4:** Write "As built" (counts, the transcripts' locations, rulings, obligations). Commit docs by explicit paths. **Stop and report**: no merge, push or deployment without the owner.

## Acceptance criteria

- Spec B10, B11, B14, B16 and the settlement half of B5 pass at the HTTP level with hand-written manifests.
- Every §A point and every §C row has a passing test named in the "As built" section.
- The five required race and crash tests (owner item 16) pass.
- No path voids without one of §F's three conditions. A lapsed lease, a timer, a restart, an outage, an incomplete outbox scan, a missing recorded hash, a disable or a revocation never voids (tests).
- Nothing about a revoked attempt settles or voids without an operator command (tests).
- The golden vectors pass, and `Backend/contracts/manifest-v1/README.md` describes them.
- The full suite is green on the VM runner. The concurrency and fault classes are green 3 times running. Smoke, the crash drill and the restore drill pass at schema 9.
- `0000`–`0008` are byte-identical. Only `Backend/`, `Docs/` and `CLAUDE.md` changed. The owner's two Unity files and `.claude/` are untouched.
- Both independent reviews report no open Critical or Important finding.

## Out of scope

- Any agent, Unity or client code (Stages 10–16). Co-op enablement. Reconciling a void against a later valid result. Manifest correction tooling. Incremental reconciliation. Policy for existing inventories when a newer catalogue tightens a limit. Deployment.

## Obligations this stage hands on (proposed)

- **Stage 10 (agent):**
  - relay an outbox file only after its mission unit has exited (S9-B23);
  - relay every outbox file before reporting `exited`;
  - send `exited` only with an exit status, and `outbox.complete: true` only after a full, successful outbox scan that lists every started participant;
  - when a scan can't complete, retry it, and send `complete: false` only when it is definitively impossible;
  - handle each answer exactly as §C says, recording relay state in its own directory and never deleting a relay file before Phase 14's cleanup;
  - keep `AttemptRevoked` files for the operator;
  - provide the trusted emergency stop (R8).
- **Stage 11 (Unity core):**
  - produce manifests in format v1, canonical by construction;
  - pass every golden vector in `Backend/contracts/manifest-v1/` in EditMode tests;
  - compute the canonical `manifestHash` for `recorded`.
- **Stage 12 (server):**
  - write and `fsync` the outbox file, then call `recorded` with its canonical hash before telling the client "result saved", and before exiting (S9-B23);
  - exit after informing its clients, without polling settlement (S9-B6);
  - treat a 401 as a stop, keeping the outbox file.
- **Stage 16 (client):** poll `GET /v1/attempts/{id}/receipt` and show only committed rewards; show "under review" for `under_review`.
- **Phase 14:**
  - void-versus-late-result reconciliation;
  - manifest correction and resubmission;
  - outbox and submission cleanup;
  - a policy for inventories when a newer catalogue tightens a limit;
  - incremental reconciliation.
