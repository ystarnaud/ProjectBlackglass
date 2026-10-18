# Phase 13 Stage 9: settlement, receipts, quarantine and protective voids — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans (recommended, per the owner's lean-execution instruction) or superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

Date: 2026-10-17. **Revision 3: the owner reviewed revision 2 (`cdeb5f5`) and authorized implementation on 2026-10-17, with the final rulings recorded in "Revision 3" below. Where revision 3 and an older section disagree, revision 3 wins; the affected sections are amended in place and marked "(rev 3)".**
- **Base:** `main` @ `e65370b` (Stage 8 merged and pushed; schema version 8; backend 713 domain and 1,602 integration tests).
- **Branch:** `phase13-stage9`. Implementation commits stay local until the owner approves a push.
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

## Revision 3: the owner's final rulings (2026-10-17; implementation authorized)

Every revision-2 decision not named here stands as approved (B2–B15, B17, B18).

| Item | Owner ruling | What changes |
|---|---|---|
| B1 | Approved as revision 2 reads it | No payload is stored for a revoked attempt on the normal route; pre-revocation evidence stays immutable; a newly rotated host secret never restores the attempt's process authorization; post-revocation results enter only through `import-result` and `revalidate` |
| R8 targeted recovery | Revised | `host declare-lost` stays for confirmed permanent host or outbox loss. **Added:** an audited per-participant protective void for a revoked attempt, basis `operator_revoked_unrecoverable`, without declaring the host lost (S9-B9, Task 6). Neither a disable nor suspected compromise alone voids anything |
| B16 | Option (a) approved | `joined → settled` and `left → settled`; no fabricated `result_pending`; the settlement row's `outcome` and `void_basis` tell a settlement from a void; `quarantined → settled` only through operator recovery |
| B19 | Option (a) approved | Minimal agent answers; no receipt, inventory, payout or minted-item detail to the agent |
| B21 | Approved | Sticky quarantine, even for a later submission that passes every ordinary check |
| B22 | Approved, provisional | 16 distinct submissions per participant (`BLACKGLASS_RESULT_SUBMISSIONS_MAX`), 64 unidentified per host per 24 h (`BLACKGLASS_RESULT_UNIDENTIFIED_MAX`). An identical retry returns its recorded outcome even past the cap. `import-result` is the audited path for evidence beyond the cap and never counts against it |
| B23 | Revised | The agent may relay a published manifest while its process is alive. §H defines the publish/visibility contract for Stages 10–12. A late recorded hash that contradicts an unsettled participant's evidence quarantines it; one that contradicts a settlement raises a durable anomaly and leaves the receipt unchanged; a revoked credential never authenticates |
| B20 | Kept, with two corrections | (1) A newer catalogue may retire definitions; registration refuses only a change of an id's identity kind. (2) The current catalogue never re-judges an admitted mission: the S9-B20.4 structural check and the `catalogue_conflict` reason are removed. Over-capacity inventories are preserved; Stage 5's moves out of an over-capacity bag stay allowed |
| Lock order | Added check | Every path that inserts `anomaly_events`, `operator_actions` or another account-referencing row takes `accounts` `FOR KEY SHARE` before attempt, participant, inventory or wallet locks (§E, rev 3). Concurrency tests against suspension, credential rotation, energy grants and policy publication (Task 8) |
| Incomplete outbox scans | Added | A first report with `outbox.complete: false` can be followed by one immutable supplementary outbox observation (`attempt_outbox_observations`), never overwriting the report (S9-B7, rev 3; Task 5) |
| Crash drill | Clarified | Its own container on a temporary internal Docker network, removed at the end; no host networking or daemon change (S9-B17, rev 3; Task 9) |

**Rulings recorded during Task 0 (implementation choices that follow from the above):**
- **T0-1.** A late `recorded` for a solo attempt arrives after the attempt is `settled`, when the Stage 8 rule (R4) has already ended the process credential. So the `recorded` route alone authenticates the current, unrevoked credential of a `settled` attempt (never a `cancelled` or revoked one). It can only compare: a match answers 200 and changes nothing; a mismatch answers 409 and raises the anomaly. *Cost if wrong:* a contradiction after a solo settlement goes unseen, as revision 2 accepted.
- **T0-2.** A `recorded` hash that differs from an earlier recorded hash, or from an unsettled participant's stored submission, quarantines the participant (`recorded_hash_mismatch`) in the same transaction that commits the 409's anomaly. Revision 2 only answered 409.
- **T0-3.** `import-result` is allowed for any started participant, not only for a revoked attempt. For a revoked attempt it needs `--host-trust-reviewed`. It quarantines an unsettled participant (`host_revoked` for a revoked attempt, otherwise the new reason `operator_import`). For a settled participant it only stores the evidence (`same_as` or `conflict`, plus an anomaly). This is B22's "beyond the cap" path.
- **T0-4.** The supplementary outbox observation (incomplete scans) uses the existing `exited` route: a later report with the same exit status and `outbox.complete: true` after a stored `complete: false` is stored as the attempt's one observation and evaluated for voids under S9-B7. Any other differing report stays `409 ConflictingReport`.
- **T0-5.** Catalogue history enforcement: the `NOT VALID` foreign key from `mission_attempts.catalogue_version`. Test fixtures that insert attempts by SQL register a real catalogue first; nothing in production changes beyond `serve`'s registration.

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

### S9-B1. Revoked credentials and revoked attempts. *Approved (rev 3).*

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
  - Otherwise, **(rev 3)** an audited per-participant void with basis `operator_revoked_unrecoverable` (S9-B9), or `host declare-lost` for confirmed permanent host or outbox loss. A newly rotated host secret never restores the attempt's process authorization.
- A participant of a revoked attempt shows `under_review` on its receipt. A repeating `revoked_attempt_unsettled` alert runs until it is settled (S9-B18).

*Owner (rev 3):* "not accepted" means "not stored" (approved). R8's literal "declare-lost only" is revised: the per-participant `operator_revoked_unrecoverable` void is added, and the host can be recovered and re-enabled without being declared lost.

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

### S9-B5. The process's recorded hash is supporting evidence. *Approved; amended by B23 (rev 3).*

`POST /server/v1/attempts/{aid}/participants/{acc}/recorded {manifestHash}` is sent with the process credential after the outbox `fsync`.
- It moves the participant from `joined` or `left` to `result_pending` and stores `recorded_manifest_hash` once.
- **It is not a prerequisite.** A durably written outbox result relayed without any recorded hash (the process died after its write) is validated and settled normally. The settlement row records `recorded_hash = 'absent'`, and a low-severity `result_without_recorded_hash` anomaly is raised for monitoring.
- **A mismatch is never accepted silently:**
  - a relay whose canonical hash differs from a recorded hash is quarantined (V15, `recorded_hash_mismatch`);
  - a `recorded` call whose hash differs from an earlier recorded hash, a stored submission or a settlement for that participant gets `409 ConflictingReport` and raises `recorded_hash_mismatch`. **(rev 3, T0-2)** An unsettled, unquarantined participant is quarantined (`recorded_hash_mismatch`) in the same transaction. A settlement that already committed stays final and its receipt unchanged; the anomaly is the operator alert.
