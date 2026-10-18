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
  - the server binds only its slot's UDP port and never listens (superseded by "Security corrections" below: the earlier template's TCP refusal was never enforced on Ubuntu 24.04; an enforced bind restriction is the blocking acceptance requirement B24 there);
  - it follows §H's five publication steps and the exit rules;
  - it reads its credential from `$CREDENTIALS_DIRECTORY/process`;
  - a real launcher replaces the stub behind `/opt/blackglass/mission/current`;
  - under C1-C, the server would also make itself non-dumpable.
- **Phase 14:** outbox and relay cleanup, and an outbox quota or separate filesystem (M12's disk-exhaustion part).
