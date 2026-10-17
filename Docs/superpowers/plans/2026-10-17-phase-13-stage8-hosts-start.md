# Phase 13 Stage 8: hosts, claim, process sessions, tickets and the start commit — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans (recommended for this plan, per the owner's lean-execution instruction) or superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

Date: 2026-10-17. **Revision 2: authorized by the owner (2026-10-17) with rulings B1–B13 and the required design corrections in §R, which amend everything below wherever they differ.** Base: `main` @ `71827c7` (Stage 7 merged and pushed; schema version 7; backend 2031/0). Branch: `phase13-stage8`. Execution: native, in the primary session, with two independent reviews: Checkpoint 1 after Task 4 (credentials, secret storage, migration privileges, authentication and admission), and a final whole-branch review (claim concurrency, process sessions, tickets, energy charging, retries, process exit and unavailability). Tests run on the VM-local runner (§D, Task 0b); the Windows tunnel runner stays as the fallback.

**Goal:** the attempt lifecycle runs on the backend up to `running`, driven entirely over HTTP by a test client in place of the agent and the mission process.
- Hosts are registered by an operator. They authenticate with their own secret, heartbeat with their installed build, and report their units.
- An agent claims an admitted attempt for its host. The claim checks the host's capacity, a free port and the build's compatibility with the attempt's template and catalogue. No attempt is ever assigned twice.
- The claim mints an attempt-scoped process credential. With it, the mission process:
  - registers a process session (build allowlist) and receives its seed and the snapshots;
  - reports ready with its loot pool and objectives, validated against the catalogue;
  - redeems single-use, account- and attempt-bound join tickets;
  - commits each participant's start. The start charges that participant's energy exactly once, also under lost acknowledgements and retries.
- Before a participant starts, every exit path releases its reservation exactly once, per participant: player cancel, launch and join deadlines, process exit or failure, an invalid pool.
- After a start, nothing in Stage 8 voids, cancels or refunds:
  - lapsed leases make an attempt `unresponsive`, and a heartbeat brings it back;
  - backend, database or tunnel outages change nothing on their own.

**Stage 8 does not:**
- settle, quarantine, void or refund, or produce receipts (Stage 9);
- write any agent or Unity code. The tests drive the agent and process routes over HTTP (Stages 10–14);
- enable co-op;
- deploy anything.

