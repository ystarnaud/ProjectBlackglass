# Phase 13 Stage 0: decision records, the `CLAUDE.md` update, the audit note

Date: 2026-10-14. **Final, as applied in Stage 0** on branch `phase13-stage0`, after the owner's final rulings of 2026-10-14. This file keeps the exact text applied to the three targets, so the design folder and the records stay aligned:
- Draft A → `Docs/Decisions.md` (decisions 048–050);
- Draft B → `CLAUDE.md` ("Current Development Stage" and "Current Priority");
- Draft C → `Docs/OnlineReadinessAudit.md` (§9.5, and the Phase 13 status in §9.1).

Design: `Docs/superpowers/specs/2026-10-14-phase-13-backend-design.md`, revision 2.1. Plan: `Docs/superpowers/plans/2026-10-14-phase-13-backend.md`, revision 2.1.

---

## Draft A: `Docs/Decisions.md`

## 048. Phase 13 backend foundation: stack, identity, exposure and data safety (2026-10-14)

Design: `Docs/superpowers/specs/2026-10-14-phase-13-backend-design.md` (revision 2.1). Plan: `Docs/superpowers/plans/2026-10-14-phase-13-backend.md`.

- **Decided (owner rulings of 2026-10-14):**

  **Stack:**
  - **Backend:** ASP.NET Core on .NET 10 and PostgreSQL 17, run with Docker Compose on `bg-dev-services01`.
  - **Data access and tests:** Npgsql and Dapper, explicitly versioned SQL migrations, and xUnit tests against a real PostgreSQL. No EF Core, JWT library or Identity framework.
  - **Location:** the backend lives in `Backend/` in this repository.
  - **Catalogue:** the Unity catalogue (items, loot table, progression track, operative definitions, starter loadout) is exported to committed JSON. It is versioned by a content hash, and the backend and mission builds are checked against that hash.
  - **What stays provisional:** HAProxy, a private VLAN, Docker for mission servers and orchestration remain provisional and out of Phase 13.

  **Identity:**
  - **Accounts:** a server-generated UUIDv7 account id. External identities are linkable rows; Phase 13 has a development provider only.
  - **Development login:** an admin-issued 256-bit account key, exchanged for an opaque, hashed, short-lived access token.

  **Mission host credentials:**
  - a per-host secret, used only by the host agent;
  - an attempt-scoped process credential, delivered through systemd credentials;
  - a build allowlist (revision plus catalogue hash).

  **Item identity:**
  - Non-stackable items have permanent server ids, and consumables are fungible quantities per location.
  - Items minted by the backend get `UUIDv5(origin_ref)`. The reference names the kind, the attempt, the account, the container line and a **unit index**, so each unit of a non-stackable line gets its own id and a retry mints the same ids.
  - In-mission ids are local and never persisted.

  **Exposure:**
  - **Docker:** Engine 28 or newer. The API is published explicitly on `127.0.0.1:5080`, and PostgreSQL has no published port.
  - **Daemon safeguard:** `daemon.json` sets `"ip": "127.0.0.1"` for the default bridge **and** `default-network-opts` (`bridge`, `com.docker.network.bridge.host_binding_ipv4 = 127.0.0.1`) for the user-defined networks Compose uses.
  - **Exposure checks:** listeners, IPv4 and IPv6 addresses including link-local, and TCP and UDP are checked from outside both VMs, against an allowlist that includes the existing `systemd-resolved` listeners. LLMNR is disabled through a `resolved` drop-in.
  - **`ufw`:** enabled on both VMs by a sequence that keeps SSH working: console checked first, a second SSH session before closing the first.
  - **Paths:** every service path runs on loopback through SSH tunnels.

  **Data safety (in the foundation, not deferred):**
  - migrations are forward-only and checksummed, and run as a separate command after a backup;
  - expand and contract;
  - upgrade tests;
  - a restore drill with invariant queries;
  - separate migrator and application database roles.