- **A missing notification alone never causes a void.** The void rule (S9-B7) does not look at it, except that a recorded hash *blocks* a void: it proves a result file was written.
- An agent `exited` with `present: true` also moves the participant to `result_pending`.

### S9-B6. No process-side settlement polling. *Approved.*

The process tells its clients "pending" and exits. The client polls `GET /v1/attempts/{id}/receipt`, authenticated by its own bearer session. R4 stays intact.

### S9-B7. The positive-evidence void. *Approved; incomplete-scan recovery added (rev 3).*

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
- **(rev 3) Incomplete scans can be completed later.** When the stored agent report has `outbox_complete = false`, a later report with the same `exitCode` and `signal`, `outbox.complete: true` and every started participant listed is stored as the attempt's **outbox observation** (`attempt_outbox_observations` and `attempt_outbox_observation_results`, insert-only, one per attempt). It never overwrites the report. Conditions 1-3 and 6 are re-checked under the locks; condition 4 is the stored report's exit status, condition 5 the observation. A participant the first report listed `present: true` but the observation lists `present: false` is a contradiction (`exit_evidence_contradiction`, no void). An identical repeat of the observation returns its stored outcome; any other report is `409 ConflictingReport`. The answer's `outcome` is the observation's (`voided` or `none`).

### S9-B8. Host-loss declaration. *Approved.*

`admin host declare-lost` (for confirmed permanent host or outbox loss) is refused unless all of these hold:
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
- `admin attempt import-result <aid> <acc> --file <path> --reason --evidence`, plus `--host-trust-reviewed` for a revoked attempt **(rev 3, T0-3)**. Allowed for any started participant; never counted against S9-B22's cap. An unsettled participant is quarantined (`host_revoked` for a revoked attempt, else `operator_import`); for a settled one the file is stored as evidence only (`same_as` or `conflict`, with an anomaly).
- `admin attempt void-participant <aid> <acc> --reason --outbox-checked --evidence`, in one of two modes:
  - **quarantine mode** (basis `operator_quarantine`): the attempt is not revoked and the participant is quarantined; `--revalidated` when it has a stored submission;
  - **(rev 3) revoked mode** (basis `operator_revoked_unrecoverable`): the attempt is revoked, the participant started and is not settled. It also needs `--host-trust-reviewed` (the host's trust and recovery status were reviewed), `--unrecoverable` (the result is verified unrecoverable or can't be accepted safely) and, when any submission is stored, `--revalidated`. The host need not be declared lost.
  - In both modes: `--revalidated` means a `revalidate` action recorded after the participant's latest stored submission, with result `failed`; a settled participant is refused, so no settlement or recoverable result is overwritten; the findings (`--evidence`, the flags, the submission ids and hashes seen) are written to the action's `checks`.
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

### S9-B16. Participant and attempt state edges. *Approved: option (a) (rev 3).*

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

### S9-B17. Destructive outage tests on a throwaway container. *Approved; networking clarified (rev 3).*

`Backend/tools/vm-crash-drill.sh` runs outside the runner, like the smoke test.
- It creates a temporary internal Docker network (`docker network create --internal`), then starts a throwaway `blackglass-crashdb` container on `bg-dev-services01` attached to it, with its own volume, no published port and the test cluster's settings, and migrates it.
- The runner container joins the same network and reaches the database by name, so restarting the database never strands the runner in a dead network namespace (no `--network container:`).
- It drives settlements at it, `docker kill`s it mid-load, restarts it, and runs the invariant queries.
- It removes the containers, the volume and the network, and verifies they are gone. No host-network exposure and no Docker daemon configuration change.

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
  - `catalogue_unavailable`, `submission_limit`, `host_auto_drained` (rev 3: `catalogue_conflict` removed).

### S9-B19. What the agent is told. *Approved: option (a) (rev 3).*

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

### S9-B20. Catalogue history and settlement under the admission catalogue. *Approved with corrections (rev 3).*

- **S9-B20.1. Immutable history.** New table `catalogue_versions (version, bytes, registered_at)`.
  - `version` is the existing `sha256:<hex>` of the exporter's bytes.
  - A CHECK enforces `version = 'sha256:' || encode(sha256(bytes), 'hex')`.
  - Rows are insert-only: a trigger refuses update, delete and truncate.
- **S9-B20.2. Registration.**
  - At startup, `serve` inserts its validated catalogue if absent, then verifies the stored row.
  - 0009 adds a foreign key from `mission_attempts.catalogue_version` to the history, `NOT VALID`, so schema-8 rows are not checked but every new admission is. An attempt can therefore only be admitted under a catalogue the history holds.
  - **(rev 3)** Registration refuses only a catalogue that changes the identity kind (stackable or instance) of an item id present in any registered version; `serve` then exits with a new code. A newer catalogue may **retire** a definition (leave it out); every published version stays in the history for historical settlement. Item ids are permanent keys (CLAUDE.md) and are never reassigned to another kind.
- **S9-B20.3. Lookup.**
  - Settlement loads the attempt's `catalogue_version` from the history, verifies the hash in the application, and validates and caches the parsed catalogue by version.
  - Validation (V4–V17), the plan, XP and capacity use that admission catalogue; the current catalogue is never applied in its place.
  - Stage 8's `ready` is switched to the same lookup, so its `409 CatalogueMismatch` remains only for a genuinely unavailable version. *Alternative:* keep Stage 8's behaviour, in which case an attempt admitted just before an API upgrade can't start.
- **S9-B20.4. (rev 3) The current catalogue never re-judges an admission.** Removed: revision 2's structural check and the `catalogue_conflict` reason.
  - A historical mission is never quarantined because the current catalogue has a smaller bag, a smaller maximum stack, a retired definition or newer progression or balance values. Validation, rewards and capacity use the admission catalogue.
  - Permanent integrity stays enforced: database constraints, ownership, non-negative quantities, valid item identities (each definition exists in the admission catalogue, with its kind) and exact conservation.
  - An inventory left over the current capacity is preserved. Stage 5's approved recovery stays available: a bag over capacity may stay over but never grow, and retired definitions may move to the stash (S5-D7).
- **S9-B20.5. Genuinely unavailable.** The version has no history row, its bytes fail verification, or they no longer parse.
  - The submission is stored, the participant is quarantined (`catalogue_unavailable`) and `catalogue_unavailable` is raised. The answer is `202 {disposition:"held"}`.
  - The result is never judged against another catalogue, and the agent stops resending it.
  - Recovery: `admin catalogue import <file>` registers the version (verified, audited), then `attempt revalidate`.

*Cost if wrong:* a catalogue table that grows by one row per exported catalogue, which is small.

