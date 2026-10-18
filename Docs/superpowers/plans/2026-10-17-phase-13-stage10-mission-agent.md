# Phase 13 Stage 10: the Linux Mission Host Agent (detailed plan, revision 1)

> **For agentic workers:** REQUIRED SUB-SKILL: use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task by task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Status (2026-10-17):** revision 2. Implementation is authorized under the owner's rulings below, with an infrastructure checkpoint: nothing changes `bg-dev-mission01`, `bg-dev-services01`, SSH, a firewall, Docker networking or the live accounts stack until the owner separately authorizes it. Revision 1 (the recommendations) follows the rulings unchanged.

**Goal:** an unprivileged agent on `bg-dev-mission01` that:
- claims attempts and launches each one as a hardened systemd unit only after the backend has acknowledged `launching`;
- reports a complete unit inventory;
- relays every durable result from the per-attempt outbox;
- reports a process exit only after relaying that attempt's results.

It does all of this crash-safely across agent restarts, VM reboots and long backend outages, and no outage ever causes a void.

**Architecture:**
- A .NET 10 console worker, `Backend/src/MissionHost.Agent/`. It talks to the backend over HTTP only and references no backend assembly, so the backend core stays untouched.
- All side effects sit behind small seams, each with a real Linux implementation and a fake:
  - the backend client;
  - the durable state store;
  - the outbox reader;
  - systemd: the launcher, the unit inventory and the unit status;
  - the clock.
- The agent's own state lives in `/var/lib/blackglass/agent` (owned by `blackglass-agent`, 0700). Every write goes to a temporary file, is `fsync`ed, renamed, and then the directory is `fsync`ed.
- Systemd is driven by the `systemctl` CLI with fixed argument vectors (never a shell), under the Q-5 polkit rule.

**Tech stack:**
- .NET 10 (`Microsoft.Extensions.Hosting` already ships with the SDK), xUnit, systemd 255 (Ubuntu 24.04), polkit.
- No new NuGet package.

**Spec:** `Docs/superpowers/specs/2026-10-14-phase-13-backend-design.md`:
- §4.3–4.4 (credentials and secrets);
- §7.4 (units and polkit);
- §9.2 (outbox isolation and relay);
- §11 (failure matrix);
- §16.2 (agent routes);
- §0.8 R1–R8 (Stage 8 rulings);
- §0.9 (Stage 9 rulings).

The Stage 8 and Stage 9 plans' "As built" sections and §H of the Stage 9 plan (the publish/visibility contract) hold the binding obligations listed below.

## Obligations this stage inherits

From Stage 8 (R1, R2 and R4, Checkpoint 1 M5, final M3, and R8):
- Persist the claim's `Idempotency-Key` before sending the claim. Persist the claim response durably (`fsync`) before acting on it.
- Get `200` from `launching` with that response's `credentialGeneration` **before** `systemctl start`. Never launch with a generation the backend hasn't accepted.
- On `CredentialSuperseded`, retry the claim with the same key. After a restart, resume from the persisted state:

  | Persisted state | Next step |
  |---|---|
  | key, no response | claim retry |
  | response, no `launching` acknowledgement | `launching` retry |
  | acknowledged, unit not started | start |
- Send `inventory.complete: true` only after a `systemctl list-units 'blackglass-mission@*' --all` that exited 0 and parsed entirely. Either list every unit whose `launching` was acknowledged but which hasn't been seen to exit, or serialize enumeration with launches.
- Stop the units named in the heartbeat's `stop` list. Report `exited` only once the unit is gone.
- Provide a trusted emergency stop for mission units that works even when the agent can no longer authenticate (R8).

From Stage 9 (§C, §H and S9-B7):
- Relay `.ready` files while the process is alive. Never touch `.written` files while it is alive. After a verified exit, publish and relay the `.written` files (how depends on A10-D1). Never relay `.tmp` files or count them as present.
- Relay every published file before reporting `exited`. Report `present: false` only when the scan found neither a `.written` nor a `.ready` file for that participant.
- Send `outbox.complete: true` only after a full, successful read of the attempt's directory. Otherwise send `false`, and report a later successful rescan as the supplementary observation.
- Handle each answer exactly as §C says (table in Task 7), recording relay state in the agent's own directory. Never delete a relay file before Phase 14's cleanup. Keep `AttemptRevoked` files for the operator.

## Revision 2: the owner's rulings (2026-10-17; implementation authorized, infrastructure checkpoint pending)

The owner authorized test-first implementation on `phase13-stage10` with these rulings. They override the recommendations in the table below wherever the two differ.

| # | Ruling | What it changes in this plan |
|---|---|---|
| **A10-D1** | **Revised.** The agent's outbox access stays read-only. The mission process owns creation and publication: `.manifest.tmp` is incomplete and never relayed; `.manifest.written` is complete, `fsync`ed and durable but unpublished; `.manifest.ready` is published and may be relayed at once, even while the process runs. The agent never renames, replaces, truncates, removes or modifies a result. **Crash recovery:** once the agent has independently verified that the unit exited, it may relay a complete `.manifest.written` directly, without renaming it. Before treating any file as a participant's result, it validates the file name, the identity (attempt and account) and the manifest structure. Delivery state lives in the agent's own directory, never in the outbox. A failed or incomplete scan is never proof of absence, and a `.tmp` is never a result. | Task 7 relays a verified-exit `.written` in place and validates name, identity and structure first. Stage 9's §H gains the read-only recovery exception. Tests cover a crash at every publication boundary. |
| **A10-D2** | **Approved.** The authenticated host claim response includes the admitted participants' account ids as canonical Base58 strings. The list is read from the authoritative attempt records, scoped to the attempt that authenticated host claimed, immutable for the attempt, persisted durably with the claim response and restored at restart. It carries no profile information, credentials or inventory. | Task 3 adds `participants` to the claim response (store, route, contract, tests). |
| **A10-D3** | **The architecture is approved; installation is not yet.** A dedicated systemd-managed SSH tunnel: mission VM `127.0.0.1:5081` → SSH → services VM `127.0.0.1:5080`. Requirements: a dedicated SSH identity separate from administrator keys; a dedicated restricted remote account (or an equally isolated policy); only the required local TCP forward, its destination restricted to `127.0.0.1:5080`; the local listener bound only to `127.0.0.1`; strict host-key verification against a verified, pinned key, with no insecure fallback; no shell, no arbitrary commands, no agent or X11 forwarding; automatic restart with bounded backoff and visible failures. A tunnel failure never stops running missions or loses results. No public API port. | The agent's `apiBaseUrl` is `http://127.0.0.1:5081`. Task 10 writes the tunnel unit, its `ssh` options and the services-VM account policy as files only. |
| **A10-D4** | **Approved.** `systemctl` at a fixed executable path with structured argument lists, never a shell. Every unit name is derived only from a validated canonical UUID. Only start, stop and status operations against the fixed `blackglass-mission@` template. Polkit stays narrow and verifiable. | Task 5 adds tests for malicious unit names, unexpected arguments, refused operations, duplicate launches and process failures. |
| **A10-D5** | **Approved.** A root-only local emergency command stops only Blackglass mission units without the backend. It supports a dry run that lists what it would stop, is explicit, audited (a local log) and idempotent, preserves the outbox and relay evidence, and never modifies inventory, settlement, energy or insurance. The agent never stops every mission because the backend returns 401. A 401 stops new claims and launches, triggers a secure secret reload and raises an operational alert. Normal authenticated stop directives keep working. | Task 11. |
| **A10-D6** | **Approved.** A self-contained `linux-x64` executable; no machine-wide .NET runtime. Document the runtime identifier, the release hash, file ownership, the service identity and rollback. Executables and service files are root-owned and not writable by the agent or the mission process. Not installed until the infrastructure checkpoint. | Task 10 (publish script, release manifest, documentation). |
| **A10-D7** | **Approved.** A root-owned stub mission executable tests the real template, polkit, process credentials and filesystem isolation before Stage 12. The tests never overwrite or replace the Unity server installation, use isolated test instances, and on failure clean up their units and temporary files without deleting result evidence. Prepared only; no root installation without separate approval. | Task 10 writes the stub and `verify.sh`. |
| **A10-D8** | **Approved with safeguards.** The agent re-reads its host secret on `SIGHUP` and after any 401, from its protected file, validating the expected format. Rotation replaces the file atomically (documented operator procedure). If the reloaded secret is unchanged and still rejected: stop new claims and launches, back off without a rapid retry loop, raise an operational alert, and continue local supervision and durable file preservation. A disable revokes the old credential for good, and reloading it never restores authorization; recovery needs an explicit rotation and re-enable. The plaintext secret is never logged or exposed. | Tasks 2, 6 and 11. |