- **Why:**
  - Permanent state has to be authoritative and durable outside any client or mission process.
  - Opaque tokens and hashed secrets avoid key-management machinery at this scale.
  - Docker's NAT rules bypass `ufw`, and the daemon's `ip` option alone doesn't cover Compose networks, so exposure is controlled where Docker binds and is then verified, not assumed.

- **Rejected:**
  - EF Core: it hides locking and brings its own migrations machinery.
  - JWTs: no revocation without extra state.
  - Pre-started mission processes: replaced by on-demand units (049).
  - Relying on `ufw` for published ports.
  - Deferring backups and restores to Phase 14.

- **Implications:**
  - Offline client scenes keep their in-memory roster and inventory. Their developer keys can't reach the backend.
  - This supersedes the audit's preliminary schema (audit §9.5) and, for online play, the "Phase 14 serializes locally" notes in 036 and 043.

## 049. Phase 13 attempts: lifecycle, energy, insurance and loss rules (2026-10-14)

- **Decided:**

  **Attempts and participants:**
  - An attempt has one or more **participants**. Each has its own loadout snapshot, energy hold, insurance snapshot, tickets, start commit, outcome, settlement and receipt.
  - **One active attempt per account**, enforced by a PostgreSQL partial unique index. Phase 13 admits one participant per attempt.

  **Admission and start:**
  - **Idempotency:** a deploy is idempotent on `(account, key)` and the request body's hash. The same key with a **different body** returns `409 IdempotencyKeyReused`, creating and charging nothing.
  - **Health check:** before any energy is touched, admission refuses with 503 when no healthy mission capacity exists.
  - **Start, per participant:** the server is ready, the participant's identity-bound ticket has been redeemed, and the backend has committed the start. Launching a process charges nothing, and until the commit is acknowledged the participant's units are held.

  **Mission processes:**
  - Processes are on-demand, single-attempt systemd units under an unprivileged user, launched by a .NET host agent.
  - Polkit lets the agent `start` and `stop` only `blackglass-mission@<canonical UUID>.service`, whose template and binary are root-owned and fixed. The agent accepts attempt ids only from the backend's claim and validates them.
  - Prewarming is deferred until launch latency is measured.

  **Energy (all values provisional):**
  - Deducted at reservation, charged at the participant's start commit, released if the participant never starts, and refunded (provisionally 100 %) only on an infrastructure void.
  - Refunds may exceed the cap.
  - Regeneration uses the database clock and is materialized lazily. Every materialization writes a `regen` ledger entry keyed by the anchor it consumed, in the same transaction, so an interval is never credited twice.
  - A periodic reconciliation compares each wallet with its ledger. A mismatch raises an anomaly; nothing is corrected automatically.

  **Insurance (all values provisional):**
  - Policies are immutable versions.
  - **Coverage:** one coverage per account, in UTC. **Activating the same benefit again extends the remaining coverage** (`greatest(expires_at, now) + duration`). Benefits never multiply. Activating a different policy while one is active is refused for now; cross-tier behaviour is deferred.
  - **Snapshot:** at admission, the active coverage's terms are copied into the attempt, per participant. The snapshot outlives the coverage, and later extensions or policy versions don't touch it.
  - **Coverage:** defeat, abandoned, disconnected and expired. **Benefits:** consumable refunds to the stash, retained loot and progress XP.
  - **Retention:** chosen by the backend from loot still held at the outcome freeze, ranked by policy category priority with ties broken by a per-attempt secret salt. It is computed once and stored, so it is never rerolled.

  **Loss rules for backend-settled missions:**

  | Outcome | Rule |
  |---|---|
  | Success | Owned gear kept, with mission swaps applied. Used consumables expended. Loot granted. `MissionCompletionXp` goes to every operative, dead ones included, and dead operatives keep their bags and equipment. |
  | Defeat, abandoned, disconnected, expired | Owned gear kept, with swaps discarded. Used owned consumables expended. Loot discarded apart from insured retention. No XP apart from insured progress XP. |
  | Infrastructure void | The pre-mission state is untouched and the energy refunded. |

  The decision 043 rule that gives back used consumables on failure is **superseded for backend-settled missions only**. Offline scenes keep it.

  **Provenance:** looted consumables are used before owned ones, oldest pickup first. A server-side ledger of lots follows every stack merge, transfer and use, and the backend checks conservation.

  **Picks:** advancement picks persist through the API with the existing rule. The progression system is not expanded.

  **Reconnect (server side):**
  - a solo attempt pauses when its participant disconnects;
  - a ticket re-admits the same account, with its state keyed by account;
  - **process-issued resume tokens** re-admit without the backend;
  - the reconnect window and hard cap count **only time when the backend is reachable**, so an outage never turns a recoverable disconnect into `disconnected`;
  - no energy is charged again.

  **Abilities** run authoritatively on the Linux mission server through the existing `UnitAbilities` and `AbilityRules`, wired by the existing spawner path.

  **Abuse handling:** there is no automatic suspension of infrastructure refunds. Instead:
  - anomaly monitoring;
  - operator investigation;
  - explicit account suspension or host or build disabling;
  - draining failing hosts.