### S9-B21. A quarantine is never cleared automatically. *Approved (rev 3).*

Once a participant is quarantined, any later distinct submission is stored (disposition `quarantined`, with its codes, even when it would validate) and answered `202`. Only `attempt revalidate --submission <id>` or an operator void ends the quarantine.

*Why:* a normal process writes exactly one result per participant, so a second distinct manifest is itself an anomaly. Settling it automatically would let a faulty or compromised host iterate on rejected manifests.

*Alternative:* revision 1's rule, under which a later valid manifest settles automatically. *Cost if wrong:* one operator command per legitimate resubmission, which has no producer in Phase 13.

### S9-B22. A cap on stored submissions. *Approved, provisional (rev 3).*

- Distinct submissions per participant are capped at a provisional 16 (`BLACKGLASS_RESULT_SUBMISSIONS_MAX`).
- Bodies that identify no participant are capped at a provisional 64 per host per 24 h (`BLACKGLASS_RESULT_UNIDENTIFIED_MAX`).
- Past a cap, nothing more is stored except a deduplicated `submission_limit` anomaly's count, committed before the answer. The answer is `409 SubmissionLimit`.
- **(rev 3)** The identical-retry check runs before the cap: a byte-identical repeat of a stored submission returns its recorded outcome even past the cap. `attempt import-result` is the audited operator path for evidence beyond the cap and is never counted against it.

*Why:* each submission stores up to 64 KiB, twice, and a faulty host must not fill the database. *Cost if wrong:* an operator raises the setting.

### S9-B23. Result publication and relay ordering. *Revised per owner (rev 3).*

The agent does **not** wait for the mission process to exit before relaying a participant's result: that would delay independent settlement in future co-op missions. Instead the process publishes each result through the §H contract:
1. it durably writes a complete manifest (temporary file, `fsync`, atomic rename, directory `fsync`);
2. it attempts the authenticated `recorded` notification, bounded in time, whenever the backend is reachable;
3. it publishes the manifest for relay whether or not the notification succeeded, so an unreachable backend never makes a result wait indefinitely.

The agent relays published manifests while the process is alive. After a positive exit, it also publishes and relays any completely written but unpublished manifest: a crash between writing, notification and publication never makes a result count as absent. Incomplete temporary files are never relayed and never count as present.

Backend consequences (tested now):
- a recorded hash that differs from an unsettled participant's evidence quarantines it (T0-2);
- one that contradicts a settlement raises a durable `recorded_hash_mismatch` anomaly (the operator alert) and leaves the settlement and receipt unchanged; the `recorded` route authenticates a settled attempt's current credential for this purpose only (T0-1);
- a revoked process credential never authenticates, not even for a late hash;
- the absence of a notification neither blocks a valid result nor causes a void (S9-B5).

*Cost if wrong:* a contradiction found after settlement is an alert, never an automatic correction.

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
- V4–V17 failed, including V15 (`recorded_hash_mismatch`) and V16 (`inventory_base_changed`);
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
- `200 {state}`: `result_pending` on the first call or an identical repeat; the participant's current state when its evidence already matches (for example `settled`).
- `409 ConflictingReport` for a hash different from the stored recorded hash, or from a stored submission or settlement for that participant. It raises `recorded_hash_mismatch`; **(rev 3)** an unsettled, unquarantined participant is quarantined in the same transaction.
- `409 InvalidState` when the participant has not started, or was released.
- The uniform 401 when the credential is revoked or superseded, or the attempt was cancelled (R4, R8). **(rev 3, T0-1)** A `settled` attempt's current, unrevoked credential authenticates on this route only, for comparison.
- 503 during an outage.

### `POST /agent/v1/attempts/{aid}/exited` (Host scheme; Stage 8, amended)

The body gains an optional `outbox: {complete: boolean}`. A missing value is stored as `false`.
- With `complete: true`, every started participant must be listed in `results`, or the report is `400 IncompleteResults` and nothing is stored.
- The void rule is S9-B7. The answers are Stage 8's, and `outcome` may now be `voided`.
- **(rev 3)** After a stored `complete: false` report, a report with the same exit status and `complete: true` is stored as the supplementary outbox observation (S9-B7) and answered with its outcome.

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
   - Columns: PK `(attempt_id, account_id)` with a foreign key to the participant; `outcome`; `submission_id` (null iff `infra_void`); `void_basis` (`exit_evidence`, `operator_quarantine`, `operator_revoked_unrecoverable` (rev 3) or `host_declared_lost`; non-null iff `infra_void`); `recorded_hash` (`matched` or `absent`; null for a void); `catalogue_version`; `retention_selection jsonb`; `receipt jsonb`; `settled_at`.
   - Immutable; no truncate.
4. **`pool_collections`.**
   - PK `(attempt_id, container_index, line)`, with a foreign key to `attempt_loot_pool`; `collected > 0`.
   - Trigger: `collected` only grows and stays at or below the pool line's quantity; no delete.
5. **Participants.**
   - New columns: `quarantined_at`, `escalate_at`, `quarantine_reason` and `recorded_manifest_hash`.
     - The reasons are `invalid_manifest`, `malformed_manifest`, `recorded_hash_mismatch`, `inventory_base_changed`, `catalogue_unavailable`, `settlement_check_failed`, `host_revoked` and `operator_import` (rev 3: `catalogue_conflict` removed, `operator_import` added).
   - CHECK: the quarantine columns are set iff the state is `quarantined`, except that `quarantined_at` and `quarantine_reason` stay after a settlement.
   - `recorded_manifest_hash` is written once.
   - The transition function is replaced, adding the S9-B16 edges.
