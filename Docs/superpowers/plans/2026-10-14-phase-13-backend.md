# Phase 13 backend and settlement: staged implementation plan

Date: 2026-10-14. **Revision 2.1.** Status: approved by the owner; Stage 0 merged into `main` and pushed (`c76d3d5`); Stage 1 and later need the owner's go-ahead. Stage 1's detailed plan: `Docs/superpowers/plans/2026-10-14-phase-13-stage1-backend-foundation.md`. Design: `Docs/superpowers/specs/2026-10-14-phase-13-backend-design.md` revision 2.1 ("the spec"); decisions 048–050. Stage 0 drafts (as applied): `Docs/superpowers/specs/2026-10-14-phase-13-stage0-drafts.md`.

This is a stage-level plan. Each stage gets its own detailed, test-first task plan just before it starts, once its dependencies are approved. **Nothing here is implemented, and no number below is a measurement.**

---

## Rules for every stage

1. **Branch and merge.** One branch per stage (or per small group of stages). The owner decides every merge and push.
2. **Baseline.** Before the first Unity change, measure fresh EditMode and PlayMode baselines. Keep both green at every Unity stage.
3. **Real database.** Backend stages test against a real PostgreSQL 17. No transactional behaviour is tested against an in-memory substitute.
4. **No unrelated changes.** Each stage lists the paths it may touch. Before closing, run `git status --short` and `git diff --stat main...HEAD` and show that:
   - only the listed paths changed;
   - the owner's `Assets/Settings/Mobile_RPAsset.asset` and `ProjectSettings/URPProjectSettings.asset` are still unstaged and still differ only by the owner's changes;
   - `.claude/` holds only scratch output under `.claude/scratchpad/phase13/`;
   - no gameplay, scene, prefab, HUD, input or project-settings file outside the stage's list changed.
5. **Evidence.** Each stage's results go to `.claude/scratchpad/phase13/stage-NN/` (local, not committed):
   - test summaries with counts;
   - command transcripts and logs;
   - the diff stat.

   Its report quotes the counts. "Tests pass" without counts is not evidence.
6. **No exposure.** Every listener stays on loopback, and access goes through SSH.
7. **Provisional values.** Every value in the spec's §19 is configuration. No stage hard-codes one.

---

## Standing obligation: `InventoryLocked` before mission admission (owner, 2026-10-16)