- **Why:**
  - These are the owner's rulings of 2026-10-14 (C-1 to C-4, D-3, D-5, D-7, D-8, D-15, D-24, Q-5, A-items) and the final corrections (insurance extension, regeneration ledger, idempotency body, reconnect during outages).
  - A per-participant model keeps co-op from forcing a later redesign.
  - Per-participant start commits make it impossible to charge without starting, or to start without charging.

- **Rejected:**
  - Charging at process launch.
  - "Newest grant wins" insurance (A-19), and adding coverages together.
  - First-picked retention, which the player could steer.
  - Count-replay provenance, which can't follow merges.
  - Automatic refund suspension.
  - Per-attempt rather than per-participant settlement keys.
  - A reconnect path that depends only on the backend.

- **Implications:**
  - Enabling co-op still needs a knowledge-sharing rule (047), a shared pause and an abandon rule.
  - Every balancing value is listed in the spec's §19 register.

## 050. Phase 13 settlement: manifests, delivery, exactly-once, quarantine, void and the trust boundary (2026-10-14)

- **Decided:**

  **Manifest:** the mission process proposes one **facts-only manifest per participant**: outcome, objectives, consumption with its provenance, held lots, collections and equipment. It carries no XP, rewards, energy or insurance. A canonical JSON SHA-256 identifies it.

  **Delivery:**
  - The process writes the manifest to a durable **per-attempt outbox directory** (fsync, then rename) before telling players the result is recorded.
  - **Isolation:** each unit's mount namespace shows only its own directory (`TemporaryFileSystem` plus `BindPaths`, created by a root step in the fixed template). One mission process can't read, edit or delete another attempt's result. The agent has read-only group access.
  - **Relay:** the agent relays each file until it gets a definitive answer and records the answer in its own state. The outbox survives the process exiting, an agent restart and a reboot.
  - **Pending:** players see the result as pending until the backend's receipt exists.

  **Exactly-once:**
  - one transaction per participant settlement, with the lock order attempt → participant → inventory → wallet → coverage;
  - primary key `(attempt_id, account_id)`;
  - a duplicate with the same hash returns the stored receipt; a different hash gets `409`;
  - backed by deterministic per-unit minted ids, unique ledger keys and no in-memory deduplication.

  Stack consumption never writes zero: a fully consumed row is deleted, and a partially consumed one updated with `quantity > q`, so the `quantity > 0` constraint stays.

  **Validation V1–V14:**
  - the host, the attempt binding and the process session;
  - the build and catalogue;
  - the layout hash and the objectives;
  - the snapshot operatives;
  - consumption against the snapshot;
  - collections against the registered, table-bounded loot pool, across participants;
  - **conservation**;
  - bag capacity and equipment;
  - plausibility and integrity.

  **Quarantine:**
  - An invalid manifest **quarantines** the participant and is never settled as given.
  - At **24 h** (provisional) the worker escalates to the operator and repeats the alert daily. **No timer voids.**
  - Ways out:
    - a valid manifest arrives;
    - the operator re-validates the stored original manifest after a backend fix, and it settles normally;
    - an **operator-authorized protective void**, accepted only with a recorded re-validation and an outbox check.

  **Voids:**
  - **Backend unavailability never voids**: a running attempt that loses contact becomes `unresponsive` and waits.
  - An **infrastructure void** happens only:
    - on the agent's positive report, after its outbox scan, that the process ended without a result;
    - on an operator's host-loss declaration for a host that is permanently lost (never because a heartbeat expired);
    - on the operator-authorized quarantine void.
  - A void is protective: nothing is expended, no loot or XP is given, insurance goes unused, and the energy is refunded.
  - **A late result after a void gets `409`**: the void's receipt is preserved and an anomaly is recorded. Reconciliation is Phase 14.
  - Every operator action is recorded in `operator_actions`.

  **Residual trust:** a mission host is partly trusted. Validation bounds what it can claim but doesn't make a compromised host trustworthy.
  - **What it can still do, within the bounds:** grant its own participants success and full-pool loot, deny them rewards, or cause voids.
  - **What it can't do:** touch other accounts or other attempts' outbox files, exceed the pool or the snapshot, alter formulas or the retention ranking, or settle twice.
  - **Mitigations:** hardening, attempt-scoped credentials, outbox isolation, the polkit scope, the build allowlist, anomaly metrics with drain and disable, and audit records. Replay verification and attestation are Phase 14.