**Additional requirements (the owner's):**
- **Claim durability:**
  - generate and persist the idempotency key before transmitting it;
  - persist the complete accepted response (credential, generation, port, participants) before launching;
  - get `launching` acknowledged before `systemctl start`;
  - survive lost responses, superseded generations, crashes and restarts;
  - never start a process with a superseded credential;
  - protect stored process credentials and remove them only when recovery no longer needs them.
- **Exit evidence:**
  - report only after verifying the unit's terminal state and examining the durable outbox;
  - list every required participant;
  - set `outbox.complete` only after a successful authoritative enumeration;
  - relay available complete results before positive evidence of absence;
  - support Stage 9's supplementary observation.
- **Backend failures:**
  - distinguish a revoked credential from transport or database unavailability;
  - a 503, a timeout, a lost tunnel or a failed scan is never evidence of mission failure;
  - an agent restart, a process exit and a backend outage stay independently recoverable;
  - the outbox stays intact until a separately authorized cleanup.
- **Infrastructure checkpoint (replaces Task 12's execution).** Stage 10 produces the configuration files, installation scripts, unit templates, polkit rules and validation commands. Before any change to `bg-dev-mission01` or the services VM's SSH configuration it **stops** and presents:
  1. every file to install or modify;
  2. every user, group and directory to create;
  3. the tunnel and credential setup;
  4. the exact systemd and polkit permissions;
  5. the stub-test procedure;
  6. the expected verification results;
  7. rollback.

  Without that authorization, Stage 10 is reported **code-complete, infrastructure validation pending**.
- An independent security review covers credential handling, process execution, file permissions and result preservation.

## Owner decisions (A10-D1 to A10-D8), revision 1 recommendations

| # | Decision | Recommendation | Alternatives and costs |
|---|---|---|---|
| **A10-D1** | **Publishing `.written` files after an exit.** §H says the agent renames `.written` to `.ready`, but §9.2 gives the agent group *read* access only (directories `2750`, files `0640`): it can't rename. | **The agent relays a `.written` file directly once the unit's exit is verified, without renaming it.** Its relay state records "published by exit". The outbox stays strictly read-only to the agent, and the process never sees the difference, because it has exited. §H is amended to say so. | (b) A root `ExecStopPost=+` step in the fixed template renames `.written` to `.ready` after the main process exits. That adds root code and doesn't run if systemd itself is stopped mid-unit. (c) Make the directory group-writable (`2770`). Rejected: the agent could then alter or delete results. |
| **A10-D2** | **Where the participant roster comes from.** A complete exit report must list every started participant (`IncompleteResults` otherwise), but the agent only learns account ids from outbox file names. | **The claim response gains `participants: [accountBase58]`** (the attempt's admitted participants, known at claim). The agent persists it with the response. This is a small additive backend change in Stage 10 (route, store and test). | (b) A new host route, `GET /agent/v1/attempts/{id}/participants`, adds a round trip and a failure mode. (c) Never send `complete: true`, which means no exit-evidence void would ever be possible. |
| **A10-D3** | **How the mission VM reaches the loopback-only API.** Phase 13 keeps the API loopback-only behind SSH tunnels. The agent and the mission processes on `bg-dev-mission01` need `127.0.0.1:5080`. | **A dedicated `blackglass-tunnel.service`** on mission01:<br>• runs as user `blackglass-tunnel`;<br>• runs `ssh -N -L 127.0.0.1:5080:127.0.0.1:5080` with `Restart=always`;<br>• its key on services01 is restricted to `restrict,permitopen="127.0.0.1:5080",command="/bin/false"`.<br>The agent and the processes use `http://127.0.0.1:5080`. This changes services01's `authorized_keys` and mission01 (owner present, Task 12). | (b) autossh adds a package and changes nothing material. (c) A private VLAN or WireGuard is out of Phase 13's scope. |
| **A10-D4** | **The systemd interface.** | **The `systemctl` CLI** with fixed argument vectors:<br>• `start`/`stop blackglass-mission@<uuid>.service`;<br>• `list-units 'blackglass-mission@*' --all` with JSON output where the VM's systemd 255 provides it (Task 5 records real output from the VM; otherwise `--plain --no-legend --no-pager`, parsed strictly);<br>• `show -p ActiveState,SubState,Result,ExecMainCode,ExecMainStatus,InvocationID`.<br>No new package. | A D-Bus library (Tmds.DBus) gives typed signals, but it is a new dependency. Polling every 1 s is enough at this scale. |
| **A10-D5** | **Emergency stop.** | A root-only local command, `blackglass-agent emergency-stop`, run with `sudo` by the operator on the VM. It stops every `blackglass-mission@*` unit through systemd as root, needs no backend and no host secret, and logs what it stopped. **The agent does not stop running units on its own after a 401.** Processes stop themselves on a revoked credential (Stage 12), and ended attempts are stopped through the heartbeat's `stop` list. | (b) The agent stops all units after N consecutive heartbeat 401s. A rotated-but-not-installed secret would then kill legitimate missions. |
| **A10-D6** | **Runtime on the VM.** | A **self-contained, single-file `linux-x64` publish**, root-owned under `/opt/blackglass/agent/releases/<revision>/`, with a `current` symlink. No .NET runtime is installed on the VM. | Installing the .NET runtime from Microsoft's apt feed adds a system package and an external source. |
| **A10-D7** | **What the Stage 10 VM tests launch.** The server can't register before Stage 12. | The VM step uses the **real template** with a **root-owned stub mission executable**. A test `current` symlink under a separate `/opt/blackglass/mission-stub/` is selected by a template variable file (`EnvironmentFile=` stays root-owned). The stub writes `.tmp`, `.written` and `.ready` files and exits with a chosen status, so every §H crash point is exercised. The owner's `/opt/blackglass/server/` is untouched. | Point `current` at the owner's server build and test only launch and exit. That leaves the outbox path unexercised until Stage 12. |
| **A10-D8** | **Host secret rotation.** | The agent re-reads `/etc/blackglass/agent/host-secret` (0600, `blackglass-agent`) on `SIGHUP` and after any 401. The operator installs the new secret as root and sends `SIGHUP` (or restarts the agent). | Restart-only rotation leaves the agent idle until the restart. |

Provisional configuration (spec §19), each with a default in the agent's config file:
- heartbeat 10 s;
- claim long poll ≤ 25 s;
- relay backoff 1–60 s with full jitter;
- outbox scan 1 s;
- outbox disk alert at 80% of the filesystem or 1 GiB used;
- the agent unit's `MemoryMax=256M` and `TasksMax=64`;
- each mission unit's `MemoryMax=2G`, `CPUQuota=200%` and `TasksMax=256`;
- the largest file the agent sends: 4 MiB (anything larger is a local anomaly, never sent).

## Global constraints

- Touch only:
  - `Backend/src/MissionHost.Agent/`, `Backend/tests/MissionHost.Agent.Tests/`, `Backend/deploy/mission/`;
  - the additive claim-response change (if A10-D2 is approved): `Backend/src/Accounts.Persistence/Hosts/HostStore.cs`, `Backend/src/Accounts.Api/Hosts/*`, their tests and the README;
  - `Docs/` and `CLAUDE.md`.
- No migration (schema stays 9), unless A10-D2's alternative (b) needs none either.
- Keep `0000`–`0009` byte-identical.
- The agent is never root. Only the template's fixed `ExecStartPre=+` lines run as root, plus the emergency-stop command run by the operator with `sudo`.
- The agent validates every attempt id as a canonical lowercase UUID (`^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$`, after parsing) before building a unit name or a path, and refuses anything else.
- Never log, persist outside the state directory, or print:
  - the host secret, a process credential or an `Idempotency-Key`'s secret part;
  - manifest contents.

  Logs carry at most an 8-hex prefix of a hash. A static check enforces this, as Stage 8's M8 did.
- No result file is ever modified or deleted by the agent.
- Every timer, size and threshold is provisional configuration.
- Tests run on the VM-local runner (Linux containers), so filesystem semantics (`fsync`, rename, modes) are real. The root install steps run only in Task 12, with the owner.
- Never commit secrets. Stage by explicit paths only, never `git commit -a`.
- Preserve the owner's two Unity settings files and `.claude/`.
- No merge, push or deployment without the owner.

## Review focus

1. **A crash between any two durable steps of claim → persist → `launching` → credential file → `start` → started.** The expected behaviour: never a second unit for one attempt, never a launch with an unacknowledged generation, and no orphaned credential file after start. Task 3 injects a crash at every step.
2. **A `.written` file next to a live process, a `.tmp` file left by a crash, and a file that grows while it is being read.** The expected behaviour: only `.ready` files are relayed while the process is alive; `.written` files are relayed only after a verified exit; `.tmp` files are never relayed; a file is read once, by size and then content, and retried if it changes. Task 7.
3. **Enumeration racing a launch.** The expected behaviour: a `complete: true` inventory never omits a unit whose launch was acknowledged, so the backend never frees a live slot. Tasks 5 and 6.
4. **A long backend outage during a mission's end.** The expected behaviour: relays and exit reports wait and retry forever, nothing reports absence without a complete scan, and the backend applies no void. Tasks 7–9 and the integration test with the API stopped and started.
5. **A rotated or revoked host secret.** The expected behaviour: claims stop, files are kept, no unit is stopped on the agent's own initiative, the secret is re-read on 401 and `SIGHUP`, and the emergency stop works without the backend. Tasks 6, 9 and 11.

---

## Task 0: record the owner's rulings

- [ ] Record A10-D1 to A10-D8 as the owner rules, amend §H if A10-D1 is approved, and update the spec (§0.10), the staged plan and `CLAUDE.md`. Commit docs only.

## Task 1: skeleton, configuration, identifiers and durable files

**Files:**
- Create: `Backend/src/MissionHost.Agent/` (`Program.cs`, `AgentConfig.cs`, `AttemptIds.cs`, `AtomicFile.cs`, `StateStore.cs`);
- Create: `Backend/tests/MissionHost.Agent.Tests/` and add both projects to `Backend.sln`.

**Interfaces (produced):**
- `AgentConfig.Load(path)` reads JSON with `hostId`, `apiBaseUrl` (loopback only), `stateDir`, `outboxDir`, `relayDir`, `launchDir`, `serverBuildInfo` (the path of the server's `build-info.txt`, which supplies `revision`; `catalogueVersion` is read from the same file, or from the config until Stage 12's build writes it) and the provisional values. An invalid config is refused with exit 4.
- `AttemptIds.TryCanonical(string, out Guid, out string canonical)` and `UnitNames.For(string canonical)`, giving `blackglass-mission@<uuid>.service`.
- `AtomicFile.WriteAsync(path, bytes)` writes the temporary file, `fsync`s it, renames it and `fsync`s the directory. `AtomicFile.ReadAsync` ignores `*.tmp`.
- `StateStore`: claims, launches and relay records, one file each, keyed by canonical attempt id and Base58 account id.

**Steps (test-first):**
- [ ] Tests for `AttemptIds`. Upper-case, braces, no hyphens, spaces, `..`, a trailing newline, a 37-character string, the nil UUID, and a UUIDv4 rendered upper-case are all refused or canonicalized exactly once. The unit name is built only from the canonical form.
- [ ] Tests for `AtomicFile`:
  - after a write, only the final file exists;
  - a leftover `x.tmp` from a simulated crash is ignored and overwritten by the next write;
  - a concurrent reader sees the old bytes or the new ones, never partial ones;
  - files are created 0600 under a 0700 directory.
- [ ] Tests for `AgentConfig`: missing or invalid values are refused, a non-loopback `apiBaseUrl` is refused, and no secret path's contents are logged.
- [ ] Implement until green. Run on the VM runner: `vm-test.sh --filter MissionHost`.

## Task 2: the backend client and answer classification

**Files:**
- Create: `BackendClient.cs`, `Answers.cs`, `HostSecret.cs`.

**Interfaces (produced):**
- `BackendClient` with:
  - `ClaimAsync(key)` returning `Claimed`, `NoWork`, `Unavailable` or `Retry`;
  - `LaunchingAsync(attempt, generation)` returning `Acknowledged`, `Superseded`, `InvalidState`, `NotFound` or `Retry`;
  - `HeartbeatAsync(...)` returning `{drained, stop}` or `Retry`;
  - `ExitedAsync(attempt, report)` returning `Applied`, `Conflicting`, `IncompleteResults`, `UnknownParticipant` or `Retry`;
  - `RelayAsync(bytes)` returning a `RelayAnswer`.
- `HostSecret` reads the 0600 file, keeps the value only in memory, re-reads on demand, and never logs or prints it.

**Steps:**
- [ ] Tests with a fake HTTP handler for the full §C matrix:

  | Answer | Relay state |
  |---|---|
  | 200 `settled` or `duplicate` | `done` |
  | 202 `quarantined` or `held` | `under_review` (never resent) |
  | 409 `Conflict` or `AlreadySettledAsVoid` | `conflict` (kept) |
  | 409 `AttemptRevoked` | `revoked` (kept) |
  | 409 `WrongAttempt` or `InvalidState` | `refused` (kept) |
  | 409 `SubmissionLimit` | `limited` (kept; an operator import goes past the cap) |
  | 422 | `rejected` (never resent) |
  | 413 or 415 | `transport_refused` (a local anomaly; never resent automatically) |

  401, 429, 500, 503, timeouts, connection refusal and a closed tunnel are **retry**: backoff 1–60 s with full jitter, indefinitely, and a 401 re-reads the secret.
- [ ] Integration tests against a real in-process API and test database (the existing `ApiServer` and `TestCluster` harness): claim, launching, heartbeat, exited and results map exactly as above.
- [ ] Implement until green.

## Task 3: durable claim and launch

**Files:**
- Create: `ClaimLoop.cs`, `LaunchStep.cs`;
- Modify (A10-D2): `HostStore` claim result and claim route, adding `participants`, plus their tests and the README.

**State machine** (persisted per claim key, in `stateDir/claims/<key>.json`):

```
keyed --claim 200--> claimed (response + participants fsynced)
claimed --launching 200 (gen)--> acknowledged
claimed --CredentialSuperseded--> keyed (claim retry, same key: a recovery, new generation)
acknowledged --credential written 0400--> credential_ready
credential_ready --systemctl start ok--> started (credential file deleted)
started --unit seen gone--> exiting (Task 8)
```

**Steps:**
- [ ] Tests with a fake backend and a fake launcher. A crash injected after every transition (a hook throws, and a new agent instance resumes from disk) gives:
  - exactly one `start` per attempt;
  - never a start before an acknowledged `launching`;
  - a recovery that reuses the key and gets a new generation;
  - an `InvalidState` after a launch makes the agent supervise the existing unit instead of starting another;
  - a malformed attempt id in a claim response is refused, logged as an anomaly with the id never used in a path, and not launched.
- [ ] A test that a drained host or a failing heartbeat lease sends no claim.
- [ ] Integration tests against the real API: Stage 8's recovery, superseded and `InvalidState` paths, end to end with the fake launcher.
- [ ] A backend test (A10-D2): the claim response lists exactly the attempt's admitted participants as Base58, on first claim and on a recovery.
- [ ] Implement until green.

## Task 4: the process credential handoff

**Files:**
- Create: `CredentialHandoff.cs`.

**Steps:**
- [ ] Tests:
  - the credential is written to `launchDir/<uuid>/process` as 0400 under a 0700 directory owned by the agent, through `AtomicFile`;
  - it is deleted once `start` returns and the unit's `ActiveState` is `active`, `activating` or already ended;
  - after a crash it is deleted at startup for every attempt past `started`, and rewritten from the persisted response for an attempt at `credential_ready`;
  - it never appears in logs, state files other than the claim record, or exception messages.
- [ ] The static check: no logging call in `MissionHost.Agent` takes a credential, secret or manifest value (as Stage 8's M8).
- [ ] Implement until green.

## Task 5: systemd launcher, inventory and status

**Files:**
- Create: `Systemd/SystemctlRunner.cs`, `SystemdLauncher.cs`, `UnitInventory.cs`, `UnitStatus.cs`, and their fakes in the test project.

**Steps:**
- [ ] Tests of the argument vectors. They are built only from canonical ids and never through a shell, and a unit name outside the pattern is refused before any process is started.
- [ ] Parse tests with recorded `systemctl list-units` samples (JSON, or the strict plain form), including a partial document, garbage, an empty list, an unknown unit, a non-zero exit and a timeout. Only a zero exit with the whole document parsed gives `complete: true`.
- [ ] Mapping tests from `ExecMainCode`/`ExecMainStatus` to the exit report: `CLD_EXITED` gives `exitCode`; `CLD_KILLED` and `CLD_DUMPED` give `signal` (the name, such as `SIGKILL`).
- [ ] Tests of enumeration serialized with launches. A launch holds the inventory lock from its acknowledged `launching` until the unit is listed or `start` fails, and every acknowledged-but-unlisted attempt is added to the inventory, so a `complete` inventory never omits a live unit.
- [ ] Implement until green.

## Task 6: the heartbeat

**Files:**
- Create: `HeartbeatLoop.cs`.

**Steps:**
- [ ] Tests:
  - every 10 s, the agent sends `{buildRevision, catalogueVersion, inventory:{complete, units}, outboxPending}`, where `outboxPending` counts published files without a definitive relay state, plus outbox disk usage;
  - `drained: true` stops claims;
  - every attempt in `stop` is stopped (`systemctl stop`), and an id outside the agent's own claims is still validated and stopped only if its unit exists;
  - a 401 stops claims, re-reads the secret and keeps supervising and relaying locally, with no unit stopped;
  - an outage keeps everything running, and the next successful heartbeat carries the current inventory.
- [ ] Integration test: the real API frees a settled attempt's slot only after a complete inventory without it.
- [ ] Implement until green.

## Task 7: outbox scanning and relay (§H, A10-D1)

**Files:**
- Create: `OutboxScanner.cs`, `Relayer.cs`.

**Steps:**
- [ ] Scanner tests on a temporary outbox:
  - the classes `.tmp`, `.written` and `.ready` per Base58 account;
  - a file name that is not a canonical Base58 account plus a known suffix is ignored and reported as an anomaly;
  - `complete` is true only if every directory entry was read without an error;
  - an unreadable file (simulated with mode 000), an `EIO` from a fake reader or a directory that vanishes gives `complete: false`;
  - a file read twice with different size or content is not relayed in that round.
- [ ] Relay tests:
  - `.ready` files are relayed while the unit is alive;
  - `.written` files are untouched while it is alive, and relayed after a verified exit (recorded as published by exit);
  - `.tmp` files are never relayed;
  - each definitive answer is recorded once in `relayDir/<uuid>/<account>.state` and never resent;
  - retries back off with jitter and continue indefinitely;
  - nothing in the outbox is ever written, renamed or deleted (the test outbox is made read-only to the agent's user);
  - a file over 4 MiB is a local anomaly and never sent.
- [ ] Integration tests against the real API:
  - relay across API downtime (the test stops and restarts the API server): the result settles once, with no void and no duplicate effect;
  - a `202` quarantine is never resent;
  - `409 AttemptRevoked` after a disable keeps the file and the state.
- [ ] Implement until green.

## Task 8: exit supervision and exit reports

**Files:**
- Create: `ExitSupervisor.cs`.

**Steps:**
- [ ] Tests:
  - when a started unit is gone (`systemctl show` says `inactive` or `failed`, with an exit status), the agent scans, relays every published file (`.ready`, then `.written` per A10-D1) until each has a definitive answer, and only then sends `exited`;
  - the report carries `{exitCode | signal, results: [{accountId, present}] for every participant in the claim's roster, outbox: {complete}}`, where `present` means a `.written` or `.ready` file exists;
  - an incomplete scan sends `complete: false`, and a later successful rescan sends the same report with `complete: true` (the supplementary observation);
  - an identical repeat is idempotent, and a `409 ConflictingReport` is an anomaly that is not retried;
  - a unit that never started (start failed) is reported with its status, which cancels before start.
- [ ] Integration test: results relayed before the exit report, then the exit report frees the slot. A fake-launcher crash with no file present is reported `present: false` with a complete scan, and the backend voids it (exit evidence). The same crash with the API down: nothing is reported until the API returns, and no void happens before that.
- [ ] Implement until green.

## Task 9: restart, reboot and long outages

**Steps:**
- [ ] Tests with a new agent instance over the same state, outbox and fake systemd:
  - restart at every point of Tasks 3–8 resumes without a duplicate start, relay or report;
  - at startup, everything without a definitive relay state is relayed **before** any exit report;
  - units that ended while the agent was down are found through `systemctl show` and reported after their relays;
  - a VM reboot (the fake systemd has lost every unit, and `/run` is empty) is handled from the persisted claims.
- [ ] A long-outage test: with the API unreachable for a simulated hour (fake clock), there are no claims, relays and reports are only retried, nothing reports absence early, and after recovery every result settles and no void is applied.
- [ ] Implement until green.

## Task 10: deployment artifacts (written, not installed)

**Files:**
- Create in `Backend/deploy/mission/`:
  - `blackglass-agent.service`;
  - `blackglass-mission@.service`;
  - `60-blackglass-mission.rules`;
  - `sysusers.d/blackglass.conf`;
  - `tmpfiles.d/blackglass.conf`;
  - `blackglass-tunnel.service` (A10-D3);
  - `install.sh` and `uninstall.sh` (both idempotent, both requiring an explicit `--yes`);
  - `verify.sh` (the VM security checks).

**Steps:**
- [ ] Static tests that read the files:
  - **Template:** `User=blackglass-mission`, `Restart=no`, `UMask=0027`, `NoNewPrivileges=yes`, `ProtectSystem=strict`, `ProtectHome=yes`, `PrivateTmp=yes`, `PrivateDevices=yes`, `RestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX`, an empty `CapabilityBoundingSet=`, `LoadCredential=process:/run/blackglass/launch/%i/process`, the two fixed `ExecStartPre=+/usr/bin/install …` lines, `TemporaryFileSystem=/var/lib/blackglass/outbox:ro`, `BindPaths=` and `ReadWritePaths=` for `%i` only, and the resource limits.
  - **Agent unit:** `User=blackglass-agent`, `Restart=always`, `NoNewPrivileges=yes`, `ProtectSystem=strict`, `ReadWritePaths=` limited to its state, relay and launch directories, read-only access to the outbox, and the resource limits.
  - **Polkit:** the rule's regex and verbs are tested against a C# reading of the same pattern:
    - it accepts only `start` and `stop` of `^blackglass-mission@<uuid>\.service$`;
    - it refuses `enable`, `mask`, `restart`, other units, upper case, and appended text.
  - **Paths:** tmpfiles give the outbox root `root:blackglass-agent 0750`, the relay, state and launch directories `blackglass-agent 0700`, and the log root as specified.
- [ ] Implement until green.

## Task 11: hardening, observability and the emergency stop

**Steps:**
- [ ] Tests:
  - `blackglass-agent status` prints its claims, units, relay counts and the last heartbeat, with no secrets;
  - `blackglass-agent emergency-stop` (A10-D5) refuses unless it runs as root, then stops every listed `blackglass-mission@*` unit and prints the list (fake systemd);
  - logs are structured, one line per event, with hash prefixes only;
  - a source audit: no `Process.Start` with a shell, no `/bin/sh -c`, no string-built unit names.
- [ ] Implement until green.

## Task 12: the VM step (needs the owner's approval and presence)

This task changes `bg-dev-mission01` (users, directories, units, polkit, agent) and `bg-dev-services01` (one restricted key for the tunnel). It runs only when the owner authorizes it.
- [ ] Record the VM state before: listeners, users, units and packages.
- [ ] Install with `install.sh --yes`, then run `verify.sh`:
  - two units, A and B, from the real template, running the stub (A10-D7):
    - A can't read, write, rename or delete in B's directory (`ENOENT`), and can write its own;
    - the agent can read both and write neither;
  - polkit refuses the agent `enable`, `mask`, `restart`, a malformed unit name and any other unit, and allows `start` and `stop` of a canonical mission unit;
  - the output of `systemd-analyze security` for both units is recorded;
  - the credential file is gone after start, and the unit read it via `$CREDENTIALS_DIRECTORY`.
- [ ] End to end through the tunnel against a **throwaway** accounts stack on services01 (the smoke project's pattern, never the live stack):
  - claim, launch and relay with the stub at each §H crash point, then settle;
  - the API stopped for ten minutes during a mission's end: no void, and everything settles after;
  - an agent restart and a VM reboot mid-mission;
  - the emergency stop;
  - a host disable: claims stop, the files are kept and no unit is stopped by the agent;
  - after a rotation and `SIGHUP`, the agent resumes.
- [ ] Record the VM state after, plus the off-host probe of both VMs. Keep `uninstall.sh` and a verified rollback.

## Task 13: reviews, "As built" and stop

- [ ] Checkpoint review after Task 6. Final whole-branch review on the most capable model. Fix every Critical and Important finding test-first.
- [ ] Full backend suite on the VM runner, the agent suite three times running, then smoke and the restore drill (the claim response change).
- [ ] "As built" (counts, transcripts, rulings and obligations for Stages 11, 12, 16 and 17). Stop before merging, pushing or deploying.

## Acceptance criteria

- Every Stage 8 and Stage 9 agent obligation above has a passing test named in "As built".
- No crash point creates a second unit, launches with an unacknowledged generation, leaves a credential file behind after start, relays a `.tmp` file, touches a `.written` file while its process is alive, or reports `exited` before its relays.
- No backend outage, however long, causes a void. Tested end to end with the API stopped.
- The VM step's isolation, polkit and `systemd-analyze security` evidence is recorded with the owner present.
- `0000`–`0009` are unchanged. Only the listed paths changed. The owner's two Unity files and `.claude/` are untouched.

## Out of scope

- The Unity server's registration, the manifest writer and the `recorded` call (Stages 11–12).
- Co-op attempts.
- Prewarming.
- HAProxy, a private VLAN, Docker for mission servers and orchestration.
- Outbox cleanup (Phase 14).
- Production deployment.

## Infrastructure checkpoint package (for the owner's authorization; nothing below has been executed)

Stage 10 is **code-complete, infrastructure validation pending**. Every file below exists in `Backend/deploy/mission/` and is covered by `DeployAndAuditTests`. Nothing has been installed. Neither VM's system configuration, SSH configuration or firewall was changed.

### 0. Two decisions that block installation (from the independent security review)

The approved design (spec §9.2 and §4.3, Q-5) runs every mission unit as one Unix user, `blackglass-mission`, and hands each process its credential through `LoadCredential=` from an agent-writable path. The security review found that both premises fail. Fixing them changes the identity and credential architecture, so this section presents the options instead of changing the architecture silently. `verify.sh` checks both and **fails until they are resolved**.

**C1: one shared uid breaks per-attempt isolation (Critical).**
- **The problem:** `ProtectProc=invisible` hides only other users' processes. With one uid, a compromised mission process A sees B's PID and can:
  - write or delete B's results through `/proc/<B>/root/var/lib/blackglass/outbox/<B>/` (resolved inside B's namespace);
  - read B's credential and logs;
  - kill B.
- **Why the obvious fix doesn't fit:** systemd's per-instance `DynamicUser=` needs a user name of at most 31 characters, and the unit instance is a 36-character UUID. Unit-prefix-derived dynamic users would be shared across instances.
- **Options:**
  - **C1-A (recommended): one static user per slot.**
    - 64 users `blackglass-m00`…`blackglass-m63` (no shell). The slot is the backend's port assignment: claim `missionPort` − host `portBase`, 0–63, persisted with the claim.
    - Units `blackglass-mission-NN@<uuid>.service`, generated by `install.sh` from one template that differs only in `User=` and `Group=`.
    - The polkit regex becomes `^blackglass-mission-(0[0-9]|[1-5][0-9]|6[0-3])@<uuid>\.service$`.
    - The agent derives the unit name from the validated UUID and the persisted, validated slot.
    - A slot is reused only after its previous unit ended (slots are released only on exit evidence).
    - Distinct uids close every C1 path: `/proc`, signals, `/run/credentials` (per-uid 0500) and logs.
  - **C1-B: `DynamicUser=yes` with a short instance name.**
    - The unit instance becomes a 26-character Base32 encoding of the attempt UUID (still derived only from it), with `User=bgm-%i`.
    - The launcher passes the canonical UUID to the server.
    - This changes A10-D4's naming everywhere (spec §7.4, polkit, heartbeat parsing).
  - **C1-C (not recommended): keep one uid for Phase 13.** Stage 12's server makes itself non-dumpable (`PR_SET_DUMPABLE 0`). That blocks the `/proc/<pid>/root` paths, but not `kill()` or a visible `/run/credentials`.

**I3: `LoadCredential=` reads an agent-writable path as root (Important).**
- **The problem:** systemd follows a symbolic link, a directory or a socket at that path. A compromised agent could have root-only files (`/etc/shadow`, the SSH host keys, the tunnel key) copied into a mission sandbox.
- **Options:**
  - **I3-A (recommended, independent of the C1 choice): a root credential stager.**
    - The mission template gains `Requires=` and `After=blackglass-credential@%i.service`. That root oneshot runs the agent binary's `stage-credential <uuid>` mode, written test-first after the decision.
    - It reuses `Unix.ReadUntrusted`: `O_NOFOLLOW`, then fstat for a regular file, owner `blackglass-agent`, one link and at most 128 bytes, and a `pc1_` secret.
    - It copies the file to `/run/blackglass/credentials/<uuid>/process` (root 0600 in root 0700), and `LoadCredential=` reads only that root-owned copy.
    - Root then never resolves an agent-controlled final component. The polkit rule is unchanged, because the agent still starts only the mission unit.
  - **I3-B (not recommended): accept the risk.** The agent is already trusted with the host secret. The risk is an escalation from the agent's uid to root-readable files.

Once the owner chooses, the chosen options are implemented test-first (agent, units, polkit, `install.sh`, `verify.sh`), reviewed again, and only then installed.

### 1. Every file to install or modify

**bg-dev-mission01** (all root-owned unless noted):

| Path | Mode | Source |
|---|---|---|
| `/etc/sysusers.d/blackglass.conf` | 0644 | `sysusers.d/blackglass.conf` |
| `/etc/tmpfiles.d/blackglass.conf` | 0644 | `tmpfiles.d/blackglass.conf` |
| `/opt/blackglass/agent/releases/<rev>/` (`blackglass-agent`, native libraries, `record-exit`, `RELEASE`) | 0755 dirs and executables, 0644 data | `publish-agent.sh` output, hash-verified by `install.sh` |
| `/opt/blackglass/agent/current` | symlink | `install.sh` |
| `/opt/blackglass/mission/releases/stub/run-mission` and the `/opt/blackglass/mission/current` link | 0755 | `stub/run-mission` (only with `--stub`) |
| `/etc/systemd/system/blackglass-agent.service`, `blackglass-mission@.service`, `blackglass-tunnel.service` | 0644 | the unit files |
| `/etc/polkit-1/rules.d/60-blackglass-mission.rules` | 0644 | the rule |
| `/etc/blackglass/agent/agent.json` | `root:blackglass-agent` 0640 | `agent.json.example` (catalogueVersion set at install) |
| `/etc/blackglass/agent/host-secret` | `blackglass-agent` 0600 | `admin host register` or `rotate-secret` output, installed by the operator |
| `/etc/blackglass/tunnel/ssh_config`, `known_hosts` | 0644 | `tunnel/ssh_config` (host substituted), the verified pinned key |
| `/etc/blackglass/tunnel/id_ed25519` (+ `.pub`) | `blackglass-tunnel` 0600 | generated on the VM by `install.sh` |
| `/etc/blackglass-stub/plans/<test-uuid>/` | 0755/0644 | written by `verify.sh` during the test only |

`/opt/blackglass/server` is not touched.

**bg-dev-services01:**

| Change | Detail |
|---|---|
| Account `bg-api-tunnel` | no password, shell `/usr/sbin/nologin`, in no administrator group |
| `/etc/ssh/sshd_config.d/60-bg-api-tunnel.conf` | 0644; the `Match User` block (`tunnel/services-vm-sshd.conf`) |
| `/etc/ssh/authorized_keys.d/bg-api-tunnel` | 0644; one forward-only key line (`tunnel/services-vm-authorized_keys`) |
| `AllowUsers`/`AllowGroups` | add `bg-api-tunnel` **only if** `sshd -T` shows such a restriction |
| End-to-end window only | a throwaway accounts stack (a separate Compose project, as the smoke test does) on `127.0.0.1:5080`, with `admin host register` for the test host; removed afterwards. Never the live stack, which is not deployed. |

### 2. Users, groups and directories

- **Mission VM users:**
  - `blackglass-agent`, `blackglass-mission` (or the 64 slot users under C1-A) and `blackglass-tunnel`;
  - all system users with shell `/usr/sbin/nologin` and their own group;
  - none is in another's group.
- **Mission VM directories:**
  - `/var/lib/blackglass` (0755 root);
  - `/var/lib/blackglass/outbox` and `/var/lib/blackglass/exits` (0750 `root:blackglass-agent`);
  - `/var/lib/blackglass/agent`, `/var/lib/blackglass/relay` and `/run/blackglass/launch` (0700 `blackglass-agent`);
  - `/var/lib/blackglass/tunnel` (0700 `blackglass-tunnel`);
  - `/var/log/blackglass` (0755 root);
  - `/run/blackglass` (0755 root);
  - per attempt, created by the template as root: `/var/lib/blackglass/outbox/<uuid>` (2750 `<mission uid>:blackglass-agent`) and `/var/log/blackglass/<uuid>` (0750 `<mission uid>`).
- **Services VM:** the account `bg-api-tunnel`.

### 3. Tunnel and credential setup

1. On the services VM, read its host key fingerprint locally: `ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub`.
2. On the mission VM, build `known_hosts` from the key and compare its fingerprint with step 1. Only an identical fingerprint is installed.
3. `install.sh --yes --release <dir> --services-host <address> --known-hosts <file> [--stub]` generates `/etc/blackglass/tunnel/id_ed25519` on the mission VM and prints the public key. The private key never leaves the VM.
4. On the services VM, create `bg-api-tunnel`, install `60-bg-api-tunnel.conf` and the one-line `authorized_keys` with that public key, then run `sshd -t` and `systemctl reload ssh`.
5. On the services VM, `admin host register` for the mission host (throwaway stack in the test window) prints the host secret once. Install it as `/etc/blackglass/agent/host-secret` (`blackglass-agent` 0600) through `read -rs`, never on a command line.
6. `systemctl enable --now blackglass-tunnel blackglass-agent` (or `install.sh --start`).

**Credentials at a glance:**

| Secret | Lives | Mode | Lifetime |
|---|---|---|---|
| Tunnel key | the mission VM | 0600 | until rotated |
| Host secret | the mission VM | 0600 `blackglass-agent` | until `rotate-secret`; a disable revokes it for good |
| Process credentials | in the claim record until the unit has started; LoadCredential file in `/run` (0400) until the start; then only in the unit's private credential directory | | the attempt |

### 4. The exact systemd and polkit permissions

- **Polkit:**
  - `blackglass-agent` may `start` and `stop` units matching `^blackglass-mission@[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.service$` (under C1-A, the slot form);
  - everything else it asks polkit for is `NO`;
  - other subjects are unchanged.
- **Mission template:**
  - `User=`/`Group=` the mission user; `Restart=no`, `UMask=0027`;
  - `ProtectSystem=strict`, `ProtectHome`, `PrivateTmp`, `PrivateDevices`;
  - `TemporaryFileSystem=/var/lib/blackglass/outbox:ro` + `BindPaths=` + `ReadWritePaths=` for `%i` only;
  - `InaccessiblePaths=` for `/etc/blackglass`, the agent, relay, exits and tunnel state, and `/run/blackglass`;
  - `NoNewPrivileges`, an empty `CapabilityBoundingSet`;
  - `RestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX`, `SocketBindAllow=udp` + `SocketBindDeny=any`;
  - the namespace, realtime, SUID, personality, kernel, clock, hostname and proc protections, `SystemCallArchitectures=native`;
  - `MemoryMax=2G`, `CPUQuota=200%`, `TasksMax=256`;
  - `LoadCredential=process:…` (under I3-A, from the root-owned staging copy).
  - **Root steps:** two fixed `ExecStartPre=+/usr/bin/install -d …` lines and `ExecStopPost=+…/record-exit %i`.
- **Agent unit:**
  - `User=blackglass-agent`, `Restart=always`;
  - writes only `/var/lib/blackglass/{agent,relay}` and `/run/blackglass/launch`; reads the outbox and exits;
  - `NoNewPrivileges`, no capabilities, `SocketBindDeny=any`;
  - `MemoryMax=256M`, `TasksMax=64`.
- **Tunnel unit:**
  - `User=blackglass-tunnel`;
  - `ssh -N -F /etc/blackglass/tunnel/ssh_config`: `StrictHostKeyChecking yes`, the pinned `known_hosts`, `ExitOnForwardFailure`, `SessionType none`, no agent or X11 forwarding, `LocalForward 127.0.0.1:5081 127.0.0.1:5080`;
  - `SocketBindAllow=tcp:5081`, `SocketBindDeny=any`;
  - restart with backoff of 5 s rising to at most 120 s.
- **The agent's own check (I1):** it sends nothing unless `/proc/net/tcp` shows the 127.0.0.1:5081 listener owned by `blackglass-tunnel` (`tunnelUser` in `agent.json`).

### 5. The stub-test procedure

1. Record the VM's state before: listeners (`ss -ltnup`), users, units, packages, and `/opt/blackglass/server` (find with mtime and sizes).
2. Install with `install.sh --stub` (the stub is selected only through `/opt/blackglass/mission/current`).
3. Run `verify.sh --yes`, which starts three test attempts under the real template and checks:
   - the polkit grants and refusals (enable, mask, restart, kill, reset-failed, set-property, transient units, other units, malformed names);
   - isolation: A from B through paths, through `/proc`, signals, credentials and logs; the agent reading but never writing results;
   - the tunnel port's protection;
   - the credential path's resistance to a symbolic link;
   - the exit records;
   - `systemd-analyze security` for the mission and agent units.

   The test attempts' outbox and exit records are kept as evidence.
4. End to end through the tunnel, against the throwaway stack:
   - claim, launch and the stub publishing at each §H crash point;
   - relay and settlement;
   - the API stopped for ten minutes during an exit (no void until the agent reports), then restarted;
   - an agent restart and a VM reboot mid-mission;
   - the emergency stop (`--dry-run`, then `--yes`);
   - a host disable (claims stop, files kept, no unit stopped);
   - rotation and `SIGHUP`.
5. Record the state after, the off-host probe of both VMs (no new listener; 5080, 5081, 5432 and 55432 closed off-host), and the transcripts.

### 6. Expected verification results

- **With the current design (shared uid, direct `LoadCredential`), `verify.sh` reports FAIL for:**
  - "A cannot read/write through B's /proc root";
  - "A cannot signal B";
  - probably "A cannot read B's credential";
  - "a symlinked credential source never reaches the unit".

  Everything else is expected `ok`: polkit, path isolation, read-only agent, tunnel port, exit records.
- **After C1-A and I3-A:** every line `ok`.
- **`systemd-analyze security`:** recorded, not gated. For the mission unit, an exposure level of "OK" or better is expected.
- **End to end:** each result settles exactly once; no void during the outage; the void only after the agent's complete scan reports a participant absent; no unit stopped by a 401.
- **Off-host:** nothing new listening; the tunnel's 5081 on loopback only.

### 7. Rollback

- **Mission VM:**
  - `uninstall.sh --yes` stops and disables the agent and the tunnel and removes the units, the polkit rule and the stub link. Mission units are left running unless `--stop-missions` is given.
  - It never deletes `/var/lib/blackglass/*` or `/var/log/blackglass`.
  - Then remove `/etc/blackglass/tunnel/id_ed25519*`, `/etc/sysusers.d/blackglass.conf` and `/etc/tmpfiles.d/blackglass.conf` (the users stay, because they own the evidence).
  - Agent releases stay under `/opt/blackglass/agent/releases`, and a previous one is restored with `ln -sfn` and `mv -T`.
- **Services VM:**
  - remove `/etc/ssh/authorized_keys.d/bg-api-tunnel` and `/etc/ssh/sshd_config.d/60-bg-api-tunnel.conf`, run `sshd -t` and `systemctl reload ssh`, then lock or delete `bg-api-tunnel`;
  - take the throwaway stack down (`docker compose -p <project> down -v`) and confirm the off-host probe.

## As built (2026-10-17, branch `phase13-stage10`; implementation commits local, not pushed or merged; nothing installed)

**Status: code-complete, infrastructure validation pending.** The owner's infrastructure checkpoint has not been authorized, and C1 and I3 (section 0 above) need the owner's decision first.

**Commits:**
- `1969dcd`: Task 0, the rulings.
- `ca67265`: Tasks 1–11, the agent, its tests, the deployment files, the runner's agent mode and the A10-D2 claim participants.
- `5c5341d`: the scripts made executable.
- `307060c`: a test strengthened after a mutation check.
- `14e8037`: the integration tests against the real API.
- `260e834`: the deployment and backend READMEs, and the claim contract.
- `dfee555`: the security review fixes.
- The tested `Backend/` tree is `a9fd31c` (`HEAD:Backend` at `dfee555`).

**Tests (VM runner, `a9fd31c`):**
- Domain: 759 passed, 0 failed.
- Agent: 143 passed, 0 failed.
- Integration: 1,768 passed, 0 failed (the three agent tests against the real API included), in about 9 m.

  The local wrapper was stopped by the PC's low-memory reaper; the VM run finished and its results were collected. No `bg_t_*` database was left.
- An earlier full run at `260e834`: 759 / 136 / 1,768.
- Mutation checks: seven independent defects each turned a test red:
  - a superseded generation launched;
  - the proof that a unit already started ignored;
  - the exit report sent before the relays;
  - `.written` read while the unit is alive;
  - no slow retry on a refused secret;
  - links followed;
  - a failed enumeration reported as complete.

  The relay-before-report test was strengthened after the first batch.
- Schema: unchanged at version 9. No migration. The A10-D2 claim response is additive.

**The owner's requirements, where they are tested:**
- **Claim durability:** `ClaimFlowTests`, including a crash at every step; the integration test.
- **Systemd command construction and malicious names:** `SystemdTests`, `FoundationTests`.
- **Complete enumeration:** `SystemdTests`, `SupervisionTests`.
- **Outbox safety and the A10-D1 read-only recovery:** `OutboxTests`, `SupervisionTests`, and the integration outage test (a `.written` file relayed in place).
- **Relay idempotency and retries:** `OutboxTests`.
- **Process exit and exit evidence:** `SupervisionTests`.
- **Backend failures, outages and the 401 handling (A10-D8):** `SupervisionTests` and the integration tests.
- **Emergency stop (A10-D5):** `SupervisionTests`.
- **Deployment files and the source audit:** `DeployAndAuditTests`.
- **Security review fixes:** `SecurityReviewTests`.

**Independent security review** (opus): 1 Critical, 3 Important, 14 Minor (`.superpowers/sdd/2026-10-17-phase-13-stage10-mission-agent/security-review.md`).
- **Fixed test-first:**
  - I1: the agent sends nothing unless the tunnel port's listener belongs to `tunnelUser`; socket-bind policies on the mission, agent and tunnel units.
  - I2: an unknown start outcome is stopped first and reported only once systemd shows the unit terminal.
  - M1: a credential lost with `/run` is rewritten from the record before start.
  - M2: an entry that names a participant but breaks the naming contract makes the scan incomplete.
  - M12: a flooded outbox (more than 256 entries) is incomplete.
  - M13: a stop directive before the start abandons the claim under the launch gate.
- **Also fixed:**
  - M3: the emergency stop needs no configuration file; `run` and `status` refuse root.
  - M5: `install.sh` validates the host, requires the exact file set and refuses to overwrite.
  - M6: no redirects and no proxy.
  - M8: polkit refuses every other action of the agent.
  - M11: the reads fail closed off x86_64.
  - M14: `systemctl` is killed on cancellation.
- **Documented:** M4 (the emergency stop's caveat), M9 (rotation without the secret on a command line), M10 (full tunnel rollback).
- **M7:** `verify.sh` probes C1 and I3.
- **Owner decisions:** C1 and I3 (section 0).

**Rulings made during execution** (the ledger has each with its cost if wrong):
- The agent's code was written before its unit tests, a TDD-order deviation compensated by the mutation batch.
- The agent references `Accounts.Domain` only, so it validates results exactly as the backend parses them.
- Strict plain `systemctl list-units` parsing instead of JSON.
- A root `ExecStopPost=+` exit recorder; without one, the status is reported as unknown, never invented.
- The `/opt/blackglass/mission/current` launcher link keeps the stub away from `/opt/blackglass/server`.
- An invalid result file makes the scan incomplete; a malformed body that still claims its participant is relayed for quarantine.
- The claim roster is every participant row of the attempt.
- C1 and I3 are presented, not decided.
- No agent release has been published yet.

**Obligations handed on:**
- **The checkpoint (owner):** decide C1 and I3; then implement them test-first, review, publish a release (its `RELEASE` hashes), install, verify and run the end-to-end checks.
- **Stage 11:** manifests in format v1, written as `<accountBase58>.manifest.tmp|written|ready`.
- **Stage 12:**
  - the server binds and listens only on its slot's TCP port (FishNet WebSockets; corrected 2026-10-18 by the owner's final ruling 5), under the enforced restriction B24, which "Security corrections" below makes blocking. The earlier template's TCP refusal was never enforced on Ubuntu 24.04;
  - it follows §H's five publication steps and the exit rules;
  - it reads its credential from `$CREDENTIALS_DIRECTORY/process`;
  - a real launcher replaces the stub behind `/opt/blackglass/mission/current`;
  - under C1-C, the server would also make itself non-dumpable.
- **Phase 14:** outbox and relay cleanup, and an outbox quota or separate filesystem (M12's disk-exhaustion part).

## Security corrections (2026-10-18; the owner's authorization of C1, I3 and the listener review; branch `phase13-stage10`, local; nothing installed)

**Status:** code-complete and lab-validated. Installation on `bg-dev-mission01` still waits for the owner's infrastructure checkpoint. Sections 0 to 7 of the checkpoint package above are superseded where they name `blackglass-mission@`, `User=blackglass-mission`, `/run/blackglass/launch` or the C1/I3 options. This section is the corrected package.

### C1: was `DynamicUser=yes` viable per mission unit? No, and why

Checked in a disposable, privileged, network-less Ubuntu 24.04 container on `bg-dev-services01` (`bg-sdlab`). It runs systemd `255.4-1ubuntu8.18`, the exact build of `bg-dev-mission01` (whose `systemctl --version` was read, nothing else). Two facts rule it out:
1. **No `User=`:** systemd derives an unnamed dynamic user from the **template prefix**, not the instance. `lab-mission@A` and `lab-mission@B` both ran as dynamic user `lab-mission`, uid 64823. A hash is used only when the prefix isn't a valid user name, and it is still the prefix's hash, so every instance would share it.
2. **`User=` built from `%i`:** a dynamic user name is capped at 31 characters, and a full UUID is 36. `User=%i`, `m%i` and `bgm-%i` all fail with "Failed to spawn 'start' task: Invalid argument"; 31 characters work and 32 fail.

The owner requires full UUID unit identities, so one dynamic identity per unit is impossible on this systemd. A shortened instance name (Base32) would drop the full-UUID identity.

### C1 as built: a dynamic user per slot

This is the approved per-slot fallback's structure, with systemd-allocated uids rather than static accounts:
- **Units:** the template `blackglass-mission-slot.service.in` is rendered by `render-slot-units.sh <portBase> <slots>` into one root-owned unit per slot, `blackglass-mission-NN@.service`, NN 00..63.
  - The slot is the attempt's port minus the host's port base (Stage 8 allocates the lowest free port of `[port_base, port_base + capacity)` and releases it only after a confirmed stop). The mapping is deterministic.
  - Each unit runs `DynamicUser=yes`, `User=blackglass-mNN`, so no two live missions share a uid: systemd gives two live dynamic users two uids, and the lab showed 61484 and 64194, 62833 and 62728.
  - No static `blackglass-mNN` may exist (systemd would use it); `install.sh` and `verify.sh` refuse one.
- **One live unit per slot:**
  - The agent starts a slot's unit only when a complete inventory shows no other live unit in that slot. Otherwise it waits, and the launch deadline abandons the claim.
  - The root `ExecStartPre=+slot-guard NN %i` refuses a second live unit.
  - A port outside the host's slots is abandoned before `launching` is reported, and never launched.
- **Outbox and logs:** these are systemd's `StateDirectory=blackglass-outbox/%i` (mode 0755, files 0644 under `UMask=0022`) and `LogsDirectory=blackglass-mission/%i`. They live under `/var/lib/private` and `/var/log/private` (root 0700), and each unit's namespace shows only its own.
  - **uid reuse:** the lab handed a later unit an ended attempt's recycled uid on purpose (systemd first tries the uid that owns one of a unit's directories). It could not read, write, rename or delete the old files.
  - **The agent:** it reads every outbox through its unit's `BindReadOnlyPaths=/var/lib/private/blackglass-outbox:/var/lib/blackglass/outbox`; write, rename and delete fail with EROFS. Outside its unit, `/var/lib/private` is closed to it.
  - **Persistence:** the results survive the unit's exit and a restart of the lab container, read back unchanged.
- **Single start:** an attempt's unit runs at most once, through `AssertPathExists=!` on its outbox and on its exit record. The agent already never restarts one; this also holds against a compromised agent.

### I3 as built: the root credential broker

- **The socket:** `blackglass-credential.socket` listens on `/run/blackglass/credential.sock`, `root:blackglass-agent 0660`, with `Accept=yes` and at most 8 connections. Each connection starts `blackglass-credential@.service`, which runs `blackglass-credential-broker`, a separate small executable in the agent release with no project references.
  - It runs as root **with no capabilities** (`CapabilityBoundingSet=`).
  - Its only writable path is `/run/blackglass/credentials`; it has no network, and `/var/lib/private`, `/etc/blackglass` and `/var/lib/blackglass` are inaccessible to it.
- **The interface:** one request per connection, at most 512 bytes of printable ASCII.
  - The two verbs are `prepare <attempt> <generation>\n<pc1 credential>\n` and `discard <attempt>\n`.
  - The attempt must be canonical lowercase and not nil, the generation 1..2147483647 without leading zeros, and the credential exactly a pc1 secret.
  - The peer must be the agent's uid, read from `/etc/passwd` and never root (`SO_PEERCRED`). No path, no other verb.
- **The store:** `/run/blackglass/credentials/<attempt>/{process,generation}`, files root 0400 in a root 0700 directory.
  - Every request holds an exclusive `flock` on the store.
  - The store, the attempt directory and both files must be the owner's, non-links, of the right type, singly linked and private; otherwise the answer is `unsafe_store` and nothing is read or written through them.
  - Writes are new files (`O_CREAT|O_EXCL|O_NOFOLLOW`, exact mode), fsynced, renamed into place, and the directory is fsynced.
  - **Generation rules:**
    - a higher generation replaces;
    - the same generation is idempotent with the same bytes, and a conflict with other bytes;
    - a lower generation is stale;
    - the credential is written before its generation.
  - Any failure answers `write_failed`. The log names the attempt, the generation and the result, never the credential.
- **The units:** `LoadCredential=process:/run/blackglass/credentials/%i/process` is the only source. A missing file fails the unit's start; the lab showed it fails closed.
- **The agent (`SocketCredentialBroker`):**
  - It prepares at the `Acknowledged` step and again right before the start (idempotent; this covers a reboot that emptied `/run`).
  - It starts nothing without an exact `ok`: a refusal, a garbled answer, no broker or no answer in 10 s each keeps the claim where it is.
  - It discards the credential after the start and on abandonment.
  - It sends nothing but a pc1 credential for a real attempt.
- **Lab:** the agent user cannot write, link into or read the store. A traversal id, a path or host secret in place of the credential, an oversized request and an unknown verb are refused. Another user can't connect, and root is refused by the peer check. Generations move forward, and 8 concurrent preparations end at the highest. The unit received exactly the prepared bytes (compared by SHA-256; the credential is never printed).

### Network listeners

- **Mission units:**
  - `SystemCallFilter=~listen` (EPERM): no stream listener at all, so a mission can never impersonate the tunnel's loopback port, even while the tunnel restarts. This holds on every systemd build (seccomp), and the lab confirmed it.
  - `SocketBindAllow=udp:<the slot's port>` with `SocketBindDeny=any`: meant to hold a mission to its own port (written for UDP; the real server is TCP, see the corrected B24 below), **but systemd enforces it only when built with `BPF_FRAMEWORK`**. Ubuntu 24.04's isn't (lab and `bg-dev-mission01`: `-BPF_FRAMEWORK`). There the lab showed a mission can still bind another slot's UDP port and bind (not listen on) TCP 5081.
  - The second case is a tunnel denial of service only: no secret can leak, because the agent sends nothing unless the port's listener belongs to `tunnelUser`, and a squatter has no listener.
- **Stage 10's I1 statement corrected:** the earlier template's `SocketBindAllow=udp`/`SocketBindDeny=any` never had effect on the mission VM. The agent's listener-uid check was, and stays, the effective I1 control. `verify.sh` now records which enforcement the host has.
- **Agent and broker:** they get `SystemCallFilter=~listen` and `DOTNET_EnableDiagnostics=0`, so the .NET diagnostic IPC listener is never opened.
- **Blocking Stage 12 acceptance requirement (spec §18.2 B24; corrected 2026-10-18 by the owner's final ruling 5):**
  - **The transport:** the Unity server uses FishNet over WebSockets on Unity Transport (decision 046). Its game port is therefore TCP, and it must `listen()`. The earlier "own UDP port" wording was wrong.
  - **The rule:**
    - a mission process can bind and listen only on its slot's TCP port `portBase + NN`;
    - it can bind no other TCP port, on any address: not 127.0.0.1:5081, not another slot's port, not port 0;
    - its port can't be reached from off the host (Phase 13 has no public exposure);
    - explicit UDP binds are denied too, or the owner accepts a recorded residual (see Landlock below).
  - **The Stage 10 template at Stage 12:**
    - Stage 12 replaces `SystemCallFilter=~listen`, which a TCP server can't live with, and the inert `SocketBindAllow=udp:@PORT@` with the chosen mechanism, and switches `verify.sh`'s probes to TCP.
    - Stage 10 keeps both as tested: the stub never binds or listens, and the `udp:` line is unenforced on Ubuntu and would fail closed under a BPF-enabled systemd.
  - **Mechanisms, evaluated in this order, on `bg-dev-mission01` itself:**
    1. **systemd's cgroup-BPF `SocketBindAllow=tcp:<port>` / `SocketBindDeny=any`.**
       - **Read-only finding (2026-10-18):** the VM's systemd 255.4-1ubuntu8.18 is built `-BPF_FRAMEWORK`, so the stock package can't enforce this.
       - The checkpoint's `verify.sh` run records it from a real mission unit.
       - Enforcing it would need a non-stock systemd, which is an owner decision.
    2. **Landlock ABI ≥ 4 (`LANDLOCK_ACCESS_NET_BIND_TCP`).**
       - **Read-only finding:** kernel 6.8.0-146, `landlock` is in the active LSM list, and the ABI query returns 4.
       - **The shape:**
         - a small root-owned launcher, the unit's `ExecStart`, applies a ruleset that handles TCP bind, with one rule for the slot port;
         - it calls `landlock_restrict_self` (under `NoNewPrivileges`), then execs the server;
         - every descendant inherits the restriction, and nothing can lift it.
       - **Limits:**
         - ABI 4 restricts TCP only, and by port, not by address. The address half (loopback only) then needs `IPAddressDeny=any` + `IPAddressAllow=localhost` (systemd's own IP filter, which doesn't depend on `BPF_FRAMEWORK`; to be demonstrated on the VM), or the owner's acceptance.
         - UDP binds stay unrestricted. The only UDP services on the host are `systemd-resolved`'s on 127.0.0.53 and 127.0.0.54 port 53.
       - **Proof:** written test-first, plus a trusted probe from a mission unit on the VM.
    3. **Socket activation:**
       - a per-slot root `.socket` (`ListenStream=127.0.0.1:<portBase+NN>`) passed with `Sockets=`, plus `SystemCallFilter=~bind listen`;
       - this works only if Unity Transport (FishyUnityTransport on Unity Transport 2.7.4) can adopt an inherited listener;
       - that is unverified: its native socket layer creates its own sockets.
  - **Not proof:** nftables (`socket cgroupv2` or port rules) filters packets. It never shows that a process can't bind a port, so it can only supplement one of the mechanisms above.
  - **Until the mechanism is demonstrated on the target VM:**
    - only the trusted stub runs on `bg-dev-mission01`, never a real Unity mission workload;
    - Stage 12 can't be accepted.
  - **Known gap until then** (security review 2, I-1, restated for TCP). A compromised mission could:
    - hold TCP 127.0.0.1:5081 during a tunnel restart: a host-wide outage until `emergency-stop`. No secret leaks, because the agent sends nothing unless the listener belongs to `tunnelUser`, and missions can't listen today;
    - take another slot's idle port.

    Both need a compromised mission process. The stub is root-owned trusted code.
  - **Also for Stage 12:**
    - the server binds its slot port once at start, on 127.0.0.1, and fails if it can't;
    - it never sets `SO_REUSEPORT`;
    - game ports stay outside `net.ipv4.ip_local_port_range` (32768–60999 on the VM; 7770–7777 is outside it);
    - there is no development player-connection listener;
    - whether the server needs D-Bus decides `InaccessiblePaths=-/run/dbus` (review 2, M-6).

### Tests and evidence

- **Test-first:**
  - each new behavior's tests were written and run red before the code: the broker tests (44 after strengthening), the client tests (4), the claim-flow slot and broker tests, the slot-unit naming and parsing tests, the deployment-file and `slot-guard` tests, and review 2's I-2;
  - the one slip: `Identifiers.cs` was written early, then reverted until its tests had failed.
- **Mutation checks:** 14 defects were injected into the new safety code, and 13 turned a test red:
  - a busy slot started;
  - the broker's "ok" skipped;
  - a port outside the slots launched;
  - a wider mode, a lower generation, no flock or any peer accepted;
  - any answer taken as "ok";
  - a link followed;
  - the read's hard-link check removed.

  The entry-level hard-link check alone is masked by the read's own check (defense in depth; both are pinned). Two weak tests were strengthened after the first batch: a private same-owner link target, and a request held inside the lock making another wait.
- **The 143 original agent tests are all kept.** They were adapted to slot unit names and the broker; the credential-file test keeps its name and now asserts the broker store's modes.
- **The real-API agent tests:** two were added, bringing them to 5.
  - Each attempt runs in its port's slot unit with exactly the backend's credential, through the real broker protocol and store over a real Unix socket. Its process session registered with that credential.
  - Without the broker's confirmation no unit starts, the claim waits acknowledged, and it starts once when the broker is back.
- **Lab:** the final run from `eba31d1` used a fresh container, the real `publish-agent.sh`, `install.sh --stub` and `verify.sh`. It passed 85/85 checks, 0 failures, with one expected "note" (no BPF socket-bind on this build); the after-restart part passed 9/9. Evidence is in `.claude/scratchpad/phase13/stage-10/lab/final-run.txt`; the earlier runs and mutation logs are beside it.
  - The lab container is started with shared mount propagation (`mount --make-rshared /`). Docker leaves `/` private, which breaks every unit's `LoadCredential` ("Failed to set up credentials: Protocol error"). The mission VM's `/` and `/run` are already shared (read-only check), and `verify.sh` refuses a host whose `/` isn't.
- **Lab-found defects fixed:**
  - `install.sh` now creates `/etc/sysusers.d` and `/etc/tmpfiles.d` (absent on a minimal Ubuntu).
  - `install.sh` detected a missing stub link wrongly (`readlink -f` prints the path even when it's absent; it now uses `-e`).
  - `install.sh` and `publish-agent.sh` refuse CRLF scripts.
  - Four `verify.sh` and stub probe defects.

### Corrected installation manifest (bg-dev-mission01; nothing installed)

**Installed by `install.sh`, all root-owned unless noted:**
- **Configuration:** `/etc/sysusers.d/blackglass.conf` and `/etc/tmpfiles.d/blackglass.conf` (0644).
- **Static users:** only `blackglass-agent` and `blackglass-tunnel`. The previous package's `blackglass-mission` user and group are gone.
- **Releases:**
  - `/opt/blackglass/agent/releases/<rev>/`: `blackglass-agent`, `blackglass-credential-broker`, their native libraries, `record-exit`, `slot-guard`, `RELEASE`; directories 0755, executables 0755; `/opt/blackglass/agent/current` links to it;
  - with `--stub` only: `/opt/blackglass/mission/releases/stub/run-mission` (0755), `/opt/blackglass/mission/current`, `/etc/blackglass-stub/plans` (0755). `/opt/blackglass/server` is never touched.
- **`/etc/systemd/system/` (0644):**
  - `blackglass-mission-00@.service` to `blackglass-mission-<N-1>@.service`, rendered for `--port-base P --slots N`;
  - `blackglass-credential.socket`, `blackglass-credential@.service`, `blackglass-agent.service`, `blackglass-tunnel.service`.

  No drop-ins; the retired `blackglass-mission@.service` is refused.
- **Polkit:** `/etc/polkit-1/rules.d/60-blackglass-mission.rules` (0644).
- **`/etc/blackglass/agent/` (`root:blackglass-agent 0750`):**
  - `agent.json` (0640, with `portBase` P and `localCapacity` N);
  - `host-secret` (`blackglass-agent 0600`, installed by the owner with the documented atomic procedure).
- **`/etc/blackglass/tunnel/`:** `ssh_config` and the pinned `known_hosts` (0644); `id_ed25519` (`blackglass-tunnel 0600`) and its `.pub`.

**Directories (tmpfiles):**

| Path | Owner and mode | Purpose |
|---|---|---|
| `/var/lib/blackglass` | root 0755 | |
| `/var/lib/blackglass/outbox` | root 0755 | empty mount point for the agent's bind |
| `/var/lib/blackglass/exits` | `root:blackglass-agent 0750` | |
| `/var/lib/blackglass/agent`, `/var/lib/blackglass/relay` | `blackglass-agent 0700` | |
| `/var/lib/blackglass/tunnel` | `blackglass-tunnel 0700` | |
| `/var/lib/private` | root 0700 | |
| `/var/lib/private/blackglass-outbox` | root 0755 | |
| `/var/log/blackglass` | root 0755 | |
| `/run/blackglass` | root 0755 | |
| `/run/blackglass/credentials` | root 0700 | |

**Created by systemd at run time:**
- each attempt's `/var/lib/private/blackglass-outbox/<attempt>` (its dynamic uid, 0755) and `/var/log/private/blackglass-mission/<attempt>` (0750);
- `/run/blackglass/credential.sock` (`root:blackglass-agent 0660`);
- `/run/blackglass/credentials/<attempt>/` (root 0700, files 0400; discarded after the start);
- `/var/lib/blackglass/exits/<attempt>.exit` (`root:blackglass-agent 0640`).

**Enabled:**
- `blackglass-credential.socket` by `install.sh`;
- `blackglass-tunnel.service` and `blackglass-agent.service` only with `--start`, after the host secret is installed.

**Services VM side:** unchanged from section 3 above: the `bg-api-tunnel` account, the `sshd` drop-in and the restricted `authorized_keys` line.

**Backend side (an owner admin action):** the host's registration must use `port_base` = P and `capacity` ≤ N.

### Root-level verification procedure (for the owner's review; NOT executed on either VM)

Run on `bg-dev-mission01` as root, only after the owner authorizes the checkpoint.

**Read-only prechecks.** Expected:
- `systemctl --version | head -2`: 255.4, `-BPF_FRAMEWORK`.
- `findmnt -n -o PROPAGATION / /run`: shared.
- `grep -E '^blackglass-' /etc/passwd`: nothing.
- `ls /etc/systemd/system | grep blackglass`: nothing.
- `ls -la /opt/blackglass`.

**Steps:**
1. **Build the release:** on `bg-dev-services01`'s SDK container, from the approved commit checked out with LF, run `deploy/mission/publish-agent.sh <out>`. Record `RELEASE` (revision and the SHA-256 lines). Copy the release and `deploy/mission/` to `/root/bg-stage10/` on the mission VM.
2. **Install:** `install.sh --yes --release /root/bg-stage10/agent-<rev> --services-host <address> --known-hosts <pinned file> --port-base P --slots N --stub`. N = 8 is recommended for Phase 13 (one participant per attempt).
   - Expected: "installed agent release <rev>; slots 00..07 on ports P..P+7";
   - `systemctl is-active blackglass-credential.socket` is active.
3. **Verify:** `verify.sh --yes`.
   - Expected: every check "ok" (85 in the lab), 0 FAIL, and one "note" stating that this systemd doesn't enforce `SocketBindAllow`.
   - The report is `/var/log/blackglass/verify-<time>.txt`; it prints the state file for the next step.
4. **Reboot:** `reboot`, then `verify.sh --yes --after-restart <state file>`. Expected: all "ok".
5. **Tunnel and end to end:** only then, the tunnel (section 3) and the agent (`install.sh … --start`, or `systemctl enable --now blackglass-tunnel blackglass-agent`) for the stub end-to-end claim of section 5, with the new unit names.

**Expected security facts:**
- distinct dynamic uids `blackglass-mNN`;
- no cross-attempt access in either direction;
- no access after uid reuse;
- the agent reads every result and can change none;
- the broker refuses every malformed or hostile request, and the unit gets exactly the prepared bytes;
- no TCP listener possible;
- results survive the reboot.

**Rollback:**
- `uninstall.sh --yes`, with `--stop-missions` to stop live missions. It removes units, the polkit rule and the stub link, and keeps all evidence.
- Users are removed only by hand (`userdel blackglass-agent blackglass-tunnel`, after the evidence is archived).
- The services VM's tunnel retirement is as in section 7.

### Independent security review 2 (opus): 0 Critical, 2 Important, 9 Minor (`.superpowers/sdd/…/security-review-2.md`)

**Important, both resolved:**
- **I-1:** the Stage 12 listener requirement had no durable record, and it was too narrow. It is now B24 in spec §18.2 and in the Stage 12 obligations above, worded for every address and port (the tunnel's included). It names three options, including passed sockets with `bind` denied, which needs neither BPF nor nftables. The known gap is recorded: a compromised mission could deny the tunnel's port during a tunnel restart, or take another slot's idle game port; it can never impersonate the tunnel.
- **I-2:** the unit was derived from mutable configuration. A changed `portBase` or `localCapacity` made a live mission look ended: `.written` files read, false exit evidence. Fixed test-first: each claim persists its slot, and starts and supervision use only that (`R2_I2_…`, both cases red, then green).

**Minor:**
- **Fixed:**
  - M-1: the broker can't see `/etc/shadow`, `/etc/gshadow`, `/etc/ssh` or the sudoers files, and the docs now state its real confinement.
  - M-3: `slot-guard` has no environment hook. It validates whole values (newline-proof; `record-exit` too), treats every non-terminal state as live, and refuses the same attempt in another slot.
  - M-4: `slot-guard` refuses while any process still runs as the slot's uid. It reads that uid from systemd, not NSS (the lab caught the NSS dependency).
  - M-5: `LimitCORE=0`, and `/var/crash` and apport are inaccessible.
  - M-6: `PrivateIPC=yes`.
  - M-7: `CollectMode=inactive-or-failed`, in `[Unit]` (the lab caught it misplaced in `[Service]`, where systemd ignores it).
  - M-8: `install.sh` and `verify.sh` check systemd's own `DropInPaths` and `FragmentPath`.
  - M-9: stale statements.
- **Deferred (owner decisions, not defects):**
  - M-1's stronger option: run the broker as a dedicated static non-root user owning the store. `LoadCredential` would still work, since PID 1 reads as root. This changes the approved "root-owned broker" shape.
  - M-2: the broker's 5 s read deadline likely doesn't interrupt a blocking read. `RuntimeMaxSec=15s` bounds it, and it fails closed: no answer, nothing written.
  - M-6's `InaccessiblePaths=-/run/dbus` waits for Stage 12 to confirm the server needs no D-Bus.
  - M-3's note: `slot-guard` runs after systemd has set up the refused unit, so that attempt is burned (only a compromised agent reaches it). This is documented.

### Final state (2026-10-18)

- **Full suite** (VM runner, tree `f109cf9` = `eba31d1:Backend`): domain 759/759, agent 250/250, integration 1,770/1,770 (9 m), cleanup verified. The pre-review run at `0b70a76` was 759 / 242 / 1,770.
- **Compared with the tested Stage 10 tree** (`a9fd31c` at `dfee555`): 51 files under `Backend/` changed (+2,462/−468).
  - Changed: the agent (unit naming, slots, the broker client, configuration), the new broker project, the deployment files and the tests.
  - Unchanged: migrations, the API, persistence and the domain. Schema version 9.
  - The old stub held a stray NUL byte; the new one is plain text.
- **Commits** (local `phase13-stage10`, not pushed or merged):
  - `9ecb8ca`: the C1, I3 and listener corrections;
  - `ff7c218`, `8351638`, `eba31d1`: lab-found fixes;
  - `7e0841a`: strengthened tests;
  - `0b70a76`: READMEs and spec 2.11;
  - `afbf174`: review 2 fixes;
  - then these docs.
- **Preserved:** the two Unity settings files and `.claude/` are untouched. Nothing was installed on either VM. The mission VM was only read (`systemctl --version`, `findmnt`, `nsswitch.conf`, the `service.d` listings).
- **Lab environment, disposable:**
  - a privileged, network-less container `bg-sdlab` (image `bg-sdlab:24.04`) on `bg-dev-services01`, stopped;
  - its build directory `/var/tmp/bg-sdlab`.

  Both can be removed with `docker rm bg-sdlab; docker rmi bg-sdlab:24.04; rm -rf /var/tmp/bg-sdlab`. No firewall, SSH, Docker or systemd configuration was changed.

**Remaining blockers:** superseded by "Final owner rulings and merge (2026-10-18)" below.

## Final owner rulings and merge (2026-10-18)

The owner accepted the security corrections at `23e06fc` (the per-slot dynamic-user design, the root broker, the lab results and the test totals) and ruled:
1. **C1:** one systemd-managed dynamic Unix identity per mission slot (`blackglass-mNN`), one active mission per slot, protected per-unit persistent results.
2. **I3:** the small root-owned broker stays. A separate non-root broker (review 2, M-1's stronger option) isn't required for Stage 10.
3. **Capacity:** eight concurrent slots, 00–07. The architecture keeps 64; the other 56 aren't activated.
4. **Port base:** the registered `port_base` equals the agent's `portBase`. It's chosen from the mission VM's evidence below, not invented.
5. **Listeners:** the server is FishNet over WebSockets (TCP), not UDP. B24 is corrected in "Network listeners" above: an evaluation order, nftables alone is never proof, and only the trusted stub runs until the mechanism is demonstrated on the target VM.
6. **Lab cleanup:** the results are preserved, then only the lab's container, image and directory are removed (below).

**Port base: 7770, slots 00–07 on TCP 7770–7777.** Read-only evidence from `bg-dev-mission01`, 2026-10-18:
- **Listeners** (`ss -tulnp`):
  - `sshd` on 22, all addresses;
  - `systemd-resolved` on 127.0.0.53 and 127.0.0.54 port 53 (TCP and UDP);
  - an editor's remote server on 127.0.0.1:45133, which is inside the ephemeral range.

  Nothing listens on 1024–32767.
- **Port settings:**
  - `net.ipv4.ip_local_port_range` is 32768–60999;
  - `net.ipv4.ip_local_reserved_ports` is empty;
  - `net.ipv4.ip_unprivileged_port_start` is 1024;
  - `ufw` is inactive and the nftables ruleset is empty.
- **Spec:** §4.5 puts the mission ports at `127.0.0.1:<7770 + n>`.
- **Why 7770–7777 fits:**
  - nothing listens there;
  - it is outside the ephemeral range, so no implicit bind lands there and `ip_local_reserved_ports` needs no change;
  - it is clear of the tunnel's 5081.
- **One conflict:** `/opt/blackglass/server/run-net.sh`, the networking experiment's script, is run by hand and defaults to `NET_PORT=7770`.
  - It is harmless for Stage 10's stub, which binds nothing.
  - From Stage 12 on, it runs only with `NET_PORT` outside 7770–7777, or is retired.
  - If the owner prefers to keep 7770 for experiments, 7800–7807 meets every other criterion equally. The value must then change in the installation and the registration together.

**Final validation (2026-10-18):**
- **Tested tree:** `23e06fc:Backend` = `eba31d1:Backend` = tree `f109cf9`, the tree of the final suite and the final lab. `eba31d1..23e06fc` changed only `CLAUDE.md` and this plan.
- **Suite** (VM runner, from the trx counters): domain 759/759, agent 250/250, integration 1,770/1,770, 0 failed. Cleanup verified: no `bg_t_*` database and no runner container.
- **Lab:** `verify.sh` 85/85 ok and the after-restart part 9/9 ok, 0 FAIL, plus the one expected BPF note.
- **Review 2:** 0 Critical. I-2 (the unit derived from configuration) is resolved test-first. I-1 is retained as B24, the blocking Stage 12 network-enforcement obligation.
- **Paths:** `main..phase13-stage10` changes only `Backend/`, `Docs/` and `CLAUDE.md`. The owner's two Unity settings files and `.claude/` are untouched.
- **The final-rulings commit** changes only `Docs/`, `CLAUDE.md` and the text of `Backend/deploy/mission/README.md`. No executable, deployment or test file changed after the full run, so the evidence stands without a rerun.

**Lab cleanup** (`bg-dev-services01`, 2026-10-18):
- **Preserved first:** the in-container reports (`verify-final.txt`, `verify-final-after.txt`), the lab's `Dockerfile` and the release manifest were copied to `.claude/scratchpad/phase13/stage-10/lab/container-reports/`, beside `final-run.txt`.
- **Removed:**
  - the stopped container `bg-sdlab` (no mounts, no volumes);
  - the image `bg-sdlab:24.04` (no other container used it, it carried no other tag, and it had no child image);
  - `/var/tmp/bg-sdlab`.
- **Untouched:**
  - the running `blackglass-testdb-postgres-1`;
  - the images `blackglass-accounts-api:dev`, `mcr.microsoft.com/dotnet/sdk:10.0` and `postgres:17`;
  - the volumes `bg-test-nuget`, `bg-test-work` and `blackglass-testdb_testdata`.

  No Docker daemon setting changed.

**Merge:** `phase13-stage10` was fast-forwarded into `main` at the commit that adds this section, and pushed without force.

**Status:** Stage 10 is **code-complete and lab-validated, with mission-VM infrastructure validation outstanding**. It is not operational on the mission VM.

**Remaining blockers:**
1. **The infrastructure checkpoint below:** it needs the owner's explicit authorization, phase by phase.
2. **Stage 12 B24:** the bind mechanism must be demonstrated on `bg-dev-mission01` before any real Unity mission workload runs there.
3. **The backend registration** (`mission01`, capacity 8, `port_base` 7770): first in the checkpoint's throwaway stack, then in the live stack when the owner deploys it.

## Host-specific infrastructure checkpoint (2026-10-18; for the owner's explicit authorization; nothing below has been executed)

This section supersedes the earlier "Infrastructure checkpoint package" and "Corrected installation manifest" wherever they differ.

**Fixed values:**
- host id `mission01`;
- `portBase` 7770 and 8 slots (00–07, TCP 7770–7777);
- the tunnel from `127.0.0.1:5081` on the mission VM to `127.0.0.1:5080` on the services VM.

**Placeholders:**
- `<rev>` is the first 12 characters of the merged `main` commit;
- `<services-address>` and `<mission-address>` are typed by the operator and never committed;
- `<catalogue-sha>` is `sha256sum Backend/catalogue/catalogue.json` at `<rev>`.

**Never in this checkpoint:**
- a firewall or Docker daemon change;
- a change to the mission VM's SSH server;
- the live accounts stack;
- a real Unity mission workload.

Phases A to C are the infrastructure installation; phase D, the end-to-end run against a throwaway stack, needs its own approval.

### Manifest

**`bg-dev-mission01`** (root-owned unless noted):

| Item | Owner, mode | Notes |
|---|---|---|
| Users `blackglass-agent`, `blackglass-tunnel` | system users, each only in its own group, shell `/usr/sbin/nologin` | from `/etc/sysusers.d/blackglass.conf`. No static mission user: `install.sh` refuses any `blackglass-mNN` in `/etc/passwd` |
| Mission identities `blackglass-m00` … `blackglass-m07` | systemd dynamic users (61184–65519; none in use on the VM today) | allocated per live unit; never in the agent's group |
| `/etc/sysusers.d/` (new; absent today), `/etc/sysusers.d/blackglass.conf`, `/etc/tmpfiles.d/blackglass.conf` | 0755, 0644, 0644 | |
| Directories (tmpfiles) | as in "Corrected installation manifest" | adds `/var/lib/private`, `/var/lib/private/blackglass-outbox` and `/run/blackglass/credentials` (root 0700) |
| `/opt/blackglass/agent/releases/<rev>/` and the `current` link | 0755 directories and executables, 0644 data | `blackglass-agent`, `blackglass-credential-broker`, their native libraries, `record-exit`, `slot-guard`, `RELEASE`; every file is checked against `RELEASE` |
| `/opt/blackglass/mission/releases/stub/run-mission`, the `current` link, `/etc/blackglass-stub/plans` | 0755 | the trusted stub only. `/opt/blackglass/server` and `/opt/blackglass/dev-tokens*.txt` are never touched |
| `/etc/systemd/system/blackglass-mission-00@.service` … `-07@.service` | 0644 | rendered for 7770 and 8 slots: `User=blackglass-mNN`, `SocketBindAllow=udp:77NN` (inert here, see B24), `ExecStartPre=+slot-guard NN %i`, `LoadCredential=process:/run/blackglass/credentials/%i/process` |
| `blackglass-credential.socket` | 0644 | `/run/blackglass/credential.sock`, `root:blackglass-agent 0660`, `Accept=yes`, at most 8 connections |
| `blackglass-credential@.service` | 0644 | the broker, as root with an empty capability set; `PrivateNetwork`, `AF_UNIX` only; writes only `/run/blackglass/credentials`; `/etc/shadow`, `/etc/gshadow`, `/etc/ssh`, the sudoers files, `/etc/blackglass` and the result stores are inaccessible; `RuntimeMaxSec=15s` |
| `blackglass-agent.service` | 0644 | `User=blackglass-agent`; writes `/var/lib/blackglass/{agent,relay}`; reads the exits and, through a read-only bind, the outboxes; can't listen |
| `blackglass-tunnel.service` | 0644 | `User=blackglass-tunnel`, `ssh -N -F /etc/blackglass/tunnel/ssh_config bg-api-tunnel`. Its `SocketBindAllow=tcp:5081` is inert here too; the agent's listener-owner check is the control |
| `/etc/polkit-1/rules.d/60-blackglass-mission.rules` | 0644 | `blackglass-agent` may only `start` and `stop` `blackglass-mission-NN@<uuid>.service`; everything else it asks is NO; other subjects are unaffected |
| `/etc/blackglass/agent/` | `root:blackglass-agent 0750` | |
| `/etc/blackglass/agent/agent.json` | `root:blackglass-agent 0640` | `hostId` `mission01`, `portBase` 7770, `localCapacity` 8 (set by `install.sh`). In phase D only: `serverBuildInfo` and `catalogueVersion` |
| `/etc/blackglass/agent/host-secret` | `blackglass-agent 0600` | phase D only, by the README's `read -rs` procedure; never on a command line |
| `/etc/blackglass/tunnel/ssh_config`, `known_hosts` | 0644 | `known_hosts` holds the pinned ED25519 key, whose fingerprint was compared with the services VM's own reading. `StrictHostKeyChecking yes`, no fallback |
| `/etc/blackglass/tunnel/id_ed25519` and `.pub` | `blackglass-tunnel 0600` and 0644 | generated on the VM; the private key never leaves it |
| Run time | | per-attempt outbox and logs under `/var/lib/private` and `/var/log/private`; credentials `/run/blackglass/credentials/<attempt>/process` (root 0400 in root 0700, discarded after the start); exit records `/var/lib/blackglass/exits/<attempt>.exit` (`root:blackglass-agent 0640`) |
| Enabled | | `blackglass-credential.socket` (phase A), `blackglass-tunnel.service` (phase C), `blackglass-agent.service` (phase D only) |

**`bg-dev-services01`:**

| Item | Detail |
|---|---|
| Account `bg-api-tunnel` | system account, own group, no home, shell `/usr/sbin/nologin`, no password, in no administrator group |
| `/etc/ssh/authorized_keys.d/` (new) and `/etc/ssh/authorized_keys.d/bg-api-tunnel` | root 0755 and root 0644. One line: `restrict,port-forwarding,permitopen="127.0.0.1:5080",command="/bin/false"` plus the mission VM's tunnel public key |
| `/etc/ssh/sshd_config.d/60-bg-api-tunnel.conf` | root 0644, `tunnel/services-vm-sshd.conf` (`Match User bg-api-tunnel`: public key only, local forward to 127.0.0.1:5080 only, no listen, TTY, agent, X11, stream-local or tunnel; `ForceCommand /bin/false`) |
| `AllowUsers`/`AllowGroups` | none set (`sshd -T`, 2026-10-18), so no edit |
| Phase D only | a throwaway Compose project `bg-e2e` on `127.0.0.1:5080`, removed afterwards; never the live stack |

### Ordered commands

**Phase A: the mission host, the stub and the isolation tests.** This phase changes no SSH configuration on either VM.

A1. **Services VM, read-only:**

```
ssh bg-dev-services01-admin
sudo -n ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub   # the ED25519 fingerprint the owner verified on the console (2026-10-14); keep it for A4
sudo -n sshd -T | grep -Ei '^(allowusers|allowgroups) '     # expected: nothing
ss -H -tuln; getent passwd bg-api-tunnel                     # record; expected: 22, 53 (loopback), 127.0.0.1:55432; no account
```

A2. **Build the release from the merged commit** (PC, then an SDK container on the services VM). Only `/var/tmp/bg-stage10` is written.

```
git bundle create <scratch>/bg-main.bundle main
ssh bg-dev-services01-admin 'sudo -n install -d -m 0700 /var/tmp/bg-stage10' && ssh bg-dev-services01-admin 'sudo -n tee /var/tmp/bg-stage10/bg-main.bundle >/dev/null' < <scratch>/bg-main.bundle
ssh bg-dev-services01-admin "sudo -n docker run --rm -v /var/tmp/bg-stage10:/w mcr.microsoft.com/dotnet/sdk:10.0 bash -c 'git config --global core.autocrlf false && git clone -q /w/bg-main.bundle /w/src && cd /w/src && git checkout -q <rev> && cd Backend && deploy/mission/publish-agent.sh /w/out'"
ssh bg-dev-services01-admin 'sudo -n cat /var/tmp/bg-stage10/out/agent-<rev>/RELEASE'   # record: revision=<rev>, runtime=linux-x64, a SHA-256 for every file
```

A3. **Copy the release and the deployment files to the mission VM** (PC; LF copies):

```
ssh bg-dev-mission01 'install -d -m 0700 /root/bg-stage10 /root/bg-stage10/evidence'
ssh bg-dev-services01-admin 'sudo -n tar -C /var/tmp/bg-stage10/out -cf - agent-<rev>' | ssh bg-dev-mission01 'tar -x -C /root/bg-stage10'
git -c core.autocrlf=false -c core.eol=lf archive --format=tar --prefix=mission/ <rev>:Backend/deploy/mission | ssh bg-dev-mission01 'tar -x -C /root/bg-stage10'
```

A4. **Mission VM: the state before, and the pinned host key:**

```
ssh bg-dev-mission01
{ date -u; uname -r; systemctl --version | head -2; findmnt -n -o TARGET,PROPAGATION /; findmnt -n -o TARGET,PROPAGATION /run
  ss -H -tulnp; getent passwd | cut -d: -f1,3,7; systemctl list-unit-files --no-legend | grep -i blackglass
  ls -la /opt/blackglass /etc/polkit-1/rules.d; find /opt/blackglass/server -printf '%p %s %T@\n' | sort | sha256sum
  sysctl net.ipv4.ip_local_port_range net.ipv4.ip_local_reserved_ports; } > /root/bg-stage10/evidence/before.txt 2>&1
ssh-keyscan -t ed25519 <services-address> 2>/dev/null > /root/bg-stage10/services.known_hosts
ssh-keygen -lf /root/bg-stage10/services.known_hosts   # must equal A1's fingerprint exactly; otherwise STOP
```

Expected in `before.txt`:
- systemd 255.4 with `-BPF_FRAMEWORK`;
- `/` and `/run` shared;
- no `blackglass` user or unit;
- nothing on 5081 or 7770–7777.

A5. **Install** (mission VM, root):

```
cd /root/bg-stage10/mission
./install.sh --yes --release /root/bg-stage10/agent-<rev> --services-host <services-address> \
  --known-hosts /root/bg-stage10/services.known_hosts --port-base 7770 --slots 8 --stub | tee /root/bg-stage10/evidence/install.txt
```

Expected:
- the tunnel's public key, then "installed agent release <rev>; slots 00..07 on ports 7770..7777";
- `systemctl is-active blackglass-credential.socket` is active;
- the tunnel and the agent are neither enabled nor running.

Checks:

```
ls /etc/systemd/system | grep blackglass   # mission-00@ … mission-07@, credential.socket, credential@, agent, tunnel; nothing else
systemctl cat blackglass-mission-07@.service | grep -E '^(User|SocketBindAllow)='   # User=blackglass-m07, SocketBindAllow=udp:7777
stat -c '%U:%G %a %n' /run/blackglass/credential.sock /run/blackglass/credentials /etc/blackglass/agent /etc/blackglass/agent/agent.json /etc/blackglass/tunnel/id_ed25519
# root:blackglass-agent 660; root:root 700; root:blackglass-agent 750; root:blackglass-agent 640; blackglass-tunnel:blackglass-tunnel 600
grep -E '"(hostId|portBase|localCapacity)"' /etc/blackglass/agent/agent.json   # mission01, 7770, 8
id blackglass-agent; id blackglass-tunnel                                     # each only in its own group
```

A6. **The isolation tests** (mission VM, root):

```
/root/bg-stage10/mission/verify.sh --yes --report /root/bg-stage10/evidence/verify-1.txt; echo exit=$?
```

Expected:
- exit 0;
- every check "ok" (85 in the lab), 0 FAIL;
- one "note": this systemd doesn't enforce `SocketBindAllow` (`udp_other_port=allowed`, `tcp_bind_5081=allowed`). That note is the on-VM record of B24's first mechanism.

The script prints the state file for A7.

A7. **Reboot** (mission VM; the owner's other sessions on it drop):

```
systemctl reboot
# after reconnecting:
findmnt -n -o TARGET,PROPAGATION /; ls /run/blackglass/credentials | wc -l   # shared; 0
/root/bg-stage10/mission/verify.sh --yes --after-restart <state file> --report /root/bg-stage10/evidence/verify-after-reboot.txt; echo exit=$?
```

Expected: exit 0 and 9/9 ok. Phase A ends here: the units are installed, only the broker's socket is enabled, and nothing connects anywhere.

**Phase B: the services VM's tunnel account.** This phase changes the services VM's SSH configuration.

Keep the current admin session open throughout. Close it only after a new admin login has succeeded.

```
ssh -t bg-dev-services01-admin sudo -i
useradd --system --user-group --no-create-home --home-dir /nonexistent --shell /usr/sbin/nologin bg-api-tunnel
install -d -m 0755 -o root -g root /etc/ssh/authorized_keys.d
```

From the PC, install the two files from `<rev>`. The public key isn't a secret.

```
git show <rev>:Backend/deploy/mission/tunnel/services-vm-sshd.conf | ssh bg-dev-services01-admin 'sudo -n tee /etc/ssh/sshd_config.d/60-bg-api-tunnel.conf >/dev/null && sudo -n chmod 0644 /etc/ssh/sshd_config.d/60-bg-api-tunnel.conf'
pub="$(ssh bg-dev-mission01 cat /etc/blackglass/tunnel/id_ed25519.pub | awk '{print $1" "$2}')"
printf 'restrict,port-forwarding,permitopen="127.0.0.1:5080",command="/bin/false" %s bg-api-tunnel@bg-dev-mission01\n' "$pub" \
  | ssh bg-dev-services01-admin 'sudo -n tee /etc/ssh/authorized_keys.d/bg-api-tunnel >/dev/null && sudo -n chmod 0644 /etc/ssh/authorized_keys.d/bg-api-tunnel'
```

Then, in the root shell:

```
sshd -t                                    # no output
sshd -T -C user=bg-api-tunnel,host=bg-dev-mission01,addr=<mission-address> | grep -Ei '^(authenticationmethods|allowtcpforwarding|permitopen|permitlisten|permittty|forcecommand|authorizedkeysfile|x11forwarding|allowagentforwarding|allowstreamlocalforwarding|permittunnel|gatewayports) '
# expected: publickey; local; 127.0.0.1:5080; none; no; /bin/false; /etc/ssh/authorized_keys.d/bg-api-tunnel; no; no; no; no; no
sshd -T -C user=admin,host=pc,addr=<an admin source address> | grep -Ei '^(allowtcpforwarding|permitopen|forcecommand) '   # yes; any; none (admin unchanged)
systemctl reload ssh
```

From the PC, in a new session: `ssh bg-dev-services01-admin true && echo admin-ok`.

**Phase C: the tunnel only** (mission VM, root; the agent stays off):

```
systemctl enable --now blackglass-tunnel.service; sleep 3; systemctl is-active blackglass-tunnel   # active
awk 'NR > 1 && $2 == "0100007F:13D9" && $4 == "0A" { print $8 }' /proc/net/tcp | xargs -r getent passwd | cut -d: -f1   # blackglass-tunnel (the agent's own check)
ss -H -ltn 'sport = :5081'                                                                       # exactly one listener, 127.0.0.1:5081
journalctl -u blackglass-tunnel -n 20 --no-pager                                                 # no host-key or forwarding error
```

Negative checks of the key, the pinning and the forced restrictions. Each must fail:

```
runuser -u blackglass-tunnel -- ssh -F /etc/blackglass/tunnel/ssh_config -o ClearAllForwardings=yes -o UserKnownHostsFile=/dev/null bg-api-tunnel true; echo exit=$?   # 255, "Host key verification failed"
runuser -u blackglass-tunnel -- ssh -F /etc/blackglass/tunnel/ssh_config -o ClearAllForwardings=yes -o SessionType=default -T bg-api-tunnel id; echo exit=$?          # no shell: /bin/false (exit 1), no id output
runuser -u blackglass-tunnel -- ssh -F /etc/blackglass/tunnel/ssh_config -o ClearAllForwardings=yes -o ExitOnForwardFailure=yes -R 127.0.0.1:15082:127.0.0.1:22 bg-api-tunnel; echo exit=$?   # remote forward refused
runuser -u blackglass-tunnel -- ssh -F /etc/blackglass/tunnel/ssh_config -o ClearAllForwardings=yes -L 127.0.0.1:15081:127.0.0.1:22 -f -N bg-api-tunnel; timeout 3 bash -c 'exec 3<>/dev/tcp/127.0.0.1/15081; head -c 4 <&3'; pkill -u blackglass-tunnel -f 15081   # prints no "SSH-" banner: the open is "administratively prohibited"
```

The checks after phases A to C:
- **Off-host probe:** `Backend/tools/offhost-probe.sh` from the PC, run against both VMs. Only 22 is reachable; 5080, 5081, 5432, 55432 and 7770–7777 are closed.
- **Listeners:** `ss -H -tuln` on both VMs equals the "before" state, plus the mission VM's 127.0.0.1:5081.
- **The server directory:** the `/opt/blackglass/server` hash is unchanged.

**Phase D: the end-to-end run with the stub.** It needs separate explicit approval and uses a throwaway stack, never the live one.

1. **Services VM, a root shell, from `<rev>` synced by `tools/vm-sync.sh`:**

   ```
   cd /opt/blackglass/backend/src/Backend/deploy/services
   export BLACKGLASS_COMPOSE_PROJECT=bg-e2e BLACKGLASS_API_TAG=e2e; work="$(mktemp -d)"
   export BLACKGLASS_SECRETS_DIR="$work/secrets" BLACKGLASS_BACKUP_DIR="$work/backups"
   dc() { docker compose -p bg-e2e -f compose.yaml "$@"; }
   ss -H -ltn '( sport = :5080 )'      # must print nothing
   bash make-secrets.sh "$BLACKGLASS_SECRETS_DIR" >/dev/null && dc build api && dc up -d --wait postgres && bash deploy.sh
   curl -sf http://127.0.0.1:5080/health   # {"status":"ok","schemaVersion":9}
   dc --profile tools run --rm -T admin host register --host-id mission01 --capacity 8 --port-base 7770 --operator owner --reason stage10-e2e --yes
   dc --profile tools run --rm -T admin build allow --revision stage10-stub --catalogue-version sha256:<catalogue-sha> --templates procedural-standard:1 --operator owner --reason stage10-e2e --yes
   ```

   The host secret is printed once. It goes only into the mission VM's `read -rs` prompt: never into a file, a note or a shell history on the PC.

2. **Mission VM, root:**

   ```
   printf 'revision=stage10-stub\ncatalogueVersion=sha256:<catalogue-sha>\n' > /etc/blackglass-stub/build-info.txt; chmod 0644 /etc/blackglass-stub/build-info.txt
   sed -i -e 's|"serverBuildInfo": "[^"]*"|"serverBuildInfo": "/etc/blackglass-stub/build-info.txt"|' \
          -e 's|"catalogueVersion": "[^"]*"|"catalogueVersion": "sha256:<catalogue-sha>"|' /etc/blackglass/agent/agent.json
   install -o blackglass-agent -g blackglass-agent -m 0600 /dev/null /etc/blackglass/agent/host-secret.new
   read -rs secret && printf '%s\n' "$secret" > /etc/blackglass/agent/host-secret.new && unset secret
   mv -f /etc/blackglass/agent/host-secret.new /etc/blackglass/agent/host-secret
   systemctl enable --now blackglass-agent.service; journalctl -u blackglass-agent -n 50 --no-pager   # heartbeats accepted; no secret in the log
   ```

3. **The scenarios** of "Infrastructure checkpoint package" section 5 step 4, with the slot unit names. Drive the attempts the way `tests/smoke-test.sh`'s Stage 8 and 9 sections do; `BLACKGLASS_DEV_HOSTS` stays disabled. The scenarios:
   - claim and launch, then the stub's §H crash points;
   - relay and settlement;
   - the API stopped for ten minutes during an exit;
   - an agent restart and a VM reboot mid-mission;
   - `emergency-stop --dry-run`, then `--yes`;
   - a host disable;
   - a rotation with `SIGHUP`.

   Also check:
   - every claimed port is in 7770–7777;
   - each unit is `blackglass-mission-<port − 7770>@<attempt>.service`;
   - no slot ever runs two units.

4. **Afterwards:**
   - on the mission VM: `systemctl disable --now blackglass-agent`, then `rm -f /etc/blackglass/agent/host-secret`;
   - on the services VM: `dc --profile tools down -v --remove-orphans`, `docker image rm blackglass-accounts-api:e2e` and `rm -rf "$work"`;
   - repeat the off-host probe.

### What only the real mission VM can establish

The container lab can't establish these:
1. **Mount propagation and credential delivery on the real host:** `LoadCredential` needs a shared `/`. The lab had to force that, and the VM has it natively.
2. **A real reboot:**
   - `/run` (including the staged credentials) is cleared, while `/var/lib/private` and `/var/log/private` persist;
   - the broker's socket, the tunnel and the agent come up in order;
   - a dynamic uid is reused through its directory's owner.

   A container restart only approximates this.
3. **The real kernel and cgroup tree** (6.8, cgroup v2, not delegated): seccomp `~listen`, `ProtectProc=invisible`, the cgroup limits (`MemoryMax`, `TasksMax`, `CPUQuota`), and `ProtectKernel*` under a real PID 1.
4. **The B24 baseline from a real mission unit:** `SocketBindAllow` unenforced (`-BPF_FRAMEWORK`). The VM's Landlock is available: the ABI query returns 4, and `landlock` is in the active LSM list (read-only, 2026-10-18). Demonstrating it is Stage 12's work.
5. **AppArmor:** it is enforcing on the VM (`apparmor_restrict_unprivileged_userns=1`), while the lab ran privileged and unconfined. This covers the units' namespacing under PID 1 and `ssh` as `blackglass-tunnel`.
6. **The real `polkitd`** (124-2ubuntu1.24.04.4) **and logind/D-Bus,** alongside the distribution's own rules.
7. **The real SSH tunnel:** the lab had no network. This covers the pinned host key against the real services VM, the forward-only restrictions, `ExitOnForwardFailure`, the backoff, and the agent's listener-owner check against a real `ssh` process.
8. **Name service:** `files systemd`, for dynamic user names in tools. `slot-guard` doesn't depend on it.
9. **Coexistence with the existing host:**
   - the owner's `sshd` and editor server;
   - `/opt/blackglass/server`, unchanged;
   - the dynamic uid range, unused;
   - `unattended-upgrades`;
   - persistent journald.
10. **End to end against a backend:** the claim's port allocation from `port_base` 7770, and settlement through the tunnel.

### Rollback

- **After any phase:**
  - **Mission VM:**
    - `/root/bg-stage10/mission/uninstall.sh --yes --stop-missions` stops and disables the agent, the tunnel and the broker's socket, and removes the units, the polkit rule and the stub link;
    - then:

      ```
      rm -f /etc/blackglass/tunnel/id_ed25519 /etc/blackglass/tunnel/id_ed25519.pub /etc/blackglass/agent/host-secret
      rm -f /etc/sysusers.d/blackglass.conf /etc/tmpfiles.d/blackglass.conf; rmdir /etc/sysusers.d; systemctl daemon-reload
      ```
    - The evidence stays until the owner archives it: `/var/lib/private/blackglass-outbox`, `/var/log/private/blackglass-mission`, `/var/lib/blackglass`, `/var/log/blackglass` and `/root/bg-stage10/evidence`.
    - Then: `userdel blackglass-agent; userdel blackglass-tunnel`, and remove `/opt/blackglass/agent`, `/opt/blackglass/mission`, `/etc/blackglass`, `/etc/blackglass-stub` and the evidence directories.
    - Check: `systemctl list-unit-files | grep blackglass` prints nothing, `ss -H -tuln` matches `before.txt`, and the `/opt/blackglass/server` hash is unchanged.
  - **Services VM:**
    - in an open admin session:

      ```
      rm -f /etc/ssh/authorized_keys.d/bg-api-tunnel /etc/ssh/sshd_config.d/60-bg-api-tunnel.conf; rmdir /etc/ssh/authorized_keys.d
      sshd -t && systemctl reload ssh
      ```
    - a new admin login must work;
    - then `userdel bg-api-tunnel`, `rm -rf /var/tmp/bg-stage10`, and phase D's cleanup if it ran.
- **Partial failures:**
  - **A5 refuses:** nothing is half-installed past its first refusal, and `uninstall.sh` is idempotent.
  - **A6 shows a FAIL:** stop and keep the report.
  - **A failed `sshd -t` in phase B:** remove the drop-in before any reload.