6. **Holds.** The transition function is replaced, adding `charged → settled` and `charged → refunded`.
7. **Hosts.** `declared_lost_at`, permanent. A trigger refuses `enabled = true` and secret rotation once it is set.
8. **Exit reports.**
   - Columns `outbox_complete` and `attempt_revoked` are added, each with a `NOT VALID` CHECK that agent rows set them.
   - `outcome` gains `voided`; the CHECK is replaced by name.
   - **(rev 3)** `attempt_outbox_observations` (PK `attempt_id`, the attempt's own host, `attempt_revoked`, `outcome` `voided` or `none`, `received_at`; it requires a stored agent report with `outbox_complete = false`) and `attempt_outbox_observation_results` (PK `(attempt_id, account_id)`, `present`). Insert-only, no truncate.
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
    - every `exit_evidence` void references an agent exit report with an exit status and `attempt_revoked = false`, and either that report has `outbox_complete` and `present = false` for the participant, or the attempt's outbox observation does (rev 3);
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

These take no inventory, wallet or policy lock, except that V16 runs after the inventory lock in the settle order. Their quarantine is written in the same transaction, which then skips steps 13–18.

**Refused revoked, malformed (unidentified), submission limit:** anomaly insert only. When the anomaly references an account, `accounts` `FOR KEY SHARE` is taken first, through the foreign key.

**Recorded** (process) **(rev 3)**:
1. `accounts` `FOR KEY SHARE` (the route's account), always, because a mismatch inserts an account-referencing anomaly;
2. `mission_attempts`;
3. `attempt_participants`.

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

**Monitoring sweeps (rev 3):**
- Candidates are read unlocked. Each is then handled in its own transaction: `accounts` `FOR KEY SHARE` (the participant's), then the attempt `FOR UPDATE SKIP LOCKED`, then the participant `FOR UPDATE`, then the anomaly insert and the period stamp. A locked attempt is left for the next sweep.
- Auto-drain: the host row `FOR UPDATE` alone; its anomaly carries no account.

**Audit and anomaly rows (rev 3 check).** `anomaly_events.account_id` and `operator_actions.account_id` reference `accounts`, so their inserts take a `FOR KEY SHARE` lock on that account row, which waits behind `account suspend` (`FOR UPDATE`). Every path that inserts one with an account id therefore takes `accounts` `FOR KEY SHARE` first: settlement, quarantine, refusals, `recorded`, `exited` (all participants, ascending), the operator verbs and the sweeps. Anomalies without a participant (unidentified malformed bodies, `submission_limit` on the host cap, `host_auto_drained`) carry no account id. `result_submissions` and `participant_settlements` reference the participant row, which the path already holds.

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
| Exit report with an exit status, `outbox.complete` and `present: false`, or (rev 3) the supplementary observation after an incomplete report | Host secret, as above; attempt not revoked; attempt started | positive evidence of absence: a void (S9-B7) | evidence when incomplete, without an exit status, contradicted, or post-revocation |
| Process `failed` after start | Process credential | nothing (409, as in Stage 8) | evidence of absence |
| Lease lapse, outage, restart, timers | none | an alert | a void |
| Host disable or credential revocation | operator | isolation | a void (owner) |

**Exhaustive void conditions** (§11.1, restated):
1. S9-B7's exit evidence.
2. S9-B8's host-loss declaration.
3. An operator void (S9-B9): of a quarantined participant (`operator_quarantine`), or **(rev 3)** of a revoked attempt's started, unsettled participant whose result is verified unrecoverable (`operator_revoked_unrecoverable`).

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

## H. The publish/visibility contract (rev 3; obligations for Stages 10-12)

**Outbox layout.** One directory per attempt, owned by the mission unit's user and readable by the agent. Per participant, three names, distinguished by suffix only:
- `<accountBase58>.manifest.tmp`: being written. **Never complete, never relayed, never counted as present.** Kept for diagnosis.
- `<accountBase58>.manifest.written`: complete and durable, not yet published.
- `<accountBase58>.manifest.ready`: complete, durable and published for relay.

**Process (Stage 12), per participant, in order:**
1. Write the canonical manifest bytes to `.tmp`, `fsync` the file.
2. Rename `.tmp` to `.written`, then `fsync` the directory. From here the result is durable and complete (the rename is atomic, so a `.written` file is never partial).
3. Call `recorded` with the canonical hash, retrying for at most a provisional 30 s in total. Any answer, or the time bound, ends this step: 200, 409 and 401 all continue to step 4 (the backend keeps the evidence; a 401 means the credential ended), as do 5xx and timeouts.
4. Rename `.written` to `.ready`, then `fsync` the directory. Published.
5. Tell the participant's client that the result is saved and pending (S9-B6).

**Agent (Stage 10):**
- Relays every `.ready` file as soon as it appears, whether or not the process is alive, and handles each answer as §C says.
- Never touches a `.written` file while the process is alive (the process will publish it).
- After the unit exits with a positive exit status (`exitCode` or `signal`), renames each remaining `.written` file to `.ready` (then `fsync`s the directory) and relays it. Such a participant is reported `present: true`.
- Reports `present: false` for a participant only when the scan found neither a `.written` nor a `.ready` file for it. A leftover `.tmp` alone is not a result: the process died before step 2, so no hash was ever notified and no complete result existed.
- Sets `outbox.complete: true` only when the whole directory was read without error. A failed or partial read is `complete: false`; a later successful rescan is reported as the supplementary observation (S9-B7, rev 3).
- Relays every published file before reporting `exited`, and never deletes a relay file before Phase 14's cleanup.

**Crash recovery:**

| Crash point | State left | Outcome |
|---|---|---|
| During step 1 | `.tmp` only | No durable result: `present: false`; a void needs S9-B7's positive evidence |
| Between 2 and 3 | `.written`, no recorded hash | After exit the agent publishes and relays; settles with `recorded_hash = 'absent'` |
| Between 3 and 4 | `.written`, recorded hash stored | After exit the agent publishes and relays; the hash must match (V15) |
| After 4 | `.ready` | Normal relay |
| Backend unreachable at step 3 | `.ready` without a recorded hash | Relayed when the backend returns; a later `recorded` is compared (T0-1, T0-2) |

**Backend guarantees (Stage 9):** a relay is accepted with or without a recorded hash; a recorded hash always blocks a void; a mismatch never settles silently; a revoked credential never authenticates.

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
4. **An API restarted with a new catalogue while results wait.** Each result settles under its admission catalogue, with no 503 loop, even when the new catalogue has a smaller bag, a smaller stack or a retired definition (rev 3); a missing version is `202 held` and recovers through `catalogue import`. Tasks 2a, 3 and 8.
5. **A body close to the size cap, with deep nesting, long strings or a non-canonical encoding.** Over the cap or out of shape: `422` with the raw length or bytes kept and never logged verbatim. A non-canonical but valid encoding: canonicalized, both forms stored. Tasks 1 and 3.

---

## Task 0: record the authorization (rev 3)

**Files:**
- Modify: `Docs/superpowers/specs/2026-10-14-phase-13-backend-design.md` (§0.9 amended with the final rulings)
- Modify: `Docs/superpowers/plans/2026-10-14-phase-13-backend.md` (§Stage 9 status)
- Modify: `CLAUDE.md` (Stage 9 authorized)
- Modify: this plan (revision 3)

- [x] **Step 1:** Record the owner's rulings of 2026-10-17 (the "Revision 3" table, T0-1 to T0-5, and the sections marked "(rev 3)").
- [x] **Step 2:** Commit docs only, by explicit paths.

## Task 1: Pure settlement rules (domain) and the golden vectors

**Files:**
- Create: `Backend/src/Accounts.Domain/Settlement/ResultManifest.cs`, `ManifestValidator.cs`, `SettlementPlanner.cs`, `RetentionRanker.cs`, `Receipt.cs`, `CanonicalJson.cs` (extracted from `CanonicalCatalogueJson` and reused by it, with no behaviour change: its existing tests must stay green)
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
  - Run: `dotnet test Backend/tests/Accounts.Domain.Tests --filter "FullyQualifiedName~Settlement"`. Expected: FAIL (types missing).
- [ ] **Step 2: Implement** the parser (on `StrictJson`), canonicalization (the extracted `CanonicalJson`), validator, planner, ranker and receipt writer, and write the vectors.
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
  - **(rev 3)** A catalogue that retires (drops) an id registers; one that flips an id's kind (stackable or instance) is refused, and `serve` exits with its new code.
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
    - **(rev 3)** a Y with a smaller bag, a smaller maximum stack and a retired definition still settles under X; the bag left over Y's capacity is preserved, and a Stage 5 move of an item from that bag to the stash then succeeds under Y;
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
  10. lock the hold and the inventory; check V16;
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
    - a different hash gets 409, raises `recorded_hash_mismatch` and **(rev 3)** quarantines the participant (`recorded_hash_mismatch`);
    - a revoked or superseded credential, or a cancelled attempt's, gets 401;
    - **(rev 3, T0-1)** after a solo settlement, the same credential gets 200 for the settled hash and 409 plus the anomaly for another, with the settlement and receipt unchanged; after a revocation it gets 401.
  - A relay whose hash differs from the recorded one quarantines (V15, `recorded_hash_mismatch`).
  - A `recorded` call after a settlement with a different hash, in a two-participant fixture, gets 409 and raises the anomaly; the settlement is unchanged.
  - **(rev 3, B23)** A relay that arrives before any `recorded` call settles (`absent`); a quarantined participant's later `recorded` with a different hash stays quarantined and raises the anomaly.
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
  - A repeat report is idempotent; a conflicting one is `ConflictingReport` and voids nothing.
  - **(rev 3) Incomplete scan, then the observation:** a stored `complete: false` report voids nothing; a later report with the same exit status and `complete: true` listing the participant absent is stored as the observation and voids it; the first report is byte-identical afterwards; a repeat of the observation returns its outcome; a third, different report is `ConflictingReport`; an observation with another exit status is `ConflictingReport`; an observation contradicting the first report's `present: true` voids nothing and raises `exit_evidence_contradiction`; an observation after a revocation voids nothing.
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
    - refused without `--reason` and `--evidence`, and for a revoked attempt without `--host-trust-reviewed`;
    - it stores the file (`source = 'operator_import'`, the raw and canonical forms) and quarantines (`host_revoked` for a revoked attempt, else `operator_import`), but never settles by itself;
    - **(rev 3)** it works past S9-B22's cap and doesn't count against it; for a settled participant it stores the file as evidence only, with an anomaly, and the settlement is unchanged;
    - importing the same file twice adds no row;
    - a file whose canonical hash equals a pre-revocation recorded hash is shown as matching in `attempt show`.
  - `void-participant`:
    - refused without `--reason`, `--outbox-checked` and `--evidence`;
    - refused without `--revalidated` when a submission is stored (a `revalidate` action after the latest submission must exist);
    - refused for a participant that is not quarantined;
    - quarantine mode voids with basis `operator_quarantine`, with the action and its checks in the same transaction;
    - **(rev 3) revoked mode:** refused without `--host-trust-reviewed` or `--unrecoverable`, without `--revalidated` when a submission is stored, and for a settled participant; otherwise it voids with basis `operator_revoked_unrecoverable` while the host stays registered (not declared lost), and the host can then be rotated and re-enabled; a revoked attempt's quarantined participant uses this mode, never quarantine mode.
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
  - **(rev 3)** Settlement, quarantine, `recorded` (with a mismatch), an exit-evidence void and a monitoring sweep, each racing `account suspend` and `credential rotate` on the same account: no deadlock, every path ends in its documented outcome, and the suspension waits for (or precedes) the account lock without a timeout.
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
  2. **(rev 3)** it creates a temporary internal network, starts the container on it and migrates it;
  3. a runner container on the same network (never `--network container:`) drives 200 settlements and voids, and survives the restart;
  4. it `docker kill`s the container mid-load, then restarts it;
  5. it runs the invariant queries;
  6. it runs a resubmission pass: every result gets `Settled` or `Duplicate`;
  7. it removes the containers, volume and network, and verifies that they are gone and that the shared test container's ID and uptime are unchanged.
- [ ] **Step 2: Run** the smoke test, the crash drill and the restore drill. Expected:
  - 0 failures, invariants hold, no secret in any transcript;
  - the off-host probe still shows only port 22 open;
  - Docker containers, volumes and listeners match before and after, except while the drill runs.
- [ ] **Step 3: Commit.**

## Task 10: Secrecy, docs as built, final review and stop

**Files:**
- Test: `Database/LogSecrecyTests.cs` (extended across settlement, quarantine, void, import and the recovery verbs: captured logs, anomaly details, `operator_actions`, receipts and CLI output contain no seed, salt, host secret, process credential, ticket or raw manifest)
- Modify: the spec (§0.9 "As built"), the stage plan, `CLAUDE.md`, this plan ("As built"), `Backend/README.md`

- [x] **Step 1:** Run the extended secrecy test. Expected: PASS.
- [x] **Step 2:** Run the full suite once on the VM runner and record the counts and duration. Run the concurrency and fault classes once more.
- [x] **Step 3:** Dispatch the final whole-branch review (most capable model) with this plan, the spec and the review focus. Fix Critical and Important findings test-first in one pass, re-run the suite, and record minors.
- [x] **Step 4:** Write "As built" (counts, the transcripts' locations, rulings, obligations). Commit docs by explicit paths. **Stop and report**: no merge, push or deployment without the owner.

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
  - follow §H: relay `.ready` files while the process is alive; after a positive exit, publish and relay `.written` files; never relay or count `.tmp` files (S9-B23, rev 3);
  - relay every published file before reporting `exited`;
  - send `exited` only with an exit status, and `outbox.complete: true` only after a full, successful outbox scan that lists every started participant;
  - when a scan can't complete, send `complete: false`, and report a later successful rescan as the supplementary observation (S9-B7, rev 3);
  - handle each answer exactly as §C says, recording relay state in its own directory and never deleting a relay file before Phase 14's cleanup;
  - keep `AttemptRevoked` files for the operator;
  - provide the trusted emergency stop (R8).
- **Stage 11 (Unity core):**
  - produce manifests in format v1, canonical by construction;
  - pass every golden vector in `Backend/contracts/manifest-v1/` in EditMode tests;
  - compute the canonical `manifestHash` for `recorded`.
- **Stage 12 (server):**
  - follow §H's five steps: durable write and atomic rename, the bounded `recorded` attempt, publication, then "result saved" to the client (S9-B23, rev 3);
  - exit after informing its clients, without polling settlement (S9-B6);
  - treat a 401 as a stop, keeping the outbox file.
- **Stage 16 (client):** poll `GET /v1/attempts/{id}/receipt` and show only committed rewards; show "under review" for `under_review`.
- **Phase 14:**
  - void-versus-late-result reconciliation;
  - manifest correction and resubmission;
  - outbox and submission cleanup;
  - a policy for inventories when a newer catalogue tightens a limit;
  - incremental reconciliation.

## As built (2026-10-17, branch `phase13-stage9`, implementation commits local, not pushed or merged)

**Commits:**
- `86495ad`: Task 0, revision 3 and spec 2.9 final rulings.
- `27b12c3`: Task 1.
- `a9bd493`: Tasks 2 and 2a.
- `55a5a96`: Task 3.
- `8e027c2`: Task 4.
- `07a66a4`: Task 5.
- `d99ee2f`: Task 6 and the Checkpoint 1 fixes.
- `a576cb8`: Task 7.
- `33c778b`: Task 8.
- `10d34cd` and `1414ab1`: Task 9.
- `e12f4ef`: Task 10, the secrecy test.
- `3e803e4`: earlier stages' tests follow 0009.
- `98a7e82`: final review fixes.
- The tested `Backend/` tree is `91aa3ee` (`HEAD:Backend` at `98a7e82`).

**Schema:** version 9.
- `0009_settlement` applies to a populated schema 8 (`A_populated_schema_8_database_upgrades_and_keeps_its_rows`).
- `0000`–`0008` are byte-identical (`Migrations_0000_to_0008_are_byte_identical`; the branch diff touches only `0009_settlement.sql` under `db/migrations`).
- The invariants file is `db/invariants/0009_settlement.sql` (14 checks).
- New tables:
  - `catalogue_versions`, with a NOT VALID foreign key from `mission_attempts`;
  - `result_submissions` and `result_submission_payloads` (the bytes, kept apart so no constraint DETAIL prints a manifest);
  - `participant_settlements` (the receipt stored as served text);
  - `pool_collections`;
  - `attempt_outbox_observations` and their results.
- Additions to existing tables:
  - participant quarantine columns (`quarantined_at`, `quarantine_reason`, `escalate_at`, `recorded_manifest_hash`), with direct `joined→settled` and `left→settled` (B16);
  - `mission_hosts.declared_lost_at`;
  - exit report marks;
  - `operative_experience_never_decreases`;
  - the anomaly review trigger;
  - exact `settle:` and `void_refund:` ledger keys;
  - operator actions `result_import`, `catalogue_import` and `anomaly_review`.

**Manifest format v1 and golden vectors:**
- Canonical JSON with sorted keys and minimal escapes, capped at 64 KiB.
- 15 bounded refusal codes, and the V4–V15 rules with codes such as `V8.owned_exceeds`.
- `Backend/contracts/manifest-v1/` holds 39 valid vectors, 40 invalid, 6 receipts and 2 retention vectors, generated by the independent `Backend/tools/manifest_vectors.py`. `GoldenVectorTests` reads every file.
- Stage 11 must pass the same files.

**Historical catalogues (B20):**
- The SHA-256-verified history is immutable. `serve` registers its catalogue at startup and exits 5 on an identity-kind change.
- Every new attempt references a registered version. Settlement and `ready` use the admission catalogue.
- Retired definitions are allowed.
- V11's bound is the larger of the admission capacity and the snapshot bag's own entries, so an over-capacity bag may stay over capacity but never grow.
- A missing version is `202 held` and recovers through `catalogue import`.
- Test: admitted under X, settled after the API switches to Y, including a smaller bag, a smaller stack and a retired definition (CatalogueHistoryTests, SettlementTests).

**Exactly-once effects:**
- One transaction applies items, XP, inventory, energy and insurance, in the §E lock order:
  1. account `FOR KEY SHARE`;
  2. host `FOR KEY SHARE` (an addition: the attempt row's second update re-checks its host foreign key, which deadlocked with `host disable`);
  3. attempt;
  4. participant;
  5. hold, then inventory;
  6. wallet, then the policy lock;
  7. writes, then the post-apply bag check.
- Minting uses `(origin, origin_ref)`, with the unit numbers counted per line across operatives.
- XP never decreases.
- The settle entry is a single delta of 0. The void refund is floor(amount × `void_refund_pct` / 100).
- Insurance payouts are minted as `insurance:` items listed on the receipt (invariant `minted_not_in_receipt`).

**Quarantine and operator recovery:**
- Quarantine is sticky (B21) and escalates every 24 h; it never voids.
- Operator verbs:
  - `attempt revalidate` (only for a quarantined participant or one of a revoked attempt; re-checks V2, the session, then V4–V16);
  - `attempt import-result` (evidence beyond the cap; never counted against it);
  - `attempt void-participant` in quarantine or revoked mode (R8; never overwrites a settlement; needs a failed revalidation of every distinct stored manifest that wasn't only `catalogue_unavailable`);
  - `host declare-lost`, `attempt show`, `anomaly list` and `anomaly review`, and `catalogue import`.
- Every verb is audited in `operator_actions`, taking the account lock first.

**Infrastructure void evidence:**
- A void needs an exit status, an unrevoked attempt and the participant listed absent by a complete scan, either the report itself or its supplementary, immutable outbox observation.
- An incomplete scan, a lapsed lease, a timer, a restart, a missing recorded hash, a disable or a revocation never voids (EvidenceVoidTests).
- A result arriving after a void is stored as `late_after_void` with an anomaly.

**Host disable and revoked attempts (B1):**
- No payload is stored for a revoked attempt.
- A rotated host secret doesn't restore process authorization: the relay gets `409 AttemptRevoked`.
- Recovery goes through import, revalidation or the revoked-mode void, without declaring the host lost.
- The disable race: the in-transaction re-check gives the uniform 401 and writes nothing.

**Result and receipt idempotency:**
- A byte-identical repeat returns its stored answer, even past the cap.
- A re-encoding gets its root row's answer.
- A different manifest after a settlement is `409 Conflict` with an anomaly.
- The receipt route needs an account session with a per-account limit. It serves the stored bytes verbatim, or `202 under_review`.

**Publish contract (§H):**
- `recorded` is accepted while the process is alive, and on a settled attempt for comparison.
- A mismatch quarantines a not-yet-settled participant; after a settlement it raises a durable anomaly without touching the receipt.
- A missing hash never blocks or voids (RecordedTests).

**Tests (VM runner, tree `91aa3ee`):**
- Domain: 759 passed, 0 failed.
- Integration: 1,739 passed, 0 failed, in 8 m 40 s.
- An earlier final run at `3e803e4` had 1,734 passing, in 8 m 28 s.
- Concurrency and fault classes: 29/29, three consecutive runs (23–24 s each).
- Mutation checks:
  - disabling duplicate detection fails the parallel-relay and void-race tests;
  - removing the in-transaction re-checks fails the disable race.
- Evidence: `.claude/scratchpad/phase13/stage-09/` (`full-suite-2`, `full-suite-3`, the crash-drill directory, the smoke and drill-test transcripts, the off-host probe and the VM state before and after). The secret scan was clean.

**VM:**
- Crash drill: a throwaway `blackglass-crashdrill` cluster on a temporary `--internal` Docker network, with no published port.
  - 60 participants; killed mid-load at 10 s; 37 load failures.
  - The invariants held after the restart.
  - The resubmission pass gave 24 Settled, 16 Duplicate and 20 voided: exactly one outcome each.
  - Cleanup verified: no drill container, volume, network or password left; the shared test cluster's id and start time unchanged; listeners unchanged.
- Smoke: 0 failures, including the Stage 9 section (settle, receipt, energy, evidence void and refund, quarantine and `under_review`) and the restore drill at schema 9 (41 count lines, 7 invariant files).
- drill-test: 0 failures.
- Off-host probe: 5080, 5432 and 55432 closed; 22 open.
- Docker containers, volumes and listeners identical before and after.
- No deployment of the live stack, and no change to the mission VM, SSH, the firewall or Docker daemon networking.

**Reviews:**
- **Checkpoint 1** (after Task 4, opus): 0 Critical, 4 Important, 6 Minor.
  - Fixed: I1 (unit numbering per line), I2 (receipt route authorization and limit), I3 (no host foreign key on submissions), I4 (ruled: over-capacity admissions), M1 (an unwritable plan becomes the `settlement_check_failed` quarantine), M3 and M4.
  - Deferred: M2 (`submission_limit` keeps no count), M5 (the hold_refund iff and the pool upper bound are not in the invariants) and M6 (registration skips unreadable stored versions).
- **Final review** (opus): 0 Critical, 3 Important, 11 Minor. Migrations 0000–0008 confirmed byte-identical.
  - Fixed, each test-first (RED, then GREEN):
    - I1: a void needs a failed revalidation of every distinct stored manifest, recorded after it was stored, and a `catalogue_unavailable`-only failure doesn't count. Tests: `A_void_needs_a_failed_revalidation_of_every_stored_manifest`, `A_revalidation_that_failed_only_for_an_unavailable_catalogue_never_unlocks_a_void`.
    - I2: `unresponsive_attempt` counts from the start when there was never a heartbeat. Test: `An_attempt_unresponsive_before_its_first_heartbeat_alerts_from_its_start`.
    - I3: the auto-drain counts only voids after the host's last `host enable`. Test: the drain test now re-enables.
    - M1, re-graded Important: revalidate re-checks the process session and runs only for a quarantined participant or one of a revoked attempt. Before the fix, a forged-session manifest really settled. Tests: `Revalidate_never_settles_a_manifest_naming_another_process_session`, `Revalidate_refuses_a_participant_neither_quarantined_nor_of_a_revoked_attempt`.
  - Deferred minors:
    - M2: a few invariant or overflow exceptions become a retried 500 with no anomaly (with the Task 3 deferral of the `settlement_failure` anomaly).
    - M3: for a schema-8 attempt whose catalogue is missing from the history, a second update fails until `catalogue import`. The README says so, and the Task 5 ruling is corrected.
    - M4: concurrent identical unlinked bodies give one 500, and the retry succeeds; over-cap bodies are not deduplicated.
    - M5: past the cap, a re-encoded duplicate of a settled manifest answers 409.
    - M6: concurrent anomaly reviews write two audit rows.
    - M7: the crash drill can exit 0 without a kill, and its build container isn't in the trap. This run's transcript shows the kill.
    - M8: a late matching hash after an `absent` settlement leaves the anomaly open.
    - M9: monitoring failures are logged as deadline-sweep failures.
    - M10: the three-way race is only weakly tested.
    - M11: the host and account lock order differs between `exited` and settlement; both are KEY SHARE, so there's no deadlock today.
  - Owner question: an attempt in `ending` whose relay never arrives has no repeating alert (not in S9-B18's list).

**Rulings made during execution** (each with its cost if wrong; the full list is in the stage ledger and the handoff report):
- Task 1:
  - a separate compact `CanonicalJson`;
  - 15 refusal codes instead of 8;
  - no non-stackable consumables in format v1;
  - depth 4 read as four container levels, with equipment refs flattened.
- Task 2:
  - payloads in their own table;
  - the receipt stored as text;
  - the void refund percentage instead of `+amount`;
  - test attempts name the embedded catalogue.
- Task 2a: admission registers its own catalogue.
- Task 3:
  - a byte-identical repeat answers before the revocation check;
  - a foreign host's body is stored unlinked;
  - 415 and 413 are transport refusals;
  - the 500 anomaly is deferred.
- Task 4: `RecordedAsync` lives in `SettlementStore`.
- Task 5:
  - `participant_settlements.catalogue_version` has no foreign key;
  - Stage 8's `ReleaseTests` moved to `result_pending`;
  - contradictions are checked for every started participant.
- Checkpoint 1:
  - the V11 bound for over-capacity bags;
  - a revalidate write failure is an operator error.
- Task 6:
  - attestation switches per verb;
  - partial-failure coverage through Task 8.
- Task 7: monitoring runs in the deadline worker.
- Task 8:
  - host key-share before the attempt lock;
  - settlement against declare-lost is unreachable;
  - the inventory-edit race.
- Task 9:
  - 60 drill participants instead of 200;
  - the `blackglass-crashdrill` name with `BLACKGLASS_CRASH_DRILL=1`;
  - the restore drill inside the smoke test.
- Task 10:
  - `SettlementSecrecyTests` as a new class;
  - earlier-stage test updates.
- Final: the Task 5 ruling on schema-8 voids corrected (M3).

**Obligations handed on:** as listed in "Obligations this stage hands on" above, plus:
- Stage 10 sends `exited` only after relaying every published file. (Superseded by merge correction 6: a void or exit report never needs the catalogue; only a result settlement waits for `catalogue import`.)
- Phase 14 picks up the deferred minors above.

## Merge corrections (2026-10-17, the owner's "Stage 9 Final Correction and Merge Authorization")

1. **Revoked-attempt replay precedence.** The results route checks the attempt's permanent revocation right after it authenticates the host and takes the account, host and attempt locks, before any replay answer. A body stored before the revocation now gets `409 AttemptRevoked`, like any other. Nothing stored changes: not the rows, not their repeat counts, not a settlement or a receipt.
   - Boundary: host disable revokes live attempts only (Stage 8 R8). An attempt that settled before the disable was never revoked, so its replay still answers `duplicate` with its unchanged receipt.
   - Tests: `A_replay_of_a_submission_stored_before_the_revocation_is_refused_and_changes_nothing` and `An_attempt_settled_before_a_disable_is_not_revoked_and_its_replay_keeps_the_settlements_answer`. The Task 3 ruling ("a byte-identical repeat answers before the revocation check") is withdrawn.
2. **Canonical replay past the cap.** Once the participant's cap is reached, a parsed body whose canonical manifest hash matches a stored row gets that row's answer, and no new raw representation is stored. A genuinely different manifest still gets `409 SubmissionLimit`.
   - Tests: `A_canonical_replay_past_the_cap_answers_its_stored_outcome_before_a_settlement` and `…_answers_duplicate_after_a_settlement`.
   - Found by item 5's deterministic test: once an operator revalidation had settled a row stored as `quarantined`, a byte-identical replay of it answered `202 quarantined`. Any stored row whose manifest is the participant's settled one now answers `duplicate`.
3. **Stalled `ending` attempts.** A new `mission_attempts.ending_at` column is stamped by a database trigger when the attempt enters `ending` and is never changed after that; attempts already in `ending` at the upgrade get the upgrade time.
   - The monitoring sweep raises `ending_attempt_stalled` once an attempt has been in `ending` for `BLACKGLASS_ENDING_ALERT_SECONDS` (600), then every `BLACKGLASS_ENDING_ALERT_REPEAT_SECONDS` (3600). It deduplicates per attempt and interval (`<attempt>:<interval>`).
   - The anomaly carries `host_id`, the attempt and a detail of `{hostId, elapsedSeconds, unsettled}`. It changes no state.
   - Tests: `Ending_at_is_stamped_when_the_attempt_enters_ending_and_the_app_cannot_change_it`, `A_stalled_ending_attempt_alerts_after_ten_minutes_then_hourly_and_never_changes_anything` (generation, deduplication, repetition, no change, recovery), `Concurrent_sweeps_raise_one_stalled_ending_alert_per_interval` and `Stage9_monitoring_settings_defaults_bounds_and_refusals`.
4. **Crash-drill reliability.** The script fails (exit 80) unless:
   - it verified that exactly the drill's container (by id, never the shared cluster's) stopped with exit 137;
   - the container runs again with a new start time;
   - the driver saw PostgreSQL's `pg_postmaster_start_time()` change before its invariant and resubmission checks, and wrote `restarted=true`.

   Cleanup runs on EXIT, SIGINT, SIGTERM and SIGHUP, and covers the build runner. Long commands run in the background and are waited on, so a signal is handled at once. `--negative-skip-kill` is the negative test.
5. **Concurrent settlement and recovery.**
   - `Two_relays_and_a_revalidation_in_a_forced_order_commit_exactly_one_settlement` (two orders, forced by an advisory-lock pause at the first writer's settlement insert, with the other two queued on its locks): exactly one settlement, one unchanged receipt, and the same XP, items, stacks and settle entries as a control participant settled alone.
   - `A_path_holding_its_locks_and_a_conflicting_path_both_finish`: 14 ordered pairs of result relay, exit-evidence void, operator void, host disable and account suspension. Each holder is paused at its last write while holding all its locks; both paths always finish. No deadlock was reproduced, and no lock order changed.
   - **Lock modes as built** (A = the account row, H = the host row, T = the attempt row):
     - result relay, revalidate, import and operator void (`RecoveryStore.LockAsync`): A `FOR KEY SHARE`, then H `FOR KEY SHARE`, then T `FOR UPDATE`, then the participant, hold, inventory, wallet and policy lock;
     - `recorded`: A `FOR KEY SHARE`, then T `FOR UPDATE` (no H: it updates the attempt row at most once, `MarkEndingAsync`, and its anomaly names no host);
     - exit report: H `FOR KEY SHARE`, then A `FOR KEY SHARE` (in the participant release), then T `FOR UPDATE`;
     - host disable: H `FOR UPDATE`, then T `FOR UPDATE` (each attempt row updated once, so no foreign-key re-check reaches A);
     - `host declare-lost`: its stamp transaction takes H `FOR UPDATE` alone; each void then runs in its own transaction through `RecoveryStore.LockAsync` (A, H, T as above);
     - claim and heartbeat: H `FOR UPDATE`, then attempts `SKIP LOCKED` (never waits);
     - auto-drain: H `FOR UPDATE` only;
     - start, process session, ready, process heartbeat, `launching`, player cancel and the deadline release: T `FOR UPDATE` (some after A `FOR KEY SHARE`) without H, each updating the attempt row once;
     - account suspension, credential rotation and revocation: A `FOR UPDATE`, then that account's sessions and credentials only;
     - starter backfill (A `FOR UPDATE`, then `inventories FOR UPDATE`), energy grant and open (A `FOR UPDATE`, then `energy_wallets FOR UPDATE`, then the policy lock), insurance administration (A `FOR UPDATE`, then `insurance_coverages FOR UPDATE`);
     - quarantine escalation: A `FOR KEY SHARE`, then T `SKIP LOCKED`.
   - **Why no cycle can form:**
     - `KEY SHARE` conflicts only with `FOR UPDATE` (and with a key update, which no path makes).
     - **A.** The A `FOR UPDATE` holders never take H or T. Those that take later locks (inventory, wallet and the policy lock, coverage) take them only after A `FOR UPDATE`. Every settlement-family path that takes the same later locks holds A in `KEY SHARE` first, which conflicts, so an A `FOR UPDATE` holder and a settlement-family path are never both past A on the same account. The A holder either waits at A, or holds A while the other waits at A. Neither holds a lock the other is waiting for.
     - **H.** A transaction needs H after T only if it updates an attempt row twice (PostgreSQL re-checks the host foreign key) or inserts a row with a host foreign key. Every such path takes H in `KEY SHARE` before T. The paths that wait on T without holding H update the attempt row once and never need H afterwards. So no transaction holds T while waiting for H, and the H `FOR UPDATE` holders (disable, declare-lost, claim, heartbeat, auto-drain), which take T after H or never, can't close a cycle through H and T.
     - **A and H order.** The exit report and settlement take A and H in opposite orders, but both in `KEY SHARE`, which never conflicts with itself, and neither later acquires a conflicting mode on the other row.
     - **After T,** every path takes the participant, hold, inventory, wallet and policy lock in the same order.
   - Evidence: the 14-pair test above, plus the existing random-order races (suspension, credential rotation, energy grant and policy publication against every account-referencing Stage 9 path).
6. **Historical catalogue and protective voids.** Verified: the NOT VALID foreign key from `mission_attempts.catalogue_version` was re-checked whenever a transaction updated an attempt row twice. As a result:
   - an exit-evidence void of a schema-8 attempt whose catalogue was never registered failed with 23503 (`running → ending → settled`, then the slot release);
   - so did an operator void.

   It is replaced in 0009, which is not yet applied anywhere (0000–0008 are unchanged), by an insert-time trigger raising the same 23503 under the same constraint name. A new attempt still needs a registered catalogue. The reference can't dangle: `catalogue_version` never changes, and history rows are immutable and never removed. Result settlement still waits for the admission catalogue (`202 held`, then `catalogue import`).
   - Test: `An_attempt_admitted_before_the_catalogue_history_is_voided_without_a_catalogue_import` (an exit-evidence void and a revoked-mode operator void).
   - The final review's M3 and the corrected Task 5 ruling are resolved.