- **Why:**
  - The owner's rulings D-10, D-11, Q-2 (revised), Q-3 and Q-4, and corrections 2, 3, 5 and 7.
  - A completed result must not be lost or voided because its first delivery failed.
  - Authentication proves who sent a result, not that the result is right.
  - An account must never sit locked without an operator knowing.

- **Rejected:**
  - Voiding on lease expiry.
  - An unconditional void when quarantine expires.
  - Redelivery by the next mission process.
  - A single shared outbox directory.
  - An HMAC-signed manifest as the main control.
  - Trusting authenticated results without conservation.

- **Implications:**
  - Phase 14 adds checkpoints, corrected-manifest resubmission, reconciliation of late results, outbox cleanup, replay verification and per-host reward limits.

---

## Draft B: `CLAUDE.md` (ASCII, replaces two sections' paragraphs)

### "Current Development Stage" (paragraphs 2 and 3 and the out-of-scope list)

> The owner is building the online foundation with an authoritative dedicated server; see `Docs/OnlineReadinessAudit.md`. For Phase 13 the backend stack is decided (decision 048): ASP.NET Core on .NET 10, PostgreSQL, Npgsql and Dapper, Docker Compose on `bg-dev-services01`. FishNet and the Unity Linux dedicated server are in use. HAProxy, a private VLAN, Docker for mission servers and orchestration remain **provisional, not decided**.
>
> The step currently authorized is **Phase 13: authenticated between-mission flow and trusted settlement**, following `Docs/superpowers/specs/2026-10-14-phase-13-backend-design.md` and the staged plan `Docs/superpowers/plans/2026-10-14-phase-13-backend.md`, one stage at a time, each stage started only when the owner says so. Decisions 048-050 record the design. Phase 13 authorizes:
> - the account and mission backend in `Backend/`, loopback-only, reached through SSH tunnels;
> - the mission host agent and on-demand systemd mission units on `bg-dev-mission01`, under unprivileged users;
> - server-side reconnect (solo pause, resume tokens, re-admission by ticket) and backend settlement;
> - authoritative abilities, items and loot on the Linux mission server;
> - one separate minimal online client entry scene (login, loadout through the API, deploy, ticket join, play with the existing network client view and controls, pending result, then the receipt).
>
> Each of these stays out of scope until the owner authorizes it:
> - changes to the offline client scenes (`ProceduralMission`, `Prototype`) or their input, HUD and gameplay;
> - production authentication providers, purchases, stores and payment processors;
> - public exposure, HAProxy, a private VLAN, Docker for mission servers, prewarming and orchestration frameworks;
> - co-op attempts with more than one participant;
> - layout streaming and geometry secrecy;
> - final production content (graphics, animation, audio, story).