**Architecture:**
- **Domain (pure).**
  - `Accounts.Domain/Auth/OpaqueSecret` gains three prefixes: `hs1` (host secret), `pc1` (process credential) and `jt1` (join ticket).
  - `Accounts.Domain/Hosts`: `BuildIdentity` (revision and catalogue-version parsing) and `PortAllocator` (the lowest free port in a host's range).
  - `Accounts.Domain/Attempts`:
    - `ReadyReport` (strict parse of the ready body);
    - `LootPoolValidator` (the pool against the catalogue's loot table);
    - `ObjectiveList`;
    - the strict request readers for the agent and process bodies.
- **Persistence.**
  - Migration `0008_hosts_sessions_tickets`:
    - `attempt_secrets` (the seed and salt move out of `mission_attempts`; §B1);
    - the host agent columns;
    - `allowed_builds` and `allowed_build_templates`;
    - the attempt assignment, session and deadline columns;
    - participant start and release columns;
    - `join_tickets`, `attempt_objectives` and `attempt_loot_pool`;
    - replaced transition functions, the tightened `charge:` key, operator actions, privileges and invariants.
  - New stores:
    - `HostAdminStore` (operator verbs);
    - `HostStore` (authentication, heartbeat, claim, launching, exited);
    - `ProcessStore` (authentication, register, ready, failed, heartbeat);
    - `TicketStore` (mint, redeem);
    - `StartStore` (the start commit).
  - `AttemptStore`'s release becomes per participant (`ReleaseParticipantsLockedAsync`).
  - `ServiceHealth` counts live slots and checks build compatibility.
- **API.**
  - Two new authentication schemes: `Host` (`hs1_`, agent routes) and `Process` (`pc1_`, process routes, bound to the route's attempt).
  - Agent routes `/agent/v1/*` and process routes `/server/v1/*` (§C).
  - The client route `POST /v1/attempts/{id}/tickets`, and `join` in `GET /v1/attempts/{id}`.
  - `AttemptDeadlineWorker` replaces `AssignDeadlineWorker`. It sweeps the assign, launch and join deadlines and the process lease.
- **Admin CLI.**
  - Hosts: `host register`, `host rotate-secret`, `host drain`, `host enable`, `host disable`.
  - Builds: `build allow`, `build disable`.
  - Every verb is confirmed and audited.

**Spec:** `Docs/superpowers/specs/2026-10-14-phase-13-backend-design.md`:
- §4.3–§4.4, §7.1–§7.6, §11.1, §11.4 rows 6–11 and 15, §12, §13;
- §15.1–§15.4, §16.1–§16.4, §18, §19;
- the §0.7 Stage 8 obligations.

Decisions 048–050. It becomes revision 2.8 in Task 0. Stage plan: `Docs/superpowers/plans/2026-10-14-phase-13-backend.md` §Stage 8.

---

## A. The owner's mandatory requirements, mapped

| # | Requirement | Where it is met |
|---|---|---|
| 1 | Authenticated host registration; attempt-scoped process credentials | Task 3 (operator registration, secret printed once); Task 4 (`Host` and `Process` schemes); Task 5 (credential minted at claim, valid only for its attempt until it ends) |
| 2 | Build revision, catalogue version and template compatibility | Task 2 (`allowed_builds`, `allowed_build_templates`); Task 4 (health); Task 5 (claim); Task 7 (register) |
| 3 | Actual capacity and active-assignment accounting | Task 2 (`slot_released_at`, port index); Task 4 (health counts live slots; heartbeat reconciles units); Task 5 (claim under the host row lock) |
| 4 | Concurrency-safe claiming, never a duplicate assignment | Task 5 (host `FOR UPDATE`, attempt `SKIP LOCKED`, guarded transition; 10 claimers × 3 attempts; two hosts racing) |
| 5 | Secure seed and salt; constraint errors and logs never disclose them | Task 2 (`attempt_secrets`; the CHECK-DETAIL tests); Task 7 (seed only in the register response); Task 10 (log capture across the lifecycle) |
| 6 | Single-use, account- and attempt-bound tickets | Task 8 |
| 7 | Backend-committed start with exactly-once charging | Task 9 |
| 8 | Lost start acknowledgements; idempotent retries | Task 9 (start replay), Task 5 (claim replay), Task 7 (register replay), Task 8 (redeem replay) |
| 9 | Pre-start cancellation and reservation release | Task 6 |
| 10 | Per-participant transitions; independently accounted holds | Task 2 (transition functions); Task 6 (per-participant release); Task 9 (per-participant charge); co-op-shaped tests with direct SQL |
| 11 | Host heartbeat, lease and failure behaviour | Task 4 (heartbeat, host lease); Task 7 (process lease); Task 6 (process exit); Task 10 |
| 12 | Backend unavailability never voids an active attempt | Task 10; no Stage 8 code path voids; the transition functions forbid `running → cancelled` |

## B. Decisions that need the owner's approval

Each comes with a recommendation and its cost if wrong.

| # | Decision | Recommendation |
|---|---|---|
| **B1** | **The seed and salt move to a new insert-only `attempt_secrets` table, and 0008 drops `mission_attempts.seed` and `retention_salt`.** Dropping them breaks spec §15.4's expand-and-contract rule (no destructive change in the release that stops using a column). Leaving them would keep the secrets in a row that Stage 8 gives a dozen new CHECKs, and a CHECK violation's `DETAIL` prints the whole row in the server log (the Stage 7 CP3 finding). | **Drop in 0008.** The accounts stack has never been deployed. Deployment stops the API before migrating, and the API refuses a schema it doesn't expect, so no old binary can read the dropped columns. The upgrade test copies every existing row's secrets first. Cost if wrong: a later migration has to restore two columns that no code reads. |
| **B2** | **Defence in depth: start the accounts stack's PostgreSQL with `-c log_error_verbosity=terse`.** With it, the server log has no `DETAIL` (failing rows, key values, hashes) for any table. The test cluster stays at the default, because the tests read `DETAIL` from the client-side exception, which this setting doesn't affect. | **Yes**, as one line in `Backend/deploy/services/compose.yaml`, applied at the next (owner-approved) deployment and covered by a static compose test. It changes no network setting. Cost if wrong: server-log diagnostics lose row detail; the SQLSTATE and constraint name remain. |
| **B3** | **Hosts are registered only by the operator CLI** (`admin host register`, confirmed and audited). It prints the host secret once, for installation at `/etc/blackglass/agent/host-secret` (Stage 10/17). There is no agent self-registration and no HTTP admin route. `kind = 'agent'` rows exist only through this verb (the Stage 7 obligation). | **Yes.** Same pattern as `account create`. Cost if wrong: one HTTP route later. |
| **B4** | **Build compatibility model.** `allowed_builds (build_revision, catalogue_version, enabled)` plus `allowed_build_templates (build_revision, catalogue_version, template_id, template_version)` list what each build may run. The host reports its installed build in every heartbeat. A build is compatible with an attempt when four things hold: it is enabled; it lists the attempt's template version; its catalogue version **equals** the attempt's catalogue version (the API catalogue it was admitted with, a new `mission_attempts.catalogue_version` column); and the process registers with exactly that build. The check runs at health (§B5), at claim and at register. Disabling a build stops new claims and registrations on it and never touches a running attempt. | **Yes.** Strict equality is simplest, and it is the condition Stage 9's manifest validation needs anyway. Cost if wrong: a looser rule later is a relaxation, never a migration of data. |
| **B5** | **"Verified capacity" becomes template- and build-aware.** A deploy is admitted only when some enabled, undrained `agent` host has a heartbeat within the lease, fewer live slots than its capacity, and a reported build compatible (§B4) with the requested template's latest version and the API's catalogue. `dev_fixture` hosts still count only with `BLACKGLASS_DEV_HOSTS=enabled`, and their build is not checked (they never claim). | **Yes.** Without it, a deploy that no build can run is admitted and then times out after 60 s. Cost if wrong: a template published between the health check and admission can still time out (a documented, harmless race). |
| **B6** | **A host slot (capacity and port) stays occupied until the agent reports the unit gone**: an `exited` report, or a heartbeat whose unit list no longer names the attempt. Cancelling an attempt does not free its slot, because its unit may still be running on that port. The heartbeat answers `stop: [attemptId…]` for the host's ended attempts that still have units. | **Yes.** Freeing the slot at cancel could hand a port that is still in use to the next claim. Cost if wrong: a silent host keeps its slots until its lease lapses, after which it gets no claims anyway. |
| **B7** | **Tickets.** `POST /v1/attempts/{id}/tickets` mints. `GET` never mints; it shows `join: {hostId, port}` once the attempt is ready. A participant in `admitted`, `joined` or `left` may get a ticket, so the reconnect ticket path works when Stage 15 arrives. Minting revokes the participant's earlier unredeemed tickets, so at most one is live. `AlreadyConnected` waits for Stage 15, which knows connection state. | **Yes.** Cost if wrong: one more state check. |
| **B8** | **A start after the join deadline (database clock) is refused** with `409 DeadlinePassed`, even if the worker hasn't released yet. The deadline is authoritative, not the worker's timing. `start_ack_deadline` equals the join deadline (§7.6). | **Yes.** Cost if wrong: a few seconds of grace lost. |
| **B9** | **The charge is on the ledger.** For a positive hold, the start writes `charge:<attempt>` with delta 0 (0006 requires `charge` deltas of 0) under the wallet lock, after materialization, so every hold transition has its entry. A zero-cost hold charges with no entry, as it reserved with none. | **Yes.** Cost if wrong: a wallet and policy lock per start. |
| **B10** | **The process heartbeat applies `joined ⇄ left`** from `participants[].connected` (the backend half of §11.2). | **Yes**, it is small and makes the per-participant model complete. Alternative: defer to Stage 15. |
| **B11** | **An `exited` or `failed` report after a start does nothing in Stage 8**: `exited` answers `{applied:"none"}` and `failed` answers `409 InvalidState`. The positive-evidence void is Stage 9. | **Yes.** No agent exists before Stage 10. |
| **B12** | **One stage, one branch** (13 tasks), or split into 8A (schema, hosts, builds, claim, release refactor; Tasks 0–6) and 8B (process session, tickets, start, unavailability; Tasks 7–12) with a merge between. | **One branch with Checkpoint 1**, which costs fewer review rounds. Split if you prefer smaller merges. |
| **B13** | **The test runner and test-cluster settings** (§D). | See §D. |

## R. Revision 2: the owner's rulings and required corrections (2026-10-17)

**Rulings.**

| # | Ruling |
|---|---|
| B1 | **Approved.** The seed and retention salt move to insert-only `attempt_secrets`, and 0008 drops the old columns: an approved exception to expand-and-contract, because the live stack was never deployed. The upgrade is tested from a populated schema-7 database |
| B2 | **Approved.** `log_error_verbosity=terse` for the eventual accounts stack only (no deployment, no change to the test cluster). Tests also verify that application logging and PostgreSQL error handling never expose secrets or sensitive parameters |
| B3 | **Approved.** Hosts are registered only by confirmed, audited operator CLI verbs. The secret is generated securely, stored hashed, and shown once |
| B4 | **Approved.** An enabled build revision, an exact catalogue-version match and explicit template-version allowlisting |
| B5 | **Approved.** Health counts real occupied slots and compatible builds, and stays fail-closed |
| B6 | **Approved with safeguards.** A slot (capacity and port) is never freed because an attempt was cancelled or a lease lapsed. Only reliable confirmation that the process is gone frees it (R2) |
| B7 | **Approved.** Minting is a separate authenticated POST; GET never mints. Tickets are single-use and bound to account, attempt and process session |
| B8 | **Approved.** A first start after the join deadline is refused. A start already committed returns its stored result even after the deadline (tested: lost acknowledgement, then a late retry) |
| B9 | **Approved.** A positive hold charging writes one zero-delta `charge:<attempt>` entry; a zero-cost hold writes none |
| B10 | **Approved.** The process heartbeat moves **started** participants between `joined` and `left`. Foreign, duplicate or invalid participant ids are refused (nothing written); a heartbeat never marks an unstarted participant joined |
| B11 | **Revised.** After a participant's start, exit and failure reports never cancel, settle, void or refund in Stage 8, but they are **stored durably** for Stage 9 (R6): exit status and per-participant result presence. Reports are idempotent; a conflicting report is refused and never overwrites evidence. Stage 9 evaluates positive evidence and performs any protective void |
| B12 | **Approved.** One branch; an independent security review after Task 4 and a final independent whole-branch review |
| B13 | **Approved** with the restrictions in Task 0b |

**R1. Claim recovery and credential rotation.**
- Hash-only storage stays. `mission_attempts.credential_generation` (1 at the claim) counts issued credentials.
- **A recovery rotation is possible only before launching begins**: a claim retry with the same `Idempotency-Key` from the same host while the attempt is `assigned` returns **the same attempt with a newly issued credential and the next generation**. It is documented as a recovery, not as a replay of the original response.
- `POST /agent/v1/attempts/{aid}/launching` takes `{credentialGeneration}`. It moves `assigned → launching` only when the generation is the current one; otherwise `409 CredentialSuperseded`, nothing written. A repeat with the current generation once `launching` or later → 200 with the state (lost acknowledgement).
- Once the attempt is `launching` or later (by the agent's report or by the process registering from `assigned`), a claim retry is `409 InvalidState` and never rotates or invalidates the running credential.
- Every claim, recovery, launching and register path locks the attempt row, so a recovery and a launching or register serialize: either the recovery commits first (the old credential is 401, the old generation is `CredentialSuperseded`) or the launch does (the recovery is 409).
- **Stage 10 agent obligations:** persist the idempotency key before sending a claim, and the claim response durably (fsync) before acting on it; get a 200 from `launching` with that response's generation **before** `systemctl start`; never launch with a credential whose generation the backend has not accepted; on `CredentialSuperseded`, retry the claim with the same key; after a restart, resume from the persisted state (no response → claim retry; response but no launching acknowledgement → launching retry).

**R2. Complete host-unit inventory.**
- The heartbeat reports `inventory: {complete, units}`. A missing unit counts as confirmation that a process is gone **only** when `complete` is true: the heartbeat is a successful, complete, authoritative enumeration of the host's mission units.
- With `complete: false` (partial, failed or ambiguous), the heartbeat frees nothing; `stop` is still computed from the units it lists.
- What frees a slot: an `exited` report for the attempt (per-unit confirmation), or a complete inventory that no longer lists an ended (`cancelled` or `settled`) attempt. A `running` or `unresponsive` attempt's slot is freed only by `exited`.
- A host whose lease lapses gets no new claims and counts for no capacity; its slots and ports stay occupied.
- **Stage 10 agent obligation:** set `complete: true` only after a `systemctl list-units 'blackglass-mission@*' --all` that exited 0 and parsed entirely; on any error or doubt, send `complete: false`.

**R3. The ready report's `lootHash`.**
- The ready body carries `lootHash` (spec §16.3, §8.4). It is not Unity's FNV `LootPlan.Hash`, which covers tiles the backend never sees.
- **Canonical calculation:** `"sha256:" +` the lowercase hex SHA-256 of the UTF-8 text `blackglass-loot-pool/1\n` + `table=<lootTableVersion>\n` + one line `<containerIndex>,<line>,<definitionId>,<quantity>\n` per pool line, sorted by `(containerIndex, line)`, integers in invariant decimal. Definition ids match the catalogue id pattern, so they hold no comma or newline.
- The backend recomputes it; a mismatch is an invalid pool (`422 InvalidLootPool`, cancels and releases like any invalid pool).
- `mission_attempts.loot_table_version` and `loot_hash` are written once at ready, with `attempt_loot_pool`, and never change. Stage 9's validation compares a manifest against the stored pool and hash with no schema change.

**R4. Process heartbeat after cancellation.** **Chosen: stop instructions travel only through the authenticated host agent.**
- A process credential is the uniform 401 on every route once its attempt is `cancelled` or `settled`: there is no exception for the heartbeat.
- The process heartbeat answers `{}` and carries no directive.
- The agent heartbeat's `stop` list names the host's ended attempts whose units it still reports. **Stage 10 obligation:** the agent stops those units. **Stage 12 obligation:** the server treats a 401 on its credential after registration as a stop: it admits no one new and exits.

**R5. Migration dependencies.** `allowed_builds` and `allowed_build_templates` are created before any foreign key that points at them; the migration's order is tables, then backfills, then constraints and triggers that depend on them. `0000`–`0007` stay byte-identical (the checksum test).

**R6. Post-start exit evidence.**
- New insert-only `attempt_exit_reports (attempt_id, source ('agent'|'process'), …)` with primary key `(attempt_id, source)`, and `attempt_exit_results (attempt_id, account_id, present)` for the agent's per-participant result presence.
  - `source = 'agent'`: from `exited`, with `exit_code`, `signal`, the attempt state when received, whether any participant had started, and the outcome applied (`cancelled` or `none`).
  - `source = 'process'`: from `failed`, with the process session and `reason`.
- A repeat with an identical report returns the stored outcome and writes nothing. A different report is `409 ConflictingReport` and writes nothing. Result rows must name participants of the attempt, at most once each (otherwise 400, nothing written).
- Before any start, `exited` and `failed` still cancel and release (pre-start path); after a start they only store the report (and `exited` frees the slot).

**R7. Locking.**
- Exit evidence and slot release never lock a host row. They lock the attempt row (the release order before a start, `mission_attempts` `FOR UPDATE` after it), so no transaction takes an attempt row before a host row.
- Only claim and host heartbeat lock host rows, both host → attempt (`SKIP LOCKED`).
- Concurrency tests cover claims, cancellation, the start commit, host heartbeat, grants and policy publication. No energy-touching transaction is retried automatically.

## C. API contracts (Stage 8)

**Revision 2 supersedes these rows where they differ:**
- **heartbeat** takes `{buildRevision, catalogueVersion, inventory:{complete, units:[{attemptId, activeState}]}, outboxPending}` (R2);
- **claim** returns `credentialGeneration`, and a retry rotates only before launching begins (R1);
- **launching** takes `{credentialGeneration}` (R1);
- **exited** stores its report (R6);
- **ready** takes `lootHash` (R3);
- **failed** after a start stores and answers `{applied:"none"}` (R6);
- the **process heartbeat** answers `{}` and its credential is 401 after cancellation (R4);
- a **start** already committed answers its stored result even after the join deadline (B8);
- the **process heartbeat** refuses foreign or duplicate participants (B10).

All bodies are strict JSON: exactly the listed members, with unknown or duplicated members refused (`400 InvalidBody`). Errors are ProblemDetails `{code}`. Account ids are Base58 strings. Attempt ids are canonical lowercase UUIDs; a non-canonical one is refused like an unknown one. No agent or process body carries a host id: the host comes from its credential (controller ruling C-1).

**Agent routes** (`Authorization: Bearer hs1_…`; a disabled or unknown host gets the uniform 401; rate-limited per host):

| Route | Body → response | Errors |
|---|---|---|
| `POST /agent/v1/heartbeat` | `{buildRevision, catalogueVersion, inventory:{complete, units:[{attemptId, activeState}]}, outboxPending}` → `{drained, stop:[attemptId]}`. Stamps `last_heartbeat_at` and the reported build. Only when `complete` is true, frees the slot of each of this host's ended attempts that `units` no longer lists (R2). `stop` lists this host's ended attempts that `units` still lists | 400; 503 |
| `POST /agent/v1/claim` + `Idempotency-Key` | `{}` → 200 `{attemptId, missionPort, processCredential, credentialGeneration, launchDeadline}` or 204 after the wait (`BLACKGLASS_CLAIM_WAIT_SECONDS`, provisional 25, polling every `BLACKGLASS_CLAIM_POLL_MS`, provisional 1000). A retry of the key while the attempt is still `assigned` is a **recovery**: the same attempt with a newly issued credential and the next generation (the old hash is replaced; R1). Once `launching` or later, 409 `InvalidState`, nothing rotated | 409 `HostUnavailable` (drained, lease lapsed or no allowed build reported; the agent heartbeats first); 503 |
| `POST /agent/v1/attempts/{aid}/launching` | `{credentialGeneration}` → 200 `{state}`. `assigned → launching` when the generation is current; already `launching` or later with the current generation → 200 with the current state (lost acknowledgement) | 404 `AttemptNotFound` (unknown or another host's); 409 `CredentialSuperseded` (not the current generation), `InvalidState` (cancelled) |
| `POST /agent/v1/attempts/{aid}/exited` | `{exitCode, signal, results:[{accountId, present}]}` → `{applied:"none"\|"cancelled"}`. Stores the report (R6) and frees the slot. Before any start (`admitted`, `assigned`, `launching`, `ready` with no started participant): also cancels with `process_exited` and releases every admitted participant. After a start: `none` (B11). An identical repeat returns the stored outcome | 404; 400 (unknown or duplicate participant); 409 `ConflictingReport` |

**Process routes** (`Authorization: Bearer pc1_…`). The credential is valid only for the attempt in the route, and only until that attempt is `cancelled` or `settled`; anything else is the uniform 401. Every body after `sessions` names `processSessionId`, and a different one is `409 WrongSession`.

| Route | Body → response | Errors |
|---|---|---|
| `POST /server/v1/attempts/{aid}/sessions` | `{buildRevision, catalogueVersion}` → `{processSessionId, seed, missionTemplate:{id, version}, catalogueVersion, participants:[{accountId, loadout}], deadlines:{launch}}`. Accepts `assigned` (moving it to `launching`, for a lost `launching` report) or `launching`. A replay while `launching` returns the same session and body. **The only response that carries the seed. The salt never leaves the backend** | 409 `BuildNotAllowed` (not exactly the claimed build, or the build was disabled since; nothing written); 409 `InvalidState` |
| `POST /server/v1/attempts/{aid}/ready` | `{processSessionId, layoutHash, lootTableVersion, lootHash, objectives:[{objectiveId, required}], lootPool:[{containerIndex, line, definitionId, quantity}]}` → `{joinDeadline}`. `launching → ready`, storing the objectives and pool. A pool outside the table, or a `lootHash` that isn't the pool's canonical hash (R3), is `422 {code:"InvalidLootPool", errors:[…]}` and **cancels the attempt** (`invalid_pool`, releasing every participant; spec §7.4). A replay while `ready` with an identical body → 200 | 409 `WrongSession`, `InvalidState`; 422 `InvalidLootPool`, `InvalidObjectives` |
| `POST /server/v1/attempts/{aid}/failed` | `{processSessionId, reason}` → `{applied:"cancelled"\|"none"}`. Stores the report (R6). Before any start: cancels with `process_failed`. After a start: `none` (B11). An identical repeat returns the stored outcome | 409 `ConflictingReport`, `WrongSession` |
| `POST /server/v1/attempts/{aid}/tickets/redeem` | `{processSessionId, ticket}` → `{accountId, operativeIds}` (the snapshot order). Single use. A replay of the same ticket by the same session → the same answer | 401 `UnknownToken` (unknown, expired, revoked, or redeemed by another session); 409 `WrongAttempt`, `InvalidState` (the participant was released or settled, or the attempt is not ready or running) |
| `POST /server/v1/attempts/{aid}/participants/{acc}/start` | `{processSessionId}` → `{started:true, startedAt}`. A repeat → 200 with the stored `startedAt`, no second charge, **even after the join deadline** (B8) | 409 `NotRedeemed` (no ticket redeemed by this session for this participant), `Cancelled` (released or attempt cancelled), `DeadlinePassed` (§B8), `WrongSession` |
| `POST /server/v1/attempts/{aid}/heartbeat` | `{processSessionId, phase, participants:[{accountId, connected}]}` → `{}`. Stamps `last_process_heartbeat_at`; `unresponsive → running`; `joined ⇄ left` for started participants only (B10). After cancellation the credential is 401 like every process route (R4) | 400 (unknown, foreign or duplicate participant; nothing written); 409 `WrongSession` |

**Client routes** (session):

| Route | Body → response | Errors |
|---|---|---|
| `GET /v1/attempts/{id}` | Stage 7's body plus `deadlines:{assign, launch?, join?}` and `join:{hostId, port}` once `ready` or `running` (never a ticket, never the seed) | 404 |
| `POST /v1/attempts/{id}/tickets` | `{}` → 201 `{ticket, ticketExpiresAt}` (`jt1_`, 256-bit, 120 s provisional; earlier unredeemed tickets revoked) | 404; 409 `InvalidState` (attempt not ready or running, or participant released or settled) |
| `POST /v1/attempts/{id}/cancel` | as Stage 7, now allowed while the attempt is `admitted`, `assigned`, `launching` or `ready` and **this participant** is `admitted`. Releases this participant. The attempt is cancelled when no participant is active and none started | 409 `InvalidState` after this participant's start |

**Admin CLI** (each confirmed by typing its target or `--yes`, each with `--operator` and `--reason`, each one `operator_actions` row in the same transaction):
- `host register --host-id <id> --capacity <n> --port-base <p>` prints `hostSecret=hs1_…` **once**.
- `host rotate-secret --host-id` prints a new secret once and invalidates the old one at commit.
- `host drain|enable|disable --host-id`, and `host capacity --host-id --capacity <n>`.
- `build allow --revision <r> --catalogue-version sha256:<hex> --template <id>:<version> [--template …]` inserts or re-enables the build and adds its templates. It is idempotent.
- `build disable --revision --catalogue-version`.

## D. Test performance: findings and the proposed runner (§B13)

**Measured on 2026-10-17 at `71827c7`** (evidence: `.claude/scratchpad/phase13/stage-08-perf/`, local).

| Measurement | Result |
|---|---|
| Stage 7 full integration runs (three: CP1, CP2, final) | 51 m 38 s, 53 m 42 s, 52 m 29 s, about 2 h 38 m of the session's 8 h. The rest was implementer and reviewer subagents, fix rounds and polling |
| Suite growth | Stage 5: 790 tests, 25 m. Stage 6: 1055, 35 m. Stage 7: 1402, 52 m. About 2.2 s per integration test, linear in the test count. Domain: 629 tests in 0.2 s |
| A timed sample (172 tests, 5 classes, trx) | 10 m 54 s wall, of which only 359 s is inside test methods. The rest is per-test setup and teardown, which xUnit does not count in a test's duration, run serially within each class |
| **Network round trip, PC → `bg-dev-services01`** | **46 ms by ping; 47.6 ms per `select 1` through the tunnel.** The VM is about 46 ms away from the PC, and the SSH tunnel adds almost nothing on top |
| The same `select 1` on the VM | **0.069 ms** |
| One test's database lifecycle from the PC (create, first connection, migrate 0000–0007, two more first connections, drop) | **3.1 s**: migrate 1.9 s, each first connection about 0.3–0.36 s, create about 0.1 s, drop about 0.08 s |
| The same lifecycle measured on the server | About 0.3 s: create 60 ms, eight migrations about 0.2 s, drop 50 ms (one requested checkpoint per create-and-drop pair) |
| VM load during a run | About 95 % idle; PostgreSQL waits on the client, not on CPU or disk |

**Where the time goes.**
- About 1,070 of the 1,402 integration tests build a fresh database and migrate it in `InitializeAsync`, which xUnit runs per test. Each costs about 3.1 s from the PC, almost all of it network round trips: a migration is about 40 round trips, a new connection about 7.
- The test bodies are round-trip-bound in the same way: a schema test's median of 1.2 s is about 25 round trips.
- The PC's free memory (about 1.9 GB of 64 GB while Unity runs) forces two test threads, which keeps the waits serialized.
- **Estimate:** at least 85 % of the 52 minutes is the 46 ms latency. The first VM run measures the real figure.

**Recommendation: a disposable .NET 10 SDK container on `bg-dev-services01`, next to the existing test cluster.** It needs no Docker Desktop on the PC: the local option would remove the latency too, but it competes with Unity for the PC's memory.
- **Shape:** `Backend/tools/vm-test.sh [--filter <expr>] [--full]`:
  1. syncs `Backend/` with the existing `vm-sync.sh`;
  2. on the VM, runs:
     ```
     docker run --rm --name bg-test-runner \
       --cpus 2 --memory 3g --pids-limit 1024 \
       --network container:blackglass-testdb-postgres-1 \
       -e BLACKGLASS_TEST_PG_PORT=5432 -e BLACKGLASS_TEST_PG_SECRETS=/secrets \
       -v <synced src>:/src:ro -v <the three test-cluster password files>:/secrets/pg_*_password:ro \
       -v bg-test-nuget:/root/.nuget/packages -v bg-test-work:/work \
       mcr.microsoft.com/dotnet/sdk:10.0@sha256:<pinned> \
       timeout 3600 /src/Backend/tools/run-tests-in-container.sh
     ```
     The in-container script copies the sources into `/work`, builds incrementally, and runs Domain, then Integration, with the trx logger;
  3. copies the trx and a summary back to the stage's evidence folder.
- **Network:** `--network container:` shares only the test cluster container's own network namespace. The tests reach PostgreSQL on its loopback, and the API servers the tests start bind loopback ports nobody else can see. There is no published port, no host networking, no daemon or firewall change, and no new listener on any interface.
- **Isolation and cleanup:**
  - unchanged: a fresh `bg_t_*` database per test or class, dropped on dispose, plus the existing sweep of leftovers older than 2 h;
  - the script ends by listing any `bg_t_*` database and failing loudly if one remains;
  - `--rm` removes the container every time;
  - the persistent volumes hold only the NuGet cache and build output, never a secret. The password files are mounted read-only from the VM's existing root-only secrets directory and never copied.
- **Limits:**
  - 2 of the 4 vCPUs and 3 GB of the 6.8 GB available, a pids limit, and a one-hour wall-clock cap;
  - xUnit threads start at 4, then are tuned by measurement;
  - the test cluster itself is unchanged: still `fsync=on`, still production-like.
- **What it preserves:** every test as it is, against the same real PostgreSQL 17, including the concurrency, authorization, rollback, failure-injection and deadlock tests. No test is weakened or removed.
- **Workflow:**
  - focused classes per task (`--filter`; an incremental build plus a few classes in about a minute);
  - **one full suite at final acceptance**;
  - Checkpoint 1 uses focused runs only.
- **Expected:** the full suite in roughly 5–10 minutes instead of about 55 (to be measured, and reported in the as-built), and no PC memory pressure or CET compiler workaround.
- **Needs the owner's approval** (nothing is installed until then):
  - pulling the SDK image (about 0.9 GB; the VM has 72 GB free);
  - two named Docker volumes;
  - two new scripts in `Backend/tools/`.

  The tunnel path stays as the fallback.
- **Later, only if the VM measurement shows it's worth it:** migrate once per run into a `bg_t_tpl_*` template database, and give each test `CREATE DATABASE … TEMPLATE` (measured at about 0.18 s against about 0.3 s server-side to create and migrate). The gain is small once the latency is gone, so it isn't proposed now.

---

## Global Constraints

- **Migrations:** forward-only, numbered, checksummed, one transaction each. **0000–0007 are applied and immutable.** Stage 8 adds `0008_hosts_sessions_tickets.sql` only.
- **Ids:**
  - Attempt ids and process session ids are UUIDv7. Idempotency keys are UUIDs.
  - Host ids keep 0007's pattern.
  - No client, agent or process request names its own principal: the account comes from the session, the host from the host credential, the attempt from the process credential.
  - Another host's or account's attempt is the same 404 as an unknown one. A process credential presented on another attempt's route is the uniform 401.
- **Clock:** every deadline, lease, expiry and stamp is PostgreSQL's `clock_timestamp()`, read after the transaction's locks (and after the energy policy lock where energy is touched).
- **Credentials:** host secrets, process credentials and join tickets are 256-bit `OpaqueSecret`s (`hs1_`, `pc1_`, `jt1_`), stored as SHA-256 only, and compared by hash lookup. Each is shown exactly once: the host secret by the CLI, the process credential in the claim response, the ticket in the mint response. Logs carry at most `OpaqueSecret.HashPrefix`.
- **Locking** (spec §15.3, extended; every wait bounded by `lock_timeout`, a timeout is busy and writes nothing; **no automatic retry** of any energy-touching transaction):
  - **Claim:**
    1. the caller's `mission_hosts` row `FOR UPDATE` (serializes one host's claims, so the capacity and port checks can't race);
    2. one candidate attempt `FOR UPDATE SKIP LOCKED` (never waits on an attempt);
    3. clock;
    4. the attempt update.

    It takes no account, inventory, wallet or policy lock.
  - **Host heartbeat:** the host row `FOR UPDATE` → clock → host update → this host's ended attempts `FOR UPDATE SKIP LOCKED` (slot release).
  - **Release** (cancel, every deadline, exit, failure, invalid pool), for the participants being released, in ascending `account_id` throughout:
    1. `accounts` `FOR KEY SHARE`;
    2. `mission_attempts` `FOR UPDATE`;
    3. `attempt_participants` `FOR UPDATE`;
    4. `energy_holds` `FOR UPDATE`;
    5. `inventories` `FOR UPDATE`;
    6. every positive hold's `energy_wallets` `FOR UPDATE`, **all wallets before any materialization**;
    7. the policy lock;
    8. clock;
    9. writes.

    The participant set is read unlocked first; it is insert-only and fixed at admission.
  - **Start:** that participant's `accounts` `FOR KEY SHARE` → `mission_attempts` `FOR UPDATE` → that `attempt_participants` row `FOR UPDATE` → its `energy_holds` row `FOR UPDATE` → (positive) its `energy_wallets` `FOR UPDATE` → the policy lock → clock → writes. The participant stays active, so start takes no `inventories` lock.
  - **Ticket mint:** `mission_attempts` `FOR SHARE` → the participant `FOR UPDATE` → its earlier tickets (revoke) → clock → insert. **Redeem:** the ticket read unlocked → `mission_attempts` `FOR SHARE` → the participant `FOR SHARE` → the ticket `FOR UPDATE` → clock → write. `join_tickets` references `attempt_participants` only (no direct `accounts` FK), so no key-share on `accounts` happens after the attempt lock.
  - **Process register, ready, heartbeat:** `mission_attempts` `FOR UPDATE` → (heartbeat) participants `FOR UPDATE` ascending → clock → writes. The **invalid-pool** cancel runs the release order instead: ready validates the body purely before taking any lock.
  - **Worker sweeps:** selection `FOR UPDATE SKIP LOCKED` in a short transaction, then each item in its own transaction with the order of its operation.
  - **The rule** (Stage 7): any transaction that writes an account-owned row twice takes `accounts` `FOR KEY SHARE` first. After the energy policy lock, the leaf rule holds: only own or new rows, policy rows, and at most one global unique key per transaction.
- **Energy:**
  - Reserve at admission (Stage 7).
  - **Charge at the start commit**: hold `reserved → charged` plus, for a positive hold, one `charge:<attempt>` delta-0 entry, once.
  - Release before start: `reserved → released` plus `release:<attempt>`, once per participant.
  - 0008 tightens the `charge` key to exactly `'charge:' || attempt_id`.
  - Nothing refunds in Stage 8.
- **Secrets:**
  - The seed and salt live only in `attempt_secrets` (insert-only; the app role can insert and select, never update or delete).
  - The seed appears only in the process register response.
  - The salt appears in no response at all.
  - Neither appears in a client DTO, a log, an audit row, an error body or a constraint `DETAIL`. A reflection test covers every agent, process and client DTO.
- **No void:** no Stage 8 code path cancels, voids or refunds a participant after its start. The replaced transition functions keep `running → cancelled` impossible, and `running → unresponsive → running` is the only lease effect.
- **Failures:** `DbFailure` and the Stage 3 classes; ProblemDetails codes as §C.
- **Scope:** as the goal. No Unity path.
- **Touches:**
  - `Backend/`: src, tests, `db/migrations/0008_hosts_sessions_tickets.sql`, `db/invariants/0008_sessions.sql`, the compose passthrough of new settings, `compose.yaml`'s `log_error_verbosity` (if B2 is approved), the smoke script and the README;
  - `Docs/`; `CLAUDE.md`.
- **Infrastructure:** no live deployment; no firewall, SSH, Docker networking or mission-VM change. The VM work uses the throwaway `bg-smoke`/`bg-drill` projects and the test cluster. Tests create or drop only `bg_t_*` databases (and, if §D is approved, the `bg_t_tpl_*` templates).
- **Preserve** the owner's two Unity settings files and `.claude/`.
- **Git:** logically separated commits on `phase13-stage8`. Nothing is pushed or merged without the owner's approval.
- **Commit trailer:** `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **Ten claimers and three attempts; two hosts racing for one attempt.**
   - Expected: each attempt assigned exactly once, to one host and one port, with no host over capacity; the losers get other attempts or 204.
   - Tests: Task 5 `Ten_claimers_and_three_attempts_assign_each_once` and `Two_hosts_race_for_one_attempt_and_one_wins`.
2. **A start racing a player cancel, the join-deadline worker, an energy grant and a queued energy policy publication.**
   - Expected: exactly one of charged or released. One ledger entry for the winner, none for the loser. No 40P01.
   - Tests: Task 9 `A_start_and_a_cancel_race_and_exactly_one_wins`, `A_start_and_the_join_deadline_race_and_exactly_one_wins` and `A_start_drains_with_a_grant_and_a_queued_publication_without_deadlock`.
   - A mutation dropping the start's `accounts` key-share step must fail the last one.
3. **Lost acknowledgements.** The start, claim, register, redeem and launching calls are each repeated after a committed first call.
   - Expected: the same answer with no second effect (one charge, one credential in force, one session, one redemption).
   - Tests: Task 9 `A_repeated_start_charges_once_and_returns_the_stored_startedAt`; Task 5 `A_claim_replay_while_assigned_rotates_the_credential`; Task 7 `A_register_replay_returns_the_same_session`; Task 8 `A_redeem_replay_by_the_same_session_returns_the_same_answer`.
4. **A constraint violation on `mission_attempts` or `attempt_secrets`.**
   - Expected: the PostgreSQL error's `Detail` (what the server logs) holds neither the seed nor the salt, and the API's logs over a whole lifecycle hold neither.
   - Tests: Task 2 `A_check_violation_on_mission_attempts_does_not_disclose_the_secrets` and Task 10 `No_log_or_client_body_in_a_whole_lifecycle_contains_the_seed_or_salt`.
5. **The backend unavailable during `running`:** the database refuses connections for longer than both leases, then returns; the API restarts.
   - Expected: the attempt goes at most to `unresponsive`, returns to `running` on the next heartbeat, and the participant stays `joined` with its hold `charged`. Nothing is cancelled, released or refunded.
   - Tests: Task 10 `A_database_outage_during_running_never_voids` and `An_API_restart_during_running_changes_nothing`.

---

## Task 0 (docs first, after the owner's approval): record the rulings

- **Spec revision 2.8:**
  - §0.8 with the owner's §B rulings and the requirement map;
  - §4.3 (registration by CLI, credential prefixes);
  - §7.4–§7.6 as planned;
  - §15.1 note (`0008_hosts_sessions_tickets`, `attempt_secrets`, the `slot_released_at` model);
  - §15.3 (the lock orders above);
  - §16.2–§16.4 (§C);
  - §19 (new settings);
  - §B13's test approach in §18.1.
- **Stage plan:** the Stage 8 section (authorized; this plan; migration `0008`).
- **`CLAUDE.md`.**
- **This plan:** revision 2.
- **Commit:** `Phase 13 Stage 8: owner rulings recorded (spec revision 2.8, plan revision 2, CLAUDE.md)`.

## Task 0b: The VM-local test runner (B13)

**Files:** `Backend/tools/vm-test.sh`, `Backend/tools/run-tests-in-container.sh`, README (testing section).

- **`vm-test.sh [--filter <expr>] [--full] [--evidence <dir>]`** (PC side):
  - snapshots the working tree's `Backend/` (tracked and untracked, `.gitignore` respected) into a tree id with a temporary index (`git write-tree`), so uncommitted test-first work can run and every transcript names the exact tree;
  - streams `git archive <tree> Backend` to `/opt/blackglass/backend/test-src` on the VM (root-owned, separate from the deploy copy) and records the tree id;
  - verifies `cluster_name = blackglass-test` on the test cluster (`docker exec ... psql`), and refuses otherwise;
  - takes a VM-side `flock` and uses the fixed container name `bg-test-runner`, so only one runner runs at a time;
  - runs the container, then fails loudly if any `bg_t_*` database remains, and copies the trx files and the summary back.
- **The container:**
  - `docker run --rm --name bg-test-runner --cpus 2 --memory 3g --pids-limit 1024 --cap-drop ALL --security-opt no-new-privileges --network container:blackglass-testdb-postgres-1`;
  - the image pinned by digest;
  - read-only mounts: the source snapshot and exactly the three test-cluster password files;
  - named volumes `bg-test-nuget` (NuGet cache) and `bg-test-work` (build output only);
  - no Docker socket, host network or published port; `timeout 3600`.
- **`run-tests-in-container.sh`:**
  - updates `/work/src` from the snapshot, copying only files whose content differs (fresh timestamps, so MSBuild rebuilds exactly what changed), and deleting files the snapshot no longer has (outside `bin/` and `obj/`). A cached build can therefore never test an old revision;
  - builds, then runs Domain, then Integration (or the filter), with the trx logger;
  - writes the tree id, the commands, the counts and the timings to the summary.
- **Credentials:**
  - the password files are read by the test process only; they are never copied, echoed or written to a volume, and the scripts never use `set -x`;
  - the API's per-test password files live in the container's `/tmp`, which `--rm` removes.
- **Outage tests** never stop or recreate the shared PostgreSQL container, since the runner shares its network namespace. They take only their own `bg_t_*` database offline: `alter database ... allow_connections false` and `pg_terminate_backend` for that database, then `allow_connections true`.
- **Benchmark first:** the 172-test Windows sample (`AttemptSchemaTests`, `AttemptApiTests`, `MigratorTests`, `AttemptPrivilegeTests`, `MissionTemplateTests`; 10 m 54 s on Windows) on the VM, with the real timings recorded. No full-suite figure is claimed until it is measured.
- [ ] **Commit.** `Phase 13 Stage 8: VM-local disposable .NET 10 test runner (B13)`.

## Task 1: Pure rules (domain)

**Files:** `src/Accounts.Domain/Auth/OpaqueSecret.cs` (prefixes), `src/Accounts.Domain/Hosts/{BuildIdentity,PortAllocator}.cs`, `src/Accounts.Domain/Attempts/{ReadyReport,LootPoolValidator,ObjectiveList,AgentRequests,ProcessRequests}.cs`; tests under `tests/Accounts.Domain.Tests/{Hosts,Attempts}/`.

**Interfaces (produced):**

```csharp
namespace Blackglass.Accounts.Domain.Auth;
public sealed partial class OpaqueSecret
{
    public const string HostSecretPrefix = "hs1";
    public const string ProcessCredentialPrefix = "pc1";
    public const string JoinTicketPrefix = "jt1";
}

namespace Blackglass.Accounts.Domain.Hosts;
/// buildRevision: ^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$; catalogueVersion: CatalogueVersion's "sha256:<64 lowercase hex>".
public sealed record BuildIdentity(string Revision, string CatalogueVersion)
{ public static bool TryParse(string? revision, string? catalogueVersion, out BuildIdentity? build); }
public static class PortAllocator
{
    /// The lowest port in [portBase, portBase + capacity) not in used; null when none is free.
    public static int? Lowest(int portBase, int capacity, IReadOnlySet<int> used);
}

namespace Blackglass.Accounts.Domain.Attempts;
public sealed record PoolLine(int ContainerIndex, int Line, string DefinitionId, int Quantity);
public sealed record Objective(string ObjectiveId, bool Required);
public sealed record ReadyReport(Guid ProcessSessionId, string LayoutHash, int LootTableVersion, string LootHash, IReadOnlyList<Objective> Objectives, IReadOnlyList<PoolLine> LootPool)
{ public static ReadyReport Parse(ReadOnlySpan<byte> utf8Json); } // strict; throws RequestFormatException
public static class LootPoolValidator
{
    /// Every error, in order: table version mismatch; container index outside [0, containerCount); per-container line count
    /// outside [minItems, maxItems]; lines not 0..n-1 per container; a definition not in the table's entries; a quantity
    /// outside the entry's [min, max]. Empty list = valid.
    public static IReadOnlyList<string> Validate(LootTable table, IReadOnlyList<PoolLine> pool);
}
public static class LootHash
{   /// R3's canonical "sha256:<hex>" over the table version and the pool lines sorted by (containerIndex, line).
    public static string Compute(int lootTableVersion, IReadOnlyList<PoolLine> pool); }
public static class ObjectiveList
{   /// 1..32 objectives, unique ids matching ^[a-z0-9]+([._-][a-z0-9]+)*$ (≤ 64), at least one required.
    public static IReadOnlyList<string> Validate(IReadOnlyList<Objective> objectives); }
// AgentRequests: HeartbeatRequest, ExitedRequest; ProcessRequests: RegisterRequest, FailedRequest, RedeemRequest,
// StartRequest, ProcessHeartbeatRequest — each with a strict static Parse(ReadOnlySpan<byte>).
```

- [ ] **Step 1: Write the failing tests:**
  - `New_secret_prefixes_round_trip_and_refuse_each_others_text` (theory over the five prefixes);
  - `BuildIdentity_parses_strictly` (theory);
  - `PortAllocator_returns_the_lowest_free_port_or_null`;
  - `ReadyReport_parses_strictly` (theory: unknown members, duplicates, wrong types, negative indices, an oversized pool);
  - `LootPoolValidator_accepts_a_pool_inside_the_table`;
  - `LootPoolValidator_reports_every_violation` (theory: one row per rule);
  - `LootHash_is_canonical_and_order_independent` (a pinned vector; the input order doesn't matter; any field change changes it);
  - `ObjectiveList_validates`;
  - `Agent_and_process_requests_parse_strictly` (theory per request type).
- [ ] **Step 2:** run (FAIL: types missing), implement, run `Accounts.Domain.Tests` (PASS).
- [ ] **Step 3: Commit.** `Phase 13 Stage 8: pure rules for host builds, ports, ready reports, loot pools, objectives and agent/process requests`.

## Task 2: Migration `0008_hosts_sessions_tickets`, invariants, privileges and secret storage

**Files:** `db/migrations/0008_hosts_sessions_tickets.sql`, `db/invariants/0008_sessions.sql`; tests `tests/Accounts.Integration.Tests/Database/{SessionSchemaTests,SessionPrivilegeTests,SecretStorageTests}.cs`; `UpgradeTests` (0007 → 0008 case).

**The migration, in order** (R5: every referenced table exists before a foreign key points at it):
0. **`allowed_builds`** and **`allowed_build_templates`** (item 4 below) are created first.
1. **`attempt_secrets`** `(attempt_id uuid primary key references mission_attempts, seed integer not null check (seed >= 0), retention_salt bytea not null check (length(retention_salt) = 32), created_at timestamptz not null default statement_timestamp())`.
   - Triggers refuse update, delete and truncate.
   - `insert into attempt_secrets select attempt_id, seed, retention_salt, created_at from mission_attempts`.
2. **`mission_attempts`:**
   - Replace `mission_attempt_transition()` (no reference to the secrets).
   - Drop `seed` and `retention_salt` (§B1).
   - Add:
     - `catalogue_version text`, backfilled from the participants' `loadout_snapshot->>'catalogueVersion'`, then `not null`;
     - `host_id text references mission_hosts`, `mission_port integer`, `build_revision text`;
     - `process_credential_hash bytea unique check (length = 32)`, `claim_key uuid unique`, `process_session_id uuid unique`, `layout_hash text`;
     - `launch_deadline`, `ready_at`, `join_deadline`, `started_at`, `last_process_heartbeat_at`, `slot_released_at` (all `timestamptz`);
     - `credential_generation integer` (R1; >= 1 with a credential), `loot_table_version integer` and `loot_hash text` (R3; written once at ready).
   - Constraints:
     - FK `(build_revision, catalogue_version)` → `allowed_builds`;
     - `cancel_reason in ('no_capacity','player_cancel','launch_timeout','join_timeout','process_exited','process_failed','invalid_pool')`;
     - CHECKs by state: assigned and later need host, port, build, credential hash and launch deadline; ready and later need the session, `ready_at` and the join deadline; running and later need `started_at`;
     - `slot_released_at` only with a host.
   - Index: `one_port_per_live_slot on (host_id, mission_port) where host_id is not null and slot_released_at is null`.
   - The new transition function adds:
     - `assigned → launching` when registering from `assigned`;
     - write-once rules: the host, port, build, session, `ready_at`, `started_at` and the deadlines, once set, never change, except that a claim replay may replace the credential hash while `assigned`;
     - `slot_released_at` is set once.

     It keeps every Stage 7 edge and still forbids `running|unresponsive|ending → cancelled`.
3. **`mission_hosts`:**
   - Add `port_base integer`, with `kind = 'agent'` ⇒ `port_base` not null, and `port_base` in 1024..65535 − `capacity`. Add `build_revision text` and `catalogue_version text` (as reported). Add `secret_rotated_at timestamptz`.
   - Grants: insert of these columns; update of `enabled`, `drained`, `capacity`, `secret_hash`, `secret_rotated_at`, `last_heartbeat_at`, `build_revision`, `catalogue_version`.
4. **`allowed_builds`** `(build_revision, catalogue_version, enabled boolean not null default true, created_at)`, PK on both, patterns as `BuildIdentity`. **`allowed_build_templates`** `(build_revision, catalogue_version, template_id, template_version)`, FKs to `allowed_builds` and `mission_templates`, insert-only.
5. **`attempt_participants`:**
   - Add `started_at timestamptz` (set exactly when first `joined`, never changed) and `release_reason text` (set exactly when `released`; the cancel reasons' set).
   - Replace `attempt_participant_transition()` (same edges plus these write-once rules).
6. **`join_tickets`** `(ticket_hash bytea primary key check (length = 32), attempt_id, account_id, issued_at, expires_at, redeemed_at, redeemed_by_session uuid, revoked_at)`:
   - FK `(attempt_id, account_id)` → `attempt_participants`;
   - `expires_at > issued_at`; redeemed ⇔ session; not both redeemed and revoked;
   - a trigger allows only `redeemed_at` with `redeemed_by_session` once, or `revoked_at` once; never delete or truncate;
   - index on `(attempt_id, account_id) where redeemed_at is null and revoked_at is null`.
7. **`attempt_objectives`** and **`attempt_loot_pool`** as spec §15.1, insert-only (triggers).
7b. **`attempt_exit_reports`** and **`attempt_exit_results`** (R6), insert-only; results reference `attempt_participants`.
8. **`energy_ledger`:** the per-kind key CHECK replaced by name with every rule identical, except `charge` = `'charge:' || attempt_id::text`.
9. **`energy_holds`:** transition unchanged (`reserved → charged | released`; Stage 9 adds its edges).
10. **`operator_actions`:** add `host_register`, `host_rotate_secret`, `host_disable`, `host_capacity`, `build_allow`.
11. **Privileges:** column-level grants per path, as 0007's comment block. The app never updates `attempt_secrets`, objectives, the pool or `allowed_build_templates`, and never deletes anything.

**Invariants `0008_sessions.sql`** (each returns violating rows):
- every attempt has exactly one `attempt_secrets` row;
- a charged positive hold has exactly one `charge:` entry, and a charged hold's participant is started (`joined`, `left` or later);
- a started participant's hold is charged and its attempt is `running`, `ending`, `unresponsive` or `settled`;
- a redeemed ticket's session is its attempt's `process_session_id`;
- a host's live slots never exceed its capacity at claim time (an advisory row, not a failure, after a capacity decrease) and no two live slots share a port;
- an assigned-or-later attempt's build is allowed for its template.

- [ ] **Step 1: Write the failing tests:**
  - `Migration_0008_applies_to_a_populated_0007_database_and_copies_every_secret` (upgrade: Stage 7 attempts with known seeds are in `attempt_secrets` afterwards; the columns are gone; the invariants hold);
  - `Mission_attempts_no_longer_has_seed_or_salt_columns`;
  - `Attempt_secrets_is_insert_only_for_the_app` (update, delete and truncate refused);
  - **`A_check_violation_on_mission_attempts_does_not_disclose_the_secrets`**: an UPDATE as the app violating a state CHECK; `PostgresException.Detail` contains neither the seed (decimal) nor the salt (`\x` hex);
  - **`A_check_violation_on_attempt_secrets_discloses_only_the_rejected_values`**: an insert with an invalid salt length; `Detail` holds nothing of any other attempt's secrets;
  - `The_transition_functions_refuse_every_edge_not_listed` (theory over all pairs; `running → cancelled` included);
  - `Write_once_columns_never_change` (theory);
  - `One_port_per_live_slot_refuses_a_clash_and_allows_reuse_after_release`;
  - `The_charge_key_must_name_its_attempt`;
  - `Join_tickets_redeem_once_and_never_delete`;
  - `Objectives_and_pool_are_insert_only`;
  - `Exit_reports_are_insert_only_and_results_name_participants`;
  - `Migrations_0000_to_0007_are_byte_identical` (the existing checksum test, kept);
  - `App_privileges_are_exactly_the_planned_columns` (catalogue query);
  - the invariant files each catch a hand-made violation.
- [ ] **Step 2:** run (FAIL), write the migration, run the Task 2 classes and the existing schema classes (PASS).
- [ ] **Step 3: Commit.** `Phase 13 Stage 8: migration 0008 (attempt secrets, hosts, builds, sessions, tickets, pools), invariants and privileges`.

## Task 3: Operator verbs for hosts and builds

**Files:** `src/Accounts.Persistence/Hosts/HostAdminStore.cs`, `src/Accounts.Api/Admin/{AdminCommand,AdminArguments}.cs`; tests `HostAdminStoreTests`, `HostCliTests`.

- `host register` generates the secret with `OpaqueSecret.Generate(HostSecretPrefix)`, stores only its hash, inserts `kind = 'agent'`, `enabled = true`, `drained = false`, and prints `hostId=`, `kind=agent`, `capacity=`, `portBase=` and `hostSecret=` **once**. A taken host id exits 3 with nothing written.
- `host rotate-secret` replaces the hash. The old secret is refused from the commit on.
- `drain`, `enable` (undrain and enable), `disable` and `capacity` change only their columns.
- `build allow` and `build disable` as §C.
- Every verb prints its target first, acts only when confirmed, and writes one `operator_actions` row with a correlation id. **The audit row never contains the secret or its hash.**
- [ ] **Step 1: Write the failing tests:**
  - `Register_prints_the_secret_once_and_stores_only_its_hash`;
  - `Register_refuses_a_taken_id_and_writes_nothing`;
  - `Rotate_invalidates_the_old_secret`;
  - `Drain_enable_disable_and_capacity_change_only_their_columns`;
  - `Build_allow_is_idempotent_and_adds_templates`;
  - `Build_disable_keeps_running_attempts`;
  - `Every_verb_is_confirmed_and_audited_without_secrets`;
  - `Unconfirmed_verbs_change_nothing`.
- [ ] **Step 2:** run (FAIL), implement, run (PASS).
- [ ] **Step 3: Commit.** `Phase 13 Stage 8: operator verbs for host registration, rotation, drain, enable, disable, capacity and build allowlisting`.

## Task 4: Host and process authentication, host heartbeat, capacity-aware health, admission writes

**Files:** `src/Accounts.Api/Auth/{HostAuthenticationHandler,ProcessAuthenticationHandler,MissionClaims}.cs`, `src/Accounts.Persistence/Hosts/HostStore.cs` (authenticate, heartbeat), `src/Accounts.Persistence/Attempts/{ProcessStore.cs (authenticate only),ServiceHealth.cs,AdmissionStore.cs}`, `src/Accounts.Api/Hosts/AgentEndpoints.cs`, `ServeCommand.cs`; tests `HostAuthTests`, `ProcessAuthTests`, `HostHeartbeatTests`, `ServiceHealthTests` (extended), `AdmissionStoreTests` (secrets and catalogue column).

- **Authentication.**
  - Schemes `Host` and `Process`, each a handler like `BearerAuthenticationHandler`: one header, the exact prefix, one indexed lookup per request (no cache), and the uniform 401 on failure.
  - A route group requires exactly its scheme. A client `at1_` token on an agent route, a host secret on a process route, and so on, are each 401.
  - A process credential authenticates only while its attempt is not `cancelled` or `settled`, and only on its own attempt's routes. The handler compares the route's `{aid}` with the credential's attempt.
- **Heartbeat** (§C): host `FOR UPDATE` → clock → stamp and reported build → slot reconciliation (`SKIP LOCKED`) → `{drained, stop}`.
- **ServiceHealth** (§B5): one statement, no lock. Live slots per host = `count(*) where host_id = h and slot_released_at is null`. The build join is as §B4, taking the requested template id (its latest version, resolved in the statement) and the API's catalogue version. The admission call passes the template id from the parsed request.
- **Admission** inserts `catalogue_version` on the attempt and the secrets into `attempt_secrets`, right after the attempt row (the immediate-FK insert order). Its replayed bodies stay byte-identical.
- [ ] **Step 1: Write the failing tests:**
  - `Each_scheme_accepts_only_its_own_credential` (theory over the 3 × 3 pairs plus malformed headers);
  - `A_disabled_host_and_a_rotated_secret_are_401`;
  - `A_process_credential_is_401_on_another_attempts_route_and_after_its_attempt_ends`;
  - `Heartbeat_stamps_the_database_time_and_reported_build`;
  - `Heartbeat_frees_the_slots_of_ended_attempts_no_longer_listed_and_asks_to_stop_the_rest`;
  - `An_incomplete_inventory_frees_no_slot` (R2);
  - `A_lapsed_host_keeps_its_slots_and_gets_no_capacity` (R2);
  - `A_heartbeat_never_frees_a_running_attempts_slot` (R2);
  - `Npgsql_and_the_api_never_log_parameters_or_error_detail` (B2: no parameter logging, and no secret in a logged database failure);
  - `Compose_starts_postgres_with_terse_error_verbosity` (B2, static);
  - `Health_counts_live_slots_not_capacity` (a host at capacity is unhealthy);
  - `Health_requires_an_allowed_build_for_the_template_and_catalogue` (theory: disabled build, template not listed, catalogue mismatch, no build reported);
  - `Health_ignores_lapsed_drained_and_disabled_hosts`;
  - `Dev_fixtures_still_count_only_when_enabled`;
  - `Admission_writes_the_secrets_row_and_catalogue_version_and_replays_byte_identically`;
  - `A_failing_health_query_is_a_database_failure_never_healthy`.
- [ ] **Step 2:** run (FAIL), implement, run the Task 4 classes plus `AdmissionStoreTests`, `AttemptApiTests` and `ServiceHealthTests` (PASS).
- [ ] **Step 3: Commit.** `Phase 13 Stage 8: host and process authentication, host heartbeat, slot-counting build-aware service health, admission writes attempt secrets`.

### Checkpoint 1: independent security review (most capable model)

Scope: Tasks 1–4.
- secret storage and the `DETAIL` tests;
- credential formats, lookup and the scheme isolation;
- privileges;
- the transition functions;
- the health statement;
- the upgrade.

Fix Critical and Important findings in one pass, each with a failing test first. Minors go to the ledger.

## Task 5: Claim and launching

**Files:** `HostStore.cs` (claim, launching), `AgentEndpoints.cs`, `AttemptSettings.cs` (claim wait and poll, launch deadline); tests `ClaimTests`, `ClaimConcurrencyTests`.

- **Claim, one transaction:**
  1. Lock the host row `FOR UPDATE`. Refuse with `HostUnavailable` when the host is disabled, drained, past its lease or reports no allowed build.
  2. Count its live slots. At capacity → no candidate.
  3. Choose a candidate:
     ```sql
     select a.attempt_id from mission_attempts a
     where a.state = 'admitted' and a.assign_deadline > clock_timestamp()
       and a.catalogue_version = @hostCatalogue
       and exists (select 1 from allowed_build_templates t join allowed_builds b using (build_revision, catalogue_version)
                   where b.enabled and t.build_revision = @hostBuild and t.catalogue_version = @hostCatalogue
                     and t.template_id = a.template_id and t.template_version = a.template_version)
     order by a.created_at limit 1
     for update of a skip locked
     ```
  4. Pick the port with `PortAllocator.Lowest` over the host's live slots.
  5. Mint a `pc1_` credential.
  6. One guarded `UPDATE … where state = 'admitted'` writes `assigned`, the host, port, build, credential hash, `claim_key` and `launch_deadline = clock + BLACKGLASS_LAUNCH_DEADLINE_SECONDS`.
  7. Commit, then return the plaintext credential once.
- **No candidate:** commit, wait `BLACKGLASS_CLAIM_POLL_MS`, and retry up to the claim wait, each try a new short transaction (no connection is held while waiting). Then 204. The request's cancellation ends the wait.
- **Claim recovery** (same `Idempotency-Key` from the same host): while `assigned`, a new credential replaces the hash and the generation increments in a guarded update (`where state = 'assigned'`); once `launching` or later, 409 `InvalidState` (R1). A key that names another host's attempt is never found (404).
- **Launching:** attempt `FOR UPDATE`; the body's generation must be current (`CredentialSuperseded`); guarded `assigned → launching`; already later → 200 with the state; cancelled → 409.
- [ ] **Step 1: Write the failing tests:**
  - `A_claim_assigns_the_oldest_compatible_attempt_with_the_lowest_free_port`;
  - `A_claim_skips_incompatible_templates_catalogues_and_disabled_builds`;
  - `A_host_at_capacity_gets_204` (and a slot freed by heartbeat makes room again);
  - `Ten_claimers_and_three_attempts_assign_each_once` (10 tasks over 2 hosts released together; each attempt in exactly one 200; no host over capacity; no port clash);
  - `Two_hosts_race_for_one_attempt_and_one_wins`;
  - `A_claim_never_takes_an_attempt_past_its_assign_deadline`;
  - `A_claim_waits_and_returns_an_attempt_admitted_during_the_wait`, and `A_claim_returns_204_after_the_wait` (short configured wait);
  - `A_claim_retry_while_assigned_recovers_with_a_new_credential_and_generation` (the old credential is 401, the new one authenticates);
  - `A_claim_retry_after_launching_is_409_and_keeps_the_credential` (also after a process registered from `assigned`);
  - `Launching_with_a_superseded_generation_is_409_and_writes_nothing`;
  - `Claim_recovery_racing_launching_leaves_exactly_one_consistent_outcome` (repeated; either recovery then superseded, or launching then 409);
  - `Claim_recovery_racing_register_leaves_exactly_one_consistent_outcome`;
  - `Launching_is_idempotent_and_refused_after_cancel`;
  - `Claims_heartbeats_and_exits_race_without_deadlock` (R7: host heartbeat slot release, exited and claims on one host);
  - `A_drained_or_lapsed_host_is_HostUnavailable_and_takes_nothing`;
  - `A_claim_holds_no_connection_while_waiting` (the pool is not exhausted by 10 waiting claims with a pool of 5).
- [ ] **Step 2:** run (FAIL), implement, run (PASS).
- [ ] **Step 3: Commit.** `Phase 13 Stage 8: concurrency-safe claim with capacity, ports, build compatibility, credential minting and replay; launching`.

## Task 6: Per-participant release, pre-start cancel, exit and failure, and the deadline worker

**Files:** `AttemptStore.cs` (release refactor), `HostStore.cs` (exited), `ProcessStore.cs` (failed), `src/Accounts.Api/Attempts/AttemptDeadlineWorker.cs` (replaces `AssignDeadlineWorker`), `AttemptEndpoints.cs` (cancel), `AttemptSettings.cs`; tests `ReleaseTests`, `ExitTests`, `AttemptDeadlineWorkerTests` (replacing `AssignDeadlineWorkerTests`; every existing case kept), `AttemptStoreTests` (extended).

- **`ReleaseParticipantsLockedAsync(conn, tx, attemptId, IReadOnlyList<long> accounts, reason)`** follows the Release lock order. For each named participant still `admitted`: participant `→ released` with `release_reason`, hold `reserved → released`, and for a positive hold, wallet credit plus `release:<attempt>`. Then, if no participant of the attempt is active-and-unstarted or started, the attempt `→ cancelled` with `cancel_reason` and `ended_at`. Released participants return their stored outcomes (a replay writes nothing). Stage 7's "exactly one participant" guard is removed.
- **Callers:**
  - player cancel (that participant only; attempt states `admitted`, `assigned`, `launching`, `ready`);
  - the assign deadline (`no_capacity`, every participant);
  - the launch deadline (`assigned` or `launching` past `launch_deadline`: `launch_timeout`);
  - the join deadline (`ready` or `running` past `join_deadline`: every still-`admitted` participant with `join_timeout`; the attempt is cancelled only if none started);
  - `exited` before start (`process_exited`; also sets `slot_released_at`);
  - `failed` (`process_failed`);
  - an invalid pool (`invalid_pool`; Task 7).
- **The worker** runs one sweep every `BLACKGLASS_ATTEMPT_SWEEP_SECONDS` over four selections: assign, launch and join deadlines, and the process lease (Task 7). Each selection is `FOR UPDATE SKIP LOCKED`, oldest deadline first, up to the batch size, and each item runs in its own transaction.
  - **Cool-down (Stage 7 CP2 M3):** an item whose transaction fails is excluded from its selection for `min(2^failures, 64)` sweeps (an in-memory map, bounded at 10 000 entries, cleared on success). Its failures are logged by SQLSTATE and constraint only, so a persistently failing item can't hold the head of the batch.
- [ ] **Step 1: Write the failing tests:**
  - `Cancel_before_start_releases_that_participant_once_in_every_pre_start_state` (theory: admitted, assigned, launching, ready);
  - `Cancel_after_this_participants_start_is_409_and_writes_nothing`;
  - `Two_participants_release_independently` (co-op shaped, direct SQL: one released, one started; the attempt stays `running`; holds and ledger independent);
  - `The_attempt_is_cancelled_only_when_no_participant_remains`;
  - `Launch_deadline_cancels_assigned_and_launching_attempts_and_keeps_the_slot_until_exited`;
  - `Join_deadline_releases_unstarted_participants_and_cancels_only_if_none_started`;
  - `Exited_before_start_cancels_releases_and_frees_the_slot`;
  - `Exited_after_start_applies_none_and_stores_the_evidence` (B11, R6: exit code, signal, per-participant presence);
  - `An_identical_exit_report_repeats_and_a_conflicting_one_is_409_and_keeps_the_first` (both sources);
  - `An_exit_report_naming_a_foreign_or_duplicate_participant_is_400_and_writes_nothing`;
  - `Failed_before_start_cancels_and_after_start_stores_the_report_and_applies_none`;
  - `A_cancel_and_every_deadline_race_and_release_once` (theory over the three deadlines);
  - `A_release_drains_with_a_grant_and_a_queued_publication_without_deadlock` (the Stage 7 test, extended to a launch-deadline release);
  - `A_persistently_failing_item_cools_down_and_the_rest_of_the_batch_proceeds`;
  - `The_worker_never_touches_a_running_attempt_except_the_lease` (mutation: a selection that admits `running` must fail it).
- [ ] **Step 2:** run (FAIL), implement, run the Task 6 classes plus `AttemptStoreTests`, `AttemptApiTests` and `AdmissionStoreTests` (PASS).
- [ ] **Step 3: Commit.** `Phase 13 Stage 8: per-participant release for cancel, deadlines, exits and failures; deadline worker with cool-down`.

## Task 7: Process register, ready, heartbeat and the process lease

**Files:** `ProcessStore.cs`, `src/Accounts.Api/Attempts/ProcessEndpoints.cs`; tests `ProcessSessionTests`, `ReadyTests`, `ProcessHeartbeatTests`.

- **Register:**
  - Attempt `FOR UPDATE`. The state must be `assigned` (→ `launching`) or `launching`.
  - The body's build must equal the attempt's `build_revision` and `catalogue_version`, which must still be enabled and list the template. Otherwise 409 `BuildNotAllowed`, nothing written.
  - The first call writes a new UUIDv7 `process_session_id`. A replay while `launching` returns it unchanged.
  - The response reads the seed from `attempt_secrets` and the loadouts from the participants. It never logs any of it.
- **Ready:**
  - The body is parsed and validated purely first (`LootPoolValidator` against the catalogue's table, `LootHash.Compute` against the reported `lootHash`, and `ObjectiveList`). An invalid pool calls the release with `invalid_pool` and answers 422; invalid objectives answer 422 with nothing written.
  - A valid body: attempt `FOR UPDATE`, the session matches, `launching` → `ready`, `ready_at` and `join_deadline = clock + BLACKGLASS_JOIN_DEADLINE_SECONDS`, objectives and pool inserted.
  - A replay while `ready` with an identical body → 200; a different body → 409 `InvalidState`.
- **Heartbeat:**
  - Attempt `FOR UPDATE`, the session matches, stamp `last_process_heartbeat_at`.
  - `unresponsive` → `running`.
  - For each listed participant: `joined` → `left` when not connected, `left` → `joined` when connected (guarded, one update each).
  - Participants are validated before any write: each a participant of this attempt, at most once (400 otherwise, nothing written). Only started participants move; an `admitted` one never becomes `joined` here (B10).
  - The response is `{}`. After cancellation the credential is 401 on this route like every other (R4); stop travels through the agent.
- **Lease selection** (the worker): `running` attempts with `last_process_heartbeat_at < clock − BLACKGLASS_PROCESS_LEASE_SECONDS` → `unresponsive`. Nothing else.
- [ ] **Step 1: Write the failing tests:**
  - `Register_returns_the_session_seed_and_snapshots_never_the_salt`;
  - `Register_from_assigned_moves_to_launching` (lost `launching`);
  - `A_register_replay_returns_the_same_session`;
  - `Register_with_another_build_or_a_disabled_build_is_BuildNotAllowed_and_writes_nothing` (theory);
  - `Ready_stores_objectives_and_pool_and_sets_the_join_deadline`;
  - `An_invalid_pool_cancels_and_releases` (theory per validator rule);
  - `Invalid_objectives_write_nothing`;
  - `Ready_replay_identical_is_200_and_different_is_409`;
  - `Every_process_call_with_another_session_is_WrongSession`;
  - `A_lapsed_process_lease_makes_running_unresponsive_and_a_heartbeat_restores_it`;
  - `Heartbeat_moves_joined_and_left`;
  - `Heartbeat_never_marks_an_unstarted_participant_joined`;
  - `Heartbeat_with_a_foreign_or_duplicate_participant_is_400_and_writes_nothing`;
  - `Every_process_route_is_401_after_cancel` (R4, heartbeat included);
  - `A_wrong_loot_hash_cancels_as_an_invalid_pool` (R3);
  - `Ready_stores_the_loot_hash_and_table_version_once`.
- [ ] **Step 2:** run (FAIL), implement, run (PASS).
- [ ] **Step 3: Commit.** `Phase 13 Stage 8: process sessions with build checks, ready with pool and objective validation, process heartbeat and lease`.

## Task 8: Join tickets

**Files:** `src/Accounts.Persistence/Attempts/TicketStore.cs`, `AttemptEndpoints.cs` (mint, `join`), `ProcessEndpoints.cs` (redeem), `AttemptSettings.cs` (`BLACKGLASS_TICKET_SECONDS`, 120, range 10..600); tests `TicketTests`, `TicketConcurrencyTests`.

- **Mint** (client):
  - The attempt must be `ready` or `running`, and the participant `admitted`, `joined` or `left`.
  - Revoke the participant's live tickets (`revoked_at = clock`), insert the new hash with `expires_at = clock + ticket seconds`, and return the plaintext once.
  - Rate-limited per account.
- **Redeem** (process):
  - The hash is looked up unlocked. Unknown → 401 `UnknownToken`.
  - Lock in order and re-check:
    - another attempt → 409 `WrongAttempt`;
    - expired or revoked → 401;
    - already redeemed by **this** session → the same answer (replay);
    - redeemed by another session → 401;
    - participant released or settled, or attempt not `ready`/`running` → 409 `InvalidState`.
  - Otherwise set `redeemed_at` and `redeemed_by_session`, and return `{accountId, operativeIds}` in snapshot order.
- [ ] **Step 1: Write the failing tests:**
  - `A_ticket_redeems_once_and_returns_the_snapshot_operatives`;
  - `A_redeem_replay_by_the_same_session_returns_the_same_answer`;
  - `A_ticket_redeemed_by_one_session_is_401_for_another`;
  - `An_expired_ticket_is_401` (backdated as the superuser; time is never mocked);
  - `Minting_revokes_the_earlier_live_ticket`;
  - `A_ticket_for_another_attempt_is_WrongAttempt`;
  - `A_ticket_of_a_released_participant_is_InvalidState`;
  - `Twenty_concurrent_redeems_of_one_ticket_succeed_once` (one session; across two sessions, exactly one wins);
  - `Mint_before_ready_and_after_release_is_InvalidState`;
  - `GET_shows_join_host_and_port_but_never_a_ticket_or_seed`.
- [ ] **Step 2:** run (FAIL), implement, run (PASS).
- [ ] **Step 3: Commit.** `Phase 13 Stage 8: single-use, account- and attempt-bound join tickets (mint, revoke, redeem, replay)`.

## Task 9: The start commit with exactly-once charging

**Files:** `src/Accounts.Persistence/Attempts/StartStore.cs`, `ProcessEndpoints.cs`; tests `StartTests`, `StartConcurrencyTests`.

- **One READ COMMITTED transaction, Start lock order:**
  1. Session check. An already started participant answers its stored result here, before the deadline check (B8).
  2. The participant must have a ticket redeemed by this session (`NotRedeemed` otherwise).
  3. `clock > join_deadline` → `DeadlinePassed` (§B8).
  4. Participant `admitted` → `joined` with `started_at = clock`.
  5. The hold `reserved → charged`; for a positive hold, after materialization, insert `charge:<attempt>` with delta 0 and `balance_after` = the materialized balance.
  6. The attempt `ready → running` with `started_at` if it was `ready`.

  An already `joined`, `left` or later participant → 200 with the stored `started_at`, nothing written. A released participant or a cancelled attempt → 409 `Cancelled`. No retry.
- [ ] **Step 1: Write the failing tests:**
  - `A_start_charges_once_and_moves_the_attempt_to_running`;
  - `A_repeated_start_charges_once_and_returns_the_stored_startedAt` (20 concurrent repeats too);
  - `A_zero_cost_start_charges_with_no_ledger_entry`;
  - `A_start_without_a_redeemed_ticket_is_NotRedeemed`;
  - `A_start_after_the_join_deadline_is_DeadlinePassed_and_the_worker_then_releases`;
  - `A_lost_start_acknowledgement_retried_after_the_join_deadline_returns_the_stored_result` (B8);
  - `A_start_after_cancel_is_409_Cancelled`;
  - `A_start_and_a_cancel_race_and_exactly_one_wins`;
  - `A_start_and_the_join_deadline_race_and_exactly_one_wins`;
  - `A_start_drains_with_a_grant_and_a_queued_publication_without_deadlock` (mutation: dropping the `accounts` key-share step makes it 40P01);
  - `A_start_commit_failure_injected_after_each_write_leaves_nothing` (a trigger installed by the superuser raises at each step: the hold stays `reserved`, no `charge` entry, the participant stays `admitted`);
  - `Per_participant_charges_are_independent` (co-op shaped, direct SQL).
- [ ] **Step 2:** run (FAIL), implement, run (PASS).
- [ ] **Step 3: Commit.** `Phase 13 Stage 8: backend-committed per-participant start with exactly-once charge`.

## Task 10: Unavailability, secrecy across the lifecycle, and the HTTP lifecycle

**Files:** tests `LifecycleTests`, `UnavailabilityTests`, `DtoAuditTests` (extended), `LogSecrecyTests` (extended).

- [ ] **Step 1: Write the failing tests:**
  - `The_whole_lifecycle_to_running_is_driven_over_HTTP`: host register (store call), build allow, heartbeat, deploy, claim, launching, register, ready, mint, redeem, start, heartbeat. Assertions: energy charged once, inventory locked, `GET` shows `running`;
  - `No_log_or_client_body_in_a_whole_lifecycle_contains_the_seed_or_salt` (capturing logger over every category; every client and agent body; the seed's decimal and the salt's hex and base64);
  - `Every_client_and_agent_dto_has_no_seed_salt_or_credential_hash_member` (reflection);
  - `A_database_outage_during_running_never_voids`: take only this test's database offline (`allow_connections false` and terminate its backends; never the shared container) past both leases, run sweeps (which fail), restore, sweep. Expected: at most `unresponsive`, then `running` on heartbeat; participant `joined`; hold `charged`; no `release` or refund;
  - `An_API_restart_during_running_changes_nothing`;
  - `A_lapsed_host_lease_never_cancels_a_running_attempt`;
  - `A_backend_restart_before_start_keeps_the_deadlines_authoritative` (database time).
- [ ] **Step 2:** run (FAIL where behaviour is missing), fix, run (PASS).
- [ ] **Step 3: Commit.** `Phase 13 Stage 8: lifecycle over HTTP, no-void under unavailability, secrecy across the lifecycle`.

## Task 11: Smoke, VM and restore drill

- The smoke script gains a Stage 8 section. Every secret is written to files under `$work` and never echoed.
  1. `host register` with the secret saved to a file;
  2. `build allow`;
  3. heartbeat by `curl`;
  4. deploy;
  5. claim;
  6. launching;
  7. register (the seed is checked present and never printed);
  8. ready;
  9. ticket;
  10. redeem;
  11. start;
  12. energy charged, attempt `running`;
  13. a second account's deploy cancelled pre-start, releasing once.
- The VM run: smoke, exposure probe and restore drill at schema 8, invariants included; VM state identical before and after.
- [ ] **Commit.** `Phase 13 Stage 8: smoke coverage of the lifecycle to running`.

## Task 12: Docs as built, final review, stop

- The final full suite (§D).
- The spec's and this plan's "As built", the stage plan, `CLAUDE.md`, README settings rows.
- The final whole-branch review (most capable model) with this plan's Review Focus. One fix pass.
- Report and stop: no push or merge without the owner.

## Acceptance criteria

1. `0008` applies to a populated Stage 7 database, copying every secret, and 0000–0007 are unchanged. The 0000–0008 invariants hold after every test and after the restore drill.
2. Hosts exist only by audited registration; each authenticates with its own secret; a process credential works only for its attempt, until it ends.
3. No attempt is assigned twice; no host exceeds its capacity; no live slot shares a port; incompatible builds, templates and catalogues are never claimed or registered.
4. The seed reaches only the registered process; the salt reaches nothing; neither appears in a log, client body, audit row or constraint `DETAIL`.
5. Tickets are single-use, expire, are revoked by a newer mint, and are bound to attempt, account and session.
6. The start charges exactly once per participant under repeats, races and lost acknowledgements, and only after a redeemed ticket and before the join deadline.
7. Every pre-start exit releases each participant exactly once; holds are independent per participant.
8. After a start nothing voids, cancels or refunds; outages and lapsed leases give at most `unresponsive`.
9. Every injected failure, lock timeout and terminated connection leaves no partial state; no energy-touching transaction is retried automatically.
10. Infrastructure: no Unity path, firewall, SSH, Docker networking or exposure change, and no deployment.

## Out of scope

- Settlement, quarantine, voids, refunds, receipts and anomaly events (Stage 9).
- The mission host agent, systemd units and polkit (Stage 10).
- Unity backend core, the server lifecycle and ticket admission in Unity (Stages 11–14).
- Resume tokens and the reconnect window (Stage 15); `AlreadyConnected`.
- Co-op enablement; prewarming; orchestration; HTTP admin routes.

---

## As built (2026-10-17, branch `phase13-stage8`, implementation commits local, not pushed or merged)

**Commits:**
- `f932369`: rulings recorded.
- `6377cf5` and `b215a22`: the VM runner.
- `85d4c91`: Task 1.
- `615e5d0`: Task 2.
- `4a8c308`: Task 3.
- `d21606c`: Task 4.
- `acecc1f`: Task 5.
- `c904864`: Task 6.
- `a83a1ed`: Checkpoint 1 fixes.
- `5c498c5`: Task 7.
- `cc06beb`: Task 8.
- `84c1eaa`: Task 9.
- `1f63f31`: Task 10.
- `8d662e5`: Task 11.
- `a63af8d`: final review fixes.
- `5aa1919`: README.
- The tested `Backend/` tree is `37abd13` (`HEAD:Backend`).

**Schema:** version 8. `0008_hosts_sessions_tickets` applies to a populated schema 7 (`Seeds/0008.sql`, `Migration_0008_applies_to_a_populated_0007_database_and_copies_every_secret`). `0000`–`0007` are byte-identical (pinned checksums). The invariants file is `0008_sessions.sql`.
- Additions after the plan:
  - `credential_generation` (R1);
  - `loot_table_version` and `loot_hash` (R3);
  - `attempt_exit_reports` and `attempt_exit_results` (R6), bound to the attempt's host and session;
  - a trigger rule: a live attempt's slot is released only after its agent exit report (Checkpoint 1);
  - agent capacity of at most 64, the units one heartbeat can list.

**Tests (VM runner, tree `37abd13`):**
- Domain: 713 passed, 0 failed.
- Integration: 1,589 passed, 0 failed, in 6 m 3 s (Stage 7 had 1,402 tests in about 52 min through the tunnel).
- The 172-test benchmark: 47 s on the VM against 10 m 54 s on Windows.
- Cold build 38 s; warm build 3 s.
- One final full run. It was repeated twice because code and a race test changed after the first.

**VM:**
- Smoke: 0 failures, on `8d662e5` and again on `5aa1919`, including the Stage 8 lifecycle section.
- Off-host probe: 5080, 5432 and 55432 closed; 22 open.
- Restore drill at schema 8: 6 invariant files held.
- Docker containers, volumes and listeners identical before and after.
- No deployment of the live stack.

**Reviews:**
- **Checkpoint 1** (after Task 4): 0 Critical, 0 Important, 10 Minor.
  - Fixed: M1 (slot release bounded in the database), M3 (exit evidence's host foreign key), M4 (capacity within one heartbeat), M7 (process-scheme isolation tests with valid credentials), M8 (static check against parameter logging), M9 (runner lock over sync and copy-back), M10 (accepted risk recorded in the README).
  - Deferred: M2 (a first write of lifecycle columns is not tied to its edge; the CHECKs pin most states), M5 (Stage 10, below), M6 (owner decision, below).
- **Final review:** 0 Critical, 0 Important, 4 Minor.
  - Fixed:
    - M1: register re-checks the presenting credential under its lock, and a failure report after the attempt has ended gets the uniform 401;
    - M2: `ready` past the launch deadline is refused;
    - M4: `ready` under another catalogue is `409 CatalogueMismatch`.
  - Recorded: M3 (Stage 10, below).

**Rulings made during execution:**
- Admission's attempt-secrets and catalogue writes, and `release_reason`, moved into Task 2 (dropping the columns needs them).
- `SessionSchemaTests` was written alongside the migration; the existing suite's 102 failures drove the migration.
- The VM runner connects to the container's address (scram-sha-256), not its loopback, which `pg_hba` trusts.
- `build allow` takes `--templates id:v[,…]`.
- The process-scheme HTTP tests landed with the first process route.
- Default authentication forwards by token prefix; the default authorization policy is Bearer only.
- Admission resolves the template before health, so an unknown template is 422.
- The claim response adds `recovered`.
- The launch-deadline drain is covered by the shared release core's tests.
- Stage 7 tests that expected a refused cancel in `assigned` now use a started participant.
- The start takes `accounts FOR KEY SHARE` first; no 40P01 mutation is asserted, because the start writes the wallet at most once.

**Owner decision requested (Checkpoint 1 M6):** a disabled host's process credentials keep working, so its running attempts continue. Disabling stops claims and the agent's own routes. A kill path for a compromised host is left to a later `host_declared_lost`.

**Obligations:**
- **Stage 9:**
  - evaluate `attempt_exit_reports`/`attempt_exit_results` as positive evidence for any protective void;
  - validate manifests against `attempt_loot_pool`, `loot_hash`, `layout_hash` and `attempt_objectives`;
  - add the hold's settle and refund edges;
  - replace `mission_attempt_started` and the transition function if settlement needs other edges.
- **Stage 10 (agent):**
  - persist the claim key before sending, and the claim response durably;
  - get `launching` acknowledged with that generation before `systemctl start`;
  - on `CredentialSuperseded`, retry the claim with the same key;
  - send `complete: true` only after a successful, fully parsed enumeration, and either list every unit it has acknowledged launching but not yet seen exit, or serialize enumeration with launches (Checkpoint 1 M5, final M3);
  - stop the units named in `stop`;
  - report `exited` after the unit is gone.
- **Stage 12 (server):** treat a 401 on its credential after registration as a stop (R4); compute `lootHash` as R3 defines it.
- **Stage 17:**
  - deploy with `log_error_verbosity=terse` (B2);
  - keep `BLACKGLASS_DEV_HOSTS` disabled, with no `dev_fixture` rows in production.

## R8. Host disable semantics (owner decision after provisional approval, 2026-10-17)

There are three distinct host administration operations:

| Operation | Purpose and effect |
|---|---|
| **Drain** | Planned maintenance. No new claims. The agent and its processes keep their credentials and work normally: heartbeats, supervision, exit reports, and later result relay |
| **Disable** | Emergency isolation (detailed below) |
| **Declare lost** | Stage 9. Confirmed unrecoverable host or outbox loss, with positive recorded evidence and an explicit reason. Only that workflow may perform protective voids and refunds. Suspected compromise or credential revocation alone never triggers a void |

**Disable** runs in one audited READ COMMITTED transaction, in the host lock order: the host row `FOR UPDATE`, then its attempts.
- **What it changes:**
  - `enabled = false`, so the host gets no claims;
  - its secret is replaced by the hash of an unknown random secret, and `secret_revoked = true`;
  - every attempt assigned to the host that is not yet cancelled or settled gets `credential_revoked_at`, written once.
- **Mechanism:** the smallest permanent one, a revocation record per attempt plus a revoked flag on the host.
  - A later enable never restores a revoked process credential.
  - A claim retry never re-issues a revoked credential, and launching a revoked attempt is refused.
  - `host enable` is refused (`SecretRevoked`, exit 66) until `host rotate-secret` issues a new secret. A rotation clears the flag but does not enable the host.
- **Authentication:**
  - The `Host` scheme refuses a disabled or revoked host.
  - The `Process` scheme refuses an attempt with `credential_revoked_at`.
  - Every process store re-checks the revocation under the attempt lock (register, ready, heartbeat, failed, redeem, start), and so does ticket minting. A request that authenticated before the disable committed therefore writes nothing after it: those answers are the uniform 401, and minting is a 409.
  - Disable locks the attempt rows after the host row, which is the claim and heartbeat order. Process paths never lock a host row, so there is no reverse dependency.
- **What it never does:**
  - cancel a started attempt, release a charged hold, void, refund, or free a slot;
  - delete or change any attempt, participant, energy, insurance, inventory or evidence record.

  The re-enabled host's agent, with its new secret, can still report exits and heartbeat for the old attempts, freeing their slots on evidence.
- **Agent exit reports** are not refused for revoked attempts: they are the evidence that frees slots.

**Obligations:**
- Backend revocation does not terminate a running Linux process.
- **Stage 10:** a trusted emergency stop for mission units, including when the agent can no longer authenticate.
- **Stage 12:** the server treats a revoked authorization (401) as a stop, keeping its durable results, without settlement or energy changes. The ordinary drain path never interrupts a legitimate running mission.
- **Stage 9:** evidence from a disabled or compromised host is weighed as such. `host_declared_lost` needs positive recorded evidence and an explicit reason.

**Tests** (`HostDisableTests`, plus the updated host tests):
- drain keeps the agent and its processes working;
- disable revokes the host secret and every process credential, and preserves every record;
- disabled-host processes are refused at the store, even past authentication;
- re-enabling needs a rotation and never restores a revoked credential, and the reauthorized host returns to service;
- unknown, foreign, disabled and rotated host credentials get identical 401s;
- a failed disable leaves no partial state;
- disable races claims, registration and the start commit with serializable outcomes and no duplicate charge;
- the CLI is confirmed and audited, with no secret printed.

**R8 independent security review:** no Critical or Important findings. Fixed:
- **Stale host secrets (M1, M2):** every host store operation now re-checks the presented secret's hash inside its own transaction. Claim and heartbeat check it under the host lock; launching and exit reports check it with a plain read while they hold the attempt row, which the disable must lock after the host. So a request that authenticated with the old secret writes nothing once a disable (or a rotation) commits, even across a later re-enable.
- **Dev fixtures (M3):** a disabled dev fixture can be enabled again, since it has no agent secret to rotate.
- **Tests (M4):**
  - `ready`, including the invalid-pool path, is refused once the credential is revoked;
  - revocation is permanent in the trigger;
  - a re-enabled host's exit report for a revoked attempt is accepted as evidence;
  - stale-secret writes are refused.
- **Operator view (M5):** the confirmation preview shows `target.secretRevoked`.

A disable that times out on a lock (exit 75) isolates nothing and must be re-run.

**Final validation after R8 (2026-10-17):**
- Backend tree `0e58e76` (`286ae93`): 713 domain and 1,602 integration tests passed, 0 failed, the integration suite in 6 m 3 s.
- The concurrency and failure-injection classes, rerun on their own: 89 passed.
- VM smoke: 0 failures. Off-host probe: 5080, 5432 and 55432 closed; 22 open. Restore drill at schema 8: passed.
- Docker containers, volumes and listeners identical before and after. The runner left no `bg_t_*` database, container or tunnel.
- Only `Backend/`, `Docs/` and `CLAUDE.md` changed.
- Approved by the owner, then fast-forwarded into `main` and pushed.