**`InventoryLocked` must become a mandatory, fail-closed, database-backed check against active mission participations before mission admission is enabled.**
- It applies to **both inventory mutations (`POST /v1/inventory/operations`) and advancement picks (`POST /v1/operatives/{id}/choices`)**, and to `GET /v1/inventory`'s `locked`.
- The current always-unlocked development fallback (`NoParticipationsYet`, and the stores' optional `IInventoryLock` parameter that defaults to it) **must not remain reachable in production**. The lock becomes a required dependency of every store that uses it.
- "Fail-closed": if the participation query fails or cannot run, the mutation is refused (a database failure, never a silent "unlocked").
- Stage 7 cannot be accepted, and no admission route may be enabled, until this holds. See Stage 7's acceptance criteria.

---

## Stage 0: decisions and authorization (docs only, owner-driven)

**Inputs (received 2026-10-14):**
- the final rulings Q-1 to Q-5;
- acceptance of the A-items;
- corrections 1–9.

**Work:**
- add the drafted decisions 048–050 to `Docs/Decisions.md`;
- apply the drafted `CLAUDE.md` change, worded as the owner approves;
- add audit §9.5 (C-6).

**Evidence:**
- the diff contains only `Docs/Decisions.md`, `CLAUDE.md`, `Docs/OnlineReadinessAudit.md` and the three Phase 13 design documents;
- the owner's two settings files are unchanged and unstaged.

**Exit:** the decisions are recorded and Phase 13 is authorized in `CLAUDE.md`. **Status: done; merged into `main` and pushed (`c76d3d5`, 2026-10-14).**

## Environment preparation E1: Docker on `bg-dev-services01` (before Stage 1's database work)

`bg-dev-services01`, as reported by the owner:
- Debian 13, 4 vCPUs, about 8 GiB of memory, an 80 GB disk;
- SSH verified and public networking configured;
- Docker not yet established.

Stage 1's integration tests need PostgreSQL 17 in Docker, so the Docker installation moved out of Stage 17 into E1.

**Status: done 2026-10-14 (owner-approved).** The full record is in the Stage 1 plan, Part A.
- **Access:** VM administration uses the alias `bg-dev-services01-admin` (`admin` with `sudo`). Nobody is in the `docker` group. The owner verified console recovery as `admin` and the host-key fingerprint.
- **Docker:** Engine 29.9.0, Compose 5.6.0 and containerd.io 2.4.2-2, from Docker's Debian 13 repository. `daemon.json` (`ip` plus `default-network-opts` `host_binding_ipv4` = `127.0.0.1`) was written before the first start.
- **Verified:** published ports on the default bridge and on a Compose network bind only to `127.0.0.1`, over TCP and UDP. They are unreachable off-host over IPv4 (from the PC) and over IPv4 and IPv6 (from `bg-dev-mission01`).
- **Host changes:** IPv4 forwarding is on, the `FORWARD` policy is DROP, and Docker's NAT chains are added, with no `INPUT` rules. The listeners after the probes were identical to before.
- **Brought forward from Stage 17 with the owner's approval, as separate verified changes:**
  - sshd drop-in `10-blackglass-hardening.conf`: key-only, `PermitRootLogin no`;
  - resolved drop-in `10-blackglass-no-llmnr.conf`: 5355 closed.
- **Public listeners now:** only SSH on 22. `ufw` is untouched.

- **Reboot-validated 2026-10-14.** All nine checks passed: SSH, Docker, Compose, `daemon.json`, the firewall and NAT rules, the network, LLMNR, listeners and cleanup.
- **Follow-ups:**
  - 61 Debian upgrades are pending;
  - the Docker update policy is open, because unattended-upgrades covers Debian origins only.

Stage 17 keeps:
- `ufw`, and the firewall review (the `nftables` package came in as a Docker recommendation; its service is disabled and must stay so until then);
- the live stack, its secrets and the backup timer;
- the off-host IPv4 and IPv6 TCP and UDP scans of the live stack;

and it re-verifies the Docker version, `daemon.json`, and the sshd and resolved drop-ins.

## Stage 1: backend foundation (skeleton, migrations, backup and restore)

Detailed task plan: `Docs/superpowers/plans/2026-10-14-phase-13-stage1-backend-foundation.md`. Tasks 1 to 4 need no database; Task 5 onward needs E1.

**Status: done; merged into `main` and pushed (`ad14373`).** Task plan: `Docs/superpowers/plans/2026-10-14-phase-13-stage1-backend-foundation.md`. Evidence (test counts, drill and smoke transcripts, exposure output, diff stat): `.claude/scratchpad/phase13/stage-01/summary.md` (not committed).

**Touches:** `Backend/` (new), `.gitignore` (the `Backend` csproj/sln exceptions and `bin`, `obj`, `secrets`), `.gitattributes` (LF for `Backend` scripts and SQL), and in Task 13 the spec's §2.3, §4.4 and §4.5 rows.

**Work:**
- create the solution and its projects (spec §2.3);
- add `GET /health`;
- write the migrator: explicit `NNNN_name.sql`, SHA-256 checksums, one transaction per file, `lock_timeout`, refuses a changed file, separate `migrate` command;
- make the API refuse to start on a database that is behind;
- create the `accounts_migrator` and `accounts_app` roles;
- write migration `0000_foundation` (privileges);
- write `deploy/services/compose.yaml`:
  - the API on `127.0.0.1:5080:8080`;
  - PostgreSQL on an `internal: true` network with no `ports:`;
- write `backup.sh` (`pg_dump -Fc`) and `restore-drill.sh` (restore into a scratch database, run the invariant queries, compare row counts);
- make the deployment order stop API → backup → migrate → start (no zero-downtime compatibility; owner, 2026-10-14).

**Tests:**
- the migrator: empty database, re-run (no-op), changed file (refused), behind-version API start (refused);
- an upgrade test harness: apply up to N−1, seed, apply N, run the invariants;
- the restore drill on a seeded database;
- a compose check (static test) that `postgres` has no `ports` and the API binds `127.0.0.1` only.

**Evidence:**
- `dotnet test` counts;
- the restore-drill output;
- integration tests run from the PC against a loopback-only test cluster on `bg-dev-services01` (`127.0.0.1:55432`, `cluster_name=blackglass-test`) through an SSH tunnel;
- the drill and smoke transcripts come from `bg-dev-services01`.

**Exit:** the foundation can migrate, back up and restore a database, with evidence.

## Stage 2: catalogue export

**Status: done.** Corrections after review (owner, 2026-10-14): the backend enforces the loot table's 0-6 container bound, and the role and Recon Scan builders give the assets they create their approved ids. Merging into `main` is owner-authorized once the gates pass. Detailed plan: `Docs/superpowers/plans/2026-10-14-phase-13-stage2-catalogue-export.md`. Evidence: `.claude/scratchpad/phase13/stage-02/summary.md` (not committed).

**Touches (owner-approved expansion, 2026-10-14):**
- `Assets/_Project/Editor/Backend/` (new Editor-only assembly `Blackglass.Backend.Editor`);
- additive ids: `AbilityDefinition`, `OperativeRole` (and their 7 assets, one `id:` line each) and `StarterLoadout.Operatives`;
- the EditMode test assembly's references; `CatalogueExportTests.cs` and `CatalogueBuilderTests.cs`;
- `Backend/catalogue/`, `Backend/contracts/catalogue/` (shared golden vector);
- `Backend/src/Accounts.Domain/`, `Backend/tests/Accounts.Domain.Tests/`, `Backend/Backend.sln`;
- the `serve` startup gate in `Backend/src/Accounts.Api/` (exit 4) and its test `Backend/tests/Accounts.Integration.Tests/Unit/ServeCatalogueTests.cs`, `Backend/Dockerfile`, `Backend/README.md`;
- `CLAUDE.md` (Stage 1 done, Stage 2 authorized).
- owner-requested corrections (2026-10-14): `Assets/_Project/Editor/OperativeDataBuilder.cs`, `IntelligenceSceneBuilder.cs` and `Presentation/CombatPresentationSceneBuilder.cs` (approved ids for created roles and the Recon Scan; the PreviewBlast clone carries no id), `Assets/_Project/Editor/Backend/PermanentIds.cs` (the assembly is now `autoReferenced` so the builders can call it), `Assets/_Project/Tests/EditMode/PermanentIdsTests.cs`, and the backend's 0-6 container bound.

**Work:**
- an Editor batch export of items, loot table, progression track, operative definitions (with ability ids) and the starter loadout, with a canonical hash;
- the backend loader and validator.

**Tests:**
- EditMode: the committed file equals a fresh export, and the hash is stable;
- xUnit: invalid catalogues are rejected.

**Evidence:** both test counts; the same `catalogueVersion` on both sides.

## Stage 3: accounts and development authentication

**Status: implemented on branch `phase13-stage3` (owner-authorized 2026-10-14); not merged or pushed; the live accounts stack is not deployed.** Measured: ALL 537/0, UNIT 319/0; VM smoke 28 ok, drill 16 ok, off-host probe pass. **Security correction pass (2026-10-15, owner request):** migration `0002_revocation_is_final`, bounded waits, error classification, Base58-explicit CLI with confirmation, stamps after row locks, test-secret hygiene; ALL 669/0, UNIT 372/0, VM smoke 32 ok, 0 failures, drill 16 ok, 0 failures, at `1cda7e9`; schema version 2. Detailed plan: `Docs/superpowers/plans/2026-10-14-phase-13-stage3-accounts-dev-auth.md` (revision 2). Account ids are a PostgreSQL-generated `bigint` shown as Base58 (decision 048 amendment, spec revision 2.2); every later stage's account foreign keys are `bigint`.

**Touches:** `Backend/` only.

**Work:**
- the admin CLI;
- login, the bearer handler and `GET /v1/me`;
- configuration gates for the dev provider and admin routes;
- rate limits.

**Tests:**
- 401 on a bad, expired or revoked token;
- the account is taken from the token only;
- a log-capture test finds no keys or tokens in the logs;
- a source audit bans string-built SQL;
- carried over from the Stage 1 review: with `0001` and its invariants in place, the upgrade tests and `drill-test.sh` still show that invariants numbered at or below the database's version run and higher-numbered ones are skipped, with versions derived from the catalog or the backup (no hard-coded numbers).

**Carried over from the Stage 1 review (owner, 2026-10-14):** if `0001` is the appropriate first forward migration, it revokes PostgreSQL's default `PUBLIC` `EXECUTE` on the migrator's future functions (`alter default privileges for role accounts_migrator revoke execute on functions from public`, global: a per-schema revoke can't remove the built-in global default), with a test. `0000_foundation` stays unchanged. In any case this must land before Stage 6's first function; see `Backend/README.md`, "Required follow-ups".

**Evidence:** test counts; the log-capture output. Covers spec B2.

## Stage 4: provisioning, roster, inventory read and picks (C-4)

**Status: done, merged into `main` and pushed (`f3cada4`), including the admin catalogue-boundary correction (backend ALL 1001/0 with it; see the detailed plan).** Authorized by the owner on 2026-10-15 with rulings D1–D6 (decision 048 amendment 2, spec revision 2.4). Measured: backend ALL 974/0 (Domain 292, Integration 682), UNIT 480/0 (`--filter "Requires!=Postgres"`); Unity EditMode 1524/0, PlayMode 1434/0; the catalogue `catalogueVersion` is `sha256:e83352b1c9d8764a92281551a25bea42621da2b592bc30d35986a5f0a86c496b`. The VM results are in the detailed plan's "As built" section. Detailed plan: `Docs/superpowers/plans/2026-10-15-phase-13-stage4-operatives-inventory.md` (revision 2). It supersedes the work list below where they differ: bigint owned ids, provisioning at account creation plus a backfill, catalogue format 2 with `InventoryRules` (so it touches Unity's editor export and EditMode tests, no scene), and shared vectors.

**Touches:** `Backend/` only.

**Work:**
- migrations `0003` and `0004` (`0002` is Stage 3's `revocation_is_final`);
- idempotent provisioning;
- `GET /v1/roster` and `GET /v1/inventory`;
- `POST /v1/operatives/{id}/choices`, with the rule ported from `PersistentOperativeState.TryPickChoice`. No new progression features.

**Tests:**
- provisioning twice grants once;
- a composite foreign key refuses a cross-account placement;
- the one-item-per-slot index holds;
- a pick needs a pending pick and a known choice;
- a stale version gets 409.

**Evidence:** test counts. Covers spec B3.

## Stage 5: inventory operations

**Status: done, merged into `main` and pushed (`62cbfcf`, 2026-10-16; live stack not deployed).** Authorized by the owner on 2026-10-16 with rulings S5-D1–S5-D8 (spec revision 2.5, §0.5). Measured: backend ALL 1250/0 (Domain 460, Integration 790) at `6a487cc` and again at the merged head `62cbfcf`; Unity EditMode 1579/0 (1524 + 54 shared inventory cases + 1 guard), PlayMode 1434/0. VM smoke, probe and drill passed; see the detailed plan's "As built". Detailed plan: `Docs/superpowers/plans/2026-10-15-phase-13-stage5-inventory-operations.md` (revision 2). It supersedes the work list below where they differ: migration `0005_inventory_edits` (so energy's migration becomes `0006`), the `IInventoryLock` seam for picks and edits, vectors at `Backend/contracts/inventory/vectors.json` with `shared` and `backend` scopes, and the backfill's starter-versus-holdings correction.

**Touches:**
- `Backend/`;
- `Backend/contracts/inventory/vectors.json` (revision 2.5; was `inventory-vectors.json`);
- one new EditMode test file.

**Work:**
- port the rules to the domain project;
- the batch endpoint with row lock, version check and all-or-nothing.

**Tests:**
- every golden vector in xUnit **and** in Unity EditMode against `InventorySession`;
- concurrent edits serialize.

**Evidence:** counts on both sides. Covers spec B4 (the lock half arrives in stage 7).

## Stage 6: energy

**Status: done, approved by the owner and merged into `main` and pushed (2026-10-17), including the owner-required grant-replay correction; not deployed.** Authorized by the owner on 2026-10-16 with rulings E1–E7 (spec revision 2.6, §0.6). Schema version 6. Measured counts, VM evidence, reviews and rulings: the detailed plan's "As built". Detailed plan: `Docs/superpowers/plans/2026-10-16-phase-13-stage6-energy.md` (revision 2). It supersedes the work list below where they differ: non-retroactive policy versions with database-set effective times, a stored partial-progress column, the explicit `energy open` verb, CLI-only grants, and the reconciliation hosted service.

**Touches:** `Backend/` only.

**Work:**
- migration `0006` (energy; `0005` is Stage 5's inventory edits, spec revision 2.5);
- the regeneration function (only after the default `PUBLIC` `EXECUTE` revoke carried over from Stage 1 is in place; grant `EXECUTE` to `accounts_app` explicitly, and test that an ungranted function is refused);
- the wallet under lock;
- the ledger with idempotency keys, **including a `regen` entry for every materialization**, keyed by the anchor it consumed, in the same transaction;
- the wallet-versus-ledger reconciliation check (anomaly on a mismatch, no automatic correction);
- the dev grant.

**Tests:**
- domain: cap, partial intervals, above-cap refunds;
- integration: backdated anchors, concurrent grants, duplicate keys;
- materializing twice, or concurrently, writes one `regen` entry;
- the ledger-sum invariant holds after every test;
- a corrupted balance raises an anomaly and is left untouched.

**Evidence:** test counts.

## Stage 7: insurance, participants and admission

**Status: done, approved by the owner, merged into `main` and pushed (`71827c7`, 2026-10-17); not deployed.** Authorized on 2026-10-17 with rulings S7-D1–S7-D9 (spec revision 2.7, §0.7). Schema version 7. **The standing `InventoryLocked` obligation is fulfilled:** `ParticipationLock` is mandatory, fail-closed and database-backed for edits, picks and reads, and the always-unlocked fallback is deleted. Evidence: the detailed plan's "As built". Detailed plan: `Docs/superpowers/plans/2026-10-17-phase-13-stage7-admission.md`. It supersedes the work list below where they differ (the migration is narrowed to what Stage 7 uses; the `accounts` key-share step in admission's lock order).

**Touches:** `Backend/` only.

**Work:**
- insurance policies, coverage and activations: the same policy **extends** (`greatest(expires_at, now) + duration`), a different policy while active is refused (`409 CrossTierDeferred`), an idempotency key is honoured once; immutability triggers;
- migration `0007` (`0007_attempts_and_insurance`: insurance, mission templates, the host rows the health check reads, attempts, participants with the `one_active_participation` partial unique index, holds, snapshots, deploy requests; builds, tickets, loot pools, objectives and the host agent columns move to Stage 8's `0008`);
- `POST /v1/attempts`, following the spec's §7.3 and the §15.3 lock order;
- the service-health admission check, with a host-health seam until stage 8;
- `GET /v1/attempts/{id}` and `cancel`;
- the worker for `assign_deadline`.

**Tests:**
- **50 parallel deploys** for one account: one attempt, one deduction, the rest answer `409`;
- a reused idempotency key with a different body, sequential and concurrent: `409 IdempotencyKeyReused`, and no attempt, hold or charge;
- a direct SQL insert of a second active participation fails on the index;
- 20 requests with the same idempotency key give one attempt and identical bodies;
- insufficient energy writes nothing;
- no healthy host gives 503 with energy untouched;
- the insurance snapshot is immutable against expiry, extension and new versions;
- an extension keeps the remaining time, and a duplicate key doesn't extend twice;
- the inventory is locked during an active participation;
- a reflection test finds no seed or salt field in any client DTO;
- co-op-shaped data (two participants, direct SQL) keeps holds and snapshots independent.

**Evidence:** test counts; the concurrency test logs. Covers spec B5 (admission half) and B6 (snapshot half).

**Acceptance criteria (mandatory; Stage 7 is not accepted, and admission is not enabled, until all hold):**
- **`InventoryLocked` is a mandatory, fail-closed, database-backed check** against active participations (`one_active_participation`'s rows) under the `inventories` row lock, for inventory mutations **and** advancement picks (the standing obligation above). Tests:
  - an account with an active participation gets `409 InventoryLocked` on an edit and on a pick, with nothing written and the version unchanged;
  - the same account after its participation ends can edit and pick again;
  - a failing participation query refuses the mutation (never answers unlocked);
  - `NoParticipationsYet` and every optional or defaulted `IInventoryLock` parameter are deleted, or unreachable from `serve`. A test resolves the stores from `serve`'s container and asserts the database-backed implementation;
  - `GET /v1/inventory` reports `locked: true` during a participation.
- Every test listed above passes against the real PostgreSQL 17 test cluster.

## Stage 8: hosts, claim, process sessions, ready, tickets and start (backend side)

**Status: done, approved by the owner, merged into `main` and pushed** (2026-10-17; owner rulings B1–B13, the design corrections and the host-disable decision R8, spec §0.8; `Docs/superpowers/plans/2026-10-17-phase-13-stage8-hosts-start.md`, revision 2, with "As built"; migration `0008_hosts_sessions_tickets`, schema version 8; backend 713 domain and 1,602 integration tests green on the VM-local runner; not deployed).

**Touches:** `Backend/` only.

**Work:**
- migration `0008` (the build allowlist, join tickets, loot pools and objectives, and the attempt and host columns that claims and process sessions need);
- host heartbeat and drain;
- claim (`SKIP LOCKED`, port allocation, process credential);
- `launching` and `exited` (pre-start cancel);
- process register (build allowlist), `ready` (pool validated against the table, objectives stored), `failed`;
- ticket mint and redeem;
- the per-participant start commit with the charge;
- the worker for the launch, join and lease deadlines (`running` → `unresponsive` only).

**Tests:**
- 10 claimers and 3 attempts: each attempt assigned once;
- tickets are single-use, expire, and are bound to attempt, account and session;
- a repeated start charges once; a start after a cancel gets 409;
- a pre-start exit releases;
- a lapsed lease after start becomes `unresponsive`, **never** a void;
- an unlisted build can't register.

**Evidence:** test counts. The lifecycle up to `running` can be driven entirely over HTTP by a test client.

## Stage 9: settlement, quarantine, void and monitoring (backend side)

**Status (2026-10-17):** done, merged into `main` and pushed (`d72a325`; schema version 9; not deployed), after the owner's merge corrections; the detailed plan is `2026-10-17-phase-13-stage9-settlement.md` (revision 3, the owner's final rulings, "As built"). Where it differs from this outline, it wins.

**Touches:** `Backend/` only.

**Work:**
- migration `0009` (settlement, review and monitoring; was sketched as `0007`);
- domain validation V4–V14, including conservation;
- the retention selection (category priority plus salted hash), payout and settlement plan;
- the settlement transaction (spec §10.3), with stack consumption as `DELETE` when fully consumed and `UPDATE … WHERE quantity > q` when partial, and per-unit `origin_ref` minting;
- `pool_collections`;
- quarantine: a 24 h escalation alert repeated daily, **never** a void; operator re-validation of the stored manifest; an operator protective void only with the recorded re-validation and outbox check; `operator_actions`;
- the positive-evidence void from `exited {result: absent}`;
- the operator host-loss declaration (Q-3), which disables the host and voids its started, unsettled participants;
- a late result after a void: `409 AlreadySettledAsVoid` with the receipt preserved, plus an anomaly (Q-4);
- receipts;
- `anomaly_events`;
- host drain on threshold (provisional).

**Tests:**
- each validation rule settles nothing and quarantines;
- stack consumption: an exact quantity deletes the row, a smaller one updates it, a larger one aborts; nothing writes `quantity = 0`;
- a non-stackable line of quantity 2 mints two ids; a replay mints none;
- quarantine escalation never changes state; the operator void is refused without its checks; re-validation after a validator fix settles once;
- a late result after a void gets 409, and the receipt is byte-identical;
- **20 identical manifests** apply once; a different manifest gets 409;
- a settlement racing `exited{absent}`: exactly one wins, and the invariants hold;
- retention is identical across 3 retries and changes with the salt;
- the fault hook after every step leaves the database unchanged, and a retry succeeds once;
- `pg_terminate_backend` mid-transaction;
- killing the PostgreSQL container under load, then restarting, then running the invariant queries;
- a lost response returns the identical receipt;
- an `unresponsive` attempt **without** positive evidence is never voided by the worker.

**Evidence:** test counts; fault-injection transcripts. Covers spec B10, B11, B14 and B16 at the HTTP level (manifests written by hand), and the settlement half of B5.

## Stage 10: mission host agent

**Status (2026-10-19): accepted by the owner. Code-complete and infrastructure-validated for trusted-stub workloads**, merged into `main` and pushed. The detailed plan is `2026-10-17-phase-13-stage10-mission-agent.md` ("Final acceptance" lists the evidence: Phases A–D and the mid-mission reboot test on `bg-dev-mission01`). Where it differs from this outline, it wins. Real Unity mission hosting is not validated: Stage 12's B24 (the assigned per-slot TCP gameplay port, enforced on the mission VM) remains a blocking requirement. Minor follow-up: signalled-exit logging (the agent should log a killed mission's signal ahead of systemd's `result=success`).

**Touches:**
- `Backend/src/MissionHost.Agent/` and its tests;
- `Backend/deploy/mission/` (units, polkit rule; written, not installed).

**Work:**
- claim, then launch through an `ILauncher` (a systemd implementation and a fake);
- the per-attempt outbox layout (`/var/lib/blackglass/outbox/<attempt>/`, group-readable by the agent, never written by it); relay with backoff until definitive, recording `done`, `conflict` or `rejected` in the agent's own relay state;
- strict validation of the claim's attempt id (canonical lowercase UUID) before building any unit name;
- at startup, relay before reporting exits;
- exit reports with result presence;
- heartbeat;
- the process credential handed over through a `LoadCredential` file path.

**Tests:**
- relay across API downtime (stop and start the API in the test);
- restart with pending files;
- results relayed before the exit report;
- a torn temp file is ignored;
- 409 and 422 are archived;
- a fake launcher's crash is reported as `absent`;
- a malformed attempt id is refused;
- **VM step:** with the root-owned template and polkit rule installed on `bg-dev-mission01`:
  - unit A can't read, write, rename or delete in unit B's outbox directory, and can write its own;
  - the agent can read both and write neither;
  - polkit refuses the agent `enable`, a malformed unit name and any other unit;
  - the output of `systemd-analyze security` is recorded. The owner is present for these root installs.

**Evidence:** test counts; a relay-across-downtime transcript.

## Stage 11: Unity backend core (library-free)

**Status (2026-10-19):** implemented on `phase13-stage11` per the detailed plan `2026-10-19-phase-13-stage11-unity-backend-core.md` (revision 2.1, "As built"), not merged or pushed, awaiting the owner's review. EditMode 1835/1835, PlayMode 1434/1434, backend green (VM full suite and the Linux outbox tests). Open before Stage 12: final-review I1 (fill attempt containers one entry per plan item, or another owner ruling) and the Mono `-outbox-selftest`. Where the detailed plan differs from this outline, it wins.

**Touches:**
- `Assets/_Project/Scripts/Backend/Core/` (new);
- new EditMode test files;
- `Backend/contracts/` (shared vectors).

**Work:**
- DTOs compatible with `JsonUtility`;
- a canonical JSON writer and hash;
- `MissionItemLedger` (lots, loot first and oldest first, the invariant check);
- `ManifestBuilder`;
- `OutboxWriter` (temp file, fsync, rename);
- `INetAdmission`.

**Tests:**
- hash equality with the .NET vectors;
- the ledger: a picked medkit merged into an owned stack, partial takes, use order, swaps, the mismatch path to `integrity`;
- conservation holds for every generated manifest;
- a source audit: no networking library or `UnityEngine.Networking` in Core.

**Evidence:** EditMode counts against the baseline; one manifest accepted byte for byte by the stage 9 validator fixture.

## Stage 12: server mission from a snapshot, with abilities (D-24)

**Touches:**
- `MissionDirector` (`GenerateForAttempt`, the pool read);
- `SquadRoster` (`InitializeFromSnapshot`);
- `InventorySession` and `SquadInventory` (snapshot start);
- `UnitItems` (additive detail events);
- `ServerSceneBuilder` and the regenerated `ServerMission.unity`;
- `ServerBootstrap` (`-attempt` mode);
- a new `ServerClock`;
- new tests.

**No changes** to client scenes, prefabs, HUD or input.

**Work:**
- the catalogue, loot table, roster and inventory in snapshot mode on the server;
- the spawner path then adds `UnitAbilities`, `UnitIdentity` and `UnitItems`;
- the ledger wired to the `UnitItems` events;
- the outcome freeze at the first terminal event.

**Tests (PlayMode):**
- the snapshot operatives spawn with their gear and abilities;
- Recon Scan and Aimed Shot resolve on the server, and cooldowns are enforced;
- use and collect feed the ledger, which stays consistent with the inventory;
- play after the end changes nothing;
- the offline-scene suites are unchanged;
- the Checkpoint A diagnostic passes.

**Evidence:** full EditMode and PlayMode counts against the baseline; the diff stat shows no client-scene file.

## Stage 13: server ↔ backend lifecycle in Unity

**Touches:**
- `Assets/_Project/Scripts/Backend/Adapter/` (new);
- `ServerOptions`;
- `ServerBootstrap`;
- new tests.

**Work:**
- a `UnityWebRequest` client with retries;
- register (process credential), configure, generate, ready;
- the start hold (`NotStarted`) with idempotent start retries;
- the heartbeat;
- the outcome freeze → the durable outbox write (`.written`, on Stage 11's I/O worker) → the bounded `recorded` notice (any answer or the time bound ends it) → publication (`.ready`) → `NetResultRecordedMessage` (saved, pending) → exit. **The process never polls settlement** (S9-B6, which supersedes this outline's earlier "status poll → `NetResultConfirmedMessage`"): the authenticated player client polls its own receipt (`GET /v1/attempts/{id}/receipt`), and durable publication never waits for the backend.

**Tests:**
- EditMode with an HTTP seam: retries, a lost start response, 409 handling;
- PlayMode against a local backend: one attempt from claim to settlement in the Editor.

**Evidence:** counts; the Editor run's transcript, including the receipt.

## Stage 14: ticket admission, ownership and new requests

**Touches:**
- `Scripts/Net/Core` and `Scripts/Net/Adapter`;
- the harness;
- the tests.

**Work:**
- `NetTicketAdmission`;
- ownership as an explicit id list in `NetWelcomeMessage`;
- state keyed by account;
- the requests `NetInteract`, `NetCollect`, `NetUseItem`, `NetEquipItem`, `NetUseAbility` and `NetAbandon` through the gate;
- the message-shape audit extended.

**Tests:**
- rejections for every new request: not owner, unknown unit, unsearched container, ability on cooldown, not started;
- the hidden-information suites unchanged;
- the full A3 set of nine bad requests.

**Evidence:** counts; harness roles listed with PASS lines. Covers spec B8 and B17 (Editor).

## Stage 15: reconnect window

**Touches:** `Scripts/Net` and the server lifecycle; tests.

**Work:**
- the solo auto-pause on disconnect;
- the wall-clock window, then the `disconnected` outcome;
- re-admission with a new ticket;
- **process-issued resume tokens** (rotated on use, checked locally);
- the reconnect window and hard cap counting only backend-reachable time for disconnected participants;
- resync by account.

**Tests:**
- a reconnect inside the window resumes with no charge;
- outside it, `disconnected` settles;
- with the API stopped, a client resumes with its token;
- with the API stopped, a client that stays away isn't frozen `disconnected` until a full window of reachable time has passed after the API returns;
- a reused (rotated) token is refused.

**Evidence:** counts; harness transcript. Covers spec B12 (Editor).

## Stage 16: minimal authenticated real-client path (D-1, scope from Q-1)

**Touches:** a new online entry scene and its scripts, as Q-1 defines them; the client build script.

**No changes** to `ProceduralMission`, `Prototype` or their HUD and input.

**Work:**
- login;
- inventory, loadout and picks through the API;
- deploy;
- ticket join;
- play through the existing network client view and controls;
- pending, then the receipt.

**Tests:**
- PlayMode against a local backend and an Editor server;
- a source audit: the new scene's code makes no reward or energy claims and only reads receipts.

**Evidence:** counts; a log or recording of a full cycle. Covers spec B18 (local).

## Stage 17: VM deployment and end-to-end acceptance

**Touches:**
- `Backend/deploy/`, `Docs/DedicatedServer.md` and a new operator guide;
- the VMs, configured only with the owner present for the `ufw` steps.

**Work:**

`bg-dev-services01`:
- **assert `BLACKGLASS_DEV_HOSTS` is unset or `disabled` for `api` and `admin`, and that no `dev_fixture` row exists in `mission_hosts`** (Stage 7 obligation: development host fixtures must never count as production capacity);
- **re-verify** Docker Engine **≥ 28** and Compose (installed in E1), with the version recorded;
- **re-verify** `daemon.json` (`"ip": "127.0.0.1"` **and** `default-network-opts` → `bridge` → `com.docker.network.bridge.host_binding_ipv4: 127.0.0.1`); the stack's networks recreated and inspected;
- the secrets files;
- before the stack holds persistent application data: the base images (`postgres:17`, the .NET SDK and ASP.NET images) pinned to approved versions and digests, carried over from the Stage 1 review; no unrelated image upgrades;
- the nightly backup timer;
- `ufw`, following the spec's §4.5 sequence (console checked, second SSH session);
- the `systemd-resolved` LLMNR drop-in (`LLMNR=no`, `MulticastDNS=no`), applied the same SSH-safe way. If the owner prefers to keep LLMNR, 5355 must show filtered instead.

`bg-dev-mission01`:
- the `blackglass-agent` and `blackglass-mission` users;
- the root-owned unit template and the polkit rule (canonical-UUID `start`/`stop` only);
- the outbox directory;
- the tunnel service with its restricted key;
- `ufw`;
- the LLMNR drop-in.

On both: deploy and run the full acceptance set.

**Evidence, recorded in audit §9.5:**
- B1 exposure:
  - `docker version` (≥ 28) and `docker network inspect` (binding option);
  - `ss -H -ltnup` and `ss -H -lunp` on both VMs, matched against the spec's allowlist (including the `systemd-resolved` listeners);
  - `docker ps` ports;
  - the IPv4 and IPv6 nat tables;
  - TCP and UDP scans of every IPv4, global IPv6 and link-local IPv6 address, from the peer VM and the PC;
  - `ufw status verbose`;
- B7: on-demand launch-to-ready times (median and maximum over ≥ 10 launches), which decide whether prewarming is needed;
- B9–B16 harness and `kill -9` transcripts;
- B13 backend-down timeline: the API container stopped mid-mission, play continued, the result pending, then settled after the restart;
- B18 real client against the VMs;
- B19 restore drill on the VM;
- B21 outbox isolation and polkit scope; B22 quarantine and operator flows (with a shortened, test-configured escalation deadline); B23 insurance extension, regeneration reconciliation, key reuse and minting;
- B20 suites, the diagnostic and the existing harness roles.

**Exit:** every criterion B1–B23 has its evidence. The owner decides whether Phase 13 is complete and whether to merge.

---

## Dependencies

```
0 ─ 1*─┬─ 2 ─ 3 ─ 4 ─ 5 ─ 6 ─ 7 ─ 8 ─ 9 ─ 10 ─┐
       └─ 2 ─ 11 ─ 12 ───────────────────────┴─ 13 ─ 14 ─ 15 ─ 16 ─ 17
```

\* E1 (Docker on `bg-dev-services01`) must finish before Stage 1's Task 5, the first database task.

Unity stages 11 and 12 can run alongside backend stages 3 to 10 once stage 2 exists. Stage 13 needs stages 9 and 10. Unity's one-instance limit serializes the Unity stages among themselves.

## Deferred to Phase 14

- mTLS, HAProxy and the VLAN;
- Docker for mission servers;
- prewarming (unless B7 says otherwise);
- a multi-host pool;
- checkpoints;
- corrected-manifest resubmission and late-result reconciliation (Q-4);
- the item ledger;
- external auth providers;
- enabling co-op;
- WAL archiving and PITR;
- a metrics stack;
- replay verification and attestation.