### "Current Priority" (replace)

> Phase 13, stage by stage (plan revision 2.1). Stage 0 (decision records and this authorization) is done; Stage 1 (backend foundation) starts only when the owner says so.
>
> - Each stage records concrete evidence (test counts, transcripts, diff stat) and changes only the paths it lists.
> - Keep visibility policy and the backend core free of networking-library types.
> - Keep the offline client scenes and their gameplay unchanged.
> - Every energy, insurance, XP, retention and timer value is provisional configuration.
>
> Next step, authorized separately: Phase 14 (server persistence hardening and recovery).

---

## Draft C: `Docs/OnlineReadinessAudit.md`

In the §9.1 table, the "Phase 13" row's status changes from "Not started" to:

> Design approved and decisions 048–050 recorded 2026-10-14 (§9.5); implementation (Stage 1 onward) not started

§9.5 is appended:

> ### 9.5 Phase 13 design: the preliminary schema replaced (2026-10-14)
>
> The Phase 13A design (`Docs/superpowers/specs/2026-10-14-phase-13-backend-design.md`, revision 2.1; decisions 048–050) replaces the schema sketched in §7 and §8. Sections 1–8 stay as the original record.
>
> | Audit sketch | Phase 13 schema | Why |
> |---|---|---|
> | `attempts(attempt_id, account_id, server_id, state)` | `mission_attempts` + `attempt_participants` (one row per account) | Co-op; energy, insurance and settlement per participant |
> | `server_id` | `host_id` + `process_session_id` + an attempt-scoped process credential, and `allowed_builds` | Tells host, process and restart apart; bounds a compromised process |
> | `settlements(attempt_id PK, result_hash, payload)` | `participant_settlements((attempt_id, account_id) PK)` | Exactly once per participant |
> | HMAC-signed manifest | Credential and session binding, then validation V1–V14 with conservation | Signing doesn't bound content |
> | `operatives`, `item_instances`, `loadout_versions` | `operatives` + `operative_choices`, `item_instances` (per-unit `origin_ref`) + `item_stacks`, `inventories.version` + snapshots | Fungible consumables, persisted picks, account-level versioning, collision-free minting |
> | §7 "disposable settlement endpoint" | A per-attempt, isolated outbox relayed by a host agent; quarantine with operator escalation; a void only on positive evidence or operator authorization | A completed result is never lost or voided because its first delivery failed |
>
> Added beyond the audit:
> - energy wallets, holds and a ledger including regeneration;
> - insurance policies, one coverage per account with an activation history (extension, never multiplication), and attempt snapshots;
> - join tickets with an idempotent deploy keyed by request hash;
> - the loot pool and collections;
> - `rejected_results`;
> - `anomaly_events`;
> - `operator_actions`;
> - the `one_active_participation` index.
>
> The §6 product decisions are settled for Phase 13 in decisions 049 and 050:
> - the reconnect window and pause for solo;
> - an infrastructure void instead of an "Abandoned" default on interruption;
> - explicit consumption and retention rules;
> - a new server-chosen seed for every attempt;
> - XP for dead operatives kept.
>
> Phase 13 acceptance criteria B1–B23 (spec §18.2) extend A1–A10. Their results will be recorded here as the stages run.
